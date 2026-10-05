using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct SidecarPlaybackState(
    string State,
    string Action,
    ulong StreamGeneration,
    string RequestedDevice,
    string ResolvedDevice,
    bool RequestedDefault,
    bool RequestedMatched,
    bool FellBackToDefault,
    bool Running,
    string Error = "",
    string ErrorCode = "",
    bool Changed = false);

internal readonly record struct SidecarCaptureState(
    string State,
    string Action,
    ulong StreamGeneration,
    bool Running,
    bool Changed);

/// <summary>
/// The narrow surface the process-lifetime host needs from the desktop helper client. Keeping
/// this seam explicit lets the lease/ownership rules be tested without launching pc-capture.
/// </summary>
internal interface ISidecarVoiceClient : IDisposable
{
    event Action<float[], int>? OnFrame;
    event Action<string>? OnDead;
    event Action<string, string>? OnRecoverableError;
    event Action<string, int, string, string>? OnLocalSdp;
    event Action<string, int, string>? OnLocalCandidate;
    event Action<string, int, string>? OnPeerState;
    event Action<float, bool>? OnLevel;
    event Action<IReadOnlyList<SidecarProtocol.PeerLevel>>? OnPeerLevels;
    event Action<SidecarPlaybackState>? OnPlaybackState;
    event Action<SidecarCaptureState>? OnCaptureState;

    CaptureHealth Health { get; }
    IReadOnlyList<VoiceDeviceInfo> OutputDevices { get; }
    bool CleanupComplete { get; }
    bool Start(string? micDevice, string? spkDevice);
    bool TryConfigureInitialCapture(
        string micDevice,
        string outputDevice,
        bool aec,
        bool agc,
        bool ns,
        bool nsVeryHigh,
        bool hpf,
        float gain,
        float vadThreshold,
        float noiseGateThreshold,
        bool synthetic,
        bool micActive,
        bool micWarm,
        bool monitorEnabled,
        bool monitorDelayed,
        float monitorGain,
        IEnumerable<IceServer>? iceServers);
    void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf);
    void SetSynthetic(bool enabled);
    void SetMonitor(bool enabled, bool delayed, float gain);
    void SetInput(float gain, float vadThreshold, float noiseGateThreshold);
    void SetMicActive(bool active);
    void SetMicWarm();
    bool ConfigurePrivateRadio(bool active, IReadOnlyList<string> receivers);
    void SelectMicDevice(string deviceId);
    bool SelectOutputDevice(string deviceId);
    bool ConfigureAudioRoute(
        string inputDevice,
        string outputDevice,
        SidecarCaptureMode captureMode,
        bool synthetic);
    void SendOutputTestFrame(float[] interleavedStereo);
    bool SendOutputTestFrameAndWait(float[] interleavedStereo);
    bool AddPeer(string peerId, bool isOfferer, int generation);
    bool RemovePeer(string peerId, int generation);
    bool RestartIce(string peerId, int generation, bool createOffer);
    bool SetRemoteSdp(string peerId, int generation, string sdpType, string sdp);
    bool AddIceCandidate(string peerId, int generation, string candidate);
    void SetIceServers(IEnumerable<IceServer> servers);
    void SendGameState(bool deaf, float master, IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers);
}

#if WINDOWS
internal sealed class SidecarVoiceCallbacks
{
    public SidecarVoiceCallbacks(
        Action<float[], int> onFrame,
        Action<string> onDead,
        Action<string, string> onRecoverableError,
        Action<string, int, string, string> onLocalSdp,
        Action<string, int, string> onLocalCandidate,
        Action<string, int, string> onPeerState,
        Action<float, bool> onLevel,
        Action<IReadOnlyList<SidecarProtocol.PeerLevel>> onPeerLevels,
        Action<SidecarCaptureState> onCaptureState,
        Action<SidecarPlaybackState> onPlaybackState)
    {
        OnFrame = onFrame ?? throw new ArgumentNullException(nameof(onFrame));
        OnDead = onDead ?? throw new ArgumentNullException(nameof(onDead));
        OnRecoverableError = onRecoverableError ?? throw new ArgumentNullException(nameof(onRecoverableError));
        OnLocalSdp = onLocalSdp ?? throw new ArgumentNullException(nameof(onLocalSdp));
        OnLocalCandidate = onLocalCandidate ?? throw new ArgumentNullException(nameof(onLocalCandidate));
        OnPeerState = onPeerState ?? throw new ArgumentNullException(nameof(onPeerState));
        OnLevel = onLevel ?? throw new ArgumentNullException(nameof(onLevel));
        OnPeerLevels = onPeerLevels ?? throw new ArgumentNullException(nameof(onPeerLevels));
        OnCaptureState = onCaptureState ?? throw new ArgumentNullException(nameof(onCaptureState));
        OnPlaybackState = onPlaybackState ?? throw new ArgumentNullException(nameof(onPlaybackState));
    }

