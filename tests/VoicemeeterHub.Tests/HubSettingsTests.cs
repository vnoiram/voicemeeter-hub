using VoicemeeterHub;
using Xunit;

namespace VoicemeeterHub.Tests;

public class HubSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vmhub-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_ReturnsDefaultsWhenMissing()
    {
        Assert.Null(HubSettings.Load(Path.Combine(_dir, "settings.json")).RestartHotkey);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsHotkey()
    {
        var path = Path.Combine(_dir, "settings.json");
        new HubSettings { RestartHotkey = new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52) }.Save(path);

        var loaded = HubSettings.Load(path);
        Assert.Equal(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52), loaded.RestartHotkey);
    }

    [Fact]
    public void Load_FallsBackOnMalformedJson()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ not json");
        Assert.Null(HubSettings.Load(path).RestartHotkey);
    }

    [Fact]
    public void Load_DropsInvalidHotkey()
    {
        var path = Path.Combine(_dir, "settings.json");
        new HubSettings { RestartHotkey = new HotkeyBinding(HotkeyModifiers.None, 0x52) }.Save(path);
        Assert.Null(HubSettings.Load(path).RestartHotkey);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52, "Ctrl+Alt+R")]
    [InlineData(HotkeyModifiers.Shift | HotkeyModifiers.Win, 0x71, "Shift+Win+F2")]
    [InlineData(HotkeyModifiers.Control, 0x35, "Ctrl+5")]
    public void ToString_FormatsReadably(HotkeyModifiers mods, int vk, string expected)
    {
        Assert.Equal(expected, new HotkeyBinding(mods, vk).ToString());
    }

    [Fact]
    public void IsValid_RequiresModifierAndSupportedKey()
    {
        Assert.False(new HotkeyBinding(HotkeyModifiers.None, 0x52).IsValid);
        Assert.False(new HotkeyBinding(HotkeyModifiers.Control, 0x20).IsValid);
        Assert.True(new HotkeyBinding(HotkeyModifiers.Control, 0x52).IsValid);
        Assert.False(new HotkeyBinding(HotkeyModifiers.Shift, 0x52).IsValid);
        Assert.False(new HotkeyBinding(HotkeyModifiers.Alt, 0x73).IsValid);
    }
}
