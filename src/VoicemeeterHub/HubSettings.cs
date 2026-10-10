using System.Text.Json;
using System.Text.Json.Serialization;
using log4net;

namespace VoicemeeterHub;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8
}

/// <summary>A global hotkey: Win32 modifier flags plus a virtual-key code (A-Z, 0-9, F1-F24).</summary>
public sealed record HotkeyBinding(HotkeyModifiers Modifiers, int VirtualKey)
{
    /// <summary>
    ///     Needs a supported key and Ctrl, Alt or Win (Shift alone would swallow ordinary typing
    ///     system-wide). Alt+F4 is rejected so it cannot hijack window closing.
    /// </summary>
    public bool IsValid =>
        (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0 &&
        IsSupportedKey(VirtualKey) &&
        !(Modifiers.HasFlag(HotkeyModifiers.Alt) && VirtualKey == 0x73);

    public static bool IsSupportedKey(int virtualKey) =>
        virtualKey is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) or (>= 0x70 and <= 0x87);

    public static string KeyName(int virtualKey) => virtualKey switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
        _ => $"0x{virtualKey:X2}"
    };

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }
}

/// <summary>User-editable hub settings persisted as JSON under the per-user application data folder.</summary>
public sealed class HubSettings
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(HubSettings));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Hotkey that restarts the Voicemeeter audio engine; <see langword="null"/> disables it.</summary>
    public HotkeyBinding? RestartHotkey { get; set; }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoicemeeterHub",
        "settings.json");

    /// <summary>Loads settings, falling back to defaults when the file is missing or malformed.</summary>
    public static HubSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new HubSettings();
            var settings = JsonSerializer.Deserialize<HubSettings>(File.ReadAllText(path), JsonOptions) ?? new HubSettings();
            if (settings.RestartHotkey is { IsValid: false })
                settings.RestartHotkey = null;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Failed to read settings; using defaults: {ex.Message}");
            return new HubSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
