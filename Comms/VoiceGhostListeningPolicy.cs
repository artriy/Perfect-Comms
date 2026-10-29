namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceGhostListeningPolicy
{
    internal static bool Enabled(VoiceRoomSettingsSnapshot settings)
        => settings.GhostsHearLivingDuringTasks &&
           CanApply(settings.OnlyMeetingOrLobby, settings.OnlyGhostsCanTalk, settings.OnlyMeetingOrLobbyAffectsGhosts);

    // The exception only matters while living task voice is restricted, and the explicit ghost
    // meeting restriction overrides it. The host panel hides the toggle whenever this is false.
    internal static bool CanApply(bool onlyMeetingOrLobby, bool onlyGhostsCanTalk, bool ghostsAlsoMeetingOrLobby)
        => (onlyMeetingOrLobby || onlyGhostsCanTalk) && !(onlyMeetingOrLobby && ghostsAlsoMeetingOrLobby);

    internal static bool IsGhostOnlySpeaker(
        VoiceRoomSettingsSnapshot settings, VoiceGamePhase phase,
        bool dead, bool spectator, bool impostorTeam)
        => phase == VoiceGamePhase.Tasks && Enabled(settings) && !dead && !spectator &&
           !(VoiceImpostorPolicy.SpecialChatEnabled(settings, phase) &&
             VoiceImpostorPolicy.CanTransmit(settings, impostorTeam, dead, spectator));

    internal static bool CanReceive(
        VoiceRoomSettingsSnapshot settings, VoiceGamePhase phase,
        VoicePlayerSnapshot listener, VoicePlayerSnapshot speaker)
        => IsGhostOnlySpeaker(settings, phase, speaker.IsDead, speaker.IsSpectator, speaker.IsImpostorTeam) &&
           (listener.IsDead || listener.IsSpectator) && !listener.Disconnected && !listener.IsDummy &&
           !speaker.Disconnected && !speaker.IsDummy && speaker.IsVisible;
}