    internal Action<float[], int> OnFrame { get; }
    internal Action<string> OnDead { get; }
    internal Action<string, string> OnRecoverableError { get; }
    internal Action<string, int, string, string> OnLocalSdp { get; }
    internal Action<string, int, string> OnLocalCandidate { get; }
    internal Action<string, int, string> OnPeerState { get; }
    internal Action<float, bool> OnLevel { get; }
    internal Action<IReadOnlyList<SidecarProtocol.PeerLevel>> OnPeerLevels { get; }
    internal Action<SidecarCaptureState> OnCaptureState { get; }
    internal Action<SidecarPlaybackState> OnPlaybackState { get; }
}

/// <summary>
/// Exclusive ownership of the helper's current voice session. Disposing the final lease clears
/// the session and terminates the helper process. Only the host coordinator remains reusable; a
/// later lobby launches a fresh helper without carrying audio-device or permission state across rooms.
/// </summary>
internal sealed class SidecarVoiceLease : IDisposable
{
    private readonly SidecarVoiceHostCore _host;
    private readonly SidecarVoiceCallbacks _callbacks;
    private readonly Dictionary<string, int> _peerGenerations = new(StringComparer.Ordinal);
    private int _active = 1;
    private readonly TaskCompletionSource<bool> _retirementCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Action<float[], int> _frameForwarder;
    private readonly Action<string> _deadForwarder;
    private readonly Action<string, string> _recoverableErrorForwarder;
    private readonly Action<string, int, string, string> _sdpForwarder;
    private readonly Action<string, int, string> _candidateForwarder;
    private readonly Action<string, int, string> _peerStateForwarder;
    private readonly Action<float, bool> _levelForwarder;
    private readonly Action<IReadOnlyList<SidecarProtocol.PeerLevel>> _peerLevelsForwarder;
    private readonly Action<SidecarCaptureState> _captureStateForwarder;
    private readonly Action<SidecarPlaybackState> _playbackStateForwarder;

    internal SidecarVoiceLease(SidecarVoiceHostCore host, long id, SidecarVoiceCallbacks callbacks)
    {
        _host = host;
        Id = id;
        _callbacks = callbacks;
        _frameForwarder = (frame, samples) => { if (IsActive) _callbacks.OnFrame(frame, samples); };
        _deadForwarder = reason => { if (IsActive) _callbacks.OnDead(reason); };
        _recoverableErrorForwarder = (code, message) => { if (IsActive) _callbacks.OnRecoverableError(code, message); };
        _sdpForwarder = (peer, generation, type, sdp) => { if (IsActive) _callbacks.OnLocalSdp(peer, generation, type, sdp); };
        _candidateForwarder = (peer, generation, candidate) => { if (IsActive) _callbacks.OnLocalCandidate(peer, generation, candidate); };
        _peerStateForwarder = (peer, generation, state) => { if (IsActive) _callbacks.OnPeerState(peer, generation, state); };
        _levelForwarder = (peak, speaking) => { if (IsActive) _callbacks.OnLevel(peak, speaking); };
        _peerLevelsForwarder = levels => { if (IsActive) _callbacks.OnPeerLevels(levels); };
        _captureStateForwarder = state => { if (IsActive) _callbacks.OnCaptureState(state); };
        _playbackStateForwarder = state => { if (IsActive) _callbacks.OnPlaybackState(state); };
    }

    internal long Id { get; }
    internal bool IsActive => Volatile.Read(ref _active) != 0;
    public CaptureHealth Health => _host.GetHealth(this);
    public IReadOnlyList<VoiceDeviceInfo> OutputDevices => _host.GetOutputDevices(this);

    internal void Attach(ISidecarVoiceClient client)
    {
        client.OnFrame += _frameForwarder;
        client.OnDead += _deadForwarder;
        client.OnRecoverableError += _recoverableErrorForwarder;
        client.OnLocalSdp += _sdpForwarder;
        client.OnLocalCandidate += _candidateForwarder;
        client.OnPeerState += _peerStateForwarder;
        client.OnLevel += _levelForwarder;
        client.OnPeerLevels += _peerLevelsForwarder;
        client.OnCaptureState += _captureStateForwarder;
        client.OnPlaybackState += _playbackStateForwarder;
    }

