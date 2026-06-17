using System;
#if !WINDOWS
using Concentus.Enums;
using Concentus.Structs;
using VoiceChatPlugin.Audio;
#endif

namespace VoiceChatPlugin.VoiceChat;

internal interface IVoiceEncoder : IDisposable
{
    int Bitrate { set; }
    int PacketLossPercent { set; }
    int Encode(short[] pcm, int offset, int samples, byte[] packet, int packetOffset, int maxData);
    int Encode(ReadOnlySpan<short> pcm, int frameSize, Span<byte> data, int maxData);
}

internal interface IVoiceDecoder : IDisposable
{
    // data empty => packet-loss concealment (native libopus uses neural deep PLC); fec => reconstruct the
    // prior lost frame from this packet's in-band FEC.
    int Decode(ReadOnlySpan<byte> data, Span<short> pcm, int frameSize, bool fec);
    int Decode(byte[] packet, float[] pcm, int frameSize, bool fec);
    // Reconstruct a lost frame from a later "recovering" packet's Deep REDundancy. dredOffsetSamples = samples
    // back from that packet to the lost frame. Native libopus uses DRED; platforms without it conceal (PLC).
    int DecodeDred(byte[] recoveringPacket, int dredOffsetSamples, Span<short> pcm, int frameSize);
}

// Windows uses native libopus 1.6.1 (neural deep PLC); Android uses the managed Concentus port. This is
// platform-conditional compilation, not a runtime fallback: each build has exactly one codec.
internal static class VoiceCodec
{
    public static IVoiceEncoder CreateEncoder(int bitrate, int complexity, bool voiceSignal, bool vbr, bool constrainedVbr, bool dtx, bool fec, int packetLossPercent)
#if WINDOWS
        => new NativeOpusEncoder(bitrate, complexity, voiceSignal, vbr, constrainedVbr, dtx, fec, packetLossPercent);
#else
        => new ConcentusVoiceEncoder(bitrate, complexity, voiceSignal, vbr, constrainedVbr, dtx, fec, packetLossPercent);
#endif

    public static IVoiceDecoder CreateDecoder()
#if WINDOWS
        => new NativeOpusDecoder();
#else
        => new ConcentusVoiceDecoder();
#endif
}

#if !WINDOWS
#pragma warning disable CS0618 // Concentus marks its direct ctors/Encode obsolete in favor of its own factory; the wrappers below call them deliberately.
internal sealed class ConcentusVoiceEncoder : IVoiceEncoder
{
    private readonly OpusEncoder _enc;

    public ConcentusVoiceEncoder(int bitrate, int complexity, bool voiceSignal, bool vbr, bool constrainedVbr, bool dtx, bool fec, int packetLossPercent)
    {
        _enc = new OpusEncoder(AudioHelpers.ClockRate, AudioHelpers.Channels, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = bitrate,
            Complexity = complexity,
            SignalType = voiceSignal ? OpusSignal.OPUS_SIGNAL_VOICE : OpusSignal.OPUS_SIGNAL_AUTO,
            UseVBR = vbr,
            UseConstrainedVBR = constrainedVbr,
            UseDTX = dtx,
            UseInbandFEC = fec,
            PacketLossPercent = packetLossPercent,
        };
    }

    public int Bitrate { set => _enc.Bitrate = value; }
    public int PacketLossPercent { set => _enc.PacketLossPercent = value; }

    public int Encode(short[] pcm, int offset, int samples, byte[] packet, int packetOffset, int maxData)
        => _enc.Encode(pcm, offset, samples, packet, packetOffset, maxData);

    public int Encode(ReadOnlySpan<short> pcm, int frameSize, Span<byte> data, int maxData)
        => _enc.Encode(pcm, frameSize, data, maxData);

    public void Dispose() { }
}

internal sealed class ConcentusVoiceDecoder : IVoiceDecoder
{
    private readonly OpusDecoder _dec = new(AudioHelpers.ClockRate, AudioHelpers.Channels);

    public int Decode(ReadOnlySpan<byte> data, Span<short> pcm, int frameSize, bool fec)
        => _dec.Decode(data, pcm, frameSize, fec);

    public int Decode(byte[] packet, float[] pcm, int frameSize, bool fec)
        => _dec.Decode(packet, pcm, frameSize, fec);

    // Concentus has no DRED; conceal the lost frame with its built-in PLC.
    public int DecodeDred(byte[] recoveringPacket, int dredOffsetSamples, Span<short> pcm, int frameSize)
        => _dec.Decode(ReadOnlySpan<byte>.Empty, pcm, frameSize, false);

    public void Dispose() { }
}
#pragma warning restore CS0618
#endif
