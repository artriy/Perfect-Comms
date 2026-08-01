using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Xunit;

public sealed class VoiceRoomLifetimeGateTests
{
    [Fact]
    public void ExplicitDisconnectStaysLatchedUntilConfirmedJoin()
    {
        VoiceRoomLifetimeGate.ConfirmJoinedSession("test-reset", gameId: 101);

        VoiceRoomLifetimeGate.MarkExplicitDisconnect("end-game-exit");
        Assert.True(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);

        // Lingering EndGame/LobbyBehaviour and a stale InnerNet Joined value do not clear it.
        Assert.True(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);

        VoiceRoomLifetimeGate.ConfirmJoinedSession("new-session", gameId: 102);
        Assert.False(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);
    }

    [Fact]
    public void ConfirmedJoinIsBoundToOneGameAndLifecycleGeneration()
    {
        VoiceRoomLifetimeGate.ConfirmJoinedSession("generation-a", gameId: 701);
        int firstGeneration = VoiceRoomLifetimeGate.CurrentSessionGeneration;
        Assert.True(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(701));
        Assert.False(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(702));

        VoiceRoomLifetimeGate.MarkExplicitDisconnect("generation-end");
        Assert.True(VoiceRoomLifetimeGate.CurrentSessionGeneration > firstGeneration);
        Assert.False(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(701));

        VoiceRoomLifetimeGate.ConfirmJoinedSession("generation-b", gameId: 702);
        Assert.True(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(702));
        Assert.False(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(701));
    }

    [Theory]
    [InlineData(8675309)]
    [InlineData(-8675309)]
    public void SameNonzeroGameIdContinuesConfirmedSessionWithoutResettingRemotePolicy(int gameId)
    {
        ResetLifetimeState("same-id-setup");
        try
        {
            VoiceRoomLifetimeGate.ConfirmJoinedSession("same-id-first-confirmation", gameId);
            var remotePolicy = DistinctiveRemotePolicy();
            VoiceRoomSettingsState.ApplyRemote(remotePolicy, gameId);
            int generation = VoiceRoomLifetimeGate.CurrentSessionGeneration;

            VoiceRoomLifetimeGate.ConfirmJoinedSession("same-id-continuation", gameId);

            Assert.Equal(generation, VoiceRoomLifetimeGate.CurrentSessionGeneration);
            Assert.False(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);
            Assert.True(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(gameId));
            Assert.True(VoiceRoomSettingsState.SessionConfirmed);
            Assert.Equal(gameId, VoiceRoomSettingsState.SessionGameId);
            Assert.Equal(remotePolicy.Clamp(), VoiceRoomSettingsState.RemoteSnapshot);
        }
        finally
        {
            ResetLifetimeState("same-id-cleanup");
        }
    }

    [Fact]
    public void ExplicitDisconnectThenSameGameIdStartsNewEpochAndClearsRemotePolicy()
    {
        const int gameId = 24680;
        ResetLifetimeState("disconnect-boundary-setup");
        try
        {
            VoiceRoomLifetimeGate.ConfirmJoinedSession("disconnect-boundary-first", gameId);
            var remotePolicy = DistinctiveRemotePolicy();
            VoiceRoomSettingsState.ApplyRemote(remotePolicy, gameId);
            Assert.Equal(remotePolicy.Clamp(), VoiceRoomSettingsState.RemoteSnapshot);
            int firstGeneration = VoiceRoomLifetimeGate.CurrentSessionGeneration;

            VoiceRoomLifetimeGate.MarkExplicitDisconnect("disconnect-boundary");
            int disconnectedGeneration = VoiceRoomLifetimeGate.CurrentSessionGeneration;
            VoiceRoomLifetimeGate.ConfirmJoinedSession("disconnect-boundary-rejoin", gameId);

            Assert.True(disconnectedGeneration > firstGeneration);
            Assert.True(VoiceRoomLifetimeGate.CurrentSessionGeneration > disconnectedGeneration);
            Assert.False(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);
            Assert.True(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(gameId));
            Assert.True(VoiceRoomSettingsState.SessionConfirmed);
            Assert.Equal(gameId, VoiceRoomSettingsState.SessionGameId);
            Assert.Null(VoiceRoomSettingsState.RemoteSnapshot);
        }
        finally
        {
            ResetLifetimeState("disconnect-boundary-cleanup");
        }
    }

