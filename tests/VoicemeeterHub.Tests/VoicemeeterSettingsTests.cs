using VoicemeeterHub;
using Xunit;

namespace VoicemeeterHub.Tests;

public class VoicemeeterSettingsTests
{
    [Theory]
    [InlineData("strip", 0, "strip:0")]
    [InlineData("bus", 3, "bus:3")]
    public void BuildChannelKey_FormatsKindAndIndex(string kind, int index, string expected)
    {
        Assert.Equal(expected, VoicemeeterSettings.BuildChannelKey(kind, index));
    }

    [Theory]
    [InlineData("strip:2", "strip", 2)]
    [InlineData("BUS:5", "bus", 5)]
    public void ParseChannelKey_RoundTrips(string key, string expectedKind, int expectedIndex)
    {
        var parsed = VoicemeeterSettings.ParseChannelKey(key);
        Assert.NotNull(parsed);
        Assert.Equal(expectedKind, parsed!.Value.Kind);
        Assert.Equal(expectedIndex, parsed.Value.Index);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("strip:notanumber")]
    [InlineData("")]
    public void ParseChannelKey_ReturnsNullForInvalid(string key)
    {
        Assert.Null(VoicemeeterSettings.ParseChannelKey(key));
    }

    [Fact]
    public void ParseChannelKey_ClampsIndexToRange()
    {
        var parsed = VoicemeeterSettings.ParseChannelKey("strip:99");
        Assert.NotNull(parsed);
        Assert.Equal(VoicemeeterSettings.MaxChannelIndex, parsed!.Value.Index);
    }

    [Fact]
    public void AbbreviatedLabel_UsesEditionTable()
    {
        Assert.Equal("VM In", VoicemeeterSettings.AbbreviatedLabelFor("strip", 2, VoicemeeterEdition.Standard));
        Assert.Equal("Out B1", VoicemeeterSettings.AbbreviatedLabelFor("bus", 3, VoicemeeterEdition.Banana));
    }

    [Fact]
    public void AbbreviatedLabel_FallsBackForUnknownEdition()
    {
        // Index beyond the known tables falls back to the generic short form.
        Assert.Equal("S7", VoicemeeterSettings.AbbreviatedLabelFor("strip", 7, VoicemeeterEdition.Standard));
    }
}
