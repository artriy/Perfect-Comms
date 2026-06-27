using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
#if ANDROID
using SIPSorceryMedia.Abstractions;
#endif
using SocketIOClient;
using UnityEngine;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class PerfectCommsVoiceBackend : IVoiceBackend
{
    private const int DataControlPrefixLength = 4;
    // Hard ceiling on an untrusted inbound datagram (largest legit message is a ~4 KB voice packet).
    private const int MaxIncomingDatagramBytes = 16 * 1024;
    private static readonly int PerfectCommsOpusBitrate = 48_000;
    private static readonly bool PerfectCommsOpusUseConstrainedVbr = true;
    private static readonly bool PerfectCommsOpusUseInbandFec = true;   // arm LossResistant flag (sender) + the jitter-buffer Fec drain arm
    private static readonly int PerfectCommsOpusPacketLossPercent = 15; // non-zero PLP so Opus embeds FEC redundancy in the wire frame
    private const int PlaybackLatencyMs = 60;
    private const int JitterTargetDelayFrames = 2;
    private const int JitterMaxBufferedFrames = 25;
    private const int JitterMinTargetFrames = 1;
    private const int JitterMaxTargetFrames = 15;
    private const int CodecAdaptIntervalFrames = 100;
    private const int PlpDeadbandPercent = 3;
    private const float RemoteSpeakingThreshold = 0.004f;
    private const double SyntheticToneFrequency = 220.0;
    private const float SyntheticToneAmplitude = 0.012f;
    private static readonly TimeSpan RemoteActivityHold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MicCalibrationLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuietTailFlushDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan QuietTailFlushTimerDelay = QuietTailFlushDelay + TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan SignalRejectLogInterval = TimeSpan.FromSeconds(5);
    private static readonly byte[] DataControlPrefix = [(byte)'P', (byte)'C', (byte)'B', (byte)'C'];
    private static readonly byte[] LossReportMagic = [(byte)'P', (byte)'C', (byte)'L', (byte)'R'];
    private static readonly byte[] RadioStateMagic = [(byte)'P', (byte)'C', (byte)'R', (byte)'D'];
    private static readonly byte[] KeepaliveMagic = [(byte)'P', (byte)'C', (byte)'K', (byte)'A'];
    private static readonly byte[] KeepaliveBytes = BuildKeepaliveMessage();
    private static readonly RTCIceServer[] DefaultIceServers =
    [
        new() { urls = "stun:stun.l.google.com:19302" },
        new() { urls = "stun:stun1.l.google.com:19302" },
        new() { urls = "stun:stun.cloudflare.com:3478" },
        new() { urls = "stun:global.stun.twilio.com:3478" },
    ];
    private static readonly HttpClient TurnHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly MicPreprocessor _micPreprocessor = new();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();
    private readonly object _captureFrameSync = new();
    private readonly object _peerSync = new();
    private readonly Dictionary<string, PeerConnection> _peersBySocket = new();
    private readonly Dictionary<int, string> _clientToSocket = new();
    private readonly Dictionary<string, int> _socketToClient = new();
    private readonly Dictionary<string, Queue<string>> _pendingSignalsBySocket = new();
    private readonly Dictionary<string, DateTime> _lastSignalRejectLogUtc = new();
    private readonly Dictionary<string, DateTime> _recentConnectionFailureUtcBySocket = new();
    private DateTime _lastMassConnectionFailureUtc = DateTime.MinValue;
    private DateTime _deferralEpisodeStartUtc = DateTime.MinValue;
    // Reused by the per-frame Update loop so it doesn't allocate a PeerConnection[] every frame. Only
    // touched on the Unity main thread (Update), so no extra synchronization beyond the _peerSync snapshot.
    private readonly List<PeerConnection> _updatePeerScratch = new();
    private readonly Dictionary<int, PeerConnection> _routeClientScratch = new();
    private readonly Dictionary<int, DateTime> _duplicateRouteLogUtcByClient = new();
    private static readonly TimeSpan DuplicateRouteLogInterval = TimeSpan.FromSeconds(2);
    // volatile: reassigned on a background socket callback, read on the main thread.
    private volatile List<RTCIceServer> _iceServers = DefaultIceServers.ToList();
    private CancellationTokenSource? _turnCts;
    // Set once when the room's recovery loop reports repeated total peer failure (peers=0 with remotePlayers>0):
    // direct/STUN clearly can't connect this client, so escalate to the SAME relay-only path the Wine fix uses.
    // Latched for the session (we never oscillate direct<->relay). volatile: set on the main thread, read by
    // ReadIceSettings on the pool-refill ThreadPool thread.
    private volatile bool _forceRelayEscalated;
    // Pre-built RTCPeerConnections. `new RTCPeerConnection` generates a self-signed DTLS certificate, which
    // costs ~300-500ms in IL2CPP — and it used to run on the Unity main thread (via _mainThreadActions ->
    // MapClient -> EnsurePeer -> WireNewPeerConnection) under _peerSync, freezing rendering AND the audio
    // threads when a peer joined. We now keep a background-filled pool so WireNewPeerConnection rents a ready
    // connection instantly; the cert generation happens on a ThreadPool thread. Thread-safe (ConcurrentQueue).
    // Each entry carries the ICE-config signature it was built with, so a config change (Nat Fix / TURN /
    // signaling ICE servers) invalidates exactly the stale entries at rent time — there is no shared signature
    // stamp that can fall out of sync with the pool's actual contents.
    private readonly ConcurrentQueue<PooledPeerConnection> _pcPool = new();
    // Keep a few warm connections so a lobby filling with several near-simultaneous joins is far less likely
    // to drain the pool and force an inline (main-thread) build. The background refiller tops it up after
    // every rent and on every ICE-config change.
    private const int PcPoolTarget = 4;
    private const int MaxPendingSignalsPerSocket = 32;
    private static readonly TimeSpan PendingSignalSocketMaxAge = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, DateTime> _pendingSignalFirstSeenUtc = new();
    private int _pcPoolRefilling;
    private static readonly TimeSpan JoinRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OfferRetryInterval = TimeSpan.FromSeconds(3);
    // Stuck in 'connecting' past this = failed handshake; re-offer on a fresh connection.
    // Above OfferRetryInterval so a normal negotiation isn't torn down mid-handshake.
    private static readonly TimeSpan StuckConnectingTimeout = TimeSpan.FromSeconds(8);
    // Min spacing between per-peer recovery attempts so a failing peer can't storm re-offers.
    private static readonly TimeSpan PeerRecoveryDebounce = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MassConnectionFailureWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MassConnectionFailureRearmInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeerEscalationDeferralWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeerEscalationDeferralEpisodeMax = TimeSpan.FromSeconds(15);
    private const int PeerRecoveryInFlightMaxAttempts = 2;
    // Inter-frame gap above this = main-thread stall (GC/scene load); skip liveness + rebase so resume can't mass-trip.
    private static readonly TimeSpan ResumeFrameThreshold = TimeSpan.FromSeconds(2);
    // Conservative because the audio channel is unreliable (ordered=false, maxRetransmits=0): bursty loss must not trip silence.
    private static readonly TimeSpan PeerSilenceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilenceLogInterval = TimeSpan.FromSeconds(10);
    // Receive-side watchdog: how long a peer's data channel may stay non-open while its connection is alive
    // (it contributes to openChannels < peers) before we proactively re-request/re-offer for THAT peer
    // regardless of role. Targets the one-directional "hears nobody / hears most" signature directly and
    // self-heals both the answerer-gate gap and any storm residue. Above StuckConnectingTimeout so a normal
    // first handshake is never pre-empted.
    private static readonly TimeSpan ChannelDeficitWatchdogTimeout = TimeSpan.FromSeconds(12);
    private DateTime _lastStatsLogUtc = DateTime.MinValue;
    private DateTime _lastJoinAttemptUtc = DateTime.MinValue;
    private DateTime _lastOfferRetryUtc = DateTime.MinValue;
    private byte _lastPlayerId = byte.MaxValue;
    private byte _joinedPlayerId = byte.MaxValue;
    private int _joinedClientId = -1;
    private bool _joinedIsHost;
    private int _publicLobbyJoinEpoch;
    private string _localSocketId = string.Empty;
    private string _lastPlayerName = string.Empty;
    private volatile VoiceTeamRadioChannel _lastLocalRadioChannel = VoiceTeamRadioChannel.None;
    private int _joinInFlight;
    private int _customTx;
    private int _customRx;
    private int _encodedTx;
    private int _encodedRx;
    private int _micCallbacks;
    private int _micBytes;
    private int _micSamples;
    private int _micMutedDrops;
    private int _micEncodeFailures;
    private int _audioDecodeFailures;
    private int _micEncodedFrames;
    private int _micNoOpenChannelDrops;
    private ushort _sendSequence;
    private long _lastUpdateTicks;
    private uint _sendTimestamp;
    private int _nextProvisionalPeerGroupId = -1000;
    private readonly object _micStatsLock = new();
    private float _micPeakSinceStats;
    private double _micSquareSumSinceStats;
    private int _micSamplesSinceStats;
    private int _micNonZeroSamplesSinceStats;
    private int _micSilentCallbacksSinceStats;
    private int _micNearClipSamplesSinceStats;
    private int _micZeroCrossingsSinceStats;
    private int _opusBytesSinceStats;
    private int _opusFramesSinceStats;
    private int _opusMinBytesSinceStats;
    private int _opusMaxBytesSinceStats;
    private float _txPeakSinceStats;
    private double _txSquareSumSinceStats;
    private int _txSamplesSinceStats;
    private readonly float[] _captureFrameBuffer = new float[AudioHelpers.FrameSize];
    private int _captureFrameSamples;
    // Bumped under _captureFrameSync on every capture stop/restart. A BassRecorder callback that was already
    // dispatched before teardown snapshots the epoch on entry and is dropped once it acquires the lock, so a
    // stale device's frame can never push samples into state the next device is about to reuse.
    private int _captureEpoch;

#if ANDROID || WINDOWS
    private readonly object _unityEncodeSync = new();
    private readonly Queue<(float[] buffer, int samples, int epoch)> _unityEncodeQueue = new();
    private Thread? _unityEncodeWorker;
    private bool _unityEncodeStop;
    private const int UnityEncodeQueueMaxFrames = 16;
#endif

    private SocketIOClient.SocketIO? _socket;
#if WINDOWS
    private readonly object _captureWorkerSync = new();
    private Task _captureWorker = Task.CompletedTask;
    private bool _captureDesiredRunning;
    private string _captureDesiredReason = "init";
    private int _captureTransitionVersion;
    private static readonly TimeSpan SpeakerTopologyPollInterval = TimeSpan.FromSeconds(3);
    private DateTime _speakerTopologyLastPollUtc = DateTime.MinValue;
    private string _speakerTopologySignature = string.Empty;
    private static readonly TimeSpan SpeakerTopologyFastPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpeakerTopologyFastWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SpeakerRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PlaybackStoppedRestartInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BtMutedMicReleaseDelay = TimeSpan.FromSeconds(10);
    private long _speakerTopologyFastUntilTicks;
    private DateTime _lastSpeakerRetryUtc = DateTime.MinValue;
    private DateTime _lastPlaybackStoppedRestartUtc = DateTime.MinValue;
    private DateTime _muteSinceUtc = DateTime.MinValue;
    private bool _btProfileConflict;
    private bool _btMuteReleaseRequested;
    private const int SpeakerRetryFailureLimit = 3;
    private string _speakerRetryExhaustedSignature = string.Empty;
    private string _lastSpeakerDeviceName = string.Empty;
#endif
#if ANDROID || WINDOWS
    private AndroidMicrophone? _androidMicrophone;
    private AndroidSampleProviderSpeaker? _androidSpeaker;
#endif
#if WINDOWS
    private enum CaptureSlot { Sidecar, Bass, Unity }
    private CaptureSlot[] _captureSlots = Array.Empty<CaptureSlot>();
    private CaptureSlot _activeCaptureSlot = CaptureSlot.Bass;
    private CaptureSupervisor? _captureSupervisor;
    private int _supervisorActiveIndex;
    private SidecarVoiceClient? _voice;
    private volatile List<RTCIceServer>? _pendingIceServers;
    private Task _voiceStartTask = Task.CompletedTask;
    private int _voiceColdStartRetries;
    private readonly object _voiceSync = new();
    private Thread? _voicePump;
    private volatile bool _voicePumpRunning;
    private readonly float[] _playbackBlock = new float[SidecarProtocol.AudioOutSamples];
    private bool CaptureUsesUnity => _activeCaptureSlot == CaptureSlot.Unity;
#else
    private bool CaptureUsesUnity => true;
#endif
#if WINDOWS || ANDROID
    private PeerSessionManager? _peerSession;
    private IVoiceTransport? _rpcTransport;
    private readonly RpcSignalingSender _rpcSender = new();
    private bool _rpcOnMessageHooked;
    private DateTime _rpcPollNextUtc = DateTime.MinValue;
    private static readonly TimeSpan RpcPollInterval = TimeSpan.FromSeconds(1);
    private readonly HashSet<int> _rpcKnownClients = new();
    private readonly HashSet<int> _rpcPresentScratch = new();
    private readonly List<int> _rpcLeftScratch = new();
#endif
    private IVoiceEncoder _encoder = CreateEncoder();
    private Timer? _syntheticMicTimer;
    private string _lastMicDeviceName = string.Empty;
    private volatile float _micVolume = 1f;
    private float _noiseGateThreshold;
    private float _vadThreshold = 0.004f;
    private bool _autoMicGain = true;
    private volatile float _localLevel;
    private volatile bool _localSpeaking;
    private bool _microphoneReady;
    private volatile bool _speakerReady;
    private VoiceMixer? _voiceMixer;
    private VoiceCaptureRuntimeOptions _captureOptions;
    private const float DeadInputPeakThreshold = 0.00012f;
    private const float LiveSignalPeakThreshold = 0.005f;
    private const int DeadInputTriggerFrames = 500;
    private bool _captureLiveSignalSeen;
    private int _deadInputFrames;
    private int _deadInputDetected;
    private int _lastOpenedRecordDevice = -1;

    // Never-live + 10s of pure quantization noise = dead feed; a device that produced speech once is trusted (quiet != dead).
    // Diagnostics only — device choice is always the user's; the app never switches capture devices on its own.
    private void TrackCaptureHealthLocked(float peak)
    {
        if (peak >= LiveSignalPeakThreshold)
        {
            _captureLiveSignalSeen = true;
            _deadInputFrames = 0;
            Interlocked.Exchange(ref _deadInputDetected, 0);
            return;
        }
        if (_captureLiveSignalSeen) return;
        if (peak >= DeadInputPeakThreshold)
        {
            _deadInputFrames = 0;
            return;
        }
        if (++_deadInputFrames < DeadInputTriggerFrames) return;
        _deadInputFrames = 0;
        Interlocked.Exchange(ref _deadInputDetected, 1);
        VoiceDiagnostics.Log("bcl.mic.dead-input",
            $"device=\"{_lastMicDeviceName}\" recordDevice={Volatile.Read(ref _lastOpenedRecordDevice)} peak={peak:0.000000}");
    }
    private double _syntheticTonePhase;
    private int _syntheticFrames;
    private DateTime _lastMicCalibrationLogUtc = DateTime.MinValue;
    private string _lastGateReason = "none";
    private float _lastGatePeak;
    private float _lastGateRms;
    private float _lastGateThreshold;
    private float _lastTransmitGain = 1f;
    private float _lastTransmitPeak;
    // volatile: written on the Unity main thread (Dispose) and read on background ThreadPool threads (the pool
    // refill loop) without any other barrier, so teardown is observed promptly and the refiller stops building.
    private volatile bool _disposed;

    public event Action<VoiceBackendCustomMessage>? CustomMessageReceived;

    public PerfectCommsVoiceBackend(string roomCode, string region, string serverUrl)
    {
        RoomCode = roomCode;
        Region = region;
        ServerUrl = VoiceEndpointSettings.NormalizeBetterCrewLinkServerUrl(serverUrl);

#if ANDROID
        _sendPacer = new VoiceSendPacer(SendOpusFrameToAudioTracks);
#else
        _sendPacer = new VoiceSendPacer(SendFramedToChannels);
#endif
        ConnectSocket();
        StartTurnCredentialFetch();
        WarmOpusCodec();
        VoiceDiagnostics.Log("bcl.created", $"room={RoomCode} region={Region} endpoint={ServerUrl}");
        if (WineEnvironment.IsWine)
        {
            ReadIceSettings(out _, out _, out _, out _, out var forceRelay);
            VoiceDiagnostics.Log("env.wine", $"detected=true forceRelay={forceRelay} localIp={WineEnvironment.GetLocalIPv4()?.ToString() ?? "none"}");
        }
    }

    // Prime the Concentus Opus codec off the main thread at startup. The first OpusDecoder construction JITs
    // the codec and initialises its shared (process-wide) static tables — tens of ms that otherwise landed on
    // the Unity main thread inside the first peer-join (new PeerConnection -> new OpusDecoder). Runs once per
    // process, and long before any peer can connect (socket connect + signaling take far longer), so there's
    // no construction race with a real peer decoder.
    private static int _opusWarmed;
    private static void WarmOpusCodec()
    {
        if (Interlocked.Exchange(ref _opusWarmed, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                VoiceCodec.CreateDecoder().Dispose();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("bcl.codec.warm.error", $"error=\"{ex.Message}\"");
            }
        });
    }

    private void StartTurnCredentialFetch()
    {
        var cached = TurnCredentialClient.Cached;
        if (cached is { Count: > 0 })
            ApplyIceServers(new List<RTCIceServer>(cached));
        var cts = new CancellationTokenSource();
        _turnCts = cts;
        _ = Task.Run(() => TurnCredentialLoopAsync(cts.Token));
    }

    private async Task TurnCredentialLoopAsync(CancellationToken ct)
    {
        var url = TurnCredentialsUrl();
        while (!ct.IsCancellationRequested && !_disposed)
        {
            TimeSpan wait;
            try
            {
                if (TurnCredentialClient.NeedsRefresh(DateTime.UtcNow))
                {
                    var servers = await TurnCredentialClient.FetchAsync(TurnHttp, url, ct).ConfigureAwait(false);
                    ApplyIceServers(servers);
                    VoiceDiagnostics.Log("bcl.turn", $"ready=true iceServers={servers.Count}");
                }
                wait = TurnCredentialClient.CredentialTtl - TurnCredentialClient.RefreshMargin;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("bcl.turn", $"ready=false error=\"{ex.Message}\"");
                wait = TimeSpan.FromMinutes(5);
            }
            try { await Task.Delay(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ApplyIceServers(List<RTCIceServer> servers)
    {
        if (servers == null || servers.Count == 0 || _disposed) return;
#if WINDOWS
        _pendingIceServers = servers;
        SidecarVoiceClient? voice;
        lock (_voiceSync) voice = _voice;
        voice?.SetIceServers(servers);
#else
        _iceServers = servers;
        DrainPeerConnectionPool();
        RefillPeerConnectionPool();
#endif
    }

    private static string TurnCredentialsUrl()
    {
        var baseUrl = "https://perfect-comms-lobbies.edgetel.workers.dev";
        try
        {
            var configured = VoiceSettings.Instance?.LobbyRegistryUrl.Value;
            if (!string.IsNullOrWhiteSpace(configured) &&
                Uri.TryCreate(configured!.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                baseUrl = configured.Trim();
        }
        catch { }
        return baseUrl.TrimEnd('/') + "/turn-credentials";
    }

    public string RoomCode { get; }
    public string Region { get; }
    public string ServerUrl { get; }
    internal int PublicLobbyJoinEpoch => Volatile.Read(ref _publicLobbyJoinEpoch);
    public bool UsingMicrophone => _microphoneReady;
    public bool UsingSpeaker => _speakerReady;
    private volatile bool _mute;
    public bool Mute => _mute;
    public float LocalLevel => _localLevel;
    public bool LocalSpeaking => _localSpeaking;
    public int PeerCount
    {
        get { lock (_peerSync) return _peersBySocket.Count; }
    }

    public IEnumerable<VoiceRemoteOverlayState> RemoteOverlayStates
    {
        get
        {
            var list = new List<VoiceRemoteOverlayState>();
            AppendRemoteOverlayStates(list);
            return list;
        }
    }

    // Per-frame overlay path: fill the caller's buffer under the peer lock instead of the old
    // SnapshotPeers().Where().Select() chain, which allocated a PeerConnection[] plus LINQ enumerators
    // every frame. Mirrors the allocation-free CountMappedRemotePeers pattern.
    public void AppendRemoteOverlayStates(List<VoiceRemoteOverlayState> buffer)
    {
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
            {
                if (peer.PlayerId == byte.MaxValue) continue;
                buffer.Add(peer.ToOverlayState());
            }
        }
    }

    // Per-frame on the recovery hot path: kept allocation-free (no snapshot array, no LINQ closure).
    public int CountMappedRemotePeers(VoiceGameStateSnapshot snapshot)
    {
        var count = 0;
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
            {
                if (peer.PlayerId == byte.MaxValue) continue;
                foreach (var player in snapshot.Players)
                {
                    if (!player.IsLocal && !player.Disconnected && !player.IsDummy && player.PlayerId == peer.PlayerId)
                    {
                        count++;
                        break;
                    }
                }
            }
        }
        return count;
    }

    public int CountPeersWithOpenChannel(VoiceGameStateSnapshot snapshot)
    {
        var count = 0;
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
            {
                if (peer.PlayerId == byte.MaxValue) continue;
                if (peer.DataChannel?.readyState != RTCDataChannelState.open) continue;
                foreach (var player in snapshot.Players)
                {
                    if (!player.IsLocal && !player.Disconnected && !player.IsDummy && player.PlayerId == peer.PlayerId)
                    {
                        count++;
                        break;
                    }
                }
            }
        }
        return count;
    }

    // Peers with a physically-open data channel, ignoring clientId->player mapping. Lets the room tell a
    // healthy-but-not-yet-remapped mesh (round boundary; audio still flowing) apart from a real collapse.
    public int CountOpenDataChannels()
    {
        var count = 0;
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
                if (peer.DataChannel?.readyState == RTCDataChannelState.open) count++;
        }
        return count;
    }

    // Targeted, non-destructive recovery (P0.2). For each expected remote player that has a mapped peer
    // whose link is unhealthy (data channel not open while the connection is alive/dead), re-drive ONLY that
    // peer through the existing per-peer recovery (role-aware: initiator recreates+re-offers, answerer
    // re-requests an offer) on the same per-peer backoff, leaving every already-open peer's channel intact.
    // This replaces the global Rejoin()/ClearPeers() teardown for the common "most peers mapped, a few are
    // wedged" shortfall. A player with NO mapped peer at all (the genuinely-unmappable seed) cannot be
    // re-offered here — there is no socket to target — so it is reported but not acted on. Returns the number
    // of peers this call drove a recovery on; 0 means nothing was actionable (e.g. only unmapped seeds remain).
    public int TryRecoverMissingClients(VoiceGameStateSnapshot snapshot)
    {
        if (_disposed || snapshot == null) return 0;
#if ANDROID
        if (_peerSession != null) return 0;
#endif
        // Snapshot the unhealthy mapped peers under the lock, then act outside it (StartOfferAsync /
        // RequestOfferFromPeer / RecreatePeerConnection each take _peerSync themselves).
        var targets = new List<(string SocketId, bool Initiator, bool Stuck)>();
        var now = DateTime.UtcNow;
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
            {
                if (peer.PlayerId == byte.MaxValue) continue; // not yet mapped to a live player
                // Healthy = data channel open. Leave it strictly alone.
                if (peer.DataChannel?.readyState == RTCDataChannelState.open) continue;
                if (!ExpectsPlayer(snapshot, peer.PlayerId)) continue;
                if (now < peer.NextRetryUtc) continue; // honor per-peer backoff so this can't storm
                bool initiator = ShouldInitiateOffer(peer.SocketId);
                bool stuck = IsStuckConnecting(peer, now);
                targets.Add((peer.SocketId, initiator, stuck));
            }
        }
        if (targets.Count == 0) return 0;
        foreach (var (socketId, initiator, stuck) in targets)
        {
            VoiceDiagnostics.Log("bcl.peer.targeted-recovery", $"socket={socketId} initiator={initiator} stuck={stuck}");
            if (initiator)
            {
                // A wedged 'connecting' channel can't be re-offered on the same connection; rebuild first.
                if (stuck) RecreatePeerConnection(socketId);
                _ = StartOfferAsync(socketId);
            }
            else
            {
                RequestOfferFromPeer(socketId);
            }
            lock (_peerSync)
            {
                if (_peersBySocket.TryGetValue(socketId, out var peer))
                {
                    peer.RecoveryAttempts++;
                    peer.NextRetryUtc = now + RecoveryBackoff(peer.RecoveryAttempts);
                }
            }
        }
        return targets.Count;
    }

    private static bool ExpectsPlayer(VoiceGameStateSnapshot snapshot, byte playerId)
    {
        var players = snapshot.Players;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (!player.IsLocal && !player.Disconnected && !player.IsDummy && player.PlayerId == playerId)
                return true;
        }
        return false;
    }

    public void SetMute(bool mute)
    {
        if (_mute == mute) return;
        _mute = mute;
        if (mute)
        {
            // Clear latched local-speaking state. A push-to-talk release mutes mid-utterance but keeps the
            // Windows mic running, so the capture loop that decays _localLevel never runs to clear it, which
            // otherwise leaves the local speaking indicator stuck on until the user resets.
            _localLevel = 0f;
            _localSpeaking = false;
        }
#if WINDOWS
        _muteSinceUtc = mute ? DateTime.UtcNow : DateTime.MinValue;
        _btMuteReleaseRequested = false;
        if (mute)
            _voice?.SetMicActive(false);
        else if (_microphoneReady && !_captureOptions.SyntheticMicToneEnabled)
            _voice?.SetMicActive(true);
        if (!mute && !_microphoneReady)
            QueueMicrophoneTransition(true, "unmuted");
#elif ANDROID
        if (mute) StopAndroidMicrophone("muted");
        else StartAndroidMicrophone("unmuted");
#endif
        VoiceDiagnostics.Log("bcl.mute", $"mute={Mute} micReady={_microphoneReady} callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)}");
    }
    public void ToggleMute() => SetMute(!Mute);
    public void SetLoopBack(bool loopBack) { }
    private float _masterVolume = 1f;
    public void SetMasterVolume(float volume)
    {
        _masterVolume = Math.Clamp(volume, 0f, 2f);
        _voiceMixer?.SetMasterVolume(_masterVolume);
    }
    public void SetMicVolume(float volume)
    {
        _micVolume = Mathf.Clamp(volume, 0f, 2f);
#if ANDROID || WINDOWS
        _androidMicrophone?.SetVolume(_micVolume);
#endif
    }

    public void SetNoiseGate(float noiseGateThreshold, float vadThreshold)
    {
        _noiseGateThreshold = Mathf.Clamp(noiseGateThreshold, 0.0005f, 0.10f);
        _vadThreshold = Mathf.Clamp(vadThreshold, 0.0005f, 0.080f);
    }

    public void SetCaptureRuntimeOptions(VoiceCaptureRuntimeOptions options)
    {
        var restartCapture = _captureOptions.SyntheticMicToneEnabled != options.SyntheticMicToneEnabled;
        _captureOptions = options;
        _autoMicGain = ReadAutoMicGainSetting();

#if WINDOWS
        _voice?.SetDsp(options.EchoCancellationEnabled, _autoMicGain, options.NoiseSuppressionEnabled, true);
        EnsureCaptureSupervisor();
        if (restartCapture && !Mute && _microphoneReady)
            QueueMicrophoneTransition(true, "capture-options");
#elif ANDROID
        if (restartCapture && !Mute && _microphoneReady)
            StartAndroidMicrophone("capture-options");
#endif
        VoiceDiagnostics.Log("bcl.capture-options",
            $"capture={DescribeCaptureMode()} syntheticTone={options.SyntheticMicToneEnabled} noiseSuppression={options.NoiseSuppressionEnabled} autoMicGain={_autoMicGain} calibration={options.MicCalibrationDiagnostics} sensitivity={options.MicSensitivity:0.00}");
    }

    private static bool ReadAutoMicGainSetting()
    {
        try
        {
            return VoiceSettings.Instance?.AutoMicGain.Value ?? true;
        }
        catch
        {
            return true;
        }
    }

    public void RebuildCaptureSupervisor()
    {
#if WINDOWS
        BuildCaptureSupervisor();
#endif
    }

    public void SetMicrophone(string deviceName, float volume)
    {
        var normalizedName = deviceName ?? string.Empty;
        var deviceChanged = !string.Equals(_lastMicDeviceName, normalizedName, StringComparison.Ordinal);
        _lastMicDeviceName = normalizedName;
        _micVolume = Mathf.Clamp(volume, 0f, 2f);
#if WINDOWS
        if (deviceChanged)
            _btProfileConflict = false;
        if (Mute)
        {
            QueueMicrophoneTransition(false, "set-muted");
            VoiceDiagnostics.Log("bcl.mic", $"ready=false muted=true device=\"{_lastMicDeviceName}\" callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)}");
            return;
        }

        QueueMicrophoneTransition(true, "settings");
#elif ANDROID
        if (Mute)
        {
            StopAndroidMicrophone("set-muted");
            VoiceDiagnostics.Log("bcl.mic", $"ready=false muted=true device=\"{_lastMicDeviceName}\" callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)}");
            return;
        }

        StartAndroidMicrophone("settings");
#else
        _microphoneReady = false;
#endif
    }

