using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using log4net;
using Microsoft.Win32;

namespace VoicemeeterHub;

public sealed class VoicemeeterClient : IVoicemeeterClient
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(VoicemeeterClient));
    private const int MacroModeDefault = 0x00000000;
    private const int MacroModeStateOnly = 0x00000002;

    private readonly BlockingCollection<NativeCall> _nativeCalls = new();
    private readonly Thread _nativeThread;
    private int _disposed;
    private int _reconnectSuppressed;
    private bool _loginSucceeded;
    private bool _loggedIn;
    private VoicemeeterEdition _lastEdition = VoicemeeterEdition.Unknown;

    public VoicemeeterClient()
    {
        VoicemeeterNativeLibrary.EnsureResolverRegistered();
        _nativeThread = new Thread(RunNativeCalls)
        {
            IsBackground = false,
            Name = "Voicemeeter Remote API"
        };
        _nativeThread.Start();
    }

    public string? DllPath => VoicemeeterNativeLibrary.ResolvedPath;
    public string? DiscoveryError => VoicemeeterNativeLibrary.DiscoveryError;
    public VoicemeeterOperationResult? LastResult { get; private set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            if (_loginSucceeded)
            {
                RunDuringDispose(() =>
                {
                    LogoutSessionSync("dispose");
                    return true;
                });
            }
        }
        finally
        {
            _nativeCalls.CompleteAdding();
            if (!_nativeThread.Join(TimeSpan.FromSeconds(2)))
                Log.Warn("Voicemeeter native API thread did not stop within timeout");
            _nativeCalls.Dispose();
        }
    }

    public Task<VoicemeeterOperationResult> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(EnsureConnectedSync, cancellationToken);
    }

    public void SuppressReconnect()
    {
        Interlocked.Exchange(ref _reconnectSuppressed, 1);
    }

    public async Task<VoicemeeterEdition> GetEditionAsync(CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return VoicemeeterEdition.Unknown;

        return await RunAsync(() =>
        {
            var code = NativeMethods.VBVMR_GetVoicemeeterType(out var type);
            if (TryReconnectAfterDisconnected(code, "GetVoicemeeterType"))
                code = NativeMethods.VBVMR_GetVoicemeeterType(out type);
            ObserveStatusCode(code);
            if (code != 0) return VoicemeeterEdition.Unknown;
            var edition = type switch
            {
                1 => VoicemeeterEdition.Standard,
                2 => VoicemeeterEdition.Banana,
                3 => VoicemeeterEdition.Potato,
                6 => VoicemeeterEdition.PotatoX64,
                _ => VoicemeeterEdition.Unknown
            };
            _lastEdition = edition;
            return edition;
        }, cancellationToken);
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return null;

        return await RunAsync(() =>
        {
            var code = NativeMethods.VBVMR_GetVoicemeeterVersion(out var packed);
            if (TryReconnectAfterDisconnected(code, "GetVoicemeeterVersion"))
                code = NativeMethods.VBVMR_GetVoicemeeterVersion(out packed);
            ObserveStatusCode(code);
            if (code != 0) return null;
            var v1 = (packed >> 24) & 0xFF;
            var v2 = (packed >> 16) & 0xFF;
            var v3 = (packed >> 8) & 0xFF;
            var v4 = packed & 0xFF;
            return $"{v1}.{v2}.{v3}.{v4}";
        }, cancellationToken);
    }

    public async Task<VoicemeeterChannelState> GetChannelStateAsync(string channelKind, int index, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) throw new InvalidOperationException(connect.ErrorSummary);

        var prefix = ParamPrefix(channelKind, index);
        return await RunAsync(() =>
        {
            var gain = ReadFloatParamSync($"{prefix}.Gain");
            var muted = ReadFloatParamSync($"{prefix}.Mute");
            if (gain is null || muted is null)
                throw new InvalidOperationException($"Failed to read {prefix} state from Voicemeeter");
            return new VoicemeeterChannelState(gain.Value, muted.Value >= 0.5f);
        }, cancellationToken);
    }

    public async Task<VoicemeeterOperationResult> SetGainAsync(string channelKind, int index, double gainDb, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return connect;

        var prefix = ParamPrefix(channelKind, index);
        var clamped = (float)Math.Clamp(gainDb, -60.0, 12.0);
        return await RunAsync(() => WriteFloatParamSync($"{prefix}.Gain", clamped), cancellationToken);
    }

    public async Task<VoicemeeterOperationResult> SetMuteAsync(string channelKind, int index, bool muted, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return connect;

        var prefix = ParamPrefix(channelKind, index);
        return await RunAsync(() => WriteFloatParamSync($"{prefix}.Mute", muted ? 1f : 0f), cancellationToken);
    }

    public async Task<bool> GetSoloAsync(int stripIndex, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return false;

        return await RunAsync(() => (ReadFloatParamSync($"Strip[{stripIndex}].Solo") ?? 0) >= 0.5f, cancellationToken);
    }

    public async Task<VoicemeeterOperationResult> SetSoloAsync(int stripIndex, bool solo, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return connect;

        return await RunAsync(() => WriteFloatParamSync($"Strip[{stripIndex}].Solo", solo ? 1f : 0f), cancellationToken);
    }

    public async Task<bool> GetMonoAsync(string channelKind, int index, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return false;

        var prefix = ParamPrefix(channelKind, index);
        return await RunAsync(() => (ReadFloatParamSync($"{prefix}.Mono") ?? 0) >= 0.5f, cancellationToken);
    }

    public async Task<VoicemeeterOperationResult> SetMonoAsync(string channelKind, int index, bool mono, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return connect;

        var prefix = ParamPrefix(channelKind, index);
        return await RunAsync(() => WriteFloatParamSync($"{prefix}.Mono", mono ? 1f : 0f), cancellationToken);
    }

    public async Task<VoicemeeterOperationResult> SetDeviceAsync(string channelKind, int index, string driver, string deviceName, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return connect;

        var paramName = $"{ParamPrefix(channelKind, index)}.device.{driver}";
        return await RunAsync(() => WriteStringParamSync(paramName, deviceName), cancellationToken);
    }

    public async Task<string?> GetDeviceAsync(string channelKind, int index, string driver, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return null;

        var paramName = $"{ParamPrefix(channelKind, index)}.device.{driver}";
        return await RunAsync(() => ReadStringParamSync(paramName), cancellationToken);
    }

    public async Task<IReadOnlyList<VoicemeeterDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return Array.Empty<VoicemeeterDeviceInfo>();

        return await RunAsync(() => EnumerateDevicesSync(false), cancellationToken);
    }

    public async Task<IReadOnlyList<VoicemeeterDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return Array.Empty<VoicemeeterDeviceInfo>();

        return await RunAsync(() => EnumerateDevicesSync(true), cancellationToken);
    }

    public async Task<VoicemeeterOperationResult> PressMacroButtonAsync(int index, bool pressed, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return connect;

        return await RunAsync(() =>
        {
            var code = NativeMethods.VBVMR_MacroButton_SetStatus(index, pressed ? 1f : 0f, MacroModeDefault);
            if (TryReconnectAfterDisconnected(code, $"MacroButton_SetStatus[{index}]"))
                code = NativeMethods.VBVMR_MacroButton_SetStatus(index, pressed ? 1f : 0f, MacroModeDefault);
            ObserveStatusCode(code);
            var result = code == 0
                ? VoicemeeterOperationResult.Ok($"MacroButton[{index}]")
                : VoicemeeterOperationResult.Fail(code, $"MacroButton[{index}]", null);
            LastResult = result;
            return result;
        }, cancellationToken);
    }

    public async Task<bool> GetMacroButtonStateAsync(int index, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return false;

        return await RunAsync(() =>
        {
            var code = NativeMethods.VBVMR_MacroButton_GetStatus(index, out var value, MacroModeStateOnly);
            if (TryReconnectAfterDisconnected(code, $"MacroButton_GetStatus[{index}]"))
                code = NativeMethods.VBVMR_MacroButton_GetStatus(index, out value, MacroModeStateOnly);
            ObserveStatusCode(code);
            return code == 0 && value >= 0.5f;
        }, cancellationToken);
    }

    public async Task<bool> IsParametersDirtyAsync(CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return false;

        return await RunAsync(() =>
        {
            var code = NativeMethods.VBVMR_IsParametersDirty();
            if (TryReconnectAfterDisconnected(code, "IsParametersDirty"))
                code = NativeMethods.VBVMR_IsParametersDirty();
            ObserveStatusCode(code);
            return code == 1;
        }, cancellationToken);
    }

    public async Task<bool> IsMacroButtonDirtyAsync(CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return false;

        return await RunAsync(() =>
        {
            var code = NativeMethods.VBVMR_MacroButton_IsDirty();
            if (TryReconnectAfterDisconnected(code, "MacroButton_IsDirty"))
                code = NativeMethods.VBVMR_MacroButton_IsDirty();
            ObserveStatusCode(code);
            return code == 1;
        }, cancellationToken);
    }

    public Task<VoicemeeterOperationResult> SetRecorderAsync(bool recording, CancellationToken cancellationToken = default)
    {
        return SetCommandFloatAsync("Recorder.Record", recording ? 1f : 0f, cancellationToken);
    }

    public async Task<bool> GetRecorderStateAsync(CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return false;

        return await RunAsync(() => (ReadFloatParamSync("Recorder.Record") ?? 0) >= 0.5f, cancellationToken);
    }

    public Task<VoicemeeterOperationResult> SetEqAsync(int busIndex, bool on, CancellationToken cancellationToken = default)
    {
        return SetCommandFloatAsync($"Bus[{busIndex}].EQ.on", on ? 1f : 0f, cancellationToken);
    }

    public async Task<bool> GetEqStateAsync(int busIndex, CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return false;

        return await RunAsync(() => (ReadFloatParamSync($"Bus[{busIndex}].EQ.on") ?? 0) >= 0.5f, cancellationToken);
    }

    public Task<VoicemeeterOperationResult> TriggerCommandAsync(string commandName, CancellationToken cancellationToken = default)
    {
        return SetCommandFloatAsync($"Command.{commandName}", 1f, cancellationToken);
    }

    public async Task<object> BuildDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        var edition = connect.Success ? await GetEditionAsync(cancellationToken) : VoicemeeterEdition.Unknown;
        var version = connect.Success ? await GetVersionAsync(cancellationToken) : null;
        return new
        {
            dllPath = DllPath,
            discoveryError = DiscoveryError,
            remoteMode = "hub",
            loggedIn = connect.Success,
            connectError = connect.Success ? null : connect.ErrorSummary,
            edition = edition.ToString(),
            version,
            maxChannelIndex = VoicemeeterSettings.MaxChannelIndex,
            lastResult = LastResult == null
                ? null
                : new
                {
                    LastResult.Success,
                    LastResult.StatusCode,
                    LastResult.ParamName,
                    LastResult.ErrorSummary
                }
        };
    }

    private async Task<VoicemeeterOperationResult> SetCommandFloatAsync(string paramName, float value, CancellationToken cancellationToken)
    {
        var connect = await EnsureConnectedAsync(cancellationToken);
        if (!connect.Success) return connect;

        return await RunAsync(() => WriteFloatParamSync(paramName, value), cancellationToken);
    }

    private void ObserveStatusCode(int code)
    {
        if (VoicemeeterOperationResult.IndicatesDisconnected(code)) _loggedIn = false;
    }

    private float? ReadFloatParamSync(string name)
    {
        var code = NativeMethods.VBVMR_GetParameterFloat(name, out var value);
        if (TryReconnectAfterDisconnected(code, $"GetParameterFloat {name}"))
            code = NativeMethods.VBVMR_GetParameterFloat(name, out value);
        ObserveStatusCode(code);
        if (code != 0)
        {
            Log.Warn($"GetParameterFloat failed name={name} code={code}");
            return null;
        }

        return value;
    }

    private VoicemeeterOperationResult WriteFloatParamSync(string name, float value)
    {
        var code = NativeMethods.VBVMR_SetParameterFloat(name, value);
        if (TryReconnectAfterDisconnected(code, $"SetParameterFloat {name}"))
            code = NativeMethods.VBVMR_SetParameterFloat(name, value);
        ObserveStatusCode(code);
        var result = code == 0
            ? VoicemeeterOperationResult.Ok(name)
            : VoicemeeterOperationResult.Fail(code, name, null);
        if (!result.Success) Log.Warn($"SetParameterFloat failed name={name} value={value} code={code}");
        LastResult = result;
        return result;
    }

    private string? ReadStringParamSync(string name)
    {
        var buffer = new StringBuilder(512);
        var code = NativeMethods.VBVMR_GetParameterStringA(name, buffer);
        if (TryReconnectAfterDisconnected(code, $"GetParameterStringA {name}"))
            code = NativeMethods.VBVMR_GetParameterStringA(name, buffer);
        ObserveStatusCode(code);
        if (code != 0)
        {
            Log.Warn($"GetParameterStringA failed name={name} code={code}");
            return null;
        }

        return buffer.ToString();
    }

    private VoicemeeterOperationResult WriteStringParamSync(string name, string value)
    {
        var code = NativeMethods.VBVMR_SetParameterStringA(name, value);
        if (TryReconnectAfterDisconnected(code, $"SetParameterStringA {name}"))
            code = NativeMethods.VBVMR_SetParameterStringA(name, value);
        ObserveStatusCode(code);
        var result = code == 0
            ? VoicemeeterOperationResult.Ok(name)
            : VoicemeeterOperationResult.Fail(code, name, null);
        if (!result.Success) Log.Warn($"SetParameterStringA failed name={name} value={value} code={code}");
        LastResult = result;
        return result;
    }

    private static IReadOnlyList<VoicemeeterDeviceInfo> EnumerateDevicesSync(bool isOutput)
    {
        var count = isOutput ? NativeMethods.VBVMR_Output_GetDeviceNumber() : NativeMethods.VBVMR_Input_GetDeviceNumber();
        var results = new List<VoicemeeterDeviceInfo>();
        for (var i = 0; i < count; i++)
        {
            var nameBuffer = new StringBuilder(512);
            var hwIdBuffer = new StringBuilder(512);
            int code;
            int nType;
            if (isOutput)
                code = NativeMethods.VBVMR_Output_GetDeviceDescA(i, out nType, nameBuffer, hwIdBuffer);
            else
                code = NativeMethods.VBVMR_Input_GetDeviceDescA(i, out nType, nameBuffer, hwIdBuffer);

            if (code != 0) continue;
            var driver = nType switch
            {
                1 => VoicemeeterDeviceDriver.Mme,
                3 => VoicemeeterDeviceDriver.Wdm,
                4 => VoicemeeterDeviceDriver.Ks,
                5 => VoicemeeterDeviceDriver.Asio,
                _ => VoicemeeterDeviceDriver.Wdm
            };
            results.Add(new VoicemeeterDeviceInfo(i, driver, nameBuffer.ToString(), hwIdBuffer.ToString()));
        }

        return results;
    }

    private static string ParamPrefix(string channelKind, int index)
    {
        return string.Equals(channelKind, "bus", StringComparison.OrdinalIgnoreCase) ? $"Bus[{index}]" : $"Strip[{index}]";
    }

    private VoicemeeterOperationResult EnsureConnectedSync()
    {
        if (_loggedIn) return VoicemeeterOperationResult.Ok();
        if (Volatile.Read(ref _reconnectSuppressed) != 0)
            return VoicemeeterOperationResult.Fail(-2, null, "Voicemeeter reconnect is suppressed while the hub is shutting down.");

        var code = NativeMethods.VBVMR_Login();
        Log.Info($"Voicemeeter login code={code} dll={DllPath ?? "(not resolved)"}");
        if (code == 0)
        {
            _loggedIn = true;
            _loginSucceeded = true;
            return VoicemeeterOperationResult.Ok();
        }

        if (code == 1)
        {
            _loginSucceeded = true;
            if (_lastEdition != VoicemeeterEdition.Unknown)
            {
                var runType = _lastEdition == VoicemeeterEdition.PotatoX64 ? 3 : (int)_lastEdition;
                var runCode = NativeMethods.VBVMR_RunVoicemeeter(runType);
                Log.Info($"Voicemeeter RunVoicemeeter type={runType} code={runCode}");
                Thread.Sleep(1500);
                if (runCode == 0)
                {
                    _loggedIn = true;
                    _loginSucceeded = true;
                    return VoicemeeterOperationResult.Ok();
                }

                LogoutSessionSync("run-voicemeeter-failed");
                return VoicemeeterOperationResult.Fail(runCode, null, VoicemeeterOperationResult.DescribeStatusCode(runCode));
            }

            LogoutSessionSync("voicemeeter-not-running");
            return VoicemeeterOperationResult.Fail(code, null,
                "Voicemeeter is installed but not currently running. Launch Voicemeeter and try again.");
        }

        return VoicemeeterOperationResult.Fail(code, null, VoicemeeterOperationResult.DescribeStatusCode(code));
    }

    private bool TryReconnectAfterDisconnected(int code, string operation)
    {
        if (!VoicemeeterOperationResult.IndicatesDisconnected(code)) return false;

        _loggedIn = false;
        if (Volatile.Read(ref _reconnectSuppressed) != 0) return false;

        Log.Warn($"Voicemeeter remote session disconnected during {operation}; reconnecting");
        if (_loginSucceeded) LogoutSessionSync("reconnect");

        var reconnect = EnsureConnectedSync();
        if (reconnect.Success) return true;

        LastResult = reconnect;
        Log.Warn($"Voicemeeter reconnect failed during {operation}: {reconnect.ErrorSummary}");
        return false;
    }

    private void LogoutSessionSync(string reason)
    {
        try
        {
            var code = NativeMethods.VBVMR_Logout();
            Log.Info($"Voicemeeter logout reason={reason} code={code}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Voicemeeter logout failed reason={reason}: {ex.Message}");
        }
        finally
        {
            _loggedIn = false;
            _loginSucceeded = false;
        }
    }

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = new NativeCall<T>(action, completion, cancellationToken);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            _nativeCalls.Add(call, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            throw new ObjectDisposedException(nameof(VoicemeeterClient), ex);
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private T RunDuringDispose<T>(Func<T> action)
    {
        if (Thread.CurrentThread.ManagedThreadId == _nativeThread.ManagedThreadId) return action();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _nativeCalls.Add(new NativeCall<T>(action, completion, CancellationToken.None));
        return completion.Task.GetAwaiter().GetResult();
    }

    private void RunNativeCalls()
    {
        foreach (var call in _nativeCalls.GetConsumingEnumerable()) call.Execute();
    }

    private abstract class NativeCall
    {
        public abstract void Execute();
    }

    private sealed class NativeCall<T>(
        Func<T> action,
        TaskCompletionSource<T> completion,
        CancellationToken cancellationToken) : NativeCall
    {
        public override void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }
    }
}

