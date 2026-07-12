using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class TurnCredentialParseTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls;
        public HttpMethod? Method;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Method = request.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"ttl\":3600,\"iceServers\":[{\"urls\":\"turn:relay.example:3478\",\"username\":\"u\",\"credential\":\"c\"}]}")
            });
        }
    }

    private const string LiveJson =
        "{\"iceServers\":[{\"urls\":[\"stun:stun.cloudflare.com:3478\",\"stun:stun.cloudflare.com:53\"]}," +
        "{\"urls\":[\"turn:turnv2.realtime.cloudflare.com:3478?transport=udp\"," +
        "\"turn:turn.cloudflare.com:3478?transport=tcp\"," +
        "\"turns:turn.cloudflare.com:5349?transport=tcp\"]," +
        "\"username\":\"user-abc\",\"credential\":\"cred-xyz\"}]}";

    [Fact]
    public void StunEntryYieldsUrlOnlyServers()
    {
        var servers = TurnCredentialClient.ParseIceServers(LiveJson);

        var stun = servers.Where(s => s.Urls.StartsWith("stun:")).ToList();
        Xunit.Assert.Equal(2, stun.Count);
        Xunit.Assert.Contains(stun, s => s.Urls == "stun:stun.cloudflare.com:3478");
        Xunit.Assert.Contains(stun, s => s.Urls == "stun:stun.cloudflare.com:53");
        Xunit.Assert.All(stun, s => Xunit.Assert.Empty(s.Username));
        Xunit.Assert.All(stun, s => Xunit.Assert.Empty(s.Credential));
    }

    [Fact]
    public void TurnEntryYieldsServersWithCredentials()
    {
        var servers = TurnCredentialClient.ParseIceServers(LiveJson);

        var turn = servers.Where(s => s.Urls.StartsWith("turn:") || s.Urls.StartsWith("turns:")).ToList();
        Xunit.Assert.Equal(3, turn.Count);
        Xunit.Assert.All(turn, s => Xunit.Assert.Equal("user-abc", s.Username));
        Xunit.Assert.All(turn, s => Xunit.Assert.Equal("cred-xyz", s.Credential));
        Xunit.Assert.Contains(turn, s => s.Urls == "turn:turnv2.realtime.cloudflare.com:3478?transport=udp");
        Xunit.Assert.Contains(turn, s => s.Urls == "turns:turn.cloudflare.com:5349?transport=tcp");
    }

    [Fact]
    public void ExpandsEveryUrlIntoOneServer()
    {
        var servers = TurnCredentialClient.ParseIceServers(LiveJson);
        Xunit.Assert.Equal(5, servers.Count);
    }

    [Fact]
    public void RejectsUncredentialedAndUnsupportedRelayUrls()
    {
        const string json = "{\"iceServers\":[" +
                            "{\"urls\":[\"turn:relay.example:3478\",\"https://not-ice.example\"]}," +
                            "{\"urls\":\"turn:?transport=udp\",\"username\":\"u\",\"credential\":\"c\"}," +
                            "{\"urls\":\"turns:relay.example:5349\",\"username\":\"u\",\"credential\":\"c\"}]}";

        var servers = TurnCredentialClient.ParseIceServers(json);

        var relay = Xunit.Assert.Single(servers);
        Xunit.Assert.Equal("turns:relay.example:5349", relay.Urls);
        Xunit.Assert.Equal("u", relay.Username);
        Xunit.Assert.Equal("c", relay.Credential);
    }

    [Theory]
    [InlineData("{\"ttl\":3600}", 3600)]
    [InlineData("{\"ttl\":\"1800\"}", 1800)]
    [InlineData("{\"ttl\":1}", 300)]
    [InlineData("{\"ttl\":999999}", 86400)]
    [InlineData("{}", 3600)]
    public void CredentialTtlUsesWorkerValueWithSafeBounds(string json, int expectedSeconds)
    {
        Xunit.Assert.Equal(expectedSeconds, TurnCredentialClient.ParseCredentialTtl(json).TotalSeconds);
    }

    [Fact]
    public async Task CredentialFetchUsesOnePostRequest()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);

        var servers = await TurnCredentialClient.FetchAsync(http, "https://worker.example/turn-credentials");

        Xunit.Assert.Equal(1, handler.Calls);
        Xunit.Assert.Equal(HttpMethod.Post, handler.Method);
        Xunit.Assert.Single(servers);
    }
}
