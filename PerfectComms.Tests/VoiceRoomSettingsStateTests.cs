using System;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceRoomSettingsStateTests : IDisposable
{
    public VoiceRoomSettingsStateTests()
        => VoiceRoomSettingsState.EndSession();

    public void Dispose()
        => VoiceRoomSettingsState.EndSession();

    [Fact]
    public void EarlyProvisionalSnapshotSurvivesFirstMatchingConfirmedJoin()
    {
        var early = VoiceRoomSettingsSnapshot.Defaults with { MaxChatDistance = 7.25f };

        VoiceRoomSettingsState.ApplyRemote(early, gameId: 55);

        Assert.Equal(55, VoiceRoomSettingsState.SessionGameId);
        Assert.False(VoiceRoomSettingsState.SessionConfirmed);
        Assert.Equal(early.Clamp(), VoiceRoomSettingsState.RemoteSnapshot);

        VoiceRoomSettingsState.BeginSession(55);

        Assert.True(VoiceRoomSettingsState.SessionConfirmed);
        Assert.Equal(early.Clamp(), VoiceRoomSettingsState.RemoteSnapshot);
    }

    [Fact]
    public void EarlyUnscopedSnapshotIsAdoptedByFirstConfirmedJoin()
    {
        var early = VoiceRoomSettingsSnapshot.Defaults with { MaxChatDistance = 8.5f };
        VoiceRoomSettingsState.ApplyRemote(early, gameId: 0);

        VoiceRoomSettingsState.BeginSession(66);

        Assert.Equal(66, VoiceRoomSettingsState.SessionGameId);
        Assert.True(VoiceRoomSettingsState.SessionConfirmed);
        Assert.Equal(early.Clamp(), VoiceRoomSettingsState.RemoteSnapshot);
    }

    [Fact]
    public void SubsequentConfirmedJoinWithSameGameIdClearsStaleSnapshot()
    {
        var stale = VoiceRoomSettingsSnapshot.Defaults with { MaxChatDistance = 9.75f };
        VoiceRoomSettingsState.BeginSession(77);
        VoiceRoomSettingsState.ApplyRemote(stale, gameId: 77);
        Assert.Equal(stale.Clamp(), VoiceRoomSettingsState.RemoteSnapshot);

        VoiceRoomSettingsState.BeginSession(77);

        Assert.Equal(77, VoiceRoomSettingsState.SessionGameId);
        Assert.True(VoiceRoomSettingsState.SessionConfirmed);
        Assert.Null(VoiceRoomSettingsState.RemoteSnapshot);
    }

    [Fact]
    public void ConfirmingDifferentGameRejectsMismatchedProvisionalSnapshot()
    {
        var wrongRoom = VoiceRoomSettingsSnapshot.Defaults with { MaxChatDistance = 11f };
        VoiceRoomSettingsState.ApplyRemote(wrongRoom, gameId: 88);

        VoiceRoomSettingsState.BeginSession(89);

        Assert.Equal(89, VoiceRoomSettingsState.SessionGameId);
        Assert.True(VoiceRoomSettingsState.SessionConfirmed);
        Assert.Null(VoiceRoomSettingsState.RemoteSnapshot);
    }
}
