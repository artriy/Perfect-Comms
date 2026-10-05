namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceImpostorPolicy
{
    internal static bool SpecialChatEnabled(VoiceRoomSettingsSnapshot settings, VoiceGamePhase phase)
        => phase == VoiceGamePhase.Tasks && settings.OnlyMeetingOrLobby && settings.MeetingOnlyImpostorChat;

    internal static bool MeetingRadioEnabled(VoiceRoomSettingsSnapshot settings, VoiceGamePhase phase)
        => phase == VoiceGamePhase.Meeting && settings.ImpostorsTalkAcrossDeath &&
           settings.ImpostorsTalkAcrossDeathInMeetings;

    internal static bool CanTransmit(VoiceRoomSettingsSnapshot settings, VoicePlayerSnapshot player)
        => CanTransmit(settings, player.IsImpostorTeam, player.IsDead, player.IsSpectator)
           && !player.Disconnected && !player.IsDummy && (player.IsVisible || player.IsDead);

    internal static bool CanTransmit(
        VoiceRoomSettingsSnapshot settings, bool impostorTeam, bool dead, bool spectator)
        => impostorTeam && !spectator && (!dead || settings.ImpostorsTalkAcrossDeath);

    internal static bool CanListen(VoicePlayerSnapshot player)
        => !player.Disconnected && !player.IsDummy &&
           (player.IsDead || player.IsSpectator || (player.IsImpostorTeam && player.IsVisible));

    internal static bool CanHearMeetingRadio(
        VoiceRoomSettingsSnapshot settings, VoiceGamePhase phase,
        VoicePlayerSnapshot listener, VoicePlayerSnapshot speaker)
        => MeetingRadioEnabled(settings, phase) && CanTransmit(settings, speaker) && CanListen(listener)
           && (!settings.OnlyGhostsCanTalk || speaker.IsDead && listener.IsDead);

    internal static bool CanHearRadio(
        VoiceRoomSettingsSnapshot settings, VoiceGamePhase phase,
        VoicePlayerSnapshot listener, VoicePlayerSnapshot speaker)
        => settings.TeamRadioImpostors
           && CanTransmit(settings, speaker) && CanListen(listener)
           && !(VoiceSceneState.IsMeetingVoicePhase(phase) &&
                speaker.IsDead && !listener.IsDead && !listener.IsSpectator);
}
