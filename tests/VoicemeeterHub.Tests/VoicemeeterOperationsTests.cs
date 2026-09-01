using System.Text.Json;
using VoicemeeterHub;
using Xunit;

namespace VoicemeeterHub.Tests;

public class VoicemeeterOperationsTests
{
    private static Dictionary<string, JsonElement> Args(object value)
    {
        var json = JsonSerializer.SerializeToElement(value, HubProtocol.JsonOptions);
        return json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
    }

    [Fact]
    public async Task SetGain_Then_GetChannelState_RoundTrips()
    {
        var client = new FakeVoicemeeterClient();

        await VoicemeeterOperations.DispatchAsync(client, "SetGain",
            Args(new { channelKind = "strip", index = 1, gainDb = -6.0 }), CancellationToken.None);
        var state = await VoicemeeterOperations.DispatchAsync(client, "GetChannelState",
            Args(new { channelKind = "strip", index = 1 }), CancellationToken.None);

        var channel = Assert.IsType<VoicemeeterChannelState>(state);
        Assert.Equal(-6.0, channel.GainDb);
        Assert.False(channel.Muted);
    }

    [Fact]
    public async Task SetMute_UpdatesState()
    {
        var client = new FakeVoicemeeterClient();
        await VoicemeeterOperations.DispatchAsync(client, "SetMute",
            Args(new { channelKind = "bus", index = 0, muted = true }), CancellationToken.None);

        var state = await client.GetChannelStateAsync("bus", 0);
        Assert.True(state.Muted);
    }

    [Fact]
    public async Task UnknownOperation_Throws()
    {
        var client = new FakeVoicemeeterClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VoicemeeterOperations.DispatchAsync(client, "Nope", null, CancellationToken.None));
    }

    [Fact]
    public async Task MissingArgument_Throws()
    {
        var client = new FakeVoicemeeterClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VoicemeeterOperations.DispatchAsync(client, "SetMute",
                Args(new { channelKind = "strip", index = 0 }), CancellationToken.None));
    }

    [Fact]
    public async Task GetEdition_ReturnsClientEdition()
    {
        var client = new FakeVoicemeeterClient { Edition = VoicemeeterEdition.Potato };
        var result = await VoicemeeterOperations.DispatchAsync(client, "GetEdition", null, CancellationToken.None);
        Assert.Equal(VoicemeeterEdition.Potato, result);
    }
}
