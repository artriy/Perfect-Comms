#if ANDROID
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

// Android voice transport backed by the in-process Rust core (libpc_mobile.so). Mirrors the
// role SidecarVoiceClient plays on desktop, but calls the engine over FFI instead of TCP and
// reuses SidecarProtocol verbatim: control JSON is the builder payload minus its frame header,
// and incoming signals are parsed by the same SidecarProtocol.TryRead* helpers.
internal sealed class MobileVoiceClient : IDisposable
{
    private const int MicFrame = SidecarProtocol.AudioSamples;         // 960 mono @ 48k (20ms)
    private const int PlaybackFrame = SidecarProtocol.AudioOutSamples; // 1920 interleaved stereo
    private const int SignalBufBytes = 64 * 1024;
    private const int MaximumSignalBufBytes = SidecarProtocol.MaxPayloadBytes + 1;
    private const int ShutdownTimeoutMs = 2_000;
    private const int InitialStartRetryMs = 500;
    private const int MaximumStartRetryMs = 30_000;
    private const int WorkerStallTimeoutMs = 5_000;
    private const int NativeHealthProbeIntervalMs = 1_000;

    private static readonly object StartRetryGate = new();
    private static bool _startInProgress;
    private static int _startFailures;
    private static long _startNotBeforeMs;

    private IntPtr _h;
    private int _activeNativeCalls;
    private int _faulted;
    private volatile bool _running;
    private long _pollHeartbeatMs;
    private long _pumpHeartbeatMs;
    private Thread? _pollThread;
    private Thread? _pumpThread;

    private readonly object _ctrlLock = new();
    private byte[] _controlBuffer = new byte[4 * 1024];
    private int _lastDiagnosticsEnabled = -1;
    private ulong _cachedGameStateFingerprint;
    private byte[]? _cachedGameStateFrame;

    private readonly float[] _micAccum = new float[MicFrame];
    private int _micFill;
    private int _micActive;
    private readonly object _micLock = new();

    // The native pump is the sole producer and Unity's audio callback is the sole consumer.
    // Keep enough local render cushion for scheduler jitter without ever taking a callback lock.
    private readonly SpscFloatRing _playbackRing = new(
        PlaybackFrame * 16,
        channels: 2,
        fadeFrames: 48,
        targetLatencySamples: PlaybackFrame * 4,
        primeLatencySamples: PlaybackFrame * 4,
        maximumLatencySamples: PlaybackFrame * 8,
        enableClockDriftCorrection: true); // 80ms target/prime, 160ms recovery bound, 320ms capacity
    private readonly float[] _pumpBuf = new float[PlaybackFrame];
    private long _pumpLateCycles;
    private long _nativeEmptyPulls;

    public event Action<string, int, string, string>? OnLocalSdp;
    public event Action<string, int, string>? OnLocalCandidate;
    public event Action<string, int, string>? OnPeerState;
    public event Action<float, bool>? OnLevel;
    public event Action<IReadOnlyList<SidecarProtocol.PeerLevel>>? OnPeerLevels;

    public bool IsRunning
    {
        get
        {
            if (!_running || Volatile.Read(ref _faulted) != 0 || ReadHandle() == IntPtr.Zero)
                return false;
            var now = Environment.TickCount64;
            return WorkerHeartbeatsHealthy(
                now,
                Volatile.Read(ref _pollHeartbeatMs),
                Volatile.Read(ref _pumpHeartbeatMs),
                WorkerStallTimeoutMs);
        }
    }
    public bool StartWasDeferred { get; private set; }
    public int StartRetryAfterMs { get; private set; }
    internal int PlaybackDepthSamples => _playbackRing.DepthSamples;
    internal long PlaybackHighWaterSamples => _playbackRing.HighWaterSamples;
    internal long PlaybackDroppedSamples => _playbackRing.DroppedSamples;
    internal long PlaybackZeroFilledSamples => _playbackRing.ZeroFilledSamples;
    internal long PlaybackSkippedSamples => _playbackRing.SkippedSamples;
    internal long PlaybackPrimingZeroFilledSamples => _playbackRing.PrimingZeroFilledSamples;
    internal long PlaybackClockCorrectionSamples => _playbackRing.ClockCorrectionSamples;
    internal long PlaybackClockCorrectionCallbacks => _playbackRing.ClockCorrectionCallbacks;
    internal long PlaybackPumpLateCycles => Interlocked.Read(ref _pumpLateCycles);
    internal long PlaybackNativeEmptyPulls => Interlocked.Read(ref _nativeEmptyPulls);