#if WINDOWS
    private void QueueMicrophoneTransition(bool shouldRun, string reason)
    {
        if (_activeCaptureSlot == CaptureSlot.Sidecar)
        {
            StopBassCapture("switch-to-sidecar");
            if (_androidMicrophone != null)
                _mainThreadActions.Enqueue(() => StopAndroidMicrophone("switch-to-sidecar"));
            _mainThreadActions.Enqueue(() =>
            {
                if (shouldRun && !_disposed) StartSidecarMicrophone(reason);
                else StopSidecarMicrophone(reason);
            });
            return;
        }
        if (_activeCaptureSlot == CaptureSlot.Unity)
        {
            StopBassCapture("switch-to-unity");
            if (_voice != null)
                StopSidecarMicrophone("switch-to-unity");
            _mainThreadActions.Enqueue(() =>
            {
                if (shouldRun && !_disposed) StartAndroidMicrophone(reason);
                else StopAndroidMicrophone(reason);
            });
            return;
        }
        if (_voice != null)
            StopSidecarMicrophone("switch-to-bass");
        if (_androidMicrophone != null)
            _mainThreadActions.Enqueue(() => StopAndroidMicrophone("switch-to-bass"));
        lock (_captureWorkerSync)
        {
            _captureDesiredRunning = shouldRun && !_disposed;
            _captureDesiredReason = reason;
            _captureTransitionVersion++;

            if (!_captureWorker.IsCompleted) return;
            _captureWorker = Task.Run(ProcessMicrophoneTransitions);
        }
    }

    private static CaptureSlot[] BuildCaptureSlots()
    {
        return new[] { CaptureSlot.Sidecar };
    }

    private void EnsureCaptureSupervisor()
    {
        if (_captureSupervisor == null)
            BuildCaptureSupervisor();
    }

    private void BuildCaptureSupervisor()
    {
        _captureSlots = BuildCaptureSlots();
        _supervisorActiveIndex = 0;
        _activeCaptureSlot = _captureSlots[0];
        var restartBudget = Array.IndexOf(_captureSlots, CaptureSlot.Sidecar) >= 0 ? 2 : 0;
        _captureSupervisor = new CaptureSupervisor(
            sourceCount: _captureSlots.Length,
            restartInPlace: ApplyCaptureSwitch,
            switchTo: ApplyCaptureSwitch,
            onAllFailed: reason => VoiceDiagnostics.Log("bcl.mic.capture-all-failed",
                $"reason={reason} slot={_activeCaptureSlot} device=\"{_lastMicDeviceName}\" wine={WineEnvironment.IsWine} hint=check-os-mic-permission"),
            restartBudget: restartBudget);
    }

    private void ApplyCaptureSwitch(int index, string reason)
    {
        var from = _captureSlots[_supervisorActiveIndex];
        var to = _captureSlots[index];
        _supervisorActiveIndex = index;
        _activeCaptureSlot = to;
        VoiceDiagnostics.Log("bcl.mic.capture-switch",
            $"reason={reason} from={from} to={to} index={index} device=\"{_lastMicDeviceName}\" wine={WineEnvironment.IsWine}");
        QueueMicrophoneTransition(true, "capture-switch");
    }

    private void OnSidecarHeartbeatLost(SidecarVoiceClient voice, string reason)
    {
        _mainThreadActions.Enqueue(() =>
        {
            if (_disposed || !ReferenceEquals(_voice, voice))
                return;
            if (CaptureTransitionInFlight())
                return;
            VoiceDiagnostics.Log("bcl.voice", $"reason=heartbeat-lost detail={reason} restart=true");
            StopVoiceSession("heartbeat-lost");
            EnsureVoiceSession("heartbeat-lost");
        });
    }

    private void EnsureVoiceSession(string reason)
    {
        lock (_voiceSync)
        {
            if (_voice != null) return;
            var voice = new SidecarVoiceClient(LaunchSidecarHelper);
            voice.OnFrame += ProcessBassMicFrame;
            voice.OnDead += r => OnSidecarHeartbeatLost(voice, r);
            voice.OnLocalSdp += OnHelperLocalSdp;
            voice.OnLocalCandidate += OnHelperLocalCandidate;
            voice.OnPeerState += OnHelperPeerState;
            voice.OnLevel += OnSidecarLevel;
            _voice = voice;
            var mic = _lastMicDeviceName;
            var spk = _lastSpeakerDeviceName;
            _voiceStartTask = Task.Run(() => RunVoiceStart(voice, mic, spk, reason));
        }
    }

    private void OnHelperLocalSdp(string peerId, string sdpType, string sdp)
    {
        if (SidecarVoiceTransport.TryParseClientId(peerId, out var clientId))
            _mainThreadActions.Enqueue(() => _peerSession?.OnLocalSdp(clientId, sdpType, sdp));
    }

    private void OnHelperLocalCandidate(string peerId, string candidate)
    {
        if (SidecarVoiceTransport.TryParseClientId(peerId, out var clientId))
            _mainThreadActions.Enqueue(() => _peerSession?.OnLocalCandidate(clientId, candidate));
    }

    private void OnHelperPeerState(string peerId, string state)
    {
        if (!SidecarVoiceTransport.TryParseClientId(peerId, out var clientId)) return;
        var nowMs = Environment.TickCount64;
        _mainThreadActions.Enqueue(() =>
        {
            if (_peerSession == null) return;
            if (state == "connected")
                _peerSession.OnPeerConnected(clientId);
            else if (state is "failed" or "disconnected" or "closed")
                _peerSession.OnPeerConnectionLost(clientId, nowMs);
        });
    }

    private void OnSidecarLevel(float peak, bool speaking)
    {
        if (Mute) { _localLevel = 0f; _localSpeaking = false; return; }
        _localLevel = peak;
        _localSpeaking = speaking;
    }

    private void RunVoiceStart(SidecarVoiceClient voice, string mic, string spk, string reason)
    {
        bool started;
        try
        {
            started = voice.Start(mic, spk);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.voice", $"ready=false reason={reason} error=\"{ex.Message}\"");
            started = false;
        }

        if (!started || _disposed || !ReferenceEquals(_voice, voice))
        {
            try { voice.OnFrame -= ProcessBassMicFrame; } catch { }
            try { voice.Dispose(); } catch { }
            lock (_voiceSync)
                if (ReferenceEquals(_voice, voice)) _voice = null;
            _speakerReady = false;
            if (!started)
            {
                VoiceDiagnostics.Log("bcl.voice", $"ready=false reason={reason} error=\"sidecar voice start failed\"");
                if (!_disposed && _voiceColdStartRetries++ < 2)
                    Task.Run(async () => { try { await Task.Delay(700); if (!_disposed) EnsureVoiceSession("voice-cold-retry"); } catch { } });
            }
            return;
        }

        _voiceColdStartRetries = 0;
        voice.SetDsp(_captureOptions.EchoCancellationEnabled, _autoMicGain, _captureOptions.NoiseSuppressionEnabled, true);
        voice.SetMicActive(!Mute && _microphoneReady && !_captureOptions.SyntheticMicToneEnabled);
        var pendingIce = _pendingIceServers;
        if (pendingIce != null) voice.SetIceServers(pendingIce);
        _speakerReady = true;
        if (voice.OutputDevices.Count > 0)
            VoiceChatLocalSettings.SetSpkDeviceNamesFromSidecar(voice.OutputDevices);
        StartVoicePump();
        Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
        VoiceDiagnostics.Log("bcl.voice", $"ready=true reason={reason} mic=\"{mic}\" spk=\"{spk}\" outputs={voice.OutputDevices.Count}");
    }

    private void StopVoiceSession(string reason)
    {
        StopVoicePump();
        SidecarVoiceClient? voice;
        Task startTask;
        lock (_voiceSync)
        {
            voice = _voice;
            _voice = null;
            startTask = _voiceStartTask;
        }
        if (voice != null)
            try { voice.OnFrame -= ProcessBassMicFrame; } catch { }
        Task.Run(() =>
        {
            try { voice?.Dispose(); } catch { }
            try { startTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        });
        _speakerReady = false;
        VoiceDiagnostics.Log("bcl.voice", $"stopped reason={reason}");
    }

    private void StartVoicePump()
    {
        lock (_voiceSync)
        {
            if (_voicePumpRunning) return;
            _voicePumpRunning = true;
            _voicePump = new Thread(VoicePumpLoop) { IsBackground = true, Name = "SidecarVoicePump" };
            _voicePump.Start();
        }
    }

    private void StopVoicePump()
    {
        Thread? pump;
        lock (_voiceSync)
        {
            _voicePumpRunning = false;
            pump = _voicePump;
            _voicePump = null;
        }
        if (pump != null && pump != Thread.CurrentThread)
            try { pump.Join(1000); } catch { }
    }

    private void VoicePumpLoop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long nextMs = 0;
        const int frameMs = 20;
        while (_voicePumpRunning)
        {
            var voice = _voice;
            if (voice == null) break;
            try
            {
                var mixer = _voiceMixer;
                if (mixer != null) mixer.Read(_playbackBlock);
                else Array.Clear(_playbackBlock, 0, _playbackBlock.Length);
            }
            catch { Array.Clear(_playbackBlock, 0, _playbackBlock.Length); }
            voice.SendPlayback(_playbackBlock);

            nextMs += frameMs;
            var sleep = nextMs - sw.ElapsedMilliseconds;
            if (sleep > 1) Thread.Sleep((int)sleep);
            else if (sleep < -200) nextMs = sw.ElapsedMilliseconds;
        }
    }

    private void FeedCaptureSupervisor(int micWindowSamples)
    {
        EnsureCaptureSupervisor();
        var supervisor = _captureSupervisor!;

        if (Mute || !_microphoneReady || _captureOptions.SyntheticMicToneEnabled || CaptureTransitionInFlight())
            return;

        supervisor.OnStatsWindow(micWindowSamples, unmutedAndCapturing: true);
    }

    private bool CaptureTransitionInFlight()
    {
        if (!_mainThreadActions.IsEmpty) return true;
        lock (_captureWorkerSync) return !_captureWorker.IsCompleted;
    }

    private void StopBassCapture(string reason)
    {
        lock (_captureWorkerSync)
        {
            if (!_captureDesiredRunning && _captureWorker.IsCompleted) return;
            _captureDesiredRunning = false;
            _captureDesiredReason = reason;
            _captureTransitionVersion++;
            if (_captureWorker.IsCompleted)
                _captureWorker = Task.Run(ProcessMicrophoneTransitions);
        }
    }

    private void ProcessMicrophoneTransitions()
    {
        while (true)
        {
            bool shouldRun;
            string reason;
            int version;
            lock (_captureWorkerSync)
            {
                shouldRun = _captureDesiredRunning && !_disposed;
                reason = _captureDesiredReason;
                version = _captureTransitionVersion;
            }

            try
            {
                if (shouldRun) StartMicrophone(reason);
                else StopMicrophone(reason);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("bcl.mic.worker", $"reason={reason} start={shouldRun} err=\"{ex.Message}\"");
            }

            lock (_captureWorkerSync)
            {
                if (version != _captureTransitionVersion) continue;
                _captureWorker = Task.CompletedTask;
                return;
            }
        }
    }

    private void StopMicrophoneWorkerForDispose()
    {
        if (_voice != null)
        {
            StopVoiceSession("dispose");
            return;
        }
        if (_activeCaptureSlot == CaptureSlot.Unity || _androidMicrophone != null)
        {
            StopAndroidMicrophone("dispose");
            return;
        }
        Task worker;
        lock (_captureWorkerSync)
        {
            _captureDesiredRunning = false;
            _captureDesiredReason = "dispose";
            _captureTransitionVersion++;

            if (_captureWorker.IsCompleted)
                _captureWorker = Task.Run(ProcessMicrophoneTransitions);
            worker = _captureWorker;
        }

        var stopped = false;
        try { stopped = worker.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { VoiceDiagnostics.Log("bcl.mic.worker", $"reason=dispose err=\"{ex.Message}\""); }
        if (stopped) StopMicrophone("dispose");
        else VoiceDiagnostics.Log("bcl.mic.worker", "reason=dispose err=\"timed out waiting for capture worker\"");
    }

    private void StartMicrophone(string reason)
    {
        try
        {
            StopMicrophone($"restart:{reason}");
            _latchedMicChannel = 0;
            _micChannelSwitchStreak = 0;
            var captureKind = "none";
            var captureDevice = "default";

            if (_captureOptions.SyntheticMicToneEnabled)
            {
                captureKind = "synthetic";
                captureDevice = "generated-48khz-tone";
                StartSyntheticMicTone(reason);
            }

            _microphoneReady = true;
            Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
            VoiceDiagnostics.Log("bcl.mic", $"ready=true reason={reason} capture={captureKind} device=\"{_lastMicDeviceName}\" captureDevice=\"{captureDevice}\" captureFormat=\"48000Hz/float/mono\" syntheticTone={_captureOptions.SyntheticMicToneEnabled} volume={_micVolume:0.00}");
        }
        catch (Exception ex)
        {
            StopMicrophone($"failed:{reason}");
            VoiceDiagnostics.Log("bcl.mic", $"ready=false reason={reason} device=\"{_lastMicDeviceName}\" error=\"{ex.Message}\"");
        }
    }

    private void StopMicrophone(string reason)
    {
        StopSyntheticMicTone();
        var hadMic = _microphoneReady;
        _microphoneReady = false;
        if (hadMic)
            Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
        // Reset the native RNNoise preprocessor INSIDE the capture lock: a BassRecorder callback can still be
        // mid-flight in ProcessMicrophoneFrameLocked -> TryApplyNoiseSuppression (native ProcessFrame on the
        // same state pointer Reset destroys). Holding _captureFrameSync makes the two mutually exclusive and
        // closes the use-after-free that crashed the game on fast device/mic switches.
        lock (_captureFrameSync)
        {
            _captureEpoch++;
            _captureFrameSamples = 0;
            _captureLiveSignalSeen = false;
            _deadInputFrames = 0;
            Interlocked.Exchange(ref _deadInputDetected, 0);
            _micPreprocessor.Reset(preserveAutoGain: true);
        }
        _localLevel = 0f;
        _localSpeaking = false;
        if (hadMic)
            VoiceDiagnostics.Log("bcl.mic", $"ready=false reason={reason} device=\"{_lastMicDeviceName}\" callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)} samples={Volatile.Read(ref _micSamples)}");
    }

    private void StartSidecarMicrophone(string reason)
    {
        try
        {
            StopSyntheticMicTone();
            if (_captureOptions.SyntheticMicToneEnabled)
            {
                StartSyntheticMicTone(reason);
                _microphoneReady = true;
                EnsureVoiceSession(reason);
                _voice?.SetMicActive(false);
                VoiceDiagnostics.Log("bcl.mic", $"ready=true reason={reason} capture=synthetic device=\"{_lastMicDeviceName}\" syntheticTone=true volume={_micVolume:0.00}");
                return;
            }

            _microphoneReady = true;
            EnsureVoiceSession(reason);
            var voice = _voice;
            if (voice != null)
            {
                voice.SelectMicDevice(_lastMicDeviceName);
                voice.SetMicActive(true);
            }
            Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
            VoiceDiagnostics.Log("bcl.mic", $"ready=true reason={reason} capture=sidecar device=\"{_lastMicDeviceName}\" captureFormat=\"48000Hz/float/mono\" volume={_micVolume:0.00}");
        }
        catch (Exception ex)
        {
            StopSidecarMicrophone($"failed:{reason}");
            VoiceDiagnostics.Log("bcl.mic", $"ready=false reason={reason} device=\"{_lastMicDeviceName}\" error=\"{ex.Message}\"");
        }
    }

    private void StopSidecarMicrophone(string reason)
    {
        StopSyntheticMicTone();
        _voice?.SetMicActive(false);
        var hadMic = _microphoneReady;
        _microphoneReady = false;
        lock (_captureFrameSync)
        {
            _captureEpoch++;
            _captureFrameSamples = 0;
            _captureLiveSignalSeen = false;
            _deadInputFrames = 0;
            Interlocked.Exchange(ref _deadInputDetected, 0);
            _micPreprocessor.Reset(preserveAutoGain: true);
        }
        _localLevel = 0f;
        _localSpeaking = false;
        if (hadMic)
            VoiceDiagnostics.Log("bcl.mic", $"ready=false reason={reason} device=\"{_lastMicDeviceName}\" callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)} samples={Volatile.Read(ref _micSamples)}");
    }

    private static SidecarLaunchResult LaunchSidecarHelper(string token, string deviceId)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var helperPath = SidecarLauncher.EnsureHelperExtracted(assembly, AppContext.BaseDirectory, force: false);
        return SidecarLauncher.Launch(helperPath, token, handshakeTimeoutMs: 4000, wine: WineEnvironment.IsWine, resolveWineHostPath: WineEnvironment.ResolveHostPath);
    }
#endif

#if WINDOWS || ANDROID
    private void OnRpcSignal(int senderClientId, SignalMsgType type, byte[] payload)
        => _peerSession?.OnSignal(senderClientId, type, payload, Environment.TickCount64);

    private void PumpRpcSignaling(VoiceGameStateSnapshot? snapshot)
    {
        var now = DateTime.UtcNow;
        if (now < _rpcPollNextUtc) return;
        _rpcPollNextUtc = now + RpcPollInterval;
        var nowMs = Environment.TickCount64;

        var client = AmongUsClient.Instance;
        if (client == null || snapshot == null)
        {
            if (_peerSession != null && _rpcKnownClients.Count > 0)
            {
                _peerSession.Reset();
                _rpcKnownClients.Clear();
            }
            return;
        }

        var localId = client.ClientId;
        if (localId < 0) return;

        if (_peerSession == null)
        {
#if WINDOWS
            _rpcTransport = new SidecarVoiceTransport(() => _voice);
#elif ANDROID
            _rpcTransport = new SipsorceryVoiceTransport(this);
#endif
            _peerSession = new PeerSessionManager(localId, _rpcTransport!, _rpcSender);
            if (!_rpcOnMessageHooked)
            {
                AmongUsRpcSignaling.OnMessage += OnRpcSignal;
                _rpcOnMessageHooked = true;
            }
        }

        _rpcPresentScratch.Clear();
        foreach (var remote in client.allClients)
        {
            var id = remote.Id;
            if (id < 0 || id == localId) continue;
            _rpcPresentScratch.Add(id);
            if (_rpcKnownClients.Add(id))
                _peerSession.OnPlayerJoined(id, nowMs);
        }

        if (_rpcKnownClients.Count != _rpcPresentScratch.Count)
        {
            _rpcLeftScratch.Clear();
            foreach (var id in _rpcKnownClients)
                if (!_rpcPresentScratch.Contains(id))
                    _rpcLeftScratch.Add(id);
            foreach (var id in _rpcLeftScratch)
            {
                _peerSession.OnPlayerLeft(id);
                _rpcKnownClients.Remove(id);
            }
        }

        _peerSession.Tick(nowMs);
    }
#endif

    private void StartSyntheticMicTone(string reason)
    {
        StopSyntheticMicTone();
        _syntheticTonePhase = 0.0;
        _syntheticMicTimer = new Timer(_ => OnSyntheticMicTick(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        VoiceDiagnostics.Log("bcl.synthetic", $"state=started reason={reason} sampleRate={AudioHelpers.ClockRate} frameSize={AudioHelpers.FrameSize} toneHz={SyntheticToneFrequency:0} amplitude={SyntheticToneAmplitude:0.000}");
    }

    private void StopSyntheticMicTone()
    {
        var timer = _syntheticMicTimer;
        _syntheticMicTimer = null;
        try { timer?.Dispose(); } catch { }
    }

    private void OnSyntheticMicTick()
    {
        if (_disposed || Mute || !_captureOptions.SyntheticMicToneEnabled) return;
        try
        {
            var frame = new float[AudioHelpers.FrameSize];
            const double frequency = SyntheticToneFrequency;
            const float amplitude = SyntheticToneAmplitude;
            var phaseStep = frequency / AudioHelpers.ClockRate;
            var phase = _syntheticTonePhase;
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (float)Math.Sin(phase * Math.PI * 2.0) * amplitude;
                phase += phaseStep;
                if (phase >= 1.0) phase -= 1.0;
            }
            _syntheticTonePhase = phase;
            Interlocked.Increment(ref _syntheticFrames);
            ProcessMicrophoneFrame(frame, frame.Length, "synthetic");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _micEncodeFailures);
            VoiceDiagnostics.Log("bcl.mic.capture_error", $"source=synthetic error=\"{ex.Message}\"");
        }
    }

#if WINDOWS

    private static string NormalizeAudioDeviceName(string? deviceName)
        => string.Join(" ", (deviceName ?? string.Empty)
            .Trim()
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static bool DeviceNamesMatch(string requested, string actual)
        => !string.IsNullOrWhiteSpace(actual) &&
           (string.Equals(actual, requested, StringComparison.Ordinal) ||
            actual.StartsWith(requested, StringComparison.Ordinal) ||
            requested.StartsWith(actual, StringComparison.Ordinal));

#endif

#if ANDROID || WINDOWS
    private void StartAndroidMicrophone(string reason)
    {
        try
        {
            StopAndroidMicrophone($"restart:{reason}");
            if (_captureOptions.SyntheticMicToneEnabled)
            {
                StartSyntheticMicTone(reason);
            }
            else
            {
                _androidMicrophone = new AndroidMicrophone();
                _androidMicrophone.ReuseBuffer = true;
                _androidMicrophone.DataAvailable += OnAndroidMicrophoneData;
                _androidMicrophone.SetVolume(_micVolume);
                _androidMicrophone.Start(_lastMicDeviceName);
                VoiceDiagnostics.Log("bcl.unity.mic", $"requested=\"{_lastMicDeviceName}\" unityDevices=\"{string.Join("|", AndroidMicrophone.GetDeviceNames())}\"");
            }

            _microphoneReady = true;
            VoiceDiagnostics.Log("bcl.mic", $"ready=true reason={reason} capture={DescribeCaptureMode()} device=\"{_lastMicDeviceName}\" syntheticTone={_captureOptions.SyntheticMicToneEnabled} volume={_micVolume:0.00}");
        }
        catch (Exception ex)
        {
            StopAndroidMicrophone($"failed:{reason}");
            VoiceDiagnostics.Log("bcl.mic", $"ready=false reason={reason} device=\"{_lastMicDeviceName}\" error=\"{ex.Message}\"");
        }
    }

    private void StopAndroidMicrophone(string reason)
    {
        StopSyntheticMicTone();
        var microphone = _androidMicrophone;
        var hadMic = microphone != null || _microphoneReady;
        _androidMicrophone = null;
        if (microphone != null)
        {
            try { microphone.DataAvailable -= OnAndroidMicrophoneData; } catch { }
            try { microphone.Dispose(); } catch { }
        }

        _microphoneReady = false;
        StopUnityEncodeWorker();
        lock (_captureFrameSync)
        {
            _captureEpoch++;
            _captureFrameSamples = 0;
            _micPreprocessor.Reset(preserveAutoGain: true);
        }
        _localLevel = 0f;
        _localSpeaking = false;
        if (hadMic)
            VoiceDiagnostics.Log("bcl.mic", $"ready=false reason={reason} device=\"{_lastMicDeviceName}\" callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)} samples={Volatile.Read(ref _micSamples)}");
    }

    private void OnAndroidMicrophoneData(float[] buffer, int length)
    {
        if (_disposed || buffer.Length == 0) return;
        Interlocked.Increment(ref _micCallbacks);
        int samples = Math.Min(Math.Max(length, 0), buffer.Length);
        Interlocked.Add(ref _micBytes, samples * sizeof(float));
        if (Mute)
        {
            Interlocked.Increment(ref _micMutedDrops);
            return;
        }
        if (samples <= 0) return;
        Interlocked.Add(ref _micSamples, samples);

        var epoch = Volatile.Read(ref _captureEpoch);
        var copy = new float[samples];
        Array.Copy(buffer, 0, copy, 0, samples);
        lock (_unityEncodeSync)
        {
            if (_disposed) return;
            while (_unityEncodeQueue.Count >= UnityEncodeQueueMaxFrames)
                _unityEncodeQueue.Dequeue();
            _unityEncodeQueue.Enqueue((copy, samples, epoch));
            if (_unityEncodeWorker == null)
            {
                _unityEncodeStop = false;
                _unityEncodeWorker = new Thread(UnityEncodeWorkerLoop) { IsBackground = true, Name = "PerfectComms.UnityEncode" };
                _unityEncodeWorker.Start();
            }
            Monitor.Pulse(_unityEncodeSync);
        }
    }

    private void UnityEncodeWorkerLoop()
    {
        while (true)
        {
            float[] buffer;
            int samples;
            int epoch;
            lock (_unityEncodeSync)
            {
                while (_unityEncodeQueue.Count == 0 && !_unityEncodeStop && !_disposed)
                    Monitor.Wait(_unityEncodeSync);
                if (_unityEncodeStop || _disposed)
                    return;
                (buffer, samples, epoch) = _unityEncodeQueue.Dequeue();
            }

            if (epoch != Volatile.Read(ref _captureEpoch)) continue;
            lock (_captureFrameSync)
            {
                if (epoch != _captureEpoch) continue;
                try
                {
                    ProcessMicrophoneCaptureSamples(buffer, samples);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _micEncodeFailures);
                    VoiceDiagnostics.Log("bcl.mic.capture_error", $"source=android error=\"{ex.Message}\"");
                }
            }
        }
    }

    private void StopUnityEncodeWorker()
    {
        Thread? worker;
        lock (_unityEncodeSync)
        {
            worker = _unityEncodeWorker;
            _unityEncodeWorker = null;
            _unityEncodeStop = true;
            _unityEncodeQueue.Clear();
            Monitor.PulseAll(_unityEncodeSync);
        }
        try { worker?.Join(500); } catch { }
    }
#endif


    public void SetSpeaker(string deviceName)
    {
#if WINDOWS
        try
        {
            if (!string.Equals(_lastSpeakerDeviceName, deviceName ?? string.Empty, StringComparison.Ordinal))
                _btProfileConflict = false;
            _lastSpeakerDeviceName = deviceName ?? string.Empty;
            SetSpeakerSidecar(deviceName ?? string.Empty);
        }
        catch (Exception ex)
        {
            _speakerReady = false;
            VoiceDiagnostics.Log("bcl.speaker", $"ready=false device=\"{deviceName}\" error=\"{ex.Message}\"");
        }
#else
#if ANDROID
        try
        {
            _androidSpeaker?.Dispose();
            var mixer = _voiceMixer ?? new VoiceMixer();
            _voiceMixer = mixer;
            _androidSpeaker = new AndroidSampleProviderSpeaker(mixer);
            _speakerReady = _androidSpeaker.IsPlaying;
            lock (_peerSync)
                foreach (var peer in _peersBySocket.Values)
                    peer.SetMixer(mixer);
            VoiceDiagnostics.Log("bcl.speaker", $"ready={_speakerReady} device=\"{deviceName}\" backend=android-managed");
        }
        catch (Exception ex)
        {
            try { _androidSpeaker?.Dispose(); } catch { }
            _androidSpeaker = null;
            _speakerReady = false;
            VoiceDiagnostics.Log("bcl.speaker", $"ready=false device=\"{deviceName}\" error=\"{ex.Message}\"");
        }
#else
        _speakerReady = false;
#endif
#endif
    }

