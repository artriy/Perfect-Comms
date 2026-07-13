using System;

namespace VoiceChatPlugin.VoiceChat;

internal enum SupervisorActionKind
{
    None,
    RestartInPlace,
    SwitchSource,
    EnterAllFailed,
    Reprobe,
}

internal readonly struct SupervisorAction
{
    public SupervisorAction(SupervisorActionKind kind, int sourceIndex, string reason)
    {
        Kind = kind;
        SourceIndex = sourceIndex;
        Reason = reason;
    }

    public SupervisorActionKind Kind { get; }
    public int SourceIndex { get; }
    public string Reason { get; }

    public static readonly SupervisorAction None = new(SupervisorActionKind.None, -1, "");
}

internal sealed class CaptureSupervisorCore
{
    private readonly int _sourceCount;
    private readonly int _restartBudget;
    private readonly int _deadWindowsToTrip;
    private readonly int _healthyWindowsToResetBudget;
    private readonly int _reprobeWindows;

    private int _activeIndex;
    private int _deadStreak;
    private int _healthyStreak;
    private int _restartsUsed;
    private bool _allFailed;
    private int _reprobeCountdown;

    public CaptureSupervisorCore(
        int sourceCount,
        int restartBudget = 3,
        int deadWindowsToTrip = 3,
        int healthyWindowsToResetBudget = 2,
        int reprobeWindows = 6)
    {
        if (sourceCount < 1) throw new ArgumentOutOfRangeException(nameof(sourceCount));
        _sourceCount = sourceCount;
        _restartBudget = Math.Max(0, restartBudget);
        _deadWindowsToTrip = Math.Max(1, deadWindowsToTrip);
        _healthyWindowsToResetBudget = Math.Max(1, healthyWindowsToResetBudget);
        _reprobeWindows = Math.Max(1, reprobeWindows);
    }

    public int ActiveIndex => _activeIndex;
    public bool IsAllFailed => _allFailed;

    public SupervisorAction OnHealthWindow(CaptureHealth health)
    {
        if (_allFailed)
        {
            if (health == CaptureHealth.Healthy)
            {
                _allFailed = false;
                _reprobeCountdown = 0;
                _deadStreak = 0;
                return SupervisorAction.None;
            }
            if (health != CaptureHealth.Dead)
                return SupervisorAction.None;
            _reprobeCountdown++;
            if (_reprobeCountdown < _reprobeWindows)
                return SupervisorAction.None;
            _reprobeCountdown = 0;
            return PromoteToTop("reprobe", SupervisorActionKind.Reprobe);
        }

        if (health == CaptureHealth.Healthy)
        {
            _deadStreak = 0;
            _healthyStreak++;
            if (_healthyStreak >= _healthyWindowsToResetBudget)
                _restartsUsed = 0;
            return SupervisorAction.None;
        }

        if (health == CaptureHealth.Silent)
        {
            _deadStreak = 0;
            return SupervisorAction.None;
        }

        _healthyStreak = 0;
        _deadStreak++;
        if (_deadStreak < _deadWindowsToTrip)
            return SupervisorAction.None;

        _deadStreak = 0;
        return Trip("dead-window");
    }

    public SupervisorAction OnHeartbeatLost(string reason)
    {
        if (_allFailed)
            return SupervisorAction.None;
        _healthyStreak = 0;
        _deadStreak = 0;
        return Trip(reason);
    }

    public SupervisorAction OnBoundary()
    {
        if (_allFailed)
        {
            _allFailed = false;
            _reprobeCountdown = 0;
            return PromoteToTop("boundary", SupervisorActionKind.SwitchSource);
        }
        if (_activeIndex == 0)
            return SupervisorAction.None;
        return PromoteToTop("boundary", SupervisorActionKind.SwitchSource);
    }

    public void NoteSwitchApplied(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _sourceCount)
            throw new ArgumentOutOfRangeException(nameof(newIndex));
        _activeIndex = newIndex;
        _restartsUsed = 0;
        _deadStreak = 0;
        _healthyStreak = 0;
    }

    public void ResetForSession()
    {
        _activeIndex = 0;
        _deadStreak = 0;
        _healthyStreak = 0;
        _restartsUsed = 0;
        _allFailed = false;
        _reprobeCountdown = 0;
    }

    private SupervisorAction Trip(string reason)
    {
        if (_restartsUsed < _restartBudget)
        {
            _restartsUsed++;
            return new SupervisorAction(SupervisorActionKind.RestartInPlace, _activeIndex, reason);
        }
        if (_activeIndex + 1 < _sourceCount)
        {
            _restartsUsed = 0;
            return new SupervisorAction(SupervisorActionKind.SwitchSource, _activeIndex + 1, reason);
        }
        _allFailed = true;
        _reprobeCountdown = 0;
        return new SupervisorAction(SupervisorActionKind.EnterAllFailed, _activeIndex, reason);
    }

    private SupervisorAction PromoteToTop(string reason, SupervisorActionKind kind)
        => new(kind, 0, reason);
}
