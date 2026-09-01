using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using log4net;

namespace VoicemeeterHub;

/// <summary>
///     The Voicemeeter Hub server: one loopback WebSocket endpoint that owns the single
///     <see cref="VoicemeeterClient"/> Remote API session and the single state poller, and fans
///     state out to every subscribed client. It exits after an idle period with no connections so
///     it does not hold the Remote API session open forever; clients restart it on demand.
/// </summary>
public sealed class HubServer : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(HubServer));
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<HubConnection, byte> _connections = new();
    private readonly IVoicemeeterClient _client;
    private readonly HubStateService _state;
    private readonly TaskCompletionSource<int> _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastActivityTicks;
    private int _disposed;

    /// <param name="client">
    ///     The Remote API client. Defaults to a real <see cref="VoicemeeterClient"/>; tests inject
    ///     a fake so the server can run without the native DLL.
    /// </param>
    public HubServer(IVoicemeeterClient? client = null)
    {
        _client = client ?? new VoicemeeterClient();
        _state = new HubStateService(_client, () => AnySubscriber);
        _state.StateChanged += BroadcastStateAsync;
        Volatile.Write(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public IVoicemeeterClient Client => _client;

    public string? ServerVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString();

    /// <summary>Completes with the bound TCP port once the listener is accepting (useful for tests).</summary>
    public Task<int> Listening => _listening.Task;

    private bool AnySubscriber
    {
        get
        {
            foreach (var connection in _connections.Keys)
                if (connection.SubscribedToState)
                    return true;
            return false;
        }
    }

    public async Task<int> RunAsync(int port, CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            Log.Error($"Failed to bind 127.0.0.1:{port}: {ex.Message}");
            _listening.TrySetException(ex);
            return 1;
        }

        var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listening.TrySetResult(boundPort);
        WriteEndpointFile(boundPort);
        Log.Info($"Voicemeeter hub listening on 127.0.0.1:{boundPort} (protocol v{HubProtocol.Version}).");

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var idleTask = WatchIdleAsync(shutdown);

        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                TcpClient tcpClient;
                try
                {
                    tcpClient = await listener.AcceptTcpClientAsync(shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                _ = Task.Run(() => AcceptConnectionAsync(tcpClient, shutdown.Token), shutdown.Token);
            }
        }
        finally
        {
            shutdown.Cancel();
            listener.Stop();
            try { await idleTask; } catch (OperationCanceledException) { }
            DeleteEndpointFile();
            Log.Info("Voicemeeter hub stopped.");
        }

        return 0;
    }

    internal async Task SendCurrentStateAsync(HubConnection connection, CancellationToken cancellationToken)
    {
        var snapshot = _state.Current;
        snapshot ??= await SafeRefreshAsync(cancellationToken);
        if (snapshot != null) await connection.SendStateAsync(snapshot, cancellationToken);
    }

    private async Task<VoicemeeterSnapshot?> SafeRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _state.RefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warn($"Initial state refresh failed: {ex.Message}");
            return null;
        }
    }

    private async Task AcceptConnectionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        using (tcpClient)
        {
            tcpClient.NoDelay = true;
            var socket = await WebSocketHandshake.AcceptAsync(tcpClient, cancellationToken);
            if (socket == null) return;

            var connection = new HubConnection(socket, this);
            _connections[connection] = 0;
            Volatile.Write(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
            try
            {
                await connection.RunAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Warn($"Connection ended with error: {ex.Message}");
            }
            finally
            {
                _connections.TryRemove(connection, out _);
                connection.Dispose();
                Volatile.Write(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
        }
    }

    private async Task BroadcastStateAsync(VoicemeeterSnapshot snapshot)
    {
        var tasks = new List<Task>();
        foreach (var connection in _connections.Keys)
            if (connection.SubscribedToState)
                tasks.Add(connection.SendStateAsync(snapshot, CancellationToken.None));
        if (tasks.Count > 0) await Task.WhenAll(tasks);
    }

    private async Task WatchIdleAsync(CancellationTokenSource shutdown)
    {
        var cancellationToken = shutdown.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!_connections.IsEmpty) continue;
            var last = new DateTimeOffset(Volatile.Read(ref _lastActivityTicks), TimeSpan.Zero);
            if (DateTimeOffset.UtcNow - last < IdleTimeout) continue;
            Log.Info("Voicemeeter hub idle timeout reached; shutting down.");
            shutdown.Cancel();
            return;
        }
    }

    private void WriteEndpointFile(int port)
    {
        try
        {
            var path = HubProtocol.EndpointFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new HubEndpointInfo(port, Environment.ProcessId, HubProtocol.Version, HubProtocol.ServerName, DateTimeOffset.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(info, HubProtocol.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to write endpoint file: {ex.Message}");
        }
    }

    private static void DeleteEndpointFile()
    {
        try
        {
            var path = HubProtocol.EndpointFilePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // best-effort cleanup
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _state.Dispose();
        _client.SuppressReconnect();
        _client.Dispose();
        foreach (var connection in _connections.Keys) connection.Dispose();
    }
}
