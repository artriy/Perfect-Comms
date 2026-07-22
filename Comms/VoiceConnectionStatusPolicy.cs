namespace VoiceChatPlugin.VoiceChat;

internal enum VoiceConnectionStage
{
    StartingAudio,
    RetryingAudio,
    SyncingSession,
    ConnectingPlayers,
    Ready,
}

internal readonly record struct VoiceConnectionProgress(
    VoiceConnectionStage Stage,
    int ConnectedPlayers,
    int ExpectedPlayers)
{
    internal static readonly VoiceConnectionProgress Starting = new(
        VoiceConnectionStage.StartingAudio,
        0,
        0);

    internal bool IsConnecting => Stage != VoiceConnectionStage.Ready;
}

internal readonly record struct VoiceConnectionRosterSignature(
    int Count,
    int Xor,
    long Sum);

internal readonly record struct VoiceConnectionDisplayState(
    bool HasConnectingPlayersEpisode,
    VoiceConnectionRosterSignature EpisodeRoster,
    float EpisodeStartedAt,
    bool Visible)
{
    internal static readonly VoiceConnectionDisplayState Initial = new(
        false,
        default,
        0f,
        true);
}

/// <summary>
/// Pure connection/readiness policy for the compact voice HUD. Local helper startup is only the
/// first gate: clients cannot actually exchange routed lobby audio until the live roster is known,
/// host policy is ready, and every expected remote has an established media channel.
/// </summary>
internal static class VoiceConnectionStatusPolicy
{
    internal const float ConnectingPlayersDisplayTimeoutSeconds = 30f;

    internal static bool IsSnapshotReady(
        VoiceGameStateSnapshot? snapshot,
        bool authenticatedRosterAvailable,
        bool containsEveryAuthenticatedRemote)
    {
        if (!authenticatedRosterAvailable
            || !containsEveryAuthenticatedRemote
            || !VoiceRoomLifetimeGate.IsSafeForRouting(snapshot)
            || snapshot == null)
            return false;

        // EndGame intentionally routes by the retained authenticated client-id mesh and has no
        // live PlayerControl world roster. Other phases must wait for the newly spawned roster.
        if (snapshot.Phase == VoiceGamePhase.EndGame)
            return true;

        return !snapshot.RoutingRosterRetained
               && snapshot.LiveLocalPlayerResolved
               && snapshot.PlayerEnumerationCompleted;
    }

    internal static VoiceConnectionProgress Evaluate(
        bool backendAvailable,
        bool transportInitializing,
        bool retryingAfterFailure,
        bool snapshotReady,
        bool routingPolicyReady,
        int connectedPlayers,
        int expectedPlayers,
        bool retainReadyDuringSnapshotGap)
    {
        expectedPlayers = System.Math.Max(0, expectedPlayers);
        connectedPlayers = System.Math.Clamp(connectedPlayers, 0, expectedPlayers);

        if (retryingAfterFailure)
            return new VoiceConnectionProgress(VoiceConnectionStage.RetryingAudio, 0, 0);

        if (!backendAvailable || transportInitializing)
            return VoiceConnectionProgress.Starting;

        if (!routingPolicyReady)
            return new VoiceConnectionProgress(
                VoiceConnectionStage.SyncingSession,
                connectedPlayers,
                expectedPlayers);

        // Scene reconstruction briefly makes the roster incomplete. Once a session was fully
        // ready, the room may suppress that flicker for a bounded grace. The room owns the clock;
        // this policy must never feed a synthetic Ready result back as an unbounded readiness latch.
        if (!snapshotReady)
            return retainReadyDuringSnapshotGap
                ? new VoiceConnectionProgress(VoiceConnectionStage.Ready, 0, 0)
                : new VoiceConnectionProgress(VoiceConnectionStage.SyncingSession, 0, 0);

        if (connectedPlayers < expectedPlayers)
            return new VoiceConnectionProgress(
                VoiceConnectionStage.ConnectingPlayers,
                connectedPlayers,
                expectedPlayers);

        return new VoiceConnectionProgress(
            VoiceConnectionStage.Ready,
            connectedPlayers,
            expectedPlayers);
    }

    internal static VoiceConnectionDisplayState UpdateDisplayState(
        VoiceConnectionDisplayState current,
        VoiceConnectionProgress progress,
        VoiceConnectionRosterSignature roster,
        float now)
    {
        if (progress.Stage == VoiceConnectionStage.Ready)
            return new VoiceConnectionDisplayState(false, default, 0f, false);

        // Local startup, retry, and session-sync messages describe failures outside an individual
        // peer's control. Keep those visible, but preserve any existing player-connection episode
        // so a brief snapshot gap cannot restart an already-expired 30-second budget.
        if (progress.Stage != VoiceConnectionStage.ConnectingPlayers)
            return current with { Visible = true };

        bool startEpisode = !current.HasConnectingPlayersEpisode
                            || current.EpisodeRoster != roster
                            || now < current.EpisodeStartedAt;
        float startedAt = startEpisode ? now : current.EpisodeStartedAt;
        bool visible = now - startedAt < ConnectingPlayersDisplayTimeoutSeconds;
        return new VoiceConnectionDisplayState(true, roster, startedAt, visible);
    }

    internal static bool ShouldPresent(
        VoiceConnectionProgress progress,
        VoiceGamePhase phase,
        bool enabled)
    {
        if (!enabled || !progress.IsConnecting)
            return false;
        return progress.Stage == VoiceConnectionStage.RetryingAudio ||
               phase == VoiceGamePhase.Lobby;
    }

    internal static string BuildText(VoiceConnectionProgress progress, int animationFrame)
    {
        if (!progress.IsConnecting) return string.Empty;

        int normalizedFrame = animationFrame % 3;
        if (normalizedFrame < 0) normalizedFrame += 3;
        // Keep at least a full ellipsis. Cycling between three and five dots reads as activity
        // without the single-dot frame looking like the end of a sentence.
        string dots = new('.', normalizedFrame + 3);
        string prefix = "Connecting voice" + dots;

        return progress.Stage switch
        {
            VoiceConnectionStage.StartingAudio => prefix + " starting audio",
            VoiceConnectionStage.RetryingAudio => "Voice unavailable - retrying audio" + dots,
            VoiceConnectionStage.SyncingSession => prefix + " syncing session",
            VoiceConnectionStage.ConnectingPlayers when progress.ExpectedPlayers > 0 =>
                $"{prefix} {progress.ConnectedPlayers}/{progress.ExpectedPlayers} players connected",
            VoiceConnectionStage.ConnectingPlayers => prefix,
            _ => string.Empty,
        };
    }
}
