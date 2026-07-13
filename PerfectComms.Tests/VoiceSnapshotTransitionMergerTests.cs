using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Xunit;

public sealed class VoiceSnapshotTransitionMergerTests
{
    [Fact]
    public void FreshPhaseWinsWhileAuthenticatedLocalAndRosterAreRetained()
    {
        var previous = Snapshot(
            VoiceGamePhase.Tasks,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        var refreshed = Snapshot(VoiceGamePhase.Lobby, liveLocal: false);
        var missing = new Dictionary<int, float>();

        var merged = VoiceSnapshotTransitionMerger.Merge(
            refreshed,
            previous,
            new HashSet<int> { 7, 8 },
            missing,
            now: 10f,
            graceSeconds: 3f);

        Assert.Equal(VoiceGamePhase.Lobby, merged.Phase);
        Assert.False(merged.LiveLocalPlayerResolved);
        Assert.True(merged.RoutingRosterRetained);
        Assert.Equal(2, merged.Players.Count);
        Assert.Equal((byte)1, merged.LocalPlayerId);
        Assert.Equal(7, merged.LocalClientId);
        Assert.True(merged.LocalPosition.HasValue);
        Assert.True(VoiceRoomLifetimeGate.IsSafeForRouting(merged));
    }

    [Fact]
    public void AuthenticatedLeaveRemovesFreshAndRetainedRouteImmediately()
    {
        var previous = Snapshot(
            VoiceGamePhase.Tasks,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        // A stale PlayerControl can survive a real InnerNet leave for a frame.
        var refreshed = Snapshot(
            VoiceGamePhase.Tasks,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));

        var merged = VoiceSnapshotTransitionMerger.Merge(
            refreshed,
            previous,
            new HashSet<int> { 7 },
            new Dictionary<int, float>(),
            now: 10f,
            graceSeconds: 3f);

        Assert.Single(merged.Players);
        Assert.False(merged.TryGetClient(8, out _));
        Assert.False(merged.RoutingRosterRetained);
    }

    [Fact]
    public void PersistentNonEndGameAbsenceExpiresAfterBoundedGrace()
    {
        var previous = Snapshot(
            VoiceGamePhase.Tasks,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        var refreshed = Snapshot(
            VoiceGamePhase.Lobby,
            liveLocal: true,
            Player(1, 7, isLocal: true));
        var missing = new Dictionary<int, float>();
        var authenticated = new HashSet<int> { 7, 8 };

        var withinGrace = VoiceSnapshotTransitionMerger.Merge(
            refreshed, previous, authenticated, missing, now: 10f, graceSeconds: 3f);
        Assert.True(withinGrace.TryGetClient(8, out _));
        Assert.True(withinGrace.RoutingRosterRetained);

        var expired = VoiceSnapshotTransitionMerger.Merge(
            refreshed, withinGrace, authenticated, missing, now: 13f, graceSeconds: 3f);
        Assert.False(expired.TryGetClient(8, out _));
        Assert.False(expired.RoutingRosterRetained);
    }

    [Fact]
    public void EndGameKeepsOnlyStillAuthenticatedClientsWithoutGraceExpiry()
    {
        var previous = Snapshot(
            VoiceGamePhase.Tasks,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        var refreshed = Snapshot(VoiceGamePhase.EndGame, liveLocal: false);
        var missing = new Dictionary<int, float>();

        var retained = VoiceSnapshotTransitionMerger.Merge(
            refreshed,
            previous,
            new HashSet<int> { 7, 8 },
            missing,
            now: 10f,
            graceSeconds: 3f);
        var retainedMuchLater = VoiceSnapshotTransitionMerger.Merge(
            refreshed,
            retained,
            new HashSet<int> { 7, 8 },
            missing,
            now: 100f,
            graceSeconds: 3f);

        Assert.Equal(2, retainedMuchLater.Players.Count);
        Assert.True(retainedMuchLater.RoutingRosterRetained);

        var afterLeave = VoiceSnapshotTransitionMerger.Merge(
            refreshed,
            retainedMuchLater,
            new HashSet<int> { 7 },
            missing,
            now: 101f,
            graceSeconds: 3f);
        Assert.Single(afterLeave.Players);
        Assert.False(afterLeave.TryGetClient(8, out _));
    }

    [Fact]
    public void FirstEndGameAuthenticationCollectionFailureRetainsPriorRoutes()
    {
        var previous = Snapshot(
            VoiceGamePhase.Tasks,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        var emptyEndGame = Snapshot(VoiceGamePhase.EndGame, liveLocal: false) with
        {
            PlayerEnumerationCompleted = false,
        };

        var retained = VoiceSnapshotTransitionMerger.RetainPriorRoutesDuringAuthGap(
            emptyEndGame,
            previous,
            authGapSeconds: 30f,
            maxTransitionGapSeconds: 3f);

        Assert.Equal(VoiceGamePhase.EndGame, retained.Phase);
        Assert.Equal(2, retained.Players.Count);
        Assert.True(retained.TryGetClient(8, out _));
        Assert.Equal((byte)1, retained.LocalPlayerId);
        Assert.Equal(7, retained.LocalClientId);
        Assert.True(retained.LocalPosition.HasValue);
        Assert.True(retained.RoutingRosterRetained);
        Assert.False(retained.PlayerEnumerationCompleted);
        Assert.True(VoiceRoomLifetimeGate.IsSafeForRouting(retained));
    }

    [Fact]
    public void AuthenticationCollectionFailureRetainsLobbyRoutesOnlyForBoundedGap()
    {
        var previous = Snapshot(
            VoiceGamePhase.Tasks,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        var emptyLobby = Snapshot(VoiceGamePhase.Lobby, liveLocal: false);

        var withinGap = VoiceSnapshotTransitionMerger.RetainPriorRoutesDuringAuthGap(
            emptyLobby,
            previous,
            authGapSeconds: 2.99f,
            maxTransitionGapSeconds: 3f);
        var expired = VoiceSnapshotTransitionMerger.RetainPriorRoutesDuringAuthGap(
            emptyLobby,
            withinGap,
            authGapSeconds: 3f,
            maxTransitionGapSeconds: 3f);

        Assert.Equal(2, withinGap.Players.Count);
        Assert.True(withinGap.RoutingRosterRetained);
        Assert.Empty(expired.Players);
        Assert.False(expired.RoutingRosterRetained);
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Tasks)]
    [InlineData((int)VoiceGamePhase.Meeting)]
    [InlineData((int)VoiceGamePhase.Exile)]
    public void AuthenticationCollectionFailureDoesNotRetainWorldBackedGameplayRoutes(
        int phaseValue)
    {
        var phase = (VoiceGamePhase)phaseValue;
        var previous = Snapshot(
            VoiceGamePhase.Tasks,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        var refreshed = Snapshot(phase, liveLocal: false);

        var result = VoiceSnapshotTransitionMerger.RetainPriorRoutesDuringAuthGap(
            refreshed,
            previous,
            authGapSeconds: 0f,
            maxTransitionGapSeconds: 3f);

        Assert.Empty(result.Players);
        Assert.False(result.RoutingRosterRetained);
    }

    [Fact]
    public void AuthGapClockSpansEndGameUnknownIntroLobbyWithoutResetting()
    {
        var start = VoiceSnapshotTransitionMerger.NextAuthGapStart(
            VoiceGamePhase.EndGame,
            VoiceGamePhase.Tasks,
            currentStart: 4f,
            now: 10f);
        Assert.Equal(-1f, start);

        start = VoiceSnapshotTransitionMerger.NextAuthGapStart(
            VoiceGamePhase.Unknown,
            VoiceGamePhase.EndGame,
            start,
            now: 11f);
        Assert.Equal(11f, start);

        start = VoiceSnapshotTransitionMerger.NextAuthGapStart(
            VoiceGamePhase.Intro,
            VoiceGamePhase.Unknown,
            start,
            now: 12f);
        Assert.Equal(11f, start);

        start = VoiceSnapshotTransitionMerger.NextAuthGapStart(
            VoiceGamePhase.Lobby,
            VoiceGamePhase.Intro,
            start,
            now: 13f);
        Assert.Equal(11f, start);

        var previous = Snapshot(
            VoiceGamePhase.Intro,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        var expired = VoiceSnapshotTransitionMerger.RetainPriorRoutesDuringAuthGap(
            Snapshot(VoiceGamePhase.Lobby, liveLocal: false),
            previous,
            authGapSeconds: 3f,
            maxTransitionGapSeconds: 3f);

        Assert.Empty(expired.Players);
        Assert.False(expired.RoutingRosterRetained);
    }

    [Fact]
    public void EnumerationProvenanceSurvivesRosterMerge()
    {
        var previous = Snapshot(
            VoiceGamePhase.Meeting,
            liveLocal: true,
            Player(1, 7, isLocal: true),
            Player(2, 8));
        var refreshed = Snapshot(
            VoiceGamePhase.Meeting,
            liveLocal: true,
            Player(1, 7, isLocal: true)) with
        {
            PlayerEnumerationCompleted = false,
        };

        var merged = VoiceSnapshotTransitionMerger.Merge(
            refreshed,
            previous,
            new HashSet<int> { 7, 8 },
            new Dictionary<int, float>(),
            now: 10f,
            graceSeconds: 3f);

        Assert.False(merged.PlayerEnumerationCompleted);
        Assert.True(merged.RoutingRosterRetained);
        Assert.True(merged.TryGetClient(8, out _));
    }

    private static VoicePlayerSnapshot Player(byte playerId, int clientId, bool isLocal = false)
        => default(VoicePlayerSnapshot) with
        {
            PlayerId = playerId,
            ClientId = clientId,
            PlayerName = isLocal ? "Local" : $"Remote-{clientId}",
            Position = new Vector2(playerId, playerId),
            IsLocal = isLocal,
            IsVisible = true,
        };

    private static VoiceGameStateSnapshot Snapshot(
        VoiceGamePhase phase,
        bool liveLocal,
        params VoicePlayerSnapshot[] players)
        => new(
            phase,
            0,
            7,
            7,
            liveLocal ? (byte)1 : byte.MaxValue,
            liveLocal ? new Vector2(1f, 1f) : null,
            2f,
            false,
            -1,
            null,
            players,
            false,
            phase is VoiceGamePhase.Meeting or VoiceGamePhase.Exile,
            0,
            0,
            liveLocal,
            RoutingRosterRetained: false,
            PlayerEnumerationCompleted: true);
}
