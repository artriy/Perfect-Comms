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
