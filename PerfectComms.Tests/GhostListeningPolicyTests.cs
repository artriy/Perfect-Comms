using System;
using System.Collections.Generic;
using PerfectComms.Api;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class GhostListeningPolicyTests : IDisposable
{
    private static VoiceRoomSettingsSnapshot Settings => VoiceRoomSettingsSnapshot.Defaults with
    {
        GhostsHearLivingDuringTasks = true,
        OnlyMeetingOrLobby = true,
        MaxChatDistance = 6f,
        FalloffMode = (int)VoiceFalloffMode.Linear,
        OnlyHearInSight = false,
        WallsBlockSound = false,
        VentPrivateChat = true,
        TeamRadio = false,
    };

    public void Dispose() => VoiceRoomSettingsState.ClearRemote();

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, true)]
    public void ExceptionAppliesOnlyWhileLivingTaskVoiceIsRestrictedAndGhostsAreNot(
        bool meetingOnly, bool onlyGhosts, bool ghostsAlsoMeetingOnly, bool expected)
    {
        Assert.Equal(expected, VoiceGhostListeningPolicy.CanApply(meetingOnly, onlyGhosts, ghostsAlsoMeetingOnly));
        var settings = Settings with
        {
            OnlyMeetingOrLobby = meetingOnly,
            OnlyGhostsCanTalk = onlyGhosts,
            OnlyMeetingOrLobbyAffectsGhosts = ghostsAlsoMeetingOnly,
        };
        Assert.Equal(expected, VoiceGhostListeningPolicy.Enabled(settings));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RestrictedLivingSpeechReachesOnlyNearbyGhosts(bool meetingOnly, bool onlyGhosts)
    {
        var settings = Settings with { OnlyMeetingOrLobby = meetingOnly, OnlyGhostsCanTalk = onlyGhosts };
        VoiceRoomSettingsState.ApplyRemote(settings);
        var speaker = Player(1);
        var ghost = Player(2, dead: true);
        var near = Task(ghost, speaker);
        Assert.True(near.Audible);
        Assert.Equal(VoiceProximityReason.LocalDeadHearsLiving, near.Reason);
        Assert.InRange(near.NormalVolume, 0.99f, 1f);
        Assert.False(Task(Player(3), speaker).Audible);
        Assert.False(Task(Player(3), ghost).Audible);
        Assert.False(Task(ghost, speaker with { Position = Position(20f) }).Audible);
        settings = settings with { GhostsHearLivingDuringTasks = false };
        VoiceRoomSettingsState.ApplyRemote(settings);
        Assert.False(Task(ghost, speaker).Audible);
    }

    [Fact]
    public void ExplicitGhostMeetingRestrictionStillWins()
    {
        var settings = Settings with { OnlyMeetingOrLobbyAffectsGhosts = true };
        VoiceRoomSettingsState.ApplyRemote(settings);
        Assert.False(Task(Player(2, dead: true), Player(1)).Audible);
        Assert.True(VoiceChatHudState.IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase.Tasks, false, false, false));
        var recipients = new List<int>();
        Collect(Snapshot(Player(1) with { IsLocal = true }, Player(2, dead: true)), settings, recipients);
        Assert.Empty(recipients);
    }

    [Fact]
    public void ExceptionRetainsVentAndHostRoleAndPairMutes()
    {
        VoiceRoomSettingsState.ApplyRemote(Settings);
        var ghost = Player(2, dead: true);
        var speaker = Player(1);
        Assert.Equal(VoiceProximityReason.VentPrivateMuted, Task(ghost, speaker with { InVent = true }).Reason);
        Assert.Equal(VoiceProximityReason.RoleMuted,
            Task(ghost, speaker with { External = ExternalVoiceState.None with { Muted = true } }).Reason);
        var pairMuted = speaker with
        {
            External = ExternalVoiceState.None with
            {
                Pair = ExternalVoicePairState.None with { Verdict = VoicePairVerdict.Mute },
            },
        };
        Assert.Equal(VoiceProximityReason.RoleMuted, Task(ghost, pairMuted).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GhostOnlyTransportExcludesLivingIncludingModPrivilegedCrew(bool specialChat)
    {
        var settings = Settings with { MeetingOnlyImpostorChat = specialChat };
        var privileged = Player(4) with
        {
            IsImpostor = true,
            External = ExternalVoiceState.None with { ListenerBypassTaskVoiceGates = true },
        };
        var snapshot = Snapshot(Player(1) with { IsLocal = true }, Player(2, dead: true),
            Player(3, impostor: true), privileged,
            Player(5) with { IsSpectator = true, IsVisible = false },
            Player(6, dead: true) with { Disconnected = true }, Player(7, dead: true) with { IsDummy = true });
        var recipients = new List<int>();
        Assert.True(Collect(snapshot, settings, recipients));
        Assert.Equal(new[] { 2, 5 }, recipients);
        VoiceRoomSettingsState.ApplyRemote(settings);
        Assert.True(Task(Player(2, dead: true), Player(1)).Audible);
        Assert.False(Task(privileged, Player(1)).Audible);
    }

    [Fact]
    public void UnknownOrRetainedRostersFailClosedBeforeAnyPrivateTransmission()
    {
        var recipients = new List<int> { 99 };
        Assert.True(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(null, Settings,
            VoiceGamePhase.Tasks, false, VoiceRadioState.None, false, recipients));
        Assert.Empty(recipients);
        var snapshot = Snapshot(Player(1) with { IsLocal = true }, Player(2, dead: true));
        foreach (var untrusted in new[]
        {
            snapshot with { RoutingRosterRetained = true },
            snapshot with { PlayerEnumerationCompleted = false },
            snapshot with { LiveLocalPlayerResolved = false },
            snapshot with { Phase = VoiceGamePhase.Meeting },
            snapshot with { AuthenticatedRosterAvailable = false },
            Snapshot(Player(1) with { IsLocal = true, IsAuthenticatedClient = false }, Player(2, dead: true)),
        })
        {
            Assert.True(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(untrusted, Settings,
                VoiceGamePhase.Tasks, true, VoiceRadioState.None, false, recipients));
            Assert.Empty(recipients);
        }
    }

    [Fact]
    public void RecipientSetTracksDeathRevivalAndOptionCutovers()
    {
        var speaker = Player(1) with { IsLocal = true };
        var first = Player(2, dead: true);
        var second = Player(3);
        var recipients = new List<int>();
        Assert.True(Collect(Snapshot(speaker, first, second), Settings, recipients));
        Assert.Equal(new[] { 2 }, recipients);
        Assert.True(Collect(Snapshot(speaker, first with { IsDead = false }, second with { IsDead = true }), Settings, recipients));
        Assert.Equal(new[] { 3 }, recipients);
        Assert.True(Collect(Snapshot(speaker, first with { IsDead = false }), Settings, recipients));
        Assert.Empty(recipients);
        Assert.False(Collect(Snapshot(speaker, first), Settings with { GhostsHearLivingDuringTasks = false }, recipients));
        Assert.Empty(recipients);
        Assert.False(Collect(Snapshot(speaker with { IsDead = true }, first), Settings, recipients));
        Assert.Empty(recipients);
        var meeting = Snapshot(speaker, first) with { Phase = VoiceGamePhase.Meeting };
        Assert.False(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(meeting, Settings,
            meeting.Phase, true, VoiceRadioState.None, true, recipients));
        Assert.Empty(recipients);
    }

    [Fact]
    public void ExistingImpostorChatAndPrivateRadioAudiencesAreUnchanged()
    {
        var settings = Settings with { MeetingOnlyImpostorChat = true };
        var speaker = Player(1, impostor: true) with { IsLocal = true };
        var snapshot = Snapshot(speaker, Player(2, dead: true), Player(3, impostor: true), Player(4));
        var recipients = new List<int>();
        Assert.True(Collect(snapshot, settings, recipients));
        Assert.Equal(new[] { 2, 3 }, recipients);
        VoiceRoomSettingsState.ApplyRemote(settings);
        Assert.Equal(VoiceProximityReason.ImpostorChat, Task(Player(3, impostor: true), speaker).Reason);
        settings = Settings with { OnlyMeetingOrLobby = false, OnlyGhostsCanTalk = true, TeamRadio = true };
        Assert.True(PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(snapshot, settings,
            snapshot.Phase, true, VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors), false, recipients));
        Assert.Empty(recipients);
        VoiceRoomSettingsState.ApplyRemote(settings);
        var radioWithGhostListening = Task(Player(2, dead: true), speaker, radio: true);
        VoiceRoomSettingsState.ApplyRemote(settings with { GhostsHearLivingDuringTasks = false });
        var radioWithoutGhostListening = Task(Player(2, dead: true), speaker, radio: true);
        Assert.Equal(radioWithGhostListening.Audible, radioWithoutGhostListening.Audible);
        Assert.Equal(radioWithGhostListening.RadioVolume, radioWithoutGhostListening.RadioVolume);
    }

    [Fact]
    public void LivingMicrophoneExceptionEndsWhenToggleTurnsOff()
    {
        VoiceRoomSettingsState.ApplyRemote(Settings);
        Assert.False(VoiceChatHudState.IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase.Tasks, false, false, false));
        VoiceRoomSettingsState.ApplyRemote(Settings with { GhostsHearLivingDuringTasks = false });
        Assert.True(VoiceChatHudState.IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase.Tasks, false, false, false));
    }

    private static bool Collect(VoiceGameStateSnapshot snapshot, VoiceRoomSettingsSnapshot settings, List<int> recipients)
        => PerfectCommsVoiceBackend.CollectPrivateRadioReceivers(snapshot, settings, snapshot.Phase,
            true, VoiceRadioState.None, false, recipients);

    private static VoiceGameStateSnapshot Snapshot(params VoicePlayerSnapshot[] players)
        => new(VoiceGamePhase.Tasks, 0, 1, 1, 1, default(Vector2), 6f, false, -1, null,
            players, false, false, 0, 0, AuthenticatedRosterAvailable: true);

    private static VoicePlayerSnapshot Player(byte id, bool dead = false, bool impostor = false)
        => new(id, id, "Player", default, false, dead, false, impostor, false, false, false, true,
            VoiceControlHearingMode.None, default, -1f, ExternalVoiceState.None, impostor, IsAuthenticatedClient: true);

    private static Vector2 Position(float x)
    {
        var position = default(Vector2);
        position.x = x;
        return position;
    }

    private static VoiceProximityResult Task(VoicePlayerSnapshot listener, VoicePlayerSnapshot speaker, bool radio = false)
        => VoiceProximityCalculator.CalculateTaskPhase(listener, speaker, default(Vector2), 6f, 0,
            false, -1, null, Array.Empty<VoiceChatRoom.SpeakerCache>(), Array.Empty<IVoiceComponent>(),
            false, radio, false, 1f, VoiceTeamRadioChannel.Impostors);
}
