using System;
using System.Linq;
using System.Text;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class AmongUsRpcSignalingFramingTests
{
    private static byte[] SdpLikePayload(int bytes)
    {
        var sb = new StringBuilder();
        var line = 0;
        while (sb.Length < bytes)
            sb.Append("a=candidate:").Append(line++).Append(" 1 udp 2122260223 192.168.0.").Append(line % 255).Append(" 54321 typ host\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString().Substring(0, bytes));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    [InlineData((byte)6)]
    [InlineData((byte)7)]
    [InlineData((byte)8)]
    public void RoundTripsTwoKilobytePayload(byte typeByte)
    {
        var type = (SignalMsgType)typeByte;
        var payload = SdpLikePayload(2048);

        var frame = AmongUsRpcSignaling.Frame(type, payload);
        var ok = AmongUsRpcSignaling.TryParse(frame, out var parsedType, out var parsedPayload);

        Xunit.Assert.True(ok);
        Xunit.Assert.Equal(type, parsedType);
        Xunit.Assert.Equal(payload, parsedPayload);
    }

    [Fact]
    public void SdpOffersAnswersAndIceRestartsAreGzipCompressedOnTheWire()
    {
        var payload = SdpLikePayload(2048);
        var offerFrame = AmongUsRpcSignaling.Frame(SignalMsgType.Offer, payload);
        var restartFrame = AmongUsRpcSignaling.Frame(SignalMsgType.IceRestartOffer, payload);
        var helloFrame = AmongUsRpcSignaling.Frame(SignalMsgType.Hello, payload);

        Xunit.Assert.True(offerFrame.Length < helloFrame.Length);
        Xunit.Assert.True(restartFrame.Length < helloFrame.Length);
        Xunit.Assert.Equal(3 + 2048, helloFrame.Length);
    }

    [Fact]
    public void FrameHeaderIsTypeThenBigEndianLength()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = AmongUsRpcSignaling.Frame(SignalMsgType.Candidate, payload);

        Xunit.Assert.Equal((byte)SignalMsgType.Candidate, frame[0]);
        Xunit.Assert.Equal(0, frame[1]);
        Xunit.Assert.Equal(5, frame[2]);
        Xunit.Assert.Equal(8, frame.Length);
    }

    [Fact]
    public void EmptyPayloadRoundTrips()
    {
        var frame = AmongUsRpcSignaling.Frame(SignalMsgType.Bye, Array.Empty<byte>());
        var ok = AmongUsRpcSignaling.TryParse(frame, out var type, out var payload);

        Xunit.Assert.True(ok);
        Xunit.Assert.Equal(SignalMsgType.Bye, type);
        Xunit.Assert.Empty(payload);
    }

    [Fact]
    public void RejectsTruncatedFrame()
    {
        var frame = AmongUsRpcSignaling.Frame(SignalMsgType.Candidate, new byte[] { 9, 9, 9 });
        var truncated = frame.Take(frame.Length - 1).ToArray();

        Xunit.Assert.False(AmongUsRpcSignaling.TryParse(truncated, out _, out _));
    }

    [Fact]
    public void RejectsUnknownType()
    {
        var bad = new byte[] { 99, 0, 0 };
        Xunit.Assert.False(AmongUsRpcSignaling.TryParse(bad, out _, out _));
    }

    [Fact]
    public void RejectsCompressedPayloadThatExpandsPastTheSafetyLimit()
    {
        var payload = new byte[AmongUsRpcSignaling.MaxDecompressedPayloadBytes + 1];
        var frame = AmongUsRpcSignaling.Frame(SignalMsgType.Offer, payload);

        Xunit.Assert.False(AmongUsRpcSignaling.TryParse(frame, out _, out _));
    }

    [Fact]
    public void SignalingRpcIdAvoidsKnownIds()
    {
        Xunit.Assert.NotEqual(198, AmongUsRpcSignaling.SignalingRpcId);
        Xunit.Assert.NotEqual(203, AmongUsRpcSignaling.SignalingRpcId);
        Xunit.Assert.Equal(209, AmongUsRpcSignaling.SignalingRpcId);
    }
}
