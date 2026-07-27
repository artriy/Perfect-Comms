using System;

namespace VoiceChatPlugin.VoiceChat;

public readonly record struct VoiceRoomSettingsSnapshot(
    int Backend,
    string BackendServerUrl,
    float MaxChatDistance,
    int FalloffMode,
    int OcclusionMode,
    bool WallsBlockSound,
    bool OnlyHearInSight,
    bool ImpostorHearGhosts,
    bool HearInVent,
    bool VentPrivateChat,
    bool CommsSabDisables,
    bool CameraCanHear,
    bool TeamRadio,
    bool TeamRadioImpostors,
    bool OnlyGhostsCanTalk,
    bool OnlyMeetingOrLobby,
    bool OnlyMeetingOrLobbyAffectsGhosts,
    bool TeamRadioInMeetings,
    bool TeamRadioInTasks,
    bool GhostsHearEachOtherUnlimited,
    bool GracePeriodEnabled,
    float GracePeriodSeconds)
{
    public const float MinChatDistance = 1.5f;
    public const float MaxChatDistanceLimit = 20f;
    // Reserved wire fields retained at the front of the RPC snapshot for mixed-version decoding.
    // Media is always native-engine + Among Us RPC; these values are deliberately inert.
    public const int ReservedBackend = 0;
    public const string ReservedBackendServerUrl = "";

    public static VoiceRoomSettingsSnapshot Defaults { get; } = new(
        Backend: ReservedBackend,
        BackendServerUrl: ReservedBackendServerUrl,
        MaxChatDistance: 6f,
        FalloffMode: (int)VoiceFalloffMode.Smooth,
        OcclusionMode: (int)VoiceOcclusionMode.VisionOnly,
        WallsBlockSound: true,
        OnlyHearInSight: true,
        ImpostorHearGhosts: false,
        HearInVent: false,
        VentPrivateChat: true,
        CommsSabDisables: true,
        CameraCanHear: true,
        TeamRadio: true,
        TeamRadioImpostors: true,
        OnlyGhostsCanTalk: false,
        OnlyMeetingOrLobby: false,
        OnlyMeetingOrLobbyAffectsGhosts: false,
        TeamRadioInMeetings: false,
        TeamRadioInTasks: true,
        GhostsHearEachOtherUnlimited: false,
        GracePeriodEnabled: false,
        GracePeriodSeconds: 5f);

    public static VoiceRoomSettingsSnapshot FromGameOptions()
    {
        var options = VoiceChatGameOptions.GetInstance();
        return new VoiceRoomSettingsSnapshot(
            Backend: ReservedBackend,
            BackendServerUrl: ReservedBackendServerUrl,
            MaxChatDistance: options.MaxChatDistance.Value,
            FalloffMode: options.FalloffMode.Value,
            OcclusionMode: options.OcclusionMode.Value,
            WallsBlockSound: options.WallsBlockSound.Value,
            OnlyHearInSight: options.OnlyHearInSight.Value,
            ImpostorHearGhosts: options.ImpostorHearGhosts.Value,
            HearInVent: options.HearInVent.Value,
            VentPrivateChat: options.VentPrivateChat.Value,
            CommsSabDisables: options.CommsSabDisables.Value,
            CameraCanHear: options.CameraCanHear.Value,
            TeamRadio: options.TeamRadio.Value,
            TeamRadioImpostors: options.TeamRadioImpostors.Value,
            OnlyGhostsCanTalk: options.OnlyGhostsCanTalk.Value,
            OnlyMeetingOrLobby: options.OnlyMeetingOrLobby.Value,
            OnlyMeetingOrLobbyAffectsGhosts: options.OnlyMeetingOrLobbyAffectsGhosts.Value,
            TeamRadioInMeetings: options.TeamRadioInMeetings.Value,
            TeamRadioInTasks: options.TeamRadioInTasks.Value,
            GhostsHearEachOtherUnlimited: options.GhostsHearEachOtherUnlimited.Value,
            GracePeriodEnabled: options.GracePeriodEnabled.Value,
            GracePeriodSeconds: options.GracePeriodSeconds.Value).Clamp();
    }

    public VoiceRoomSettingsSnapshot Clamp()
        => this with
        {
            Backend = ReservedBackend,
            BackendServerUrl = ReservedBackendServerUrl,
            MaxChatDistance = Math.Clamp(MaxChatDistance, MinChatDistance, MaxChatDistanceLimit),
            FalloffMode = Enum.IsDefined(typeof(VoiceFalloffMode), FalloffMode)
                ? FalloffMode
                : (int)VoiceFalloffMode.Smooth,
            OcclusionMode = Enum.IsDefined(typeof(VoiceOcclusionMode), OcclusionMode)
                ? OcclusionMode
                : (int)VoiceOcclusionMode.VisionOnly,
            GracePeriodSeconds = Math.Clamp(GracePeriodSeconds, 0f, 15f),
        };
}

