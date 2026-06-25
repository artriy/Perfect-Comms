using InnerNet;
using VoiceChatPlugin.Audio;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.Linq;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Manages the in-game voice chat session.
///
/// Audio transport uses the Interstellar voice backend instead of custom Among Us voice RPCs.
///
/// Remote voice playback and routing are owned by the Interstellar backend.
/// </summary>
public class VoiceChatRoom
{
    private const float StateRefreshInterval = 0.05f;
    private const float CommsSabotageRefreshInterval = 0.10f;
    private const float BootstrapWindowSeconds = 6f;
    private const float BootstrapRefreshInterval = 0.50f;
    private const float MissingPeerRecoveryGraceSeconds = 8f;
    private const float InterstellarSwitchPeerRecoveryGraceSeconds = 2f;
    private const float HostSyncWaitTimeoutSeconds = 10f;
    private const float MissingPeerRecoveryIntervalSeconds = 5f;
    private const float PeerEscalationDeferralRecheckSeconds = 3f;
    private const int PeerEscalationDeferralMaxConsecutive = 4;
    private const double RadioStateRpcHeartbeatSeconds = 1.0;
    private const float TransitionTraceSeconds = 45f;
    private const float TransitionTraceStateInterval = 0.25f;
    private const int TransitionTraceAudioFrames = 64;
    private const int TransitionTracePerfEvents = 48;
    private const double StalePlaybackBufferTimeoutSeconds = VoiceProtocol.MaxQueuedFrameAgeSeconds;
    private const double SlowUpdateLogThresholdMs = 20.0;
    private const double SlowOperationLogThresholdMs = 2.0;
    private const double HostSettingsResponseRateLimitSeconds = 2.0;
    private const float HostVoiceRefreshCooldownSeconds = 10f;
    private const float LocalVoiceRefreshCooldownSeconds = 10f;

    // ── Singleton ─────────────────────────────────────────────────────────────
    public static VoiceChatRoom? Current { get; private set; }

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
    internal bool IsBetterCrewLinkBackendActive => _betterCrewLinkVoice != null;
    internal int BetterCrewLinkPublicLobbyJoinEpoch => _betterCrewLinkVoice?.PublicLobbyJoinEpoch ?? 0;
    private IVoiceBackend? _voiceBackend;
    private BetterCrewLinkVoiceBackend? _betterCrewLinkVoice;
    private VoiceRoomSettingsSnapshot? _lastSentHostSettings;
    private int _lastSentModOptionRevision = -1;
    private DateTime _lastSentHostSettingsUtc = DateTime.MinValue;
    private const double MinHostSettingsSnapshotIntervalSeconds = 1.0;
    private DateTime _lastHostSettingsRequestUtc = DateTime.MinValue;
    private int _lastObservedHostClientId = -1;
    private bool _hostSettingsResyncPending;
    private float _hostSyncWaitStartTime = -1f;
    private int _lastAppliedHostVoiceRefreshNonce;
    private float _lastAppliedHostVoiceRefreshTime = -999f;
    private const float HostVoiceRefreshApplyCooldownSeconds = 8f;
    private float _lastHostVoiceRefreshRequestTime = -999f;
    private float _lastLocalVoiceRefreshRequestTime = -999f;
    // Seeded randomly per process so two different hosts don't both start at nonce 1. After host migration,
    // the new host's first refresh would otherwise collide with the old host's nonce 1 and be ignored by
    // every client as a duplicate — exactly when voice is most likely broken.
    private static int _nextHostVoiceRefreshNonce = new System.Random().Next(1, int.MaxValue / 2);
    private byte _lastRadioRpcPlayerId = byte.MaxValue;
    private VoiceTeamRadioChannel _lastRadioRpcChannel = VoiceTeamRadioChannel.None;
    private DateTime _lastRadioRpcSentUtc = DateTime.MinValue;
    private VoiceTransportBackend _activeBackend = VoiceTransportBackend.BetterCrewLink;
    // Set by missing-peer recovery to make EnsureVoiceBackend fully rebuild the active backend, used
    // for Interstellar whose VCRoom.Rejoin (RequestReload) cannot repopulate cleared peers.
    private bool _forceBackendRebuild;
    private string? _activeEndpoint;
    private string? _activeRoomCode;
    private string? _activeRegion;
    internal IEnumerable<VoiceRemoteOverlayState> InterstellarRemoteOverlayStates => _voiceBackend?.RemoteOverlayStates ?? Enumerable.Empty<VoiceRemoteOverlayState>();

    // Allocation-free per-frame path used by VoiceOverlayState.Build.
    internal void AppendRemoteOverlayStates(List<VoiceRemoteOverlayState> buffer)
        => _voiceBackend?.AppendRemoteOverlayStates(buffer);

