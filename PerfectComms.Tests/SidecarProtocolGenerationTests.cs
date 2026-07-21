using System;
using System.Text;
using System.Text.Json;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarProtocolGenerationTests
{
    [Fact]
    public void PeerAddCarriesRequiredGeneration()
    {
        var frame = SidecarProtocol.AddPeerFrame("peer-7", isOfferer: true, generation: 41);

        Xunit.Assert.True(SidecarProtocol.TryParseFrame(
            frame,
            frame.Length,
            out var type,
            out var payloadOffset,
            out var payloadLength,
            out _));
        Xunit.Assert.Equal(SidecarProtocol.TypeControl, type);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame, payloadOffset, payloadLength));
        Xunit.Assert.Equal("peer-add", doc.RootElement.GetProperty("op").GetString());
        Xunit.Assert.Equal("peer-7", doc.RootElement.GetProperty("peer_id").GetString());
        Xunit.Assert.True(doc.RootElement.GetProperty("offerer").GetBoolean());
        Xunit.Assert.False(doc.RootElement.GetProperty("relay_only").GetBoolean());
        Xunit.Assert.Equal(41, doc.RootElement.GetProperty("generation").GetInt32());
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
            SidecarProtocol.AddPeerFrame("peer-7", true, generation: 0));
    }

    [Fact]
    public void IceRestartCarriesAutomaticMixedPolicyAndOfferRole()
    {
        var frame = SidecarProtocol.RestartIceFrame("peer-7", generation: 42, createOffer: true);

        Xunit.Assert.True(SidecarProtocol.TryParseFrame(
            frame,
            frame.Length,
            out var type,
            out var payloadOffset,
            out var payloadLength,
            out _));
        Xunit.Assert.Equal(SidecarProtocol.TypeControl, type);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame, payloadOffset, payloadLength));
        Xunit.Assert.Equal("restart-ice", doc.RootElement.GetProperty("op").GetString());
        Xunit.Assert.Equal("peer-7", doc.RootElement.GetProperty("peer_id").GetString());
        Xunit.Assert.Equal(42, doc.RootElement.GetProperty("generation").GetInt32());
        Xunit.Assert.False(doc.RootElement.GetProperty("relay_only").GetBoolean());
        Xunit.Assert.True(doc.RootElement.GetProperty("create_offer").GetBoolean());
    }

    [Fact]
    public void EveryPeerMutationCarriesRequiredGeneration()
    {
        var frames = new[]
        {
            SidecarProtocol.RemovePeerFrame("peer-7", generation: 43),
            SidecarProtocol.SetRemoteSdpFrame("peer-7", generation: 43, "answer", "v=0"),
            SidecarProtocol.AddIceCandidateFrame("peer-7", generation: 43, "candidate"),
        };

        foreach (var frame in frames)
        {
            Xunit.Assert.True(SidecarProtocol.TryParseFrame(
                frame,
                frame.Length,
                out _,
                out var payloadOffset,
                out var payloadLength,
                out _));
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame, payloadOffset, payloadLength));
            Xunit.Assert.Equal("peer-7", doc.RootElement.GetProperty("peer_id").GetString());
            Xunit.Assert.Equal(43, doc.RootElement.GetProperty("generation").GetInt32());
        }

        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
            SidecarProtocol.RemovePeerFrame("peer-7", generation: 0));
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
            SidecarProtocol.SetRemoteSdpFrame("peer-7", generation: 0, "answer", "v=0"));
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
            SidecarProtocol.AddIceCandidateFrame("peer-7", generation: 0, "candidate"));
        Xunit.Assert.Throws<ArgumentOutOfRangeException>(() =>
            SidecarProtocol.RestartIceFrame("peer-7", generation: 0, createOffer: true));
    }

    [Fact]
    public void LocalRtcEventsRequirePositiveGeneration()
    {
        Xunit.Assert.True(SidecarProtocol.TryReadLocalSdp(
            "{\"op\":\"local-sdp\",\"peer_id\":\"p\",\"generation\":41,\"sdp_type\":\"offer\",\"sdp\":\"v=0\"}",
            out var sdpPeer,
            out var sdpGeneration,
            out var sdpType,
            out var sdp));
        Xunit.Assert.Equal("p", sdpPeer);
        Xunit.Assert.Equal(41, sdpGeneration);
        Xunit.Assert.Equal("offer", sdpType);
        Xunit.Assert.Equal("v=0", sdp);

        Xunit.Assert.True(SidecarProtocol.TryReadLocalCandidate(
            "{\"op\":\"local-candidate\",\"peer_id\":\"p\",\"generation\":42,\"candidate\":\"c\"}",
            out _,
            out var candidateGeneration,
            out _));
        Xunit.Assert.Equal(42, candidateGeneration);

        Xunit.Assert.True(SidecarProtocol.TryReadPeerState(
            "{\"op\":\"peer-state\",\"peer_id\":\"p\",\"generation\":43,\"state\":\"connected\"}",
            out _,
            out var stateGeneration,
            out _));
        Xunit.Assert.Equal(43, stateGeneration);

        Xunit.Assert.False(SidecarProtocol.TryReadLocalSdp(
            "{\"op\":\"local-sdp\",\"peer_id\":\"p\",\"sdp_type\":\"offer\",\"sdp\":\"v=0\"}",
            out _, out _, out _, out _));
        Xunit.Assert.False(SidecarProtocol.TryReadLocalCandidate(
            "{\"op\":\"local-candidate\",\"peer_id\":\"p\",\"generation\":0,\"candidate\":\"c\"}",
            out _, out _, out _));
        Xunit.Assert.False(SidecarProtocol.TryReadPeerState(
            "{\"op\":\"peer-state\",\"peer_id\":\"p\",\"generation\":-1,\"state\":\"failed\"}",
            out _, out _, out _));
    }
}
