using System.Text.Json;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceLobbyLiveProtocolTests
{
    [Theory]
    [InlineData("https://perfect-comms-lobbies.example", "browser", "wss://perfect-comms-lobbies.example/lobbies/live?role=browser")]
    [InlineData("https://example.com/api/", "host", "wss://example.com/api/lobbies/live?role=host")]
    public void WebSocketEndpointUsesSecureCanonicalLiveRoute(
        string registryUrl,
        string role,
        string expected)
    {
        Assert.Equal(expected, VoiceLobbyLiveProtocol.BuildWebSocketUri(registryUrl, role).AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("http://insecure.example")]
    public void InvalidRegistryEndpointFallsBackToOwnedService(string registryUrl)
    {
        var uri = VoiceLobbyLiveProtocol.BuildWebSocketUri(registryUrl, "browser");

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("perfect-comms-lobbies.edgetel.workers.dev", uri.Host);
        Assert.Equal("/lobbies/live?role=browser", uri.PathAndQuery);
    }

    [Fact]
    public void PublishEnvelopeCarriesWireVersionOwnershipAndCurrentLobbyState()
    {
        var payload = VoiceLobbyLiveProtocol.SerializePublish(new VoiceLobbyPublishRequest
        {
            Id = "listing-id",
            OwnerToken = "owner-token-at-least-16-characters",
            Code = "ABCDEF",
            Region = "North America",
            Language = "English",
            Title = "Crew Night",
            Host = "Alice",
            Players = 4,
            MaxPlayers = 15,
            State = "Lobby",
            ModVersion = "4.1.7",
            ProtocolVersion = 5,
        });

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("wire").GetInt32());
        Assert.Equal("publish", root.GetProperty("type").GetString());
        var lobby = root.GetProperty("lobby");
        Assert.Equal("listing-id", lobby.GetProperty("id").GetString());
        Assert.Equal("owner-token-at-least-16-characters", lobby.GetProperty("ownerToken").GetString());
        Assert.Equal("North America", lobby.GetProperty("region").GetString());
        Assert.Equal("Lobby", lobby.GetProperty("state").GetString());
        Assert.False(lobby.TryGetProperty("ttlSeconds", out _));
    }

    [Fact]
    public void SnapshotContractDeserializesCurrentStateAndRegion()
    {
        const string payload = """
            {
              "wire": 1,
              "type": "snapshot",
              "lobbies": [{
                "id": "listing-id",
                "code": "ABCDEF",
                "region": "Modded EU (MEU)",
                "language": "English",
                "title": "Crew Night",
                "host": "Alice",
                "players": 6,
                "maxPlayers": 15,
                "state": "InGame",
                "stateChangedAt": 123,
                "modVersion": "4.1.7",
                "protocolVersion": 5,
                "updatedAt": 124,
                "expiresAt": 214
              }]
            }
            """;

        var envelope = JsonSerializer.Deserialize<VoiceLobbyLiveEnvelope>(
            payload,
            VoiceLobbyLiveProtocol.JsonOptions);

        Assert.NotNull(envelope);
        Assert.Equal(VoiceLobbyLiveProtocol.WireVersion, envelope.Wire);
        var listing = Assert.Single(envelope.Lobbies!);
        Assert.Equal("Modded EU (MEU)", listing.Region);
        Assert.Equal("InGame", listing.State);
        Assert.Equal(123, listing.StateChangedAt);
    }
}
