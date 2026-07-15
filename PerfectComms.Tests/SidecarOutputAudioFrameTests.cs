using System;
using System.Buffers.Binary;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarOutputAudioFrameTests
{
    [Fact]
    public void EncodesBoundedLittleEndianStereoFrame()
    {
        var samples = new float[SidecarProtocol.AudioOutSamples];
        samples[0] = 0.25f;
        samples[1] = -0.5f;
        samples[2] = float.NaN;
        samples[3] = 4f;

        var frame = SidecarProtocol.OutputAudioFrame(samples);

        Assert.Equal(SidecarProtocol.TypeAudioOut, frame[0]);
        Assert.Equal(
            SidecarProtocol.AudioOutPayloadBytes,
            BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(1, 4)));
        Assert.Equal(0.25f, Read(frame, 0));
        Assert.Equal(-0.5f, Read(frame, 1));
        Assert.Equal(0f, Read(frame, 2));
        Assert.Equal(1f, Read(frame, 3));
    }

    [Fact]
    public void RejectsWrongFrameSize()
    {
        Assert.Throws<ArgumentException>(() =>
            SidecarProtocol.OutputAudioFrame(
                new float[SidecarProtocol.AudioOutSamples - 1]));
    }

    private static float Read(byte[] frame, int sampleIndex)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            frame.AsSpan(SidecarProtocol.HeaderBytes + sampleIndex * sizeof(float), sizeof(float))));
}
