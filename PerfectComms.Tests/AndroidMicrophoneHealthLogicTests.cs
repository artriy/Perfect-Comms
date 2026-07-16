using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class AndroidMicrophoneHealthLogicTests
{
    private const long Second = 10_000_000L;

    [Fact]
    public void NotRecordingIsDead()
    {
        var health = AndroidMicrophone.ClassifyPosition(recording: false, now: 100 * Second, lastAdvanceTicks: 99 * Second, deadAfterTicks: 15 * Second);
        Xunit.Assert.Equal(CaptureHealth.Dead, health);
    }

    [Fact]
    public void RecentAdvanceIsHealthy()
    {
        var health = AndroidMicrophone.ClassifyPosition(recording: true, now: 100 * Second, lastAdvanceTicks: 99 * Second, deadAfterTicks: 15 * Second);
        Xunit.Assert.Equal(CaptureHealth.Healthy, health);
    }

    [Fact]
    public void StalledPositionPastDeadlineIsDead()
    {
        var health = AndroidMicrophone.ClassifyPosition(recording: true, now: 100 * Second, lastAdvanceTicks: 80 * Second, deadAfterTicks: 15 * Second);
        Xunit.Assert.Equal(CaptureHealth.Dead, health);
    }

    [Fact]
    public void StalledPositionWithinDeadlineIsHealthy()
    {
        var health = AndroidMicrophone.ClassifyPosition(recording: true, now: 100 * Second, lastAdvanceTicks: 90 * Second, deadAfterTicks: 15 * Second);
        Xunit.Assert.Equal(CaptureHealth.Healthy, health);
    }

    [Theory]
    [InlineData(false, false, 1_000, 0, false)]
    [InlineData(true, true, 1_000, 0, false)]
    [InlineData(true, false, 999, 1_000, false)]
    [InlineData(true, false, 1_000, 1_000, true)]
    [InlineData(true, false, 1_001, 1_000, true)]
    public void RecoveryRequiresAnOutstandingRequestAndDueDeadline(
        bool requested,
        bool recording,
        long nowMs,
        long nextRecoveryMs,
        bool expected)
    {
        Xunit.Assert.Equal(expected,
            AndroidMicrophone.ShouldAttemptRecovery(requested, recording, nowMs, nextRecoveryMs));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void DestroyedClipCannotRemainStuckInRecordingState(
        bool recording,
        bool clipAvailable,
        bool expected)
    {
        Xunit.Assert.Equal(
            expected,
            AndroidMicrophone.ShouldRecoverDestroyedClip(recording, clipAvailable));
    }

    [Theory]
    [InlineData(1, 250)]
    [InlineData(2, 500)]
    [InlineData(3, 1_000)]
    [InlineData(8, 30_000)]
    [InlineData(30, 30_000)]
    public void RecoveryBackoffContinuesAndCapsInsteadOfGivingUp(int attempt, int expectedMs)
    {
        Xunit.Assert.Equal(expectedMs,
            AndroidMicrophone.RecoveryDelayMilliseconds(attempt, 250, 30_000));
    }

    [Theory]
    [InlineData(1, 3_000)]
    [InlineData(4, 3_000)]
    [InlineData(5, 4_000)]
    [InlineData(6, 8_000)]
    [InlineData(30, 10_000)]
    public void PlaybackRetryBackoffHonorsCallbackGraceAndCaps(int attempt, int expectedMs)
    {
        Xunit.Assert.Equal(expectedMs,
            AndroidMicrophone.RecoveryDelayMilliseconds(
                attempt, 250, 10_000, minimumDelayMs: 3_000));
    }
}
