using System;

namespace VoiceChatPlugin.VoiceChat;

internal enum VoiceAliveDeadMixFocus
{
    Neutral = 0,
    Alive = 1,
    Dead = 2,
}

internal readonly record struct VoiceAliveDeadMixProfile(
    float AliveVolume,
    float DeadVolume);

internal static class VoiceVolumeMath
{
    internal const float MaxUserVolume = 2f;
    internal const float MaxComposedPeerGain = MaxUserVolume * MaxUserVolume;
    internal const float DefaultLouderVolume = 2f;
    internal const float DefaultQuieterVolume = 0.5f;

    internal static readonly VoiceAliveDeadMixProfile DefaultAliveFocusProfile =
        new(DefaultLouderVolume, DefaultQuieterVolume);
    internal static readonly VoiceAliveDeadMixProfile DefaultDeadFocusProfile =
        new(DefaultQuieterVolume, DefaultLouderVolume);

    internal static float NormalizeUserVolume(float volume)
        => float.IsFinite(volume) ? Math.Clamp(volume, 0f, MaxUserVolume) : 1f;

    internal static float SelectGroupVolume(
        VoiceAliveDeadMixFocus focus,
        bool? targetIsDead,
        VoiceAliveDeadMixProfile aliveFocusProfile,
        VoiceAliveDeadMixProfile deadFocusProfile)
    {
        if ((focus != VoiceAliveDeadMixFocus.Alive && focus != VoiceAliveDeadMixFocus.Dead)
            || !targetIsDead.HasValue)
            return 1f;

        VoiceAliveDeadMixProfile profile = focus switch
        {
            VoiceAliveDeadMixFocus.Alive => aliveFocusProfile,
            VoiceAliveDeadMixFocus.Dead => deadFocusProfile,
            _ => default,
        };
        return NormalizeUserVolume(targetIsDead.Value
            ? profile.DeadVolume
            : profile.AliveVolume);
    }

    /// <summary>
    /// The focus exists only while exactly one action is held. Releasing both immediately returns
    /// to neutral; holding both is also neutral so overlapping bindings cannot produce an arbitrary winner.
    /// </summary>
    internal static VoiceAliveDeadMixFocus ResolveAliveDeadMixFocus(
        bool aliveLouderHeld,
        bool deadLouderHeld)
        => aliveLouderHeld == deadLouderHeld
            ? VoiceAliveDeadMixFocus.Neutral
            : aliveLouderHeld
                ? VoiceAliveDeadMixFocus.Alive
                : VoiceAliveDeadMixFocus.Dead;

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
