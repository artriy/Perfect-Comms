using System;
using PerfectComms.Api;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class ImpostorCommunicationPolicyTests : IDisposable
{
    public void Dispose() => VoiceRoomSettingsState.ClearRemote();

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public void DeadImpostorTransmissionRequiresAcrossDeathButGhostListeningDoesNot(bool acrossDeath, bool speakerDead, bool listenerDead, bool allowed)
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            OnlyMeetingOrLobby = true, MeetingOnlyImpostorChat = true,
            ImpostorsTalkAcrossDeath = acrossDeath,
        };
        var speaker = Player(1, true, speakerDead);
        var listener = Player(2, true, listenerDead);
        VoiceRoomSettingsState.ApplyRemote(settings);
        Assert.Equal(allowed, Task(listener, speaker).Audible);
        settings = settings with { OnlyMeetingOrLobby = false, MeetingOnlyImpostorChat = false };
        VoiceRoomSettingsState.ApplyRemote(settings);
        Assert.Equal(allowed, Task(listener, speaker, true).Audible);
    }

    [Theory]
    [InlineData(VoicePairRouteShape.Proximity, 0f)]
    [InlineData(VoicePairRouteShape.Ghost, 0f)]
    [InlineData(VoicePairRouteShape.Radio, 0f)]
    [InlineData(VoicePairRouteShape.Proximity, 0.35f)]
    [InlineData(VoicePairRouteShape.Ghost, 0.35f)]
    [InlineData(VoicePairRouteShape.Radio, 0.35f)]
    public void SpecialChatPreservesIncomingPairRouteGain(VoicePairRouteShape shape, float volume)
    {
        VoiceRoomSettingsState.ApplyRemote(VoiceRoomSettingsSnapshot.Defaults with
        {
            OnlyMeetingOrLobby = true, MeetingOnlyImpostorChat = true,
        });
        var speaker = Player(1, true, false) with
        {
            External = ExternalVoiceState.None with
            {
                Pair = ExternalVoicePairState.None with
                {
                    Verdict = VoicePairVerdict.Route, Shape = (int)shape, Volume = volume,
                },
            },
        };
        var result = VoiceProximityCalculator.ApplyExternalAudioEffects(
            Task(Player(2, true, false), speaker), speaker, VoiceGamePhase.Tasks);
        Assert.Equal(volume > 0f, result.Audible);
        Assert.Equal(shape == VoicePairRouteShape.Proximity ? volume : 0f, result.NormalVolume);
        Assert.Equal(shape == VoicePairRouteShape.Ghost ? volume : 0f, result.GhostVolume);
        Assert.Equal(shape == VoicePairRouteShape.Radio ? volume : 0f, result.RadioVolume);
    }

    [Fact]
    public void ModVoicePrivilegesCannotGrantCrewTransmissionOrLivingCrewAccess()
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            OnlyMeetingOrLobby = true, MeetingOnlyImpostorChat = true,
            ImpostorsTalkAcrossDeath = true, GhostsHearEachOtherUnlimited = true,
        };
        VoiceRoomSettingsState.ApplyRemote(settings);
        var distantPosition = default(Vector2);
        distantPosition.x = 1000f;
        var impostor = Player(1, true, false) with { Position = distantPosition };
        var crew = Player(2, false, false) with
        {
            IsImpostor = true,
            External = ExternalVoiceState.None with
            {
                ListenerBypassTaskVoiceGates = true,
                ManagedRadioChannels = [new("lovers:pair", "Lovers", "L")],
                Channels = [new("vampire:team", true, (int)VoicePairRouteShape.Radio, 1f, false, default)],
                Pair = ExternalVoicePairState.None with
                {
                    Verdict = VoicePairVerdict.Route, Shape = (int)VoicePairRouteShape.Radio, Volume = 1f,
                },
            },
        };
        foreach (var outsider in new[] { crew, crew with { IsDead = true }, impostor with { IsSpectator = true } })
        {
            bool canListen = outsider.IsDead || outsider.IsSpectator;
            Assert.Equal(canListen, Task(outsider, impostor, true).Audible);
            Assert.False(Task(impostor, outsider, true).Audible);
            Assert.True(VoiceChatHudState.IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase.Tasks,
                outsider.IsDead, outsider.IsImpostorTeam, outsider.IsSpectator));
        }
        Assert.True(Task(impostor, Player(3, true, false), true).Audible);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void CrewGhostsAndSpectatorsListenWithoutGainingAnImpostorMicrophone(
        bool acrossDeath, bool speakerDead, bool allowed)
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            OnlyMeetingOrLobby = true, MeetingOnlyImpostorChat = true,
            OnlyMeetingOrLobbyAffectsGhosts = true, OnlyGhostsCanTalk = true,
            ImpostorsTalkAcrossDeath = acrossDeath,
        };
        var speaker = Player(1, true, speakerDead);
        var listeners = new[]
        {
            Player(2, false, true) with { IsVisible = false },
            Player(3, false, false) with { IsSpectator = true, IsVisible = false },
        };
        VoiceRoomSettingsState.ApplyRemote(settings);
        foreach (var listener in listeners)
        {
            Assert.Equal(allowed, Task(listener, speaker).Audible);
            Assert.False(Task(speaker, listener).Audible);
            Assert.True(VoiceChatHudState.IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase.Tasks,
                listener.IsDead, listener.IsImpostorTeam, listener.IsSpectator));
        }

        settings = settings with
        {
            OnlyMeetingOrLobby = false, MeetingOnlyImpostorChat = false, OnlyGhostsCanTalk = false,
        };
        VoiceRoomSettingsState.ApplyRemote(settings);
        foreach (var listener in listeners)
        {
            Assert.Equal(allowed, Task(listener, speaker, true).Audible);
            Assert.Equal(allowed, VoiceProximityCalculator.CanReceiveRadioState(settings, VoiceGamePhase.Tasks,
                speaker, listener, VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors)));
            Assert.False(Task(speaker, listener, true).Audible);
        }
    }

    [Fact]
    public void MeetingRestoresCrewVoiceWithoutAllowingLivingPlayersToHearDeadImpostors()
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            OnlyMeetingOrLobby = true, MeetingOnlyImpostorChat = true, ImpostorsTalkAcrossDeath = true,
            ImpostorsTalkAcrossDeathInMeetings = false,
        };
        VoiceRoomSettingsState.ApplyRemote(settings);
        var crew = Player(1, false, false);
        var impostor = Player(2, true, false);
        var deadImpostor = impostor with { IsDead = true };
        Assert.True(VoiceProximityCalculator.CalculateMeeting(crew, impostor, false, VoiceGamePhase.Meeting).Audible);
        Assert.False(VoiceProximityCalculator.CalculateMeeting(crew, deadImpostor, false, VoiceGamePhase.Meeting).Audible);
        settings = settings with { TeamRadioInMeetings = true };
        VoiceRoomSettingsState.ApplyRemote(settings);
        Assert.False(VoiceProximityCalculator.CalculateMeeting(impostor, deadImpostor, true,
            VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
        Assert.False(VoiceProximityCalculator.CanReceiveRadioState(settings, VoiceGamePhase.Meeting,
            deadImpostor, impostor, VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors)));
        var ghostListener = crew with { IsDead = true, IsVisible = false };
        Assert.True(VoiceProximityCalculator.CalculateMeeting(ghostListener, impostor, true,
            VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
        Assert.True(VoiceProximityCalculator.CanReceiveRadioState(settings, VoiceGamePhase.Meeting,
            impostor, ghostListener, VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors)));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void MeetingRadioCrossesDeathWithoutOrdinaryRadio(bool speakerDead, bool listenerDead, bool teamRadio)
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            ImpostorsTalkAcrossDeath = true, TeamRadio = teamRadio, TeamRadioImpostors = false,
            TeamRadioInMeetings = false, TeamRadioInTasks = false,
        };
        VoiceRoomSettingsState.ApplyRemote(settings);
        var speaker = Player(1, true, speakerDead);
        var listener = Player(2, true, listenerDead);
        var result = VoiceProximityCalculator.CalculateMeeting(listener, speaker, true,
            VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors);
        Assert.True(result.Audible);
        Assert.Equal(1f, result.RadioVolume);
        Assert.Equal(0f, result.NormalVolume);
        Assert.True(VoiceProximityCalculator.CanReceiveRadioState(settings, VoiceGamePhase.Meeting,
            speaker, listener, VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MeetingOverrideRequiresBothToggles(bool acrossDeath, bool inMeetings)
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            ImpostorsTalkAcrossDeath = acrossDeath, ImpostorsTalkAcrossDeathInMeetings = inMeetings,
            TeamRadio = false, TeamRadioInMeetings = false,
        };
        VoiceRoomSettingsState.ApplyRemote(settings);
        var speaker = Player(1, true, true);
        var listener = Player(2, true, false);
        Assert.Equal(acrossDeath && inMeetings, VoiceProximityCalculator.CalculateMeeting(
            listener, speaker, true, VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
        Assert.Equal(acrossDeath && inMeetings, VoiceProximityCalculator.CanReceiveRadioState(
            settings, VoiceGamePhase.Meeting, speaker, listener,
            VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeetingRadioLetsGhostsListenButRejectsCrewAndSpectatorTransmitters(bool speakerDead)
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            ImpostorsTalkAcrossDeath = true, TeamRadio = false,
        };
        VoiceRoomSettingsState.ApplyRemote(settings);
        var speaker = Player(1, true, speakerDead);
        var privilegedCrew = Player(2, false, false) with
        {
            IsImpostor = true,
            External = ExternalVoiceState.None with
            {
                ListenerBypassTaskVoiceGates = true,
                Pair = ExternalVoicePairState.None with
                {
                    Verdict = VoicePairVerdict.Route, Shape = (int)VoicePairRouteShape.Radio, Volume = 1f,
                },
            },
        };
        var radio = VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors);
        foreach (var listener in new[]
        {
            privilegedCrew,
            privilegedCrew with { IsDead = true, IsVisible = false },
            Player(3, false, false) with { IsSpectator = true, IsVisible = false },
            Player(4, true, false) with { IsSpectator = true },
        })
        {
            bool allowed = listener.IsDead || listener.IsSpectator;
            Assert.Equal(allowed, VoiceProximityCalculator.CalculateMeeting(listener, speaker, true,
                VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
            Assert.Equal(allowed, VoiceProximityCalculator.CanReceiveRadioState(
                settings, VoiceGamePhase.Meeting, speaker, listener, radio));
            Assert.False(VoiceProximityCalculator.CalculateMeeting(speaker, listener, true,
                VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
            Assert.False(VoiceProximityCalculator.CanReceiveRadioState(
                settings, VoiceGamePhase.Meeting, listener, speaker, radio));
        }
    }

    [Fact]
    public void MeetingRadioReleaseRestoresPublicLivingSpeechAndDeadSeparation()
    {
        VoiceRoomSettingsState.ApplyRemote(VoiceRoomSettingsSnapshot.Defaults with
        {
            ImpostorsTalkAcrossDeath = true, TeamRadio = false,
        });
        var speaker = Player(1, true, false);
        var crew = Player(2, false, false);
        var impostor = Player(3, true, false);
        Assert.False(VoiceProximityCalculator.CalculateMeeting(crew, speaker, true,
            VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
        Assert.True(VoiceProximityCalculator.CalculateMeeting(crew, speaker, false,
            VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
        var dead = speaker with { IsDead = true };
        Assert.True(VoiceProximityCalculator.CalculateMeeting(impostor, dead, true,
            VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
        Assert.False(VoiceProximityCalculator.CalculateMeeting(impostor, dead, false,
            VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors).Audible);
    }

    [Theory]
    [InlineData(VoicePairRouteShape.Proximity, 0f)]
    [InlineData(VoicePairRouteShape.Ghost, 0.35f)]
    [InlineData(VoicePairRouteShape.Radio, 0.35f)]
    public void MeetingOverridePreservesPairRouteGainAndMute(VoicePairRouteShape shape, float volume)
    {
        VoiceRoomSettingsState.ApplyRemote(VoiceRoomSettingsSnapshot.Defaults with
        {
            ImpostorsTalkAcrossDeath = true, TeamRadio = false,
        });
        var speaker = Player(1, true, true) with
        {
            External = ExternalVoiceState.None with
            {
                Pair = ExternalVoicePairState.None with
                {
                    Verdict = VoicePairVerdict.Route, Shape = (int)shape, Volume = volume,
                },
            },
        };
        var listener = Player(2, true, false);
        var result = VoiceProximityCalculator.ApplyExternalAudioEffects(
            VoiceProximityCalculator.CalculateMeeting(listener, speaker, true,
                VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors), speaker, VoiceGamePhase.Meeting);
        Assert.Equal(volume > 0f, result.Audible);
        Assert.Equal(shape == VoicePairRouteShape.Proximity ? volume : 0f, result.NormalVolume);
        Assert.Equal(shape == VoicePairRouteShape.Ghost ? volume : 0f, result.GhostVolume);
        Assert.Equal(shape == VoicePairRouteShape.Radio ? volume : 0f, result.RadioVolume);
        speaker = speaker with { External = speaker.External with { Muted = true } };
        Assert.False(VoiceProximityCalculator.CanReceiveRadioState(VoiceRoomSettingsState.Current,
            VoiceGamePhase.Meeting, speaker, listener, VoiceRadioState.BuiltIn(VoiceTeamRadioChannel.Impostors)));
        Assert.False(VoiceProximityCalculator.ApplyExternalAudioEffects(
            VoiceProximityCalculator.CalculateMeeting(listener, speaker, true,
                VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors), speaker, VoiceGamePhase.Meeting).Audible);
    }

    [Fact]
    public void CallerFloorStartsWhenMeetingIsReadyExpiresAndClearsAtTransitions()
    {
        var floor = new MeetingVoiceFloor();
        var settings = VoiceRoomSettingsSnapshot.Defaults with { GracePeriodEnabled = true, GracePeriodSeconds = 5f };
        floor.Begin(1, settings);
        floor.Update(VoiceGamePhase.Tasks, false, true, 10f);
        floor.Update(VoiceGamePhase.Meeting, false, true, 20f);
        Assert.False(floor.Active(20f));
        floor.Update(VoiceGamePhase.Meeting, true, true, 30f);
        Assert.False(floor.Blocks(1, false, VoiceGamePhase.Meeting, 31f));
        Assert.True(floor.Blocks(2, false, VoiceGamePhase.Meeting, 31f));
        Assert.False(floor.Blocks(2, true, VoiceGamePhase.Meeting, 31f));
        Assert.Equal(4, floor.SecondsRemaining(31f));
        floor.Update(VoiceGamePhase.Meeting, true, true, 34f);
        Assert.False(floor.Blocks(2, false, VoiceGamePhase.Meeting, 35f));
        floor.Update(VoiceGamePhase.Tasks, false, true, 36f);
        Assert.Equal(byte.MaxValue, floor.CallerId);
        floor.Begin(3, settings);
        floor.Update(VoiceGamePhase.Meeting, true, true, 40f);
        floor.Update(VoiceGamePhase.Exile, false, true, 41f);
        Assert.False(floor.Active(41f));
        floor.Begin(4, settings);
        floor.Update(VoiceGamePhase.Meeting, true, false, 42f);
        Assert.Equal(byte.MaxValue, floor.CallerId);
    }

    private static VoicePlayerSnapshot Player(byte id, bool impostorTeam, bool dead)
        => new(id, id, "Player", default, false, dead, false, impostorTeam, false, false, false, true,
            VoiceControlHearingMode.None, default, -1f, ExternalVoiceState.None, impostorTeam);

    private static VoiceProximityResult Task(VoicePlayerSnapshot listener, VoicePlayerSnapshot speaker, bool radio = false)
        => VoiceProximityCalculator.CalculateTaskPhase(listener, speaker, default(Vector2), 1f, 0,
            false, -1, null, Array.Empty<VoiceChatRoom.SpeakerCache>(), Array.Empty<IVoiceComponent>(),
            false, radio, false, 1f, VoiceTeamRadioChannel.Impostors);
}
