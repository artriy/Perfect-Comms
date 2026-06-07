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
    public const int PlaybackRecoveryPrebufferSamples = FrameSize * 3; // 60 ms after stream already started (baseline, UNCHANGED)
    public const int PlaybackMaxRecoveryPrebufferSamples = FrameSize * 8; // 160 ms per-peer STARTING ceiling; < 300 ms ring
    // Per-peer link-aware HARD cap (Fix 2a / P0.2). Only a peer whose UNCLAMPED jitter target stays pinned at its
    // current ceiling for a sustained streak ratchets its OWN ceiling one frame-step at a time from the 160 ms
    // start toward this 240 ms hard max; healthy peers never move off 160 ms (or lower) and pay no extra latency.
    // The 240 ms cap + 2-frame trim headroom (280 ms) fits under the 300 ms (FrameSize*15) ring.
    public const int PlaybackMaxRecoveryPrebufferSamplesHard = FrameSize * 12; // 240 ms hard per-peer escalation cap
    // Sustained-clamp streak (CLAMPED RecomputeSetpointLocked calls — i.e. clamped underruns accrued within a
    // talkspurt, since a recompute runs on every underrun — whose UNCLAMPED jitter target is at/above the current
    // per-peer ceiling) required before the per-peer ceiling ratchets up one frame-step. A single unclamped
    // recompute decays the streak by one (floored at 0); only an idle-reset / Clear fully clears it. So a
    // transient jitter spike does not deepen a healthy peer, while a genuinely jittery link sustains the clamp
    // long enough to earn the extra latency.
    public const int PerPeerCeilingClampStreakToGrow = 2;
    public const int JitterDepthMarginSamples = FrameSize * 2; // 40 ms safety margin above measured jitter
    public const float JitterGain = 2.5f;                       // target ~= baseline + gain*jitterStdev + margin
    public const int RecoveryGrowFloorSamples = FrameSize * 2;  // min +40 ms jump per underrun for a not-yet-measured peer
    public const int PlaybackMaxPrebufferWaitMilliseconds = 180; // do not strand short utterances forever
    public const int OpusBitrate = 48_000;
    public const int OpusComplexity = 10;
    public const bool OpusUseConstrainedVbr = true;
    public const bool OpusUseInbandFec = true;     // arm the LossResistant flag + the Drain() Fec arm
    public const int OpusPacketLossPercent = 15;   // non-zero PLP so Opus actually EMBEDS FEC redundancy
    public const float TransmitPeakCeiling = 0.30f;
    public const float TransmitLimiterReleasePerFrame = 0.025f;
    public const float CaptureEncodePeakCeiling = 0.95f;
    public const float PlaybackMixPeakCeiling = 0.92f; // ~-0.7 dBFS true headroom for many simultaneous speakers (was 0.95)
    public const float PlaybackMixLimiterReleasePerFrame = 0.05f;

    // ── Per-peer link-aware ceiling escalation/decay (P0.2) ────────────────────────────────────────────────
    // Pure, unit-testable decision functions (mirroring IsMeshCollapse / AnswererShouldRerequest). The owning
    // BufferedSampleProvider holds the mutable per-peer ceiling + clamp streak and the live cut-sizes; these two
    // helpers only compute "what should the next ceiling be" so the policy can be exercised without the ring.

    // True when the UNCLAMPED jitter target (baseline + gain*jitter + margin, BEFORE clamping to the ceiling)
    // is at/above the current per-peer ceiling — i.e. the link genuinely wants more depth than the ceiling allows.
    public static bool PeerCeilingIsClamped(int unclampedTargetSamples, int currentCeilingSamples)
        => unclampedTargetSamples >= currentCeilingSamples;

    // The next per-peer ceiling. Grows exactly ONE frame-step (toward the 240 ms hard cap) only once the
    // sustained-clamp streak has reached PerPeerCeilingClampStreakToGrow; otherwise unchanged. Never exceeds the
    // hard cap and never drops below the 160 ms start. Caller resets the streak after a grow.
    public static int NextPeerCeilingOnGrow(int currentCeilingSamples, int clampStreak)
    {
        if (clampStreak < PerPeerCeilingClampStreakToGrow)
            return Math.Clamp(currentCeilingSamples, PlaybackMaxRecoveryPrebufferSamples, PlaybackMaxRecoveryPrebufferSamplesHard);
        int grown = currentCeilingSamples + FrameSize;
        return Math.Clamp(grown, PlaybackMaxRecoveryPrebufferSamples, PlaybackMaxRecoveryPrebufferSamplesHard);
    }

    // The next per-peer ceiling on decay: lower ONE frame-step toward the 160 ms start (never below it), so a
    // one-time bad spell that ratcheted a peer up to 240 ms does not strand it there forever once jitter falls.
    public static int NextPeerCeilingOnDecay(int currentCeilingSamples)
    {
        int lowered = currentCeilingSamples - FrameSize;
        return Math.Clamp(lowered, PlaybackMaxRecoveryPrebufferSamples, PlaybackMaxRecoveryPrebufferSamplesHard);
    }

    public static int NextClampStreak(int currentStreak, bool clamped)
        => clamped ? currentStreak + 1 : Math.Max(0, currentStreak - 1);

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

    public static float GetCaptureEncodeLimiterGain(float peak)
    {
        if (peak <= 0f || peak <= CaptureEncodePeakCeiling) return 1f;
        return CaptureEncodePeakCeiling / peak;
    }

    public static float GetPlaybackMixLimiterGain(float peak)
    {
        if (peak <= 0f || peak <= PlaybackMixPeakCeiling) return 1f;
        return PlaybackMixPeakCeiling / peak;
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
