using System;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class CaptureSupervisor
{
    private readonly CaptureSupervisorCore _core;
    private readonly Action<int, string> _restartInPlace;
    private readonly Action<int, string> _switchTo;
    private readonly Action<string> _onAllFailed;

    public CaptureSupervisor(
        int sourceCount,
        Action<int, string> restartInPlace,
        Action<int, string> switchTo,
        Action<string> onAllFailed)
    {
        _core = new CaptureSupervisorCore(sourceCount);
        _restartInPlace = restartInPlace;
        _switchTo = switchTo;
        _onAllFailed = onAllFailed;
    }

    public int ActiveIndex => _core.ActiveIndex;
    public bool IsAllFailed => _core.IsAllFailed;

    public static CaptureHealth ClassifySamples(int micWindowSamples, bool unmutedAndCapturing)
    {
        if (!unmutedAndCapturing) return CaptureHealth.Silent;
        return micWindowSamples > 0 ? CaptureHealth.Healthy : CaptureHealth.Dead;
    }

    public void OnStatsWindow(int micWindowSamples, bool unmutedAndCapturing)
        => Apply(_core.OnHealthWindow(ClassifySamples(micWindowSamples, unmutedAndCapturing)));

    public void OnHeartbeatLost(string reason)
        => Apply(_core.OnHeartbeatLost(reason));

    public void OnBoundary()
        => Apply(_core.OnBoundary());

    public void ResetForSession()
        => _core.ResetForSession();

    private void Apply(SupervisorAction action)
    {
        switch (action.Kind)
        {
            case SupervisorActionKind.RestartInPlace:
                _restartInPlace(action.SourceIndex, action.Reason);
                break;
            case SupervisorActionKind.SwitchSource:
            case SupervisorActionKind.Reprobe:
                _switchTo(action.SourceIndex, action.Reason);
                _core.NoteSwitchApplied(action.SourceIndex);
                break;
            case SupervisorActionKind.EnterAllFailed:
                _onAllFailed(action.Reason);
                break;
            case SupervisorActionKind.None:
            default:
                break;
        }
    }
}
