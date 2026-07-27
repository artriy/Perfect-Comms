namespace VoiceChatPlugin.VoiceChat;

internal enum VoiceProximityReason
{
    Lobby,
    Unmapped,
    TargetUnavailable,
    NoListener,
    OnlyMeetingOrLobby,
    OnlyGhostsCanTalk,
    CommsSabotage,
    LocalDeadHearsGhost,
    LocalDeadHearsLiving,
    RoleMuted,
    GracePeriod,
    ModChannel,
    ModPairRoute,
    MeetingLiving,
    TeamRadio,
    TeamRadioMuted,
    ImpostorHearsGhost,
    TargetDeadMuted,
    VentMuted,
    VentPrivateMuted,
    SightBlocked,
    HardOcclusion,
    CameraProxy,
    Proximity,
}
