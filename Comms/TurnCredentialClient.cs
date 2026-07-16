using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatPlugin.VoiceChat;

internal static class TurnCredentialClient
{
    private const int MaxResponseBytes = 128 * 1024;
    private const int MaxIceServers = 32;
    private const int MaxUrlLength = 2048;
    private const int MaxUsernameLength = 512;
    private const int MaxCredentialLength = 512;
    public static readonly TimeSpan DefaultCredentialTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinCredentialTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxCredentialTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxRefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly object Sync = new();
    private static List<IceServer>? _cached;
    private static string _cachedSourceUrl = string.Empty;
    private static DateTime _fetchedAtUtc = DateTime.MinValue;
    private static DateTime _expiresAtUtc = DateTime.MinValue;
    private static DateTime _refreshAtUtc = DateTime.MinValue;

    public static IReadOnlyList<IceServer>? Cached
    {
        get { lock (Sync) return _cached == null ? null : new List<IceServer>(_cached); }
    }

    public static DateTime FetchedAtUtc
    {
        get { lock (Sync) return _fetchedAtUtc; }
    }

    public static bool NeedsRefresh(DateTime nowUtc, string? sourceUrl = null)
    {
        lock (Sync)
            return _cached == null || !SourceMatches(sourceUrl) || nowUtc >= _refreshAtUtc;
    }

    public static bool IsExpired(DateTime nowUtc, string? sourceUrl = null)
    {
        lock (Sync)
            return _cached == null || !SourceMatches(sourceUrl) || nowUtc >= _expiresAtUtc;
    }

    public static bool TryGetFreshCached(DateTime nowUtc, string sourceUrl, out List<IceServer> servers)
    {
        lock (Sync)
        {
            if (_cached == null || !SourceMatches(sourceUrl) || nowUtc >= _expiresAtUtc)
            {
                servers = new List<IceServer>();
                return false;
            }
            servers = new List<IceServer>(_cached);
            return true;
        }
    }

    public static async Task<List<IceServer>> FetchAsync(
        HttpClient http, string url, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("TURN credential response was too large");
        var json = await ReadBoundedUtf8Async(response.Content, cancellationToken).ConfigureAwait(false);
        var servers = ParseIceServers(json);
        if (!ContainsSupportedTurnServer(servers))
            throw new InvalidDataException("TURN credential response contained no supported TURN-over-UDP URLs");

        var ttl = ParseCredentialTtl(json);
        var fetchedAt = DateTime.UtcNow;
        var margin = TimeSpan.FromTicks(Math.Min(MaxRefreshMargin.Ticks, ttl.Ticks / 4));
        lock (Sync)
        {
            _cached = new List<IceServer>(servers);
            _cachedSourceUrl = url;
            _fetchedAtUtc = fetchedAt;
            _expiresAtUtc = fetchedAt + ttl;
            _refreshAtUtc = _expiresAtUtc - margin;
        }
        return servers;
    }

    internal static TimeSpan ParseCredentialTtl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("ttl", out var ttlElement))
                return DefaultCredentialTtl;

            double seconds;
            if (ttlElement.ValueKind == JsonValueKind.Number && ttlElement.TryGetDouble(out seconds))
            {
                // parsed below
            }
            else if (ttlElement.ValueKind == JsonValueKind.String &&
                     double.TryParse(
                         ttlElement.GetString(),
                         System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture,
                         out seconds))
            {
                // parsed below
            }
            else
            {
                return DefaultCredentialTtl;
            }

            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                return DefaultCredentialTtl;
            return TimeSpan.FromSeconds(Math.Clamp(
                seconds,
                MinCredentialTtl.TotalSeconds,
                MaxCredentialTtl.TotalSeconds));
        }
        catch
        {
            return DefaultCredentialTtl;
        }
    }

    public static List<IceServer> ParseIceServers(string json)
    {
        var result = new List<IceServer>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
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
                {
                    if (element.ValueKind == JsonValueKind.String)
                        AddServer(result, element.GetString(), username, credential);
                    if (result.Count >= MaxIceServers) break;
                }
            }
            else if (urls.ValueKind == JsonValueKind.String)
            {
                AddServer(result, urls.GetString(), username, credential);
            }
            if (result.Count >= MaxIceServers) break;
        }

        return result;
    }

    private static void AddServer(List<IceServer> result, string? url, string? username, string? credential)
    {
        if (result.Count >= MaxIceServers || string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        if (url.Length > MaxUrlLength || url.Any(char.IsWhiteSpace) || url.Any(char.IsControl)) return;

        var isStun = url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) && HasIceEndpoint(url);
        var isTurn = (url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
                      url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)) && HasIceEndpoint(url);
        if (!isStun && !isTurn) return;

        username ??= string.Empty;
        credential ??= string.Empty;
        if (username.Length > MaxUsernameLength || credential.Length > MaxCredentialLength) return;
        if (isTurn && (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(credential)))
            return;

        result.Add(new IceServer(url, isTurn ? username : string.Empty, isTurn ? credential : string.Empty));
    }

    private static bool ContainsSupportedTurnServer(IEnumerable<IceServer> servers)
        => servers.Any(server => PerfectCommsVoiceBackend.IsSupportedUdpTurnUrl(server.Urls));

    private static bool HasIceEndpoint(string url)
    {
        var colon = url.IndexOf(':');
        if (colon < 0 || colon + 1 >= url.Length) return false;
        var endpoint = url.Substring(colon + 1);
        if (endpoint.StartsWith("//", StringComparison.Ordinal)) endpoint = endpoint.Substring(2);
        var query = endpoint.IndexOf('?');
        if (query >= 0) endpoint = endpoint.Substring(0, query);
        return endpoint.Length > 0 && endpoint.IndexOf('@') < 0;
    }

    private static bool SourceMatches(string? sourceUrl)
        => string.IsNullOrEmpty(sourceUrl) ||
           string.Equals(_cachedSourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBoundedUtf8Async(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes)
                throw new InvalidDataException("TURN credential response was too large");
            output.Write(buffer, 0, read);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(output.ToArray());
    }
}