#if WINDOWS
    private void SetSpeakerSidecar(string deviceName)
    {
        try { _androidSpeaker?.Dispose(); } catch { }
        _androidSpeaker = null;
        var mixer = _voiceMixer ?? new VoiceMixer();
        _voiceMixer = mixer;
        mixer.SetMasterVolume(_masterVolume);
        lock (_peerSync)
            foreach (var peer in _peersBySocket.Values)
                peer.SetMixer(mixer);
        var voice = _voice;
        if (voice != null)
        {
            voice.SelectOutputDevice(deviceName);
            StartVoicePump();
            _speakerReady = true;
            VoiceDiagnostics.Log("bcl.speaker", $"ready=true device=\"{deviceName}\" backend=sidecar reused=true");
        }
        else
        {
            EnsureVoiceSession("speaker");
            VoiceDiagnostics.Log("bcl.speaker", $"ready=pending device=\"{deviceName}\" backend=sidecar");
        }
    }
#endif

#if WINDOWS
    private void MaybeReleaseBluetoothMutedMicrophone()
    {
        if (!Mute || !_microphoneReady || !_btProfileConflict || _btMuteReleaseRequested) return;
        if (_muteSinceUtc == DateTime.MinValue || DateTime.UtcNow - _muteSinceUtc < BtMutedMicReleaseDelay) return;
        _btMuteReleaseRequested = true;
        VoiceDiagnostics.Log("bcl.mic", $"reason=muted-bt-release device=\"{_lastMicDeviceName}\"");
        QueueMicrophoneTransition(false, "muted-bt-release");
    }
#endif

    public void UpdateProfile(byte playerId, string playerName)
    {
        _lastPlayerId = playerId;
        _lastPlayerName = string.IsNullOrWhiteSpace(playerName) ? "Unknown" : playerName;
    }

    public void SendRadioState(byte playerId, VoiceTeamRadioChannel channel)
    {
        channel = VoiceTeamRadioChannels.Normalize(channel);
        if (_lastLocalRadioChannel == channel) return;
        _lastLocalRadioChannel = channel;
        SendCustomMessage([RadioStateMagic[0], RadioStateMagic[1], RadioStateMagic[2], RadioStateMagic[3], playerId, VoiceTeamRadioChannels.IsActive(channel) ? (byte)1 : (byte)0, (byte)channel]);
    }

    public void SendCustomMessage(byte[] payload)
    {
        var wrapped = new byte[payload.Length + DataControlPrefixLength];
        Array.Copy(DataControlPrefix, wrapped, DataControlPrefixLength);
        Array.Copy(payload, 0, wrapped, DataControlPrefixLength, payload.Length);
        foreach (var peer in SnapshotPeers())
        {
            try
            {
                if (peer.DataChannel?.readyState == RTCDataChannelState.open)
                {
                    peer.DataChannel.send(wrapped);
                    Interlocked.Increment(ref _customTx);
                }
            }
            catch { }
        }
    }

    internal bool TryPublishPublicLobby(VoiceLobbyPublishRequest request)
    {
        if (_socket?.Connected != true || _disposed || _joinedClientId < 0)
            return false;
        if (!string.Equals(RoomCode, request.Code, StringComparison.OrdinalIgnoreCase))
            return false;

        _ = _socket.EmitAsync("lobby", new object[] { request.Code, BetterCrewLinkLobbyMetadata.ToBclLobby(request) });
        return true;
    }

    internal bool TryRemovePublicLobby(string code)
    {
        if (_socket?.Connected != true || _disposed || string.IsNullOrWhiteSpace(code))
            return false;
        _ = _socket.EmitAsync("remove_lobby", code);
        return true;
    }

    public void ApplyRemoteRadioState(byte playerId, VoiceTeamRadioChannel channel)
    {
        channel = VoiceTeamRadioChannels.Normalize(channel);
        foreach (var peer in SnapshotPeers())
        {
            if (peer.PlayerId == playerId)
            {
                peer.ApplyRadioChannel(channel);
                VoiceDiagnostics.Log("bcl.radio.rx", $"client={peer.ClientId} player={playerId} active={VoiceTeamRadioChannels.IsActive(channel)} channel={channel}");
            }
        }
    }

    public void Rejoin()
    {
        ClearPeers();
        ResetJoinState();
        if (_socket != null)
            _ = JoinAsync();
        VoiceDiagnostics.Log("bcl.rejoin", "state=cleared");
    }

    public bool TrySetRemoteVolume(byte playerId, string playerName, float volume)
    {
        foreach (var peer in SnapshotPeers())
        {
            if (peer.PlayerId == playerId || string.Equals(peer.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
            {
                peer.SetVolume(volume);
                return true;
            }
        }
        return false;
    }

    public int ResetPeerMappingsNoMute()
    {
        var count = 0;
        foreach (var peer in SnapshotPeers())
        {
            peer.ResetMappingNoMute();
            count++;
        }
        return count;
    }

    public void Update(
        VoiceGameStateSnapshot? snapshot,
        IReadOnlyList<VoiceChatRoom.SpeakerCache> speakerCache,
        IReadOnlyList<IVoiceComponent> virtualMicrophones,
        bool localInVent,
        bool commsSabActive)
    {
        // backend.mainactions is where peer-join work (MapClient -> WireNewPeerConnection -> RentPeerConnection)
        // runs; timing it confirms the pooled connection keeps the connect off the main-thread critical path.
        long mainActionsTicks = VoiceFrameProfiler.Begin();
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); } catch (Exception ex) { VoiceDiagnostics.Log("bcl.error", $"stage=mainThread error=\"{ex.Message}\""); }
        }
        VoiceFrameProfiler.End("backend.mainactions", mainActionsTicks);

#if ANDROID
        _androidMicrophone?.Tick();
#elif WINDOWS
        _androidMicrophone?.Tick();
        MaybeReleaseBluetoothMutedMicrophone();
#endif

#if WINDOWS || ANDROID
        PumpRpcSignaling(snapshot);
#endif

        if (snapshot == null)
        {
            SnapshotPeersInto(_updatePeerScratch);
            foreach (var peer in _updatePeerScratch) peer.MuteAll();
            MaybeLogStats(snapshot, "no-snapshot");
            return;
        }

        long joinTicks = VoiceFrameProfiler.Begin();
        _ = JoinAsync(snapshot);
        VoiceFrameProfiler.End("backend.join", joinTicks);
        long retryTicks = VoiceFrameProfiler.Begin();
        RetryClosedDataChannels();
        VoiceFrameProfiler.End("backend.retry", retryTicks);

        var localPlayer = snapshot.TryGetLocalPlayer(out var local) ? local : (VoicePlayerSnapshot?)null;
        var listenerPos = localPlayer?.Position;


        long proxTicks = VoiceFrameProfiler.Begin();
        SnapshotPeersInto(_updatePeerScratch);
        _routeClientScratch.Clear();
        var lossReportNowUtc = DateTime.UtcNow;
        var frameGapTicks = _lastUpdateTicks == 0 ? 0 : lossReportNowUtc.Ticks - _lastUpdateTicks;
        _lastUpdateTicks = lossReportNowUtc.Ticks;
        var resumeFrame = IsResumeFrame(frameGapTicks, ResumeFrameThreshold.Ticks);
        List<SidecarProtocol.GameStatePeerInput>? helperGameStatePeers = null;
#if WINDOWS
        if (_voice != null && listenerPos.HasValue)
            helperGameStatePeers = new List<SidecarProtocol.GameStatePeerInput>();
#endif
        foreach (var peer in _updatePeerScratch)
        {
            if (peer.ClientId >= 0)
            {
                if (_routeClientScratch.TryGetValue(peer.ClientId, out var rival))
                {
                    var kept = ChooseDuplicateRouteWinner(rival, peer);
                    var muted = ReferenceEquals(kept, peer) ? rival : peer;
                    _routeClientScratch[peer.ClientId] = kept;
                    SuppressDuplicateRoutePeer(kept, muted);
                    if (ReferenceEquals(muted, peer)) continue;
                }
                else
                    _routeClientScratch[peer.ClientId] = peer;
            }
            var target = FindTarget(snapshot, peer);
            if (target.HasValue && VoiceProximityCalculator.IsUnavailableTarget(target.Value))
                peer.ResetMappingNoMute();
            if (target.HasValue && !VoiceProximityCalculator.IsUnavailableTarget(target.Value) && peer.UpdateProfile(target.Value.PlayerId, target.Value.PlayerName))
                ApplySavedVolume(peer);

            VoiceProximityResult result;
            if (snapshot.Phase == VoiceGamePhase.EndGame)
                result = VoiceProximityCalculator.CalculateEndGame();
            else if (VoiceSceneState.IsLobbyVoicePhase(snapshot.Phase))
                result = VoiceProximityCalculator.CalculateLobby(target, listenerPos);
            else if (VoiceSceneState.IsMeetingVoicePhase(snapshot.Phase))
                result = VoiceProximityCalculator.CalculateMeeting(localPlayer, target, peer.RadioActive, snapshot.Phase, peer.RadioChannel);
            else
                result = VoiceProximityCalculator.CalculateTaskPhase(localPlayer, target, listenerPos, snapshot.LocalLightRadius, snapshot.MapId, snapshot.CameraViewActive, snapshot.ActiveCameraIndex, snapshot.ActiveCameraPosition, speakerCache, virtualMicrophones, localInVent, peer.RadioActive, commsSabActive, peer.WallCoefficient, peer.RadioChannel);

            result = VoiceRoleMuteState.ApplyLocalListenerAudioMuffle(result);
            peer.Apply(result); // proximity volumes only — volatile route writes, no _sync, no decode
            if (helperGameStatePeers != null && target.HasValue && peer.ClientId >= 0)
                helperGameStatePeers.Add(new SidecarProtocol.GameStatePeerInput(
                    peer.ClientId.ToString(CultureInfo.InvariantCulture), target.Value.Position.x, target.Value.Position.y,
                    !result.Audible, peer.ClientVolume, 0u));
            LogCenteredLoudRoute(peer, target, listenerPos, result, snapshot.Phase);
            // Deliberately NOT calling peer.TryFlushBufferedVoice here. Doing so held the per-peer _sync lock
            // across the Concentus Opus decode ON THE UNITY MAIN/RENDER THREAD, so a packet arriving on the
            // WebRTC receive thread (or the tail-flush timer firing) could block rendering — a frame-stutter
            // vector that scaled with talker count. The quiet tail is already drained off the render thread by
            // the per-peer 40 ms _tailFlushTimer (re-armed on every received packet in TryReceiveVoicePacket),
            // so all Opus decode now happens on the receive/timer threads only. Worst case: ~40 ms extra tail
            // latency on the final frames of a talkspurt, which is imperceptible.
            peer.MaybeSendLossReport(lossReportNowUtc);
            peer.MaybeSendKeepalive(lossReportNowUtc);
            if (resumeFrame)
                peer.RebaseInbound(lossReportNowUtc.Ticks);
            else
                NotePeerLivenessForDiagnostics(peer, lossReportNowUtc);
            peer.SampleDiagnostics();
        }
        VoiceFrameProfiler.End("room.backend.proximity", proxTicks);

#if WINDOWS
        if (helperGameStatePeers != null && listenerPos.HasValue)
        {
            var lp = listenerPos.Value;
            var rs = VoiceRoomSettingsState.Current;
            _voice?.SendGameState(lp.x, lp.y, 0f, VoiceChatHudState.IsSpeakerMuted,
                _masterVolume, rs.MaxChatDistance, rs.FalloffMode, helperGameStatePeers);
        }
