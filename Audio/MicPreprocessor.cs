using System;

namespace VoiceChatPlugin.Audio;

internal readonly record struct MicFrameDecision(
    bool ShouldTransmit,
    float Peak,
    float Rms,
    float Threshold,
    string Reason);

internal sealed class MicPreprocessor : IDisposable
{
    private const float MinimumTransmitGate = 0.0005f;

    public void Reset(bool preserveAutoGain = false)
    {
    }

    public float ProcessCaptureSample(float sample, float gain) => sample;

    public float LimitFramePeakForEncode(float[] pcm, int sampleCount)
    {
        int count = Math.Min(sampleCount, pcm.Length);
        if (count <= 0)
            return 1f;

        float peak = 0f;
        for (int i = 0; i < count; i++)
        {
            float sample = pcm[i];
            if (!float.IsFinite(sample))
                continue;

            float abs = sample < 0f ? -sample : sample;
            if (abs > peak) peak = abs;
        }

        var gain = AudioHelpers.GetCaptureEncodeLimiterGain(peak);
        if (gain >= 1f)
            return 1f;

        for (int i = 0; i < count; i++)
        {
            if (!float.IsFinite(pcm[i]))
                pcm[i] = 0f;
            else
                pcm[i] *= gain;
        }

        return gain;
    }

    public MicFrameDecision PrepareFrameForEncode(
        float[] pcm,
        int sampleCount,
        float manualGateThreshold,
        float vadThreshold,
        float preSuppressionPeak)
    {
        int count = Math.Min(sampleCount, pcm.Length);
        if (count <= 0)
            return new MicFrameDecision(false, 0f, 0f, 0f, "empty");

        _ = vadThreshold;
        _ = preSuppressionPeak;
        float peak = 0f;
        double sumSquares = 0.0;
        for (int i = 0; i < count; i++)
        {
            float sample = pcm[i];
            float abs = sample < 0f ? -sample : sample;
            if (abs > peak) peak = abs;
            sumSquares += sample * sample;
        }

        float rms = (float)Math.Sqrt(sumSquares / count);
        float threshold = Math.Max(MinimumTransmitGate, manualGateThreshold);
        return new MicFrameDecision(true, peak, rms, threshold, peak >= threshold ? "voice" : "silence");
    }

    public void Dispose()
    {
    }
}
