using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class PerfectCommsVoiceBackend : IVoiceBackend
{
    private const float RemoteSpeakingThreshold = 0.004f;
    private const double SyntheticToneFrequency = 220.0;
    private const float SyntheticToneAmplitude = 0.012f;
    private static readonly TimeSpan RemoteActivityHold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MicCalibrationLogInterval = TimeSpan.FromSeconds(5);
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
    private readonly Dictionary<byte, VoiceTeamRadioChannel> _radioStateByPlayerId = new();

    private const string RpcRoutePeerPrefix = "rpc-route:";
    private readonly List<PeerConnection> _updatePeerScratch = new();
    private readonly Dictionary<int, PeerConnection> _routeClientScratch = new();
    private readonly Dictionary<int, PeerConnection> _canonicalRouteScratch = new();
    private readonly HashSet<int> _snapshotRouteClientIds = new();
    private readonly List<SidecarProtocol.GameStatePeerInput> _helperGameStatePeers = new(32);
    private readonly GameStateSendGate _gameStateSendGate = new();
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
    private int _turnFetchFailureRound;
    private int _turnConfigGeneration;
    private DateTime _turnFetchNotBeforeUtc = DateTime.MinValue;

    private static readonly TimeSpan ResumeFrameThreshold = TimeSpan.FromSeconds(2);
    private DateTime _lastStatsLogUtc = DateTime.MinValue;
    private int _micCallbacks;
    private int _micBytes;
    private int _micSamples;
    private int _micMutedDrops;
    private int _micProcessingFailures;
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
    private readonly object _micStatsLock = new();
    private float _micPeakSinceStats;
    private double _micSquareSumSinceStats;
    private int _micSamplesSinceStats;
    private int _micNonZeroSamplesSinceStats;
    private int _micSilentCallbacksSinceStats;
    private int _micNearClipSamplesSinceStats;
    private int _micZeroCrossingsSinceStats;
    private readonly float[] _captureFrameBuffer = new float[AudioHelpers.FrameSize];
    private int _captureFrameSamples;

    private int _captureEpoch;

#if ANDROID || WINDOWS
    private readonly object _unityEncodeSync = new();
    private readonly Queue<(float[] buffer, int samples, int epoch)> _unityEncodeQueue = new();
    private readonly float[] _unityCaptureAccum = new float[AudioHelpers.FrameSize];
    private int _unityCaptureFill;
    private long _unityEncodeDroppedFrames;
    private Thread? _unityEncodeWorker;
    private bool _unityEncodeStop;
    private const int UnityEncodeQueueMaxFrames = 16;
#endif

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
    private int _voiceUnavailableRetrying;
#endif
#if ANDROID
    private MobileVoiceClient? _mobileVoice;
    private AndroidEnginePcmSpeaker? _mobileSpeaker;
    private readonly AndroidMicrophoneMonitor _androidMicrophoneMonitor = new();
    private long _mobileVoiceStartRetryAtMs;
    private int _mobileVoiceStartFailures;
    private int _mobileVoiceGeneration;
    private int _mobileSpeakerRetryAttempts;
    private long _mobileSpeakerRetryAtMs;
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
    private int _voiceRecoveryScheduled;
    private int _voiceRecoveryBackoffAttempts;
    private long _voiceStartedAtTicks;
    private long _voiceSessionGeneration;
    private const int VoiceHeartbeatRestartBudget = 3;
    private static readonly TimeSpan VoiceStableThreshold = TimeSpan.FromSeconds(30);
    internal readonly record struct HeartbeatRecoveryDecision(
        bool RestartImmediately,
        int ImmediateRestarts,
        bool ResetRecoveryBackoff);
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
    private SignalingSubscription _rpcSubscription;
    private long _rpcRosterPollNextMs;
    private long _rpcSessionTickNextMs;
    private long _managedTurnRefreshPollNextMs;
    private const int RpcRosterPollIntervalMs = 250;
    private const int RpcSessionTickIntervalMs = 100;
    private const int ManagedTurnRefreshPollIntervalMs = 5_000;
    private const int NetworkChangeDebounceMs = 1_500;
    private const int NetworkChangeCooldownMs = 15_000;
    private long _networkChangeSignaledAtMs;
    private long _networkChangeLastAppliedMs;
    private bool _networkChangeSubscribed;
    private readonly HashSet<int> _rpcKnownClients = new();
    private readonly HashSet<int> _rpcPresentScratch = new();
    private readonly List<int> _rpcLeftScratch = new();
    private bool _rpcRosterGapActive;
#endif
    private Timer? _syntheticMicTimer;
    private string _lastMicDeviceName = string.Empty;
    private volatile float _micVolume = 1f;
    private float _noiseGateThreshold;
    private float _vadThreshold = 0.004f;
    private volatile float _localLevel;
    private volatile bool _localSpeaking;
#if WINDOWS
    private volatile bool _microphoneRequested;
    private volatile bool _sidecarCaptureAwaitingFirstLevel;
    private long _sidecarCaptureSourceGeneration;
    private long _sidecarCaptureProvenGeneration;
    private long _sidecarCaptureAttemptGeneration;
    private long _sidecarCaptureProvenAttemptGeneration;
    private long _sidecarCaptureAcceptAfterTimestamp = long.MaxValue;
    private long _sidecarCaptureConfirmationGeneration;
    private int _sidecarCaptureConfirmationCount;
    private const int SidecarFreshLevelConfirmationsRequired = 2;
    private static readonly long SidecarQueuedLevelGuardTicks =
        Math.Max(1L, System.Diagnostics.Stopwatch.Frequency / 4L);
#endif
    private bool _microphoneReady;
    private volatile bool _speakerReady;
    private VoiceCaptureRuntimeOptions _captureOptions;
    private const float DigitalSilencePeakThreshold = 0.0000001f;
    private const int DigitalSilenceTriggerFrames = 500;
    private int _digitalSilenceFrames;
    private int _digitalSilenceDetected;
    private int _lastOpenedRecordDevice = -1;

    private void TrackCaptureTelemetryLocked(float peak)
    {
        if (peak > DigitalSilencePeakThreshold)
        {
            _digitalSilenceFrames = 0;
            if (Interlocked.Exchange(ref _digitalSilenceDetected, 0) != 0)
            {
                VoiceDiagnostics.Log(
                    "voice.mic.digital-silence",
                    $"state=cleared device=selected recordDevice={Volatile.Read(ref _lastOpenedRecordDevice)} peak={peak:0.000000000}");
            }
            return;
        }

        if (_digitalSilenceFrames < DigitalSilenceTriggerFrames)
            _digitalSilenceFrames++;
        if (_digitalSilenceFrames < DigitalSilenceTriggerFrames) return;

        // Amplitude is never liveness. A hardware mute or a user who simply does not talk can
        // legitimately deliver zero-valued callbacks forever. Record that state once for device
        // diagnostics without classifying capture, signaling, or playback as dead.
        if (Interlocked.CompareExchange(ref _digitalSilenceDetected, 1, 0) == 0)
        {
            VoiceDiagnostics.Log(
                "voice.mic.digital-silence",
                $"state=observed impact=none device=selected recordDevice={Volatile.Read(ref _lastOpenedRecordDevice)} peak={peak:0.000000000}");
        }
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

    public PerfectCommsVoiceBackend(string roomCode, string region)
    {
        RoomCode = roomCode;
        Region = region;

        RefreshConfiguredIceServers("startup");
        // Fetch short-lived relay credentials in parallel with normal direct ICE setup. Direct
        // peers still use host/srflx candidates, but a symmetric-NAT failure can now escalate
        // immediately instead of waiting for a credential round trip after the timeout.
        if (UsesManagedTurn())
            EnsureManagedTurnCredentials(refreshRelayPeers: false);
#if WINDOWS || ANDROID
        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            _networkChangeSubscribed = true;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("voice.ice.network-change", $"subscribed=false errorType={ex.GetType().Name}");
        }
#endif
        VoiceDiagnostics.Log("voice.engine.created",
            $"{VoiceDiagnostics.DescribeRoom(RoomCode)} {VoiceDiagnostics.DescribeRegion(Region)} signaling=among-us-rpc");
        if (WineEnvironment.IsWine)
            VoiceDiagnostics.Log("env.wine", "detected=true");
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
        var customConfigured = TryGetCustomTurnServer(out var custom, out var customInvalid);

        if (customConfigured)
        {
            configured.Add(custom);
        }
        else if (!customInvalid)
        {
            lock (_turnSync)
                configured.AddRange(_managedIceServers);
        }

        configured = DeduplicateIceServers(configured);
        ApplyIceServers(configured);
        if (customInvalid)
            VoiceChatPluginMain.Logger.LogWarning(
                "[VC] Custom TURN configuration is invalid; automatic relay fallback is unavailable until the TURN URL, username, and credential are corrected.");
        VoiceDiagnostics.Log(
            "voice.ice.config",
            $"reason={reason} fallback=automatic source={(customConfigured ? "custom" : customInvalid ? "invalid-custom" : "managed")} " +
            $"relayAvailable={RelayAvailable()} iceServers={configured.Count}");
    }

    private void RequestRelayCredentials(int clientId)
    {
        if (_disposed) return;
        if (TryGetCustomTurnServer(out _, out _))
        {
            _mainThreadActions.Enqueue(() => _peerSession?.EscalatePeer(clientId, Environment.TickCount64));
            return;
        }
        if (!UsesManagedTurn()) return;

        lock (_turnSync)
            _pendingRelayPeerIds.Add(clientId);
        EnsureManagedTurnCredentials(refreshRelayPeers: false);
    }

    private void EnsureManagedTurnCredentials(bool refreshRelayPeers)
    {
        if (_disposed || !UsesManagedTurn()) return;

        var endpoint = TurnCredentialsUrl();
        if (TurnCredentialClient.TryGetFreshCached(DateTime.UtcNow, endpoint, out var cached) &&
            !TurnCredentialClient.NeedsRefresh(DateTime.UtcNow, endpoint))
        {
            CompleteManagedTurnCredentials(cached, refreshRelayPeers);
            return;
        }

        int generation;
        CancellationToken token;
        lock (_turnSync)
        {
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
                CompleteManagedTurnCredentials(servers, refreshRelayPeers: false, generation);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                lastError = ex;
                VoiceDiagnostics.Log("voice.turn", $"ready=false attempt={attempt}/3 error=\"{ex.Message}\"");
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
            "voice.turn",
            $"ready=false exhausted=true pendingPeers={PendingRelayPeerCount()} error=\"{lastError?.Message ?? "unknown"}\"");
    }

    private void CompleteManagedTurnCredentials(
        List<IceServer> servers,
        bool refreshRelayPeers,
        int? generation = null)
    {
        int[] pendingPeers;
        bool refreshAwaiting;
        CancellationTokenSource? completedCts;
        lock (_turnSync)
        {
            if (generation.HasValue && generation.Value != _turnConfigGeneration) return;
            _managedIceServers = new List<IceServer>(servers);
            pendingPeers = _pendingRelayPeerIds.ToArray();
            _pendingRelayPeerIds.Clear();
            refreshAwaiting = _turnRefreshRelayPeersAwaiting || refreshRelayPeers;
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
            var recreatedPeers = new HashSet<int>();
            foreach (var clientId in pendingPeers)
                if (manager.EscalatePeer(clientId, Environment.TickCount64))
                    recreatedPeers.Add(clientId);
            if (refreshAwaiting)
                manager.RefreshRelayPeers(Environment.TickCount64, recreatedPeers);
#endif
        });
        VoiceDiagnostics.Log(
            "voice.turn",
            $"ready=true iceServers={servers.Count} escalations={pendingPeers.Length} refresh={refreshAwaiting} policy=mixed");
    }

    private void MaybeRefreshManagedTurnCredentials()
    {
#if WINDOWS || ANDROID
        if (!UsesManagedTurn()) return;

        bool pendingPeers;
        bool refreshAwaiting;
        lock (_turnSync)
        {
            pendingPeers = _pendingRelayPeerIds.Count > 0;
            refreshAwaiting = _turnRefreshRelayPeersAwaiting;
        }
        if (ShouldRetryPendingTurnIntent(pendingPeers, refreshAwaiting))
        {
            EnsureManagedTurnCredentials(refreshAwaiting);
            return;
        }

        if (_peerSession?.HasRelayPeers != true) return;
        if (TurnCredentialClient.NeedsRefresh(DateTime.UtcNow, TurnCredentialsUrl()))
            EnsureManagedTurnCredentials(refreshRelayPeers: true);
#endif
    }

    internal static bool ShouldRetryPendingTurnIntent(
        bool pendingPeers,
        bool refreshAwaiting)
        => pendingPeers || refreshAwaiting;

    private void ResetTurnCredentialState()
    {
        CancellationTokenSource? cts;
        lock (_turnSync)
        {
            _turnConfigGeneration++;
            cts = _turnCts;
            _turnCts = null;
            _turnFetchInFlight = false;
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

    private static bool TryGetCustomTurnServer(out IceServer server, out bool invalid)
    {
        server = default;
        invalid = false;
        var raw = VoiceSettings.Instance?.TurnServerUrl.Value?.Trim() ?? string.Empty;
        if (raw.Length == 0) return false;
        var username = VoiceSettings.Instance?.TurnUsername.Value ?? string.Empty;
        var credential = VoiceSettings.Instance?.TurnCredential.Value ?? string.Empty;
        if (!TryCreateCustomTurnServer(raw, username, credential, out server))
        {
            invalid = true;
            return false;
        }
        return true;
    }

    internal static bool TryCreateCustomTurnServer(
        string? raw,
        string? username,
        string? credential,
        out IceServer server)
    {
        server = default;
        var trimmed = raw?.Trim() ?? string.Empty;
        username ??= string.Empty;
        credential ??= string.Empty;
        if (trimmed.Length > 2048 ||
            !TryNormalizeSupportedTurnUrl(trimmed, out var normalizedUrl) ||
            string.IsNullOrWhiteSpace(username) || username.Length > 512 || username.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(credential) || credential.Length > 512 || credential.Any(char.IsControl))
            return false;

        server = new IceServer(normalizedUrl, username, credential);
        return true;
    }

    private static bool UsesManagedTurn()
    {
        var hasCustom = TryGetCustomTurnServer(out _, out var invalid);
        return ShouldUseManagedTurnPolicy(hasCustom, invalid);
    }

    internal static bool ShouldUseManagedTurnPolicy(bool customConfigured, bool customInvalid)
        => !customConfigured && !customInvalid;

    internal static bool IsSupportedTurnUrl(string? url)
        => TryNormalizeSupportedTurnUrl(url, out _);

    internal static bool TryNormalizeSupportedStunUrl(string? url, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        const int schemeLength = 5;
        if (trimmed.Any(char.IsWhiteSpace) || trimmed.Any(char.IsControl) ||
            !trimmed.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Length <= schemeLength ||
            trimmed.IndexOf('?') >= 0 ||
            trimmed.IndexOf('#') >= 0 ||
            !TryNormalizeIceEndpoint(trimmed.Substring(schemeLength), out var endpoint))
            return false;

        normalizedUrl = "stun:" + endpoint;
        return true;
    }

    internal static bool TryNormalizeSupportedTurnUrl(string? url, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (trimmed.Any(char.IsWhiteSpace) || trimmed.Any(char.IsControl)) return false;
        var isTls = trimmed.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);
        var schemeLength = isTls ? 6 : 5;
        if (!isTls && !trimmed.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.Length <= schemeLength)
            return false;

        var queryIndex = trimmed.IndexOf('?');
        var endpointLength = (queryIndex < 0 ? trimmed.Length : queryIndex) - schemeLength;
        if (endpointLength <= 0 ||
            !TryNormalizeIceEndpoint(
                trimmed.Substring(schemeLength, endpointLength),
                out var endpoint))
            return false;
        if (queryIndex < 0)
        {
            if (trimmed.IndexOf('#') >= 0) return false;
            normalizedUrl = (isTls ? "turns:" : "turn:") + endpoint;
            return true;
        }
        var query = trimmed.Substring(queryIndex + 1);
        // Pion accepts either no query or exactly one decoded transport parameter. Parse a
        // stricter raw subset here: rejecting escapes and extra delimiters prevents encoded keys,
        // duplicates, or unknown options from being classified differently by the native parser.
        if (query.Length == 0 || query.IndexOf('&') >= 0 || query.IndexOf('%') >= 0 || query.IndexOf('#') >= 0)
            return false;
        var separator = query.IndexOf('=');
        if (separator <= 0 || query.IndexOf('=', separator + 1) >= 0 ||
            !string.Equals(query.Substring(0, separator), "transport", StringComparison.OrdinalIgnoreCase))
            return false;
        var transport = query.Substring(separator + 1);
        var isTcp = string.Equals(transport, "tcp", StringComparison.OrdinalIgnoreCase);
        // Pion maps turns+udp to TURN over DTLS/UDP, while turns+tcp uses TLS/TCP.
        var isUdp = string.Equals(transport, "udp", StringComparison.OrdinalIgnoreCase);
        if (!isTcp && !isUdp) return false;

        normalizedUrl = (isTls ? "turns:" : "turn:") +
                        endpoint +
                        "?transport=" + (isTcp ? "tcp" : "udp");
        return true;
    }

    private static bool TryNormalizeIceEndpoint(string rawEndpoint, out string normalizedEndpoint)
    {
        normalizedEndpoint = string.Empty;
        var endpoint = rawEndpoint;
        if (endpoint.StartsWith("//", StringComparison.Ordinal)) endpoint = endpoint.Substring(2);
        if (endpoint.Length == 0 ||
            endpoint.IndexOfAny(new[] { '@', '/', '\\', '?', '#', '%' }) >= 0)
            return false;

        string host;
        string? portText = null;
        if (endpoint[0] == '[')
        {
            var closeBracket = endpoint.IndexOf(']');
            if (closeBracket <= 1) return false;
            host = endpoint.Substring(1, closeBracket - 1);
            var suffix = endpoint.Substring(closeBracket + 1);
            if (suffix.Length > 0)
            {
                if (suffix[0] != ':' || suffix.Length == 1) return false;
                portText = suffix.Substring(1);
            }
        }
        else
        {
            if (endpoint.IndexOf('[') >= 0 || endpoint.IndexOf(']') >= 0) return false;
            var colon = endpoint.LastIndexOf(':');
            if (colon >= 0)
            {
                if (endpoint.IndexOf(':') != colon || colon == 0 || colon == endpoint.Length - 1)
                    return false;
                host = endpoint.Substring(0, colon);
                portText = endpoint.Substring(colon + 1);
            }
            else
            {
                host = endpoint;
            }
        }

        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl))
            return false;
        var hostType = Uri.CheckHostName(host);
        if (endpoint[0] == '[')
        {
            if (hostType != UriHostNameType.IPv6) return false;
        }
        else if (hostType is UriHostNameType.Unknown or UriHostNameType.IPv6)
        {
            return false;
        }
        if (portText != null &&
            (!portText.All(char.IsDigit) ||
             !int.TryParse(portText, out var port) ||
             port is < 1 or > 65535))
            return false;

        normalizedEndpoint = endpoint;
        return true;
    }

    private bool RelayAvailable()
    {
        if (TryGetCustomTurnServer(out _, out _)) return true;
        lock (_turnSync)
            return _managedIceServers.Any(server => IsSupportedTurnUrl(server.Urls)) &&
                   !TurnCredentialClient.IsExpired(DateTime.UtcNow, TurnCredentialsUrl());
    }

    internal static List<IceServer> DeduplicateIceServers(IEnumerable<IceServer> servers)
    {
        var result = new List<IceServer>();
        // TURN usernames/passwords are case-sensitive. Exact keys avoid collapsing a credential
        // rotation whose values differ only by case.
        var seen = new HashSet<string>(StringComparer.Ordinal);
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
    public bool UsingMicrophone
    {
        get
        {
#if WINDOWS
            var voice = _voice;
            return _microphoneReady
                   && _voiceReady
                   && voice != null
                   && voice.Health == CaptureHealth.Healthy;
#else
            return _microphoneReady;
#endif
        }
    }
    public bool UsingSpeaker => _speakerReady;
    internal bool IsInitializing
    {
        get
        {
#if WINDOWS
            return !_disposed
                   && (!_voiceReady || Volatile.Read(ref _voiceRecoveryScheduled) != 0);
#elif ANDROID
            return !_disposed && (_mobileVoice?.IsRunning != true || _mobileSpeaker == null);
#else
            return false;
#endif
        }
    }
    internal bool IsUnavailableRetrying
    {
        get
        {
#if WINDOWS || ANDROID
            return !_disposed && Volatile.Read(ref _voiceUnavailableRetrying) != 0;
#else
            return false;
#endif
        }
    }
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

    internal bool IsIncompatibleRemoteClient(int clientId)
    {
#if WINDOWS || ANDROID
        return _peerSession?.IsPeerIncompatible(clientId) == true;
#else
        return false;
#endif
    }

    internal bool IsRemoteClientEstablished(int clientId)
    {
#if WINDOWS || ANDROID
        return _peerSession?.IsPeerEstablished(clientId) == true;
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
#if WINDOWS
        SidecarVoiceLease? sidecarVoice;
        long sidecarSessionGeneration;
        long captureSourceGeneration;
        bool resumeSidecarDirectly;
        lock (_voiceSync)
        {
            sidecarVoice = _voice;
            sidecarSessionGeneration = Volatile.Read(ref _voiceSessionGeneration);
            captureSourceGeneration = Volatile.Read(ref _sidecarCaptureSourceGeneration);
            resumeSidecarDirectly = !mute && _microphoneReady && _voiceReady && sidecarVoice != null;
            if (!mute && captureSourceGeneration > 0)
                DisarmSidecarCaptureEvidenceLocked(awaitingFirstLevel: true);
        }
#endif
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
        {
            lock (_voiceSync)
            {
                captureSourceGeneration = Volatile.Read(ref _sidecarCaptureSourceGeneration);
                if (captureSourceGeneration > 0)
                    DisarmSidecarCaptureEvidenceLocked(awaitingFirstLevel: false);
            }
            sidecarVoice?.SetMicActive(false);
        }
        else if (resumeSidecarDirectly && sidecarVoice != null)
        {
            sidecarVoice.SetMicActive(true);
            lock (_voiceSync)
            {
                resumeSidecarDirectly = IsCurrentVoiceSessionLocked(
                    sidecarVoice,
                    sidecarSessionGeneration)
                    && ArmSidecarCaptureEvidenceLocked(captureSourceGeneration, captureActive: true);
            }
        }
        if (!mute && !resumeSidecarDirectly)
        {
            QueueMicrophoneTransition(true, "unmuted");
            if (!_voiceReady || _voice == null || _voice.Health != CaptureHealth.Healthy)
                ScheduleVoiceRecovery("unmuted", immediate: true);
        }
#elif ANDROID
        if (mute && !_loopBack) StopAndroidMicrophone("muted");
        else if (mute) _mobileVoice?.SetMicActive(false);
        else StartAndroidMicrophone("unmuted");
#endif
        VoiceDiagnostics.Log("voice.mute", $"mute={Mute} micReady={_microphoneReady} callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)}");
    }
    public void ToggleMute() => SetMute(!Mute);
    private volatile bool _loopBack;
    private volatile bool _loopBackDelayed;
    private volatile float _loopBackGain = 1f;

    public void SetLoopBack(bool loopBack, bool delayed, float gain)
    {
        delayed &= loopBack;
        gain = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 2f) : 1f;
        if (_loopBack == loopBack && _loopBackDelayed == delayed &&
            Math.Abs(_loopBackGain - gain) < 0.0001f) return;
        _loopBack = loopBack;
        _loopBackDelayed = delayed;
        _loopBackGain = gain;
#if WINDOWS
        _voice?.SetMonitor(loopBack, delayed, gain);
#elif ANDROID
        _androidMicrophoneMonitor.Configure(loopBack, delayed, gain * _micVolume);
        if (loopBack && Mute)
            StartAndroidMicrophone("microphone-test");
        else if (!loopBack && Mute)
            StopAndroidMicrophone("microphone-test-ended");
#endif
    }
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
        _voice?.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
