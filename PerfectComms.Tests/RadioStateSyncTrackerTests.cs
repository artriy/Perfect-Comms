using System;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class RadioStateSyncTrackerTests
{
    [Fact]
    public void FailedReleaseDoesNotAdvanceStateAndRetriesQuickly()
    {
        var tracker = new RadioStateSyncTracker(
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1));
        var start = DateTime.UtcNow;

        Assert.True(tracker.ShouldAttempt(4, VoiceTeamRadioChannel.Impostors, start));
        tracker.RecordAttempt(4, VoiceTeamRadioChannel.Impostors, start, sent: true);
        Assert.Equal(VoiceTeamRadioChannel.Impostors, tracker.LastChannel);

        var failedRelease = start.AddMilliseconds(251);
        Assert.True(tracker.ShouldAttempt(4, VoiceTeamRadioChannel.None, failedRelease));
        tracker.RecordAttempt(4, VoiceTeamRadioChannel.None, failedRelease, sent: false);
        Assert.Equal(VoiceTeamRadioChannel.Impostors, tracker.LastChannel);

        Assert.False(tracker.ShouldAttempt(
            4, VoiceTeamRadioChannel.None, failedRelease.AddMilliseconds(249)));
        var retry = failedRelease.AddMilliseconds(250);
        Assert.True(tracker.ShouldAttempt(4, VoiceTeamRadioChannel.None, retry));
        tracker.RecordAttempt(4, VoiceTeamRadioChannel.None, retry, sent: true);
        Assert.Equal(VoiceTeamRadioChannel.None, tracker.LastChannel);
    }

    [Fact]
    public void InactiveStateIsHeartbeated()
    {
        var tracker = new RadioStateSyncTracker(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1));
        var start = DateTime.UtcNow;

        tracker.RecordAttempt(8, VoiceTeamRadioChannel.None, start, sent: true);
        Assert.False(tracker.ShouldAttempt(8, VoiceTeamRadioChannel.None, start.AddMilliseconds(999)));
        Assert.True(tracker.ShouldAttempt(8, VoiceTeamRadioChannel.None, start.AddMilliseconds(1000)));
    }
}
