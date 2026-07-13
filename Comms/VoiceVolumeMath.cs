using System;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceVolumeMath
{
    internal const float MaxUserVolume = 2f;
    internal const float MaxComposedPeerGain = MaxUserVolume * MaxUserVolume;

    internal static float NormalizeUserVolume(float volume)
        => float.IsFinite(volume) ? Math.Clamp(volume, 0f, MaxUserVolume) : 1f;

    internal static bool ShouldApplyDeadMeetingMix(bool meetingActive, bool localPlayerIsDead)
        => meetingActive && localPlayerIsDead;

    internal static float SelectGroupVolume(
        bool deadMeetingMixActive,
        bool? targetIsDead,
        float alivePlayerVolume,
        float deadPlayerVolume)
    {
        if (!deadMeetingMixActive || !targetIsDead.HasValue)
            return 1f;

        return NormalizeUserVolume(targetIsDead.Value ? deadPlayerVolume : alivePlayerVolume);
    }

    internal static float ResolvePeerGain(
        VoiceProximityResult route,
        float perPlayerVolume,
        float groupVolume)
    {
        if (!route.Audible)
            return 0f;

        float routeSum = route.NormalVolume + route.GhostVolume + route.RadioVolume;
        float routeGain = float.IsFinite(routeSum) ? Math.Clamp(routeSum, 0f, 1f) : 0f;
        float composed = routeGain
                         * NormalizeUserVolume(perPlayerVolume)
                         * NormalizeUserVolume(groupVolume);
        return float.IsFinite(composed) ? Math.Clamp(composed, 0f, MaxComposedPeerGain) : 0f;
    }
}
