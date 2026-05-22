using System;

namespace VoiceChatPlugin.Audio;

internal readonly record struct MicFrameDecision(
    bool ShouldTransmit,
    float Peak,
    float Rms,
    float Threshold,
    string Reason);

internal sealed class MicPreprocessor
{
    private const int HangoverFrames = 8;
    private const float MinimumTransmitGate = 0.0005f;
    private int _hangoverFramesRemaining;

    public void Reset()
    {
        _hangoverFramesRemaining = 0;
    }

    public float ProcessCaptureSample(float sample, float gain) => sample;

    public MicFrameDecision PrepareFrameForEncode(
        float[] pcm,
        int sampleCount,
        float manualGateThreshold,
        float vadThreshold)
    {
        int count = Math.Min(sampleCount, pcm.Length);
        if (count <= 0)
            return new MicFrameDecision(false, 0f, 0f, 0f, "empty");

        // VAD and gate state are diagnostics/speaking inputs; OpenMic transport stays continuous.
        _ = vadThreshold;

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

        if (peak >= threshold)
        {
            _hangoverFramesRemaining = HangoverFrames;
            return new MicFrameDecision(true, peak, rms, threshold, "voice");
        }

        if (_hangoverFramesRemaining > 0)
        {
            _hangoverFramesRemaining--;
            return new MicFrameDecision(true, peak, rms, threshold, "hangover");
        }

        return new MicFrameDecision(false, peak, rms, threshold, "below-threshold");
    }
}
