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
    private VoiceTeamRadioChannel _lastChannel = VoiceTeamRadioChannel.None;
    private DateTime _lastSentUtc = DateTime.MinValue;

    internal RadioStateSyncTracker(TimeSpan failedRetry, TimeSpan heartbeatInterval)
    {
        _heartbeatInterval = heartbeatInterval;
        _sendGate = new SuccessfulSendGate(failedRetry, heartbeatInterval);
    }

    internal byte LastPlayerId => _lastPlayerId;
    internal VoiceTeamRadioChannel LastChannel => _lastChannel;

    internal bool ShouldAttempt(
        byte playerId,
        VoiceTeamRadioChannel channel,
        DateTime nowUtc)
    {
        channel = VoiceTeamRadioChannels.Normalize(channel);
        var changed = playerId != _lastPlayerId || channel != _lastChannel;
        var heartbeat = _lastSentUtc == DateTime.MinValue
                        || nowUtc - _lastSentUtc >= _heartbeatInterval;
        return (changed || heartbeat) && _sendGate.CanAttempt(nowUtc, force: changed);
    }

    internal void RecordAttempt(
        byte playerId,
        VoiceTeamRadioChannel channel,
        DateTime nowUtc,
        bool sent)
    {
        _sendGate.RecordAttempt(nowUtc, sent);
        if (!sent) return;

        _lastPlayerId = playerId;
        _lastChannel = VoiceTeamRadioChannels.Normalize(channel);
        _lastSentUtc = nowUtc;
    }

    internal void Reset()
    {
        _lastPlayerId = byte.MaxValue;
        _lastChannel = VoiceTeamRadioChannel.None;
        _lastSentUtc = DateTime.MinValue;
        _sendGate.Reset();
    }
}
