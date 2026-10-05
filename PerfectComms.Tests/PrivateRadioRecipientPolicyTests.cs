using System;
using System.Collections.Generic;
using PerfectComms.Api;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class PrivateRadioRecipientPolicyTests
{
    private static readonly VoiceRadioState ImpostorRadio = VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors);
    private static readonly VoiceRoomSettingsSnapshot Settings = VoiceRoomSettingsSnapshot.Defaults with
    {
        TeamRadio = true, TeamRadioImpostors = true, TeamRadioInMeetings = true, TeamRadioInTasks = true,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrivateSpeechExcludesLivingCrewWithoutExcludingGhostsOrSpectators(bool specialChat)
    {
        var speaker = Player(1, true) with { IsLocal = true };
        var settings = Settings with { OnlyMeetingOrLobby = specialChat, MeetingOnlyImpostorChat = specialChat };
        var crewWithModPrivileges = Player(6, false) with
        {
            IsImpostor = true,
            External = ExternalVoiceState.None with { ListenerBypassTaskVoiceGates = true },
        };
        var snapshot = Snapshot(VoiceGamePhase.Tasks, speaker, Player(2, true), Player(3, false),
            Player(4, false) with { IsDead = true, IsVisible = false },
            Player(5, false) with { IsSpectator = true, IsVisible = false }, crewWithModPrivileges,
            Player(7, true) with { Disconnected = true }, Player(8, true) with { IsDummy = true });
        var receivers = new List<int>();
        Assert.True(Collect(snapshot, settings, specialChat ? VoiceRadioState.None : ImpostorRadio, receivers));
        Assert.Equal(new[] { 2, 4, 5 }, receivers);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeadImpostorTransmissionRequiresToggleOnBothPrivatePaths(bool specialChat, bool acrossDeath)
    {
        var settings = Settings with
        {
            OnlyMeetingOrLobby = specialChat, MeetingOnlyImpostorChat = specialChat,
            ImpostorsTalkAcrossDeath = acrossDeath,
        };
        var snapshot = Snapshot(VoiceGamePhase.Tasks,
            Player(1, true) with { IsLocal = true, IsDead = true }, Player(2, true),
            Player(3, false) with { IsDead = true });
        var receivers = new List<int>();
        Assert.True(Collect(snapshot, settings, specialChat ? VoiceRadioState.None : ImpostorRadio, receivers));
        Assert.Equal(acrossDeath ? new[] { 2, 3 } : Array.Empty<int>(), receivers);
    }

    [Fact]
    public void MeetingDeathSeparationAppliesToPrivateRadioAndTaskChatEnds()
    {
        var settings = Settings with
        {
            OnlyMeetingOrLobby = true, MeetingOnlyImpostorChat = true, ImpostorsTalkAcrossDeath = true,
            ImpostorsTalkAcrossDeathInMeetings = false,
        };
        var snapshot = Snapshot(VoiceGamePhase.Meeting,
            Player(1, true) with { IsLocal = true, IsDead = true }, Player(2, true),
            Player(3, false) with { IsDead = true }, Player(4, false) with { IsSpectator = true });
        var receivers = new List<int>();
        Assert.True(Collect(snapshot, settings, ImpostorRadio, receivers));
        Assert.Equal(new[] { 3, 4 }, receivers);
        Assert.False(Collect(snapshot, settings, VoiceRadioState.None, receivers));
        Assert.Empty(receivers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedOrUntrustedRostersCannotExpandPrivateAudience(bool retained)
    {
        var snapshot = Snapshot(VoiceGamePhase.Tasks, Player(1, true) with { IsLocal = true }, Player(2, true))
            with { RoutingRosterRetained = retained };
        var receivers = new List<int> { 999 };
        Assert.True(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(snapshot, Settings,
            VoiceGamePhase.Tasks, hostPolicyReady: retained, ImpostorRadio, false, receivers));
        Assert.Empty(receivers);
        Assert.True(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(null, Settings,
            VoiceGamePhase.Tasks, false, VoiceRadioState.None, true, receivers));
        Assert.Empty(receivers);
    }

    [Fact]
    public void ExternalRadioSelectsOnlyMatchingManagedMembership()
    {
        var membership = new ExternalVoiceManagedRadioState("mod\0team", "Team", "T");
        var speaker = Player(1, false) with
        {
            IsLocal = true, External = ExternalVoiceState.None with { ManagedRadioChannels = [membership] },
        };
        var member = Player(2, false) with { External = speaker.External };
        var snapshot = Snapshot(VoiceGamePhase.Tasks, speaker, member, Player(3, true),
            member with { PlayerId = 4, ClientId = 4, IsDead = true });
        var receivers = new List<int>();
        Assert.True(Collect(snapshot, Settings, VoiceRadioState.Managed("mod\0team"), receivers));
        Assert.Equal(new[] { 2 }, receivers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeetingOverrideSendsOnlyToImpostorsGhostsAndSpectators(bool speakerDead)
    {
        var settings = Settings with
        {
            ImpostorsTalkAcrossDeath = true, TeamRadio = false, TeamRadioImpostors = false,
            TeamRadioInMeetings = false, TeamRadioInTasks = false,
        };
        var privilegedCrew = Player(7, false) with
        {
            IsImpostor = true, External = ExternalVoiceState.None with { ListenerBypassTaskVoiceGates = true },
        };
        var snapshot = Snapshot(VoiceGamePhase.Meeting,
            Player(1, true) with { IsLocal = true, IsDead = speakerDead },
            Player(2, true), Player(3, true) with { IsDead = true, IsVisible = false },
            Player(4, false), Player(5, false) with { IsDead = true, IsVisible = false },
            Player(6, false) with { IsSpectator = true, IsVisible = false }, privilegedCrew,
            Player(8, true) with { Disconnected = true }, Player(9, true) with { IsDummy = true });
        var receivers = new List<int>();
        Assert.True(Collect(snapshot, settings, ImpostorRadio, receivers));
        Assert.Equal(new[] { 2, 3, 5, 6 }, receivers);
        Assert.False(Collect(snapshot, settings, VoiceRadioState.None, receivers));
        Assert.Empty(receivers);
    }

    [Fact]
    public void MeetingOverrideNeverFallsBackToPublicForEmptyOrUntrustedAudience()
    {
        var settings = Settings with { ImpostorsTalkAcrossDeath = true, TeamRadio = false };
        var snapshot = Snapshot(VoiceGamePhase.Meeting,
            Player(1, true) with { IsLocal = true }, Player(2, false));
        var receivers = new List<int> { 999 };
        Assert.True(Collect(snapshot, settings, ImpostorRadio, receivers));
        Assert.Empty(receivers);
        Assert.True(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(snapshot, settings,
            snapshot.Phase, false, ImpostorRadio, true, receivers));
        Assert.Empty(receivers);
        Assert.True(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(
            snapshot with { RoutingRosterRetained = true }, settings, snapshot.Phase,
            true, VoiceRadioState.None, true, receivers));
        Assert.Empty(receivers);
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Exile)]
    [InlineData((int)VoiceGamePhase.Tasks)]
    public void MeetingOverrideCannotGrantRadioInOtherPhases(int phaseValue)
    {
        var phase = (VoiceGamePhase)phaseValue;
        var settings = Settings with { ImpostorsTalkAcrossDeath = true, TeamRadio = false };
        var speaker = Player(1, true) with { IsLocal = true, IsDead = true };
        var listener = Player(2, true);
        var receivers = new List<int>();
        Assert.True(Collect(Snapshot(VoiceGamePhase.Meeting, speaker, listener), settings, ImpostorRadio, receivers));
        Assert.Equal(new[] { 2 }, receivers);
        Assert.True(Collect(Snapshot(phase, speaker, listener), settings, ImpostorRadio, receivers));
        Assert.Empty(receivers);
    }

    [Fact]
    public void ChangingMeetingToggleWhileHeldRecomputesRecipientsAndPreservesTaskRadio()
    {
        var settings = Settings with { ImpostorsTalkAcrossDeath = true };
        var speaker = Player(1, true) with { IsLocal = true, IsDead = true };
        var living = Player(2, true);
        var ghost = Player(3, false) with { IsDead = true };
        var snapshot = Snapshot(VoiceGamePhase.Meeting, speaker, living, ghost);
        var receivers = new List<int>();
        Assert.True(Collect(snapshot, settings, ImpostorRadio, receivers));
        Assert.Equal(new[] { 2, 3 }, receivers);
        settings = settings with { ImpostorsTalkAcrossDeathInMeetings = false };
        Assert.True(Collect(snapshot, settings, ImpostorRadio, receivers));
        Assert.Equal(new[] { 3 }, receivers);
        var task = Snapshot(VoiceGamePhase.Tasks, speaker, living, ghost);
        Assert.True(Collect(task, settings, ImpostorRadio, receivers));
        Assert.Equal(new[] { 2, 3 }, receivers);
        Assert.True(Collect(task, settings with { ImpostorsTalkAcrossDeathInMeetings = true }, ImpostorRadio, receivers));
        Assert.Equal(new[] { 2, 3 }, receivers);
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Menu)]
    [InlineData((int)VoiceGamePhase.Lobby)]
    [InlineData((int)VoiceGamePhase.Intro)]
    [InlineData((int)VoiceGamePhase.EndGame)]
    public void GlobalVoicePhasesReleasePrivateScopeEvenWithRadioHeldOrRetainedRoster(int phaseValue)
    {
        var phase = (VoiceGamePhase)phaseValue;
        var settings = Settings with { OnlyMeetingOrLobby = true, MeetingOnlyImpostorChat = true };
        var snapshot = Snapshot(phase, Player(1, true) with { IsLocal = true }, Player(2, true));
        var receivers = new List<int> { 999 };
        Assert.False(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(
            snapshot, settings, phase, true, ImpostorRadio, true, receivers));
        Assert.Empty(receivers);
        Assert.False(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(
            snapshot with { RoutingRosterRetained = true }, settings, phase, false,
            ImpostorRadio, true, receivers));
        Assert.Empty(receivers);
    }

    private static bool Collect(VoiceGameStateSnapshot snapshot, VoiceRoomSettingsSnapshot settings,
        VoiceRadioState radio, List<int> receivers)
        => PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(snapshot, settings,
            snapshot.Phase, true, radio, false, receivers);

    private static VoicePlayerSnapshot Player(byte id, bool impostorTeam)
        => new(id, id, "Player", default, false, false, false, impostorTeam, false, false, false, true,
            VoiceControlHearingMode.None, default, -1f, ExternalVoiceState.None, impostorTeam);

    private static VoiceGameStateSnapshot Snapshot(VoiceGamePhase phase, params VoicePlayerSnapshot[] players)
        => new(phase, 0, 1, 1, 1, default, 1f, false, -1, null, players, false,
            phase == VoiceGamePhase.Meeting, 0, 0);
}
