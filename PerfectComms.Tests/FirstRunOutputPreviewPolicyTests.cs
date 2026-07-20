using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class FirstRunOutputPreviewPolicyTests
{
    [Fact]
    public void ExplicitSpeakerFailureGetsOneDefaultFallbackAttempt()
    {
        Assert.True(FirstRunOutputPreviewPolicy.ShouldTryDefaultFallback("wasapi:endpoint", false));
        Assert.False(FirstRunOutputPreviewPolicy.ShouldTryDefaultFallback("wasapi:endpoint", true));
        Assert.False(FirstRunOutputPreviewPolicy.ShouldTryDefaultFallback(string.Empty, false));
        Assert.False(FirstRunOutputPreviewPolicy.ShouldTryDefaultFallback(null, false));
    }

    [Fact]
    public void OpaqueSpeakerIdsMatchCaseSensitively()
    {
        Assert.True(FirstRunOutputPreviewPolicy.RequestedOutputMatches(
            "cubeb-endpoint-A", "cubeb-endpoint-A", false));
        Assert.False(FirstRunOutputPreviewPolicy.RequestedOutputMatches(
            "cubeb-endpoint-A", "cubeb-endpoint-a", false));
        Assert.True(FirstRunOutputPreviewPolicy.RequestedOutputMatches(
            string.Empty, string.Empty, true));
        Assert.False(FirstRunOutputPreviewPolicy.RequestedOutputMatches(
            "cubeb-endpoint-A", string.Empty, true));
    }

    [Theory]
    [InlineData("device-unavailable", "That speaker is no longer available. Reconnect it, select it again, or use Default.")]
    [InlineData("device-busy", "That speaker is busy in another app. Close the other audio session or use Default.")]
    [InlineData("permission-denied", "The system denied access to that speaker. Check audio permissions or use Default.")]
    [InlineData("timeout", "The speaker did not respond in time. Reconnect it or use Default.")]
    public void StructuredNativeErrorCodesRemainActionableAcrossAudioBackends(
        string errorCode,
        string expected)
        => Assert.Equal(
            expected,
            FirstRunOutputPreviewPolicy.DescribeNativeOutputFailure(
                "cubeb stream init failed",
                errorCode));

    [Theory]
    [InlineData(
        "pc-capture: playback error: selected output device is unavailable",
        "That speaker is no longer available. Reconnect it, select it again, or use Default.")]
    [InlineData(
        "build output stream: device is busy\r\nclose exclusive app",
        "The system could not open that speaker: device is busy close exclusive app")]
    [InlineData(
        "output stream play: permission denied",
        "The speaker opened, but playback could not start: permission denied")]
    [InlineData(
        "output device callback failed",
        "The speaker stopped responding after playback began. Reconnect it or use Default.")]
    public void NativePlaybackReasonsBecomeActionableStatus(string nativeReason, string expected)
        => Assert.Equal(expected, FirstRunOutputPreviewPolicy.DescribeNativeOutputFailure(nativeReason));

    [Fact]
    public void MissingNativeReasonRetainsCompatibleGenericStatus()
    {
        Assert.Equal(
            "Could not open the selected speaker",
            FirstRunOutputPreviewPolicy.DescribeNativeOutputFailure(null));
        Assert.Equal(
            "The selected speaker stopped during the test",
            FirstRunOutputPreviewPolicy.DescribeNativeOutputFailure("", duringPlayback: true));
    }

    [Theory]
    [InlineData("error", true)]
    [InlineData("stopped", true)]
    [InlineData("stream-started", false)]
    [InlineData("first-callback", false)]
    public void DesktopToneCannotSucceedAfterTerminalPlaybackState(string state, bool expected)
        => Assert.Equal(expected, FirstRunOutputPreviewPolicy.IsTonePlaybackTerminal(state));

    [Theory]
    [InlineData(42L, 42UL, "error", true)]
    [InlineData(42L, 42UL, "stopped", true)]
    [InlineData(42L, 41UL, "error", false)]
    [InlineData(0L, 0UL, "error", false)]
    [InlineData(42L, 42UL, "first-callback", false)]
    public void TerminalPlaybackStateRemainsCorrelatedAfterToneWorkerStops(
        long activeGeneration,
        ulong eventGeneration,
        string state,
        bool expected)
        => Assert.Equal(
            expected,
            FirstRunOutputPreviewPolicy.ShouldApplyTonePlaybackTerminal(
                activeGeneration,
                eventGeneration,
                state));

    [Fact]
    public void CorrelatedTerminalSurvivesActiveGenerationCleanupRace()
    {
        Assert.True(FirstRunOutputPreviewPolicy.ShouldApplyTonePlaybackTerminal(
            activeGeneration: 0,
            eventGeneration: 42,
            state: "error",
            correlatedTerminalPending: true,
            correlatedTerminalGeneration: 42));
        Assert.False(FirstRunOutputPreviewPolicy.ShouldApplyTonePlaybackTerminal(
            activeGeneration: 0,
            eventGeneration: 42,
            state: "error",
            correlatedTerminalPending: false,
            correlatedTerminalGeneration: 42));
        Assert.False(FirstRunOutputPreviewPolicy.ShouldApplyTonePlaybackTerminal(
            activeGeneration: 0,
            eventGeneration: 41,
            state: "error",
            correlatedTerminalPending: true,
            correlatedTerminalGeneration: 42));
    }

}
