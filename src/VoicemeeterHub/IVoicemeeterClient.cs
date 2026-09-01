namespace VoicemeeterHub;

public interface IVoicemeeterClient : IDisposable
{
    string? DllPath { get; }
    string? DiscoveryError { get; }
    VoicemeeterOperationResult? LastResult { get; }

    Task<VoicemeeterOperationResult> EnsureConnectedAsync(CancellationToken cancellationToken = default);
    void SuppressReconnect();
    Task<VoicemeeterEdition> GetEditionAsync(CancellationToken cancellationToken = default);
    Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);
    Task<VoicemeeterChannelState> GetChannelStateAsync(string channelKind, int index, CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> SetGainAsync(string channelKind, int index, double gainDb, CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> SetMuteAsync(string channelKind, int index, bool muted, CancellationToken cancellationToken = default);
    Task<bool> GetSoloAsync(int stripIndex, CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> SetSoloAsync(int stripIndex, bool solo, CancellationToken cancellationToken = default);
    Task<bool> GetMonoAsync(string channelKind, int index, CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> SetMonoAsync(string channelKind, int index, bool mono, CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> SetDeviceAsync(string channelKind, int index, string driver, string deviceName, CancellationToken cancellationToken = default);
    Task<string?> GetDeviceAsync(string channelKind, int index, string driver, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoicemeeterDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoicemeeterDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> PressMacroButtonAsync(int index, bool pressed, CancellationToken cancellationToken = default);
    Task<bool> GetMacroButtonStateAsync(int index, CancellationToken cancellationToken = default);
    Task<bool> IsParametersDirtyAsync(CancellationToken cancellationToken = default);
    Task<bool> IsMacroButtonDirtyAsync(CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> SetRecorderAsync(bool recording, CancellationToken cancellationToken = default);
    Task<bool> GetRecorderStateAsync(CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> SetEqAsync(int busIndex, bool on, CancellationToken cancellationToken = default);
    Task<bool> GetEqStateAsync(int busIndex, CancellationToken cancellationToken = default);
    Task<VoicemeeterOperationResult> TriggerCommandAsync(string commandName, CancellationToken cancellationToken = default);
    Task<object> BuildDiagnosticsAsync(CancellationToken cancellationToken = default);
}