#elif ANDROID
        _mobileVoice?.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
        _androidMicrophoneMonitor.Configure(_loopBack, _loopBackDelayed, _loopBackGain * _micVolume);
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
        _voice?.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
#elif ANDROID
        _mobileVoice?.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
#endif
    }

    public void SetCaptureRuntimeOptions(VoiceCaptureRuntimeOptions options)
    {
#if WINDOWS || ANDROID
        var restartCapture = _captureOptions.SyntheticMicToneEnabled != options.SyntheticMicToneEnabled;
#endif
#if WINDOWS
        var captureSourceGeneration = restartCapture
            ? BeginSidecarCaptureSourceGeneration(
                awaitingFirstLevel: !Mute && _microphoneRequested && _voiceReady)
            : 0;
#endif
        _captureOptions = options;
        // Lifting the signal before suppression also lifts wind and keyboard transients during
        // speech, so the native capture path deliberately runs without automatic gain.
        const bool automaticMicGain = false;

#if WINDOWS
        SidecarVoiceLease? captureVoice;
        long captureSessionGeneration;
        lock (_voiceSync)
        {
            captureVoice = _voice;
            captureSessionGeneration = Volatile.Read(ref _voiceSessionGeneration);
        }
        captureVoice?.SetDsp(
            options.EchoCancellationEnabled,
            automaticMicGain,
            options.NoiseSuppressionEnabled,
            options.StrongerNoiseSuppressionEnabled,
            true);
        captureVoice?.SetSynthetic(options.SyntheticMicToneEnabled);
        captureVoice?.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
        if (restartCapture)
        {
            var captureHealth = captureVoice?.Health ?? CaptureHealth.Dead;
            lock (_voiceSync)
            {
                var captureActive = !Mute
                                    && _microphoneRequested
                                    && _voiceReady
                                    && captureVoice != null
                                    && IsCurrentVoiceSessionLocked(captureVoice, captureSessionGeneration)
                                    && captureHealth == CaptureHealth.Healthy;
                ArmSidecarCaptureEvidenceLocked(captureSourceGeneration, captureActive);
            }
        }
        EnsureCaptureSupervisor();
#elif ANDROID
        // Android intentionally uses managed synthetic PCM pushed into pc-mobile; keep native
        // synthetic generation off and the APM/DSP path disabled.
        _mobileVoice?.SetSynthetic(false);
        _mobileVoice?.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
        if (restartCapture && !Mute && _microphoneReady)
            StartAndroidMicrophone("capture-options");
#endif
        VoiceDiagnostics.Log("voice.capture-options",
            $"capture={DescribeCaptureMode()} syntheticTone={options.SyntheticMicToneEnabled} noiseSuppression={options.NoiseSuppressionEnabled} strongerNoiseSuppression={options.NoiseSuppressionEnabled && options.StrongerNoiseSuppressionEnabled} automaticMicGain={automaticMicGain} calibration={options.MicCalibrationDiagnostics} sensitivity={options.MicSensitivity:0.00}");
    }

    public void RebuildCaptureSupervisor()
    {
#if WINDOWS
        BuildCaptureSupervisor();
#endif
    }

