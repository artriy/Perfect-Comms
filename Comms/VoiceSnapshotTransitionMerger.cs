using System.Collections.Generic;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Reconciles the frame-local PlayerControl roster with the server-maintained InnerNet roster.
/// PlayerControls legitimately disappear while scenes change, but an InnerNet client leaving is
/// authoritative and must remove its route immediately.
/// </summary>
internal static class VoiceSnapshotTransitionMerger
{
    /// <summary>
    /// Carries the last same-session routing roster while both PlayerControls and the resolved
    /// allClients collection are transiently unavailable. EndGame has no world roster and can keep
    /// the routes until the room lifetime gate observes a real leave. Lobby/Intro/Unknown gaps are
    /// bounded so an unavailable auth API cannot preserve stale membership indefinitely.
    /// </summary>
    internal static VoiceGameStateSnapshot RetainPriorRoutesDuringAuthGap(
        VoiceGameStateSnapshot refreshed,
        VoiceGameStateSnapshot previous,
        float authGapSeconds,
        float maxTransitionGapSeconds)
        => RetainPriorRoutes(
            refreshed,
            previous,
            authGapSeconds,
            maxTransitionGapSeconds,
            allowWorldBackedPhase: false);

    /// <summary>
    /// A successfully enumerated but empty allClients collection is not a valid joined roster: it
    /// does not even contain the local client. Among Us emits that state just before EndGame. Allow
    /// the prior same-session routes through that short world-backed lead-in, then keep them for
    /// EndGame; generic authentication failures remain restricted to safe transition phases.
    /// </summary>
    internal static VoiceGameStateSnapshot RetainPriorRoutesDuringEmptyAuthenticatedRosterGap(
        VoiceGameStateSnapshot refreshed,
        VoiceGameStateSnapshot previous,
        float authGapSeconds,
        float maxTransitionGapSeconds)
        => RetainPriorRoutes(
            refreshed,
            previous,
            authGapSeconds,
            maxTransitionGapSeconds,
            allowWorldBackedPhase: true);

    private static VoiceGameStateSnapshot RetainPriorRoutes(
        VoiceGameStateSnapshot refreshed,
        VoiceGameStateSnapshot previous,
        float authGapSeconds,
        float maxTransitionGapSeconds,
        bool allowWorldBackedPhase)
    {
        var endGame = refreshed.Phase == VoiceGamePhase.EndGame;
        var boundedTransition = (allowWorldBackedPhase || IsBoundedAuthGapPhase(refreshed.Phase))
                                && authGapSeconds >= 0f
                                && authGapSeconds < maxTransitionGapSeconds;
        if ((!endGame && !boundedTransition) || previous.Players.Count == 0)
            return refreshed;

        var retainedPlayers = new List<VoicePlayerSnapshot>(previous.Players.Count);
        for (int i = 0; i < previous.Players.Count; i++)
        {
            var player = previous.Players[i];
            if (player.ClientId >= 0 && !player.Disconnected && !player.IsDummy)
                retainedPlayers.Add(player);
        }
        if (retainedPlayers.Count == 0)
            return refreshed;

        var localClientId = refreshed.LocalClientId;
        var localPlayerId = refreshed.LocalPlayerId;
        var localPosition = refreshed.LocalPosition;
        var localLightRadius = refreshed.LocalLightRadius;
        if (!refreshed.LiveLocalPlayerResolved)
        {
            localClientId = localClientId >= 0 ? localClientId : previous.LocalClientId;
            if (TryFindRetainedLocal(retainedPlayers, localClientId, out var retainedLocal))
            {
                localPlayerId = retainedLocal.PlayerId;
                localPosition = retainedLocal.Position;
                localLightRadius = previous.LocalLightRadius;
            }
        }

        return refreshed with
        {
            LocalClientId = localClientId,
            LocalPlayerId = localPlayerId,
            LocalPosition = localPosition,
            LocalLightRadius = localLightRadius,
            Players = retainedPlayers,
            RoutingRosterRetained = true,
        };
    }

    internal static bool IsBoundedAuthGapPhase(VoiceGamePhase phase)
        => phase is VoiceGamePhase.Lobby or VoiceGamePhase.Intro or VoiceGamePhase.Unknown;

    internal static float NextAuthGapStart(
        VoiceGamePhase refreshedPhase,
        VoiceGamePhase previousPhase,
        float currentStart,
        float now)
    {
        if (refreshedPhase == VoiceGamePhase.EndGame)
            return -1f;
        if (!IsBoundedAuthGapPhase(refreshedPhase))
            return -1f;
        return currentStart < 0f || previousPhase == VoiceGamePhase.EndGame
            ? now
            : currentStart;
    }

    internal static float NextEmptyAuthenticatedRosterGapStart(
        VoiceGamePhase refreshedPhase,
        VoiceGamePhase previousPhase,
        float currentStart,
        float now)
    {
        if (refreshedPhase == VoiceGamePhase.EndGame)
            return -1f;
        return currentStart < 0f || previousPhase == VoiceGamePhase.EndGame
            ? now
            : currentStart;
    }