#endif

        MaybeLogStats(snapshot, "ok");
    }

    private PeerConnection ChooseDuplicateRouteWinner(PeerConnection first, PeerConnection second)
    {
        string? mappedSocket;
        lock (_peerSync)
            _clientToSocket.TryGetValue(second.ClientId, out mappedSocket);
        if (mappedSocket != null)
        {
            var firstMapped = string.Equals(mappedSocket, first.SocketId, StringComparison.Ordinal);
            var secondMapped = string.Equals(mappedSocket, second.SocketId, StringComparison.Ordinal);
            if (firstMapped != secondMapped) return firstMapped ? first : second;
        }
        var firstOpen = first.DataChannel?.readyState == RTCDataChannelState.open;
        var secondOpen = second.DataChannel?.readyState == RTCDataChannelState.open;
        if (firstOpen != secondOpen) return firstOpen ? first : second;
        return ConnectionRecencyUtc(second) >= ConnectionRecencyUtc(first) ? second : first;
    }

    private static DateTime ConnectionRecencyUtc(PeerConnection peer)
    {
        var recency = peer.LastConnectionRebuildUtc > peer.LastRecoveryUtc ? peer.LastConnectionRebuildUtc : peer.LastRecoveryUtc;
        return peer.OfferStartedUtc > recency ? peer.OfferStartedUtc : recency;
    }

    private void SuppressDuplicateRoutePeer(PeerConnection kept, PeerConnection muted)
    {
        muted.Apply(VoiceProximityResult.Muted(VoiceProximityReason.Unmapped));
        var now = DateTime.UtcNow;
        if (_duplicateRouteLogUtcByClient.TryGetValue(muted.ClientId, out var lastLog) && now - lastLog < DuplicateRouteLogInterval) return;
        _duplicateRouteLogUtcByClient[muted.ClientId] = now;
        var keptOpen = kept.DataChannel?.readyState == RTCDataChannelState.open;
        var mutedOpen = muted.DataChannel?.readyState == RTCDataChannelState.open;
        VoiceDiagnostics.Log("bcl.peer.duplicate", $"client={muted.ClientId} keptSocket={kept.SocketId} mutedSocket={muted.SocketId} keptOpen={keptOpen} mutedOpen={mutedOpen}");
        if (keptOpen && !mutedOpen)
        {
            var mutedSocket = muted.SocketId;
            var keptSocket = kept.SocketId;
            _mainThreadActions.Enqueue(() => RemoveDuplicateRoutePeer(mutedSocket, keptSocket));
        }
    }

    private void RemoveDuplicateRoutePeer(string mutedSocketId, string keptSocketId)
    {
        lock (_peerSync)
        {
            if (!_peersBySocket.TryGetValue(mutedSocketId, out var muted)) return;
            if (!_peersBySocket.TryGetValue(keptSocketId, out var kept)) return;
            if (muted.ClientId < 0 || muted.ClientId != kept.ClientId) return;
            if (_clientToSocket.TryGetValue(muted.ClientId, out var mappedSocket)
                && string.Equals(mappedSocket, mutedSocketId, StringComparison.Ordinal)) return;
            if (muted.DataChannel?.readyState == RTCDataChannelState.open) return;
            if (kept.DataChannel?.readyState != RTCDataChannelState.open) return;
            VoiceDiagnostics.Log("bcl.peer.superseded", $"oldSocket={mutedSocketId} newSocket={keptSocketId} client={muted.ClientId} reason=route-duplicate");
            RemovePeer(mutedSocketId);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _turnCts?.Cancel(); _turnCts?.Dispose(); } catch { }
        _sendPacer.Dispose();
#if WINDOWS || ANDROID
        if (_rpcOnMessageHooked)
        {
            AmongUsRpcSignaling.OnMessage -= OnRpcSignal;
            _rpcOnMessageHooked = false;
        }
        _peerSession?.Reset();
        _peerSession = null;
        _rpcKnownClients.Clear();
#endif
#if WINDOWS
        StopMicrophoneWorkerForDispose();
        StopVoiceSession("dispose");
        try { _androidSpeaker?.Dispose(); } catch { }
        _androidSpeaker = null;
        _voiceMixer = null;
#elif ANDROID
        StopAndroidMicrophone("dispose");
        try { _androidSpeaker?.Dispose(); } catch { }
        _androidSpeaker = null;
#endif
        lock (_captureFrameSync)
            _micPreprocessor.Dispose();
        try { _encoder.Dispose(); } catch { }
        var socket = _socket;
        _socket = null;
        if (socket != null)
            _ = DisconnectAndDisposeSocketAsync(socket);
        ClearPeers();
        DrainPeerConnectionPool();
        _lastCenteredLoudLogUtc.Clear();
    }

    private static async Task DisconnectAndDisposeSocketAsync(SocketIOClient.SocketIO socket)
    {
        try { await socket.DisconnectAsync().ConfigureAwait(false); } catch { }
        // Dispose frees the reconnect-loop CTS/timers DisconnectAsync leaks (no finalizer).
        try { socket.Dispose(); } catch { }
    }

    private void ConnectSocket()
    {
        _socket = new SocketIOClient.SocketIO(new Uri(ServerUrl), BetterCrewLinkSocketOptions.Create());

        _socket.OnConnected += async (_, _) =>
        {
            var socketId = _socket?.Id ?? string.Empty;
            lock (_peerSync)
                _localSocketId = socketId;
            VoiceDiagnostics.Log("bcl.socket", $"connected=True socketId={socketId}");
            await Task.CompletedTask;
        };
        _socket.OnDisconnected += (_, _) => _mainThreadActions.Enqueue(() =>
        {
            ResetJoinState();
            VoiceDiagnostics.Log("bcl.socket", "connected=False peersKept=true");
        });
        // Socket.IO handlers run async-void on a background socket thread; ctx.GetValue<T> throws on a
        // malformed/hostile server payload. Without a guard that escapes as an unhandled exception and
        // crashes the process on .NET 6. Each body is wrapped + null-validated so a bad packet is logged and
        // dropped instead. (signal/clientPeerConfig below already follow this pattern.)
        _socket.On("setClient", async ctx =>
        {
            try
            {
                var socketId = ctx.GetValue<string>(0);
                var client = ctx.GetValue<VoiceBackendClient>(1);
                if (socketId == null || client == null) return;
#if !ANDROID
                _mainThreadActions.Enqueue(() => MapClient(socketId, client));
#endif
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=setClient error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("setClients", async ctx =>
        {
            try
            {
                var clients = ctx.GetValue<Dictionary<string, VoiceBackendClient>>(0);
                if (clients == null) return;
#if !ANDROID
                _mainThreadActions.Enqueue(() =>
                {
                    foreach (var sid in SnapshotMappedSocketIds())
                        if (!clients.ContainsKey(sid)) RemovePeer(sid);
                    foreach (var kv in clients)
                    {
                        if (kv.Key == null || kv.Value == null) continue;
                        if (MapClient(kv.Key, kv.Value))
                        {
                            var peer = EnsurePeer(kv.Key);
                            if (peer != null && ShouldInitiateOffer(kv.Key))
                                _ = StartOfferAsync(kv.Key);
                        }
                    }
                });
#endif
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=setClients error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("join", async ctx =>
        {
            try
            {
                var socketId = ctx.GetValue<string>(0);
                var client = ctx.GetValue<VoiceBackendClient>(1);
                if (socketId == null || client == null) return;
#if !ANDROID
                _mainThreadActions.Enqueue(() =>
                {
                    if (MapClient(socketId, client) && EnsurePeer(socketId) != null && ShouldInitiateOffer(socketId))
                        _ = StartOfferAsync(socketId);
                });
#endif
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=join error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("leave", async ctx =>
        {
            try
            {
                var socketId = ctx.GetValue<string>(0);
                if (socketId == null) return;
#if !ANDROID
                _mainThreadActions.Enqueue(() => RemovePeer(socketId));
#endif
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=leave error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("signal", async ctx =>
        {
            try
            {
                var payload = ctx.GetValue<SignalPayload>(0);
                if (payload == null) return;
#if !ANDROID
                VoiceDiagnostics.Log("bcl.signal.queued", $"fromSocket={payload.from} queueDepth={_mainThreadActions.Count + 1}");
                _mainThreadActions.Enqueue(() => HandleQueuedSignal(payload.from, payload.data));
#endif
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=signal error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("clientPeerConfig", async ctx =>
        {
            try
            {
                var config = ctx.GetValue<ClientPeerConfig>(0);
                if (config?.iceServers != null && config.iceServers.Length > 0)
                {
                    _iceServers = config.iceServers.Select(server => new RTCIceServer
                    {
                        urls = server.urls,
                        username = server.username,
                        credential = server.credential,
                    }).ToList();
                    // Pooled connections were built with the previous ICE servers; rebuild them.
                    DrainPeerConnectionPool();
                    RefillPeerConnectionPool();
                }
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=clientPeerConfig error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("VAD", async _ => await Task.CompletedTask);
        _ = _socket.ConnectAsync();
        // Warm the pool so the first peer-join doesn't generate a DTLS certificate on the main thread.
        RefillPeerConnectionPool();
    }

    private async Task JoinAsync(VoiceGameStateSnapshot? snapshot = null)
    {
        if (_socket == null || !_socket.Connected || _disposed || _lastPlayerId == byte.MaxValue || RoomCode == "MENU" || snapshot == null)
            return;

        var localClientId = snapshot.LocalClientId;
        var isHost = snapshot?.HostClientId == localClientId;
        if (_joinedPlayerId == _lastPlayerId && _joinedClientId == localClientId && _joinedIsHost == isHost)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastJoinAttemptUtc < JoinRetryInterval)
            return;
        _lastJoinAttemptUtc = now;

        if (Interlocked.Exchange(ref _joinInFlight, 1) == 1)
            return;

        try
        {
            if (await JoinWithIdentityAsync(_lastPlayerId, localClientId, isHost))
            {
                _joinedPlayerId = _lastPlayerId;
                _joinedClientId = localClientId;
                _joinedIsHost = isHost;
                Interlocked.Increment(ref _publicLobbyJoinEpoch);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _joinInFlight, 0);
        }
    }

    private async Task<bool> JoinWithIdentityAsync(byte playerId, int clientId, bool isHost)
    {
        if (_socket == null || _disposed) return false;
        try
        {
            await _socket.EmitAsync("id", new object[] { playerId, clientId });
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.join", $"state=failed stage=id room={RoomCode} error=\"{ex.Message}\"");
            return false;
        }

        try
        {
            await _socket.EmitAsync("join", new object[] { RoomCode, playerId, clientId, isHost });
            return true;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.join", $"state=failed stage=join room={RoomCode} error=\"{ex.Message}\"");
            return false;
        }
    }

    private void ResetJoinState()
    {
        _joinedPlayerId = byte.MaxValue;
        _joinedClientId = -1;
        _joinedIsHost = false;
        _lastJoinAttemptUtc = DateTime.MinValue;
        lock (_peerSync)
            _localSocketId = GetConnectedSocketId();
        _lastOfferRetryUtc = DateTime.MinValue;
    }

    private bool MapClient(string socketId, VoiceBackendClient client)
    {
        if (client.clientId < 0) return false;
        var localReason = GetLocalClientReason(socketId, client);
        if (localReason != null)
        {
            lock (_peerSync)
                _localSocketId = socketId;
            RemovePeer(socketId);
            DropPendingSignals(socketId, "local");
            VoiceDiagnostics.Log("bcl.map", $"socket={socketId} client={client.clientId} player={client.playerId} local=true reason={localReason} ownSocket={_socket?.Id ?? "none"} joinedClient={_joinedClientId}");
            return false;
        }

        bool changed;
        string? supersededSocket = null;
        lock (_peerSync)
        {
            changed = !_socketToClient.TryGetValue(socketId, out var oldClientId) || oldClientId != client.clientId;
            // A client whose Socket.IO id rotated (reconnect/churn) re-maps to a NEW socket under the same
            // stable client id. The previous socket's peer is now orphaned and nothing else tears it down,
            // so a single client accumulates duplicate peers + decoder/jitter/playback sinks. That leak
            // inflates peers/openChannels and briefly double-plays a talkspurt across the handoff — the
            // "echoey" bug report. Evict the superseded socket so it stays one peer + one sink per client.
            if (_clientToSocket.TryGetValue(client.clientId, out var priorSocket)
                && !string.Equals(priorSocket, socketId, StringComparison.Ordinal))
                supersededSocket = priorSocket;
            _clientToSocket[client.clientId] = socketId;
            _socketToClient[socketId] = client.clientId;
        }
        if (supersededSocket != null)
        {
            VoiceDiagnostics.Log("bcl.peer.superseded", $"oldSocket={supersededSocket} newSocket={socketId} client={client.clientId}");
            RemovePeer(supersededSocket);
        }
        if (changed)
        {
            VoiceDiagnostics.Log("bcl.map", $"socket={socketId} client={client.clientId} player={client.playerId} local=false ownSocket={_socket?.Id ?? "none"} joinedClient={_joinedClientId}");
            VoiceDiagnostics.Log("bcl.client.mapped", $"socket={socketId} client={client.clientId} player={client.playerId}");
        }
        RepairPeerClientMapping(socketId, client);
        ReplayPendingSignals(socketId);
        return true;
    }

    private void RepairPeerClientMapping(string socketId, VoiceBackendClient client)
    {
        PeerConnection? peer;
        int oldClientId;
        lock (_peerSync)
        {
            if (!_peersBySocket.TryGetValue(socketId, out peer)) return;
            oldClientId = peer.ClientId;
            if (!peer.UpdateClientId(client.clientId)) return;
            if (client.playerId >= 0 && client.playerId <= byte.MaxValue)
                peer.UpdateProfile((byte)client.playerId, peer.PlayerName);
        }

        VoiceDiagnostics.Log("bcl.peer.mapping.repaired", $"socket={socketId} oldClient={oldClientId} newClient={peer.ClientId} player={client.playerId}");
    }

    private bool IsMappedSocket(string socketId)
    {
        lock (_peerSync)
            return _socketToClient.ContainsKey(socketId);
    }

    private void HandleQueuedSignal(string fromSocketId, string dataJson)
    {
        if (_disposed || string.IsNullOrWhiteSpace(fromSocketId)) return;
        if (!IsMappedSocket(fromSocketId))
        {
            int pendingCount;
            lock (_peerSync)
            {
                if (!_pendingSignalsBySocket.TryGetValue(fromSocketId, out var pending))
                {
                    pending = new Queue<string>();
                    _pendingSignalsBySocket[fromSocketId] = pending;
                    _pendingSignalFirstSeenUtc[fromSocketId] = DateTime.UtcNow;
                }

                pending.Enqueue(dataJson ?? string.Empty);
                while (pending.Count > MaxPendingSignalsPerSocket) pending.Dequeue();
                pendingCount = pending.Count;
            }

            VoiceDiagnostics.Log("bcl.signal.deferred", $"fromSocket={fromSocketId} pending={pendingCount}");
            VoiceDiagnostics.Log("bcl.peer.mapping.unresolved", $"socket={fromSocketId} pendingSignals={pendingCount}");
            return;
        }

        _ = RunSignalHandlerAsync(fromSocketId, dataJson);
    }

    private void ReplayPendingSignals(string socketId)
    {
        Queue<string>? pending;
        lock (_peerSync)
        {
            if (!_pendingSignalsBySocket.Remove(socketId, out pending))
                return;
        }

        while (pending.Count > 0)
        {
            var data = pending.Dequeue();
            VoiceDiagnostics.Log("bcl.signal.replayed", $"fromSocket={socketId} remaining={pending.Count}");
            _ = RunSignalHandlerAsync(socketId, data);
        }
    }

    private void DropPendingSignals(string socketId, string reason)
    {
        int dropped = 0;
        lock (_peerSync)
        {
            if (_pendingSignalsBySocket.Remove(socketId, out var pending))
                dropped = pending.Count;
        }

        if (dropped > 0)
            VoiceDiagnostics.Log("bcl.signal.dropped", $"fromSocket={socketId} count={dropped} reason={reason}");
    }

    private void EvictStalePendingSignals()
    {
        var now = DateTime.UtcNow;
        List<string>? orphans = null;
        List<string>? aged = null;
        lock (_peerSync)
        {
            if (_pendingSignalFirstSeenUtc.Count == 0) return;
            foreach (var kv in _pendingSignalFirstSeenUtc)
            {
                if (!_pendingSignalsBySocket.ContainsKey(kv.Key))
                    (orphans ??= new()).Add(kv.Key);
                else if (now - kv.Value > PendingSignalSocketMaxAge && !_socketToClient.ContainsKey(kv.Key))
                    (aged ??= new()).Add(kv.Key);
            }
            if (orphans != null)
                foreach (var socket in orphans) _pendingSignalFirstSeenUtc.Remove(socket);
            if (aged != null)
                foreach (var socket in aged) _pendingSignalFirstSeenUtc.Remove(socket);
        }
        if (aged != null)
            foreach (var socket in aged) DropPendingSignals(socket, "unmapped-timeout");
    }

    private string? GetLocalClientReason(string socketId, VoiceBackendClient client)
    {
        if (_socket != null && !string.IsNullOrEmpty(_socket.Id) && string.Equals(socketId, _socket.Id, StringComparison.Ordinal))
            return "socket";
        if (_joinedClientId >= 0 && client.clientId == _joinedClientId)
            return "client";
        return null;
    }

    private string GetConnectedSocketId()
    {
        var socket = _socket;
        return socket?.Connected == true && !string.IsNullOrEmpty(socket.Id) ? socket.Id : string.Empty;
    }

    private string GetEffectiveLocalSocketId()
    {
        var socketId = _socket?.Id;
        return !string.IsNullOrEmpty(socketId) ? socketId : _localSocketId;
    }

    private bool IsLocalSocket(string socketId)
    {
        var localSocketId = GetEffectiveLocalSocketId();
        return !string.IsNullOrEmpty(localSocketId) && string.Equals(socketId, localSocketId, StringComparison.Ordinal);
    }

    private bool ShouldInitiateOffer(string socketId)
    {
#if ANDROID
        if (_peerSession != null)
            return RpcLocalIsOfferer(socketId);
#endif
        var localSocketId = GetEffectiveLocalSocketId();
        return !string.IsNullOrEmpty(localSocketId)
            && !string.Equals(socketId, localSocketId, StringComparison.Ordinal)
            && string.CompareOrdinal(localSocketId, socketId) < 0;
    }

    private static bool IsRetryableDataChannelState(RTCDataChannelState? state)
        => state == null || state == RTCDataChannelState.closed;

    // Receive-side watchdog gate (pure, unit-tested). The watchdog must ONLY fire on the TRUE wedged signature:
    // the data channel is non-open while the connection has FINISHED negotiating (connected) or is dead
    // (disconnected/failed/closed), AND the per-peer deficit clock has expired. A connection still in
    // 'connecting'/'new' is a legitimate first handshake — possibly seconds from opening over a cold relay/TURN
    // candidate — and must NOT be pre-empted, even past the fixed deficit timeout. (StuckConnecting covers the
    // initiator-only OfferStartedUtc anchor separately; this is the role-independent backstop.)
    private static bool WatchdogShouldRedrive(RTCPeerConnectionState connState, RTCDataChannelState? chanState, bool deficitExpired)
    {
        if (!deficitExpired) return false;
        if (chanState == RTCDataChannelState.open) return false;
        // Only consider a connection that has left negotiation: connected (handshake done, channel wedged) or a
        // terminal/unhealthy state. Skip a live first handshake still in 'connecting'/'new'.
        if (connState is RTCPeerConnectionState.connecting or RTCPeerConnectionState.@new) return false;
        return true;
    }

    // Whether an answerer should re-request a fresh offer for a peer whose link is unhealthy. The connection
    // has stopped actively establishing (it is NOT a fresh 'connecting' first handshake), yet the data
    // channel is non-open. Covers: (a) connection-state failed/closed (the original, kept), (b) the channel
    // is closed/null while the connection reads 'connected' (SCTP/channel died, ICE alive — the wedged
    // one-directional case the old gate missed), and (c) a sustained 'disconnected' (debounced by the
    // per-peer NextRetryUtc backoff). A live 'connecting' handshake is deliberately excluded so this never
    // produces a duplicate offer during normal establishment.
    private static bool AnswererShouldRerequest(RTCPeerConnectionState connectionState, RTCDataChannelState? channelState)
    {
        if (channelState == RTCDataChannelState.open) return false;
        if (connectionState is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) return true;
        if (connectionState == RTCPeerConnectionState.connected && IsRetryableDataChannelState(channelState)) return true;
        if (connectionState == RTCPeerConnectionState.disconnected) return true; // sustained; NextRetryUtc debounces
        return false;
    }

    // P2.1: cheap, pure structural validation of an Opus packet from its TOC byte, used to pre-empt the
    // per-packet Concentus decode THROW on a clearly-invalid (foreign/incompatible) packet. The TOC byte encodes
    // a 2-bit frame-count CODE in its low bits (RFC 6716 §3.1):
    //   c=0 -> 1 frame                (min length 1: the TOC alone is a valid DTX/silence packet)
    //   c=1 -> 2 frames, equal size   (needs >= 2 bytes: the second frame's data cannot be zero-length here)
    //   c=2 -> 2 frames, VBR          (needs >= 2 bytes: 1 length byte after the TOC, min)
    //   c=3 -> arbitrary frame count  (needs >= 2 bytes: a frame-count byte must follow the TOC)
    // Returns true ONLY for the unambiguous cases (empty, or a multi-frame code with too few bytes to even hold
    // its required header). It is deliberately CONSERVATIVE: it never rejects a packet that could be a valid small
    // frame (a 1-byte code-0 packet passes), so valid-audio behaviour is unchanged. Concentus still validates the
    // full frame-length arithmetic; this only removes the steady-state throw cost on streams that can never decode.
    internal static bool IsOpusPacketStructurallyInvalid(byte[] data)
    {
        if (data == null || data.Length == 0) return true; // R1: an Opus packet must be at least one byte (the TOC)
        int code = data[0] & 0x3;          // low 2 bits of the TOC = frame-count code
        if (code == 0)
        {
            if (data.Length > 1) return false;
            return ((data[0] >> 3) & 0x1F) >= 12; // bare TOC is valid DTX only for SILK (config<12); Hybrid/CELT throw
        }
        // Code 1 (two CBR frames of EQUAL size) is also valid as a bare 1-byte packet per RFC 6716: it encodes two
        // zero-length frames (silence), so the TOC alone is structurally fine — let Concentus decode it to silence.
        if (code == 1) return false;
        // Codes 2/3 REQUIRE at least one more byte after the TOC (a length byte / a frame-count byte). A packet
        // that is exactly the TOC for either of these is structurally impossible.
        return data.Length < 2;
    }

    internal static bool IsOpusDtxSilencePacket(byte[] data)
        => data != null && data.Length == 1 && (data[0] & 0x3) <= 1 && ((data[0] >> 3) & 0x1F) >= 12;

    private void NotePeerLivenessForDiagnostics(PeerConnection peer, DateTime nowUtc)
    {
        if (peer.DataChannel?.readyState != RTCDataChannelState.open || !peer.HasOpenedAtLeastOnce) return;
        if (IsPeerLive(true, true, peer.LastInboundTicks, nowUtc.Ticks, PeerSilenceTimeout.Ticks))
        {
            peer.SilentSinceLoggedUtc = DateTime.MinValue;
            return;
        }
        if (!VoiceDiagnostics.IsEnabled) return;
        if (peer.SilentSinceLoggedUtc != DateTime.MinValue && nowUtc - peer.SilentSinceLoggedUtc < SilenceLogInterval) return;
        peer.SilentSinceLoggedUtc = nowUtc;
        var connState = peer.Connection?.connectionState.ToString() ?? "none";
        var corroborated = (peer.Connection != null && IsDeadConnectionState(peer.Connection.connectionState))
                           || peer.ConsecutiveKeepaliveSendFailures > 0;
        var silentMs = (nowUtc.Ticks - peer.LastInboundTicks) / TimeSpan.TicksPerMillisecond;
        VoiceDiagnostics.Log("bcl.peer.silent",
            $"client={peer.ClientId} silentMs={silentMs} conn={connState} sendFail={peer.ConsecutiveKeepaliveSendFailures} corroborated={corroborated}");
    }

    private void RetryClosedDataChannels()
    {
#if ANDROID
        if (_peerSession != null) return;
#endif
        var now = DateTime.UtcNow;
        if (now - _lastOfferRetryUtc < OfferRetryInterval) return;
        EvictStalePendingSignals();
        var peers = SnapshotPeers();
        // The answerer's dc.onopen reset never fires under SIPSorcery (no inbound channel-open callback), so
        // its RecoveryAttempts/NextRetryUtc would otherwise stay stale after a channel actually opens. Observe
        // 'open' here and reset the backoff so a recovered peer that later glitches retries fast. Also clears
        // the watchdog deficit clock for any open channel and arms it ONLY for a non-open channel whose
        // connection has LEFT negotiation — never during a live 'connecting'/'new' first handshake (which may
        // be seconds from opening over a cold relay), so the 12s deficit window measures the true wedged span.
        foreach (var peer in peers)
        {
            if (peer.DataChannel?.readyState == RTCDataChannelState.open)
            {
                peer.HasOpenedAtLeastOnce = true;
                if (peer.RecoveryAttempts != 0 || peer.NextRetryUtc != DateTime.MinValue)
                {
                    peer.RecoveryAttempts = 0;
                    peer.NextRetryUtc = DateTime.MinValue;
                }
                peer.ChannelDeficitSinceUtc = DateTime.MinValue;
            }
            else if (peer.Connection != null && !IsLocalSocket(peer.SocketId)
                     && peer.Connection.connectionState is not (RTCPeerConnectionState.connecting or RTCPeerConnectionState.@new))
            {
                if (peer.ChannelDeficitSinceUtc == DateTime.MinValue)
                    peer.ChannelDeficitSinceUtc = now;
            }
            else
            {
                peer.ChannelDeficitSinceUtc = DateTime.MinValue;
            }
        }
        var retryPeers = peers
            .Where(peer => ShouldInitiateOffer(peer.SocketId)
                && now >= peer.NextRetryUtc
                && (IsRetryableDataChannelState(peer.DataChannel?.readyState) || IsStuckConnecting(peer, now)))
            .ToArray();
        // Answerer side can't offer (would glare) and OnPeerConnectionDied only fires its one-shot
        // request-offer on the failed/closed *transition*. If our link to the elected initiator is unhealthy
        // but no fresh offer came back, re-ask on the same cadence so a dropped request-offer or a
        // slow/asymmetric initiator self-heals without waiting for a meeting/scene reset. The gate
        // (AnswererShouldRerequest) now also covers a closed/null channel on a 'connected' connection (the
        // wedged one-directional case) and a sustained 'disconnected', not just failed/closed; NextRetryUtc
        // debounces it. The initiator still only re-offers if ITS own link needs a rebuild.
        var rerequestPeers = peers
            .Where(peer =>
            {
                if (ShouldInitiateOffer(peer.SocketId) || IsLocalSocket(peer.SocketId)) return false;
                if (now < peer.NextRetryUtc) return false;
                var conn = peer.Connection;
                return conn != null && AnswererShouldRerequest(conn.connectionState, peer.DataChannel?.readyState);
            })
            .ToArray();
        // Receive-side watchdog: a peer whose data channel has stayed non-open while its connection is alive
        // past ChannelDeficitWatchdogTimeout — the measurable openChannels < peers / one-directional signature
        // — gets a proactive re-drive regardless of role. The initiator recreates+re-offers; the answerer
        // re-requests. This is the role-independent backstop for both the answerer gate and any storm residue.
        var watchdogPeers = peers
            .Where(peer =>
            {
                if (IsLocalSocket(peer.SocketId)) return false;
                var conn = peer.Connection;
                if (conn == null) return false;
                if (now < peer.NextRetryUtc) return false;
                bool deficitExpired = peer.ChannelDeficitSinceUtc != DateTime.MinValue
                    && now - peer.ChannelDeficitSinceUtc >= ChannelDeficitWatchdogTimeout;
                // Only the TRUE wedged signature fires: channel non-open while the connection has LEFT
                // negotiation (connected/disconnected/failed/closed). A 'connecting'/'new' first handshake —
                // which may be seconds from opening over a cold relay — is never pre-empted.
                return WatchdogShouldRedrive(conn.connectionState, peer.DataChannel?.readyState, deficitExpired);
            })
            .ToArray();
        if (retryPeers.Length == 0 && rerequestPeers.Length == 0 && watchdogPeers.Length == 0) return;
        _lastOfferRetryUtc = now;
        foreach (var peer in retryPeers)
        {
            var stuck = IsStuckConnecting(peer, now);
            VoiceDiagnostics.Log("bcl.offer", $"reason={(stuck ? "stuck-connecting" : "retry")} socket={peer.SocketId} client={peer.ClientId} state={peer.DataChannel?.readyState.ToString() ?? "none"}");
            // A wedged 'connecting' channel can't be re-offered on the same connection; rebuild
            // first so StartOfferAsync sees a null channel and proceeds.
            if (stuck) RecreatePeerConnection(peer.SocketId);
            _ = StartOfferAsync(peer.SocketId);
            peer.RecoveryAttempts++;
            peer.NextRetryUtc = now + RecoveryBackoff(peer.RecoveryAttempts);
        }
        foreach (var peer in rerequestPeers)
        {
            VoiceDiagnostics.Log("bcl.offer", $"reason=re-request socket={peer.SocketId} client={peer.ClientId} state=connection-{peer.Connection?.connectionState.ToString().ToLowerInvariant() ?? "none"} channel={peer.DataChannel?.readyState.ToString() ?? "none"}");
            RequestOfferFromPeer(peer.SocketId);
            peer.RecoveryAttempts++;
            peer.NextRetryUtc = now + RecoveryBackoff(peer.RecoveryAttempts);
        }
        foreach (var peer in watchdogPeers)
        {
            // Don't double-drive a peer already handled by the initiator/answerer passes above.
            if (Array.IndexOf(retryPeers, peer) >= 0 || Array.IndexOf(rerequestPeers, peer) >= 0) continue;
            var initiator = ShouldInitiateOffer(peer.SocketId);
            VoiceDiagnostics.Log("bcl.offer", $"reason=watchdog socket={peer.SocketId} client={peer.ClientId} initiator={initiator} deficitMs={(now - peer.ChannelDeficitSinceUtc).TotalMilliseconds:0} state={peer.DataChannel?.readyState.ToString() ?? "none"}");
            if (initiator)
            {
                RecreatePeerConnection(peer.SocketId);
                _ = StartOfferAsync(peer.SocketId);
            }
            else
            {
                RequestOfferFromPeer(peer.SocketId);
            }
            peer.RecoveryAttempts++;
            peer.NextRetryUtc = now + RecoveryBackoff(peer.RecoveryAttempts);
        }
    }

    // Evict any other live peer mapped to the same client as socketId. A re-home (reconnect -> new socket,
    // same clientId) can leave the prior socket's peer live while the new socket is already mapped, so both
    // resolve to the same speaker and double-feed playback (loud, centered, non-directional). Caller MUST
    // hold _peerSync; must run BEFORE route generation so a shared-group RemoveInput can't strip the
    // survivor's just-added inputs.
    private void EvictSameClientPeersLocked(string socketId)
    {
        var clientId = _socketToClient.TryGetValue(socketId, out var mappedClientId) ? mappedClientId : -1;
        if (clientId < 0) return;
        List<string>? stalePeerSockets = null;
        foreach (var kv in _peersBySocket)
            if (kv.Value.ClientId == clientId && !string.Equals(kv.Key, socketId, StringComparison.Ordinal))
                (stalePeerSockets ??= new()).Add(kv.Key);
        if (stalePeerSockets == null) return;
        foreach (var stale in stalePeerSockets)
        {
            VoiceDiagnostics.Log("bcl.peer.superseded", $"oldSocket={stale} newSocket={socketId} client={clientId} reason=ensure-peer");
            RemovePeer(stale);
        }
    }

    private PeerConnection? EnsurePeer(string socketId)
    {
        if (IsLocalSocket(socketId)) return null;
        lock (_peerSync)
        {
            // Fast path: re-offers for a known peer return without renting (renting must happen off-lock).
            if (_peersBySocket.TryGetValue(socketId, out var known)) return known;
            // Evict same-client duplicates up front, independent of the rent below, so a re-home race can't
            // leave a stale peer double-feeding playback. Must precede route generation.
            EvictSameClientPeersLocked(socketId);
        }

        // Rent OUTSIDE _peerSync: a cold-miss DTLS build under the lock freezes the audio send path.
        var pc = RentPeerConnection();
        lock (_peerSync)
        {
            if (IsLocalSocket(socketId)) { CloseRented(pc); return null; }
            if (_peersBySocket.TryGetValue(socketId, out var existing)) { CloseRented(pc); return existing; }
            var clientId = _socketToClient.TryGetValue(socketId, out var mappedClientId) ? mappedClientId : -1;
            var playbackGroupId = clientId >= 0 ? clientId : _nextProvisionalPeerGroupId--;
            var peer = new PeerConnection(socketId, clientId, playbackGroupId);
            WireNewPeerConnection(peer, socketId, pc);
            _peersBySocket[socketId] = peer;
            if (_voiceMixer != null)
                peer.SetMixer(_voiceMixer);
            VoiceDiagnostics.Log("bcl.peer.created", $"socket={socketId} client={clientId} playbackGroup={playbackGroupId} provisional={(clientId < 0).ToString().ToLowerInvariant()}");
            VoiceDiagnostics.Log("bcl.peer-connected", $"socket={socketId} client={clientId}");
            return peer;
        }
    }

    // A pooled, pre-built connection paired with the ICE-config signature it was constructed from. The pool
    // compares each entry's signature against the live config at rent time, so there is no shared stamp that
    // can disagree with the pool's contents (which a config change racing the refiller used to cause).
    private readonly struct PooledPeerConnection
    {
        public readonly RTCPeerConnection Connection;
        public readonly string Signature;
        public PooledPeerConnection(RTCPeerConnection connection, string signature)
        {
            Connection = connection;
            Signature = signature;
        }
    }

    // Constructs an RTCPeerConnection (the expensive DTLS self-signed-cert step). Safe to call off the
    // Unity main thread — SIPSorcery is pure managed networking and ICE gathering only starts later, at
    // setLocalDescription, not at construction. The connection is paired with the signature of the exact
    // config it was built from (one settings snapshot drives both), so a pooled entry's recorded signature
    // can never disagree with the policy it actually carries.
    private PooledPeerConnection BuildPeerConnection()
    {
        var (cfg, signature) = ResolveIce();
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        var pc = new RTCPeerConnection(cfg);
        if (VoiceDiagnostics.IsEnabled)
        {
            bool hasTurn = cfg.iceServers.Any(s => s.urls != null && s.urls.StartsWith("turn", StringComparison.OrdinalIgnoreCase));
            VoiceDiagnostics.Log("bcl.pcpool.built",
                $"ms={(System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} poolSize={_pcPool.Count} thread={Environment.CurrentManagedThreadId} policy={cfg.iceTransportPolicy} iceServers={cfg.iceServers.Count} turn={hasTurn}");
        }
        return new PooledPeerConnection(pc, signature);
    }

    // Read the Nat Fix / TURN settings once into locals so the resolved config and its signature are always
    // derived from the SAME snapshot — there is no torn read between "what we built" and "what we stamped".
    private void ReadIceSettings(out bool natFix, out string turnUrl, out string turnUser, out string turnCred, out bool forceRelay)
    {
        natFix = true;
        turnUrl = "";
        turnUser = "";
        turnCred = "";
        // Wine uses the same automatic ICE selection as native (host -> srflx -> relay),
        // so TURN is only used when direct/STUN fail. Opt-in relay-only via WineForceRelay.
        forceRelay = false;
        try
        {
            var settings = VoiceSettings.Instance;
            if (settings != null)
            {
                natFix = settings.NatFix.Value;
                turnUrl = settings.TurnServerUrl.Value;
                turnUser = settings.TurnUsername.Value;
                turnCred = settings.TurnCredential.Value;
                forceRelay = settings.WineForceRelay.Value && WineEnvironment.IsWine;
            }
        }
        catch { /* settings not ready; fall back to defaults (Nat Fix on, automatic ICE relay selection) */ }

        // Runtime escalation: if direct/STUN repeatedly failed this session, force relay regardless of platform.
        forceRelay |= _forceRelayEscalated;
    }

    // Called by the room's recovery loop after repeated TOTAL peer failure (never connected to anyone).
    // Latches relay-only ICE for the rest of the session and rebuilds the warm pool so the next peer
    // connections use it. Reuses the exact forceRelay path the Wine fix already validated. Idempotent.
    public void EscalateToRelayOnly(string reason)
    {
        if (_forceRelayEscalated || _disposed) return;
        _forceRelayEscalated = true;
        VoiceDiagnostics.Log("bcl.ice.escalate", $"forceRelay=true reason={reason} (repeated direct/STUN failure -> relay-only for session)");
        DrainPeerConnectionPool();
        RefillPeerConnectionPool();
    }

    // Resolve the ICE configuration AND its signature from one settings snapshot + one read of the (volatile)
    // _iceServers, so a pooled connection's recorded signature always matches the config it was built with.
    private (RTCConfiguration Config, string Signature) ResolveIce()
    {
        ReadIceSettings(out var natFix, out var turnUrl, out var turnUser, out var turnCred, out var forceRelay);
        var servers = _iceServers;
        var cfg = BuildIceConfiguration(servers, natFix, turnUrl, turnUser, turnCred, forceRelay);
        var sig = ComputeIceSignature(servers, natFix, turnUrl, turnUser, turnCred, forceRelay);
        return (cfg, sig);
    }

    // The signature of the CURRENTLY desired ICE config (read live). Compared against each pooled connection's
    // recorded signature at rent time to discard stale entries.
    private string CurrentIceSignature()
    {
        ReadIceSettings(out var natFix, out var turnUrl, out var turnUser, out var turnCred, out var forceRelay);
        return ComputeIceSignature(_iceServers, natFix, turnUrl, turnUser, turnCred, forceRelay);
    }

    // Pure ICE-config builder (unit-testable). With Nat Fix on, guarantees a STUN entry AND the configured TURN
    // relay are present so a NAT-blocked peer can fall back to relay; iceTransportPolicy stays 'all', so ICE
    // uses a direct path when it can and relays ONLY the peers that need it. With Nat Fix off, the base servers
    // are used unchanged (legacy STUN-only behaviour, no relay).
    //
    // forceRelay (Wine): under Wine, SIPSorcery's host/srflx candidate gathering is unreliable and often
    // yields zero candidates, so direct/STUN never connect. We add a TURN-over-TCP variant (Wine's UDP path
    // may be the broken part, but an outbound TCP connect works) and set iceTransportPolicy = relay so ICE
    // skips the broken local-candidate gathering and goes straight to the TURN allocation. Relay needs only a
    // single working outbound socket to the TURN server, which Wine can do. If TURN can't authenticate, we
    // canNOT force relay (relay-only with no TURN = guaranteed failure), so we fall back to 'all'.
    internal static RTCConfiguration BuildIceConfiguration(IReadOnlyList<RTCIceServer> baseServers, bool natFix, string turnUrl, string turnUsername, string turnCredential, bool forceRelay = false)
    {
        var servers = new List<RTCIceServer>();
        if (baseServers != null) servers.AddRange(baseServers);
        bool turnUsable = false;

        if (natFix && !string.IsNullOrWhiteSpace(turnUrl))
        {
            if (!servers.Any(s => s.urls != null && s.urls.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)))
                servers.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
            // Only add the TURN relay if it can actually authenticate. A long-term-credential TURN allocation
            // with a blank username/credential is unusable and would just add a dead ICE server, so skip it
            // (the peer falls back to STUN-only for this connection) when either credential is missing.
            if (!string.IsNullOrWhiteSpace(turnUsername) && !string.IsNullOrWhiteSpace(turnCredential))
            {
                turnUsable = true;
                if (!servers.Any(s => string.Equals(s.urls, turnUrl, StringComparison.OrdinalIgnoreCase)))
                    servers.Add(new RTCIceServer { urls = turnUrl, username = turnUsername, credential = turnCredential });
                // On Wine, also offer TURN over TCP — survives when Wine's UDP path is the broken part.
                if (forceRelay)
                {
                    var tcpUrl = AppendTransportTcp(turnUrl);
                    if (tcpUrl != null && !servers.Any(s => string.Equals(s.urls, tcpUrl, StringComparison.OrdinalIgnoreCase)))
                        servers.Add(new RTCIceServer { urls = tcpUrl, username = turnUsername, credential = turnCredential });
                }
            }
        }

        // The managed worker supplies TURN servers directly in baseServers; treat those as a usable relay too,
        // so relay-only escalation works even without a bring-your-own TurnServerUrl.
        if (!turnUsable && servers.Any(s => s.urls != null
                && (s.urls.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                    || s.urls.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))))
            turnUsable = true;

        // Relay-only is only safe when there is a usable TURN relay to route through; otherwise fall back to
        // 'all' so we don't strand the client with no reachable candidates at all.
        var policy = (forceRelay && turnUsable) ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all;
        var cfg = new RTCConfiguration { iceServers = servers, iceTransportPolicy = policy };

        // Wine: hand SIPSorcery an explicit bind address resolved WITHOUT the (Wine-unreliable) interface
        // enumeration, so ICE has a valid local endpoint to gather host/relay candidates from. Harmless on
        // native Windows but only applied under Wine. Wrapped so an SDK shape change can't break the build path.
        if (forceRelay)
        {
            try
            {
                var local = WineEnvironment.GetLocalIPv4();
                if (local != null) cfg.X_BindAddress = local;
            }
            catch { /* leave default binding */ }
        }

        return cfg;
    }

    // Add ?transport=tcp to a turn: URL if it doesn't already specify a transport.
    private static string? AppendTransportTcp(string turnUrl)
    {
        if (string.IsNullOrWhiteSpace(turnUrl)) return null;
        if (turnUrl.IndexOf("transport=", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        return turnUrl + (turnUrl.Contains('?') ? "&" : "?") + "transport=tcp";
    }

    // Cheap signature of everything that affects the resolved ICE config: Nat Fix, the TURN server URL AND its
    // credentials, and the signaling-provided base ICE servers. The TURN credentials are included so that a
    // credential-only change still invalidates the pool.
    private static string ComputeIceSignature(IReadOnlyList<RTCIceServer> baseServers, bool natFix, string turnUrl, string turnUser, string turnCred, bool forceRelay = false)
    {
        var baseUrls = baseServers == null ? "" : string.Join(",", baseServers.Select(s => s.urls));
        // The signature must mirror exactly the inputs BuildIceConfiguration actually consumes. With Nat Fix
        // OFF the TURN fields are ignored by the builder, so leaving them out keeps an edit to an unused TURN
        // setting from needlessly invalidating the warm pool. The "1|" / "0|" prefixes keep the two forms
        // distinct so a Nat-Fix-on config can never collide with a Nat-Fix-off one. The R|/D| prefix keeps
        // relay-forced (Wine) configs distinct from direct ones so the warm pool invalidates when it flips.
        var core = natFix
            ? "1|" + turnUrl + "|" + turnUser + "|" + turnCred + "|" + baseUrls
            : "0|" + baseUrls;
        return (forceRelay ? "R|" : "D|") + core;
    }

    // Rent a pre-built connection (instant) or, on a pool miss, build inline (the old behaviour — now rare:
    // only a cold start before the warm pool fills, or a burst that drains it). Stale-config entries are
    // discarded per-entry here (no shared stamp), so a config change can never hand out a wrong-policy
    // connection. Always kicks a background refill so the next join stays off the main thread.
    private RTCPeerConnection RentPeerConnection()
    {
        var liveSignature = CurrentIceSignature();
        RTCPeerConnection? pc = null;
        while (_pcPool.TryDequeue(out var pooled))
        {
            if (pooled.Signature == liveSignature) { pc = pooled.Connection; break; }
            // Built with a now-stale ICE config (Nat Fix toggled, TURN changed, or new signaling ICE
            // servers); close and skip it.
            try { pooled.Connection.close(); } catch { }
        }
        bool hit = pc != null;
        pc ??= BuildPeerConnection().Connection;
        if (VoiceDiagnostics.IsEnabled)
            VoiceDiagnostics.Log("bcl.pcpool.rent", $"hit={(hit ? "true" : "false")} poolSize={_pcPool.Count}");
        RefillPeerConnectionPool();
        return pc;
    }

    // Top the pool up to PcPoolTarget on a ThreadPool thread (one refiller at a time). The DTLS cert
    // generation therefore happens off the Unity main thread, ahead of when a peer actually joins. Each entry
    // carries its own build-time signature, so no shared stamp can be left stale by a config change that
    // races this refiller.
    private void RefillPeerConnectionPool()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _pcPoolRefilling, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                while (!_disposed && _pcPool.Count < PcPoolTarget)
                    _pcPool.Enqueue(BuildPeerConnection());
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("bcl.pcpool.error", $"stage=refill error=\"{ex.Message}\"");
            }
            finally
            {
                Interlocked.Exchange(ref _pcPoolRefilling, 0);
                if (_disposed) DrainPeerConnectionPool(); // close any straggler enqueued during teardown
                // Close the check-then-act window: if a concurrent rent dequeued an entry after our loop saw
                // the pool full but before we cleared the flag above, that rent's RefillPeerConnectionPool was
                // a no-op (flag still 1), leaving the pool one short with no active refiller. Now that the flag
                // is clear, re-check and reschedule if still below target. Bounded: a successful fill leaves
                // count == target, so the next pass does not reschedule.
                else if (_pcPool.Count < PcPoolTarget) RefillPeerConnectionPool();
            }
        });
    }

    // Close a connection that was rented but never wired onto a live peer (lost a create/recreate race).
    private static void CloseRented(RTCPeerConnection pc)
    {
        try { pc.close(); } catch { }
    }

    // Close and discard every pooled (never-wired) connection — used when the ICE config changes (the pooled
    // connections carry stale config) and on dispose.
    private void DrainPeerConnectionPool()
    {
        while (_pcPool.TryDequeue(out var pooled))
            try { pooled.Connection.close(); } catch { }
    }

    // Rebuild the warm pool off the main thread when a Nat Fix / TURN setting changes (invoked from the
    // settings dispatch). Doing this proactively means the next peer-join rents a current-config connection
    // instead of generating a DTLS cert inline on the Unity render thread — the exact stall the pool exists
    // to avoid. Pool ops are thread-safe, so this is safe to call from the settings/UI thread.
    public void RebuildIceConnectionPool()
    {
        if (_disposed) return;
        DrainPeerConnectionPool();
        RefillPeerConnectionPool();
    }

    // Wires handlers onto a pre-rented RTCPeerConnection. Caller MUST hold _peerSync and MUST have rented
    // 'pc' OUTSIDE the lock (a cold-miss DTLS build under _peerSync freezes the audio send path).
    private void WireNewPeerConnection(PeerConnection peer, string socketId, RTCPeerConnection pc)
    {
        // The rented connection is reachable for teardown only once it is stored on a peer that is in
        // _peersBySocket. If anything below throws before wiring completes, close it here so a still-open
        // DTLS connection can't be orphaned (nothing else would ever close it).
        bool wired = false;
        try
        {
            peer.Connection = pc;
#if ANDROID
            pc.addTrack(CreateOpusAudioTrack());
            pc.OnRtpPacketReceived += (System.Net.IPEndPoint ep, SDPMediaTypesEnum media, RTPPacket pkt) =>
            {
                if (media != SDPMediaTypesEnum.audio) return;
                OnAudioTrackRtp(peer, pkt);
            };
#endif
            pc.ondatachannel += dc =>
            {
                lock (_peerSync)
                    peer.DataChannel = dc;
                dc.onopen += () =>
                {
                    // SIPSorcery background thread: take _peerSync so the RecoveryAttempts/NextRetryUtc reset
                    // doesn't race the main-thread recovery poll (NextRetryUtc is the storm bound).
                    lock (_peerSync)
                    {
                        peer.RecoveryAttempts = 0;
                        peer.NextRetryUtc = DateTime.MinValue;
                    }
                    VoiceDiagnostics.Log("bcl.channel", $"socket={socketId} client={peer.ClientId} state=open inbound=true");
                };
                dc.onmessage += (_, _, data) => OnDataChannelMessage(peer, data);
            };
            pc.onicecandidate += candidate =>
            {
                if (candidate == null) return;
                // Diagnostic: candidate type (host/srflx/relay) proves whether TURN relay candidates were
                // gathered for this peer — i.e. whether Nat Fix is actually reaching the relay.
                if (VoiceDiagnostics.IsEnabled)
                    VoiceDiagnostics.Log("bcl.ice.candidate", $"socket={socketId} type={candidate.type} protocol={candidate.protocol}");
                var signalData = JsonSerializer.Serialize(new { candidate = candidate.candidate, sdpMid = candidate.sdpMid, sdpMLineIndex = candidate.sdpMLineIndex });
#if ANDROID
                if (_peerSession != null)
                {
                    if (SipsorceryVoiceTransport.TryParseClientId(socketId, out var candClientId))
                        _mainThreadActions.Enqueue(() => _peerSession?.OnLocalCandidate(candClientId, signalData));
                    return;
                }
#endif
                if (_socket == null) return;
                _ = _socket.EmitAsync("signal", new object[] { new { to = socketId, data = signalData } });
            };
            pc.oniceconnectionstatechange += iceState =>
            {
                if (VoiceDiagnostics.IsEnabled)
                    VoiceDiagnostics.Log("bcl.ice.state", $"socket={socketId} client={peer.ClientId} iceState={iceState}");
            };
            // Background-thread liveness from SIPSorcery: marshal recovery to the main thread.
            // Captured pc lets the handler ignore events from a connection we already replaced.
            pc.onconnectionstatechange += state =>
            {
#if ANDROID
                if (_peerSession != null)
                {
                    _mainThreadActions.Enqueue(() => OnRpcPeerConnectionState(socketId, pc, state));
                    return;
                }
#endif
                if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                    _mainThreadActions.Enqueue(() => OnPeerConnectionDied(socketId, pc, state));
            };
            wired = true;
        }
        finally
        {
            if (!wired) { try { pc.close(); } catch { } }
        }
    }

#if ANDROID
    private static MediaStreamTrack CreateOpusAudioTrack()
    {
        var opus = new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1");
        return new MediaStreamTrack(
            SDPMediaTypesEnum.audio,
            false,
            new List<SDPAudioVideoMediaFormat> { new SDPAudioVideoMediaFormat(opus) },
            MediaStreamStatusEnum.SendRecv);
    }

    private readonly List<RTCPeerConnection> _audioTrackPeerScratch = new();

    private void SendOpusFrameToAudioTracks(byte[] framed)
    {
        if (!VoicePacket.TryRead(framed, out var packet)) return;
        var payload = packet.Payload;
        if (payload.Length == 0) return;
        uint duration = packet.Duration == 0 ? (uint)AudioHelpers.FrameSize : packet.Duration;
        var sent = false;
        SnapshotAudioTrackPeersInto(_audioTrackPeerScratch);
        foreach (var conn in _audioTrackPeerScratch)
        {
            try
            {
                conn.SendAudio(duration, payload);
                sent = true;
                Interlocked.Increment(ref _encodedTx);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("bcl.mic.send_error", $"bytes={payload.Length} error=\"{ex.Message}\"");
            }
        }
        if (!sent) Interlocked.Increment(ref _micNoOpenChannelDrops);
    }

    private void SnapshotAudioTrackPeersInto(List<RTCPeerConnection> buffer)
    {
        buffer.Clear();
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
            {
                var conn = peer.Connection;
                if (conn != null && conn.connectionState == RTCPeerConnectionState.connected)
                    buffer.Add(conn);
            }
        }
    }

    private void OnAudioTrackRtp(PeerConnection peer, RTPPacket pkt)
    {
        var payload = pkt.Payload;
        if (payload == null || payload.Length == 0 || payload.Length > MaxIncomingDatagramBytes) return;
        peer.StampInbound(DateTime.UtcNow.Ticks);
        peer.TryReceiveAudioTrackOpus(payload, out var error, out var decodedFrames);
        if (!string.IsNullOrEmpty(error))
        {
            Interlocked.Increment(ref _audioDecodeFailures);
            if (peer.ShouldLogAudioDrop(out var suppressed))
                VoiceDiagnostics.Log("bcl.audio.drop", $"client={peer.ClientId} bytes={payload.Length} error=\"{error}\" suppressed={suppressed} source=rtp");
        }
        if (decodedFrames > 0)
            Interlocked.Add(ref _encodedRx, decodedFrames);
    }

    internal void RpcAddPeer(string key)
    {
        var peer = EnsureRpcPeer(key);
        if (peer == null) return;
        _ = StartOfferAsync(key);
    }

    internal void RpcClosePeer(string key) => RemovePeer(key);

    internal void RpcApplyRemoteSdp(string key, string sdpType, string sdp)
    {
        if (string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase))
        {
            _ = RpcAnswerOfferAsync(key, sdp);
            return;
        }
        RTCPeerConnection? conn;
        lock (_peerSync)
            conn = _peersBySocket.TryGetValue(key, out var peer) ? peer.Connection : null;
        if (conn == null) return;
        try { conn.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp }); }
        catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"socket={key} error=\"{DiagnosticSafe(ex.Message)}\" source=rpc-answer"); }
    }

    internal void RpcApplyRemoteCandidate(string key, string candidate)
    {
        RTCPeerConnection? conn;
        lock (_peerSync)
            conn = _peersBySocket.TryGetValue(key, out var peer) ? peer.Connection : null;
        if (conn == null) return;
        var init = TryDecodeSignal(candidate, out var signal, out _) && signal.Kind == "candidate"
            ? new RTCIceCandidateInit { candidate = signal.Candidate, sdpMid = signal.SdpMid, sdpMLineIndex = signal.SdpMLineIndex }
            : new RTCIceCandidateInit { candidate = candidate, sdpMid = "0", sdpMLineIndex = 0 };
        try { conn.addIceCandidate(init); }
        catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"socket={key} error=\"{DiagnosticSafe(ex.Message)}\" source=rpc-candidate"); }
    }

    private async Task RpcAnswerOfferAsync(string key, string sdp)
    {
        RTCPeerConnection? conn;
        lock (_peerSync)
            conn = _peersBySocket.TryGetValue(key, out var peer) ? peer.Connection : null;
        if (conn == null) return;
        try
        {
            conn.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
            var answer = conn.createAnswer(null);
            await conn.setLocalDescription(answer);
            lock (_peerSync)
            {
                if (_disposed || !_peersBySocket.TryGetValue(key, out var p) || !ReferenceEquals(p.Connection, conn)) return;
            }
            if (_peerSession != null && SipsorceryVoiceTransport.TryParseClientId(key, out var clientId))
            {
                var localSdp = answer.sdp;
                _mainThreadActions.Enqueue(() => _peerSession?.OnLocalSdp(clientId, "answer", localSdp));
            }
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.offer.error", $"socket={key} error=\"{DiagnosticSafe(ex.Message)}\" source=rpc-answer");
        }
    }

    private bool RpcLocalIsOfferer(string key)
    {
        var client = AmongUsClient.Instance;
        return client != null
            && SipsorceryVoiceTransport.TryParseClientId(key, out var remote)
            && client.ClientId < remote;
    }

    private PeerConnection? EnsureRpcPeer(string key)
    {
        lock (_peerSync)
            if (_peersBySocket.TryGetValue(key, out var known)) return known;

        var pc = RentPeerConnection();
        lock (_peerSync)
        {
            if (_peersBySocket.TryGetValue(key, out var existing)) { CloseRented(pc); return existing; }
            var clientId = SipsorceryVoiceTransport.TryParseClientId(key, out var parsed) ? parsed : -1;
            if (clientId >= 0)
            {
                _clientToSocket[clientId] = key;
                _socketToClient[key] = clientId;
            }
            var playbackGroupId = clientId >= 0 ? clientId : _nextProvisionalPeerGroupId--;
            var peer = new PeerConnection(key, clientId, playbackGroupId);
            WireNewPeerConnection(peer, key, pc);
            _peersBySocket[key] = peer;
            if (_voiceMixer != null)
                peer.SetMixer(_voiceMixer);
            VoiceDiagnostics.Log("bcl.peer.created", $"socket={key} client={clientId} playbackGroup={playbackGroupId} source=rpc");
            return peer;
        }
    }

    private void OnRpcPeerConnectionState(string socketId, RTCPeerConnection pc, RTCPeerConnectionState state)
    {
        if (_peerSession == null) return;
        lock (_peerSync)
        {
            if (!_peersBySocket.TryGetValue(socketId, out var peer)) return;
            if (!ReferenceEquals(peer.Connection, pc)) return;
        }
        if (!SipsorceryVoiceTransport.TryParseClientId(socketId, out var clientId)) return;
        if (state == RTCPeerConnectionState.connected)
            _peerSession.OnPeerConnected(clientId);
        else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected or RTCPeerConnectionState.closed)
            _peerSession.OnPeerConnectionLost(clientId, Environment.TickCount64);
    }
#endif

    // Includes 'disconnected' on purpose: used by the offer-gate (LocalLinkNeedsRebuild) and the
    // request-offer handling, where a sustained disconnect should still permit a rebuild. The edge-
    // triggered onconnectionstatechange handler above deliberately acts only on failed/closed so a
    // TRANSIENT 'disconnected' (which SIPSorcery often auto-recovers to 'connected') doesn't trigger a
    // premature teardown. The two predicates diverge intentionally; keep them in sync only on purpose.
    private static bool IsDeadConnectionState(RTCPeerConnectionState state)
        => state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected;

    private static bool IsStuckConnecting(PeerConnection peer, DateTime now)
        => peer.DataChannel?.readyState == RTCDataChannelState.connecting
           && peer.OfferStartedUtc != DateTime.MinValue
           && now - peer.OfferStartedUtc > StuckConnectingTimeout;

    // True when OUR side is unhealthy; gates remote request-offer so a peer can't tear down a
    // working link. "DataChannel open" is healthy; non-open states rebuild even if ICE reads connected.
    private static bool LocalLinkNeedsRebuild(PeerConnection peer)
    {
        if (peer.DataChannel?.readyState != RTCDataChannelState.open) return true;
        var connection = peer.Connection;
        return connection == null || IsDeadConnectionState(connection.connectionState);
    }

    internal static bool RemoteConnectionWasRecreated(string previousUfrag, string incomingUfrag)
        => !string.IsNullOrEmpty(previousUfrag) && !string.IsNullOrEmpty(incomingUfrag)
           && !string.Equals(previousUfrag, incomingUfrag, StringComparison.Ordinal);

    internal static string ExtractIceUfrag(string? sdp)
    {
        if (string.IsNullOrEmpty(sdp)) return string.Empty;
        var m = System.Text.RegularExpressions.Regex.Match(sdp, @"a=ice-ufrag:(\S+)");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    // Decides whether an incoming OFFER must be answered on a freshly-rebuilt connection. A second offer for a
    // peer only happens when the initiator RE-created its connection and re-offered, so any offer that arrives
    // while our connection has left 'new' AND we have no open data channel is such a re-offer — answer it on a
    // clean connection, never on the stale one (which would split-brain). A true first-contact offer (still
    // 'new') is answered on the fresh connection in place; a healthy already-open channel is left alone. Pure +
    // unit-tested; preserves the prior dead-state rebuild exactly.
    private static bool OfferRequiresRebuild(RTCPeerConnectionState connState, RTCDataChannelState? channelState)
    {
        if (IsDeadConnectionState(connState)) return true;               // failed/closed/disconnected: rebuild (unchanged)
        if (channelState == RTCDataChannelState.open) return false;      // healthy open channel: renegotiate in place
        return connState != RTCPeerConnectionState.@new;                 // established/establishing without a channel -> rebuild; 'new' first-contact -> answer in place
    }

    // Replaces only the connection + data channel, keeping playback/decoder/jitter/mapping intact,
    // so one failed handshake rebuilds without a global Rejoin that re-rolls every other peer.
    private void RecreatePeerConnection(string socketId)
    {
        if (IsLocalSocket(socketId)) return;
        // Rent OUTSIDE _peerSync: a cold-miss DTLS build under the lock freezes the audio send path
        // (which also locks _peerSync) — the storm-time stall behind the jitter-buffer underruns.
        var pc = RentPeerConnection();
        lock (_peerSync)
        {
            if (!_peersBySocket.TryGetValue(socketId, out var peer)) { CloseRented(pc); return; }
            try { peer.DataChannel?.close(); } catch { }
            try { peer.Connection?.close(); } catch { }
            peer.DataChannel = null;
            peer.ResetLiveness();
            peer.OfferStartedUtc = DateTime.MinValue;
            peer.LastConnectionRebuildUtc = DateTime.UtcNow;
            WireNewPeerConnection(peer, socketId, pc);
        }
        VoiceDiagnostics.Log("bcl.peer.recreated", $"socket={socketId}");
    }

    // Main-thread recovery for a dead connection: initiator rebuilds+re-offers, answerer requests
    // an offer (offering itself would cause glare). Per-peer debounced against re-offer storms.
    private void OnPeerConnectionDied(string socketId, RTCPeerConnection pc, RTCPeerConnectionState state)
    {
#if ANDROID
        if (_peerSession != null) return;
#endif
        lock (_peerSync)
        {
            if (!_peersBySocket.TryGetValue(socketId, out var peer)) return;
            if (!ReferenceEquals(peer.Connection, pc)) return; // event from a connection we already replaced
            NoteConnectionFailureLocked(socketId);
        }
        if (!TryBeginRecovery(socketId)) return;

        VoiceDiagnostics.Log("bcl.peer.recovery", $"socket={socketId} reason=connection-{state.ToString().ToLowerInvariant()} initiator={ShouldInitiateOffer(socketId)}");
        if (ShouldInitiateOffer(socketId))
        {
            RecreatePeerConnection(socketId);
            _ = StartOfferAsync(socketId);
        }
        else
        {
            RequestOfferFromPeer(socketId);
        }
    }

    // Recover a peer whose signal handling threw (e.g. setRemoteDescription/createAnswer failed, so no answer
    // was sent and the channel can never open). Mirrors OnPeerConnectionDied's recovery half; per-peer backoff
    // (TryBeginRecovery) bounds it, and a non-peer socket is a no-op.
    private void MarkPeerForImmediateRecovery(string socketId)
    {
        if (_disposed || !TryBeginRecovery(socketId)) return;
        VoiceDiagnostics.Log("bcl.peer.recovery", $"socket={socketId} reason=signal-failure initiator={ShouldInitiateOffer(socketId)}");
        if (ShouldInitiateOffer(socketId))
        {
            RecreatePeerConnection(socketId);
            _ = StartOfferAsync(socketId);
        }
        else
        {
            RequestOfferFromPeer(socketId);
        }
    }

    // Answerer-side recovery: ask the elected initiator to send a fresh offer (handled in HandleSignalAsync).
    private void RequestOfferFromPeer(string socketId)
    {
        var socket = _socket;
        if (socket == null) return;
        // Tell the initiator whether OUR link is genuinely wedged (channel non-open/dead). The open initiator
        // honors a wedged request-offer and rebuilds even when ITS channel reads 'open' (the split-brain case),
        // bounded by its per-peer recovery backoff. Reflects our live link state at send time.
        bool senderLinkWedged;
        lock (_peerSync)
            senderLinkWedged = _peersBySocket.TryGetValue(socketId, out var peer) && LocalLinkNeedsRebuild(peer);
        var signalData = JsonSerializer.Serialize(new { type = "request-offer", rebuild = senderLinkWedged });
        _ = socket.EmitAsync("signal", new object[] { new { to = socketId, data = signalData } });
        VoiceDiagnostics.Log("bcl.offer", $"reason=request socket={socketId} rebuild={senderLinkWedged.ToString().ToLowerInvariant()}");
    }

    // Exponential reconnect backoff: 3s, 6s, 12s, 24s, capped at 30s. The first retries stay fast so a one-off
    // glitch heals quickly; only a persistently-failing peer backs off, which stops an unreachable peer from
    // re-offering/recreating every 3s for the entire session (the storm seen in the field logs).
    private static TimeSpan RecoveryBackoff(int attempts)
        => TimeSpan.FromSeconds(Math.Min(30.0, PeerRecoveryDebounce.TotalSeconds * Math.Pow(2, Math.Max(0, attempts - 1))));

    // Per-peer recovery gate shared by the state handler and request-offer path, against storms. Honors the
    // exponential backoff so a peer that keeps failing isn't recreated on a tight loop.
    private bool TryBeginRecovery(string socketId)
    {
        lock (_peerSync)
        {
            if (!_peersBySocket.TryGetValue(socketId, out var peer)) return false;
            var now = DateTime.UtcNow;
            if (now < peer.NextRetryUtc) return false;
            peer.LastRecoveryUtc = now;
            peer.RecoveryAttempts++;
            peer.NextRetryUtc = now + RecoveryBackoff(peer.RecoveryAttempts);
            return true;
        }
    }

    private void NoteConnectionFailureLocked(string socketId)
    {
        var now = DateTime.UtcNow;
        List<string>? stale = null;
        foreach (var pair in _recentConnectionFailureUtcBySocket)
        {
            if (now - pair.Value >= MassConnectionFailureWindow)
                (stale ??= new List<string>()).Add(pair.Key);
        }
        if (stale != null)
            foreach (var key in stale)
                _recentConnectionFailureUtcBySocket.Remove(key);
        _recentConnectionFailureUtcBySocket[socketId] = now;

        int remotePeers = 0;
        foreach (var peer in _peersBySocket.Values)
        {
            if (!IsLocalSocket(peer.SocketId)) remotePeers++;
        }
        if (remotePeers == 0 || _recentConnectionFailureUtcBySocket.Count < 2 || _recentConnectionFailureUtcBySocket.Count * 2 < remotePeers) return;
        if (now - _lastMassConnectionFailureUtc < MassConnectionFailureRearmInterval) return;
        _lastMassConnectionFailureUtc = now;
        VoiceDiagnostics.Log("bcl.peer.massfailure", $"failed={_recentConnectionFailureUtcBySocket.Count} remotePeers={remotePeers}");
    }

    public bool ShouldDeferPeerEscalation
    {
        get
        {
            var now = DateTime.UtcNow;
            lock (_peerSync)
            {
                if (!HasPeerEscalationDeferralLocked(now))
                {
                    _deferralEpisodeStartUtc = DateTime.MinValue;
                    return false;
                }
                if (_deferralEpisodeStartUtc == DateTime.MinValue)
                    _deferralEpisodeStartUtc = now;
                return now - _deferralEpisodeStartUtc <= PeerEscalationDeferralEpisodeMax;
            }
        }
    }

    private bool HasPeerEscalationDeferralLocked(DateTime now)
    {
        if (_lastMassConnectionFailureUtc != DateTime.MinValue && now - _lastMassConnectionFailureUtc < PeerEscalationDeferralWindow)
            return true;
        foreach (var peer in _peersBySocket.Values)
        {
            if (IsLocalSocket(peer.SocketId)) continue;
            if (peer.DataChannel?.readyState == RTCDataChannelState.open) continue;
            if (peer.RecoveryAttempts > PeerRecoveryInFlightMaxAttempts) continue;
            var anchor = peer.LastConnectionRebuildUtc > peer.LastRecoveryUtc ? peer.LastConnectionRebuildUtc : peer.LastRecoveryUtc;
            if (anchor != DateTime.MinValue && now - anchor < PeerEscalationDeferralWindow)
                return true;
        }
        return false;
    }

    private async Task StartOfferAsync(string socketId)
    {
        PeerConnection? peer;
        SocketIOClient.SocketIO? socket;
        RTCPeerConnection? conn;
        lock (_peerSync)
        {
            if (!_peersBySocket.TryGetValue(socketId, out peer)) return;
            if (!ShouldInitiateOffer(socketId)) return;
            if (!IsRetryableDataChannelState(peer.DataChannel?.readyState)) return;
            socket = _socket;
            conn = peer.Connection; // capture once; use for the whole handshake
        }
        if (conn == null) return;
#if WINDOWS
        await Task.CompletedTask;
#else
        VoiceDiagnostics.Log("bcl.offer", $"reason=start socket={socketId} client={peer.ClientId} state={peer.DataChannel?.readyState.ToString() ?? "none"}");
        try
        {
            // No SynchronizationContext under BepInEx/IL2CPP: peer.Connection can be swapped across
            // awaits. Use captured `conn` and re-validate so channel/offer share one connection.
            var channel = await conn.createDataChannel("audio", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
            lock (_peerSync)
            {
                if (_disposed || !ReferenceEquals(peer.Connection, conn))
                {
                    try { channel.close(); } catch { }
                    return;
                }
                peer.ResetLiveness();
                peer.DataChannel = channel;
                peer.OfferStartedUtc = DateTime.UtcNow;
            }
            channel.onopen += () =>
            {
                // SIPSorcery background thread: take _peerSync so the RecoveryAttempts/NextRetryUtc reset
                // doesn't race the main-thread recovery poll (NextRetryUtc is the storm bound).
                lock (_peerSync)
                {
                    peer.RecoveryAttempts = 0;
                    peer.NextRetryUtc = DateTime.MinValue;
                }
                VoiceDiagnostics.Log("bcl.channel", $"socket={socketId} client={peer.ClientId} state=open inbound=false");
            };
            channel.onmessage += (_, _, data) => OnDataChannelMessage(peer, data);
            var offer = conn.createOffer(null);
            await conn.setLocalDescription(offer);
            lock (_peerSync)
            {
                if (_disposed || !ReferenceEquals(peer.Connection, conn)) return; // don't emit a stale offer
            }
#if ANDROID
            if (_peerSession != null && SipsorceryVoiceTransport.TryParseClientId(socketId, out var offerClientId))
            {
                var localSdp = offer.sdp;
                _mainThreadActions.Enqueue(() => _peerSession?.OnLocalSdp(offerClientId, "offer", localSdp));
            }
            else
#endif
            {
                if (socket == null) return;
                var sdpJson = JsonSerializer.Serialize(new { type = "offer", sdp = offer.sdp });
                await socket.EmitAsync("signal", new object[] { new { to = socketId, data = sdpJson } });
            }
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.offer.error", $"socket={socketId} error=\"{DiagnosticSafe(ex.Message)}\"");
        }
#endif
    }

    private async Task RunSignalHandlerAsync(string fromSocketId, string dataJson)
    {
        try
        {
            await HandleSignalAsync(fromSocketId, dataJson);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.signal.error", $"fromSocket={fromSocketId} error=\"{DiagnosticSafe(ex.Message)}\"");
            // A failed offer/answer leaves the peer wedged with no answer sent; recover now (backoff-bounded)
            // instead of waiting out the ~12s connection watchdog.
            _mainThreadActions.Enqueue(() => MarkPeerForImmediateRecovery(fromSocketId));
        }
    }

    private async Task HandleSignalAsync(string fromSocketId, string dataJson)
    {
        if (_disposed || string.IsNullOrWhiteSpace(fromSocketId)) return;
        VoiceDiagnostics.Log("bcl.signal.received", $"fromSocket={fromSocketId} bytes={(dataJson ?? string.Empty).Length}");
        if (!TryDecodeSignal(dataJson, out var signal, out var rejectReason))
        {
            LogSignalRejected(fromSocketId, rejectReason);
            return;
        }

        var peer = EnsurePeer(fromSocketId);
        if (peer?.Connection == null || _socket == null) return;
        VoiceDiagnostics.Log("bcl.signal.accepted", $"fromSocket={fromSocketId} client={peer.ClientId} type={signal.Kind}");
#if WINDOWS
        await Task.CompletedTask;
#endif
        if (signal.Kind == "request-offer")
        {
            // Answerer asked us to re-offer. Only the initiator may offer; share the recovery debounce.
            // Rebuild when OUR side needs it OR the sender flagged its link wedged (signal.RebuildRequested) —
            // the latter heals the split-brain where our channel reads 'open' but the remote's never opened.
            // ShouldInitiateOffer (no glare) + TryBeginRecovery (per-peer backoff) bound it to one peer, no storm.
            if (ShouldInitiateOffer(fromSocketId) && (LocalLinkNeedsRebuild(peer) || signal.RebuildRequested) && TryBeginRecovery(fromSocketId))
            {
#if !WINDOWS
                RecreatePeerConnection(fromSocketId);
#endif
                _ = StartOfferAsync(fromSocketId);
            }
            return;
        }
        if (signal.Kind == "offer")
        {
#if WINDOWS
#else
            // Rebuild so this offer lands on a CLEAN connection whenever our current one cannot carry its
            // channel. A second OFFER only arrives for a peer when the initiator RE-created its connection
            // (after a stuck/dead link) and re-offered. Applying such a re-offer to our stale connection —
            // which negotiated its ICE/DTLS/SCTP against the initiator's now-CLOSED connection — can never
            // open the data channel and leaves the two ends SPLIT-BRAINED (answerer reads 'connected' with no
            // channel, initiator reads 'connecting' on its fresh connection), a state only a full rejoin
            // previously cleared. OfferRequiresRebuild covers that 'connected/connecting-but-no-channel' case
            // in addition to the dead states, while leaving a true first-contact offer (connection 'new') and
            // a healthy already-open channel untouched.
            // RemoteConnectionWasRecreated: if the remote ICE ufrag changed, the remote RECREATED its
            // RTCPeerConnection — our open channel is now stale (the open answerer split-brain sub-case).
            string incomingUfrag = ExtractIceUfrag(signal.Sdp);
            string prevUfrag;
            lock (_peerSync) { prevUfrag = peer.LastRemoteIceUfrag; }
            bool ufragDriven = RemoteConnectionWasRecreated(prevUfrag, incomingUfrag);
            if (ufragDriven)
                VoiceDiagnostics.Log("bcl.offer.rebuild", $"reason=ufrag-changed socket={fromSocketId}");
            if (OfferRequiresRebuild(peer.Connection.connectionState, peer.DataChannel?.readyState) || ufragDriven)
            {
                RecreatePeerConnection(fromSocketId);
                PeerConnection? rebuilt = null;
                lock (_peerSync)
                {
                    if (_peersBySocket.TryGetValue(fromSocketId, out var found)) rebuilt = found;
                }
                if (rebuilt?.Connection == null) return;
                peer = rebuilt;
            }
            // Capture once: peer.Connection can be swapped/closed across the await, so re-reading it
            // could answer on a different connection than the remote description was set on.
            RTCPeerConnection? answerConn;
            lock (_peerSync) { answerConn = peer.Connection; }
            if (answerConn == null) return;
            answerConn.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = signal.Sdp });
            var answer = answerConn.createAnswer(null);
            await answerConn.setLocalDescription(answer);
            lock (_peerSync)
            {
                if (_disposed || !ReferenceEquals(peer.Connection, answerConn)) return; // swapped/closed underneath us
                peer.LastRemoteIceUfrag = incomingUfrag;
            }
            var answerJson = JsonSerializer.Serialize(new { type = "answer", sdp = answer.sdp });
            await _socket.EmitAsync("signal", new object[] { new { to = fromSocketId, data = answerJson } });
#endif
        }
        else if (signal.Kind == "answer")
        {
#if WINDOWS
#else
            peer.Connection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = signal.Sdp });
