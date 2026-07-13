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
    bool TeamRadioVampires,
    bool TeamRadioLovers,
    bool OnlyGhostsCanTalk,
    bool OnlyMeetingOrLobby,
    bool OnlyMeetingOrLobbyAffectsGhosts,
    bool MuteBlackmailedInMeetings,
    bool MuteBlackmailedNextRound,
    bool MuteJailedInMeetings,
    bool JailorCanUnmuteJailed,
    bool MuteParasiteControlled,
    bool MutePuppeteerControlled,
    bool CrewpostorUsesImpostorVoice,
    bool MuteSwooperWhileSwooped,
    int MediumGhostVoice,
    bool MuteGlitchHacked,
    bool MuffleBlindedOrFlashedHearing,
    bool MuffleHypnotizedDuringHysteria,
    bool TeamRadioInMeetings,
    bool PuppeteerHearFromVictim,
    bool ParasiteHearFromVictim,
    bool TeamRadioInTasks,
    bool GhostsHearEachOtherUnlimited,
    bool JailPersistsAfterJailorDeath,
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
        ReservedBackend,
        ReservedBackendServerUrl,
        6f,
        (int)VoiceFalloffMode.Smooth,
        (int)VoiceOcclusionMode.VisionOnly,
        true,
        true,
        false,
        false,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        false,
        false,
        false,
        true,
        false,
        true,
        true,
        true,
        true,
        true,
        true,
        (int)MediumGhostVoiceMode.None,
        true,
        true,
        true,
        false,
        true,
        true,
        true,
        false,
        false,
        false,
        5f);

    public static VoiceRoomSettingsSnapshot FromGameOptions()
    {
        var s = VoiceChatGameOptions.GetInstance();
        var role = VoiceRoleIntegrationOptions.GetInstance();
        return new VoiceRoomSettingsSnapshot(
            ReservedBackend,
            ReservedBackendServerUrl,
            s.MaxChatDistance.Value,
            s.FalloffMode.Value,
            s.OcclusionMode.Value,
            s.WallsBlockSound.Value,
            s.OnlyHearInSight.Value,
            s.ImpostorHearGhosts.Value,
            s.HearInVent.Value,
            s.VentPrivateChat.Value,
            s.CommsSabDisables.Value,
            s.CameraCanHear.Value,
            s.TeamRadio.Value,
            s.TeamRadioImpostors.Value,
            s.TeamRadioVampires.Value,
            s.TeamRadioLovers.Value,
            s.OnlyGhostsCanTalk.Value,
            s.OnlyMeetingOrLobby.Value,
            s.OnlyMeetingOrLobbyAffectsGhosts.Value,
            role.MuteBlackmailedInMeetings.Value,
            role.MuteBlackmailedNextRound.Value,
            role.MuteJailedInMeetings.Value,
            role.JailorCanUnmuteJailed.Value,
            role.MuteParasiteControlled.Value,
            role.MutePuppeteerControlled.Value,
            role.CrewpostorUsesImpostorVoice.Value,
            role.MuteSwooperWhileSwooped.Value,
            role.MediumGhostVoice.Value,
            role.MuteGlitchHacked.Value,
            role.MuffleBlindedOrFlashedHearing.Value,
            role.MuffleHypnotizedDuringHysteria.Value,
            s.TeamRadioInMeetings.Value,
            role.PuppeteerHearFromVictim.Value,
            role.ParasiteHearFromVictim.Value,
            s.TeamRadioInTasks.Value,
            s.GhostsHearEachOtherUnlimited.Value,
            role.JailPersistsAfterJailorDeath.Value,
            s.GracePeriodEnabled.Value,
            s.GracePeriodSeconds.Value).Clamp();
    }

    public VoiceRoomSettingsSnapshot Clamp()
    {
        return this with
        {
            Backend = ReservedBackend,
            BackendServerUrl = ReservedBackendServerUrl,
            MaxChatDistance = Math.Clamp(MaxChatDistance, MinChatDistance, MaxChatDistanceLimit),
            FalloffMode = Enum.IsDefined(typeof(VoiceFalloffMode), FalloffMode) ? FalloffMode : (int)VoiceFalloffMode.Smooth,
            OcclusionMode = Enum.IsDefined(typeof(VoiceOcclusionMode), OcclusionMode) ? OcclusionMode : (int)VoiceOcclusionMode.VisionOnly,
            MediumGhostVoice = Enum.IsDefined(typeof(MediumGhostVoiceMode), MediumGhostVoice) ? MediumGhostVoice : (int)MediumGhostVoiceMode.None,
            GracePeriodSeconds = Math.Clamp(GracePeriodSeconds, 0f, 15f),
        };
    }

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

        // An authenticated settings snapshot can beat OnGameJoined while the room is still being
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
