using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using InnerNet;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch(typeof(FindAGameManager), nameof(FindAGameManager.RefreshList))]
internal static class FindAGameManagerRefreshListPatch
{
    [HarmonyPostfix]
    public static void Postfix()
        => VanillaLobbyMetadataCache.BeginRefresh(force: true);
}

[HarmonyPatch(typeof(FindAGameManager), nameof(FindAGameManager.Update))]
internal static class FindAGameManagerUpdatePatch
{
    private const float UiRefreshIntervalSeconds = 0.25f;
    private static float _nextRefreshTime;

    [HarmonyPostfix]
    public static void Postfix(FindAGameManager __instance)
    {
        VanillaLobbyMetadataCache.BeginRefresh();
        if (Time.unscaledTime < _nextRefreshTime && !VanillaLobbyMetadataCache.ConsumeDirty()) return;

        _nextRefreshTime = Time.unscaledTime + UiRefreshIntervalSeconds;
        VanillaLobbyBrowserRowUi.RefreshVisibleRows(__instance);
    }
}

[HarmonyPatch(typeof(FindAGameManager), nameof(FindAGameManager.HandleList))]
internal static class FindAGameManagerHandleListPatch
{
    [HarmonyPostfix]
    public static void Postfix(FindAGameManager __instance, InnerNetClient.TotalGameData totalGames, HttpMatchmakerManager.FindGamesListFilteredResponse response)
    {
        VanillaLobbyBrowserRowUi.RememberList(response?.Games);
        VanillaLobbyMetadataCache.BeginRefresh();
        VanillaLobbyBrowserRowUi.RefreshVisibleRows(__instance);
    }
}

[HarmonyPatch(typeof(GameContainer), nameof(GameContainer.SetGameListing))]
internal static class GameContainerSetGameListingPatch
{
    [HarmonyPostfix]
    public static void Postfix(GameContainer __instance, GameListing gameL)
    {
        VanillaLobbyBrowserRowUi.Remember(__instance, gameL);
        VanillaLobbyBrowserRowUi.Apply(__instance, gameL);
    }
}

[HarmonyPatch(typeof(GameContainer), nameof(GameContainer.SetupGameInfo))]
internal static class GameContainerSetupGameInfoPatch
{
    [HarmonyPostfix]
    public static void Postfix(GameContainer __instance)
    {
        if (VanillaLobbyBrowserRowUi.TryReadListing(__instance, out var gameListing))
            VanillaLobbyBrowserRowUi.Apply(__instance, gameListing);
    }
}

[HarmonyPatch(typeof(FindGameMoreInfoPopup), nameof(FindGameMoreInfoPopup.SetupInfo))]
internal static class FindGameMoreInfoPopupSetupInfoPatch
{
    [HarmonyPostfix]
    public static void Postfix(FindGameMoreInfoPopup __instance, GameListing gameL)
        => VanillaLobbyMoreInfoUi.Apply(__instance, gameL);
}

