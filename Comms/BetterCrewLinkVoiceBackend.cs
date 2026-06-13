using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;
using MiraAPI.LocalSettings;
using NAudio.Wave;
using SIPSorcery.Net;
using SocketIOClient;
using UnityEngine;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BetterCrewLinkVoiceBackend : IVoiceBackend
{
    private const int DataControlPrefixLength = 4;
    // Hard ceiling on an untrusted inbound datagram (largest legit message is a ~4 KB voice packet).
    private const int MaxIncomingDatagramBytes = 16 * 1024;
    private static readonly int BclOpusBitrate = 48_000;
    private static readonly bool BclOpusUseConstrainedVbr = true;
    private static readonly bool BclOpusUseInbandFec = true;   // arm LossResistant flag (sender) + the jitter-buffer Fec drain arm
    private static readonly int BclOpusPacketLossPercent = 15; // non-zero PLP so Opus embeds FEC redundancy in the wire frame
    private const int BclPlaybackLatencyMs = 60;
    private const int BclJitterTargetDelayFrames = 4;
    private const int BclJitterMaxBufferedFrames = 8;
    private const int BclJitterMinTargetFrames = 2;
    private const int BclJitterMaxTargetFrames = 6;
    private const int CodecAdaptIntervalFrames = 100;
    private const int PlpDeadbandPercent = 3;
    private const float RemoteSpeakingThreshold = 0.004f;
    private const double SyntheticToneFrequency = 220.0;
    private const float SyntheticToneAmplitude = 0.012f;
    private static readonly TimeSpan RemoteActivityHold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MicCalibrationLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RnNoiseStatsLogInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BclQuietTailFlushDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan BclQuietTailFlushTimerDelay = BclQuietTailFlushDelay + TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan SignalRejectLogInterval = TimeSpan.FromSeconds(5);
    private static readonly byte[] DataControlPrefix = [(byte)'P', (byte)'C', (byte)'B', (byte)'C'];
    private static readonly byte[] LossReportMagic = [(byte)'P', (byte)'C', (byte)'L', (byte)'R'];
    private static readonly byte[] RadioStateMagic = [(byte)'P', (byte)'C', (byte)'R', (byte)'D'];
    private static readonly RTCIceServer[] DefaultIceServers = [new() { urls = "stun:stun.l.google.com:19302" }];

    private readonly BclMonoPlaybackGraph _leftPlayback;
    private readonly BclMonoPlaybackGraph _rightPlayback;
    private readonly BclStereoPlaybackProvider _playbackProvider;
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
    private readonly float[] _referenceFrame = new float[AudioHelpers.FrameSize];
    // Far-end reference shared with the playback provider: ~500ms ring, occupancy capped at ~200ms so the
    // echo-canceller's reference delay can't drift unbounded relative to capture.
    private readonly FarEndReference _farEndReference = new(AudioHelpers.ClockRate / 2, AudioHelpers.ClockRate / 5);
    private int _captureFrameSamples;
    // Bumped under _captureFrameSync on every capture stop/restart. A WaveInEvent callback that was already
    // dispatched before teardown snapshots the epoch on entry and is dropped once it acquires the lock, so a
    // stale device's frame can never push samples into state the next device is about to reuse.
    private int _captureEpoch;

    private SocketIOClient.SocketIO? _socket;
#if WINDOWS
    private IWaveIn? _waveIn;
    private IWavePlayer? _waveOut;
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
    private bool _speakerRequested;
    private bool _btProfileConflict;
    private bool _btMuteReleaseRequested;
    private const int SpeakerRetryFailureLimit = 3;
    private int _speakerRetryFailures;
    private string _speakerRetryExhaustedSignature = string.Empty;
    private int _openedSpeakerDeviceNumber = int.MinValue;
    private string _openedSpeakerProductName = string.Empty;
    private string _lastSpeakerDeviceName = string.Empty;
#endif
#if ANDROID
    private AndroidMicrophone? _androidMicrophone;
    private AndroidSampleProviderSpeaker? _androidSpeaker;
#endif
    private OpusEncoder _encoder = CreateEncoder();
    private Timer? _syntheticMicTimer;
    private string _lastMicDeviceName = string.Empty;
    private volatile float _micVolume = 1f;
    private float _noiseGateThreshold;
    private float _vadThreshold = 0.004f;
    private bool _autoMicGain = true;
    private volatile float _localLevel;
    private volatile bool _localSpeaking;
    private bool _microphoneReady;
    private bool _speakerReady;
    private VoiceCaptureRuntimeOptions _captureOptions;
    private const float DeadInputPeakThreshold = 0.00012f;
    private const float LiveSignalPeakThreshold = 0.005f;
    private const int DeadInputTriggerFrames = 500;
    private bool _captureLiveSignalSeen;
    private int _deadInputFrames;
    private int _deadInputDetected;
    private int _lastOpenedWaveInDevice = -1;

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
            $"device=\"{_lastMicDeviceName}\" openedWaveInDevice={Volatile.Read(ref _lastOpenedWaveInDevice)} peak={peak:0.000000}");
    }
    private double _syntheticTonePhase;
    private int _syntheticFrames;
    private DateTime _lastMicCalibrationLogUtc = DateTime.MinValue;
    private DateTime _lastRnNoiseStatsLogUtc = DateTime.MinValue;
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

    public BetterCrewLinkVoiceBackend(string roomCode, string region, string serverUrl)
    {
        RoomCode = roomCode;
        Region = region;
        ServerUrl = VoiceEndpointSettings.NormalizeBetterCrewLinkServerUrl(serverUrl);

        _leftPlayback = new BclMonoPlaybackGraph(
            AudioHelpers.PlaybackPrebufferSamples,
            AudioHelpers.PlaybackPrebufferSamples * 3,
            enableRecoveryPrebuffer: true,
            instancePrebufferSamples: AudioHelpers.PlaybackRecoveryPrebufferSamples);
        _rightPlayback = new BclMonoPlaybackGraph(
            AudioHelpers.PlaybackPrebufferSamples,
            AudioHelpers.PlaybackPrebufferSamples * 3,
            enableRecoveryPrebuffer: true,
            instancePrebufferSamples: AudioHelpers.PlaybackRecoveryPrebufferSamples);
        _playbackProvider = new BclStereoPlaybackProvider(_leftPlayback.Endpoint, _rightPlayback.Endpoint, _farEndReference);

        ConnectSocket();
        WarmOpusCodec();
        VoiceDiagnostics.Log("bcl.created", $"room={RoomCode} region={Region} endpoint={ServerUrl}");
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
#pragma warning disable CS0618
                _ = new OpusDecoder(AudioHelpers.ClockRate, AudioHelpers.Channels);
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("bcl.codec.warm.error", $"error=\"{ex.Message}\"");
            }
        });
    }

    public string RoomCode { get; }
    public string Region { get; }
    public string ServerUrl { get; }
    internal int PublicLobbyJoinEpoch => Volatile.Read(ref _publicLobbyJoinEpoch);
    public bool UsingMicrophone => _microphoneReady;
    public bool UsingSpeaker => _speakerReady;
    public bool Mute { get; private set; }
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
        if (Mute == mute) return;
        Mute = mute;
