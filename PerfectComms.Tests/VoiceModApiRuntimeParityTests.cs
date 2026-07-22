using System.Runtime.CompilerServices;
using System.Reflection;
using PerfectComms.Api;
using UnityEngine;
using VoiceChatPlugin.Audio;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceModApiRuntimeParityTests : IDisposable
{
    private readonly List<string> _modIds = new();

    public VoiceModApiRuntimeParityTests()
    {
        VoiceModRegistry.ClearRemoteSyncedValues();
        VoiceRoomSettingsState.ClearRemote();
        VoiceRoomSettingsState.ApplyRemote(BaseSettings());
        VoiceProximityCalculator.LocalListenerBlindedOrFlashedProvider = () => false;
        VoiceProximityCalculator.ResetSightState();
    }

    public void Dispose()
    {
        foreach (string modId in _modIds)
            PerfectCommsApi.Unregister(modId);

        VoiceModRegistry.ClearRemoteSyncedValues();
        VoiceRoomSettingsState.ClearRemote();
        VoiceProximityCalculator.LocalListenerBlindedOrFlashedProvider = null;
        VoiceProximityCalculator.ResetSightState();
    }

    [Fact]
    public void RuntimeAdvertisesEveryRoleParityCapability()
    {
        VoiceApiCapability expected =
            VoiceApiCapability.PerSpeakerMuffle |
            VoiceApiCapability.GlobalReceiveGate |
            VoiceApiCapability.DirectionalChannels |
            VoiceApiCapability.MultipleChannels |
            VoiceApiCapability.ContextualListeners |
            VoiceApiCapability.PairRouting |
            VoiceApiCapability.PlayerTraits |
            VoiceApiCapability.PhaseObservers |
            VoiceApiCapability.ConditionalHostOptions |
            VoiceApiCapability.NumericHostOptions |
            VoiceApiCapability.OverlayPrivacy;

        Assert.Equal("1.1", PerfectCommsApi.ApiVersion);
        Assert.Equal(expected, PerfectCommsApi.Capabilities);
        Assert.True(PerfectCommsApi.Supports(expected));
        Assert.False(PerfectCommsApi.Supports(VoiceApiCapability.None));
    }

    [Fact]
    public void GlobalGateMutesLivingAndDeadCaptureAndReceiveRoutes()
    {
        string modId = NewModId("global");
        var observedDeadStates = new List<bool>();
        PerfectCommsApi.RegisterContextualGlobalGate(
            modId,
            VoicePhaseKind.Tasks,
            context =>
            {
                observedDeadStates.Add(context.LocalIsDead);
                return true;
            },
            "System jammed");

        PlayerControl livingControl = FakePlayer();
        PlayerControl deadControl = FakePlayer();
        ExternalVoiceState livingState = VoiceModRegistry.ResolvePlayer(
            livingControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        ExternalVoiceState deadState = VoiceModRegistry.ResolvePlayer(
            deadControl, VoicePhaseKind.Tasks, isLocal: true, isDead: true);

        Assert.True(livingState.Muted);
        Assert.True(deadState.Muted);
        Assert.Equal("System jammed", livingState.Reason);
        Assert.Equal("System jammed", deadState.Reason);
        Assert.True(VoiceModRegistry.LocalGate(
            livingControl, VoicePhaseKind.Tasks, isDead: false, out string livingReason));
        Assert.True(VoiceModRegistry.LocalGate(
            deadControl, VoicePhaseKind.Tasks, isDead: true, out string deadReason));
        Assert.Equal("System jammed", livingReason);
        Assert.Equal("System jammed", deadReason);
        Assert.Contains(false, observedDeadStates);
        Assert.Contains(true, observedDeadStates);

        ExternalVoiceState meetingState = VoiceModRegistry.ResolvePlayer(
            livingControl, VoicePhaseKind.Meeting, isLocal: true, isDead: false);
        Assert.False(meetingState.Muted);

        VoicePlayerSnapshot livingListener = Player(0, 0f, isLocal: true);
        VoicePlayerSnapshot livingTarget = Player(1, 1f, external: livingState);
        VoiceProximityResult livingReceive = VoiceProximityCalculator.ApplyExternalAudioEffects(
            Task(livingListener, livingTarget),
            livingTarget);
        Assert.False(livingReceive.Audible);
        Assert.Equal(VoiceProximityReason.RoleMuted, livingReceive.Reason);

        VoicePlayerSnapshot deadListener = Player(2, 0f, isLocal: true, isDead: true);
        VoicePlayerSnapshot deadTarget = Player(3, 1f, isDead: true, external: deadState);
        VoiceProximityResult rawDeadRoute = Task(deadListener, deadTarget);
        Assert.True(rawDeadRoute.Audible);
        VoiceProximityResult deadReceive = VoiceProximityCalculator.ApplyExternalAudioEffects(
            rawDeadRoute,
            deadTarget);
        Assert.False(deadReceive.Audible);
        Assert.Equal(VoiceProximityReason.RoleMuted, deadReceive.Reason);
    }

    [Fact]
    public void EarlierMuteStillRunsLaterLegacyStateRefreshAndKeepsMuteReason()
    {
        bool earlierMuteActive = true;
        bool legacySourceActive = true;
        bool legacyCachedActive = false;
        PerfectCommsApi.RegisterVoiceRule(
            NewModId("earlier-mute"),
            _ => earlierMuteActive
                ? VoiceRuleResult.Mute("Earlier mute")
                : VoiceRuleResult.Pass);

        string legacyModId = NewModId("legacy-side-effects");
        PerfectCommsApi.RegisterVoiceRule(legacyModId, _ =>
        {
            // The unchanged TouMCE bridge refreshes Hacker/listener/phase state inside this rule.
            legacyCachedActive = legacySourceActive;
            return VoiceRuleResult.Pass;
        });
        PerfectCommsApi.RegisterVoiceRule(
            NewModId("later-muffle"),
            _ => VoiceRuleResult.Muffle("Later muffle"));
        PerfectCommsApi.RegisterGlobalGate(
            legacyModId,
            VoicePhaseKind.Tasks,
            () => legacyCachedActive,
            "Legacy global gate");

        PlayerControl player = FakePlayer();
        ExternalVoiceState first = VoiceModRegistry.ResolvePlayer(
            player,
            VoicePhaseKind.Tasks,
            isLocal: true,
            isDead: false);
        Assert.True(legacyCachedActive);
        Assert.True(first.Muted);
        Assert.Equal("Earlier mute", first.Reason);

        earlierMuteActive = false;
        legacySourceActive = true;
        legacyCachedActive = false;
        ExternalVoiceState second = VoiceModRegistry.ResolvePlayer(
            player,
            VoicePhaseKind.Tasks,
            isLocal: true,
            isDead: false);
        Assert.True(legacyCachedActive);
        Assert.True(second.Muted);
        Assert.Equal("Legacy global gate", second.Reason);
    }

    [Fact]
    public void PerSpeakerMuffleIsAppliedAfterRoutingWithoutAffectingOtherSpeakers()
    {
        string modId = NewModId("speaker-muffle");
        PlayerControl muffledControl = FakePlayer();
        PlayerControl clearControl = FakePlayer();
        PerfectCommsApi.RegisterVoiceRule(
            modId,
            context => ReferenceEquals(context.Player, muffledControl)
                ? VoiceRuleResult.Muffle("Disoriented")
                : VoiceRuleResult.Pass);

        ExternalVoiceState muffledState = VoiceModRegistry.ResolvePlayer(
            muffledControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        ExternalVoiceState clearState = VoiceModRegistry.ResolvePlayer(
            clearControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        Assert.True(muffledState.Muffled);
        Assert.False(clearState.Muffled);

        VoicePlayerSnapshot listener = Player(0, 0f, isLocal: true);
        VoicePlayerSnapshot muffled = Player(1, 2f, external: muffledState);
        VoicePlayerSnapshot clear = Player(2, 2f, external: clearState);
        VoiceProximityResult rawMuffled = Task(listener, muffled);
        VoiceProximityResult processedMuffled =
            VoiceProximityCalculator.ApplyExternalAudioEffects(rawMuffled, muffled);
        VoiceProximityResult processedClear =
            VoiceProximityCalculator.ApplyExternalAudioEffects(Task(listener, clear), clear);

        Assert.True(processedMuffled.Audible);
        Assert.Equal(rawMuffled.NormalVolume, processedMuffled.NormalVolume);
        Assert.Equal(rawMuffled.Pan, processedMuffled.Pan);
        Assert.Equal(VoiceAudioFilterMode.ListenerMuffle, processedMuffled.FilterMode);
        Assert.Equal(VoiceAudioFilterMode.None, processedClear.FilterMode);
    }

    [Fact]
    public void EndGameGroupCallIgnoresTransitionRetainedExternalState()
    {
        ExternalVoiceState staleTaskState = ExternalVoiceState.None with
        {
            Muted = true,
            Muffled = true,
            Reason = "Stale task mute",
            Pair = ExternalVoicePairState.None with
            {
                Verdict = VoicePairVerdict.Mute,
                Muffled = true,
            },
        };
        VoicePlayerSnapshot retainedTarget = Player(1, 1f, external: staleTaskState);
        VoiceProximityResult groupCall = VoiceProximityCalculator.CalculateEndGame();

        VoiceProximityResult endGame = VoiceProximityCalculator.ApplyExternalAudioEffects(
            groupCall,
            retainedTarget,
            VoiceGamePhase.EndGame);
        VoiceProximityResult tasks = VoiceProximityCalculator.ApplyExternalAudioEffects(
            groupCall,
            retainedTarget,
            VoiceGamePhase.Tasks);

        Assert.True(endGame.Audible);
        Assert.Equal(groupCall.NormalVolume, endGame.NormalVolume);
        Assert.Equal(groupCall.GhostVolume, endGame.GhostVolume);
        Assert.Equal(groupCall.RadioVolume, endGame.RadioVolume);
        Assert.Equal(groupCall.FilterMode, endGame.FilterMode);
        Assert.Equal(groupCall.Reason, endGame.Reason);
        Assert.False(tasks.Audible);
        Assert.Equal(VoiceProximityReason.RoleMuted, tasks.Reason);
    }

    [Fact]
    public void MultipleChannelMembershipsAreRetainedAndLaterOverlapCanRoute()
    {
        string modId = NewModId("multi-channel");
        PlayerControl listenerControl = FakePlayer();
        PlayerControl speakerControl = FakePlayer();

        PerfectCommsApi.RegisterVoiceChannel(
            modId,
            context => new VoiceChannelResult(
                ReferenceEquals(context.Player, listenerControl) ? "listener-only" : "speaker-only",
                Shape: VoiceAudioShape.Radio,
                Volume: 0.2f));
        PerfectCommsApi.RegisterVoiceChannel(
            modId,
            _ => new VoiceChannelResult(
                "shared",
                Shape: VoiceAudioShape.Radio,
                Volume: 0.65f));

        ExternalVoiceState listenerState = VoiceModRegistry.ResolvePlayer(
            listenerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        ExternalVoiceState speakerState = VoiceModRegistry.ResolvePlayer(
            speakerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);

        Assert.Equal(2, listenerState.ChannelSpan.Length);
        Assert.Equal(2, speakerState.ChannelSpan.Length);
        VoiceProximityResult result = Task(
            Player(0, 0f, isLocal: true, external: listenerState),
            Player(1, 2f, external: speakerState));

        Assert.True(result.Audible);
        Assert.Equal(VoiceProximityReason.ModChannel, result.Reason);
        AssertClose(0.65f, result.RadioVolume);
    }

    [Fact]
    public void TwoWayFalseIsReceiveOnlyAndDoesNotTransmitInReverse()
    {
        string modId = NewModId("one-way-channel");
        PlayerControl receiveOnlyControl = FakePlayer();
        PlayerControl transmitterControl = FakePlayer();
        PerfectCommsApi.RegisterVoiceChannel(
            modId,
            context => new VoiceChannelResult(
                "directional",
                TwoWay: !ReferenceEquals(context.Player, receiveOnlyControl),
                Shape: VoiceAudioShape.Radio,
                Volume: 0.8f));

        ExternalVoiceState receiveOnlyState = VoiceModRegistry.ResolvePlayer(
            receiveOnlyControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        ExternalVoiceState transmitterState = VoiceModRegistry.ResolvePlayer(
            transmitterControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        Assert.False(receiveOnlyState.ChannelSpan[0].CanTransmit);
        Assert.True(transmitterState.ChannelSpan[0].CanTransmit);

        VoiceProximityResult receives = Task(
            Player(0, 0f, isLocal: true, external: receiveOnlyState),
            Player(1, 1f, external: transmitterState));
        VoiceProximityResult cannotTransmitBack = Task(
            Player(1, 0f, isLocal: true, external: transmitterState),
            Player(0, 1f, external: receiveOnlyState));

        Assert.Equal(VoiceProximityReason.ModChannel, receives.Reason);
        AssertClose(0.8f, receives.RadioVolume);
        Assert.Equal(VoiceProximityReason.Proximity, cannotTransmitBack.Reason);
        Assert.Equal(0f, cannotTransmitBack.RadioVolume);
    }

    [Fact]
    public void IdenticalRawChannelKeysFromDifferentModsRemainIsolated()
    {
        string firstModId = NewModId("channel-scope-first");
        string secondModId = NewModId("channel-scope-second");
        PlayerControl listenerControl = FakePlayer();
        PlayerControl matchingSpeakerControl = FakePlayer();
        PlayerControl otherModSpeakerControl = FakePlayer();

        PerfectCommsApi.RegisterVoiceChannel(
            firstModId,
            context => ReferenceEquals(context.Player, listenerControl)
                       || ReferenceEquals(context.Player, matchingSpeakerControl)
                ? new VoiceChannelResult("same-raw-key", Shape: VoiceAudioShape.Radio, Volume: 0.7f)
                : null);
        PerfectCommsApi.RegisterVoiceChannel(
            secondModId,
            context => ReferenceEquals(context.Player, otherModSpeakerControl)
                ? new VoiceChannelResult("same-raw-key", Shape: VoiceAudioShape.Radio, Volume: 0.9f)
                : null);

        ExternalVoiceState listenerState = VoiceModRegistry.ResolvePlayer(
            listenerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        ExternalVoiceState matchingSpeakerState = VoiceModRegistry.ResolvePlayer(
            matchingSpeakerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        ExternalVoiceState otherModSpeakerState = VoiceModRegistry.ResolvePlayer(
            otherModSpeakerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);

        Assert.Equal(1, listenerState.ChannelSpan.Length);
        Assert.Equal(1, matchingSpeakerState.ChannelSpan.Length);
        Assert.Equal(1, otherModSpeakerState.ChannelSpan.Length);
        Assert.Equal(listenerState.ChannelSpan[0].Key, matchingSpeakerState.ChannelSpan[0].Key);
        Assert.NotEqual(listenerState.ChannelSpan[0].Key, otherModSpeakerState.ChannelSpan[0].Key);

        VoicePlayerSnapshot listener = Player(0, 0f, isLocal: true, external: listenerState);
        VoiceProximityResult matching = Task(
            listener,
            Player(1, 2f, external: matchingSpeakerState));
        VoiceProximityResult isolated = Task(
            listener,
            Player(2, 2f, external: otherModSpeakerState));

        Assert.Equal(VoiceProximityReason.ModChannel, matching.Reason);
        AssertClose(0.7f, matching.RadioVolume);
        Assert.Equal(VoiceProximityReason.Proximity, isolated.Reason);
        Assert.Equal(0f, isolated.RadioVolume);
    }

    [Fact]
    public void ProximityChannelsUseSpeakerBodyByDefaultAndExplicitOriginInTasksAndMeetings()
    {
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { MaxChatDistance = 10f });
        string modId = NewModId("proximity-channel");
        PlayerControl listenerControl = FakePlayer();
        PlayerControl bodySpeakerControl = FakePlayer();
        PlayerControl originSpeakerControl = FakePlayer();

        PerfectCommsApi.RegisterVoiceChannel(
            modId,
            context => ReferenceEquals(context.Player, listenerControl)
                       || ReferenceEquals(context.Player, bodySpeakerControl)
                ? new VoiceChannelResult("body", Shape: VoiceAudioShape.Proximity)
                : null);
        PerfectCommsApi.RegisterVoiceChannel(
            modId,
            context => ReferenceEquals(context.Player, listenerControl)
                       || ReferenceEquals(context.Player, originSpeakerControl)
                ? new VoiceChannelResult(
                    "origin",
                    Shape: VoiceAudioShape.Proximity,
                    Origin: ReferenceEquals(context.Player, originSpeakerControl)
                        ? Vector(2f, 0f)
                        : null)
                : null);

        ExternalVoiceState listenerState = VoiceModRegistry.ResolvePlayer(
            listenerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        ExternalVoiceState bodyState = VoiceModRegistry.ResolvePlayer(
            bodySpeakerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        ExternalVoiceState originState = VoiceModRegistry.ResolvePlayer(
            originSpeakerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        VoicePlayerSnapshot listener = Player(0, 0f, isLocal: true, external: listenerState);
        VoicePlayerSnapshot bodySpeaker = Player(1, 5f, external: bodyState);
        VoicePlayerSnapshot originSpeaker = Player(2, 8f, external: originState);

        VoiceProximityResult bodyTask = Task(listener, bodySpeaker);
        VoiceProximityResult originTask = Task(listener, originSpeaker);
        VoiceProximityResult bodyMeeting =
            VoiceProximityCalculator.CalculateMeeting(listener, bodySpeaker, false);
        VoiceProximityResult originMeeting =
            VoiceProximityCalculator.CalculateMeeting(listener, originSpeaker, false);

        Assert.Equal(VoiceProximityReason.ModChannel, bodyTask.Reason);
        Assert.Equal(VoiceProximityReason.ModChannel, originTask.Reason);
        Assert.Equal(VoiceProximityReason.ModChannel, bodyMeeting.Reason);
        Assert.Equal(VoiceProximityReason.ModChannel, originMeeting.Reason);
        AssertClose(0.5f, bodyTask.NormalVolume);
        AssertClose(0.8f, originTask.NormalVolume);
        AssertClose(0.5f, bodyMeeting.NormalVolume);
        AssertClose(0.8f, originMeeting.NormalVolume);
    }

    [Fact]
    public void PairRouteAndMuffleComposeButLaterMuteWins()
    {
        PlayerControl listenerControl = FakePlayer();
        PlayerControl speakerControl = FakePlayer();
        PerfectCommsApi.RegisterVoicePairRule(
            NewModId("pair-route"),
            _ => VoicePairResult.Route(VoicePairRouteShape.Radio, volume: 0.75f));
        PerfectCommsApi.RegisterVoicePairRule(
            NewModId("pair-muffle"),
            _ => VoicePairResult.Muffle("Low pass"));

        ExternalVoicePairState routed = VoiceModRegistry.ResolvePair(
            listenerControl,
            speakerControl,
            VoicePhaseKind.Tasks,
            listenerIsDead: false,
            speakerIsDead: false);
        Assert.Equal(VoicePairVerdict.Route, routed.Verdict);
        Assert.True(routed.Muffled);

        VoicePlayerSnapshot listener = Player(0, 0f, isLocal: true);
        VoicePlayerSnapshot speaker = Player(
            1,
            3f,
            external: ExternalVoiceState.None with { Pair = routed });
        VoiceProximityResult raw = Task(listener, speaker);
        VoiceProximityResult processed =
            VoiceProximityCalculator.ApplyExternalAudioEffects(raw, speaker);
        Assert.Equal(VoiceProximityReason.ModPairRoute, raw.Reason);
        AssertClose(0.75f, processed.RadioVolume);
        Assert.Equal(VoiceAudioFilterMode.ListenerMuffle, processed.FilterMode);

        PerfectCommsApi.RegisterVoicePairRule(
            NewModId("pair-mute"),
            _ => VoicePairResult.Mute("Private"));
        ExternalVoicePairState muted = VoiceModRegistry.ResolvePair(
            listenerControl,
            speakerControl,
            VoicePhaseKind.Tasks,
            listenerIsDead: false,
            speakerIsDead: false);
        Assert.Equal(VoicePairVerdict.Mute, muted.Verdict);
        Assert.Equal("Private", muted.Reason);

        VoicePlayerSnapshot mutedSpeaker = speaker with
        {
            External = ExternalVoiceState.None with { Pair = muted },
        };
        VoiceProximityResult mutedResult = Task(listener, mutedSpeaker);
        Assert.False(mutedResult.Audible);
        Assert.Equal(VoiceProximityReason.RoleMuted, mutedResult.Reason);
    }

    [Fact]
    public void PairRouteHostPolicyPrecedenceIsExplicit()
    {
        ExternalVoicePairState radioPair = ExternalVoicePairState.None with
        {
            Verdict = VoicePairVerdict.Route,
            Shape = (int)VoicePairRouteShape.Radio,
            Volume = 1f,
        };
        VoicePlayerSnapshot local = Player(0, 0f, isLocal: true);
        VoicePlayerSnapshot routed = Player(
            1,
            1f,
            external: ExternalVoiceState.None with { Pair = radioPair });

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            OnlyGhostsCanTalk = true,
            CommsSabDisables = true,
        });
        VoiceProximityResult taskRoleException = Task(
            local,
            routed,
            commsSabActive: true);
        VoiceProximityResult meetingHostBlock =
            VoiceProximityCalculator.CalculateMeeting(local, routed, false);
        Assert.True(taskRoleException.Audible);
        Assert.Equal(VoiceProximityReason.ModPairRoute, taskRoleException.Reason);
        Assert.False(meetingHostBlock.Audible);
        Assert.Equal(VoiceProximityReason.OnlyGhostsCanTalk, meetingHostBlock.Reason);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            OnlyMeetingOrLobby = true,
        });
        VoiceProximityResult phaseBlock = Task(local, routed);
        Assert.False(phaseBlock.Audible);
        Assert.Equal(VoiceProximityReason.OnlyMeetingOrLobby, phaseBlock.Reason);

    }

    [Fact]
    public void PairRulesExpressMediumDirectionsAndPrivateAudience()
    {
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { MaxChatDistance = 10f });
        string modId = NewModId("medium-pairs");
        PlayerControl medium = FakePlayer();
        PlayerControl selectedGhost = FakePlayer();
        PlayerControl otherGhost = FakePlayer();
        PlayerControl living = FakePlayer();
        Vector2 spirit = Vector(6f, 0f);

        PerfectCommsApi.RegisterHostEnumOption(
            modId,
            new VoiceHostEnumOption(
                "mode",
                "Medium voice",
                0,
                new[] { "None", "Medium -> Ghost", "Ghost -> Medium", "Both" }));
        PerfectCommsApi.RegisterVoicePairRule(modId, context =>
        {
            if (context.Phase != VoicePhaseKind.Tasks)
                return VoicePairResult.Pass;

            int mode = context.GetEnumOption("mode");
            if (mode == 0)
                return VoicePairResult.Pass;
            bool mediumToGhost = mode is 1 or 3;
            bool ghostToMedium = mode is 2 or 3;

            if (ReferenceEquals(context.Speaker, medium))
            {
                return context.ListenerIsDead && mediumToGhost
                    ? VoicePairResult.Route(
                        VoicePairRouteShape.Proximity,
                        speakerOrigin: Vector(2f, 0f))
                    : VoicePairResult.Mute("Medium private");
            }

            if (ReferenceEquals(context.Listener, medium) && context.SpeakerIsDead)
            {
                if (!ghostToMedium) return VoicePairResult.Pass;
                return ReferenceEquals(context.Speaker, selectedGhost) && ghostToMedium
                    ? VoicePairResult.Route(
                        VoicePairRouteShape.Ghost,
                        listenerOrigin: spirit)
                    : VoicePairResult.Mute("Not selected");
            }

            return VoicePairResult.Pass;
        });

        VoiceModRegistry.SetEnumValue(VoiceModRegistry.Compose(modId, "mode"), 1);
        Assert.Equal(
            VoicePairVerdict.Route,
            ResolvePair(selectedGhost, medium, listenerDead: true, speakerDead: false).Verdict);
        Assert.Equal(
            VoicePairVerdict.Pass,
            ResolvePair(medium, selectedGhost, listenerDead: false, speakerDead: true).Verdict);
        Assert.Equal(
            VoicePairVerdict.Mute,
            ResolvePair(living, medium, listenerDead: false, speakerDead: false).Verdict);

        VoiceModRegistry.SetEnumValue(VoiceModRegistry.Compose(modId, "mode"), 2);
        Assert.Equal(
            VoicePairVerdict.Mute,
            ResolvePair(selectedGhost, medium, listenerDead: true, speakerDead: false).Verdict);
        Assert.Equal(
            VoicePairVerdict.Route,
            ResolvePair(medium, selectedGhost, listenerDead: false, speakerDead: true).Verdict);
        Assert.Equal(
            VoicePairVerdict.Mute,
            ResolvePair(medium, otherGhost, listenerDead: false, speakerDead: true).Verdict);

        VoiceModRegistry.SetEnumValue(VoiceModRegistry.Compose(modId, "mode"), 3);
        ExternalVoicePairState mediumToGhostPair =
            ResolvePair(selectedGhost, medium, listenerDead: true, speakerDead: false);
        ExternalVoicePairState ghostToMediumPair =
            ResolvePair(medium, selectedGhost, listenerDead: false, speakerDead: true);
        ExternalVoicePairState livingPrivacyPair =
            ResolvePair(living, medium, listenerDead: false, speakerDead: false);
        ExternalVoicePairState selectionPrivacyPair =
            ResolvePair(medium, otherGhost, listenerDead: false, speakerDead: true);

        VoiceProximityResult hearsMedium = Task(
            Player(0, 0f, isLocal: true, isDead: true),
            Player(
                1,
                9f,
                external: ExternalVoiceState.None with { Pair = mediumToGhostPair }));
        VoiceProximityResult hearsSelectedGhost = Task(
            Player(1, 0f, isLocal: true),
            Player(
                2,
                8f,
                isDead: true,
                external: ExternalVoiceState.None with { Pair = ghostToMediumPair }));
        VoiceProximityResult livingCannotHearMedium = Task(
            Player(3, 0f, isLocal: true),
            Player(
                1,
                9f,
                external: ExternalVoiceState.None with { Pair = livingPrivacyPair }));
        VoiceProximityResult mediumCannotHearOtherGhost = Task(
            Player(1, 0f, isLocal: true),
            Player(
                4,
                8f,
                isDead: true,
                external: ExternalVoiceState.None with { Pair = selectionPrivacyPair }));

        Assert.Equal(VoiceProximityReason.ModPairRoute, hearsMedium.Reason);
        AssertClose(0.8f, hearsMedium.NormalVolume);
        Assert.Equal(VoiceProximityReason.ModPairRoute, hearsSelectedGhost.Reason);
        AssertClose(0.8f, hearsSelectedGhost.GhostVolume);
        Assert.Equal(VoiceAudioFilterMode.Ghost, hearsSelectedGhost.FilterMode);
        Assert.Equal(VoiceProximityReason.RoleMuted, livingCannotHearMedium.Reason);
        Assert.Equal(VoiceProximityReason.RoleMuted, mediumCannotHearOtherGhost.Reason);

        Assert.Equal(
            VoicePairVerdict.Pass,
            ResolvePair(
                selectedGhost,
                medium,
                listenerDead: true,
                speakerDead: false,
                phase: VoicePhaseKind.Meeting).Verdict);
        Assert.Equal(
            VoicePairVerdict.Pass,
            ResolvePair(
                medium,
                selectedGhost,
                listenerDead: false,
                speakerDead: true,
                phase: VoicePhaseKind.Exile).Verdict);

        VoiceModRegistry.SetEnumValue(VoiceModRegistry.Compose(modId, "mode"), 0);
        Assert.Equal(
            VoicePairVerdict.Pass,
            ResolvePair(living, medium, listenerDead: false, speakerDead: false).Verdict);

        ExternalVoicePairState ResolvePair(
            PlayerControl listener,
            PlayerControl speaker,
            bool listenerDead,
            bool speakerDead,
            VoicePhaseKind phase = VoicePhaseKind.Tasks)
            => VoiceModRegistry.ResolvePair(
                listener,
                speaker,
                phase,
                listenerDead,
                speakerDead);
    }

    [Fact]
    public void PlayerTraitsComposeByOrAndDriveDeadSpectatorAndImpostorVoiceRouting()
    {
        PlayerControl composite = FakePlayer();
        PlayerControl voiceImpostor = FakePlayer();
        PlayerControl voiceDead = FakePlayer();
        PlayerControl spectator = FakePlayer();

        PerfectCommsApi.RegisterVoicePlayerTraits(
            NewModId("trait-impostor"),
            context => ReferenceEquals(context.Player, composite)
                       || ReferenceEquals(context.Player, voiceImpostor)
                ? VoicePlayerTraits.ImpostorVoice
                : VoicePlayerTraits.None);
        PerfectCommsApi.RegisterVoicePlayerTraits(
            NewModId("trait-dead"),
            context => ReferenceEquals(context.Player, composite)
                       || ReferenceEquals(context.Player, voiceDead)
                ? VoicePlayerTraits.VoiceDead
                : VoicePlayerTraits.None);
        PerfectCommsApi.RegisterVoicePlayerTraits(
            NewModId("trait-spectator"),
            context => ReferenceEquals(context.Player, composite)
                       || ReferenceEquals(context.Player, spectator)
                ? VoicePlayerTraits.Spectator
                : VoicePlayerTraits.None);
        PerfectCommsApi.RegisterVoicePlayerTraits(
            NewModId("trait-invalid"),
            _ => (VoicePlayerTraits)(1 << 20));
        PerfectCommsApi.RegisterVoicePlayerTraits(
            NewModId("trait-throw"),
            _ => throw new InvalidOperationException("third-party failure"));

        VoicePlayerTraits combined = VoiceModRegistry.ResolvePlayerTraits(
            composite, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        VoicePlayerTraits impostorTraits = VoiceModRegistry.ResolvePlayerTraits(
            voiceImpostor, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        VoicePlayerTraits deadTraits = VoiceModRegistry.ResolvePlayerTraits(
            voiceDead, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        VoicePlayerTraits spectatorTraits = VoiceModRegistry.ResolvePlayerTraits(
            spectator, VoicePhaseKind.Tasks, isLocal: true, isDead: false);

        Assert.Equal(
            VoicePlayerTraits.ImpostorVoice |
            VoicePlayerTraits.VoiceDead |
            VoicePlayerTraits.Spectator,
            combined);
        Assert.Equal(VoicePlayerTraits.ImpostorVoice, impostorTraits);
        Assert.Equal(VoicePlayerTraits.VoiceDead, deadTraits);
        Assert.Equal(
            VoicePlayerTraits.Spectator | VoicePlayerTraits.VoiceDead,
            spectatorTraits);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { ImpostorHearGhosts = true });
        VoicePlayerSnapshot traitImpostor = Player(
            0,
            0f,
            isLocal: true,
            isImpostor: (impostorTraits & VoicePlayerTraits.ImpostorVoice) != 0);
        VoicePlayerSnapshot traitGhost = Player(
            1,
            1f,
            isDead: (deadTraits & VoicePlayerTraits.VoiceDead) != 0);
        VoicePlayerSnapshot traitSpectator = Player(
            2,
            1f,
            isDead: (spectatorTraits & VoicePlayerTraits.VoiceDead) != 0,
            isSpectator: (spectatorTraits & VoicePlayerTraits.Spectator) != 0);
        VoicePlayerSnapshot baseImpostor = Player(3, 0f, isLocal: true, isImpostor: true);

        Assert.Equal(VoiceProximityReason.ImpostorHearsGhost, Task(traitImpostor, traitGhost).Reason);
        Assert.Equal(VoiceProximityReason.TargetDeadMuted, Task(traitImpostor, traitSpectator).Reason);
        Assert.Equal(VoiceProximityReason.ImpostorHearsGhost, Task(baseImpostor, traitGhost).Reason);
    }

    [Fact]
    public void NegativeOneListenerLightRadiusInheritsCurrentResolvedRadius()
    {
        string modId = NewModId("light-radius");
        PlayerControl listenerControl = FakePlayer();
        PerfectCommsApi.RegisterContextualListenerOrigin(
            modId,
            _ => new VoiceListenerResult(
                Vector(0f, 0f),
                LightRadius: -1f,
                VoiceListenerMode.Replace));

        ExternalVoiceState state = VoiceModRegistry.ResolvePlayer(
            listenerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        Assert.True(state.ListenerActive);
        Assert.Equal(-1f, state.ListenerLightRadius);

        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with
        {
            MaxChatDistance = 10f,
            OnlyHearInSight = true,
        });
        AssertClose(2f, ResolveSightLimitedMaxDistance(
            maxDistance: 10f,
            localLightRadius: 2f,
            listenerBlindedOrFlashed: false));
        AssertClose(10f, ResolveSightLimitedMaxDistance(
            maxDistance: 10f,
            localLightRadius: 0f,
            listenerBlindedOrFlashed: false));
    }

    [Fact]
    public void ExternalReplaceAndAdditiveOriginsDriveTaskRoutingAndPreserveExplicitLightRadius()
    {
        VoiceRoomSettingsState.ApplyRemote(BaseSettings() with { MaxChatDistance = 10f });
        PlayerControl listenerControl = FakePlayer();

        string replaceModId = NewModId("external-replace");
        PerfectCommsApi.RegisterContextualListenerOrigin(
            replaceModId,
            _ => new VoiceListenerResult(
                Vector(8f, 0f),
                LightRadius: 3f,
                VoiceListenerMode.Replace));
        ExternalVoiceState replaceState = VoiceModRegistry.ResolvePlayer(
            listenerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        Assert.True(replaceState.ListenerActive);
        Assert.True(replaceState.ListenerReplace);
        AssertClose(3f, replaceState.ListenerLightRadius);

        VoicePlayerSnapshot bodyListener = Player(0, 0f, isLocal: true);
        VoicePlayerSnapshot replaceListener = Player(
            0,
            0f,
            isLocal: true,
            external: replaceState,
            controlMode: VoiceControlHearingMode.ExternalReplace,
            controlledOrigin: replaceState.ListenerOrigin,
            controlledLightRadius: replaceState.ListenerLightRadius);
        VoicePlayerSnapshot targetAtExternalOrigin = Player(1, 8f);

        AssertClose(0.2f, Task(bodyListener, targetAtExternalOrigin).NormalVolume);
        AssertClose(1f, Task(replaceListener, targetAtExternalOrigin).NormalVolume);
        AssertClose(3f, ResolveSightLimitedMaxDistance(
            maxDistance: 10f,
            localLightRadius: replaceListener.ControlledVictimLightRadius,
            listenerBlindedOrFlashed: false));

        PerfectCommsApi.Unregister(replaceModId);

        string additiveModId = NewModId("external-additive");
        PerfectCommsApi.RegisterContextualListenerOrigin(
            additiveModId,
            _ => new VoiceListenerResult(
                Vector(8f, 0f),
                LightRadius: 4f,
                VoiceListenerMode.Additive));
        ExternalVoiceState additiveState = VoiceModRegistry.ResolvePlayer(
            listenerControl, VoicePhaseKind.Tasks, isLocal: true, isDead: false);
        Assert.True(additiveState.ListenerActive);
        Assert.False(additiveState.ListenerReplace);
        AssertClose(4f, additiveState.ListenerLightRadius);

        VoicePlayerSnapshot additiveListener = Player(
            0,
            0f,
            isLocal: true,
            external: additiveState,
            controlMode: VoiceControlHearingMode.ExternalAdditive,
            controlledOrigin: additiveState.ListenerOrigin,
            controlledLightRadius: additiveState.ListenerLightRadius);

        AssertClose(1f, Task(additiveListener, Player(2, 0f)).NormalVolume);
        AssertClose(1f, Task(additiveListener, targetAtExternalOrigin).NormalVolume);
        AssertClose(4f, ResolveSightLimitedMaxDistance(
            maxDistance: 10f,
            localLightRadius: additiveListener.ControlledVictimLightRadius,
            listenerBlindedOrFlashed: false));
    }

    [Fact]
    public void ContextualListenersAndPhaseObserversReceiveScopedPhaseAndOptions()
    {
        string modId = NewModId("context");
        PlayerControl listener = FakePlayer();
        PerfectCommsApi.RegisterHostOption(
            modId,
            new VoiceHostOption("enabled", "Enabled", true));
        PerfectCommsApi.RegisterHostEnumOption(
            modId,
            new VoiceHostEnumOption("mode", "Mode", 2, new[] { "Off", "One", "Two" }));
        PerfectCommsApi.RegisterHostNumberOption(
            modId,
            new VoiceHostNumberOption("range", "Range", 3.5f, 0f, 10f, 0.5f));

        VoiceListenerContext? observedOrigin = null;
        VoiceListenerContext? observedFilter = null;
        var transitions = new List<VoicePhaseChangedContext>();
        PerfectCommsApi.RegisterContextualListenerOrigin(modId, context =>
        {
            observedOrigin = context;
            return context.GetOption("enabled")
                   && context.GetEnumOption("mode") == 2
                   && Math.Abs(context.GetNumberOption("range") - 3.5f) < 0.001f
                ? new VoiceListenerResult(Vector(4f, 0f), -1f, VoiceListenerMode.Additive)
                : null;
        });
        PerfectCommsApi.RegisterContextualListenerFilter(modId, context =>
        {
            observedFilter = context;
            return new VoiceListenerFilterResult(
                context.GetOption("enabled") && context.GetNumberOption("range") > 3f);
        });
        PerfectCommsApi.RegisterVoicePhaseObserver(modId, context => transitions.Add(context));

        ExternalVoiceState state = VoiceModRegistry.ResolvePlayer(
            listener,
            VoicePhaseKind.Exile,
            isLocal: true,
            isDead: true);
        Assert.True(state.ListenerActive);
        Assert.False(state.ListenerReplace);
        Assert.NotNull(observedOrigin);
        Assert.Equal(VoicePhaseKind.Exile, observedOrigin!.Phase);
        Assert.True(observedOrigin.IsDead);
        Assert.True(observedOrigin.GetOption("enabled"));
        Assert.Equal(2, observedOrigin.GetEnumOption("mode"));
        Assert.Equal(3.5f, observedOrigin.GetNumberOption("range"));

        bool listenerMuffled = VoiceModRegistry.ResolveListenerMuffled(
            listener,
            VoicePhaseKind.Exile,
            isDead: true);
        Assert.True(listenerMuffled);
        Assert.NotNull(observedFilter);
        Assert.True(observedFilter!.GetOption("enabled"));
        Assert.Equal(2, observedFilter.GetEnumOption("mode"));
        Assert.Equal(3.5f, observedFilter.GetNumberOption("range"));

        VoiceModRegistry.NotifyPhase(VoicePhaseKind.Tasks, listener);
        VoiceModRegistry.NotifyPhase(VoicePhaseKind.Meeting, listener);
        VoicePhaseChangedContext meeting = Assert.Single(
            transitions,
            context => context.Phase == VoicePhaseKind.Meeting);
        Assert.Equal(VoicePhaseKind.Tasks, meeting.PreviousPhase);
        Assert.Same(listener, meeting.LocalPlayer);
        Assert.True(meeting.GetOption("enabled"));
        Assert.Equal(2, meeting.GetEnumOption("mode"));
        Assert.Equal(3.5f, meeting.GetNumberOption("range"));
    }

    [Fact]
    public void PhaseObserverReportsNormalMeetingExileTasksLifecycle()
    {
        var transitions = new List<VoicePhaseChangedContext>();
        int nextRoundCommits = 0;
        PerfectCommsApi.RegisterVoicePhaseObserver(NewModId("phase-lifecycle"), context =>
        {
            transitions.Add(context);
            if ((context.PreviousPhase is VoicePhaseKind.Meeting or VoicePhaseKind.Exile)
                && context.Phase == VoicePhaseKind.Tasks)
            {
                nextRoundCommits++;
            }
        });

        PlayerControl listener = FakePlayer();
        VoiceModRegistry.NotifyPhase(VoicePhaseKind.Meeting, listener);
        VoiceModRegistry.NotifyPhase(VoicePhaseKind.Exile, listener);
        VoiceModRegistry.NotifyPhase(VoicePhaseKind.Tasks, listener);

        Assert.Single(
            transitions,
            context => context.PreviousPhase == VoicePhaseKind.Meeting
                       && context.Phase == VoicePhaseKind.Exile);
        Assert.Single(
            transitions,
            context => context.PreviousPhase == VoicePhaseKind.Exile
                       && context.Phase == VoicePhaseKind.Tasks);
        Assert.Equal(1, nextRoundCommits);
    }

    [Fact]
    public void ContextualListenerFilterRemainsAvailableDuringMeeting()
    {
        PlayerControl listener = FakePlayer();
        VoicePhaseKind? observedPhase = null;
        PerfectCommsApi.RegisterContextualListenerFilter(
            NewModId("meeting-listener-filter"),
            context =>
            {
                observedPhase = context.Phase;
                return new VoiceListenerFilterResult(true);
            });

        Assert.True(VoiceModRegistry.ResolveListenerMuffled(
            listener,
            VoicePhaseKind.Meeting,
            isDead: false));
        Assert.Equal(VoicePhaseKind.Meeting, observedPhase);
    }

    [Fact]
    public void ContextualAndLegacyListenerFiltersResolveAndMuffleAnAudibleRoute()
    {
        PlayerControl listenerControl = FakePlayer();
        bool contextualActive = true;
        bool legacyActive = false;
        int contextualCalls = 0;
        int legacyCalls = 0;

        string filterModId = NewModId("listener-filters");
        PerfectCommsApi.RegisterContextualListenerFilter(
            filterModId,
            context =>
            {
                contextualCalls++;
                Assert.Same(listenerControl, context.Listener);
                Assert.Equal(VoicePhaseKind.Tasks, context.Phase);
                return new VoiceListenerFilterResult(contextualActive);
            });
        PerfectCommsApi.RegisterListenerFilter(
            filterModId,
            listener =>
            {
                legacyCalls++;
                Assert.Same(listenerControl, listener);
                return legacyActive;
            });

        VoiceProximityResult audible = Task(
            Player(0, 0f, isLocal: true),
            Player(1, 2f));
        Assert.True(audible.Audible);
        Assert.Equal(VoiceAudioFilterMode.None, audible.FilterMode);

        bool contextualMuffle = VoiceModRegistry.ResolveListenerMuffled(
            listenerControl,
            VoicePhaseKind.Tasks,
            isDead: false);
        VoiceProximityResult contextuallyFiltered = ApplyResolvedListenerMuffle(
            audible,
            contextualMuffle);
        Assert.True(contextualMuffle);
        Assert.Equal(1, contextualCalls);
        Assert.Equal(0, legacyCalls);
        Assert.Equal(VoiceAudioFilterMode.ListenerMuffle, contextuallyFiltered.FilterMode);
        Assert.Equal(audible.NormalVolume, contextuallyFiltered.NormalVolume);
        Assert.Equal(audible.Pan, contextuallyFiltered.Pan);

        contextualActive = false;
        legacyActive = true;
        bool legacyMuffle = VoiceModRegistry.ResolveListenerMuffled(
            listenerControl,
            VoicePhaseKind.Tasks,
            isDead: false);
        VoiceProximityResult legacyFiltered = ApplyResolvedListenerMuffle(audible, legacyMuffle);
        Assert.True(legacyMuffle);
        Assert.Equal(2, contextualCalls);
        Assert.Equal(1, legacyCalls);
        Assert.Equal(VoiceAudioFilterMode.ListenerMuffle, legacyFiltered.FilterMode);

        contextualActive = false;
        legacyActive = false;
        bool noMuffle = VoiceModRegistry.ResolveListenerMuffled(
            listenerControl,
            VoicePhaseKind.Tasks,
            isDead: false);
        Assert.False(noMuffle);
        VoiceProximityResult unchanged = ApplyResolvedListenerMuffle(audible, noMuffle);
        Assert.Equal(audible.NormalVolume, unchanged.NormalVolume);
        Assert.Equal(audible.GhostVolume, unchanged.GhostVolume);
        Assert.Equal(audible.RadioVolume, unchanged.RadioVolume);
        Assert.Equal(audible.Pan, unchanged.Pan);
        Assert.Equal(audible.FilterMode, unchanged.FilterMode);
        Assert.Equal(audible.Audible, unchanged.Audible);
        Assert.Equal(audible.Reason, unchanged.Reason);
    }

    [Fact]
    public void CallbackRegistrationMutationIsDeferredAndCannotBreakCurrentEvaluation()
    {
        string lateModId = NewModId("late-rule");
        int lateRuleCalls = 0;
        bool lateRuleRegistered = false;
        PerfectCommsApi.RegisterVoiceRule(NewModId("rule-mutator"), _ =>
        {
            if (!lateRuleRegistered)
            {
                lateRuleRegistered = true;
                PerfectCommsApi.RegisterVoiceRule(lateModId, _ =>
                {
                    lateRuleCalls++;
                    return VoiceRuleResult.Pass;
                });
            }
            return VoiceRuleResult.Pass;
        });

        PlayerControl player = FakePlayer();
        _ = VoiceModRegistry.ResolvePlayer(
            player,
            VoicePhaseKind.Tasks,
            isLocal: true,
            isDead: false);
        Assert.Equal(0, lateRuleCalls);
        _ = VoiceModRegistry.ResolvePlayer(
            player,
            VoicePhaseKind.Tasks,
            isLocal: true,
            isDead: false);
        Assert.Equal(1, lateRuleCalls);

        string selfRemovingModId = NewModId("self-removing-observer");
        int selfRemovingCalls = 0;
        int stableObserverCalls = 0;
        PerfectCommsApi.RegisterVoicePhaseObserver(selfRemovingModId, _ =>
        {
            selfRemovingCalls++;
            PerfectCommsApi.Unregister(selfRemovingModId);
        });
        PerfectCommsApi.RegisterVoicePhaseObserver(
            NewModId("stable-observer"),
            _ => stableObserverCalls++);

        VoiceModRegistry.NotifyPhase(VoicePhaseKind.Tasks, player);
        VoiceModRegistry.NotifyPhase(VoicePhaseKind.Meeting, player);
        Assert.Equal(1, selfRemovingCalls);
        int stableAfterRemoval = stableObserverCalls;
        Assert.True(stableAfterRemoval >= 1);

        VoiceModRegistry.NotifyPhase(VoicePhaseKind.Exile, player);
        Assert.Equal(1, selfRemovingCalls);
        Assert.Equal(stableAfterRemoval + 1, stableObserverCalls);
    }

    [Fact]
    public void ConditionalAndNumericHostOptionsUseScopedValuesAndNormalizedSteps()
    {
        string modId = NewModId("host-options");
        PerfectCommsApi.RegisterModTab(modId, "Role Voice");
        PerfectCommsApi.RegisterHostOption(
            modId,
            new VoiceHostOption("parent", "Parent", false));
        PerfectCommsApi.RegisterHostEnumOption(
            modId,
            new VoiceHostEnumOption("mode", "Mode", 1, new[] { "Off", "On" })
            {
                Visible = context => context.GetOption("parent"),
            });
        PerfectCommsApi.RegisterHostNumberOption(
            modId,
            new VoiceHostNumberOption("distance", "Distance", 3.1f, 1f, 5f, 0.5f, "0.0")
            {
                Visible = context => context.GetOption("parent")
                                     && context.GetEnumOption("mode") == 1,
            });
        PerfectCommsApi.RegisterHostOption(
            modId,
            new VoiceHostOption("throwing", "Throwing visibility", true)
            {
                Visible = _ => throw new InvalidOperationException("UI callback failure"),
            });
        PerfectCommsApi.RegisterHostNumberOption(
            modId,
            new VoiceHostNumberOption("invalid", "Invalid", 2f, 5f, 1f, 0.5f));

        int tabIndex = -1;
        for (int i = 0; i < VoiceModRegistry.Tabs.Count; i++)
        {
            if (VoiceModRegistry.Tabs[i].ModId != modId) continue;
            tabIndex = i;
            break;
        }
        Assert.True(tabIndex >= 0);
        List<OptionHolder> holders = VoiceModRegistry.HoldersForTab(tabIndex);
        ModToggleHolder parent = Assert.IsType<ModToggleHolder>(
            holders.Single(holder => holder.Label == "Parent"));
        ModEnumHolder mode = Assert.IsType<ModEnumHolder>(
            holders.Single(holder => holder.Label == "Mode"));
        ModNumberHolder distance = Assert.IsType<ModNumberHolder>(
            holders.Single(holder => holder.Label == "Distance"));
        OptionHolder throwing = holders.Single(holder => holder.Label == "Throwing visibility");

        Assert.False(mode.IsVisible);
        Assert.False(distance.IsVisible);
        Assert.True(throwing.IsVisible);
        AssertClose(3f, distance.Value);

        parent.Value = true;
        Assert.True(mode.IsVisible);
        Assert.True(distance.IsVisible);
        distance.Value = 4.74f;
        AssertClose(4.5f, distance.Value);
        distance.Value = 99f;
        AssertClose(5f, distance.Value);
        distance.Value = float.NaN;
        AssertClose(3f, distance.Value);

        Assert.DoesNotContain(
            VoiceModRegistry.NumberOptionsFor(modId),
            option => option.Key == "invalid");

        int currentNumberBits = BitConverter.SingleToInt32Bits(distance.Value);
        int hash = VoiceModRegistry.SyncedValues()
            .Single(value => value.IsEnum && value.Value == currentNumberBits)
            .Hash;
        VoiceModRegistry.BeginRemoteSync();
        VoiceModRegistry.ApplySyncedValue(
            hash,
            isEnum: true,
            BitConverter.SingleToInt32Bits(4.9f));
        AssertClose(5f, VoiceModRegistry.GetNumberValue(
            VoiceModRegistry.Compose(modId, "distance")));
    }

    [Fact]
    public void HostOptionInventoryRejectsTypeCollisionsClampsEnumsAndUnregistersExactModOnly()
    {
        string parentModId = NewModId("option-scope");
        string nestedModId = parentModId + ".nested";
        _modIds.Add(nestedModId);
        int revisionBefore = VoiceModRegistry.OptionRevision;

        PerfectCommsApi.RegisterHostOption(
            parentModId,
            new VoiceHostOption("shared", "Parent", true));
        PerfectCommsApi.RegisterHostEnumOption(
            parentModId,
            new VoiceHostEnumOption("shared", "Duplicate type", 0, new[] { "Off" }));
        PerfectCommsApi.RegisterHostNumberOption(
            parentModId,
            new VoiceHostNumberOption("shared", "Duplicate type", 1f, 0f, 2f, 0.5f));
        PerfectCommsApi.RegisterHostEnumOption(
            parentModId,
            new VoiceHostEnumOption("mode", "Mode", 99, new[] { "Off", "On" }));
        PerfectCommsApi.RegisterHostEnumOption(
            parentModId,
            new VoiceHostEnumOption("invalid", "Invalid", 0, null!));
        PerfectCommsApi.RegisterHostOption(
            nestedModId,
            new VoiceHostOption("enabled", "Nested", true));

        Assert.Single(VoiceModRegistry.BoolOptionsFor(parentModId));
        VoiceHostEnumOption mode = Assert.Single(VoiceModRegistry.EnumOptionsFor(parentModId));
        Assert.Equal("mode", mode.Key);
        Assert.Equal(1, mode.Default);
        Assert.Empty(VoiceModRegistry.NumberOptionsFor(parentModId));
        Assert.True(VoiceModRegistry.OptionRevision >= revisionBefore + 3);

        string modeKey = VoiceModRegistry.Compose(parentModId, "mode");
        VoiceModRegistry.SetEnumValue(modeKey, -50);
        Assert.Equal(0, VoiceModRegistry.GetEnumValue(modeKey));
        VoiceModRegistry.SetEnumValue(modeKey, 50);
        Assert.Equal(1, VoiceModRegistry.GetEnumValue(modeKey));

        PerfectCommsApi.Unregister(parentModId);
        Assert.Empty(VoiceModRegistry.BoolOptionsFor(parentModId));
        Assert.Empty(VoiceModRegistry.EnumOptionsFor(parentModId));
        Assert.True(VoiceModRegistry.GetBoolValue(
            VoiceModRegistry.Compose(nestedModId, "enabled")));
        Assert.Single(VoiceModRegistry.BoolOptionsFor(nestedModId));
    }

    private string NewModId(string suffix)
    {
        string modId = $"tests.api-parity.{suffix}.{Guid.NewGuid():N}";
        _modIds.Add(modId);
        return modId;
    }

    private static PlayerControl FakePlayer()
        => (PlayerControl)RuntimeHelpers.GetUninitializedObject(typeof(PlayerControl));

    private static float ResolveSightLimitedMaxDistance(
        float maxDistance,
        float localLightRadius,
        bool listenerBlindedOrFlashed)
    {
        MethodInfo resolve = typeof(VoiceProximityCalculator).GetMethod(
            "ResolveSightLimitedMaxDistance",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (float)resolve.Invoke(
            null,
            new object[] { maxDistance, localLightRadius, listenerBlindedOrFlashed })!;
    }

    private static VoiceProximityResult Task(
        VoicePlayerSnapshot local,
        VoicePlayerSnapshot target,
        float localLightRadius = -1f,
        bool commsSabActive = false)
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
            false,
            false,
            commsSabActive,
            1f);

    private static VoiceProximityResult ApplyResolvedListenerMuffle(
        VoiceProximityResult result,
        bool listenerMuffled)
        => result.Audible && listenerMuffled
            ? result with { FilterMode = VoiceAudioFilterMode.ListenerMuffle }
            : result;

    private static VoicePlayerSnapshot Player(
        byte id,
        float x,
        bool isLocal = false,
        bool isDead = false,
        bool isSpectator = false,
        bool isImpostor = false,
        ExternalVoiceState? external = null,
        VoiceControlHearingMode controlMode = VoiceControlHearingMode.None,
        Vector2 controlledOrigin = default,
        float controlledLightRadius = -1f)
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
            InVent: false,
            Disconnected: false,
            IsDummy: false,
            IsVisible: true,
            IsBlackmailed: false,
            IsJailed: false,
            JailorId: byte.MaxValue,
            IsParasiteControlled: false,
            IsPuppeteerControlled: false,
            IsBlackmailedNextRound: false,
            IsSwooped: false,
            IsMedium: false,
            HasMediumSpirit: false,
            MediumSpiritPosition: Vector(x, 0f),
            IsMediatedGhost: false,
            MediatingMediumId: byte.MaxValue,
            ControlHearingMode: controlMode,
            ControlledVictimPosition: controlledOrigin,
            ControlledVictimLightRadius: controlledLightRadius,
            External: external ?? ExternalVoiceState.None);

    private static VoiceRoomSettingsSnapshot BaseSettings()
        => VoiceRoomSettingsSnapshot.Defaults with
        {
            MaxChatDistance = 10f,
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
            OnlyGhostsCanTalk = false,
            OnlyMeetingOrLobby = false,
            OnlyMeetingOrLobbyAffectsGhosts = false,
            MediumGhostVoice = (int)MediumGhostVoiceMode.None,
        };

    private static Vector2 Vector(float x, float y)
    {
        var value = default(Vector2);
        value.x = x;
        value.y = y;
        return value;
    }

    private static void AssertClose(float expected, float actual)
        => Assert.InRange(actual, expected - 0.001f, expected + 0.001f);
}
