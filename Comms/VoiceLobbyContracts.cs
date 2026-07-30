using System.Text.Json.Serialization;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class VoiceLobbyListing
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("stateChangedAt")] public long StateChangedAt { get; set; }
    [JsonPropertyName("modVersion")] public string ModVersion { get; set; } = "";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
}

internal sealed class VoiceLobbyPublishRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("ownerToken")] public string OwnerToken { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("modVersion")] public string ModVersion { get; set; } = "";
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; set; }
}
