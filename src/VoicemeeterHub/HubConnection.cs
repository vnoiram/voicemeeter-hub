using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using log4net;

namespace VoicemeeterHub;

/// <summary>
///     One connected WebSocket client. Reads request frames sequentially, dispatches them to the
///     shared <see cref="IVoicemeeterClient"/>, and writes responses. Also receives broadcast
///     state events from <see cref="HubServer"/>. All sends are serialized through a semaphore
///     because responses and events can race.
/// </summary>
internal sealed class HubConnection : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(HubConnection));
    private const int MaxFrameBytes = 1 << 20; // 1 MiB

    private readonly WebSocket _socket;
    private readonly HubServer _server;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private int _subscribedToState;

    public HubConnection(WebSocket socket, HubServer server)
    {
        _socket = socket;
        _server = server;
    }

    public bool SubscribedToState => Volatile.Read(ref _subscribedToState) != 0;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await SendAsync(HubMessage.Hello(_server.ServerVersion), cancellationToken);

        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveTextAsync(cancellationToken);
            if (message == null) break;
            if (message.Length == 0) continue;

            HubRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<HubRequest>(message, HubProtocol.JsonOptions);
            }
            catch (JsonException ex)
            {
                await SendAsync(HubMessage.ErrorResponse(null, $"Invalid request JSON: {ex.Message}"), cancellationToken);
                continue;
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Op))
            {
                await SendAsync(HubMessage.ErrorResponse(request?.Id, "Missing 'op'."), cancellationToken);
                continue;
            }

            await HandleRequestAsync(request, cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HubRequest request, CancellationToken cancellationToken)
    {
        switch (request.Op)
        {
            case "Subscribe":
                Interlocked.Exchange(ref _subscribedToState, 1);
                await SendAsync(HubMessage.OkResponse(request.Id, VoicemeeterOperationResult.Ok()), cancellationToken);
                await _server.SendCurrentStateAsync(this, cancellationToken);
                return;
            case "Unsubscribe":
                Interlocked.Exchange(ref _subscribedToState, 0);
                await SendAsync(HubMessage.OkResponse(request.Id, VoicemeeterOperationResult.Ok()), cancellationToken);
                return;
            case "Ping":
                await SendAsync(HubMessage.OkResponse(request.Id, "pong"), cancellationToken);
                return;
        }

        try
        {
            var result = await VoicemeeterOperations.DispatchAsync(_server.Client, request.Op, request.Args, cancellationToken);
            await SendAsync(HubMessage.OkResponse(request.Id, result), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Operation '{request.Op}' failed: {ex.Message}");
            await SendAsync(HubMessage.ErrorResponse(request.Id, ex.Message), cancellationToken);
        }
    }

    public async Task SendStateAsync(VoicemeeterSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!SubscribedToState) return;
        await SendAsync(HubMessage.Event(HubProtocol.StateTopic, snapshot), cancellationToken);
    }

    public async Task SendAsync(HubMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, HubProtocol.JsonOptions);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State != WebSocketState.Open) return;
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Peer went away between the state check and the send; drop it.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                }
                catch (Exception)
                {
                    // best-effort close
                }

                return null;
            }

            payload.Write(buffer, 0, result.Count);
            if (payload.Length > MaxFrameBytes) return null;
            if (result.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
    }

    public void Dispose()
    {
        _sendLock.Dispose();
        _socket.Dispose();
    }
}
