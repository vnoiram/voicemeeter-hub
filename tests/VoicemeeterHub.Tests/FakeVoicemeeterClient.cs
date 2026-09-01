using System.Collections.Concurrent;
using VoicemeeterHub;

namespace VoicemeeterHub.Tests;

/// <summary>
///     In-memory <see cref="IVoicemeeterClient"/> for tests. Records gain/mute per channel so the
///     server and dispatch layer can be exercised without the native Remote API DLL.
/// </summary>
internal sealed class FakeVoicemeeterClient : IVoicemeeterClient
{
    private readonly ConcurrentDictionary<string, double> _gain = new();
    private readonly ConcurrentDictionary<string, bool> _mute = new();

    public VoicemeeterEdition Edition { get; set; } = VoicemeeterEdition.Banana;
    public int DirtyOnce { get; set; }
    public string? DllPath => @"C:\fake\VoicemeeterRemote64.dll";
    public string? DiscoveryError => null;
    public VoicemeeterOperationResult? LastResult { get; private set; }

    public Task<VoicemeeterOperationResult> EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(VoicemeeterOperationResult.Ok());

    public void SuppressReconnect() { }

    public Task<VoicemeeterEdition> GetEditionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Edition);

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>("1.1.1.1");

    public Task<VoicemeeterChannelState> GetChannelStateAsync(string channelKind, int index, CancellationToken cancellationToken = default)
    {
        var key = VoicemeeterSettings.BuildChannelKey(channelKind, index);
        return Task.FromResult(new VoicemeeterChannelState(_gain.GetValueOrDefault(key), _mute.GetValueOrDefault(key)));
    }

    public Task<VoicemeeterOperationResult> SetGainAsync(string channelKind, int index, double gainDb, CancellationToken cancellationToken = default)
    {
        _gain[VoicemeeterSettings.BuildChannelKey(channelKind, index)] = gainDb;
        return Ok($"{Prefix(channelKind, index)}.Gain");
    }

    public Task<VoicemeeterOperationResult> SetMuteAsync(string channelKind, int index, bool muted, CancellationToken cancellationToken = default)
    {
        _mute[VoicemeeterSettings.BuildChannelKey(channelKind, index)] = muted;
        return Ok($"{Prefix(channelKind, index)}.Mute");
    }

    public Task<bool> GetSoloAsync(int stripIndex, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<VoicemeeterOperationResult> SetSoloAsync(int stripIndex, bool solo, CancellationToken cancellationToken = default) => Ok($"Strip[{stripIndex}].Solo");
    public Task<bool> GetMonoAsync(string channelKind, int index, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<VoicemeeterOperationResult> SetMonoAsync(string channelKind, int index, bool mono, CancellationToken cancellationToken = default) => Ok($"{Prefix(channelKind, index)}.Mono");
    public Task<VoicemeeterOperationResult> SetDeviceAsync(string channelKind, int index, string driver, string deviceName, CancellationToken cancellationToken = default) => Ok($"{Prefix(channelKind, index)}.device.{driver}");
    public Task<string?> GetDeviceAsync(string channelKind, int index, string driver, CancellationToken cancellationToken = default) => Task.FromResult<string?>("Speakers");
    public Task<IReadOnlyList<VoicemeeterDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VoicemeeterDeviceInfo>>(Array.Empty<VoicemeeterDeviceInfo>());
    public Task<IReadOnlyList<VoicemeeterDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VoicemeeterDeviceInfo>>(Array.Empty<VoicemeeterDeviceInfo>());
    public Task<VoicemeeterOperationResult> PressMacroButtonAsync(int index, bool pressed, CancellationToken cancellationToken = default) => Ok($"MacroButton[{index}]");
    public Task<bool> GetMacroButtonStateAsync(int index, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<bool> IsParametersDirtyAsync(CancellationToken cancellationToken = default)
    {
        if (DirtyOnce > 0)
        {
            DirtyOnce--;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> IsMacroButtonDirtyAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<VoicemeeterOperationResult> SetRecorderAsync(bool recording, CancellationToken cancellationToken = default) => Ok("Recorder.Record");
    public Task<bool> GetRecorderStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<VoicemeeterOperationResult> SetEqAsync(int busIndex, bool on, CancellationToken cancellationToken = default) => Ok($"Bus[{busIndex}].EQ.on");
    public Task<bool> GetEqStateAsync(int busIndex, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<VoicemeeterOperationResult> TriggerCommandAsync(string commandName, CancellationToken cancellationToken = default) => Ok($"Command.{commandName}");
    public Task<object> BuildDiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult<object>(new { remoteMode = "hub", loggedIn = true });

    public void Dispose() { }

    private static string Prefix(string channelKind, int index) =>
        string.Equals(channelKind, "bus", StringComparison.OrdinalIgnoreCase) ? $"Bus[{index}]" : $"Strip[{index}]";

    private Task<VoicemeeterOperationResult> Ok(string paramName)
    {
        var result = VoicemeeterOperationResult.Ok(paramName);
        LastResult = result;
        return Task.FromResult(result);
    }
}
