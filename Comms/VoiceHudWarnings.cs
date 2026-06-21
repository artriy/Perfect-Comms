using System;
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

    // Memoize the last (core ping text, warning) -> rendered result so the per-frame body does no
    // string allocation when neither input changed. The marker-strip substring and the interpolated
    // "$...</color>" used to allocate every frame a warning was active; now they only run on a change.
    private static string _lastCore = "";
    private static string _lastWarning = "";
    private static string? _lastRendered;

    private static void Postfix(PingTracker __instance)
    {
        if (__instance?.text == null) return;

        // One unavoidable managed-string marshal for the current TMP text; reused for the final compare.
        string currentText = __instance.text.text ?? "";
        int markerIndex = currentText.IndexOf(Marker, System.StringComparison.Ordinal);
        int coreLen = markerIndex >= 0 ? markerIndex : currentText.Length;

        if (Time.time >= _nextWarningRefreshTime)
        {
            _nextWarningRefreshTime = Time.time + WarningRefreshInterval;
            _cachedWarning = BuildWarning();
        }

        // Fast path: the underlying ping text (the "core") and the warning are unchanged since last
        // frame, so the desired output equals the cached _lastRendered — no substring/interpolation.
        bool coreSame = _lastCore.Length == coreLen
            && currentText.AsSpan(0, coreLen).SequenceEqual(_lastCore.AsSpan());

        string rendered;
        if (coreSame && _cachedWarning == _lastWarning && _lastRendered != null)
        {
            rendered = _lastRendered;
        }
        else
        {
            string core = markerIndex >= 0 ? currentText[..markerIndex] : currentText;
            rendered = string.IsNullOrEmpty(_cachedWarning)
                ? core
                : $"{core}{Marker} {_cachedWarning}</color>";
            _lastCore = core;
            _lastWarning = _cachedWarning;
            _lastRendered = rendered;
        }

        if (currentText != rendered)
            __instance.text.text = rendered;
    }

    private static string BuildWarning()
    {
        var room = VoiceChatRoom.Current;
        if (room == null) return "";

        if (VoiceRoleMuteState.IsGracePeriodActive)
        {
            int secs = VoiceRoleMuteState.GracePeriodSecondsRemaining;
            var local = PlayerControl.LocalPlayer;
            if (local != null && VoiceRoleMuteState.GracePeriodCallerId == local.PlayerId)
                return $"you have the floor ({secs}s)";
            return $"caller has the floor ({secs}s)";
        }

        if (!room.UsingMicrophone && !room.Mute)
            return "mic unavailable";

        if (!room.UsingSpeaker && !VoiceChatHudState.IsSpeakerMuted)
            return "speaker unavailable";

        // Protocol-mismatch / no-compatible-clients warnings were removed: VoiceClientRegistry is not
        // populated in the current transport (no live handshake feeds it), so GetCompatibilitySummary
        // always reported zero clients and these branches could never fire. Re-add only together with
        // a real, sender-authenticated compatibility handshake that actually populates the registry.
        return "";
    }
}
