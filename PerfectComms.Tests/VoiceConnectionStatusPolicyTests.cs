using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using Xunit;

namespace PerfectComms.Tests;

public sealed class VoiceConnectionStatusPolicyTests
{
    [Fact]
    public void FreshWorldSnapshotWaitsForEveryAuthenticatedRemotePlayer()
    {
        var snapshot = Snapshot(
            VoiceGamePhase.Lobby,
            default(VoicePlayerSnapshot) with
            {
                PlayerId = 1,
                ClientId = 7,
                PlayerName = "Local",
                Position = Vector2.zero,
                IsLocal = true,
            });

        Assert.False(VoiceConnectionStatusPolicy.IsSnapshotReady(
            snapshot,
            authenticatedRosterAvailable: true,
            containsEveryAuthenticatedRemote: false));
        Assert.True(VoiceConnectionStatusPolicy.IsSnapshotReady(
            snapshot,
            authenticatedRosterAvailable: true,
            containsEveryAuthenticatedRemote: true));
    }

    [Fact]
    public void PlayerlessEndGameCanFinishConnectingFromTheAuthenticatedMesh()
    {
        var snapshot = Snapshot(VoiceGamePhase.EndGame) with
        {
            LocalPlayerId = byte.MaxValue,
            LocalPosition = null,
            LiveLocalPlayerResolved = false,
            PlayerEnumerationCompleted = false,
        };

        Assert.True(VoiceConnectionStatusPolicy.IsSnapshotReady(
            snapshot,
            authenticatedRosterAvailable: true,
            containsEveryAuthenticatedRemote: true));
    }

    [Fact]
    public void RetainedTransitionRosterDoesNotBecomeFreshReadinessEvidence()
    {
        var snapshot = Snapshot(
            VoiceGamePhase.Lobby,
            default(VoicePlayerSnapshot) with
            {
                PlayerId = 1,
                ClientId = 7,
                PlayerName = "Local",
                Position = Vector2.zero,
                IsLocal = true,
            }) with
        {
            LiveLocalPlayerResolved = false,
            RoutingRosterRetained = true,
            PlayerEnumerationCompleted = false,
        };

        Assert.False(VoiceConnectionStatusPolicy.IsSnapshotReady(
            snapshot,
            authenticatedRosterAvailable: true,
            containsEveryAuthenticatedRemote: true));
    }

    [Fact]
    public void StartsConnectingBeforeTheBackendIsPublished()
    {
        var progress = Evaluate(backendAvailable: false, snapshotReady: false);

        Assert.Equal(VoiceConnectionStage.StartingAudio, progress.Stage);
        Assert.Equal("Connecting voice... starting audio", Build(progress, 0));
    }

    [Fact]
    public void LocalTransportStartupRemainsVisibleEvenAfterAnEarlierReadyState()
    {
        var progress = Evaluate(
            transportInitializing: true,
            snapshotReady: true,
            routingPolicyReady: true,
            connectedPlayers: 2,
            expectedPlayers: 2,
            retainReadyDuringSnapshotGap: true);

        Assert.Equal(VoiceConnectionStage.StartingAudio, progress.Stage);
    }

    [Fact]
    public void WaitsForATrustworthyLobbySnapshotBeforeClaimingReady()
    {
        var progress = Evaluate(snapshotReady: false, routingPolicyReady: true);

        Assert.Equal(VoiceConnectionStage.SyncingSession, progress.Stage);
        Assert.Equal("Connecting voice.... syncing session", Build(progress, 1));
    }

    [Fact]
    public void ABoundedTransientRosterGapDoesNotFlashConnectingAfterReadiness()
    {
        var progress = Evaluate(
            snapshotReady: false,
            routingPolicyReady: true,
            retainReadyDuringSnapshotGap: true);

        Assert.Equal(VoiceConnectionStage.Ready, progress.Stage);
        Assert.Equal(string.Empty, Build(progress, 2));
    }

    [Fact]
    public void AnExpiredRosterGapReturnsToSyncingInsteadOfLatchingReady()
    {
        var progress = Evaluate(
            snapshotReady: false,
            routingPolicyReady: true,
            retainReadyDuringSnapshotGap: false);

        Assert.Equal(VoiceConnectionStage.SyncingSession, progress.Stage);
        Assert.Equal("Connecting voice... syncing session", Build(progress, 0));
    }

    [Fact]
    public void HostPolicySyncRemainsPartOfTalkReadiness()
    {
        var progress = Evaluate(
            snapshotReady: true,
            routingPolicyReady: false,
            connectedPlayers: 3,
            expectedPlayers: 3);

        Assert.Equal(VoiceConnectionStage.SyncingSession, progress.Stage);
        Assert.Equal("Connecting voice..... syncing session", Build(progress, 2));
    }

    [Fact]
    public void PartialMeshShowsLivePlayerProgress()
    {
        var progress = Evaluate(
            snapshotReady: true,
            routingPolicyReady: true,
            connectedPlayers: 1,
            expectedPlayers: 3);

        Assert.Equal(VoiceConnectionStage.ConnectingPlayers, progress.Stage);
        Assert.Equal("Connecting voice..... 1/3 players connected", Build(progress, 2));
    }

    [Fact]
    public void PersistentHelperFailureClearlyShowsThatVoiceIsRetrying()
    {
        var progress = Evaluate(
            backendAvailable: false,
            retryingAfterFailure: true,
            snapshotReady: false);

        Assert.Equal(VoiceConnectionStage.RetryingAudio, progress.Stage);
        Assert.Equal("Voice unavailable - retrying audio...", Build(progress, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    public void SoloOrCompleteLobbyIsReady(int connectedPlayers, int expectedPlayers)
    {
        var progress = Evaluate(
            snapshotReady: true,
            routingPolicyReady: true,
            connectedPlayers: connectedPlayers,
            expectedPlayers: expectedPlayers);

        Assert.Equal(VoiceConnectionStage.Ready, progress.Stage);
        Assert.False(progress.IsConnecting);
        Assert.Equal(string.Empty, Build(progress, 0));
    }

    [Fact]
    public void CountsAreClampedBeforeTheyReachPlayerFacingCopy()
    {
        var progress = Evaluate(
            snapshotReady: true,
            routingPolicyReady: true,
            connectedPlayers: 7,
            expectedPlayers: 2);

        Assert.Equal(VoiceConnectionStage.Ready, progress.Stage);
        Assert.Equal(2, progress.ConnectedPlayers);
        Assert.Equal(2, progress.ExpectedPlayers);
    }

    private static VoiceConnectionProgress Evaluate(
        bool backendAvailable = true,
        bool transportInitializing = false,
        bool retryingAfterFailure = false,
        bool snapshotReady = false,
        bool routingPolicyReady = false,
        int connectedPlayers = 0,
        int expectedPlayers = 0,
        bool retainReadyDuringSnapshotGap = false)
        => VoiceConnectionStatusPolicy.Evaluate(
            backendAvailable,
            transportInitializing,
            retryingAfterFailure,
            snapshotReady,
            routingPolicyReady,
            connectedPlayers,
            expectedPlayers,
            retainReadyDuringSnapshotGap);

    private static string Build(VoiceConnectionProgress progress, int animationFrame)
        => VoiceConnectionStatusPolicy.BuildText(progress, animationFrame);

    private static VoiceGameStateSnapshot Snapshot(
        VoiceGamePhase phase,
        params VoicePlayerSnapshot[] players)
        => new(
            phase,
            0,
            7,
            7,
            1,
            Vector2.zero,
            2f,
            false,
            -1,
            null,
            players,
            false,
            false,
            0,
            0);
}
