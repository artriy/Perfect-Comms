using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Pure scrolling math shared by the voice panels and their tests. Unity input remains in
/// the panels, while clamping, easing, and scrollbar geometry stay deterministic here.
/// </summary>
internal static class VoiceSettingsScrollPolicy
{
    internal static float MaxScroll(float contentHeight, float viewHeight)
        => Math.Max(0f, Sanitize(contentHeight) - Sanitize(viewHeight));

    internal static float Clamp(float value, float maxScroll)
        => Math.Clamp(Sanitize(value), 0f, Math.Max(0f, Sanitize(maxScroll)));

    // Critically damped: about 0.13 s to 90% of a wheel step, no overshoot from rest.
    internal const float SmoothScrollResponse = 30f;

    /// <summary>
    /// Exact critically damped spring step toward <paramref name="target"/>. Velocity carries
    /// across target changes, so consecutive wheel notches blend instead of restarting, and the
    /// result is independent of frame rate for a fixed target.
    /// </summary>
    internal static float Follow(
        float current,
        ref float velocity,
        float target,
        float deltaTime,
        float response = SmoothScrollResponse)
    {
        current = Sanitize(current);
        target = Sanitize(target);
        velocity = Sanitize(velocity);
        response = Math.Max(0f, Sanitize(response));
        deltaTime = Math.Max(0f, Sanitize(deltaTime));
        if (response <= 0f || deltaTime <= 0f) return current;
        float offset = current - target;
        float slope = velocity + response * offset;
        float decay = MathF.Exp(-response * deltaTime);
        float remaining = offset + slope * deltaTime;
        float next = target + remaining * decay;
        velocity = (slope - response * remaining) * decay;
        if (MathF.Abs(next - target) < 0.05f && MathF.Abs(velocity) < 1f)
        {
            velocity = 0f;
            return target;
        }
        return next;
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

    /// <summary>
    /// Wheel target and spring velocity for a panel whose displayed scroll field is written
    /// directly by scrollbar drags, rebuilds, and resets. A displayed value that changed since
    /// the last <see cref="Step"/> is treated as a jump: the target follows it immediately.
    /// </summary>
    internal sealed class SmoothScroller
    {
        private float _target;
        private float _velocity;
        private float _shown;

        internal float Target => _target;

        internal void Scroll(float current, float delta, float maxScroll)
        {
            Sync(current);
            _target = Clamp(_target + Sanitize(delta), maxScroll);
        }

        internal float Step(float current, float maxScroll, float deltaTime)
        {
            Sync(current);
            _target = Clamp(_target, maxScroll);
            float next = Follow(Clamp(current, maxScroll), ref _velocity, _target, deltaTime);
            float clamped = Clamp(next, maxScroll);
            if (clamped != next) _velocity = 0f;
            _shown = clamped;
            return clamped;
        }

        private void Sync(float current)
        {
            current = Sanitize(current);
            if (current == _shown) return;
            _shown = current;
            _target = current;
            _velocity = 0f;
        }
    }

    private static float Sanitize(float value)
        => float.IsFinite(value) ? value : 0f;
}