    internal static VoiceGameStateSnapshot Merge(
        VoiceGameStateSnapshot refreshed,
        VoiceGameStateSnapshot? previous,
        ISet<int> authenticatedClientIds,
        IDictionary<int, float> missingSinceByClientId,
        float now,
        float graceSeconds)
    {
        if (previous == null)
        {
            missingSinceByClientId.Clear();
            return refreshed with { RoutingRosterRetained = false };
        }

        var expectedLocalClientId = refreshed.LocalClientId >= 0
            ? refreshed.LocalClientId
            : previous.LocalClientId;
        var players = new List<VoicePlayerSnapshot>(
            refreshed.Players.Count + previous.Players.Count);
        var representedClientIds = new HashSet<int>();

        // A transition-frame LocalPlayer can exist while Data/client ownership is not resolved yet.
        // Do not let that half-built object replace the last authenticated local role/identity.
        for (int i = 0; i < refreshed.Players.Count; i++)
        {
            var player = refreshed.Players[i];
            if (player.ClientId >= 0 && !authenticatedClientIds.Contains(player.ClientId))
            {
                missingSinceByClientId.Remove(player.ClientId);
                continue;
            }
            var unreliableLocal = !refreshed.LiveLocalPlayerResolved
                                  && (player.IsLocal
                                      || (expectedLocalClientId >= 0
                                          && player.ClientId == expectedLocalClientId));
            if (unreliableLocal)
                continue;

            players.Add(player);
            if (player.ClientId >= 0)
            {
                representedClientIds.Add(player.ClientId);
                missingSinceByClientId.Remove(player.ClientId);
            }
        }

        var trackedMissingClientIds = new HashSet<int>();
        var retainedAny = false;
        for (int i = 0; i < previous.Players.Count; i++)
        {
            var player = previous.Players[i];
            var clientId = player.ClientId;
            if (clientId < 0 || player.Disconnected || player.IsDummy)
                continue;

            // InnerNet membership wins over stale scene state. This makes a real leave immediate,
            // including on the results screen where PlayerControls no longer exist.
            if (!authenticatedClientIds.Contains(clientId))
            {
                missingSinceByClientId.Remove(clientId);
                continue;
            }

            if (representedClientIds.Contains(clientId))
            {
                missingSinceByClientId.Remove(clientId);
                continue;
            }

            trackedMissingClientIds.Add(clientId);
            var retain = refreshed.Phase == VoiceGamePhase.EndGame;
            if (retain)
            {
                // EndGame has no world roster for its entire lifetime. Keep only clients still in
                // authenticated allClients, and start a fresh bounded grace if the lobby reforms.
                missingSinceByClientId.Remove(clientId);
            }
            else
            {
                if (!missingSinceByClientId.TryGetValue(clientId, out var missingSince))
                {
                    missingSince = now;
                    missingSinceByClientId[clientId] = missingSince;
                }

                retain = now - missingSince < graceSeconds;
            }

            if (!retain)
                continue;

            players.Add(player);
            representedClientIds.Add(clientId);
            retainedAny = true;
        }

        // Forget timers that no longer describe a missing player in the previous routing roster.
        List<int>? staleTimers = null;
        foreach (var pair in missingSinceByClientId)
        {
            if (trackedMissingClientIds.Contains(pair.Key))
                continue;
            staleTimers ??= new List<int>();
            staleTimers.Add(pair.Key);
        }
        if (staleTimers != null)
            foreach (var clientId in staleTimers)
                missingSinceByClientId.Remove(clientId);

        var localClientId = refreshed.LocalClientId;
        var localPlayerId = refreshed.LocalPlayerId;
        var localPosition = refreshed.LocalPosition;
        var localLightRadius = refreshed.LocalLightRadius;
        if (!refreshed.LiveLocalPlayerResolved
            && TryFindRetainedLocal(players, expectedLocalClientId, out var retainedLocal))
        {
            localClientId = expectedLocalClientId;
            localPlayerId = retainedLocal.PlayerId;
            localPosition = retainedLocal.Position;
            localLightRadius = previous.LocalLightRadius;
        }

        return refreshed with
        {
            LocalClientId = localClientId,
            LocalPlayerId = localPlayerId,
            LocalPosition = localPosition,
            LocalLightRadius = localLightRadius,
            Players = players,
            RoutingRosterRetained = retainedAny,
        };
    }

    private static bool TryFindRetainedLocal(
        IReadOnlyList<VoicePlayerSnapshot> players,
        int expectedLocalClientId,
        out VoicePlayerSnapshot local)
    {
        if (expectedLocalClientId >= 0)
        {
            for (int i = 0; i < players.Count; i++)
            {
                var candidate = players[i];
                if (candidate.ClientId == expectedLocalClientId
                    && candidate.IsLocal
                    && !candidate.Disconnected)
                {
                    local = candidate;
                    return true;
                }
            }
        }

        local = default;
        return false;
    }
}
