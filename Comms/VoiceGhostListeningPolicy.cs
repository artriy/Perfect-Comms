namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceGhostListeningPolicy
{
    internal static bool Enabled(VoiceRoomSettingsSnapshot settings)
        => settings.GhostsHearLivingDuringTasks &&
           (settings.OnlyMeetingOrLobby || settings.OnlyGhostsCanTalk) &&
           !(settings.OnlyMeetingOrLobby && settings.OnlyMeetingOrLobbyAffectsGhosts);

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