internal readonly struct VanillaLobbyDisplayData
{
    internal VanillaLobbyDisplayData(GameListing listing, VanillaLobbyMetadata metadata, bool fromApi, TimeSpan statusAge)
    {
        Listing = listing;
        Metadata = metadata;
        FromApi = fromApi;
        StatusAge = statusAge;
    }

    internal GameListing Listing { get; }
    internal VanillaLobbyMetadata Metadata { get; }
    internal bool FromApi { get; }
    internal TimeSpan StatusAge { get; }
    internal string Code => string.IsNullOrWhiteSpace(Metadata.Code) ? ResolveCode(Listing.GameId) : Metadata.Code;
    internal string Host => string.IsNullOrWhiteSpace(Metadata.HostName) ? ResolveHostName(Listing) : Metadata.HostName;
    internal string Status => FormatStatusLabel(Metadata.StatusLabel);
    internal int Players => Metadata.PlayerCount > 0 ? Metadata.PlayerCount : Listing.PlayerCount;
    internal int MaxPlayers => Metadata.MaxPlayers > 0 ? Metadata.MaxPlayers : Listing.MaxPlayers;
    internal string PlayerText => $"{Players}/{MaxPlayers}";
    internal string StatusText => $"{Status} {FormatStatusAge(StatusAge)}";
    internal string HostTagText => $"Host: {Host}";
    internal string StatusTagText => StatusText;
    internal string MapText => ResolveMapName(Metadata.MapId != 0 ? Metadata.MapId : Listing.MapId);
    internal string ModsText => Metadata.GetModSummary(5);

    internal static VanillaLobbyDisplayData Build(GameListing listing)
    {
        var fromApi = VanillaLobbyMetadataCache.TryGet(listing, out var apiMetadata);
        var metadata = fromApi ? apiMetadata : BuildFallbackMetadata(listing);
        var code = string.IsNullOrWhiteSpace(metadata.Code) ? ResolveCode(listing.GameId) : metadata.Code;
        var status = FormatStatusLabel(metadata.StatusLabel);
        var statusAge = VanillaLobbyStatusAgeTracker.Observe(code, status);
        return new VanillaLobbyDisplayData(listing, metadata, fromApi, statusAge);
    }

    private static VanillaLobbyMetadata BuildFallbackMetadata(GameListing listing)
    {
        return new VanillaLobbyMetadata
        {
            Code = ResolveCode(listing.GameId),
            HostName = ResolveHostName(listing),
            Status = "Lobby",
            PlayerCount = listing.PlayerCount,
            MaxPlayers = listing.MaxPlayers,
            MapId = listing.MapId
        };
    }

    internal static string ResolveHostName(GameListing listing)
    {
        if (!string.IsNullOrWhiteSpace(listing.HostName)) return listing.HostName.Trim();
        if (!string.IsNullOrWhiteSpace(listing.TrueHostName)) return listing.TrueHostName.Trim();
        if (!string.IsNullOrWhiteSpace(listing.HostPlatformName)) return listing.HostPlatformName.Trim();
        return "Unknown host";
    }

    internal static string ResolveCode(int gameId)
    {
        try { return VanillaLobbyPublicApiClient.NormalizeCode(GameCode.IntToGameName(gameId)); }
        catch { return ""; }
    }

    private static string FormatStatusLabel(string status)
    {
        status = string.IsNullOrWhiteSpace(status) ? "Lobby" : status.Trim();
        return status.Replace("_", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.OrdinalIgnoreCase) switch
        {
            "InGame" => "In Game",
            "Started" => "In Game",
            "Game" => "In Game",
            _ => status
        };
    }

    private static string FormatStatusAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalHours >= 1
            ? $"{(int)age.TotalHours}h{age.Minutes:00}m"
            : $"{(int)age.TotalMinutes}:{age.Seconds:00}";
    }

    private static string ResolveMapName(int mapId) => mapId switch
    {
        0 => "The Skeld",
        1 => "MIRA HQ",
        2 => "Polus",
        3 => "The Airship",
        4 => "The Fungle",
        _ => $"Map {mapId}"
    };
}

internal static class VanillaLobbyStatusAgeTracker
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    internal static TimeSpan Observe(string code, string status)
    {
        var now = DateTime.UtcNow;
        code = VanillaLobbyPublicApiClient.NormalizeCode(code);
        status = string.IsNullOrWhiteSpace(status) ? "Lobby" : status.Trim();
        if (code.Length == 0) return TimeSpan.Zero;

        lock (Gate)
        {
            if (!Entries.TryGetValue(code, out var entry) || !string.Equals(entry.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                Entries[code] = new Entry(status, now);
                return TimeSpan.Zero;
            }

            return now - entry.SinceUtc;
        }
    }

    private readonly struct Entry
    {
        internal Entry(string status, DateTime sinceUtc)
        {
            Status = status;
            SinceUtc = sinceUtc;
        }

        internal string Status { get; }
        internal DateTime SinceUtc { get; }
    }
}

internal static class VanillaLobbyBrowserRowUi
{
    private static readonly bool ShowExtraDetails = false;
    private static readonly Dictionary<int, GameListing> Listings = new();
    private static readonly Dictionary<int, TextMeshPro> RowDetailsByContainer = new();
    private static readonly List<GameListing> LastResponseListings = new();
    private static readonly FieldInfo? GameListingField = SafeField(typeof(GameContainer), "gameListing");
    private static readonly FieldInfo? Tag1Field = SafeField(typeof(GameContainer), "tag1");
    private static readonly FieldInfo? Tag2Field = SafeField(typeof(GameContainer), "tag2");
    private static readonly FieldInfo? CapacityField = SafeField(typeof(GameContainer), "capacity");
    private static readonly FieldInfo? GameContainersField = SafeField(typeof(FindAGameManager), "gameContainers");
    private static string? _lastWarning;

