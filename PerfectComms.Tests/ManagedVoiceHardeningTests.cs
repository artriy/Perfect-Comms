using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class ManagedVoiceHardeningTests
{
#if ANDROID
    [Theory]
    [InlineData(0, true)]
    [InlineData(1920, true)]
    [InlineData(48000, true)]
    [InlineData(48001, false)]
    [InlineData(-1, false)]
    public void AndroidSpeakerUsesOnePreallocatedBoundedCallbackScratch(int samples, bool expected)
    {
        Assert.Equal(48_000, AndroidEnginePcmSpeaker.MaximumCallbackSamples);
        Assert.Equal(expected, AndroidEnginePcmSpeaker.IsSupportedCallbackSize(samples));
    }

    [Theory]
    [InlineData(64 * 1024, 128 * 1024)]
    [InlineData(512 * 1024, 1024 * 1024)]
    [InlineData(1024 * 1024, (1024 * 1024) + 1)]
    [InlineData((1024 * 1024) + 1, (1024 * 1024) + 1)]
    public void AndroidSignalPollingGrowsWithoutExceedingProtocolCap(int current, int expected)
        => Assert.Equal(expected, MobileVoiceClient.NextSignalBufferSize(current));
#endif

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    public void PrivacyBoundariesSuppressAllVoiceInput(
        bool focused,
        bool rebinding,
        bool modalOpen,
        bool chatOpen,
        bool expected)
    {
        Assert.Equal(expected, VoiceChatPatches.ShouldSuppressVoiceInput(
            focused, rebinding, modalOpen, chatOpen));
    }

    [Theory]
    [InlineData(true, true, true, true, false, true)]
    [InlineData(true, true, false, false, true, true)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(false, true, true, true, false, false)]
    public void CriticalJoinAndDisconnectHooksAreAnAtomicHealthContract(
        bool joined,
        bool disconnect,
        bool inject,
        bool validate,
        bool reactor,
        bool expected)
    {
        Assert.Equal(expected, VoiceJoinGuard.IsCriticalPatchPairHealthy(
            joined, disconnect, inject, validate, reactor));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.1:22023", true)]
    [InlineData("localhost", true)]
    [InlineData("https://localhost:22023", true)]
    [InlineData("[::1]:22023", true)]
    [InlineData("192.0.2.10", false)]
    [InlineData("region.example.test:22023", false)]
    public void LocalAndFreeplayEndpointsCannotOwnVoice(string address, bool expected)
        => Assert.Equal(expected, VoiceChatRoom.IsLocalVoiceEndpoint(address));

    [Fact]
    public void RoomUpdateFailuresLogSparselyAndRebuildAtMostThreeTimes()
    {
        var first = VoiceChatPlugin.VoiceChatRoomDriver.DecideUpdateFailureRecovery(
            consecutiveFailures: 1, rebuildAttempts: 0,
            now: 0f, nextRebuildTime: 0f, lastLogTime: -999f);
        Assert.True(first.ShouldLog);
        Assert.False(first.ShouldRebuild);

        var third = VoiceChatPlugin.VoiceChatRoomDriver.DecideUpdateFailureRecovery(
            consecutiveFailures: 3, rebuildAttempts: 0,
            now: 0f, nextRebuildTime: 0f, lastLogTime: 0f);
        Assert.True(third.ShouldRebuild);

        var capped = VoiceChatPlugin.VoiceChatRoomDriver.DecideUpdateFailureRecovery(
            consecutiveFailures: 128, rebuildAttempts: 3,
            now: 999f, nextRebuildTime: 0f, lastLogTime: 998f);
        Assert.False(capped.ShouldRebuild);
        Assert.Equal(5f, VoiceChatPlugin.VoiceChatRoomDriver.UpdateFailureRebuildDelaySeconds(1));
        Assert.Equal(10f, VoiceChatPlugin.VoiceChatRoomDriver.UpdateFailureRebuildDelaySeconds(2));
        Assert.Equal(20f, VoiceChatPlugin.VoiceChatRoomDriver.UpdateFailureRebuildDelaySeconds(3));
    }

    [Fact]
    public void ListenerBlindProviderIsEvaluatedOnlyOncePerUnityFrame()
    {
        int calls = 0;
        VoiceProximityCalculator.LocalListenerBlindedOrFlashedProvider = () =>
        {
            calls++;
            return true;
        };
        try
        {
            Assert.True(VoiceProximityCalculator.IsLocalListenerBlindedOrFlashedThisFrame());
            Assert.True(VoiceProximityCalculator.IsLocalListenerBlindedOrFlashedThisFrame());
            Assert.Equal(1, calls);
        }
        finally
        {
            VoiceProximityCalculator.LocalListenerBlindedOrFlashedProvider = null;
            VoiceProximityCalculator.ResetSightState();
        }
    }

    [Fact]
    public void JoinGuardVersionMatchesBuiltPluginVersion()
    {
        var assemblyVersion = typeof(PerfectCommsVoiceBackend)
            .Assembly
            .GetName()
            .Version;

        Assert.NotNull(assemblyVersion);
        Assert.Equal(
            VoiceChatPlugin.VoiceChatPluginMain.Version,
            $"{assemblyVersion!.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Tasks, true)]
    [InlineData((int)VoiceGamePhase.Meeting, true)]
    [InlineData((int)VoiceGamePhase.Exile, true)]
    [InlineData((int)VoiceGamePhase.Lobby, false)]
    [InlineData((int)VoiceGamePhase.Intro, false)]
    [InlineData((int)VoiceGamePhase.EndGame, false)]
    public void MissingHudOrLocalIdentityFailsClosedOnlyInRestrictedGameplay(
        int phaseValue,
        bool expected)
    {
        var phase = (VoiceGamePhase)phaseValue;
        Assert.True(VoiceChatHudState.ShouldApplyMicStateWhileHudUnavailable(phase));
        Assert.Equal(expected, VoiceChatHudState.ShouldFailClosedWithoutLocalIdentity(phase));
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public void EveryIndependentTransmitBlockMutesCaptureButNotTheSpeaker(
        bool speakerMuted,
        bool manualMuted,
        bool pushToTalkMuted,
        bool roleMuted,
        bool policyMuted)
    {
        Assert.True(VoiceChatHudState.CombineTransmitMute(
            speakerMuted, manualMuted, pushToTalkMuted, roleMuted, policyMuted));
    }

    [Fact]
    public void DeafenCopyMatchesTheFailClosedPlaybackAndTransmitBehavior()
    {
        Assert.Equal("Toggle Deafen", VoiceChatKeybinds.ToggleDeafenDisplayName);
        Assert.Contains("mutes voice playback", VoiceChatKeybinds.ToggleDeafenHelpText);
        Assert.Contains("pauses microphone transmission", VoiceChatKeybinds.ToggleDeafenHelpText);
        Assert.True(VoiceChatHudState.CombineTransmitMute(
            speakerMuted: true,
            manualMuted: false,
            pushToTalkMuted: false,
            roleMuted: false,
            policyMuted: false));
    }

    [Fact]
    public void LocalRefreshUsesTenSecondCooldown()
    {
        float sharedLastRequestTime = -999f;

        Assert.True(VoiceChatRoom.TryConsumeLocalVoiceRefreshCooldown(
            now: 100f, ref sharedLastRequestTime));
        Assert.False(VoiceChatRoom.TryConsumeLocalVoiceRefreshCooldown(
            now: 109.999f, ref sharedLastRequestTime));
        Assert.Equal(100f, sharedLastRequestTime);
        Assert.True(VoiceChatRoom.TryConsumeLocalVoiceRefreshCooldown(
            now: 110f, ref sharedLastRequestTime));
        Assert.Equal(110f, sharedLastRequestTime);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ManagedTurnIsAutomaticUnlessCustomConfigurationTakesPrecedence(
        bool customConfigured,
        bool customInvalid,
        bool expected)
    {
        Assert.Equal(expected, PerfectCommsVoiceBackend.ShouldUseManagedTurnPolicy(
            customConfigured, customInvalid));
    }

    [Theory]
    [InlineData("turn:relay.example:3478", true)]
    [InlineData("TURN:relay.example:53?transport=UDP", true)]
    [InlineData("turn:relay.example:3478?foo=bar", false)]
    [InlineData("turn:relay.example:80?transport=tcp", false)]
    [InlineData("turn:relay.example:3478?transport=udp&transport=udp", false)]
    [InlineData("turn:relay.example:3478?transport=udp&transport=tcp", false)]
    [InlineData("turn:relay.example:3478?transport", false)]
    [InlineData("turn:relay.example:80?trans%70ort=tcp", false)]
    [InlineData("turn:relay.example:3478#fragment", false)]
    [InlineData("turns:relay.example:5349?transport=tcp", false)]
    [InlineData("stun:relay.example:3478", false)]
    public void CustomTurnAcceptsOnlyTheUdpTransportSupportedByNativeIce(string url, bool expected)
    {
        Assert.Equal(expected, PerfectCommsVoiceBackend.IsSupportedUdpTurnUrl(url));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void PendingTurnIntentAlwaysSchedulesAnotherCredentialAttempt(
        bool pendingPeers,
        bool refreshAwaiting,
        bool expected)
    {
        Assert.Equal(expected, PerfectCommsVoiceBackend.ShouldRetryPendingTurnIntent(
            pendingPeers, refreshAwaiting));
    }

    [Theory]
    [InlineData(2_499, 1_000, 0, false)]
    [InlineData(2_500, 1_000, 0, true)]
    [InlineData(20_000, 1_000, 10_000, false)]
    [InlineData(25_000, 1_000, 10_000, true)]
    public void NetworkChangeIceRestartIsDebouncedAndRateLimited(
        long nowMs,
        long signaledAtMs,
        long lastAppliedMs,
        bool expected)
    {
        Assert.Equal(expected, PerfectCommsVoiceBackend.ShouldRestartIceForNetworkChange(
            nowMs,
            signaledAtMs,
            lastAppliedMs,
            debounceMs: 1_500,
            cooldownMs: 15_000));
    }

    [Fact]
    public void SameRoomMediaResetPreservesPendingHostAuthorityGeneration()
    {
        Assert.False(VoiceChatRoom.ShouldClearHostAuthorityOnSettingsReset(
            clearRemote: false,
            preserveHostAuthority: true));
        Assert.True(VoiceChatRoom.ShouldClearHostAuthorityOnSettingsReset(
            clearRemote: true,
            preserveHostAuthority: true));
    }

    [Theory]
    [InlineData(7, -1, false, true)]
    [InlineData(7, -1, true, false)]
    [InlineData(-1, -1, false, false)]
    [InlineData(7, 8, false, false)]
    public void KnownHostBecomingUnresolvedStartsOneBoundedResyncGeneration(
        int previousHost,
        int resolvedHost,
        bool alreadyPending,
        bool expected)
    {
        Assert.Equal(expected, VoiceChatRoom.ShouldBeginUnknownHostResync(
            previousHost, resolvedHost, alreadyPending));
    }

    private static JsonElement DecodeControl(byte[] frame)
    {
        Assert.True(SidecarProtocol.TryParseFrame(
            frame, frame.Length, out var type, out var offset, out var length, out var frameLength));
        Assert.Equal(SidecarProtocol.TypeControl, type);
        Assert.Equal(frame.Length, frameLength);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame, offset, length));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void NativeInputSettingsUseBoundedFiniteSnakeCaseContract()
    {
        var root = DecodeControl(SidecarProtocol.SetInputFrame(float.NaN, float.PositiveInfinity, float.NaN));
        Assert.Equal("set-input", root.GetProperty("op").GetString());
        Assert.Equal(1f, root.GetProperty("gain").GetSingle());
        Assert.Equal(0.004f, root.GetProperty("vad_threshold").GetSingle());
        Assert.Equal(0.003f, root.GetProperty("noise_gate_threshold").GetSingle());

        root = DecodeControl(SidecarProtocol.SetInputFrame(99f, -4f, 99f));
        Assert.Equal(2f, root.GetProperty("gain").GetSingle());
        Assert.Equal(0.0001f, root.GetProperty("vad_threshold").GetSingle());
        Assert.Equal(1f, root.GetProperty("noise_gate_threshold").GetSingle());
    }

    [Fact]
    public void RuntimeSyntheticControlUsesNativeContract()
    {
        var root = DecodeControl(SidecarProtocol.SetSyntheticFrame(enabled: true));
        Assert.Equal("set-synthetic", root.GetProperty("op").GetString());
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(13, SidecarVoiceClient.Proto);
        Assert.Equal(3, SidecarProtocol.MobileAbi);
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1000)]
    [InlineData(false, true, 0)]
    public void NativeMicrophoneMonitorUsesBoundedExplicitContract(
        bool enabled,
        bool delayed,
        int expectedDelayMs)
    {
        var root = DecodeControl(SidecarProtocol.SetMonitorFrame(enabled, delayed, 99f));
        Assert.Equal("set-monitor", root.GetProperty("op").GetString());
        Assert.Equal(enabled, root.GetProperty("enabled").GetBoolean());
        Assert.Equal(expectedDelayMs, root.GetProperty("delay_ms").GetInt32());
        Assert.Equal(2f, root.GetProperty("gain").GetSingle());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void NativeNoiseSuppressionContractSelectsVeryHighOnlyWhenEnabled(
        bool enabled,
        bool stronger,
        bool expectedVeryHigh)
    {
        var root = DecodeControl(SidecarProtocol.SetDspFrame(
            aec: true,
            agc: false,
            ns: enabled,
            nsVeryHigh: stronger,
            hpf: true));

        Assert.Equal("set-dsp", root.GetProperty("op").GetString());
        Assert.Equal(enabled, root.GetProperty("ns").GetBoolean());
        Assert.Equal(expectedVeryHigh, root.GetProperty("ns_very_high").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeDiagnosticsSamplingUsesExplicitRuntimeContract(bool enabled)
    {
        var root = DecodeControl(SidecarProtocol.SetDiagnosticsFrame(enabled));
        Assert.Equal("set-diagnostics", root.GetProperty("op").GetString());
        Assert.Equal(enabled, root.GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData(true, "start")]
    [InlineData(false, "stop")]
    public void NativeMicActivityUsesExplicitPrivacyBoundaryContract(bool active, string expectedOp)
    {
        var root = DecodeControl(active ? SidecarProtocol.StartFrame() : SidecarProtocol.StopFrame());
        Assert.Equal(expectedOp, root.GetProperty("op").GetString());
    }

    [Fact]
    public void EmptyDeviceIdExplicitlySelectsOperatingSystemDefault()
    {
        var input = DecodeControl(SidecarProtocol.SelectDeviceFrame(string.Empty));
        Assert.Equal("select-device", input.GetProperty("op").GetString());
        Assert.Equal(string.Empty, input.GetProperty("id").GetString());

        var output = DecodeControl(SidecarProtocol.SelectOutputDeviceFrame(string.Empty));
        Assert.Equal("select-output-device", output.GetProperty("op").GetString());
        Assert.Equal(string.Empty, output.GetProperty("id").GetString());
    }

    [Fact]
    public void StreamingDeviceUpdatesAreAcceptedAndParsed()
    {
        const string json = "{\"op\":\"devices\",\"devices\":[{\"id\":\"mic-1\",\"name\":\"Mic One\",\"default\":true}]," +
                            "\"outputDevices\":[{\"id\":\"spk-1\",\"name\":\"Speaker One\",\"default\":true}]}";

        Assert.True(SidecarVoiceClient.TryReadDeviceUpdate(json, out var inputs, out var outputs));
        Assert.Equal(new VoiceDeviceInfo("mic-1", "Mic One", true), Assert.Single(inputs));
        Assert.Equal(new VoiceDeviceInfo("spk-1", "Speaker One", true), Assert.Single(outputs));
        Assert.False(SidecarVoiceClient.TryReadDeviceUpdate(
            "{\"op\":\"devices\",\"devices\":[]}", out _, out _));
    }

    [Fact]
    public void DeviceUpdatesKeepDuplicateDisplayNamesDistinctByStableId()
    {
        const string json = "{\"op\":\"devices\",\"devices\":[" +
                            "{\"id\":\"mic-a\",\"name\":\"USB Audio\",\"default\":false}," +
                            "{\"id\":\"mic-b\",\"name\":\"USB Audio\",\"default\":true}]," +
                            "\"outputDevices\":[]}";

        Assert.True(SidecarVoiceClient.TryReadDeviceUpdate(json, out var inputs, out _));
        Assert.Equal(2, inputs.Length);
        Assert.Equal("mic-a", inputs[0].Id);
        Assert.Equal("mic-b", inputs[1].Id);
        Assert.Equal(inputs[0].Name, inputs[1].Name);
        Assert.False(inputs[0].IsDefault);
        Assert.True(inputs[1].IsDefault);
    }

    [Fact]
    public void DeviceUpdatesRejectMissingOrDuplicateStableIds()
    {
        Assert.False(SidecarVoiceClient.TryReadDeviceUpdate(
            "{\"op\":\"devices\",\"devices\":[{\"name\":\"Mic\"}],\"outputDevices\":[]}",
            out _, out _));
        Assert.False(SidecarVoiceClient.TryReadDeviceUpdate(
            "{\"op\":\"devices\",\"devices\":[" +
            "{\"id\":\"same\",\"name\":\"Mic A\"},{\"id\":\"same\",\"name\":\"Mic B\"}]," +
            "\"outputDevices\":[]}", out _, out _));
    }

    [Fact]
    public void PeerLevelParserIsBoundedAndClampsFinitePeaks()
    {
        const string json = "{\"op\":\"peer-levels\",\"levels\":[" +
            "{\"peer_id\":\"7\",\"peak\":1.5}," +
            "{\"peer_id\":\"8\",\"peak\":-1}," +
            "{\"peer_id\":\"bad\",\"peak\":1e999}," +
            "{\"peer_id\":\"9\",\"peak\":0.25}]}";

        Assert.True(SidecarProtocol.TryReadPeerLevels(json, out var levels));
        Assert.Equal(3, levels.Count);
        Assert.Equal(("7", 1f), (levels[0].PeerId, levels[0].Peak));
        Assert.Equal(("8", 0f), (levels[1].PeerId, levels[1].Peak));
        Assert.Equal(("9", 0.25f), (levels[2].PeerId, levels[2].Peak));

        var tooMany = "{\"op\":\"peer-levels\",\"levels\":[" +
            string.Join(',', Enumerable.Range(0, SidecarProtocol.MaxPeerLevelsPerBatch + 1)
                .Select(i => $"{{\"peer_id\":\"{i}\",\"peak\":0.1}}")) + "]}";
        Assert.False(SidecarProtocol.TryReadPeerLevels(tooMany, out _));
    }

    #if WINDOWS
    [Fact]
    public void DesktopRpcWaitsForConfiguredHealthyHelper()
    {
        Assert.False(PerfectCommsVoiceBackend.CanPumpDesktopRpc(false, CaptureHealth.Healthy));
        Assert.False(PerfectCommsVoiceBackend.CanPumpDesktopRpc(true, CaptureHealth.Dead));
        Assert.True(PerfectCommsVoiceBackend.CanPumpDesktopRpc(true, CaptureHealth.Healthy));
    }

    [Fact]
    public void RecoverableMicErrorDoesNotClassifyControlTransportAsDead()
    {
        Assert.True(SidecarVoiceClient.IsRecoverableHelperError("mic-error"));
        Assert.True(SidecarVoiceClient.IsRecoverableHelperError("MIC-ERROR"));
        Assert.False(SidecarVoiceClient.IsRecoverableHelperError("busy"));
        Assert.False(SidecarVoiceClient.IsRecoverableHelperError("rtc-error"));
        Assert.False(SidecarVoiceClient.IsRecoverableHelperError(null));
    }

    [Fact]
    public void SidecarDeadNotificationGateResetsForEachStartGeneration()
    {
        long state = 0;
        SidecarVoiceClient.ResetDeadNotification(ref state, generation: 7);
        Assert.True(SidecarVoiceClient.TryBeginDeadNotification(ref state, generation: 7));
        Assert.False(SidecarVoiceClient.TryBeginDeadNotification(ref state, generation: 7));

        SidecarVoiceClient.ResetDeadNotification(ref state, generation: 8);

        Assert.False(SidecarVoiceClient.TryBeginDeadNotification(ref state, generation: 7));
        Assert.True(SidecarVoiceClient.TryBeginDeadNotification(ref state, generation: 8));
    }

    [Fact]
    public void RetiredSidecarWorkersCannotActOnAReplacementGeneration()
    {
        Assert.True(SidecarVoiceClient.IsWorkerGenerationCurrent(
            running: true, currentGeneration: 8, workerGeneration: 8));
        Assert.False(SidecarVoiceClient.IsWorkerGenerationCurrent(
            running: true, currentGeneration: 8, workerGeneration: 7));
        Assert.False(SidecarVoiceClient.IsWorkerGenerationCurrent(
            running: false, currentGeneration: 8, workerGeneration: 8));
    }

    [Fact]
    public void SidecarHandshakeWindowCrossesTheLegacyIntTickRollover()
    {
        long startedAt = int.MaxValue - 10L;

        Assert.True(SidecarVoiceClient.IsHandshakePending(startedAt, startedAt + 20));
        Assert.True(SidecarVoiceClient.IsHandshakePending(startedAt, startedAt + 3_999));
        Assert.False(SidecarVoiceClient.IsHandshakePending(startedAt, startedAt + 4_000));
        Assert.False(SidecarVoiceClient.IsHandshakePending(startedAt, startedAt - 1));
    }

    [Fact]
    public void SidecarRecoveryBackoffContinuesAfterCircuitBreaker()
    {
        Assert.Equal(750, PerfectCommsVoiceBackend.VoiceRecoveryDelayMs(0));
        Assert.Equal(1_500, PerfectCommsVoiceBackend.VoiceRecoveryDelayMs(1));
        Assert.Equal(3_000, PerfectCommsVoiceBackend.VoiceRecoveryDelayMs(2));
        Assert.Equal(24_000, PerfectCommsVoiceBackend.VoiceRecoveryDelayMs(5));
        Assert.Equal(30_000, PerfectCommsVoiceBackend.VoiceRecoveryDelayMs(6));
        Assert.Equal(30_000, PerfectCommsVoiceBackend.VoiceRecoveryDelayMs(100));
    }

    [Fact]
    public void UnstableHeartbeatFlapsKeepTheirSpentBudgetAndIncreasingBackoff()
    {
        var immediateRestarts = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var immediate = PerfectCommsVoiceBackend.DecideHeartbeatRecovery(
                immediateRestarts,
                uptimeTicks: TimeSpan.FromSeconds(2).Ticks);
            Assert.True(immediate.RestartImmediately);
            Assert.False(immediate.ResetRecoveryBackoff);
            immediateRestarts = immediate.ImmediateRestarts;
        }

        var firstBreaker = PerfectCommsVoiceBackend.DecideHeartbeatRecovery(
            immediateRestarts,
            uptimeTicks: TimeSpan.FromSeconds(2).Ticks);
        Assert.False(firstBreaker.RestartImmediately);
        Assert.Equal(3, firstBreaker.ImmediateRestarts);
        Assert.Equal(750, PerfectCommsVoiceBackend.VoiceRecoveryDelayMs(0));

        // A short-lived successful start does not alter either counter. Its next loss therefore
        // remains on the breaker and advances to the next delay instead of returning to 750ms.
        var afterShortSuccess = PerfectCommsVoiceBackend.DecideHeartbeatRecovery(
            firstBreaker.ImmediateRestarts,
            uptimeTicks: TimeSpan.FromSeconds(2).Ticks);
        Assert.False(afterShortSuccess.RestartImmediately);
        Assert.Equal(1_500, PerfectCommsVoiceBackend.VoiceRecoveryDelayMs(1));
    }

    [Fact]
    public void StableHeartbeatSessionEarnsOneFreshImmediateRestartBudget()
    {
        var decision = PerfectCommsVoiceBackend.DecideHeartbeatRecovery(
            priorImmediateRestarts: 3,
            uptimeTicks: TimeSpan.FromSeconds(30).Ticks);

        Assert.True(decision.RestartImmediately);
        Assert.Equal(1, decision.ImmediateRestarts);
        Assert.True(decision.ResetRecoveryBackoff);
    }
    #endif

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void OnlyForcedSameRoomBackendRebuildPreservesRecoveryEscalation(
        bool forceRebuild,
        bool continuingSameRoom,
        bool expected)
    {
        Assert.Equal(
            expected,
            VoiceChatRoom.ShouldPreserveMissingPeerRecoveryState(forceRebuild, continuingSameRoom));
    }

    [Fact]
    public void PreservedSameRoomCollapseAdvancesRecoveryBackoff()
    {
        Assert.True(VoiceChatRoom.ShouldPreserveMissingPeerRecoveryState(
            forceRebuild: true,
            continuingSameRoom: true));
        Assert.Equal(5f, VoiceChatRoom.RecoveryBackoffSeconds(0));
        Assert.Equal(10f, VoiceChatRoom.RecoveryBackoffSeconds(1));
        Assert.Equal(20f, VoiceChatRoom.RecoveryBackoffSeconds(2));
    }

    [Fact]
    public void HostMigrationPolicyExpiresAndHudIndependentAudioTickCoversEveryPhase()
    {
        Assert.True(VoiceChatRoom.CanUseTransitionalHostPolicy(
            resyncPending: true, hasRemoteSnapshot: true, waitedSeconds: 9.99f));
        Assert.False(VoiceChatRoom.CanUseTransitionalHostPolicy(
            resyncPending: true, hasRemoteSnapshot: true, waitedSeconds: 10f));
        Assert.True(VoiceChatRoom.HasHostPolicyResyncTimedOut(true, 10f));
        Assert.False(VoiceChatRoom.HasHostPolicyResyncTimedOut(false, 100f));

        Assert.True(VoiceChatHudState.ShouldApplyMicStateWhileHudUnavailable(VoiceGamePhase.EndGame));
        Assert.True(VoiceChatHudState.ShouldApplyMicStateWhileHudUnavailable(VoiceGamePhase.Lobby));
        Assert.True(VoiceChatHudState.ShouldApplyMicStateWhileHudUnavailable(VoiceGamePhase.Tasks));
        Assert.True(VoiceChatHudState.ShouldApplyMicStateWhileHudUnavailable(VoiceGamePhase.Meeting));
    }

    #if WINDOWS
    [Fact]
    public void RpcPumpDeadlinesAdvanceIndependently()
    {
        long rosterNext = 0;
        long sessionNext = 0;

        Assert.True(PerfectCommsVoiceBackend.AdvancePumpDeadline(1_000, ref rosterNext, 250));
        Assert.True(PerfectCommsVoiceBackend.AdvancePumpDeadline(1_000, ref sessionNext, 100));
        Assert.Equal(1_250, rosterNext);
        Assert.Equal(1_100, sessionNext);

        Assert.False(PerfectCommsVoiceBackend.AdvancePumpDeadline(1_099, ref sessionNext, 100));
        Assert.True(PerfectCommsVoiceBackend.AdvancePumpDeadline(1_100, ref sessionNext, 100));
        Assert.False(PerfectCommsVoiceBackend.AdvancePumpDeadline(1_100, ref rosterNext, 250));
        Assert.True(PerfectCommsVoiceBackend.AdvancePumpDeadline(1_250, ref rosterNext, 250));
    }

    [Fact]
    public void RpcSessionIsReplacedWhenLocalClientIdChangesInPlace()
    {
        Assert.False(PerfectCommsVoiceBackend.ShouldReplaceRpcSession(7, 7));
        Assert.True(PerfectCommsVoiceBackend.ShouldReplaceRpcSession(7, 11));
    }

    [Fact]
    public void SidecarLivenessUsesLevelEventsInsteadOfRetiredPcmFrames()
    {
        var silentLevelCadence = PerfectCommsVoiceBackend.SelectCaptureActivity(
            nativeSidecarOwnsCapture: true, sidecarLevelEvents: 4, managedPcmSamples: 0);
        Assert.Equal(4, silentLevelCadence);
        // Activity counts callback/telemetry cadence, not amplitude: a working user who simply
        // stays silent remains healthy and never drives a false capture restart.
        Assert.Equal(CaptureHealth.Healthy,
            CaptureSupervisor.ClassifySamples(silentLevelCadence, unmutedAndCapturing: true));
        Assert.Equal(960, PerfectCommsVoiceBackend.SelectCaptureActivity(
            nativeSidecarOwnsCapture: false, sidecarLevelEvents: 0, managedPcmSamples: 960));
        Assert.Equal(0, PerfectCommsVoiceBackend.SelectCaptureActivity(
            nativeSidecarOwnsCapture: true, sidecarLevelEvents: -1, managedPcmSamples: 960));
    }

    [Fact]
    public void DuplicateRouteWinnerIsStable()
    {
        Assert.True(PerfectCommsVoiceBackend.PreferSecondRouteRecord(
            "rpc-route:9", "rpc-route:7"));
        Assert.False(PerfectCommsVoiceBackend.PreferSecondRouteRecord(
            "rpc-route:7", "rpc-route:9"));
    }

    [Fact]
    public void RemoteSpeakingPeakHasTwoHundredFiftyMillisecondHold()
    {
        var start = DateTime.UtcNow.Ticks;
        Assert.True(PerfectCommsVoiceBackend.ShouldHoldPreviousRemoteLevel(
            previous: 0.5f, previousTicks: start, next: 0f,
            nowTicks: start + TimeSpan.FromMilliseconds(249).Ticks));
        Assert.False(PerfectCommsVoiceBackend.ShouldHoldPreviousRemoteLevel(
            previous: 0.5f, previousTicks: start, next: 0f,
            nowTicks: start + TimeSpan.FromMilliseconds(250).Ticks));
        Assert.False(PerfectCommsVoiceBackend.ShouldHoldPreviousRemoteLevel(
            previous: 0.5f, previousTicks: start, next: 0.8f,
            nowTicks: start + TimeSpan.FromMilliseconds(10).Ticks));
    }
    #endif
}
