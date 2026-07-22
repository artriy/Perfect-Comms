using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

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
    string ErrorCode = "");

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
        IEnumerable<IceServer>? iceServers);
    void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf);
    void SetSynthetic(bool enabled);
    void SetMonitor(bool enabled, bool delayed, float gain);
    void SetInput(float gain, float vadThreshold, float noiseGateThreshold);
    void SetMicActive(bool active);
    void SetMicWarm();
    void SelectMicDevice(string deviceId);
    bool SelectOutputDevice(string deviceId);
    void SendOutputTestFrame(float[] interleavedStereo);
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
        IEnumerable<IceServer>? iceServers)
        => _host.Use(this, client => client.TryConfigureInitialCapture(
            micDevice, outputDevice, aec, agc, ns, nsVeryHigh, hpf, gain, vadThreshold, noiseGateThreshold,
            synthetic, micActive, micWarm, iceServers), false);

    public void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf)
        => _host.Use(this, client => client.SetDsp(aec, agc, ns, nsVeryHigh, hpf));
    public void SetSynthetic(bool enabled)
        => _host.Use(this, client => client.SetSynthetic(enabled));
    public void SetMonitor(bool enabled, bool delayed, float gain)
        => _host.Use(this, client => client.SetMonitor(enabled, delayed, gain));
    public void SetInput(float gain, float vadThreshold, float noiseGateThreshold)
        => _host.Use(this, client => client.SetInput(gain, vadThreshold, noiseGateThreshold));
    public void SetMicActive(bool active)
        => _host.Use(this, client => client.SetMicActive(active));
    public void SetMicWarm()
        => _host.Use(this, client => client.SetMicWarm());
    public void SelectMicDevice(string deviceId)
        => _host.Use(this, client => client.SelectMicDevice(deviceId));
    public bool SelectOutputDevice(string deviceId)
        => _host.Use(this, client => client.SelectOutputDevice(deviceId), false);
    public bool TrySelectOutputDeviceIf(string deviceId, Func<bool> stillCurrent)
        => _host.Use(this, client =>
            stillCurrent() && client.SelectOutputDevice(deviceId), false);
    public void SendOutputTestFrame(float[] interleavedStereo)
        => _host.Use(this, client => client.SendOutputTestFrame(interleavedStereo));
    public bool AddPeer(string peerId, bool isOfferer, int generation)
        => _host.Use(this, client =>
        {
            if (!client.AddPeer(peerId, isOfferer, generation)) return false;
            TrackPeer(peerId, generation);
            return true;
        }, false);
    public bool RemovePeer(string peerId, int generation)
        => _host.Use(this, client =>
        {
            var written = client.RemovePeer(peerId, generation);
            if (written) UntrackPeer(peerId);
            return written;
        }, false);
    public bool RestartIce(string peerId, int generation, bool createOffer)
        => _host.Use(this, client => client.RestartIce(peerId, generation, createOffer), false);
    public bool SetRemoteSdp(string peerId, int generation, string sdpType, string sdp)
        => _host.Use(this, client => client.SetRemoteSdp(peerId, generation, sdpType, sdp), false);
    public bool AddIceCandidate(string peerId, int generation, string candidate)
        => _host.Use(this, client => client.AddIceCandidate(peerId, generation, candidate), false);
    public void SetIceServers(IEnumerable<IceServer> servers)
        => _host.Use(this, client => client.SetIceServers(servers));
    public void SendGameState(bool deaf, float master, IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
        => _host.Use(this, client => client.SendGameState(deaf, master, peers));

    public void Dispose() => _host.Release(this, "lease-dispose");
}

/// <summary>
/// Serializes helper start, lease handoff and process shutdown. The gate is intentionally held
/// across Start/config commands: those are rare lifecycle operations, and serialization prevents
/// a disposed backend's async start from racing room teardown or the next lobby's configuration.
/// </summary>
internal sealed class SidecarVoiceHostCore
{
    private readonly object _gate = new();
    private readonly Func<ISidecarVoiceClient> _createClient;
    private ISidecarVoiceClient? _client;
    private SidecarVoiceLease? _owner;
    private long _nextLeaseId;
    private bool _shutdown;

    internal SidecarVoiceHostCore(Func<ISidecarVoiceClient> createClient)
    {
        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
    }

    internal SidecarVoiceLease? TryAcquire(SidecarVoiceCallbacks callbacks, out string failure)
    {
        lock (_gate)
        {
            if (_shutdown)
            {
                failure = "host-shutdown";
                return null;
            }
            if (_owner != null)
            {
                failure = $"lease-active:{_owner.Id}";
                return null;
            }

            if (_client != null && _client.Health == CaptureHealth.Dead)
                DropClientLocked(null, "dead-before-acquire");

            var lease = new SidecarVoiceLease(this, ++_nextLeaseId, callbacks);
            _owner = lease;
            if (_client != null) lease.Attach(_client);
            failure = string.Empty;
            VoiceDiagnostics.Log(
                "sidecar.host",
                $"event=lease-acquired lease={lease.Id} reused={(_client != null).ToString().ToLowerInvariant()}");
            return lease;
        }
    }

