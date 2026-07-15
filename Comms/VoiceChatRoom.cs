using InnerNet;
using VoiceChatPlugin.Audio;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Net;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Manages the in-game voice chat session.
///
/// C# owns game-state routing and authenticated Among Us RPC signaling. Native pc-capture/pc-mobile
/// engines own capture, WebRTC RTP media, jitter buffering, mixing, and playback.
/// </summary>
public class VoiceChatRoom
{
    private const float StateRefreshInterval = 0.05f;
    private const float CommsSabotageRefreshInterval = 0.10f;
    private const float BootstrapWindowSeconds = 6f;
    private const float BootstrapRefreshInterval = 0.50f;
    private const float MissingPeerRecoveryGraceSeconds = 8f;
    private const float MissingPeerRecoveryIntervalSeconds = 5f;
    private const float PeerEscalationDeferralRecheckSeconds = 3f;
    private const double RadioStateRpcHeartbeatSeconds = 1.0;
    private const float TransitionTraceSeconds = 45f;
    private const float TransitionRosterRetentionMaxSeconds = 3f;
    private const float TransitionTraceStateInterval = 0.25f;
    private const int TransitionTraceAudioFrames = 64;
    private const int TransitionTracePerfEvents = 48;
    private const double StalePlaybackBufferTimeoutSeconds = VoiceProtocol.MaxQueuedFrameAgeSeconds;
    private const double SlowUpdateLogThresholdMs = 20.0;
    private const double SlowOperationLogThresholdMs = 2.0;
    private const float HostVoiceRefreshCooldownSeconds = 10f;
    private const float LocalVoiceRefreshCooldownSeconds = 10f;
    private const float HostPolicySyncFallbackSeconds = 10f;

    // ── Singleton ─────────────────────────────────────────────────────────────
    private static readonly object CurrentLifecycleLock = new();
    private static VoiceChatRoom? _current;
    public static VoiceChatRoom? Current => Volatile.Read(ref _current);

    // ── Virtual components ─────────────────────────────────────────────────────
    private readonly List<IVoiceComponent> _virtualMics     = new();
    private readonly List<IVoiceComponent> _virtualSpeakers = new();
    private readonly List<SpeakerCache> _speakerCacheBuffer = new();
    public void AddVirtualMicrophone(IVoiceComponent c)    => _virtualMics.Add(c);
    public void AddVirtualSpeaker(IVoiceComponent c)       => _virtualSpeakers.Add(c);
    public void RemoveVirtualMicrophone(IVoiceComponent c) => _virtualMics.Remove(c);
    public void RemoveVirtualSpeaker(IVoiceComponent c)    => _virtualSpeakers.Remove(c);

    // ── Microphone ─────────────────────────────────────────────────────────────
    public bool UsingMicrophone => _voiceBackend?.UsingMicrophone == true;
    private readonly object _backendLifecycleSync = new();
    private IVoiceBackend? _voiceBackend;
    private PerfectCommsVoiceBackend? _perfectCommsVoice;
    private VoiceRoomSettingsSnapshot? _lastSentHostSettings;
    private int _lastSentModOptionRevision = -1;
    private readonly SuccessfulSendGate _hostSettingsBroadcastGate = new(
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1));
    private readonly SuccessfulSendGate _hostSettingsRequestGate = new(
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(2));
    private ClientRosterSignature? _lastSentHostSettingsRoster;
    private int _lastObservedHostClientId = -1;
    private bool _hostSettingsResyncPending;
    private float _hostPolicyWaitStartTime = -1f;
    private bool _hostPolicyFallbackLogged;
    private int _lastAppliedHostVoiceRefreshNonce;
    private float _lastAppliedHostVoiceRefreshTime = -999f;
    private const float HostVoiceRefreshApplyCooldownSeconds = 8f;
    private float _lastHostVoiceRefreshRequestTime = -999f;
    private float _lastLocalVoiceRefreshRequestTime = -999f;
    // Seeded randomly per process so two different hosts don't both start at nonce 1. After host migration,
    // the new host's first refresh would otherwise collide with the old host's nonce 1 and be ignored by
    // every client as a duplicate — exactly when voice is most likely broken.
    private static int _nextHostVoiceRefreshNonce = new System.Random().Next(1, int.MaxValue / 2);
    private readonly RadioStateSyncTracker _radioStateSync = new(
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(RadioStateRpcHeartbeatSeconds));
    // Set by missing-peer recovery to make EnsureVoiceBackend fully rebuild the native media session.
    private bool _forceBackendRebuild;
    private bool _relayOnlyForSession;
    private string? _activeRoomCode;
    private string? _activeRegion;
    internal IEnumerable<VoiceRemoteOverlayState> RemoteOverlayStates => _voiceBackend?.RemoteOverlayStates ?? Enumerable.Empty<VoiceRemoteOverlayState>();

    // Allocation-free per-frame path used by VoiceOverlayState.Build.
    internal void AppendRemoteOverlayStates(List<VoiceRemoteOverlayState> buffer)
        => _voiceBackend?.AppendRemoteOverlayStates(buffer);

    // For VoiceFrameProfiler context only.
    internal int BackendPeerCount => _voiceBackend?.PeerCount ?? -1;
    internal bool TrySetRemoteVolume(byte playerId, string playerName, float volume)
        => _voiceBackend?.TrySetRemoteVolume(playerId, playerName, volume) == true;
    internal int ResetRemotePeerMappingsNoMute()
        => _voiceBackend?.ResetPeerMappingsNoMute() ?? 0;
    public float LocalMicLevel => _voiceBackend?.LocalLevel ?? 0f;
    public bool LocalMicSpeaking => _voiceBackend?.LocalSpeaking == true;
    // Capture is fail-closed from construction through the first authoritative policy tick. The
    // backend observes this value before opening a microphone, so StartMuted/PTT/deafen state can
    // never race an early OnGameJoined transport bootstrap.
    public bool Mute  { get; private set; } = true;
    public int  SampleRate => AudioHelpers.ClockRate;
    internal VoiceGameStateSnapshot? CurrentSnapshot { get; private set; }

    // ── Speaker ────────────────────────────────────────────────────────────────
    public bool UsingSpeaker => _voiceBackend?.UsingSpeaker == true;
    internal bool VoiceTransportInitializing => _perfectCommsVoice?.IsInitializing == true;

    // ── Misc ───────────────────────────────────────────────────────────────────
    private bool  _commsSabActive;
    private float _commsSabCheckTimer;
    private string _lastCommsSabotageSource = "";
    private byte   _lastId   = byte.MaxValue;
    private string _lastName = null!;
    private float  _lastCompatibilityRefreshTime = -999f;
    private float  _snapshotRefreshTimer;
    private int _snapshotGameId;
    private bool _retainingTransitionSnapshot;
    private readonly Dictionary<int, float> _missingSnapshotClientSince = new();
    private readonly HashSet<int> _authenticatedSnapshotClientIds = new();
    private float _authRosterUnavailableSince = -1f;
    private float  _bootstrapUntilTime = -999f;
    private float  _bootstrapRefreshTimer;
    private float _missingPeerRecoveryReadyTime = -999f;
    private float _lastMissingPeerRecoveryTime = -999f;
    // Storm guard (P0): a permanently-unmappable remote keeps mappedPeers < remotePlayers forever. Track how
    // many consecutive recovery attempts did NOT improve mappedPeers; after a hard cap we LATCH (stop firing
    // recovery on that shortfall) until the expected-remote set changes or mappedPeers actually increases, so
    // an unmappable peer can't drive the old unbounded 5 s teardown cadence. _lastHealthyMappedPeers records
    // the best mapped count seen, so escalation to a global rebuild is reserved for a real collapse.
    private int _missingPeerRecoveryAttempts;          // consecutive non-improving attempts on the current shortfall (targeted path)
    private int _globalRebuildAttempts;                 // consecutive global/collapse rebuilds (bounds the collapse-path backoff)
    private int _lastRecoveryOpenPeers = -1;             // openPeers observed at the previous attempt
    private int _lastHealthyMappedPeers;                // best mappedPeers ever seen this session (diagnostics only)
    private int _lastRecoveryRemoteSignature;           // cheap hash of the expected-remote set at the previous attempt
    private bool _missingPeerRecoveryLatched;           // true once capped; cleared when the set/count changes
    private int _consecutivePeerEscalationDeferrals;
    private const int MissingPeerRecoveryMaxAttempts = 3;        // non-improving attempts before latching
    private const int MissingPeerRecoveryBackoffShiftCap = 4;    // max backoff doublings (5s base -> ~80s cap on the global path)
    private bool _haveTracePhase;
    private VoiceGamePhase _lastTracePhase = VoiceGamePhase.Unknown;
    private bool _haveRoutingPhase;
    private VoiceGamePhase _lastRoutingPhase = VoiceGamePhase.Unknown;
    private DateTime _transitionTraceUntilUtc = DateTime.MinValue;
    private float _transitionTraceStateTimer;
    private int _tracePerfEventsRemaining;
    private string? _lastLoggedLocalState;
    private string? _lastLoggedOptions;
    private DateTime _lastDebugStateLogUtc = DateTime.MinValue;
    private readonly Dictionary<string, SuccessfulSendGate> _hostSettingsResponseGates = new();
    private int _closed;

    private readonly record struct ClientRosterSignature(int Count, int Xor, long Sum);
    private enum AuthenticatedRosterCollectionState
    {
        Unavailable,
        Empty,
        Populated,
    }

    // ======================================================================
    // Factory
    // ======================================================================

    public static VoiceChatRoom Start()
    {
        VoiceChatRoom room;
        lock (CurrentLifecycleLock)
        {
            var previous = Interlocked.Exchange(ref _current, null);
            previous?.Close("room replaced", clearUi: true);
            room = new VoiceChatRoom();
            Volatile.Write(ref _current, room);
        }
        room.TryStartTransportBootstrap("room-start");
        return room;
    }

    internal static VoiceChatRoom EnsureStartedForJoinedSession()
    {
        VoiceChatRoom room;
        lock (CurrentLifecycleLock)
        {
            room = Current!;
            if (room == null)
            {
                room = new VoiceChatRoom();
                Volatile.Write(ref _current, room);
            }
        }
        room.TryStartTransportBootstrap("among-us-on-game-joined");
        return room;
    }

    private void TryStartTransportBootstrap(string source)
    {
        if (_voiceBackend != null || Volatile.Read(ref _closed) != 0)
            return;
        try
        {
            EnsureVoiceBackend(null, VoiceSettings.Instance);
            if (_voiceBackend != null)
                VoiceDiagnostics.Log("transport.bootstrap.early", $"source={source} result=started");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugError($"[VC] Early transport bootstrap failed source={source}: {ex.Message}");
        }
    }

    public static void CloseCurrentRoom()
        => CloseCurrentRoom("room close");

    internal static void CloseCurrentRoom(string reason)
        => CloseCurrentRoomCore(reason, clearUi: true);

    // Process/domain shutdown may run after Unity objects have begun tearing down. Release the
    // active session without touching HUD GameObjects; the process host is shut down separately.
    internal static void ShutdownCurrentRoom(string reason)
        => CloseCurrentRoomCore(reason, clearUi: false);

    // Scene callbacks run before the next room tick. Expire the small periodic snapshot cache so
    // EndGame/lobby/intro routing consumes the new phase on that very tick instead of up to 50 ms
    // later, while keeping the backend and sidecar lease continuously alive.
    internal static void NotifyScenePhaseBoundary()
    {
        VoiceChatHudState.InvalidateAudioPolicyCache();
        var current = Current;
        if (current != null)
            current._snapshotRefreshTimer = 0f;
    }

    private static void CloseCurrentRoomCore(string reason, bool clearUi)
    {
        lock (CurrentLifecycleLock)
        {
            var room = Interlocked.Exchange(ref _current, null);
            room?.Close(reason, clearUi);
        }
    }

    internal static void ClearVoiceUiForLifecycleReset(string reason)
    {
        RunCleanupStep("speaking-bar", PingTrackerPatch.ClearSpeakingBar);
        RunCleanupStep("meeting-indicators", MeetingSpeakingIndicatorPatch.ClearAllIndicators);
        RunCleanupStep("overlay-cache", VoiceOverlayState.InvalidateCache);
        RunCleanupStep("volume-menu", VoiceVolumeMenu.ForceClose);
        RunCleanupStep("avatar-cache", CrewmateAvatarRenderer.ClearCache);
        RunCleanupStep("camera-state", VoiceCameraState.Clear);
        RunCleanupStep("sight-state", VoiceProximityCalculator.ResetSightState);
        RunCleanupStep("audio-policy-cache", VoiceChatHudState.ResetAudioPolicyCache);
        try { VoiceDiagnostics.Log("voice.ui.clear", $"reason={LogSafe(reason)}"); } catch { }
    }

    private static void RunCleanupStep(string stage, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception ex)
        {
            try { VoiceDiagnostics.Log("voice.cleanup.error", $"stage={stage} error=\"{LogSafe(ex.Message)}\""); }
            catch { }
        }
    }

    // ======================================================================
    // Constructor
    // ======================================================================

    private VoiceChatRoom()
    {
        VoiceChatPatches.ReleaseHeldTransmitInputs();
        VoiceChatHudState.BeginVoiceSession();
        ResetSettingsSyncState();
        RefreshLocalAudioSettings();
        VoiceDiagnostics.DebugInfo("[VC] VoiceChatRoom constructed.");
        StartBootstrapWindow("room constructed");
        StartTransitionTrace("room constructed", CurrentSnapshot);
    }

    // ======================================================================
    // Volume / mute
    // ======================================================================

    public void SetMasterVolume(float v)
    {
        _voiceBackend?.SetMasterVolume(VoiceChatHudState.GetEffectiveMasterVolume(v));
    }

    public void SetMicVolume(float v)
    {
        _voiceBackend?.SetMicVolume(v);
    }

    public void RefreshLocalAudioSettings()
    {
        var settings = VoiceSettings.Instance;
        _voiceBackend?.SetMicVolume(settings?.MicVolume.Value ?? 1f);
        _voiceBackend?.SetNoiseGate(
            ApplyMicSensitivity(settings?.NoiseGateThreshold.Value ?? 0.003f, settings?.MicSensitivity.Value ?? 1f),
            ApplyMicSensitivity(settings?.VadThreshold.Value ?? 0.004f, settings?.MicSensitivity.Value ?? 1f));
        _voiceBackend?.SetCaptureRuntimeOptions(BuildCaptureRuntimeOptions(settings));
    }

    private static float ApplyMicSensitivity(float threshold, float sensitivity)
    {
        sensitivity = Math.Clamp(sensitivity, 0.25f, 2f);
        return threshold / sensitivity;
    }

    private static VoiceCaptureRuntimeOptions BuildCaptureRuntimeOptions(VoiceChatLocalSettings? settings)
        => new(
            settings?.SyntheticMicTone.Value ?? false,
            settings?.MicCalibrationDiagnostics.Value ?? false,
            settings?.NoiseSuppressionEnabled.Value ?? false,
            settings?.EchoCancellationEnabled.Value ?? true,
            settings?.MicSensitivity.Value ?? 1f);

    public void RebuildCaptureSupervisor() => _perfectCommsVoice?.RebuildCaptureSupervisor();

    public void SetMute(bool mute)
    {
        bool wasMuted = Mute;
        Mute = mute;
#if ANDROID
        if (!mute && !Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            // Preserve listen-only playback while the OS prompt is pending/denied. The backend
            // remains capture-muted until PermissionHelper confirms permission on this live room.
            if (wasMuted)
                SetMicrophone(VoiceSettings.Instance?.MicrophoneDevice ?? string.Empty);
        }
        else
        {
            _voiceBackend?.SetMute(mute);
        }
#else
        _voiceBackend?.SetMute(mute);
#endif
        if (!mute && wasMuted)
            StartBootstrapWindow("local unmuted");
    }

    public void ToggleMute() => SetMute(!Mute);
    public void SetLoopBack(bool lb) => _voiceBackend?.SetLoopBack(lb);

    // ======================================================================
    // Microphone
    // ======================================================================

    public void SetMicrophone(string deviceName)
    {
#if ANDROID
        if (Mute)
        {
            // Store the selected device without prompting or opening capture. A listen-only user
            // can stay muted for the entire session; unmuting requests permission at that point.
            StartMicNow(deviceName ?? string.Empty);
            return;
        }

        if (VoiceChatPluginMain.ResidentObject != null)
        {
            var behaviour = VoiceChatPluginMain.ResidentObject.GetComponent<PermissionHelper>()
                ?? VoiceChatPluginMain.ResidentObject.AddComponent<PermissionHelper>();
            behaviour.RequestMicAndStart(this, deviceName ?? string.Empty);
        }
        else
        {
            StartMicNow(deviceName ?? string.Empty);
        }
#else
        StartMicNow(deviceName ?? string.Empty);
#endif
    }

    internal void StartMicNow(string deviceName)
    {
        var settings = VoiceSettings.Instance;
        _voiceBackend?.SetMicrophone(deviceName, settings?.MicVolume.Value ?? 1f);
    }

