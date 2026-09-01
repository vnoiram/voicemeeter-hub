using System.Text.Json;

namespace VoicemeeterHub;

/// <summary>
///     Maps protocol operation names to <see cref="IVoicemeeterClient"/> calls. Shared by every
///     connection; the client itself serializes native access onto a single thread, so concurrent
///     requests from multiple clients are safe. Subscription control (<c>Subscribe</c>/
///     <c>Unsubscribe</c>) is handled by the connection layer, not here.
/// </summary>
public static class VoicemeeterOperations
{
    public static async Task<object?> DispatchAsync(
        IVoicemeeterClient client,
        string operation,
        IReadOnlyDictionary<string, JsonElement>? rawArgs,
        CancellationToken cancellationToken)
    {
        var args = rawArgs ?? new Dictionary<string, JsonElement>();
        switch (operation)
        {
            case "EnsureConnected":
                return await client.EnsureConnectedAsync(cancellationToken);
            case "SuppressReconnect":
                client.SuppressReconnect();
                return VoicemeeterOperationResult.Ok();
            case "GetEdition":
                return await client.GetEditionAsync(cancellationToken);
            case "GetVersion":
                return await client.GetVersionAsync(cancellationToken);
            case "GetChannelState":
                return await client.GetChannelStateAsync(Arg<string>(args, "channelKind"), Arg<int>(args, "index"), cancellationToken);
            case "SetGain":
                return await client.SetGainAsync(Arg<string>(args, "channelKind"), Arg<int>(args, "index"), Arg<double>(args, "gainDb"), cancellationToken);
            case "SetMute":
                return await client.SetMuteAsync(Arg<string>(args, "channelKind"), Arg<int>(args, "index"), Arg<bool>(args, "muted"), cancellationToken);
            case "GetSolo":
                return await client.GetSoloAsync(Arg<int>(args, "stripIndex"), cancellationToken);
            case "SetSolo":
                return await client.SetSoloAsync(Arg<int>(args, "stripIndex"), Arg<bool>(args, "solo"), cancellationToken);
            case "GetMono":
                return await client.GetMonoAsync(Arg<string>(args, "channelKind"), Arg<int>(args, "index"), cancellationToken);
            case "SetMono":
                return await client.SetMonoAsync(Arg<string>(args, "channelKind"), Arg<int>(args, "index"), Arg<bool>(args, "mono"), cancellationToken);
            case "SetDevice":
                return await client.SetDeviceAsync(Arg<string>(args, "channelKind"), Arg<int>(args, "index"), Arg<string>(args, "driver"), Arg<string>(args, "deviceName"), cancellationToken);
            case "GetDevice":
                return await client.GetDeviceAsync(Arg<string>(args, "channelKind"), Arg<int>(args, "index"), Arg<string>(args, "driver"), cancellationToken);
            case "GetInputDevices":
                return await client.GetInputDevicesAsync(cancellationToken);
            case "GetOutputDevices":
                return await client.GetOutputDevicesAsync(cancellationToken);
            case "PressMacroButton":
                return await client.PressMacroButtonAsync(Arg<int>(args, "index"), Arg<bool>(args, "pressed"), cancellationToken);
            case "GetMacroButtonState":
                return await client.GetMacroButtonStateAsync(Arg<int>(args, "index"), cancellationToken);
            case "IsParametersDirty":
                return await client.IsParametersDirtyAsync(cancellationToken);
            case "IsMacroButtonDirty":
                return await client.IsMacroButtonDirtyAsync(cancellationToken);
            case "SetRecorder":
                return await client.SetRecorderAsync(Arg<bool>(args, "recording"), cancellationToken);
            case "GetRecorderState":
                return await client.GetRecorderStateAsync(cancellationToken);
            case "SetEq":
                return await client.SetEqAsync(Arg<int>(args, "busIndex"), Arg<bool>(args, "on"), cancellationToken);
            case "GetEqState":
                return await client.GetEqStateAsync(Arg<int>(args, "busIndex"), cancellationToken);
            case "TriggerCommand":
                return await client.TriggerCommandAsync(Arg<string>(args, "commandName"), cancellationToken);
            case "BuildDiagnostics":
                return await client.BuildDiagnosticsAsync(cancellationToken);
            default:
                throw new InvalidOperationException($"Unknown Voicemeeter hub operation '{operation}'.");
        }
    }

    private static T Arg<T>(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var value)) throw new InvalidOperationException($"Missing argument '{name}'.");
        return value.Deserialize<T>(HubProtocol.JsonOptions)!;
    }
}
