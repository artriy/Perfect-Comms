using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class VoiceActivityTracker
{
    private const float OnThreshold = 0.004f;
    private const float GraceSeconds = 0.22f;
    private const float DecayPerSecond = 10f;

    private float _lastActiveTime = -999f;

    public float VadThreshold { get; set; } = OnThreshold;
    public float Level { get; private set; }
    public bool IsSpeaking { get; private set; }

    public void PushSamples(float[] samples, int count)
    {
        float peak = 0f;
        int limit = Mathf.Min(count, samples.Length);
        for (int i = 0; i < limit; i++)
        {
            float abs = Mathf.Abs(samples[i]);
            if (abs > peak) peak = abs;
        }

        PushLevel(peak);
    }

    public void PushLevel(float level)
    {
        float onThreshold = Mathf.Max(0.0001f, VadThreshold);
        float offThreshold = onThreshold * 0.5f;

        float decay = Mathf.Clamp01(Time.deltaTime * DecayPerSecond);
        Level = Mathf.Max(level, Mathf.Lerp(Level, 0f, decay));

        if (level >= onThreshold)
        {
            IsSpeaking = true;
            _lastActiveTime = Time.time;
        }
        else if (IsSpeaking && Time.time - _lastActiveTime > GraceSeconds && Level <= offThreshold)
        {
            IsSpeaking = false;
        }
    }

    public void TickSilence() => PushLevel(0f);

    public void Reset()
    {
        Level = 0f;
        IsSpeaking = false;
        _lastActiveTime = -999f;
    }
}
