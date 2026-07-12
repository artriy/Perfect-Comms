using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class CaptureSupervisorCoreTests
{
    private static CaptureSupervisorCore NewCore(int sources = 3)
        => new CaptureSupervisorCore(sources);

    [Fact]
    public void StartsOnHighestPrioritySource()
    {
        var core = NewCore();
        Xunit.Assert.Equal(0, core.ActiveIndex);
        Xunit.Assert.False(core.IsAllFailed);
    }

    [Fact]
    public void HealthyWindowDoesNothing()
    {
        var core = NewCore();
        var action = core.OnHealthWindow(CaptureHealth.Healthy);
        Xunit.Assert.Equal(SupervisorActionKind.None, action.Kind);
        Xunit.Assert.Equal(0, core.ActiveIndex);
    }

    [Fact]
    public void SilentWindowNeverFailsOver()
    {
        var core = NewCore();
        for (var i = 0; i < 50; i++)
        {
            var action = core.OnHealthWindow(CaptureHealth.Silent);
            Xunit.Assert.Equal(SupervisorActionKind.None, action.Kind);
        }
        Xunit.Assert.Equal(0, core.ActiveIndex);
        Xunit.Assert.False(core.IsAllFailed);
    }

    [Fact]
    public void DeadTripsOnlyAfterThreeWindows()
    {
        var core = NewCore();
        Xunit.Assert.Equal(SupervisorActionKind.None, core.OnHealthWindow(CaptureHealth.Dead).Kind);
        Xunit.Assert.Equal(SupervisorActionKind.None, core.OnHealthWindow(CaptureHealth.Dead).Kind);
        var third = core.OnHealthWindow(CaptureHealth.Dead);
        Xunit.Assert.Equal(SupervisorActionKind.RestartInPlace, third.Kind);
        Xunit.Assert.Equal(0, third.SourceIndex);
    }

    [Fact]
    public void DeadStreakResetsWhenSamplesReturn()
    {
        var core = NewCore();
        core.OnHealthWindow(CaptureHealth.Dead);
        core.OnHealthWindow(CaptureHealth.Dead);
        Xunit.Assert.Equal(SupervisorActionKind.None, core.OnHealthWindow(CaptureHealth.Healthy).Kind);
        Xunit.Assert.Equal(SupervisorActionKind.None, core.OnHealthWindow(CaptureHealth.Dead).Kind);
        Xunit.Assert.Equal(SupervisorActionKind.None, core.OnHealthWindow(CaptureHealth.Dead).Kind);
        Xunit.Assert.Equal(SupervisorActionKind.RestartInPlace, core.OnHealthWindow(CaptureHealth.Dead).Kind);
    }

    [Fact]
    public void BudgetBlownSwitchesToNextSource()
    {
        var core = NewCore(3);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
            var trip = core.OnHealthWindow(CaptureHealth.Dead);
            Xunit.Assert.Equal(SupervisorActionKind.RestartInPlace, trip.Kind);
        }
        core.OnHealthWindow(CaptureHealth.Dead);
        core.OnHealthWindow(CaptureHealth.Dead);
        var blown = core.OnHealthWindow(CaptureHealth.Dead);
        Xunit.Assert.Equal(SupervisorActionKind.SwitchSource, blown.Kind);
        Xunit.Assert.Equal(1, blown.SourceIndex);
        core.NoteSwitchApplied(1);
        Xunit.Assert.Equal(1, core.ActiveIndex);
    }

    [Fact]
    public void BudgetIsPerSourceAfterSwitch()
    {
        var core = NewCore(3);
        DrainSourceToSwitch(core, 0, 1);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
            var trip = core.OnHealthWindow(CaptureHealth.Dead);
            Xunit.Assert.Equal(SupervisorActionKind.RestartInPlace, trip.Kind);
            Xunit.Assert.Equal(1, trip.SourceIndex);
        }
    }

    [Fact]
    public void HealthyWindowsResetTheBudget()
    {
        var core = NewCore(3);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
        }
        core.OnHealthWindow(CaptureHealth.Healthy);
        core.OnHealthWindow(CaptureHealth.Healthy);
        core.OnHealthWindow(CaptureHealth.Dead);
        core.OnHealthWindow(CaptureHealth.Dead);
        var afterReset = core.OnHealthWindow(CaptureHealth.Dead);
        Xunit.Assert.Equal(SupervisorActionKind.RestartInPlace, afterReset.Kind);
        Xunit.Assert.Equal(0, core.ActiveIndex);
    }

    [Fact]
    public void HeartbeatLostTripsDeadImmediately()
    {
        var core = NewCore(3);
        var action = core.OnHeartbeatLost("socket-closed");
        Xunit.Assert.Equal(SupervisorActionKind.RestartInPlace, action.Kind);
        Xunit.Assert.Equal(0, action.SourceIndex);
        Xunit.Assert.Equal("socket-closed", action.Reason);
    }

    [Fact]
    public void AllSourcesExhaustedEntersAllFailedOnce()
    {
        var core = NewCore(3);
        DrainSourceToSwitch(core, 0, 1);
        DrainSourceToSwitch(core, 1, 2);
        var failed = DrainSourceToAllFailed(core, 2);
        Xunit.Assert.Equal(SupervisorActionKind.EnterAllFailed, failed.Kind);
        Xunit.Assert.True(core.IsAllFailed);
        core.OnHealthWindow(CaptureHealth.Dead);
        var again = core.OnHealthWindow(CaptureHealth.Dead);
        Xunit.Assert.NotEqual(SupervisorActionKind.EnterAllFailed, again.Kind);
    }

    [Fact]
    public void AllFailedReprobesSlowlyAfterReprobeWindows()
    {
        var core = NewCore(3);
        DrainSourceToSwitch(core, 0, 1);
        DrainSourceToSwitch(core, 1, 2);
        DrainSourceToAllFailed(core, 2);
        SupervisorAction reprobe = default;
        for (var i = 0; i < 6; i++)
            reprobe = core.OnHealthWindow(CaptureHealth.Dead);
        Xunit.Assert.Equal(SupervisorActionKind.Reprobe, reprobe.Kind);
        Xunit.Assert.Equal(0, reprobe.SourceIndex);
    }

    [Fact]
    public void BoundaryUpgradesToHigherPrioritySource()
    {
        var core = NewCore(3);
        DrainSourceToSwitch(core, 0, 1);
        var boundary = core.OnBoundary();
        Xunit.Assert.Equal(SupervisorActionKind.SwitchSource, boundary.Kind);
        Xunit.Assert.Equal(0, boundary.SourceIndex);
        core.NoteSwitchApplied(0);
        Xunit.Assert.Equal(0, core.ActiveIndex);
    }

    [Fact]
    public void BoundaryOnTopSourceDoesNothing()
    {
        var core = NewCore(3);
        var boundary = core.OnBoundary();
        Xunit.Assert.Equal(SupervisorActionKind.None, boundary.Kind);
        Xunit.Assert.Equal(0, core.ActiveIndex);
    }

    [Fact]
    public void BoundaryFromAllFailedRestartsAtTop()
    {
        var core = NewCore(3);
        DrainSourceToSwitch(core, 0, 1);
        DrainSourceToSwitch(core, 1, 2);
        DrainSourceToAllFailed(core, 2);
        var boundary = core.OnBoundary();
        Xunit.Assert.Equal(SupervisorActionKind.SwitchSource, boundary.Kind);
        Xunit.Assert.Equal(0, boundary.SourceIndex);
        core.NoteSwitchApplied(0);
        Xunit.Assert.False(core.IsAllFailed);
        Xunit.Assert.Equal(0, core.ActiveIndex);
    }

    [Fact]
    public void SingleSourceAndroidNeverSwitchesAwayJustRestartsThenAllFailed()
    {
        var core = NewCore(1);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
            var trip = core.OnHealthWindow(CaptureHealth.Dead);
            Xunit.Assert.Equal(SupervisorActionKind.RestartInPlace, trip.Kind);
            Xunit.Assert.Equal(0, trip.SourceIndex);
        }
        core.OnHealthWindow(CaptureHealth.Dead);
        core.OnHealthWindow(CaptureHealth.Dead);
        var failed = core.OnHealthWindow(CaptureHealth.Dead);
        Xunit.Assert.Equal(SupervisorActionKind.EnterAllFailed, failed.Kind);
        Xunit.Assert.True(core.IsAllFailed);
    }

    [Fact]
    public void ResetForSessionReturnsToTopAndClearsState()
    {
        var core = NewCore(3);
        DrainSourceToSwitch(core, 0, 1);
        core.ResetForSession();
        Xunit.Assert.Equal(0, core.ActiveIndex);
        Xunit.Assert.False(core.IsAllFailed);
        Xunit.Assert.Equal(SupervisorActionKind.None, core.OnHealthWindow(CaptureHealth.Dead).Kind);
        Xunit.Assert.Equal(SupervisorActionKind.None, core.OnHealthWindow(CaptureHealth.Dead).Kind);
        Xunit.Assert.Equal(SupervisorActionKind.RestartInPlace, core.OnHealthWindow(CaptureHealth.Dead).Kind);
    }

    private static void DrainSourceToSwitch(CaptureSupervisorCore core, int from, int to)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
        }
        core.OnHealthWindow(CaptureHealth.Dead);
        core.OnHealthWindow(CaptureHealth.Dead);
        var blown = core.OnHealthWindow(CaptureHealth.Dead);
        Xunit.Assert.Equal(SupervisorActionKind.SwitchSource, blown.Kind);
        Xunit.Assert.Equal(to, blown.SourceIndex);
        core.NoteSwitchApplied(to);
        Xunit.Assert.Equal(to, core.ActiveIndex);
    }

    private static SupervisorAction DrainSourceToAllFailed(CaptureSupervisorCore core, int lastIndex)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
            core.OnHealthWindow(CaptureHealth.Dead);
        }
        core.OnHealthWindow(CaptureHealth.Dead);
        core.OnHealthWindow(CaptureHealth.Dead);
        return core.OnHealthWindow(CaptureHealth.Dead);
    }
}
