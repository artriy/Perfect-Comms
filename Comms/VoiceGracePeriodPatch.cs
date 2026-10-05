using System;
using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class VoiceGracePeriodPatch
{
    public static void Prefix()
        => VoiceChatRoom.Current?.ResetRadioStateForTransition();

    public static void Postfix(PlayerControl __instance)
    {
        try
        {
            if (__instance != null)
                VoiceRoleMuteState.OnMeetingStarted(__instance.PlayerId);
        }
        catch (Exception ex)
        {
            VoiceChatRoom.Current?.RecoverAfterMeetingVoiceFailure(ex);
        }
    }
}
