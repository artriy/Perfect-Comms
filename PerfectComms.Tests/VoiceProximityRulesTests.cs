using System;
using UnityEngine;
using VoiceChatPlugin.Audio;
using VoiceChatPlugin.VoiceChat;
using Xunit;

// These are behavioral ports of the current lobby/rule cases from the old console harness.
// They deliberately exercise the production calculator instead of matching source-code strings.
public sealed class VoiceProximityRulesTests : IDisposable
{
    public VoiceProximityRulesTests()
    {
        VoiceProximityCalculator.ResetSightState();
        VoiceRoomSettingsState.ApplyRemote(BaseSettings());
    }

    public void Dispose()
    {
        VoiceRoomSettingsState.ClearRemote();
        VoiceProximityCalculator.ResetSightState();
    }

    [Fact]
    public void PublicLobbyProtocolRejectsTheRetiredTransport()
    {
        Assert.Equal(5, VoiceProtocol.ProtocolVersion);
        Assert.Equal(5, VoiceProtocol.MinCompatibleVersion);
        Assert.True(VoiceProtocol.IsCompatible(5, 5));
        Assert.False(VoiceProtocol.IsCompatible(3, 3));
        Assert.False(VoiceProtocol.IsCompatible(4, 4));
    }

    [Fact]
    public void UnavailableTargetsAreMutedInEveryPhase()
    {
        var local = Player(0, 0f, isLocal: true);
        var targets = new[]
        {
            Player(1, 1f, disconnected: true),
            Player(2, 1f, isDummy: true),
            Player(3, 1f, isVisible: false),
        };

        foreach (var target in targets)
        {
            AssertMutedUnavailable(VoiceProximityCalculator.CalculateLobby(target, local.Position));
            AssertMutedUnavailable(VoiceProximityCalculator.CalculateMeeting(local, target, true));
            AssertMutedUnavailable(Task(local, target, targetRadioActive: true));
        }
    }