    internal void Detach(ISidecarVoiceClient client)
    {
        client.OnFrame -= _frameForwarder;
        client.OnDead -= _deadForwarder;
        client.OnRecoverableError -= _recoverableErrorForwarder;
        client.OnLocalSdp -= _sdpForwarder;
        client.OnLocalCandidate -= _candidateForwarder;
        client.OnPeerState -= _peerStateForwarder;
        client.OnLevel -= _levelForwarder;
        client.OnPeerLevels -= _peerLevelsForwarder;
        client.OnCaptureState -= _captureStateForwarder;
        client.OnPlaybackState -= _playbackStateForwarder;
    }

    internal void Deactivate() => Interlocked.Exchange(ref _active, 0);
    internal void TrackPeer(string peerId, int generation)
    {
        if (!string.IsNullOrEmpty(peerId)) _peerGenerations[peerId] = generation;
    }
    internal void UntrackPeer(string peerId)
    {
        if (!string.IsNullOrEmpty(peerId)) _peerGenerations.Remove(peerId);
    }
    internal KeyValuePair<string, int>[] TakePeers()
    {
        var peers = _peerGenerations.ToArray();
        _peerGenerations.Clear();
        return peers;
    }

    public bool EnsureStarted(string micDevice, string outputDevice)
        => _host.EnsureStarted(this, micDevice, outputDevice);

    public bool TryConfigureInitialCapture(
        string micDevice,
        string outputDevice,
        bool aec,
        bool agc,
        bool ns,
        bool nsVeryHigh,
        bool hpf,
        float gain,
        float vadThreshold,
        float noiseGateThreshold,
        bool synthetic,
        bool micActive,
        bool micWarm,
        bool monitorEnabled,
        bool monitorDelayed,
        float monitorGain,
        IEnumerable<IceServer>? iceServers)
    {
        var iceServerSnapshot = iceServers?.ToArray();
        return _host.Execute(this, client => client.TryConfigureInitialCapture(
            micDevice, outputDevice, aec, agc, ns, nsVeryHigh, hpf, gain, vadThreshold, noiseGateThreshold,
            synthetic, micActive, micWarm, monitorEnabled, monitorDelayed, monitorGain, iceServerSnapshot));
    }

