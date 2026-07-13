using System;
using System.Text;
using System.Text.Json;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarGameStateSafetyTests
{
    [Fact]
    public void NoSnapshotSafetyFrameIsDeafenedAndHasNoPeerRoutes()
    {
        var frame = SidecarProtocol.GameStateFrame(
            deaf: true,
            master: 0f,
            peers: Array.Empty<SidecarProtocol.GameStatePeerInput>());

        Xunit.Assert.True(SidecarProtocol.TryParseFrame(
            frame,
            frame.Length,
            out var type,
            out var payloadOffset,
            out var payloadLength,
            out var frameLength));
        Xunit.Assert.Equal(SidecarProtocol.TypeControl, type);
        Xunit.Assert.Equal(frame.Length, frameLength);

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(frame, payloadOffset, payloadLength));
        var root = json.RootElement;
        Xunit.Assert.Equal("game-state", root.GetProperty("op").GetString());
        Xunit.Assert.True(root.GetProperty("deaf").GetBoolean());
        Xunit.Assert.Equal(0f, root.GetProperty("master").GetSingle());
        Xunit.Assert.Equal(0, root.GetProperty("peers").GetArrayLength());
    }

    [Fact]
    public void GameStateSanitizesInvalidValuesAndPreservesComposedBoosts()
    {
        var frame = SidecarProtocol.GameStateFrame(
            deaf: false,
            master: float.NaN,
            peers: new[]
            {
                new SidecarProtocol.GameStatePeerInput("invalid", float.NaN, float.PositiveInfinity, 0),
                new SidecarProtocol.GameStatePeerInput("boosted", 4f, 2f, 0),
            });

        Assert.True(SidecarProtocol.TryParseFrame(
            frame, frame.Length, out _, out var offset, out var length, out _));
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(frame, offset, length));
        var root = json.RootElement;
        Assert.Equal(1f, root.GetProperty("master").GetSingle());

        var peers = root.GetProperty("peers");
        Assert.Equal(0f, peers[0].GetProperty("gain").GetSingle());
        Assert.Equal(0f, peers[0].GetProperty("pan").GetSingle());
        Assert.Equal(4f, peers[1].GetProperty("gain").GetSingle());
        Assert.Equal(1f, peers[1].GetProperty("pan").GetSingle());
    }
}
