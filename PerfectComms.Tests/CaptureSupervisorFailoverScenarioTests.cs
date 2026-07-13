using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class CaptureSupervisorFailoverScenarioTests
{
    private sealed record Decision(string Kind, int Index);

    [Fact]
    public void DescendsThroughAllSourcesThenRecoversOnBoundary()
    {
        var log = new List<Decision>();
        var sup = new CaptureSupervisor(
            sourceCount: 3,
            restartInPlace: (i, _) => log.Add(new Decision("restart", i)),
            switchTo: (i, _) => log.Add(new Decision("switch", i)),
            onAllFailed: _ => log.Add(new Decision("allfailed", -1)));

        for (var window = 0; window < 60; window++)
            sup.OnStatsWindow(0, unmutedAndCapturing: true);

        Xunit.Assert.Contains(new Decision("switch", 1), log);
        Xunit.Assert.Contains(new Decision("switch", 2), log);
        Xunit.Assert.Contains(new Decision("allfailed", -1), log);
        Xunit.Assert.Single(log, d => d.Kind == "allfailed");
        Xunit.Assert.True(sup.IsAllFailed);

        sup.OnBoundary();
        Xunit.Assert.Equal(0, sup.ActiveIndex);
        Xunit.Assert.False(sup.IsAllFailed);
    }

    [Fact]
    public void HealthyWindowMidDescentResetsBudgetAndStaysOnSource()
    {
        var switches = new List<int>();
        var sup = new CaptureSupervisor(
            sourceCount: 3,
            restartInPlace: (_, _) => { },
            switchTo: (i, _) => switches.Add(i),
            onAllFailed: _ => { });

        sup.OnStatsWindow(0, true);
        sup.OnStatsWindow(0, true);
        sup.OnStatsWindow(0, true);
        sup.OnStatsWindow(48000, true);
        sup.OnStatsWindow(48000, true);

        for (var window = 0; window < 9; window++)
            sup.OnStatsWindow(0, true);

        Xunit.Assert.Empty(switches);
        Xunit.Assert.Equal(0, sup.ActiveIndex);
    }
}