    public void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf)
        => _host.Submit(this, client => client.SetDsp(aec, agc, ns, nsVeryHigh, hpf));
    public void SetSynthetic(bool enabled)
        => _host.Submit(this, client => client.SetSynthetic(enabled));
    public void SetMonitor(bool enabled, bool delayed, float gain)
        => _host.Submit(this, client => client.SetMonitor(enabled, delayed, gain));
    public void SetInput(float gain, float vadThreshold, float noiseGateThreshold)
        => _host.Submit(this, client => client.SetInput(gain, vadThreshold, noiseGateThreshold));
    public void SetMicActive(bool active)
        => _host.SubmitCritical(this, client => client.SetMicActive(active));
    public void SetMicWarm()
        => _host.SubmitCritical(this, client => client.SetMicWarm());
    public bool ConfigurePrivateRadio(bool active, IReadOnlyList<string> receivers)
    {
        var snapshot = active ? receivers.ToArray() : Array.Empty<string>();
        return _host.ExecuteCritical(this, client => client.ConfigurePrivateRadio(active, snapshot));
    }
    public void SelectMicDevice(string deviceId)
        => _host.Submit(this, client => client.SelectMicDevice(deviceId));
    public bool SelectOutputDevice(string deviceId)
        => _host.Submit(this, client => client.SelectOutputDevice(deviceId));
    public bool TrySelectOutputDeviceIf(string deviceId, Func<bool> stillCurrent)
        => _host.Submit(this, client => client.SelectOutputDevice(deviceId), stillCurrent);
    public bool ConfigureAudioRoute(
        string inputDevice,
        string outputDevice,
        SidecarCaptureMode captureMode,
        bool synthetic)
        => _host.SubmitCritical(this, client => client.ConfigureAudioRoute(
            inputDevice, outputDevice, captureMode, synthetic));
    public bool TryConfigureAudioRouteIf(
        string inputDevice,
        string outputDevice,
        SidecarCaptureMode captureMode,
        bool synthetic,
        Func<bool> stillCurrent)
        => _host.SubmitCritical(this, client => client.ConfigureAudioRoute(
            inputDevice, outputDevice, captureMode, synthetic), stillCurrent);
    internal bool ConfigureAudioRouteAndWait(
        string inputDevice,
        string outputDevice,
        SidecarCaptureMode captureMode,
        bool synthetic)
        => _host.ExecuteCritical(this, client => client.ConfigureAudioRoute(
            inputDevice, outputDevice, captureMode, synthetic));
    internal bool TryConfigureAudioRouteIfAndWait(
        string inputDevice,
        string outputDevice,
        SidecarCaptureMode captureMode,
        bool synthetic,
        Func<bool> stillCurrent)
        => _host.ExecuteCritical(this, client => client.ConfigureAudioRoute(
            inputDevice, outputDevice, captureMode, synthetic), stillCurrent);
    public void SendOutputTestFrame(float[] interleavedStereo)
    {
        if (interleavedStereo == null) throw new ArgumentNullException(nameof(interleavedStereo));
        var snapshot = (float[])interleavedStereo.Clone();
        _host.Submit(this, client => client.SendOutputTestFrame(snapshot));
    }
    public bool SendOutputTestFrameAndWait(float[] interleavedStereo)
    {
        if (interleavedStereo == null) throw new ArgumentNullException(nameof(interleavedStereo));
        return _host.Execute(this, client => client.SendOutputTestFrameAndWait(interleavedStereo));
    }
    public bool AddPeer(string peerId, bool isOfferer, int generation)
        => _host.Submit(this, client =>
        {
            if (!client.AddPeer(peerId, isOfferer, generation)) return false;
            TrackPeer(peerId, generation);
            return true;
        });
    public bool RemovePeer(string peerId, int generation)
        => _host.Submit(this, client =>
        {
            var written = client.RemovePeer(peerId, generation);
            if (written) UntrackPeer(peerId);
            return written;
        });
    public bool RestartIce(string peerId, int generation, bool createOffer)
        => _host.Submit(this, client => client.RestartIce(peerId, generation, createOffer));
    public bool SetRemoteSdp(string peerId, int generation, string sdpType, string sdp)
        => _host.Submit(this, client => client.SetRemoteSdp(peerId, generation, sdpType, sdp));
    public bool AddIceCandidate(string peerId, int generation, string candidate)
        => _host.Submit(this, client => client.AddIceCandidate(peerId, generation, candidate));
    public void SetIceServers(IEnumerable<IceServer> servers)
    {
        if (servers == null) return;
        var snapshot = servers.ToArray();
        _host.Submit(this, client => client.SetIceServers(snapshot));
    }
    public void SendGameState(bool deaf, float master, IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
    {
        var snapshot = peers?.ToArray() ?? Array.Empty<SidecarProtocol.GameStatePeerInput>();
        _host.Submit(this, client => client.SendGameState(deaf, master, snapshot));
    }

    internal Task RetirementCompletion => _retirementCompletion.Task;
    internal void CompleteRetirement() => _retirementCompletion.TrySetResult(true);

    public Task ReleaseAsync()
    {
        _host.Release(this, "lease-release-async");
        return RetirementCompletion;
    }

    public void Dispose() => _host.Release(this, "lease-dispose");
}

internal sealed class SidecarVoiceHostCore
{
    private sealed class StartupAttempt
    {
        internal readonly ManualResetEventSlim Completed = new();
        internal bool Result;
    }

    private sealed class CommandWork
    {
        internal Func<ISidecarVoiceClient, bool> Action = null!;
        internal Func<bool>? Predicate;
        internal ManualResetEventSlim? Completed;
        internal bool Result;
        internal long Generation;
        internal bool Retirement;
        internal bool Quiesce;
        internal string Reason = string.Empty;
        internal bool CriticalState;
    }

    private abstract class RetirementSession
    {
        private readonly Action<RetirementSession> _retired;

        protected RetirementSession(
            ISidecarVoiceClient client,
            SidecarVoiceLease lease,
            long generation,
            Action<RetirementSession> retired)
        {
            Client = client;
            Lease = lease;
            Generation = generation;
            _retired = retired;
        }

        internal ISidecarVoiceClient Client { get; }
        internal SidecarVoiceLease Lease { get; }
        internal long Generation { get; }

        internal abstract void BeginRetirement(bool quiesce, string reason);

