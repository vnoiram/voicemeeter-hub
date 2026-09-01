using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoicemeeterHub;
using Xunit;

namespace VoicemeeterHub.Tests;

/// <summary>
///     End-to-end coverage over a real loopback WebSocket: the server (with a fake Remote API
///     client) is driven by a real <see cref="ClientWebSocket"/>, so the handshake, framing,
///     request/response correlation, and state subscription are all exercised together. Runs on
///     any OS because only the native DLL layer is faked.
/// </summary>
public class HubServerIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RequestResponse_And_StateSubscription_Work()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var fake = new FakeVoicemeeterClient { Edition = VoicemeeterEdition.Banana };
        using var server = new HubServer(fake);
        var serverTask = server.RunAsync(0, cts.Token);
        var port = await server.Listening.WaitAsync(cts.Token);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token);

        // First frame is the hello handshake.
        var hello = await ReceiveJsonAsync(ws, cts.Token);
        Assert.Equal("hello", hello.GetProperty("type").GetString());
        Assert.Equal(HubProtocol.Version, hello.GetProperty("protocol").GetInt32());

        // A request must come back with the same id and a success result.
        await SendAsync(ws, new { id = "r1", op = "SetMute", args = new { channelKind = "strip", index = 0, muted = true } }, cts.Token);
        var response = await ReceiveResponseAsync(ws, "r1", cts.Token);
        Assert.True(response.GetProperty("result").GetProperty("success").GetBoolean());

        // Subscribing yields an immediate state snapshot reflecting the mute we just set.
        await SendAsync(ws, new { id = "sub", op = "Subscribe" }, cts.Token);
        var stateEvent = await ReceiveEventAsync(ws, HubProtocol.StateTopic, cts.Token);
        var muted = stateEvent
            .GetProperty("data")
            .GetProperty("currentStates")
            .GetProperty("strip:0")
            .GetProperty("muted")
            .GetBoolean();
        Assert.True(muted);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        cts.Cancel();
        await serverTask;
    }

    private static async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, HubProtocol.JsonOptions);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cancellationToken);
            payload.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        var text = Encoding.UTF8.GetString(payload.ToArray());
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> ReceiveResponseAsync(ClientWebSocket ws, string id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReceiveJsonAsync(ws, cancellationToken);
            if (frame.GetProperty("type").GetString() == "response" &&
                frame.TryGetProperty("id", out var frameId) && frameId.GetString() == id)
                return frame;
        }
    }

    private static async Task<JsonElement> ReceiveEventAsync(ClientWebSocket ws, string topic, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReceiveJsonAsync(ws, cancellationToken);
            if (frame.GetProperty("type").GetString() == "event" &&
                frame.TryGetProperty("topic", out var frameTopic) && frameTopic.GetString() == topic)
                return frame;
        }
    }
}
