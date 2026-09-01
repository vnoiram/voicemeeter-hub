using log4net;

namespace VoicemeeterHub;

/// <summary>
///     The single server-side Voicemeeter state poller. It replaces the per-client polling that
///     each application used to run on its own: one <see cref="VoicemeeterClient"/> session, one
///     <c>IsParametersDirty</c> poll loop, and full <see cref="VoicemeeterSnapshot"/> snapshots
///     pushed to every subscribed client. Polling only runs while at least one client is
///     subscribed, so an idle hub does not touch the Remote API needlessly.
/// </summary>
public sealed class HubStateService : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(HubStateService));
    private static readonly string[] ChannelKinds = ["strip", "bus"];

    private readonly IVoicemeeterClient _client;
    private readonly Func<bool> _hasSubscribers;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly System.Threading.Timer _timer;
    private int _disposed;

    public HubStateService(IVoicemeeterClient client, Func<bool> hasSubscribers, TimeSpan? pollInterval = null)
    {
        _client = client;
        _hasSubscribers = hasSubscribers;
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, interval, interval);
    }

    /// <summary>Raised after each successful refresh so the server can broadcast to subscribers.</summary>
    public event Func<VoicemeeterSnapshot, Task>? StateChanged;

    public VoicemeeterSnapshot? Current { get; private set; }

    /// <summary>
    ///     Forces an immediate refresh and returns the fresh snapshot. Used when a client subscribes
    ///     so it receives current state without waiting for the next dirty tick.
    /// </summary>
    public async Task<VoicemeeterSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
        var token = linkedCancellation.Token;

        await _refreshLock.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            VoicemeeterSnapshot snapshot;
            try
            {
                var edition = await _client.GetEditionAsync(token);
                var states = new Dictionary<string, VoicemeeterOverviewState>(StringComparer.OrdinalIgnoreCase);
                foreach (var kind in ChannelKinds)
                for (var index = 0; index <= VoicemeeterSettings.MaxChannelIndex; index++)
                {
                    var key = VoicemeeterSettings.BuildChannelKey(kind, index);
                    var shortLabel = VoicemeeterSettings.AbbreviatedLabelFor(kind, index, edition);
                    try
                    {
                        var state = await _client.GetChannelStateAsync(kind, index, token);
                        states[key] = new VoicemeeterOverviewState(key, shortLabel, state.GainDb, state.Muted, null);
                    }
                    catch (Exception ex)
                    {
                        states[key] = new VoicemeeterOverviewState(key, shortLabel, null, null, ex.Message);
                    }
                }

                snapshot = new VoicemeeterSnapshot(DateTimeOffset.Now, edition, states, null);
            }
            catch (Exception ex)
            {
                Log.Warn($"Voicemeeter state refresh failed: {ex.Message}");
                snapshot = new VoicemeeterSnapshot(DateTimeOffset.Now, VoicemeeterEdition.Unknown,
                    new Dictionary<string, VoicemeeterOverviewState>(), ex.Message);
            }

            Current = snapshot;
            await NotifyAsync(snapshot);
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _disposeCancellation.Cancel();
        using var timerDisposed = new ManualResetEvent(false);
        if (_timer.Dispose(timerDisposed)) timerDisposed.WaitOne(TimeSpan.FromSeconds(2));
        _disposeCancellation.Dispose();
        _refreshLock.Dispose();
    }

    private async Task TickAsync()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!_hasSubscribers()) return;
            var dirty = Current == null || await _client.IsParametersDirtyAsync(_disposeCancellation.Token);
            if (dirty && Volatile.Read(ref _disposed) == 0) await RefreshAsync(_disposeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Voicemeeter state tick failed: {ex.Message}");
        }
    }

    private async Task NotifyAsync(VoicemeeterSnapshot snapshot)
    {
        var handlers = StateChanged;
        if (handlers == null) return;
        foreach (Func<VoicemeeterSnapshot, Task> handler in handlers.GetInvocationList())
            try
            {
                await handler(snapshot);
            }
            catch (Exception ex)
            {
                Log.Warn($"Voicemeeter state listener failed: {ex.Message}");
            }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