internal static class VoicemeeterNativeLibrary
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(VoicemeeterNativeLibrary));
    private const string LogicalName = "VoicemeeterRemote64";
    private static readonly object InitLock = new();
    private static bool _initialized;

    public static string? ResolvedPath { get; private set; }
    public static string? DiscoveryError { get; private set; }

    public static void EnsureResolverRegistered()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            _initialized = true;
            NativeLibrary.SetDllImportResolver(typeof(VoicemeeterClient).Assembly, Resolve);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LogicalName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var path = DiscoverDllPath();
        if (path == null) return IntPtr.Zero;

        if (NativeLibrary.TryLoad(path, out var handle))
        {
            ResolvedPath = path;
            return handle;
        }

        DiscoveryError = $"Found VoicemeeterRemote64.dll at '{path}' but failed to load it.";
        return IntPtr.Zero;
    }

    private static string? DiscoverDllPath()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate)) return candidate;
        }

        DiscoveryError = "VoicemeeterRemote64.dll was not found via registry or default install paths.";
        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var fromRegistry = TryReadRegistryInstallDir();
        if (!string.IsNullOrWhiteSpace(fromRegistry))
            yield return Path.Combine(fromRegistry, "VoicemeeterRemote64.dll");

        yield return @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";
        yield return @"C:\Program Files\VB\Voicemeeter\VoicemeeterRemote64.dll";
    }

    private static readonly string[] RegistryKeyCandidates =
    [
        @"SOFTWARE\VB:Audio\Voicemeeter",
        @"SOFTWARE\WOW6432Node\VB:Audio\Voicemeeter",
        @"SOFTWARE\VB:Audio\Voicemeter",
        @"SOFTWARE\WOW6432Node\VB:Audio\Voicemeter"
    ];

    private static string? TryReadRegistryInstallDir()
    {
        try
        {
            foreach (var keyPath in RegistryKeyCandidates)
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                var uninstallString = key?.GetValue("UninstallString") as string;
                if (string.IsNullOrWhiteSpace(uninstallString)) continue;
                var dir = Path.GetDirectoryName(uninstallString.Trim('"'));
                if (!string.IsNullOrWhiteSpace(dir)) return dir;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Voicemeeter registry discovery failed: {ex.Message}");
            return null;
        }
    }
}

internal static class NativeMethods
{
    private const string DllName = "VoicemeeterRemote64";

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_Login();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_Logout();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_RunVoicemeeter(int vType);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_GetVoicemeeterType(out int type);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_GetVoicemeeterVersion(out int version);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_IsParametersDirty();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int VBVMR_GetParameterFloat(string szParamName, out float pValue);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int VBVMR_SetParameterFloat(string szParamName, float value);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int VBVMR_GetParameterStringA(string szParamName, StringBuilder szString);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int VBVMR_SetParameterStringA(string szParamName, string szString);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_Output_GetDeviceNumber();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int VBVMR_Output_GetDeviceDescA(int zindex, out int nType, StringBuilder szDeviceName, StringBuilder szHardwareId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_Input_GetDeviceNumber();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int VBVMR_Input_GetDeviceDescA(int zindex, out int nType, StringBuilder szDeviceName, StringBuilder szHardwareId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_MacroButton_IsDirty();

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_MacroButton_GetStatus(int nuLogicalButton, out float pValue, int bitmode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int VBVMR_MacroButton_SetStatus(int nuLogicalButton, float fValue, int bitmode);
}
