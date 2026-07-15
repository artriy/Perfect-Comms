using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Pure managed chime generation for the first-run output test. Desktop calls this from a
/// ThreadPool worker, so this file must never depend on engine or IL2CPP-backed APIs.
/// </summary>
internal static class FirstRunToneGenerator
{
    internal const int FrameMilliseconds = 20;
    internal const int FrameCount = 30;
    internal const int SampleRate = 48_000;

    internal static float[] CreateFrame(int frameIndex, float volume)
    {
        if ((uint)frameIndex >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        var samples = new float[SidecarProtocol.AudioOutSamples];
        float master = float.IsFinite(volume) ? Math.Clamp(volume, 0f, 2f) : 0f;
        int totalFrames = FrameCount * SidecarProtocol.AudioOutFrames;
        int startFrame = frameIndex * SidecarProtocol.AudioOutFrames;
        for (int i = 0; i < SidecarProtocol.AudioOutFrames; i++)
        {
            int n = startFrame + i;
            double t = n / (double)SampleRate;
            double progress = n / (double)Math.Max(1, totalFrames - 1);
            double envelope = Math.Sin(Math.PI * progress);
            envelope *= envelope;
            double chime = Math.Sin(2d * Math.PI * 523.25d * t) * 0.72d +
                           Math.Sin(2d * Math.PI * 659.25d * t) * 0.28d;
            float sample = Math.Clamp(
                (float)(chime * envelope * 0.16d * master), -0.7f, 0.7f);
            samples[i * 2] = sample;
            samples[i * 2 + 1] = sample;
        }
        return samples;
    }
}