#if WINDOWS
    private long BeginSidecarCaptureSourceGeneration(bool awaitingFirstLevel)
    {
        lock (_voiceSync)
            return BeginSidecarCaptureSourceGenerationLocked(awaitingFirstLevel);
    }

    private long BeginSidecarCaptureSourceGenerationLocked(bool awaitingFirstLevel)
    {
        var generation = Interlocked.Increment(ref _sidecarCaptureSourceGeneration);
        Volatile.Write(ref _sidecarCaptureProvenGeneration, 0);
        _microphoneReady = false;
        DisarmSidecarCaptureEvidenceLocked(awaitingFirstLevel);
        return generation;
    }

    private long DisarmSidecarCaptureEvidenceLocked(bool awaitingFirstLevel)
    {
        var attemptGeneration = Interlocked.Increment(ref _sidecarCaptureAttemptGeneration);
        Volatile.Write(ref _sidecarCaptureProvenAttemptGeneration, 0);
        Volatile.Write(ref _sidecarCaptureAcceptAfterTimestamp, long.MaxValue);
        Volatile.Write(ref _sidecarCaptureConfirmationGeneration, attemptGeneration);
        Volatile.Write(ref _sidecarCaptureConfirmationCount, 0);
        _sidecarCaptureAwaitingFirstLevel = awaitingFirstLevel;
        if (awaitingFirstLevel)
            _microphoneReady = false;
        return attemptGeneration;
    }

    private bool ArmSidecarCaptureEvidenceLocked(long generation, bool captureActive)
    {
        if (generation <= 0 || generation != Volatile.Read(ref _sidecarCaptureSourceGeneration))
            return false;

        var attemptGeneration = Interlocked.Increment(ref _sidecarCaptureAttemptGeneration);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var acceptAfter = now > long.MaxValue - SidecarQueuedLevelGuardTicks
            ? long.MaxValue
            : now + SidecarQueuedLevelGuardTicks;
        Volatile.Write(ref _sidecarCaptureAcceptAfterTimestamp, acceptAfter);
        Volatile.Write(ref _sidecarCaptureProvenAttemptGeneration, 0);
        Volatile.Write(ref _sidecarCaptureConfirmationGeneration, attemptGeneration);
        Volatile.Write(ref _sidecarCaptureConfirmationCount, 0);
        _sidecarCaptureAwaitingFirstLevel = captureActive;
        if (captureActive)
            _microphoneReady = false;
        return true;
    }

    internal static bool IsCaptureSourceProven(long sourceGeneration, long provenGeneration)
        => sourceGeneration > 0 && sourceGeneration == provenGeneration;

    internal static bool ShouldPromoteSidecarLevel(
        long attemptGeneration,
        long confirmationGeneration,
        int priorConfirmationCount,
        long nowTimestamp,
        long acceptAfterTimestamp)
        => attemptGeneration > 0
           && attemptGeneration == confirmationGeneration
           && nowTimestamp >= acceptAfterTimestamp
           && priorConfirmationCount + 1 >= SidecarFreshLevelConfirmationsRequired;

    internal static bool IsCurrentVoiceStart(
        long taskGeneration,
        long currentGeneration,
        bool leaseMatches,
        bool disposed)
        => !disposed && leaseMatches && taskGeneration == currentGeneration;

    private bool IsCurrentVoiceSessionLocked(SidecarVoiceLease voice, long sessionGeneration)
        => IsCurrentVoiceStart(
            sessionGeneration,
            Volatile.Read(ref _voiceSessionGeneration),
            ReferenceEquals(_voice, voice),
            _disposed);

    private bool IsCurrentVoiceSession(SidecarVoiceLease voice, long sessionGeneration)
    {
        lock (_voiceSync)
            return IsCurrentVoiceSessionLocked(voice, sessionGeneration);
    }

    private bool TryDetachCurrentVoiceSession(SidecarVoiceLease voice, long sessionGeneration)
    {
        lock (_voiceSync)
        {
            if (!IsCurrentVoiceSessionLocked(voice, sessionGeneration))
                return false;

            Interlocked.Increment(ref _voiceSessionGeneration);
            _voiceReady = false;
            _speakerReady = false;
            _microphoneReady = false;
            _voice = null;
            _gameStateSendGate.Reset();
            BeginSidecarCaptureSourceGenerationLocked(awaitingFirstLevel: false);
            return true;
        }
    }