#if WINDOWS
        _muteSinceUtc = mute ? DateTime.UtcNow : DateTime.MinValue;
        _btMuteReleaseRequested = false;
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
    public void SetMasterVolume(float volume)
    {
        _leftPlayback.SetMasterVolume(volume);
        _rightPlayback.SetMasterVolume(volume);
    }
    public void SetMicVolume(float volume)
    {
        _micVolume = Mathf.Clamp(volume, 0f, 2f);
#if ANDROID
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
        lock (_captureFrameSync)
            _micPreprocessor.SetNoiseSuppressionEnabled(options.NoiseSuppressionEnabled);

#if WINDOWS
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
            return LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance?.AutoMicGain.Value ?? true;
        }
        catch
        {
            return true;
        }
    }

    public void SetMicrophone(string deviceName, float volume)
    {
        var normalizedName = deviceName ?? string.Empty;
        var deviceChanged = !string.Equals(_lastMicDeviceName, normalizedName, StringComparison.Ordinal);
        _lastMicDeviceName = normalizedName;
        _micVolume = Mathf.Clamp(volume, 0f, 2f);
#if WINDOWS
        if (deviceChanged)
        {
            _btProfileConflict = false;
            lock (_captureFrameSync)
                _micPreprocessor.ResetAutoGain();
        }
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
        lock (_captureWorkerSync)
        {
            _captureDesiredRunning = shouldRun && !_disposed;
            _captureDesiredReason = reason;
            _captureTransitionVersion++;

            if (!_captureWorker.IsCompleted) return;
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
            var captureKind = "wavein";
            var captureDevice = "mapper";
            int waveInDevice = -1;
            if (!_captureOptions.SyntheticMicToneEnabled)
            {
                waveInDevice = ResolveWaveInDevice(_lastMicDeviceName);
                Volatile.Write(ref _lastOpenedWaveInDevice, waveInDevice);
                var waveIn = new WaveInEvent
                {
                    BufferMilliseconds = 20,
                    NumberOfBuffers = 4,
                    WaveFormat = new WaveFormat(AudioHelpers.ClockRate, 16, 1),
                    DeviceNumber = waveInDevice,
                };
                _waveIn = waveIn;
                captureKind = "wavein";
                captureDevice = DescribeWaveInDevice(waveInDevice);
            }

            if (_waveIn != null)
            {
                _waveIn.DataAvailable += OnMicrophoneData;
                _waveIn.StartRecording();
            }

            if (_captureOptions.SyntheticMicToneEnabled)
            {
                captureKind = "synthetic";
                captureDevice = "generated-48khz-tone";
                StartSyntheticMicTone(reason);
            }

            _microphoneReady = true;
            // Start capturing the playback reference (and drop any stale adaptation) only for a real mic; the
            // synthetic tone is never echo-cancelled.
            _farEndReference.Reset();
            _farEndReference.Enabled = !_captureOptions.SyntheticMicToneEnabled;
            _micPreprocessor.ResetEchoCancellation();
            Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
            VoiceDiagnostics.Log("bcl.mic", $"ready=true reason={reason} capture={captureKind} device=\"{_lastMicDeviceName}\" captureDevice=\"{captureDevice}\" captureFormat=\"{DescribeWaveFormat(_waveIn?.WaveFormat)}\" waveInDevice={waveInDevice} defaultWaveIn=\"{DescribeDefaultWaveInDevice()}\" waveInDevices=\"{DescribeWaveInDevices()}\" syntheticTone={_captureOptions.SyntheticMicToneEnabled} volume={_micVolume:0.00}");
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
        _farEndReference.Enabled = false;
        var waveIn = _waveIn;
        var hadMic = waveIn != null || _microphoneReady;
        _waveIn = null;
        if (waveIn != null)
        {
            try { waveIn.DataAvailable -= OnMicrophoneData; } catch { }
            try { waveIn.StopRecording(); } catch { }
            try { waveIn.Dispose(); } catch { }
        }
        _microphoneReady = false;
        if (hadMic)
            Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
        // Reset the native RNNoise preprocessor INSIDE the capture lock: a WaveInEvent callback can still be
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

#if WINDOWS
    private static int ResolveWaveInDevice(string deviceName)
    {
        var requested = NormalizeAudioDeviceName(deviceName);
        if (!string.IsNullOrWhiteSpace(requested))
            return ResolveWaveInDeviceByNormalizedName(requested);

        return ResolveDefaultWaveInDevice();
    }

    private static int ResolveDefaultWaveInDevice()
        => -1;

    private static int ResolveWaveInDeviceByNormalizedName(string requested)
    {
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var productName = NormalizeAudioDeviceName(WaveInEvent.GetCapabilities(i).ProductName);
            if (DeviceNamesMatch(requested, productName))
                return i;
        }
        VoiceDiagnostics.Log("bcl.mic", $"ready=false reason=device-miss requested=\"{requested}\" fallback=mapper waveInDevices=\"{DescribeWaveInDevices()}\"");
        return -1;
    }

    private static string DescribeDefaultWaveInDevice()
        => "mapper";

    private static string DescribeWaveInDevice(int deviceNumber)
    {
        if (deviceNumber < 0) return "mapper";
        try { return WaveInEvent.GetCapabilities(deviceNumber).ProductName ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static string DescribeWaveInDevices()
    {
        try
        {
            var names = new List<string>();
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                names.Add($"{i}:{WaveInEvent.GetCapabilities(i).ProductName}");
            return names.Count == 0 ? "none" : string.Join("|", names);
        }
        catch (Exception ex)
        {
            return $"error:{ex.Message}";
        }
    }

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

    private static int ResolveWaveOutDevice(string deviceName)
    {
        var requested = NormalizeAudioDeviceName(deviceName);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            for (var i = 0; i < WinMmOutputDevices.DeviceCount; i++)
            {
                var productName = NormalizeAudioDeviceName(WinMmOutputDevices.GetProductName(i));
                if (DeviceNamesMatch(requested, productName))
                    return i;
            }
            VoiceDiagnostics.Log("bcl.speaker", $"ready=false reason=device-miss requested=\"{requested}\" fallback=mapper outputDevices=\"{DescribeWaveOutDevices()}\"");
        }

        return -1;
    }

    private static string DescribeWaveOutDevice(int deviceNumber)
    {
        if (deviceNumber < 0) return "mapper";
        try { return WinMmOutputDevices.GetProductName(deviceNumber); }
        catch { return "unknown"; }
    }

    private static string DescribeWaveOutDevices()
    {
        try
        {
            var names = new List<string>();
            for (var i = 0; i < WinMmOutputDevices.DeviceCount; i++)
                names.Add($"{i}:{WinMmOutputDevices.GetProductName(i)}");
            return names.Count == 0 ? "none" : string.Join("|", names);
        }
        catch (Exception ex)
        {
            return $"error:{ex.Message}";
        }
    }
#endif

#if ANDROID
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
                _androidMicrophone.DataAvailable += OnAndroidMicrophoneData;
                _androidMicrophone.SetVolume(_micVolume);
                _androidMicrophone.Start(_lastMicDeviceName);
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
        lock (_captureFrameSync)
        {
            if (epoch != _captureEpoch) return;
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
#endif

    private static string DescribeWaveFormat(WaveFormat? format)
    {
        if (format == null) return "none";
        return $"{format.Encoding}/{format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch";
    }

    public void SetSpeaker(string deviceName)
    {
#if WINDOWS
        try
        {
            if (!string.Equals(_lastSpeakerDeviceName, deviceName ?? string.Empty, StringComparison.Ordinal))
                _btProfileConflict = false;
            _lastSpeakerDeviceName = deviceName ?? string.Empty;
            _speakerRequested = true;
            _speakerTopologySignature = DescribeWaveOutDevices();
            var previousWaveOut = _waveOut;
            _waveOut = null;
            if (previousWaveOut != null)
            {
                try { previousWaveOut.PlaybackStopped -= OnPlaybackStopped; } catch { }
                try { previousWaveOut.Stop(); } catch { }
                try { previousWaveOut.Dispose(); } catch { }
            }
            var outputDevice = ResolveWaveOutDevice(deviceName);
            _waveOut = new WaveOutEvent
            {
                DeviceNumber = outputDevice,
                DesiredLatency = BclPlaybackLatencyMs,
                NumberOfBuffers = 3,
            };
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Init(_playbackProvider.ToWaveProvider());
            _waveOut.Play();
            // Drop stale far-end reference buffered against a previous output device/format.
            _farEndReference.Reset();
            _speakerReady = _waveOut.PlaybackState == PlaybackState.Playing;
            _openedSpeakerDeviceNumber = outputDevice;
            _openedSpeakerProductName = DescribeWaveOutDevice(outputDevice);
            VoiceDiagnostics.Log("bcl.speaker", $"ready={_speakerReady} device=\"{deviceName}\" outputDevice=\"{DescribeWaveOutDevice(outputDevice)}\" outputDeviceNumber={outputDevice} sourceFormat=\"left={_leftPlayback.Endpoint.WaveFormat};right={_rightPlayback.Endpoint.WaveFormat}\" graphFormat=\"{_playbackProvider.WaveFormat}\" outputDevices=\"{DescribeWaveOutDevices()}\" latencyMs={BclPlaybackLatencyMs} splitMonoGraphs=true");
        }
        catch (Exception ex)
        {
            try { _waveOut?.Stop(); } catch { }
            try { _waveOut?.Dispose(); } catch { }
            _waveOut = null;
            _speakerReady = false;
            _openedSpeakerDeviceNumber = int.MinValue;
            _openedSpeakerProductName = string.Empty;
            VoiceDiagnostics.Log("bcl.speaker", $"ready=false device=\"{deviceName}\" error=\"{ex.Message}\"");
        }
#else
#if ANDROID
        try
        {
            _androidSpeaker?.Dispose();
            _androidSpeaker = new AndroidSampleProviderSpeaker(_playbackProvider);
            _speakerReady = _androidSpeaker.IsPlaying;
            VoiceDiagnostics.Log("bcl.speaker", $"ready={_speakerReady} device=\"{deviceName}\" sourceFormat=\"left={_leftPlayback.Endpoint.WaveFormat};right={_rightPlayback.Endpoint.WaveFormat}\" graphFormat=\"{_playbackProvider.WaveFormat}\" androidAudioSource=true");
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
    // WinMM WaveOutEvent binds at open time and never re-resolves; the topology poll reopens it when the
    // render-device list changes (default-device flips, USB (un)plug, Bluetooth A2DP<->HFP profile switches).
    // Pinned devices follow too, guarded by a resolution diff so an unrelated list change never reopens them.
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        if (!ReferenceEquals(sender, _waveOut)) return;
        _speakerReady = false;
        if (e.Exception == null)
        {
            VoiceDiagnostics.Log("bcl.speaker", "ready=false reason=playback-stopped-clean");
            return;
        }
        VoiceDiagnostics.Log("bcl.speaker", $"ready=false reason=playback-stopped error=\"{e.Exception.Message}\"");
        var now = DateTime.UtcNow;
        if (now - _lastPlaybackStoppedRestartUtc < PlaybackStoppedRestartInterval) return;
        _lastPlaybackStoppedRestartUtc = now;
        _mainThreadActions.Enqueue(() =>
        {
            if (_disposed) return;
            SetSpeaker(_lastSpeakerDeviceName);
            // The rings kept filling while playback was stalled; drop that backlog or it replays late.
            foreach (var peer in SnapshotPeers())
                peer.FadeClearRoutes();
        });
    }

    private void MaybeFollowDefaultSpeaker()
    {
        if (_disposed || !_speakerRequested) return;

        var now = DateTime.UtcNow;
        bool fastWindow = now.Ticks < Interlocked.Read(ref _speakerTopologyFastUntilTicks);
        var interval = fastWindow ? SpeakerTopologyFastPollInterval : SpeakerTopologyPollInterval;
        if (now - _speakerTopologyLastPollUtc < interval) return;
        _speakerTopologyLastPollUtc = now;

        string signature;
        try { signature = DescribeWaveOutDevices(); }
        catch { return; }

        if (signature == _speakerTopologySignature)
        {
            if (_speakerReady)
            {
                _speakerRetryFailures = 0;
                return;
            }
            if (_speakerRetryFailures >= SpeakerRetryFailureLimit && signature == _speakerRetryExhaustedSignature) return;
            if (now - _lastSpeakerRetryUtc < SpeakerRetryInterval) return;
            _lastSpeakerRetryUtc = now;
            VoiceDiagnostics.Log("bcl.speaker", $"ready=false reason=retry attempt={_speakerRetryFailures + 1} devices=\"{signature}\"");
            SetSpeaker(_lastSpeakerDeviceName);
            if (_speakerReady)
            {
                _speakerRetryFailures = 0;
            }
            else if (++_speakerRetryFailures >= SpeakerRetryFailureLimit)
            {
                _speakerRetryExhaustedSignature = signature;
                VoiceDiagnostics.Log("bcl.speaker", $"ready=false reason=retry-exhausted devices=\"{signature}\"");
            }
            return;
        }

        _speakerRetryFailures = 0;
        bool pinned = !string.IsNullOrWhiteSpace(_lastSpeakerDeviceName);
        if (pinned && _speakerReady)
        {
            var resolved = ResolveWaveOutDevice(_lastSpeakerDeviceName);
            if (resolved == _openedSpeakerDeviceNumber
                && string.Equals(DescribeWaveOutDevice(resolved), _openedSpeakerProductName, StringComparison.Ordinal))
            {
                _speakerTopologySignature = signature;
                return;
            }
        }

        bool openedEndpointVanished = _openedSpeakerProductName.Length > 0
            && _speakerTopologySignature.Contains(_openedSpeakerProductName, StringComparison.Ordinal)
            && !signature.Contains(_openedSpeakerProductName, StringComparison.Ordinal);
        if (fastWindow && openedEndpointVanished && !_btProfileConflict)
        {
            _btProfileConflict = true;
            VoiceDiagnostics.Log("bcl.speaker.profile-conflict",
                $"mic=\"{_lastMicDeviceName}\" speaker=\"{_lastSpeakerDeviceName}\" oldDevices=\"{_speakerTopologySignature}\" newDevices=\"{signature}\"");
        }
        else if (_btProfileConflict && !openedEndpointVanished)
        {
            _btProfileConflict = false;
            VoiceDiagnostics.Log("bcl.speaker.profile-conflict", "resolved=true");
        }
        VoiceDiagnostics.Log("bcl.speaker", $"ready={_speakerReady} reason={(pinned ? "pinned-follow" : "default-follow")} oldDevices=\"{_speakerTopologySignature}\" newDevices=\"{signature}\"");
        SetSpeaker(_lastSpeakerDeviceName); // re-resolves against the new device list; refreshes signature
    }

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
        MaybeReleaseBluetoothMutedMicrophone();
        MaybeFollowDefaultSpeaker();
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
            LogCenteredLoudRoute(peer, target, listenerPos, result, snapshot.Phase);
            // Deliberately NOT calling peer.TryFlushBufferedVoice here. Doing so held the per-peer _sync lock
            // across the Concentus Opus decode ON THE UNITY MAIN/RENDER THREAD, so a packet arriving on the
            // WebRTC receive thread (or the tail-flush timer firing) could block rendering — a frame-stutter
            // vector that scaled with talker count. The quiet tail is already drained off the render thread by
            // the per-peer 40 ms _tailFlushTimer (re-armed on every received packet in TryReceiveVoicePacket),
            // so all Opus decode now happens on the receive/timer threads only. Worst case: ~40 ms extra tail
            // latency on the final frames of a talkspurt, which is imperceptible.
            peer.MaybeSendLossReport(lossReportNowUtc);
            peer.SampleDiagnostics();
        }
        VoiceFrameProfiler.End("room.backend.proximity", proxTicks);

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
#if WINDOWS
        StopMicrophoneWorkerForDispose();
        try { _waveOut?.Stop(); } catch { }
        try { _waveOut?.Dispose(); } catch { }
        _waveOut = null;
#elif ANDROID
        StopAndroidMicrophone("dispose");
        try { _androidSpeaker?.Dispose(); } catch { }
        _androidSpeaker = null;
#endif
        lock (_captureFrameSync)
            _micPreprocessor.Dispose();
        var socket = _socket;
        _socket = null;
        if (socket != null)
            _ = DisconnectAndDisposeSocketAsync(socket);
        ClearPeers();
        DrainPeerConnectionPool();
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
            ClearPeers();
            ResetJoinState();
        });
        _socket.On("setClient", async ctx =>
        {
            var socketId = ctx.GetValue<string>(0);
            var client = ctx.GetValue<BclClient>(1);
            _mainThreadActions.Enqueue(() => MapClient(socketId, client));
            await Task.CompletedTask;
        });
        _socket.On("setClients", async ctx =>
        {
            var clients = ctx.GetValue<Dictionary<string, BclClient>>(0);
            _mainThreadActions.Enqueue(() =>
            {
                foreach (var sid in SnapshotMappedSocketIds())
                    if (!clients.ContainsKey(sid)) RemovePeer(sid);
                foreach (var kv in clients)
                {
                    if (MapClient(kv.Key, kv.Value))
                    {
                        var peer = EnsurePeer(kv.Key);
                        if (peer != null && ShouldInitiateOffer(kv.Key))
                            _ = StartOfferAsync(kv.Key);
                    }
                }
            });
            await Task.CompletedTask;
        });
        _socket.On("join", async ctx =>
        {
            var socketId = ctx.GetValue<string>(0);
            var client = ctx.GetValue<BclClient>(1);
            _mainThreadActions.Enqueue(() =>
            {
                if (MapClient(socketId, client) && EnsurePeer(socketId) != null && ShouldInitiateOffer(socketId))
                    _ = StartOfferAsync(socketId);
            });
            await Task.CompletedTask;
        });
        _socket.On("leave", async ctx =>
        {
            var socketId = ctx.GetValue<string>(0);
            _mainThreadActions.Enqueue(() => RemovePeer(socketId));
            await Task.CompletedTask;
        });
        _socket.On("signal", async ctx =>
        {
            var payload = ctx.GetValue<SignalPayload>(0);
            VoiceDiagnostics.Log("bcl.signal.queued", $"fromSocket={payload.from} queueDepth={_mainThreadActions.Count + 1}");
            _mainThreadActions.Enqueue(() => HandleQueuedSignal(payload.from, payload.data));
            await Task.CompletedTask;
        });
        _socket.On("clientPeerConfig", async ctx =>
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

    private bool MapClient(string socketId, BclClient client)
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

    private void RepairPeerClientMapping(string socketId, BclClient client)
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
                }

                pending.Enqueue(dataJson ?? string.Empty);
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

    private string? GetLocalClientReason(string socketId, BclClient client)
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

    private void RetryClosedDataChannels()
    {
        var now = DateTime.UtcNow;
        if (now - _lastOfferRetryUtc < OfferRetryInterval) return;
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
            var leftRoute = _leftPlayback.Generate(playbackGroupId);
            var rightRoute = _rightPlayback.Generate(playbackGroupId);
            var peer = new PeerConnection(socketId, clientId, playbackGroupId, leftRoute, rightRoute);
            peer.SetInterleaveSync(_playbackProvider.InterleaveSync);
            WireNewPeerConnection(peer, socketId, pc);
            _peersBySocket[socketId] = peer;
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
    private static void ReadIceSettings(out bool natFix, out string turnUrl, out string turnUser, out string turnCred)
    {
        natFix = true;
        turnUrl = "turn:turn.bettercrewl.ink:3478";
        turnUser = "";
        turnCred = "";
        try
        {
            var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
            if (settings != null)
            {
                natFix = settings.NatFix.Value;
                turnUrl = settings.TurnServerUrl.Value;
                turnUser = settings.TurnUsername.Value;
                turnCred = settings.TurnCredential.Value;
            }
        }
        catch { /* settings not ready; fall back to defaults (Nat Fix on) */ }
    }

    // Resolve the ICE configuration AND its signature from one settings snapshot + one read of the (volatile)
    // _iceServers, so a pooled connection's recorded signature always matches the config it was built with.
    private (RTCConfiguration Config, string Signature) ResolveIce()
    {
        ReadIceSettings(out var natFix, out var turnUrl, out var turnUser, out var turnCred);
        var servers = _iceServers;
        var cfg = BuildIceConfiguration(servers, natFix, turnUrl, turnUser, turnCred);
        var sig = ComputeIceSignature(servers, natFix, turnUrl, turnUser, turnCred);
        return (cfg, sig);
    }

    // The signature of the CURRENTLY desired ICE config (read live). Compared against each pooled connection's
    // recorded signature at rent time to discard stale entries.
    private string CurrentIceSignature()
    {
        ReadIceSettings(out var natFix, out var turnUrl, out var turnUser, out var turnCred);
        return ComputeIceSignature(_iceServers, natFix, turnUrl, turnUser, turnCred);
    }

    // Pure ICE-config builder (unit-testable). With Nat Fix on, guarantees a STUN entry AND the configured TURN
    // relay are present so a NAT-blocked peer can fall back to relay; iceTransportPolicy stays 'all', so ICE
    // uses a direct path when it can and relays ONLY the peers that need it. With Nat Fix off, the base servers
    // are used unchanged (legacy STUN-only behaviour, no relay).
    internal static RTCConfiguration BuildIceConfiguration(IReadOnlyList<RTCIceServer> baseServers, bool natFix, string turnUrl, string turnUsername, string turnCredential)
    {
        var servers = new List<RTCIceServer>();
        if (baseServers != null) servers.AddRange(baseServers);

        if (natFix && !string.IsNullOrWhiteSpace(turnUrl))
        {
            if (!servers.Any(s => s.urls != null && s.urls.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)))
                servers.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
            // Only add the TURN relay if it can actually authenticate. A long-term-credential TURN allocation
            // with a blank username/credential is unusable and would just add a dead ICE server, so skip it
            // (the peer falls back to STUN-only for this connection) when either credential is missing.
            if (!string.IsNullOrWhiteSpace(turnUsername) && !string.IsNullOrWhiteSpace(turnCredential)
                && !servers.Any(s => string.Equals(s.urls, turnUrl, StringComparison.OrdinalIgnoreCase)))
                servers.Add(new RTCIceServer { urls = turnUrl, username = turnUsername, credential = turnCredential });
        }

        return new RTCConfiguration { iceServers = servers, iceTransportPolicy = RTCIceTransportPolicy.all };
    }

    // Cheap signature of everything that affects the resolved ICE config: Nat Fix, the TURN server URL AND its
    // credentials, and the signaling-provided base ICE servers. The TURN credentials are included so that a
    // credential-only change still invalidates the pool.
    private static string ComputeIceSignature(IReadOnlyList<RTCIceServer> baseServers, bool natFix, string turnUrl, string turnUser, string turnCred)
    {
        var baseUrls = baseServers == null ? "" : string.Join(",", baseServers.Select(s => s.urls));
        // The signature must mirror exactly the inputs BuildIceConfiguration actually consumes. With Nat Fix
        // OFF the TURN fields are ignored by the builder, so leaving them out keeps an edit to an unused TURN
        // setting from needlessly invalidating the warm pool. The "1|" / "0|" prefixes keep the two forms
        // distinct so a Nat-Fix-on config can never collide with a Nat-Fix-off one.
        return natFix
            ? "1|" + turnUrl + "|" + turnUser + "|" + turnCred + "|" + baseUrls
            : "0|" + baseUrls;
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
                if (candidate == null || _socket == null) return;
                // Diagnostic: candidate type (host/srflx/relay) proves whether TURN relay candidates were
                // gathered for this peer — i.e. whether Nat Fix is actually reaching the relay.
                if (VoiceDiagnostics.IsEnabled)
                    VoiceDiagnostics.Log("bcl.ice.candidate", $"socket={socketId} type={candidate.type} protocol={candidate.protocol}");
                var signalData = JsonSerializer.Serialize(new { candidate = candidate.candidate, sdpMid = candidate.sdpMid, sdpMLineIndex = candidate.sdpMLineIndex });
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
        if (socket == null || conn == null) return;
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
            var sdpJson = JsonSerializer.Serialize(new { type = "offer", sdp = offer.sdp });
            await socket.EmitAsync("signal", new object[] { new { to = socketId, data = sdpJson } });
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.offer.error", $"socket={socketId} error=\"{DiagnosticSafe(ex.Message)}\"");
        }
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
        if (signal.Kind == "request-offer")
        {
            // Answerer asked us to re-offer. Only the initiator may offer; share the recovery debounce.
            // Rebuild when OUR side needs it OR the sender flagged its link wedged (signal.RebuildRequested) —
            // the latter heals the split-brain where our channel reads 'open' but the remote's never opened.
            // ShouldInitiateOffer (no glare) + TryBeginRecovery (per-peer backoff) bound it to one peer, no storm.
            if (ShouldInitiateOffer(fromSocketId) && (LocalLinkNeedsRebuild(peer) || signal.RebuildRequested) && TryBeginRecovery(fromSocketId))
            {
                RecreatePeerConnection(fromSocketId);
                _ = StartOfferAsync(fromSocketId);
            }
            return;
        }
        if (signal.Kind == "offer")
        {
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
        }
        else if (signal.Kind == "answer")
        {
            peer.Connection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = signal.Sdp });
        }
        else if (signal.Kind == "candidate")
        {
            peer.Connection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = signal.Candidate,
                sdpMid = signal.SdpMid,
                sdpMLineIndex = signal.SdpMLineIndex,
            });
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
        if (HasDataControlPrefix(data))
        {
            var payload = new byte[data.Length - DataControlPrefixLength];
            Array.Copy(data, DataControlPrefixLength, payload, 0, payload.Length);
            if (TryHandleRadioState(payload, peer.PlayerId)) return;
            if (TryHandleLossReport(payload, peer)) return;
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

#if WINDOWS
    private void OnMicrophoneData(object? sender, WaveInEventArgs e)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _micCallbacks);
        var buffer = e.Buffer;
        if (buffer == null) return;
        var recordedBytes = Math.Min(e.BytesRecorded, buffer.Length);
        Interlocked.Add(ref _micBytes, recordedBytes);
        if (Mute)
        {
            Interlocked.Increment(ref _micMutedDrops);
            return;
        }
        if (recordedBytes <= 1) return;

        // Serialize the whole capture path (buffer conversion writes the shared _micConvertScratch, which is
        // reallocated lock-free) under _captureFrameSync so overlapping callbacks during a fast device switch
        // can't tear it. Snapshot the epoch first and drop the frame if a teardown happened in between. Never
        // let an exception escape onto NAudio's callback thread — that would crash the process.
        var epoch = Volatile.Read(ref _captureEpoch);
        lock (_captureFrameSync)
        {
            if (epoch != _captureEpoch) return;
            try
            {
                var format = (sender as IWaveIn)?.WaveFormat ?? _waveIn?.WaveFormat;
                if (format == null) return;
                var samples = ConvertMicrophoneBufferToMonoFloat(buffer, recordedBytes, format, out var floatPcm);
                if (samples <= 0) return;
                Interlocked.Add(ref _micSamples, samples);
                ProcessMicrophoneCaptureSamples(floatPcm, samples);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _micEncodeFailures);
                VoiceDiagnostics.Log("bcl.mic.capture_error", $"source=callback error=\"{ex.Message}\"");
            }
        }
    }
