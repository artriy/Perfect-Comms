using System.Diagnostics;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarDiagnosticsSafetyTests
{
    [Fact]
    public void ProcessDiagnosticsDrainBothRedirectedStreams()
    {
        var payload = new string('x', 160);
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("cmd.exe");
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/q");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(
                $"(for /L %i in (1,1,5000) do @echo stdout-{payload}) " +
                $"& (for /L %i in (1,1,5000) do @echo stderr-{payload} 1>&2)");
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                $"i=0; while [ $i -lt 5000 ]; do printf '%s\\n' 'stdout-{payload}'; i=$((i+1)); done; " +
                $"i=0; while [ $i -lt 5000 ]; do printf '%s\\n' 'stderr-{payload}' >&2; i=$((i+1)); done");
        }
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var diagnostics = new SidecarProcessDiagnostics(process!, string.Empty, "test-drain");
        try
        {
            diagnostics.Attach();
            Assert.True(process!.WaitForExit(10_000), "redirected helper output was not fully drained");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            try { if (!process!.HasExited) process.Kill(); } catch { }
            diagnostics.Complete("test-finished");
        }
    }

    [Fact]
    public void StderrSanitizerRedactsTokensAndSignalingBodies()
    {
        const string token = "0123456789ABCDEF0123456789ABCDEF";

        var tokenLine = SidecarLauncher.SanitizeStderrForDiagnostics(
            $"fatal token={token}\r\nnext-line",
            token);
        var candidateLine = SidecarLauncher.SanitizeStderrForDiagnostics(
            "candidate=candidate:1 1 udp 2122260223 192.0.2.1 5000 typ host",
            token);
        var sdpLine = SidecarLauncher.SanitizeStderrForDiagnostics(
            "remote sdp: v=0 o=- 123 2 IN IP4 127.0.0.1",
            token);

        Assert.DoesNotContain(token, tokenLine);
        Assert.DoesNotContain('\r', tokenLine);
        Assert.DoesNotContain('\n', tokenLine);
        Assert.Contains("token=[redacted]", tokenLine);
        Assert.DoesNotContain("192.0.2.1", candidateLine);
        Assert.Contains("candidate=[redacted]", candidateLine);
        Assert.DoesNotContain("v=0", sdpLine);
        Assert.Contains("sdp:[redacted]", sdpLine);
    }

    [Fact]
    public void StderrSanitizerBoundsLineLength()
    {
        var safe = SidecarLauncher.SanitizeStderrForDiagnostics(new string('x', 2000), string.Empty);

        Assert.Equal(SidecarProcessDiagnostics.MaxLineChars + 3, safe.Length);
        Assert.EndsWith("...", safe);
    }

    [Fact]
    public void DeviceDiagnosticsUseStableFingerprintWithoutRawId()
    {
        const string id = "private microphone device name";

        var first = SidecarVoiceClient.DescribeDeviceForDiagnostics(id);
        var second = SidecarVoiceClient.DescribeDeviceForDiagnostics(id);

        Assert.Equal(first, second);
        Assert.DoesNotContain(id, first);
        Assert.Contains("default=false", first);
        Assert.Contains("idHash=", first);
        Assert.Equal("default=true", SidecarVoiceClient.DescribeDeviceForDiagnostics(string.Empty));
    }

    [Fact]
    public void MediaStateDiagnosticsFingerprintRequestedAndResolvedDevices()
    {
        const string requested = "Private Headset With Serial 123";
        const string resolved = "Private Default Microphone Owner Name";
        var json = $$"""
            {"op":"media-state","schema":1,"direction":"capture","state":"stream-started","stream_generation":2,"running":true,
             "requested_device":"{{requested}}","resolved_device":"{{resolved}}","requested_matched":false,
             "fell_back_to_default":true,"sample_rate":44100,"channels":2,"sample_format":"F32"}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(json, json.Length, out var details));
        Assert.DoesNotContain(requested, details);
        Assert.DoesNotContain(resolved, details);
        Assert.Contains("requested=default=false idHash=", details);
        Assert.Contains("resolved=default=false idHash=", details);
        Assert.Contains("fellBackToDefault=true", details);
        Assert.Contains("rate=44100", details);
        Assert.Contains("schema=1 schemaSupported=true", details);
        Assert.DoesNotContain("retryAttempt=", details);
        Assert.DoesNotContain("callbackFrames=", details);
    }

    [Fact]
    public void MediaStateDiagnosticsRequireDirectionAndState()
    {
        Assert.False(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(
            "{\"op\":\"media-state\",\"direction\":\"capture\"}",
            42,
            out _));
    }

    [Fact]
    public void SparseMediaStateEventsOnlyDescribeFieldsRelevantToTheirState()
    {
        const string command = """
            {"op":"media-state","direction":"capture","state":"command-accepted",
             "command_seq":0,"stream_generation":0,"open_attempt":0,"changed":false,"running":false,
             "retry_attempt":0,"retry_delay_ms":0,"requested_default":false,
             "sample_rate":0,"channels":0,"callback_frames":0,"elapsed_ms":0}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(
            command, command.Length, out var details));
        Assert.Contains("schema=na schemaSupported=na", details);
        Assert.Contains("streamGeneration=0 running=false", details);
        Assert.Contains("action=na commandSeq=0 changed=false openAttempt=0", details);
        Assert.DoesNotContain("requested=", details);
        Assert.DoesNotContain("resolved=", details);
        Assert.DoesNotContain("retryAttempt=", details);
        Assert.DoesNotContain("rate=", details);
        Assert.DoesNotContain("callbackFrames=", details);
    }

    [Fact]
    public void MediaStateDiagnosticsDistinguishMissingValuesFromExplicitZeroAndDefault()
    {
        const string explicitZero = """
            {"op":"media-state","schema":1,"direction":"capture","state":"first-callback",
             "stream_generation":2,"running":true,"callback_frames":0,"elapsed_ms":0}
            """;
        const string missing = """
            {"op":"media-state","schema":1,"direction":"capture","state":"first-callback",
             "stream_generation":2,"running":true}
            """;
        const string defaultStart = """
            {"op":"media-state","schema":1,"direction":"capture","state":"starting",
             "stream_generation":3,"running":true,"requested_default":true}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(
            explicitZero, explicitZero.Length, out var explicitDetails));
        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(
            missing, missing.Length, out var missingDetails));
        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(
            defaultStart, defaultStart.Length, out var defaultDetails));

        Assert.Contains("callbackFrames=0 elapsedMs=0", explicitDetails);
        Assert.Contains("callbackFrames=na elapsedMs=na", missingDetails);
        Assert.Contains("requested=default=true requestedDefault=true", defaultDetails);
        Assert.DoesNotContain("resolved=", defaultDetails);
    }

    [Fact]
    public void MediaStateTryParserRejectsMalformedJsonWithoutThrowing()
    {
        Assert.False(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(
            "{not-json", 9, out var details));
        Assert.Equal(string.Empty, details);
    }

    [Fact]
    public void FutureMediaStateSchemaRemainsSafeAndExplicitlyUnsupported()
    {
        const string json = """
            {"op":"media-state","schema":99,"direction":"capture","state":"future-extension",
             "stream_generation":0,"running":false,"private_extension":"do not log this"}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(
            json, json.Length, out var details));
        Assert.Contains("schema=99 schemaSupported=false", details);
        Assert.Contains("streamGeneration=0 running=false", details);
        Assert.Contains("extensionFieldsIgnored=true", details);
        Assert.DoesNotContain("do not log this", details);
    }

    [Fact]
    public void OldHelperStatsWithoutMediaDiagnosticsProduceNoExtraLines()
    {
        const string json = """
            {"op":"stats","capture_frames":1,"opus_encoded":1,"rtp_tx_ok":1,
             "rtp_rx_packets":1,"decode_frames":1,"mix_rounds":1,"playback_queued_pairs":1}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeMediaDiagnosticsForDiagnostics(
            json, json.Length, out var lines));
        Assert.Empty(lines);
    }

    [Fact]
    public void NestedMediaDiagnosticsArePrivacySafeBoundedAndPreserveMissingValues()
    {
        const string captureRequested = "Private Capture Device Owner Serial 123";
        const string captureResolved = "Private Resolved Capture Device";
        const string playbackRequested = "Private Playback Device";
        const string playbackResolved = "Private Resolved Playback Device";
        var json = $$"""
            {"op":"stats","diagnostics":{"schema":1,"window_seq":0,"window_ms":0,
              "media_state_events_dropped":2,
               "capture":{"stream_generation":0,"state":"running\r\nforged","running":false,
                 "healthy":true,"synthetic":false,"requested_device":"{{captureRequested}}",
                 "resolved_device":"{{captureResolved}}","requested_default":false,
                 "stream_started":false,"first_callback_seen":false,"callback_seen":false,"callback_window_seen":false,
                 "callback_interval_seen":false,"callback_interval_window_seen":false,
                 "start_to_open_ms":0,"open_to_first_callback_ms":0,
                 "stream_age_ms":0,"callbacks_total":0,"callback_age_ms":0,"callback_frames_last":0,
                 "callback_frames_min":0,"callback_frames_max":0,
                 "callback_interval_last_us":0,"callback_interval_max_us":0,"ring_has_frames":false,
                 "ring_oldest_frame_age_ms":0,"encoder_frame_seen":false,"encoder_window_seen":false,
                 "encoder_pop_age_last_ms":0,
                 "encoder_pop_age_max_ms":0,"stale_generation_frames":3,
                 "raw_input":{"samples":0,"dropped_records":4,"rms":0.0},"pre_dsp":{} },
               "playback":{"stream_generation":0,"state":"running","running":false,
                 "requested_device":"{{playbackRequested}}","resolved_device":"{{playbackResolved}}",
                 "requested_default":false,"callbacks_total":0,"callback_seen":false,
                 "callback_interval_seen":false,"callback_interval_window_seen":false,
                 "callback_age_ms":0,"callback_frames_last":0,
                 "callback_interval_last_us":0,"callback_interval_max_us":0} } }
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeMediaDiagnosticsForDiagnostics(
            json, json.Length, out var lines));
        Assert.Equal(3, lines.Length);
        var rendered = string.Join("\n", lines.Select(line => $"{line.Category} {line.Details}"));

        Assert.DoesNotContain(captureRequested, rendered);
        Assert.DoesNotContain(captureResolved, rendered);
        Assert.DoesNotContain(playbackRequested, rendered);
        Assert.DoesNotContain(playbackResolved, rendered);
        Assert.Contains("schema=1 schemaSupported=true windowSeq=0 windowMs=0", rendered);
        Assert.Contains("mediaStateEventsDropped=2", rendered);
        Assert.Contains("streamGeneration=0", rendered);
        Assert.Contains("staleGenerationFrames=3", rendered);
        Assert.Contains("startToOpenMs=na openToFirstCallbackMs=na streamAgeMs=na", rendered);
        Assert.Contains("callbacksTotal=0 callbackAgeMs=na", rendered);
        Assert.Contains("callbackFramesLast=na callbackFramesMin=na callbackFramesMax=na", rendered);
        Assert.Contains("ringOldestAgeMs=na encoderPopAgeLastMs=na encoderPopAgeMaxMs=na", rendered);
        Assert.Contains("rawPresent=true rawSamples=0", rendered);
        Assert.Contains("rawDroppedRecords=4", rendered);
        Assert.Contains("rawRms=0.000000", rendered);
        Assert.Contains("preDspPresent=true preDspSamples=na", rendered);
        Assert.Contains("postDspPresent=false", rendered);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain('\r', line.Details);
            Assert.DoesNotContain('\n', line.Details);
            Assert.True(line.Details.Length < 8192, $"{line.Category} was {line.Details.Length} chars");
        });
    }

    [Fact]
    public void NativePresenceFlagsDistinguishUnavailableMeasurementsFromExplicitZero()
    {
        const string json = """
            {"op":"stats","diagnostics":{"schema":1,
              "capture":{"stream_started":true,"first_callback_seen":true,"callback_seen":true,"callback_window_seen":true,
                "callback_interval_seen":true,"callback_interval_window_seen":true,
                "start_to_open_ms":0,"open_to_first_callback_ms":0,
                "stream_age_ms":0,"callback_age_ms":0,"callback_frames_last":0,
                "callback_frames_min":0,"callback_frames_max":0,
                "callback_interval_last_us":0,"callback_interval_max_us":0,"ring_has_frames":true,
                "ring_oldest_frame_age_ms":0,"encoder_frame_seen":true,"encoder_window_seen":true,
                "encoder_pop_age_last_ms":0,
                "encoder_pop_age_max_ms":0},
              "playback":{"callback_seen":true,"callback_interval_seen":true,
                "callback_interval_window_seen":true,"callback_age_ms":0,
                "callback_frames_last":0,"callback_interval_last_us":0,"callback_interval_max_us":0}}}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeMediaDiagnosticsForDiagnostics(
            json, json.Length, out var lines));
        var capture = Assert.Single(lines, line => line.Category == "sidecar.native.capture");
        var playback = Assert.Single(lines, line => line.Category == "sidecar.native.playback");

        Assert.Contains("startToOpenMs=0 openToFirstCallbackMs=0 streamAgeMs=0", capture.Details);
        Assert.Contains("callbackAgeMs=0 callbackFramesLast=0 callbackFramesMin=0 callbackFramesMax=0", capture.Details);
        Assert.Contains("callbackIntervalLastUs=0 callbackIntervalMaxUs=0", capture.Details);
        Assert.Contains("ringOldestAgeMs=0 encoderPopAgeLastMs=0 encoderPopAgeMaxMs=0", capture.Details);
        Assert.Contains("callbackAgeMs=0 callbackFramesLast=0", playback.Details);
        Assert.Contains("callbackIntervalLastUs=0 callbackIntervalMaxUs=0", playback.Details);
    }

    [Fact]
    public void CaptureClockDiagnosticsPreserveSignedMeasurementsAndAvailability()
    {
        const string unavailable = """
            {"op":"stats","diagnostics":{"schema":1,
              "capture":{"capture_clock_delta_seen":false,
                "capture_clock_delta_last_us":0,"capture_clock_expected_delta_us":0,
                "capture_clock_delta_error_us":0,"capture_clock_bridge_residual_seen":false,
                "capture_clock_bridge_residual_us":0,"capture_clock_status":"anchor-established",
                "last_timestamp_discontinuity_reason":"unavailable"}}}
            """;
        const string present = """
            {"op":"stats","diagnostics":{"schema":1,
              "capture":{"capture_clock_delta_seen":true,
                "capture_clock_delta_last_us":10000,"capture_clock_expected_delta_us":10000,
                "capture_clock_delta_error_us":-125,"capture_clock_bridge_residual_seen":true,
                "capture_clock_bridge_residual_us":-3000,"capture_clock_status":"continuous\r\nforged",
                "last_timestamp_discontinuity_reason":"backend-capture-delta-mismatch"}}}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeMediaDiagnosticsForDiagnostics(
            unavailable, unavailable.Length, out var unavailableLines));
        var unavailableCapture = Assert.Single(
            unavailableLines, line => line.Category == "sidecar.native.capture");
        Assert.Contains(
            "captureClockDeltaSeen=false captureClockDeltaLastUs=na captureClockExpectedDeltaUs=na captureClockDeltaErrorUs=na",
            unavailableCapture.Details);
        Assert.Contains(
            "captureClockBridgeResidualSeen=false captureClockBridgeResidualUs=na",
            unavailableCapture.Details);

        Assert.True(SidecarVoiceClient.TryDescribeNativeMediaDiagnosticsForDiagnostics(
            present, present.Length, out var presentLines));
        var presentCapture = Assert.Single(
            presentLines, line => line.Category == "sidecar.native.capture");
        Assert.Contains(
            "captureClockDeltaSeen=true captureClockDeltaLastUs=10000 captureClockExpectedDeltaUs=10000 captureClockDeltaErrorUs=-125",
            presentCapture.Details);
        Assert.Contains(
            "captureClockBridgeResidualSeen=true captureClockBridgeResidualUs=-3000",
            presentCapture.Details);
        Assert.Contains("captureClockStatus=continuous  forged", presentCapture.Details);
        Assert.Contains(
            "lastTimestampDiscontinuityReason=backend-capture-delta-mismatch",
            presentCapture.Details);
        Assert.DoesNotContain('\r', presentCapture.Details);
        Assert.DoesNotContain('\n', presentCapture.Details);
    }

    [Fact]
    public void FutureNestedDiagnosticsSchemaIsLoggedWithoutAssumingMissingFieldsAreZero()
    {
        const string json = """
            {"op":"stats","diagnostics":{"schema":99,"window_seq":0,
              "capture":{"stream_generation":0,"running":false}}}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeMediaDiagnosticsForDiagnostics(
            json, json.Length, out var lines));
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Contains("schema=99 schemaSupported=false", line.Details));
        Assert.Contains("streamGeneration=0 state=na running=false", lines[0].Details);
        Assert.Contains("healthy=na", lines[0].Details);
        Assert.Contains("windowMs=na", lines[0].Details);
    }

    [Fact]
    public void NestedDiagnosticsTryParserRejectsMalformedJsonWithoutThrowing()
    {
        Assert.False(SidecarVoiceClient.TryDescribeNativeMediaDiagnosticsForDiagnostics(
            "{not-json", 9, out var lines));
        Assert.Empty(lines);
    }

    [Fact]
    public void DspAndInputStatsDistinguishRequestedAppliedAndMissingValues()
    {
        const string current = """
            {"dsp_config_generation":4,
             "dsp_requested_aec":true,"dsp_requested_agc":true,
             "dsp_requested_ns":false,"dsp_requested_hpf":true,
             "dsp_apm_loaded":true,"dsp_config_fully_applied":false,
             "dsp_applied_aec":true,"dsp_applied_agc":false,
             "dsp_applied_ns":false,"dsp_applied_hpf":true,
             "input_gain":1.25,"input_vad_threshold":0.0001,
             "input_noise_gate_threshold":0.003}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeDspInputForDiagnostics(current, out var details));
        Assert.Contains("dspConfigGeneration=4", details);
        Assert.Contains("dspRequestedAec=true dspRequestedAgc=true dspRequestedNs=false dspRequestedHpf=true", details);
        Assert.Contains("dspApmLoaded=true dspConfigFullyApplied=false", details);
        Assert.Contains("dspAppliedAec=true dspAppliedAgc=false dspAppliedNs=false dspAppliedHpf=true", details);
        Assert.Contains("inputGain=1.25 vadThreshold=0.0001 noiseGateThreshold=0.003", details);

        Assert.True(SidecarVoiceClient.TryDescribeNativeDspInputForDiagnostics("{}", out var oldDetails));
        Assert.Contains("dspConfigGeneration=na", oldDetails);
        Assert.Contains("dspApmLoaded=na dspConfigFullyApplied=na", oldDetails);
        Assert.Contains("inputGain=na vadThreshold=na noiseGateThreshold=na", oldDetails);
        Assert.False(SidecarVoiceClient.TryDescribeNativeDspInputForDiagnostics("{bad", out _));
    }

    [Fact]
    public void NetworkPathStatsExposeQualityWithoutLeakingCandidateIdentifiersOrAddresses()
    {
        const string json = """
            {"network_paths":[{
              "peer_id":"peer-7\r\nforged","generation":4,
              "candidate_pair_id":"192.0.2.1:5000-to-198.51.100.2:6000",
              "candidate_state":"succeeded\nforged","local_candidate_type":"relay",
              "remote_candidate_type":"srflx","relay":true,"current_rtt_ms":247.5,
              "available_outgoing_bitrate":32000,"available_incoming_bitrate":64000,
              "remote_packets_received":91,"remote_packets_lost":7,
              "remote_fraction_lost":0.0714,"remote_report_rtt_ms":260.25,
              "remote_rtt_measurements":9}]}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeNetworkPathsForDiagnostics(
            json, json.Length, out var lines));
        var line = Assert.Single(lines);
        Assert.Equal("sidecar.native.network-path", line.Category);
        Assert.Contains("peer=\"peer-7  forged\" generation=4 state=succeeded forged", line.Details);
        Assert.Contains("localType=relay remoteType=srflx relay=true currentRttMs=247.5", line.Details);
        Assert.Contains("remotePacketsLost=7 remoteFractionLost=0.0714", line.Details);
        Assert.DoesNotContain("candidate_pair_id", line.Details);
        Assert.DoesNotContain("192.0.2.1", line.Details);
        Assert.DoesNotContain('\r', line.Details);
        Assert.DoesNotContain('\n', line.Details);
    }

    [Fact]
    public void AecAvailabilityStatsPreserveMissingAndSanitizeFallbackReason()
    {
        const string current = """
            {"aec_timing_complete":false,"aec_input_timing_present":true,
             "aec_output_timing_present":false,"aec_render_timing_present":true,
             "aec_capture_path_present":true,"aec_fallback_reason":"callback-fallback\r\nforged"}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeAecAvailabilityForDiagnostics(current, out var details));
        Assert.Contains("aecTimingComplete=false", details);
        Assert.Contains("aecInputTimingPresent=true aecOutputTimingPresent=false", details);
        Assert.Contains("aecRenderTimingPresent=true aecCapturePathPresent=true", details);
        Assert.Contains("aecFallbackReason=callback-fallback  forged", details);
        Assert.DoesNotContain('\r', details);
        Assert.DoesNotContain('\n', details);

        Assert.True(SidecarVoiceClient.TryDescribeNativeAecAvailabilityForDiagnostics("{}", out var oldDetails));
        Assert.Contains("aecTimingComplete=na", oldDetails);
        Assert.Contains("aecInputTimingPresent=na", oldDetails);
        Assert.Contains("aecFallbackReason=na", oldDetails);
    }

    [Fact]
    public void AecTimingMeasurementsHonorComponentAndFramePresenceFlags()
    {
        const string unavailable = """
            {"aec_input_timing_present":false,"aec_output_timing_present":false,
             "aec_render_timing_present":false,"aec_capture_path_present":false,
             "aec_input_latency_ms":0,"aec_output_latency_ms":0,"aec_render_queue_ms":0,
             "aec_capture_processing_ms":0,"aec_capture_path_ms":0,
             "aec_last_frame_processed_present":false,"aec_frame_timestamp_valid":false,
             "aec_last_frame_processed_age_ms":0}
            """;
        const string presentZero = """
            {"aec_input_timing_present":true,"aec_output_timing_present":true,
             "aec_render_timing_present":true,"aec_capture_path_present":true,
             "aec_input_latency_ms":0,"aec_output_latency_ms":0,"aec_render_queue_ms":0,
             "aec_capture_processing_ms":0,"aec_capture_path_ms":0,
             "aec_last_frame_processed_present":true,"aec_frame_timestamp_valid":false,
             "aec_last_frame_processed_age_ms":0}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeNativeAecTimingForDiagnostics(
            unavailable, out var unavailableDetails));
        Assert.Contains("aecInputLatencyMs=na aecOutputLatencyMs=na", unavailableDetails);
        Assert.Contains("aecRenderQueueMs=na aecCaptureProcessingMs=na aecCapturePathMs=na", unavailableDetails);
        Assert.Contains("aecFrameTimestampValid=na aecLastFrameProcessedAgeMs=na", unavailableDetails);

        Assert.True(SidecarVoiceClient.TryDescribeNativeAecTimingForDiagnostics(
            presentZero, out var presentDetails));
        Assert.Contains("aecInputLatencyMs=0 aecOutputLatencyMs=0", presentDetails);
        Assert.Contains("aecRenderQueueMs=0 aecCaptureProcessingMs=0 aecCapturePathMs=0", presentDetails);
        Assert.Contains("aecFrameTimestampValid=false aecLastFrameProcessedAgeMs=0", presentDetails);
        Assert.False(SidecarVoiceClient.TryDescribeNativeAecTimingForDiagnostics("{bad", out _));
    }

    [Fact]
    public void MediaStateDiagnosticsExposeCaptureAttemptsAndTreatPlaybackTerminalStatesAsKnown()
    {
        const string captureStarting = """
            {"op":"media-state","direction":"capture","state":"starting",
             "stream_generation":3,"running":true,"open_attempt":5,"requested_default":true}
            """;
        const string capture = """
            {"op":"media-state","direction":"capture","state":"stop-failed",
             "stream_generation":3,"running":false,"command_seq":8,"open_attempt":5,"elapsed_ms":17}
            """;
        const string captureStopped = """
            {"op":"media-state","direction":"capture","state":"stopped",
             "stream_generation":3,"running":false,"command_seq":9,"open_attempt":5,"elapsed_ms":12,
             "final_window":{"callbacks":4,
               "raw_input":{"samples":8,"dropped_records":0,"peak":0.25,"rms":0.1,"dc":0},
               "pre_dsp":{"samples":8},"post_dsp":{"samples":8},"post_gain":{"samples":8}}}
            """;
        const string playbackError = """
            {"op":"media-state","direction":"playback","state":"error",
             "stream_generation":2,"running":false,"command_seq":0,"elapsed_ms":0}
            """;
        const string playbackStopped = """
            {"op":"media-state","direction":"playback","state":"stopped",
             "stream_generation":2,"running":false,"command_seq":0,"elapsed_ms":0}
            """;

        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(captureStarting, captureStarting.Length, out var startingDetails));
        Assert.Contains("openAttempt=5", startingDetails);
        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(capture, capture.Length, out var captureDetails));
        Assert.Contains("commandSeq=8 openAttempt=5 elapsedMs=17", captureDetails);
        Assert.DoesNotContain("extensionFieldsIgnored", captureDetails);
        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(captureStopped, captureStopped.Length, out var captureStoppedDetails));
        Assert.Contains("finalWindowPresent=true finalCallbacks=4", captureStoppedDetails);
        Assert.Contains("finalRawSamples=8", captureStoppedDetails);
        Assert.Contains("finalPostGainSamples=8", captureStoppedDetails);

        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(playbackError, playbackError.Length, out var errorDetails));
        Assert.True(SidecarVoiceClient.TryDescribeMediaStateForDiagnostics(playbackStopped, playbackStopped.Length, out var stoppedDetails));
        Assert.DoesNotContain("extensionFieldsIgnored", errorDetails);
        Assert.DoesNotContain("commandSeq=", errorDetails);
        Assert.DoesNotContain("elapsedMs=", errorDetails);
        Assert.DoesNotContain("commandSeq=", stoppedDetails);
        Assert.DoesNotContain("elapsedMs=", stoppedDetails);
    }

    [Fact]
    public void PlaybackStateParserPreservesSelectionAcknowledgementAndResolvedRoute()
    {
        const string accepted = """
            {"op":"media-state","direction":"playback","state":"command-accepted",
             "action":"select-output-device","stream_generation":0,"running":false,
             "requested_device":"Headphones","requested_default":false}
            """;
        const string started = """
            {"op":"media-state","direction":"playback","state":"stream-started",
             "stream_generation":9,"running":true,"requested_device":"Headphones",
             "resolved_device":"Default Speakers","requested_default":false,
             "requested_matched":false,"fell_back_to_default":true}
            """;

        Assert.True(SidecarVoiceClient.TryReadPlaybackState(accepted, out var acknowledgement));
        Assert.Equal("command-accepted", acknowledgement.State);
        Assert.Equal("select-output-device", acknowledgement.Action);
        Assert.Equal("Headphones", acknowledgement.RequestedDevice);

        Assert.True(SidecarVoiceClient.TryReadPlaybackState(started, out var playback));
        Assert.Equal((ulong)9, playback.StreamGeneration);
        Assert.Equal("Default Speakers", playback.ResolvedDevice);
        Assert.True(playback.FellBackToDefault);
        Assert.False(playback.RequestedMatched);

        Assert.False(SidecarVoiceClient.TryReadPlaybackState(
            "{\"direction\":\"capture\",\"state\":\"stream-started\",\"stream_generation\":1}",
            out _));
    }
}
