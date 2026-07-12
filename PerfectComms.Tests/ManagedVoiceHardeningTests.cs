using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class ManagedVoiceHardeningTests
{
    private static JsonElement DecodeControl(byte[] frame)
    {
        Assert.True(SidecarProtocol.TryParseFrame(
            frame, frame.Length, out var type, out var offset, out var length, out var frameLength));
        Assert.Equal(SidecarProtocol.TypeControl, type);
        Assert.Equal(frame.Length, frameLength);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame, offset, length));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void NativeInputSettingsUseBoundedFiniteSnakeCaseContract()
    {
        var root = DecodeControl(SidecarProtocol.SetInputFrame(float.NaN, float.PositiveInfinity));
        Assert.Equal("set-input", root.GetProperty("op").GetString());
        Assert.Equal(1f, root.GetProperty("gain").GetSingle());
        Assert.Equal(0.004f, root.GetProperty("vad_threshold").GetSingle());

        root = DecodeControl(SidecarProtocol.SetInputFrame(99f, -4f));
        Assert.Equal(2f, root.GetProperty("gain").GetSingle());
        Assert.Equal(0.0001f, root.GetProperty("vad_threshold").GetSingle());
    }

    [Fact]
    public void RuntimeSyntheticControlUsesNativeContract()
    {
        var root = DecodeControl(SidecarProtocol.SetSyntheticFrame(enabled: true));
        Assert.Equal("set-synthetic", root.GetProperty("op").GetString());
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, SidecarVoiceClient.Proto);
        Assert.Equal(3, SidecarProtocol.MobileAbi);
    }

    [Fact]
    public void EmptyDeviceIdExplicitlySelectsOperatingSystemDefault()
    {
        var input = DecodeControl(SidecarProtocol.SelectDeviceFrame(string.Empty));
        Assert.Equal("select-device", input.GetProperty("op").GetString());
        Assert.Equal(string.Empty, input.GetProperty("id").GetString());

        var output = DecodeControl(SidecarProtocol.SelectOutputDeviceFrame(string.Empty));
        Assert.Equal("select-output-device", output.GetProperty("op").GetString());
        Assert.Equal(string.Empty, output.GetProperty("id").GetString());
    }

    [Fact]
    public void PeerLevelParserIsBoundedAndClampsFinitePeaks()
    {
        const string json = "{\"op\":\"peer-levels\",\"levels\":[" +
            "{\"peer_id\":\"7\",\"peak\":1.5}," +
            "{\"peer_id\":\"8\",\"peak\":-1}," +
            "{\"peer_id\":\"bad\",\"peak\":1e999}," +
            "{\"peer_id\":\"9\",\"peak\":0.25}]}";

        Assert.True(SidecarProtocol.TryReadPeerLevels(json, out var levels));
        Assert.Equal(3, levels.Count);
        Assert.Equal(("7", 1f), (levels[0].PeerId, levels[0].Peak));
        Assert.Equal(("8", 0f), (levels[1].PeerId, levels[1].Peak));
        Assert.Equal(("9", 0.25f), (levels[2].PeerId, levels[2].Peak));

        var tooMany = "{\"op\":\"peer-levels\",\"levels\":[" +
            string.Join(',', Enumerable.Range(0, SidecarProtocol.MaxPeerLevelsPerBatch + 1)
                .Select(i => $"{{\"peer_id\":\"{i}\",\"peak\":0.1}}")) + "]}";
        Assert.False(SidecarProtocol.TryReadPeerLevels(tooMany, out _));
    }

    [Fact]
    public void DesktopRpcWaitsForConfiguredHealthyHelper()
    {
        Assert.False(PerfectCommsVoiceBackend.CanPumpDesktopRpc(false, CaptureHealth.Healthy));
        Assert.False(PerfectCommsVoiceBackend.CanPumpDesktopRpc(true, CaptureHealth.Dead));
        Assert.True(PerfectCommsVoiceBackend.CanPumpDesktopRpc(true, CaptureHealth.Healthy));
    }

    [Fact]
    public void SidecarLivenessUsesLevelEventsInsteadOfRetiredPcmFrames()
    {
        Assert.Equal(4, PerfectCommsVoiceBackend.SelectCaptureActivity(
            nativeSidecarOwnsCapture: true, sidecarLevelEvents: 4, managedPcmSamples: 0));
        Assert.Equal(960, PerfectCommsVoiceBackend.SelectCaptureActivity(
            nativeSidecarOwnsCapture: false, sidecarLevelEvents: 0, managedPcmSamples: 960));
        Assert.Equal(0, PerfectCommsVoiceBackend.SelectCaptureActivity(
            nativeSidecarOwnsCapture: true, sidecarLevelEvents: -1, managedPcmSamples: 960));
    }

    [Fact]
    public void DuplicateRouteWinnerIsStableAndPrefersMappedSocket()
    {
        Assert.True(PerfectCommsVoiceBackend.PreferSecondRouteRecord(
            "rpc-route:7", "socket-b", mappedSocket: "socket-b"));
        Assert.False(PerfectCommsVoiceBackend.PreferSecondRouteRecord(
            "socket-b", "rpc-route:7", mappedSocket: "socket-b"));
        Assert.True(PerfectCommsVoiceBackend.PreferSecondRouteRecord(
            "socket-z", "socket-a", mappedSocket: null));
    }

    [Fact]
    public void RemoteSpeakingPeakHasTwoHundredFiftyMillisecondHold()
    {
        var start = DateTime.UtcNow.Ticks;
        Assert.True(PerfectCommsVoiceBackend.ShouldHoldPreviousRemoteLevel(
            previous: 0.5f, previousTicks: start, next: 0f,
            nowTicks: start + TimeSpan.FromMilliseconds(249).Ticks));
        Assert.False(PerfectCommsVoiceBackend.ShouldHoldPreviousRemoteLevel(
            previous: 0.5f, previousTicks: start, next: 0f,
            nowTicks: start + TimeSpan.FromMilliseconds(250).Ticks));
        Assert.False(PerfectCommsVoiceBackend.ShouldHoldPreviousRemoteLevel(
            previous: 0.5f, previousTicks: start, next: 0.8f,
            nowTicks: start + TimeSpan.FromMilliseconds(10).Ticks));
    }
}
