using System.Text.Json;
using VoicemeeterHub;
using Xunit;

namespace VoicemeeterHub.Tests;

public class HubProtocolTests
{
    [Fact]
    public void HubRequest_DeserializesOpAndArgs()
    {
        const string json = """{"id":"abc","op":"SetMute","args":{"channelKind":"strip","index":0,"muted":true}}""";
        var request = JsonSerializer.Deserialize<HubRequest>(json, HubProtocol.JsonOptions);

        Assert.NotNull(request);
        Assert.Equal("abc", request!.Id);
        Assert.Equal("SetMute", request.Op);
        Assert.NotNull(request.Args);
        Assert.True(request.Args!["muted"].GetBoolean());
        Assert.Equal("strip", request.Args["channelKind"].GetString());
    }

    [Fact]
    public void OkResponse_SerializesTypeIdAndResult()
    {
        var message = HubMessage.OkResponse("id1", VoicemeeterOperationResult.Ok("Strip[0].Mute"));
        var json = JsonSerializer.Serialize(message, HubProtocol.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("response", root.GetProperty("type").GetString());
        Assert.Equal("id1", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("result").GetProperty("success").GetBoolean());
        // Absent fields must be omitted, not emitted as null noise.
        Assert.False(root.TryGetProperty("topic", out _));
    }

    [Fact]
    public void Hello_CarriesProtocolVersion()
    {
        var message = HubMessage.Hello("0.1.0");
        var json = JsonSerializer.Serialize(message, HubProtocol.JsonOptions);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("hello", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(HubProtocol.Version, doc.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal("voicemeeter-hub", doc.RootElement.GetProperty("server").GetString());
    }

    [Fact]
    public void Event_CarriesTopicAndData()
    {
        var message = HubMessage.Event(HubProtocol.StateTopic, new { hello = "world" });
        var json = JsonSerializer.Serialize(message, HubProtocol.JsonOptions);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("event", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("state", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal("world", doc.RootElement.GetProperty("data").GetProperty("hello").GetString());
    }

    [Fact]
    public void EndpointInfo_RoundTrips()
    {
        var info = new HubEndpointInfo(50505, 123, HubProtocol.Version, HubProtocol.ServerName, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(info, HubProtocol.JsonOptions);
        var back = JsonSerializer.Deserialize<HubEndpointInfo>(json, HubProtocol.JsonOptions);

        Assert.NotNull(back);
        Assert.Equal(50505, back!.Port);
        Assert.Equal(123, back.Pid);
        Assert.Equal(HubProtocol.ServerName, back.Server);
    }
}
