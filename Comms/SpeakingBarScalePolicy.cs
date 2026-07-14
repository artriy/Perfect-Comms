using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Defines the v4 speaking-bar scale contract independently from Unity rendering.
/// The renderer applies <see cref="VisualBaseline"/> after the user-facing scale,
/// while the one-time migration converts legacy values so existing users keep the
/// same rendered size.
/// </summary>
internal static class SpeakingBarScalePolicy
{
    internal const int CurrentSettingsVersion = 1;
    internal const float VisualBaseline = 0.90f;
    internal const float MinimumUserScale = 0.50f;
    internal const float MaximumUserScale = 2.25f;

    internal static float ToRenderedScale(float userScale)
        => userScale * VisualBaseline;

    internal static float MigrateLegacyScale(float legacyScale)
    {
        if (float.IsNaN(legacyScale))
            return 1f;
        if (float.IsPositiveInfinity(legacyScale))
            return MaximumUserScale;
        if (float.IsNegativeInfinity(legacyScale))
            return MinimumUserScale;

        return Math.Clamp(
            legacyScale / VisualBaseline,
            MinimumUserScale,
            MaximumUserScale);
    }

    internal static SpeakingBarV4MigrationPlan PlanV4Migration(
        int storedVersion,
        bool hadLegacyScaleSetting,
        float currentScale,
        bool currentBackdrop,
        SpeakingBarNamePosition currentNamePosition)
    {
        if (storedVersion >= CurrentSettingsVersion)
        {
            return new SpeakingBarV4MigrationPlan(
                shouldApply: false,
                scale: currentScale,
                backdrop: currentBackdrop,
                namePosition: currentNamePosition,
                targetVersion: storedVersion);
        }

        return new SpeakingBarV4MigrationPlan(
            shouldApply: true,
            scale: hadLegacyScaleSetting ? MigrateLegacyScale(currentScale) : currentScale,
            backdrop: true,
            namePosition: SpeakingBarNamePosition.Auto,
            targetVersion: CurrentSettingsVersion);
    }

    /// <summary>
    /// Checks the pre-bind config snapshot. This distinguishes an existing v3 scale
    /// from the v4 default that BepInEx creates when a brand-new config is bound.
    /// </summary>
    internal static bool ConfigTextContainsSetting(string? configText, string section, string key)
    {
        if (string.IsNullOrEmpty(configText) || string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
            return false;

        string? currentSection = null;
        string[] lines = configText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                currentSection = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            if (!string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
                continue;

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            string candidateKey = line.Substring(0, separator).Trim();
            if (string.Equals(candidateKey, key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

internal readonly struct SpeakingBarV4MigrationPlan
{
    internal SpeakingBarV4MigrationPlan(
        bool shouldApply,
        float scale,
        bool backdrop,
        SpeakingBarNamePosition namePosition,
        int targetVersion)
    {
        ShouldApply = shouldApply;
        Scale = scale;
        Backdrop = backdrop;
        NamePosition = namePosition;
        TargetVersion = targetVersion;
    }

    internal bool ShouldApply { get; }
    internal float Scale { get; }
    internal bool Backdrop { get; }
    internal SpeakingBarNamePosition NamePosition { get; }
    internal int TargetVersion { get; }
}
