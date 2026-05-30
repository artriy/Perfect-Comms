using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using InnerNet;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class VanillaLobbyModMetadata
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public int Flags { get; init; }

    public string DisplayName => Id switch
    {
        VoiceChatPluginMain.Id => "Perfect Comms",
        "gg.reactor.api" => "Reactor",
        "mira.api" => "MiraAPI",
        "auavengers.tou.mira" => "TOU Mira",
        _ => Id
    };
}

internal sealed class VanillaLobbyMetadata
{
    public string Code { get; init; } = "";
    public string HostName { get; init; } = "";
    public string Status { get; init; } = "";
    public int PlayerCount { get; init; }
    public int MaxPlayers { get; init; }
    public int ChatMode { get; init; }
    public int ChatLanguage { get; init; }
    public int MapId { get; init; }
    public int RegionId { get; init; }
    public IReadOnlyList<VanillaLobbyModMetadata> Mods { get; init; } = Array.Empty<VanillaLobbyModMetadata>();

    public string StatusLabel => string.IsNullOrWhiteSpace(Status) ? "Unknown" : Status.Trim();

    public bool HasMod(string id)
    {
        foreach (var mod in Mods)
        {
            if (string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public string GetModSummary(int maxMods)
    {
        if (Mods.Count == 0) return "None reported";

        var parts = new List<string>(Math.Min(maxMods, Mods.Count));
        for (var i = 0; i < Mods.Count && parts.Count < maxMods; i++)
        {
            var mod = Mods[i];
            if (string.IsNullOrWhiteSpace(mod.Id)) continue;
            parts.Add(string.IsNullOrWhiteSpace(mod.Version)
                ? mod.DisplayName
                : $"{mod.DisplayName} {mod.Version}");
        }

        if (Mods.Count > parts.Count) parts.Add($"+{Mods.Count - parts.Count} more");
        return parts.Count == 0 ? "None reported" : string.Join(", ", parts);
    }
}

internal static class VanillaLobbyPublicApiClient
{
    internal const string DefaultApiUrl = "https://au-eu.duikbo.at/public_api/games";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static async Task<IReadOnlyDictionary<string, VanillaLobbyMetadata>> FetchAsync()
    {
        VanillaLobbyDiagnostics.Throttled("api.fetch.start", "api", $"GET {DefaultApiUrl}", 1.0);
        using var response = await Client.GetAsync(DefaultApiUrl).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        VanillaLobbyDiagnostics.Throttled("api.fetch.done", "api", $"status={(int)response.StatusCode} bytes={text.Length} contentType={response.Content.Headers.ContentType}", 1.0);
        response.EnsureSuccessStatusCode();
        var parsed = ParseGamesJson(text);
        VanillaLobbyDiagnostics.Throttled("api.fetch.parsed", "api", $"parsed={parsed.Count} codes=[{string.Join(",", parsed.Keys)}]", 1.0);
        return parsed;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new FlexibleIntConverter());
        return options;
    }

    internal static IReadOnlyDictionary<string, VanillaLobbyMetadata> ParseGamesJson(string json)
    {
        var result = new Dictionary<string, VanillaLobbyMetadata>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            VanillaLobbyDiagnostics.Warning("api", "empty JSON body");
            return result;
        }

        using var document = JsonDocument.Parse(json);
        var gamesElement = ResolveGamesElement(document.RootElement);
        if (gamesElement.ValueKind != JsonValueKind.Array)
        {
            VanillaLobbyDiagnostics.Warning("api", $"no games array rootKind={document.RootElement.ValueKind}");
            return result;
        }

        var games = JsonSerializer.Deserialize<List<VanillaLobbyApiGame>>(gamesElement.GetRawText(), JsonOptions)
            ?? new List<VanillaLobbyApiGame>();

        foreach (var game in games)
        {
            var code = NormalizeCode(game.Code);
            if (code.Length == 0) continue;

            result[code] = new VanillaLobbyMetadata
            {
                Code = code,
                HostName = Clamp(game.HostName, 32, "Unknown host"),
                Status = Clamp(game.Status, 24, "Unknown"),
                PlayerCount = Math.Max(0, game.PlayerCount),
                MaxPlayers = Math.Max(game.MaxPlayers, game.PlayerCount),
                ChatMode = game.ChatMode,
                ChatLanguage = game.ChatLanguage,
                MapId = game.MapId,
                RegionId = game.RegionId,
                Mods = BuildMods(game.Mods)
            };
        }

        VanillaLobbyDiagnostics.Limited("api.parse", "api", $"games={games.Count} usable={result.Count} codes=[{string.Join(",", result.Keys)}]", first: 4, every: 30);
        return result;
    }

    internal static string NormalizeCode(string? code)
        => (code ?? "").Trim().ToUpperInvariant();

    private static JsonElement ResolveGamesElement(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return default;
        if (root.TryGetProperty("games", out var games)) return games;
        if (root.TryGetProperty("lobbies", out var lobbies)) return lobbies;
        return default;
    }

    private static IReadOnlyList<VanillaLobbyModMetadata> BuildMods(List<VanillaLobbyApiMod>? mods)
    {
        if (mods == null || mods.Count == 0) return Array.Empty<VanillaLobbyModMetadata>();

        var result = new List<VanillaLobbyModMetadata>(mods.Count);
        foreach (var mod in mods)
        {
            var id = Clamp(mod.Id, 96, "");
            if (id.Length == 0) continue;
            result.Add(new VanillaLobbyModMetadata
            {
                Id = id,
                Version = Clamp(mod.Version, 64, ""),
                Flags = mod.Flags
            });
        }

        return result;
    }

    private static string Clamp(string? value, int max, string fallback)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) text = fallback;
        return text.Length <= max ? text : text[..max];
    }

    private sealed class VanillaLobbyApiGame
    {
        [JsonPropertyName("code")] public string Code { get; set; } = "";
        [JsonPropertyName("host_name")] public string HostName { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("player_count")] public int PlayerCount { get; set; }
        [JsonPropertyName("max_players")] public int MaxPlayers { get; set; }
        [JsonPropertyName("chat_mode")] public int ChatMode { get; set; }
        [JsonPropertyName("chat_lang")] public int ChatLanguage { get; set; }
        [JsonPropertyName("map_id")] public int MapId { get; set; }
        [JsonPropertyName("region_id")] public int RegionId { get; set; }
        [JsonPropertyName("mods")] public List<VanillaLobbyApiMod> Mods { get; set; } = new();
    }

    private sealed class VanillaLobbyApiMod
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("flags")] public int Flags { get; set; }
    }

    private sealed class FlexibleIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)) return value;
            if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out value)) return value;
            return 0;
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }
}

