namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// The artwork selected for a speaking-bar avatar after applying the public-death privacy gate.
/// Kept Unity-free so transitions and fallback behavior can be covered by unit tests.
/// </summary>
internal enum SpeakingBarGhostBodyArt
{
    Alive,
    VanillaGhost,
    FadedAliveFallback
}

internal readonly record struct SpeakingBarGhostAppearance(
    SpeakingBarGhostBodyArt BodyArt,
    float Alpha);

internal enum SpeakingBarGhostCosmeticRefresh
{
    None,
    Living,
    Ghost
}

internal static class SpeakingBarGhostAppearancePolicy
{
    internal const float GhostAlpha = 0.45f;

    internal static SpeakingBarGhostAppearance Resolve(
        bool publiclyDead,
        bool vanillaGhostAvailable)
    {
        if (!publiclyDead)
            return new SpeakingBarGhostAppearance(SpeakingBarGhostBodyArt.Alive, 1f);

        return new SpeakingBarGhostAppearance(
            vanillaGhostAvailable
                ? SpeakingBarGhostBodyArt.VanillaGhost
                : SpeakingBarGhostBodyArt.FadedAliveFallback,
            GhostAlpha);
    }

    internal static bool RequiresBodyRefresh(
        bool hasAppliedRequest,
        bool appliedPubliclyDead,
        bool requestedPubliclyDead)
        => !hasAppliedRequest || appliedPubliclyDead != requestedPubliclyDead;

    internal static SpeakingBarGhostCosmeticRefresh ResolveCosmeticRefresh(
        bool iconChanged,
        bool hasAppliedRequest,
        bool appliedPubliclyDead,
        bool requestedPubliclyDead)
    {
        if (requestedPubliclyDead
            && (iconChanged || !hasAppliedRequest || !appliedPubliclyDead))
            return SpeakingBarGhostCosmeticRefresh.Ghost;

        if (!requestedPubliclyDead && hasAppliedRequest && appliedPubliclyDead)
            return SpeakingBarGhostCosmeticRefresh.Living;

        return SpeakingBarGhostCosmeticRefresh.None;
    }

    /// <summary>
    /// Selects the cosmetic renderer set atomically with the body artwork. Until an exact post-ghost
    /// snapshot exists, living hats/visors remain usable while the living skin is suppressed.
    /// </summary>
    internal static bool ShouldShowCosmetic(
        bool useGhostBody,
        bool hasExactGhostCosmetics,
        bool ghostLayer,
        bool skinLayer)
    {
        if (!useGhostBody) return !ghostLayer;
        if (hasExactGhostCosmetics) return ghostLayer;
        return !ghostLayer && !skinLayer;
    }
}
