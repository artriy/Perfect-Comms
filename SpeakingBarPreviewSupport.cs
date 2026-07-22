using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Shared world-space measurements used by the live speaking bar and its settings preview.
/// Keeping these in one place prevents the miniature viewport from drifting away from the HUD.
/// </summary>
internal static class SpeakingBarVisualMetrics
{
    internal const float LabelSize = 0.95f;
    internal const float SlotWidth = 0.52f;
    internal const float SlotHeight = 0.64f;
    internal const float LabelOffset = 0.34f;
    internal const float LabelSideOffset = 0.30f;
    internal const float TopNameExtraPitch = 0.30f;
    internal const float RingScale = 0.48f;
    internal const float BackdropPad = 0.05f;
    internal const float NameGap = 0.10f;
    internal const float MaximumLabelWidth = 1.40f;
    internal const float MaximumLabelHeight = 0.45f;
}

/// <summary>
/// Deterministic stress roster shared by Advanced &gt; Show Fake 15 Players and the
/// isolated settings preview. It never reads game, network, privacy, or voice state.
/// </summary>
internal static class SpeakingBarPreviewRoster
{
    internal const byte PlayerIdStart = 240;
    internal const int PlayerCount = 15;
    internal const int GhostStartIndex = 10;

    private static readonly string[] Names =
    {
        "Player 01", "Player 02", "Player 03", "Player 04", "Player 05",
        "Player 06", "Player 07", "Player 08", "Player 09", "Player 10",
        "Ghost 11", "Ghost 12", "Ghost 13", "Ghost 14", "Ghost 15",
    };

    internal static string Name(int previewIndex)
    {
        ValidateIndex(previewIndex);
        return Names[previewIndex];
    }

    internal static bool IsGhost(int previewIndex)
    {
        ValidateIndex(previewIndex);
        return previewIndex >= GhostStartIndex;
    }

    internal static float VoiceLevel(int previewIndex)
    {
        ValidateIndex(previewIndex);
        return previewIndex switch
        {
            0 => 0.86f,
            4 => 0.58f,
            9 => 0.74f,
            12 => 0.92f,
            _ => 0f,
        };
    }

    internal static byte PlayerId(int previewIndex)
    {
        ValidateIndex(previewIndex);
        return (byte)(PlayerIdStart + previewIndex);
    }

    internal static bool IsPlayerId(byte playerId)
        => playerId >= PlayerIdStart && playerId < PlayerIdStart + PlayerCount;

    internal static int Index(byte playerId)
    {
        if (!IsPlayerId(playerId))
            throw new ArgumentOutOfRangeException(nameof(playerId), playerId, "Not a preview player id.");
        return playerId - PlayerIdStart;
    }

    private static void ValidateIndex(int previewIndex)
    {
        if (previewIndex < 0 || previewIndex >= PlayerCount)
            throw new ArgumentOutOfRangeException(nameof(previewIndex), previewIndex, "Preview index is outside the 15-player roster.");
    }
}

/// <summary>
/// Keeps the miniature preview's topology aligned with the live HUD. The preview viewport may
/// uniformly shrink that topology to fit, but its smaller canvas must not create extra rows or
/// columns that would not exist in game.
/// </summary>
internal static class SpeakingBarLivePreviewLayoutPolicy
{
    internal static int? RequiredLineCount(int itemCount, bool singleLane)
    {
        int preferredLineCount = SpeakingBarLayoutPolicy.GetLineCount(itemCount);
        if (preferredLineCount == 0) return null;
        return singleLane ? 1 : preferredLineCount;
    }
}

internal readonly record struct SpeakingBarPreviewWorkspace(
    float Scale,
    float SettingsOffsetX,
    float PreviewLocalCenterX);

internal readonly record struct SpeakingBarPreviewTransition(
    float Move,
    float Reveal);

