using System;
using System.Collections.Generic;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Minimal roster observation used by the speaking-bar policy. Keeping this type independent of
/// Unity and PlayerControl makes public-death publication and ordering deterministic in tests.
/// </summary>
internal readonly record struct SpeakingBarRosterMember(byte PlayerId, bool IsDead);

/// <summary>
/// Pure policy for the speaking bar's public roster. Task-world deaths remain frozen until a
/// meeting/exile boundary publishes them; each publication rebuilds instead of unioning so a
/// publicly revived player can return to the alive group at the next boundary.
/// </summary>
internal static class SpeakingBarRosterPolicy
{
    internal static int ComparePlayerIds(
        byte leftPlayerId,
        byte rightPlayerId,
        IReadOnlySet<byte> publiclyDead)
    {
        if (publiclyDead == null)
            throw new ArgumentNullException(nameof(publiclyDead));

        bool leftDead = publiclyDead.Contains(leftPlayerId);
        bool rightDead = publiclyDead.Contains(rightPlayerId);
        if (leftDead != rightDead)
            return leftDead ? 1 : -1;

        return leftPlayerId.CompareTo(rightPlayerId);
    }

    /// <summary>Returns a sorted copy; the source sequence is never modified.</summary>
    internal static List<byte> OrderPlayerIds(
        IEnumerable<byte> playerIds,
        IReadOnlySet<byte> publiclyDead)
    {
        if (playerIds == null)
            throw new ArgumentNullException(nameof(playerIds));
        if (publiclyDead == null)
            throw new ArgumentNullException(nameof(publiclyDead));

        var ordered = new List<byte>(playerIds);
        ordered.Sort((left, right) => ComparePlayerIds(left, right, publiclyDead));
        return ordered;
    }

    internal static void SortPlayerIdsInPlace(
        List<byte> playerIds,
        IReadOnlySet<byte> publiclyDead)
    {
        if (playerIds == null)
            throw new ArgumentNullException(nameof(playerIds));
        if (publiclyDead == null)
            throw new ArgumentNullException(nameof(publiclyDead));

        playerIds.Sort((left, right) => ComparePlayerIds(left, right, publiclyDead));
    }

    internal static bool IsPublicDeathResetPhase(VoiceGamePhase phase)
        => phase is VoiceGamePhase.Menu or VoiceGamePhase.Lobby;

    /// <summary>
    /// A transition-retained or partially enumerated roster must not consume the single
    /// meeting/exile publication. The renderer keeps that publication pending until this is true.
    /// </summary>
    internal static bool CanPublishFromSnapshot(
        bool playerEnumerationCompleted,
        bool routingRosterRetained)
        => playerEnumerationCompleted && !routingRosterRetained;

    /// <summary>
    /// Detects a public-information edge rather than treating every frame inside a meeting as a
    /// fresh publication. MeetingActive can rise one frame before Phase becomes Meeting. Exile is
    /// a separate edge because it publishes the ejected player's death.
    /// </summary>
    internal static bool IsPublicDeathPublicationBoundary(
        VoiceGamePhase previousPhase,
        bool previousMeetingActive,
        VoiceGamePhase currentPhase,
        bool currentMeetingActive)
    {
        if (IsPublicDeathResetPhase(currentPhase))
            return false;

        if (currentPhase == VoiceGamePhase.Exile && previousPhase != VoiceGamePhase.Exile)
            return true;

        bool previousPublic = previousMeetingActive
                              || previousPhase is VoiceGamePhase.Meeting or VoiceGamePhase.Exile;
        bool currentMeeting = currentMeetingActive || currentPhase == VoiceGamePhase.Meeting;
        return currentMeeting && !previousPublic;
    }

    /// <summary>
    /// Produces the next immutable-by-convention snapshot as a new set. Menu/Lobby clears it,
    /// Meeting/Exile edges rebuild it from the current roster, and all other frames clone the
    /// prior snapshot unchanged. Rebuilding is what clears a revive at the next public boundary.
    /// </summary>
    internal static HashSet<byte> NextPubliclyDeadSnapshot(
        IReadOnlySet<byte> previousSnapshot,
        VoiceGamePhase previousPhase,
        bool previousMeetingActive,
        VoiceGamePhase currentPhase,
        bool currentMeetingActive,
        IEnumerable<SpeakingBarRosterMember> currentRoster)
    {
        if (previousSnapshot == null)
            throw new ArgumentNullException(nameof(previousSnapshot));
        if (currentRoster == null)
            throw new ArgumentNullException(nameof(currentRoster));

        if (IsPublicDeathResetPhase(currentPhase))
            return new HashSet<byte>();

        if (!IsPublicDeathPublicationBoundary(
                previousPhase,
                previousMeetingActive,
                currentPhase,
                currentMeetingActive))
            return new HashSet<byte>(previousSnapshot);

        return RebuildPubliclyDeadSnapshot(currentRoster);
    }

    internal static HashSet<byte> RebuildPubliclyDeadSnapshot(
        IEnumerable<SpeakingBarRosterMember> roster)
    {
        if (roster == null)
            throw new ArgumentNullException(nameof(roster));

        var rebuilt = new HashSet<byte>();
        foreach (var player in roster)
        {
            if (player.IsDead)
                rebuilt.Add(player.PlayerId);
        }

        return rebuilt;
    }

    /// <summary>
    /// Adds deaths exposed by an already-public surface such as MeetingHud player cards. Unlike a
    /// boundary rebuild, this deliberately never removes an earlier publication: a transient live
    /// data read or an in-meeting revive must not make an already-revealed death private again.
    /// </summary>
    internal static HashSet<byte> AddPubliclyObservedDeaths(
        IReadOnlySet<byte> previousSnapshot,
        IEnumerable<SpeakingBarRosterMember> publicRoster)
    {
        if (previousSnapshot == null)
            throw new ArgumentNullException(nameof(previousSnapshot));
        if (publicRoster == null)
            throw new ArgumentNullException(nameof(publicRoster));

        var merged = new HashSet<byte>(previousSnapshot);
        foreach (var player in publicRoster)
        {
            if (player.IsDead)
                merged.Add(player.PlayerId);
        }
        return merged;
    }
}