#endif

    internal static bool ShouldRestartMicrophoneSelection(
        string previousDeviceId,
        string currentDeviceId,
        bool forceRestart)
        => forceRestart ||
           !string.Equals(previousDeviceId, currentDeviceId, StringComparison.Ordinal);

    public void SetMicrophone(string deviceName, float volume, bool forceRestart = false)
    {
        var normalizedName = deviceName ?? string.Empty;
        var restartSidecar = ShouldRestartMicrophoneSelection(
            _lastMicDeviceName, normalizedName, forceRestart);
        _lastMicDeviceName = normalizedName;
        _micVolume = NormalizeMicGain(volume);
#if WINDOWS
        if (restartSidecar)
        {
            _btProfileConflict = false;
            BeginSidecarCaptureSourceGeneration(
                awaitingFirstLevel: !Mute && _microphoneRequested && _voiceReady);
        }
        if (Mute)
        {
            QueueMicrophoneTransition(false, "set-muted");
            VoiceDiagnostics.Log("voice.mic", $"ready=false muted=true device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)}");
            return;
        }

        QueueMicrophoneTransition(true, "settings", restartSidecar);
#elif ANDROID
        if (Mute)
        {
            StopAndroidMicrophone("set-muted");
            VoiceDiagnostics.Log("voice.mic", $"ready=false muted=true device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)}");
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
            onAllFailed: RecoverExhaustedCaptureSession,
            restartBudget: restartBudget);
    }

    private void RecoverExhaustedCaptureSession(string reason)
    {
        var sourceGeneration = Volatile.Read(ref _sidecarCaptureSourceGeneration);
        var provenGeneration = Volatile.Read(ref _sidecarCaptureProvenGeneration);
        var currentSourceProven = IsCaptureSourceProven(sourceGeneration, provenGeneration);
        if (!ShouldRebuildAfterCaptureExhausted(
                Mute,
                _microphoneRequested,
                currentSourceProven,
                _disposed))
        {
            // A capture command that never produced a callback is an unavailable microphone, not
            // a failed voice session. Leave WebRTC and speaker playback intact while native device
            // retry continues in the helper.
            _microphoneReady = false;
            _sidecarCaptureAwaitingFirstLevel = false;
            VoiceDiagnostics.Log(
                "voice.mic.degraded",
                $"code=capture-callback-unavailable reason={reason} action=listen-only helperReady={_voiceReady}");
            return;
        }

        // The supervisor already attempted bounded in-place stop/start recovery. At this point a
        // previously-working CPAL callback may be wedged while helper pongs remain healthy, so a full
        // helper/media rebuild is the only reliable recovery boundary. A never-present/denied mic is
        // explicitly excluded above and stays in quiet listen-only mode without restart churn.
        _microphoneReady = false;
        _sidecarCaptureAwaitingFirstLevel = false;
        VoiceDiagnostics.Log(
            "voice.mic.recovery",
            $"reason={reason} action=rebuild-helper sourceGeneration={sourceGeneration} previouslyProduced=true");
        try { VoiceChatHudState.ShowToastThreadSafe("Microphone stalled - restarting audio helper"); } catch { }
        StopVoiceSession("capture-supervisor-exhausted");
        _peerSession?.ResetAndNotify("capture-supervisor-exhausted");
        _rpcKnownClients.Clear();
        BuildCaptureSupervisor();
        ScheduleVoiceRecovery("capture-supervisor-exhausted", immediate: true);
    }

    internal static bool ShouldRebuildAfterCaptureExhausted(
        bool muted,
        bool microphoneRequested,
        bool captureEverProduced,
        bool disposed)
        => !disposed && !muted && microphoneRequested && captureEverProduced;

    private void ApplyCaptureSwitch(int index, string reason)
    {
        var from = _captureSlots[_supervisorActiveIndex];
        var to = _captureSlots[index];
        _supervisorActiveIndex = index;
        _activeCaptureSlot = to;
        VoiceDiagnostics.Log("voice.mic.capture-switch",
            $"reason={reason} from={from} to={to} index={index} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} wine={WineEnvironment.IsWine}");
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

            long startedTicks = Interlocked.Read(ref _voiceStartedAtTicks);
            long nowTicks = DateTime.UtcNow.Ticks;
            long uptimeTicks = startedTicks > 0 && nowTicks >= startedTicks
                ? nowTicks - startedTicks
                : 0;
            var decision = DecideHeartbeatRecovery(_voiceHeartbeatRestarts, uptimeTicks);
            _voiceHeartbeatRestarts = decision.ImmediateRestarts;
            if (decision.ResetRecoveryBackoff)
                Interlocked.Exchange(ref _voiceRecoveryBackoffAttempts, 0);

            if (!decision.RestartImmediately)
            {
                StopVoiceSession("voice-unavailable");
                _peerSession?.ResetAndNotify("voice-heartbeat-restarts-exhausted");
                _rpcKnownClients.Clear();
                EnterVoiceUnavailable($"heartbeat-lost detail={reason} restarts-exhausted={_voiceHeartbeatRestarts}/{VoiceHeartbeatRestartBudget}");
                // Do not refund the immediate-restart budget here and do not clear the recovery
                // counter after a short successful start. Repeated connect-then-die flaps therefore
                // progress through 750ms, 1.5s, 3s ... up to the 30s cap. Only a session which
                // actually survives VoiceStableThreshold earns a fresh budget.
                ScheduleVoiceRecovery("heartbeat-circuit-breaker");
                return;
            }

            VoiceDiagnostics.Log("voice.sidecar", $"reason=heartbeat-lost detail={reason} restart={_voiceHeartbeatRestarts}/{VoiceHeartbeatRestartBudget}");
            StopVoiceSession("heartbeat-lost");
            EnsureVoiceSession("heartbeat-lost");

            _peerSession?.ResetAndNotify("voice-heartbeat-restart");
            _rpcKnownClients.Clear();
        });
    }

    // Single chokepoint for "voice cannot run and there is no desktop fallback" (BASS removed in 4.0):
    // always logs to the BepInEx log (not the debug-gated diagnostics file) and shows the user an
    // on-screen banner, so a terminal failure is never silent.
    private void EnterVoiceUnavailable(string detail)
    {
        Interlocked.Exchange(ref _voiceUnavailableRetrying, 1);
        VoiceChatPluginMain.Logger.LogWarning(
            $"[VC] Voice unavailable: {detail}. No desktop fallback (BASS removed in 4.0). " +
            $"slot={_activeCaptureSlot} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} wine={WineEnvironment.IsWine} " +
            "hint=check OS mic permission / antivirus quarantine / helper extraction");
        VoiceDiagnostics.Log("voice.sidecar.unavailable", detail);
        try { VoiceChatHudState.ShowToastThreadSafe("Voice unavailable - retrying audio helper"); } catch { }
    }

    internal static int VoiceRecoveryDelayMs(int priorAttempts)
    {
        var shift = Math.Min(Math.Max(priorAttempts, 0), 6);
        return Math.Min(30_000, 750 * (1 << shift));
    }

    internal static HeartbeatRecoveryDecision DecideHeartbeatRecovery(
        int priorImmediateRestarts,
        long uptimeTicks)
    {
        var stable = uptimeTicks >= VoiceStableThreshold.Ticks;
        var usedRestarts = stable
            ? 0
            : Math.Min(Math.Max(priorImmediateRestarts, 0), VoiceHeartbeatRestartBudget);
        if (usedRestarts < VoiceHeartbeatRestartBudget)
        {
            return new HeartbeatRecoveryDecision(
                RestartImmediately: true,
                ImmediateRestarts: usedRestarts + 1,
                ResetRecoveryBackoff: stable);
        }

        return new HeartbeatRecoveryDecision(
            RestartImmediately: false,
            ImmediateRestarts: usedRestarts,
            ResetRecoveryBackoff: false);
    }

    private void RefreshVoiceRecoveryBudgetAfterStableUptime()
    {
        if (!_voiceReady)
            return;
        var startedTicks = Interlocked.Read(ref _voiceStartedAtTicks);
        var nowTicks = DateTime.UtcNow.Ticks;
        if (startedTicks <= 0
            || nowTicks < startedTicks
            || nowTicks - startedTicks < VoiceStableThreshold.Ticks)
            return;
        if (_voiceHeartbeatRestarts == 0 && Volatile.Read(ref _voiceRecoveryBackoffAttempts) == 0)
            return;

        _voiceHeartbeatRestarts = 0;
        Interlocked.Exchange(ref _voiceRecoveryBackoffAttempts, 0);
        VoiceDiagnostics.Log(
            "voice.sidecar.recovery",
            $"event=budget-reset stableMs={(nowTicks - startedTicks) / TimeSpan.TicksPerMillisecond}");
    }

    private void ScheduleVoiceRecovery(string reason, bool immediate = false)
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _voiceRecoveryScheduled, 1, 0) != 0)
            return;

        var priorAttempts = Interlocked.Increment(ref _voiceRecoveryBackoffAttempts) - 1;
        var delayMs = immediate ? 0 : VoiceRecoveryDelayMs(priorAttempts);
        VoiceDiagnostics.Log(
            "voice.sidecar.recovery",
            $"event=scheduled reason={reason} attempt={priorAttempts + 1} delayMs={delayMs}");

        Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs).ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _voiceRecoveryScheduled, 0);
            }

            if (_disposed) return;
            EnsureVoiceSession("recovery:" + reason);
        });
    }

    private void EnsureVoiceSession(string reason)
    {
        lock (_voiceSync)
        {
            // Recovery tasks are delayed off-thread. Dispose can win after their outer
            // _disposed check but before this method is entered; never let that stale task
            // reacquire the process-lifetime lease and relaunch a helper for a closed room.
            if (_disposed) return;
            if (_voice != null) return;
            var sessionGeneration = Interlocked.Increment(ref _voiceSessionGeneration);
            var callbacks = new SidecarVoiceCallbacks(
                ProcessBassMicFrame,
                OnSidecarHeartbeatLost,
                (code, message) => OnSidecarRecoverableError(sessionGeneration, code, message),
                OnHelperLocalSdp,
                OnHelperLocalCandidate,
                OnHelperPeerState,
                (peak, speaking) => OnSidecarLevel(sessionGeneration, peak, speaking),
                OnSidecarPeerLevels,
                _ => { });
            var voice = SidecarVoiceHost.TryAcquire(callbacks, out var failure);
            if (voice == null)
            {
                _microphoneReady = false;
                VoiceDiagnostics.Log("voice.sidecar", $"ready=false reason={reason} error=\"sidecar host lease rejected: {failure}\"");
                HandleVoiceStartFailure("lease-rejected", reason);
                return;
            }
            _voiceReady = false;
            _speakerReady = false;
            BeginSidecarCaptureSourceGenerationLocked(awaitingFirstLevel: false);
            _voice = voice;
            _voiceStartTask = Task.Run(() => RunVoiceStart(voice, sessionGeneration, reason));
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

    private void OnSidecarRecoverableError(long sessionGeneration, string code, string message)
    {
        if (_disposed) return;
        _mainThreadActions.Enqueue(() =>
        {
            if (_disposed
                || sessionGeneration != Volatile.Read(ref _voiceSessionGeneration)
                || !IsRecoverableSidecarMicError(code)) return;
            _microphoneReady = false;
            _sidecarCaptureAwaitingFirstLevel = false;
            _localLevel = 0f;
            _localSpeaking = false;
            VoiceDiagnostics.Log(
                "voice.mic.degraded",
                $"code={code} requested={_microphoneRequested} helperReady={_voiceReady} action=native-retry");
            try { VoiceChatHudState.ShowToastThreadSafe("Microphone unavailable - retrying device"); } catch { }
        });
    }

    internal static bool IsRecoverableSidecarMicError(string? code)
        => SidecarVoiceClient.IsRecoverableHelperError(code);

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
            VoiceDiagnostics.Log(
                "voice.ice.peer-state",
                $"client={clientId} generation={generation} state={state} policy=automatic-mixed");
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

    private void OnSidecarLevel(long sessionGeneration, float peak, bool speaking)
    {
        var recovered = false;
        long sourceGeneration;
        long attemptGeneration;
        lock (_voiceSync)
        {
            if (_disposed
                || sessionGeneration != Volatile.Read(ref _voiceSessionGeneration)
                || _voice == null)
                return;

            Interlocked.Increment(ref _sidecarLevelEventsTotal);
            Interlocked.Exchange(ref _lastSidecarLevelUtcTicks, DateTime.UtcNow.Ticks);

            if (_loopBack && _voiceReady && (Mute || !_microphoneRequested))
            {
                _localLevel = peak;
                _localSpeaking = speaking;
                return;
            }

            // A final queued level can arrive just after mute/stop or a device/source command. It is
            // useful raw telemetry, but it must not establish capture provenance for the new source.
            // pc-capture publishes at 100 ms through a latest-wins one-slot mailbox; hold the new
            // attempt closed for 250 ms, then require two cadence events. One queued pre-command
            // event therefore cannot promote a no-mic client into whole-helper recovery.
            var activeCapture = _microphoneRequested && !Mute && _voiceReady;
            if (!activeCapture)
            {
                _localLevel = 0f;
                _localSpeaking = false;
                return;
            }

            sourceGeneration = Volatile.Read(ref _sidecarCaptureSourceGeneration);
            attemptGeneration = Volatile.Read(ref _sidecarCaptureAttemptGeneration);
            var confirmationGeneration = Volatile.Read(ref _sidecarCaptureConfirmationGeneration);
            var priorConfirmationCount = confirmationGeneration == attemptGeneration
                ? Volatile.Read(ref _sidecarCaptureConfirmationCount)
                : 0;
            if (confirmationGeneration != attemptGeneration)
            {
                Volatile.Write(ref _sidecarCaptureConfirmationGeneration, attemptGeneration);
                Volatile.Write(ref _sidecarCaptureConfirmationCount, 0);
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var acceptAfter = Volatile.Read(ref _sidecarCaptureAcceptAfterTimestamp);
            var promotesAttempt = IsCaptureSourceProven(
                                      attemptGeneration,
                                      Volatile.Read(ref _sidecarCaptureProvenAttemptGeneration))
                                  || ShouldPromoteSidecarLevel(
                                      attemptGeneration,
                                      attemptGeneration,
                                      priorConfirmationCount,
                                      now,
                                      acceptAfter);
            if (now < acceptAfter || attemptGeneration <= 0)
            {
                _localLevel = 0f;
                _localSpeaking = false;
                return;
            }

            Volatile.Write(
                ref _sidecarCaptureConfirmationCount,
                Math.Min(SidecarFreshLevelConfirmationsRequired, priorConfirmationCount + 1));
            if (!promotesAttempt)
            {
                _localLevel = 0f;
                _localSpeaking = false;
                return;
            }

            Volatile.Write(ref _sidecarCaptureProvenAttemptGeneration, attemptGeneration);
            if (!IsCaptureSourceProven(
                    sourceGeneration,
                    Volatile.Read(ref _sidecarCaptureProvenGeneration)))
                Volatile.Write(ref _sidecarCaptureProvenGeneration, sourceGeneration);
            _sidecarCaptureAwaitingFirstLevel = false;
            if (!_microphoneReady)
            {
                _microphoneReady = true;
                recovered = true;
            }
            _localLevel = peak;
            _localSpeaking = speaking;
        }

        Interlocked.Increment(ref _sidecarCaptureActivitySinceStats);
        if (recovered)
            VoiceDiagnostics.Log(
                "voice.mic.recovered",
                $"source=sidecar-level nativeRetry=true sourceGeneration={sourceGeneration} attemptGeneration={attemptGeneration}");
    }

    private void OnSidecarPeerLevels(IReadOnlyList<SidecarProtocol.PeerLevel> levels)
    {
        if (levels.Count == 0 || _disposed) return;
        Interlocked.Increment(ref _sidecarPeerLevelBatchesTotal);
        Interlocked.Add(ref _sidecarPeerLevelsTotal, levels.Count);
        Interlocked.Exchange(ref _lastSidecarPeerLevelsUtcTicks, DateTime.UtcNow.Ticks);
        _mainThreadActions.Enqueue(() => ApplyRemotePeerLevels(levels));
    }

    private void RunVoiceStart(SidecarVoiceLease voice, long sessionGeneration, string reason)
    {
        // The Task can begin after room teardown and even after a later room acquired a new lease.
        // It must prove both lease identity and the backend session generation before every global
        // state mutation; disposing a stale lease is safe because the host rejects non-owners.
        if (!IsCurrentVoiceSession(voice, sessionGeneration))
        {
            try { voice.Dispose(); } catch { }
            return;
        }

        bool started;
        try
        {
            started = voice.EnsureStarted(_lastMicDeviceName, _lastSpeakerDeviceName);
        }
        catch (Exception ex)
        {
            if (IsCurrentVoiceSession(voice, sessionGeneration))
                VoiceDiagnostics.Log("voice.sidecar", $"ready=false reason={reason} error=\"{ex.Message}\"");
            started = false;
        }

        if (!started || !IsCurrentVoiceSession(voice, sessionGeneration))
        {
            var detachedCurrent = TryDetachCurrentVoiceSession(voice, sessionGeneration);
            try { voice.Dispose(); } catch { }
            if (detachedCurrent && !started)
            {
                VoiceDiagnostics.Log("voice.sidecar", $"ready=false reason={reason} error=\"sidecar voice start failed\"");
                HandleVoiceStartFailure("cold-start", reason);
            }
            return;
        }

        List<IceServer>? pendingIce;
        string currentMic;
        string currentSpk;
        bool micActive;
        long sourceGeneration;
        var staleBeforeConfiguration = false;
        lock (_voiceSync)
        {
            if (!IsCurrentVoiceSessionLocked(voice, sessionGeneration))
            {
                staleBeforeConfiguration = true;
            }
            if (!staleBeforeConfiguration)
            {
                Interlocked.Exchange(ref _voiceStartedAtTicks, DateTime.UtcNow.Ticks);
                pendingIce = _pendingIceServers;
                currentMic = _lastMicDeviceName;
                currentSpk = _lastSpeakerDeviceName;
                micActive = !Mute && _microphoneRequested;
                sourceGeneration = Volatile.Read(ref _sidecarCaptureSourceGeneration);
                // Initial configuration is a stop/select/synthetic/start command group. Disarm before
                // sending it so no callback queued by a pre-configuration producer can mark it ready.
                DisarmSidecarCaptureEvidenceLocked(awaitingFirstLevel: micActive);
            }
            else
            {
                pendingIce = null;
                currentMic = string.Empty;
                currentSpk = string.Empty;
                micActive = false;
                sourceGeneration = 0;
            }
        }
        if (staleBeforeConfiguration)
        {
            try { voice.Dispose(); } catch { }
            return;
        }

        var configured = voice.TryConfigureInitialCapture(
            currentMic,
            currentSpk,
            _captureOptions.EchoCancellationEnabled,
            false,
            _captureOptions.NoiseSuppressionEnabled,
            _captureOptions.StrongerNoiseSuppressionEnabled,
            true,
            _micVolume,
            _vadThreshold,
            _noiseGateThreshold,
            _captureOptions.SyntheticMicToneEnabled,
            micActive,
            pendingIce);
        if (!configured)
        {
            var detachedCurrent = TryDetachCurrentVoiceSession(voice, sessionGeneration);
            try { voice.Dispose(); } catch { }
            if (detachedCurrent)
            {
                VoiceDiagnostics.Log("voice.sidecar", $"ready=false reason={reason} error=\"initial sidecar configuration failed\"");
                HandleVoiceStartFailure("initial-configuration", reason);
            }
            return;
        }

        voice.SetMonitor(_loopBack, _loopBackDelayed, _loopBackGain);

        var outputDevices = voice.OutputDevices.ToArray();
        var health = voice.Health;
        var accepted = false;
        lock (_voiceSync)
        {
            if (IsCurrentVoiceSessionLocked(voice, sessionGeneration)
                && health == CaptureHealth.Healthy)
            {
                _voiceReady = true;
                Interlocked.Exchange(ref _voiceUnavailableRetrying, 0);
                _speakerReady = true;
                // A fresh native GameState starts empty. Force the first mixer snapshot through
                // even if the previous helper sent the same fingerprint less than one second ago.
                _gameStateSendGate.Reset();
                ArmSidecarCaptureEvidenceLocked(sourceGeneration, micActive);
                StartVoicePump();
                if (outputDevices.Length > 0)
                    VoiceChatLocalSettings.SetSpkDevicesFromSidecar(outputDevices);
                _voiceColdStartRetries = 0;
                Interlocked.Exchange(
                    ref _speakerTopologyFastUntilTicks,
                    (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
                accepted = true;
            }
        }
        if (!accepted)
        {
            var detachedCurrent = TryDetachCurrentVoiceSession(voice, sessionGeneration);
            try { voice.Dispose(); } catch { }
            if (detachedCurrent)
                HandleVoiceStartFailure("post-configuration-health", reason);
            return;
        }

        VoiceDiagnostics.Log(
            "voice.sidecar",
            $"ready=true reason={reason} sessionGeneration={sessionGeneration} micRequested={_microphoneRequested} micReady={_microphoneReady} micAwaitingLevel={_sidecarCaptureAwaitingFirstLevel} output={VoiceDiagnostics.DescribeDevice(currentSpk)} outputs={outputDevices.Length}");
    }

    private void HandleVoiceStartFailure(string stage, string reason)
    {
        if (_disposed) return;

        var retries = Interlocked.Increment(ref _voiceColdStartRetries);
        if (retries >= 3)
            EnterVoiceUnavailable($"{stage}-failed reason={reason} retries={retries} recovery=backoff");
        ScheduleVoiceRecovery(stage);
    }

    private void StopVoiceSession(
        string reason,
        bool waitForStop = false,
        bool cleanupInBackground = false)
    {
        SidecarVoiceLease? voice;
        Task startTask;
        lock (_voiceSync)
        {
            // Invalidate callbacks and async starts before releasing the lease. A delayed task from
            // this room can then only dispose its stale lease; it cannot clear a later room's state.
            Interlocked.Increment(ref _voiceSessionGeneration);
            _voiceReady = false;
            _speakerReady = false;
            _microphoneReady = false;
            BeginSidecarCaptureSourceGenerationLocked(awaitingFirstLevel: false);
            voice = _voice;
            _voice = null;
            _gameStateSendGate.Reset();
            startTask = _voiceStartTask;
        }
        StopVoicePump();
        // Releasing the room lease quiesces its peers/configuration and terminates that helper. The
        // coordinator remains reusable for a later lobby, while EndGame -> lobby continuity is
        // achieved by retaining the same room lease across the transition. Lease release is
        // serialized with Start/configuration so an old async start cannot configure a new owner.
        void CleanupLease()
        {
            try { voice?.Dispose(); } catch { }
            if (waitForStop)
                try { startTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            VoiceDiagnostics.Log("voice.sidecar", $"cleanup-complete reason={reason}");
        }
        if (cleanupInBackground)
            _ = Task.Run(CleanupLease);
        else
            CleanupLease();
        VoiceDiagnostics.Log("voice.sidecar", $"stopped reason={reason}");
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

        if (!ShouldSuperviseCapture(
                Mute,
                _microphoneRequested,
                _microphoneReady,
                _sidecarCaptureAwaitingFirstLevel,
                CaptureTransitionInFlight()))
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

    internal static bool ShouldSuperviseCapture(
        bool muted,
        bool microphoneRequested,
        bool microphoneReady,
        bool captureAwaitingFirstLevel,
        bool transitionInFlight)
        => !muted
           && microphoneRequested
           && (microphoneReady || captureAwaitingFirstLevel)
           && !transitionInFlight;

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
                VoiceDiagnostics.Log("voice.mic.worker", $"reason={reason} start={shouldRun} err=\"{ex.Message}\"");
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
        catch (Exception ex) { VoiceDiagnostics.Log("voice.mic.worker", $"reason=dispose err=\"{ex.Message}\""); }
        if (stopped) StopMicrophone("dispose");
        else VoiceDiagnostics.Log("voice.mic.worker", "reason=dispose err=\"timed out waiting for capture worker\"");
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
            VoiceDiagnostics.Log("voice.mic", $"ready=true reason={reason} capture={captureKind} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} captureDevice=\"{captureDevice}\" captureFormat=\"48000Hz/float/mono\" syntheticTone={_captureOptions.SyntheticMicToneEnabled} volume={_micVolume:0.00}");
        }
        catch (Exception ex)
        {
            StopMicrophone($"failed:{reason}");
            VoiceDiagnostics.Log("voice.mic", $"ready=false reason={reason} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} error=\"{ex.Message}\"");
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
            _digitalSilenceFrames = 0;
            Interlocked.Exchange(ref _digitalSilenceDetected, 0);
            _micPreprocessor.Reset(preserveAutoGain: true);
        }
        _localLevel = 0f;
        _localSpeaking = false;
        if (hadMic)
            VoiceDiagnostics.Log("voice.mic", $"ready=false reason={reason} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)} samples={Volatile.Read(ref _micSamples)}");
    }

    private void StartSidecarMicrophone(string reason, bool restartCapture = false)
    {
        try
        {
            // Desktop synthetic capture is generated inside the helper so it traverses the exact
            // production Opus/WebRTC path. Do not start the retired managed diagnostic timer here.
            StopSyntheticMicTone();
            _microphoneRequested = true;
            if (restartCapture)
                _microphoneReady = false;
            EnsureVoiceSession(reason);
            SidecarVoiceLease? voice;
            long sessionGeneration;
            long sourceGeneration;
            lock (_voiceSync)
            {
                voice = _voice;
                sessionGeneration = Volatile.Read(ref _voiceSessionGeneration);
                sourceGeneration = Volatile.Read(ref _sidecarCaptureSourceGeneration);
                if (voice != null)
                    DisarmSidecarCaptureEvidenceLocked(awaitingFirstLevel: !Mute);
            }
            if (voice != null)
            {
                // A native select-device while active can race the capture callback. Explicitly
                // stop -> select/configure -> start for device changes and supervisor restarts.
                if (restartCapture)
                    voice.SetMicActive(false);
                voice.SelectMicDevice(_lastMicDeviceName);
                voice.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
                voice.SetSynthetic(_captureOptions.SyntheticMicToneEnabled);
                voice.SetMicActive(!Mute);
            }
            var voiceHealth = voice?.Health ?? CaptureHealth.Dead;
            lock (_voiceSync)
            {
                var currentAndHealthy = voice != null
                                        && IsCurrentVoiceSessionLocked(voice, sessionGeneration)
                                        && _voiceReady
                                        && voiceHealth == CaptureHealth.Healthy;
                if (currentAndHealthy)
                {
                    ArmSidecarCaptureEvidenceLocked(sourceGeneration, captureActive: !Mute);
                }
                else if (sourceGeneration == Volatile.Read(ref _sidecarCaptureSourceGeneration))
                {
                    _microphoneReady = false;
                    _sidecarCaptureAwaitingFirstLevel = false;
                }
            }
            Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
            VoiceDiagnostics.Log("voice.mic", $"ready={_microphoneReady} requested=true reason={reason} capture={(_captureOptions.SyntheticMicToneEnabled ? "sidecar-synthetic" : "sidecar")} restart={restartCapture.ToString().ToLowerInvariant()} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} captureFormat=\"48000Hz/float/mono\" volume={_micVolume:0.00} vad={_vadThreshold:0.0000}");
        }
        catch (Exception ex)
        {
            StopSidecarMicrophone($"failed:{reason}");
            VoiceDiagnostics.Log("voice.mic", $"ready=false reason={reason} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} error=\"{ex.Message}\"");
        }
    }

    private void StopSidecarMicrophone(string reason)
    {
        StopSyntheticMicTone();
        _microphoneRequested = false;
        SidecarVoiceLease? voice;
        lock (_voiceSync)
        {
            DisarmSidecarCaptureEvidenceLocked(awaitingFirstLevel: false);
            voice = _voice;
        }
        voice?.SetMicActive(false);
        voice?.SetSynthetic(false);
        var hadMic = _microphoneReady;
        _microphoneReady = false;
        lock (_captureFrameSync)
        {
            _captureEpoch++;
            _captureFrameSamples = 0;
            _digitalSilenceFrames = 0;
            Interlocked.Exchange(ref _digitalSilenceDetected, 0);
            _micPreprocessor.Reset(preserveAutoGain: true);
        }
        _localLevel = 0f;
        _localSpeaking = false;
        if (hadMic)
            VoiceDiagnostics.Log("voice.mic", $"ready=false reason={reason} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)} samples={Volatile.Read(ref _micSamples)}");
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
            var deferred = type == SignalMsgType.Hello
                           && AmongUsRpcSignaling.DeferHello(senderClientId, 0, payload, "backend-voice-not-ready");
            VoiceDiagnostics.Log(
                deferred ? "signaling.session.defer" : "signaling.session.drop",
                $"stage=backend-dispatch sender={senderClientId} type={type} payloadBytes={payload.Length} reason=voice-not-ready voiceReady={_voiceReady} voiceHealth={voiceHealth}");
            return;
        }
#endif
        var manager = _peerSession;
        if (manager == null)
        {
            var deferred = type == SignalMsgType.Hello
                           && AmongUsRpcSignaling.DeferHello(senderClientId, 0, payload, "session-manager-null");
            VoiceDiagnostics.Log(
                deferred ? "signaling.session.defer" : "signaling.session.drop",
                $"stage=backend-dispatch sender={senderClientId} type={type} payloadBytes={payload.Length} reason=session-manager-null");
            return;
        }

        var nowMs = Environment.TickCount64;
        if (!_rpcKnownClients.Contains(senderClientId))
        {
            if (!IsCurrentRpcRosterClient(senderClientId))
            {
                VoiceDiagnostics.Log(
                    "signaling.session.drop",
                    $"stage=backend-dispatch sender={senderClientId} type={type} payloadBytes={payload.Length} reason=sender-not-in-current-roster");
                return;
            }

            // Bye is terminal for the manager's current peer generation, not a new join. Avoid a
            // pointless Hello immediately before dropping it; removing the roster marker below
            // lets the next poll (or the replacement's Hello) bootstrap symmetrically.
            if (type != SignalMsgType.Bye)
            {
                _rpcKnownClients.Add(senderClientId);
                manager.OnPlayerJoined(senderClientId, nowMs);
            }
        }

        manager.OnSignal(senderClientId, type, payload, nowMs);
        if (type == SignalMsgType.Bye && !manager.TryGetPeerState(senderClientId, out _))
            _rpcKnownClients.Remove(senderClientId);
    }

    private static bool IsCurrentRpcRosterClient(int senderClientId)
    {
        if (senderClientId < 0) return false;
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null || senderClientId == client.ClientId) return false;
            foreach (var remote in client.allClients)
                if (remote != null && remote.Id == senderClientId)
                    return true;
        }
        catch
        {
        }
        return false;
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
        var nowMs = Environment.TickCount64;

        var client = AmongUsClient.Instance;
        if (client == null)
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
        if (_peerSession != null && ShouldReplaceRpcSession(_peerSession.LocalClientId, localId))
        {
            var previousLocalId = _peerSession.LocalClientId;
            _peerSession.Reset();
            _peerSession = null;
            _rpcTransport = null;
            _rpcKnownClients.Clear();
            _rpcPresentScratch.Clear();
            _rpcLeftScratch.Clear();
            _rpcRosterGapActive = false;
            _rpcRosterPollNextMs = 0;
            _rpcSessionTickNextMs = 0;
            VoiceDiagnostics.Log(
                "signaling.session.local-client-rollover",
                $"previousLocal={previousLocalId} currentLocal={localId} action=recreate-manager");
        }
        if (_peerSession == null)
        {
            _rpcRosterGapActive = false;
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
                requestRelay: RequestRelayCredentials);
        }

        var registeredNow = false;
        if (!_rpcOnMessageHooked)
        {
            _rpcSubscription = AmongUsRpcSignaling.RegisterSubscriber(OnRpcSignal);
            _rpcOnMessageHooked = true;
            registeredNow = true;
        }
        // Attach before roster discovery sends Hello, then consume anything received during helper
        // startup. That closes the old window where both clients sent into an unregistered backend.
        if (registeredNow)
            AmongUsRpcSignaling.ReplayDeferredHellos(_rpcSubscription);

        if (AdvancePumpDeadline(nowMs, ref _rpcRosterPollNextMs, RpcRosterPollIntervalMs))
        {
            _rpcPresentScratch.Clear();
            var resolvedRosterEntries = 0;
            foreach (var remote in client.allClients)
            {
                if (remote == null) continue;
                var id = remote.Id;
                if (id < 0) continue;
                resolvedRosterEntries++;
                if (id == localId) continue;
                _rpcPresentScratch.Add(id);
                if (_rpcKnownClients.Add(id))
                    _peerSession.OnPlayerJoined(id, nowMs);
            }

            // A joined InnerNet roster must contain at least the local client. Among Us briefly
            // publishes an empty collection between the final meeting frame and EndGame; treating
            // that impossible snapshot as authoritative used to tear down the healthy mesh.
            if (resolvedRosterEntries == 0)
            {
                if (!_rpcRosterGapActive)
                {
                    _rpcRosterGapActive = true;
                    VoiceDiagnostics.Log(
                        "signaling.session.roster-gap",
                        $"action=retain-known knownPeers={_rpcKnownClients.Count} localClient={localId}");
                }
            }
            else
            {
                if (_rpcRosterGapActive)
                {
                    _rpcRosterGapActive = false;
                    VoiceDiagnostics.Log(
                        "signaling.session.roster-gap",
                        $"action=recovered rosterEntries={resolvedRosterEntries} knownPeers={_rpcKnownClients.Count} localClient={localId}");
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
            }
        }

        if (AdvancePumpDeadline(nowMs, ref _rpcSessionTickNextMs, RpcSessionTickIntervalMs))
        {
            if (!registeredNow)
                AmongUsRpcSignaling.ReplayDeferredHellos(_rpcSubscription);
            MaybeRestartIceAfterNetworkChange(nowMs);
            _peerSession.Tick(nowMs);
        }

        if (AdvancePumpDeadline(nowMs, ref _managedTurnRefreshPollNextMs, ManagedTurnRefreshPollIntervalMs))
            MaybeRefreshManagedTurnCredentials();
    }

    internal static bool AdvancePumpDeadline(long nowMs, ref long nextMs, int intervalMs)
    {
        if (nowMs < nextMs) return false;
        nextMs = nowMs + Math.Max(1, intervalMs);
        return true;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var nowMs = Environment.TickCount64;
        Volatile.Write(ref _networkChangeSignaledAtMs, nowMs == 0 ? 1 : nowMs);
    }

    private void MaybeRestartIceAfterNetworkChange(long nowMs)
    {
        var signaledAtMs = Volatile.Read(ref _networkChangeSignaledAtMs);
        var lastAppliedMs = Volatile.Read(ref _networkChangeLastAppliedMs);
        if (!ShouldRestartIceForNetworkChange(
                nowMs,
                signaledAtMs,
                lastAppliedMs,
                NetworkChangeDebounceMs,
                NetworkChangeCooldownMs))
            return;
        if (Interlocked.CompareExchange(ref _networkChangeSignaledAtMs, 0, signaledAtMs) != signaledAtMs)
            return;

        Volatile.Write(ref _networkChangeLastAppliedMs, nowMs);
        var restarted = _peerSession?.RestartIceAfterNetworkChange(nowMs) ?? 0;
        VoiceDiagnostics.Log(
            "voice.ice.network-change",
            $"action=restart-ice peers={restarted} debounceMs={NetworkChangeDebounceMs} cooldownMs={NetworkChangeCooldownMs}");
    }

    internal static bool ShouldRestartIceForNetworkChange(
        long nowMs,
        long signaledAtMs,
        long lastAppliedMs,
        int debounceMs,
        int cooldownMs)
    {
        if (signaledAtMs <= 0 || nowMs < signaledAtMs || nowMs - signaledAtMs < Math.Max(0, debounceMs))
            return false;
        return lastAppliedMs <= 0
               || nowMs < lastAppliedMs
               || nowMs - lastAppliedMs >= Math.Max(0, cooldownMs);
    }

    internal static bool ShouldReplaceRpcSession(int sessionLocalClientId, int currentLocalClientId)
        => sessionLocalClientId != currentLocalClientId;

#if WINDOWS
    internal static bool CanPumpDesktopRpc(bool configuredReady, CaptureHealth health)
        => configuredReady && health == CaptureHealth.Healthy;
#endif
#endif

#if ANDROID
    private void EnsureMobileVoice()
    {
        var nowMs = Environment.TickCount64;
        var existing = _mobileVoice;
        if (existing?.IsRunning == true)
        {
            existing.SetDiagnostics(VoiceDiagnostics.IsEnabled);
            EnsureMobileSpeaker(existing, nowMs, force: false);
            return;
        }

        if (nowMs < _mobileVoiceStartRetryAtMs) return;

        if (existing != null)
            InvalidateMobileVoice(existing, "engine-not-running");

        var mv = new MobileVoiceClient();
        var callbackGeneration = Interlocked.Increment(ref _mobileVoiceGeneration);
        mv.OnLocalSdp += (peerId, generation, type, sdp) =>
        {
            if (!IsCurrentMobileVoice(mv, callbackGeneration)) return;
            if (MobileVoiceTransport.TryParseClientId(peerId, out var cid))
                _mainThreadActions.Enqueue(() =>
                {
                    if (IsCurrentMobileVoice(mv, callbackGeneration))
                        _peerSession?.OnLocalSdp(cid, generation, type, sdp);
                });
        };
        mv.OnLocalCandidate += (peerId, generation, cand) =>
        {
            if (!IsCurrentMobileVoice(mv, callbackGeneration)) return;
            if (MobileVoiceTransport.TryParseClientId(peerId, out var cid))
                _mainThreadActions.Enqueue(() =>
                {
                    if (IsCurrentMobileVoice(mv, callbackGeneration))
                        _peerSession?.OnLocalCandidate(cid, generation, cand);
                });
        };
        mv.OnPeerState += (peerId, generation, state) =>
        {
            if (!IsCurrentMobileVoice(mv, callbackGeneration)) return;
            if (MobileVoiceTransport.TryParseClientId(peerId, out var cid))
                _mainThreadActions.Enqueue(() =>
                {
                    if (IsCurrentMobileVoice(mv, callbackGeneration))
                        OnMobilePeerState(cid, generation, state);
                });
        };
        mv.OnLevel += (peak, speaking) =>
        {
            if (!IsCurrentMobileVoice(mv, callbackGeneration)) return;
            if (Mute && !_loopBack)
            {
                _localLevel = 0f;
                _localSpeaking = false;
                return;
            }
            _localLevel = peak;
            _localSpeaking = speaking;
        };
        mv.OnPeerLevels += levels =>
        {
            if (levels.Count > 0 && IsCurrentMobileVoice(mv, callbackGeneration))
                _mainThreadActions.Enqueue(() =>
                {
                    if (IsCurrentMobileVoice(mv, callbackGeneration))
                        ApplyRemotePeerLevels(levels);
                });
        };
        if (!mv.Start())
        {
            var deferred = mv.StartWasDeferred;
            var retryAfterMs = Math.Max(100, mv.StartRetryAfterMs);
            _mobileVoiceStartRetryAtMs = nowMs + retryAfterMs;
            mv.Dispose();
            if (!deferred)
            {
                _mobileVoiceStartFailures = Math.Min(_mobileVoiceStartFailures + 1, 30);
                if (_mobileVoiceStartFailures >= 3)
                    Interlocked.Exchange(ref _voiceUnavailableRetrying, 1);
                VoiceDiagnostics.Log("voice.mobile",
                    $"state=start-failed attempt={_mobileVoiceStartFailures} retryAfterMs={retryAfterMs}");
            }
            return;
        }
        _mobileVoiceStartRetryAtMs = 0;
        _mobileVoiceStartFailures = 0;
        if (_iceServers != null && _iceServers.Count > 0) mv.SetIceServers(_iceServers);
        mv.SetDiagnostics(VoiceDiagnostics.IsEnabled);
        mv.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
        mv.SetSynthetic(false);
        // Android intentionally ships without the desktop WebRTC APM side library.
        mv.SetDsp(false, false, false, false, false);
        // A fresh engine is fail-closed. Explicit Stop when muted/not ready also resets any
        // encoder history before peers can be authorized; an already-running source gets Start.
        mv.SetMicActive(!Mute && _microphoneReady);
        _gameStateSendGate.Reset();
        _mobileVoice = mv;
        EnsureMobileSpeaker(mv, nowMs, force: true);
        VoiceDiagnostics.Log("voice.mobile", $"state=started backend=pc-mobile generation={callbackGeneration} dsp=platform-passthrough");
    }

    private bool IsCurrentMobileVoice(MobileVoiceClient voice, int generation)
        => !_disposed
           && generation == Volatile.Read(ref _mobileVoiceGeneration)
           && ReferenceEquals(_mobileVoice, voice)
           && voice.IsRunning;

    private void InvalidateMobileVoice(MobileVoiceClient voice, string reason)
    {
        Interlocked.Increment(ref _mobileVoiceGeneration);
        try { _mobileSpeaker?.Dispose(); } catch { }
        _mobileSpeaker = null;
        _speakerReady = false;
        _mobileSpeakerRetryAttempts = 0;
        _mobileSpeakerRetryAtMs = 0;

        try { _peerSession?.ResetAndNotify($"mobile-{reason}"); } catch { }
        _peerSession = null;
        _rpcTransport = null;
        _rpcKnownClients.Clear();
        _rpcPresentScratch.Clear();
        _rpcLeftScratch.Clear();
        _rpcRosterGapActive = false;
        _rpcRosterPollNextMs = 0;
        _rpcSessionTickNextMs = 0;

        if (ReferenceEquals(_mobileVoice, voice))
        {
            _mobileVoice = null;
            _gameStateSendGate.Reset();
        }
        try { voice.ResetMicInput(); } catch { }
        try { voice.Dispose(); } catch { }
        VoiceDiagnostics.Log("voice.mobile", $"state=invalidated reason={reason}");
    }

    private void EnsureMobileSpeaker(MobileVoiceClient voice, long nowMs, bool force)
    {
        if (_mobileSpeaker != null || !ReferenceEquals(_mobileVoice, voice) || !voice.IsRunning)
            return;
        if (!force && nowMs < _mobileSpeakerRetryAtMs)
            return;

        try
        {
            _mobileSpeaker = new AndroidEnginePcmSpeaker(voice, _androidMicrophoneMonitor);
            _speakerReady = _mobileSpeaker.IsPlaying;
            _mobileSpeakerRetryAttempts = 0;
            _mobileSpeakerRetryAtMs = 0;
            Interlocked.Exchange(ref _voiceUnavailableRetrying, _speakerReady ? 0 : 1);
        }
        catch (Exception ex)
        {
            _mobileSpeaker = null;
            _speakerReady = false;
            _mobileSpeakerRetryAttempts = Math.Min(_mobileSpeakerRetryAttempts + 1, 30);
            if (_mobileSpeakerRetryAttempts >= 3)
                Interlocked.Exchange(ref _voiceUnavailableRetrying, 1);
            var retryMs = AndroidMicrophone.RecoveryDelayMilliseconds(
                _mobileSpeakerRetryAttempts, 250, 30_000);
            _mobileSpeakerRetryAtMs = nowMs + retryMs;
            VoiceDiagnostics.Log("voice.speaker",
                $"ready=false backend=pc-mobile retryAfterMs={retryMs} error=\"{ex.Message}\"");
        }
    }

    private void OnMobilePeerState(int clientId, int generation, string state)
    {
        if (_peerSession == null) return;
        var nowMs = Environment.TickCount64;
        VoiceDiagnostics.Log(
            "voice.ice.peer-state",
            $"client={clientId} generation={generation} state={state} policy=automatic-mixed backend=mobile");
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
        VoiceDiagnostics.Log("voice.synthetic", $"state=started reason={reason} sampleRate={AudioHelpers.ClockRate} frameSize={AudioHelpers.FrameSize} toneHz={SyntheticToneFrequency:0} amplitude={SyntheticToneAmplitude:0.000}");
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
            Interlocked.Increment(ref _micProcessingFailures);
            VoiceDiagnostics.Log("voice.mic.capture_error", $"source=synthetic error=\"{ex.Message}\"");
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
            _mobileVoice?.SetInput(_micVolume, _vadThreshold, _noiseGateThreshold);
#endif
            if (_captureOptions.SyntheticMicToneEnabled)
            {
                StartSyntheticMicTone(reason);
                _microphoneReady = true;
            }
            else
            {
                _androidMicrophone = new AndroidMicrophone();
                _androidMicrophone.ReuseBuffer = true;
                _androidMicrophone.DataAvailable += OnAndroidMicrophoneData;
                _androidMicrophone.SetVolume(1f);
                // A setup preview can briefly own Unity's process-global Microphone surface while
                // the room is starting. Production capture remains requested and reacquires that
                // lease with bounded backoff as soon as the preview is disposed.
                _microphoneReady = _androidMicrophone.Start(
                    _lastMicDeviceName,
                    retryLeaseWhenUnavailable: true);
                VoiceDiagnostics.Log(
                    "voice.unity.mic",
                    $"requested={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} unityDevices=[{string.Join(",", AndroidMicrophone.GetDeviceNames().Select(VoiceDiagnostics.DescribeDevice))}]");
            }

#if ANDROID
            // Open only after the source reports ready. MobileVoiceClient drops PCM until the
            // native Start reset succeeds, so a failed source or control call stays fail-closed.
            _mobileVoice?.SetMicActive(_microphoneReady && !Mute);
#endif

            VoiceDiagnostics.Log("voice.mic", $"ready={_microphoneReady} reason={reason} capture={DescribeCaptureMode()} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} syntheticTone={_captureOptions.SyntheticMicToneEnabled} volume={_micVolume:0.00}");
        }
        catch (Exception ex)
        {
            StopAndroidMicrophone($"failed:{reason}");
            VoiceDiagnostics.Log("voice.mic", $"ready=false reason={reason} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} error=\"{ex.Message}\"");
        }
    }

    private void StopAndroidMicrophone(string reason)
    {
#if ANDROID
        // Close native transmission and clear Opus/DRED history before source teardown. Mute is
        // already visible to capture callbacks, and the managed client closes its PCM gate first.
        try { _mobileVoice?.SetMicActive(false); } catch { }
#endif
        StopSyntheticMicTone();
        // Invalidate queued/device-old PCM before waiting for either Unity or the encode worker.
        // The worker rechecks both this epoch and Mute immediately before entering pc-mobile.
        lock (_captureFrameSync)
        {
            _captureEpoch++;
            _captureFrameSamples = 0;
            _micPreprocessor.Reset(preserveAutoGain: true);
        }
#if ANDROID
        try { _mobileVoice?.ResetMicInput(); } catch { }
#endif
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
        _localLevel = 0f;
        _localSpeaking = false;
        if (hadMic)
            VoiceDiagnostics.Log("voice.mic", $"ready=false reason={reason} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)} callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)} samples={Volatile.Read(ref _micSamples)}");
    }

    private void OnAndroidMicrophoneData(float[] buffer, int length)
    {
        if (_disposed || buffer.Length == 0) return;
        Interlocked.Increment(ref _micCallbacks);
        int samples = Math.Min(Math.Max(length, 0), buffer.Length);
        Interlocked.Add(ref _micBytes, samples * sizeof(float));
#if ANDROID
        if (_loopBack && samples > 0)
        {
            _androidMicrophoneMonitor.Write(buffer, samples);
            float peak = 0f;
            for (int i = 0; i < samples; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
            _localLevel = peak;
            _localSpeaking = peak >= Math.Max(0.0001f, _vadThreshold);
        }
#endif
        if (Mute)
        {
            Interlocked.Increment(ref _micMutedDrops);
            return;
        }
        if (samples <= 0) return;
        Interlocked.Add(ref _micSamples, samples);

        var epoch = Volatile.Read(ref _captureEpoch);
        lock (_unityEncodeSync)
        {
            if (_disposed) return;
            var offset = 0;
            while (offset < samples)
            {
                var take = Math.Min(AudioHelpers.FrameSize - _unityCaptureFill, samples - offset);
                Array.Copy(buffer, offset, _unityCaptureAccum, _unityCaptureFill, take);
                _unityCaptureFill += take;
                offset += take;
                if (_unityCaptureFill != AudioHelpers.FrameSize) continue;

                var frame = ArrayPool<float>.Shared.Rent(AudioHelpers.FrameSize);
                Array.Copy(_unityCaptureAccum, 0, frame, 0, AudioHelpers.FrameSize);
                _unityCaptureFill = 0;
                while (_unityEncodeQueue.Count >= UnityEncodeQueueMaxFrames)
                {
                    var dropped = _unityEncodeQueue.Dequeue();
                    ArrayPool<float>.Shared.Return(dropped.buffer, clearArray: false);
                    Interlocked.Increment(ref _unityEncodeDroppedFrames);
                }
                _unityEncodeQueue.Enqueue((frame, AudioHelpers.FrameSize, epoch));
            }
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

            try
            {
                if (epoch != Volatile.Read(ref _captureEpoch)) continue;
                lock (_captureFrameSync)
                {
                    if (epoch != _captureEpoch || Mute) continue;
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
                        Interlocked.Increment(ref _micProcessingFailures);
                        VoiceDiagnostics.Log("voice.mic.capture_error", $"source=android error=\"{ex.Message}\"");
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer, clearArray: false);
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
            _unityCaptureFill = 0;
            while (_unityEncodeQueue.Count > 0)
            {
                var queued = _unityEncodeQueue.Dequeue();
                ArrayPool<float>.Shared.Return(queued.buffer, clearArray: false);
            }
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
            VoiceDiagnostics.Log("voice.speaker", $"ready=false device={VoiceDiagnostics.DescribeDevice(deviceName)} error=\"{ex.Message}\"");
        }
#else
#if ANDROID
        EnsureMobileVoice();
        try { _mobileSpeaker?.Dispose(); } catch { }
        _mobileSpeaker = null;
        _speakerReady = false;
        _mobileSpeakerRetryAttempts = 0;
        _mobileSpeakerRetryAtMs = 0;
        var mobileVoice = _mobileVoice;
        if (mobileVoice != null)
            EnsureMobileSpeaker(mobileVoice, Environment.TickCount64, force: true);
        VoiceDiagnostics.Log("voice.speaker", $"ready={_speakerReady} device={VoiceDiagnostics.DescribeDevice(deviceName)} backend=pc-mobile");
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
            VoiceDiagnostics.Log("voice.speaker", $"ready=true device={VoiceDiagnostics.DescribeDevice(deviceName)} backend=sidecar reused=true");
        }
        else
        {
            EnsureVoiceSession("speaker");
            VoiceDiagnostics.Log("voice.speaker", $"ready=pending device={VoiceDiagnostics.DescribeDevice(deviceName)} backend=sidecar");
        }
    }
#endif

#if WINDOWS
    private void MaybeReleaseBluetoothMutedMicrophone()
    {
        if (!Mute || !_microphoneReady || !_btProfileConflict || _btMuteReleaseRequested) return;
        if (_muteSinceUtc == DateTime.MinValue || DateTime.UtcNow - _muteSinceUtc < BtMutedMicReleaseDelay) return;
        _btMuteReleaseRequested = true;
        VoiceDiagnostics.Log("voice.mic", $"reason=muted-bt-release device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)}");
        QueueMicrophoneTransition(false, "muted-bt-release");
    }
#endif

    public void UpdateProfile(byte playerId, string playerName)
    {
        // Native peers are keyed by the Among Us client id resolved from the live roster. Player profile data is
        // refreshed from VoiceGameStateSnapshot in EnsureSnapshotRoutePeers.
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
                VoiceDiagnostics.Log("voice.radio.rx", $"client={peer.ClientId} player={playerId} active={VoiceTeamRadioChannels.IsActive(channel)} channel={channel}");
            }
        }
    }

    public void Rejoin()
    {
        ClearPeers();
        VoiceDiagnostics.Log("voice.engine.rejoin", "state=cleared");
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
            try { action(); } catch (Exception ex) { VoiceDiagnostics.Log("voice.error", $"stage=mainThread error=\"{ex.Message}\""); }
        }
        VoiceFrameProfiler.End("backend.mainactions", mainActionsTicks);

