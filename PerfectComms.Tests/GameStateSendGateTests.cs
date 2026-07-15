using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class GameStateSendGateTests
{
    [Fact]
    public void SendsChangesAtTwentyHertzAndUnchangedHeartbeatAtOneHertz()
    {
        var gate = new GameStateSendGate(50, 1_000);
        Assert.True(gate.ShouldSend(10, 1));
        Assert.False(gate.ShouldSend(40, 2));
        Assert.True(gate.ShouldSend(60, 2));
        Assert.False(gate.ShouldSend(999, 2));
        Assert.True(gate.ShouldSend(1_060, 2));
    }

    [Fact]
    public void ResetForcesImmediateFailClosedState()
    {
        var gate = new GameStateSendGate();
        Assert.True(gate.ShouldSend(100, 7));
        gate.Reset();
        Assert.True(gate.ShouldSend(101, 7));
    }

    [Fact]
    public void NativeEngineReplacementCannotInheritThePreviousHeartbeatWindow()
    {
        var gate = new GameStateSendGate(50, 1_000);
        Assert.True(gate.ShouldSend(10_000, 42));
        Assert.False(gate.ShouldSend(10_010, 42));

        // A replacement native mixer starts with no routes, even when its desired fingerprint is
        // identical. Reset makes that first authoritative state immediate instead of waiting 1s.
        gate.Reset();
        Assert.True(gate.ShouldSend(10_011, 42));
    }
}
