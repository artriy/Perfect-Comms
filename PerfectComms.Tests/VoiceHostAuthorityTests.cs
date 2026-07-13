using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceHostAuthorityTests
{
    [Fact]
    public void KnownHostRequiresResolvedMatchingSenderClient()
    {
        Assert.False(VoiceHostAuthority.IsTrustedResolvedHostSender(
            new VoiceHostSenderIdentity(-1, 2, "host-net-object", "rpc"),
            hostClientId: 3,
            hostPlayerId: 2,
            out var unresolvedReason));
        Assert.Equal("sender-client-unresolved", unresolvedReason);

        Assert.True(VoiceHostAuthority.IsTrustedResolvedHostSender(
            new VoiceHostSenderIdentity(3, 2, "host-net-object", "rpc"),
            hostClientId: 3,
            hostPlayerId: 2,
            out var trustedReason));
        Assert.Equal("host-client", trustedReason);

        Assert.False(VoiceHostAuthority.IsTrustedResolvedHostSender(
            new VoiceHostSenderIdentity(9, 2, "forged-host-player", "rpc"),
            hostClientId: 3,
            hostPlayerId: 2,
            out var nonHostReason));
        Assert.Equal("non-host", nonHostReason);
    }

    [Fact]
    public void UnknownHostClientNeverFallsBackToPlayerId()
    {
        Assert.False(VoiceHostAuthority.IsTrustedResolvedHostSender(
            new VoiceHostSenderIdentity(-1, 2, "unresolved-net-object", "rpc"),
            hostClientId: -1,
            hostPlayerId: 2,
            out var reason));
        Assert.Equal("host-client-unresolved", reason);
    }

    [Fact]
    public void DecodedModOptionsApplyOnlyAfterTrustDecision()
    {
        var values = new[]
        {
            new VoiceRoomSettingsRpc.SyncedModOptionValue(123, IsEnum: false, Value: 1),
            new VoiceRoomSettingsRpc.SyncedModOptionValue(456, IsEnum: true, Value: 2),
        };
        var applied = new List<(int Hash, bool IsEnum, int Value)>();

        Assert.False(VoiceRoomSettingsRpc.ApplyDecodedModOptions(
            trusted: false, values, (hash, isEnum, value) => applied.Add((hash, isEnum, value))));
        Assert.Empty(applied);

        Assert.True(VoiceRoomSettingsRpc.ApplyDecodedModOptions(
            trusted: true, values, (hash, isEnum, value) => applied.Add((hash, isEnum, value))));
        Assert.Equal(new[] { (123, false, 1), (456, true, 2) }, applied);
    }
}