#endif
        }
        else if (signal.Kind == "candidate")
        {
#if WINDOWS
#else
            peer.Connection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = signal.Candidate,
                sdpMid = signal.SdpMid,
                sdpMLineIndex = signal.SdpMLineIndex,
            });
#endif
        }
    }

    private static bool TryDecodeSignal(string? dataJson, out DecodedSignal signal, out string reason)
    {
        signal = default;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            reason = "invalid-json";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(dataJson);
        }
        catch
        {
            reason = "invalid-json";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeProp))
            {
                if (typeProp.ValueKind != JsonValueKind.String)
                {
                    reason = "unsupported-type";
                    return false;
                }

                var type = typeProp.GetString() ?? string.Empty;
                if (type == "request-offer")
                {
                    // SDP-less control message: answerer asks the initiator to re-offer. Optional 'rebuild' bool
                    // (default false) signals the sender's link is wedged, so we rebuild even on an open channel.
                    bool rebuild = root.TryGetProperty("rebuild", out var rb) && rb.ValueKind == JsonValueKind.True;
                    signal = DecodedSignal.Control("request-offer") with { RebuildRequested = rebuild };
                    return true;
                }
                if (type != "offer" && type != "answer")
                {
                    reason = "unsupported-type";
                    return false;
                }

                if (!root.TryGetProperty("sdp", out var sdpProp)
                    || sdpProp.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(sdpProp.GetString()))
                {
                    reason = "missing-sdp";
                    return false;
                }

                signal = DecodedSignal.Session(type, sdpProp.GetString()!);
                return true;
            }

            if (root.TryGetProperty("candidate", out var candidateProp))
            {
                if (candidateProp.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(candidateProp.GetString()))
                {
                    reason = "invalid-candidate";
                    return false;
                }

                ushort sdpMLineIndex = 0;
                if (root.TryGetProperty("sdpMLineIndex", out var indexProp))
                {
                    if (!indexProp.TryGetInt32(out var rawIndex) || rawIndex < 0 || rawIndex > ushort.MaxValue)
                    {
                        reason = "invalid-candidate";
                        return false;
                    }

                    sdpMLineIndex = (ushort)rawIndex;
                }

                var sdpMid = root.TryGetProperty("sdpMid", out var midProp) && midProp.ValueKind == JsonValueKind.String
                    ? midProp.GetString()
                    : null;
                signal = DecodedSignal.CandidateSignal(candidateProp.GetString()!, sdpMid, sdpMLineIndex);
                return true;
            }
        }

        reason = "invalid-candidate";
        return false;
    }

    private void LogSignalRejected(string fromSocketId, string reason)
    {
        if (!ShouldLogSignalRejected(fromSocketId, reason)) return;
        VoiceDiagnostics.Log("bcl.signal.rejected", $"fromSocket={fromSocketId} reason={reason}");
    }

    // Caller must hold _peerSync. Removes every "socketId:reason" entry for a departed socket.
    private void RemoveSignalRejectLogEntriesLocked(string socketId)
    {
        if (_lastSignalRejectLogUtc is null || _lastSignalRejectLogUtc.Count == 0) return;
        var prefix = socketId + ":";
        List<string>? toRemove = null;
        foreach (var key in _lastSignalRejectLogUtc.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                (toRemove ??= new List<string>()).Add(key);
        }
        if (toRemove == null) return;
        foreach (var key in toRemove)
            _lastSignalRejectLogUtc.Remove(key);
    }

    private bool ShouldLogSignalRejected(string fromSocketId, string reason)
    {
        var key = $"{fromSocketId}:{reason}";
        var now = DateTime.UtcNow;
        lock (_peerSync)
        {
            if (_lastSignalRejectLogUtc.TryGetValue(key, out var last)
                && now - last < SignalRejectLogInterval)
                return false;

            _lastSignalRejectLogUtc[key] = now;
            return true;
        }
    }

    private void OnDataChannelMessage(PeerConnection peer, byte[] data)
    {
        if (data.Length > MaxIncomingDatagramBytes) return;
        peer.StampInbound(DateTime.UtcNow.Ticks);
        if (HasDataControlPrefix(data))
        {
            var payload = new byte[data.Length - DataControlPrefixLength];
            Array.Copy(data, DataControlPrefixLength, payload, 0, payload.Length);
            if (TryHandleRadioState(payload, peer.PlayerId)) return;
            if (TryHandleLossReport(payload, peer)) return;
            if (TryParseKeepalivePayload(payload)) return;
            Interlocked.Increment(ref _customRx);
            // Side-channel is untrusted for authority (self-asserted id); also avoids a torn
            // read of peer.ClientId/PlayerId on this background thread vs the mapping thread.
            CustomMessageReceived?.Invoke(VoiceBackendCustomMessage.Unknown(payload, peer.SocketId));
            return;
        }

        if (TryHandleRadioState(data, peer.PlayerId)) return;

        peer.TryReceiveVoicePacket(data, out var error, out var decodedFrames);
        if (!string.IsNullOrEmpty(error))
        {
            Interlocked.Increment(ref _audioDecodeFailures);
            if (peer.ShouldLogAudioDrop(out var suppressed))
                VoiceDiagnostics.Log("bcl.audio.drop", $"client={peer.ClientId} bytes={data.Length} error=\"{error}\" suppressed={suppressed}");
        }
        if (decodedFrames > 0)
            Interlocked.Add(ref _encodedRx, decodedFrames);
    }

    // Direct byte compare; avoids per-packet Take(4)+SequenceEqual allocations.
    private static bool HasDataControlPrefix(byte[] data)
        => data.Length >= DataControlPrefixLength
           && data[0] == DataControlPrefix[0]
           && data[1] == DataControlPrefix[1]
           && data[2] == DataControlPrefix[2]
           && data[3] == DataControlPrefix[3];

    private bool TryHandleRadioState(byte[] payload, byte senderPlayerId)
    {
        if (payload.Length is not (6 or 7)
            || payload[0] != RadioStateMagic[0]
            || payload[1] != RadioStateMagic[1]
            || payload[2] != RadioStateMagic[2]
            || payload[3] != RadioStateMagic[3])
        {
            return false;
        }

        var playerId = payload[4];
        // A peer may only set its OWN radio state; consume (true) spoofed/unmapped without applying.
        if (senderPlayerId == byte.MaxValue || playerId != senderPlayerId)
        {
            VoiceDiagnostics.Log("bcl.radio.reject", $"sender={senderPlayerId} claimed={playerId}");
            return true;
        }

        var active = payload[5] != 0;
        var channel = VoiceTeamRadioChannels.FromWire(active, payload.Length >= 7 ? payload[6] : null);
        ApplyRemoteRadioState(playerId, channel);
        return true;
    }

    private static bool TryHandleLossReport(byte[] payload, PeerConnection peer)
    {
        if (!TryParseLossReportPayload(payload, out var lossPermille)) return false;
        peer.StoreLossReport(lossPermille);
        return true;
    }

    internal static byte[] BuildLossReportMessage(int lossPermille)
    {
        var permille = (ushort)Math.Clamp(lossPermille, 0, 1000);
        return new byte[]
        {
            DataControlPrefix[0], DataControlPrefix[1], DataControlPrefix[2], DataControlPrefix[3],
            LossReportMagic[0], LossReportMagic[1], LossReportMagic[2], LossReportMagic[3],
            1,
            (byte)(permille >> 8), (byte)permille,
        };
    }

    internal static bool TryParseLossReportPayload(byte[] payload, out int lossPermille)
    {
        lossPermille = 0;
        if (payload.Length != 7
            || payload[0] != LossReportMagic[0]
            || payload[1] != LossReportMagic[1]
            || payload[2] != LossReportMagic[2]
            || payload[3] != LossReportMagic[3]
            || payload[4] != 1)
            return false;
        var permille = (payload[5] << 8) | payload[6];
        if (permille > 1000) return false;
        lossPermille = permille;
        return true;
    }

    internal static byte[] BuildKeepaliveMessage()
        => new byte[]
        {
            DataControlPrefix[0], DataControlPrefix[1], DataControlPrefix[2], DataControlPrefix[3],
            KeepaliveMagic[0], KeepaliveMagic[1], KeepaliveMagic[2], KeepaliveMagic[3],
            1,
        };

    internal static bool TryParseKeepalivePayload(byte[] payload)
        => payload.Length == 5
           && payload[0] == KeepaliveMagic[0]
           && payload[1] == KeepaliveMagic[1]
           && payload[2] == KeepaliveMagic[2]
           && payload[3] == KeepaliveMagic[3]
           && payload[4] == 1;

    internal static bool IsPeerLive(bool channelOpen, bool hasOpenedAtLeastOnce, long lastInboundTicks, long nowTicks, long timeoutTicks)
        => channelOpen
           && hasOpenedAtLeastOnce
           && lastInboundTicks != 0
           && nowTicks - lastInboundTicks < timeoutTicks;

    internal static bool IsResumeFrame(long frameGapTicks, long resumeThresholdTicks)
        => frameGapTicks > resumeThresholdTicks;

