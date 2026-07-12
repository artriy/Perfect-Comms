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
}
