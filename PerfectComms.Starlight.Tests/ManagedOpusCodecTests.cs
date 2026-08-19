using Concentus;
using PerfectComms.Starlight.Media;
using Xunit;

namespace PerfectComms.Starlight.Tests;

public sealed class ManagedOpusCodecTests
{
    [Fact]
    public void ManagedCodecRoundTripsContinuousSpeech()
    {
        const int frameCount = 12;
        using var encoder = new ManagedOpusEncoder();
        using var decoder = new ManagedOpusDecoder();
        var source = new float[ManagedOpusEncoder.FrameSamples * frameCount];
        var decoded = new float[source.Length];
        var packet = new byte[ManagedOpusEncoder.MaxPacketBytes];

        encoder.Configure(1f, 0f, 0f);
        for (int frame = 0; frame < frameCount; frame++)
        {
            Span<float> input = source.AsSpan(
                frame * ManagedOpusEncoder.FrameSamples,
                ManagedOpusEncoder.FrameSamples);
            FillSignal(input, frame * ManagedOpusEncoder.FrameSamples, 733f, 0.22f);
            int packetLength = encoder.Encode(input, packet, out float peak, out bool speaking);
            int sampleCount = decoder.Decode(
                packet.AsSpan(0, packetLength),
                decoded.AsSpan(frame * ManagedOpusEncoder.FrameSamples));

            Assert.InRange(packetLength, 1, ManagedOpusEncoder.MaxPacketBytes);
            Assert.Equal(ManagedOpusEncoder.FrameSamples, sampleCount);
            Assert.InRange(peak, 0.21f, 0.23f);
            Assert.True(speaking);
        }

        double bestCorrelation = 0d;
        int firstComparedSample = ManagedOpusEncoder.FrameSamples * 2;
        for (int delay = 0; delay <= 480; delay++)
        {
            double cross = 0d;
            double sourceSquare = 0d;
            double decodedSquare = 0d;
            for (int i = firstComparedSample; i + delay < decoded.Length; i++)
            {
                double expected = source[i];
                double actual = decoded[i + delay];
                cross += expected * actual;
                sourceSquare += expected * expected;
                decodedSquare += actual * actual;
            }

            double correlation = cross / Math.Sqrt(sourceSquare * decodedSquare);
            bestCorrelation = Math.Max(bestCorrelation, Math.Abs(correlation));
        }

        Assert.True(bestCorrelation > 0.92d, $"Best normalized correlation was {bestCorrelation:F4}.");
        Assert.All(decoded, sample => Assert.True(float.IsFinite(sample)));
        Assert.False(OpusCodecFactory.AttemptToUseNativeLibrary);
    }

    [Fact]
    public void ResetRemovesEncoderAndDecoderHistory()
    {
        var history = new float[ManagedOpusEncoder.FrameSamples];
        var target = new float[ManagedOpusEncoder.FrameSamples];
        var dirtyPacket = new byte[ManagedOpusEncoder.MaxPacketBytes];
        var resetPacket = new byte[ManagedOpusEncoder.MaxPacketBytes];
        var freshPacket = new byte[ManagedOpusEncoder.MaxPacketBytes];
        FillSignal(history, 0, 181f, 0.7f);
        FillSignal(target, 0, 997f, 0.18f);

        using var resetEncoder = new ManagedOpusEncoder();
        resetEncoder.Configure(1f, 0f, 0f);
        resetEncoder.Encode(history, dirtyPacket, out _, out _);
        resetEncoder.Reset();
        int resetLength = resetEncoder.Encode(target, resetPacket, out _, out _);

        using var freshEncoder = new ManagedOpusEncoder();
        freshEncoder.Configure(1f, 0f, 0f);
        freshEncoder.Reset();
        int freshLength = freshEncoder.Encode(target, freshPacket, out _, out _);

        Assert.Equal(freshLength, resetLength);
        Assert.True(resetPacket.AsSpan(0, resetLength).SequenceEqual(freshPacket.AsSpan(0, freshLength)));

        var historyPacket = new byte[ManagedOpusEncoder.MaxPacketBytes];
        freshEncoder.Reset();
        int historyLength = freshEncoder.Encode(history, historyPacket, out _, out _);
        var afterReset = new float[ManagedOpusDecoder.FrameSamples];
        var freshDecode = new float[ManagedOpusDecoder.FrameSamples];
        using var resetDecoder = new ManagedOpusDecoder();
        resetDecoder.Decode(historyPacket.AsSpan(0, historyLength), afterReset);
        resetDecoder.Reset();
        resetDecoder.Decode(resetPacket.AsSpan(0, resetLength), afterReset);
        using var freshDecoder = new ManagedOpusDecoder();
        freshDecoder.Decode(resetPacket.AsSpan(0, resetLength), freshDecode);

        Assert.True(afterReset.AsSpan().SequenceEqual(freshDecode));
    }