        protected void Retire(bool quiesce, string reason)
        {
            try
            {
                if (quiesce)
                {
                    try { Quiesce(Client, Lease.TakePeers(), reason); } catch { }
                }
                else
                {
                    try { Lease.TakePeers(); } catch { }
                }
                try { Lease.Detach(Client); } catch { }
                try { Client.Dispose(); } catch { }
                while (true)
                {
                    try
                    {
                        if (Client.CleanupComplete) break;
                    }
                    catch
                    {
                        break;
                    }
                    Thread.Sleep(10);
                }
            }
            finally
            {
                _retired(this);
            }
        }
    }


    private sealed class FallbackRetirementSession : RetirementSession
    {
        private int _retirementStarted;

        internal FallbackRetirementSession(
            ISidecarVoiceClient client,
            SidecarVoiceLease lease,
            long generation,
            Action<RetirementSession> retired)
            : base(client, lease, generation, retired)
        {
        }

        internal override void BeginRetirement(bool quiesce, string reason)
        {
            if (Interlocked.Exchange(ref _retirementStarted, 1) != 0) return;
            _ = Task.Run(() => Retire(quiesce, reason));
        }
    }

    private sealed class CommandSession : RetirementSession
    {
        private readonly object _queueGate = new();
        private readonly Queue<CommandWork> _queue = new();
        private readonly AutoResetEvent _available = new(false);
        private readonly int _capacity;
        private readonly Action<CommandSession> _criticalStateFailed;
        private CommandWork? _reservedCriticalState;
        private bool _accepting = true;

        internal CommandSession(
            ISidecarVoiceClient client,
            SidecarVoiceLease lease,
            long generation,
            int capacity,
            Action<RetirementSession> retired,
            Action<CommandSession> criticalStateFailed,
            Action<ThreadStart, string> startWorker)
            : base(client, lease, generation, retired)
        {
            _capacity = capacity;
            _criticalStateFailed = criticalStateFailed;
            startWorker(Run, $"SidecarVoiceCommands-{generation}");
        }

        internal bool TryEnqueue(CommandWork work)
        {
            lock (_queueGate)
            {
                if (!_accepting) return false;
                work.Generation = Generation;
                if (_reservedCriticalState == null && _queue.Count < _capacity)
                    _queue.Enqueue(work);
                else if (work.CriticalState && _reservedCriticalState == null)
                    _reservedCriticalState = work;
                else
                    return false;
                _available.Set();
                return true;
            }
        }

        internal override void BeginRetirement(bool quiesce, string reason)
        {
            lock (_queueGate)
            {
                if (!_accepting) return;
                _accepting = false;
                if (_reservedCriticalState != null)
                {
                    _queue.Enqueue(_reservedCriticalState);
                    _reservedCriticalState = null;
                }
                _queue.Enqueue(new CommandWork
                {
                    Action = _ => true,
                    Generation = Generation,
                    Retirement = true,
                    Quiesce = quiesce,
                    Reason = reason,
                });
                _available.Set();
            }
        }

        private void Run()
        {
            while (true)
            {
                CommandWork? work = null;
                lock (_queueGate)
                {
                    if (_queue.Count != 0)
                        work = _queue.Dequeue();
                    else if (_reservedCriticalState != null)
                    {
                        work = _reservedCriticalState;
                        _reservedCriticalState = null;
                    }
                }
                if (work == null)
                {
                    _available.WaitOne();
                    continue;
                }
                if (work.Retirement)
                {
                    Retire(work.Quiesce, work.Reason);
                    return;
                }

                var current = false;
                try
                {
                    current = work.Generation == Generation
                              && (work.Predicate == null || work.Predicate());
                    work.Result = current && work.Action(Client);
                }
                catch
                {
                    work.Result = false;
                }
                finally
                {
                    if (current && work.CriticalState && !work.Result)
                        _criticalStateFailed(this);
                    work.Completed?.Set();
                }
            }
        }
    }

    private const int DefaultCommandCapacity = 256;
    private readonly object _gate = new();
    private readonly Func<ISidecarVoiceClient> _createClient;
    private readonly Action<ThreadStart, string> _startCommandWorker;
    private readonly int _commandCapacity;
    private CommandSession? _session;
    private RetirementSession? _retiring;
    private StartupAttempt? _starting;
    private SidecarVoiceLease? _owner;
    private long _nextLeaseId;
    private long _nextGeneration;
    private bool _shutdown;

    internal SidecarVoiceHostCore(Func<ISidecarVoiceClient> createClient)
        : this(createClient, DefaultCommandCapacity)
    {
    }