#if ANDROID
        var androidMicrophone = _androidMicrophone;
        androidMicrophone?.Tick();
        if (androidMicrophone != null)
        {
            bool wasReady = _microphoneReady;
            bool isReady = androidMicrophone.IsCapturing;
            _microphoneReady = isReady;
            if (isReady != wasReady)
            {
                // Close the native PCM gate during every Unity restart/lease wait. Re-open it only
                // after the source has actually recovered so stale encoder history and queued PCM
                // cannot leak across a capture discontinuity.
                try { _mobileVoice?.SetMicActive(isReady && !Mute); } catch { }
                VoiceDiagnostics.Log(
                    "voice.mic",
                    $"ready={isReady.ToString().ToLowerInvariant()} reason=unity-capture-transition leaseUnavailable={androidMicrophone.LeaseUnavailable.ToString().ToLowerInvariant()} device={VoiceDiagnostics.DescribeDevice(_lastMicDeviceName)}");
            }
        }

        var mobileSpeaker = _mobileSpeaker;
        if (mobileSpeaker != null)
        {
            _speakerReady = mobileSpeaker.Tick();
            Interlocked.Exchange(ref _voiceUnavailableRetrying, _speakerReady ? 0 : 1);
        }
#elif WINDOWS
        _androidMicrophone?.Tick();
        RefreshVoiceRecoveryBudgetAfterStableUptime();
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
            if (_voice != null)
                SendNativeGameStateIfDue(true, 0f, Array.Empty<SidecarProtocol.GameStatePeerInput>());
