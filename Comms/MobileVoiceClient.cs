#if STARLIGHT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using PerfectComms.Starlight.Media;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class MobileVoiceClient : IDisposable
{
    private const int MicFrame = SidecarProtocol.AudioSamples;
    private const int ShutdownTimeoutMs = 2_000;
    private const int InitialStartRetryMs = 500;
    private const int MaximumStartRetryMs = 30_000;

    private static readonly object StartRetryGate = new();
    private static bool _startInProgress;
    private static int _startFailures;
    private static long _startNotBeforeMs;

    private readonly object _lifecycleLock = new();
    private readonly object _micLock = new();
    private readonly object _controlLock = new();
    private readonly float[] _micAccum = new float[MicFrame];
    private readonly List<ManagedPeerRoute> _managedRoutes = new(32);
    private ManagedVoiceEngine? _engine;
    private int _activeEngineCalls;
    private int _disposed;
    private ManagedVoiceEngine? _retiredEngine;
    private int _retiredEngineDisposalCount;
    private int _retiredEngineDisposalQueued;
    private int _micActive;
    private int _micFill;
    private int _lastDiagnosticsEnabled = -1;

    public event Action<string, int, string, string>? OnLocalSdp;
    public event Action<string, int, string>? OnLocalCandidate;
    public event Action<string, int, string>? OnPeerState;
    public event Action<float, bool>? OnLevel;
    public event Action<IReadOnlyList<SidecarProtocol.PeerLevel>>? OnPeerLevels;

    public bool IsRunning
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return false;
            try { return engine.IsRunning; }
            finally { ReleaseEngine(); }
        }
    }

    public bool StartWasDeferred { get; private set; }
    public int StartRetryAfterMs { get; private set; }

    internal int PlaybackDepthSamples
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackDepthSamples; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackHighWaterSamples
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackHighWaterSamples; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackDroppedSamples
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackDroppedSamples; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackZeroFilledSamples
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackZeroFilledSamples; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackSkippedSamples
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackSkippedSamples; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackPrimingZeroFilledSamples
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackPrimingZeroFilledSamples; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackClockCorrectionSamples
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackClockCorrectionSamples; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackClockCorrectionCallbacks
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackClockCorrectionCallbacks; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackPumpLateCycles
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackPumpLateCycles; }
            finally { ReleaseEngine(); }
        }
    }

    internal long PlaybackNativeEmptyPulls
    {
        get
        {
            if (!TryAcquireEngine(out var engine)) return 0;
            try { return engine.PlaybackEmptyPulls; }
            finally { ReleaseEngine(); }
        }
    }

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
        ManagedVoiceEngine? engine = null;
        try
        {
            lock (_lifecycleLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return FailStart("client disposed");

                engine = new ManagedVoiceEngine();
                Subscribe(engine);
                if (!engine.Start())
                {
                    var failedEngine = engine;
                    engine = null;
                    Unsubscribe(failedEngine);
                    failedEngine.Dispose();
                    return FailStart("managed engine start failed");
                }
                if (Volatile.Read(ref _disposed) != 0)
                {
                    var rejectedEngine = engine;
                    engine = null;
                    Unsubscribe(rejectedEngine);
                    rejectedEngine.Dispose();
                    return FailStart("client disposed during managed engine start");
                }

                Interlocked.Exchange(ref _engine, engine);
                engine = null;
            }

            CompleteStart(success: true);
            try { VoiceDiagnostics.DebugInfo("[VC] MobileVoiceClient managed-starlight engine started"); }
            catch { }
            return true;
        }
        catch (Exception ex)
        {
            var reason = $"{ex.GetType().Name}: {ex.Message}";
            if (engine != null)
            {
                try
                {
                    Unsubscribe(engine);
                    engine.Dispose();
                }
                catch (Exception cleanupException)
                {
                    reason += $"; cleanup {cleanupException.GetType().Name}: {cleanupException.Message}";
                }
            }
            return FailStart(reason);
        }
    }

    public bool AddPeer(string peerId, bool isOfferer, int generation)
    {
        lock (_micLock)
        {
            if (!TryAcquireEngine(out var engine)) return false;
            try { return engine.AddPeer(peerId, isOfferer, generation); }
            finally { ReleaseEngine(); }
        }
    }

    public bool RemovePeer(string peerId, int generation)
    {
        if (!TryAcquireEngine(out var engine)) return false;
        try { return engine.RemovePeer(peerId, generation); }
        finally { ReleaseEngine(); }
    }

    public bool RestartIce(string peerId, int generation, bool createOffer)
    {
        if (!TryAcquireEngine(out var engine)) return false;
        try { return engine.RestartIce(peerId, generation, createOffer); }
        finally { ReleaseEngine(); }
    }

    public bool SetRemoteSdp(string peerId, int generation, string sdpType, string sdp)
    {
        if (!TryAcquireEngine(out var engine)) return false;
        try { return engine.SetRemoteSdp(peerId, generation, sdpType, sdp); }
        finally { ReleaseEngine(); }
    }

    public bool AddIceCandidate(string peerId, int generation, string candidate)
    {
        if (!TryAcquireEngine(out var engine)) return false;
        try { return engine.AddIceCandidate(peerId, generation, candidate); }
        finally { ReleaseEngine(); }
    }

    public void SetIceServers(IEnumerable<IceServer> servers)
    {
        if (servers == null) throw new ArgumentNullException(nameof(servers));
        var managedServers = new List<ManagedIceServer>();
        foreach (var server in servers)
            managedServers.Add(new ManagedIceServer(server.Urls, server.Username, server.Credential));

        if (!TryAcquireEngine(out var engine)) return;
        try { engine.SetIceServers(managedServers); }
        finally { ReleaseEngine(); }
    }

    public void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf)
    {
        if (aec || agc || ns || nsVeryHigh || hpf)
            throw new NotSupportedException("Managed Starlight does not expose WebRTC audio processing.");
    }

    public void SetDiagnostics(bool enabled)
    {
        var requested = enabled ? 1 : 0;
        if (Volatile.Read(ref _lastDiagnosticsEnabled) == requested) return;
        if (!TryAcquireEngine(out var engine)) return;
        try
        {
            engine.SetDiagnostics(enabled);
            Volatile.Write(ref _lastDiagnosticsEnabled, requested);
        }
        finally { ReleaseEngine(); }
    }

    public void SetMicActive(bool active)
    {
        Volatile.Write(ref _micActive, 0);
        lock (_micLock)
        {
            ResetMicAccumulation();
            if (!TryAcquireEngine(out var engine)) return;
            try
            {
                engine.ResetMicInput();
                engine.SetMicActive(active);
                if (active && engine.IsRunning)
                    Volatile.Write(ref _micActive, 1);
            }
            finally { ReleaseEngine(); }
        }
    }

    public void SetSynthetic(bool enabled)
    {
        if (!TryAcquireEngine(out var engine)) return;
        try { engine.SetSynthetic(enabled); }
        finally { ReleaseEngine(); }
    }

    public void SetInput(float gain, float vadThreshold, float noiseGateThreshold)
    {
        if (!TryAcquireEngine(out var engine)) return;
        try { engine.SetInput(gain, vadThreshold, noiseGateThreshold); }
        finally { ReleaseEngine(); }
    }

    public void SendGameState(
        bool deaf,
        float master,
        IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
    {
        if (peers == null) throw new ArgumentNullException(nameof(peers));
        lock (_controlLock)
        {
            _managedRoutes.Clear();
            for (var i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                _managedRoutes.Add(new ManagedPeerRoute(
                    peer.Id,
                    peer.Gain,
                    peer.Pan,
                    peer.Mode,
                    peer.Muffled));
            }

            if (!TryAcquireEngine(out var engine)) return;
            try { engine.ConfigureGameState(deaf, master, _managedRoutes); }
            finally { ReleaseEngine(); }
        }
    }

    public void PushMic(float[] mono, int count)
        => PushMicInternal(mono, count, skippedBeforeCurrent: 0, gapAware: false);

    public void PushMicWithMediaGap(float[] mono, int count, ulong skippedBeforeCurrent)
        => PushMicInternal(mono, count, skippedBeforeCurrent, gapAware: true);

    private void PushMicInternal(
        float[] mono,
        int count,
        ulong skippedBeforeCurrent,
        bool gapAware)
    {
        if (mono == null || count <= 0 || Volatile.Read(ref _micActive) == 0) return;
        lock (_micLock)
        {
            if (Volatile.Read(ref _micActive) == 0) return;
            if (gapAware && _micFill != 0)
                ResetMicAccumulation();

            var offset = 0;
            while (offset < count)
            {
                var take = Math.Min(MicFrame - _micFill, count - offset);
                Array.Copy(mono, offset, _micAccum, _micFill, take);
                _micFill += take;
                offset += take;
                if (_micFill != MicFrame) continue;

                if (TryAcquireEngine(out var engine))
                {
                    try
                    {
                        engine.PushMic(
                            _micAccum,
                            MicFrame,
                            gapAware ? skippedBeforeCurrent : 0);
                    }
                    finally { ReleaseEngine(); }
                }

                skippedBeforeCurrent = 0;
                _micFill = 0;
            }
        }
    }

    public void ResetMicInput()
    {
        lock (_micLock)
        {
            ResetMicAccumulation();
            if (!TryAcquireEngine(out var engine)) return;
            try { engine.ResetMicInput(); }
            finally { ReleaseEngine(); }
        }
    }

    public void ReadPlayback(float[] interleavedStereo, int count)
    {
        if (interleavedStereo == null) throw new ArgumentNullException(nameof(interleavedStereo));
        if ((uint)count > (uint)interleavedStereo.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (!TryAcquireEngine(out var engine))
        {
            Array.Clear(interleavedStereo, 0, count);
            return;
        }

        try { engine.ReadPlayback(interleavedStereo, count); }
        finally { ReleaseEngine(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        ManagedVoiceEngine? engine;
        lock (_lifecycleLock)
        {
            Volatile.Write(ref _micActive, 0);
            if (Monitor.TryEnter(_micLock))
            {
                try { ResetMicAccumulation(); }
                finally { Monitor.Exit(_micLock); }
            }
            engine = Interlocked.Exchange(ref _engine, null);
        }

        var deadline = Stopwatch.GetTimestamp() + ShutdownTimeoutMs * Stopwatch.Frequency / 1000;
        while (Volatile.Read(ref _activeEngineCalls) != 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.Sleep(1);

        if (engine == null) return;
        var activeCalls = Volatile.Read(ref _activeEngineCalls);
        if (activeCalls != 0)
        {
            Interlocked.Exchange(ref _retiredEngine, engine);
            QueueRetiredEngineDisposalIfReady();
            VoiceDiagnostics.Log("voice.mobile.shutdown",
                $"state=deferred-engine-dispose reason=dispose timeoutMs={ShutdownTimeoutMs} activeCalls={activeCalls}");
            return;
        }

        DisposeEngine(engine);
    }

    private void QueueRetiredEngineDisposalIfReady()
    {
        if (Volatile.Read(ref _activeEngineCalls) != 0 ||
            Volatile.Read(ref _retiredEngine) == null ||
            Interlocked.CompareExchange(ref _retiredEngineDisposalQueued, 1, 0) != 0)
            return;

        ThreadPool.QueueUserWorkItem(static state =>
        {
            var client = (MobileVoiceClient)state!;
            try { client.DisposeRetiredEngine(); }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("voice.mobile.shutdown",
                    $"state=deferred-engine-dispose-failed error=\"{ex.Message.Replace('"', '\'')}\"");
            }
        }, this);
    }

    private void DisposeRetiredEngine()
    {
        var engine = Interlocked.Exchange(ref _retiredEngine, null);
        if (engine != null)
            DisposeEngine(engine);
    }

    private void DisposeEngine(ManagedVoiceEngine engine)
    {
        try
        {
            Unsubscribe(engine);
        }
        finally
        {
            try { engine.Dispose(); }
            finally { Interlocked.Increment(ref _retiredEngineDisposalCount); }
        }
        try { VoiceDiagnostics.DebugInfo("[VC] MobileVoiceClient managed-starlight engine disposed"); }
        catch { }
    }

    internal int EngineDisposalCountForTest
        => Volatile.Read(ref _retiredEngineDisposalCount);

    internal bool HoldEngineCallForTest(ManualResetEventSlim acquired, ManualResetEventSlim release)
    {
        if (!TryAcquireEngine(out var engine)) return false;
        try
        {
            acquired.Set();
            release.Wait();
            return true;
        }
        finally
        {
            ReleaseEngine();
        }
    }

    private bool TryAcquireEngine(out ManagedVoiceEngine engine)
    {
        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                engine = null!;
                return false;
            }
            var current = Volatile.Read(ref _engine);
            if (current == null)
            {
                engine = null!;
                return false;
            }

            Interlocked.Increment(ref _activeEngineCalls);
            if (ReferenceEquals(Volatile.Read(ref _engine), current))
            {
                engine = current;
                return true;
            }
            ReleaseEngine();
        }
    }

    private void ReleaseEngine()
    {
        if (Interlocked.Decrement(ref _activeEngineCalls) == 0)
            QueueRetiredEngineDisposalIfReady();
    }

    private void Subscribe(ManagedVoiceEngine engine)
    {
        engine.LocalSdp += HandleLocalSdp;
        engine.LocalCandidate += HandleLocalCandidate;
        engine.PeerState += HandlePeerState;
        engine.LocalLevel += HandleLocalLevel;
        engine.PeerLevels += HandlePeerLevels;
    }

    private void Unsubscribe(ManagedVoiceEngine engine)
    {
        engine.LocalSdp -= HandleLocalSdp;
        engine.LocalCandidate -= HandleLocalCandidate;
        engine.PeerState -= HandlePeerState;
        engine.LocalLevel -= HandleLocalLevel;
        engine.PeerLevels -= HandlePeerLevels;
    }

    private void HandleLocalSdp(string peerId, int generation, string type, string sdp)
    {
        if (Volatile.Read(ref _disposed) == 0)
            OnLocalSdp?.Invoke(peerId, generation, type, sdp);
    }

    private void HandleLocalCandidate(string peerId, int generation, string candidate)
    {
        if (Volatile.Read(ref _disposed) == 0)
            OnLocalCandidate?.Invoke(peerId, generation, candidate);
    }

    private void HandlePeerState(string peerId, int generation, string state)
    {
        if (Volatile.Read(ref _disposed) == 0)
            OnPeerState?.Invoke(peerId, generation, state);
    }

    private void HandleLocalLevel(float peak, bool speaking)
    {
        if (Volatile.Read(ref _disposed) == 0)
            OnLevel?.Invoke(peak, speaking);
    }

    private void HandlePeerLevels(IReadOnlyList<ManagedPeerLevel> levels)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var mapped = new SidecarProtocol.PeerLevel[levels.Count];
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            mapped[i] = new SidecarProtocol.PeerLevel(level.PeerId, level.Peak);
        }
        OnPeerLevels?.Invoke(mapped);
    }

    private void ResetMicAccumulation()
    {
        Array.Clear(_micAccum, 0, _micAccum.Length);
        _micFill = 0;
    }

    private bool FailStart(string reason)
    {
        var delayMs = CompleteStart(success: false);
        StartRetryAfterMs = delayMs;
        VoiceDiagnostics.Log("voice.mobile.start",
            $"state=retry-scheduled backend=managed-starlight delayMs={delayMs} reason=\"{reason.Replace('"', '\'')}\"");
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
                _startFailures,
                InitialStartRetryMs,
                MaximumStartRetryMs);
            _startNotBeforeMs = Environment.TickCount64 + delayMs;
            return delayMs;
        }
    }
}
#endif
