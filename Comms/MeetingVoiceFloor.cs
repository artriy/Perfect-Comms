using System;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class MeetingVoiceFloor
{
    internal byte CallerId { get; private set; } = byte.MaxValue;
    private float _duration;
    private float _deadline;
    private bool _started;

    internal void Begin(byte callerId, VoiceRoomSettingsSnapshot settings)
    {
        Reset();
        if (!settings.GracePeriodEnabled || settings.GracePeriodSeconds <= 0f || callerId == byte.MaxValue)
            return;
        CallerId = callerId;
        _duration = settings.GracePeriodSeconds;
    }

    internal void Update(VoiceGamePhase phase, bool meetingReady, bool enabled, float now)
    {
        if (!enabled || phase is VoiceGamePhase.Exile or VoiceGamePhase.EndGame or VoiceGamePhase.Menu or VoiceGamePhase.Lobby)
        {
            Reset();
            return;
        }
        if (phase == VoiceGamePhase.Meeting && meetingReady && CallerId != byte.MaxValue && !_started)
        {
            _deadline = now + _duration;
            _started = true;
        }
        else if (_started && phase == VoiceGamePhase.Tasks)
        {
            Reset();
        }
    }

    internal bool Active(float now) => _started && now < _deadline;
    internal bool Blocks(byte playerId, bool dead, VoiceGamePhase phase, float now)
        => phase == VoiceGamePhase.Meeting && !dead && Active(now) && playerId != CallerId;
    internal int SecondsRemaining(float now) => Active(now) ? Math.Max(1, (int)Math.Ceiling(_deadline - now)) : 0;

    internal void Reset()
    {
        CallerId = byte.MaxValue;
        _duration = 0f;
        _deadline = 0f;
        _started = false;
    }
}