    [Fact]
    public void DifferentGameIdWithoutDisconnectStartsNewEpochAndClearsRemotePolicy()
    {
        const int firstGameId = 13579;
        const int nextGameId = -97531;
        ResetLifetimeState("different-id-setup");
        try
        {
            VoiceRoomLifetimeGate.ConfirmJoinedSession("different-id-first", firstGameId);
            var remotePolicy = DistinctiveRemotePolicy();
            VoiceRoomSettingsState.ApplyRemote(remotePolicy, firstGameId);
            Assert.Equal(remotePolicy.Clamp(), VoiceRoomSettingsState.RemoteSnapshot);
            int firstGeneration = VoiceRoomLifetimeGate.CurrentSessionGeneration;

            VoiceRoomLifetimeGate.ConfirmJoinedSession("different-id-next", nextGameId);

            Assert.True(VoiceRoomLifetimeGate.CurrentSessionGeneration > firstGeneration);
            Assert.False(VoiceRoomLifetimeGate.IsExplicitDisconnectLatched);
            Assert.False(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(firstGameId));
            Assert.True(VoiceRoomLifetimeGate.IsConfirmedJoinedGame(nextGameId));
            Assert.True(VoiceRoomSettingsState.SessionConfirmed);
            Assert.Equal(nextGameId, VoiceRoomSettingsState.SessionGameId);
            Assert.Null(VoiceRoomSettingsState.RemoteSnapshot);
        }
        finally
        {
            ResetLifetimeState("different-id-cleanup");
        }
    }

