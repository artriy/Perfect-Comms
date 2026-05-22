using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

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
    [JsonPropertyName("ttlSeconds")] public int TtlSeconds { get; set; } = 120;
}

internal sealed class VoiceLobbyHeartbeatRequest
{
    [JsonPropertyName("ownerToken")] public string OwnerToken { get; set; } = "";
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("ttlSeconds")] public int TtlSeconds { get; set; } = 120;
}

internal sealed class VoiceLobbyDeleteRequest
{
    [JsonPropertyName("ownerToken")] public string OwnerToken { get; set; } = "";
}

internal sealed class VoiceLobbyListResponse
{
    [JsonPropertyName("lobbies")] public List<VoiceLobbyListing> Lobbies { get; set; } = new();
}

internal static class VoiceLobbyRegistryClient
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static async Task<IReadOnlyList<VoiceLobbyListing>> ListAsync(string registryUrl)
    {
        var url = BuildUrl(registryUrl, "/lobbies");
        using var response = await Client.GetAsync(url).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var parsed = JsonSerializer.Deserialize<VoiceLobbyListResponse>(text, JsonOptions);
        return parsed?.Lobbies ?? new List<VoiceLobbyListing>();
    }

    internal static Task PublishAsync(string registryUrl, VoiceLobbyPublishRequest request)
        => SendJsonAsync(HttpMethod.Post, BuildUrl(registryUrl, "/lobbies"), request);

    internal static Task HeartbeatAsync(string registryUrl, string id, VoiceLobbyHeartbeatRequest request)
        => SendJsonAsync(HttpMethod.Post, BuildUrl(registryUrl, $"/lobbies/{Uri.EscapeDataString(id)}/heartbeat"), request);

    internal static Task DeleteAsync(string registryUrl, string id, string ownerToken)
        => SendJsonAsync(HttpMethod.Delete, BuildUrl(registryUrl, $"/lobbies/{Uri.EscapeDataString(id)}"),
            new VoiceLobbyDeleteRequest { OwnerToken = ownerToken });

    private static async Task SendJsonAsync(HttpMethod method, string url, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException($"registry {method.Method} {url} failed {(int)response.StatusCode} {response.ReasonPhrase}: {text}");
        }
    }

    private static string BuildUrl(string registryUrl, string path)
    {
        var baseUrl = string.IsNullOrWhiteSpace(registryUrl)
            ? "https://perfect-comms-lobbies.edgetel.workers.dev"
            : registryUrl.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            baseUrl = "https://perfect-comms-lobbies.edgetel.workers.dev";
        return baseUrl.TrimEnd('/') + path;
    }
}