#endif

    private int ConvertMicrophoneBufferToMonoFloat(byte[] buffer, int recordedBytes, WaveFormat format, out float[] floatPcm)
    {
        if (format is WaveFormatExtensible extensible)
            format = extensible.ToStandardWaveFormat();
        var channels = Math.Max(1, format.Channels);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return ConvertIeeeFloat32ToMonoFloat(buffer, recordedBytes, channels, out floatPcm);
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            return ConvertPcm16ToMonoFloat(buffer, recordedBytes, channels, out floatPcm);
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 24)
            return ConvertPcm24ToMonoFloat(buffer, recordedBytes, channels, out floatPcm);
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 32)
            return ConvertPcm32ToMonoFloat(buffer, recordedBytes, channels, out floatPcm);

        floatPcm = Array.Empty<float>();
        Interlocked.Increment(ref _micEncodeFailures);
        VoiceDiagnostics.Log("bcl.mic.capture_error", $"format=\"{DescribeWaveFormat(format)}\" bytes={recordedBytes} error=\"unsupported-capture-format\"");
        return 0;
    }

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
    // Reused open-channel list for the send loop. Only used inside ProcessMicrophoneFrameLocked, which
    // always runs under _captureFrameSync, so it is effectively single-threaded; holds channel references
    // (not audio data), so no aliasing concern. Replaces the per-frame SnapshotOpenChannels LINQ array.
    private readonly List<RTCDataChannel> _openChannelScratch = new();

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

        if (!IsSyntheticSource(source))
            _micPreprocessor.ApplyHighPass(floatPcm, samples);

        // Acoustic echo cancellation: subtract the played far-end signal that bleeds back into the mic, before
        // AGC so the echo path the adaptive filter models stays linear. Skips when the reference FIFO is short.
        if (!IsSyntheticSource(source) && _farEndReference.TryReadAligned(_referenceFrame, samples))
            _micPreprocessor.ApplyEchoCancellation(floatPcm, _referenceFrame, samples);

        var rawCapturePeak = AudioHelpers.MeasurePeak(floatPcm, samples);
        var agcGain = _micPreprocessor.ApplyAutoGain(floatPcm, samples, _autoMicGain && !IsSyntheticSource(source), out var preSuppressionPeak);

        var preSuppressionGuardGain = AudioHelpers.GetCaptureEncodeLimiterGain(preSuppressionPeak);
        if (preSuppressionGuardGain < 1f)
        {
            AudioHelpers.ApplyGain(floatPcm, samples, preSuppressionGuardGain);
            preSuppressionPeak *= preSuppressionGuardGain;
        }

        if (!IsSyntheticSource(source))
            TrackCaptureHealthLocked(rawCapturePeak);

        if (captureOptions.NoiseSuppressionEnabled && !IsSyntheticSource(source))
            _micPreprocessor.TryApplyNoiseSuppression(floatPcm, samples);

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
        var voiceFlags = BclVoicePacketFlags.None;
        if (VoiceTeamRadioChannels.IsActive(_lastLocalRadioChannel)) voiceFlags |= BclVoicePacketFlags.Radio;
        if (BclOpusUseInbandFec) voiceFlags |= BclVoicePacketFlags.LossResistant;
        if (IsSyntheticSource(source)) voiceFlags |= BclVoicePacketFlags.Synthetic;
        // Wrap directly from the reusable encode scratch (first `encoded` bytes), skipping the old
        // intermediate trimmed byte[] copy. The framed buffer itself stays a fresh per-frame allocation
        // because channel.send queues it for asynchronous SCTP transmission (reusing it would corrupt
        // in-flight data).
        var framed = BclVoicePacket.Wrap(packet, encoded, _sendSequence++, frameTimestamp, (ushort)samples, voiceFlags, BclVoicePacket.QuantizeLevel(transmitPeak));
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

    private void RemovePeer(string socketId)
    {
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
        _leftPlayback.Remove(peer.PlaybackGroupId);
        _rightPlayback.Remove(peer.PlaybackGroupId);
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
            _leftPlayback.Remove(peer.PlaybackGroupId);
            _rightPlayback.Remove(peer.PlaybackGroupId);
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
        var micClipPct = micWindowSamples == 0 ? 0f : micNearClipSamples * 100f / micWindowSamples;
        var micZeroCrossRate = micWindowSamples <= 1 ? 0f : micZeroCrossings / (float)(micWindowSamples - 1);
        var micCrest = micRms <= 0.0 ? 0.0 : micPeak / micRms;
        var opusAvgBytes = opusFrames == 0 ? 0.0 : opusBytes / (double)opusFrames;
        var rnnoise = _micPreprocessor.ConsumeNoiseSuppressionDiagnostics();
        var rnnoiseSummary =
            $"enabled={_captureOptions.NoiseSuppressionEnabled} rnnoiseState={rnnoise.State} rnnoiseAttempts={rnnoise.Attempts} rnnoiseFrames={rnnoise.ProcessedFrames} rnnoiseUnavailable={rnnoise.UnavailableFrames} rnnoiseSamples={rnnoise.Samples} " +
            $"rnnoiseInputPeak={rnnoise.InputPeak:0.000000} rnnoiseInputRms={rnnoise.InputRms:0.000000} rnnoiseOutputPeak={rnnoise.OutputPeak:0.000000} rnnoiseOutputRms={rnnoise.OutputRms:0.000000} rnnoiseSpeechMax={rnnoise.SpeechProbabilityMax:0.000} " +
            $"rnnoiseFrameSize={rnnoise.FrameSize} rnnoiseNativePath=\"{DiagnosticSafe(rnnoise.NativePath)}\" rnnoiseLastError=\"{DiagnosticSafe(rnnoise.LastError)}\"";
        VoiceDiagnostics.Log("bcl.stats",
            $"reason={reason} room={RoomCode} region={Region} endpoint={ServerUrl} phase={snapshot?.Phase.ToString() ?? "none"} socketConnected={_socket?.Connected == true} socketId={_socket?.Id ?? "none"} " +
            $"peers={peers.Length} openChannels={openChannels} peerTransport={peerTransport} localSocket={GetEffectiveLocalSocketId()} joinedClient={_joinedClientId} joinInFlight={Volatile.Read(ref _joinInFlight)} joinRetryAgeMs={(DateTime.UtcNow - _lastJoinAttemptUtc).TotalMilliseconds:0} audible={peers.Count(peer => peer.CurrentRoute.Audible)} speaking={peers.Count(peer => peer.IsSpeaking)} " +
            $"localLevel={LocalLevel:0.000} localSpeaking={LocalSpeaking} mute={Mute} remoteLevelMax={remoteMax:0.000} " +
            $"audibleTicks={audibleTicks} audibleSilentTicks={audibleSilentTicks} silentPct={silentPct:0.0} peerWindows={peerWindows} peerJitter={peerJitter} peerBuffers={peerBuffers} " +
            $"encodedTx={Volatile.Read(ref _encodedTx)} encodedRx={Volatile.Read(ref _encodedRx)} customTx={Volatile.Read(ref _customTx)} customRx={Volatile.Read(ref _customRx)} " +
            $"micCallbacks={Volatile.Read(ref _micCallbacks)} micBytes={Volatile.Read(ref _micBytes)} micSamples={Volatile.Read(ref _micSamples)} micWindowSamples={micWindowSamples} micPeak={micPeak:0.000000} micRms={micRms:0.000000} micCrest={micCrest:0.00} micNonZeroSamples={micNonZeroSamples} micSilentCallbacks={micSilentCallbacks} micNearClipSamples={micNearClipSamples} micClipPct={micClipPct:0.000} micZeroCrossRate={micZeroCrossRate:0.0000} " +
            $"micMutedDrops={Volatile.Read(ref _micMutedDrops)} micEncodeFailures={Volatile.Read(ref _micEncodeFailures)} micEncodedFrames={Volatile.Read(ref _micEncodedFrames)} micNoOpenChannelDrops={Volatile.Read(ref _micNoOpenChannelDrops)} audioDecodeFailures={Volatile.Read(ref _audioDecodeFailures)} " +
            $"noiseGate={noiseGateThreshold:0.000000} vadThreshold={vadThreshold:0.000000} gateReason={_lastGateReason} gatePeak={_lastGatePeak:0.000000} gateRms={_lastGateRms:0.000000} gateThreshold={_lastGateThreshold:0.000000} txGain={_lastTransmitGain:0.000} txPeak={_lastTransmitPeak:0.000000} txPeakMax={txPeakMax:0.000000} txRms={txRms:0.000000} txSamples={txSamples} opusBytesAvg={opusAvgBytes:0.0} opusBytesMin={opusMinBytes} opusBytesMax={opusMaxBytes} " +
            $"syntheticTone={_captureOptions.SyntheticMicToneEnabled} noiseSuppression={_captureOptions.NoiseSuppressionEnabled} {rnnoiseSummary} syntheticFrames={Volatile.Read(ref _syntheticFrames)} capture={DescribeCaptureMode()} calibration={_captureOptions.MicCalibrationDiagnostics} sensitivity={_captureOptions.MicSensitivity:0.00} micReady={_microphoneReady} speakerReady={_speakerReady} plp={_adaptedPacketLossPercent} bitrate={_adaptedBitrate} micOpened={Volatile.Read(ref _lastOpenedWaveInDevice)} micDeadInput={Volatile.Read(ref _deadInputDetected)}");
        if ((_captureOptions.NoiseSuppressionEnabled || rnnoise.Attempts > 0 || rnnoise.UnavailableFrames > 0)
            && now - _lastRnNoiseStatsLogUtc >= RnNoiseStatsLogInterval)
        {
            _lastRnNoiseStatsLogUtc = now;
            LogNoiseSuppressionStats(rnnoiseSummary);
        }
    }

    private static void LogNoiseSuppressionStats(string message)
        => VoiceDiagnostics.Log("bcl.rnnoise.stats", message);

    private string DescribeCaptureMode()
    {
        if (_captureOptions.SyntheticMicToneEnabled) return "synthetic";
#if ANDROID
        return "android-unity-microphone";
#else
        return "wavein-only";
#endif
    }

    private int _adaptedPacketLossPercent = BclOpusPacketLossPercent;
    private int _adaptedBitrate = BclOpusBitrate;
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

    private static OpusEncoder CreateEncoder()
    {
#pragma warning disable CS0618
        var encoder = new OpusEncoder(AudioHelpers.ClockRate, AudioHelpers.Channels, OpusApplication.OPUS_APPLICATION_VOIP);
#pragma warning restore CS0618
        encoder.Bitrate = BclOpusBitrate;
        encoder.Complexity = AudioHelpers.OpusComplexity;
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        encoder.UseVBR = true;
        encoder.UseConstrainedVBR = BclOpusUseConstrainedVbr;
        encoder.UseDTX = true; // shrinks any quiet frames that pass the ShouldTransmit gate
        encoder.UseInbandFEC = BclOpusUseInbandFec;
        encoder.PacketLossPercent = BclOpusPacketLossPercent;
        return encoder;
    }

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
        private readonly BclPeerPlaybackRoute _leftRoute;
        private readonly BclPeerPlaybackRoute _rightRoute;
        private readonly BclVoiceJitterBuffer _jitterBuffer = new(targetDelayFrames: BclJitterTargetDelayFrames, maxBufferedFrames: BclJitterMaxBufferedFrames, minTargetDelayFrames: BclJitterMinTargetFrames, maxTargetDelayFrames: BclJitterMaxTargetFrames);
        private VoiceProximityResult _currentRoute = VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);
        private float _levelPeakSinceStats;
        private float _packetLevelPeakSinceStats;
        private float _recentVoiceLevel;
        private DateTime _lastVoiceLevelUtc = DateTime.MinValue;
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
        // Latest Opus-PLC concealment frame, synthesized on the DECODE thread (under _sync) after each real
        // decode and published atomically. The ring's trailing-stall bridge (Fix 2b-3) reads it on the NAudio
        // pull thread via Volatile.Read — never calling the decoder off-thread (which would corrupt decoder
        // state) and never taking _sync from the read path (which would invert the _stateLock<->_sync order
        // and deadlock). Published from a per-peer 3-slot ring (P0.1, see _bridgeSlots): the newly faded frame
        // goes into the next slot, so a reader still copying a previously-published slot is never overwritten
        // mid-read even across two back-to-back publishes with two independent readers.
        private float[]? _latestPlcFrame;
        // Per-peer 3-SLOT RING for the published bridge frame (P0.1): three reusable float[960] + an advancing
        // index, replacing the per-frame `new float[960]`. The faded frame is written into the NEXT slot
        // (index advances modulo 3), then the reference is published via Volatile.Write. THREE slots (readers+1,
        // not 2) are required because a single jitter-buffer batch drain emits SEVERAL PublishBridgeFrameLocked
        // calls back-to-back: with two readers (the left + right route, both wired to GetFreshBridgeFrame) a
        // 2-slot toggle could land the second of two back-to-back publishes onto the very slot a reader
        // snapshotted, tearing its in-flight copy. readers+1 slots make the slot a reader snapshotted provably
        // never the next-written slot even across two consecutive publishes — a HARD guarantee.
        // Allocated lazily on the first publish (decode thread, under _sync) and reused thereafter; pure allocation
        // hygiene (flattens decode-path garbage as talker count grows) — NOT a freeze fix (GC was not the cause).
        private const int BridgeSlotCount = 3; // readers (left+right) + 1
        private float[][]? _bridgeSlots;
        private int _bridgeSlot;
        // Monotonic publish timestamp (Stopwatch ticks) for _latestPlcFrame. The provider lambda self-expires
        // the bridge frame: a frame older than the ring's idle-reset window (200 ms) is never returned, so a
        // bridge can never replay a faded copy of a PREVIOUS utterance's tail after an idle gap or a
        // data-channel reopen. Written under _sync alongside the frame; read cross-thread via Volatile.Read
        // (long reads are atomic on x64).
        private long _latestPlcFrameTicks;
        // 200 ms, matching AudioProviders' IdleRecoveryResetWindow. Expressed in Stopwatch ticks once.
        private static readonly long PlcFrameMaxAgeTicks = System.Diagnostics.Stopwatch.Frequency / 5;
        private bool _disposed;

        // Retained: constructed by the test harness via reflection. Forwards clientId as the group id.
        public PeerConnection(string socketId, int clientId, BclPeerPlaybackRoute leftRoute, BclPeerPlaybackRoute rightRoute)
            : this(socketId, clientId, clientId, leftRoute, rightRoute)
        {
        }

        public PeerConnection(string socketId, int clientId, int playbackGroupId, BclPeerPlaybackRoute leftRoute, BclPeerPlaybackRoute rightRoute)
        {
            SocketId = socketId;
            ClientId = clientId;
            PlaybackGroupId = playbackGroupId;
            _leftRoute = leftRoute;
            _rightRoute = rightRoute;
            // Per-peer adaptive jitter wiring (Fix 2a-3 / 2b-3). The clean arrival-jitter signal is read off
            // _jitterBuffer; the PLC bridge frame is the atomically-published _latestPlcFrame. Both callbacks
            // are invoked under the ring's own _stateLock and touch only thread-safe state.
            _leftRoute.SetJitterSamplesProvider(() => _jitterBuffer.CurrentJitterSamples);
            _rightRoute.SetJitterSamplesProvider(() => _jitterBuffer.CurrentJitterSamples);
            _leftRoute.SetPlcFrameProvider(GetFreshBridgeFrame);
            _rightRoute.SetPlcFrameProvider(GetFreshBridgeFrame);
            _tailFlushTimer = new Timer(static state => ((PeerConnection)state!).FlushBufferedVoiceFromTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            MuteAll();
        }

        private object _lrSync = new();
        public void SetInterleaveSync(object sync) => _lrSync = sync;

        public void FadeClearRoutes()
        {
            _leftRoute.FadeClearBufferedSamples();
            _rightRoute.FadeClearBufferedSamples();
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
        public OpusDecoder Decoder { get; } = new(AudioHelpers.ClockRate, AudioHelpers.Channels);
#pragma warning restore CS0618
        public byte PlayerId { get; private set; } = byte.MaxValue;
        public string PlayerName { get; private set; } = "Unknown";
        private volatile VoiceTeamRadioChannel _radioChannel = VoiceTeamRadioChannel.None;
        // PlayerId the cached _radioChannel was applied for; a transient unmap+remap to the same
        // player keeps the channel, a genuine remap to another player drops it (see UpdateProfile).
        private volatile byte _radioChannelOwner = byte.MaxValue;
        private int _consecutiveNonRadioVoicePackets;
        private DateTime _radioChannelAppliedUtc = DateTime.MinValue;
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
            _consecutiveNonRadioVoicePackets = 0;
            if (VoiceTeamRadioChannels.IsActive(_radioChannel))
                _radioChannelAppliedUtc = DateTime.UtcNow;
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
                var level = Math.Max(_leftRoute.Level, _rightRoute.Level);
                if (_lastVoiceLevelUtc != DateTime.MinValue && DateTime.UtcNow - _lastVoiceLevelUtc <= RemoteActivityHold)
                    level = Math.Max(level, _recentVoiceLevel);
                return level;
            }
        }

        private void ObserveVoiceLevel(float level)
        {
            if (level <= 0f) return;
            _recentVoiceLevel = Math.Clamp(level, 0f, 1f);
            _lastVoiceLevelUtc = DateTime.UtcNow;
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
        public void SetVolume(float volume)
        {
            var clamped = Math.Clamp(volume, 0f, 2f);
            _leftRoute.SetVolume(clamped);
            _rightRoute.SetVolume(clamped);
        }
        public void MuteAll()
        {
            _leftRoute.MuteAll();
            _rightRoute.MuteAll();
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
            var wasAudible = _currentRoute.Audible;
            _currentRoute = result;
            WallCoefficient = result.WallCoefficient;
            _appliedPan = result.Pan;
            // Edge-only fade-clear; clearing on BECOMING audible chops speech onsets and re-arms a >=60ms prebuffer.
            var bufferedSamples = wasAudible && !result.Audible ? Math.Max(_leftRoute.BufferedSamples, _rightRoute.BufferedSamples) : 0;
            if (bufferedSamples > 0)
            {
                _leftRoute.FadeClearBufferedSamples();
                _rightRoute.FadeClearBufferedSamples();
                _routeClearsSinceStats++;
                // Gate the interpolated diagnostic string: it fires in bursts on audible<->inaudible
                // transitions, so building it eagerly (even with diagnostics off) is needless alloc churn.
                if (VoiceDiagnostics.IsEnabled)
                    VoiceDiagnostics.Log("bcl.route.clear",
                        $"client={ClientId} reason={result.Reason} wasAudible={wasAudible} audible={result.Audible} bufferedSamples={bufferedSamples} normal={result.NormalVolume:0.000} ghost={result.GhostVolume:0.000} radio={result.RadioVolume:0.000} pan={result.Pan:0.000} appliedPan={_appliedPan:0.000}");
            }
            BclStereoPlaybackProvider.GetPanGains(_appliedPan, out var leftGain, out var rightGain);
            _leftRoute.Apply(result, leftGain);
            _rightRoute.Apply(result, rightGain);
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
            int leftBuffered, rightBuffered, delta;
            lock (_lrSync)
            {
                leftBuffered = _leftRoute.BufferedSamples;
                rightBuffered = _rightRoute.BufferedSamples;
                delta = leftBuffered - rightBuffered;
                if (Math.Abs(delta) < AudioHelpers.FrameSize) return;
                if (delta > 0) _leftRoute.DiscardBufferedSamples(delta);
                else _rightRoute.DiscardBufferedSamples(-delta);
            }
            if (VoiceDiagnostics.IsEnabled && DateTime.UtcNow - _lastLrDivergenceLogUtc >= LrDivergenceLogInterval)
            {
                _lastLrDivergenceLogUtc = DateTime.UtcNow;
                VoiceDiagnostics.Log("bcl.lr.divergence",
                    $"client={ClientId} left={leftBuffered} right={rightBuffered} realigned={Math.Abs(delta)}");
            }
        }
        public PeerDiagnostics ConsumeDiagnostics()
        {
            lock (_sync)
            {
                var bufferStats = $"left={_leftRoute.ConsumeDebugStats()} right={_rightRoute.ConsumeDebugStats()}";
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
        public bool TryReceiveVoicePacket(byte[] data, out string? error, out int decodedFrames)
        {
            lock (_sync)
            {
                error = null;
                decodedFrames = 0;
                if (_disposed) return false;

                if (!BclVoicePacket.HasMagic(data))
                    return DecodeLegacyPacket(data, out error, out decodedFrames);

                if (!BclVoicePacket.TryRead(data, out var packet))
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
                if ((packet.Flags & BclVoicePacketFlags.Radio) != 0)
                    _consecutiveNonRadioVoicePackets = 0;
                else if (RadioActive
                         && DateTime.UtcNow - _radioChannelAppliedUtc > RadioStaleClearActivationGrace
                         && ++_consecutiveNonRadioVoicePackets >= RadioStaleClearPacketThreshold)
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

                foreach (var frame in frames)
                {
                    var payload = frame.Packet?.Payload ?? Array.Empty<byte>();
                    var decodeFec = frame.Kind == BclVoicePlayoutKind.Fec;
                    // FEC-reconstructed frame: decode at standard size; frame.Duration here is the
                    // successor's, not the lost frame's.
                    var frameSize = decodeFec
                        ? AudioHelpers.FrameSize
                        : NormalizeOpusFrameSize(Math.Max(AudioHelpers.FrameSize, (int)frame.Duration));
                    // A single bad frame must NOT abandon the rest of the drained batch (up to
                    // MaxDrainFramesPerPacket frames). DecodeAndAddSamples conceals the failed slot with
                    // silence, so surface the first error for telemetry and keep draining the successors.
                    if (DecodeAndAddSamples(payload, false, decodeFec, frameSize, out var frameError, out var decoded))
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

                var frames = _jitterBuffer.DrainDue(DateTime.UtcNow, BclQuietTailFlushDelay);
                foreach (var frame in frames)
                {
                    if (frame.Kind == BclVoicePlayoutKind.Plc)
                    {
                        RouteSilence(NormalizeOpusFrameSize(Math.Max(AudioHelpers.FrameSize, (int)frame.Duration)));
                        continue;
                    }
                    var payload = frame.Packet?.Payload ?? Array.Empty<byte>();
                    var decodeFec = frame.Kind == BclVoicePlayoutKind.Fec;
                    // FEC-reconstructed frame: decode at standard size; frame.Duration here is the
                    // successor's, not the lost frame's.
                    var frameSize = decodeFec
                        ? AudioHelpers.FrameSize
                        : NormalizeOpusFrameSize(Math.Max(AudioHelpers.FrameSize, (int)frame.Duration));
                    // Conceal a failed slot and keep draining instead of abandoning the rest of the tail batch.
                    if (DecodeAndAddSamples(payload, false, decodeFec, frameSize, out var frameError, out var decoded))
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
            try { _tailFlushTimer.Change(BclQuietTailFlushTimerDelay, Timeout.InfiniteTimeSpan); }
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

        private bool DecodeAndAddSamples(byte[] data, bool isLegacy, bool decodeFec, int frameSize, out string? error, out int decodedFrames)
        {
            error = null;
            decodedFrames = 0;

            // Suppressed stream: skip the decode entirely (no throw, no drop log), just keep the timeline aligned.
            if (TryHandleSuppressedDecode(frameSize))
                return false;

            if (IsOpusDtxSilencePacket(data))
            {
                RouteSilence(frameSize);
                NoteDecodeSuccess();
                return true;
            }

            // PLC (empty payload) and FEC require frame_size to equal the EXACT missing duration; a real
            // packet is decoded at full capacity so its true (possibly larger) frame size always fits.
            var conceal = data.Length == 0 || decodeFec;

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
                decoded = Decoder.Decode(data.AsSpan(0, data.Length), pcm.AsSpan(0, capacity), decodeFrameSize, decodeFec);
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
            lock (_lrSync)
            {
                _leftRoute.AddSamples(samples, 0, decoded);
                _rightRoute.AddSamples(samples, 0, decoded);
            }
            // Publish a bridge concealment frame for the ring's trailing-stall PLC (Fix 2b-3). Only after a
            // real decode, never on a conceal path. NOTE (deviation from plan, [confirm in code]): the plan
            // asked for live Opus PLC synthesis from the ring's read thread, but the peer Decoder is shared
            // with the decode thread (under _sync) and is NOT thread-safe, and calling it off-thread would
            // also invert the _stateLock<->_sync lock order (deadlock) and corrupt decoder state on every
            // frame. So the bridge is a fast-faded copy of the just-decoded real audio — energy-matched,
            // bounded to MaxTrailingPlcFrames (~60 ms), trim-bounded, and published atomically.
            if (!conceal) PublishBridgeFrameLocked(samples, decoded);
            decodedFrames = 1;
            return true;
        }

        // Build one ~20 ms concealment frame from the tail of the last real decode (gentle fade-out so a held
        // bridge doesn't buzz) and publish it atomically for the ring's trailing-stall bridge. Runs on the
        // decode thread under _sync; the ring reads the published reference via Volatile.Read on the NAudio
        // thread without touching the decoder or _sync (no off-thread decode, no lock-order inversion).
        private void PublishBridgeFrameLocked(float[] samples, int decoded)
        {
            int n = AudioHelpers.FrameSize * AudioHelpers.Channels;
            if (decoded < n) return; // need a full frame to bridge from
            // Publish into a 3-slot ring (P0.1): advance to the next slot, then publish it. Runs on the decode
            // thread under _sync, but the cadence is per-DRAINED-FRAME, not per-20ms — a single jitter-buffer
            // batch drain emits several PublishBridgeFrameLocked calls back-to-back. With readers+1 = 3 slots the
            // next-written slot is never one a reader could currently hold even across two back-to-back publishes,
            // so a reader's in-flight copy is never torn; readers only read the volatile-published reference.
            if (_bridgeSlots == null)
                _bridgeSlots = new[] { new float[n], new float[n], new float[n] };
            _bridgeSlot = (_bridgeSlot + 1) % BridgeSlotCount; // advance to the next slot in the ring
            var frame = _bridgeSlots[_bridgeSlot];
            int start = decoded - n; // take the most recent frame's worth of samples
            Array.Copy(samples, start, frame, 0, n);
            // Publish the frame, then its monotonic age stamp. Order doesn't matter for correctness because the
            // reader only USES a frame whose stamp is fresh; a torn read at worst expires a still-valid frame.
            System.Threading.Volatile.Write(ref _latestPlcFrame, frame);
            System.Threading.Volatile.Write(ref _latestPlcFrameTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        }

        // Provider for the ring's trailing-stall bridge (Fix 2b-3), invoked on the NAudio pull thread under the
        // ring's own _stateLock. Returns the published bridge frame ONLY while it is fresher than the 200 ms
        // idle-reset window; an older frame (an idle gap or data-channel reopen happened) returns null so the
        // bridge can never replay a faded copy of a previous utterance's tail. No decoder call, no _sync.
        private float[]? GetFreshBridgeFrame()
        {
            long stamp = System.Threading.Volatile.Read(ref _latestPlcFrameTicks);
            if (stamp == 0) return null; // nothing published yet
            if (System.Diagnostics.Stopwatch.GetTimestamp() - stamp > PlcFrameMaxAgeTicks) return null; // stale
            return System.Threading.Volatile.Read(ref _latestPlcFrame);
        }

        // Routes one frame of silence to keep the playout timeline aligned when a packet cannot be decoded,
        // so a single bad/edge frame becomes a 20 ms blip instead of a gap that also re-prebuffers the ring.
        private void RouteSilence(int frameSize)
        {
            int n = frameSize * AudioHelpers.Channels;
            if (n <= 0) return;
            if (_decodeFloat.Length < n) _decodeFloat = new float[n];
            Array.Clear(_decodeFloat, 0, n);
            lock (_lrSync)
            {
                _leftRoute.AddSamples(_decodeFloat, 0, n);
                _rightRoute.AddSamples(_decodeFloat, 0, n);
            }
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
            }
        }
    }

    private readonly struct PeerDiagnostics
    {
        public PeerDiagnostics(int clientId, float levelPeak, float packetLevelPeak, int samples, int audibleSamples, int audibleSilentSamples, int routeClears, VoiceProximityResult route, float appliedPan, BclVoiceJitterWindowStats jitter, string bufferStats)
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
        public BclVoiceJitterWindowStats Jitter { get; }
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
    private sealed class BclClient { public int clientId { get; set; } = -1; public int playerId { get; set; } = -1; public bool isHost { get; set; } }
    private sealed class ClientPeerConfig { public IceServerDto[]? iceServers { get; set; } }
    private sealed class IceServerDto { public string urls { get; set; } = string.Empty; public string? username { get; set; } public string? credential { get; set; } }
}