    internal SidecarVoiceHostCore(
        Func<ISidecarVoiceClient> createClient,
        int commandCapacity,
        Action<ThreadStart, string>? startCommandWorker = null)
    {
        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
        if (commandCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        _commandCapacity = commandCapacity;
        _startCommandWorker = startCommandWorker ?? StartCommandWorker;
    }

    internal SidecarVoiceLease? TryAcquire(SidecarVoiceCallbacks callbacks, out string failure)
    {
        SidecarVoiceLease? lease = null;
        lock (_gate)
        {
            if (_shutdown)
                failure = "host-shutdown";
            else if (_owner != null)
                failure = $"lease-active:{_owner.Id}";
            else if (_starting != null || _retiring != null)
                failure = "helper-retiring";
            else
            {
                lease = new SidecarVoiceLease(this, ++_nextLeaseId, callbacks);
                _owner = lease;
                failure = string.Empty;
            }
        }
        if (lease != null)
            VoiceDiagnostics.Log("sidecar.host", $"event=lease-acquired lease={lease.Id} reused=false");
        return lease;
    }

    internal bool EnsureStarted(SidecarVoiceLease lease, string micDevice, string outputDevice)
    {
        StartupAttempt? waiting;
        CommandSession? existing;
        var reserved = false;
        lock (_gate)
        {
            if (!OwnsLocked(lease)) return false;
            existing = _session;
            waiting = _starting;
            if (existing == null && waiting == null)
            {
                if (_retiring != null) return false;
                waiting = new StartupAttempt();
                _starting = waiting;
                reserved = true;
            }
        }

        if (existing != null)
        {
            CaptureHealth health;
            try { health = existing.Client.Health; }
            catch { health = CaptureHealth.Dead; }
            if (health == CaptureHealth.Healthy)
            {
                lock (_gate)
                    return OwnsLocked(lease) && ReferenceEquals(_session, existing);
            }
            RetireDeadSession(lease, existing);
            return false;
        }

        if (!reserved)
        {
            waiting!.Completed.Wait();
            return waiting.Result && lease.IsActive;
        }

        return StartReserved(lease, waiting!, micDevice, outputDevice);
    }


    private bool StartReserved(
        SidecarVoiceLease lease,
        StartupAttempt attempt,
        string micDevice,
        string outputDevice)
    {
        ISidecarVoiceClient? client = null;
        var started = false;
        try
        {
            client = _createClient();
            lease.Attach(client);
            started = client.Start(micDevice, outputDevice);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar.host", $"event=start-threw lease={lease.Id} error=\"{ex.Message}\"");
        }

        var healthy = false;
        if (client != null && started)
        {
            try { healthy = client.Health == CaptureHealth.Healthy; }
            catch { }
        }

        CommandSession? session = null;
        RetirementSession? retirement = null;
        var generation = Interlocked.Increment(ref _nextGeneration);
        var commandWorkerFailed = false;
        if (client != null)
        {
            try
            {
                session = new CommandSession(
                    client,
                    lease,
                    generation,
                    _commandCapacity,
                    OnRetired,
                    RetireAfterCriticalExecutionFailure,
                    _startCommandWorker);
            }
            catch (Exception ex)
            {
                commandWorkerFailed = true;
                retirement = new FallbackRetirementSession(client, lease, generation, OnRetired);
                VoiceDiagnostics.Log("sidecar.host", $"event=queue-start-failed lease={lease.Id} error=\"{ex.Message}\"");
            }
        }

        var accepted = false;
        var completeReleasedLease = false;
        lock (_gate)
        {
            if (ReferenceEquals(_starting, attempt))
                _starting = null;
            accepted = session != null
                       && healthy
                       && OwnsLocked(lease)
                       && _session == null
                       && _retiring == null;
            if (accepted)
                _session = session;
            else
            {
                retirement ??= session;
                if (retirement != null)
                    _retiring = retirement;
                else
                    completeReleasedLease = !lease.IsActive;
            }
            attempt.Result = accepted;
        }

        if (accepted)
            VoiceDiagnostics.Log("sidecar.host", $"event=helper-ready lease={lease.Id}");
        else if (retirement != null)
            retirement.BeginRetirement(
                started,
                commandWorkerFailed ? "queue-start-failed" : "start-failed");
        else if (completeReleasedLease)
            lease.CompleteRetirement();
        attempt.Completed.Set();
        return accepted;
    }