    // For VoiceFrameProfiler context only.
    internal int BackendPeerCount => _voiceBackend?.PeerCount ?? -1;
    internal bool TrySetRemoteVolume(byte playerId, string playerName, float volume)
        => _voiceBackend?.TrySetRemoteVolume(playerId, playerName, volume) == true;
    internal int ResetRemotePeerMappingsNoMute()
        => _voiceBackend?.ResetPeerMappingsNoMute() ?? 0;
    internal bool TryPublishBetterCrewLinkLobby(VoiceLobbyPublishRequest request)
        => _betterCrewLinkVoice?.TryPublishPublicLobby(request) == true;
    internal bool TryRemoveBetterCrewLinkLobby(string code)
        => _betterCrewLinkVoice?.TryRemovePublicLobby(code) == true;
    public float LocalMicLevel => _voiceBackend?.LocalLevel ?? 0f;
    public bool LocalMicSpeaking => _voiceBackend?.LocalSpeaking == true;
    public bool Mute  { get; private set; }
    public int  SampleRate => AudioHelpers.ClockRate;
    internal VoiceGameStateSnapshot? CurrentSnapshot { get; private set; }

    // ── Speaker ────────────────────────────────────────────────────────────────
    public bool UsingSpeaker => _voiceBackend?.UsingSpeaker == true;

    // ── Misc ───────────────────────────────────────────────────────────────────
    private bool  _commsSabActive;
    private float _commsSabCheckTimer;
    private string _lastCommsSabotageSource = "";
    private byte   _lastId   = byte.MaxValue;
    private string _lastName = null!;
    private float  _lastCompatibilityRefreshTime = -999f;
    private float  _snapshotRefreshTimer;
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
    private DateTime _transitionTraceUntilUtc = DateTime.MinValue;
    private float _transitionTraceStateTimer;
    private int _tracePerfEventsRemaining;
    private string? _lastLoggedLocalState;
    private string? _lastLoggedOptions;
    private DateTime _lastDebugStateLogUtc = DateTime.MinValue;
    private readonly ConcurrentQueue<VoiceBackendCustomMessage> _pendingBackendCustomMessages = new();
    private readonly Dictionary<string, DateTime> _lastHostSettingsResponseBySender = new();

    // ======================================================================
    // Factory
    // ======================================================================

    public static VoiceChatRoom Start()
    {
        Current?.Close();
        Current = new VoiceChatRoom();
        return Current;
    }

    public static void CloseCurrentRoom()
    {
        Current?.Close();
        Current = null;
    }

    internal static void ClearVoiceUiForLifecycleReset(string reason)
    {
        PingTrackerPatch.ClearSpeakingBar();
        MeetingSpeakingIndicatorPatch.ClearAllIndicators();
        VoiceOverlayState.InvalidateCache();
        VoiceVolumeMenu.ForceClose();
        CrewmateAvatarRenderer.ClearCache();
        VoiceCameraState.Clear();
        VoiceProximityCalculator.ResetSightState();
        VoiceDiagnostics.Log("voice.ui.clear", $"reason={LogSafe(reason)}");
    }

    // ======================================================================
    // Constructor
    // ======================================================================

    private VoiceChatRoom()
    {
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

    public void RebuildCaptureSupervisor() => _betterCrewLinkVoice?.RebuildCaptureSupervisor();

    public void SetMute(bool mute)
    {
        bool wasMuted = Mute;
        Mute = mute;
        _voiceBackend?.SetMute(mute);
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
        DrainBackendCustomMessages();
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
            CurrentSnapshot = VoiceSnapshotBuilder.Build(_commsSabActive);
            VoiceFrameProfiler.End("room.snapshot", __snapTicks);
        }

        var snapshot = CurrentSnapshot;
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
            long __backendTicks = VoiceFrameProfiler.Begin();
            _voiceBackend.Update(snapshot, speakerCache, _virtualMics, localInVent, _commsSabActive);
            VoiceFrameProfiler.End("room.backend", __backendTicks);
            if (snapshot != null)
                SendRadioState(snapshot.LocalPlayerId, VoiceChatHudState.ActiveTeamRadioChannel());
            long __recoveryTicks = VoiceFrameProfiler.Begin();
            TryRecoverMissingBackendPeers(snapshot);
            VoiceFrameProfiler.End("room.recovery", __recoveryTicks);
        }

        updateStep = "diagnostics";
        MaybeLogTransitionTraceState(snapshot);