/// <summary>
/// Complete immutable input for the isolated speaking-bar renderer. The settings panel builds
/// this from live config entries, while first-run setup builds it from a staged draft so merely
/// hovering a preset never writes config or mutates the real HUD.
/// </summary>
internal readonly record struct SpeakingBarPreviewSettings(
    SpeakingBarPosition Position,
    SpeakingBarSideLayout SideLayout,
    bool ManualLayout,
    VoiceControlsLayout ManualOrientation,
    SpeakingBarAvatarFacing ManualAvatarFacing,
    float ManualX,
    float ManualY,
    SpeakingBarNamePosition NamePosition,
    float Scale,
    bool Backdrop)
{
    internal static SpeakingBarPreviewSettings From(VoiceChatLocalSettings settings)
        => new(
            settings.SpeakingBarPosition.Value,
            settings.SpeakingBarSideLayout.Value,
            settings.SpeakingBarManualLayout.Value,
            settings.SpeakingBarLayout.Value,
            settings.SpeakingBarAvatarFacing.Value,
            settings.SpeakingBarX.Value,
            settings.SpeakingBarY.Value,
            settings.SpeakingBarNamePosition.Value,
            settings.SpeakingBarScale.Value,
            settings.SpeakingBarBackdrop.Value);
}

/// <summary>
/// Stages the settings-card movement ahead of the preview reveal. Because both values
/// are derived from one reversible progress value, disabling naturally fades the preview
/// away before the settings card glides back to center.
/// </summary>
internal static class SpeakingBarLivePreviewTransitionPolicy
{
    internal const float DurationSeconds = 0.82f;
    internal const float MoveEnd = 0.58f;
    internal const float RevealStart = 0.52f;

    internal static float Advance(float progress, bool enabled, float unscaledDeltaTime)
    {
        if (!float.IsFinite(progress)) progress = 0f;
        if (!float.IsFinite(unscaledDeltaTime)) unscaledDeltaTime = 0f;
        progress = Math.Clamp(progress, 0f, 1f);
        float step = Math.Max(0f, unscaledDeltaTime) / DurationSeconds;
        return enabled
            ? Math.Min(1f, progress + step)
            : Math.Max(0f, progress - step);
    }

    internal static SpeakingBarPreviewTransition Resolve(float progress)
    {
        if (!float.IsFinite(progress)) progress = 0f;
        progress = Math.Clamp(progress, 0f, 1f);
        return new SpeakingBarPreviewTransition(
            SmootherStep(progress / MoveEnd),
            SmootherStep((progress - RevealStart) / (1f - RevealStart)));
    }

    private static float SmootherStep(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value * value * value * (value * (value * 6f - 15f) + 10f);
    }
}

internal static class SpeakingBarLivePreviewLifecyclePolicy
{
    internal static bool ShouldDisableForCategory(VoiceSettingsCategory category)
        => category != VoiceSettingsCategory.Hud;
}

/// <summary>
/// Pure sizing policy for placing the unchanged settings card and preview card side-by-side.
/// </summary>
internal static class SpeakingBarLivePreviewWorkspacePolicy
{
    internal const float SettingsWidth = 908f;
    internal const float SettingsHeight = 554f;
    internal const float PreviewWidth = 450f;
    internal const float PreviewHeight = 554f;
    internal const float Gap = 18f;
    // Includes the settings and preview cards' outer glow/shadow footprint at normal scale.
    internal const float SafeMargin = 64f;
    internal const float NormalScale = 1.30f;

    internal static SpeakingBarPreviewWorkspace Compute(float canvasWidth, float canvasHeight)
    {
        if (canvasWidth <= 0f || float.IsNaN(canvasWidth) || float.IsInfinity(canvasWidth))
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), canvasWidth, "Canvas width must be positive and finite.");
        if (canvasHeight <= 0f || float.IsNaN(canvasHeight) || float.IsInfinity(canvasHeight))
            throw new ArgumentOutOfRangeException(nameof(canvasHeight), canvasHeight, "Canvas height must be positive and finite.");

        float combinedWidth = SettingsWidth + Gap + PreviewWidth;
        float widthScale = Math.Max(1f, canvasWidth - SafeMargin * 2f) / combinedWidth;
        float heightScale = Math.Max(1f, canvasHeight - SafeMargin * 2f) / SettingsHeight;
        float scale = Math.Min(NormalScale, Math.Min(widthScale, heightScale));
        scale = Math.Max(0.50f, scale);

        float addedWidth = Gap + PreviewWidth;
        return new SpeakingBarPreviewWorkspace(
            scale,
            SettingsOffsetX: -addedWidth * 0.5f * scale,
            PreviewLocalCenterX: SettingsWidth * 0.5f + Gap + PreviewWidth * 0.5f);
    }
}
