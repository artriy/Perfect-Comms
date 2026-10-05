using System.Runtime.CompilerServices;
using PerfectComms.Api;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceRoleMuteStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApiSpectatorImpostorCannotTransmitSpecialChatOrRadio(bool acrossDeath)
    {
        string modId = $"tests.voice-life.spectator.{Guid.NewGuid():N}";
        var player = (PlayerControl)RuntimeHelpers.GetUninitializedObject(typeof(PlayerControl));
        PerfectCommsApi.RegisterVoicePlayerTraits(modId, _ => VoicePlayerTraits.Spectator);
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            OnlyMeetingOrLobby = true,
            MeetingOnlyImpostorChat = true,
            ImpostorsTalkAcrossDeath = acrossDeath,
            TeamRadio = true,
            TeamRadioImpostors = true,
        };
        VoiceRoomSettingsState.ApplyRemote(settings);
        try
        {
            var traits = VoiceModRegistry.ResolvePlayerTraits(
                player, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
            VoiceRoleMuteState.ResolveVoiceLifeState(
                dataDead: false, roleDead: false, traits, out bool voiceDead, out bool spectator);

            Assert.True(voiceDead);
            Assert.True(spectator);
            Assert.True(VoiceChatHudState.IsLocalRoomPolicyVoiceBlocked(
                VoiceGamePhase.Tasks, voiceDead, localImpostorTeam: true, spectator));
            Assert.False(VoiceImpostorPolicy.CanTransmit(settings, true, voiceDead, spectator));
        }
        finally
        {
            PerfectCommsApi.Unregister(modId);
            VoiceRoomSettingsState.ClearRemote();
        }
    }

    [Theory]
    [InlineData(false, false, VoicePlayerTraits.None, false, false)]
    [InlineData(false, true, VoicePlayerTraits.None, true, true)]
    [InlineData(true, true, VoicePlayerTraits.None, true, false)]
    [InlineData(true, false, VoicePlayerTraits.None, true, false)]
    [InlineData(false, false, VoicePlayerTraits.VoiceDead, true, false)]
    [InlineData(true, true, VoicePlayerTraits.Spectator | VoicePlayerTraits.VoiceDead, true, true)]
    public void BaseDeathAndModTraitsKeepDeadImpostorsDistinctFromListenOnlySpectators(
        bool dataDead, bool roleDead, VoicePlayerTraits traits, bool expectedDead, bool expectedSpectator)
    {
        VoiceRoleMuteState.ResolveVoiceLifeState(dataDead, roleDead, traits,
            out bool voiceDead, out bool spectator);

        Assert.Equal(expectedDead, voiceDead);
        Assert.Equal(expectedSpectator, spectator);
        var settings = VoiceRoomSettingsSnapshot.Defaults with { ImpostorsTalkAcrossDeath = true };
        Assert.Equal(!expectedSpectator,
            VoiceImpostorPolicy.CanTransmit(settings, true, voiceDead, spectator));
    }

    [Theory]
    [InlineData(true, false, false, true, true, true)]
    [InlineData(true, true, false, true, true, true)]
    [InlineData(true, false, true, true, true, false)]
    [InlineData(true, true, true, true, true, false)]
    [InlineData(false, false, false, true, true, false)]
    [InlineData(false, true, false, true, true, false)]
    [InlineData(true, false, false, false, true, false)]
    [InlineData(true, true, false, true, false, false)]
    public void MeetingAcrossDeathRadioRequiresActualImpostorAndBothOptions(
        bool actualImpostor, bool dead, bool spectator,
        bool acrossDeath, bool inMeetings, bool expected)
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            TeamRadio = false,
            TeamRadioImpostors = false,
            TeamRadioInMeetings = false,
            MeetingOnlyImpostorChat = false,
            ImpostorsTalkAcrossDeath = acrossDeath,
            ImpostorsTalkAcrossDeathInMeetings = inMeetings,
        };

        Assert.Equal(expected, VoiceRoleMuteState.CanUseTeamRadioChannel(
            settings, VoiceGamePhase.Meeting, actualImpostor, dead, spectator,
            VoiceTeamRadioChannel.Impostors));
        Assert.Equal(!expected, VoiceChatHudState.TeamRadioBlockedByMeetingPolicy(
            settings, VoiceGamePhase.Meeting, VoiceTeamRadioChannel.Impostors,
            actualImpostor, dead, spectator));
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Meeting, false, false, false, true, false)]
    [InlineData((int)VoiceGamePhase.Meeting, true, false, false, true, false)]
    [InlineData((int)VoiceGamePhase.Meeting, false, true, false, true, false)]
    [InlineData((int)VoiceGamePhase.Exile, false, false, false, false, true)]
    [InlineData((int)VoiceGamePhase.Tasks, false, false, false, false, false)]
    [InlineData((int)VoiceGamePhase.Exile, true, true, true, true, false)]
    [InlineData((int)VoiceGamePhase.Exile, true, false, true, false, false)]
    [InlineData((int)VoiceGamePhase.Tasks, true, true, false, true, false)]
    [InlineData((int)VoiceGamePhase.Tasks, true, false, false, false, false)]
    [InlineData((int)VoiceGamePhase.Tasks, true, true, true, true, true)]
    public void MeetingRadioOverrideDoesNotReplaceTaskOrExileGates(
        int phaseValue, bool teamRadio, bool impostorRadio, bool ordinaryMeetingRadio,
        bool expectedEligible, bool expectedPhaseBlocked)
    {
        var phase = (VoiceGamePhase)phaseValue;
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            TeamRadio = teamRadio,
            TeamRadioImpostors = impostorRadio,
            TeamRadioInMeetings = ordinaryMeetingRadio,
            TeamRadioInTasks = false,
            ImpostorsTalkAcrossDeath = true,
            ImpostorsTalkAcrossDeathInMeetings = true,
        };

        Assert.Equal(expectedEligible, VoiceRoleMuteState.CanUseTeamRadioChannel(
            settings, phase, true, true, false, VoiceTeamRadioChannel.Impostors));
        Assert.Equal(expectedPhaseBlocked, VoiceChatHudState.TeamRadioBlockedByMeetingPolicy(
            settings, phase, VoiceTeamRadioChannel.Impostors, true, true, false));
    }

    [Theory]
    [InlineData((int)VoiceTeamRadioChannel.Impostors, true, false, false, false, false)]
    [InlineData((int)VoiceTeamRadioChannel.External, true, false, false, false, true)]
    [InlineData((int)VoiceTeamRadioChannel.All, true, false, false, false, true)]
    [InlineData((int)VoiceTeamRadioChannel.None, true, false, false, false, true)]
    [InlineData((int)VoiceTeamRadioChannel.Impostors, false, true, false, false, true)]
    [InlineData((int)VoiceTeamRadioChannel.Impostors, true, true, true, false, true)]
    [InlineData((int)VoiceTeamRadioChannel.External, true, false, false, true, false)]
    [InlineData((int)VoiceTeamRadioChannel.External, false, false, false, true, false)]
    public void MeetingExceptionCannotAuthorizeManagedChannelsOrListenOnlyPlayers(
        int channel, bool actualImpostor, bool dead, bool spectator,
        bool ordinaryMeetingRadio, bool expectedBlocked)
    {
        var settings = VoiceRoomSettingsSnapshot.Defaults with
        {
            TeamRadio = true,
            TeamRadioImpostors = false,
            TeamRadioInMeetings = ordinaryMeetingRadio,
            ImpostorsTalkAcrossDeath = true,
            ImpostorsTalkAcrossDeathInMeetings = true,
        };

        Assert.Equal(expectedBlocked, VoiceChatHudState.TeamRadioBlockedByMeetingPolicy(
            settings, VoiceGamePhase.Meeting, (VoiceTeamRadioChannel)channel,
            actualImpostor, dead, spectator));
    }
}