internal static class VanillaLobbyMetadataCache
{
    private static readonly Dictionary<string, VanillaLobbyMetadata> Games = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(8);
    private static Task? _pending;
    private static DateTime _nextRefreshUtc = DateTime.MinValue;
    private static bool _dirty;
    private static string? _lastWarning;

    internal static void BeginRefresh(bool force = false)
    {
        lock (Gate)
        {
            if (_pending is { IsCompleted: false })
            {
                VanillaLobbyDiagnostics.Throttled("cache.refresh.pending", "cache", $"skip refresh: pending force={force} cached={Games.Count}", 2.0);
                return;
            }
            var now = DateTime.UtcNow;
            if (!force && now < _nextRefreshUtc)
            {
                VanillaLobbyDiagnostics.Throttled("cache.refresh.wait", "cache", $"skip refresh: next={_nextRefreshUtc:O} cached={Games.Count} dirty={_dirty}", 2.0);
                return;
            }

            _nextRefreshUtc = now.Add(RefreshInterval);
            VanillaLobbyDiagnostics.Info("cache", $"starting refresh force={force} cached={Games.Count} next={_nextRefreshUtc:O}");
            _pending = FetchAndStoreAsync();
        }
    }

    internal static bool TryGet(string code, out VanillaLobbyMetadata metadata)
    {
        var normalized = VanillaLobbyPublicApiClient.NormalizeCode(code);
        lock (Gate)
        {
            var found = Games.TryGetValue(normalized, out metadata!);
            VanillaLobbyDiagnostics.Limited("cache.tryget.code", "cache", $"code='{normalized}' found={found} cached={Games.Count}", first: 20, every: 120);
            return found;
        }
    }

    internal static bool TryGet(GameListing listing, out VanillaLobbyMetadata metadata)
    {
        var code = ResolveCode(listing.GameId);
        if (TryGet(code, out metadata))
        {
            VanillaLobbyDiagnostics.Limited("cache.tryget.listing.exact", "cache", $"exact code={code} gameId={listing.GameId} map={listing.MapId} players={listing.PlayerCount}/{listing.MaxPlayers}", first: 20, every: 120);
            return true;
        }

        // No fuzzy map+player-count fallback: a coincidental match (e.g. a Skeld 1/15 lobby) would
        // attach an unrelated lobby's host/status to this row. Only exact game-code matches are
        // trusted; on a miss the caller falls back to neutral metadata.
        VanillaLobbyDiagnostics.Limited("cache.tryget.listing.miss", "cache", $"miss code={code} gameId={listing.GameId} map={listing.MapId} players={listing.PlayerCount}/{listing.MaxPlayers} cached={Games.Count}", first: 20, every: 120);
        metadata = null!;
        return false;
    }

    internal static bool ConsumeDirty()
    {
        lock (Gate)
        {
            if (!_dirty) return false;
            _dirty = false;
            return true;
        }
    }

    private static string ResolveCode(int gameId)
    {
        try { return VanillaLobbyPublicApiClient.NormalizeCode(GameCode.IntToGameName(gameId)); }
        catch { return ""; }
    }

    private static async Task FetchAndStoreAsync()
    {
        try
        {
            var fetched = await VanillaLobbyPublicApiClient.FetchAsync().ConfigureAwait(false);
            lock (Gate)
            {
                Games.Clear();
                foreach (var pair in fetched) Games[pair.Key] = pair.Value;
                _dirty = true;
                _lastWarning = null;
                VanillaLobbyDiagnostics.Info("cache", $"refresh stored count={Games.Count} codes=[{string.Join(",", Games.Keys)}]");
            }
        }
        catch (Exception ex)
        {
            var warning = ex.Message;
            var shouldLog = false;
            lock (Gate)
            {
                if (!string.Equals(_lastWarning, warning, StringComparison.Ordinal))
                {
                    _lastWarning = warning;
                    shouldLog = true;
                }
            }

            if (shouldLog)
                VoiceDiagnostics.DebugWarning($"[VC] Vanilla lobby metadata fetch failed: {warning}");
        }
    }
}