    [Fact]
    public void WarmedEncodeAndDecodeStayWithinManagedAllocationBudget()
    {
        const int warmupFrames = 64;
        const int measuredFrames = 128;
        const long maximumEncodeBytesPerFrame = 135_000;
        const long maximumDecodeBytesPerFrame = 40_000;
        using var encoder = new ManagedOpusEncoder();
        using var decoder = new ManagedOpusDecoder();
        var input = new float[ManagedOpusEncoder.FrameSamples];
        var packet = new byte[ManagedOpusEncoder.MaxPacketBytes];
        var output = new float[ManagedOpusDecoder.FrameSamples];
        float[] inputBuffer = input;
        byte[] packetBuffer = packet;
        float[] outputBuffer = output;
        FillSignal(input, 0, 523f, 0.2f);
        encoder.Configure(1f, 0f, 0f);

        int packetLength = 0;
        int decodedSamples = 0;
        for (int i = 0; i < warmupFrames; i++)
        {
            packetLength = encoder.Encode(input, packet, out _, out _);
            decodedSamples = decoder.Decode(packet.AsSpan(0, packetLength), output);
        }

        long beforeEncode = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measuredFrames; i++)
            packetLength = encoder.Encode(input, packet, out _, out _);
        long encodeBytes = GC.GetAllocatedBytesForCurrentThread() - beforeEncode;

        long beforeDecode = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < measuredFrames; i++)
            decodedSamples = decoder.Decode(packet.AsSpan(0, packetLength), output);
        long decodeBytes = GC.GetAllocatedBytesForCurrentThread() - beforeDecode;
        double encodeBytesPerFrame = encodeBytes / (double)measuredFrames;
        double decodeBytesPerFrame = decodeBytes / (double)measuredFrames;

        Assert.Same(inputBuffer, input);
        Assert.Same(packetBuffer, packet);
        Assert.Same(outputBuffer, output);
        Assert.Equal(ManagedOpusEncoder.FrameSamples, decodedSamples);
        Assert.InRange(packetLength, 1, ManagedOpusEncoder.MaxPacketBytes);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
        Assert.True(
            encodeBytesPerFrame <= maximumEncodeBytesPerFrame,
            $"Encode allocated {encodeBytesPerFrame:F0} bytes per frame.");
        Assert.True(
            decodeBytesPerFrame <= maximumDecodeBytesPerFrame,
            $"Decode allocated {decodeBytesPerFrame:F0} bytes per frame.");
    }

    [Fact]
    public void CaptureLimiterIsFiniteBoundedAndMonotonic()
    {
        float[] amplitudes = [0f, 0.1f, 0.25f, 0.5f, 0.9f, 2f, 10f];
        var input = new float[ManagedOpusEncoder.FrameSamples];
        var packet = new byte[ManagedOpusEncoder.MaxPacketBytes];
        float previousPeak = -1f;

        foreach (float amplitude in amplitudes)
        {
            Array.Fill(input, amplitude);
            using var encoder = new ManagedOpusEncoder();
            encoder.Configure(2f, 0f, 0f);
            encoder.Encode(input, packet, out float peak, out bool speaking);

            Assert.True(float.IsFinite(peak));
            Assert.InRange(peak, 0f, 1f);
            Assert.True(peak > previousPeak);
            Assert.Equal(amplitude > 0f, speaking);
            previousPeak = peak;
        }
    }

    [Fact]
    public void NoiseGateSubstitutesSilenceWithoutBreakingCodecContinuity()
    {
        var gated = new float[ManagedOpusEncoder.FrameSamples];
        var silence = new float[ManagedOpusEncoder.FrameSamples];
        var speech = new float[ManagedOpusEncoder.FrameSamples];
        var gatedPacket = new byte[ManagedOpusEncoder.MaxPacketBytes];
        var silentPacket = new byte[ManagedOpusEncoder.MaxPacketBytes];
        var afterGate = new byte[ManagedOpusEncoder.MaxPacketBytes];
        var afterSilence = new byte[ManagedOpusEncoder.MaxPacketBytes];
        Array.Fill(gated, 0.01f);
        FillSignal(speech, 0, 641f, 0.2f);

        using var gatedEncoder = new ManagedOpusEncoder();
        using var silentEncoder = new ManagedOpusEncoder();
        gatedEncoder.Configure(1f, 0.02f, 0.05f);
        silentEncoder.Configure(1f, 0.02f, 0.05f);
        gatedEncoder.Reset();
        silentEncoder.Reset();

        int gatedLength = gatedEncoder.Encode(gated, gatedPacket, out float gatedPeak, out bool gatedSpeaking);
        int silentLength = silentEncoder.Encode(silence, silentPacket, out _, out _);
        int afterGateLength = gatedEncoder.Encode(speech, afterGate, out _, out bool speechSpeaking);
        int afterSilenceLength = silentEncoder.Encode(speech, afterSilence, out _, out _);

        Assert.InRange(gatedPeak, 0.0099f, 0.0101f);
        Assert.False(gatedSpeaking);
        Assert.True(speechSpeaking);
        Assert.Equal(silentLength, gatedLength);
        Assert.True(gatedPacket.AsSpan(0, gatedLength).SequenceEqual(silentPacket.AsSpan(0, silentLength)));
        Assert.Equal(afterSilenceLength, afterGateLength);
        Assert.True(afterGate.AsSpan(0, afterGateLength).SequenceEqual(afterSilence.AsSpan(0, afterSilenceLength)));
    }

    private static void FillSignal(Span<float> samples, int sampleOffset, float frequency, float amplitude)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double phase = Math.Tau * frequency * (sampleOffset + i) / ManagedOpusEncoder.SampleRate;
            samples[i] = amplitude * (float)(Math.Sin(phase) + 0.2d * Math.Sin(phase * 2.37d)) / 1.2f;
        }
    }
}
