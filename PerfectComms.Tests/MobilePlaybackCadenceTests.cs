using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class MobilePlaybackCadenceTests
{
    [Fact]
    public void AdvancesExactlyOneTwentyMillisecondPeriodPerPull()
    {
        const long frequency = 1_000_000;
        const long now = 10_000_000;

        var first = MobilePlaybackCadence.NextDeadline(0, now, frequency);
        var second = MobilePlaybackCadence.NextDeadline(first, first, frequency);

        Assert.Equal(20_000, first - now);
        Assert.Equal(20_000, second - first);
        Assert.Equal(20, MobilePlaybackCadence.DelayMilliseconds(now, first, frequency));
    }

    [Fact]
    public void LongPauseResetsInsteadOfBurstingCatchUpPulls()
    {
        const long frequency = 1_000_000;
        const long staleDeadline = 1_000_000;
        const long resumedAt = 2_000_000;

        var next = MobilePlaybackCadence.NextDeadline(staleDeadline, resumedAt, frequency);

        Assert.Equal(resumedAt + 20_000, next);
    }

    [Fact]
    public void SlightSchedulerLatenessKeepsTheMonotonicTimeline()
    {
        const long frequency = 1_000_000;
        const long previous = 1_000_000;

        var next = MobilePlaybackCadence.NextDeadline(previous, 1_005_000, frequency);

        Assert.Equal(1_020_000, next);
        Assert.Equal(15, MobilePlaybackCadence.DelayMilliseconds(1_005_000, next, frequency));
    }
}