#if WINDOWS
    // NAudio raises this when capture stops. e.Exception == null is OUR own StopRecording (mute, device
    // switch, teardown) -> ignore. e.Exception != null is a fault (device unplugged, driver error): the OS
    // has stopped capturing but _microphoneReady would otherwise stay true, leaving the user shown as live
    // while transmitting nothing. We only correct that flag here. We deliberately do NOT auto-reopen: we
    // can't tell "mic broke, want it back" from "unplugged on purpose, done talking", and re-opening could
    // grab a device the user didn't choose. If they want it back, unmuting or changing the device setting
    // re-queues capture through the existing QueueMicrophoneTransition paths.
    private void ProcessBassMicFrame(float[] pcm, int samples)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _micCallbacks);
        Interlocked.Add(ref _micBytes, samples * 4);
        if (Mute)
        {
            Interlocked.Increment(ref _micMutedDrops);
            return;
        }
        if (samples <= 0) return;

        var epoch = Volatile.Read(ref _captureEpoch);
        lock (_captureFrameSync)
        {
            if (epoch != _captureEpoch) return;
            try
            {
                var gain = _micVolume;
                if (gain != 1f)
                    for (var i = 0; i < samples; i++)
                        pcm[i] *= gain;
                Interlocked.Add(ref _micSamples, samples);
                ProcessMicrophoneCaptureSamples(pcm, samples);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _micEncodeFailures);
                VoiceDiagnostics.Log("bcl.mic.capture_error", $"source=bass error=\"{ex.Message}\"");
            }
        }
    }
#endif


    private int ConvertIeeeFloat32ToMonoFloat(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        var frames = recordedBytes / (sizeof(float) * channels);
        floatPcm = EnsureMicConvertCapacity(frames);
        var dominantChannel = SelectDominantIeeeFloat32Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * sizeof(float);
            floatPcm[frame] = ReadIeeeFloat32Sample(buffer, offset) * _micVolume;
        }
        return frames;
    }

    private int ConvertPcm16ToMonoFloat(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        var frames = recordedBytes / (sizeof(short) * channels);
        floatPcm = EnsureMicConvertCapacity(frames);
        var dominantChannel = SelectDominantPcm16Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * sizeof(short);
            var sample = BitConverter.ToInt16(buffer, offset) / (float)short.MaxValue;
            floatPcm[frame] = sample * _micVolume;
        }
        return frames;
    }

    private int ConvertPcm24ToMonoFloat(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        const int bytesPerSample = 3;
        var frames = recordedBytes / (bytesPerSample * channels);
        floatPcm = EnsureMicConvertCapacity(frames);
        var dominantChannel = SelectDominantPcm24Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * bytesPerSample;
            var sample = ReadPcm24Sample(buffer, offset);
            floatPcm[frame] = sample * _micVolume;
        }
        return frames;
    }

    private int ConvertPcm32ToMonoFloat(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        var frames = recordedBytes / (sizeof(int) * channels);
        floatPcm = EnsureMicConvertCapacity(frames);
        var dominantChannel = SelectDominantPcm32Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * sizeof(int);
            var sample = BitConverter.ToInt32(buffer, offset) / 2147483648f;
            floatPcm[frame] = sample * _micVolume;
        }
        return frames;
    }

    private const double MicChannelSwitchEnergyRatio = 2.0;
    private const int MicChannelSwitchStreakCallbacks = 25;
    private int _latchedMicChannel;
    private int _micChannelSwitchStreak;
    private double[] _micChannelEnergyScratch = Array.Empty<double>();

    private double[] EnsureMicChannelEnergyCapacity(int channels)
    {
        if (_micChannelEnergyScratch is null || _micChannelEnergyScratch.Length < channels)
            _micChannelEnergyScratch = new double[channels];
        return _micChannelEnergyScratch;
    }

    private int LatchDominantChannel(double[] energies, int channels)
    {
        if (_latchedMicChannel >= channels)
        {
            _latchedMicChannel = 0;
            _micChannelSwitchStreak = 0;
        }

        var bestChannel = 0;
        var bestEnergy = 0.0;
        for (var channel = 0; channel < channels; channel++)
        {
            if (energies[channel] > bestEnergy)
            {
                bestEnergy = energies[channel];
                bestChannel = channel;
            }
        }

        if (bestChannel == _latchedMicChannel)
        {
            _micChannelSwitchStreak = 0;
            return _latchedMicChannel;
        }

        if (bestEnergy > energies[_latchedMicChannel] * MicChannelSwitchEnergyRatio)
        {
            if (++_micChannelSwitchStreak >= MicChannelSwitchStreakCallbacks)
            {
                VoiceDiagnostics.Log("bcl.mic.channel-latch",
                    $"from={_latchedMicChannel} to={bestChannel} energy={bestEnergy:0.000000} latchedEnergy={energies[_latchedMicChannel]:0.000000}");
                _latchedMicChannel = bestChannel;
                _micChannelSwitchStreak = 0;
            }
        }
        else
        {
            _micChannelSwitchStreak = 0;
        }

        return _latchedMicChannel;
    }

    private int SelectDominantIeeeFloat32Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var energies = EnsureMicChannelEnergyCapacity(channels);
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = (frame * channels + channel) * sizeof(float);
                var sample = ReadIeeeFloat32Sample(buffer, offset);
                energy += (double)sample * sample;
            }
            energies[channel] = energy;
        }
        return LatchDominantChannel(energies, channels);
    }

    private int SelectDominantPcm16Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var energies = EnsureMicChannelEnergyCapacity(channels);
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = (frame * channels + channel) * sizeof(short);
                var sample = BitConverter.ToInt16(buffer, offset) / (float)short.MaxValue;
                energy += (double)sample * sample;
            }
            energies[channel] = energy;
        }
        return LatchDominantChannel(energies, channels);
    }

    private int SelectDominantPcm24Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var energies = EnsureMicChannelEnergyCapacity(channels);
        const int bytesPerSample = 3;
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = (frame * channels + channel) * bytesPerSample;
                var sample = ReadPcm24Sample(buffer, offset);
                energy += (double)sample * sample;
            }
            energies[channel] = energy;
        }
        return LatchDominantChannel(energies, channels);
    }

    private int SelectDominantPcm32Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var energies = EnsureMicChannelEnergyCapacity(channels);
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = (frame * channels + channel) * sizeof(int);
                var sample = BitConverter.ToInt32(buffer, offset) / 2147483648f;
                energy += (double)sample * sample;
            }
            energies[channel] = energy;
        }
        return LatchDominantChannel(energies, channels);
    }

    private static float ReadIeeeFloat32Sample(byte[] buffer, int offset)
    {
        var sample = BitConverter.ToSingle(buffer, offset);
        if (float.IsNaN(sample)) return 0f;
        return Math.Clamp(sample, -1f, 1f);
    }

    private static float ReadPcm24Sample(byte[] buffer, int offset)
    {
        var sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
        return sample / 8388608f;
    }

    // Reused encode scratch; all writes occur under _captureFrameSync and never escape.
    private readonly short[] _encodePcm = new short[AudioHelpers.FrameSize];
    private readonly byte[] _encodeScratch = new byte[1024];
    // Reused open-channel list for the send loop. Only used inside SendFramedToChannels, which runs on the
    // single send-pacer timer thread, so it is effectively single-threaded; holds channel references (not
    // audio data), so no aliasing concern. Replaces the per-frame SnapshotOpenChannels LINQ array.
#if !ANDROID
    private readonly List<RTCDataChannel> _openChannelScratch = new();
#endif
    // Paces encoded frames out at a steady ~20 ms cadence (up to 300 ms buffered) so a local stall can't clump our send.
    private readonly VoiceSendPacer _sendPacer;

    // Reused mono-downmix buffer for mic capture conversion. Only touched on the single NAudio capture
    // thread (OnMicrophoneData), and its contents are copied into the reused _captureFrameBuffer by
    // ProcessMicrophoneCaptureSamples (which bounds reads by the sample count), so reuse is safe and the
    // per-callback float[] allocation is eliminated. Grows on demand, never shrinks.
    private float[] _micConvertScratch = Array.Empty<float>();

    private float[] EnsureMicConvertCapacity(int frames)
    {
        if (_micConvertScratch is null || _micConvertScratch.Length < frames)
            _micConvertScratch = new float[frames];
        return _micConvertScratch;
    }

    private void ProcessMicrophoneCaptureSamples(float[] floatPcm, int samples)
    {
        if (_disposed || samples <= 0 || floatPcm.Length == 0) return;

        lock (_captureFrameSync)
        {
            samples = Math.Min(samples, floatPcm.Length);
            var offset = 0;
            while (offset < samples)
            {
                var copy = Math.Min(AudioHelpers.FrameSize - _captureFrameSamples, samples - offset);
                Array.Copy(floatPcm, offset, _captureFrameBuffer, _captureFrameSamples, copy);
                _captureFrameSamples += copy;
                offset += copy;

                if (_captureFrameSamples != AudioHelpers.FrameSize) continue;

                // Encode from the reusable capture buffer; refilled from index 0 next.
                _captureFrameSamples = 0;
                ProcessMicrophoneFrameLocked(_captureFrameBuffer, AudioHelpers.FrameSize, "capture");
            }
        }
    }

    private void ProcessMicrophoneFrame(float[] floatPcm, int samples, string source)
    {
        if (_disposed || samples <= 0 || floatPcm.Length == 0) return;

        lock (_captureFrameSync)
            ProcessMicrophoneFrameLocked(floatPcm, samples, source);
    }

    private void ProcessMicrophoneFrameLocked(float[] floatPcm, int samples, string source)
    {
        if (_disposed || samples <= 0 || floatPcm.Length == 0) return;

        samples = Math.Min(samples, floatPcm.Length);
        if (samples != AudioHelpers.FrameSize)
        {
            Interlocked.Increment(ref _micEncodeFailures);
            VoiceDiagnostics.Log("bcl.mic.encode_error", $"source={source} samples={samples} expected={AudioHelpers.FrameSize} error=\"invalid-opus-frame-size\"");
            return;
        }

        MaybeAdaptEncoderLocked();
        // Snapshot the runtime options once so every per-frame read is consistent: the struct is swapped
        // wholesale from the settings thread, so re-reading the field could mix old/new fields mid-frame.
        var captureOptions = _captureOptions;

        var rawCapturePeak = AudioHelpers.MeasurePeak(floatPcm, samples);

        float agcGain = 1f;
        float preSuppressionPeak = rawCapturePeak;

        var preSuppressionGuardGain = AudioHelpers.GetCaptureEncodeLimiterGain(preSuppressionPeak);
        if (preSuppressionGuardGain < 1f)
        {
            AudioHelpers.ApplyGain(floatPcm, samples, preSuppressionGuardGain);
            preSuppressionPeak *= preSuppressionGuardGain;
        }

        if (!IsSyntheticSource(source))
            TrackCaptureHealthLocked(rawCapturePeak);

        var transmitGain = _micPreprocessor.LimitFramePeakForEncode(floatPcm, samples);
        var max = 0f;
        double squareSum = 0.0;
        var nonZeroSamples = 0;
        var nearClipSamples = 0;
        var zeroCrossings = 0;
        var previousSign = 0;
        for (var i = 0; i < samples; i++)
        {
            var scaled = Math.Clamp(floatPcm[i], -1f, 1f);
            floatPcm[i] = scaled;
            var abs = Math.Abs(scaled);
            max = Math.Max(max, abs);
            squareSum += (double)(scaled * short.MaxValue) * (scaled * short.MaxValue);
            if (scaled != 0f) nonZeroSamples++;
            if (abs >= 0.98f) nearClipSamples++;
            var sign = scaled > 0f ? 1 : scaled < 0f ? -1 : 0;
            if (sign == 0) continue;
            if (previousSign != 0 && sign != previousSign) zeroCrossings++;
            previousSign = sign;
        }

        _localLevel = Math.Max(0f, _localLevel - samples / (float)AudioHelpers.ClockRate * 0.5f);
        if (max > _localLevel) _localLevel = max;
        var speakingThreshold = Math.Max(0.0001f, _vadThreshold);
        _localSpeaking = _localLevel >= speakingThreshold;

        lock (_micStatsLock)
        {
            _micPeakSinceStats = Math.Max(_micPeakSinceStats, max);
            _micSquareSumSinceStats += squareSum;
            _micSamplesSinceStats += samples;
            _micNonZeroSamplesSinceStats += nonZeroSamples;
            if (max <= 0.000001f) _micSilentCallbacksSinceStats++;
            _micNearClipSamplesSinceStats += nearClipSamples;
            _micZeroCrossingsSinceStats += zeroCrossings;
        }

        var decision = _micPreprocessor.PrepareFrameForEncode(floatPcm, samples, _noiseGateThreshold, _vadThreshold, preSuppressionPeak);
        _lastGateReason = decision.Reason;
        _lastGatePeak = decision.Peak;
        _lastGateRms = decision.Rms;
        _lastGateThreshold = decision.Threshold;

        if (captureOptions.MicCalibrationDiagnostics)
            MaybeLogMicCalibration(source, decision, false, speakingThreshold, nearClipSamples, zeroCrossings, samples, agcGain);

        var frameTimestamp = _sendTimestamp;
        unchecked { _sendTimestamp += (uint)samples; }

        // Silence suppression: skip encode+send when gated. _sendSequence advances only at Wrap,
        // so resumed packets stay sequence-contiguous and won't trigger spurious PLC on receivers.
        if (!decision.ShouldTransmit)
            return;

        var transmitPeak = 0f;
        double transmitSquareSum = 0.0;
        var pcm = _encodePcm;
        for (var i = 0; i < samples; i++)
        {
            var scaled = Math.Clamp(floatPcm[i], -1f, 1f);
            var pcmSample = (short)MathF.Round(scaled * short.MaxValue);
            pcm[i] = pcmSample;
            transmitSquareSum += (double)(scaled * short.MaxValue) * (scaled * short.MaxValue);
            var abs = Math.Abs(scaled);
            if (abs > transmitPeak) transmitPeak = abs;
        }
        _lastTransmitGain = transmitGain;
        _lastTransmitPeak = transmitPeak;

        var packet = _encodeScratch;
        int encoded;
        try
        {
#pragma warning disable CS0618
            encoded = _encoder.Encode(pcm, 0, samples, packet, 0, packet.Length);
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _micEncodeFailures);
            VoiceDiagnostics.Log("bcl.mic.encode_error", $"source={source} samples={samples} error=\"{ex.Message}\"");
            return;
        }
        if (encoded <= 0)
        {
            Interlocked.Increment(ref _micEncodeFailures);
            VoiceDiagnostics.Log("bcl.mic.encode_error", $"source={source} samples={samples} encoded={encoded} error=\"empty-opus-packet\"");
            return;
        }
        Interlocked.Increment(ref _micEncodedFrames);
        lock (_micStatsLock)
        {
            _opusBytesSinceStats += encoded;
            _opusFramesSinceStats++;
            _opusMinBytesSinceStats = _opusMinBytesSinceStats == 0 ? encoded : Math.Min(_opusMinBytesSinceStats, encoded);
            _opusMaxBytesSinceStats = Math.Max(_opusMaxBytesSinceStats, encoded);
            _txPeakSinceStats = Math.Max(_txPeakSinceStats, transmitPeak);
            _txSquareSumSinceStats += transmitSquareSum;
            _txSamplesSinceStats += samples;
        }
        var voiceFlags = VoicePacketFlags.None;
        if (VoiceTeamRadioChannels.IsActive(_lastLocalRadioChannel)) voiceFlags |= VoicePacketFlags.Radio;
        if (PerfectCommsOpusUseInbandFec) voiceFlags |= VoicePacketFlags.LossResistant;
        if (IsSyntheticSource(source)) voiceFlags |= VoicePacketFlags.Synthetic;
        // Wrap directly from the reusable encode scratch (first `encoded` bytes), skipping the old
        // intermediate trimmed byte[] copy. The framed buffer itself stays a fresh per-frame allocation
        // because channel.send queues it for asynchronous SCTP transmission (reusing it would corrupt
        // in-flight data).
        var framed = VoicePacket.Wrap(packet, encoded, _sendSequence++, frameTimestamp, (ushort)samples, voiceFlags, VoicePacket.QuantizeLevel(transmitPeak));
        _sendPacer.Enqueue(framed);
    }

#if !ANDROID
    private void SendFramedToChannels(byte[] framed)
    {
        var sent = false;
        SnapshotOpenChannelsInto(_openChannelScratch);
        foreach (var channel in _openChannelScratch)
        {
            try
            {
                channel.send(framed);
                sent = true;
                Interlocked.Increment(ref _encodedTx);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("bcl.mic.send_error", $"bytes={framed.Length} error=\"{ex.Message}\"");
            }
        }
        if (!sent) Interlocked.Increment(ref _micNoOpenChannelDrops);
    }
