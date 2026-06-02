using InnerNet;
using VoiceChatPlugin.Audio;
using VoiceChatPlugin.VoiceChat;
using MiraAPI.LocalSettings;
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
    private const float MissingPeerRecoveryIntervalSeconds = 5f;
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
    public void AddVirtualMicrophone(IVoiceComponent c)    => _virtualMics.Add(c);
    public void AddVirtualSpeaker(IVoiceComponent c)       => _virtualSpeakers.Add(c);
    public void RemoveVirtualMicrophone(IVoiceComponent c) => _virtualMics.Remove(c);
    public void RemoveVirtualSpeaker(IVoiceComponent c)    => _virtualSpeakers.Remove(c);

    // ── Microphone ─────────────────────────────────────────────────────────────
    public bool UsingMicrophone => _voiceBackend?.UsingMicrophone == true;
    internal bool IsBetterCrewLinkBackendActive => _betterCrewLinkVoice != null;
    internal int BetterCrewLinkPublicLobbyJoinEpoch => _betterCrewLinkVoice?.PublicLobbyJoinEpoch ?? 0;
    private IVoiceBackend? _voiceBackend;
    private InterstellarVoiceBackend? _interstellarVoice;
    private BetterCrewLinkVoiceBackend? _betterCrewLinkVoice;
    private VoiceRoomSettingsSnapshot? _lastSentHostSettings;
    private DateTime _lastHostSettingsRequestUtc = DateTime.MinValue;
    private int _lastObservedHostClientId = -1;
    private bool _hostSettingsResyncPending;
    private int _lastAppliedHostVoiceRefreshNonce;
    private float _lastHostVoiceRefreshRequestTime = -999f;
    private float _lastLocalVoiceRefreshRequestTime = -999f;
    private static int _nextHostVoiceRefreshNonce;
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
    private bool _haveTracePhase;
    private VoiceGamePhase _lastTracePhase = VoiceGamePhase.Unknown;
    private DateTime _transitionTraceUntilUtc = DateTime.MinValue;
    private float _transitionTraceStateTimer;
    private int _tracePerfEventsRemaining;
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
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
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
            settings?.MicSensitivity.Value ?? 1f);

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
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
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

        IReadOnlyList<SpeakerCache> speakerCache = Array.Empty<SpeakerCache>();
        updateStep = "speaker-cache";
        if (listenerPos.HasValue && _virtualSpeakers.Count > 0)
        {
            var settings = VoiceRoomSettingsState.Current;
            float maxRange = settings.MaxChatDistance;
            var list = new List<SpeakerCache>(_virtualSpeakers.Count);
            foreach (var speaker in _virtualSpeakers)
            {
                float d = Vector2.Distance(speaker.Position, listenerPos.Value);
                float volume = VoiceAudioOcclusion.ApplyFalloff(d, maxRange, (VoiceFalloffMode)settings.FalloffMode);
                if (volume > 0f)
                    list.Add(new(speaker, volume, GetPan(listenerPos.Value.x, speaker.Position.x)));
            }
            speakerCache = list;
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

        _lastAppliedHostVoiceRefreshNonce = nonce;
        var snapshot = CurrentSnapshot;
        VoiceDiagnostics.Log("voice.refresh.applied",
            $"{sender.ToDiagnosticFields()} nonce={nonce} trigger={trigger} backend={_activeBackend} room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")} peers={_voiceBackend?.PeerCount ?? 0}");

        // Rejoin() begins with ClearVoiceUiForLifecycleReset, so the UI teardown runs exactly once.
        StartTransitionTrace($"host voice refresh: {trigger}", snapshot);
        Rejoin("host voice refresh");
    }

    private void ApplyLocalVoiceRefresh(string trigger)
    {
        var snapshot = CurrentSnapshot;
        VoiceDiagnostics.Log("voice.refresh.local.applied",
            $"trigger={trigger} backend={_activeBackend} room={LogSafe(_activeRoomCode ?? "unknown")} region={LogSafe(_activeRegion ?? "unknown")} peers={_voiceBackend?.PeerCount ?? 0}");

        // Rejoin() begins with ClearVoiceUiForLifecycleReset, so the UI teardown runs exactly once.
        StartTransitionTrace($"local voice refresh: {trigger}", snapshot);
        Rejoin("local voice refresh");
    }

    private void SendHostSettingsSnapshot(bool force)
    {
        if (!IsLocalHost() || _voiceBackend == null) return;

        var settings = VoiceRoomSettingsSnapshot.FromGameOptions();
        if (!force && _lastSentHostSettings.HasValue && _lastSentHostSettings.Value.Equals(settings))
            return;

        // Authority only via authenticated Among Us RPC; the side-channel's self-asserted
        // sender id would let any peer forge host voice settings.
        VoiceRoomSettingsRpc.SendSnapshot(settings);
        _lastSentHostSettings = settings;
        VoiceDiagnostics.Log("settings.sent", $"kind=host-snapshot transport={_activeBackend} rpc=true");
    }

    private void TickVoiceBackend(VoiceGameStateSnapshot? snapshot)
    {
        TrackHostSettingsAuthority(snapshot);
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        var roomSettings = VoiceRoomSettingsState.Current;
        var endpoint = VoiceEndpointSettings.ResolveHostSelected(
            roomSettings,
            settings?.BetterCrewLinkServerUrl.Value,
            settings?.InterstellarServerUrl.Value);

        EnsureVoiceBackend(snapshot, settings, endpoint);
        SendHostSettingsSnapshot(force: false);
        RequestHostSettingsSnapshotIfNeeded();
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
        _voiceBackend?.Dispose();
        _voiceBackend = null;
        _interstellarVoice = null;
        _betterCrewLinkVoice = null;
        if (IsLocalHost())
            VoiceRoomSettingsState.ClearRemote();
        _lastSentHostSettings = null;
        _lastHostSettingsRequestUtc = DateTime.MinValue;
        ResetRadioStateSync();
        _voiceBackend = endpoint.IsInterstellar
            ? new InterstellarVoiceBackend(roomCode, region, endpoint.ServerUrl)
            : new BetterCrewLinkVoiceBackend(roomCode, region, endpoint.ServerUrl);
        _interstellarVoice = _voiceBackend as InterstellarVoiceBackend;
        _betterCrewLinkVoice = _voiceBackend as BetterCrewLinkVoiceBackend;
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
        StartBootstrapWindow($"backend switched to {endpoint.Backend}");
        ForceUpdateLocalProfile();
        SendHostSettingsSnapshot(force: true);
        VoiceDiagnostics.Log("transport.selected", $"backend={endpoint.Backend} room={roomCode} region={region} endpoint={endpointLabel} mic={UsingMicrophone} speaker={UsingSpeaker} localLevel={LocalMicLevel:0.000}");
    }

    private void TryRecoverMissingBackendPeers(VoiceGameStateSnapshot? snapshot)
    {
        if (_voiceBackend == null || snapshot == null)
            return;

        int remotePlayers = CountExpectedRemotePlayers(snapshot);
        int mappedPeers = _voiceBackend.CountMappedRemotePeers(snapshot);
        if (remotePlayers == 0 || mappedPeers >= remotePlayers)
        {
            _missingPeerRecoveryReadyTime = Time.time + MissingPeerRecoveryGraceSeconds;
            return;
        }

        if (Time.time < _missingPeerRecoveryReadyTime)
            return;

        if (Time.time - _lastMissingPeerRecoveryTime < MissingPeerRecoveryIntervalSeconds)
            return;

        _lastMissingPeerRecoveryTime = Time.time;
        VoiceDiagnostics.Log("transport.peer-recovery",
            $"backend={_activeBackend} reason=missing-peer remotePlayers={remotePlayers} peers={mappedPeers} rawPeers={_voiceBackend.PeerCount} " +
            $"room={_activeRoomCode ?? "unknown"} region={_activeRegion ?? "unknown"} " +
            $"liveClients=[{DescribeExpectedRemotePlayers(snapshot)}]");
        ClearVoiceUiForLifecycleReset("missing peer recovery");
        if (_activeBackend == VoiceTransportBackend.Interstellar)
        {
            // Interstellar's VCRoom.Rejoin only sends RequestReload and never clears the library's
            // audioInstances, so onConnectClient never re-fires and the cleared peers are never
            // repopulated (every remote peer stays silent permanently). Force a full backend rebuild
            // instead, which re-establishes the connection and recreates each peer via onConnectClient.
            _forceBackendRebuild = true;
        }
        else
        {
            _voiceBackend.Rejoin();
        }
        ResetSettingsSyncState();
        StartBootstrapWindow("missing voice backend peer");
        ForceUpdateLocalProfile();
    }

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
        if (IsLocalHost() || _voiceBackend == null) return;
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
        _voiceBackend?.Dispose();
        _voiceBackend = null;
        _interstellarVoice = null;
        _betterCrewLinkVoice = null;
        CurrentSnapshot = null;
        _snapshotRefreshTimer = 0f;
        _missingPeerRecoveryReadyTime = -999f;
        _lastMissingPeerRecoveryTime = -999f;
        ResetSettingsSyncState();
        StartBootstrapWindow(reason);
    }

    private void ResetSettingsSyncState()
    {
        VoiceRoomSettingsState.ClearRemote();
        _lastSentHostSettings = null;
        _lastHostSettingsRequestUtc = DateTime.MinValue;
        _lastObservedHostClientId = -1;
        _hostSettingsResyncPending = false;
        _lastHostSettingsResponseBySender.Clear();
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
        _voiceBackend?.Dispose();
        _voiceBackend = null;
        _interstellarVoice = null;
        _betterCrewLinkVoice = null;
        _lastCompatibilityRefreshTime = -999f;
        CurrentSnapshot = null;
        _snapshotRefreshTimer = 0f;
        _bootstrapUntilTime = -999f;
        _bootstrapRefreshTimer = 0f;
        ResetSettingsSyncState();
        ResetTransitionTraceState();
        VoiceDiagnostics.Log("room.close", "state cleared");
        VoiceClientRegistry.Reset();
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
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
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
        VoiceDiagnostics.Log("state.local",
            $"phase={snapshot.Phase} map={snapshot.MapId} localClient={snapshot.LocalClientId} localPlayer={snapshot.LocalPlayerId} " +
            $"localFound={localFound} local={DescribePlayer(localFound ? local : null)} " +
            $"localPos={FormatVector(snapshot.LocalPosition)} localLight={snapshot.LocalLightRadius:0.000} meeting={snapshot.MeetingActive} comms={snapshot.CommsSabotageActive} " +
            $"cameras={snapshot.CameraCount} cameraActive={snapshot.CameraViewActive} cameraIndex={snapshot.ActiveCameraIndex} cameraPos={FormatVector(snapshot.ActiveCameraPosition)} closedDoors={snapshot.ClosedDoorCount} virtualMics={_virtualMics.Count} virtualSpeakers={_virtualSpeakers.Count} " +
            $"micMuted={Mute} speakerMuted={VoiceChatHudState.IsSpeakerMuted} radioHeld={VoiceChatHudState.IsTeamRadio} radioChannel={VoiceChatHudState.ActiveTeamRadioChannel()}");

        VoiceDiagnostics.Log("state.options", DescribeGameOptions());
    }

    private static string DescribeGameOptions()
    {
        var o = VoiceChatGameOptions.GetInstance();
        return
            $"publicLobby={o.PublicVoiceLobby.Value} maxDistance={o.MaxChatDistance.Value:0.000} falloff={(VoiceFalloffMode)o.FalloffMode.Value} occlusion={(VoiceOcclusionMode)o.OcclusionMode.Value} " +
            $"wallsBlock={o.WallsBlockSound.Value} onlySight={o.OnlyHearInSight.Value} cameraCanHear={o.CameraCanHear.Value} " +
            $"hearInVent={o.HearInVent.Value} ventPrivate={o.VentPrivateChat.Value} commsDisable={o.CommsSabDisables.Value} " +
            $"impHearGhosts={o.ImpostorHearGhosts.Value} teamRadio={o.TeamRadio.Value} teamRadioImps={o.TeamRadioImpostors.Value} teamRadioVamps={o.TeamRadioVampires.Value} teamRadioLovers={o.TeamRadioLovers.Value} onlyGhosts={o.OnlyGhostsCanTalk.Value} onlyMeetingLobby={o.OnlyMeetingOrLobby.Value}";
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

    internal record SpeakerCache(IVoiceComponent Speaker, float Volume, float Pan);
}
