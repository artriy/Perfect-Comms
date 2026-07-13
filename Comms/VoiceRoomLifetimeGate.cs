using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal enum VoiceSnapshotRefreshDecision
{
    UseRefreshed,
    RetainPrevious,
    Clear,
}

/// <summary>
/// Makes an explicit InnerNet disconnect authoritative over stale scene objects. EndGame and
/// LobbyBehaviour can survive for a frame while DisconnectInternal is unwinding; without this
/// latch the room driver could recreate capture immediately after the user chose Exit.
/// Only AmongUsClient.OnGameJoined confirms that a later network session may own voice again.
/// </summary>
internal static class VoiceRoomLifetimeGate
{
    private static int _explicitDisconnectLatched;

    internal static bool IsExplicitDisconnectLatched
        => Volatile.Read(ref _explicitDisconnectLatched) != 0;

    /// <summary>
    /// Returns true only for confirmed voice-session terminal conditions. Scene objects and
    /// InnerNet GameState are deliberately absent: both are transient while moving between the
    /// game, EndGame, lobby, and intro, and must never tear down an otherwise-live voice mesh.
    /// </summary>
    internal static bool IsTerminalCondition(
        bool hasAmongUsClient,
        bool isLocalServer,
        bool explicitDisconnectLatched)
        => !hasAmongUsClient || isLocalServer || explicitDisconnectLatched;

    internal static bool CanRetainSnapshot(
        bool hasSnapshot,
        int snapshotGameId,
        int currentGameId,
        bool explicitDisconnectLatched)
        => hasSnapshot
           && snapshotGameId != 0
           && currentGameId == snapshotGameId
           && !explicitDisconnectLatched;

    /// <summary>
    /// Rejects transition-frame snapshots that have lost the authenticated local identity or
    /// player roster. Such snapshots are non-null but cannot route voice safely and must not
    /// replace a still-usable snapshot from the same live session.
    /// </summary>
    internal static bool IsUsableSnapshot(VoiceGameStateSnapshot? snapshot)
    {
        if (snapshot == null
            || snapshot.LocalClientId < 0)
            return false;

        // EndGame intentionally has no world PlayerControl roster or positions. It is still safe to
        // route because the backend retains authenticated client-id peers and CalculateEndGame is a
        // global, position-independent route. Rejecting it retained the prior Tasks snapshot and
        // prevented seamless post-game voice.
        if (snapshot.Phase == VoiceGamePhase.EndGame)
            return true;

        if (!snapshot.LiveLocalPlayerResolved
            || snapshot.LocalPlayerId == byte.MaxValue
            || !snapshot.LocalPosition.HasValue
            || snapshot.Players.Count == 0
            || !snapshot.TryGetLocalPlayer(out var localPlayer))
            return false;

        return localPlayer.IsLocal
               && localPlayer.ClientId == snapshot.LocalClientId
               && !localPlayer.Disconnected;
    }

    /// <summary>
    /// A phase-promoted transition snapshot may safely continue routing with a bounded, previously
    /// authenticated local identity even though the live LocalPlayer is not resolved on this exact
    /// frame. The provenance remains false so recovery code cannot mistake it for a settled roster.
    /// </summary>
    internal static bool IsSafeForRouting(VoiceGameStateSnapshot? snapshot)
    {
        if (IsUsableSnapshot(snapshot))
            return true;

        if (snapshot == null
            || !snapshot.RoutingRosterRetained
            || snapshot.LocalClientId < 0
            || snapshot.LocalPlayerId == byte.MaxValue
            || !snapshot.LocalPosition.HasValue
            || !snapshot.TryGetLocalPlayer(out var localPlayer))
            return false;

        return localPlayer.IsLocal
               && localPlayer.ClientId == snapshot.LocalClientId
               && !localPlayer.Disconnected;
    }