#if ANDROID
    internal void StartMicAfterPermission(string deviceName)
    {
        if (Volatile.Read(ref _closed) != 0 || !ReferenceEquals(Current, this) || Mute)
            return;
        StartMicNow(deviceName);
        _voiceBackend?.SetMute(false);
    }
#endif

    // ======================================================================
    // Speaker
    // ======================================================================

    public void SetSpeaker(string deviceName)
    {
        _voiceBackend?.SetSpeaker(deviceName ?? string.Empty);
    }

    internal static void SendJailVoicePacket(byte jailedPlayerId, bool allowed)
    {
        var local = PlayerControl.LocalPlayer;
        if (local == null) return;

        // Authority only via authenticated Among Us RPC (binds InnerNet sender); the voice
        // side-channel's self-asserted identity would let any peer forge a jailor's mute.
        VoiceJailVoiceRpc.Send(local.PlayerId, jailedPlayerId, allowed);
    }

    // ======================================================================
    // Main update loop (WITH AGGRESSIVE SPEAKER RECOVERY - FIXED!)
    // ======================================================================

    public void Update()
    {
        long updateStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        string updateStep = "speaker-check";
        updateStep = "transport";        TryUpdateLocalProfile();
        TryRunBootstrapRefresh();        TickVoiceBackend(CurrentSnapshot);
        MaybeLogNetworkStats();

        updateStep = "snapshot";
        _commsSabCheckTimer -= Time.deltaTime;
        if (_commsSabCheckTimer <= 0f)
        {
            _commsSabCheckTimer = CommsSabotageRefreshInterval;
            bool commsSabActive = CheckCommsSabotage(out var commsSabotageSource);
            if (commsSabActive != _commsSabActive || commsSabotageSource != _lastCommsSabotageSource)
            {
                VoiceDiagnostics.Log("state.comms",
                    $"active={commsSabActive} source={commsSabotageSource} map={ShipStatus.Instance?.Type.ToString() ?? "none"}");
            }
            _commsSabActive = commsSabActive;
            _lastCommsSabotageSource = commsSabotageSource;
        }

        _snapshotRefreshTimer -= Time.deltaTime;
        bool refreshSnapshot = CurrentSnapshot == null || _snapshotRefreshTimer <= 0f;
        if (refreshSnapshot)
        {
            _snapshotRefreshTimer = StateRefreshInterval;
            long __snapTicks = VoiceFrameProfiler.Begin();
            var refreshedSnapshot = VoiceSnapshotBuilder.Build(_commsSabActive);
            var currentGameId = ResolveCurrentGameId();
            var explicitDisconnect = VoiceRoomLifetimeGate.IsExplicitDisconnectLatched;
            var previousRetainable = VoiceRoomLifetimeGate.CanRetainSnapshot(
                CurrentSnapshot != null,
                _snapshotGameId,
                currentGameId,
                explicitDisconnect);

            // Scene transitions often remove PlayerControls before the authenticated InnerNet
            // roster changes. Promote the freshly observed phase immediately, but merge a missing
            // established identity only while allClients still authenticates it. Non-EndGame gaps
            // are bounded; EndGame keeps its authenticated roster because it has no world roster.
            if (previousRetainable && CurrentSnapshot != null)
            {
                var authRosterState = CollectAuthenticatedSnapshotClientIds();
                if (authRosterState == AuthenticatedRosterCollectionState.Populated)
                {
                    _authRosterUnavailableSince = -1f;
                    refreshedSnapshot = VoiceSnapshotTransitionMerger.Merge(
                        refreshedSnapshot,
                        CurrentSnapshot,
                        _authenticatedSnapshotClientIds,
                        _missingSnapshotClientSince,
                        Time.realtimeSinceStartup,
                        TransitionRosterRetentionMaxSeconds);
                }
                else if (authRosterState == AuthenticatedRosterCollectionState.Empty)
                {
                    var now = Time.realtimeSinceStartup;
                    _authRosterUnavailableSince = VoiceSnapshotTransitionMerger.NextEmptyAuthenticatedRosterGapStart(
                        refreshedSnapshot.Phase,
                        CurrentSnapshot.Phase,
                        _authRosterUnavailableSince,
                        now);
                    var gapSeconds = _authRosterUnavailableSince < 0f
                        ? 0f
                        : Math.Max(0f, now - _authRosterUnavailableSince);
                    var wasAlreadyRetained = CurrentSnapshot.RoutingRosterRetained;
                    refreshedSnapshot = VoiceSnapshotTransitionMerger.RetainPriorRoutesDuringEmptyAuthenticatedRosterGap(
                        refreshedSnapshot,
                        CurrentSnapshot,
                        gapSeconds,
                        TransitionRosterRetentionMaxSeconds);
                    if (!wasAlreadyRetained && refreshedSnapshot.RoutingRosterRetained)
                    {
                        VoiceDiagnostics.Log(
                            "voice.snapshot.auth_roster_empty",
                            $"phase={refreshedSnapshot.Phase} action=retain-prior players={refreshedSnapshot.Players.Count} gapSeconds={gapSeconds:0.000} gameId={currentGameId}");
                    }
                }
                else if (refreshedSnapshot.Phase == VoiceGamePhase.EndGame
                         || VoiceSnapshotTransitionMerger.IsBoundedAuthGapPhase(refreshedSnapshot.Phase))
                {
                    var now = Time.realtimeSinceStartup;
                    // Start the bounded clock only after EndGame. Its own missing world roster is
                    // expected and the explicit-disconnect lifetime gate still stops voice at once.
                    _authRosterUnavailableSince = VoiceSnapshotTransitionMerger.NextAuthGapStart(
                        refreshedSnapshot.Phase,
                        CurrentSnapshot.Phase,
                        _authRosterUnavailableSince,
                        now);

                    var wasAlreadyRetained = CurrentSnapshot.RoutingRosterRetained;
                    var gapSeconds = _authRosterUnavailableSince < 0f
                        ? 0f
                        : Math.Max(0f, now - _authRosterUnavailableSince);
                    refreshedSnapshot = VoiceSnapshotTransitionMerger.RetainPriorRoutesDuringAuthGap(
                        refreshedSnapshot,
                        CurrentSnapshot,
                        gapSeconds,
                        TransitionRosterRetentionMaxSeconds);
                    if (!wasAlreadyRetained && refreshedSnapshot.RoutingRosterRetained)
                    {
                        VoiceDiagnostics.Log(
                            "voice.snapshot.auth_roster_unavailable",
                            $"phase={refreshedSnapshot.Phase} action=retain-prior players={refreshedSnapshot.Players.Count} gapSeconds={gapSeconds:0.000} gameId={currentGameId}");
                    }
                }
                else
                {
                    _authRosterUnavailableSince = -1f;
                    _missingSnapshotClientSince.Clear();
                }
            }
            else
            {
                _authRosterUnavailableSince = -1f;
                _missingSnapshotClientSince.Clear();
            }

            var refreshDecision = VoiceRoomLifetimeGate.DecideSnapshotRefresh(
                sessionActive: currentGameId != 0 && !explicitDisconnect,
                refreshedUsable: IsRefreshedSnapshotUsableForRouting(refreshedSnapshot),
                // The merger is the only retention path: it verifies live InnerNet membership,
                // carries the current phase, and expires each absence. Falling back to the exact
                // previous snapshot would reintroduce stale phase/roster state.
                previousUsable: false,
                previousRetainable: false);
            if (refreshDecision == VoiceSnapshotRefreshDecision.UseRefreshed)
            {
                if (_retainingTransitionSnapshot && !refreshedSnapshot.RoutingRosterRetained)
                    VoiceDiagnostics.Log(
                        "voice.snapshot.transition_recovered",
                        $"phase={refreshedSnapshot.Phase} players={refreshedSnapshot.Players.Count} gameId={currentGameId}");
                if (!_retainingTransitionSnapshot && refreshedSnapshot.RoutingRosterRetained)
                    VoiceDiagnostics.Log(
                        "voice.snapshot.transition_retained",
                        $"previousPhase={CurrentSnapshot?.Phase.ToString() ?? "none"} refreshedPhase={refreshedSnapshot.Phase} " +
                        $"previousPlayers={CurrentSnapshot?.Players.Count ?? 0} routedPlayers={refreshedSnapshot.Players.Count} gameId={currentGameId}");
                _retainingTransitionSnapshot = refreshedSnapshot.RoutingRosterRetained;
                CurrentSnapshot = refreshedSnapshot;
                _snapshotGameId = currentGameId;
            }
            else if (refreshDecision == VoiceSnapshotRefreshDecision.Clear)
            {
                _retainingTransitionSnapshot = false;
                _authRosterUnavailableSince = -1f;
                _missingSnapshotClientSince.Clear();
                CurrentSnapshot = null;
                _snapshotGameId = 0;
            }
            VoiceFrameProfiler.End("room.snapshot", __snapTicks);
        }

        var snapshot = CurrentSnapshot;
        // Audio policy is room-owned and must follow the effective snapshot phase even when the HUD
        // update returned early (or did not run in a transition frame).
        VoiceChatHudState.ApplyAudioPolicy(snapshot);
        Vector2? listenerPos = snapshot?.LocalPosition;
        // One-time-per-map occlusion warm-up so the first in-range speaker doesn't pay the physics-broadphase
        // build + door-cache scan (~70-100ms) mid-round. No-op after the first call for a given map.
        if (listenerPos.HasValue) VoiceAudioOcclusion.WarmUp(listenerPos.Value);
        TrackTransitionPhase(snapshot);        bool localInVent = snapshot != null &&
                            snapshot.TryGetLocalPlayer(out var localSnapshot) &&
                            localSnapshot.InVent;

        _speakerCacheBuffer.Clear();
        IReadOnlyList<SpeakerCache> speakerCache = _speakerCacheBuffer;
        updateStep = "speaker-cache";
        if (listenerPos.HasValue && _virtualSpeakers.Count > 0)
        {
            var settings = VoiceRoomSettingsState.Current;
            float maxRange = settings.MaxChatDistance;
            foreach (var speaker in _virtualSpeakers)
            {
                float d = Vector2.Distance(speaker.Position, listenerPos.Value);
                float volume = VoiceAudioOcclusion.ApplyFalloff(d, maxRange, (VoiceFalloffMode)settings.FalloffMode);
                if (volume > 0f)
                    _speakerCacheBuffer.Add(new(speaker, volume, GetPan(listenerPos.Value.x, speaker.Position.x)));
            }
        }
        updateStep = "routes";

        if (_voiceBackend != null)
        {
            // Negotiate ICE immediately, but do not apply untrusted local rule defaults while a
            // client is still waiting for its host's authenticated policy snapshot.
            var policyReady = IsHostPolicyReady();
            var routedSnapshot = policyReady ? snapshot : null;
            long __backendTicks = VoiceFrameProfiler.Begin();
            _voiceBackend.Update(routedSnapshot, speakerCache, _virtualMics, localInVent, _commsSabActive);
            VoiceFrameProfiler.End("room.backend", __backendTicks);
            if (routedSnapshot != null)
                SendRadioState(routedSnapshot.LocalPlayerId, VoiceChatHudState.ActiveTeamRadioChannel());
            long __recoveryTicks = VoiceFrameProfiler.Begin();
            TryRecoverMissingBackendPeers(routedSnapshot);
            VoiceFrameProfiler.End("room.recovery", __recoveryTicks);
        }

        updateStep = "diagnostics";
        MaybeLogTransitionTraceState(snapshot);

        TraceUpdateCost(updateStartTicks, updateStep, snapshot);
    }

    internal void FailClosedAfterUpdateFailure()
    {
        // A partially-applied transition can leave the native mixer holding the previous route
        // generation. Mute capture and explicitly publish a null game state so stale routes cannot
        // remain audible while the managed update loop recovers.
        SetMute(true);
        try
        {
            _voiceBackend?.Update(
                null,
                Array.Empty<SpeakerCache>(),
                Array.Empty<IVoiceComponent>(),
                localInVent: false,
                commsSabActive: false);
        }
        catch
        {
            // The caller already owns rate-limited diagnostics and bounded recovery. This path is
            // best-effort and must never replace the original update exception.
        }
    }

    internal void RequestBoundedUpdateFailureRecovery(int attempt)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        _forceBackendRebuild = true;
        _snapshotRefreshTimer = 0f;
        StartBootstrapWindow($"managed-update-failure-{attempt}");
        VoiceDiagnostics.Log(
            "voice.room.update_recovery",
            $"attempt={attempt} action=rebuild-next-update bounded=true");
    }

    private bool IsRefreshedSnapshotUsableForRouting(VoiceGameStateSnapshot snapshot)
        => VoiceRoomLifetimeGate.IsSafeForRouting(snapshot);

    private AuthenticatedRosterCollectionState CollectAuthenticatedSnapshotClientIds()
    {
        _authenticatedSnapshotClientIds.Clear();
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null)
                return AuthenticatedRosterCollectionState.Unavailable;

            if (client.ClientId >= 0)
                _authenticatedSnapshotClientIds.Add(client.ClientId);
            var authenticatedRosterEntries = 0;
            foreach (var member in client.allClients)
            {
                if (member != null && member.Id >= 0)
                {
                    authenticatedRosterEntries++;
                    _authenticatedSnapshotClientIds.Add(member.Id);
                }
            }
            return authenticatedRosterEntries == 0
                ? AuthenticatedRosterCollectionState.Empty
                : AuthenticatedRosterCollectionState.Populated;
        }
        catch
        {
            _authenticatedSnapshotClientIds.Clear();
            return AuthenticatedRosterCollectionState.Unavailable;
        }
    }

    private bool IsLocalHost()
    {
        try
        {
            return AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;
        }
        catch
        {
            return false;
        }
    }

    internal static void RequestHostVoiceRefreshFromKeybind()
    {
        var current = Current;
        if (current == null)
        {
            VoiceDiagnostics.Log("voice.refresh.ignored", "reason=no-room trigger=keybind");
            return;
        }

        current.RequestHostVoiceRefreshFromHost();
    }

    internal static void RequestLocalVoiceRefreshFromKeybind()
    {
        var current = Current;
        if (current == null)
        {
            VoiceDiagnostics.Log("voice.refresh.local.ignored", "reason=no-room trigger=keybind");
            return;
        }

        current.RequestLocalVoiceRefresh();
    }

    private void RequestLocalVoiceRefresh()
    {
        if (Time.time - _lastLocalVoiceRefreshRequestTime < LocalVoiceRefreshCooldownSeconds)
        {
            VoiceDiagnostics.Log("voice.refresh.local.rate_limited",
                $"trigger=keybind cooldown={LocalVoiceRefreshCooldownSeconds:0.0}s");
            return;
        }

        _lastLocalVoiceRefreshRequestTime = Time.time;
        VoiceDiagnostics.Log("voice.refresh.local.requested",
            $"backend=native-engine room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")} peers={_voiceBackend?.PeerCount ?? 0}");
        ApplyLocalVoiceRefresh("keybind");
    }

    private void RequestHostVoiceRefreshFromHost()
    {
        if (!IsLocalHost())
        {
            VoiceDiagnostics.Log("voice.refresh.rejected", "reason=not-host trigger=keybind");
            return;
        }

        if (Time.time - _lastHostVoiceRefreshRequestTime < HostVoiceRefreshCooldownSeconds)
        {
            VoiceDiagnostics.Log("voice.refresh.rate_limited",
                $"trigger=keybind cooldown={HostVoiceRefreshCooldownSeconds:0.0}s");
            return;
        }

        _lastHostVoiceRefreshRequestTime = Time.time;
        var nonce = CreateHostVoiceRefreshNonce();
        var localClientId = ResolveLocalClientId(CurrentSnapshot);
        VoiceDiagnostics.Log("voice.refresh.requested",
            $"nonce={nonce} hostClient={localClientId} backend=native-engine room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")}");

        VoiceHostRefreshRpc.Send(nonce);
        ApplyHostVoiceRefresh(VoiceHostAuthority.FromPlayer(PlayerControl.LocalPlayer, "local"), nonce, "keybind");
    }

    internal static void ApplyHostVoiceRefreshFromRpc(PlayerControl sender, int nonce)
    {
        var current = Current;
        if (current == null)
        {
            VoiceDiagnostics.Log("voice.refresh.ignored", $"reason=no-room trigger=rpc nonce={nonce}");
            return;
        }

        var senderIdentity = VoiceHostAuthority.FromPlayer(sender, "rpc");
        if (!VoiceHostAuthority.IsTrustedHostSender(
                sender,
                current.CurrentSnapshot,
                "rpc",
                out senderIdentity,
                out var reason,
                out var hostClientId,
                out var hostPlayerId))
        {
            VoiceDiagnostics.Log("voice.refresh.rejected",
                $"{senderIdentity.ToDiagnosticFields()} reason={reason} hostClient={hostClientId} hostPlayer={hostPlayerId} nonce={nonce}");
            return;
        }

        current.ApplyHostVoiceRefresh(senderIdentity, nonce, "rpc");
    }

    private static int CreateHostVoiceRefreshNonce()
    {
        unchecked
        {
            var nonce = ++_nextHostVoiceRefreshNonce;
            return nonce != 0 ? nonce : ++_nextHostVoiceRefreshNonce;
        }
    }

    private void ApplyHostVoiceRefresh(VoiceHostSenderIdentity sender, int nonce, string trigger)
    {
        if (nonce != 0 && nonce == _lastAppliedHostVoiceRefreshNonce)
        {
            VoiceDiagnostics.Log("voice.refresh.ignored",
                $"{sender.ToDiagnosticFields()} reason=duplicate nonce={nonce} trigger={trigger}");
            return;
        }

        // Only rate-limit RPC-driven refreshes whose nonce is NOT newer than the
        // last applied one (i.e. duplicate/stale resends). A genuinely new nonce
        // (host re-tap, host migration) is always honored even inside the window.
        var nonceIsNewer = nonce != 0 && nonce != _lastAppliedHostVoiceRefreshNonce;
        if (trigger == "rpc"
            && !nonceIsNewer
            && Time.time - _lastAppliedHostVoiceRefreshTime < HostVoiceRefreshApplyCooldownSeconds)
        {
            VoiceDiagnostics.Log("voice.refresh.rate_limited",
                $"{sender.ToDiagnosticFields()} reason=rate-limited nonce={nonce} trigger={trigger}");
            return;
        }

        _lastAppliedHostVoiceRefreshTime = Time.time;
        _lastAppliedHostVoiceRefreshNonce = nonce;
        var snapshot = CurrentSnapshot;
        VoiceDiagnostics.Log("voice.refresh.applied",
            $"{sender.ToDiagnosticFields()} nonce={nonce} trigger={trigger} backend=native-engine room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")} peers={_voiceBackend?.PeerCount ?? 0}");

        VoiceChatHudState.ShowToast(trigger == "rpc"
            ? "Host refreshed voice connections"
            : "You refreshed voice for everyone");

        // Rejoin() begins with ClearVoiceUiForLifecycleReset, so the UI teardown runs exactly once.
        StartTransitionTrace($"host voice refresh: {trigger}", snapshot);
        Rejoin("host voice refresh");
    }

    private void ApplyLocalVoiceRefresh(string trigger)
    {
        var snapshot = CurrentSnapshot;
        VoiceDiagnostics.Log("voice.refresh.local.applied",
            $"trigger={trigger} backend=native-engine room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")} peers={_voiceBackend?.PeerCount ?? 0}");

        VoiceChatHudState.ShowToast("Voice connection refreshed");

        // Rejoin() begins with ClearVoiceUiForLifecycleReset, so the UI teardown runs exactly once.
        StartTransitionTrace($"local voice refresh: {trigger}", snapshot);
        Rejoin("local voice refresh");
    }

    private bool SendHostSettingsSnapshot(bool force, string reason, int targetClientId = -1)
    {
        if (!IsLocalHost()) return false;

        var settings = VoiceRoomSettingsSnapshot.FromGameOptions();
        int modRevision = VoiceModRegistry.OptionRevision;
        var now = DateTime.UtcNow;
        var isBroadcast = targetClientId < 0;
        var roster = isBroadcast ? CaptureClientRosterSignature() : null;
        var rosterChanged = isBroadcast
                            && roster.HasValue
                            && (!_lastSentHostSettingsRoster.HasValue || _lastSentHostSettingsRoster.Value != roster.Value);
        var effectiveForce = force || rosterChanged;

        if (isBroadcast)
        {
            if (!effectiveForce
                && _lastSentHostSettings.HasValue
                && _lastSentHostSettings.Value.Equals(settings)
                && _lastSentModOptionRevision == modRevision)
                return false;
            if (!_hostSettingsBroadcastGate.CanAttempt(now, force: effectiveForce))
                return false;
        }

        // Authority only via authenticated Among Us RPC; the side-channel's self-asserted
        // sender id would let any peer forge host voice settings.
        var sent = VoiceRoomSettingsRpc.TrySendSnapshot(settings, targetClientId);
        if (isBroadcast)
            _hostSettingsBroadcastGate.RecordAttempt(now, sent);
        if (!sent)
        {
            VoiceDiagnostics.Log(
                "settings.send_deferred",
                $"kind=host-snapshot transport=among-us-rpc target={targetClientId} reason={reason} rosterChanged={rosterChanged}");
            return false;
        }

        // A targeted response must not advance global broadcast dedupe: the rest of the lobby did
        // not receive it. Broadcast fallback responses may safely advance it.
        if (isBroadcast)
        {
            _lastSentHostSettings = settings;
            _lastSentModOptionRevision = modRevision;
            if (roster.HasValue)
                _lastSentHostSettingsRoster = roster;
        }
        VoiceDiagnostics.Log(
            "settings.sent",
            $"kind=host-snapshot transport=among-us-rpc target={targetClientId} reason={reason} rosterChanged={rosterChanged}");
        return true;
    }

    private bool IsHostPolicyReady()
    {
        if (IsLocalHost())
        {
            if (_hostPolicyFallbackLogged)
            {
                VoiceDiagnostics.Log(
                    "settings.policy.synced_after_fallback",
                    $"waited={Math.Max(0f, Time.realtimeSinceStartup - _hostPolicyWaitStartTime):0.000}s");
            }

            _hostPolicyWaitStartTime = -1f;
            _hostPolicyFallbackLogged = false;
            return true;
        }

        var now = Time.realtimeSinceStartup;

        if (_hostSettingsResyncPending)
        {
            if (_hostPolicyWaitStartTime < 0f)
                _hostPolicyWaitStartTime = now;

            var migrationWaited = Math.Max(0f, now - _hostPolicyWaitStartTime);
            if (CanUseTransitionalHostPolicy(
                    resyncPending: true,
                    hasRemoteSnapshot: VoiceRoomSettingsState.RemoteSnapshot.HasValue,
                    waitedSeconds: migrationWaited))
            {
                // Preserve the last authenticated host policy briefly so host migration does not
                // cut voice while the new authority is resolving. It is explicitly transitional:
                // the branch below expires it even when the replacement host never responds.
                return VoiceRoomSettingsState.RemoteSnapshot.HasValue;
            }

            if (!HasHostPolicyResyncTimedOut(resyncPending: true, waitedSeconds: migrationWaited))
                return false;

            if (VoiceRoomSettingsState.RemoteSnapshot.HasValue)
            {
                VoiceRoomSettingsState.ClearRemote();
                VoiceDiagnostics.Log(
                    "settings.host.remote_expired",
                    $"reason=resync-timeout hostClient={_lastObservedHostClientId} waited={migrationWaited:0.000}s");
            }

            if (!_hostPolicyFallbackLogged)
            {
                _hostPolicyFallbackLogged = true;
                VoiceDiagnostics.Log(
                    "settings.policy.fallback",
                    $"reason=host-transfer-timeout waited={migrationWaited:0.000}s action=use-local-policy transport=native-engine");
            }

            // Keep resync pending so authenticated requests continue. A later new-host snapshot
            // replaces the fallback and clears the pending generation in Note...Applied.
            return true;
        }

        if (VoiceRoomSettingsState.RemoteSnapshot.HasValue)
        {
            if (_hostPolicyFallbackLogged)
            {
                VoiceDiagnostics.Log(
                    "settings.policy.synced_after_fallback",
                    $"waited={Math.Max(0f, now - _hostPolicyWaitStartTime):0.000}s");
            }

            _hostPolicyWaitStartTime = -1f;
            _hostPolicyFallbackLogged = false;
            return true;
        }

        if (_hostPolicyWaitStartTime < 0f)
            _hostPolicyWaitStartTime = now;

        var waited = Math.Max(0f, now - _hostPolicyWaitStartTime);
        if (waited < HostPolicySyncFallbackSeconds)
            return false;

        if (!_hostPolicyFallbackLogged)
        {
            _hostPolicyFallbackLogged = true;
            VoiceDiagnostics.Log(
                "settings.policy.fallback",
                $"reason=host-snapshot-timeout waited={waited:0.000}s action=use-local-policy transport=native-engine");
        }

        // This is a policy-only compatibility fallback for vanilla/older hosts. The native media
        // session and authenticated RPC signaling were already allowed to bootstrap immediately.
        return true;
    }

    internal static bool HasHostPolicyResyncTimedOut(bool resyncPending, float waitedSeconds)
        => resyncPending && waitedSeconds >= HostPolicySyncFallbackSeconds;

    internal static bool CanUseTransitionalHostPolicy(
        bool resyncPending,
        bool hasRemoteSnapshot,
        float waitedSeconds)
        => resyncPending
           && hasRemoteSnapshot
           && !HasHostPolicyResyncTimedOut(true, waitedSeconds);

    private static ClientRosterSignature? CaptureClientRosterSignature()
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null) return null;
            var count = 0;
            var xor = 0;
            long sum = 0;
            foreach (var entry in client.allClients)
            {
                if (entry == null || entry.Id < 0) continue;
                count++;
                uint hash = 2166136261u;
                var id = entry.Id;
                for (var index = 0; index < 4; index++)
                    hash = (hash ^ (uint)((id >> (index * 8)) & 0xFF)) * 16777619u;
                xor ^= unchecked((int)hash);
                sum += id;
            }
            return new ClientRosterSignature(count, xor, sum);
        }
        catch
        {
            return null;
        }
    }

    private void TickVoiceBackend(VoiceGameStateSnapshot? snapshot)
    {
        TrackHostSettingsAuthority(snapshot);
        var settings = VoiceSettings.Instance;

        // Bootstrap native media and authenticated signaling immediately. Host settings are policy,
        // not transport identity, and synchronize independently below.
        EnsureVoiceBackend(snapshot, settings);
        SendHostSettingsSnapshot(force: false, reason: "periodic-or-roster-change");
        RequestHostSettingsSnapshotIfNeeded();
    }

    private void TrackHostSettingsAuthority(VoiceGameStateSnapshot? snapshot)
    {
        var hostClientId = VoiceHostAuthority.ResolveHostClientId(snapshot);
        if (hostClientId < 0)
        {
            // A known host becoming temporarily unresolved is itself an authority-generation gap.
            // Keep the previous authenticated policy only for the bounded migration window and
            // continue requesting a fresh snapshot. Otherwise a host leave during roster rebuild
            // could leave the old host's policy authoritative indefinitely.
            if (ShouldBeginUnknownHostResync(
                    _lastObservedHostClientId,
                    hostClientId,
                    _hostSettingsResyncPending))
            {
                _hostSettingsResyncPending = true;
                _hostPolicyWaitStartTime = Time.realtimeSinceStartup;
                _hostPolicyFallbackLogged = false;
                _hostSettingsRequestGate.Reset();
                VoiceDiagnostics.Log(
                    "settings.host.unresolved",
                    $"previousHost={_lastObservedHostClientId} action=request-fresh-policy transitionalSeconds={HostPolicySyncFallbackSeconds:0}");
                HostSettingsPanel.RevokeForHostAuthorityChange(
                    newHostClientId: -1,
                    localClientId: ResolveLocalClientId(snapshot));
            }
            return;
        }

        if (_lastObservedHostClientId < 0)
        {
            _lastObservedHostClientId = hostClientId;
            HostSettingsPanel.RevokeForHostAuthorityChange(
                hostClientId,
                ResolveLocalClientId(snapshot));
            return;
        }

        if (_lastObservedHostClientId == hostClientId)
            return;

        var oldHostClientId = _lastObservedHostClientId;
        _lastObservedHostClientId = hostClientId;
        _lastSentHostSettings = null;
        _lastSentHostSettingsRoster = null;
        _hostSettingsBroadcastGate.Reset();
        _hostSettingsRequestGate.Reset();
        _hostSettingsResponseGates.Clear();
        _hostSettingsResyncPending = true;
        _hostPolicyWaitStartTime = Time.realtimeSinceStartup;
        _hostPolicyFallbackLogged = false;

        var localClientId = ResolveLocalClientId(snapshot);
        var localIsNewHost = localClientId >= 0 && localClientId == hostClientId;
        HostSettingsPanel.RevokeForHostAuthorityChange(hostClientId, localClientId);
        VoiceDiagnostics.Log("settings.host.changed", $"oldHost={oldHostClientId} newHost={hostClientId} localClient={localClientId} localIsNewHost={localIsNewHost}");

        if (localIsNewHost)
        {
            if (VoiceRoomSettingsState.RemoteSnapshot.HasValue)
            {
                VoiceRoomSettingsState.ClearRemote();
                VoiceDiagnostics.Log("settings.host.remote_cleared", $"reason=promoted oldHost={oldHostClientId} newHost={hostClientId}");
            }

            _hostSettingsResyncPending = false;
            _hostPolicyWaitStartTime = -1f;
            VoiceDiagnostics.Log("settings.host.promoted", $"oldHost={oldHostClientId} newHost={hostClientId} localClient={localClientId}");
            var sent = SendHostSettingsSnapshot(force: true, reason: "host-promoted");
            VoiceDiagnostics.Log(
                sent ? "settings.host.resync_sent" : "settings.host.resync_deferred",
                $"reason=host-transfer newHost={hostClientId} sent={sent}");
            return;
        }

        VoiceDiagnostics.Log("settings.host.resync_requested", $"oldHost={oldHostClientId} newHost={hostClientId} localClient={localClientId} hasRemote={VoiceRoomSettingsState.RemoteSnapshot.HasValue}");
        RequestHostSettingsSnapshot(force: true, reason: "host-transfer");
    }

    internal static bool ShouldBeginUnknownHostResync(
        int previousHostClientId,
        int resolvedHostClientId,
        bool alreadyPending)
        => previousHostClientId >= 0
           && resolvedHostClientId < 0
           && !alreadyPending;

    private int ResolveLocalClientId(VoiceGameStateSnapshot? snapshot)
    {
        if (snapshot?.LocalClientId >= 0)
            return snapshot.LocalClientId;

        try
        {
            return AmongUsClient.Instance?.ClientId ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private void EnsureVoiceBackend(VoiceGameStateSnapshot? snapshot, VoiceChatLocalSettings? settings)
    {
        // Process/domain shutdown is the one lifecycle path that can race the Unity update thread.
        // Serialize publication, initial configuration, and disposal so Close cannot observe null,
        // return, and then have this method publish a newly owned media session behind it.
        lock (_backendLifecycleSync)
            EnsureVoiceBackendCore(snapshot, settings);
    }

    private void EnsureVoiceBackendCore(VoiceGameStateSnapshot? snapshot, VoiceChatLocalSettings? settings)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;
        if (!IsCurrentSessionEligible(out _))
            return;
        if (!TryGetVoiceRoomIdentity(out var roomCode, out var region))
            return;

        bool forceRebuild = _forceBackendRebuild;
        _forceBackendRebuild = false;
        var continuingSameRoom = string.Equals(_activeRoomCode, roomCode, StringComparison.Ordinal);
        var preserveRecoveryState = ShouldPreserveMissingPeerRecoveryState(forceRebuild, continuingSameRoom);
        if (!continuingSameRoom)
            _relayOnlyForSession = false;

        if (!forceRebuild
            && _voiceBackend != null
            && continuingSameRoom)
        {
            _activeRegion = region;
            return;
        }

        VoiceDiagnostics.Log("transport.switch", $"backend=native-engine signaling=among-us-rpc room={roomCode} region={region}");
        ClearVoiceUiForLifecycleReset("transport switch");
        DisposeVoiceBackend();
        if (Volatile.Read(ref _closed) != 0)
            return;
        _lastSentHostSettings = null;
        _hostSettingsRequestGate.Reset();
        ResetRadioStateSync();
        var backend = new PerfectCommsVoiceBackend(roomCode, region, _relayOnlyForSession);
        if (Volatile.Read(ref _closed) != 0)
        {
            backend.Dispose();
            return;
        }
        _voiceBackend = backend;
        _perfectCommsVoice = backend;
        // Process/domain shutdown can close this room from another thread. If that close won after
        // the pre-publication check above, it saw no backend to dispose; publish first, then perform
        // a closed-state rollback so the newly created media owner can never be stranded on the
        // closed room. If Close already claimed it, its exchange owns disposal instead.
        if (Volatile.Read(ref _closed) != 0)
        {
            var published = Interlocked.CompareExchange(ref _voiceBackend, null, backend);
            _perfectCommsVoice = null;
            if (ReferenceEquals(published, backend))
                backend.Dispose();
            return;
        }
        // P1.2: pre-warm the one-time HUD init (sprite PNG decode + button/tooltip GameObjects) here, off the
        // game-entry frame — the same room-construction lifecycle slot as the backend's WarmOpusCodec. Runs on
        // the Unity main thread (this method already touches VoiceChatHudState below) and is idempotent, so the
        // per-frame EnsureHudButtons path remains the fallback.
        try { VoiceChatHudState.Prewarm(); }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugError($"[VC] HUD prewarm failed during transport bootstrap: {ex.Message}");
        }
        backend.SetMute(Mute);
        backend.SetMasterVolume(VoiceChatHudState.GetEffectiveMasterVolume(settings?.MasterVolume.Value ?? 1f));
        backend.SetNoiseGate(
            ApplyMicSensitivity(settings?.NoiseGateThreshold.Value ?? 0.003f, settings?.MicSensitivity.Value ?? 1f),
            ApplyMicSensitivity(settings?.VadThreshold.Value ?? 0.004f, settings?.MicSensitivity.Value ?? 1f));
        backend.SetCaptureRuntimeOptions(BuildCaptureRuntimeOptions(settings));
#if ANDROID
        SetMicrophone(settings?.MicrophoneDevice ?? string.Empty);
#else
        backend.SetMicrophone(settings?.MicrophoneDevice ?? string.Empty, settings?.MicVolume.Value ?? 1f);
#endif
#if WINDOWS
        backend.SetSpeaker(settings?.SpeakerDevice ?? string.Empty);
#else
        backend.SetSpeaker(string.Empty);
#endif
        if (snapshot != null)
        {
            if (snapshot.TryGetLocalPlayer(out var localPlayer))
                backend.UpdateProfile(snapshot.LocalPlayerId, localPlayer.PlayerName);
            SendRadioState(snapshot.LocalPlayerId, VoiceChatHudState.ActiveTeamRadioChannel());
        }
        _activeRoomCode = roomCode;
        _activeRegion = region;
        _missingPeerRecoveryReadyTime = Time.time + MissingPeerRecoveryGraceSeconds;
        if (!preserveRecoveryState)
        {
            _lastMissingPeerRecoveryTime = -999f;
            ResetMissingPeerRecoveryStormGuard();
        }
        else
        {
            VoiceDiagnostics.Log(
                "transport.peer-recovery.state-preserved",
                $"room={roomCode} globalAttempts={_globalRebuildAttempts} targetedAttempts={_missingPeerRecoveryAttempts} relayOnly={_relayOnlyForSession}");
        }
        StartBootstrapWindow("native media session started");
        ForceUpdateLocalProfile();
        SendHostSettingsSnapshot(force: true, reason: "media-session-started");
        VoiceDiagnostics.Log("transport.selected", $"backend=native-engine signaling=among-us-rpc room={roomCode} region={region} mic={UsingMicrophone} speaker={UsingSpeaker} localLevel={LocalMicLevel:0.000}");
    }

    private void TryRecoverMissingBackendPeers(VoiceGameStateSnapshot? snapshot)
    {
        if (_voiceBackend == null || snapshot == null)
            return;

        // A retained/incomplete transition roster is deliberately not evidence of transport loss.
        // EndGame has no world roster at all, and lobby/meeting scene changes can temporarily carry
        // authenticated identities while LocalPlayer or AllPlayerControls is rebuilding. Recovery here
        // would misread that settling state as a mesh collapse and destructively rebuild healthy media.
        if (snapshot.Phase == VoiceGamePhase.EndGame
            || snapshot.RoutingRosterRetained
            || !snapshot.LiveLocalPlayerResolved
            || !snapshot.PlayerEnumerationCompleted)
        {
            _missingPeerRecoveryReadyTime = Time.time + MissingPeerRecoveryGraceSeconds;
            return;
        }

        int remotePlayers = CountExpectedRemotePlayers(snapshot);
        int mappedPeers = _voiceBackend.CountMappedRemotePeers(snapshot);   // telemetry/peak only
        int openPeers = _voiceBackend.CountPeersWithOpenChannel(snapshot);  // health + collapse decision
        // Peers whose data channel is physically open even if their clientId isn't mapped to a live snapshot
        // player yet. On the lobby right after a round, a surviving peer is briefly unmapped (the local roster
        // hasn't re-listed the remote) while its channel is healthy and audio flows; the mapping self-heals via
        // the routing's FindTarget. Counting those as healthy here stops recovery from firing a destructive
        // global rebuild (new socket) ~8s into the lobby. A genuine split-brain (channel NOT open) still fails
        // this check and recovers, because its channel is closed/never-opened.
        int openChannelsRaw = _voiceBackend.CountOpenDataChannels();
        if (mappedPeers > _lastHealthyMappedPeers)
            _lastHealthyMappedPeers = mappedPeers; // diagnostics-only peak; NOT used to judge collapse (see IsMeshCollapse)
        if (remotePlayers == 0 || openPeers >= remotePlayers || openChannelsRaw >= remotePlayers)
        {
            // Fully healthy (or no remotes). Reset the grace timer AND the storm guard so the next genuine
            // shortfall starts a fresh capped/back-off cycle.
            _missingPeerRecoveryReadyTime = Time.time + MissingPeerRecoveryGraceSeconds;
            if (_missingPeerRecoveryAttempts != 0 || _missingPeerRecoveryLatched || _globalRebuildAttempts != 0 || _consecutivePeerEscalationDeferrals != 0)
            {
                _missingPeerRecoveryAttempts = 0;
                _globalRebuildAttempts = 0;
                _missingPeerRecoveryLatched = false;
                _lastRecoveryOpenPeers = -1;
                _lastRecoveryRemoteSignature = 0;
                _consecutivePeerEscalationDeferrals = 0;
            }
            return;
        }

        if (Time.time < _missingPeerRecoveryReadyTime)
            return;

        // The set of expected remotes. If it changes, the shortfall is "new" — unlatch and restart the
        // cap/backoff so a genuinely-changed lobby always gets fresh recovery attempts. Use a cheap
        // allocation-free fold over the expected clientIds (this runs per-frame during any shortfall, before
        // the latch/backoff gates) — the human-readable LINQ signature is computed only below, once recovery
        // actually fires.
        int remoteSignature = HashExpectedRemotePlayers(snapshot);
        bool setChanged = remoteSignature != _lastRecoveryRemoteSignature;
        bool improved = _lastRecoveryOpenPeers >= 0 && openPeers > _lastRecoveryOpenPeers;
        if (setChanged || improved)
        {
            _missingPeerRecoveryLatched = false;
            _missingPeerRecoveryAttempts = 0;
        }

        // Latched on a permanently-unmappable shortfall: do NOT fire recovery (no 5 s teardown cadence). Stay
        // latched until the expected-remote set changes or mappedPeers actually increases (handled above).
        if (_missingPeerRecoveryLatched)
            return;

        // Classify a real collapse — no peers mapped at all, or mapped count fell
        // below half of the CURRENTLY-expected remote count. A small shortfall (most peers mapped) takes the
        // targeted, non-destructive path so already-open peers keep their channels. The threshold is relative
        // to the live roster (NOT a stale healthy peak) so a roster shrink can't be misread as a collapse and
        // re-fire the destructive global Rejoin on a healthy-but-smaller lobby.
        bool collapsed = IsMeshCollapse(openPeers, remotePlayers);

        // Exponential backoff between attempts. The targeted path and the global/collapse path use SEPARATE
        // counters so a genuine total collapse (mappedPeers == 0, signaling down) is still bounded instead of
        // re-firing a global Rejoin every interval forever.
        int backoffAttempts = collapsed ? _globalRebuildAttempts : _missingPeerRecoveryAttempts;
        float backoff = RecoveryBackoffSeconds(backoffAttempts);
        if (Time.time - _lastMissingPeerRecoveryTime < backoff)
            return;

        bool deferRequested = _perfectCommsVoice?.ShouldDeferPeerEscalation == true;
        if (deferRequested)
        {
            if (_consecutivePeerEscalationDeferrals < int.MaxValue)
                _consecutivePeerEscalationDeferrals++;
            _missingPeerRecoveryReadyTime = Time.time + PeerEscalationDeferralRecheckSeconds;
            VoiceDiagnostics.Log("transport.peer-recovery.deferred",
                $"backend=native-engine reason=backend-recovery-in-flight remotePlayers={remotePlayers} peers={mappedPeers} open={openPeers} " +
                $"collapsed={collapsed} recheckSec={PeerEscalationDeferralRecheckSeconds:0.0} deferrals={_consecutivePeerEscalationDeferrals}");
            return;
        }
        _consecutivePeerEscalationDeferrals = 0;

        bool finalCollapseAttempt = collapsed && (openChannelsRaw == 0 || backoffAttempts + 1 >= MissingPeerRecoveryMaxAttempts);

        _lastMissingPeerRecoveryTime = Time.time;
        string remoteSignatureText = DescribeExpectedRemotePlayers(snapshot);
        string rpcPeerDiagnostics = _perfectCommsVoice?.DescribeRpcPeerDiagnostics() ?? "backend-unavailable";
        VoiceDiagnostics.Log("transport.peer-recovery",
            $"backend=native-engine reason=missing-peer remotePlayers={remotePlayers} peers={mappedPeers} open={openPeers} rawPeers={_voiceBackend.PeerCount} " +
            $"mode={(finalCollapseAttempt ? "global" : collapsed ? "collapse-targeted" : "targeted")} attempt={(collapsed ? _globalRebuildAttempts + 1 : _missingPeerRecoveryAttempts + 1)}/{MissingPeerRecoveryMaxAttempts} healthyPeak={_lastHealthyMappedPeers} backoffSec={backoff:0.0} " +
            $"room={_activeRoomCode ?? "unknown"} region={_activeRegion ?? "unknown"} " +
            $"liveClients=[{remoteSignatureText}] rpcPeers=[{rpcPeerDiagnostics}]");

        bool didGlobal = false;
        if (finalCollapseAttempt)
        {
            // Automated relay escalation: if we've never mapped a single peer despite repeated global rebuilds
            // (remotePlayers exist but mappedPeers==0), direct/STUN ICE is clearly not working for this client
            // (strict/symmetric NAT, or a Wine box where host-candidate gathering fails). Latch the native session
            // to relay-only ICE before this rebuild so the fresh peer connections route through TURN. Reuses the
            // same forceRelay path the Wine fix validated; only fires after total failure, so a client whose
            // voice already works never reaches here.
            if (ShouldEscalateTotalCollapseToRelay(
                    openPeers,
                    openChannelsRaw,
                    remotePlayers,
                    _globalRebuildAttempts + 1))
            {
                _relayOnlyForSession = true;
                _perfectCommsVoice?.EscalateToRelayOnly($"global-attempt-{_globalRebuildAttempts + 1}");
            }

            ClearVoiceUiForLifecycleReset("missing peer recovery");
            // Rebuild only after a confirmed total collapse. Dispose sends Bye first so surviving
            // peers discard the old negotiation generation before the replacement starts.
            _forceBackendRebuild = true;
            ResetSettingsSyncState(preserveHostAuthority: true);
            StartBootstrapWindow("missing voice backend peer");
            ForceUpdateLocalProfile();
            didGlobal = true;
        }
        else
        {
            // Targeted, non-destructive recovery of only the unmapped/wedged client(s). A -1 result
            // means there is no targeted path, so fall back to one global rebuild.
            int recovered = _voiceBackend.TryRecoverMissingClients(snapshot);
            if (recovered < 0)
            {
                ClearVoiceUiForLifecycleReset("missing peer recovery");
                // Rebuild the native session when targeted recovery is unavailable.
                _forceBackendRebuild = true;
                ResetSettingsSyncState(preserveHostAuthority: true);
                StartBootstrapWindow("missing voice backend peer");
                ForceUpdateLocalProfile();
                didGlobal = true;
            }
            else
            {
                VoiceDiagnostics.Log("transport.peer-recovery-targeted",
                    $"backend=native-engine recovered={recovered} peers={mappedPeers} remotePlayers={remotePlayers}");
            }
        }

        // Count this attempt. The targeted path uses the cap+latch: after MissingPeerRecoveryMaxAttempts
        // non-improving tries it LATCHES so we stop firing on a permanent shortfall. The global/collapse path
        // does NOT latch (a total collapse must keep retrying), but it grows its OWN backoff counter so it
        // can't re-fire a destructive global Rejoin every interval forever — the interval grows to the cap.
        if (didGlobal || collapsed)
        {
            if (_globalRebuildAttempts < int.MaxValue)
                _globalRebuildAttempts++;
        }
        else
        {
            _missingPeerRecoveryAttempts++;
            if (_missingPeerRecoveryAttempts >= MissingPeerRecoveryMaxAttempts)
            {
                _missingPeerRecoveryLatched = true;
                VoiceDiagnostics.Log("transport.peer-recovery-latched",
                    $"backend=native-engine attempts={_missingPeerRecoveryAttempts} peers={mappedPeers} remotePlayers={remotePlayers} " +
                    $"reason=permanent-shortfall liveClients=[{remoteSignatureText}]");
            }
        }
        _lastRecoveryOpenPeers = openPeers;
        _lastRecoveryRemoteSignature = remoteSignature;
    }

    // P0 collapse gate (pure, unit-tested). A mesh is "collapsed" only when no peers are mapped at all, or
    // fewer than HALF of the CURRENTLY-expected remotes are mapped. Relative to the live roster — never a
    // stale peak — so a roster shrink (e.g. 12->4) can't be misclassified as a collapse and re-fire the
    // destructive global Rejoin on a healthy-but-smaller lobby. A small shortfall (e.g. 3 of 4) is NOT a
    // collapse and takes the targeted, non-destructive path.
    internal static bool IsMeshCollapse(int mappedPeers, int remotePlayers)
        => mappedPeers == 0 || (remotePlayers > 0 && mappedPeers * 2 < remotePlayers);

    // Route records are created from the game roster before ICE connects, so mappedPeers cannot
    // prove transport health. Escalate only after repeated attempts with no established channel at
    // all (including temporarily-unmapped survivors), which identifies a true direct/STUN collapse.
    internal static bool ShouldEscalateTotalCollapseToRelay(
        int openPeers,
        int openChannelsRaw,
        int remotePlayers,
        int globalAttempt)
        => remotePlayers > 0
           && openPeers == 0
           && openChannelsRaw == 0
           && globalAttempt >= 2;

    // Exponential backoff (seconds) for a recovery attempt counter: the base interval doubled per prior
    // non-improving attempt, clamped so a stubborn shortfall slows down instead of re-firing every interval.
    // Pure + unit-tested. Shared by the targeted and global/collapse paths (each with its own counter).
    internal static float RecoveryBackoffSeconds(int attempts)
        => MissingPeerRecoveryIntervalSeconds * (1 << Math.Min(Math.Max(attempts, 0), MissingPeerRecoveryBackoffShiftCap));

    // A watchdog-requested rebuild of the same authenticated room is one recovery generation,
    // not a fresh session. Keep its attempt counter, elapsed backoff and relay latch so repeated
    // failures advance toward relay and the capped interval instead of restarting at attempt one.
    internal static bool ShouldPreserveMissingPeerRecoveryState(bool forceRebuild, bool continuingSameRoom)
        => forceRebuild && continuingSameRoom;

    private bool IsExpectedRemotePlayer(VoicePlayerSnapshot player)
        => !player.IsLocal && !player.Disconnected && !player.IsDummy && player.ClientId >= 0 &&
           (_perfectCommsVoice == null || _perfectCommsVoice.IsCompatibleRemoteClient(player.ClientId));

    private int CountExpectedRemotePlayers(VoiceGameStateSnapshot snapshot)
    {
        // Indexed loop over IReadOnlyList instead of LINQ .Count(predicate): the latter boxes a heap
        // enumerator on every call, and this runs per-frame via TryRecoverMissingBackendPeers (before its
        // time gates). The predicate is unchanged.
        var players = snapshot.Players;
        int count = 0;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (IsExpectedRemotePlayer(player))
                count++;
        }
        return count;
    }

    // Allocation-free order-independent fold over the expected-remote clientIds (an FNV-1a fold mixed with an
    // order-independent XOR accumulate). Used per-frame for the recovery latch's set-change detection so we
    // don't build/compare the human-readable LINQ signature on every shortfall frame. Matches the de-LINQ'd
    // indexed-loop style of CountExpectedRemotePlayers. The human-readable signature (DescribeExpected...) is
    // only built once recovery actually fires.
    private int HashExpectedRemotePlayers(VoiceGameStateSnapshot snapshot)
    {
        var players = snapshot.Players;
        int acc = 0;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (!IsExpectedRemotePlayer(player))
                continue;
            // FNV-1a over the clientId bytes, XOR-accumulated so roster order doesn't change the result.
            uint h = 2166136261u;
            int id = player.ClientId;
            for (int b = 0; b < 4; b++)
            {
                h = (h ^ (uint)((id >> (b * 8)) & 0xFF)) * 16777619u;
            }
            acc ^= unchecked((int)h);
        }
        return acc;
    }

    private string DescribeExpectedRemotePlayers(VoiceGameStateSnapshot snapshot)
        => string.Join(",", snapshot.Players
            .Where(IsExpectedRemotePlayer)
            .Select(player => $"{player.ClientId}:{LogSafe(player.PlayerName)}"));

    internal static bool IsCurrentSessionEligible(out string reason)
    {
        reason = string.Empty;
        AmongUsClient? client;
        try { client = AmongUsClient.Instance; }
        catch
        {
            reason = "client-unavailable";
            return false;
        }

        if (client == null)
        {
            reason = "client-unavailable";
            return false;
        }
        if (VoiceRoomLifetimeGate.IsExplicitDisconnectLatched)
        {
            reason = "disconnect-latched";
            return false;
        }

        int gameId;
        string? address;
        try
        {
            gameId = client.GameId;
            address = client.networkAddress;
        }
        catch
        {
            reason = "session-identity-unavailable";
            return false;
        }

        if (gameId == 0)
        {
            reason = "missing-game-id";
            return false;
        }
        if (IsLocalVoiceEndpoint(address))
        {
            reason = "local-or-freeplay";
            return false;
        }
        if (!VoiceJoinGuard.CanStartVoiceForCurrentSession(gameId, out reason))
            return false;
        return true;
    }

    internal static bool IsLocalVoiceEndpoint(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var value = address.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
            value = uri.Host;

        if (value.StartsWith("[", StringComparison.Ordinal)
            && value.IndexOf(']') is var bracket && bracket > 1)
        {
            value = value.Substring(1, bracket - 1);
        }
        else
        {
            // Strip an IPv4/hostname port without damaging an unbracketed IPv6 literal.
            int firstColon = value.IndexOf(':');
            if (firstColon > 0 && firstColon == value.LastIndexOf(':'))
                value = value.Substring(0, firstColon);
        }

        value = value.Trim().TrimEnd('.');
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(value, out var ip) && IPAddress.IsLoopback(ip);
    }

    private static bool TryGetVoiceRoomIdentity(out string roomCode, out string region)
    {
        roomCode = string.Empty;
        region = "default";
        if (!IsCurrentSessionEligible(out _))
            return false;
        var client = AmongUsClient.Instance;
        if (client == null)
            return false;

        try
        {
            if (client.GameId == 0)
                return false;

            roomCode = GameCode.IntToGameName(client.GameId);
            if (string.IsNullOrWhiteSpace(roomCode) || string.Equals(roomCode, "????", StringComparison.Ordinal))
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            var stableRegion = DestroyableSingleton<ServerManager>.Instance?.CurrentRegion?.Name;
            if (!string.IsNullOrWhiteSpace(stableRegion))
            {
                region = stableRegion.Trim();
                return true;
            }
        }
        catch
        {
        }

        try
        {
            var networkAddress = client.networkAddress;
            if (!string.IsNullOrWhiteSpace(networkAddress))
                region = networkAddress.Trim();
        }
        catch
        {
            region = "default";
        }

        return true;
    }

    private void RequestHostSettingsSnapshotIfNeeded()
    {
        RequestHostSettingsSnapshot(force: false, reason: _hostSettingsResyncPending ? "host-transfer-pending" : "missing-host-snapshot");
    }

    private void RequestHostSettingsSnapshot(bool force, string reason)
    {
        if (IsLocalHost()) return;
        if (!force && VoiceRoomSettingsState.RemoteSnapshot.HasValue && !_hostSettingsResyncPending) return;

        var now = DateTime.UtcNow;
        if (!_hostSettingsRequestGate.CanAttempt(now, force)) return;

        // Request the host snapshot over the authenticated RPC only (see SendHostSettingsSnapshot).
        var hostClientId = VoiceHostAuthority.ResolveHostClientId(CurrentSnapshot);
        var targetClientId = hostClientId >= 0 ? hostClientId : -1;
        var sent = VoiceRoomSettingsRpc.TrySendRequest(targetClientId);
        _hostSettingsRequestGate.RecordAttempt(now, sent);
        VoiceDiagnostics.Log(
            sent ? "settings.requested" : "settings.request.deferred",
            $"kind=host-snapshot transport=among-us-rpc reason={reason} force={force} target={targetClientId} sent={sent}");
    }

    internal static void RespondToHostSettingsRequest()
    {
        Current?.RespondToHostSettingsRequestFromSender(VoiceHostSenderIdentity.Unknown("rpc"));
    }

    internal static void RespondToHostSettingsRequest(VoiceHostSenderIdentity sender)
    {
        Current?.RespondToHostSettingsRequestFromSender(sender);
    }

    internal static void NoteHostSettingsSnapshotApplied(string transport, int hostClientId, byte hostPlayerId)
    {
        var current = Current;
        if (current == null || !current._hostSettingsResyncPending)
            return;

        current._hostSettingsResyncPending = false;
        current._hostSettingsRequestGate.Reset();
        current._hostPolicyWaitStartTime = -1f;
        current._hostPolicyFallbackLogged = false;
        VoiceDiagnostics.Log("settings.host.resync_applied", $"transport={transport} hostClient={hostClientId} hostPlayer={hostPlayerId}");
    }

    internal static void NoteHostSettingsSnapshotRejected()
    {
        // Snapshot rejected (e.g. stale host id during migration): flag a re-request so settings
        // converge once the host id resolves. The successful-send gate bounds retries to 2 seconds.
        var current = Current;
        if (current == null || current.IsLocalHost())
            return;

        if (!current._hostSettingsResyncPending)
            current._hostPolicyWaitStartTime = Time.realtimeSinceStartup;
        current._hostSettingsResyncPending = true;
        current._hostSettingsRequestGate.Reset();
    }

    private static int ResolveCurrentGameId()
    {
        try { return AmongUsClient.Instance?.GameId ?? 0; }
        catch { return 0; }
    }

    private void RespondToHostSettingsRequestFromSender(VoiceHostSenderIdentity sender)
    {
        VoiceDiagnostics.Log("settings.request.received", sender.ToDiagnosticFields());
        if (!IsLocalHost()) return;

        var now = DateTime.UtcNow;
        if (!_hostSettingsResponseGates.TryGetValue(sender.StableKey, out var gate))
        {
            gate = new SuccessfulSendGate(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
            _hostSettingsResponseGates[sender.StableKey] = gate;
        }
        if (!gate.CanAttempt(now))
        {
            VoiceDiagnostics.Log("settings.request.rate_limited", sender.ToDiagnosticFields());
            return;
        }

        var targetClientId = sender.SenderClientId >= 0 ? sender.SenderClientId : -1;
        var sent = SendHostSettingsSnapshot(force: true, reason: "request-response", targetClientId: targetClientId);
        gate.RecordAttempt(now, sent);
        if (!sent)
            VoiceDiagnostics.Log(
                "settings.request.response_deferred",
                $"{sender.ToDiagnosticFields()} target={targetClientId}");
    }

    internal static void ApplyRemoteRadioState(byte playerId, VoiceTeamRadioChannel channel)
    {
        Current?._voiceBackend?.ApplyRemoteRadioState(playerId, channel);
    }

    private void SendRadioState(byte playerId, VoiceTeamRadioChannel channel)
    {
        channel = VoiceTeamRadioChannels.Normalize(channel);
        SyncRadioStateRpc(playerId, channel);
    }

    private void SyncRadioStateRpc(byte playerId, VoiceTeamRadioChannel channel)
    {
        if (playerId == byte.MaxValue) return;

        var now = DateTime.UtcNow;
        if (!_radioStateSync.ShouldAttempt(playerId, channel, now)) return;

        var sent = VoiceRadioStateRpc.TrySend(playerId, channel);
        _radioStateSync.RecordAttempt(playerId, channel, now, sent);
    }

    private void DisposeVoiceBackend()
    {
        lock (_backendLifecycleSync)
        {
            var backend = Interlocked.Exchange(ref _voiceBackend, null);
            _perfectCommsVoice = null;
            if (backend == null) return;
            RunCleanupStep("backend-dispose", backend.Dispose);
        }
    }

    private static bool CheckCommsSabotage(out string source)
    {
        var ship = ShipStatus.Instance;
        if (ship == null)
        {
            source = "no-ship";
            return false;
        }

        if (!ship.Systems.TryGetValue(SystemTypes.Comms, out var comms))
        {
            source = "no-comms-system";
            return false;
        }

        var hud = comms.TryCast<HudOverrideSystemType>();
        if (hud != null)
        {
            source = "HudOverrideSystemType";
            return hud.IsActive;
        }

        var hqHud = comms.TryCast<HqHudSystemType>();
        if (hqHud != null)
        {
            source = "HqHudSystemType";
            return hqHud.IsActive;
        }

        var activatable = comms.TryCast<IActivatable>();
        if (activatable != null)
        {
            source = $"IActivatable:{comms.GetType().Name}";
            return activatable.IsActive;
        }

        source = comms.GetType().Name;
        return false;
    }

    public void Rejoin()
        => Rejoin("manual rejoin");

    // Forward a custom TURN / relay-policy setting change to the active backend so it can rebuild its peer-connection
    // pool off the main thread (no rejoin needed; existing peers keep their connections).
    public void RebuildIceConnectionPool()
        => _voiceBackend?.RebuildIceConnectionPool();

    private void Rejoin(string reason)
    {
        // Media ownership is the non-negotiable cleanup boundary. Release it before touching any
        // transition-sensitive Unity object so a destroyed HUD cannot strand a helper process.
        DisposeVoiceBackend();
        ClearVoiceUiForLifecycleReset(reason);
        CurrentSnapshot = null;
        _snapshotGameId = 0;
        _retainingTransitionSnapshot = false;
        _missingSnapshotClientSince.Clear();
        _authenticatedSnapshotClientIds.Clear();
        _snapshotRefreshTimer = 0f;
        _missingPeerRecoveryReadyTime = -999f;
        _lastMissingPeerRecoveryTime = -999f;
        ResetMissingPeerRecoveryStormGuard();
        ResetSettingsSyncState(preserveHostAuthority: true);
        StartBootstrapWindow(reason);
    }

    // Clears the storm-guard latch/backoff so a fresh backend or lifecycle reset starts recovery clean.
    private void ResetMissingPeerRecoveryStormGuard()
    {
        _missingPeerRecoveryAttempts = 0;
        _globalRebuildAttempts = 0;
        _lastRecoveryOpenPeers = -1;
        _lastHealthyMappedPeers = 0;
        _lastRecoveryRemoteSignature = 0;
        _missingPeerRecoveryLatched = false;
        _consecutivePeerEscalationDeferrals = 0;
    }

    private void ResetSettingsSyncState(bool clearRemote = false, bool preserveHostAuthority = false)
    {
        if (clearRemote)
            VoiceRoomSettingsState.EndSession();
        _lastSentHostSettings = null;
        _lastSentModOptionRevision = -1;
        _lastSentHostSettingsRoster = null;
        _hostSettingsBroadcastGate.Reset();
        _hostSettingsRequestGate.Reset();
        if (ShouldClearHostAuthorityOnSettingsReset(clearRemote, preserveHostAuthority))
        {
            _lastObservedHostClientId = -1;
            _hostSettingsResyncPending = false;
            _hostPolicyWaitStartTime = -1f;
            _hostPolicyFallbackLogged = false;
        }
        _hostSettingsResponseGates.Clear();
        ResetRadioStateSync();
    }

    internal static bool ShouldClearHostAuthorityOnSettingsReset(
        bool clearRemote,
        bool preserveHostAuthority)
        => clearRemote || !preserveHostAuthority;

    private void ResetRadioStateSync()
    {
        _radioStateSync.Reset();
    }

    public void Close()
        => Close("room close", clearUi: true);

    private void Close(string reason, bool clearUi)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        // Teardown first, UI second: Current may already be null and Unity objects may be half
        // destroyed, but the sidecar/mobile lease must always be released exactly once.
        DisposeVoiceBackend();
        if (clearUi)
            ClearVoiceUiForLifecycleReset(reason);
        _lastCompatibilityRefreshTime = -999f;
        CurrentSnapshot = null;
        _snapshotGameId = 0;
        _retainingTransitionSnapshot = false;
        _missingSnapshotClientSince.Clear();
        _authenticatedSnapshotClientIds.Clear();
        _snapshotRefreshTimer = 0f;
        _bootstrapUntilTime = -999f;
        _bootstrapRefreshTimer = 0f;
        RunCleanupStep("settings-reset", () => ResetSettingsSyncState(clearRemote: true));
        RunCleanupStep("transition-trace-reset", ResetTransitionTraceState);
        try { VoiceDiagnostics.Log("room.close", $"reason={LogSafe(reason)} state=cleared"); } catch { }
        RunCleanupStep("client-registry-reset", VoiceClientRegistry.Reset);
        RunCleanupStep("role-mute-reset", VoiceRoleMuteState.Reset);
        RunCleanupStep("transmit-input-reset", VoiceChatPatches.ReleaseHeldTransmitInputs);
        RunCleanupStep("hud-session-reset", VoiceChatHudState.EndVoiceSession);
        RunCleanupStep("scene-state-reset", VoiceSceneState.Reset);
        if (clearUi)
            RunCleanupStep("persistent-panels", () => VoiceUiKit.ClosePersistentPanels($"session:{reason}"));
    }

    private void TryUpdateLocalProfile()  => UpdateLocalProfile(false);
    internal void ForceUpdateLocalProfile() => UpdateLocalProfile(true);

    private void UpdateLocalProfile(bool always)
    {
        var lp = PlayerControl.LocalPlayer;
        if (!lp) return;
        if (!always && lp.PlayerId == _lastId && lp.name == _lastName) return;

        _lastId   = lp.PlayerId;
        _lastName = lp.name;

        try
        {
            var safeName = TrimProfileName(_lastName);
            _voiceBackend?.UpdateProfile(_lastId, safeName);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugError($"[VC] Profile broadcast error: {ex.Message}");
        }
    }

    internal void ForceCompatibilityRefresh(string reason)
    {
        StartBootstrapWindow(reason);
        StartTransitionTrace($"transport refresh: {reason}", CurrentSnapshot);
        RefreshCompatibilityNow(reason, throttle: false);
    }

    private void StartBootstrapWindow(string reason)
    {
        _bootstrapUntilTime = Math.Max(_bootstrapUntilTime, Time.time + BootstrapWindowSeconds);
        _bootstrapRefreshTimer = 0f;
        VoiceDiagnostics.Log("bootstrap.start", reason);
    }

    private void TryRunBootstrapRefresh()
    {
        if (Time.time > _bootstrapUntilTime) return;

        _bootstrapRefreshTimer -= Time.deltaTime;
        if (_bootstrapRefreshTimer > 0f) return;

        _bootstrapRefreshTimer = BootstrapRefreshInterval;
        ForceUpdateLocalProfile();
    }

    private void RefreshCompatibilityNow(string reason, bool throttle)
    {
        if (throttle && Time.time - _lastCompatibilityRefreshTime < 0.25f)
            return;

        _lastCompatibilityRefreshTime = Time.time;
        ForceUpdateLocalProfile();
        VoiceDiagnostics.Log("transport.refresh", reason);
    }

    // Also requires diagnostics to be enabled: this single gate short-circuits MaybeLogTransitionTraceState
    // (the ~0.25s state dump), TraceUpdateCost and TraceOperationCost, so their string/LINQ snapshot
    // construction never runs during the 45s post-transition window when logging is off (the default).
    private bool IsTransitionTraceActive => VoiceDiagnostics.IsEnabled && DateTime.UtcNow <= _transitionTraceUntilUtc;

    private void StartTransitionTrace(string reason, VoiceGameStateSnapshot? snapshot)
    {
        if (!VoiceDiagnostics.IsEnabled) return;
        _transitionTraceUntilUtc = DateTime.UtcNow.AddSeconds(TransitionTraceSeconds);
        _transitionTraceStateTimer = 0f;
        _tracePerfEventsRemaining = TransitionTracePerfEvents;

        VoiceDiagnostics.Log("transition.trace.start",
            $"reason=\"{LogSafe(reason)}\" duration={TransitionTraceSeconds:0.0}s liveClients=[{DescribeLiveClients()}] snapshot=\"{LogSafe(DescribeTransitionSnapshot(snapshot))}\"");
        LogDetailedGameState(snapshot);
    }

    private void ResetTransitionTraceState()
    {
        _transitionTraceUntilUtc = DateTime.MinValue;
        _transitionTraceStateTimer = 0f;
        _tracePerfEventsRemaining = 0;
        _haveTracePhase = false;
        _lastTracePhase = VoiceGamePhase.Unknown;
        _haveRoutingPhase = false;
        _lastRoutingPhase = VoiceGamePhase.Unknown;
        _lastLoggedLocalState = null;
        _lastLoggedOptions = null;
    }

    private void TrackTransitionPhase(VoiceGameStateSnapshot? snapshot)
    {
        if (snapshot != null)
        {
            var routingPhase = snapshot.Phase;
            var resetSight = ShouldResetSightStateForPhaseBoundary(
                _haveRoutingPhase,
                _lastRoutingPhase,
                routingPhase);
            _haveRoutingPhase = true;
            _lastRoutingPhase = routingPhase;
            if (resetSight)
                VoiceProximityCalculator.ResetSightState();
        }

        // Purely diagnostic phase tracking; skip entirely (incl. the initial-phase snapshot string build)
        // when diagnostics are disabled so no per-phase-change LINQ/string.Join runs in normal play.
        if (!VoiceDiagnostics.IsEnabled) return;

        var phase = snapshot?.Phase ?? VoiceGamePhase.Unknown;
        if (!_haveTracePhase)
        {
            _haveTracePhase = true;
            _lastTracePhase = phase;
            VoiceDiagnostics.Log("transition.phase.initial", $"phase={phase} snapshot=\"{LogSafe(DescribeTransitionSnapshot(snapshot))}\"");
            return;
        }

        if (phase == _lastTracePhase) return;

        var previous = _lastTracePhase;
        _lastTracePhase = phase;
        StartTransitionTrace($"phase {previous}->{phase}", snapshot);
    }

    internal static bool ShouldResetSightStateForPhaseBoundary(
        bool havePreviousPhase,
        VoiceGamePhase previousPhase,
        VoiceGamePhase currentPhase)
        => (!havePreviousPhase || previousPhase != currentPhase)
           && currentPhase is VoiceGamePhase.Intro or VoiceGamePhase.EndGame;

    private void MaybeLogTransitionTraceState(VoiceGameStateSnapshot? snapshot)
    {
        if (!IsTransitionTraceActive) return;

        _transitionTraceStateTimer -= Time.deltaTime;
        if (_transitionTraceStateTimer > 0f) return;

        _transitionTraceStateTimer = TransitionTraceStateInterval;
        var rpcPeers = _perfectCommsVoice?.DescribeRpcPeerDiagnostics() ?? "backend-unavailable";
        VoiceDiagnostics.Log("transition.state",
            $"remaining={(_transitionTraceUntilUtc - DateTime.UtcNow).TotalSeconds:0.000}s " +
            $"liveClients=[{DescribeLiveClients()}] rpcPeers=[{rpcPeers}] legacyRegistry=[{DescribeRegistryState()}] " +
            $"micLevel={LocalMicLevel:0.000} micSpeaking={LocalMicSpeaking} mute={Mute} speakerMuted={VoiceChatHudState.IsSpeakerMuted} " +
            $"snapshot=\"{LogSafe(DescribeTransitionSnapshot(snapshot))}\"");
        LogDetailedGameState(snapshot);
    }

    private void TraceUpdateCost(long startTicks, string completedStep, VoiceGameStateSnapshot? snapshot)
    {
        if (!IsTransitionTraceActive) return;

        double elapsedMs = ElapsedMilliseconds(startTicks);
        bool slow = elapsedMs >= SlowUpdateLogThresholdMs;
        if (!slow && _tracePerfEventsRemaining <= 0) return;
        if (!slow) _tracePerfEventsRemaining--;

        VoiceDiagnostics.Log(slow ? "transition.perf.slowUpdate" : "transition.perf.update",
            $"elapsedMs={elapsedMs:0.000} completedStep={completedStep} phase={snapshot?.Phase.ToString() ?? "none"} " +            $"micLevel={LocalMicLevel:0.000} speaking={LocalMicSpeaking} mute={Mute}");
    }

    private void TraceOperationCost(string category, string message, double elapsedMs)
    {
        if (!IsTransitionTraceActive) return;
        if (elapsedMs < SlowOperationLogThresholdMs && _tracePerfEventsRemaining <= 0) return;
        if (elapsedMs < SlowOperationLogThresholdMs) _tracePerfEventsRemaining--;

        VoiceDiagnostics.Log(category, message);
    }

    private static double ElapsedMilliseconds(long startTicks)
        => (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static PcmStats InspectPcm(float[] samples, int count)
    {
        int limit = Math.Min(count, samples.Length);
        if (limit <= 0) return default;

        double sumSquares = 0;
        float peak = 0f;
        int zeroSamples = 0;
        int clippedSamples = 0;
        for (int i = 0; i < limit; i++)
        {
            float sample = samples[i];
            float abs = sample < 0f ? -sample : sample;
            if (abs > peak) peak = abs;
            if (abs <= 0.00001f) zeroSamples++;
            if (abs >= 0.98f) clippedSamples++;
            sumSquares += sample * sample;
        }

        return new PcmStats(
            peak,
            (float)Math.Sqrt(sumSquares / limit),
            zeroSamples / (float)limit,
            clippedSamples,
            samples[0],
            samples[limit - 1]);
    }

    private readonly record struct PcmStats(float Peak, float Rms, float ZeroRatio, int ClippedSamples, float FirstSample, float LastSample);

    private string DescribeTransitionSnapshot(VoiceGameStateSnapshot? snapshot)
    {
        if (snapshot == null) return "snapshot=none";

        string players = snapshot.Players.Count == 0
            ? "none"
            : string.Join(";", snapshot.Players.Select(p =>
                $"p={p.PlayerId}/c={p.ClientId}/name={LogSafe(p.PlayerName)}/pos={FormatVector(p.Position)}/local={p.IsLocal}/dead={p.IsDead}/disc={p.Disconnected}/vis={p.IsVisible}"));

        return $"phase={snapshot.Phase} map={snapshot.MapId} localClient={snapshot.LocalClientId} localPlayer={snapshot.LocalPlayerId} " +
               $"localPos={FormatVector(snapshot.LocalPosition)} liveLocal={snapshot.LiveLocalPlayerResolved} " +
               $"rosterRetained={snapshot.RoutingRosterRetained} enumerationComplete={snapshot.PlayerEnumerationCompleted} " +
               $"meeting={snapshot.MeetingActive} comms={snapshot.CommsSabotageActive} players=[{players}]";
    }

    private string DescribeRegistryState()
    {
        var ids = new HashSet<int>();
        try
        {
            if (AmongUsClient.Instance != null)
            {
                foreach (var client in AmongUsClient.Instance.allClients)
                    ids.Add(client.Id);
            }
        }
        catch { /* diagnostics only */ }

        return ids.Count == 0
            ? "none"
            : string.Join("; ", ids.OrderBy(id => id).Select(id => LogSafe(VoiceClientRegistry.Describe(id))));
    }

    private static string DescribeLiveClients()
    {
        try
        {
            if (AmongUsClient.Instance == null) return "none";
            var ids = new List<int>();
            foreach (var client in AmongUsClient.Instance.allClients)
                ids.Add(client.Id);
            ids.Sort();
            return string.Join(",", ids);
        }
        catch (Exception ex)
        {
            return $"error:{ex.GetType().Name}";
        }
    }

    private void MaybeLogNetworkStats()
    {
        var settings = VoiceSettings.Instance;
        if (settings?.DebugVoiceStats.Value == true)
            MaybeLogDebugState();
    }

    private void LogDetailedGameState(VoiceGameStateSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            VoiceDiagnostics.Log("state.local", "snapshot=none");
            return;
        }

        snapshot.TryGetLocalPlayer(out var local);
        bool localFound = snapshot.LocalPlayerId != byte.MaxValue;
        var localState =
            $"phase={snapshot.Phase} map={snapshot.MapId} localClient={snapshot.LocalClientId} localPlayer={snapshot.LocalPlayerId} " +
            $"localFound={localFound} local={DescribePlayer(localFound ? local : null)} " +
            $"localPos={FormatVector(snapshot.LocalPosition)} localLight={snapshot.LocalLightRadius:0.000} meeting={snapshot.MeetingActive} comms={snapshot.CommsSabotageActive} " +
            $"cameras={snapshot.CameraCount} cameraActive={snapshot.CameraViewActive} cameraIndex={snapshot.ActiveCameraIndex} cameraPos={FormatVector(snapshot.ActiveCameraPosition)} closedDoors={snapshot.ClosedDoorCount} virtualMics={_virtualMics.Count} virtualSpeakers={_virtualSpeakers.Count} " +
            $"micMuted={Mute} speakerMuted={VoiceChatHudState.IsSpeakerMuted} radioHeld={VoiceChatHudState.IsTeamRadio} radioChannel={VoiceChatHudState.ActiveTeamRadioChannel()}";
        if (localState != _lastLoggedLocalState)
        {
            _lastLoggedLocalState = localState;
            VoiceDiagnostics.Log("state.local", localState);
        }

        var options = DescribeGameOptions();
        if (options != _lastLoggedOptions)
        {
            _lastLoggedOptions = options;
            VoiceDiagnostics.Log("state.options", options);
        }
    }

    private static string DescribeGameOptions()
    {
        var o = VoiceChatGameOptions.GetInstance();
        var roomSettings = VoiceRoomSettingsState.Current;
        return
            $"publicLobby={o.PublicVoiceLobby.Value} maxDistance={o.MaxChatDistance.Value:0.000} falloff={(VoiceFalloffMode)o.FalloffMode.Value} occlusion={(VoiceOcclusionMode)o.OcclusionMode.Value} " +
            $"wallsBlock={o.WallsBlockSound.Value} onlySight={o.OnlyHearInSight.Value} cameraCanHear={o.CameraCanHear.Value} " +
            $"hearInVent={o.HearInVent.Value} ventPrivate={o.VentPrivateChat.Value} commsDisable={o.CommsSabDisables.Value} " +
            $"impHearGhosts={o.ImpostorHearGhosts.Value} teamRadio={o.TeamRadio.Value} teamRadioImps={o.TeamRadioImpostors.Value} teamRadioVamps={o.TeamRadioVampires.Value} teamRadioLovers={o.TeamRadioLovers.Value} onlyGhosts={o.OnlyGhostsCanTalk.Value} onlyMeetingLobby={o.OnlyMeetingOrLobby.Value} " +
            $"muteJailed={roomSettings.MuteJailedInMeetings} jailorCanUnmute={roomSettings.JailorCanUnmuteJailed}";
    }

    private static string DescribePlayer(VoicePlayerSnapshot? player)
    {
        if (!player.HasValue) return "none";
        var p = player.Value;
        return
            $"id={p.PlayerId} client={p.ClientId} name=\"{LogSafe(p.PlayerName)}\" pos={FormatVector(p.Position)} local={p.IsLocal} dead={p.IsDead} imp={p.IsImpostor} " +
            $"vent={p.InVent} disconnected={p.Disconnected} dummy={p.IsDummy} visible={p.IsVisible}";
    }

    private static string LogSafe(string value)
        => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");

    private static string FormatVector(Vector2? value)
        => value.HasValue ? FormatVector(value.Value) : "none";

    private static string FormatVector(Vector2 value)
        => $"({value.x:0.000},{value.y:0.000})";

    private static void DiagInc(ref long value)
        => System.Threading.Interlocked.Increment(ref value);

    private static void DiagAdd(ref long value, long amount)
        => System.Threading.Interlocked.Add(ref value, amount);

    private static long DiagTake(ref long value)
        => System.Threading.Interlocked.Exchange(ref value, 0);

    private static string DescribeCurrentRegion()
    {
        try
        {
            var manager = DestroyableSingleton<ServerManager>.Instance;
            var region = manager?.CurrentRegion;
            if (region == null) return "none";
            var servers = region.Servers;
            if (servers == null || servers.Length == 0) return region.Name;
            var server = servers[0];
            return $"{region.Name} {server.Name} {server.Ip}:{server.Port} dtls={server.UseDtls}";
        }
        catch (Exception ex)
        {
            return $"region-error:{ex.GetType().Name}";
        }
    }

    private void MaybeLogDebugState()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDebugStateLogUtc) < VoiceProtocol.StatsLogInterval)
            return;

        _lastDebugStateLogUtc = now;
        var snapshot = CurrentSnapshot;
        if (snapshot == null) return;

        int livePlayers = 0;
        int deadPlayers = 0;
        int ventPlayers = 0;
        foreach (var player in snapshot.Players)
        {
            if (player.Disconnected || player.IsDummy) continue;
            livePlayers++;
            if (player.IsDead) deadPlayers++;
            if (player.InVent) ventPlayers++;
        }

        var routeCounts = new Dictionary<VoiceProximityReason, int>();
        foreach (var remote in RemoteOverlayStates)
        {
            routeCounts.TryGetValue(remote.Reason, out int count);
            routeCounts[remote.Reason] = count + 1;
        }

        string routes = routeCounts.Count == 0
            ? "none"
            : string.Join(", ", routeCounts.Select(kv => $"{kv.Key}:{kv.Value}"));

        VoiceDiagnostics.DebugInfo(
            $"[VC] State: phase={snapshot.Phase} map={snapshot.MapId} " +
            $"players={livePlayers} dead={deadPlayers} vent={ventPlayers} " +
            $"meeting={snapshot.MeetingActive} comms={snapshot.CommsSabotageActive} " +
            $"cameras={snapshot.CameraCount} cameraActive={snapshot.CameraViewActive} cameraIndex={snapshot.ActiveCameraIndex} closedDoors={snapshot.ClosedDoorCount} " +
            $"routes={routes}");
    }

    private static string TrimProfileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return name.Length <= VoiceProtocol.MaxProfileNameChars
            ? name
            : name[..VoiceProtocol.MaxProfileNameChars];
    }

    internal static float GetVolume(float dist, float maxDist)
        => Math.Clamp(1f - dist / maxDist, 0f, 1f);

    internal static float GetPan(float micX, float spkX)
        => Math.Clamp((spkX - micX) / 3f, -1f, 1f);

    internal readonly record struct SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);
}