    public bool Start()
    {
        if (IsRunning) return true;
        if (!TryBeginStart(out var retryAfterMs))
        {
            StartWasDeferred = true;
            StartRetryAfterMs = retryAfterMs;
            return false;
        }

        StartWasDeferred = false;
        StartRetryAfterMs = 0;
        try
        {
            if (!PcMobileLoader.EnsureLoaded())
                return FailStart("pc-mobile load failed");

            var handle = PcMobileNative.pc_engine_new();
            if (handle == IntPtr.Zero)
                return FailStart("pc_engine_new returned null");

            Interlocked.Exchange(ref _h, handle);
            Volatile.Write(ref _faulted, 0);
            // Clear only before either worker (and before AndroidSpeaker) can access the ring.
            // Dispose intentionally does not reset SPSC cursors while a final audio callback may
            // still be unwinding on Unity's realtime thread.
            _playbackRing.Clear();
            _running = true;
            var now = Environment.TickCount64;
            Volatile.Write(ref _pollHeartbeatMs, now);
            Volatile.Write(ref _pumpHeartbeatMs, now);
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "PcMobilePoll" };
            _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "PcMobilePump" };
            _pollThread.Start();
            _pumpThread.Start();
            CompleteStart(success: true);
            VoiceDiagnostics.DebugInfo("[VC] MobileVoiceClient started");
            return true;
        }
        catch (Exception ex)
        {
            _running = false;
            var handle = Interlocked.Exchange(ref _h, IntPtr.Zero);
            ShutdownDetachedHandle(handle, "start-failed");
            return FailStart($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool Control(byte[] framed)
    {
        int jsonLen = framed.Length - SidecarProtocol.HeaderBytes;
        if (jsonLen <= 0) return false;
        lock (_ctrlLock)
        {
            if (_controlBuffer.Length < jsonLen + 1)
                _controlBuffer = new byte[Math.Max(jsonLen + 1, _controlBuffer.Length * 2)];
            Array.Copy(framed, SidecarProtocol.HeaderBytes, _controlBuffer, 0, jsonLen);
            _controlBuffer[jsonLen] = 0;
            if (!TryAcquireNativeHandle(out var handle)) return false;
            try
            {
                PcMobileNative.pc_control(handle, _controlBuffer);
                return true;
            }
            finally { ReleaseNativeHandle(); }
        }
    }

    public bool AddPeer(string peerId, bool isOfferer, int generation)
    {
        // Serialize receiver-set expansion with mic pushes. This ensures an in-flight native push
        // finishes before AddPeer resets encoder/DRED history; native enforces the same boundary
        // for direct/concurrent FFI callers.
        lock (_micLock)
            return Control(SidecarProtocol.AddPeerFrame(peerId, isOfferer, generation));
    }
    public bool RemovePeer(string peerId) => Control(SidecarProtocol.RemovePeerFrame(peerId));
    public bool RestartIce(string peerId, bool createOffer) =>
        Control(SidecarProtocol.RestartIceFrame(peerId, createOffer));
    public bool SetRemoteSdp(string peerId, string sdpType, string sdp) => Control(SidecarProtocol.SetRemoteSdpFrame(peerId, sdpType, sdp));
    public bool AddIceCandidate(string peerId, string candidate) => Control(SidecarProtocol.AddIceCandidateFrame(peerId, candidate));
    public void SetIceServers(IEnumerable<IceServer> servers) => Control(SidecarProtocol.SetIceServersFrame(servers));
    public void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf) =>
        Control(SidecarProtocol.SetDspFrame(aec, agc, ns, nsVeryHigh, hpf));
    public void SetDiagnostics(bool enabled)
    {
        var requested = enabled ? 1 : 0;
        if (Volatile.Read(ref _lastDiagnosticsEnabled) == requested) return;
        if (Control(SidecarProtocol.SetDiagnosticsFrame(enabled)))
            Volatile.Write(ref _lastDiagnosticsEnabled, requested);
    }
    public void SetMicActive(bool active)
    {
        // Close the managed gate before waiting for an in-flight push. A successful Start opens
        // it only after native Opus/DRED history has been reset; Stop always remains fail-closed.
        Volatile.Write(ref _micActive, 0);
        lock (_micLock)
        {
            ResetMicInputLocked();
            if (Control(active ? SidecarProtocol.StartFrame() : SidecarProtocol.StopFrame()) && active)
                Volatile.Write(ref _micActive, 1);
        }
    }
    public void SetSynthetic(bool enabled) => Control(SidecarProtocol.SetSyntheticFrame(enabled));
    public void SetInput(float gain, float vadThreshold, float noiseGateThreshold)
        => Control(SidecarProtocol.SetInputFrame(gain, vadThreshold, noiseGateThreshold));
    public void SendGameState(bool deaf, float master, IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
    {
        var fingerprint = SidecarProtocol.GameStateFingerprint(deaf, master, peers);
        if (_cachedGameStateFrame == null || fingerprint != _cachedGameStateFingerprint)
        {
            _cachedGameStateFrame = SidecarProtocol.GameStateFrame(deaf, master, peers);
            _cachedGameStateFingerprint = fingerprint;
        }
        Control(_cachedGameStateFrame);
    }

    // Mic floats (mono 48k); accumulated into 960-sample frames and pushed to the engine, which
    // runs DSP + Opus + WebRTC send. Returns nothing; OnLevel fires per pushed frame.
    public void PushMic(float[] mono, int count)
    {
        if (mono == null || count <= 0 || Volatile.Read(ref _micActive) == 0) return;
        lock (_micLock)
        {
            if (ReadHandle() == IntPtr.Zero || Volatile.Read(ref _micActive) == 0) return;
            int i = 0;
            while (i < count)
            {
                int take = Math.Min(MicFrame - _micFill, count - i);
                Array.Copy(mono, i, _micAccum, _micFill, take);
                _micFill += take;
                i += take;
                if (_micFill == MicFrame)
                {
                    // Native emits the configured gain/VAD level event at a bounded 100 ms cadence.
                    // Do not race it with a per-frame hard-coded speaking threshold here.
                    if (TryAcquireNativeHandle(out var handle))
                    {
                        try { _ = PcMobileNative.pc_push_mic(handle, _micAccum, MicFrame); }
                        finally { ReleaseNativeHandle(); }
                    }
                    _micFill = 0;
                }
            }
        }
    }

    public void ResetMicInput()
    {
        lock (_micLock)
            ResetMicInputLocked();
    }

    private void ResetMicInputLocked()
    {
        Array.Clear(_micAccum, 0, _micAccum.Length);
        _micFill = 0;
    }

    // Drain the playback ring into an interleaved-stereo buffer for the Unity audio callback.
    public void ReadPlayback(float[] interleavedStereo, int count)
    {
        if (interleavedStereo == null) throw new ArgumentNullException(nameof(interleavedStereo));
        if ((uint)count > (uint)interleavedStereo.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _playbackRing.Read(interleavedStereo.AsSpan(0, count));
    }

    private void PumpLoop()
    {
        try
        {
            long nextDeadline = 0;
            while (_running)
            {
                Volatile.Write(ref _pumpHeartbeatMs, Environment.TickCount64);
                var got = 0;
                if (TryAcquireNativeHandle(out var handle))
                {
                    try { got = PcMobileNative.pc_pull_playback(handle, _pumpBuf, PlaybackFrame); }
                    finally { ReleaseNativeHandle(); }
                }
                if (got > 0)
                    _playbackRing.TryWrite(_pumpBuf.AsSpan(0, got));
                else
                    Interlocked.Increment(ref _nativeEmptyPulls);

                var now = Stopwatch.GetTimestamp();
                if (nextDeadline != 0 && now - nextDeadline > Stopwatch.Frequency / 200)
                    Interlocked.Increment(ref _pumpLateCycles);
                nextDeadline = MobilePlaybackCadence.NextDeadline(nextDeadline, now, Stopwatch.Frequency);
                var delayMs = MobilePlaybackCadence.DelayMilliseconds(
                    Stopwatch.GetTimestamp(),
                    nextDeadline,
                    Stopwatch.Frequency);
                if (delayMs > 0) Thread.Sleep(delayMs);
                else Thread.Yield();
            }
        }
        catch (Exception ex)
        {
            MarkFault("pump", ex);
        }
    }

    private void PollLoop()
    {
        try
        {
            var buf = new byte[SignalBufBytes];
            var nextHealthProbeMs = 0L;
            while (_running)
            {
                var nowMs = Environment.TickCount64;
                Volatile.Write(ref _pollHeartbeatMs, nowMs);
                var got = 0;
                if (TryAcquireNativeHandle(out var handle))
                {
                    try
                    {
                        got = PcMobileNative.pc_poll_signal(handle, buf, buf.Length);
                        if (nowMs >= nextHealthProbeMs)
                        {
                            nextHealthProbeMs = nowMs + NativeHealthProbeIntervalMs;
                            if (float.IsNaN(PcMobileNative.pc_mic_level(handle)))
                                throw new InvalidOperationException("native engine reported unhealthy");
                        }
                    }
                    finally { ReleaseNativeHandle(); }
                }
                if (got > 0)
                {
                    Dispatch(Encoding.UTF8.GetString(buf, 0, got));
                    continue;
                }
                if (got == -1)
                {
                    var nextSize = NextSignalBufferSize(buf.Length);
                    if (nextSize <= buf.Length)
                        throw new InvalidOperationException(
                            $"native signal exceeds the {SidecarProtocol.MaxPayloadBytes}-byte protocol cap");
                    Array.Resize(ref buf, nextSize);
                    continue;
                }
                if (got < 0)
                    throw new InvalidOperationException($"native signal poll failed with status {got}");
                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            MarkFault("poll", ex);
        }
    }

    private void MarkFault(string worker, Exception ex)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
        _running = false;
        VoiceDiagnostics.Log("voice.mobile.worker",
            $"state=faulted worker={worker} error=\"{ex.Message.Replace('"', '\'')}\"");
    }

    internal static bool WorkerHeartbeatsHealthy(
        long nowMs,
        long pollHeartbeatMs,
        long pumpHeartbeatMs,
        int timeoutMs)
        => pollHeartbeatMs > 0
           && pumpHeartbeatMs > 0
           && nowMs - pollHeartbeatMs <= timeoutMs
           && nowMs - pumpHeartbeatMs <= timeoutMs;

    internal static int NextSignalBufferSize(int currentSize)
    {
        if (currentSize < 2 || currentSize >= MaximumSignalBufBytes)
            return currentSize;
        return (int)Math.Min((long)currentSize * 2, MaximumSignalBufBytes);
    }

    private void Dispatch(string json)
    {
        try
        {
        switch (SidecarProtocol.ReadOp(json))
        {
            case "local-sdp":
                if (SidecarProtocol.TryReadLocalSdp(json, out var sp, out var sdpGeneration, out var st, out var sd)) OnLocalSdp?.Invoke(sp, sdpGeneration, st, sd);
                break;
            case "local-candidate":
                if (SidecarProtocol.TryReadLocalCandidate(json, out var cp, out var candidateGeneration, out var cc)) OnLocalCandidate?.Invoke(cp, candidateGeneration, cc);
                break;
            case "peer-state":
                if (SidecarProtocol.TryReadPeerState(json, out var pp, out var stateGeneration, out var ps)) OnPeerState?.Invoke(pp, stateGeneration, ps);
                break;
            case "level":
                if (SidecarProtocol.TryReadLevel(json, out var peak, out var speaking)) OnLevel?.Invoke(peak, speaking);
                break;
            case "peer-levels":
                if (SidecarProtocol.TryReadPeerLevels(json, out var levels)) OnPeerLevels?.Invoke(levels);
                break;
            case "mobile-stats":
                LogMobileDiagnostics(json);
                break;
        }
        }
        catch (Exception) { }
    }

    private static void LogMobileDiagnostics(string json)
    {
        if (!VoiceDiagnostics.IsEnabled) return;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("media_receive", out var receive)) return;

        int pathCount = 0;
        int relayPaths = 0;
        double maxRttMs = 0;
        double maxRemoteLoss = 0;
        double minimumOutgoingBitrate = 0;
        bool bandwidthEstimateValid = false;
        var pathClasses = new StringBuilder(128);
        if (root.TryGetProperty("network_paths", out var paths)
            && paths.ValueKind == JsonValueKind.Array)
        {
            foreach (var path in paths.EnumerateArray())
            {
                pathCount++;
                if (ReadBool(path, "relay")) relayPaths++;
                maxRttMs = Math.Max(maxRttMs, ReadFiniteDouble(path, "current_rtt_ms"));
                maxRemoteLoss = Math.Max(maxRemoteLoss, ReadFiniteDouble(path, "remote_fraction_lost"));
                if (ReadBool(path, "bandwidth_estimate_valid"))
                {
                    bandwidthEstimateValid = true;
                    var outgoing = ReadFiniteDouble(path, "available_outgoing_bitrate");
                    if (outgoing > 0 && (minimumOutgoingBitrate <= 0 || outgoing < minimumOutgoingBitrate))
                        minimumOutgoingBitrate = outgoing;
                }
                if (pathCount <= 16)
                {
                    if (pathClasses.Length > 0) pathClasses.Append(',');
                    pathClasses
                        .Append(SafeRouteToken(path, "local_candidate_type"))
                        .Append('-')
                        .Append(SafeRouteToken(path, "remote_candidate_type"))
                        .Append('/')
                        .Append(SafeRouteToken(path, "candidate_state"))
                        .Append(ReadBool(path, "relay") ? "/relay" : "/direct");
                }
            }
        }

        VoiceDiagnostics.Log("voice.mobile.native",
            $"activePeers={ReadUInt64(receive, "active_peers")} ingressOverflow={ReadUInt64(receive, "ingress_queue_overflow")} ingressDepth={ReadUInt64(receive, "ingress_queue_depth_current")} ingressDepthMax={ReadUInt64(receive, "ingress_queue_depth_max")} ingressPeerDepthMax={ReadUInt64(receive, "ingress_peer_queue_depth_max")} " +
            $"sequenceGaps={ReadUInt64(receive, "sequence_gaps")} reorderedRecovered={ReadUInt64(receive, "reordered_recovered")} " +
            $"lateDrops={ReadUInt64(receive, "late_drops")} duplicateDrops={ReadUInt64(receive, "duplicate_drops")} encodedOverflowDrops={ReadUInt64(receive, "encoded_overflow_drops")} " +
            $"deadlineLosses={ReadUInt64(receive, "deadline_losses")} dredFrames={ReadUInt64(receive, "dred_frames")} fecFrames={ReadUInt64(receive, "fec_frames")} plcFrames={ReadUInt64(receive, "plc_frames")} " +
            $"decoderResets={ReadUInt64(receive, "decoder_resets")} talkspurtResets={ReadUInt64(receive, "talkspurt_resets")} underruns={ReadUInt64(receive, "underruns")} rebuffers={ReadUInt64(receive, "rebuffers")} " +
            $"targetFrames={ReadUInt64(receive, "target_frames_current_max")} depthFrames={ReadUInt64(receive, "depth_frames_current")} jitterMsMax={ReadFiniteDouble(receive, "rtp_jitter_ms_max"):0.0} " +
            $"encoderLossPercent={ReadUInt64(root, "encoder_packet_loss_percent")} encoderBitrate={ReadUInt64(root, "encoder_bitrate")} encoderGeneration={ReadUInt64(root, "encoder_policy_generation")} " +
            $"rtpTxQueueDropped={ReadUInt64(root, "rtp_tx_queue_dropped")} rtpTxStaleEpochDropped={ReadUInt64(root, "rtp_tx_stale_epoch_dropped")} rtpTxWriteTimeouts={ReadUInt64(root, "rtp_tx_write_timeouts")} rtpTxQueueDepthMax={ReadUInt64(root, "rtp_tx_queue_depth_max")} " +
            $"paths={pathCount} relayPaths={relayPaths} maxRttMs={maxRttMs:0.0} maxRemoteLoss={maxRemoteLoss:0.000} bandwidthEstimateValid={bandwidthEstimateValid.ToString().ToLowerInvariant()} minOutgoingBitrate={minimumOutgoingBitrate:0} pathClasses=\"{pathClasses}\"");
    }

    private static ulong ReadUInt64(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value) && value.TryGetUInt64(out var result)
            ? result
            : 0;

    private static bool ReadBool(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value)
           && value.ValueKind is JsonValueKind.True;

    private static double ReadFiniteDouble(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || !value.TryGetDouble(out var result)
            || !double.IsFinite(result))
            return 0;
        return Math.Max(0, result);
    }

    private static string SafeRouteToken(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
            return "unknown";
        var token = value.GetString() ?? "unknown";
        if (token.Length is < 1 or > 16) return "unknown";
        foreach (var character in token)
        {
            // Candidate classes/states contain letters and hyphens only. Rejecting digits and
            // punctuation ensures an address or candidate id can never be reflected into logs.
            if (!char.IsLetter(character) && character != '-') return "unknown";
        }
        return token;
    }

    public void Dispose()
    {
        _running = false;
        Volatile.Write(ref _micActive, 0);
        ResetMicInput();
        var handle = Interlocked.Exchange(ref _h, IntPtr.Zero);
        var hadRuntime = handle != IntPtr.Zero || _pollThread != null || _pumpThread != null;
        ShutdownDetachedHandle(handle, "dispose");
        if (hadRuntime)
            VoiceDiagnostics.DebugInfo("[VC] MobileVoiceClient disposed");
    }

    private bool TryAcquireNativeHandle(out IntPtr handle)
    {
        while (true)
        {
            handle = ReadHandle();
            if (handle == IntPtr.Zero) return false;
            Interlocked.Increment(ref _activeNativeCalls);
            if (ReadHandle() == handle) return true;
            Interlocked.Decrement(ref _activeNativeCalls);
        }
    }

    private void ReleaseNativeHandle() => Interlocked.Decrement(ref _activeNativeCalls);

    private IntPtr ReadHandle() => Interlocked.CompareExchange(ref _h, IntPtr.Zero, IntPtr.Zero);

    private void ShutdownDetachedHandle(IntPtr handle, string reason)
    {
        var deadline = Stopwatch.GetTimestamp() + ShutdownTimeoutMs * Stopwatch.Frequency / 1000;
        var pollStopped = JoinUntil(_pollThread, deadline);
        var pumpStopped = JoinUntil(_pumpThread, deadline);
        _pollThread = null;
        _pumpThread = null;

        while (Volatile.Read(ref _activeNativeCalls) != 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(1);

        var activeCalls = Volatile.Read(ref _activeNativeCalls);
        if (handle == IntPtr.Zero) return;
        if (!pollStopped || !pumpStopped || activeCalls != 0)
        {
            // The current ABI has no cancellation primitive for an in-flight native call. Leaking
            // this detached handle until process exit is safer than a use-after-free or freezing
            // Unity's main thread indefinitely.
            VoiceDiagnostics.Log("voice.mobile.shutdown",
                $"state=abandoned-handle reason={reason} pollStopped={pollStopped} pumpStopped={pumpStopped} activeCalls={activeCalls}");
            return;
        }

        try
        {
            var freeThread = new Thread(() =>
            {
                try { PcMobileNative.pc_engine_free(handle); }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("voice.mobile.shutdown",
                        $"state=free-failed reason={reason} error=\"{ex.Message.Replace('"', '\'')}\"");
                }
            })
            {
                IsBackground = true,
                Name = "PcMobileFree",
            };
            freeThread.Start();
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("voice.mobile.shutdown",
                $"state=free-thread-failed reason={reason} error=\"{ex.Message.Replace('"', '\'')}\"");
        }
    }

    private static bool JoinUntil(Thread? worker, long deadline)
    {
        if (worker == null || !worker.IsAlive) return true;
        if (ReferenceEquals(worker, Thread.CurrentThread)) return false;
        try
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0) return !worker.IsAlive;
            var remainingMs = Math.Max(1, (int)Math.Min(int.MaxValue,
                remainingTicks * 1000L / Stopwatch.Frequency));
            return worker.Join(remainingMs);
        }
        catch { return false; }
    }

    private bool FailStart(string reason)
    {
        var delayMs = CompleteStart(success: false);
        StartRetryAfterMs = delayMs;
        VoiceDiagnostics.Log("voice.mobile.start",
            $"state=retry-scheduled delayMs={delayMs} reason=\"{reason.Replace('"', '\'')}\"");
        return false;
    }

    private static bool TryBeginStart(out int retryAfterMs)
    {
        lock (StartRetryGate)
        {
            var now = Environment.TickCount64;
            if (_startInProgress)
            {
                retryAfterMs = 100;
                return false;
            }
            if (now < _startNotBeforeMs)
            {
                retryAfterMs = (int)Math.Min(int.MaxValue, _startNotBeforeMs - now);
                return false;
            }
            _startInProgress = true;
            retryAfterMs = 0;
            return true;
        }
    }

    private static int CompleteStart(bool success)
    {
        lock (StartRetryGate)
        {
            _startInProgress = false;
            if (success)
            {
                _startFailures = 0;
                _startNotBeforeMs = 0;
                return 0;
            }

            _startFailures = Math.Min(_startFailures + 1, 30);
            var delayMs = AndroidMicrophone.RecoveryDelayMilliseconds(
                _startFailures, InitialStartRetryMs, MaximumStartRetryMs);
            _startNotBeforeMs = Environment.TickCount64 + delayMs;
            return delayMs;
        }
    }
}
#endif