    [Fact]
    public void DistanceMeetingOnlyAndGhostRulesRemainAuthoritative()
    {
        var crew = Player(0, 0f, isLocal: true);
        var ghost = Player(1, 0f, isLocal: true, isDead: true);
        var near = Player(2, 1f);
        var far = Player(3, 5f);
        var otherGhost = Player(4, 1f, isDead: true);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { MaxChatDistance = 2f });
        Assert.True(Task(crew, near).Audible);
        Assert.False(Task(crew, far).Audible);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { OnlyMeetingOrLobby = true });
        Assert.Equal(VoiceProximityReason.OnlyMeetingOrLobby, Task(crew, near).Reason);
        Assert.Equal(VoiceProximityReason.LocalDeadHearsGhost, Task(ghost, otherGhost).Reason);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            OnlyMeetingOrLobby = true,
            OnlyMeetingOrLobbyAffectsGhosts = true,
        });
        Assert.Equal(VoiceProximityReason.OnlyMeetingOrLobby, Task(ghost, otherGhost).Reason);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { OnlyGhostsCanTalk = true });
        Assert.Equal(VoiceProximityReason.OnlyGhostsCanTalk, Task(crew, near).Reason);
        Assert.True(Task(ghost, otherGhost).Audible);
    }

    [Fact]
    public void TeamRadioIsPrivateAndHonorsMeetingAndTaskToggles()
    {
        var localImp = Player(0, 0f, isLocal: true, isImpostor: true);
        var localCrew = Player(1, 0f, isLocal: true);
        var remoteImp = Player(2, 1f, isImpostor: true);
        var remoteCrew = Player(3, 1f);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { TeamRadio = true });
        Assert.Equal(VoiceProximityReason.TeamRadio, Task(localImp, remoteImp, true).Reason);
        Assert.Equal(VoiceProximityReason.TeamRadioMuted, Task(localCrew, remoteImp, true).Reason);
        Assert.Equal(VoiceProximityReason.TeamRadioMuted, Task(localImp, remoteCrew, true).Reason);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            TeamRadio = true,
            TeamRadioInMeetings = false,
        });
        var normalMeeting = VoiceProximityCalculator.CalculateMeeting(localImp, remoteImp, true);
        Assert.Equal(VoiceProximityReason.MeetingLiving, normalMeeting.Reason);
        Assert.Equal(0f, normalMeeting.RadioVolume);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            TeamRadio = true,
            TeamRadioInMeetings = true,
            TeamRadioInTasks = false,
        });
        Assert.Equal(VoiceProximityReason.TeamRadio, VoiceProximityCalculator.CalculateMeeting(localImp, remoteImp, true).Reason);
        Assert.NotEqual(VoiceProximityReason.TeamRadio, Task(localImp, remoteImp, true).Reason);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            TeamRadio = true,
            TeamRadioInMeetings = true,
            TeamRadioInTasks = true,
        });
        Assert.Equal(VoiceProximityReason.TeamRadio, Task(localImp, remoteImp, true).Reason);
    }

    [Fact]
    public void DeadAndSpectatorHearingMatrixIsPreserved()
    {
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { ImpostorHearGhosts = true });
        var living = Player(0, 0f, isLocal: true);
        var impostor = Player(1, 0f, isLocal: true, isImpostor: true);
        var spectator = Player(2, 0f, isLocal: true, isDead: true, isSpectator: true);
        var ghostTarget = Player(3, 1f, isDead: true);
        var spectatorTarget = Player(4, 1f, isDead: true, isSpectator: true);
        var livingTarget = Player(5, 1f);

        Assert.False(Task(living, spectatorTarget).Audible);
        Assert.Equal(VoiceProximityReason.ImpostorHearsGhost, Task(impostor, ghostTarget).Reason);
        Assert.False(Task(impostor, spectatorTarget).Audible);
        Assert.True(Task(spectator, ghostTarget).Audible);
        Assert.True(Task(spectator, spectatorTarget).Audible);
        var hearsLiving = Task(spectator, livingTarget);
        Assert.True(hearsLiving.Audible);
        Assert.Equal(VoiceProximityReason.LocalDeadHearsLiving, hearsLiving.Reason);
        Assert.Equal(0f, hearsLiving.GhostVolume);
    }

    [Fact]
    public void DeadListenersKeepVisionHearingRadiusWithoutWorldOcclusion()
    {
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            MaxChatDistance = 10f,
            FalloffMode = (int)VoiceFalloffMode.Linear,
            OcclusionMode = (int)VoiceOcclusionMode.HardBlock,
            WallsBlockSound = true,
            OnlyHearInSight = true,
        });
        var ghost = Player(0, 0f, isLocal: true, isDead: true);
        var insideVisionRadius = Player(1, 0f) with { Position = Vector(3f, 4f) };
        var outsideVisionRadius = Player(2, 0f) with { Position = Vector(0f, 7f) };

        var heard = Task(
            ghost,
            insideVisionRadius,
            localLightRadius: 6f,
            previousWallCoefficient: 0.25f);

        Assert.True(heard.Audible);
        Assert.Equal(VoiceProximityReason.LocalDeadHearsLiving, heard.Reason);
        Assert.InRange(heard.NormalVolume, 0.1666f, 0.1667f);
        Assert.Equal(0f, heard.GhostVolume);
        Assert.Equal(VoiceAudioFilterMode.None, heard.FilterMode);
        Assert.Equal(1f, heard.WallCoefficient);

        var beyondVisionRadius = Task(
            ghost,
            outsideVisionRadius,
            localLightRadius: 6f,
            previousWallCoefficient: 0.25f);

        Assert.False(beyondVisionRadius.Audible);
        Assert.Equal(VoiceProximityReason.SightBlocked, beyondVisionRadius.Reason);
        Assert.Equal(0f, beyondVisionRadius.NormalVolume);
        Assert.Equal(1f, beyondVisionRadius.WallCoefficient);
    }

    [Fact]
    public void DeadListenerWorldOcclusionBypassPreservesTaskGates()
    {
        var ghost = Player(0, 0f, isLocal: true, isDead: true);
        var living = Player(1, 1f);
        var ventedLiving = Player(2, 1f, inVent: true);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            OnlyGhostsCanTalk = true,
            OcclusionMode = (int)VoiceOcclusionMode.HardBlock,
            WallsBlockSound = true,
            OnlyHearInSight = true,
        });
        Assert.Equal(VoiceProximityReason.OnlyGhostsCanTalk, Task(ghost, living).Reason);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            VentPrivateChat = true,
            OcclusionMode = (int)VoiceOcclusionMode.HardBlock,
            WallsBlockSound = true,
            OnlyHearInSight = true,
        });
        Assert.Equal(VoiceProximityReason.VentPrivateMuted, Task(ghost, ventedLiving).Reason);
    }

    [Fact]
    public void CommsAndVentRulesAreAppliedBeforeProximity()
    {
        var local = Player(0, 0f, isLocal: true);
        var nearby = Player(1, 1f);
        var ventImp = Player(2, 1f, isImpostor: true, inVent: true);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { CommsSabDisables = true });
        Assert.Equal(VoiceProximityReason.CommsSabotage, Task(local, nearby, commsSabActive: true).Reason);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { HearInVent = false });
        Assert.Equal(VoiceProximityReason.VentMuted, Task(local, ventImp).Reason);
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { HearInVent = true });
        Assert.True(Task(local, ventImp).Audible);
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { HearInVent = true, VentPrivateChat = true });
        Assert.Equal(VoiceProximityReason.VentPrivateMuted, Task(local, ventImp).Reason);
        Assert.True(Task(local, ventImp, localInVent: true).Audible);
    }


    [Fact]
    public void LobbyMeetingAndEndGameRemainGlobalVoicePhases()
    {
        var local = Player(0, 0f, isLocal: true);
        var target = Player(1, 1f);

        Assert.Equal(VoiceProximityReason.Lobby,
            VoiceProximityCalculator.CalculateLobby(target, local.Position).Reason);
        Assert.Equal(VoiceProximityReason.MeetingLiving,
            VoiceProximityCalculator.CalculateMeeting(local, target, false).Reason);
        var end = VoiceProximityCalculator.CalculateEndGame();
        Assert.True(end.Audible);
        Assert.True(end.NormalVolume > 0f);
    }

    private static VoiceProximityResult Task(
        VoicePlayerSnapshot local,
        VoicePlayerSnapshot target,
        bool targetRadioActive = false,
        bool localInVent = false,
        bool commsSabActive = false,
        float localLightRadius = -1f,
        float previousWallCoefficient = 1f)
        => VoiceProximityCalculator.CalculateTaskPhase(
            local,
            target,
            local.Position,
            localLightRadius,
            0,
            false,
            -1,
            null,
            Array.Empty<VoiceChatRoom.SpeakerCache>(),
            Array.Empty<IVoiceComponent>(),
            localInVent,
            targetRadioActive,
            commsSabActive,
            previousWallCoefficient);

    private static void AssertMutedUnavailable(VoiceProximityResult result)
    {
        Assert.False(result.Audible);
        Assert.Equal(VoiceProximityReason.TargetUnavailable, result.Reason);
    }

    private static VoiceRoomSettingsSnapshot BaseSettings()
        => VoiceRoomSettingsSnapshot.Defaults with
        {
            MaxChatDistance = 6f,
            FalloffMode = (int)VoiceFalloffMode.Linear,
            OcclusionMode = (int)VoiceOcclusionMode.Off,
            WallsBlockSound = false,
            OnlyHearInSight = false,
            ImpostorHearGhosts = false,
            HearInVent = false,
            VentPrivateChat = false,
            CommsSabDisables = false,
            CameraCanHear = false,
            TeamRadio = false,
            TeamRadioImpostors = true,
            TeamRadioInMeetings = false,
            TeamRadioInTasks = true,
            OnlyGhostsCanTalk = false,
            OnlyMeetingOrLobby = false,
            OnlyMeetingOrLobbyAffectsGhosts = false,
        };


    private static VoicePlayerSnapshot Player(
        byte id,
        float x,
        bool isLocal = false,
        bool isDead = false,
        bool isSpectator = false,
        bool isImpostor = false,
        bool inVent = false,
        bool disconnected = false,
        bool isDummy = false,
        bool isVisible = true)
        => new(
            id,
            100 + id,
            $"Player {id}",
            Vector(x, 0f),
            isLocal,
            isDead,
            isSpectator,
            isImpostor,
            InVent: inVent,
            Disconnected: disconnected,
            IsDummy: isDummy,
            IsVisible: isVisible,
            ControlHearingMode: VoiceControlHearingMode.None,
            ControlledVictimPosition: default,
            ControlledVictimLightRadius: -1f,
            External: ExternalVoiceState.None);

    private static Vector2 Vector(float x, float y)
    {
        var value = default(Vector2);
        value.x = x;
        value.y = y;
        return value;
    }
}