#endif

    private static bool IsSyntheticSource(string source)
        => string.Equals(source, "synthetic", StringComparison.OrdinalIgnoreCase);

    private void MaybeLogMicCalibration(string source, MicFrameDecision decision, bool bypassGate, float speakingThreshold, int nearClipSamples, int zeroCrossings, int samples, float agcGain)
    {
        var now = DateTime.UtcNow;
        if (now - _lastMicCalibrationLogUtc < MicCalibrationLogInterval) return;
        _lastMicCalibrationLogUtc = now;
        var zeroCrossRate = samples <= 1 ? 0f : zeroCrossings / (float)(samples - 1);
        var crest = decision.Rms <= 0f ? 0f : decision.Peak / decision.Rms;
        VoiceDiagnostics.Log("bcl.mic.calibration",
            $"source={source} peak={decision.Peak:0.000000} rms={decision.Rms:0.000000} crest={crest:0.00} nearClipSamples={nearClipSamples} zeroCrossRate={zeroCrossRate:0.0000} gateThreshold={decision.Threshold:0.000000} vadThreshold={_vadThreshold:0.000000} effectiveSpeakingThreshold={speakingThreshold:0.000000} agcGain={agcGain:0.00} reason={decision.Reason} bypass={bypassGate} syntheticFrames={Volatile.Read(ref _syntheticFrames)}");
    }

    private PeerConnection[] SnapshotPeers()
    {
        lock (_peerSync)
            return _peersBySocket.Values.ToArray();
    }

    // Allocation-free snapshot for the per-frame Update loop: refills a caller-owned reusable buffer under
    // _peerSync instead of allocating a new array. Same concurrency semantics as SnapshotPeers (a stable
    // copy taken under the lock, then iterated outside it).
    private void SnapshotPeersInto(List<PeerConnection> buffer)
    {
        buffer.Clear();
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
                buffer.Add(peer);
        }
    }

    private string[] SnapshotMappedSocketIds()
    {
        // Every mapped socket, not just the newest per client: _clientToSocket.Values keeps only the latest
        // socket per client, so a stale duplicate socket could never be reached by the setClients prune and
        // would leak. Snapshotting the socket keys lets the prune reclaim any socket the server dropped.
        lock (_peerSync)
            return _socketToClient.Keys.ToArray();
    }

    // Fill a caller-owned reusable buffer with the currently-open data channels instead of allocating a
    // LINQ Select/Where/Cast/ToArray per transmitted 20 ms voice frame.
#if !ANDROID
    private void SnapshotOpenChannelsInto(List<RTCDataChannel> buffer)
    {
        buffer.Clear();
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
            {
                var channel = peer.DataChannel;
                if (channel != null && channel.readyState == RTCDataChannelState.open)
                    buffer.Add(channel);
            }
        }
    }
#endif

    private void RemovePeer(string socketId)
    {
#if WINDOWS
        _voice?.RemovePeer(socketId);
#endif
        PeerConnection? peer;
        lock (_peerSync)
        {
            _peersBySocket.Remove(socketId, out peer);
            // Only drop the client->socket mapping if it still points at THIS socket. After a client
            // re-homes, _clientToSocket[clientId] already points at the new socket, so reclaiming the
            // superseded socket must not clobber the live mapping. Always clear the socket-side maps even
            // when no peer was attached yet, so a mapped-but-peerless socket can't leak a stale entry.
            if (_socketToClient.Remove(socketId, out var clientId)
                && _clientToSocket.TryGetValue(clientId, out var mappedSocket)
                && string.Equals(mappedSocket, socketId, StringComparison.Ordinal))
                _clientToSocket.Remove(clientId);
            _pendingSignalsBySocket.Remove(socketId);
            // Evict this socket's signal-reject-log timestamps. Without this, _lastSignalRejectLogUtc
            // (keyed "socketId:reason") grew without bound across a session as peers connect/disconnect,
            // slowly raising the GC heap and worsening lag spikes the longer a lobby stayed up.
            RemoveSignalRejectLogEntriesLocked(socketId);
        }
        if (peer == null) return;
        peer.Dispose();
        VoiceDiagnostics.Log("bcl.peer-disconnected", $"socket={socketId} client={peer.ClientId}");
    }

    private void ClearPeers()
    {
        PeerConnection[] peers;
        lock (_peerSync)
        {
            peers = _peersBySocket.Values.ToArray();
            _peersBySocket.Clear();
            _clientToSocket.Clear();
            _socketToClient.Clear();
            _pendingSignalsBySocket.Clear();
            _lastSignalRejectLogUtc.Clear();
        }
        foreach (var peer in peers)
        {
            peer.Dispose();
            VoiceDiagnostics.Log("bcl.peer-disconnected", $"socket={peer.SocketId} client={peer.ClientId}");
        }
    }

    private void MaybeLogStats(VoiceGameStateSnapshot? snapshot, string reason)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastStatsLogUtc).TotalSeconds < 5) return;
        _lastStatsLogUtc = now;
        var peers = SnapshotPeers();
        var diagnostics = peers.Select(peer => peer.ConsumeDiagnostics()).ToArray();
        var peerTicks = diagnostics.Sum(item => item.Samples);
        var audibleTicks = diagnostics.Sum(item => item.AudibleSamples);
        var audibleSilentTicks = diagnostics.Sum(item => item.AudibleSilentSamples);
        var silentPct = audibleTicks == 0 ? 0f : audibleSilentTicks * 100f / audibleTicks;
        var remoteMax = diagnostics.Length == 0 ? 0f : diagnostics.Max(item => item.LevelPeak);
        var peerWindows = diagnostics.Length == 0 ? "none" : string.Join("|", diagnostics.Select(item => item.ToCompactString()));
        var peerJitter = diagnostics.Length == 0 ? "none" : string.Join("|", diagnostics.Select(item => item.Jitter.ToCompactString()));
        var peerBuffers = diagnostics.Length == 0 ? "none" : string.Join("|", diagnostics.Select(item => $"{item.ClientId}:{item.BufferStats}"));
        var openChannels = peers.Count(peer => peer.DataChannel?.readyState == RTCDataChannelState.open);
        // P2 observability: per-peer transport state so an in-game capture can tell a CHANNEL DEFICIT
        // (transport: dc!=open) apart from a ROUTE MUTE (audible=false). Compact: client:dc/conn/ice.
        var peerTransport = peers.Length == 0
            ? "none"
            : string.Join("|", peers.Select(peer =>
                $"{peer.ClientId}:{(peer.DataChannel?.readyState.ToString() ?? "none")}/{(peer.Connection?.connectionState.ToString() ?? "none")}/{(peer.Connection?.iceConnectionState.ToString() ?? "none")}"));
        float micPeak;
        double micRms;
        int micWindowSamples;
        int micNonZeroSamples;
        int micSilentCallbacks;
        int micNearClipSamples;
        int micZeroCrossings;
        int opusBytes;
        int opusFrames;
        int opusMinBytes;
        int opusMaxBytes;
        float txPeakMax;
        double txRms;
        int txSamples;
        float noiseGateThreshold = _noiseGateThreshold;
        float vadThreshold = _vadThreshold;
        lock (_micStatsLock)
        {
            micPeak = _micPeakSinceStats;
            micWindowSamples = _micSamplesSinceStats;
            micNonZeroSamples = _micNonZeroSamplesSinceStats;
            micSilentCallbacks = _micSilentCallbacksSinceStats;
            micNearClipSamples = _micNearClipSamplesSinceStats;
            micZeroCrossings = _micZeroCrossingsSinceStats;
            opusBytes = _opusBytesSinceStats;
            opusFrames = _opusFramesSinceStats;
            opusMinBytes = _opusFramesSinceStats == 0 ? 0 : _opusMinBytesSinceStats;
            opusMaxBytes = _opusMaxBytesSinceStats;
            txPeakMax = _txPeakSinceStats;
            txSamples = _txSamplesSinceStats;
            txRms = txSamples == 0 ? 0.0 : Math.Sqrt(_txSquareSumSinceStats / txSamples) / short.MaxValue;
            micRms = micWindowSamples == 0 ? 0.0 : Math.Sqrt(_micSquareSumSinceStats / micWindowSamples) / short.MaxValue;
            _micPeakSinceStats = 0f;
            _micSquareSumSinceStats = 0.0;
            _micSamplesSinceStats = 0;
            _micNonZeroSamplesSinceStats = 0;
            _micSilentCallbacksSinceStats = 0;
            _micNearClipSamplesSinceStats = 0;
            _micZeroCrossingsSinceStats = 0;
            _opusBytesSinceStats = 0;
            _opusFramesSinceStats = 0;
            _opusMinBytesSinceStats = 0;
            _opusMaxBytesSinceStats = 0;
            _txPeakSinceStats = 0f;
            _txSquareSumSinceStats = 0.0;
            _txSamplesSinceStats = 0;
        }
#if WINDOWS
        FeedCaptureSupervisor(micWindowSamples);
#endif
        var micClipPct = micWindowSamples == 0 ? 0f : micNearClipSamples * 100f / micWindowSamples;
        var micZeroCrossRate = micWindowSamples <= 1 ? 0f : micZeroCrossings / (float)(micWindowSamples - 1);
        var micCrest = micRms <= 0.0 ? 0.0 : micPeak / micRms;
        var opusAvgBytes = opusFrames == 0 ? 0.0 : opusBytes / (double)opusFrames;
        VoiceDiagnostics.Log("bcl.stats",
            $"reason={reason} room={RoomCode} region={Region} endpoint={ServerUrl} phase={snapshot?.Phase.ToString() ?? "none"} socketConnected={_socket?.Connected == true} socketId={_socket?.Id ?? "none"} " +
            $"peers={peers.Length} openChannels={openChannels} peerTransport={peerTransport} localSocket={GetEffectiveLocalSocketId()} joinedClient={_joinedClientId} joinInFlight={Volatile.Read(ref _joinInFlight)} joinRetryAgeMs={(DateTime.UtcNow - _lastJoinAttemptUtc).TotalMilliseconds:0} audible={peers.Count(peer => peer.CurrentRoute.Audible)} speaking={peers.Count(peer => peer.IsSpeaking)} " +
            $"localLevel={LocalLevel:0.000} localSpeaking={LocalSpeaking} mute={Mute} remoteLevelMax={remoteMax:0.000} " +
            $"audibleTicks={audibleTicks} audibleSilentTicks={audibleSilentTicks} silentPct={silentPct:0.0} peerWindows={peerWindows} peerJitter={peerJitter} peerBuffers={peerBuffers} " +
            $"encodedTx={Volatile.Read(ref _encodedTx)} encodedRx={Volatile.Read(ref _encodedRx)} customTx={Volatile.Read(ref _customTx)} customRx={Volatile.Read(ref _customRx)} " +
            $"micCallbacks={Volatile.Read(ref _micCallbacks)} micBytes={Volatile.Read(ref _micBytes)} micSamples={Volatile.Read(ref _micSamples)} micWindowSamples={micWindowSamples} micPeak={micPeak:0.000000} micRms={micRms:0.000000} micCrest={micCrest:0.00} micNonZeroSamples={micNonZeroSamples} micSilentCallbacks={micSilentCallbacks} micNearClipSamples={micNearClipSamples} micClipPct={micClipPct:0.000} micZeroCrossRate={micZeroCrossRate:0.0000} " +
            $"micMutedDrops={Volatile.Read(ref _micMutedDrops)} micEncodeFailures={Volatile.Read(ref _micEncodeFailures)} micEncodedFrames={Volatile.Read(ref _micEncodedFrames)} micNoOpenChannelDrops={Volatile.Read(ref _micNoOpenChannelDrops)} audioDecodeFailures={Volatile.Read(ref _audioDecodeFailures)} " +
            $"noiseGate={noiseGateThreshold:0.000000} vadThreshold={vadThreshold:0.000000} gateReason={_lastGateReason} gatePeak={_lastGatePeak:0.000000} gateRms={_lastGateRms:0.000000} gateThreshold={_lastGateThreshold:0.000000} txGain={_lastTransmitGain:0.000} txPeak={_lastTransmitPeak:0.000000} txPeakMax={txPeakMax:0.000000} txRms={txRms:0.000000} txSamples={txSamples} opusBytesAvg={opusAvgBytes:0.0} opusBytesMin={opusMinBytes} opusBytesMax={opusMaxBytes} " +
            $"syntheticTone={_captureOptions.SyntheticMicToneEnabled} noiseSuppression={_captureOptions.NoiseSuppressionEnabled} syntheticFrames={Volatile.Read(ref _syntheticFrames)} capture={DescribeCaptureMode()} calibration={_captureOptions.MicCalibrationDiagnostics} sensitivity={_captureOptions.MicSensitivity:0.00} micReady={_microphoneReady} speakerReady={_speakerReady} plp={_adaptedPacketLossPercent} bitrate={_adaptedBitrate} recordDevice={Volatile.Read(ref _lastOpenedRecordDevice)} micDeadInput={Volatile.Read(ref _deadInputDetected)}");
        if (CaptureUsesUnity)
            VoiceDiagnostics.Log("bcl.unity",
                $"active=true mode={DescribeCaptureMode()} micReady={_microphoneReady} micCallbacks={Volatile.Read(ref _micCallbacks)} micSamples={Volatile.Read(ref _micSamples)} micPeak={micPeak:0.000000} micRms={micRms:0.000000} micSilentCallbacks={micSilentCallbacks} micEncodedFrames={Volatile.Read(ref _micEncodedFrames)} encodedTx={Volatile.Read(ref _encodedTx)} micMutedDrops={Volatile.Read(ref _micMutedDrops)} speakerReady={_speakerReady} speakerPlaying={_androidSpeaker?.IsPlaying} speakerReads={_androidSpeaker?.ReadCallbacks ?? 0}");
        VoiceDiagnostics.Log("bcl.playout", _voiceMixer?.FormatPlayoutDiagnostics() ?? "no-mixer");
    }

    private string DescribeCaptureMode()
    {
        if (_captureOptions.SyntheticMicToneEnabled) return "synthetic";
#if ANDROID
        return "android-unity-microphone";
#elif WINDOWS
        if (_voice != null) return "sidecar";
        return _androidMicrophone != null ? "unity-microphone" : "bass";
#else
        return "bass";
#endif
    }

    private int _adaptedPacketLossPercent = PerfectCommsOpusPacketLossPercent;
    private int _adaptedBitrate = PerfectCommsOpusBitrate;
    private int _framesSinceCodecAdaptCheck;

    // Capture thread under _captureFrameSync (same nesting into _peerSync as SnapshotOpenChannelsInto).
    private void MaybeAdaptEncoderLocked()
    {
        if (++_framesSinceCodecAdaptCheck < CodecAdaptIntervalFrames) return;
        _framesSinceCodecAdaptCheck = 0;

        int maxLossPermille = -1;
        var nowUtc = DateTime.UtcNow;
        lock (_peerSync)
        {
            foreach (var peer in _peersBySocket.Values)
            {
                var reported = peer.GetFreshLossReportPermille(nowUtc);
                if (reported > maxLossPermille) maxLossPermille = reported;
            }
        }

        // No fresh report = no information; keep last value or PLP flips mid-spurt after every silence gap.
        if (maxLossPermille < 0) return;

        int targetPlp = AudioHelpers.ComputeAdaptedPacketLossPercent(maxLossPermille);
        if (Math.Abs(targetPlp - _adaptedPacketLossPercent) < PlpDeadbandPercent) return;

        int targetBitrate = AudioHelpers.ComputeAdaptedBitrate(targetPlp);
        try
        {
            _encoder.PacketLossPercent = targetPlp;
            if (targetBitrate != _adaptedBitrate)
                _encoder.Bitrate = targetBitrate;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.codec.adapt", $"error=\"{ex.Message}\"");
            return;
        }
        _adaptedPacketLossPercent = targetPlp;
        _adaptedBitrate = targetBitrate;
        VoiceDiagnostics.Log("bcl.codec.adapt", $"plp={targetPlp} bitrate={targetBitrate} maxLossPermille={maxLossPermille}");
    }

    private static IVoiceEncoder CreateEncoder()
        => VoiceCodec.CreateEncoder(
            bitrate: PerfectCommsOpusBitrate,
            complexity: AudioHelpers.OpusComplexity,
            voiceSignal: true,
            vbr: true,
            constrainedVbr: PerfectCommsOpusUseConstrainedVbr,
            dtx: false,
            fec: PerfectCommsOpusUseInbandFec,
            packetLossPercent: PerfectCommsOpusPacketLossPercent);

    private static void ApplySavedVolume(PeerConnection peer)
    {
        if (VoiceVolumeMenu.TryGetSavedVolume(peer.PlayerName, out var volume)) peer.SetVolume(volume);
    }

    private static VoicePlayerSnapshot? FindTarget(VoiceGameStateSnapshot snapshot, PeerConnection peer)
    {
        int clientId = peer.ClientId;
        byte playerId = peer.PlayerId;
        if (clientId >= 0 && snapshot.TryGetClient(clientId, out var byClient)) return byClient;
        if (playerId != byte.MaxValue
            && snapshot.TryGetPlayer(playerId, out var byPlayer)
            && byPlayer.ClientId == clientId) return byPlayer;
        return null;
    }

    private static readonly Dictionary<int, DateTime> _lastCenteredLoudLogUtc = new();

    // Diagnostic for the "loud + both-sides-centered" report: a proximity- or lobby-routed peer that is loud and centered while far away or positionless.
    private static void LogCenteredLoudRoute(PeerConnection peer, VoicePlayerSnapshot? target, UnityEngine.Vector2? listenerPos, VoiceProximityResult result, VoiceGamePhase phase)
    {
        if (!VoiceDiagnostics.IsEnabled || !result.Audible) return;
        if (result.Reason != VoiceProximityReason.Proximity && result.Reason != VoiceProximityReason.Lobby) return;
        if (Math.Abs(result.Pan) > 0.10f) return;
        if (result.NormalVolume < 0.7f) return;

        var tpos = target?.Position ?? new UnityEngine.Vector2(float.NaN, float.NaN);
        var lpos = listenerPos ?? new UnityEngine.Vector2(float.NaN, float.NaN);
        float dx = tpos.x - lpos.x;
        float dy = tpos.y - lpos.y;
        float dist = (float)Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (!float.IsNaN(dist) && dist < 3.0f) return;

        var now = DateTime.UtcNow;
        if (_lastCenteredLoudLogUtc.TryGetValue(peer.ClientId, out var last) && (now - last).TotalSeconds < 1.0) return;
        _lastCenteredLoudLogUtc[peer.ClientId] = now;

        VoiceDiagnostics.Log("bcl.route.centeredloud",
            $"client={peer.ClientId} player={peer.PlayerId} name=\"{peer.PlayerName}\" phase={phase} reason={result.Reason} " +
            $"pan={result.Pan:0.000} normal={result.NormalVolume:0.000} ghost={result.GhostVolume:0.000} radio={result.RadioVolume:0.000} " +
            $"listenerPos=({lpos.x:0.00},{lpos.y:0.00}) targetPos=({tpos.x:0.00},{tpos.y:0.00}) dx={dx:0.00} dist={dist:0.00} " +
            $"targetName=\"{(target?.PlayerName ?? "<none>")}\" targetPlayerId={(target.HasValue ? target.Value.PlayerId : (byte)255)} targetClientId={(target.HasValue ? target.Value.ClientId : -1)}");
    }

    private sealed class PeerConnection : IDisposable
    {
        private readonly object _sync = new();
        private VoiceMixer? _mixer;
        public void SetMixer(VoiceMixer? mixer) => _mixer = mixer;
        private readonly VoiceJitterBuffer _jitterBuffer = new(targetDelayFrames: JitterTargetDelayFrames, maxBufferedFrames: JitterMaxBufferedFrames, minTargetDelayFrames: JitterMinTargetFrames, maxTargetDelayFrames: JitterMaxTargetFrames);
        private VoiceProximityResult _currentRoute = VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);
        private float _levelPeakSinceStats;
        private float _packetLevelPeakSinceStats;
        private float _recentVoiceLevel;
        private long _lastVoiceLevelTicks;
        private int _samplesSinceStats;
        private int _audibleSamplesSinceStats;
        private int _audibleSilentSamplesSinceStats;
        private int _routeClearsSinceStats;
        private float _appliedPan;
        private readonly Timer _tailFlushTimer;
        // Reused grow-on-demand decode scratch; all callers hold _sync, AddSamples copies synchronously.
        private short[] _decodePcm = System.Array.Empty<short>();
        // Decode-failure suppression. A peer whose packets keep failing to decode (e.g. an incompatible
        // client/codec sharing the same public room) is taken off the Opus decode path after a threshold —
        // each failure THROWS a Concentus exception — and just routed silence, re-probing on a growing
        // interval in case the stream recovers. Accessed only under _sync (the decode lock).
        private int _decodeFailures;
        private bool _decodeSuppressed;
        private DateTime _decodeReprobeUtc;
        private const int DecodeFailureSuppressThreshold = 10;
        // Debounce: only a SUSTAINED run of throws (not a transient bad frame) accrues toward suppression,
        // so a healthy peer with the occasional undecodable packet never gets the 5s cut-out.
        private int _consecutiveThrows;
        private DateTime _firstThrowUtc;
        private const int ConsecutiveThrowSuppressRun = 8;
        private static readonly TimeSpan ThrowSuppressWindow = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AudioDropLogInterval = TimeSpan.FromSeconds(5);
        private DateTime _lastAudioDropLogUtc = DateTime.MinValue;
        private int _suppressedAudioDropLogs;
        private static readonly TimeSpan RouteDivergenceLogInterval = TimeSpan.FromSeconds(2);
        private DateTime _lastRouteDivergenceLogUtc = DateTime.MinValue;
        private float[] _decodeFloat = System.Array.Empty<float>();
        private bool _disposed;

        // Retained: constructed by the test harness via reflection. Forwards clientId as the group id.
        public PeerConnection(string socketId, int clientId)
            : this(socketId, clientId, clientId)
        {
        }

        public PeerConnection(string socketId, int clientId, int playbackGroupId)
        {
            SocketId = socketId;
            ClientId = clientId;
            PlaybackGroupId = playbackGroupId;
            _tailFlushTimer = new Timer(static state => ((PeerConnection)state!).FlushBufferedVoiceFromTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _jitterBuffer.DredCapable = Decoder.SupportsDred;
            MuteAll();
        }

        private object _lrSync = new();
        public void SetInterleaveSync(object sync) => _lrSync = sync;

        public void FadeClearRoutes()
        {
        }

        private const int MinPacketsForLossReport = 25;
        private static readonly TimeSpan LossReportInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan LossReportFreshness = TimeSpan.FromSeconds(6);
        private int _reportedLossPermille = -1;
        private long _reportedLossAtTicks;
        private DateTime _lastLossReportSentUtc = DateTime.MinValue;
        private long _lastReportedAcceptedPackets;
        private long _lastReportedLostFrames;

        public void StoreLossReport(int lossPermille)
        {
            Volatile.Write(ref _reportedLossPermille, lossPermille);
            Interlocked.Exchange(ref _reportedLossAtTicks, DateTime.UtcNow.Ticks);
            if (VoiceDiagnostics.IsEnabled)
                VoiceDiagnostics.Log("bcl.lossreport.rx", $"client={ClientId} permille={lossPermille}");
        }

        public int GetFreshLossReportPermille(DateTime nowUtc)
        {
            long atTicks = Interlocked.Read(ref _reportedLossAtTicks);
            if (atTicks == 0 || nowUtc.Ticks - atTicks > LossReportFreshness.Ticks) return -1;
            return Volatile.Read(ref _reportedLossPermille);
        }

        // Main thread only (backend Update); reports this link's recent loss so the talker tunes FEC per need.
        public void MaybeSendLossReport(DateTime nowUtc)
        {
            if (nowUtc - _lastLossReportSentUtc < LossReportInterval) return;
            _lastLossReportSentUtc = nowUtc;
            long accepted = _jitterBuffer.CumulativeAcceptedPackets;
            long lost = _jitterBuffer.CumulativeLostFrames;
            long deltaAccepted = accepted - _lastReportedAcceptedPackets;
            long deltaLost = lost - _lastReportedLostFrames;
            long total = deltaAccepted + deltaLost;
            if (total < MinPacketsForLossReport) return;
            var channel = DataChannel;
            if (channel?.readyState != RTCDataChannelState.open) return;
            int lossPermille = (int)Math.Clamp(deltaLost * 1000 / total, 0, 1000);
            try
            {
                channel.send(BuildLossReportMessage(lossPermille));
                _lastReportedAcceptedPackets = accepted;
                _lastReportedLostFrames = lost;
                if (VoiceDiagnostics.IsEnabled)
                    VoiceDiagnostics.Log("bcl.lossreport.tx",
                        $"client={ClientId} permille={lossPermille} lostDelta={deltaLost} acceptedDelta={deltaAccepted}");
            }
            catch
            {
            }
        }

        private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(1);
        private long _lastKeepaliveSentTicks;
        private volatile int _consecutiveKeepaliveSendFailures;
        public int ConsecutiveKeepaliveSendFailures => _consecutiveKeepaliveSendFailures;
        private long _lastInboundTicks;
        public long LastInboundTicks => Interlocked.Read(ref _lastInboundTicks);
        public void StampInbound(long nowTicks) => Interlocked.Exchange(ref _lastInboundTicks, nowTicks);
        public void RebaseInbound(long nowTicks) => Interlocked.Exchange(ref _lastInboundTicks, nowTicks);
        public bool HasOpenedAtLeastOnce { get; set; }
        public DateTime SilentSinceLoggedUtc { get; set; } = DateTime.MinValue;

        // Channel swap (RecreatePeerConnection) reuses this object; liveness must track the NEW channel, not the dead one.
        public void ResetLiveness()
        {
            Interlocked.Exchange(ref _lastInboundTicks, 0);
            HasOpenedAtLeastOnce = false;
            _consecutiveKeepaliveSendFailures = 0;
            Interlocked.Exchange(ref _lastKeepaliveSentTicks, 0);
            SilentSinceLoggedUtc = DateTime.MinValue;
        }

        // Unconditional liveness probe: never gated by silence suppression, so a silent-but-healthy link still proves itself.
        public void MaybeSendKeepalive(DateTime nowUtc)
        {
            if (nowUtc.Ticks - Interlocked.Read(ref _lastKeepaliveSentTicks) < KeepaliveInterval.Ticks) return;
            Interlocked.Exchange(ref _lastKeepaliveSentTicks, nowUtc.Ticks);
            var channel = DataChannel;
            if (channel?.readyState != RTCDataChannelState.open) return;
            try
            {
                channel.send(KeepaliveBytes);
                _consecutiveKeepaliveSendFailures = 0;
            }
            catch
            {
                if (_consecutiveKeepaliveSendFailures < int.MaxValue) _consecutiveKeepaliveSendFailures++;
            }
        }

        public string SocketId { get; }
        public int ClientId { get; private set; }
        public int PlaybackGroupId { get; }
        public RTCPeerConnection? Connection { get; set; }
        public RTCDataChannel? DataChannel { get; set; }
        // Last remote offer's ICE ufrag. A CHANGED ufrag on a new offer means the remote recreated its
        // connection, so our 'open' channel is stale (the open-answerer split-brain sub-case) and must rebuild.
        public string LastRemoteIceUfrag { get; set; } = string.Empty;
        // Set when the initiator creates the channel; lets it detect a wedged 'connecting' handshake.
        public DateTime OfferStartedUtc { get; set; } = DateTime.MinValue;
        // Last recovery rebuild time; debounces re-offer storms.
        public DateTime LastRecoveryUtc { get; set; } = DateTime.MinValue;
        public DateTime LastConnectionRebuildUtc { get; set; } = DateTime.MinValue;
        // Exponential reconnect backoff. A peer that keeps failing to connect (e.g. genuinely unreachable even
        // via relay) backs off its offer/recovery cadence instead of retrying every 3s for the whole session.
        // Reset to zero the instant its data channel opens, so a recovered peer that later glitches retries fast.
        public int RecoveryAttempts { get; set; }
        public DateTime NextRetryUtc { get; set; } = DateTime.MinValue;
        // Receive-side watchdog (one-directional deafness signature): when the data channel is non-open
        // while the connection is alive, this records when the deficit began so the poll can proactively
        // re-request/re-offer for THIS peer after it has persisted past a threshold, regardless of role.
        // Reset to MinValue the instant the channel is observed open. Touched only on the main-thread poll.
        public DateTime ChannelDeficitSinceUtc { get; set; } = DateTime.MinValue;
#pragma warning disable CS0618
        public IVoiceDecoder Decoder { get; } = VoiceCodec.CreateDecoder();
#pragma warning restore CS0618
        private volatile byte _playerId = byte.MaxValue;
        public byte PlayerId { get => _playerId; private set => _playerId = value; }
        public string PlayerName { get; private set; } = "Unknown";
        private volatile VoiceTeamRadioChannel _radioChannel = VoiceTeamRadioChannel.None;
        // PlayerId the cached _radioChannel was applied for; a transient unmap+remap to the same
        // player keeps the channel, a genuine remap to another player drops it (see UpdateProfile).
        private volatile byte _radioChannelOwner = byte.MaxValue;
        private int _consecutiveNonRadioVoicePackets;
        private long _lastRadioChannelAppliedTicks;
        private const int RadioStaleClearPacketThreshold = 5;
        private static readonly TimeSpan RadioStaleClearActivationGrace = TimeSpan.FromMilliseconds(500);
        public bool RadioActive
        {
            get => VoiceTeamRadioChannels.IsActive(_radioChannel);
            set
            {
                if (!value) _radioChannel = VoiceTeamRadioChannel.None;
            }
        }
        public VoiceTeamRadioChannel RadioChannel
        {
            get => _radioChannel;
            set => _radioChannel = VoiceTeamRadioChannels.Normalize(value);
        }

        // Apply a channel for the current player, recording the owner so it survives a transient unmap.
        public void ApplyRadioChannel(VoiceTeamRadioChannel channel)
        {
            _radioChannel = VoiceTeamRadioChannels.Normalize(channel);
            _radioChannelOwner = PlayerId;
            Interlocked.Exchange(ref _consecutiveNonRadioVoicePackets, 0);
            if (VoiceTeamRadioChannels.IsActive(_radioChannel))
                Interlocked.Exchange(ref _lastRadioChannelAppliedTicks, DateTime.UtcNow.Ticks);
        }
        public float WallCoefficient { get; private set; } = 1f;
        public VoiceProximityResult CurrentRoute => _currentRoute;
        public bool IsSpeaking => CurrentVoiceLevel >= RemoteSpeakingThreshold;

        public bool UpdateClientId(int clientId)
        {
            if (clientId < 0 || ClientId == clientId) return false;
            ClientId = clientId;
            return true;
        }

        private float CurrentVoiceLevel
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastVoiceLevelTicks);
                if (ticks == 0 || DateTime.UtcNow - new DateTime(ticks) > RemoteActivityHold) return 0f;
                return Volatile.Read(ref _recentVoiceLevel);
            }
        }

        private void ObserveVoiceLevel(float level)
        {
            if (level <= 0f) return;
            Volatile.Write(ref _recentVoiceLevel, Math.Clamp(level, 0f, 1f));
            Interlocked.Exchange(ref _lastVoiceLevelTicks, DateTime.UtcNow.Ticks);
        }

        public bool UpdateProfile(byte playerId, string playerName)
        {
            var normalized = string.IsNullOrWhiteSpace(playerName) ? "Unknown" : playerName;
            if (PlayerId == playerId && PlayerName == normalized) return false;

            // Drop cached channel/wall coefficient only on a genuine remap to a DIFFERENT player;
            // a transient unmap+remap to the same player keeps them (avoids ~1s radio dropout).
            if (playerId != _radioChannelOwner)
            {
                _radioChannel = VoiceTeamRadioChannel.None;
                _radioChannelOwner = byte.MaxValue;
                WallCoefficient = 1f;
            }

            PlayerId = playerId;
            PlayerName = normalized;
            return true;
        }

        public void ResetMappingNoMute()
        {
            // Clear only the player mapping; the channel/wall coefficient are dropped in UpdateProfile
            // on a genuine remap, so a transient unavailability frame doesn't wipe a valid channel.
            PlayerId = byte.MaxValue;
        }
        public float ClientVolume { get; private set; } = 1f;
        public void SetVolume(float volume)
        {
            var clamped = Math.Clamp(volume, 0f, 2f);
            ClientVolume = clamped;
            _mixer?.SetClientVolume(PlaybackGroupId, clamped);
        }
        public void MuteAll()
        {
            _mixer?.SetPeer(PlaybackGroupId, 0f, _appliedPan, VoiceAudioFilterMode.None);
        }

        public void Apply(VoiceProximityResult result)
        {
            if (VoiceDiagnostics.IsEnabled && IsSpeaking
                && Math.Abs(_appliedPan) <= 0.1f && _currentRoute.NormalVolume >= 0.7f
                && ((result.Reason == VoiceProximityReason.Proximity && Math.Abs(result.Pan) > 0.25f) || result.NormalVolume <= 0.3f))
            {
                var now = DateTime.UtcNow;
                if (now - _lastRouteDivergenceLogUtc >= RouteDivergenceLogInterval)
                {
                    _lastRouteDivergenceLogUtc = now;
                    VoiceDiagnostics.Log("bcl.route.applied-divergence",
                        $"client={ClientId} player={PlayerId} reason={result.Reason} pan={result.Pan:0.000} normal={result.NormalVolume:0.000} appliedReason={_currentRoute.Reason} appliedPan={_appliedPan:0.000} appliedNormal={_currentRoute.NormalVolume:0.000}");
                }
            }
            _currentRoute = result;
            WallCoefficient = result.WallCoefficient;
            _appliedPan = result.Pan;

            var routeVolume = Math.Clamp(result.NormalVolume + result.GhostVolume + result.RadioVolume, 0f, 1f);
            _mixer?.SetPeer(PlaybackGroupId, routeVolume, result.Pan, result.FilterMode);
        }
        public void SampleDiagnostics()
        {
            var level = CurrentVoiceLevel;
            var speaking = IsSpeaking;
            _samplesSinceStats++;
            _levelPeakSinceStats = Math.Max(_levelPeakSinceStats, level);
            if (_currentRoute.Audible)
            {
                _audibleSamplesSinceStats++;
                if (!speaking) _audibleSilentSamplesSinceStats++;
            }

            var targetFrames = _jitterBuffer.CurrentTargetDelayFrames;
            if (targetFrames != _lastLoggedJitterTarget)
            {
                if (_lastLoggedJitterTarget > 0 && VoiceDiagnostics.IsEnabled)
                    VoiceDiagnostics.Log("bcl.jitter.window",
                        $"client={ClientId} target={targetFrames} prev={_lastLoggedJitterTarget} jitterMs={_jitterBuffer.CurrentJitterSamples * 1000.0 / AudioHelpers.ClockRate:0.0}");
                _lastLoggedJitterTarget = targetFrames;
            }

            if (++_lrDivergenceCheckCounter >= 30)
            {
                _lrDivergenceCheckCounter = 0;
                RealignRoutes();
            }
        }

        private int _lastLoggedJitterTarget;
        private int _lrDivergenceCheckCounter;
        private DateTime _lastLrDivergenceLogUtc = DateTime.MinValue;
        private static readonly TimeSpan LrDivergenceLogInterval = TimeSpan.FromSeconds(5);

        // The left/right per-peer rings carry the SAME mono signal and must stay frame-aligned, or a >=1-frame
        // skew decorrelates the stereo image (audio stops being directional). The per-ring trailing-PLC bridge and
        // idle-reset can add/drop a frame on one side only, so trim the longer ring back to its twin under the
        // interleave lock to re-lock playout. Runs in production, not just when diagnostics are enabled.
        private void RealignRoutes()
        {
            _ = _lastLrDivergenceLogUtc;
            _ = LrDivergenceLogInterval;
        }
        public PeerDiagnostics ConsumeDiagnostics()
        {
            lock (_sync)
            {
                var bufferStats = "bass";
                var result = new PeerDiagnostics(ClientId, _levelPeakSinceStats, _packetLevelPeakSinceStats, _samplesSinceStats, _audibleSamplesSinceStats, _audibleSilentSamplesSinceStats, _routeClearsSinceStats, _currentRoute, _appliedPan, _jitterBuffer.ConsumeStats(), bufferStats);
                _levelPeakSinceStats = 0f;
                _packetLevelPeakSinceStats = 0f;
                _samplesSinceStats = 0;
                _audibleSamplesSinceStats = 0;
                _audibleSilentSamplesSinceStats = 0;
                _routeClearsSinceStats = 0;
                return result;
            }
        }
        public VoiceRemoteOverlayState ToOverlayState()
        {
            var level = CurrentVoiceLevel;
            return new VoiceRemoteOverlayState(PlayerId, PlayerName, level, level >= RemoteSpeakingThreshold, _currentRoute.Audible, _currentRoute.Reason);
        }
