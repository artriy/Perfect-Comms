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
}