    /// <summary>
    /// EndGame can route all surviving authenticated peers without a world roster. While the lobby
    /// rebuilds immediately afterward, retain that global snapshot until the live roster is complete
    /// so voice does not dip for players whose PlayerControl appears a few frames late. This guard is
    /// deliberately limited to EndGame -> lobby-like transitions; it must never block initial startup
    /// or keep stale task/meeting routing when a partial gameplay snapshot is available.
    /// </summary>
    internal static bool RequiresCompleteRemoteRoster(
        VoiceGamePhase? previousPhase,
        VoiceGamePhase refreshedPhase)
        => previousPhase == VoiceGamePhase.EndGame
           && refreshedPhase is VoiceGamePhase.Lobby
               or VoiceGamePhase.Intro
               or VoiceGamePhase.Unknown;

    internal static bool ShouldRetainRoutingRosterForPhasePromotion(
        VoiceGamePhase? previousPhase,
        VoiceGamePhase refreshedPhase,
        int previousPlayerCount,
        int refreshedPlayerCount)
        => previousPhase.HasValue
           && refreshedPhase == VoiceGamePhase.EndGame
           && previousPlayerCount > 0
           && refreshedPlayerCount < previousPlayerCount;

    /// <summary>
    /// Pure refresh policy used by the room update loop and regression tests. An explicit session
    /// end always wins; otherwise a usable refresh wins, then a usable retainable prior snapshot.
    /// </summary>
    internal static VoiceSnapshotRefreshDecision DecideSnapshotRefresh(
        bool sessionActive,
        bool refreshedUsable,
        bool previousUsable,
        bool previousRetainable)
    {
        if (!sessionActive) return VoiceSnapshotRefreshDecision.Clear;
        if (refreshedUsable) return VoiceSnapshotRefreshDecision.UseRefreshed;
        if (previousUsable && previousRetainable) return VoiceSnapshotRefreshDecision.RetainPrevious;
        return VoiceSnapshotRefreshDecision.Clear;
    }

    internal static void MarkExplicitDisconnect(string reason)
    {
        Interlocked.Exchange(ref _explicitDisconnectLatched, 1);
        try { VoiceRoomSettingsState.EndSession(); }
        catch { /* settings cleanup can never interfere with the vanilla disconnect */ }
        try { AmongUsRpcSignaling.ClearDeferredHellos($"disconnect:{reason}"); }
        catch { /* signaling cleanup can never interfere with the vanilla disconnect */ }
        try { VoiceDiagnostics.Log("voice.room.lifetime", $"event=disconnect-latched reason={Safe(reason)}"); }
        catch { /* a diagnostic sink can never interfere with the vanilla disconnect */ }
    }

    internal static void ConfirmJoinedSession(string source)
    {
        var gameId = 0;
        try { gameId = AmongUsClient.Instance?.GameId ?? 0; }
        catch { /* the explicit overload remains usable outside a live game */ }
        ConfirmJoinedSession(source, gameId);
    }

    internal static void ConfirmJoinedSession(string source, int gameId)
    {
        // Keep compatible buffered Hellos across this callback. The mailbox is already scoped by
        // GameId/local client, roster-filtered, TTL-bounded, and contains no SDP/ICE generation
        // state. Preserving a Hello that raced just ahead of OnGameJoined avoids adding a full
        // resend interval to initial voice bootstrap; a real disconnect clears the mailbox above.
        try { VoiceRoomSettingsState.BeginSession(gameId); }
        catch { /* joining the game must not depend on settings synchronization */ }

        var wasLatched = Interlocked.Exchange(ref _explicitDisconnectLatched, 0) != 0;
        try
        {
            VoiceDiagnostics.Log(
                "voice.room.lifetime",
                $"event={(wasLatched ? "disconnect-cleared" : "session-confirmed")} source={Safe(source)}");
        }
        catch { /* joining the game must not depend on diagnostics */ }
    }

    private static string Safe(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", System.StringComparison.Ordinal)
            .Replace("\r", "\\r", System.StringComparison.Ordinal)
            .Replace("\n", "\\n", System.StringComparison.Ordinal)
            .Replace("\"", "\\\"", System.StringComparison.Ordinal);
}
