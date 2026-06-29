using System;

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
    bool SupportsDred { get; }
}

#if WINDOWS
// Windows uses native libopus 1.6.1 (neural deep PLC). Android no longer has a managed C# codec: the
// pc-mobile Rust core does Opus in-process, so there is nothing for VoiceCodec to build off-platform.
internal static class VoiceCodec
{
    public static IVoiceEncoder CreateEncoder(int bitrate, int complexity, bool voiceSignal, bool vbr, bool constrainedVbr, bool dtx, bool fec, int packetLossPercent)
        => new NativeOpusEncoder(bitrate, complexity, voiceSignal, vbr, constrainedVbr, dtx, fec, packetLossPercent);

    public static IVoiceDecoder CreateDecoder()
        => new NativeOpusDecoder();
}
#endif
