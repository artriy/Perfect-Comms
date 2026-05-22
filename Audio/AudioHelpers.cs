using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using System;

namespace VoiceChatPlugin.Audio;

internal static class AudioHelpers
{
    public const int ClockRate  = 48000;
    public const int FrameSize  = 960;   // 20 ms @ 48 kHz
    public const int Channels   = 1;     // mono capture
    public const int PlaybackPrebufferSamples = FrameSize * 5; // 100 ms jitter cushion for HTTP/modded RPC jitter
    public const int ImmediatePlaybackPrebufferSamples = 0; // no startup hold; avoid starvation/prebuffer loops
    public const int PlaybackRecoveryPrebufferSamples = FrameSize * 3; // 60 ms after stream already started
    public const int PlaybackMaxPrebufferWaitMilliseconds = 180; // do not strand short utterances forever
    public const int OpusBitrate = 48_000;
    public const int OpusComplexity = 10;
    public const bool OpusUseConstrainedVbr = true;
    public const bool OpusUseInbandFec = false;
    public const int OpusPacketLossPercent = 0;
    public const float TransmitPeakCeiling = 0.30f;
    public const float TransmitLimiterReleasePerFrame = 0.025f;

    public static float GetTransmitLimiterGain(float peak)
    {
        if (peak <= 0f || peak <= TransmitPeakCeiling) return 1f;
        return TransmitPeakCeiling / peak;
    }

    public static float GetSmoothedTransmitLimiterGain(float currentGain, float peak)
    {
        var targetGain = GetTransmitLimiterGain(peak);
        currentGain = Math.Clamp(currentGain, 0f, 1f);
        if (targetGain < currentGain) return targetGain;
        return Math.Min(targetGain, currentGain + TransmitLimiterReleasePerFrame);
    }

    public static float MeasurePeak(float[] samples, int count)
    {
        count = Math.Min(count, samples.Length);
        var peak = 0f;
        for (var i = 0; i < count; i++)
        {
            var abs = Math.Abs(samples[i]);
            if (abs > peak) peak = abs;
        }
        return peak;
    }

    public static void ApplyGain(float[] samples, int count, float gain)
    {
        if (gain >= 1f) return;
        count = Math.Min(count, samples.Length);
        for (var i = 0; i < count; i++)
            samples[i] *= gain;
    }

    private static readonly object WarmupLock = new();
    private static readonly float[] SilenceFrame = new float[FrameSize];
    private static byte[]? _decoderWarmupPacket;

#pragma warning disable CS0618
    public static IOpusEncoder GetOpusEncoder()
    {
        var enc = CreateConfiguredEncoder();
        WarmEncoder(enc);
        return enc;
    }

    private static OpusEncoder CreateConfiguredEncoder()
    {
        var enc = new OpusEncoder(ClockRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);

        // Interstellar media transport can afford a higher voice bitrate than
        // the old RPC path while staying far below raw PCM bandwidth.
        enc.Bitrate = OpusBitrate;

        // Keep realtime voice analysis high without adding tonal filters.
        enc.Complexity = OpusComplexity;
        enc.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;

        // Keep VBR so quiet passages compress well, but constrain bursts for the
        // data-channel fallback and small relay queues.
        enc.UseVBR = true;
        enc.UseConstrainedVBR = OpusUseConstrainedVbr;

        // DTX (discontinuous transmission) OFF — it caused brief cut-ins at the
        // start of each utterance which felt like the mic was "muting itself".
        enc.UseDTX = false;

        // FEC/PLC concealment caused occasional static bursts on packet gaps in
        // the game RPC transport. Prefer a tiny dropout over synthetic noise.
        enc.UseInbandFEC = OpusUseInbandFec;
        enc.PacketLossPercent = OpusPacketLossPercent;

        return enc;
    }

    public static IOpusDecoder GetOpusDecoder()
    {
        var decoder = new OpusDecoder(ClockRate, Channels);
        WarmDecoder(decoder);
        return decoder;
    }

    private static void WarmEncoder(IOpusEncoder encoder)
    {
        try
        {
            Span<byte> scratch = stackalloc byte[64];
            encoder.Encode(SilenceFrame, FrameSize, scratch, scratch.Length);
        }
        catch
        {
            // Warmup is best-effort; actual encode path still handles errors.
        }
    }

    private static void WarmDecoder(IOpusDecoder decoder)
    {
        try
        {
            var packet = GetDecoderWarmupPacket();
            if (packet.Length == 0) return;

            var scratch = new float[FrameSize];
            decoder.Decode(packet, scratch, FrameSize, false);
        }
        catch
        {
            // Warmup is best-effort; actual decode path still handles errors.
        }
    }

    private static byte[] GetDecoderWarmupPacket()
    {
        if (_decoderWarmupPacket != null) return _decoderWarmupPacket;

        lock (WarmupLock)
        {
            if (_decoderWarmupPacket != null) return _decoderWarmupPacket;

            var encoder = CreateConfiguredEncoder();
            var buffer = new byte[64];
            int bytes = encoder.Encode(SilenceFrame, FrameSize, buffer, buffer.Length);
            if (bytes <= 0)
                return _decoderWarmupPacket = Array.Empty<byte>();

            var packet = new byte[bytes];
            Array.Copy(buffer, packet, bytes);
            _decoderWarmupPacket = packet;
            return packet;
        }
    }
#pragma warning restore CS0618
}
