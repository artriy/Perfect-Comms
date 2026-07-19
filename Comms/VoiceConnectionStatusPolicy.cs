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

/// <summary>
/// Pure connection/readiness policy for the compact voice HUD. Local helper startup is only the
/// first gate: clients cannot actually exchange routed lobby audio until the live roster is known,
/// host policy is ready, and every expected remote has an established media channel.
/// </summary>
internal static class VoiceConnectionStatusPolicy
{
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
