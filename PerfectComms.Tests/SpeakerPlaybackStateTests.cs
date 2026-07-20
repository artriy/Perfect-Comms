using VoiceChatPlugin.VoiceChat;
using Xunit;

#if WINDOWS
public sealed class SpeakerPlaybackStateTests
{
    [Fact]
    public void ReadinessRequiresConfirmedRunningNativePlaybackState()
    {
        var accepted = State("command-accepted", running: false, action: "select-output-device");
        var started = State("stream-started", running: true, generation: 7);
        var callback = State("first-callback", running: true, generation: 7);
        var stopped = State("stopped", running: false, generation: 7);

        Assert.False(PerfectCommsVoiceBackend.IsConfirmedSpeakerPlaybackState(accepted));
        Assert.False(PerfectCommsVoiceBackend.IsConfirmedSpeakerPlaybackState(started));
        Assert.True(PerfectCommsVoiceBackend.IsConfirmedSpeakerPlaybackState(callback));
        Assert.False(PerfectCommsVoiceBackend.IsConfirmedSpeakerPlaybackState(stopped));
    }

    [Fact]
    public void ExplicitPlaybackErrorFallsBackOnceButDefaultErrorDoesNotLoop()
    {
        var explicitError = State(
            "error",
            running: false,
            requestedDevice: "headset-id",
            error: "build output stream: device unavailable",
            errorCode: "open-failed");
        var sparseOldHelperError = State("error", running: false);
        var defaultError = State("error", running: false, requestedDefault: true);

        Assert.True(PerfectCommsVoiceBackend.ShouldFallbackSpeakerToDefault(
            "headset-id", fallbackPending: false, explicitError));
        Assert.False(PerfectCommsVoiceBackend.ShouldFallbackSpeakerToDefault(
            "headset-id", fallbackPending: true, explicitError));
        Assert.True(PerfectCommsVoiceBackend.ShouldFallbackSpeakerToDefault(
            "headset-id", fallbackPending: false, sparseOldHelperError));
        Assert.False(PerfectCommsVoiceBackend.ShouldFallbackSpeakerToDefault(
            string.Empty, fallbackPending: false, defaultError));
    }

    [Fact]
    public void PlaybackEventsAreBoundToRequestAndGenerationWhenFieldsArePresent()
    {
        var current = State("stream-started", true, 9, requestedDevice: "current-id");
        var staleRequest = State("error", false, 9, requestedDevice: "old-id");
        var staleDefault = State("error", false, 9, requestedDefault: true);
        var sparseOldHelper = State("error", false, 0);

        Assert.True(PerfectCommsVoiceBackend.SpeakerPlaybackStateMatchesRequest(
            "current-id", current));
        Assert.False(PerfectCommsVoiceBackend.SpeakerPlaybackStateMatchesRequest(
            "current-id", staleRequest));
        Assert.False(PerfectCommsVoiceBackend.SpeakerPlaybackStateMatchesRequest(
            "current-id", staleDefault));
        Assert.True(PerfectCommsVoiceBackend.SpeakerPlaybackStateMatchesRequest(
            string.Empty, staleDefault));
        Assert.True(PerfectCommsVoiceBackend.SpeakerPlaybackStateMatchesRequest(
            "current-id", sparseOldHelper));
        Assert.True(PerfectCommsVoiceBackend.SpeakerPlaybackGenerationMatches(9, current));
        Assert.False(PerfectCommsVoiceBackend.SpeakerPlaybackGenerationMatches(8, current));
        Assert.True(PerfectCommsVoiceBackend.SpeakerPlaybackGenerationMatches(8, sparseOldHelper));
    }

    [Fact]
    public void PlaybackFailureDescriptionUsesOptionalReasonAndBoundsUnsafeText()
    {
        var detailed = State(
            "error",
            false,
            error: "build failed\r\nendpoint unavailable",
            errorCode: "open-failed");
        var oldHelper = State("error", false);

        Assert.Equal(
            "open-failed: build failed  endpoint unavailable",
            PerfectCommsVoiceBackend.DescribeSpeakerPlaybackFailure(detailed));
        Assert.Equal(
            "native playback open failed",
            PerfectCommsVoiceBackend.DescribeSpeakerPlaybackFailure(oldHelper));
    }

    [Theory]
    [InlineData("device-busy", "", true)]
    [InlineData("timeout", "", true)]
    [InlineData("stream-error", "", true)]
    [InlineData("open-failed", "", true)]
    [InlineData("device-unavailable", "", false)]
    [InlineData("unsupported-config", "", false)]
    [InlineData("permission-denied", "", false)]
    [InlineData("", "selected output device is unavailable", false)]
    [InlineData("", "permission denied", false)]
    [InlineData("", "InvalidFormat", false)]
    [InlineData("", "temporary backend error", true)]
    public void ExplicitFallbackRetryIsLimitedToPotentiallyTransientFailures(
        string errorCode,
        string error,
        bool expected)
    {
        var state = State(
            "error",
            running: false,
            requestedDevice: "headset-id",
            error: error,
            errorCode: errorCode);

        Assert.Equal(expected, PerfectCommsVoiceBackend.ShouldRetrySpeakerAfterFallback(state));
    }

    [Fact]
    public void DefaultPlaybackFailureNeverSchedulesExplicitRetry()
    {
        var state = State(
            "error",
            running: false,
            requestedDefault: true,
            error: "temporary backend error",
            errorCode: "timeout");

        Assert.False(PerfectCommsVoiceBackend.ShouldRetrySpeakerAfterFallback(state));
    }

    private static SidecarPlaybackState State(
        string state,
        bool running,
        ulong generation = 0,
        string action = "",
        string requestedDevice = "",
        bool requestedDefault = false,
        string error = "",
        string errorCode = "")
        => new(
            state,
            action,
            generation,
            requestedDevice,
            string.Empty,
            requestedDefault,
            !string.IsNullOrEmpty(requestedDevice),
            false,
            running,
            error,
            errorCode);
}
#endif
