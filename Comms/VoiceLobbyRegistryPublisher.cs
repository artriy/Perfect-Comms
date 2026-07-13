using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AmongUs.GameOptions;
using InnerNet;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceLobbyRegistryPublisher
{
    private const int PublishIntervalSeconds = 10;
    private const int TtlSeconds = 35;

    // The per-frame body below (TryBuildRequest -> CountPlayers AllPlayerControls scan + string Clamps
    // + BuildSignature string.Join, and the BetterCrewLink publisher it drives) is host-side bookkeeping
    // that has no reason to run at frame rate. Throttle the whole thing to ~4 Hz; the actual network
    // publishes/deletes are independently gated to 5-10 s, so this changes nothing functionally while
    // removing ~93% of the per-frame allocations on a public-lobby host.
    private const double RefreshIntervalSeconds = 0.25;
    private static DateTime _nextRefreshUtc = DateTime.MinValue;

    private static string? _listingId;
    private static string? _ownerToken;
    private static string? _listingRegistryUrl;
    private static string? _lastCode;
    private static string? _lastSignature;
    private static DateTime _nextPublishUtc = DateTime.MinValue;
    private static readonly VoiceLobbyRegistryOperationQueue Operations = new();

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

        if (ResolvePublishSource(options) == VoiceLobbyBrowserSource.BetterCrewLink)
        {
            ClearCloudflareListing();
            BetterCrewLinkLobbyPublisher.Update(settings.BetterCrewLinkServerUrl.Value, request);
            return;
        }

        BetterCrewLinkLobbyPublisher.Clear();
        if (Operations.IsBusy) return;

        var registryUrl = settings.LobbyRegistryUrl.Value;
        if (!string.IsNullOrEmpty(_listingId)
            && !string.Equals(_listingRegistryUrl, registryUrl, StringComparison.Ordinal))
        {
            // A listing belongs to the endpoint where it was created. Delete it there before a
            // replacement generation is allowed to publish against the new endpoint.
            ClearCloudflareListing();
            return;
        }

        PrepareCloudflareRequest(registryUrl, request);
        var signature = BuildSignature(request);
        if (DateTime.UtcNow < _nextPublishUtc
            && string.Equals(_lastCode, request.Code, StringComparison.Ordinal)
            && string.Equals(_lastSignature, signature, StringComparison.Ordinal))
            return;

        _lastCode = request.Code;
        _lastSignature = signature;
        _nextPublishUtc = DateTime.UtcNow.AddSeconds(PublishIntervalSeconds);
        Operations.TryStart(() => PublishAsync(_listingRegistryUrl ?? registryUrl, request));
    }

    private static VoiceLobbyBrowserSource ResolvePublishSource(VoiceChatGameOptions options)
    {
        var source = (VoiceLobbyBrowserSource)options.LobbyBrowserBackend.Value;
        return Enum.IsDefined(typeof(VoiceLobbyBrowserSource), source)
            ? source
            : VoiceLobbyBrowserSource.BetterCrewLink;
    }

    internal static void ClearLocalListing()
    {
        ClearCloudflareListing();
        BetterCrewLinkLobbyPublisher.Clear();
    }

    private static void ClearCloudflareListing()
    {
        if (string.IsNullOrEmpty(_listingId) || string.IsNullOrEmpty(_ownerToken)) return;

        var registryUrl = _listingRegistryUrl ?? VoiceSettings.Instance?.LobbyRegistryUrl.Value ?? "";
        var id = _listingId;
        var token = _ownerToken;
        _listingId = null;
        _ownerToken = null;
        _listingRegistryUrl = null;
        _lastCode = null;
        _lastSignature = null;
        _nextPublishUtc = DateTime.MinValue;

        // The POST may still be in flight. Queueing the DELETE behind it guarantees that a late
        // successful publish cannot resurrect a listing after host loss, privacy changes or exit.
        // The queue also blocks a replacement listing generation until this final delete ends.
        Operations.QueueAfterCurrent(() => DeleteAsync(registryUrl, id, token));
    }

    private static async Task PublishAsync(string registryUrl, VoiceLobbyPublishRequest request)
    {
        try
        {
            await VoiceLobbyRegistryClient.PublishAsync(registryUrl, request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning($"[VC] Lobby registry publish failed: {ex.Message}");
        }
    }

    private static async Task DeleteAsync(string registryUrl, string id, string ownerToken)
    {
        try
        {
            await VoiceLobbyRegistryClient.DeleteAsync(registryUrl, id, ownerToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning($"[VC] Lobby registry delete failed: {ex.Message}");
        }
    }

    private static bool TryBuildRequest(VoiceChatLocalSettings settings, out VoiceLobbyPublishRequest request)
    {
        request = new VoiceLobbyPublishRequest();
        var client = AmongUsClient.Instance;
        if (client == null || !client.AmHost || client.GameId == 0)
            return false;

        var code = GameCode.IntToGameName(client.GameId);
        if (string.IsNullOrWhiteSpace(code) || code == "????")
            return false;

        request.Code = code;
        request.Region = ResolveRegionName();
        request.Language = Clamp(settings.LobbyBrowserLanguage.Value, 16, "English");
        request.Title = Clamp(settings.LobbyBrowserTitle.Value, 40, "Perfect Comms");
        request.Host = Clamp(PlayerControl.LocalPlayer?.Data?.PlayerName, 24, "Unknown");
        request.Players = CountPlayers();
        request.MaxPlayers = ResolveMaxPlayers();
        request.State = ResolveState(client);
        request.ModVersion = typeof(VoiceChatPluginMain).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        request.ProtocolVersion = VoiceProtocol.ProtocolVersion;
        request.TtlSeconds = TtlSeconds;
        return true;
    }

    private static void PrepareCloudflareRequest(string registryUrl, VoiceLobbyPublishRequest request)
    {
        if (string.IsNullOrEmpty(_listingId)
            || string.IsNullOrEmpty(_ownerToken)
            || !string.Equals(_lastCode, request.Code, StringComparison.Ordinal))
        {
            _listingId = Guid.NewGuid().ToString("N");
            _ownerToken = CreateToken();
            _listingRegistryUrl = registryUrl;
            _lastCode = null;
            _lastSignature = null;
            _nextPublishUtc = DateTime.MinValue;
        }

        request.Id = _listingId!;
        request.OwnerToken = _ownerToken!;
    }

    private static string CreateToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string ResolveState(AmongUsClient client)
    {
        if (LobbyBehaviour.Instance != null) return "Lobby";
        return client.GameState is InnerNetClient.GameStates.Started or InnerNetClient.GameStates.Joined
            ? "InGame"
            : "Unknown";
    }

    private static int CountPlayers()
    {
        int count = 0;
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
            if (!string.IsNullOrWhiteSpace(region?.Name)) return region.Name;
        }
        catch { }
        return "Unknown";
    }

    private static string Clamp(string? value, int max, string fallback)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrEmpty(text)) text = fallback;
        return text.Length <= max ? text : text[..max];
    }

    private static string BuildSignature(VoiceLobbyPublishRequest request)
        => string.Join("|",
            request.State,
            request.Players,
            request.MaxPlayers,
            request.Host,
            request.Title,
            request.Language,
            request.Region,
            request.ProtocolVersion);
}

/// <summary>
/// Serializes Cloudflare registry operations for one publisher. In particular, teardown is queued
/// after an in-flight publish and remains the current operation until its final delete completes.
/// </summary>
internal sealed class VoiceLobbyRegistryOperationQueue
{
    private Task? _current;

    internal bool IsBusy => _current is { IsCompleted: false };

    internal bool TryStart(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (IsBusy) return false;
        _current = operation();
        return true;
    }

    internal Task QueueAfterCurrent(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var queued = RunAfterCurrentAsync(_current, operation);
        _current = queued;
        return queued;
    }

    internal static async Task RunAfterCurrentAsync(Task? current, Func<Task> operation)
    {
        if (current != null)
        {
            try { await current.ConfigureAwait(false); }
            catch { /* Final cleanup must still run after a failed publish. */ }
        }

        await operation().ConfigureAwait(false);
    }
}