internal static class VoiceRoomSettingsState
{
    private static VoiceRoomSettingsSnapshot? _remoteSnapshot;
    private static int _sessionGameId;
    private static bool _sessionConfirmed;

    // Fix 4a (frame-cache fallback): FromGameOptions() does ~30 IL2CPP ModdedOption marshals + a
    // 34-field record-struct alloc + a `this with` clamp copy. The voice/HUD update path reads
    // Current once per peer (proximity calculator) and once per speaker, so at 12-13 peers that is
    // 12-13 full rebuilds every game-thread frame. Cache the host-options rebuild for one Unity frame
    // so the loop pays the marshal/alloc cost ONCE per frame instead of O(peers). The host-synced
    // option values change at human timescale, so a 1-frame staleness is imperceptible. The host path
    // (_remoteSnapshot.HasValue) is unaffected — it already returns the clamped remote snapshot with no
    // rebuild, which is also what the test harness exercises (it always ApplyRemote()s before reading).
    private static VoiceRoomSettingsSnapshot _frameCache;
    private static int _frameCacheFrame = int.MinValue;

    public static VoiceRoomSettingsSnapshot Current
    {
        get
        {
            if (_remoteSnapshot.HasValue)
                return _remoteSnapshot.Value;

            int frame = SafeFrameCount();
            if (frame != _frameCacheFrame)
            {
                _frameCache = VoiceRoomSettingsSnapshot.FromGameOptions();
                _frameCacheFrame = frame;
            }
            return _frameCache;
        }
    }

    // Mirrors VoiceFrameProfiler.SafeFrameCount: Time.frameCount can throw when read off the Unity
    // main thread or outside a live game (e.g. the test harness), so guard it. An int.MinValue
    // sentinel forces a rebuild on the very first read.
    private static int SafeFrameCount()
    {
        try { return UnityEngine.Time.frameCount; }
        catch { return int.MinValue + 1; }
    }

    public static VoiceRoomSettingsSnapshot? RemoteSnapshot => _remoteSnapshot;

    internal static int SessionGameId => _sessionGameId;

    internal static bool SessionConfirmed => _sessionConfirmed;

    internal static void BeginSession(int gameId)
    {
        if (gameId == 0) return;

        // A host-object-matched settings snapshot can beat OnGameJoined while the room is still being
        // constructed. In that case ApplyRemote(gameId) establishes a provisional scope; the first
        // matching authoritative join confirms it without throwing away the useful early snapshot.
        if (!_sessionConfirmed && (_sessionGameId == 0 || _sessionGameId == gameId))
        {
            _sessionGameId = gameId;
            _sessionConfirmed = true;
            return;
        }

        // OnGameJoined is a session boundary, not merely a room-code observation. A second
        // confirmed join can legitimately reuse the same GameId, so stale settings from the prior
        // connection must not survive just because the numeric room id happens to match.
        _sessionGameId = gameId;
        _sessionConfirmed = true;
        ClearRemote();
    }

    public static void ApplyRemote(VoiceRoomSettingsSnapshot snapshot)
    {
        _remoteSnapshot = snapshot.Clamp();
        VoiceChatHudState.InvalidateAudioPolicyCache();
    }

    internal static void ApplyRemote(VoiceRoomSettingsSnapshot snapshot, int gameId)
    {
        // Receiving a snapshot observes a possible session but does not authoritatively confirm a
        // join. This distinction lets an early snapshot survive the first matching OnGameJoined,
        // while a later confirmed same-GameId join still clears stale state.
        if (gameId != 0 && _sessionGameId != gameId)
        {
            _sessionGameId = gameId;
            _sessionConfirmed = false;
            ClearRemote();
        }
        ApplyRemote(snapshot);
    }

    public static void ClearRemote()
    {
        _remoteSnapshot = null;
        VoiceModRemoteOptionState.Clear();
        VoiceChatHudState.InvalidateAudioPolicyCache();
        // Drop any cached host-options rebuild so the next Current read after a host change is fresh.
        _frameCacheFrame = int.MinValue;
    }

    internal static void EndSession()
    {
        _sessionGameId = 0;
        _sessionConfirmed = false;
        ClearRemote();
    }
}
