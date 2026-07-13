using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceRoomLifetimeGateTests
{
    [Fact]
    public void ExplicitDisconnectStaysLatchedUntilConfirmedJoin()
    {
        VoiceRoomLifetimeGate.ConfirmJoinedSession("test-reset");

        VoiceRoomLifetimeGate.MarkExplicitDisconnect("end-game-exit");
        Assert.True(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);

        // Lingering EndGame/LobbyBehaviour and a stale InnerNet Joined value do not clear it.
        Assert.True(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);

        VoiceRoomLifetimeGate.ConfirmJoinedSession("new-session");
        Assert.False(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);
    }
}