#if ANDROID
        public bool TryReceiveAudioTrackOpus(byte[] payload, out string? error, out int decodedFrames)
        {
            lock (_sync)
            {
                error = null;
                decodedFrames = 0;
                if (_disposed) return false;
                return DecodeLegacyPacket(payload, out error, out decodedFrames);
            }
        }
#endif

        public bool TryReceiveVoicePacket(byte[] data, out string? error, out int decodedFrames)
        {
            lock (_sync)
            {
                error = null;
                decodedFrames = 0;
                if (_disposed) return false;

                if (!VoicePacket.HasMagic(data))
                    return DecodeLegacyPacket(data, out error, out decodedFrames);

                if (!VoicePacket.TryRead(data, out var packet))
                {
                    // Magic matched but header parse failed; do NOT legacy-decode (would feed header
                    // bytes to Opus as audio). Drop as a parse error.
                    error = "invalid-bcl-packet";
                    return false;
                }

                // Radio channel/active state is governed solely by the validated PCRD control packet
                // (ApplyRadioChannel) and the reliable radio-state RPC. Do NOT derive it from the
                // per-packet audio Radio flag: the true case is a no-op while the false case cleared
                // _radioChannel, so a reordered pre-radio audio frame arriving after a PCRD would wipe
                // the freshly validated channel and briefly drop the speaker off team radio.
                if ((packet.Flags & VoicePacketFlags.Radio) != 0)
                    Interlocked.Exchange(ref _consecutiveNonRadioVoicePackets, 0);
                else if (RadioActive
                         && DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastRadioChannelAppliedTicks)) > RadioStaleClearActivationGrace
                         && Interlocked.Increment(ref _consecutiveNonRadioVoicePackets) >= RadioStaleClearPacketThreshold)
                {
                    var staleChannel = _radioChannel;
                    ApplyRadioChannel(VoiceTeamRadioChannel.None);
                    VoiceDiagnostics.Log("bcl.radio.stale-cleared", $"client={ClientId} player={PlayerId} channel={staleChannel} packets={RadioStaleClearPacketThreshold}");
                }
                var packetLevel = packet.Level / (float)byte.MaxValue;
                _packetLevelPeakSinceStats = Math.Max(_packetLevelPeakSinceStats, packetLevel);
                ObserveVoiceLevel(packetLevel);

                var frames = _jitterBuffer.Enqueue(packet);
                ScheduleTailFlushLocked();
                if (frames.Count == 0) return true;

                for (int i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];
                    var payload = frame.Packet?.Payload ?? Array.Empty<byte>();
                    var isDred = frame.Kind == VoicePlayoutKind.Dred;
                    var decodeFec = frame.Kind == VoicePlayoutKind.Fec;
                    // FEC/DRED must decode at the missing frame's duration (carried on frame.Duration), not a fixed size (PL1).
                    var frameSize = NormalizeOpusFrameSize(Math.Max(AudioHelpers.FrameSize, (int)frame.Duration));
                    // A single bad frame must NOT abandon the rest of the drained batch (up to
                    // MaxDrainFramesPerPacket frames). DecodeAndAddSamples conceals the failed slot with
                    // silence, so surface the first error for telemetry and keep draining the successors.
                    if (DecodeAndAddSamples(payload, false, decodeFec, frameSize, out var frameError, out var decoded, isDred ? frame.DredOffset : -1))
                        decodedFrames += decoded > 0 ? 1 : 0;
                    else if (string.IsNullOrEmpty(error))
                        error = frameError;
                }

                return true;
            }
        }

        public bool TryFlushBufferedVoice(out string? error, out int decodedFrames)
        {
            lock (_sync)
            {
                error = null;
                decodedFrames = 0;
                if (_disposed) return false;

                var frames = _jitterBuffer.DrainDue(DateTime.UtcNow, QuietTailFlushDelay);
                for (int i = 0; i < frames.Count; i++)
                {
                    var frame = frames[i];
                    if (frame.Kind == VoicePlayoutKind.Plc)
                    {
                        RouteSilence(NormalizeOpusFrameSize(Math.Max(AudioHelpers.FrameSize, (int)frame.Duration)));
                        continue;
                    }
                    var payload = frame.Packet?.Payload ?? Array.Empty<byte>();
                    var isDred = frame.Kind == VoicePlayoutKind.Dred;
                    var decodeFec = frame.Kind == VoicePlayoutKind.Fec;
                    // FEC/DRED must decode at the missing frame's duration (carried on frame.Duration), not a fixed size (PL1).
                    var frameSize = NormalizeOpusFrameSize(Math.Max(AudioHelpers.FrameSize, (int)frame.Duration));
                    // Conceal a failed slot and keep draining instead of abandoning the rest of the tail batch.
                    if (DecodeAndAddSamples(payload, false, decodeFec, frameSize, out var frameError, out var decoded, isDred ? frame.DredOffset : -1))
                        decodedFrames += decoded > 0 ? 1 : 0;
                    else if (string.IsNullOrEmpty(error))
                        error = frameError;
                }

                // A capped forced drain can leave tail frames behind; re-arm so they flush instead of stranding.
                if (frames.Count > 0 && _jitterBuffer.HasBufferedPackets)
                {
                    ScheduleTailFlushLocked();
                    if (VoiceDiagnostics.IsEnabled)
                        VoiceDiagnostics.Log("bcl.audio.tailflush.rearm", $"client={ClientId} drained={frames.Count}");
                }

                return true;
            }
        }

        // Opus only accepts exact frame durations (2.5/5/10/20/40/60 ms => 120/240/480/960/1920/2880
        // samples @ 48 kHz). This build's encoder is a strict 20 ms framer (960), so this is a no-op for
        // all real traffic; it only hardens the decoder against a non-conformant peer whose advertised
        // duration isn't a valid Opus size (e.g. > 2880), which would otherwise reach Opus as an invalid
        // frame_size. Clamps down to the nearest valid size.
        private static readonly int[] OpusFrameSizes = { 120, 240, 480, 960, 1920, 2880 };
        private static int NormalizeOpusFrameSize(int frameSize)
        {
            int best = OpusFrameSizes[0];
            foreach (var size in OpusFrameSizes)
            {
                if (size <= frameSize) best = size;
                else break;
            }
            return best;
        }

        private void ScheduleTailFlushLocked()
        {
            if (_disposed) return;
            try { _tailFlushTimer.Change(QuietTailFlushTimerDelay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        private void FlushBufferedVoiceFromTimer()
        {
            TryFlushBufferedVoice(out var error, out _);
            if (!string.IsNullOrEmpty(error) && ShouldLogAudioDrop(out var suppressed))
                VoiceDiagnostics.Log("bcl.audio.drop", $"client={ClientId} error=\"{error}\" source=tail-timer suppressed={suppressed}");
        }

        public bool ShouldLogAudioDrop(out int suppressed)
        {
            lock (_sync)
            {
                var now = DateTime.UtcNow;
                if (_lastAudioDropLogUtc != DateTime.MinValue && now - _lastAudioDropLogUtc < AudioDropLogInterval)
                {
                    _suppressedAudioDropLogs++;
                    suppressed = 0;
                    return false;
                }
                suppressed = _suppressedAudioDropLogs;
                _suppressedAudioDropLogs = 0;
                _lastAudioDropLogUtc = now;
                return true;
            }
        }

        // < this many bytes cannot be a useful legacy Opus frame. Empty / sub-minimal datagrams (foreign,
        // non-Opus traffic sharing a peer's data channel) make Concentus THROW per packet, so conceal the
        // slot before it ever reaches the decoder instead of paying a stack-trace per bad packet.
        private const int MinLegacyOpusBytes = 3;

        private bool DecodeLegacyPacket(byte[] data, out string? error, out int decodedFrames)
        {
            _jitterBuffer.CountLegacyPacket();
            if (data.Length < MinLegacyOpusBytes)
            {
                error = data.Length == 0 ? "legacy-empty" : "legacy-too-small";
                RouteSilence(AudioHelpers.FrameSize); // keep the playout timeline aligned; deliberately NOT a decode failure
                decodedFrames = 0;
                return false;
            }
            return DecodeAndAddSamples(data, isLegacy: true, decodeFec: false, AudioHelpers.FrameSize, out error, out decodedFrames);
        }

        // 120 ms @ 48 kHz mono — the maximum Opus frame size. Decoding real packets into this much output
        // space (and passing it as frame_size) means a non-conformant peer that sends a 40/60 ms frame or
        // under-stamps its duration can never trip OPUS_BUFFER_TOO_SMALL / IndexOutOfRange inside Concentus.
        private const int MaxDecodeCapacitySamples = 5760;

        // Suppress a peer that keeps failing to decode: after the threshold, stop calling Opus.Decode (which
        // throws per bad packet) and route silence; re-probe on a growing 5s..60s interval. Returns true while
        // suppressed (silence routed, no decode, no log).
        private bool TryHandleSuppressedDecode(int frameSize)
        {
            if (!_decodeSuppressed || DateTime.UtcNow >= _decodeReprobeUtc) return false;
            RouteSilence(frameSize);
            return true;
        }

        private void NoteDecodeFailure()
        {
            _decodeFailures++;
            if (_decodeFailures < DecodeFailureSuppressThreshold) return;
            bool wasSuppressed = _decodeSuppressed;
            _decodeSuppressed = true;
            // Grow the re-probe interval (5s, 10s, 20s, 40s, 60s cap) so a permanently-incompatible peer is
            // probed rarely while a transient glitch still recovers within a few seconds.
            int over = _decodeFailures - DecodeFailureSuppressThreshold;
            double sec = Math.Min(60.0, 5.0 * Math.Pow(2, Math.Min(over, 4)));
            _decodeReprobeUtc = DateTime.UtcNow + TimeSpan.FromSeconds(sec);
            if (!wasSuppressed)
                VoiceDiagnostics.Log("bcl.decode.suppressed", $"client={ClientId} failures={_decodeFailures} reprobeSec={sec:0}");
        }

        // Funnel real-packet decode throws through a debounce: a transient bad frame resets the run, only a
        // sustained burst (>= ConsecutiveThrowSuppressRun within ThrowSuppressWindow) advances the 10-failure
        // breaker. With the MinLegacyOpusBytes pre-guard this means an incompatible stream is still parked
        // promptly while a healthy peer's sporadic bad frame never accumulates toward the cut-out.
        private void NoteDecodeThrottledFailure()
        {
            var now = DateTime.UtcNow;
            if (_consecutiveThrows == 0 || now - _firstThrowUtc > ThrowSuppressWindow)
            {
                _consecutiveThrows = 0;
                _firstThrowUtc = now;
            }
            _consecutiveThrows++;
            if (_consecutiveThrows >= ConsecutiveThrowSuppressRun)
            {
                NoteDecodeFailure();   // existing 10-failure breaker + 5s..60s reprobe, unchanged
                _consecutiveThrows = 0; // re-arm; a permanently-broken stream re-trips quickly
            }
        }

        private void NoteDecodeSuccess()
        {
            _consecutiveThrows = 0; // any decodable frame breaks a throw run
            if (_decodeSuppressed)
                VoiceDiagnostics.Log("bcl.decode.resumed", $"client={ClientId} afterFailures={_decodeFailures}");
            _decodeFailures = 0;
            _decodeSuppressed = false;
        }

        private bool DecodeAndAddSamples(byte[] data, bool isLegacy, bool decodeFec, int frameSize, out string? error, out int decodedFrames, int dredOffsetSamples = -1)
        {
            error = null;
            decodedFrames = 0;

            // Suppressed stream: skip the decode entirely (no throw, no drop log), just keep the timeline aligned.
            if (TryHandleSuppressedDecode(frameSize))
                return false;

            if (dredOffsetSamples < 0 && IsOpusDtxSilencePacket(data))
            {
                RouteSilence(frameSize);
                NoteDecodeSuccess();
                return true;
            }

            // PLC (empty payload) and FEC require frame_size to equal the EXACT missing duration; a real
            // packet is decoded at full capacity so its true (possibly larger) frame size always fits.
            var conceal = data.Length == 0 || decodeFec || dredOffsetSamples >= 0;

            // P2.1: pre-guard the per-packet Concentus decode THROW. On an incompatible/foreign stream sharing the
            // room, Concentus throws+catches per packet on the receive thread, stealing time from healthy streams.
            // Cheaply validate the Opus TOC byte (frame-count code vs available bytes) so a packet that is
            // UNAMBIGUOUSLY too short for its claimed frame count is RouteSilenced (and debounced toward the
            // existing suppression breaker) instead of thrown. Conservative by construction: a 1-byte code-0 frame
            // (valid DTX/silence) is never rejected; only the structurally-impossible cases short-circuit. The
            // _decodeSuppressed breaker and the MinLegacyOpusBytes legacy guard remain as backstops.
            if (!conceal && IsOpusPacketStructurallyInvalid(data))
            {
                error = "opus-toc-invalid";
                RouteSilence(frameSize);
                NoteDecodeThrottledFailure(); // same debounced path the throw would have taken — without the throw
                return false;
            }

            int capacity = MaxDecodeCapacitySamples * AudioHelpers.Channels;
            if (_decodePcm.Length < capacity) _decodePcm = new short[capacity];
            var pcm = _decodePcm;
            int decodeFrameSize = conceal ? frameSize : MaxDecodeCapacitySamples;
            int decoded;
            try
            {
                decoded = dredOffsetSamples >= 0
                    ? Decoder.DecodeDred(data, dredOffsetSamples, pcm.AsSpan(0, capacity), frameSize)
                    : Decoder.Decode(data.AsSpan(0, data.Length), pcm.AsSpan(0, capacity), decodeFrameSize, decodeFec);
            }
            catch (Exception ex)
            {
                // Concentus THROWS (it never returns an error code) on a malformed/edge Opus packet. Don't
                // drop the slot: emit one frame of silence so the playout timeline stays aligned and the
                // caller can keep draining the rest of the batch; the next real packet re-primes the decoder.
                // Capture the Opus TOC byte + payload length + path so a recurring throw burst can be traced to
                // the exact frame config (bandwidth/mode/frame-count) that trips the decoder.
                error = data.Length > 0
                    ? $"{ex.Message} toc=0x{data[0]:X2} len={data.Length} fec={decodeFec} legacy={isLegacy}"
                    : $"{ex.Message} len=0 fec={decodeFec} legacy={isLegacy}";
                RouteSilence(frameSize);
                // Count a throw toward suppression only for a real PV2 packet or a foreign legacy datagram,
                // and only after a SUSTAINED run (debounced) — never for genuine PV2 PLC/FEC concealment.
                if (!conceal || isLegacy) NoteDecodeThrottledFailure();
                return false;
            }

            if (decoded <= 0)
            {
                // Concentus normally throws on bad input; a non-positive return is anomalous, so surface it
                // for telemetry (and conceal the slot) instead of silently injecting an untracked gap.
                error = "decode-empty";
                RouteSilence(frameSize);
                if (!conceal) NoteDecodeFailure();
                return false;
            }
            if (!conceal) NoteDecodeSuccess();
            if (_decodeFloat.Length < decoded) _decodeFloat = new float[decoded];
            var samples = _decodeFloat;
            var peak = 0f;
            for (var i = 0; i < decoded; i++)
            {
                var sample = pcm[i] / (float)short.MaxValue;
                samples[i] = sample;
                var abs = Math.Abs(sample);
                if (abs > peak) peak = abs;
            }
            ObserveVoiceLevel(peak);
            _mixer?.AddSamples(PlaybackGroupId, samples, decoded, silent: false);
            decodedFrames = 1;
            return true;
        }


        // Routes one frame of silence to keep the playout timeline aligned when a packet cannot be decoded,
        // so a single bad/edge frame becomes a 20 ms blip instead of a gap that also re-prebuffers the ring.
        private void RouteSilence(int frameSize)
        {
            int n = frameSize * AudioHelpers.Channels;
            if (n <= 0) return;
            if (_decodeFloat.Length < n) _decodeFloat = new float[n];
            Array.Clear(_decodeFloat, 0, n);
            _mixer?.AddSamples(PlaybackGroupId, _decodeFloat, n, silent: true);
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                try { DataChannel?.close(); } catch { }
                try { Connection?.close(); } catch { }
                try { Decoder.Dispose(); } catch { }
                try { _tailFlushTimer.Dispose(); } catch { }
                try { _mixer?.Remove(PlaybackGroupId); } catch { }
            }
        }
    }

    private readonly struct PeerDiagnostics
    {
        public PeerDiagnostics(int clientId, float levelPeak, float packetLevelPeak, int samples, int audibleSamples, int audibleSilentSamples, int routeClears, VoiceProximityResult route, float appliedPan, VoiceJitterWindowStats jitter, string bufferStats)
        {
            ClientId = clientId;
            LevelPeak = levelPeak;
            PacketLevelPeak = packetLevelPeak;
            Samples = samples;
            AudibleSamples = audibleSamples;
            AudibleSilentSamples = audibleSilentSamples;
            RouteClears = routeClears;
            Route = route;
            AppliedPan = appliedPan;
            Jitter = jitter;
            BufferStats = bufferStats;
        }
        public int ClientId { get; }
        public float LevelPeak { get; }
        public float PacketLevelPeak { get; }
        public int Samples { get; }
        public int AudibleSamples { get; }
        public int AudibleSilentSamples { get; }
        public int RouteClears { get; }
        public VoiceProximityResult Route { get; }
        public float AppliedPan { get; }
        public VoiceJitterWindowStats Jitter { get; }
        public string BufferStats { get; }
        public string ToCompactString() => $"{ClientId}:{LevelPeak:0.000}/{PacketLevelPeak:0.000}/{Samples}/{AudibleSamples}/{AudibleSilentSamples}/route={Route.Reason}:{Route.NormalVolume:0.00},{Route.GhostVolume:0.00},{Route.RadioVolume:0.00},pan={Route.Pan:0.00},appliedPan={AppliedPan:0.00},clears={RouteClears}";
    }

    private readonly record struct DecodedSignal(
        string Kind,
        string? Sdp,
        string? Candidate,
        string? SdpMid,
        ushort SdpMLineIndex)
    {
        // Set on a request-offer when the SENDER's link is genuinely wedged (channel non-open). Lets the open
        // initiator honor the request and rebuild even when its own channel reads 'open' (the split-brain case),
        // bounded by per-peer recovery backoff. Defaults false (backward-compatible with peers that omit it).
        public bool RebuildRequested { get; init; }

        public static DecodedSignal Session(string kind, string sdp)
            => new(kind, sdp, null, null, 0);

        public static DecodedSignal Control(string kind)
            => new(kind, null, null, null, 0);

        public static DecodedSignal CandidateSignal(string candidate, string? sdpMid, ushort sdpMLineIndex)
            => new("candidate", null, candidate, sdpMid, sdpMLineIndex);
    }

    private static string DiagnosticSafe(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace("\"", "'");

    private sealed class SignalPayload { public string from { get; set; } = string.Empty; public string data { get; set; } = string.Empty; }
    private sealed class VoiceBackendClient { public int clientId { get; set; } = -1; public int playerId { get; set; } = -1; public bool isHost { get; set; } }
    private sealed class ClientPeerConfig { public IceServerDto[]? iceServers { get; set; } }
    private sealed class IceServerDto { public string urls { get; set; } = string.Empty; public string? username { get; set; } public string? credential { get; set; } }
}
