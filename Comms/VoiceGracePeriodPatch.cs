using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class VoiceGracePeriodPatch
{
    public static void Postfix(PlayerControl __instance)
    {
        if (__instance != null)
            VoiceRoleMuteState.OnMeetingStarted(__instance.PlayerId);
    }
}
