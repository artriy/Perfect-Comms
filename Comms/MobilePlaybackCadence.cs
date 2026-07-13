using System;

namespace VoiceChatPlugin.VoiceChat;

// Pure clock arithmetic for Android's native 20 ms playout pump. Kept platform-neutral so the
// release test suite can prove that the FFI engine is never advanced at 2x real time.
internal static class MobilePlaybackCadence
{
    internal const int FramesPerSecond = 50;

    public static long NextDeadline(long previousDeadline, long now, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        var period = Math.Max(1L, frequency / FramesPerSecond);
        if (previousDeadline <= 0) return now + period;

        var next = previousDeadline + period;
        // App suspend/debug pauses must not cause a burst of catch-up pulls on resume.
        return now - next > period * 4 ? now + period : next;
    }

    public static int DelayMilliseconds(long now, long deadline, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (deadline <= now) return 0;
        var remaining = deadline - now;
        var milliseconds = (remaining * 1000L + frequency - 1) / frequency;
        return (int)Math.Clamp(milliseconds, 1L, 20L);
    }
}
