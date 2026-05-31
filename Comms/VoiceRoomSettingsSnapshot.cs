using System;
using MiraAPI.LocalSettings;

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
    bool ParasiteHearFromVictim)
{
    public const float MinChatDistance = 1.5f;
    public const float MaxChatDistanceLimit = 20f;

    public static VoiceRoomSettingsSnapshot Defaults { get; } = new(
        (int)VoiceTransportBackend.BetterCrewLink,
        VoiceEndpointSettings.DefaultBetterCrewLinkServerUrl,
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
        true);

    public static VoiceRoomSettingsSnapshot FromGameOptions()
    {
        var s = VoiceChatGameOptions.GetInstance();
        var role = VoiceRoleIntegrationOptions.GetInstance();
        var local = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        var backend = (VoiceTransportBackend)s.VoiceBackend.Value;
        var endpoint = VoiceEndpointSettings.Resolve(
            backend,
            local?.BetterCrewLinkServerUrl.Value,
            local?.InterstellarServerUrl.Value);
        return new VoiceRoomSettingsSnapshot(
            (int)endpoint.Backend,
            endpoint.ServerUrl,
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
            role.ParasiteHearFromVictim.Value).Clamp();
    }

    public VoiceRoomSettingsSnapshot Clamp()
    {
        return this with
        {
            Backend = Enum.IsDefined(typeof(VoiceTransportBackend), Backend) ? Backend : (int)VoiceTransportBackend.BetterCrewLink,
            BackendServerUrl = NormalizeBackendServerUrl(Backend, BackendServerUrl),
            MaxChatDistance = Math.Clamp(MaxChatDistance, MinChatDistance, MaxChatDistanceLimit),
            FalloffMode = Enum.IsDefined(typeof(VoiceFalloffMode), FalloffMode) ? FalloffMode : (int)VoiceFalloffMode.Smooth,
            OcclusionMode = Enum.IsDefined(typeof(VoiceOcclusionMode), OcclusionMode) ? OcclusionMode : (int)VoiceOcclusionMode.VisionOnly,
            MediumGhostVoice = Enum.IsDefined(typeof(MediumGhostVoiceMode), MediumGhostVoice) ? MediumGhostVoice : (int)MediumGhostVoiceMode.None,
        };
    }

    private static string NormalizeBackendServerUrl(int backend, string? serverUrl)
    {
        return backend == (int)VoiceTransportBackend.Interstellar
            ? VoiceEndpointSettings.NormalizeInterstellarServerUrl(serverUrl)
            : VoiceEndpointSettings.NormalizeBetterCrewLinkServerUrl(serverUrl);
    }
}

internal static class VoiceRoomSettingsState
{
    private static VoiceRoomSettingsSnapshot? _remoteSnapshot;

    public static VoiceRoomSettingsSnapshot Current => _remoteSnapshot ?? VoiceRoomSettingsSnapshot.FromGameOptions();

    public static VoiceRoomSettingsSnapshot? RemoteSnapshot => _remoteSnapshot;

    public static void ApplyRemote(VoiceRoomSettingsSnapshot snapshot)
    {
        _remoteSnapshot = snapshot.Clamp();
    }

    public static void ClearRemote()
    {
        _remoteSnapshot = null;
    }
}