    internal bool EnsureStarted(SidecarVoiceLease lease, string micDevice, string outputDevice)
    {
        lock (_gate)
        {
            if (!OwnsLocked(lease)) return false;
            if (_client != null && _client.Health == CaptureHealth.Healthy) return true;
            if (_client != null) DropClientLocked(lease, "dead-before-start");

            ISidecarVoiceClient client;
            try
            {
                client = _createClient();
                _client = client;
                lease.Attach(client);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("sidecar.host", $"event=create-failed lease={lease.Id} error=\"{ex.Message}\"");
                _client = null;
                return false;
            }

            bool started;
            try { started = client.Start(micDevice, outputDevice); }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("sidecar.host", $"event=start-threw lease={lease.Id} error=\"{ex.Message}\"");
                started = false;
            }

            if (started && client.Health == CaptureHealth.Healthy && OwnsLocked(lease))
            {
                VoiceDiagnostics.Log("sidecar.host", $"event=helper-ready lease={lease.Id}");
                return true;
            }

            DropClientLocked(lease, "start-failed");
            return false;
        }
    }

    internal CaptureHealth GetHealth(SidecarVoiceLease lease)
    {
        lock (_gate)
            return OwnsLocked(lease) ? _client?.Health ?? CaptureHealth.Dead : CaptureHealth.Dead;
    }

    internal IReadOnlyList<VoiceDeviceInfo> GetOutputDevices(SidecarVoiceLease lease)
    {
        lock (_gate)
            return OwnsLocked(lease) && _client != null
                ? _client.OutputDevices.ToArray()
                : Array.Empty<VoiceDeviceInfo>();
    }

    internal void Use(SidecarVoiceLease lease, Action<ISidecarVoiceClient> action)
    {
        lock (_gate)
        {
            if (!OwnsLocked(lease) || _client == null || _client.Health == CaptureHealth.Dead) return;
            action(_client);
        }
    }

    internal T Use<T>(SidecarVoiceLease lease, Func<ISidecarVoiceClient, T> action, T fallback)
    {
        lock (_gate)
        {
            if (!OwnsLocked(lease) || _client == null || _client.Health == CaptureHealth.Dead) return fallback;
            return action(_client);
        }
    }

    internal void Release(SidecarVoiceLease lease, string reason)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, lease))
            {
                lease.Deactivate();
                return;
            }

            lease.Deactivate();
            var client = _client;
            if (client != null)
            {
                lease.Detach(client);
                if (client.Health != CaptureHealth.Dead)
                    QuiesceLocked(client, lease.TakePeers(), reason);
                else
                    lease.TakePeers();
            }
            _owner = null;

            // A room lease is the helper's process-lifetime boundary. EndGame -> lobby transitions
            // retain the same room and therefore never release this lease; an actual lobby exit
            // does release it and must not leave pc-capture idle in the background. DropClientLocked
            // disposes the IPC client/process but does not set _shutdown, so a later lobby can acquire
            // the host and launch a fresh helper.
            if (client != null)
                DropClientLocked(null, $"lease-release:{reason}");

            VoiceDiagnostics.Log(
                "sidecar.host",
                $"event=lease-released lease={lease.Id} reason={reason} helperAlive={(_client != null).ToString().ToLowerInvariant()}");
        }
    }

    internal void Shutdown(string reason)
    {
        lock (_gate)
        {
            if (_shutdown && _client == null) return;
            _shutdown = true;
            var owner = _owner;
            if (owner != null)
            {
                owner.Deactivate();
                if (_client != null) owner.Detach(_client);
                owner.TakePeers();
            }
            _owner = null;
            DropClientLocked(null, $"process-shutdown:{reason}");
            VoiceDiagnostics.Log("sidecar.host", $"event=shutdown reason={reason}");
        }
    }

    private bool OwnsLocked(SidecarVoiceLease lease)
        => !_shutdown && lease.IsActive && ReferenceEquals(_owner, lease);

    private static void QuiesceLocked(
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

    private void DropClientLocked(SidecarVoiceLease? attachedLease, string reason)
    {
        var client = _client;
        _client = null;
        if (client == null) return;
        if (attachedLease != null)
            try { attachedLease.Detach(client); } catch { }
        try { client.Dispose(); } catch { }
        VoiceDiagnostics.Log("sidecar.host", $"event=helper-dropped reason={reason}");
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
        var helperPath = SidecarLauncher.EnsureHelperExtracted(assembly, AppContext.BaseDirectory, force: false);
        return SidecarLauncher.Launch(
            helperPath,
            token,
            handshakeTimeoutMs: 4000,
            wine: WineEnvironment.IsWine,
            resolveWineHostPath: WineEnvironment.ResolveHostPath);
    }
}
#endif
