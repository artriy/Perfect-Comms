using HarmonyLib;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

/// <summary>
/// Hooks meeting lifecycle events that Perfect Comms needs for role-specific voice logic.
/// </summary>
[HarmonyPatch]
internal static class MeetingLifecyclePatches
{
    /// <summary>
    /// When a meeting starts, rotate the blackmail tracking sets so that players who
    /// were blackmailed last round can be muted this round if the host has enabled
    /// "Blackmailer: Mute Next Round".
    /// </summary>
    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    [HarmonyPostfix]
    private static void MeetingHudStart_Postfix()
    {
        VoiceRoleMuteState.OnMeetingStart();
    }
}