        TraceUpdateCost(updateStartTicks, updateStep, snapshot);
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
            $"backend={_activeBackend} room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")} peers={_voiceBackend?.PeerCount ?? 0}");
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
            $"nonce={nonce} hostClient={localClientId} backend={_activeBackend} room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")}");

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
            $"{sender.ToDiagnosticFields()} nonce={nonce} trigger={trigger} backend={_activeBackend} room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")} peers={_voiceBackend?.PeerCount ?? 0}");

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
            $"trigger={trigger} backend={_activeBackend} room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")} peers={_voiceBackend?.PeerCount ?? 0}");

        VoiceChatHudState.ShowToast("Voice connection refreshed");

        // Rejoin() begins with ClearVoiceUiForLifecycleReset, so the UI teardown runs exactly once.
        StartTransitionTrace($"local voice refresh: {trigger}", snapshot);
        Rejoin("local voice refresh");
    }

    private void SendHostSettingsSnapshot(bool force)
    {
        if (!IsLocalHost() || _voiceBackend == null) return;

        var settings = VoiceRoomSettingsSnapshot.FromGameOptions();
        int modRevision = VoiceModRegistry.OptionRevision;
        if (!force)
        {
            // Re-broadcast when either the core settings OR any third-party mod host-option changed.
            if (_lastSentHostSettings.HasValue && _lastSentHostSettings.Value.Equals(settings)
                && _lastSentModOptionRevision == modRevision)
                return;
            if ((DateTime.UtcNow - _lastSentHostSettingsUtc).TotalSeconds < MinHostSettingsSnapshotIntervalSeconds)
                return;
        }

        // Authority only via authenticated Among Us RPC; the side-channel's self-asserted
        // sender id would let any peer forge host voice settings.
        VoiceRoomSettingsRpc.SendSnapshot(settings);
        _lastSentHostSettings = settings;
        _lastSentModOptionRevision = modRevision;
        _lastSentHostSettingsUtc = DateTime.UtcNow;
        VoiceDiagnostics.Log("settings.sent", $"kind=host-snapshot transport={_activeBackend} rpc=true");
    }

    private void TickVoiceBackend(VoiceGameStateSnapshot? snapshot)
    {
        TrackHostSettingsAuthority(snapshot);
        var settings = VoiceSettings.Instance;

        if (_voiceBackend == null && ShouldWaitForHostSync())
        {
            RequestHostSettingsSnapshotIfNeeded();
            return;
        }

        var roomSettings = VoiceRoomSettingsState.Current;
        var endpoint = VoiceEndpointSettings.ResolveHostSelected(
            roomSettings,
            settings?.BetterCrewLinkServerUrl.Value,
            settings?.InterstellarServerUrl.Value);
        if (!VoiceEndpointSettings.InterstellarEnabled && endpoint.IsInterstellar)
            endpoint = VoiceEndpointSettings.Resolve(VoiceTransportBackend.BetterCrewLink, settings?.BetterCrewLinkServerUrl.Value, null);

        EnsureVoiceBackend(snapshot, settings, endpoint);
        SendHostSettingsSnapshot(force: false);
        RequestHostSettingsSnapshotIfNeeded();
    }

    // Defer the first backend build until the host settings snapshot arrives: a fresh joiner's local default backend may be Interstellar, whose websocket connect freezes Wine clients and desyncs the lobby. Timeout fallback keeps no-PerfectComms-host lobbies working.
    private bool ShouldWaitForHostSync()
    {
        if (IsLocalHost() || VoiceRoomSettingsState.RemoteSnapshot.HasValue)
        {
            _hostSyncWaitStartTime = -1f;
            return false;
        }

        if (_hostSyncWaitStartTime < 0f)
            _hostSyncWaitStartTime = Time.time;

        return Time.time - _hostSyncWaitStartTime < HostSyncWaitTimeoutSeconds;
    }

    private void TrackHostSettingsAuthority(VoiceGameStateSnapshot? snapshot)
    {
        var hostClientId = VoiceHostAuthority.ResolveHostClientId(snapshot);
        if (hostClientId < 0)
            return;

        if (_lastObservedHostClientId < 0)
        {
            _lastObservedHostClientId = hostClientId;
            return;
        }

        if (_lastObservedHostClientId == hostClientId)
            return;

        var oldHostClientId = _lastObservedHostClientId;
        _lastObservedHostClientId = hostClientId;
        _lastSentHostSettings = null;
        _lastHostSettingsRequestUtc = DateTime.MinValue;
        _lastHostSettingsResponseBySender.Clear();
        _hostSettingsResyncPending = true;

        var localClientId = ResolveLocalClientId(snapshot);
        var localIsNewHost = localClientId >= 0 && localClientId == hostClientId;
        VoiceDiagnostics.Log("settings.host.changed", $"oldHost={oldHostClientId} newHost={hostClientId} localClient={localClientId} localIsNewHost={localIsNewHost}");

        if (localIsNewHost)
        {
            if (VoiceRoomSettingsState.RemoteSnapshot.HasValue)
            {
                VoiceRoomSettingsState.ClearRemote();
                VoiceDiagnostics.Log("settings.host.remote_cleared", $"reason=promoted oldHost={oldHostClientId} newHost={hostClientId}");
            }

            _hostSettingsResyncPending = false;
            VoiceDiagnostics.Log("settings.host.promoted", $"oldHost={oldHostClientId} newHost={hostClientId} localClient={localClientId}");
            SendHostSettingsSnapshot(force: true);
            VoiceDiagnostics.Log("settings.host.resync_sent", $"reason=host-transfer newHost={hostClientId}");
            return;
        }

        VoiceDiagnostics.Log("settings.host.resync_requested", $"oldHost={oldHostClientId} newHost={hostClientId} localClient={localClientId} hasRemote={VoiceRoomSettingsState.RemoteSnapshot.HasValue}");
        RequestHostSettingsSnapshot(force: true, reason: "host-transfer");
    }

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

    private void EnsureVoiceBackend(VoiceGameStateSnapshot? snapshot, VoiceChatLocalSettings? settings, VoiceEndpoint endpoint)
    {
        if (snapshot == null || !TryGetVoiceRoomIdentity(snapshot, endpoint.Backend, out var roomCode, out var region))
            return;

        bool forceRebuild = _forceBackendRebuild;
        _forceBackendRebuild = false;

        if (!forceRebuild
            && _voiceBackend != null
            && _activeBackend == endpoint.Backend
            && string.Equals(_activeEndpoint, endpoint.ServerUrl, StringComparison.Ordinal)
            && string.Equals(_activeRoomCode, roomCode, StringComparison.Ordinal)
            && string.Equals(_activeRegion, region, StringComparison.Ordinal))
        {
            return;
        }

        var endpointLabel = endpoint.IsInterstellar ? VoiceEndpointSettings.BuildInterstellarRoomUrl(endpoint.ServerUrl) : endpoint.ServerUrl;
        VoiceDiagnostics.Log("transport.switch", $"backend={endpoint.Backend} room={roomCode} region={region} endpoint={endpointLabel}");
        ClearVoiceUiForLifecycleReset("transport switch");
        DisposeVoiceBackend();
        if (IsLocalHost())
            VoiceRoomSettingsState.ClearRemote();
        _lastSentHostSettings = null;
        _lastHostSettingsRequestUtc = DateTime.MinValue;
        ResetRadioStateSync();
        _voiceBackend = new BetterCrewLinkVoiceBackend(roomCode, region, endpoint.ServerUrl);
        _betterCrewLinkVoice = _voiceBackend as BetterCrewLinkVoiceBackend;
        // P1.2: pre-warm the one-time HUD init (sprite PNG decode + button/tooltip GameObjects) here, off the
        // game-entry frame — the same room-construction lifecycle slot as the backend's WarmOpusCodec. Runs on
        // the Unity main thread (this method already touches VoiceChatHudState below) and is idempotent, so the
        // per-frame EnsureHudButtons path remains the fallback.
        VoiceChatHudState.Prewarm();
        _voiceBackend.CustomMessageReceived += HandleBackendCustomMessage;
        _voiceBackend.SetMute(Mute);
        SetMasterVolume(settings?.MasterVolume.Value ?? 1f);
        _voiceBackend.SetNoiseGate(
            ApplyMicSensitivity(settings?.NoiseGateThreshold.Value ?? 0.003f, settings?.MicSensitivity.Value ?? 1f),
            ApplyMicSensitivity(settings?.VadThreshold.Value ?? 0.004f, settings?.MicSensitivity.Value ?? 1f));
        _voiceBackend.SetCaptureRuntimeOptions(BuildCaptureRuntimeOptions(settings));
#if ANDROID
        SetMicrophone(settings?.MicrophoneDevice ?? string.Empty);
#else
        _voiceBackend.SetMicrophone(settings?.MicrophoneDevice ?? string.Empty, settings?.MicVolume.Value ?? 1f);
#endif
#if WINDOWS
        _voiceBackend.SetSpeaker(settings?.SpeakerDevice ?? string.Empty);
#else
        _voiceBackend.SetSpeaker(string.Empty);
#endif
        if (snapshot.TryGetLocalPlayer(out var localPlayer))
            _voiceBackend.UpdateProfile(snapshot.LocalPlayerId, localPlayer.PlayerName);
        SendRadioState(snapshot.LocalPlayerId, VoiceChatHudState.ActiveTeamRadioChannel());
        _activeBackend = endpoint.Backend;
        _activeEndpoint = endpoint.ServerUrl;
        _activeRoomCode = roomCode;
        _activeRegion = region;
        _missingPeerRecoveryReadyTime = Time.time + (endpoint.IsInterstellar ? InterstellarSwitchPeerRecoveryGraceSeconds : MissingPeerRecoveryGraceSeconds);
        _lastMissingPeerRecoveryTime = -999f;
        ResetMissingPeerRecoveryStormGuard();
        StartBootstrapWindow($"backend switched to {endpoint.Backend}");
        ForceUpdateLocalProfile();
        SendHostSettingsSnapshot(force: true);
        VoiceDiagnostics.Log("transport.selected", $"backend={endpoint.Backend} room={roomCode} region={region} endpoint={endpointLabel} mic={UsingMicrophone} speaker={UsingSpeaker} localLevel={LocalMicLevel:0.000}");
    }

    private void TryRecoverMissingBackendPeers(VoiceGameStateSnapshot? snapshot)
    {
        if (_voiceBackend == null || snapshot == null)
            return;

        // During a round transition (game ends -> EndGame -> lobby reforms) the local player is briefly
        // unassigned (PlayerId 255, not yet in the snapshot). While that holds the backend cannot re-announce
        // its identity to the signaling server (JoinAsync requires a real local playerId), so peers that
        // survived the transition legitimately read as unmapped. Firing recovery in this window misreads the
        // settling state as a mesh collapse and forces a destructive global rebuild (new socket) right as the
        // lobby reforms -- the visible "everyone reconnects a few seconds after returning to the lobby" glitch.
        // Defer recovery (keep the grace fresh) until the local player resolves; the per-frame re-announce in
        // the backend Update then remaps the surviving peers in place, with no socket churn.
        if (!snapshot.TryGetLocalPlayer(out _))
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

        bool deferRequested = _betterCrewLinkVoice?.ShouldDeferPeerEscalation == true;
        if (deferRequested && _consecutivePeerEscalationDeferrals < PeerEscalationDeferralMaxConsecutive)
        {
            _consecutivePeerEscalationDeferrals++;
            _missingPeerRecoveryReadyTime = Time.time + PeerEscalationDeferralRecheckSeconds;
            VoiceDiagnostics.Log("transport.peer-recovery.deferred",
                $"backend={_activeBackend} reason=backend-recovery-in-flight remotePlayers={remotePlayers} peers={mappedPeers} open={openPeers} " +
                $"collapsed={collapsed} recheckSec={PeerEscalationDeferralRecheckSeconds:0.0} deferrals={_consecutivePeerEscalationDeferrals}/{PeerEscalationDeferralMaxConsecutive}");
            return;
        }
        if (deferRequested)
        {
            VoiceDiagnostics.Log("transport.peer-recovery.deferred",
                $"backend={_activeBackend} reason=backend-recovery-in-flight remotePlayers={remotePlayers} peers={mappedPeers} open={openPeers} " +
                $"collapsed={collapsed} deferred=overridden deferrals={_consecutivePeerEscalationDeferrals}/{PeerEscalationDeferralMaxConsecutive}");
        }
        _consecutivePeerEscalationDeferrals = 0;

        bool finalCollapseAttempt = collapsed && (openChannelsRaw == 0 || backoffAttempts + 1 >= MissingPeerRecoveryMaxAttempts);

        _lastMissingPeerRecoveryTime = Time.time;
        string remoteSignatureText = DescribeExpectedRemotePlayers(snapshot);
        VoiceDiagnostics.Log("transport.peer-recovery",
            $"backend={_activeBackend} reason=missing-peer remotePlayers={remotePlayers} peers={mappedPeers} open={openPeers} rawPeers={_voiceBackend.PeerCount} " +
            $"mode={(finalCollapseAttempt ? "global" : collapsed ? "collapse-targeted" : "targeted")} attempt={(collapsed ? _globalRebuildAttempts + 1 : _missingPeerRecoveryAttempts + 1)}/{MissingPeerRecoveryMaxAttempts} healthyPeak={_lastHealthyMappedPeers} backoffSec={backoff:0.0} " +
            $"room={_activeRoomCode ?? "unknown"} region={_activeRegion ?? "unknown"} " +
            $"liveClients=[{remoteSignatureText}]");

        bool didGlobal = false;
        if (finalCollapseAttempt)
        {
            // Automated relay escalation: if we've never mapped a single peer despite repeated global rebuilds
            // (remotePlayers exist but mappedPeers==0), direct/STUN ICE is clearly not working for this client
            // (strict/symmetric NAT, or a Wine box where host-candidate gathering fails). Latch the BCL backend
            // to relay-only ICE before this rebuild so the fresh peer connections route through TURN. Reuses the
            // same forceRelay path the Wine fix validated; only fires after total failure, so a client whose
            // voice already works never reaches here.
            if (mappedPeers == 0 && remotePlayers > 0 && _globalRebuildAttempts + 1 >= 2)
                _betterCrewLinkVoice?.EscalateToRelayOnly($"global-attempt-{_globalRebuildAttempts + 1}");

            ClearVoiceUiForLifecycleReset("missing peer recovery");
            // Force a full backend rebuild (dispose + reconnect => NEW socket id) rather than the backend's
            // light Rejoin(), which reuses the socket. Both backends need this on a collapse:
            //  - Interstellar's VCRoom.Rejoin only sends RequestReload and never clears the library's
            //    audioInstances, so onConnectClient never re-fires and peers stay silent.
            //  - BetterCrewLink's Rejoin() keeps the SAME socket id, so a split-brained peer (its channel reads
            //    'open' while ours never opened) never sees us reconnect, never supersedes its stale connection,
            //    and the collapse persists (the "two clients can't hear each other until one presses refresh"
            //    bug). A new socket id forces the remote to supersede and renegotiate clean — exactly what the
            //    manual voice-refresh keybind does.
            _forceBackendRebuild = true;
            ResetSettingsSyncState();
            StartBootstrapWindow("missing voice backend peer");
            ForceUpdateLocalProfile();
            didGlobal = true;
        }
        else
        {
            // Targeted, non-destructive recovery of only the unmapped/wedged client(s). -1 means the backend
            // has no targeted path (e.g. Interstellar) — fall back to its global rebuild this attempt.
            int recovered = _voiceBackend.TryRecoverMissingClients(snapshot);
            if (recovered < 0)
            {
                ClearVoiceUiForLifecycleReset("missing peer recovery");
                // Same rationale as the collapse path: a full backend rebuild (new socket id) is what clears a
                // split-brain; the backend's light Rejoin() reuses the socket and cannot.
                _forceBackendRebuild = true;
                ResetSettingsSyncState();
                StartBootstrapWindow("missing voice backend peer");
                ForceUpdateLocalProfile();
                didGlobal = true;
            }
            else
            {
                VoiceDiagnostics.Log("transport.peer-recovery-targeted",
                    $"backend={_activeBackend} recovered={recovered} peers={mappedPeers} remotePlayers={remotePlayers}");
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
                    $"backend={_activeBackend} attempts={_missingPeerRecoveryAttempts} peers={mappedPeers} remotePlayers={remotePlayers} " +
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

    // Exponential backoff (seconds) for a recovery attempt counter: the base interval doubled per prior
    // non-improving attempt, clamped so a stubborn shortfall slows down instead of re-firing every interval.
    // Pure + unit-tested. Shared by the targeted and global/collapse paths (each with its own counter).
    internal static float RecoveryBackoffSeconds(int attempts)
        => MissingPeerRecoveryIntervalSeconds * (1 << Math.Min(Math.Max(attempts, 0), MissingPeerRecoveryBackoffShiftCap));

    private static int CountExpectedRemotePlayers(VoiceGameStateSnapshot snapshot)
    {
        // Indexed loop over IReadOnlyList instead of LINQ .Count(predicate): the latter boxes a heap
        // enumerator on every call, and this runs per-frame via TryRecoverMissingBackendPeers (before its
        // time gates). The predicate is unchanged.
        var players = snapshot.Players;
        int count = 0;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (!player.IsLocal && !player.Disconnected && !player.IsDummy && player.ClientId >= 0)
                count++;
        }
        return count;
    }

    // Allocation-free order-independent fold over the expected-remote clientIds (an FNV-1a fold mixed with an
    // order-independent XOR accumulate). Used per-frame for the recovery latch's set-change detection so we
    // don't build/compare the human-readable LINQ signature on every shortfall frame. Matches the de-LINQ'd
    // indexed-loop style of CountExpectedRemotePlayers. The human-readable signature (DescribeExpected...) is
    // only built once recovery actually fires.
    private static int HashExpectedRemotePlayers(VoiceGameStateSnapshot snapshot)
    {
        var players = snapshot.Players;
        int acc = 0;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player.IsLocal || player.Disconnected || player.IsDummy || player.ClientId < 0)
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

    private static string DescribeExpectedRemotePlayers(VoiceGameStateSnapshot snapshot)
        => string.Join(",", snapshot.Players
            .Where(player => !player.IsLocal && !player.Disconnected && !player.IsDummy && player.ClientId >= 0)
            .Select(player => $"{player.ClientId}:{LogSafe(player.PlayerName)}"));

    private static bool TryGetVoiceRoomIdentity(VoiceGameStateSnapshot? snapshot, VoiceTransportBackend backend, out string roomCode, out string region)
        => backend == VoiceTransportBackend.Interstellar
            ? TryGetFangkuaiInterstellarRoomIdentity(snapshot, out roomCode, out region)
            : TryGetBetterCrewLinkRoomIdentity(snapshot, out roomCode, out region);

    private static bool TryGetFangkuaiInterstellarRoomIdentity(VoiceGameStateSnapshot? snapshot, out string roomCode, out string region)
    {
        roomCode = string.Empty;
        region = "default";
        var client = AmongUsClient.Instance;
        if (client == null || snapshot == null)
            return false;

        try
        {
            if (client.GameId == 0)
                return false;

            roomCode = client.GameId.ToString();
        }
        catch
        {
            return false;
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

        return !string.IsNullOrWhiteSpace(roomCode);
    }

    private static bool TryGetBetterCrewLinkRoomIdentity(VoiceGameStateSnapshot? snapshot, out string roomCode, out string region)
    {
        roomCode = string.Empty;
        region = "default";
        var client = AmongUsClient.Instance;
        if (client == null || snapshot == null)
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
        RequestHostSettingsSnapshot(force: _hostSettingsResyncPending, reason: _hostSettingsResyncPending ? "host-transfer-pending" : "missing-host-snapshot");
    }

    private void RequestHostSettingsSnapshot(bool force, string reason)
    {
        if (IsLocalHost()) return;
        if (!force && VoiceRoomSettingsState.RemoteSnapshot.HasValue) return;

        var now = DateTime.UtcNow;
        if ((now - _lastHostSettingsRequestUtc).TotalSeconds < 5)
            return;

        // Request the host snapshot over the authenticated RPC only (see SendHostSettingsSnapshot).
        VoiceRoomSettingsRpc.SendRequest();
        _lastHostSettingsRequestUtc = now;
        VoiceDiagnostics.Log("settings.requested", $"kind=host-snapshot transport={_activeBackend} rpc=true reason={reason} force={force}");
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
        current._lastHostSettingsRequestUtc = DateTime.MinValue;
        VoiceDiagnostics.Log("settings.host.resync_applied", $"transport={transport} hostClient={hostClientId} hostPlayer={hostPlayerId}");
    }

    internal static void NoteHostSettingsSnapshotRejected()
    {
        // Snapshot rejected (e.g. stale host id during migration): flag a re-request so settings
        // converge once the host id resolves. The 5s throttle in RequestHostSettingsSnapshot bounds it.
        var current = Current;
        if (current == null || current.IsLocalHost())
            return;

        current._hostSettingsResyncPending = true;
    }

    private void RespondToHostSettingsRequestFromSender(VoiceHostSenderIdentity sender)
    {
        VoiceDiagnostics.Log("settings.request.received", sender.ToDiagnosticFields());
        if (!IsLocalHost() || _voiceBackend == null) return;
        if (IsHostSettingsRequestRateLimited(sender))
        {
            VoiceDiagnostics.Log("settings.request.rate_limited", sender.ToDiagnosticFields());
            return;
        }

        SendHostSettingsSnapshot(force: true);
    }

    private bool IsHostSettingsRequestRateLimited(VoiceHostSenderIdentity sender)
    {
        var now = DateTime.UtcNow;
        var key = sender.StableKey;
        if (_lastHostSettingsResponseBySender.TryGetValue(key, out var last)
            && (now - last).TotalSeconds < HostSettingsResponseRateLimitSeconds)
            return true;

        _lastHostSettingsResponseBySender[key] = now;
        return false;
    }

    internal static void ApplyRemoteRadioState(byte playerId, VoiceTeamRadioChannel channel)
    {
        Current?._voiceBackend?.ApplyRemoteRadioState(playerId, channel);
    }

    private void SendRadioState(byte playerId, VoiceTeamRadioChannel channel)
    {
        channel = VoiceTeamRadioChannels.Normalize(channel);
        _voiceBackend?.SendRadioState(playerId, channel);
        SyncRadioStateRpc(playerId, channel);
    }

    private void SyncRadioStateRpc(byte playerId, VoiceTeamRadioChannel channel)
    {
        if (playerId == byte.MaxValue) return;

        var now = DateTime.UtcNow;
        bool active = VoiceTeamRadioChannels.IsActive(channel);
        bool changed = playerId != _lastRadioRpcPlayerId || channel != _lastRadioRpcChannel;
        bool heartbeat = active && (now - _lastRadioRpcSentUtc).TotalSeconds >= RadioStateRpcHeartbeatSeconds;
        if (!changed && !heartbeat) return;

        VoiceRadioStateRpc.Send(playerId, channel);
        _lastRadioRpcPlayerId = playerId;
        _lastRadioRpcChannel = channel;
        _lastRadioRpcSentUtc = now;
    }

    private void HandleBackendCustomMessage(VoiceBackendCustomMessage message)
    {
        _pendingBackendCustomMessages.Enqueue(message.CopyPayload());
    }

    private void DisposeVoiceBackend()
    {
        if (_voiceBackend != null)
            _voiceBackend.CustomMessageReceived -= HandleBackendCustomMessage;
        _voiceBackend?.Dispose();
        _voiceBackend = null;
        _betterCrewLinkVoice = null;
    }

    private void DrainBackendCustomMessages()
    {
        while (_pendingBackendCustomMessages.TryDequeue(out var message))
        {
            try
            {
                ProcessBackendCustomMessage(message);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("settings.message.error", $"error=\"{LogSafe(ex.Message)}\"");
            }
        }
    }

    private void ProcessBackendCustomMessage(VoiceBackendCustomMessage backendMessage)
    {
        // SECURITY: side-channel sender id is self-asserted, so it is NOT trusted for authority;
        // host settings and jail-voice flow only over authenticated RPC. Legacy payloads are ignored.
        _ = backendMessage;
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

    // Forward a Nat Fix / TURN setting change to the active backend so it can rebuild its warm peer-connection
    // pool off the main thread (no rejoin needed; existing peers keep their connections).
    public void RebuildIceConnectionPool()
        => _voiceBackend?.RebuildIceConnectionPool();

    private void Rejoin(string reason)
    {
        ClearVoiceUiForLifecycleReset(reason);
        DisposeVoiceBackend();
        CurrentSnapshot = null;
        _snapshotRefreshTimer = 0f;
        _missingPeerRecoveryReadyTime = -999f;
        _lastMissingPeerRecoveryTime = -999f;
        ResetMissingPeerRecoveryStormGuard();
        ResetSettingsSyncState();
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

    private void ResetSettingsSyncState()
    {
        VoiceRoomSettingsState.ClearRemote();
        _lastSentHostSettings = null;
        _lastHostSettingsRequestUtc = DateTime.MinValue;
        _lastObservedHostClientId = -1;
        _hostSettingsResyncPending = false;
        _lastHostSettingsResponseBySender.Clear();
        _hostSyncWaitStartTime = -1f;
        ResetRadioStateSync();
    }

    private void ResetRadioStateSync()
    {
        _lastRadioRpcPlayerId = byte.MaxValue;
        _lastRadioRpcChannel = VoiceTeamRadioChannel.None;
        _lastRadioRpcSentUtc = DateTime.MinValue;
    }

    public void Close()
    {
        ClearVoiceUiForLifecycleReset("room close");
        DisposeVoiceBackend();
        _lastCompatibilityRefreshTime = -999f;
        CurrentSnapshot = null;
        _snapshotRefreshTimer = 0f;
        _bootstrapUntilTime = -999f;
        _bootstrapRefreshTimer = 0f;
        ResetSettingsSyncState();
        ResetTransitionTraceState();
        VoiceDiagnostics.Log("room.close", "state cleared");
        VoiceClientRegistry.Reset();
        VoiceRoleMuteState.Reset();
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
        _lastLoggedLocalState = null;
        _lastLoggedOptions = null;
    }

    private void TrackTransitionPhase(VoiceGameStateSnapshot? snapshot)
    {
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

    private void MaybeLogTransitionTraceState(VoiceGameStateSnapshot? snapshot)
    {
        if (!IsTransitionTraceActive) return;

        _transitionTraceStateTimer -= Time.deltaTime;
        if (_transitionTraceStateTimer > 0f) return;

        _transitionTraceStateTimer = TransitionTraceStateInterval;
        VoiceDiagnostics.Log("transition.state",
            $"remaining={(_transitionTraceUntilUtc - DateTime.UtcNow).TotalSeconds:0.000}s " +
            $"liveClients=[{DescribeLiveClients()}] registry=[{DescribeRegistryState()}] " +            $"micLevel={LocalMicLevel:0.000} micSpeaking={LocalMicSpeaking} mute={Mute} speakerMuted={VoiceChatHudState.IsSpeakerMuted} " +
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

        return $"phase={snapshot.Phase} map={snapshot.MapId} localClient={snapshot.LocalClientId} localPlayer={snapshot.LocalPlayerId} localPos={FormatVector(snapshot.LocalPosition)} meeting={snapshot.MeetingActive} comms={snapshot.CommsSabotageActive} players=[{players}]";
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
        foreach (var remote in InterstellarRemoteOverlayStates)
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
