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
        VoiceProximityCalculator.LocalListenerBlindedOrFlashedProvider = () => false;
        VoiceProximityCalculator.ResetSightState();
        VoiceRoomSettingsState.ApplyRemote(BaseSettings());
    }

    public void Dispose()
    {
        VoiceRoomSettingsState.ClearRemote();
        VoiceProximityCalculator.LocalListenerBlindedOrFlashedProvider = null;
        VoiceProximityCalculator.ResetSightState();
    }

    [Fact]
    public void PublicLobbyProtocolRejectsTheRetiredTransport()
    {
        Assert.Equal(4, VoiceProtocol.ProtocolVersion);
        Assert.Equal(4, VoiceProtocol.MinCompatibleVersion);
        Assert.True(VoiceProtocol.IsCompatible(4, 4));
        Assert.False(VoiceProtocol.IsCompatible(3, 3));
        Assert.False(VoiceProtocol.IsCompatible(5, 5));
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
    public void CommsVentAndRoleMuteRulesAreAppliedBeforeProximity()
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

        VoiceRoomSettingsState.ApplyRemote(BaseSettings());
        Assert.Equal(VoiceProximityReason.ParasiteControlled,
            Task(local, Player(3, 1f, isParasiteControlled: true)).Reason);
        Assert.Equal(VoiceProximityReason.PuppeteerControlled,
            Task(local, Player(4, 1f, isPuppeteerControlled: true)).Reason);
        Assert.Equal(VoiceProximityReason.Swooped,
            Task(local, Player(5, 1f, isSwooped: true)).Reason);
        Assert.Equal(VoiceProximityReason.BlackmailedNextRound,
            Task(local, Player(6, 1f, isBlackmailedNextRound: true)).Reason);
    }

    [Fact]
    public void CommunicationsSabotageAudioGatePrecedesEveryAppearanceScenario()
    {
        var local = Player(0, 0f, isLocal: true);
        var speaker = Player(3, 1f);
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { CommsSabDisables = true });

        foreach (BuiltInAppearance appearance in Enum.GetValues(typeof(BuiltInAppearance)))
        {
            for (int camo = 0; camo < 2; camo++)
            {
                var audio = Task(local, speaker, commsSabActive: true);
                var privacy = ResolveBuiltInScenario(
                    VoiceGamePhase.Tasks,
                    touMiraCommsCamouflage: camo == 1,
                    appearance);

                Assert.False(audio.Audible);
                Assert.Equal(VoiceProximityReason.CommsSabotage, audio.Reason);
                Assert.Equal(
                    ExpectedTaskPrivacyDecision(appearance, camo == 1),
                    privacy.Decision);
                Assert.False(audio.Audible && privacy.HasConcretePresentation);
            }
        }
    }

    [Theory]
    [InlineData((int)BuiltInAppearance.Normal, false, (int)VoiceIdentityPrivacyDecision.Normal, 3)]
    [InlineData((int)BuiltInAppearance.MorphOrMimic, false, (int)VoiceIdentityPrivacyDecision.Alias, 9)]
    [InlineData((int)BuiltInAppearance.Venerer, false, (int)VoiceIdentityPrivacyDecision.HideSource, -1)]
    [InlineData((int)BuiltInAppearance.AnonymousCamouflage, false, (int)VoiceIdentityPrivacyDecision.HideSource, -1)]
    [InlineData((int)BuiltInAppearance.MorphWithHiddenStack, false, (int)VoiceIdentityPrivacyDecision.HideSource, -1)]
    [InlineData((int)BuiltInAppearance.Normal, true, (int)VoiceIdentityPrivacyDecision.HideAllForViewer, -1)]
    [InlineData((int)BuiltInAppearance.MorphOrMimic, true, (int)VoiceIdentityPrivacyDecision.HideAllForViewer, -1)]
    [InlineData((int)BuiltInAppearance.Venerer, true, (int)VoiceIdentityPrivacyDecision.HideAllForViewer, -1)]
    [InlineData((int)BuiltInAppearance.AnonymousCamouflage, true, (int)VoiceIdentityPrivacyDecision.HideAllForViewer, -1)]
    [InlineData((int)BuiltInAppearance.MorphWithHiddenStack, true, (int)VoiceIdentityPrivacyDecision.HideAllForViewer, -1)]
    public void TaskCommsAndAppearancePrivacyScenarioMatrix(
        int appearanceValue,
        bool touMiraCommsCamouflage,
        int expectedDecisionValue,
        int expectedPresentationPlayerId)
    {
        var local = Player(0, 0f, isLocal: true);
        var speaker = Player(3, 1f);
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { CommsSabDisables = false });

        // Ordinary communications sabotage leaves voice audible when the PerfectComms audio gate is
        // disabled. TouMira's optional camo-comms flag is a separate viewer-wide privacy input.
        var audio = Task(local, speaker, commsSabActive: true);
        var privacy = ResolveBuiltInScenario(
            VoiceGamePhase.Tasks,
            touMiraCommsCamouflage,
            (BuiltInAppearance)appearanceValue);

        Assert.True(audio.Audible);
        Assert.Equal(VoiceProximityReason.Proximity, audio.Reason);
        Assert.Equal((VoiceIdentityPrivacyDecision)expectedDecisionValue, privacy.Decision);
        Assert.Equal(
            expectedPresentationPlayerId < 0 ? null : (byte)expectedPresentationPlayerId,
            privacy.PresentationPlayerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeetingImmediatelyAfterCommsRebasesEveryBuiltInScenario(bool commsSabDisables)
    {
        var local = Player(0, 0f, isLocal: true);
        var speaker = Player(3, 1f);
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { CommsSabDisables = commsSabDisables });

        var taskAudio = Task(local, speaker, commsSabActive: true);
        Assert.Equal(!commsSabDisables, taskAudio.Audible);
        var meetingAudio = VoiceProximityCalculator.CalculateMeeting(local, speaker, false);
        Assert.True(meetingAudio.Audible);
        Assert.Equal(VoiceProximityReason.MeetingLiving, meetingAudio.Reason);

        foreach (BuiltInAppearance appearance in Enum.GetValues(typeof(BuiltInAppearance)))
        {
            for (int camo = 0; camo < 2; camo++)
            {
                bool touMiraCommsCamouflage = camo == 1;
                var taskPrivacy = ResolveBuiltInScenario(
                    VoiceGamePhase.Tasks,
                    touMiraCommsCamouflage,
                    appearance);
                var meetingPrivacy = ResolveBuiltInScenario(
                    VoiceGamePhase.Meeting,
                    touMiraCommsCamouflage,
                    appearance);

                var gate = new VoiceIdentityPrivacyTransitionGate();
                var epoch = new VoiceIdentityPrivacyPhaseEpoch();
                Assert.False(epoch.Advance(VoiceGamePhase.Tasks));
                if (taskAudio.Audible)
                    gate.Advance(taskPrivacy, isSpeaking: true);

                if (epoch.Advance(VoiceGamePhase.Meeting))
                    gate.Reset();
                var meetingTransition = gate.Advance(meetingPrivacy, isSpeaking: true);

                Assert.Equal(VoiceIdentityPrivacyDecision.Normal, meetingPrivacy.Decision);
                Assert.Equal((byte)3, meetingPrivacy.PresentationPlayerId);
                Assert.Equal(meetingPrivacy, meetingTransition.EffectiveResolution);
                Assert.False(meetingTransition.IsQuarantined);
                Assert.True(meetingAudio.Audible && meetingPrivacy.HasConcretePresentation);
            }
        }
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
        bool commsSabActive = false)
        => VoiceProximityCalculator.CalculateTaskPhase(
            local,
            target,
            local.Position,
            -1f,
            0,
            false,
            -1,
            null,
            Array.Empty<VoiceChatRoom.SpeakerCache>(),
            Array.Empty<IVoiceComponent>(),
            localInVent,
            targetRadioActive,
            commsSabActive,
            1f);

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
            TeamRadioVampires = true,
            TeamRadioLovers = true,
            TeamRadioInMeetings = false,
            TeamRadioInTasks = true,
            OnlyGhostsCanTalk = false,
            OnlyMeetingOrLobby = false,
            OnlyMeetingOrLobbyAffectsGhosts = false,
            MuteBlackmailedNextRound = true,
            MuteParasiteControlled = true,
            MutePuppeteerControlled = true,
            MuteSwooperWhileSwooped = true,
        };

    private static VoiceIdentityPrivacyResolution ResolveBuiltInScenario(
        VoiceGamePhase phase,
        bool touMiraCommsCamouflage,
        BuiltInAppearance appearance)
    {
        if (!VoiceIdentityPrivacyPhasePolicy.UsesBuiltInAppearancePrivacy(phase))
            return VoiceIdentityPrivacyPolicy.Resolve(3, VoiceIdentityPrivacyEvidence.KnownNormal);

        bool aliasActive = appearance is BuiltInAppearance.MorphOrMimic
            or BuiltInAppearance.MorphWithHiddenStack;
        bool hideSource = appearance is BuiltInAppearance.Venerer
            or BuiltInAppearance.AnonymousCamouflage
            or BuiltInAppearance.MorphWithHiddenStack;
        return VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                HideAllForViewer: touMiraCommsCamouflage,
                HideSource: hideSource,
                AliasActive: aliasActive,
                AliasPlayerId: aliasActive ? (byte)9 : null));
    }

    private static VoiceIdentityPrivacyDecision ExpectedTaskPrivacyDecision(
        BuiltInAppearance appearance,
        bool touMiraCommsCamouflage)
    {
        if (touMiraCommsCamouflage)
            return VoiceIdentityPrivacyDecision.HideAllForViewer;
        return appearance switch
        {
            BuiltInAppearance.Normal => VoiceIdentityPrivacyDecision.Normal,
            BuiltInAppearance.MorphOrMimic => VoiceIdentityPrivacyDecision.Alias,
            _ => VoiceIdentityPrivacyDecision.HideSource,
        };
    }

    private enum BuiltInAppearance
    {
        Normal,
        MorphOrMimic,
        Venerer,
        AnonymousCamouflage,
        MorphWithHiddenStack,
    }

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
        bool isVisible = true,
        bool isParasiteControlled = false,
        bool isPuppeteerControlled = false,
        bool isBlackmailedNextRound = false,
        bool isSwooped = false)
        => new(
            id,
            100 + id,
            $"Player {id}",
            Vector(x, 0f),
            isLocal,
            isDead,
            isSpectator,
            isImpostor,
            IsVampire: false,
            IsLover: false,
            LoverPartnerId: byte.MaxValue,
            InVent: inVent,
            Disconnected: disconnected,
            IsDummy: isDummy,
            IsVisible: isVisible,
            IsBlackmailed: false,
            IsJailed: false,
            JailorId: byte.MaxValue,
            IsParasiteControlled: isParasiteControlled,
            IsPuppeteerControlled: isPuppeteerControlled,
            IsBlackmailedNextRound: isBlackmailedNextRound,
            IsSwooped: isSwooped,
            IsMedium: false,
            HasMediumSpirit: false,
            MediumSpiritPosition: Vector(x, 0f),
            IsMediatedGhost: false,
            MediatingMediumId: byte.MaxValue,
            ControlHearingMode: VoiceControlHearingMode.None,
            ControlledVictimPosition: default,
            ControlledVictimLightRadius: -1f);

    private static Vector2 Vector(float x, float y)
    {
        var value = default(Vector2);
        value.x = x;
        value.y = y;
        return value;
    }
}
