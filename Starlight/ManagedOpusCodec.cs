using System;
using Concentus;
using Concentus.Enums;

namespace PerfectComms.Starlight.Media;

public sealed class ManagedOpusEncoder : IDisposable
{
    public const int SampleRate = 48_000;
    public const int FrameSamples = 960;
    public const int MaxPacketBytes = 1_275;
    private const float MaxInputGain = 2f;
    private const float CaptureLimiterKnee = 0.90f;
    private const float CaptureLimiterHeadroom = 1f - CaptureLimiterKnee;

    private readonly IOpusEncoder _encoder;
    private readonly float[] _pcm = new float[FrameSamples];
    private float _inputGain = 1f;
    private float _vadThreshold = 0.015f;
    private float _noiseGateThreshold = 0.005f;
    private bool _disposed;

    public ManagedOpusEncoder()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _encoder = OpusCodecFactory.CreateEncoder(
            SampleRate,
            1,
            OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 48_000;
        _encoder.UseVBR = true;
        _encoder.UseConstrainedVBR = true;
        _encoder.Complexity = 10;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 15;
        _encoder.UseDTX = false;
    }

    public void Configure(float gain, float vadThreshold, float noiseGateThreshold)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(gain))
        {
            throw new ArgumentOutOfRangeException(nameof(gain));
        }

        if (!float.IsFinite(vadThreshold) || vadThreshold < 0f || vadThreshold > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(vadThreshold));
        }

        if (!float.IsFinite(noiseGateThreshold) ||
            noiseGateThreshold < 0f ||
            noiseGateThreshold > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(noiseGateThreshold));
        }

        _inputGain = Math.Clamp(gain, 0f, MaxInputGain);
        _vadThreshold = vadThreshold;
        _noiseGateThreshold = noiseGateThreshold;
    }

    public int Encode(
        ReadOnlySpan<float> samples,
        Span<byte> output,
        out float peak,
        out bool speaking)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samples.Length != FrameSamples)
        {
            throw new ArgumentException($"Expected {FrameSamples} mono samples.", nameof(samples));
        }

        if (output.Length < MaxPacketBytes)
        {
            throw new ArgumentException($"Output must hold at least {MaxPacketBytes} bytes.", nameof(output));
        }

        peak = 0f;
        for (int i = 0; i < FrameSamples; i++)
        {
            float sample = samples[i];
            if (!float.IsFinite(sample))
            {
                sample = 0f;
            }

            float amplified = sample * _inputGain;
            float limited = SoftLimitCaptureSample(amplified);

            _pcm[i] = limited;
            float magnitude = MathF.Abs(limited);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        speaking = peak > 0f && peak >= _vadThreshold;
        if (peak < _noiseGateThreshold)
        {
            _pcm.AsSpan().Clear();
        }

        return _encoder.Encode(_pcm, FrameSamples, output, MaxPacketBytes);
    }

    private static float SoftLimitCaptureSample(float sample)
    {
        if (!float.IsFinite(sample))
        {
            return float.IsNaN(sample) ? 0f : MathF.CopySign(1f, sample);
        }

        double magnitude = Math.Abs((double)sample);
        if (magnitude <= CaptureLimiterKnee)
        {
            return sample;
        }

        double excess = magnitude - CaptureLimiterKnee;
        double limited = CaptureLimiterKnee +
            excess / (1d + excess / CaptureLimiterHeadroom);
        return MathF.CopySign((float)Math.Min(limited, 1d), sample);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pcm.AsSpan().Clear();
        _encoder.ResetState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pcm.AsSpan().Clear();
        _encoder.Dispose();
    }
}

public sealed class ManagedOpusDecoder : IDisposable
{
    public const int SampleRate = ManagedOpusEncoder.SampleRate;
    public const int FrameSamples = ManagedOpusEncoder.FrameSamples;

    private readonly IOpusDecoder _decoder;
    private bool _disposed;

    public ManagedOpusDecoder()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, 1);
    }

    public int Decode(ReadOnlySpan<byte> packet, Span<float> output)
    {
        if (packet.IsEmpty)
        {
            throw new ArgumentException("An Opus packet is required.", nameof(packet));
        }

        return Decode(packet, output, false);
    }

    public int DecodeFec(ReadOnlySpan<byte> nextPacket, Span<float> output)
    {
        if (nextPacket.IsEmpty)
        {
            throw new ArgumentException("The following Opus packet is required for FEC.", nameof(nextPacket));
        }

        return Decode(nextPacket, output, true);
    }

    public int DecodePlc(Span<float> output)
    {
        return Decode(ReadOnlySpan<byte>.Empty, output, false);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _decoder.ResetState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _decoder.Dispose();
    }

    private int Decode(ReadOnlySpan<byte> packet, Span<float> output, bool decodeFec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (output.Length < FrameSamples)
        {
            throw new ArgumentException($"Output must hold at least {FrameSamples} mono samples.", nameof(output));
        }

        Span<float> frame = output[..FrameSamples];
        int decodedSamples = _decoder.Decode(packet, frame, FrameSamples, decodeFec);
        if ((uint)decodedSamples > FrameSamples)
        {
            throw new InvalidOperationException($"Opus returned an invalid decoded sample count: {decodedSamples}.");
        }

        for (int i = 0; i < decodedSamples; i++)
        {
            if (!float.IsFinite(frame[i]))
            {
                frame[i] = 0f;
            }
        }

        frame[decodedSamples..].Clear();
        return decodedSamples;
    }
}