    internal CaptureHealth GetHealth(SidecarVoiceLease lease)
    {
        CommandSession? session;
        lock (_gate)
            session = OwnsLocked(lease) && _starting == null ? _session : null;
        if (session == null) return CaptureHealth.Dead;
        CaptureHealth health;
        try { health = session.Client.Health; }
        catch { return CaptureHealth.Dead; }
        lock (_gate)
            return OwnsLocked(lease) && ReferenceEquals(_session, session)
                ? health
                : CaptureHealth.Dead;
    }

    internal IReadOnlyList<VoiceDeviceInfo> GetOutputDevices(SidecarVoiceLease lease)
    {
        CommandSession? session;
        lock (_gate)
            session = OwnsLocked(lease) && _starting == null ? _session : null;
        if (session == null) return Array.Empty<VoiceDeviceInfo>();
        VoiceDeviceInfo[] devices;
        try { devices = session.Client.OutputDevices.ToArray(); }
        catch { return Array.Empty<VoiceDeviceInfo>(); }
        lock (_gate)
            return OwnsLocked(lease) && ReferenceEquals(_session, session)
                ? devices
                : Array.Empty<VoiceDeviceInfo>();
    }

    internal void Submit(SidecarVoiceLease lease, Action<ISidecarVoiceClient> action)
        => Submit(lease, client =>
        {
            action(client);
            return true;
        });

    internal void SubmitCritical(SidecarVoiceLease lease, Action<ISidecarVoiceClient> action)
        => SubmitCritical(lease, client =>
        {
            action(client);
            return true;
        });

    internal bool SubmitCritical(
        SidecarVoiceLease lease,
        Func<ISidecarVoiceClient, bool> action,
        Func<bool>? predicate = null)
    {
        CommandSession? session;
        lock (_gate)
            session = OwnsLocked(lease) && _starting == null ? _session : null;
        if (session == null) return false;
        if (session.TryEnqueue(new CommandWork
        {
            Action = action,
            Predicate = predicate,
            CriticalState = true,
        }))
            return true;
        RetireAfterCriticalAdmissionFailure(lease, session);
        return false;
    }

    internal bool Submit(
        SidecarVoiceLease lease,
        Func<ISidecarVoiceClient, bool> action,
        Func<bool>? predicate = null)
    {
        CommandSession? session;
        lock (_gate)
            session = OwnsLocked(lease) && _starting == null ? _session : null;
        return session?.TryEnqueue(new CommandWork
        {
            Action = action,
            Predicate = predicate,
        }) == true;
    }

    internal bool ExecuteCritical(
        SidecarVoiceLease lease,
        Func<ISidecarVoiceClient, bool> action,
        Func<bool>? predicate = null)
    {
        var completed = new ManualResetEventSlim();
        var work = new CommandWork
        {
            Action = action,
            Predicate = predicate,
            Completed = completed,
            CriticalState = true,
        };
        CommandSession? session;
        lock (_gate)
            session = OwnsLocked(lease) && _starting == null ? _session : null;
        if (session == null) return false;
        if (!session.TryEnqueue(work))
        {
            RetireAfterCriticalAdmissionFailure(lease, session);
            return false;
        }
        completed.Wait();
        return work.Result;
    }

    internal bool Execute(
        SidecarVoiceLease lease,
        Func<ISidecarVoiceClient, bool> action,
        Func<bool>? predicate = null)
    {
        var completed = new ManualResetEventSlim();
        var work = new CommandWork
        {
            Action = action,
            Predicate = predicate,
            Completed = completed,
        };
        CommandSession? session;
        lock (_gate)
            session = OwnsLocked(lease) && _starting == null ? _session : null;
        if (session == null || !session.TryEnqueue(work)) return false;
        completed.Wait();
        return work.Result;
    }