    private static FieldInfo? SafeField(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    internal static void RefreshVisibleRows(FindAGameManager manager)
    {
        try
        {
            var containers = ResolveContainers(manager);
            for (var i = 0; i < containers.Count; i++)
            {
                var container = containers[i];
                if (!TryReadListing(container, out var listing) && !TryReadResponseListing(i, out listing)) continue;
                Apply(container, listing);
            }
        }
        catch (Exception ex)
        {
            WarnOnce($"row refresh failed: {ex.Message}");
        }
    }

    private static List<GameContainer> ResolveContainers(FindAGameManager manager)
    {
        var result = new List<GameContainer>();
        if (GameContainersField?.GetValue(manager) is GameContainer[] containers)
            result.AddRange(containers);
        else
            result.AddRange(Object.FindObjectsOfType<GameContainer>());

        result.RemoveAll(container => container == null || !container.gameObject.activeInHierarchy);
        result.Sort((left, right) =>
        {
            var y = right.transform.position.y.CompareTo(left.transform.position.y);
            return y != 0 ? y : left.transform.position.x.CompareTo(right.transform.position.x);
        });
        return result;
    }

    internal static void Remember(GameContainer container, GameListing listing)
        => Listings[container.GetInstanceID()] = listing;

    internal static void RememberList(Il2CppSystem.Collections.Generic.List<GameListing>? listings)
    {
        LastResponseListings.Clear();
        if (listings == null) return;

        for (var i = 0; i < listings.Count; i++)
            LastResponseListings.Add(listings[i]);
    }

    private static bool TryReadResponseListing(int index, out GameListing listing)
    {
        if (index >= 0 && index < LastResponseListings.Count)
        {
            listing = LastResponseListings[index];
            return listing.GameId != 0;
        }

        listing = default!;
        return false;
    }

    internal static bool TryReadListing(GameContainer container, out GameListing gameListing)
    {
        gameListing = default!;
        try
        {
            if (GameListingField?.GetValue(container) is GameListing listing)
            {
                gameListing = listing;
                return gameListing.GameId != 0;
            }

            if (Listings.TryGetValue(container.GetInstanceID(), out var remembered))
            {
                gameListing = remembered;
                return gameListing.GameId != 0;
            }

            return false;
        }
        catch (Exception ex)
        {
            WarnOnce($"read listing failed: {ex.Message}");
            return false;
        }
    }

    internal static void Apply(GameContainer container, GameListing gameListing)
    {
        try
        {
            var data = VanillaLobbyDisplayData.Build(gameListing);
            SetBoxText(container, Tag1Field, IsHostBox, data.HostTagText, "host");
            SetBoxText(container, Tag2Field, IsStatusBox, data.StatusTagText, "status");
            SetBoxText(container, CapacityField, IsCapacityText, data.PlayerText, "capacity");
            if (ShowExtraDetails)
                ApplyDetails(container, data);
            else
                HideDetails(container);
        }
        catch (Exception ex)
        {
            WarnOnce($"row metadata failed: {ex.Message}");
        }
    }

    private static TextMeshPro? SetBoxText(GameContainer container, FieldInfo? field, Func<string, bool> predicate, string text, string label)
    {
        if (field?.GetValue(container) is TextMeshPro fieldText)
        {
            fieldText.text = text;
            return fieldText;
        }

        foreach (var tmp in container.gameObject.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp == null) continue;
            var current = tmp.text ?? "";
            if (!predicate(current)) continue;
            if (!string.Equals(current, text, StringComparison.Ordinal)) tmp.text = text;
            return tmp;
        }

        WarnOnce($"could not find row {label} text target");
        return null;
    }

    private static void ApplyDetails(GameContainer container, VanillaLobbyDisplayData data)
    {
        var details = GetOrCreateRowDetailsText(container);
        if (details == null) return;

        details.text = FormatRowDetails(data);
        details.gameObject.SetActive(true);
    }

    private static void HideDetails(GameContainer container)
    {
        if (RowDetailsByContainer.TryGetValue(container.GetInstanceID(), out var details) && details != null)
            details.gameObject.SetActive(false);
    }

