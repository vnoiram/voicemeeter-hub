using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoicemeeterHub;

/// <summary>
///     Wire contract for the Voicemeeter Hub WebSocket API (protocol v1). Documented in
///     <c>docs/protocol.md</c>. Clients send one JSON object per text frame and receive one JSON
///     object per text frame. Every server frame carries a <c>type</c> discriminator.
/// </summary>
public static class HubProtocol
{
    public const int Version = 1;
    public const string ServerName = "voicemeeter-hub";

    /// <summary>Default loopback TCP port. Override with <c>VOICEMEETER_HUB_PORT</c>.</summary>
    public const int DefaultPort = 50505;

    public const string PortEnvironmentVariable = "VOICEMEETER_HUB_PORT";

    /// <summary>Single-instance guard. <c>Global\</c> so every session on the machine shares it.</summary>
    public const string MutexName = @"Global\VoicemeeterHub.Server.v1";

    /// <summary>The only push topic in v1: full <see cref="VoicemeeterSnapshot"/> broadcasts.</summary>
    public const string StateTopic = "state";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string EndpointFilePath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "voicemeeter-hub", "endpoint.json");
    }

    public static int ResolvePort()
    {
        var raw = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        return int.TryParse(raw, out var port) && port is > 0 and < 65536 ? port : DefaultPort;
    }
}

/// <summary>Client-to-server frame. <c>op</c> is required; <c>id</c> correlates the response.</summary>
public sealed record HubRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("args")] Dictionary<string, JsonElement>? Args = null);

/// <summary>Server-to-client frame. Exactly one of the response/event/hello shapes is populated.</summary>
public sealed record HubMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("result")] JsonElement? Result = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("topic")] string? Topic = null,
    [property: JsonPropertyName("data")] JsonElement? Data = null,
    [property: JsonPropertyName("server")] string? Server = null,
    [property: JsonPropertyName("protocol")] int? Protocol = null,
    [property: JsonPropertyName("version")] string? Version = null)
{
    public static HubMessage Hello(string? serverVersion) =>
        new("hello", Server: HubProtocol.ServerName, Protocol: HubProtocol.Version, Version: serverVersion);

    public static HubMessage OkResponse(string? id, object? result) =>
        new("response", Id: id, Result: Serialize(result));

    public static HubMessage ErrorResponse(string? id, string error) =>
        new("response", Id: id, Error: error);

    public static HubMessage Event(string topic, object? data) =>
        new("event", Topic: topic, Data: Serialize(data));

    private static JsonElement? Serialize(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, HubProtocol.JsonOptions);
}

/// <summary>Contents of <c>endpoint.json</c>, written by the running server for client discovery.</summary>
public sealed record HubEndpointInfo(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("startedUtc")] DateTimeOffset StartedUtc);