    internal void Release(SidecarVoiceLease lease, string reason)
    {
        RetirementSession? retiring = null;
        var complete = false;
        var helperAlive = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, lease))
            {
                lease.Deactivate();
                complete = _starting == null
                           && !ReferenceEquals(_session?.Lease, lease)
                           && !ReferenceEquals(_retiring?.Lease, lease);
            }
            else
            {
                lease.Deactivate();
                _owner = null;
                if (_session != null)
                {
                    retiring = _session;
                    _session = null;
                    _retiring = retiring;
                }
                else if (_starting == null && !ReferenceEquals(_retiring?.Lease, lease))
                {
                    complete = true;
                }
            }
            helperAlive = ReferenceEquals(_retiring?.Lease, lease);
        }
        retiring?.BeginRetirement(true, reason);
        if (complete)
            lease.CompleteRetirement();
        VoiceDiagnostics.Log(
            "sidecar.host",
            $"event=lease-released lease={lease.Id} reason={reason} helperAlive={helperAlive.ToString().ToLowerInvariant()}");
    }

    internal void Shutdown(string reason)
    {
        RetirementSession? retiring = null;
        SidecarVoiceLease? complete = null;
        lock (_gate)
        {
            if (_shutdown) return;
            _shutdown = true;
            var owner = _owner;
            if (owner != null)
                owner.Deactivate();
            _owner = null;
            if (_session != null)
            {
                retiring = _session;
                _session = null;
                _retiring = retiring;
            }
            else if (owner != null
                     && _starting == null
                     && !ReferenceEquals(_retiring?.Lease, owner))
            {
                complete = owner;
            }
        }
        retiring?.BeginRetirement(true, $"process-shutdown:{reason}");
        complete?.CompleteRetirement();
        VoiceDiagnostics.Log("sidecar.host", $"event=shutdown reason={reason}");
    }

    private void RetireAfterCriticalAdmissionFailure(
        SidecarVoiceLease lease,
        CommandSession session)
    {
        var retire = false;
        lock (_gate)
        {
            if (!OwnsLocked(lease) || !ReferenceEquals(_session, session)) return;
            _session = null;
            _retiring = session;
            retire = true;
        }
        if (retire)
            session.BeginRetirement(true, "critical-state-admission-failed");
    }
    private void RetireAfterCriticalExecutionFailure(CommandSession session)
    {
        var retire = false;
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session)) return;
            _session = null;
            _retiring = session;
            retire = true;
        }
        if (retire)
            session.BeginRetirement(true, "critical-state-execution-failed");
    }


    private void RetireDeadSession(SidecarVoiceLease lease, CommandSession session)
    {
        lock (_gate)
        {
            if (!OwnsLocked(lease) || !ReferenceEquals(_session, session)) return;
            _session = null;
            _retiring = session;
            session.BeginRetirement(false, "dead-before-start");
        }
    }

    private void OnRetired(RetirementSession session)
    {
        var cleared = false;
        lock (_gate)
        {
            if (ReferenceEquals(_retiring, session))
            {
                _retiring = null;
                cleared = true;
            }
        }
        if (cleared && !session.Lease.IsActive)
            session.Lease.CompleteRetirement();
        VoiceDiagnostics.Log("sidecar.host", $"event=helper-dropped generation={session.Generation}");
    }

    private bool OwnsLocked(SidecarVoiceLease lease)
        => !_shutdown && lease.IsActive && ReferenceEquals(_owner, lease);

    private static void StartCommandWorker(ThreadStart run, string name)
    {
        new Thread(run)
        {
            IsBackground = true,
            Name = name,
        }.Start();
    }

    private static void Quiesce(
        ISidecarVoiceClient client,
        IReadOnlyList<KeyValuePair<string, int>> peers,
        string reason)
    {
        try { client.SetMicActive(false); } catch { }
        try { client.SetSynthetic(false); } catch { }
        try { client.SetMonitor(false, false, 1f); } catch { }
        try
        {
            client.SendGameState(
                deaf: true,
                master: 0f,
                peers: Array.Empty<SidecarProtocol.GameStatePeerInput>());
        }
        catch { }
        foreach (var peer in peers)
            try { client.RemovePeer(peer.Key, peer.Value); } catch { }
        VoiceDiagnostics.Log("sidecar.host", $"event=session-quiesced reason={reason} peersRemoved={peers.Count}");
    }
}

internal static class SidecarVoiceHost
{
    private static readonly SidecarVoiceHostCore Host = new(CreateClient);

    internal static SidecarVoiceLease? TryAcquire(SidecarVoiceCallbacks callbacks, out string failure)
        => Host.TryAcquire(callbacks, out failure);

    internal static void Shutdown(string reason) => Host.Shutdown(reason);

    private static ISidecarVoiceClient CreateClient()
        => new SidecarVoiceClient(LaunchSidecarHelper);

    private static SidecarLaunchResult LaunchSidecarHelper(string token, string deviceId)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var helperPath = SidecarLauncher.EnsureHelperExtracted(
            assembly,
            SidecarLauncher.NativeCacheBaseDirectory(),
            force: false);
        return SidecarLauncher.Launch(
            helperPath,
            token,
            handshakeTimeoutMs: 4000,
            wine: WineEnvironment.IsWine,
            resolveWineHostPath: WineEnvironment.ResolveHostPath);
    }
}
#endif
