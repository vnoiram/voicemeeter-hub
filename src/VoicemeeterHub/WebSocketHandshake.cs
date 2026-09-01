using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace VoicemeeterHub;

/// <summary>
///     Minimal RFC 6455 server handshake over a raw TCP stream. Using a TcpListener plus a manual
///     upgrade (instead of <see cref="System.Net.HttpListener"/>) avoids the HTTP.sys URL-ACL
///     reservation that would otherwise require administrator rights, so the hub runs as an
///     ordinary user process.
/// </summary>
internal static class WebSocketHandshake
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Computes the <c>Sec-WebSocket-Accept</c> value for a client key (RFC 6455 §4.2.2).</summary>
    public static string ComputeAcceptKey(string clientKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(clientKey + WebSocketGuid)));

    public static async Task<WebSocket?> AcceptAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        var stream = tcpClient.GetStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        var request = await ReadHeadersAsync(stream, timeout.Token);
        if (request == null) return null;

        if (!request.TryGetValue("sec-websocket-key", out var key) || string.IsNullOrWhiteSpace(key))
        {
            await WriteAsync(stream, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n", timeout.Token);
            return null;
        }

        var accept = ComputeAcceptKey(key);
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await WriteAsync(stream, response, timeout.Token);

        return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
    }

    private static async Task<Dictionary<string, string>?> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var used = 0;
        while (true)
        {
            if (used >= buffer.Length) return null; // header block too large
            var read = await stream.ReadAsync(buffer.AsMemory(used), cancellationToken);
            if (read == 0) return null;
            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            var terminator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (terminator < 0) continue;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = text[..terminator].Split("\r\n");
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            return headers;
        }
    }

    private static Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        return stream.WriteAsync(bytes, cancellationToken).AsTask();
    }
}
