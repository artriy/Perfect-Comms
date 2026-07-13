using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SocketIOClient;
using UnityEngine;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class PerfectCommsVoiceBackend : IVoiceBackend
{
    private const int DataControlPrefixLength = 4;

    private const int MaxIncomingDatagramBytes = 16 * 1024;
    private static readonly int PerfectCommsOpusBitrate = 48_000;
    private static readonly int PerfectCommsOpusPacketLossPercent = 15;
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
    private static readonly IceServer[] DefaultIceServers =
    [
        new("stun:stun.l.google.com:19302"),
        new("stun:stun1.l.google.com:19302"),
        new("stun:stun.cloudflare.com:3478"),
        new("stun:global.stun.twilio.com:3478"),
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
    private readonly Dictionary<byte, VoiceTeamRadioChannel> _radioStateByPlayerId = new();
    private DateTime _lastMassConnectionFailureUtc = DateTime.MinValue;
    private DateTime _deferralEpisodeStartUtc = DateTime.MinValue;

    private const string RpcRoutePeerPrefix = "rpc-route:";
    private readonly List<PeerConnection> _updatePeerScratch = new();
    private readonly Dictionary<int, PeerConnection> _routeClientScratch = new();
    private readonly Dictionary<int, PeerConnection> _canonicalRouteScratch = new();
    private readonly HashSet<int> _snapshotRouteClientIds = new();
    private readonly List<string> _staleSnapshotRoutePeerIds = new();
    private readonly Dictionary<int, DateTime> _duplicateRouteLogUtcByClient = new();
    private static readonly TimeSpan DuplicateRouteLogInterval = TimeSpan.FromSeconds(2);

    private volatile List<IceServer> _iceServers = DefaultIceServers.ToList();
    private readonly object _turnSync = new();
    private readonly HashSet<int> _pendingRelayPeerIds = new();
    private List<IceServer> _managedIceServers = new();
    private CancellationTokenSource? _turnCts;
    private bool _turnFetchInFlight;
    private bool _turnRefreshRelayPeersAwaiting;
    private bool _turnForceRelayAwaiting;
    private int _turnFetchFailureRound;
    private int _turnConfigGeneration;
    private DateTime _turnFetchNotBeforeUtc = DateTime.MinValue;

    private const int MaxPendingSignalsPerSocket = 32;
    private static readonly TimeSpan PendingSignalSocketMaxAge = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, DateTime> _pendingSignalFirstSeenUtc = new();
    private static readonly TimeSpan JoinRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OfferRetryInterval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan StuckConnectingTimeout = TimeSpan.FromSeconds(8);

    private static readonly TimeSpan PeerRecoveryDebounce = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MassConnectionFailureWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MassConnectionFailureRearmInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeerEscalationDeferralWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PeerEscalationDeferralEpisodeMax = TimeSpan.FromSeconds(15);
    private const int PeerRecoveryInFlightMaxAttempts = 2;

    private static readonly TimeSpan ResumeFrameThreshold = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PeerSilenceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SilenceLogInterval = TimeSpan.FromSeconds(10);

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
#if WINDOWS
    private int _sidecarCaptureActivitySinceStats;
    private long _sidecarLevelEventsTotal;
    private long _sidecarPeerLevelBatchesTotal;
    private long _sidecarPeerLevelsTotal;
    private int _sidecarPeerLevelsMappedSinceStats;
    private int _sidecarPeerLevelsUnmappedSinceStats;
    private long _lastSidecarLevelUtcTicks;
    private long _lastSidecarPeerLevelsUtcTicks;
#endif
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
#endif
#if ANDROID
    private MobileVoiceClient? _mobileVoice;
    private AndroidEnginePcmSpeaker? _mobileSpeaker;
#endif
#if WINDOWS
    private enum CaptureSlot { Sidecar, Bass, Unity }
    private CaptureSlot[] _captureSlots = Array.Empty<CaptureSlot>();
    private CaptureSlot _activeCaptureSlot = CaptureSlot.Bass;
    private CaptureSupervisor? _captureSupervisor;
    private int _supervisorActiveIndex;
    private SidecarVoiceLease? _voice;
    private volatile bool _voiceReady;
    private volatile List<IceServer>? _pendingIceServers;
    private Task _voiceStartTask = Task.CompletedTask;
    private int _voiceColdStartRetries;
    private int _voiceHeartbeatRestarts;
    private long _voiceStartedAtTicks;
    private const int VoiceHeartbeatRestartBudget = 3;
    private static readonly TimeSpan VoiceStableThreshold = TimeSpan.FromSeconds(30);
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
    private VoiceCaptureRuntimeOptions _captureOptions;
    private const float DeadInputPeakThreshold = 0.00012f;
    private const float LiveSignalPeakThreshold = 0.005f;
    private const int DeadInputTriggerFrames = 500;
    private bool _captureLiveSignalSeen;
    private int _deadInputFrames;
    private int _deadInputDetected;
    private int _lastOpenedRecordDevice = -1;

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

    private volatile bool _disposed;

    // Custom control messages moved to authenticated in-game RPC signaling. The interface event
    // remains for the alternate backend, but this implementation intentionally has no source.
    public event Action<VoiceBackendCustomMessage>? CustomMessageReceived
    {
        add { }
        remove { }
    }

    public PerfectCommsVoiceBackend(string roomCode, string region, string serverUrl)
    {
        RoomCode = roomCode;
        Region = region;
        ServerUrl = VoiceEndpointSettings.NormalizeBetterCrewLinkServerUrl(serverUrl);

        ConnectSocket();
        RefreshConfiguredIceServers("startup");
        if (ShouldForceRelay() && UsesManagedTurn())
            EnsureManagedTurnCredentials(forceRelay: true, refreshRelayPeers: false);
        VoiceDiagnostics.Log("bcl.created", $"room={RoomCode} region={Region} endpoint={ServerUrl}");
        if (WineEnvironment.IsWine)
        {
            var forceRelay = VoiceSettings.Instance?.WineForceRelay.Value == true;
            VoiceDiagnostics.Log("env.wine", $"detected=true forceRelay={forceRelay} localIp={WineEnvironment.GetLocalIPv4()?.ToString() ?? "none"}");
        }
    }

    private void ApplyIceServers(List<IceServer> servers)
    {
        if (servers == null || servers.Count == 0 || _disposed) return;
        _iceServers = servers;
#if WINDOWS
        _pendingIceServers = servers;
        SidecarVoiceLease? voice;
        lock (_voiceSync) voice = _voice;
        voice?.SetIceServers(servers);
#elif ANDROID
        _iceServers = servers;
        _mobileVoice?.SetIceServers(servers);
#endif
    }

    private void RefreshConfiguredIceServers(string reason)
    {
        var configured = DefaultIceServers.ToList();
        var natFix = NatFixEnabled();
        var customConfigured = TryGetCustomTurnServer(out var custom, out var customInvalid);

        if (natFix && customConfigured)
        {
            configured.Add(custom);
            if (WineEnvironment.IsWine && ShouldForceRelay())
            {
                var tcpUrl = WithTcpTransport(custom.Urls);
                configured.Add(new IceServer(
                    tcpUrl,
                    custom.Username,
                    custom.Credential));
            }
        }
        else if (natFix && !customInvalid)
        {
            lock (_turnSync)
                configured.AddRange(_managedIceServers);
        }

        configured = DeduplicateIceServers(configured);
        ApplyIceServers(configured);
        if (customInvalid)
            VoiceChatPluginMain.Logger.LogWarning(
                "[VC] Custom TURN configuration is invalid; Nat Fix relay is disabled until the TURN URL, username, and credential are corrected.");
        VoiceDiagnostics.Log(
            "bcl.ice.config",
            $"reason={reason} natFix={natFix} source={(customConfigured ? "custom" : customInvalid ? "invalid-custom" : "managed")} " +
            $"relayAvailable={RelayAvailable()} iceServers={configured.Count}");
    }

    private void RequestRelayCredentials(int clientId)
    {
        if (_disposed || !NatFixEnabled()) return;
        if (TryGetCustomTurnServer(out _, out _))
        {
            _mainThreadActions.Enqueue(() => _peerSession?.EscalatePeer(clientId, Environment.TickCount64));
            return;
        }
        if (!UsesManagedTurn()) return;

        lock (_turnSync)
            _pendingRelayPeerIds.Add(clientId);
        EnsureManagedTurnCredentials(forceRelay: false, refreshRelayPeers: false);
    }

    private void EnsureManagedTurnCredentials(bool forceRelay, bool refreshRelayPeers)
    {
        if (_disposed || !UsesManagedTurn()) return;

        var endpoint = TurnCredentialsUrl();
        if (TurnCredentialClient.TryGetFreshCached(DateTime.UtcNow, endpoint, out var cached) &&
            !TurnCredentialClient.NeedsRefresh(DateTime.UtcNow, endpoint))
        {
            CompleteManagedTurnCredentials(cached, forceRelay, refreshRelayPeers);
            return;
        }

        int generation;
        CancellationToken token;
        lock (_turnSync)
        {
            _turnForceRelayAwaiting |= forceRelay;
            _turnRefreshRelayPeersAwaiting |= refreshRelayPeers;
            if (_turnFetchInFlight) return;

            _turnFetchInFlight = true;
            generation = _turnConfigGeneration;
            _turnCts = new CancellationTokenSource();
            token = _turnCts.Token;
        }

        _ = Task.Run(() => FetchManagedTurnCredentialsAsync(generation, endpoint, token));
    }

    private async Task FetchManagedTurnCredentialsAsync(int generation, string endpoint, CancellationToken ct)
    {
        DateTime notBefore;
        lock (_turnSync) notBefore = _turnFetchNotBeforeUtc;
        var initialDelay = notBefore - DateTime.UtcNow;
        if (initialDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(initialDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        var retryDelays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var servers = await TurnCredentialClient.FetchAsync(TurnHttp, endpoint, ct)
                    .ConfigureAwait(false);
                CompleteManagedTurnCredentials(servers, forceRelay: false, refreshRelayPeers: false, generation);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                lastError = ex;
                VoiceDiagnostics.Log("bcl.turn", $"ready=false attempt={attempt}/3 error=\"{ex.Message}\"");
                if (attempt < 3)
                {
                    try { await Task.Delay(retryDelays[attempt - 1], ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        CancellationTokenSource? completedCts;
        lock (_turnSync)
        {
            if (generation != _turnConfigGeneration) return;
            _turnFetchInFlight = false;
            completedCts = _turnCts;
            _turnCts = null;
            _turnFetchFailureRound = Math.Min(_turnFetchFailureRound + 1, 4);
            var backoffSeconds = Math.Min(60, 5 * (1 << (_turnFetchFailureRound - 1)));
            _turnFetchNotBeforeUtc = DateTime.UtcNow + TimeSpan.FromSeconds(backoffSeconds);
        }
        try { completedCts?.Dispose(); } catch { }
        VoiceDiagnostics.Log(
            "bcl.turn",
            $"ready=false exhausted=true pendingPeers={PendingRelayPeerCount()} error=\"{lastError?.Message ?? "unknown"}\"");
    }

    private void CompleteManagedTurnCredentials(
        List<IceServer> servers,
        bool forceRelay,
        bool refreshRelayPeers,
        int? generation = null)
    {
        int[] pendingPeers;
        bool forceAwaiting;
        bool refreshAwaiting;
        CancellationTokenSource? completedCts;
        lock (_turnSync)
        {
            if (generation.HasValue && generation.Value != _turnConfigGeneration) return;
            _managedIceServers = new List<IceServer>(servers);
            pendingPeers = _pendingRelayPeerIds.ToArray();
            _pendingRelayPeerIds.Clear();
            forceAwaiting = _turnForceRelayAwaiting || forceRelay;
            refreshAwaiting = _turnRefreshRelayPeersAwaiting || refreshRelayPeers;
            _turnForceRelayAwaiting = false;
            _turnRefreshRelayPeersAwaiting = false;
            _turnFetchInFlight = false;
            completedCts = _turnCts;
            _turnCts = null;
            _turnFetchFailureRound = 0;
            _turnFetchNotBeforeUtc = DateTime.MinValue;
        }
        try { completedCts?.Dispose(); } catch { }

        RefreshConfiguredIceServers("managed-ready");
        _mainThreadActions.Enqueue(() =>
        {
#if WINDOWS || ANDROID
            var manager = _peerSession;
            if (manager == null) return;
            foreach (var clientId in pendingPeers)
                manager.EscalatePeer(clientId, Environment.TickCount64);
            if (forceAwaiting && ShouldForceRelay())
                manager.RebuildAll(forceRelay: true, nowMs: Environment.TickCount64);
            else if (refreshAwaiting)
                manager.RefreshRelayPeers(Environment.TickCount64);
#endif
        });
        VoiceDiagnostics.Log(
            "bcl.turn",
            $"ready=true iceServers={servers.Count} escalations={pendingPeers.Length} refresh={refreshAwaiting} forceRelay={forceAwaiting}");
    }

    private void MaybeRefreshManagedTurnCredentials()
    {
#if WINDOWS || ANDROID
        if (_peerSession?.HasRelayPeers != true || !UsesManagedTurn()) return;
        if (TurnCredentialClient.NeedsRefresh(DateTime.UtcNow, TurnCredentialsUrl()))
            EnsureManagedTurnCredentials(forceRelay: false, refreshRelayPeers: true);
#endif
    }

    private void ResetTurnCredentialState()
    {
        CancellationTokenSource? cts;
        lock (_turnSync)
        {
            _turnConfigGeneration++;
            cts = _turnCts;
            _turnCts = null;
            _turnFetchInFlight = false;
            _turnForceRelayAwaiting = false;
            _turnRefreshRelayPeersAwaiting = false;
            _pendingRelayPeerIds.Clear();
            _managedIceServers.Clear();
            _turnFetchFailureRound = 0;
            _turnFetchNotBeforeUtc = DateTime.MinValue;
        }
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
    }

    private int PendingRelayPeerCount()
    {
        lock (_turnSync) return _pendingRelayPeerIds.Count;
    }

    private static bool NatFixEnabled()
        => VoiceSettings.Instance?.NatFix.Value != false;

    private static bool ShouldForceRelay()
        => NatFixEnabled() && WineEnvironment.IsWine && VoiceSettings.Instance?.WineForceRelay.Value == true;

    private static bool TryGetCustomTurnServer(out IceServer server, out bool invalid)
    {
        server = default;
        invalid = false;
        var raw = VoiceSettings.Instance?.TurnServerUrl.Value?.Trim() ?? string.Empty;
        if (raw.Length == 0) return false;
        var username = VoiceSettings.Instance?.TurnUsername.Value ?? string.Empty;
        var credential = VoiceSettings.Instance?.TurnCredential.Value ?? string.Empty;
        if (!IsTurnUrl(raw) || !HasIceEndpoint(raw) || raw.Length > 2048 ||
            raw.Any(char.IsWhiteSpace) || raw.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(username) || username.Length > 512 || username.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(credential) || credential.Length > 512 || credential.Any(char.IsControl))
        {
            invalid = true;
            return false;
        }
        server = new IceServer(raw, username, credential);
        return true;
    }

    private static bool UsesManagedTurn()
    {
        if (!NatFixEnabled()) return false;
        var hasCustom = TryGetCustomTurnServer(out _, out var invalid);
        return !hasCustom && !invalid;
    }

    private static bool IsTurnUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        return (trimmed.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 5) ||
               (trimmed.StartsWith("turns:", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 6);
    }

    private static bool HasIceEndpoint(string url)
    {
        var colon = url.IndexOf(':');
        if (colon < 0 || colon + 1 >= url.Length) return false;
        var endpoint = url.Substring(colon + 1);
        if (endpoint.StartsWith("//", StringComparison.Ordinal)) endpoint = endpoint.Substring(2);
        var query = endpoint.IndexOf('?');
        if (query >= 0) endpoint = endpoint.Substring(0, query);
        return endpoint.Length > 0 && endpoint.IndexOf('@') < 0;
    }

    private static string WithTcpTransport(string url)
    {
        if (url.IndexOf("transport=", StringComparison.OrdinalIgnoreCase) >= 0)
            return System.Text.RegularExpressions.Regex.Replace(
                url,
                @"([?&])transport=[^&]*",
                "$1transport=tcp",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return url + (url.Contains('?') ? "&transport=tcp" : "?transport=tcp");
    }

    private bool RelayAvailable()
    {
        if (!NatFixEnabled()) return false;
        if (TryGetCustomTurnServer(out _, out _)) return true;
        lock (_turnSync)
            return _managedIceServers.Any(server => IsTurnUrl(server.Urls)) &&
                   !TurnCredentialClient.IsExpired(DateTime.UtcNow, TurnCredentialsUrl());
    }

    private static List<IceServer> DeduplicateIceServers(IEnumerable<IceServer> servers)
    {
        var result = new List<IceServer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in servers)
        {
            if (string.IsNullOrWhiteSpace(server.Urls)) continue;
            var key = $"{server.Urls.Trim()}\n{server.Username}\n{server.Credential}";
            if (seen.Add(key)) result.Add(server);
        }
        return result;
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
        get
        {
            lock (_peerSync)
            {
                BuildCanonicalRouteMapLocked(_canonicalRouteScratch);
                var provisional = 0;
                foreach (var peer in _peersBySocket.Values)
                    if (peer.ClientId < 0) provisional++;
                return _canonicalRouteScratch.Count + provisional;
            }
        }
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

    public void AppendRemoteOverlayStates(List<VoiceRemoteOverlayState> buffer)
    {
        lock (_peerSync)
        {
            BuildCanonicalRouteMapLocked(_canonicalRouteScratch);
            foreach (var peer in _canonicalRouteScratch.Values)
            {
                if (peer.PlayerId == byte.MaxValue) continue;
                buffer.Add(peer.ToOverlayState());
            }
            foreach (var peer in _peersBySocket.Values)
            {
                if (peer.ClientId >= 0 || peer.PlayerId == byte.MaxValue) continue;
                buffer.Add(peer.ToOverlayState());
            }
        }
    }

    public int CountMappedRemotePeers(VoiceGameStateSnapshot snapshot)
    {
        var count = 0;
        lock (_peerSync)
        {
            BuildCanonicalRouteMapLocked(_canonicalRouteScratch);
            foreach (var peer in _canonicalRouteScratch.Values)
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

    // Routing records are not transport health. Synthetic snapshot routes exist even while ICE is down,
    // so only native peers that reported "connected" through PeerSessionManager count as open.
    public int CountPeersWithOpenChannel(VoiceGameStateSnapshot snapshot)
    {
        var count = 0;
#if WINDOWS || ANDROID
        var manager = _peerSession;
        if (manager == null) return 0;
        foreach (var player in snapshot.Players)
        {
            if (player.IsLocal || player.Disconnected || player.IsDummy || player.ClientId < 0) continue;
            if (manager.IsPeerEstablished(player.ClientId)) count++;
        }
#endif
        return count;
    }

    public int CountOpenDataChannels()
    {
#if WINDOWS || ANDROID
        return _peerSession?.EstablishedPeerCount ?? 0;
#else
        return 0;
#endif
    }

    public int TryRecoverMissingClients(VoiceGameStateSnapshot snapshot)
    {
        var recovered = 0;
#if WINDOWS || ANDROID
        var manager = _peerSession;
        if (manager == null) return 0;
        var nowMs = Environment.TickCount64;
        foreach (var player in snapshot.Players)
        {
            if (player.IsLocal || player.Disconnected || player.IsDummy || player.ClientId < 0) continue;
            if (manager.TryRecoverPeer(player.ClientId, nowMs)) recovered++;
        }
#endif
        return recovered;
    }

    internal bool IsCompatibleRemoteClient(int clientId)
    {
#if WINDOWS || ANDROID
        return _peerSession?.IsCompatiblePeer(clientId) == true;
#else
        return false;
#endif
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

            _localLevel = 0f;
            _localSpeaking = false;
        }
#if WINDOWS
        _muteSinceUtc = mute ? DateTime.UtcNow : DateTime.MinValue;
        _btMuteReleaseRequested = false;
        if (mute)
            _voice?.SetMicActive(false);
        else if (_microphoneReady)
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
    }
    public void SetMicVolume(float volume)
    {
        _micVolume = NormalizeMicGain(volume);
#if ANDROID || WINDOWS
        // Gain is applied in the native encoder. Keeping the Unity source at unity gain avoids
        // applying the user's slider twice on Android.
        _androidMicrophone?.SetVolume(1f);
#endif
#if WINDOWS
        _voice?.SetInput(_micVolume, _vadThreshold);
#elif ANDROID
        _mobileVoice?.SetInput(_micVolume, _vadThreshold);
#endif
    }

    public void SetNoiseGate(float noiseGateThreshold, float vadThreshold)
    {
        _noiseGateThreshold = float.IsFinite(noiseGateThreshold)
            ? Mathf.Clamp(noiseGateThreshold, 0.0005f, 0.10f)
            : 0.003f;
        _vadThreshold = float.IsFinite(vadThreshold)
            ? Mathf.Clamp(vadThreshold, 0.0005f, 0.080f)
            : 0.004f;
#if WINDOWS
        _voice?.SetInput(_micVolume, _vadThreshold);
#elif ANDROID
        _mobileVoice?.SetInput(_micVolume, _vadThreshold);
#endif
    }

    public void SetCaptureRuntimeOptions(VoiceCaptureRuntimeOptions options)
    {
#if ANDROID
        var restartCapture = _captureOptions.SyntheticMicToneEnabled != options.SyntheticMicToneEnabled;
#endif
        _captureOptions = options;
        _autoMicGain = ReadAutoMicGainSetting();

#if WINDOWS
        _voice?.SetDsp(options.EchoCancellationEnabled, _autoMicGain, options.NoiseSuppressionEnabled, true);
        _voice?.SetSynthetic(options.SyntheticMicToneEnabled);
        _voice?.SetInput(_micVolume, _vadThreshold);
        EnsureCaptureSupervisor();
#elif ANDROID
        // Android intentionally uses managed synthetic PCM pushed into pc-mobile; keep native
        // synthetic generation off and the APM/DSP path disabled.
        _mobileVoice?.SetSynthetic(false);
        _mobileVoice?.SetInput(_micVolume, _vadThreshold);
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
        _micVolume = NormalizeMicGain(volume);
#if WINDOWS
        if (deviceChanged)
            _btProfileConflict = false;
        if (Mute)
        {
            QueueMicrophoneTransition(false, "set-muted");
            VoiceDiagnostics.Log("bcl.mic", $"ready=false muted=true device=\"{_lastMicDeviceName}\" callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)}");
            return;
        }

        QueueMicrophoneTransition(true, "settings", restartSidecar: deviceChanged && _microphoneReady);
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

    private static float NormalizeMicGain(float gain)
        => SidecarProtocol.NormalizeInputGain(gain);

#if WINDOWS
    private void QueueMicrophoneTransition(bool shouldRun, string reason, bool restartSidecar = false)
    {
        if (_activeCaptureSlot == CaptureSlot.Sidecar)
        {
            StopBassCapture("switch-to-sidecar");
            if (_androidMicrophone != null)
                _mainThreadActions.Enqueue(() => StopAndroidMicrophone("switch-to-sidecar"));
            _mainThreadActions.Enqueue(() =>
            {
                if (shouldRun && !_disposed) StartSidecarMicrophone(reason, restartSidecar);
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
            onAllFailed: reason => EnterVoiceUnavailable($"capture-all-failed reason={reason}"),
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
        QueueMicrophoneTransition(true, "capture-switch", restartSidecar: from == to && to == CaptureSlot.Sidecar);
    }

    private void OnSidecarHeartbeatLost(string reason)
    {
        var voice = _voice;
        if (voice == null) return;
        _mainThreadActions.Enqueue(() =>
        {
            if (_disposed || !ReferenceEquals(_voice, voice))
                return;

            // A session that stayed up past the stable threshold earned a fresh restart budget; a
            // connect-then-die flap never does, so it can't respawn forever (was: unbounded restart).
            long startedTicks = Interlocked.Read(ref _voiceStartedAtTicks);
            if (startedTicks > 0 &&
                DateTime.UtcNow - new DateTime(startedTicks, DateTimeKind.Utc) >= VoiceStableThreshold)
                _voiceHeartbeatRestarts = 0;

            if (_voiceHeartbeatRestarts >= VoiceHeartbeatRestartBudget)
            {
                StopVoiceSession("voice-unavailable");
                _peerSession?.Reset();
                _rpcKnownClients.Clear();
                EnterVoiceUnavailable($"heartbeat-lost detail={reason} restarts-exhausted={_voiceHeartbeatRestarts}/{VoiceHeartbeatRestartBudget}");
                return;
            }

            _voiceHeartbeatRestarts++;
            VoiceDiagnostics.Log("bcl.voice", $"reason=heartbeat-lost detail={reason} restart={_voiceHeartbeatRestarts}/{VoiceHeartbeatRestartBudget}");
            StopVoiceSession("heartbeat-lost");
            EnsureVoiceSession("heartbeat-lost");

            _peerSession?.Reset();
            _rpcKnownClients.Clear();
        });
    }

    // Single chokepoint for "voice cannot run and there is no desktop fallback" (BASS removed in 4.0):
    // always logs to the BepInEx log (not the debug-gated diagnostics file) and shows the user an
    // on-screen banner, so a terminal failure is never silent.
    private void EnterVoiceUnavailable(string detail)
    {
        VoiceChatPluginMain.Logger.LogWarning(
            $"[VC] Voice unavailable: {detail}. No desktop fallback (BASS removed in 4.0). " +
            $"slot={_activeCaptureSlot} device=\"{_lastMicDeviceName}\" wine={WineEnvironment.IsWine} " +
            "hint=check OS mic permission / antivirus quarantine / helper extraction");
        VoiceDiagnostics.Log("bcl.voice.unavailable", detail);
        try { VoiceChatHudState.ShowToastThreadSafe("Voice unavailable - check mic permission"); } catch { }
    }

    private void EnsureVoiceSession(string reason)
    {
        lock (_voiceSync)
        {
            if (_voice != null) return;
            var callbacks = new SidecarVoiceCallbacks(
                ProcessBassMicFrame,
                OnSidecarHeartbeatLost,
                OnHelperLocalSdp,
                OnHelperLocalCandidate,
                OnHelperPeerState,
                OnSidecarLevel,
                OnSidecarPeerLevels);
            var voice = SidecarVoiceHost.TryAcquire(callbacks, out var failure);
            if (voice == null)
            {
                VoiceDiagnostics.Log("bcl.voice", $"ready=false reason={reason} error=\"sidecar host lease rejected: {failure}\"");
                return;
            }
            _voiceReady = false;
            _voice = voice;
            _voiceStartTask = Task.Run(() => RunVoiceStart(voice, reason));
        }
    }

    private void OnHelperLocalSdp(string peerId, int generation, string sdpType, string sdp)
    {
        if (_disposed) return;
        if (!SidecarVoiceTransport.TryParseClientId(peerId, out var clientId))
        {
            VoiceDiagnostics.Log("sidecar.handoff.reject", $"op=local-sdp reason=peer-id-invalid peerIdChars={peerId?.Length ?? 0} generation={generation} sdpType={sdpType} sdpChars={sdp?.Length ?? 0}");
            return;
        }
        _mainThreadActions.Enqueue(() =>
        {
            if (_disposed) return;
            var manager = _peerSession;
            if (manager == null)
            {
                VoiceDiagnostics.Log("sidecar.handoff.reject", $"op=local-sdp reason=session-manager-null client={clientId} generation={generation} sdpType={sdpType} sdpChars={sdp?.Length ?? 0}");
                return;
            }
            manager.OnLocalSdp(clientId, generation, sdpType, sdp);
        });
    }

    private void OnHelperLocalCandidate(string peerId, int generation, string candidate)
    {
        if (_disposed) return;
        if (!SidecarVoiceTransport.TryParseClientId(peerId, out var clientId))
        {
            VoiceDiagnostics.Log("sidecar.handoff.reject", $"op=local-candidate reason=peer-id-invalid peerIdChars={peerId?.Length ?? 0} generation={generation} candidateChars={candidate?.Length ?? 0}");
            return;
        }
        _mainThreadActions.Enqueue(() =>
        {
            if (_disposed) return;
            var manager = _peerSession;
            if (manager == null)
            {
                VoiceDiagnostics.Log("sidecar.handoff.reject", $"op=local-candidate reason=session-manager-null client={clientId} generation={generation} candidateChars={candidate?.Length ?? 0}");
                return;
            }
            manager.OnLocalCandidate(clientId, generation, candidate);
        });
    }

    private void OnHelperPeerState(string peerId, int generation, string state)
    {
        if (_disposed) return;
        if (!SidecarVoiceTransport.TryParseClientId(peerId, out var clientId))
        {
            VoiceDiagnostics.Log("sidecar.handoff.reject", $"op=peer-state reason=peer-id-invalid peerIdChars={peerId?.Length ?? 0} generation={generation} state={state}");
            return;
        }
        var nowMs = Environment.TickCount64;
        _mainThreadActions.Enqueue(() =>
        {
            if (_disposed) return;
            var manager = _peerSession;
            if (manager == null)
            {
                VoiceDiagnostics.Log("sidecar.handoff.reject", $"op=peer-state reason=session-manager-null client={clientId} generation={generation} state={state}");
                return;
            }
            var relayOnly = manager.TryGetPeerRelayOnly(clientId, out var relay) && relay;
            VoiceDiagnostics.Log(
                "bcl.ice.peer-state",
                $"client={clientId} generation={generation} state={state} relayOnly={relayOnly.ToString().ToLowerInvariant()}");
            if (state == "connected")
                manager.OnPeerConnected(clientId, generation);
            else if (state is "failed" or "closed")
                manager.OnPeerConnectionLost(clientId, generation, nowMs);
            else if (state == "disconnected")
                manager.OnPeerConnectionDegraded(clientId, generation, nowMs);
            else
                VoiceDiagnostics.Log("sidecar.handoff.reject", $"op=peer-state reason=state-unhandled client={clientId} generation={generation} state={state}");
        });
    }

    private void OnSidecarLevel(float peak, bool speaking)
    {
        if (_disposed) return;
        // Desktop capture PCM is encoded inside the helper and never crosses TypeAudio anymore.
        // Frequent native level events are therefore the authoritative capture-liveness signal.
        Interlocked.Increment(ref _sidecarCaptureActivitySinceStats);
        Interlocked.Increment(ref _sidecarLevelEventsTotal);
        Interlocked.Exchange(ref _lastSidecarLevelUtcTicks, DateTime.UtcNow.Ticks);
        if (Mute) { _localLevel = 0f; _localSpeaking = false; return; }
        _localLevel = peak;
        _localSpeaking = speaking;
    }

    private void OnSidecarPeerLevels(IReadOnlyList<SidecarProtocol.PeerLevel> levels)
    {
        if (levels.Count == 0 || _disposed) return;
        Interlocked.Increment(ref _sidecarPeerLevelBatchesTotal);
        Interlocked.Add(ref _sidecarPeerLevelsTotal, levels.Count);
        Interlocked.Exchange(ref _lastSidecarPeerLevelsUtcTicks, DateTime.UtcNow.Ticks);
        _mainThreadActions.Enqueue(() => ApplyRemotePeerLevels(levels));
    }

    private void RunVoiceStart(SidecarVoiceLease voice, string reason)
    {
        bool started;
        try
        {
            started = voice.EnsureStarted(_lastMicDeviceName, _lastSpeakerDeviceName);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("bcl.voice", $"ready=false reason={reason} error=\"{ex.Message}\"");
            started = false;
        }

        if (!started || _disposed || !ReferenceEquals(_voice, voice))
        {
            try { voice.Dispose(); } catch { }
            lock (_voiceSync)
            {
                if (ReferenceEquals(_voice, voice))
                {
                    _voiceReady = false;
                    _voice = null;
                }
            }
            _speakerReady = false;
            if (!started)
            {
                VoiceDiagnostics.Log("bcl.voice", $"ready=false reason={reason} error=\"sidecar voice start failed\"");
                if (!_disposed && _voiceColdStartRetries++ < 2)
                    Task.Run(async () => { try { await Task.Delay(700); if (!_disposed) EnsureVoiceSession("voice-cold-retry"); } catch { } });
                else if (!_disposed)
                    EnterVoiceUnavailable($"cold-start-failed reason={reason} retries={_voiceColdStartRetries}");
            }
            return;
        }

        Interlocked.Exchange(ref _voiceStartedAtTicks, DateTime.UtcNow.Ticks);
        var pendingIce = _pendingIceServers;
        var currentMic = _lastMicDeviceName;
        var currentSpk = _lastSpeakerDeviceName;
        if (!voice.TryConfigureInitialCapture(
                currentMic,
                currentSpk,
                _captureOptions.EchoCancellationEnabled,
                _autoMicGain,
                _captureOptions.NoiseSuppressionEnabled,
                true,
                _micVolume,
                _vadThreshold,
                _captureOptions.SyntheticMicToneEnabled,
                !Mute && _microphoneReady,
                pendingIce))
        {
            _speakerReady = false;
            VoiceDiagnostics.Log("bcl.voice", $"ready=false reason={reason} error=\"initial sidecar configuration failed\"");
            try { voice.Dispose(); } catch { }
            lock (_voiceSync)
            {
                if (ReferenceEquals(_voice, voice))
                {
                    _voiceReady = false;
                    _voice = null;
                }
            }
            if (!_disposed && _voiceColdStartRetries++ < 2)
                Task.Run(async () => { try { await Task.Delay(700); if (!_disposed) EnsureVoiceSession("voice-config-retry"); } catch { } });
            else if (!_disposed)
                EnterVoiceUnavailable($"initial-configuration-failed reason={reason} retries={_voiceColdStartRetries}");
            return;
        }
        _speakerReady = true;
        if (voice.OutputDevices.Count > 0)
            VoiceChatLocalSettings.SetSpkDeviceNamesFromSidecar(voice.OutputDevices);
        var accepted = false;
        lock (_voiceSync)
        {
            if (!_disposed && ReferenceEquals(_voice, voice) && voice.Health == CaptureHealth.Healthy)
            {
                _voiceReady = true;
                StartVoicePump();
                accepted = true;
            }
        }
        if (!accepted) return;
        _voiceColdStartRetries = 0;
        Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
        VoiceDiagnostics.Log("bcl.voice", $"ready=true reason={reason} mic=\"{currentMic}\" spk=\"{currentSpk}\" outputs={voice.OutputDevices.Count}");
    }

    private void StopVoiceSession(string reason, bool waitForStop = false)
    {
        _voiceReady = false;
        StopVoicePump();
        SidecarVoiceLease? voice;
        Task startTask;
        lock (_voiceSync)
        {
            voice = _voice;
            _voice = null;
            startTask = _voiceStartTask;
        }
        // Releasing a lease only clears the lobby/session state. The process-lifetime host keeps
        // a healthy helper connected and idle for the next lobby/backend. Lease release is
        // serialized with Start/configuration, so an old async start cannot configure a new owner.
        try { voice?.Dispose(); } catch { }
        if (waitForStop)
            try { startTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
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
            // Sidecar renders and plays mixed remote audio directly to the output device; the C# pump no
            // longer drives a managed mixer. ponytail: pump retained but idle (dead playout path).
            Array.Clear(_playbackBlock, 0, _playbackBlock.Length);

            nextMs += frameMs;
            var sleep = nextMs - sw.ElapsedMilliseconds;
            if (sleep > 1) Thread.Sleep((int)sleep);
            else if (sleep < -200) nextMs = sw.ElapsedMilliseconds;
        }
    }

    private int FeedCaptureSupervisor(int micWindowSamples)
    {
        EnsureCaptureSupervisor();
        var supervisor = _captureSupervisor!;
        var sidecarActivity = Interlocked.Exchange(ref _sidecarCaptureActivitySinceStats, 0);

        if (Mute || !_microphoneReady || CaptureTransitionInFlight())
            return sidecarActivity;

        var activity = SelectCaptureActivity(
            nativeSidecarOwnsCapture: _activeCaptureSlot == CaptureSlot.Sidecar,
            sidecarLevelEvents: sidecarActivity,
            managedPcmSamples: micWindowSamples);
        supervisor.OnStatsWindow(activity, unmutedAndCapturing: true);
        return sidecarActivity;
    }

    internal static int SelectCaptureActivity(bool nativeSidecarOwnsCapture, int sidecarLevelEvents, int managedPcmSamples)
        => Math.Max(0, nativeSidecarOwnsCapture ? sidecarLevelEvents : managedPcmSamples);

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
            return;
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

    private void StartSidecarMicrophone(string reason, bool restartCapture = false)
    {
        try
        {
            // Desktop synthetic capture is generated inside the helper so it traverses the exact
            // production Opus/WebRTC path. Do not start the retired managed diagnostic timer here.
            StopSyntheticMicTone();
            _microphoneReady = true;
            EnsureVoiceSession(reason);
            var voice = _voice;
            if (voice != null)
            {
                // A native select-device while active can race the capture callback. Explicitly
                // stop -> select/configure -> start for device changes and supervisor restarts.
                if (restartCapture)
                    voice.SetMicActive(false);
                voice.SelectMicDevice(_lastMicDeviceName);
                voice.SetInput(_micVolume, _vadThreshold);
                voice.SetSynthetic(_captureOptions.SyntheticMicToneEnabled);
                voice.SetMicActive(!Mute);
            }
            Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
            VoiceDiagnostics.Log("bcl.mic", $"ready=true reason={reason} capture={(_captureOptions.SyntheticMicToneEnabled ? "sidecar-synthetic" : "sidecar")} restart={restartCapture.ToString().ToLowerInvariant()} device=\"{_lastMicDeviceName}\" captureFormat=\"48000Hz/float/mono\" volume={_micVolume:0.00} vad={_vadThreshold:0.0000}");
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
        _voice?.SetSynthetic(false);
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

#endif

#if WINDOWS || ANDROID
    private void OnRpcSignal(int senderClientId, SignalMsgType type, byte[] payload)
    {
        if (_disposed) return;
#if WINDOWS
        var voice = _voice;
        var voiceHealth = voice?.Health ?? CaptureHealth.Dead;
        if (!CanPumpDesktopRpc(_voiceReady, voiceHealth))
        {
            VoiceDiagnostics.Log("signaling.session.drop", $"stage=backend-dispatch sender={senderClientId} type={type} payloadBytes={payload.Length} reason=voice-not-ready voiceReady={_voiceReady} voiceHealth={voiceHealth}");
            return;
        }
#endif
        var manager = _peerSession;
        if (manager == null)
        {
            VoiceDiagnostics.Log("signaling.session.drop", $"stage=backend-dispatch sender={senderClientId} type={type} payloadBytes={payload.Length} reason=session-manager-null");
            return;
        }
        manager.OnSignal(senderClientId, type, payload, Environment.TickCount64);
    }

    private void PumpRpcSignaling(VoiceGameStateSnapshot? snapshot)
    {
#if WINDOWS
        // SidecarVoiceTransport commands are intentionally fire-and-forget. Do not create a
        // manager or mark peers Added until the helper handshake and initial configuration are
        // complete, otherwise commands issued during cold start/restart are silently dropped.
        var voice = _voice;
        if (!CanPumpDesktopRpc(_voiceReady, voice?.Health ?? CaptureHealth.Dead))
            return;
#endif
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

#if ANDROID
        EnsureMobileVoice();
        if (_mobileVoice?.IsRunning != true)
            return;
#endif
        if (_peerSession == null)
        {
#if WINDOWS
            _rpcTransport = new SidecarVoiceTransport(() => _voice);
#elif ANDROID
            _rpcTransport = new MobileVoiceTransport(() => _mobileVoice);
#endif
            _peerSession = new PeerSessionManager(
                localId,
                _rpcTransport!,
                _rpcSender,
                relayAvailable: RelayAvailable,
                requestRelay: RequestRelayCredentials,
                forceRelay: ShouldForceRelay);
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
        MaybeRefreshManagedTurnCredentials();
    }

#if WINDOWS
    internal static bool CanPumpDesktopRpc(bool configuredReady, CaptureHealth health)
        => configuredReady && health == CaptureHealth.Healthy;
#endif
#endif

#if ANDROID
    private void EnsureMobileVoice()
    {
        if (_mobileVoice != null) return;
        var mv = new MobileVoiceClient();
        mv.OnLocalSdp += (peerId, generation, type, sdp) =>
        {
            if (MobileVoiceTransport.TryParseClientId(peerId, out var cid))
                _mainThreadActions.Enqueue(() => _peerSession?.OnLocalSdp(cid, generation, type, sdp));
        };
        mv.OnLocalCandidate += (peerId, generation, cand) =>
        {
            if (MobileVoiceTransport.TryParseClientId(peerId, out var cid))
                _mainThreadActions.Enqueue(() => _peerSession?.OnLocalCandidate(cid, generation, cand));
        };
        mv.OnPeerState += (peerId, generation, state) =>
        {
            if (MobileVoiceTransport.TryParseClientId(peerId, out var cid))
                _mainThreadActions.Enqueue(() => OnMobilePeerState(cid, generation, state));
        };
        mv.OnLevel += (peak, speaking) => { _localLevel = peak; _localSpeaking = speaking; };
        mv.OnPeerLevels += levels =>
        {
            if (levels.Count > 0 && !_disposed)
                _mainThreadActions.Enqueue(() => ApplyRemotePeerLevels(levels));
        };
        if (!mv.Start())
        {
            mv.Dispose();
            VoiceDiagnostics.Log("bcl.mobile", "state=start-failed");
            return;
        }
        if (_iceServers != null && _iceServers.Count > 0) mv.SetIceServers(_iceServers);
        mv.SetInput(_micVolume, _vadThreshold);
        mv.SetSynthetic(false);
        // Android intentionally ships without the desktop APM/DeepFilter side libraries.
        mv.SetDsp(false, false, false, false);
        _mobileVoice = mv;
        VoiceDiagnostics.Log("bcl.mobile", "state=started backend=pc-mobile dsp=platform-passthrough");
    }

    private void OnMobilePeerState(int clientId, int generation, string state)
    {
        if (_peerSession == null) return;
        var nowMs = Environment.TickCount64;
        var relayOnly = _peerSession.TryGetPeerRelayOnly(clientId, out var relay) && relay;
        VoiceDiagnostics.Log(
            "bcl.ice.peer-state",
            $"client={clientId} generation={generation} state={state} relayOnly={relayOnly.ToString().ToLowerInvariant()} backend=mobile");
        if (state == "connected") _peerSession.OnPeerConnected(clientId, generation);
        else if (state is "failed" or "closed") _peerSession.OnPeerConnectionLost(clientId, generation, nowMs);
        else if (state == "disconnected") _peerSession.OnPeerConnectionDegraded(clientId, generation, nowMs);
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
#if ANDROID
            // pc-mobile owns Opus/WebRTC on Android. Synthetic diagnostics must enter that native
            // encoder just like real microphone PCM; the managed preprocessing tail is telemetry-only.
            _mobileVoice?.PushMic(frame, frame.Length);
#else
            ProcessMicrophoneFrame(frame, frame.Length, "synthetic");
#endif
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
#if ANDROID
            EnsureMobileVoice();
            _mobileVoice?.SetSynthetic(false);
            _mobileVoice?.SetInput(_micVolume, _vadThreshold);
#endif
            if (_captureOptions.SyntheticMicToneEnabled)
            {
                StartSyntheticMicTone(reason);
            }
            else
            {
                _androidMicrophone = new AndroidMicrophone();
                _androidMicrophone.ReuseBuffer = true;
                _androidMicrophone.DataAvailable += OnAndroidMicrophoneData;
                _androidMicrophone.SetVolume(1f);
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
#if ANDROID
                    _mobileVoice?.PushMic(buffer, samples);
#else
                    ProcessMicrophoneCaptureSamples(buffer, samples);
#endif
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
            EnsureMobileVoice();
            _mobileSpeaker?.Dispose();
            _mobileSpeaker = _mobileVoice != null ? new AndroidEnginePcmSpeaker(_mobileVoice) : null;
            _speakerReady = _mobileSpeaker?.IsPlaying ?? false;
            VoiceDiagnostics.Log("bcl.speaker", $"ready={_speakerReady} device=\"{deviceName}\" backend=pc-mobile");
        }
        catch (Exception ex)
        {
            try { _mobileSpeaker?.Dispose(); } catch { }
            _mobileSpeaker = null;
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
        var voice = _voice;
        if (voice != null)
        {
            voice.SelectOutputDevice(deviceName);
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

    // Custom data messages (radio state, loss reports, keepalive) rode the C# data channel. The
    // sidecar/pc-mobile engine owns the WebRTC data path now and exposes no custom-message op, so this is a
    // no-op. ponytail: kept on IVoiceBackend; radio-over-datachannel is gone on every transport.
    public void SendCustomMessage(byte[] payload)
    {
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
        lock (_peerSync)
            _radioStateByPlayerId[playerId] = channel;
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
#if WINDOWS
            _voice?.SendGameState(
                deaf: true,
                master: 0f,
                peers: Array.Empty<SidecarProtocol.GameStatePeerInput>());
#elif ANDROID
            _mobileVoice?.SendGameState(
                deaf: true,
                master: 0f,
                peers: Array.Empty<SidecarProtocol.GameStatePeerInput>());
#endif
            MaybeLogStats(snapshot, "no-snapshot");
            return;
        }

        long joinTicks = VoiceFrameProfiler.Begin();
        _ = JoinAsync(snapshot);
        VoiceFrameProfiler.End("backend.join", joinTicks);

        // Media peer ids are authoritative Among Us client ids, not BetterCrewLink socket ids.
        // Keep one lightweight route record for every snapshot client so Android (which does not
        // consume BCL roster callbacks) and a desktop client during a lobby-service outage still
        // send gain/pan/rule state to the native mixer. A later BCL roster record supersedes the
        // synthetic record without changing the actual WebRTC peer.
        EnsureSnapshotRoutePeers(snapshot);

        var localPlayer = snapshot.TryGetLocalPlayer(out var local) ? local : (VoicePlayerSnapshot?)null;
        var listenerPos = localPlayer?.Position;

        long proxTicks = VoiceFrameProfiler.Begin();
        SnapshotPeersInto(_updatePeerScratch);
        _routeClientScratch.Clear();
        foreach (var peer in _updatePeerScratch)
        {
            if (peer.ClientId < 0) continue;
            if (_routeClientScratch.TryGetValue(peer.ClientId, out var rival))
            {
                var kept = ChooseDuplicateRouteWinner(rival, peer);
                var muted = ReferenceEquals(kept, peer) ? rival : peer;
                _routeClientScratch[peer.ClientId] = kept;
                SuppressDuplicateRoutePeer(kept, muted);
            }
            else
            {
                _routeClientScratch[peer.ClientId] = peer;
            }
        }
        var lossReportNowUtc = DateTime.UtcNow;
        var frameGapTicks = _lastUpdateTicks == 0 ? 0 : lossReportNowUtc.Ticks - _lastUpdateTicks;
        _lastUpdateTicks = lossReportNowUtc.Ticks;
        var resumeFrame = IsResumeFrame(frameGapTicks, ResumeFrameThreshold.Ticks);
        List<SidecarProtocol.GameStatePeerInput>? helperGameStatePeers = null;
#if WINDOWS
        if (_voice != null)
            helperGameStatePeers = new List<SidecarProtocol.GameStatePeerInput>();
#elif ANDROID
        if (_mobileVoice != null)
            helperGameStatePeers = new List<SidecarProtocol.GameStatePeerInput>();
#endif
        foreach (var peer in _updatePeerScratch)
        {
            if (peer.ClientId >= 0 &&
                _routeClientScratch.TryGetValue(peer.ClientId, out var canonical) &&
                !ReferenceEquals(peer, canonical))
                continue;
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
            peer.Apply(result);
            if (helperGameStatePeers != null && peer.ClientId >= 0)
            {
                float gain = result.Audible
                    ? Math.Clamp(result.NormalVolume + result.GhostVolume + result.RadioVolume, 0f, 1f) * peer.ClientVolume
                    : 0f;
                helperGameStatePeers.Add(new SidecarProtocol.GameStatePeerInput(
                    peer.ClientId.ToString(CultureInfo.InvariantCulture),
                    gain, result.Pan, (int)result.FilterMode));
            }
            LogCenteredLoudRoute(peer, target, listenerPos, result, snapshot.Phase);

            if (resumeFrame)
                peer.RebaseInbound(lossReportNowUtc.Ticks);
            peer.SampleDiagnostics();
        }
        VoiceFrameProfiler.End("room.backend.proximity", proxTicks);

#if WINDOWS
        if (helperGameStatePeers != null)
        {
            _voice?.SendGameState(VoiceChatHudState.IsSpeakerMuted, _masterVolume, helperGameStatePeers);
        }
#elif ANDROID
        if (helperGameStatePeers != null)
        {
            _mobileVoice?.SendGameState(VoiceChatHudState.IsSpeakerMuted, _masterVolume, helperGameStatePeers);
        }
#endif

        MaybeLogStats(snapshot, "ok");
    }

    private PeerConnection ChooseDuplicateRouteWinner(PeerConnection first, PeerConnection second)
    {
        string? mappedSocket;
        lock (_peerSync)
            _clientToSocket.TryGetValue(second.ClientId, out mappedSocket);
        return PreferSecondRouteRecord(first.SocketId, second.SocketId, mappedSocket) ? second : first;
    }

    internal static bool PreferSecondRouteRecord(string firstSocket, string secondSocket, string? mappedSocket)
    {
        if (!string.IsNullOrEmpty(mappedSocket))
        {
            var firstMapped = string.Equals(mappedSocket, firstSocket, StringComparison.Ordinal);
            var secondMapped = string.Equals(mappedSocket, secondSocket, StringComparison.Ordinal);
            if (firstMapped != secondMapped) return secondMapped;
        }

        var firstSynthetic = firstSocket.StartsWith(RpcRoutePeerPrefix, StringComparison.Ordinal);
        var secondSynthetic = secondSocket.StartsWith(RpcRoutePeerPrefix, StringComparison.Ordinal);
        if (firstSynthetic != secondSynthetic) return firstSynthetic;

        // Stable fallback makes the winner independent of dictionary insertion order.
        return string.CompareOrdinal(secondSocket, firstSocket) < 0;
    }

    internal static bool ShouldHoldPreviousRemoteLevel(float previous, long previousTicks, float next, long nowTicks)
        => next < RemoteSpeakingThreshold &&
           previous >= RemoteSpeakingThreshold &&
           previousTicks > 0 &&
           nowTicks >= previousTicks &&
           nowTicks - previousTicks < RemoteActivityHold.Ticks;

    private void SuppressDuplicateRoutePeer(PeerConnection kept, PeerConnection muted)
    {
        muted.Apply(VoiceProximityResult.Muted(VoiceProximityReason.Unmapped));
        var now = DateTime.UtcNow;
        if (_duplicateRouteLogUtcByClient.TryGetValue(muted.ClientId, out var lastLog) && now - lastLog < DuplicateRouteLogInterval) return;
        _duplicateRouteLogUtcByClient[muted.ClientId] = now;
        VoiceDiagnostics.Log("bcl.peer.duplicate", $"client={muted.ClientId} keptSocket={kept.SocketId} mutedSocket={muted.SocketId}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetTurnCredentialState();
        var socket = _socket;
        _socket = null;
        if (socket != null)
        {
            DetachSocketHandlers(socket);
            _ = DisconnectAndDisposeSocketAsync(socket);
        }
#if WINDOWS || ANDROID
        if (_rpcOnMessageHooked)
        {
            AmongUsRpcSignaling.OnMessage -= OnRpcSignal;
            _rpcOnMessageHooked = false;
        }
        _peerSession?.Reset();
        _peerSession = null;
        _rpcTransport = null;
        _rpcKnownClients.Clear();
#endif
#if WINDOWS
        StopMicrophoneWorkerForDispose();
        StopVoiceSession("dispose", waitForStop: true);
#elif ANDROID
        StopAndroidMicrophone("dispose");
        try { _mobileSpeaker?.Dispose(); } catch { }
        _mobileSpeaker = null;
        try { _mobileVoice?.Dispose(); } catch { }
        _mobileVoice = null;
#endif
        lock (_captureFrameSync)
            _micPreprocessor.Dispose();
        ClearPeers();
        _lastCenteredLoudLogUtc.Clear();
        while (_mainThreadActions.TryDequeue(out _)) { }
    }

    private void DetachSocketHandlers(SocketIOClient.SocketIO socket)
    {
        try { socket.OnConnected -= OnSocketConnected; } catch { }
        try { socket.OnDisconnected -= OnSocketDisconnected; } catch { }
        foreach (var eventName in new[] { "setClient", "setClients", "join", "leave", "signal", "clientPeerConfig", "VAD" })
            try { socket.Off(eventName); } catch { }
    }

    private static async Task DisconnectAndDisposeSocketAsync(SocketIOClient.SocketIO socket)
    {
        try { await socket.DisconnectAsync().ConfigureAwait(false); } catch { }

        try { socket.Dispose(); } catch { }
    }

    private void ConnectSocket()
    {
        _socket = new SocketIOClient.SocketIO(new Uri(ServerUrl), BetterCrewLinkSocketOptions.Create());

        _socket.OnConnected += OnSocketConnected;
        _socket.OnDisconnected += OnSocketDisconnected;

        _socket.On("setClient", async ctx =>
        {
            if (_disposed) return;
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
            if (_disposed) return;
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
                            EnsurePeer(kv.Key);
                    }
                });
#endif
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=setClients error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("join", async ctx =>
        {
            if (_disposed) return;
            try
            {
                var socketId = ctx.GetValue<string>(0);
                var client = ctx.GetValue<VoiceBackendClient>(1);
                if (socketId == null || client == null) return;
#if !ANDROID
                _mainThreadActions.Enqueue(() =>
                {
                    if (MapClient(socketId, client))
                        EnsurePeer(socketId);
                });
#endif
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=join error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("leave", async ctx =>
        {
            if (_disposed) return;
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
        // P2P media signaling (offer/answer/candidate) moved to in-game RPC + the native engine; the legacy
        // SocketIO "signal" channel is dead. The socket is kept for the roster (setClients/join/leave) and
        // public-lobby features only.
        _socket.On("signal", async _ => await Task.CompletedTask);
        _socket.On("clientPeerConfig", async ctx =>
        {
            if (_disposed) return;
            try
            {
                var config = ctx.GetValue<ClientPeerConfig>(0);
                if (config?.iceServers != null && config.iceServers.Length > 0)
                {
                    _iceServers = config.iceServers
                        .Select(server => new IceServer(server.urls, server.username ?? "", server.credential ?? ""))
                        .ToList();
                }
            }
            catch (Exception ex) { VoiceDiagnostics.Log("bcl.signal.error", $"event=clientPeerConfig error=\"{ex.Message}\""); }
            await Task.CompletedTask;
        });
        _socket.On("VAD", async _ => await Task.CompletedTask);
        _ = _socket.ConnectAsync();
    }

    private void OnSocketConnected(object? sender, EventArgs args)
    {
        if (_disposed) return;
        var socketId = _socket?.Id ?? string.Empty;
        lock (_peerSync)
            _localSocketId = socketId;
        VoiceDiagnostics.Log("bcl.socket", $"connected=True socketId={socketId}");
    }

    private void OnSocketDisconnected(object? sender, string reason)
    {
        if (_disposed) return;
        _mainThreadActions.Enqueue(() =>
        {
            if (_disposed) return;
            ResetJoinState();
            VoiceDiagnostics.Log("bcl.socket", "connected=False peersKept=true");
        });
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

    private void ReplayPendingSignals(string socketId)
    {
        // SocketIO media signaling is gone (RPC + native engine handle it), so no signals are ever pended.
        lock (_peerSync)
            _pendingSignalsBySocket.Remove(socketId);
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

    internal static bool IsOpusPacketStructurallyInvalid(byte[] data)
    {
        if (data == null || data.Length == 0) return true;
        int code = data[0] & 0x3;
        if (code == 0)
        {
            if (data.Length > 1) return false;
            return ((data[0] >> 3) & 0x1F) >= 12;
        }

        if (code == 1) return false;

        return data.Length < 2;
    }

    internal static bool IsOpusDtxSilencePacket(byte[] data)
        => data != null && data.Length == 1 && (data[0] & 0x3) <= 1 && ((data[0] >> 3) & 0x1F) >= 12;

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
            if (_peersBySocket.TryGetValue(socketId, out var known)) return known;
            EvictSameClientPeersLocked(socketId);
            var clientId = _socketToClient.TryGetValue(socketId, out var mappedClientId) ? mappedClientId : -1;
            var playbackGroupId = clientId >= 0 ? clientId : _nextProvisionalPeerGroupId--;
            var peer = new PeerConnection(socketId, clientId, playbackGroupId);
            _peersBySocket[socketId] = peer;
            VoiceDiagnostics.Log("bcl.peer.created", $"socket={socketId} client={clientId} playbackGroup={playbackGroupId} provisional={(clientId < 0).ToString().ToLowerInvariant()}");
            VoiceDiagnostics.Log("voice.route.created", $"socket={socketId} client={clientId} playbackGroup={playbackGroupId} source=bettercrewlink-roster transportEstablished=false");
            return peer;
        }
    }

    public void EscalateToRelayOnly(string reason)
    {
        if (_disposed) return;
        var requested = 0;
#if WINDOWS || ANDROID
        requested = _peerSession?.EscalateAllToRelay(Environment.TickCount64) ?? 0;
#endif
        VoiceDiagnostics.Log(
            "bcl.ice.escalate",
            $"reason={reason} peers={requested} relayAvailable={RelayAvailable()} managed={UsesManagedTurn()}");
    }

    public void RebuildIceConnectionPool()
    {
        if (_disposed) return;
        ResetTurnCredentialState();
        RefreshConfiguredIceServers("settings-changed");
        var forceRelay = ShouldForceRelay();
#if WINDOWS || ANDROID
        _peerSession?.RebuildAll(forceRelay && RelayAvailable(), Environment.TickCount64);
#endif
        if (forceRelay && UsesManagedTurn())
            EnsureManagedTurnCredentials(forceRelay: true, refreshRelayPeers: false);
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

    // Relay/connection recovery is handled by the sidecar/pc-mobile engine; the C# backend no longer
    // tracks per-peer escalation deferral. ponytail: dead control-plane knob, never defer.
    public bool ShouldDeferPeerEscalation => false;

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

        // The native sidecar/pc-mobile engine performs Opus encoding and WebRTC transmission. The C# capture
        // path now only computes local mic level/VAD for the overlay and diagnostics. ponytail: encode/send
        // tail removed (it fed the dead data-channel transport), not stubbed.
        _ = transmitGain;
        unchecked { _sendTimestamp += (uint)samples; }
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

    private void BuildCanonicalRouteMapLocked(Dictionary<int, PeerConnection> destination)
    {
        destination.Clear();
        foreach (var peer in _peersBySocket.Values)
        {
            if (peer.ClientId < 0) continue;
            if (destination.TryGetValue(peer.ClientId, out var current))
                destination[peer.ClientId] = ChooseDuplicateRouteWinner(current, peer);
            else
                destination[peer.ClientId] = peer;
        }
    }

    private PeerConnection? FindCanonicalRoutePeerLocked(int clientId)
    {
        PeerConnection? winner = null;
        foreach (var peer in _peersBySocket.Values)
        {
            if (peer.ClientId != clientId) continue;
            winner = winner == null ? peer : ChooseDuplicateRouteWinner(winner, peer);
        }
        return winner;
    }

    private void ApplyRemotePeerLevels(IReadOnlyList<SidecarProtocol.PeerLevel> levels)
    {
        if (_disposed || levels.Count == 0) return;
        var nowTicks = DateTime.UtcNow.Ticks;
        var mapped = 0;
        var unmapped = 0;
        lock (_peerSync)
        {
            BuildCanonicalRouteMapLocked(_canonicalRouteScratch);
            for (var i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                if (!int.TryParse(level.PeerId, NumberStyles.None, CultureInfo.InvariantCulture, out var clientId) ||
                    clientId < 0 ||
                    !_canonicalRouteScratch.TryGetValue(clientId, out var peer))
                {
                    unmapped++;
                    continue;
                }
                peer.RecordVoiceLevel(level.Peak, nowTicks);
                mapped++;
            }
        }
#if WINDOWS
        if (mapped > 0) Interlocked.Add(ref _sidecarPeerLevelsMappedSinceStats, mapped);
        if (unmapped > 0) Interlocked.Add(ref _sidecarPeerLevelsUnmappedSinceStats, unmapped);
#endif
    }

    internal string DescribeRpcPeerDiagnostics()
    {
#if WINDOWS || ANDROID
        var snapshot = _peerSession?.GetDiagnosticsSnapshot() ?? PeerSessionDiagnosticsSnapshot.Empty;
        return $"known={snapshot.KnownPeers}/compatible={snapshot.CompatiblePeers}/negotiating={snapshot.NegotiatingPeers}/established={snapshot.EstablishedPeers}/states={snapshot.PeerStates}";
#else
        return "unsupported";
#endif
    }

    private void EnsureSnapshotRoutePeers(VoiceGameStateSnapshot snapshot)
    {
        _snapshotRouteClientIds.Clear();
        foreach (var player in snapshot.Players)
        {
            if (player.IsLocal || player.Disconnected || player.IsDummy || player.ClientId < 0)
                continue;

            _snapshotRouteClientIds.Add(player.ClientId);
            PeerConnection routePeer;
            VoiceTeamRadioChannel savedRadio;
            var created = false;
            lock (_peerSync)
            {
                routePeer = FindCanonicalRoutePeerLocked(player.ClientId)!;
                if (routePeer == null)
                {
                    var routeId = RpcRoutePeerPrefix + player.ClientId.ToString(CultureInfo.InvariantCulture);
                    routePeer = new PeerConnection(routeId, player.ClientId, player.ClientId);
                    _peersBySocket[routeId] = routePeer;
                    created = true;
                }
                _radioStateByPlayerId.TryGetValue(player.PlayerId, out savedRadio);
            }

            if (routePeer.UpdateProfile(player.PlayerId, player.PlayerName))
                ApplySavedVolume(routePeer);
            if (routePeer.RadioChannel != savedRadio)
                routePeer.ApplyRadioChannel(savedRadio);
            if (created)
                VoiceDiagnostics.Log(
                    "voice.route-peer.created",
                    $"client={player.ClientId} player={player.PlayerId} source=among-us-snapshot");
        }

        _staleSnapshotRoutePeerIds.Clear();
        lock (_peerSync)
        {
            foreach (var pair in _peersBySocket)
            {
                if (!pair.Key.StartsWith(RpcRoutePeerPrefix, StringComparison.Ordinal) ||
                    _snapshotRouteClientIds.Contains(pair.Value.ClientId))
                    continue;
                _staleSnapshotRoutePeerIds.Add(pair.Key);
                if (pair.Value.PlayerId != byte.MaxValue)
                    _radioStateByPlayerId.Remove(pair.Value.PlayerId);
            }
        }
        foreach (var routeId in _staleSnapshotRoutePeerIds)
            RemovePeer(routeId);
    }

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

        lock (_peerSync)
            return _socketToClient.Keys.ToArray();
    }

    private void RemovePeer(string socketId)
    {
#if WINDOWS
        _voice?.RemovePeer(socketId);
#endif
        PeerConnection? peer;
        lock (_peerSync)
        {
            _peersBySocket.Remove(socketId, out peer);

            if (_socketToClient.Remove(socketId, out var clientId)
                && _clientToSocket.TryGetValue(clientId, out var mappedSocket)
                && string.Equals(mappedSocket, socketId, StringComparison.Ordinal))
                _clientToSocket.Remove(clientId);
            _pendingSignalsBySocket.Remove(socketId);

            RemoveSignalRejectLogEntriesLocked(socketId);
        }
        if (peer == null) return;
        peer.Dispose();
        VoiceDiagnostics.Log("voice.route.removed", $"socket={socketId} client={peer.ClientId} transportState=route-only");
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
            _radioStateByPlayerId.Clear();
        }
        foreach (var peer in peers)
        {
            peer.Dispose();
            VoiceDiagnostics.Log("voice.route.removed", $"socket={peer.SocketId} client={peer.ClientId} transportState=route-only reason=clear-all");
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
        var routeRecords = peers.Length;
        var engineRouteTargets = peers.Length == 0
            ? "none"
            : string.Join("|", peers.Select(peer => $"{peer.ClientId}:engine"));
        var effectiveRoutes = peers.Length == 0
            ? "none"
            : string.Join("|", peers.Select(peer =>
            {
                var route = peer.CurrentRoute;
                var gain = route.Audible
                    ? Math.Clamp(route.NormalVolume + route.GhostVolume + route.RadioVolume, 0f, 1f) * peer.ClientVolume
                    : 0f;
                return $"{peer.ClientId}:gain={gain:0.000},pan={route.Pan:0.000},mode={(int)route.FilterMode},clientVol={peer.ClientVolume:0.000},reason={route.Reason}";
            }));
        var rpcDiagnosticsText = "rpcState=unsupported";
#if WINDOWS || ANDROID
        var rpcDiagnostics = _peerSession?.GetDiagnosticsSnapshot() ?? PeerSessionDiagnosticsSnapshot.Empty;
        rpcDiagnosticsText =
            $"rpcKnown={rpcDiagnostics.KnownPeers} rpcCompatible={rpcDiagnostics.CompatiblePeers} rpcNegotiating={rpcDiagnostics.NegotiatingPeers} establishedPeers={rpcDiagnostics.EstablishedPeers} " +
            $"localCandidatesAttempted={rpcDiagnostics.LocalCandidatesAttempted} remoteCandidatesReceived={rpcDiagnostics.RemoteCandidatesReceived} remoteCandidatesForwarded={rpcDiagnostics.RemoteCandidatesForwarded} rejectedCandidates={rpcDiagnostics.RejectedCandidates} peerStates=\"{rpcDiagnostics.PeerStates}\"";
#endif
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
        var sidecarLevelEventsWindow = 0;
        var sidecarPeerLevelsMappedWindow = 0;
        var sidecarPeerLevelsUnmappedWindow = 0;
        long sidecarLevelEventsTotal = 0;
        long sidecarPeerLevelBatchesTotal = 0;
        long sidecarPeerLevelsTotal = 0;
        long sidecarLevelAgeMs = -1;
        long sidecarPeerLevelsAgeMs = -1;
#if WINDOWS
        sidecarLevelEventsWindow = FeedCaptureSupervisor(micWindowSamples);
        sidecarPeerLevelsMappedWindow = Interlocked.Exchange(ref _sidecarPeerLevelsMappedSinceStats, 0);
        sidecarPeerLevelsUnmappedWindow = Interlocked.Exchange(ref _sidecarPeerLevelsUnmappedSinceStats, 0);
        sidecarLevelEventsTotal = Interlocked.Read(ref _sidecarLevelEventsTotal);
        sidecarPeerLevelBatchesTotal = Interlocked.Read(ref _sidecarPeerLevelBatchesTotal);
        sidecarPeerLevelsTotal = Interlocked.Read(ref _sidecarPeerLevelsTotal);
        sidecarLevelAgeMs = DiagnosticAgeMs(Interlocked.Read(ref _lastSidecarLevelUtcTicks), now.Ticks);
        sidecarPeerLevelsAgeMs = DiagnosticAgeMs(Interlocked.Read(ref _lastSidecarPeerLevelsUtcTicks), now.Ticks);
#endif
        var micClipPct = micWindowSamples == 0 ? 0f : micNearClipSamples * 100f / micWindowSamples;
        var micZeroCrossRate = micWindowSamples <= 1 ? 0f : micZeroCrossings / (float)(micWindowSamples - 1);
        var micCrest = micRms <= 0.0 ? 0.0 : micPeak / micRms;
        var opusAvgBytes = opusFrames == 0 ? 0.0 : opusBytes / (double)opusFrames;
        VoiceDiagnostics.Log("voice.stats",
            $"reason={reason} room={RoomCode} region={Region} endpoint={ServerUrl} phase={snapshot?.Phase.ToString() ?? "none"} socketConnected={_socket?.Connected == true} socketId={_socket?.Id ?? "none"} " +
            $"routeRecords={routeRecords} engineRouteTargets={engineRouteTargets} localSocket={GetEffectiveLocalSocketId()} joinedClient={_joinedClientId} joinInFlight={Volatile.Read(ref _joinInFlight)} joinRetryAgeMs={(DateTime.UtcNow - _lastJoinAttemptUtc).TotalMilliseconds:0} audibleRoutes={peers.Count(peer => peer.CurrentRoute.Audible)} speakingRoutes={peers.Count(peer => peer.IsSpeaking)} {rpcDiagnosticsText} " +
            $"localLevel={LocalLevel:0.000} localSpeaking={LocalSpeaking} mute={Mute} remoteLevelMax={remoteMax:0.000} " +
            $"routeSamples={peerTicks} audibleTicks={audibleTicks} audibleSilentTicks={audibleSilentTicks} silentPct={silentPct:0.0} routeWindows={peerWindows} effectiveRoutes={effectiveRoutes} nativeJitter=not-exposed nativeBuffers=not-exposed legacyPeerJitter={peerJitter} legacyPeerBuffers={peerBuffers} " +
            $"sidecarLevelEventsWindow={sidecarLevelEventsWindow} sidecarLevelEventsTotal={sidecarLevelEventsTotal} sidecarLevelAgeMs={sidecarLevelAgeMs} sidecarPeerLevelBatchesTotal={sidecarPeerLevelBatchesTotal} sidecarPeerLevelsTotal={sidecarPeerLevelsTotal} sidecarPeerLevelsAgeMs={sidecarPeerLevelsAgeMs} sidecarPeerLevelsMappedWindow={sidecarPeerLevelsMappedWindow} sidecarPeerLevelsUnmappedWindow={sidecarPeerLevelsUnmappedWindow} " +
            $"managedLegacyEncodedTx={Volatile.Read(ref _encodedTx)} managedLegacyEncodedRx={Volatile.Read(ref _encodedRx)} managedLegacyCustomTx={Volatile.Read(ref _customTx)} managedLegacyCustomRx={Volatile.Read(ref _customRx)} " +
            $"managedMicCallbacks={Volatile.Read(ref _micCallbacks)} managedMicBytes={Volatile.Read(ref _micBytes)} managedMicSamples={Volatile.Read(ref _micSamples)} managedMicWindowSamples={micWindowSamples} managedMicPeak={micPeak:0.000000} managedMicRms={micRms:0.000000} managedMicCrest={micCrest:0.00} managedMicNonZeroSamples={micNonZeroSamples} managedMicSilentCallbacks={micSilentCallbacks} managedMicNearClipSamples={micNearClipSamples} managedMicClipPct={micClipPct:0.000} managedMicZeroCrossRate={micZeroCrossRate:0.0000} " +
            $"managedMicMutedDrops={Volatile.Read(ref _micMutedDrops)} managedMicEncodeFailures={Volatile.Read(ref _micEncodeFailures)} managedMicEncodedFrames={Volatile.Read(ref _micEncodedFrames)} managedMicNoOpenChannelDrops={Volatile.Read(ref _micNoOpenChannelDrops)} managedAudioDecodeFailures={Volatile.Read(ref _audioDecodeFailures)} " +
            $"noiseGate={noiseGateThreshold:0.000000} vadThreshold={vadThreshold:0.000000} gateReason={_lastGateReason} gatePeak={_lastGatePeak:0.000000} gateRms={_lastGateRms:0.000000} gateThreshold={_lastGateThreshold:0.000000} txGain={_lastTransmitGain:0.000} txPeakMax={txPeakMax:0.000000} txRms={txRms:0.000000} txSamples={txSamples} opusBytesAvg={opusAvgBytes:0.0} opusBytesMin={opusMinBytes} opusBytesMax={opusMaxBytes} " +
            $"syntheticTone={_captureOptions.SyntheticMicToneEnabled} noiseSuppression={_captureOptions.NoiseSuppressionEnabled} syntheticFrames={Volatile.Read(ref _syntheticFrames)} capture={DescribeCaptureMode()} calibration={_captureOptions.MicCalibrationDiagnostics} sensitivity={_captureOptions.MicSensitivity:0.00} micReady={_microphoneReady} speakerConfigured={_speakerReady} speakerMuted={VoiceChatHudState.IsSpeakerMuted} masterVolume={_masterVolume:0.000} plp={_adaptedPacketLossPercent} bitrate={_adaptedBitrate} recordDevice={Volatile.Read(ref _lastOpenedRecordDevice)} micDeadInput={Volatile.Read(ref _deadInputDetected)}");
        if (CaptureUsesUnity)
            VoiceDiagnostics.Log("bcl.unity",
                $"active=true mode={DescribeCaptureMode()} micReady={_microphoneReady} micCallbacks={Volatile.Read(ref _micCallbacks)} micSamples={Volatile.Read(ref _micSamples)} micPeak={micPeak:0.000000} micRms={micRms:0.000000} micSilentCallbacks={micSilentCallbacks} micEncodedFrames={Volatile.Read(ref _micEncodedFrames)} encodedTx={Volatile.Read(ref _encodedTx)} micMutedDrops={Volatile.Read(ref _micMutedDrops)} speakerReady={_speakerReady}");
        VoiceDiagnostics.Log("voice.playout.state",
            $"backend=engine configured={_speakerReady} speakerMuted={VoiceChatHudState.IsSpeakerMuted} masterVolume={_masterVolume:0.000} requestedDevice=\"{DescribeRequestedSpeakerDevice()}\" confirmed=unknown confirmationSource=native.media.stats-and-stderr");
    }

    private static long DiagnosticAgeMs(long eventUtcTicks, long nowUtcTicks)
    {
        if (eventUtcTicks <= 0) return -1;
        if (nowUtcTicks < eventUtcTicks) return 0;
        return (nowUtcTicks - eventUtcTicks) / TimeSpan.TicksPerMillisecond;
    }

    private string DescribeRequestedSpeakerDevice()
    {
#if WINDOWS
        return _lastSpeakerDeviceName;
#else
        return "platform-default";
#endif
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

    // The sidecar/pc-mobile engine owns each WebRTC peer connection, Opus decode, jitter buffering and the
    // mixer. The C# PeerConnection is now only a roster + proximity-route record: it carries the
    // socket/client/player identity and the computed gain/pan/radio used to build the engine game-state.
    private sealed class PeerConnection : IDisposable
    {
        private readonly object _sync = new();
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
        private static readonly TimeSpan RouteDivergenceLogInterval = TimeSpan.FromSeconds(2);
        private DateTime _lastRouteDivergenceLogUtc = DateTime.MinValue;
        private bool _disposed;

        public PeerConnection(string socketId, int clientId)
            : this(socketId, clientId, clientId)
        {
        }

        public PeerConnection(string socketId, int clientId, int playbackGroupId)
        {
            SocketId = socketId;
            ClientId = clientId;
            PlaybackGroupId = playbackGroupId;
        }

        public void FadeClearRoutes()
        {
            MuteAll();
        }

        // Loss reporting and keepalive used the C# data channel, which the sidecar/pc-mobile engine has
        // replaced. ponytail: kept as no-ops because the per-frame maintenance loop still calls them.
        public void StoreLossReport(int lossPermille) { }
        public void MaybeSendLossReport(DateTime nowUtc) { }
        public void MaybeSendKeepalive(DateTime nowUtc) { }

        private long _lastInboundTicks;
        public long LastInboundTicks => Interlocked.Read(ref _lastInboundTicks);
        public void StampInbound(long nowTicks) => Interlocked.Exchange(ref _lastInboundTicks, nowTicks);
        public void RebaseInbound(long nowTicks) => Interlocked.Exchange(ref _lastInboundTicks, nowTicks);
        public bool HasOpenedAtLeastOnce { get; set; }
        public DateTime SilentSinceLoggedUtc { get; set; } = DateTime.MinValue;

        public void ResetLiveness()
        {
            Interlocked.Exchange(ref _lastInboundTicks, 0);
            HasOpenedAtLeastOnce = false;
            SilentSinceLoggedUtc = DateTime.MinValue;
        }

        public string SocketId { get; }
        public int ClientId { get; private set; }
        public int PlaybackGroupId { get; }

        private volatile byte _playerId = byte.MaxValue;
        public byte PlayerId { get => _playerId; private set => _playerId = value; }
        public string PlayerName { get; private set; } = "Unknown";
        private volatile VoiceTeamRadioChannel _radioChannel = VoiceTeamRadioChannel.None;
        private volatile byte _radioChannelOwner = byte.MaxValue;
        private int _consecutiveNonRadioVoicePackets;
        private long _lastRadioChannelAppliedTicks;
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

        public void RecordVoiceLevel(float peak, long nowTicks)
        {
            peak = float.IsFinite(peak) ? Math.Clamp(peak, 0f, 1f) : 0f;
            StampInbound(nowTicks);

            // Preserve a speaking peak across the helper's intervening zero batches so the HUD
            // does not flicker at the 10 Hz telemetry cadence. A low/zero sample clears it once
            // the 250 ms activity hold has elapsed.
            var previous = Volatile.Read(ref _recentVoiceLevel);
            var previousTicks = Interlocked.Read(ref _lastVoiceLevelTicks);
            if (ShouldHoldPreviousRemoteLevel(previous, previousTicks, peak, nowTicks))
                return;

            Volatile.Write(ref _recentVoiceLevel, peak);
            Interlocked.Exchange(ref _lastVoiceLevelTicks, nowTicks);
        }

        public bool UpdateProfile(byte playerId, string playerName)
        {
            var normalized = string.IsNullOrWhiteSpace(playerName) ? "Unknown" : playerName;
            if (PlayerId == playerId && PlayerName == normalized) return false;

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
            PlayerId = byte.MaxValue;
        }
        public float ClientVolume { get; private set; } = 1f;
        public void SetVolume(float volume)
        {
            ClientVolume = Math.Clamp(volume, 0f, 2f);
        }
        public void MuteAll()
        {
            _currentRoute = VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);
            WallCoefficient = 1f;
            _appliedPan = 0f;
            _routeClearsSinceStats++;
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
        }

        public PeerDiagnostics ConsumeDiagnostics()
        {
            lock (_sync)
            {
                var result = new PeerDiagnostics(ClientId, _levelPeakSinceStats, _packetLevelPeakSinceStats, _samplesSinceStats, _audibleSamplesSinceStats, _audibleSilentSamplesSinceStats, _routeClearsSinceStats, _currentRoute, _appliedPan, default, "sidecar");
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

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
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