    private static TextMeshPro? GetOrCreateRowDetailsText(GameContainer container)
    {
        var key = container.GetInstanceID();
        if (RowDetailsByContainer.TryGetValue(key, out var existing) && existing != null) return existing;

        var template = FindRowTemplate(container);
        if (template == null)
        {
            WarnOnce("could not find row details text template");
            return null;
        }

        var details = Object.Instantiate(template, template.transform.parent);
        details.name = "PerfectCommsLobbyRowDetails";
        details.richText = false;
        details.enableWordWrapping = false;
        details.alignment = TextAlignmentOptions.Left;
        details.fontSize = Mathf.Max(0.55f, template.fontSize * 0.58f);
        details.color = new Color32(176, 239, 255, 255);
        details.transform.localScale = template.transform.localScale;
        details.transform.localPosition = template.transform.localPosition + new Vector3(0f, -0.24f, -0.02f);
        details.gameObject.SetActive(false);
        RowDetailsByContainer[key] = details;
        VanillaLobbyDiagnostics.NoticeLimited("row.details.create", "row",
            $"created details container={key} template='{template.name}' parent='{template.transform.parent?.name}' font={details.fontSize:0.##} localPos={details.transform.localPosition}",
            first: 12, every: 120);
        return details;
    }

    private static TextMeshPro? FindRowTemplate(GameContainer container)
    {
        TextMeshPro? best = null;
        foreach (var tmp in container.gameObject.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp == null || tmp.name == "PerfectCommsLobbyRowDetails" || tmp.fontSize <= 0f) continue;
            if (best == null || tmp.fontSize < best.fontSize) best = tmp;
        }
        return best;
    }

    private static string FormatRowDetails(VanillaLobbyDisplayData data)
        => Shorten($"Code: {data.Code} • Map: {data.MapText} • Mods: {data.ModsText}", 92);

    private static string Shorten(string value, int max)
        => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";

    private static bool IsHostBox(string text)
        => text.IndexOf("Player Speed", StringComparison.OrdinalIgnoreCase) >= 0
           || text.StartsWith("Host:", StringComparison.OrdinalIgnoreCase);

    private static bool IsStatusBox(string text)
        => text.IndexOf("Roles", StringComparison.OrdinalIgnoreCase) >= 0
           || text.StartsWith("Lobby", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("InGame", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("In Game", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool IsCapacityText(string text)
        => text.Contains('/') && !text.Contains('•');

    private static void WarnOnce(string warning)
    {
        if (string.Equals(_lastWarning, warning, StringComparison.Ordinal)) return;
        _lastWarning = warning;
        VanillaLobbyDiagnostics.Warning("row", warning);
    }
}

internal static class VanillaLobbyMoreInfoUi
{
    private static readonly bool ShowExtraDetails = false;
    private static readonly Dictionary<int, TextMeshPro> DetailsByPopup = new();
    private static readonly FieldInfo? CapacityField = SafeField(typeof(FindGameMoreInfoPopup), "capacity");
    private static string? _lastWarning;

    private static FieldInfo? SafeField(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    internal static void Apply(FindGameMoreInfoPopup popup, GameListing gameListing)
    {
        try
        {
            if (!ShowExtraDetails)
            {
                HideDetails(popup);
                return;
            }

            var data = VanillaLobbyDisplayData.Build(gameListing);
            if (CapacityField?.GetValue(popup) is TextMeshPro capacity)
                capacity.text = data.StatusText;

            var detailsText = BuildDetailsText(data);
            if (AppendToExistingText(popup, detailsText))
            {
                VanillaLobbyDiagnostics.NoticeLimited("more.apply", "more",
                    $"popup={popup.GetInstanceID()} code={data.Code} fromApi={data.FromApi} mode=append details='{Compact(detailsText)}'",
                    first: 8, every: 60);
                return;
            }

            var details = GetOrCreateDetailsText(popup);
            if (details != null)
            {
                details.text = detailsText;
                details.gameObject.SetActive(true);
                VanillaLobbyDiagnostics.NoticeLimited("more.apply", "more",
                    $"popup={popup.GetInstanceID()} code={data.Code} fromApi={data.FromApi} mode=clone detailsTarget=true details='{Compact(detailsText)}'",
                    first: 8, every: 60);
            }
            else
            {
                VanillaLobbyDiagnostics.NoticeLimited("more.apply", "more",
                    $"popup={popup.GetInstanceID()} code={data.Code} fromApi={data.FromApi} mode=none detailsTarget=false textCount={CountPopupTexts(popup.gameObject)}",
                    first: 8, every: 60);
            }
        }
        catch (Exception ex)
        {
            WarnOnce($"more-info metadata failed: {ex.Message}");
        }
    }

    private static void HideDetails(FindGameMoreInfoPopup popup)
    {
        if (DetailsByPopup.TryGetValue(popup.GetInstanceID(), out var details) && details != null)
            details.gameObject.SetActive(false);

        var target = FindAppendTarget(popup.gameObject);
        if (target == null) return;

        var stripped = StripExistingDetails(target.text ?? "").TrimEnd();
        if (!string.Equals(target.text, stripped, StringComparison.Ordinal))
            target.text = stripped;
    }

    private static bool AppendToExistingText(FindGameMoreInfoPopup popup, string detailsText)
    {
        var target = FindAppendTarget(popup.gameObject);
        if (target == null) return false;

        var baseText = StripExistingDetails(target.text ?? "").TrimEnd();
        target.richText = true;
        target.text = string.IsNullOrWhiteSpace(baseText)
            ? detailsText
            : $"{baseText}\n\n{detailsText}";
        VanillaLobbyDiagnostics.NoticeLimited("more.append.target", "more",
            $"target='{target.name}' font={target.fontSize:0.##} baseChars={baseText.Length} finalChars={target.text.Length}",
            first: 6, every: 60);
        return true;
    }

    private static TextMeshPro? FindAppendTarget(GameObject root)
    {
        TextMeshPro? best = null;
        foreach (var tmp in root.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp == null || tmp.name == "PerfectCommsLobbyDetails" || tmp.fontSize <= 0f) continue;
            var text = tmp.text ?? "";
            if (text.Length == 0) continue;
            if (best == null || text.Length > (best.text ?? "").Length) best = tmp;
        }
        return best;
    }

    private static string StripExistingDetails(string text)
    {
        var marker = text.IndexOf("PERFECT COMMS", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return text;

        var blockStart = text.LastIndexOf('\n', marker);
        while (blockStart > 0 && text[blockStart - 1] == '\n') blockStart--;
        return blockStart <= 0 ? "" : text[..blockStart];
    }

    private static TextMeshPro? GetOrCreateDetailsText(FindGameMoreInfoPopup popup)
    {
        var key = popup.GetInstanceID();
        if (DetailsByPopup.TryGetValue(key, out var existing) && existing != null) return existing;

        var template = FindTemplateText(popup.gameObject);
        if (template == null)
        {
            WarnOnce("could not find popup text template");
            return null;
        }

        var details = Object.Instantiate(template, popup.transform);
        details.name = "PerfectCommsLobbyDetails";
        details.richText = true;
        details.enableWordWrapping = false;
        details.alignment = TextAlignmentOptions.TopLeft;
        details.fontSize = Mathf.Max(0.75f, template.fontSize * 0.58f);
        details.color = new Color32(235, 255, 252, 255);
        details.transform.localScale = Vector3.one;
        details.transform.localPosition = new Vector3(1.05f, -1.55f, -1f);
        details.gameObject.SetActive(false);
        DetailsByPopup[key] = details;
        VanillaLobbyDiagnostics.NoticeLimited("more.details.create", "more",
            $"created popup={key} template='{template.name}' font={details.fontSize:0.##} localPos={details.transform.localPosition}",
            first: 6, every: 60);
        return details;
    }

    private static TextMeshPro? FindTemplateText(GameObject root)
    {
        TextMeshPro? best = null;
        foreach (var tmp in root.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp == null || tmp.fontSize <= 0f) continue;
            if (best == null || tmp.fontSize > best.fontSize) best = tmp;
        }
        return best;
    }

    private static string BuildDetailsText(VanillaLobbyDisplayData data)
    {
        return string.Join("\n",
            "<mark=#0B1F24CC><b> PERFECT COMMS </b></mark>",
            $"<mark=#104F58CC> HOST </mark> {data.Host}",
            $"<mark=#104F58CC> STATUS </mark> {data.StatusText}",
            $"<mark=#104F58CC> CODE </mark> {data.Code}   <mark=#104F58CC> MAP </mark> {data.MapText}",
            $"<mark=#104F58CC> MODS </mark> {data.ModsText}");
    }

    private static int CountPopupTexts(GameObject root)
        => root.GetComponentsInChildren<TextMeshPro>(true).Length;

    private static string Compact(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " | ");
        return value.Length <= 160 ? value : value[..159] + "…";
    }

    private static void WarnOnce(string warning)
    {
        if (string.Equals(_lastWarning, warning, StringComparison.Ordinal)) return;
        _lastWarning = warning;
        VanillaLobbyDiagnostics.Warning("more", warning);
    }
}