    [Theory]
    [InlineData(true, false, false, 123, true, true, true)]
    [InlineData(false, false, false, 123, true, true, false)]
    [InlineData(true, true, false, 123, true, true, false)]
    [InlineData(true, false, true, 123, true, true, false)]
    [InlineData(true, false, false, 0, true, true, false)]
    [InlineData(true, false, false, 123, false, true, false)]
    [InlineData(true, false, false, 123, true, false, false)]
    public void VoiceSessionEligibilityRequiresEveryAuthoritativeBoundary(
        bool hasClient,
        bool localOrFreeplay,
        bool disconnectLatched,
        int gameId,
        bool patchPairHealthy,
        bool joinedGenerationConfirmed,
        bool expected)
    {
        Assert.Equal(expected, VoiceRoomLifetimeGate.IsVoiceSessionEligible(
            hasClient,
            localOrFreeplay,
            disconnectLatched,
            gameId,
            patchPairHealthy,
            joinedGenerationConfirmed));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void OnlyConfirmedTerminalConditionsCloseVoice(
        bool hasAmongUsClient,
        bool isLocalServer,
        bool explicitDisconnect,
        bool expectedTerminal)
    {
        Assert.Equal(
            expectedTerminal,
            VoiceRoomLifetimeGate.IsTerminalCondition(
                hasAmongUsClient,
                isLocalServer,
                explicitDisconnect));
    }

    [Theory]
    [InlineData(true, 55, 55, false, true)]
    [InlineData(false, 55, 55, false, false)]
    [InlineData(true, 0, 0, false, false)]
    [InlineData(true, 55, 56, false, false)]
    [InlineData(true, 55, 55, true, false)]
    public void SnapshotIsRetainedOnlyInsideSameLiveSession(
        bool hasSnapshot,
        int snapshotGameId,
        int currentGameId,
        bool explicitDisconnect,
        bool expectedRetained)
    {
        Assert.Equal(
            expectedRetained,
            VoiceRoomLifetimeGate.CanRetainSnapshot(
                hasSnapshot,
                snapshotGameId,
                currentGameId,
                explicitDisconnect));
    }

    [Theory]
    [InlineData(55, 44, (int)VoiceGamePhase.EndGame, false, false, 55)]
    [InlineData(0, 55, (int)VoiceGamePhase.EndGame, false, true, 55)]
    [InlineData(0, 55, (int)VoiceGamePhase.Tasks, false, true, 0)]
    [InlineData(0, 55, (int)VoiceGamePhase.EndGame, true, true, 0)]
    [InlineData(0, 55, (int)VoiceGamePhase.EndGame, false, false, 0)]
    [InlineData(0, 0, (int)VoiceGamePhase.EndGame, false, true, 0)]
    public void OnlyConfirmedEndGameSessionBridgesTransientMissingGameId(
        int currentGameId,
        int retainedSnapshotGameId,
        int retainedPhase,
        bool explicitDisconnect,
        bool retainedSessionConfirmed,
        int expectedGameId)
    {
        Assert.Equal(
            expectedGameId,
            VoiceRoomLifetimeGate.ResolveSnapshotSessionGameId(
                currentGameId,
                retainedSnapshotGameId,
                (VoiceGamePhase)retainedPhase,
                explicitDisconnect,
                retainedSessionConfirmed));
    }

    [Fact]
    public void TransitionRefreshPolicyRetainsOnlyAUsableSameSessionPreviousSnapshot()
    {
        Assert.Equal(VoiceSnapshotRefreshDecision.UseRefreshed,
            VoiceRoomLifetimeGate.DecideSnapshotRefresh(true, true, true, true));
        Assert.Equal(VoiceSnapshotRefreshDecision.RetainPrevious,
            VoiceRoomLifetimeGate.DecideSnapshotRefresh(true, false, true, true));
        Assert.Equal(VoiceSnapshotRefreshDecision.Clear,
            VoiceRoomLifetimeGate.DecideSnapshotRefresh(true, false, false, true));
        Assert.Equal(VoiceSnapshotRefreshDecision.Clear,
            VoiceRoomLifetimeGate.DecideSnapshotRefresh(true, false, true, false));
        Assert.Equal(VoiceSnapshotRefreshDecision.Clear,
            VoiceRoomLifetimeGate.DecideSnapshotRefresh(false, true, true, true));
    }

    [Fact]
    public void SnapshotUsabilityRequiresMappedLiveLocalPlayer()
    {
        Assert.True(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(phase: VoiceGamePhase.Tasks)));
        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(
            phase: VoiceGamePhase.Tasks,
            liveLocalPlayerResolved: false)));
        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(phase: VoiceGamePhase.Tasks, localClientId: -1)));
        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(phase: VoiceGamePhase.Tasks, localPlayerId: byte.MaxValue)));
        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(phase: VoiceGamePhase.Tasks, hasLocalPosition: false)));
        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(phase: VoiceGamePhase.Tasks, includeLocalPlayer: false)));
        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(phase: VoiceGamePhase.Tasks, localRosterClientId: 8)));
        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(phase: VoiceGamePhase.Tasks, localDisconnected: true)));
    }

    [Fact]
    public void BoundedRetainedLocalIdentityIsRoutingSafeButNotLiveUsable()
    {
        var retained = CreateSnapshot(
            phase: VoiceGamePhase.Lobby,
            liveLocalPlayerResolved: false,
            routingRosterRetained: true);

        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(retained));
        Assert.True(VoiceRoomLifetimeGate.IsSafeForRouting(retained));
        Assert.False(VoiceRoomLifetimeGate.IsSafeForRouting(retained with
        {
            RoutingRosterRetained = false,
        }));
    }

    [Fact]
    public void PlayerlessEndGameIsUsableWithAuthenticatedSessionIdentity()
    {
        Assert.True(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(
            phase: VoiceGamePhase.EndGame,
            localPlayerId: byte.MaxValue,
            hasLocalPosition: false,
            includeLocalPlayer: false)));
        Assert.False(VoiceRoomLifetimeGate.IsUsableSnapshot(CreateSnapshot(
            phase: VoiceGamePhase.EndGame,
            localClientId: -1,
            localPlayerId: byte.MaxValue,
            hasLocalPosition: false,
            includeLocalPlayer: false)));
    }

    [Fact]
    public void EndGamePromotesPhaseWithPriorRosterAndLobbyWaitsForCompleteReplacement()
    {
        Assert.True(VoiceRoomLifetimeGate.ShouldRetainRoutingRosterForPhasePromotion(
            VoiceGamePhase.Tasks, VoiceGamePhase.EndGame, previousPlayerCount: 4, refreshedPlayerCount: 0));
        Assert.False(VoiceRoomLifetimeGate.ShouldRetainRoutingRosterForPhasePromotion(
            VoiceGamePhase.Tasks, VoiceGamePhase.Tasks, previousPlayerCount: 4, refreshedPlayerCount: 2));

        Assert.True(VoiceRoomLifetimeGate.RequiresCompleteRemoteRoster(
            VoiceGamePhase.EndGame, VoiceGamePhase.Lobby));
        Assert.True(VoiceRoomLifetimeGate.RequiresCompleteRemoteRoster(
            VoiceGamePhase.EndGame, VoiceGamePhase.Intro));
        Assert.False(VoiceRoomLifetimeGate.RequiresCompleteRemoteRoster(
            null, VoiceGamePhase.Lobby));
        Assert.False(VoiceRoomLifetimeGate.RequiresCompleteRemoteRoster(
            VoiceGamePhase.Tasks, VoiceGamePhase.Tasks));
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Tasks, (int)VoiceGamePhase.Tasks)]
    [InlineData((int)VoiceGamePhase.Meeting, (int)VoiceGamePhase.Meeting)]
    [InlineData((int)VoiceGamePhase.Exile, (int)VoiceGamePhase.Exile)]
    [InlineData((int)VoiceGamePhase.EndGame, (int)VoiceGamePhase.EndGame)]
    [InlineData((int)VoiceGamePhase.Lobby, (int)VoiceGamePhase.Lobby)]
    public void UnknownPhaseUsesLastConfirmedPhase(
        int lastConfirmedValue,
        int expectedValue)
    {
        var lastConfirmed = (VoiceGamePhase)lastConfirmedValue;
        var expected = (VoiceGamePhase)expectedValue;
        Assert.Equal(
            expected,
            VoiceSceneState.StabilizeUnknownPhase(VoiceGamePhase.Unknown, lastConfirmed));
        Assert.Equal(
            VoiceGamePhase.Tasks,
            VoiceSceneState.StabilizeUnknownPhase(VoiceGamePhase.Tasks, lastConfirmed));
    }

    [Fact]
    public void UnknownWithoutConfirmedPhaseIsNotTreatedAsLobby()
    {
        Assert.Equal(
            VoiceGamePhase.Unknown,
            VoiceSceneState.StabilizeUnknownPhase(
                VoiceGamePhase.Unknown,
                VoiceGamePhase.Unknown));
        Assert.False(VoiceSceneState.IsLobbyVoicePhase(VoiceGamePhase.Unknown));
    }

    [Theory]
    [InlineData(false, (int)VoiceGamePhase.Unknown, (int)VoiceGamePhase.Intro, true)]
    [InlineData(true, (int)VoiceGamePhase.Tasks, (int)VoiceGamePhase.EndGame, true)]
    [InlineData(true, (int)VoiceGamePhase.Lobby, (int)VoiceGamePhase.Intro, true)]
    [InlineData(true, (int)VoiceGamePhase.Intro, (int)VoiceGamePhase.Intro, false)]
    [InlineData(true, (int)VoiceGamePhase.Tasks, (int)VoiceGamePhase.Meeting, false)]
    public void SightCacheResetsOnlyAtSeamlessRoundBoundaries(
        bool havePrevious,
        int previousValue,
        int currentValue,
        bool expected)
    {
        var previous = (VoiceGamePhase)previousValue;
        var current = (VoiceGamePhase)currentValue;
        Assert.Equal(
            expected,
            VoiceChatRoom.ShouldResetSightStateForPhaseBoundary(
                havePrevious,
                previous,
                current));
    }

    private static VoiceRoomSettingsSnapshot DistinctiveRemotePolicy()
        => VoiceRoomSettingsSnapshot.Defaults with
        {
            Backend = 42,
            BackendServerUrl = "must-be-clamped",
            MaxChatDistance = 200f,
            FalloffMode = int.MaxValue,
            OcclusionMode = int.MinValue,
            WallsBlockSound = false,
            OnlyHearInSight = false,
            ImpostorHearGhosts = true,
            HearInVent = true,
            VentPrivateChat = false,
            CommsSabDisables = false,
            CameraCanHear = false,
            TeamRadio = false,
            TeamRadioImpostors = false,
            OnlyGhostsCanTalk = true,
            OnlyMeetingOrLobby = true,
            OnlyMeetingOrLobbyAffectsGhosts = true,
            TeamRadioInMeetings = true,
            TeamRadioInTasks = false,
            GhostsHearEachOtherUnlimited = true,
            GracePeriodEnabled = true,
            GracePeriodSeconds = 200f,
        };

    private static void ResetLifetimeState(string source)
    {
        VoiceRoomLifetimeGate.MarkExplicitDisconnect(source);
        VoiceRoomSettingsState.EndSession();
    }

    private static VoiceGameStateSnapshot CreateSnapshot(
        VoiceGamePhase phase = VoiceGamePhase.EndGame,
        int localClientId = 7,
        byte localPlayerId = 1,
        bool hasLocalPosition = true,
        bool includeLocalPlayer = true,
        int localRosterClientId = 7,
        bool localDisconnected = false,
        bool liveLocalPlayerResolved = true,
        bool routingRosterRetained = false,
        bool playerEnumerationCompleted = true)
    {
        var players = includeLocalPlayer
            ? new[]
            {
                default(VoicePlayerSnapshot) with
                {
                    PlayerId = 1,
                    ClientId = localRosterClientId,
                    PlayerName = "Local",
                    Position = new Vector2(1f, 2f),
                    IsLocal = true,
                    Disconnected = localDisconnected,
                },
            }
            : Array.Empty<VoicePlayerSnapshot>();

        return new VoiceGameStateSnapshot(
            phase,
            0,
            localClientId,
            7,
            localPlayerId,
            hasLocalPosition ? new Vector2(1f, 2f) : null,
            2f,
            false,
            -1,
            null,
            players,
            false,
            false,
            0,
            0,
            liveLocalPlayerResolved,
            routingRosterRetained,
            playerEnumerationCompleted);
    }
}
