using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace VoiceChatPlugin.VoiceChat;

internal static class TurnCredentialClient
{
    public static readonly TimeSpan CredentialTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(30);

    private static readonly object Sync = new();
    private static List<RTCIceServer>? _cached;
    private static DateTime _fetchedAtUtc = DateTime.MinValue;

    public static IReadOnlyList<RTCIceServer>? Cached
    {
        get { lock (Sync) return _cached; }
    }

    public static DateTime FetchedAtUtc
    {
        get { lock (Sync) return _fetchedAtUtc; }
    }

    public static bool NeedsRefresh(DateTime nowUtc)
    {
        lock (Sync)
            return _cached == null || nowUtc - _fetchedAtUtc >= CredentialTtl - RefreshMargin;
    }

    public static async Task<List<RTCIceServer>> FetchAsync(
        HttpClient http, string url, CancellationToken cancellationToken = default)
    {
        var json = await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        var servers = ParseIceServers(json);
        lock (Sync)
        {
            _cached = servers;
            _fetchedAtUtc = DateTime.UtcNow;
        }
        return servers;
    }

    public static List<RTCIceServer> ParseIceServers(string json)
    {
        var result = new List<RTCIceServer>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("iceServers", out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var username = entry.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            var credential = entry.TryGetProperty("credential", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            if (!entry.TryGetProperty("urls", out var urls)) continue;

            if (urls.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in urls.EnumerateArray())
                    AddServer(result, element.GetString(), username, credential);
            }
            else if (urls.ValueKind == JsonValueKind.String)
            {
                AddServer(result, urls.GetString(), username, credential);
            }
        }

        return result;
    }

    private static void AddServer(List<RTCIceServer> result, string? url, string? username, string? credential)
    {
        if (string.IsNullOrEmpty(url)) return;
        var server = new RTCIceServer { urls = url };
        if (!string.IsNullOrEmpty(username)) server.username = username;
        if (!string.IsNullOrEmpty(credential)) server.credential = credential;
        result.Add(server);
    }
}
