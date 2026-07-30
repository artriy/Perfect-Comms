using System;
using System.Security.Cryptography;
using AmongUs.GameOptions;
using InnerNet;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceLobbyRegistryPublisher
{
    // Lobby metadata has no reason to be rebuilt at frame rate. Meaningful
    // changes still publish immediately on the next quarter-second tick.
    private const double RefreshIntervalSeconds = 0.25;
    private static DateTime _nextRefreshUtc = DateTime.MinValue;
    private static string? _listingId;
    private static string? _ownerToken;
    private static string? _lastCode;

    internal static void Update()
    {
        var now = DateTime.UtcNow;
        if (now < _nextRefreshUtc) return;
        _nextRefreshUtc = now.AddSeconds(RefreshIntervalSeconds);

        var settings = VoiceSettings.Instance;
        var options = VoiceChatGameOptions.GetInstance();
        if (settings == null || !options.PublicVoiceLobby.Value || !TryBuildRequest(settings, out var request))
        {
            ClearLocalListing();
            return;
        }

        PrepareIdentity(request);
        var signature = BuildSignature(request);
        VoiceLobbyLivePublisher.Update(settings.LobbyRegistryUrl.Value, request, signature);
    }

    internal static void ClearLocalListing()
    {
        _listingId = null;
        _ownerToken = null;
        _lastCode = null;
        _nextRefreshUtc = DateTime.MinValue;
        VoiceLobbyLivePublisher.Clear();
    }

    private static bool TryBuildRequest(VoiceChatLocalSettings settings, out VoiceLobbyPublishRequest request)
    {
        request = new VoiceLobbyPublishRequest();
        var client = AmongUsClient.Instance;
        if (client == null || !client.AmConnected || !client.AmHost || client.GameId == 0)
            return false;
        if (!TryResolveState(client, out var state)) return false;

        var code = GameCode.IntToGameName(client.GameId);
        if (string.IsNullOrWhiteSpace(code) || code == "????") return false;
        var region = ResolveRegionName();
        if (string.IsNullOrWhiteSpace(region)) return false;

        request.Code = code;
        request.Region = Clamp(region, 40, "");
        request.Language = Clamp(settings.LobbyBrowserLanguage.Value, 16, "English");
        request.Title = Clamp(settings.LobbyBrowserTitle.Value, 40, "Perfect Comms");
        request.Host = Clamp(PlayerControl.LocalPlayer?.Data?.PlayerName, 24, "Unknown");
        request.Players = CountPlayers();
        request.MaxPlayers = ResolveMaxPlayers();
        request.State = state;
        request.ModVersion = VoiceChatPluginMain.Version;
        request.ProtocolVersion = VoiceProtocol.ProtocolVersion;
        return true;
    }

    private static void PrepareIdentity(VoiceLobbyPublishRequest request)
    {
        if (string.IsNullOrEmpty(_listingId)
            || string.IsNullOrEmpty(_ownerToken)
            || !string.Equals(_lastCode, request.Code, StringComparison.Ordinal))
        {
            _listingId = Guid.NewGuid().ToString("N");
            _ownerToken = CreateToken();
            _lastCode = request.Code;
        }

        request.Id = _listingId;
        request.OwnerToken = _ownerToken;
    }

    private static string CreateToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static bool TryResolveState(AmongUsClient client, out string state)
    {
        state = "";
        if (client.GameState == InnerNetClient.GameStates.Joined && LobbyBehaviour.Instance != null)
        {
            state = "Lobby";
            return true;
        }
        if (client.GameState == InnerNetClient.GameStates.Started)
        {
            state = "InGame";
            return true;
        }
        return false;
    }

    private static int CountPlayers()
    {
        var count = 0;
        try
        {
            foreach (var player in PlayerControl.AllPlayerControls)
            {
                if (player == null || player.isDummy || player.notRealPlayer) continue;
                if (player.Data?.Disconnected == true) continue;
                count++;
            }
        }
        catch { }

        if (count <= 0)
        {
            try { count = AmongUsClient.Instance?.allClients?.Count ?? 0; }
            catch { }
        }
        return Math.Max(1, count);
    }

    private static int ResolveMaxPlayers()
    {
        try
        {
            var options = GameOptionsManager.Instance?.CurrentGameOptions;
            if (options != null && options.MaxPlayers > 0) return options.MaxPlayers;
        }
        catch { }
        return 15;
    }

    private static string ResolveRegionName()
    {
        try
        {
            var region = DestroyableSingleton<ServerManager>.Instance?.CurrentRegion;
            if (!string.IsNullOrWhiteSpace(region?.Name)) return region.Name.Trim();
        }
        catch { }
        return "";
    }

    private static string Clamp(string? value, int max, string fallback)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrEmpty(text)) text = fallback;
        return text.Length <= max ? text : text[..max];
    }

    private static string BuildSignature(VoiceLobbyPublishRequest request)
        => string.Join("|",
            request.Id,
            request.Code,
            request.State,
            request.Players,
            request.MaxPlayers,
            request.Host,
            request.Title,
            request.Language,
            request.Region,
            request.ModVersion,
            request.ProtocolVersion);
}
