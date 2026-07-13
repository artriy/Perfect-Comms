using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Rate-limits real sends without treating an unavailable writer as a successful attempt. Failed
/// attempts use a short retry interval; only a confirmed send consumes the longer network-response
/// interval. A forced attempt bypasses the successful-send interval but never the anti-spin guard.
/// </summary>
internal sealed class SuccessfulSendGate
{
    private readonly TimeSpan _failedRetry;
    private readonly TimeSpan _successfulRetry;

    internal SuccessfulSendGate(TimeSpan failedRetry, TimeSpan successfulRetry)
    {
        if (failedRetry < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(failedRetry));
        if (successfulRetry < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(successfulRetry));
        _failedRetry = failedRetry;
        _successfulRetry = successfulRetry;
    }

    internal DateTime LastAttemptUtc { get; private set; } = DateTime.MinValue;
    internal DateTime LastSuccessUtc { get; private set; } = DateTime.MinValue;

    internal bool CanAttempt(DateTime nowUtc, bool force = false)
    {
        if (Within(nowUtc, LastAttemptUtc, _failedRetry))
            return false;
        if (!force && Within(nowUtc, LastSuccessUtc, _successfulRetry))
            return false;
        return true;
    }

    internal void RecordAttempt(DateTime nowUtc, bool succeeded)
    {
        LastAttemptUtc = nowUtc;
        if (succeeded)
            LastSuccessUtc = nowUtc;
    }

    internal void Reset()
    {
        LastAttemptUtc = DateTime.MinValue;
        LastSuccessUtc = DateTime.MinValue;
    }

    private static bool Within(DateTime nowUtc, DateTime thenUtc, TimeSpan interval)
        => thenUtc != DateTime.MinValue
           && nowUtc >= thenUtc
           && nowUtc - thenUtc < interval;
}
