using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
internal static class VoiceHudWarnings
{
    private const string Marker = "\n<color=#ffcc66>VC:";
    private const float WarningRefreshInterval = 0.25f;
    private static float _nextWarningRefreshTime;
    private static string _cachedWarning = "";

    private static void Postfix(PingTracker __instance)
    {
        if (__instance?.text == null) return;

        string baseText = __instance.text.text ?? "";
        int markerIndex = baseText.IndexOf(Marker, System.StringComparison.Ordinal);
        if (markerIndex >= 0)
            baseText = baseText[..markerIndex];

        if (Time.time >= _nextWarningRefreshTime)
        {
            _nextWarningRefreshTime = Time.time + WarningRefreshInterval;
            _cachedWarning = BuildWarning();
        }

        string rendered = string.IsNullOrEmpty(_cachedWarning)
            ? baseText
            : $"{baseText}{Marker} {_cachedWarning}</color>";
        if (__instance.text.text != rendered)
            __instance.text.text = rendered;
    }

    private static string BuildWarning()
    {
        var room = VoiceChatRoom.Current;
        if (room == null) return "";

        if (!room.UsingMicrophone && !room.Mute)
            return "mic unavailable";

        if (!room.UsingSpeaker && !VoiceChatHudState.IsSpeakerMuted)
            return "speaker unavailable";

        VoiceClientRegistry.GetCompatibilitySummary(out int voiceClientCount, out bool hasCompatible, out bool hasIncompatible);
        if (hasIncompatible)
            return "protocol mismatch";

        bool hasOtherClients = AmongUsClient.Instance?.allClients?.Count > 1;
        if (hasOtherClients && voiceClientCount > 0 && !hasCompatible)
            return "no compatible voice clients";

        return "";
    }
}
