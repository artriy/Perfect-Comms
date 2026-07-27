using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Pure send/dedupe state for the local radio RPC. Failed sends use the short retry gate and do
/// not advance the advertised state; successful inactive states are heartbeated just like active
/// ones so a lost release always converges.
/// </summary>
internal sealed class RadioStateSyncTracker
{
    private readonly TimeSpan _heartbeatInterval;
    private readonly SuccessfulSendGate _sendGate;
    private byte _lastPlayerId = byte.MaxValue;
    private VoiceRadioState _lastState = VoiceRadioState.None;
    private DateTime _lastSentUtc = DateTime.MinValue;

    internal RadioStateSyncTracker(TimeSpan failedRetry, TimeSpan heartbeatInterval)
    {
        _heartbeatInterval = heartbeatInterval;
        _sendGate = new SuccessfulSendGate(failedRetry, heartbeatInterval);
    }

    internal byte LastPlayerId => _lastPlayerId;
    internal VoiceTeamRadioChannel LastChannel => _lastState.Channel;
    internal string LastManagedKey => _lastState.ManagedKey;
    internal VoiceRadioState LastState => _lastState;

    internal bool ShouldAttempt(
        byte playerId,
        VoiceTeamRadioChannel channel,
        DateTime nowUtc)
        => ShouldAttempt(playerId, VoiceRadioState.BuiltIn(channel), nowUtc);

    internal bool ShouldAttempt(
        byte playerId,
        VoiceRadioState state,
        DateTime nowUtc)
    {
        state = state.Normalize();
        var changed = playerId != _lastPlayerId || state != _lastState;
        var heartbeat = _lastSentUtc == DateTime.MinValue
                        || nowUtc - _lastSentUtc >= _heartbeatInterval;
        return (changed || heartbeat) && _sendGate.CanAttempt(nowUtc, force: changed);
    }

    internal void RecordAttempt(
        byte playerId,
        VoiceTeamRadioChannel channel,
        DateTime nowUtc,
        bool sent)
        => RecordAttempt(playerId, VoiceRadioState.BuiltIn(channel), nowUtc, sent);

    internal void RecordAttempt(
        byte playerId,
        VoiceRadioState state,
        DateTime nowUtc,
        bool sent)
    {
        _sendGate.RecordAttempt(nowUtc, sent);
        if (!sent) return;

        _lastPlayerId = playerId;
        _lastState = state.Normalize();
        _lastSentUtc = nowUtc;
    }

    internal void Reset()
    {
        _lastPlayerId = byte.MaxValue;
        _lastState = VoiceRadioState.None;
        _lastSentUtc = DateTime.MinValue;
        _sendGate.Reset();
    }
}
