#if ANDROID
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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

    private readonly float[] _micAccum = new float[MicFrame];
    private int _micFill;
    private readonly object _micLock = new();

    // Playback ring (interleaved stereo): the pump thread fills it from pc_pull_playback at 20ms,
    // the Unity audio callback drains it via ReadPlayback. ponytail: one short lock, fine for audio.
    private readonly float[] _ring = new float[PlaybackFrame * 8]; // ~160ms cushion
    private int _ringRead, _ringWrite, _ringCount;
    private readonly object _ringLock = new();
    private readonly float[] _pumpBuf = new float[PlaybackFrame];

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

    private void Control(byte[] framed)
    {
        int jsonLen = framed.Length - SidecarProtocol.HeaderBytes;
        if (jsonLen <= 0) return;
        var json = new byte[jsonLen + 1];
        Array.Copy(framed, SidecarProtocol.HeaderBytes, json, 0, jsonLen);
        json[jsonLen] = 0;
        lock (_ctrlLock)
        {
            if (!TryAcquireNativeHandle(out var handle)) return;
            try { PcMobileNative.pc_control(handle, json); }
            finally { ReleaseNativeHandle(); }
        }
    }

    public void AddPeer(string peerId, bool isOfferer, bool relayOnly, int generation)
        => Control(SidecarProtocol.AddPeerFrame(peerId, isOfferer, relayOnly, generation));
    public void RemovePeer(string peerId) => Control(SidecarProtocol.RemovePeerFrame(peerId));
    public void SetRemoteSdp(string peerId, string sdpType, string sdp) => Control(SidecarProtocol.SetRemoteSdpFrame(peerId, sdpType, sdp));
    public void AddIceCandidate(string peerId, string candidate) => Control(SidecarProtocol.AddIceCandidateFrame(peerId, candidate));
    public void SetIceServers(IEnumerable<IceServer> servers) => Control(SidecarProtocol.SetIceServersFrame(servers));
    public void SetDsp(bool aec, bool agc, bool ns, bool hpf) => Control(SidecarProtocol.SetDspFrame(aec, agc, ns, hpf));
    public void SetSynthetic(bool enabled) => Control(SidecarProtocol.SetSyntheticFrame(enabled));
    public void SetInput(float gain, float vadThreshold) => Control(SidecarProtocol.SetInputFrame(gain, vadThreshold));
    public void SendGameState(bool deaf, float master, IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
        => Control(SidecarProtocol.GameStateFrame(deaf, master, peers));

    // Mic floats (mono 48k); accumulated into 960-sample frames and pushed to the engine, which
    // runs DSP + Opus + WebRTC send. Returns nothing; OnLevel fires per pushed frame.
    public void PushMic(float[] mono, int count)
    {
        if (mono == null || count <= 0) return;
        lock (_micLock)
        {
            if (ReadHandle() == IntPtr.Zero) return;
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
        {
            Array.Clear(_micAccum, 0, _micAccum.Length);
            _micFill = 0;
        }
    }

    // Drain the playback ring into an interleaved-stereo buffer for the Unity audio callback.
    public void ReadPlayback(float[] interleavedStereo)
    {
        int n = interleavedStereo.Length;
        lock (_ringLock)
        {
            int avail = Math.Min(n, _ringCount);
            for (int i = 0; i < avail; i++)
            {
                interleavedStereo[i] = _ring[_ringRead];
                _ringRead = (_ringRead + 1) % _ring.Length;
            }
            _ringCount -= avail;
            for (int i = avail; i < n; i++) interleavedStereo[i] = 0f;
        }
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
                {
                    lock (_ringLock)
                    {
                        for (int i = 0; i < got; i++)
                        {
                            if (_ringCount >= _ring.Length)
                            {
                                _ringRead = (_ringRead + 1) % _ring.Length;
                                _ringCount--;
                            }
                            _ring[_ringWrite] = _pumpBuf[i];
                            _ringWrite = (_ringWrite + 1) % _ring.Length;
                            _ringCount++;
                        }
                    }
                }

                var now = Stopwatch.GetTimestamp();
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
        }
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _running = false;
        ResetMicInput();
        lock (_ringLock)
        {
            Array.Clear(_ring, 0, _ring.Length);
            _ringRead = 0;
            _ringWrite = 0;
            _ringCount = 0;
        }
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
