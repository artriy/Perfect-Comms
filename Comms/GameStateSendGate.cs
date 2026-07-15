using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>Coalesces per-render-frame mixer state into a bounded media-control cadence.</summary>
internal sealed class GameStateSendGate
{
    private readonly object _sync = new();
    private readonly long _minimumIntervalMs;
    private readonly long _heartbeatIntervalMs;
    private long _lastSentMs = long.MinValue;
    private ulong _lastFingerprint;
    private bool _hasSent;

    public GameStateSendGate(long minimumIntervalMs = 50, long heartbeatIntervalMs = 1_000)
    {
        if (minimumIntervalMs <= 0) throw new ArgumentOutOfRangeException(nameof(minimumIntervalMs));
        if (heartbeatIntervalMs < minimumIntervalMs)
            throw new ArgumentOutOfRangeException(nameof(heartbeatIntervalMs));
        _minimumIntervalMs = minimumIntervalMs;
        _heartbeatIntervalMs = heartbeatIntervalMs;
    }

    public bool ShouldSend(long nowMs, ulong fingerprint)
    {
        lock (_sync)
        {
            if (!_hasSent)
            {
                Remember(nowMs, fingerprint);
                return true;
            }

            var elapsed = nowMs >= _lastSentMs ? nowMs - _lastSentMs : long.MaxValue;
            var due = fingerprint != _lastFingerprint
                ? elapsed >= _minimumIntervalMs
                : elapsed >= _heartbeatIntervalMs;
            if (due) Remember(nowMs, fingerprint);
            return due;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _hasSent = false;
            _lastSentMs = long.MinValue;
            _lastFingerprint = 0;
        }
    }

    private void Remember(long nowMs, ulong fingerprint)
    {
        _hasSent = true;
        _lastSentMs = nowMs;
        _lastFingerprint = fingerprint;
    }
}
