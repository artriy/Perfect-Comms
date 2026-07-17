using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SpeakingBarRosterPolicyTests
{
    [Fact]
    public void OrdersAliveBeforePubliclyDeadThenByPlayerId()
    {
        byte[] source = { 5, 3, 2, 1, 4 };
        var publiclyDead = new HashSet<byte> { 2, 5 };

        var ordered = SpeakingBarRosterPolicy.OrderPlayerIds(source, publiclyDead);

        Assert.Equal(new byte[] { 1, 3, 4, 2, 5 }, ordered);
        Assert.Equal(new byte[] { 5, 3, 2, 1, 4 }, source);
    }

    [Fact]
    public void InPlaceOrderingUsesTheSameDeterministicComparator()
    {
        var playerIds = new List<byte> { 9, 4, 7, 2, 1 };
        var publiclyDead = new HashSet<byte> { 2, 7 };

        SpeakingBarRosterPolicy.SortPlayerIdsInPlace(playerIds, publiclyDead);

        Assert.Equal(new byte[] { 1, 4, 9, 2, 7 }, playerIds);
        Assert.True(SpeakingBarRosterPolicy.ComparePlayerIds(4, 2, publiclyDead) < 0);
        Assert.True(SpeakingBarRosterPolicy.ComparePlayerIds(2, 7, publiclyDead) < 0);
    }

    [Fact]
    public void TaskFramesFreezePreviouslyPublishedDeaths()
    {
        var previous = new HashSet<byte> { 2 };
        var liveTaskRoster = Roster((1, false), (2, false), (3, true));

        var next = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            previous,
            VoiceGamePhase.Meeting,
            previousMeetingActive: true,
            VoiceGamePhase.Tasks,
            currentMeetingActive: false,
            liveTaskRoster);

        AssertSet(next, 2);
        Assert.NotSame(previous, next);
    }

    [Fact]
    public void MeetingActiveRisingPublishesBeforePhaseChanges()
    {
        var next = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            new HashSet<byte>(),
            VoiceGamePhase.Tasks,
            previousMeetingActive: false,
            VoiceGamePhase.Tasks,
            currentMeetingActive: true,
            Roster((1, false), (2, true), (4, true)));

        AssertSet(next, 2, 4);
    }

    [Fact]
    public void EnteringMeetingPublishesWhenRisingFlagWasNotObserved()
    {
        var next = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            new HashSet<byte>(),
            VoiceGamePhase.Tasks,
            previousMeetingActive: false,
            VoiceGamePhase.Meeting,
            currentMeetingActive: false,
            Roster((1, false), (3, true)));

        AssertSet(next, 3);
    }

    [Fact]
    public void RepeatedMeetingFrameDoesNotRepublishTransientRosterState()
    {
        var next = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            new HashSet<byte> { 2 },
            VoiceGamePhase.Meeting,
            previousMeetingActive: true,
            VoiceGamePhase.Meeting,
            currentMeetingActive: true,
            Roster((1, false), (2, false), (3, true)));

        AssertSet(next, 2);
    }

    [Fact]
    public void PublicMeetingCardDeathIsAddedWithoutClearingEarlierPublication()
    {
        var previous = new HashSet<byte> { 2 };

        var next = SpeakingBarRosterPolicy.AddPubliclyObservedDeaths(
            previous,
            Roster((1, false), (2, false), (3, true)));

        AssertSet(next, 2, 3);
        AssertSet(previous, 2);
        Assert.NotSame(previous, next);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    public void PublicationWaitsForCompleteNonRetainedRoster(
        bool enumerationCompleted,
        bool routingRosterRetained,
        bool expected)
    {
        Assert.Equal(expected, SpeakingBarRosterPolicy.CanPublishFromSnapshot(
            enumerationCompleted,
            routingRosterRetained));
    }

    [Fact]
    public void ExileBoundaryRebuildsAndPublishesEjectedPlayer()
    {
        var next = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            new HashSet<byte> { 2 },
            VoiceGamePhase.Meeting,
            previousMeetingActive: true,
            VoiceGamePhase.Exile,
            currentMeetingActive: false,
            Roster((1, false), (2, true), (3, true)));

        AssertSet(next, 2, 3);
    }

    [Fact]
    public void NextPublicationClearsARevivedPlayerInsteadOfUnioning()
    {
        var next = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            new HashSet<byte> { 2, 3 },
            VoiceGamePhase.Tasks,
            previousMeetingActive: false,
            VoiceGamePhase.Meeting,
            currentMeetingActive: true,
            Roster((1, false), (2, false), (3, true)));

        AssertSet(next, 3);
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Menu)]
    [InlineData((int)VoiceGamePhase.Lobby)]
    public void MenuAndLobbyResetPublicDeaths(int phaseValue)
    {
        var next = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            new HashSet<byte> { 2, 3 },
            VoiceGamePhase.EndGame,
            previousMeetingActive: false,
            (VoiceGamePhase)phaseValue,
            currentMeetingActive: false,
            Roster((2, true), (3, true)));

        Assert.Empty(next);
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Unknown)]
    [InlineData((int)VoiceGamePhase.Intro)]
    [InlineData((int)VoiceGamePhase.EndGame)]
    public void NonPublicNonResetPhasesFreezeSnapshot(int phaseValue)
    {
        var next = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            new HashSet<byte> { 6 },
            VoiceGamePhase.Tasks,
            previousMeetingActive: false,
            (VoiceGamePhase)phaseValue,
            currentMeetingActive: false,
            Roster((6, false), (7, true)));

        AssertSet(next, 6);
    }

    private static SpeakingBarRosterMember[] Roster(params (byte Id, bool Dead)[] players)
    {
        var roster = new SpeakingBarRosterMember[players.Length];
        for (int i = 0; i < players.Length; i++)
            roster[i] = new SpeakingBarRosterMember(players[i].Id, players[i].Dead);
        return roster;
    }

    private static void AssertSet(HashSet<byte> actual, params byte[] expected)
        => Assert.True(
            actual.SetEquals(expected),
            $"Expected {{{string.Join(",", expected)}}}, got {{{string.Join(",", actual)}}}.");
}
