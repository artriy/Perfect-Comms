using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class CaptureSupervisorSampleHealthTests
{
    [Fact]
    public void NotCapturingIsSilentNotDead()
    {
        Xunit.Assert.Equal(CaptureHealth.Silent, CaptureSupervisor.ClassifySamples(0, unmutedAndCapturing: false));
    }

    [Fact]
    public void ZeroSamplesWhileCapturingIsDead()
    {
        Xunit.Assert.Equal(CaptureHealth.Dead, CaptureSupervisor.ClassifySamples(0, unmutedAndCapturing: true));
    }

    [Fact]
    public void SamplesFlowingIsHealthy()
    {
        Xunit.Assert.Equal(CaptureHealth.Healthy, CaptureSupervisor.ClassifySamples(48000, unmutedAndCapturing: true));
    }

    [Theory]
    [InlineData(true, true, true, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, true, true, false, true)]
    public void ListenOnlyOrUnavailableCaptureDoesNotDriveSupervisorRestarts(
        bool muted,
        bool requested,
        bool ready,
        bool awaitingFirstLevel,
        bool transitionInFlight)
    {
        Assert.False(PerfectCommsVoiceBackend.ShouldSuperviseCapture(
            muted,
            requested,
            ready,
            awaitingFirstLevel,
            transitionInFlight));
    }

    [Fact]
    public void HealthyRequestedCaptureIsSupervised()
    {
        Assert.True(PerfectCommsVoiceBackend.ShouldSuperviseCapture(
            muted: false,
            microphoneRequested: true,
            microphoneReady: true,
            captureAwaitingFirstLevel: false,
            transitionInFlight: false));
    }

    [Fact]
    public void NewlyCommandedCaptureGetsBoundedCallbackSupervisionWithoutBeingReady()
    {
        Assert.True(PerfectCommsVoiceBackend.ShouldSuperviseCapture(
            muted: false,
            microphoneRequested: true,
            microphoneReady: false,
            captureAwaitingFirstLevel: true,
            transitionInFlight: false));
    }

    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    public void ListenOnlyOrUnavailableMicNeverTriggersWholeHelperRebuild(
        bool muted,
        bool requested,
        bool everProduced,
        bool disposed)
    {
        Assert.False(PerfectCommsVoiceBackend.ShouldRebuildAfterCaptureExhausted(
            muted, requested, everProduced, disposed));
    }

    [Fact]
    public void PreviouslyHealthyStalledCaptureEscalatesAfterBoundedRestarts()
    {
        Assert.True(PerfectCommsVoiceBackend.ShouldRebuildAfterCaptureExhausted(
            muted: false,
            microphoneRequested: true,
            captureEverProduced: true,
            disposed: false));
    }

    [Fact]
    public void CaptureProofBelongsOnlyToTheCurrentSourceGeneration()
    {
        Assert.True(PerfectCommsVoiceBackend.IsCaptureSourceProven(7, 7));
        Assert.False(PerfectCommsVoiceBackend.IsCaptureSourceProven(8, 7));
        Assert.False(PerfectCommsVoiceBackend.IsCaptureSourceProven(0, 0));
    }

    [Fact]
    public void OneQueuedLevelCannotProveANewCaptureAttempt()
    {
        const long attempt = 12;
        const long acceptAfter = 1_000;

        Assert.False(PerfectCommsVoiceBackend.ShouldPromoteSidecarLevel(
            attempt, attempt, priorConfirmationCount: 0, nowTimestamp: acceptAfter, acceptAfterTimestamp: acceptAfter));
        Assert.True(PerfectCommsVoiceBackend.ShouldPromoteSidecarLevel(
            attempt, attempt, priorConfirmationCount: 1, nowTimestamp: acceptAfter, acceptAfterTimestamp: acceptAfter));
    }

    [Fact]
    public void PreGuardOrPreviousAttemptLevelsCannotProveCurrentCapture()
    {
        Assert.False(PerfectCommsVoiceBackend.ShouldPromoteSidecarLevel(
            attemptGeneration: 4,
            confirmationGeneration: 4,
            priorConfirmationCount: 20,
            nowTimestamp: 999,
            acceptAfterTimestamp: 1_000));
        Assert.False(PerfectCommsVoiceBackend.ShouldPromoteSidecarLevel(
            attemptGeneration: 5,
            confirmationGeneration: 4,
            priorConfirmationCount: 20,
            nowTimestamp: 1_000,
            acceptAfterTimestamp: 1_000));
    }

    [Theory]
    [InlineData(3, 3, true, false, true)]
    [InlineData(2, 3, true, false, false)]
    [InlineData(3, 3, false, false, false)]
    [InlineData(3, 3, true, true, false)]
    public void AsyncVoiceStartCanMutateOnlyItsCurrentLeaseGeneration(
        long taskGeneration,
        long currentGeneration,
        bool leaseMatches,
        bool disposed,
        bool expected)
    {
        Assert.Equal(expected, PerfectCommsVoiceBackend.IsCurrentVoiceStart(
            taskGeneration, currentGeneration, leaseMatches, disposed));
    }

    [Fact]
    public void DeadDecisionInvokesRestartCallbackAfterThreeWindows()
    {
        int restartIndex = -1;
        string restartReason = "";
        var sup = new CaptureSupervisor(
            sourceCount: 3,
            restartInPlace: (i, r) => { restartIndex = i; restartReason = r; },
            switchTo: (_, _) => { },
            onAllFailed: _ => { });

        sup.OnStatsWindow(0, unmutedAndCapturing: true);
        sup.OnStatsWindow(0, unmutedAndCapturing: true);
        Xunit.Assert.Equal(-1, restartIndex);
        sup.OnStatsWindow(0, unmutedAndCapturing: true);
        Xunit.Assert.Equal(0, restartIndex);
        Xunit.Assert.False(string.IsNullOrEmpty(restartReason));
    }

    [Fact]
    public void HeartbeatLostInvokesRestartImmediately()
    {
        int restartIndex = -1;
        var sup = new CaptureSupervisor(
            sourceCount: 3,
            restartInPlace: (i, _) => restartIndex = i,
            switchTo: (_, _) => { },
            onAllFailed: _ => { });
        sup.OnHeartbeatLost("socket-closed");
        Xunit.Assert.Equal(0, restartIndex);
    }

    [Fact]
    public void BudgetBlownInvokesSwitchCallbackAndAdvancesActiveIndex()
    {
        int switchIndex = -1;
        var sup = new CaptureSupervisor(
            sourceCount: 3,
            restartInPlace: (_, _) => { },
            switchTo: (i, _) => switchIndex = i,
            onAllFailed: _ => { });

        for (var window = 0; window < 12; window++)
            sup.OnStatsWindow(0, unmutedAndCapturing: true);

        Xunit.Assert.Equal(1, switchIndex);
        Xunit.Assert.Equal(1, sup.ActiveIndex);
    }

    [Fact]
    public void AllSourcesDeadRaisesAllFailedOnce()
    {
        int allFailedCount = 0;
        var sup = new CaptureSupervisor(
            sourceCount: 1,
            restartInPlace: (_, _) => { },
            switchTo: (_, _) => { },
            onAllFailed: _ => allFailedCount++);

        for (var window = 0; window < 30; window++)
            sup.OnStatsWindow(0, unmutedAndCapturing: true);

        Xunit.Assert.Equal(1, allFailedCount);
        Xunit.Assert.True(sup.IsAllFailed);
    }
}
