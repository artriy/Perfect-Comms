using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Pure scrolling math shared by the settings panel and its tests. Unity input remains in
/// the panel, while clamping, easing, and scrollbar geometry stay deterministic here.
/// </summary>
internal static class VoiceSettingsScrollPolicy
{
    internal static float MaxScroll(float contentHeight, float viewHeight)
        => Math.Max(0f, Sanitize(contentHeight) - Sanitize(viewHeight));

    internal static float Clamp(float value, float maxScroll)
        => Math.Clamp(Sanitize(value), 0f, Math.Max(0f, Sanitize(maxScroll)));

    internal static float Advance(float current, float target, float rate, float deltaTime)
    {
        current = Sanitize(current);
        target = Sanitize(target);
        rate = Math.Max(0f, Sanitize(rate));
        deltaTime = Math.Max(0f, Sanitize(deltaTime));
        if (rate <= 0f || deltaTime <= 0f) return current;
        float blend = 1f - MathF.Exp(-rate * deltaTime);
        float next = current + (target - current) * blend;
        return MathF.Abs(next - target) < 0.05f ? target : next;
    }

    internal static float ThumbHeight(
        float trackHeight,
        float viewHeight,
        float contentHeight,
        float minimumThumbHeight)
    {
        trackHeight = Math.Max(0f, Sanitize(trackHeight));
        viewHeight = Math.Max(1f, Sanitize(viewHeight));
        contentHeight = Math.Max(viewHeight, Sanitize(contentHeight));
        minimumThumbHeight = Math.Clamp(Sanitize(minimumThumbHeight), 0f, trackHeight);
        return Math.Clamp(trackHeight * viewHeight / contentHeight, minimumThumbHeight, trackHeight);
    }

    internal static float ThumbTopFromScroll(
        float scroll,
        float maxScroll,
        float trackHeight,
        float thumbHeight)
    {
        float travel = Math.Max(0f, Sanitize(trackHeight) - Sanitize(thumbHeight));
        maxScroll = Math.Max(0f, Sanitize(maxScroll));
        if (travel <= 0f || maxScroll <= 0f) return 0f;
        return travel * Clamp(scroll, maxScroll) / maxScroll;
    }

    internal static float ScrollFromThumbTop(
        float thumbTop,
        float maxScroll,
        float trackHeight,
        float thumbHeight)
    {
        float travel = Math.Max(0f, Sanitize(trackHeight) - Sanitize(thumbHeight));
        maxScroll = Math.Max(0f, Sanitize(maxScroll));
        if (travel <= 0f || maxScroll <= 0f) return 0f;
        return maxScroll * Math.Clamp(Sanitize(thumbTop) / travel, 0f, 1f);
    }

    private static float Sanitize(float value)
        => float.IsFinite(value) ? value : 0f;
}