#elif ANDROID
            if (_mobileVoice != null)
                SendNativeGameStateIfDue(true, 0f, Array.Empty<SidecarProtocol.GameStatePeerInput>());
#endif
            MaybeLogStats(snapshot, "no-snapshot");
            return;
        }

        // Media peer ids are authoritative Among Us client ids. Keep one lightweight route record
        // for every snapshot client so desktop and Android feed gain/pan/rule state to the native
        // mixer without any external roster service.
        EnsureSnapshotRoutePeers(snapshot);

        var localPlayer = snapshot.TryGetLocalPlayer(out var local) ? local : (VoicePlayerSnapshot?)null;
        var listenerPos = localPlayer?.Position;
        var aliveDeadMixFocus = VoiceChatPatches.AliveDeadMixFocus;
        var localSettings = VoiceSettings.Instance;
        var aliveFocusProfile = localSettings?.AliveFocusProfile
            ?? VoiceVolumeMath.DefaultAliveFocusProfile;
        var deadFocusProfile = localSettings?.DeadFocusProfile
            ?? VoiceVolumeMath.DefaultDeadFocusProfile;

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
            helperGameStatePeers = _helperGameStatePeers;
#elif ANDROID
        if (_mobileVoice != null)
            helperGameStatePeers = _helperGameStatePeers;
