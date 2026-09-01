using VoicemeeterHub;
using Xunit;

namespace VoicemeeterHub.Tests;

public class WebSocketHandshakeTests
{
    [Fact]
    public void ComputeAcceptKey_MatchesRfc6455Vector()
    {
        // RFC 6455 §1.3 worked example.
        var accept = WebSocketHandshake.ComputeAcceptKey("dGhlIHNhbXBsZSBub25jZQ==");
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", accept);
    }
}
