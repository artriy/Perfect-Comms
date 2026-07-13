using System;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SuccessfulSendGateTests
{
    private static readonly DateTime Start = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FailedAttemptUsesShortRetryAndDoesNotConsumeSuccessfulRetryWindow()
    {
        var gate = new SuccessfulSendGate(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));

        Assert.True(gate.CanAttempt(Start));
        gate.RecordAttempt(Start, succeeded: false);

        Assert.False(gate.CanAttempt(Start.AddMilliseconds(249)));
        Assert.True(gate.CanAttempt(Start.AddMilliseconds(250)));
        gate.RecordAttempt(Start.AddMilliseconds(250), succeeded: false);
        Assert.False(gate.CanAttempt(Start.AddMilliseconds(499)));
        Assert.True(gate.CanAttempt(Start.AddMilliseconds(500)));
        Assert.Equal(DateTime.MinValue, gate.LastSuccessUtc);
    }

    [Fact]
    public void SuccessfulAttemptUsesLongWindowWhileForceOnlyBypassesThatWindow()
    {
        var gate = new SuccessfulSendGate(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        gate.RecordAttempt(Start, succeeded: true);

        Assert.False(gate.CanAttempt(Start.AddMilliseconds(249), force: true));
        Assert.True(gate.CanAttempt(Start.AddMilliseconds(250), force: true));
        Assert.False(gate.CanAttempt(Start.AddMilliseconds(250)));
        Assert.False(gate.CanAttempt(Start.AddMilliseconds(1999)));
        Assert.True(gate.CanAttempt(Start.AddMilliseconds(2000)));
    }

    [Fact]
    public void ResetClearsBothRetryWindows()
    {
        var gate = new SuccessfulSendGate(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        gate.RecordAttempt(Start, succeeded: true);

        gate.Reset();

        Assert.Equal(DateTime.MinValue, gate.LastAttemptUtc);
        Assert.Equal(DateTime.MinValue, gate.LastSuccessUtc);
        Assert.True(gate.CanAttempt(Start));
    }
}