#endif
        helperGameStatePeers?.Clear();
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
                result = VoiceProximityCalculator.CalculateLobby(localPlayer, target, listenerPos);
            else if (VoiceSceneState.IsMeetingVoicePhase(snapshot.Phase))
                result = VoiceProximityCalculator.CalculateMeeting(localPlayer, target, peer.RadioActive, snapshot.Phase, peer.RadioChannel);
            else
                result = VoiceProximityCalculator.CalculateTaskPhase(localPlayer, target, listenerPos, snapshot.LocalLightRadius, snapshot.MapId, snapshot.CameraViewActive, snapshot.ActiveCameraIndex, snapshot.ActiveCameraPosition, speakerCache, virtualMicrophones, localInVent, peer.RadioActive, commsSabActive, peer.WallCoefficient, peer.RadioChannel);

            result = VoiceProximityCalculator.ApplyExternalAudioEffects(result, target, snapshot.Phase);
            if (snapshot.Phase != VoiceGamePhase.EndGame &&
                result.Audible &&
                VoiceProximityCalculator.IsLocalListenerAudioMuffledThisFrame())
                result = result with { FilterMode = VoiceAudioFilterMode.ListenerMuffle };
            peer.Apply(result);
            if (helperGameStatePeers != null && peer.ClientId >= 0)
            {
                float groupVolume = VoiceVolumeMath.SelectGroupVolume(
                    aliveDeadMixFocus,
                    target?.IsDead,
                    aliveFocusProfile,
                    deadFocusProfile);
                float gain = VoiceVolumeMath.ResolvePeerGain(result, peer.ClientVolume, groupVolume);
                helperGameStatePeers.Add(new SidecarProtocol.GameStatePeerInput(
                    peer.ClientIdText,
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
            SendNativeGameStateIfDue(VoiceChatHudState.IsSpeakerMuted, _masterVolume, helperGameStatePeers);
#elif ANDROID
        if (helperGameStatePeers != null)
            SendNativeGameStateIfDue(VoiceChatHudState.IsSpeakerMuted, _masterVolume, helperGameStatePeers);
#endif

        MaybeLogStats(snapshot, "ok");
    }

    private void SendNativeGameStateIfDue(
        bool deaf,
        float master,
        IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
    {
        var fingerprint = SidecarProtocol.GameStateFingerprint(deaf, master, peers);
#if WINDOWS
        var desktopVoice = _voice;
        if (!_voiceReady || desktopVoice == null || desktopVoice.Health != CaptureHealth.Healthy)
            return;
        if (!_gameStateSendGate.ShouldSend(Environment.TickCount64, fingerprint)) return;
        desktopVoice.SendGameState(deaf, master, peers);
#elif ANDROID
        var mobileVoice = _mobileVoice;
        if (mobileVoice?.IsRunning != true) return;
        if (!_gameStateSendGate.ShouldSend(Environment.TickCount64, fingerprint)) return;
        mobileVoice.SendGameState(deaf, master, peers);
#endif
    }

    private PeerConnection ChooseDuplicateRouteWinner(PeerConnection first, PeerConnection second)
    {
        return PreferSecondRouteRecord(first.SocketId, second.SocketId) ? second : first;
    }

    internal static bool PreferSecondRouteRecord(string firstSocket, string secondSocket)
        // Stable selection makes the winner independent of dictionary insertion order.
        => string.CompareOrdinal(secondSocket, firstSocket) < 0;

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
        VoiceDiagnostics.Log("voice.peer.duplicate", $"client={muted.ClientId} keptSocket={kept.SocketId} mutedSocket={muted.SocketId}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        BestEffortDispose("turn-reset", ResetTurnCredentialState);
#if WINDOWS || ANDROID
        if (_networkChangeSubscribed)
        {
            BestEffortDispose("network-change-unsubscribe", () => NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged);
            _networkChangeSubscribed = false;
        }
        BestEffortDispose("peer-reset", () => _peerSession?.ResetAndNotify("backend-dispose"));
        if (_rpcOnMessageHooked)
        {
            BestEffortDispose("rpc-unsubscribe", () => AmongUsRpcSignaling.UnregisterSubscriber(_rpcSubscription));
            _rpcOnMessageHooked = false;
            _rpcSubscription = default;
        }
        _peerSession = null;
        _rpcTransport = null;
        _rpcKnownClients.Clear();
        _rpcRosterGapActive = false;
#endif
#if WINDOWS
        BestEffortDispose("microphone-worker-stop", StopMicrophoneWorkerForDispose);
        // This owns the SidecarVoiceLease release and must run even if capture/UI/signaling cleanup failed.
        BestEffortDispose("sidecar-session-stop", () => StopVoiceSession(
            "dispose", waitForStop: true, cleanupInBackground: true));
#elif ANDROID
        BestEffortDispose("android-microphone-stop", () => StopAndroidMicrophone("dispose"));
        BestEffortDispose("mobile-speaker-dispose", () => _mobileSpeaker?.Dispose());
        _mobileSpeaker = null;
        BestEffortDispose("mobile-engine-dispose", () => _mobileVoice?.Dispose());
        _mobileVoice = null;
#endif
        BestEffortDispose("preprocessor-dispose", () =>
        {
            lock (_captureFrameSync)
                _micPreprocessor.Dispose();
        });
        BestEffortDispose("peer-routes-clear", ClearPeers);
        BestEffortDispose("loud-log-clear", _lastCenteredLoudLogUtc.Clear);
        while (_mainThreadActions.TryDequeue(out _)) { }
    }

    private static void BestEffortDispose(string stage, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            try { VoiceDiagnostics.Log("voice.dispose.error", $"stage={stage} error=\"{ex.Message.Replace('"', '\'')}\""); }
            catch { }
        }
    }

    public void RebuildIceConnectionPool()
    {
        if (_disposed) return;
        ResetTurnCredentialState();
        RefreshConfiguredIceServers("settings-changed");
#if WINDOWS || ANDROID
        _peerSession?.RebuildAll(Environment.TickCount64);
#endif
        if (UsesManagedTurn())
            EnsureManagedTurnCredentials(refreshRelayPeers: false);
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

    // PeerSessionManager owns the full 12-second SDP/ICE deadline. The room's coarser missing-peer
    // watchdog must not tear down the backend while that negotiation is still making progress.
    public bool ShouldDeferPeerEscalation
    {
        get
        {
#if WINDOWS || ANDROID
            return _peerSession?.HasActiveNegotiation(Environment.TickCount64) == true;
#else
            return false;
#endif
        }
    }

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
                Interlocked.Increment(ref _micProcessingFailures);
                VoiceDiagnostics.Log("voice.mic.capture_error", $"source=bass error=\"{ex.Message}\"");
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
                VoiceDiagnostics.Log("voice.mic.channel-latch",
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
            Interlocked.Increment(ref _micProcessingFailures);
            VoiceDiagnostics.Log("voice.mic.processing_error", $"source={source} samples={samples} expected={AudioHelpers.FrameSize} error=\"invalid-frame-size\"");
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
            TrackCaptureTelemetryLocked(rawCapturePeak);

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
        VoiceDiagnostics.Log("voice.mic.calibration",
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

    private void RemovePeer(string socketId)
    {
#if WINDOWS
        _voice?.RemovePeer(socketId);
#endif
        PeerConnection? peer;
        lock (_peerSync)
        {
            _peersBySocket.Remove(socketId, out peer);
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
        var routeRecords = peers.Length;
        var engineRouteTargets = peers.Length == 0
            ? "none"
            : string.Join("|", peers.Select(peer => $"{peer.ClientId}:engine"));
        var aliveDeadMixFocus = VoiceChatPatches.AliveDeadMixFocus;
        var localSettings = VoiceSettings.Instance;
        var aliveFocusProfile = localSettings?.AliveFocusProfile
            ?? VoiceVolumeMath.DefaultAliveFocusProfile;
        var deadFocusProfile = localSettings?.DeadFocusProfile
            ?? VoiceVolumeMath.DefaultDeadFocusProfile;
        var effectiveRoutes = peers.Length == 0
            ? "none"
            : string.Join("|", peers.Select(peer =>
            {
                var route = peer.CurrentRoute;
                var target = snapshot == null ? null : FindTarget(snapshot, peer);
                float groupVolume = VoiceVolumeMath.SelectGroupVolume(
                    aliveDeadMixFocus,
                    target?.IsDead,
                    aliveFocusProfile,
                    deadFocusProfile);
                var gain = VoiceVolumeMath.ResolvePeerGain(route, peer.ClientVolume, groupVolume);
                string mixGroup = target?.IsDead switch
                {
                    true => "dead",
                    false => "alive",
                    null => "unknown",
                };
                return $"{peer.ClientId}:gain={gain:0.000},pan={route.Pan:0.000},mode={(int)route.FilterMode},clientVol={peer.ClientVolume:0.000},groupVol={groupVolume:0.000},mixFocus={aliveDeadMixFocus},mixGroup={mixGroup},reason={route.Reason}";
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
            micRms = micWindowSamples == 0 ? 0.0 : Math.Sqrt(_micSquareSumSinceStats / micWindowSamples) / short.MaxValue;
            _micPeakSinceStats = 0f;
            _micSquareSumSinceStats = 0.0;
            _micSamplesSinceStats = 0;
            _micNonZeroSamplesSinceStats = 0;
            _micSilentCallbacksSinceStats = 0;
            _micNearClipSamplesSinceStats = 0;
            _micZeroCrossingsSinceStats = 0;
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
        var mobilePlaybackText = string.Empty;
#if ANDROID
        var mobileVoice = _mobileVoice;
        mobilePlaybackText = mobileVoice == null
            ? " mobilePlayback=unavailable"
            : $" mobilePlaybackDepthSamples={mobileVoice.PlaybackDepthSamples} mobilePlaybackHighWaterSamples={mobileVoice.PlaybackHighWaterSamples} mobilePlaybackDroppedSamples={mobileVoice.PlaybackDroppedSamples} mobilePlaybackSkippedSamples={mobileVoice.PlaybackSkippedSamples} mobilePlaybackZeroFilledSamples={mobileVoice.PlaybackZeroFilledSamples} mobilePlaybackPrimingZeroFilledSamples={mobileVoice.PlaybackPrimingZeroFilledSamples} mobilePlaybackClockCorrectionSamples={mobileVoice.PlaybackClockCorrectionSamples} mobilePlaybackClockCorrectionCallbacks={mobileVoice.PlaybackClockCorrectionCallbacks} mobilePlaybackLateCycles={mobileVoice.PlaybackPumpLateCycles} mobilePlaybackEmptyPulls={mobileVoice.PlaybackNativeEmptyPulls}";
#endif
        VoiceDiagnostics.Log("voice.stats",
            $"reason={reason} {VoiceDiagnostics.DescribeRoom(RoomCode)} {VoiceDiagnostics.DescribeRegion(Region)} media=native-engine signaling=among-us-rpc phase={snapshot?.Phase.ToString() ?? "none"} " +
            $"routeRecords={routeRecords} engineRouteTargets={engineRouteTargets} audibleRoutes={peers.Count(peer => peer.CurrentRoute.Audible)} speakingRoutes={peers.Count(peer => peer.IsSpeaking)} {rpcDiagnosticsText} " +
            $"localLevel={LocalLevel:0.000} localSpeaking={LocalSpeaking} mute={Mute} remoteLevelMax={remoteMax:0.000} " +
            $"routeSamples={peerTicks} audibleTicks={audibleTicks} audibleSilentTicks={audibleSilentTicks} silentPct={silentPct:0.0} routeWindows={peerWindows} effectiveRoutes={effectiveRoutes} " +
            $"sidecarLevelEventsWindow={sidecarLevelEventsWindow} sidecarLevelEventsTotal={sidecarLevelEventsTotal} sidecarLevelAgeMs={sidecarLevelAgeMs} sidecarPeerLevelBatchesTotal={sidecarPeerLevelBatchesTotal} sidecarPeerLevelsTotal={sidecarPeerLevelsTotal} sidecarPeerLevelsAgeMs={sidecarPeerLevelsAgeMs} sidecarPeerLevelsMappedWindow={sidecarPeerLevelsMappedWindow} sidecarPeerLevelsUnmappedWindow={sidecarPeerLevelsUnmappedWindow} " +
            $"managedMicCallbacks={Volatile.Read(ref _micCallbacks)} managedMicBytes={Volatile.Read(ref _micBytes)} managedMicSamples={Volatile.Read(ref _micSamples)} managedMicWindowSamples={micWindowSamples} managedMicPeak={micPeak:0.000000} managedMicRms={micRms:0.000000} managedMicCrest={micCrest:0.00} managedMicNonZeroSamples={micNonZeroSamples} managedMicSilentCallbacks={micSilentCallbacks} managedMicNearClipSamples={micNearClipSamples} managedMicClipPct={micClipPct:0.000} managedMicZeroCrossRate={micZeroCrossRate:0.0000} " +
            $"managedMicMutedDrops={Volatile.Read(ref _micMutedDrops)} managedMicProcessingFailures={Volatile.Read(ref _micProcessingFailures)} unityEncodeDroppedFrames={Volatile.Read(ref _unityEncodeDroppedFrames)}{mobilePlaybackText} " +
            $"noiseGate={noiseGateThreshold:0.000000} vadThreshold={vadThreshold:0.000000} gateReason={_lastGateReason} gatePeak={_lastGatePeak:0.000000} gateRms={_lastGateRms:0.000000} gateThreshold={_lastGateThreshold:0.000000} txGain={_lastTransmitGain:0.000} " +
            $"syntheticTone={_captureOptions.SyntheticMicToneEnabled} noiseSuppression={_captureOptions.NoiseSuppressionEnabled} strongerNoiseSuppression={_captureOptions.NoiseSuppressionEnabled && _captureOptions.StrongerNoiseSuppressionEnabled} syntheticFrames={Volatile.Read(ref _syntheticFrames)} capture={DescribeCaptureMode()} calibration={_captureOptions.MicCalibrationDiagnostics} sensitivity={_captureOptions.MicSensitivity:0.00} micReady={_microphoneReady} speakerConfigured={_speakerReady} speakerMuted={VoiceChatHudState.IsSpeakerMuted} masterVolume={_masterVolume:0.000} recordDevice={Volatile.Read(ref _lastOpenedRecordDevice)} micDigitalSilence={Volatile.Read(ref _digitalSilenceDetected)}");
        if (CaptureUsesUnity)
            VoiceDiagnostics.Log("voice.unity",
                $"active=true mode={DescribeCaptureMode()} micReady={_microphoneReady} micCallbacks={Volatile.Read(ref _micCallbacks)} micSamples={Volatile.Read(ref _micSamples)} micPeak={micPeak:0.000000} micRms={micRms:0.000000} micSilentCallbacks={micSilentCallbacks} micProcessingFailures={Volatile.Read(ref _micProcessingFailures)} micMutedDrops={Volatile.Read(ref _micMutedDrops)} speakerReady={_speakerReady}");
        VoiceDiagnostics.Log("voice.playout.state",
            $"backend=engine configured={_speakerReady} speakerMuted={VoiceChatHudState.IsSpeakerMuted} masterVolume={_masterVolume:0.000} requestedDevice={DescribeRequestedSpeakerDevice()} confirmed=unknown confirmationSource=native.media.stats-and-stderr");
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
        return VoiceDiagnostics.DescribeDevice(_lastSpeakerDeviceName);
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

        VoiceDiagnostics.Log("voice.route.centeredloud",
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
            ClientIdText = clientId.ToString(CultureInfo.InvariantCulture);
            PlaybackGroupId = playbackGroupId;
        }

        public void FadeClearRoutes()
        {
            MuteAll();
        }

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
        public string ClientIdText { get; private set; }
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
            ClientIdText = clientId.ToString(CultureInfo.InvariantCulture);
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
                    VoiceDiagnostics.Log("voice.route.applied-divergence",
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
                var result = new PeerDiagnostics(ClientId, _levelPeakSinceStats, _samplesSinceStats, _audibleSamplesSinceStats, _audibleSilentSamplesSinceStats, _routeClearsSinceStats, _currentRoute, _appliedPan);
                _levelPeakSinceStats = 0f;
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
        public PeerDiagnostics(int clientId, float levelPeak, int samples, int audibleSamples, int audibleSilentSamples, int routeClears, VoiceProximityResult route, float appliedPan)
        {
            ClientId = clientId;
            LevelPeak = levelPeak;
            Samples = samples;
            AudibleSamples = audibleSamples;
            AudibleSilentSamples = audibleSilentSamples;
            RouteClears = routeClears;
            Route = route;
            AppliedPan = appliedPan;
        }
        public int ClientId { get; }
        public float LevelPeak { get; }
        public int Samples { get; }
        public int AudibleSamples { get; }
        public int AudibleSilentSamples { get; }
        public int RouteClears { get; }
        public VoiceProximityResult Route { get; }
        public float AppliedPan { get; }
        public string ToCompactString() => $"{ClientId}:{LevelPeak:0.000}/{Samples}/{AudibleSamples}/{AudibleSilentSamples}/route={Route.Reason}:{Route.NormalVolume:0.00},{Route.GhostVolume:0.00},{Route.RadioVolume:0.00},pan={Route.Pan:0.00},appliedPan={AppliedPan:0.00},clears={RouteClears}";
    }

    private static string DiagnosticSafe(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace("\"", "'");
}
