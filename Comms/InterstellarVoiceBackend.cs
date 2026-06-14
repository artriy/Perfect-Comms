using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
#if WINDOWS
using NAudio.Wave;
#endif
using Interstellar.Routing;
using Interstellar.Routing.Router;
using Interstellar.VoiceChat;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class InterstellarVoiceBackend : IVoiceBackend
{
    private readonly VCRoom _room;
    // Concurrent: VCRoom callbacks fire on relay/socket threads while the Unity thread iterates;
    // ConcurrentDictionary enumeration never throws on concurrent mutation (Dictionary would).
    private readonly ConcurrentDictionary<int, Peer> _peers = new();
    private readonly StereoRouter _imager;
    private readonly VolumeRouter _normalVolume;
    private readonly VolumeRouter _ghostVolume;
    private readonly VolumeRouter _radioVolume;
    private readonly VolumeRouter _listenerMuffleVolume;
    private readonly VolumeRouter _clientVolume;
    private static readonly byte[] RadioStateMagic = [(byte)'P', (byte)'C', (byte)'R', (byte)'D'];
    // The vendored Interstellar client reserves Custom messages for its audio relay fallback.
    // Keep app-level controls local until they are explicitly multiplexed around relay traffic.
    private static readonly bool InterstellarCustomControlEnabled = false;
    private const float RadioHighPassFrequency = 650f;
    private const float RadioDistortionThreshold = 0.85f;
    private const double SyntheticToneFrequency = 220.0;
    private const float SyntheticToneAmplitude = 0.012f;
    private const int InterstellarReceiveBufferSamples = 4800;
    private const int InterstellarReceiveBufferAdditionalSamples = 9600;
    private const string DiagnosticsVersion = "interstellar-fang-switch-full-reconnect-20260522";

    private readonly LevelMeterRouter _levelMeter;
    private readonly VolumeRouter.Property _masterVolume;
    private LevelMeterRouter.Property? _localMicMeter;
    private VoiceTeamRadioChannel _lastLocalRadioChannel = VoiceTeamRadioChannel.None;
    private string _lastMicDeviceName = string.Empty;
    private float _lastMicVolume = 1f;
    private bool _microphoneReady;
    private bool _speakerReady;
    private int _customTx;
    private int _customRx;
    private int _customSkipped;
#if ANDROID
    private AndroidMicrophone? _androidMicrophone;
    private AndroidSpeaker? _androidSpeaker;
#endif
#if WINDOWS
    private static readonly TimeSpan SpeakerTopologyPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SpeakerTopologyFastPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpeakerTopologyFastWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SpeakerRetryInterval = TimeSpan.FromSeconds(5);
    private string _lastSpeakerDeviceName = string.Empty;
    private bool _speakerRequested;
    private string _speakerTopologySignature = string.Empty;
    private DateTime _speakerTopologyLastPollUtc = DateTime.MinValue;
    private DateTime _lastSpeakerRetryUtc = DateTime.MinValue;
    private long _speakerTopologyFastUntilTicks;
    private int _speakerReopenRequestedFlag;
    private DateTime _lastFlagReopenUtc = DateTime.MinValue;
    private readonly Audio.MicPreprocessor _micPreprocessor = new();
    private readonly object _micProcessSync = new();
    private int _openedSpeakerDeviceNumber = int.MinValue;
    private string _openedSpeakerProductName = string.Empty;
    private bool _autoMicGain = true;
    private float _transmitLimiterGain = 1f;
    private int _latchedMicChannel;
    private int _micChannelSwitchStreak;
    private double[] _micChannelEnergyScratch = Array.Empty<double>();
    private IWaveIn? _windowsMicrophoneCapture;
    private ManualMicrophone? _windowsMicrophone;
    private IWavePlayer? _windowsSpeakerOutput;
    private ManualSpeaker? _windowsSpeaker;
    private WindowsManualSpeakerSampleProvider? _windowsSpeakerProvider;
    private long _windowsMicCallbacks;
    private long _windowsMicBytes;
    private long _windowsMicSamples;
    private long _windowsMicPushedFrames;
    private long _windowsMicSilentCallbacks;
    private long _windowsMicTinyCallbacks;
    private long _windowsMicNoMicrophone;
    private long _windowsMicNoBuffer;
    private long _windowsMicNoFormat;
    private long _windowsMicUnsupportedFormats;
    private int _windowsMicLastBytes;
    private int _windowsMicLastSamples;
    private int _windowsMicLastPeakMilli;
    private int _windowsMicPeakMilli;
    private int _windowsMicLastGainMilli;
#endif
    private DateTime _lastStatsLogUtc = DateTime.MinValue;
    private byte _lastPlayerId = byte.MaxValue;
    private string _lastPlayerName = string.Empty;
    private VoiceCaptureRuntimeOptions _captureOptions;
    private Timer? _syntheticMicTimer;
    private ManualMicrophone? _syntheticMicrophone;
    private double _syntheticTonePhase;
    private int _syntheticFrames;

    public event Action<VoiceBackendCustomMessage>? CustomMessageReceived;

    public string ServerUrl { get; }
    public string RoomCode { get; }
    public string Region { get; }
    public bool Mute => _room.Mute;
    public float LocalLevel => Math.Max(_room.Microphone?.Level ?? 0f, _localMicMeter?.Level ?? 0f);
    public bool LocalSpeaking => LocalLevel >= ResolveSpeakingThreshold();
    public bool UsingMicrophone => _microphoneReady;
    public bool UsingSpeaker => _speakerReady;

    public IEnumerable<VoiceRemoteOverlayState> RemoteOverlayStates
    {
        get
        {
            var states = new List<VoiceRemoteOverlayState>(_peers.Count);
            AppendRemoteOverlayStates(states);
            return states;
        }
    }

    // Per-frame overlay path: fill the caller's buffer instead of allocating a fresh List on every
    // access. ConcurrentDictionary enumeration is safe against concurrent connect/disconnect.
    public void AppendRemoteOverlayStates(List<VoiceRemoteOverlayState> buffer)
    {
        foreach (var kv in _peers)
        {
            var peer = kv.Value;
            if (peer.PlayerId == byte.MaxValue) continue;
            buffer.Add(peer.ToOverlayState());
        }
    }

    public int PeerCount => _peers.Count;

    // Per-frame on the recovery path: manual nested loops instead of Count(...Any(...)) to avoid the
    // per-call enumerator + closure allocations (mirrors the BCL backend's allocation-free version).
    public int CountMappedRemotePeers(VoiceGameStateSnapshot snapshot)
    {
        var players = snapshot.Players;
        int count = 0;
        foreach (var kvp in _peers)
        {
            var peer = kvp.Value;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (!player.IsLocal && !player.Disconnected && !player.IsDummy && player.PlayerId == peer.PlayerId)
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    // Interstellar has no per-peer RTCDataChannel; relay-stream health is not observable at this layer, so a
    // mapped peer is the best available "open" signal. (BCL distinguishes mapped-but-unopened; Interstellar cannot.)
    public int CountPeersWithOpenChannel(VoiceGameStateSnapshot snapshot)
        => CountMappedRemotePeers(snapshot);

    // No observable per-peer data channel; the connected peer count is the best "physically alive" signal.
    public int CountOpenDataChannels() => _peers.Count;

    public bool TrySetRemoteVolume(byte playerId, string playerName, float volume)
    {
        foreach (var peer in _peers.Values)
        {
            if ((playerId != byte.MaxValue && peer.PlayerId == playerId) ||
                (!string.IsNullOrWhiteSpace(playerName) && string.Equals(peer.PlayerName, playerName, StringComparison.Ordinal)))
            {
                peer.SetVolume(volume);
                return true;
            }
        }

        return false;
    }

    public int ResetPeerMappingsNoMute()
    {
        int count = 0;
        foreach (var peer in _peers.Values)
        {
            peer.ResetMappingNoMute();
            count++;
        }

        return count;
    }

    // Interstellar has no per-peer offer/re-request path the room can drive — peers are created by the
    // library's onConnectClient. There is no targeted recovery to attempt, so signal the caller to fall
    // back to its existing full-rebuild path (gated, in the room, to genuine total failure).
    public int TryRecoverMissingClients(VoiceGameStateSnapshot snapshot) => -1;

    public InterstellarVoiceBackend(string roomCode, string region, string serverUrl)
    {
        ServerUrl = VoiceEndpointSettings.NormalizeInterstellarServerUrl(serverUrl);
        RoomCode = roomCode;
        Region = region;

        var source = new SimpleRouter();
        var endpoint = new SimpleEndpoint();

        _imager = new StereoRouter();
        _normalVolume = new VolumeRouter();
        _ghostVolume = new VolumeRouter();
        _radioVolume = new VolumeRouter();
        _listenerMuffleVolume = new VolumeRouter();
        _clientVolume = new VolumeRouter();
        _levelMeter = new LevelMeterRouter();

        var ghostLowpass = FilterRouter.CreateLowPassFilter(1900f, 2f);
        var listenerMuffleLowpass = FilterRouter.CreateLowPassFilter(650f, 0.8f);
        var listenerMuffleReverb = new ReverbRouter(41, 0.55f, 0.65f) { IsGlobalRouter = true };
        var ghostReverb1 = new ReverbRouter(53, 0.7f, 0.2f) { IsGlobalRouter = true };
        var ghostReverb2 = new ReverbRouter(173, 0.4f, 0.6f) { IsGlobalRouter = true };
        var radioHighpass = FilterRouter.CreateHighPassFilter(RadioHighPassFrequency, 3.2f);
        var radioDistort = new DistortionFilter { IsGlobalRouter = true, DefaultThreshold = RadioDistortionThreshold };
        var masterRouter = new VolumeRouter { IsGlobalRouter = true };

        source.Connect(_clientVolume);
        _clientVolume.Connect(_imager);
        _imager.Connect(_normalVolume);
        _normalVolume.Connect(_levelMeter);
        _levelMeter.Connect(masterRouter);
        _imager.Connect(ghostLowpass);
        ghostLowpass.Connect(_ghostVolume);
        _ghostVolume.Connect(ghostReverb1);
        ghostReverb1.Connect(ghostReverb2);
        ghostReverb2.Connect(masterRouter);
        _imager.Connect(listenerMuffleLowpass);
        listenerMuffleLowpass.Connect(_listenerMuffleVolume);
        _listenerMuffleVolume.Connect(listenerMuffleReverb);
        listenerMuffleReverb.Connect(masterRouter);
        _clientVolume.Connect(radioHighpass);
        radioHighpass.Connect(_radioVolume);
        _radioVolume.Connect(radioDistort);
        radioDistort.Connect(masterRouter);
        masterRouter.Connect(endpoint);

        _room = new VCRoom(source, roomCode, region, VoiceEndpointSettings.BuildInterstellarRoomUrl(ServerUrl),
            new VCRoomParameters
            {
                OnConnectClient = (clientId, instance, isLocal) =>
                {
                    if (isLocal)
                    {
                        _clientVolume.GetProperty(instance).Volume = 1f;
                        _normalVolume.GetProperty(instance).Volume = 1f;
                        _localMicMeter = _levelMeter.GetProperty(instance);
                        VoiceDiagnostics.Log("interstellar.local-connected", $"client={clientId} graph=fangkuai localMeter=normal-branch");
                    }
                    else
                    {
                        _peers[clientId] = new Peer(clientId, instance, _imager, _normalVolume, _ghostVolume, _radioVolume, _listenerMuffleVolume, _clientVolume, _levelMeter);
                        VoiceDiagnostics.Log("interstellar.peer-connected", $"client={clientId}");
                    }
                },
                OnUpdateProfile = (clientId, playerId, playerName) =>
                {
                    if (_peers.TryGetValue(clientId, out var peer))
                    {
                        peer.UpdateProfile(playerId, playerName);
                        ApplySavedVolume(peer);
                        VoiceDiagnostics.Log("interstellar.profile", $"client={clientId} player={playerId} name={playerName}");
                    }
                },
                OnDisconnect = clientId =>
                {
                    _peers.TryRemove(clientId, out _);
                    VoiceDiagnostics.Log("interstellar.peer-disconnected", $"client={clientId}");
                },
                MessageHandler = HandleCustomMessage,
            }.SetBufferLength(InterstellarReceiveBufferSamples, InterstellarReceiveBufferAdditionalSamples));

        _masterVolume = masterRouter.GetProperty(_room);
        VoiceDiagnostics.Log("interstellar.created", $"room={roomCode} region={region} endpoint={VoiceEndpointSettings.BuildInterstellarRoomUrl(ServerUrl)} loopbackMeter=false graph=fangkuai buffer={InterstellarReceiveBufferSamples} diagVersion={DiagnosticsVersion}");
    }

    public void SetMute(bool mute)
    {
        if (Mute == mute) return;
        _room.SetMute(mute);
        if (mute)
        {
            StopMicrophone("muted");
        }
        else
        {
            SetMicrophone(_lastMicDeviceName, _lastMicVolume);
        }
        VoiceDiagnostics.Log("interstellar.mute", $"mute={Mute} micReady={_microphoneReady} level={LocalLevel:0.000}");
    }
    public void ToggleMute() => SetMute(!Mute);
    public void SetLoopBack(bool loopBack) => _room.SetLoopBack(loopBack);
    public void SetMasterVolume(float volume)
    {
        _masterVolume.Volume = Math.Clamp(volume, 0f, 2f);
    }

    public void SetMicVolume(float volume)
    {
        _lastMicVolume = Math.Clamp(volume, 0f, 2f);
        _room.Microphone?.SetVolume(_lastMicVolume);
    }

    public void SetNoiseGate(float noiseGateThreshold, float vadThreshold)
    {
        // Interstellar owns its own microphone processing; thresholds are applied by
        // the BetterCrewLink encoder path and Interstellar local speaking still uses
        // the backend's raw/metered mic level.
    }

    public void SetCaptureRuntimeOptions(VoiceCaptureRuntimeOptions options)
    {
        var restartCapture = _captureOptions.SyntheticMicToneEnabled != options.SyntheticMicToneEnabled;
        _captureOptions = options;
#if WINDOWS
        _autoMicGain = ReadAutoMicGainSetting();
        try
        {
            lock (_micProcessSync)
            {
                _micPreprocessor.SetNoiseSuppressionEnabled(options.NoiseSuppressionEnabled);
                _micPreprocessor.SetEchoCancellationEnabled(options.EchoCancellationEnabled);
            }
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("interstellar.mic", $"noiseSuppression=error error=\"{ex.Message}\"");
        }
#endif
        if (restartCapture && _microphoneReady && !Mute)
            SetMicrophone(_lastMicDeviceName, _lastMicVolume);
        VoiceDiagnostics.Log("interstellar.capture-options", $"syntheticTone={options.SyntheticMicToneEnabled} noiseSuppression={options.NoiseSuppressionEnabled}");
    }

#if WINDOWS
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
#endif

    private float ResolveSpeakingThreshold()
    {
        var sensitivity = Math.Clamp(_captureOptions.MicSensitivity, 0.25f, 2f);
        return Math.Clamp(0.004f / sensitivity, 0.0005f, 0.080f);
    }

    public void SetMicrophone(string deviceName, float volume)
    {
        _lastMicDeviceName = deviceName ?? string.Empty;
        _lastMicVolume = Math.Clamp(volume, 0f, 2f);
        if (Mute)
        {
            StopMicrophone("set-muted");
            VoiceDiagnostics.Log("interstellar.mic", $"ready=false muted=true device=\"{_lastMicDeviceName}\" volume={_lastMicVolume:0.00}");
            return;
        }

        try
        {
            StopSyntheticMicTone();
#if WINDOWS
            StopWindowsMicrophoneCapture();
            _latchedMicChannel = 0;
            _micChannelSwitchStreak = 0;
#endif
            if (_captureOptions.SyntheticMicToneEnabled)
            {
#if ANDROID
                _androidMicrophone?.Dispose();
                _androidMicrophone = null;
#endif
                var manualMicrophone = new ManualMicrophone();
                _syntheticMicrophone = manualMicrophone;
                _room.Microphone = manualMicrophone;
                StartSyntheticMicTone("settings");
            }
            else
            {
#if ANDROID
                _androidMicrophone?.Dispose();
                var manualMicrophone = new ManualMicrophone();
                _androidMicrophone = new AndroidMicrophone();
                _androidMicrophone.DataAvailable += (buffer, _) => manualMicrophone.PushAudioData(buffer);
                _androidMicrophone.Start(_lastMicDeviceName);
                _room.Microphone = manualMicrophone;
#else
                StartWindowsMicrophone(_lastMicDeviceName);
#endif
            }
            SetMicVolume(_lastMicVolume);
            _microphoneReady = _room.Microphone != null;
            VoiceDiagnostics.Log("interstellar.mic", $"ready={_microphoneReady} device=\"{_lastMicDeviceName}\" volume={_lastMicVolume:0.00} muted={Mute}");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugError($"[VC] Interstellar mic init failed: {ex.Message}");
            StopMicrophone("failed");
            VoiceDiagnostics.Log("interstellar.mic", $"ready=false device=\"{_lastMicDeviceName}\" error=\"{ex.Message}\"");
        }
    }

    private void StopMicrophone(string reason)
    {
        var hadMic = _microphoneReady || _room.Microphone != null;
        StopSyntheticMicTone();
#if ANDROID
        _androidMicrophone?.Dispose();
        _androidMicrophone = null;
#endif
#if WINDOWS
        StopWindowsMicrophoneCapture();
#endif
        try { _room.Microphone = null; } catch { }
        _microphoneReady = false;
        if (hadMic)
            VoiceDiagnostics.Log("interstellar.mic", $"ready=false reason={reason} device=\"{_lastMicDeviceName}\" level={LocalLevel:0.000}");
    }

#if WINDOWS
    private void OnWindowsPlaybackStopped(object? sender, NAudio.Wave.StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _windowsSpeakerOutput)) return;
        _speakerReady = false;
        if (e.Exception == null) return;
        VoiceDiagnostics.Log("interstellar.speaker", $"ready=false reason=playback-stopped error=\"{e.Exception.Message}\"");
        Interlocked.Exchange(ref _speakerReopenRequestedFlag, 1);
    }

    private void MaybeFollowSpeakerTopology()
    {
        if (!_speakerRequested) return;
        if (Volatile.Read(ref _speakerReopenRequestedFlag) == 1)
        {
            var reopenNow = DateTime.UtcNow;
            if (reopenNow - _lastFlagReopenUtc >= TimeSpan.FromSeconds(1))
            {
                Interlocked.Exchange(ref _speakerReopenRequestedFlag, 0);
                _lastFlagReopenUtc = reopenNow;
                VoiceDiagnostics.Log("interstellar.speaker", "reason=playback-stopped-reopen");
                SetSpeaker(_lastSpeakerDeviceName);
                return;
            }
        }

        var now = DateTime.UtcNow;
        bool fastWindow = now.Ticks < Interlocked.Read(ref _speakerTopologyFastUntilTicks);
        var interval = fastWindow ? SpeakerTopologyFastPollInterval : SpeakerTopologyPollInterval;
        if (now - _speakerTopologyLastPollUtc < interval) return;
        _speakerTopologyLastPollUtc = now;

        string signature;
        try { signature = DescribeWindowsWaveOutDevices(); }
        catch { return; }

        if (signature == _speakerTopologySignature)
        {
            if (_speakerReady) return;
            if (now - _lastSpeakerRetryUtc < SpeakerRetryInterval) return;
            _lastSpeakerRetryUtc = now;
            VoiceDiagnostics.Log("interstellar.speaker", $"ready=false reason=retry devices=\"{signature}\"");
            SetSpeaker(_lastSpeakerDeviceName);
            return;
        }

        bool pinned = !string.IsNullOrWhiteSpace(_lastSpeakerDeviceName);
        if (pinned && _speakerReady)
        {
            var resolved = ResolveWindowsWaveOutDevice(_lastSpeakerDeviceName);
            if (resolved == _openedSpeakerDeviceNumber
                && string.Equals(DescribeWindowsWaveOutDevice(resolved), _openedSpeakerProductName, StringComparison.Ordinal))
            {
                _speakerTopologySignature = signature;
                return;
            }
        }

        VoiceDiagnostics.Log("interstellar.speaker", $"ready={_speakerReady} reason={(pinned ? "pinned-follow" : "default-follow")} oldDevices=\"{_speakerTopologySignature}\" newDevices=\"{signature}\"");
        SetSpeaker(_lastSpeakerDeviceName);
    }

    private void StartWindowsMicrophone(string deviceName)
    {
        StopWindowsMicrophoneCapture();
        var manualMicrophone = new ManualMicrophone();
        _windowsMicrophone = manualMicrophone;
        _room.Microphone = manualMicrophone;

        var waveInDevice = ResolveWindowsWaveInDevice(deviceName);
        var capture = new WaveInEvent
        {
            BufferMilliseconds = 20,
            NumberOfBuffers = 4,
            WaveFormat = new WaveFormat(Audio.AudioHelpers.ClockRate, 16, 1),
            DeviceNumber = waveInDevice,
        };
        var captureDevice = DescribeWindowsWaveInDevice(waveInDevice);

        _windowsMicrophoneCapture = capture;
        capture.DataAvailable += OnWindowsMicrophoneData;
        capture.StartRecording();
        Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
        VoiceDiagnostics.Log("interstellar.mic", $"capture=wavein-only captureDevice=\"{captureDevice}\" captureFormat=\"{DescribeWaveFormat(capture.WaveFormat)}\" waveInDevice={waveInDevice} defaultWaveIn=\"{DescribeDefaultWindowsWaveInDevice()}\"");
    }

    private void StopWindowsMicrophoneCapture()
    {
        var capture = _windowsMicrophoneCapture;
        _windowsMicrophoneCapture = null;
        _windowsMicrophone = null;
        if (capture == null) return;
        try { capture.DataAvailable -= OnWindowsMicrophoneData; } catch { }
        try { capture.StopRecording(); } catch { }
        try { capture.Dispose(); } catch { }
        Interlocked.Exchange(ref _speakerTopologyFastUntilTicks, (DateTime.UtcNow + SpeakerTopologyFastWindow).Ticks);
    }

    private void OnWindowsMicrophoneData(object? sender, WaveInEventArgs e)
    {
        Interlocked.Increment(ref _windowsMicCallbacks);
        var microphone = _windowsMicrophone;
        if (microphone == null)
        {
            Interlocked.Increment(ref _windowsMicNoMicrophone);
            return;
        }
        if (e.Buffer == null)
        {
            Interlocked.Increment(ref _windowsMicNoBuffer);
            return;
        }
        var recordedBytes = Math.Min(e.BytesRecorded, e.Buffer.Length);
        Interlocked.Add(ref _windowsMicBytes, recordedBytes);
        Volatile.Write(ref _windowsMicLastBytes, recordedBytes);
        if (recordedBytes <= 1)
        {
            Interlocked.Increment(ref _windowsMicTinyCallbacks);
            return;
        }
        var format = (sender as IWaveIn)?.WaveFormat ?? _windowsMicrophoneCapture?.WaveFormat;
        if (format == null)
        {
            Interlocked.Increment(ref _windowsMicNoFormat);
            return;
        }
        var samples = ConvertWindowsCaptureToMonoFloat(e.Buffer, recordedBytes, format, out var floatPcm);
        if (samples > 0)
        {
            Interlocked.Add(ref _windowsMicSamples, samples);
            Volatile.Write(ref _windowsMicLastSamples, samples);
            try
            {
                lock (_micProcessSync)
                {
                    _micPreprocessor.ApplyHighPass(floatPcm, samples);
                    _micPreprocessor.ApplyAutoGain(floatPcm, samples, _autoMicGain, out _);
                    if (_captureOptions.NoiseSuppressionEnabled)
                        _micPreprocessor.TryApplyNoiseSuppression(floatPcm, samples);
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("interstellar.mic.capture_error", $"stage=preprocess error=\"{ex.Message}\"");
            }
            var peak = Audio.AudioHelpers.MeasurePeak(floatPcm, samples);
            if (peak <= 0.001f)
                Interlocked.Increment(ref _windowsMicSilentCallbacks);
            var peakMilli = ToMilli(peak);
            Volatile.Write(ref _windowsMicLastPeakMilli, peakMilli);
            UpdateMax(ref _windowsMicPeakMilli, peakMilli);
            var transmitGain = Audio.AudioHelpers.GetSmoothedTransmitLimiterGain(_transmitLimiterGain, peak);
            _transmitLimiterGain = transmitGain;
            Volatile.Write(ref _windowsMicLastGainMilli, ToMilli(transmitGain));
            Audio.AudioHelpers.ApplyGain(floatPcm, samples, transmitGain);
            Interlocked.Increment(ref _windowsMicPushedFrames);
            microphone.PushAudioData(floatPcm);
        }
    }

    private int ConvertWindowsCaptureToMonoFloat(byte[] buffer, int recordedBytes, WaveFormat format, out float[] floatPcm)
    {
        var channels = Math.Max(1, format.Channels);
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return ConvertWindowsFloat32ToMono(buffer, recordedBytes, channels, out floatPcm);
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
            return ConvertWindowsPcm16ToMono(buffer, recordedBytes, channels, out floatPcm);

        floatPcm = Array.Empty<float>();
        Interlocked.Increment(ref _windowsMicUnsupportedFormats);
        VoiceDiagnostics.Log("interstellar.mic.capture_error", $"format=\"{DescribeWaveFormat(format)}\" bytes={recordedBytes} error=\"unsupported-capture-format\"");
        return 0;
    }

    private string DescribeWindowsMicCaptureDiagnostics()
        => $"callbacks={Interlocked.Read(ref _windowsMicCallbacks)} bytes={Interlocked.Read(ref _windowsMicBytes)} samples={Interlocked.Read(ref _windowsMicSamples)} pushed={Interlocked.Read(ref _windowsMicPushedFrames)} silentCallbacks={Interlocked.Read(ref _windowsMicSilentCallbacks)} tinyCallbacks={Interlocked.Read(ref _windowsMicTinyCallbacks)} noMic={Interlocked.Read(ref _windowsMicNoMicrophone)} noBuffer={Interlocked.Read(ref _windowsMicNoBuffer)} noFormat={Interlocked.Read(ref _windowsMicNoFormat)} unsupportedFormats={Interlocked.Read(ref _windowsMicUnsupportedFormats)} lastBytes={Volatile.Read(ref _windowsMicLastBytes)} lastSamples={Volatile.Read(ref _windowsMicLastSamples)} lastPeak={FromMilli(Volatile.Read(ref _windowsMicLastPeakMilli)):0.000} peak={FromMilli(Volatile.Read(ref _windowsMicPeakMilli)):0.000} lastGain={FromMilli(Volatile.Read(ref _windowsMicLastGainMilli)):0.000}";

    private static int ToMilli(float value)
        => (int)MathF.Round(Math.Clamp(value, 0f, 4f) * 1000f);

    private static float FromMilli(int value)
        => value / 1000f;

    private static void UpdateMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current) return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private int ConvertWindowsFloat32ToMono(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        var frames = recordedBytes / (sizeof(float) * channels);
        floatPcm = new float[frames];
        var dominantChannel = SelectDominantWindowsFloat32Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * sizeof(float);
            floatPcm[frame] = ReadWindowsFloat32Sample(buffer, offset);
        }
        return frames;
    }

    private int ConvertWindowsPcm16ToMono(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        var frames = recordedBytes / (sizeof(short) * channels);
        floatPcm = new float[frames];
        var dominantChannel = SelectDominantWindowsPcm16Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * sizeof(short);
            floatPcm[frame] = BitConverter.ToInt16(buffer, offset) / (float)short.MaxValue;
        }
        return frames;
    }

    private const double MicChannelSwitchEnergyRatio = 2.0;
    private const int MicChannelSwitchStreakCallbacks = 25;

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
                VoiceDiagnostics.Log("interstellar.mic.channel-latch",
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

    private int SelectDominantWindowsFloat32Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var energies = EnsureMicChannelEnergyCapacity(channels);
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = (frame * channels + channel) * sizeof(float);
                var sample = ReadWindowsFloat32Sample(buffer, offset);
                energy += (double)sample * sample;
            }
            energies[channel] = energy;
        }
        return LatchDominantChannel(energies, channels);
    }

    private int SelectDominantWindowsPcm16Channel(byte[] buffer, int frames, int channels)
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

    private static float ReadWindowsFloat32Sample(byte[] buffer, int offset)
    {
        var sample = BitConverter.ToSingle(buffer, offset);
        if (float.IsNaN(sample)) return 0f;
        return Math.Clamp(sample, -1f, 1f);
    }

    private static int ResolveWindowsWaveInDevice(string deviceName)
    {
        var requested = NormalizeAudioDeviceName(deviceName);
        if (!string.IsNullOrWhiteSpace(requested))
            return ResolveWindowsWaveInDeviceByName(requested);
        return -1;
    }

    private static int ResolveWindowsWaveInDeviceByName(string requested)
    {
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var productName = NormalizeAudioDeviceName(WaveInEvent.GetCapabilities(i).ProductName);
            if (DeviceNamesMatch(requested, productName))
                return i;
        }
        return -1;
    }

    private static string DescribeWindowsWaveInDevice(int deviceNumber)
    {
        if (deviceNumber < 0) return "mapper";
        try { return WaveInEvent.GetCapabilities(deviceNumber).ProductName ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static string DescribeDefaultWindowsWaveInDevice()
        => "mapper";

    private static int ResolveWindowsWaveOutDevice(string? deviceName)
    {
        var requested = NormalizeAudioDeviceName(deviceName);
        if (!string.IsNullOrWhiteSpace(requested))
        {
            for (var i = 0; i < Audio.WinMmOutputDevices.DeviceCount; i++)
            {
                var productName = NormalizeAudioDeviceName(Audio.WinMmOutputDevices.GetProductName(i));
                if (DeviceNamesMatch(requested, productName))
                    return i;
            }
        }

        return -1;
    }

    private static string DescribeWindowsWaveOutDevice(int deviceNumber)
        => deviceNumber < 0 ? "mapper" : Audio.WinMmOutputDevices.GetProductName(deviceNumber);

    private static string DescribeWindowsWaveOutDevices()
    {
        try
        {
            var names = new List<string>();
            for (var i = 0; i < Audio.WinMmOutputDevices.DeviceCount; i++)
                names.Add($"{i}:{Audio.WinMmOutputDevices.GetProductName(i)}");
            return names.Count == 0 ? "none" : string.Join("|", names);
        }
        catch (Exception ex)
        {
            return $"error:{ex.Message}";
        }
    }

    private static string DescribeWaveFormat(WaveFormat? format)
    {
        if (format == null) return "none";
        return $"{format.Encoding}/{format.SampleRate}Hz/{format.BitsPerSample}bit/{format.Channels}ch";
    }

    private static string NormalizeAudioDeviceName(string? deviceName)
        => string.Join(" ", (deviceName ?? string.Empty)
            .Trim()
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            .Trim();

    private static bool DeviceNamesMatch(string requested, string actual)
        => string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase) ||
           actual.StartsWith(requested, StringComparison.OrdinalIgnoreCase) ||
           requested.StartsWith(actual, StringComparison.OrdinalIgnoreCase);
#endif

    private void StartSyntheticMicTone(string reason)
    {
        StopSyntheticMicTone();
        _syntheticTonePhase = 0.0;
        _syntheticMicTimer = new Timer(_ => OnSyntheticMicTick(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
        VoiceDiagnostics.Log("interstellar.synthetic", $"state=started reason={reason} sampleRate={Audio.AudioHelpers.ClockRate} frameSize={Audio.AudioHelpers.FrameSize} toneHz={SyntheticToneFrequency:0} amplitude={SyntheticToneAmplitude:0.000}");
    }

    private void StopSyntheticMicTone()
    {
        var timer = _syntheticMicTimer;
        _syntheticMicTimer = null;
        _syntheticMicrophone = null;
        try { timer?.Dispose(); } catch { }
    }

    private void OnSyntheticMicTick()
    {
        var microphone = _syntheticMicrophone;
        if (microphone == null || Mute || !_captureOptions.SyntheticMicToneEnabled) return;
        var frame = new float[Audio.AudioHelpers.FrameSize];
        const double frequency = SyntheticToneFrequency;
        const float amplitude = SyntheticToneAmplitude;
        var phaseStep = frequency / Audio.AudioHelpers.ClockRate;
        var phase = _syntheticTonePhase;
        for (var i = 0; i < frame.Length; i++)
        {
            frame[i] = (float)Math.Sin(phase * Math.PI * 2.0) * amplitude;
            phase += phaseStep;
            if (phase >= 1.0) phase -= 1.0;
        }
        _syntheticTonePhase = phase;
        Interlocked.Increment(ref _syntheticFrames);
        microphone.PushAudioData(frame);
    }

    public void SetSpeaker(string deviceName)
    {
        try
        {
#if ANDROID
            _androidSpeaker?.Dispose();
            var manualSpeaker = new ManualSpeaker(() => _androidSpeaker?.Dispose());
            _room.Speaker = manualSpeaker;
            _androidSpeaker = new AndroidSpeaker(manualSpeaker);
            _speakerReady = _room.Speaker != null;
            VoiceDiagnostics.Log("interstellar.speaker", $"ready={_speakerReady} device=\"{deviceName}\"");
#elif WINDOWS
            StopWindowsSpeaker();
            _lastSpeakerDeviceName = deviceName ?? string.Empty;
            _speakerRequested = true;
            _speakerTopologySignature = DescribeWindowsWaveOutDevices();
            var manualSpeaker = new ManualSpeaker(StopWindowsSpeaker);
            _room.Speaker = manualSpeaker;
            _windowsSpeaker = manualSpeaker;
            var provider = new WindowsManualSpeakerSampleProvider(manualSpeaker);
            _windowsSpeakerProvider = provider;

            var outputDevice = ResolveWindowsWaveOutDevice(deviceName);
            _windowsSpeakerOutput = new WaveOutEvent
            {
                DeviceNumber = outputDevice,
                DesiredLatency = 60,
                NumberOfBuffers = 3,
            };
            _windowsSpeakerOutput.PlaybackStopped += OnWindowsPlaybackStopped;
            _windowsSpeakerOutput.Init(provider.ToWaveProvider());
            _windowsSpeakerOutput.Play();
            _speakerReady = _windowsSpeakerOutput.PlaybackState == PlaybackState.Playing;
            _openedSpeakerDeviceNumber = outputDevice;
            _openedSpeakerProductName = DescribeWindowsWaveOutDevice(outputDevice);
            VoiceDiagnostics.Log("interstellar.speaker", $"ready={_speakerReady} device=\"{deviceName}\" outputDevice=\"{DescribeWindowsWaveOutDevice(outputDevice)}\" outputDeviceNumber={outputDevice} sourceFormat=\"{provider.WaveFormat}\" graphFormat=\"{provider.WaveFormat}\" outputDevices=\"{DescribeWindowsWaveOutDevices()}\" latencyMs=60 manualSpeaker=true");
#else
            try { _room.Speaker = null; } catch { }
            _speakerReady = false;
            VoiceDiagnostics.Log("interstellar.speaker", $"ready=false device=\"{deviceName}\" reason=unsupported-platform-no-winmm-speaker");
#endif
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugError($"[VC] Interstellar speaker init failed: {ex.Message}");
#if ANDROID
            _androidSpeaker?.Dispose();
            _androidSpeaker = null;
#elif WINDOWS
            StopWindowsSpeaker();
#endif
            try { _room.Speaker = null; } catch { }
            _speakerReady = false;
            VoiceDiagnostics.Log("interstellar.speaker", $"ready=false device=\"{deviceName}\" error=\"{ex.Message}\"");
        }
    }

    public void UpdateProfile(byte playerId, string? playerName)
    {
        var safeName = string.IsNullOrWhiteSpace(playerName) ? "Unknown" : playerName.Trim();
        if (_lastPlayerId == playerId && string.Equals(_lastPlayerName, safeName, StringComparison.Ordinal))
            return;

        _lastPlayerId = playerId;
        _lastPlayerName = safeName;
        _room.UpdateProfile(safeName, playerId);
    }

    public void SendCustomMessage(byte[] payload)
    {
        if (!InterstellarCustomControlEnabled)
        {
            Interlocked.Increment(ref _customSkipped);
            VoiceDiagnostics.Log("interstellar.custom.skip", $"bytes={payload.Length} reason=audio-relay-reserved");
            return;
        }

        Interlocked.Increment(ref _customTx);
        VoiceDiagnostics.Log("interstellar.custom.tx", $"bytes={payload.Length}");
        _room.SendCustomMessage(payload);
    }

    public void SendRadioState(byte playerId, VoiceTeamRadioChannel channel)
    {
        if (_lastPlayerId == byte.MaxValue) return;
        channel = VoiceTeamRadioChannels.Normalize(channel);
        if (_lastLocalRadioChannel == channel && playerId == _lastPlayerId) return;

        _lastLocalRadioChannel = channel;
        var payload = new byte[7];
        Array.Copy(RadioStateMagic, payload, RadioStateMagic.Length);
        payload[4] = playerId;
        payload[5] = VoiceTeamRadioChannels.IsActive(channel) ? (byte)1 : (byte)0;
        payload[6] = (byte)channel;
        if (!InterstellarCustomControlEnabled)
        {
            Interlocked.Increment(ref _customSkipped);
            VoiceDiagnostics.Log("interstellar.radio.skip", $"player={playerId} active={VoiceTeamRadioChannels.IsActive(channel)} channel={channel} reason=audio-relay-reserved");
            return;
        }

        Interlocked.Increment(ref _customTx);
        VoiceDiagnostics.Log("interstellar.radio.tx", $"player={playerId} active={VoiceTeamRadioChannels.IsActive(channel)} channel={channel}");
        _room.SendCustomMessage(payload);
    }

    public void ApplyRemoteRadioState(byte playerId, VoiceTeamRadioChannel channel)
    {
        channel = VoiceTeamRadioChannels.Normalize(channel);
        foreach (var peer in _peers.Values)
        {
            if (peer.PlayerId == playerId)
            {
                peer.ApplyRadioChannel(channel);
                VoiceDiagnostics.Log("interstellar.radio.rx", $"client={peer.ClientId} player={playerId} active={VoiceTeamRadioChannels.IsActive(channel)} channel={channel}");
            }
        }
    }

    public void Rejoin()
    {
        _peers.Clear();
        _room.Rejoin();
        _lastPlayerId = byte.MaxValue;
        _lastPlayerName = string.Empty;
        _lastLocalRadioChannel = VoiceTeamRadioChannel.None;
        VoiceDiagnostics.Log("interstellar.rejoin", "state=cleared");
    }

    // No-op: Interstellar does not pre-build a peer-connection / ICE pool, so there is nothing to rebuild
    // when the Nat Fix / TURN settings change (those settings are BetterCrewLink-backend only).
    public void RebuildIceConnectionPool() { }

    public void Update(
        VoiceGameStateSnapshot? snapshot,
        IReadOnlyList<VoiceChatRoom.SpeakerCache> speakerCache,
        IReadOnlyList<IVoiceComponent> virtualMicrophones,
        bool localInVent,
        bool commsSabActive)
    {
#if ANDROID
        _androidMicrophone?.Tick();
#elif WINDOWS
        MaybeFollowSpeakerTopology();
#endif
        if (snapshot == null)
        {
            // Iterate the ConcurrentDictionary directly; _peers.Values allocates a snapshot List every call.
            foreach (var kvp in _peers)
                kvp.Value.MuteAll();
            MaybeLogStats(snapshot, "no-snapshot");
            return;
        }

        var localPlayer = snapshot.TryGetLocalPlayer(out var local) ? local : (VoicePlayerSnapshot?)null;
        var listenerPos = localPlayer?.Position;

        // ConcurrentDictionary enumeration is safe against concurrent connect/disconnect (the loop body
        // never adds/removes peers), so iterate it directly instead of allocating _peers.Values.ToArray()
        // (a snapshot List PLUS an array) every frame on the Unity main thread.
        long proxTicks = VoiceFrameProfiler.Begin();
        foreach (var kvp in _peers)
        {
            var peer = kvp.Value;
            var target = FindTarget(snapshot, peer);
            if (!target.HasValue && TryApplySingleRemoteFallback(snapshot, peer, out var fallback))
                target = fallback;
            if (target.HasValue && VoiceProximityCalculator.IsUnavailableTarget(target.Value))
                peer.ResetMappingNoMute();
            if (target.HasValue && !VoiceProximityCalculator.IsUnavailableTarget(target.Value) &&
                peer.UpdateProfile(target.Value.PlayerId, target.Value.PlayerName))
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
            peer.SampleDiagnostics();
        }
        VoiceFrameProfiler.End("room.backend.proximity", proxTicks);

        MaybeLogStats(snapshot, "ok");
    }

    private bool TryApplySingleRemoteFallback(VoiceGameStateSnapshot snapshot, Peer peer, out VoicePlayerSnapshot target)
    {
        target = default;
        if (peer.PlayerId != byte.MaxValue)
            return false;

        // Manual scans with early-exit instead of Where().ToArray() + Count(predicate): this runs per
        // unmapped peer per frame during join/churn, and the LINQ allocated an array + enumerators + closures.
        VoicePlayerSnapshot single = default;
        int remoteCount = 0;
        var players = snapshot.Players;
        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player.IsLocal || VoiceProximityCalculator.IsUnavailableTarget(player) || player.ClientId < 0)
                continue;
            if (++remoteCount > 1) return false;
            single = player;
        }
        if (remoteCount != 1)
            return false;

        int unprofiledPeers = 0;
        foreach (var kvp in _peers)
        {
            if (kvp.Value.PlayerId == byte.MaxValue && ++unprofiledPeers > 1)
                return false;
        }
        if (unprofiledPeers != 1)
            return false;

        target = single;
        if (peer.UpdateProfile(target.PlayerId, target.PlayerName))
        {
            ApplySavedVolume(peer);
            VoiceDiagnostics.Log("interstellar.profile.fallback",
                $"client={peer.ClientId} player={target.PlayerId} name={target.PlayerName} reason=single-remote-unprofiled-peer");
        }
        return true;
    }

    public void Dispose()
    {
        StopMicrophone("dispose");
        try { _room.Disconnect(); } catch { }
        _peers.Clear();
#if WINDOWS
        _speakerRequested = false;
        try { lock (_micProcessSync) _micPreprocessor.Dispose(); } catch { }
#endif
#if ANDROID
        _androidSpeaker?.Dispose();
        _androidSpeaker = null;
#elif WINDOWS
        StopWindowsSpeaker();
#endif
        try { _room.Microphone = null; } catch { }
        try { _room.Speaker = null; } catch { }
    }

#if WINDOWS
    private void StopWindowsSpeaker()
    {
        var speaker = _windowsSpeakerOutput;
        _windowsSpeakerOutput = null;
        _windowsSpeaker = null;
        _windowsSpeakerProvider = null;
        _speakerReady = false;
        if (speaker == null) return;
        try { speaker.PlaybackStopped -= OnWindowsPlaybackStopped; } catch { }
        try { speaker.Stop(); } catch { }
        try { speaker.Dispose(); } catch { }
    }

    private sealed class WindowsManualSpeakerSampleProvider : ISampleProvider
    {
        private const int Channels = 2;
        private readonly ManualSpeaker _speaker;
        private float[] _scratch = Array.Empty<float>();
        private int _readCallbacks;
        private int _readFailures;
        private long _readSamples;
        private long _silentReads;
        private int _lastReadCount;
        private int _lastPeakMilli;
        private int _maxPeakMilli;

        public WindowsManualSpeakerSampleProvider(ManualSpeaker speaker)
        {
            _speaker = speaker;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Audio.AudioHelpers.ClockRate, Channels);
        public int ReadCallbacks => Volatile.Read(ref _readCallbacks);
        public int ReadFailures => Volatile.Read(ref _readFailures);
        public string Diagnostics => $"reads={Volatile.Read(ref _readCallbacks)} failures={Volatile.Read(ref _readFailures)} samples={Interlocked.Read(ref _readSamples)} silentReads={Interlocked.Read(ref _silentReads)} lastCount={Volatile.Read(ref _lastReadCount)} lastPeak={FromMilli(Volatile.Read(ref _lastPeakMilli)):0.000} peak={FromMilli(Volatile.Read(ref _maxPeakMilli)):0.000}";

        public int Read(float[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
            Interlocked.Increment(ref _readCallbacks);

            try
            {
                if (_scratch.Length != count)
                    _scratch = new float[count];
                _speaker.Read(_scratch);
                Interlocked.Add(ref _readSamples, count);
                Volatile.Write(ref _lastReadCount, count);
                var peak = 0f;
                for (var i = 0; i < count; i++)
                {
                    var abs = Math.Abs(_scratch[i]);
                    if (abs > peak) peak = abs;
                }
                if (peak <= 0.001f)
                    Interlocked.Increment(ref _silentReads);
                var peakMilli = ToMilli(peak);
                Volatile.Write(ref _lastPeakMilli, peakMilli);
                UpdateMax(ref _maxPeakMilli, peakMilli);
                for (var i = 0; i < count; i++)
                    buffer[offset + i] = _scratch[i];
                return count;
            }
            catch (Exception ex)
            {
                Array.Clear(buffer, offset, count);
                if (Interlocked.Increment(ref _readFailures) == 1)
                    VoiceDiagnostics.Log("interstellar.speaker.read_error", $"error=\"{ex.Message}\"");
            }
            return count;
        }
    }
#endif

    private void HandleCustomMessage(byte[] payload)
    {
        if (payload.Length is 6 or 7
            && payload[0] == RadioStateMagic[0]
            && payload[1] == RadioStateMagic[1]
            && payload[2] == RadioStateMagic[2]
            && payload[3] == RadioStateMagic[3])
        {
            // No sender identity on this path, so it can't be authenticated; rely on the
            // parallel authenticated Hazel RPC (VoiceRadioStateRpc) instead.
            return;
        }

        Interlocked.Increment(ref _customRx);
        VoiceDiagnostics.Log("interstellar.custom.rx", $"bytes={payload.Length}");
        CustomMessageReceived?.Invoke(VoiceBackendCustomMessage.Unknown(payload, "interstellar"));
    }

    private void MaybeLogStats(VoiceGameStateSnapshot? snapshot, string reason)
    {
        _room.TickConnectionRecovery();
        var now = DateTime.UtcNow;
        if ((now - _lastStatsLogUtc).TotalSeconds < 5)
            return;

        _lastStatsLogUtc = now;
        var peers = _peers.Values.ToArray();
        var audible = peers.Count(peer => peer.CurrentRoute.Audible);
        var speaking = peers.Count(peer => peer.IsSpeaking);
        var peerDiagnostics = peers.Select(peer => peer.ConsumeDiagnostics()).ToArray();
        int peerTicks = peerDiagnostics.Sum(item => item.Samples);
        int audibleTicks = peerDiagnostics.Sum(item => item.AudibleSamples);
        int audibleSilentTicks = peerDiagnostics.Sum(item => item.AudibleSilentSamples);
        int speakingFlips = peerDiagnostics.Sum(item => item.SpeakingTransitions);
        int routeFlips = peerDiagnostics.Sum(item => item.RouteChanges);
        float remoteLevelMax = peerDiagnostics.Length == 0 ? 0f : peerDiagnostics.Max(item => item.LevelPeak);
        float remoteLevelAvg = peerTicks == 0 ? 0f : peerDiagnostics.Sum(item => item.LevelSum) / peerTicks;
        float silentPct = audibleTicks == 0 ? 0f : audibleSilentTicks * 100f / audibleTicks;
        string routes = peers.Length == 0
            ? "none"
            : string.Join(",", peers
                .GroupBy(peer => peer.CurrentRoute.Reason)
                .Select(group => $"{group.Key}:{group.Count()}"));
        string peerWindows = peerDiagnostics.Length == 0
            ? "none"
            : string.Join("|", peerDiagnostics.Select(item => item.ToCompactString()));
        VoiceDiagnostics.Log("interstellar.stats",
            $"reason={reason} diagVersion={DiagnosticsVersion} room={RoomCode} region={Region} endpoint={VoiceEndpointSettings.BuildInterstellarRoomUrl(ServerUrl)} " +
            $"phase={snapshot?.Phase.ToString() ?? "none"} peers={_peers.Count} audible={audible} speaking={speaking} routes={routes} " +
            $"localLevel={LocalLevel:0.000} rawMicLevel={_room.Microphone?.Level ?? 0f:0.000} meterLevel={_localMicMeter?.Level ?? 0f:0.000} localSpeaking={LocalSpeaking} mute={Mute} " +
            $"remoteLevelMax={remoteLevelMax:0.000} remoteLevelAvg={remoteLevelAvg:0.000} peerTicks={peerTicks} " +
            $"audibleTicks={audibleTicks} audibleSilentTicks={audibleSilentTicks} silentPct={silentPct:0.0} " +
            $"speakingFlips={speakingFlips} routeFlips={routeFlips} peerWindows={peerWindows} " +
            $"rxBuffer={InterstellarReceiveBufferSamples} rxBufferMax={InterstellarReceiveBufferSamples + InterstellarReceiveBufferAdditionalSamples} " +
            $"connection=({_room.ConnectionDiagnostics}) " +
            $"micHudMuted={VoiceChatHudState.IsMuted} speakerHudMuted={VoiceChatHudState.IsSpeakerMuted} " +
            $"customTx={Volatile.Read(ref _customTx)} customRx={Volatile.Read(ref _customRx)} customSkipped={Volatile.Read(ref _customSkipped)} " +
#if ANDROID
            $"micReady={_microphoneReady} speakerReady={_speakerReady} androidSpeakerPlaying={_androidSpeaker?.IsPlaying == true} " +
            $"androidSpeakerReads={_androidSpeaker?.ReadCallbacks ?? 0} localMeterReady={_localMicMeter != null}");
#elif WINDOWS
            $"micReady={_microphoneReady} speakerReady={_speakerReady} windowsSpeakerReads={_windowsSpeakerProvider?.ReadCallbacks ?? 0} windowsSpeakerReadFailures={_windowsSpeakerProvider?.ReadFailures ?? 0} " +
            $"micCapture=({DescribeWindowsMicCaptureDiagnostics()}) speakerPull=({_windowsSpeakerProvider?.Diagnostics ?? "none"}) " +
            $"localMeterReady={_localMicMeter != null} syntheticTone={_captureOptions.SyntheticMicToneEnabled} syntheticFrames={Volatile.Read(ref _syntheticFrames)}");
#else
            $"micReady={_microphoneReady} speakerReady={_speakerReady} localMeterReady={_localMicMeter != null} syntheticTone={_captureOptions.SyntheticMicToneEnabled} syntheticFrames={Volatile.Read(ref _syntheticFrames)}");
#endif
    }

    private static void ApplySavedVolume(Peer peer)
    {
        if (VoiceVolumeMenu.TryGetSavedVolume(peer.PlayerName, out var volume))
            peer.SetVolume(volume);
    }

    private static VoicePlayerSnapshot? FindTarget(VoiceGameStateSnapshot snapshot, Peer peer)
    {
        int clientId = peer.ClientId;
        byte playerId = peer.PlayerId;
        if (clientId >= 0 && snapshot.TryGetClient(clientId, out var byClient))
            return byClient;

        if (playerId != byte.MaxValue
            && snapshot.TryGetPlayer(playerId, out var byPlayer)
            && byPlayer.ClientId == clientId)
            return byPlayer;

        return null;
    }

    private readonly struct PeerDiagnostics
    {
        public PeerDiagnostics(
            int clientId,
            float levelPeak,
            float levelSum,
            int samples,
            int audibleSamples,
            int audibleSilentSamples,
            int speakingTransitions,
            int routeChanges)
        {
            ClientId = clientId;
            LevelPeak = levelPeak;
            LevelSum = levelSum;
            Samples = samples;
            AudibleSamples = audibleSamples;
            AudibleSilentSamples = audibleSilentSamples;
            SpeakingTransitions = speakingTransitions;
            RouteChanges = routeChanges;
        }

        public int ClientId { get; }
        public float LevelPeak { get; }
        public float LevelSum { get; }
        public int Samples { get; }
        public int AudibleSamples { get; }
        public int AudibleSilentSamples { get; }
        public int SpeakingTransitions { get; }
        public int RouteChanges { get; }

        public string ToCompactString()
            => $"{ClientId}:{LevelPeak:0.000}/{Samples}/{AudibleSamples}/{AudibleSilentSamples}/{SpeakingTransitions}/{RouteChanges}";
    }

    private sealed class Peer
    {
        private readonly StereoRouter.Property _imager;
        private readonly VolumeRouter.Property _normalVolume;
        private readonly VolumeRouter.Property _ghostVolume;
        private readonly VolumeRouter.Property _radioVolume;
        private readonly VolumeRouter.Property _listenerMuffleVolume;
        private readonly VolumeRouter.Property _clientVolume;
        private readonly LevelMeterRouter.Property _levelMeter;

        public int ClientId { get; }
        public byte PlayerId { get; private set; } = byte.MaxValue;
        public string PlayerName { get; private set; } = "Unknown";
        public float WallCoefficient { get; private set; } = 1f;
        private VoiceTeamRadioChannel _radioChannel = VoiceTeamRadioChannel.None;
        // Owner of the cached _radioChannel: transient unmap keeps it, remap to another player drops it.
        private byte _radioChannelOwner = byte.MaxValue;
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

        // Apply a radio channel and record its owning PlayerId (see _radioChannelOwner).
        public void ApplyRadioChannel(VoiceTeamRadioChannel channel)
        {
            _radioChannel = VoiceTeamRadioChannels.Normalize(channel);
            _radioChannelOwner = PlayerId;
        }
        private VoiceProximityResult _currentRoute = VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);
        private float _levelPeakSinceStats;
        private float _levelSumSinceStats;
        private int _samplesSinceStats;
        private int _audibleSamplesSinceStats;
        private int _audibleSilentSamplesSinceStats;
        private int _speakingTransitionsSinceStats;
        private int _routeChangesSinceStats;
        private bool _lastSpeaking;
        private bool _hasSpeakingSample;
        private bool _hasRouteSample;
        public VoiceProximityResult CurrentRoute => _currentRoute;
        public bool IsSpeaking => _levelMeter.Level >= 0.004f;

        public Peer(
            int clientId,
            AudioRoutingInstance instance,
            StereoRouter imager,
            VolumeRouter normalVolume,
            VolumeRouter ghostVolume,
            VolumeRouter radioVolume,
            VolumeRouter listenerMuffleVolume,
            VolumeRouter clientVolume,
            LevelMeterRouter levelMeter)
        {
            ClientId = clientId;
            _imager = imager.GetProperty(instance);
            _normalVolume = normalVolume.GetProperty(instance);
            _ghostVolume = ghostVolume.GetProperty(instance);
            _radioVolume = radioVolume.GetProperty(instance);
            _listenerMuffleVolume = listenerMuffleVolume.GetProperty(instance);
            _clientVolume = clientVolume.GetProperty(instance);
            _levelMeter = levelMeter.GetProperty(instance);
            _clientVolume.Volume = 1f;
            MuteAll();
        }

        public bool UpdateProfile(byte playerId, string playerName)
        {
            string normalizedName = string.IsNullOrWhiteSpace(playerName) ? "Unknown" : playerName;
            if (PlayerId == playerId && PlayerName == normalizedName)
                return false;

            // Drop cached radio channel / wall coef only on a real remap to a different player;
            // a transient unmap-and-remap to the same player keeps it (avoids radio dropout).
            if (playerId != _radioChannelOwner)
            {
                _radioChannel = VoiceTeamRadioChannel.None;
                _radioChannelOwner = byte.MaxValue;
                WallCoefficient = 1f;
            }

            PlayerId = playerId;
            PlayerName = normalizedName;
            return true;
        }

        public void ResetMappingNoMute()
        {
            // Clear only the mapping; radio/wall are dropped in UpdateProfile on a genuine remap.
            PlayerId = byte.MaxValue;
        }

        public void SetVolume(float volume)
        {
            _clientVolume.Volume = Mathf.Clamp(volume, 0f, 2f);
        }

        public void MuteAll()
        {
            _normalVolume.Volume = 0f;
            _ghostVolume.Volume = 0f;
            _radioVolume.Volume = 0f;
            _listenerMuffleVolume.Volume = 0f;
            _imager.Pan = 0f;
        }

        public void Apply(VoiceProximityResult result)
        {
            if (_hasRouteSample && result.Reason != _currentRoute.Reason)
                _routeChangesSinceStats++;
            _hasRouteSample = true;
            _currentRoute = result;
            WallCoefficient = result.WallCoefficient;
            bool listenerMuffled = result.FilterMode == VoiceAudioFilterMode.ListenerMuffle;
            float routeVolume = Math.Clamp(result.NormalVolume + result.GhostVolume + result.RadioVolume, 0f, 1f);
            _normalVolume.Volume = listenerMuffled ? 0f : result.NormalVolume;
            _ghostVolume.Volume = listenerMuffled ? 0f : result.GhostVolume;
            _radioVolume.Volume = listenerMuffled ? 0f : result.RadioVolume;
            _listenerMuffleVolume.Volume = listenerMuffled ? routeVolume * 0.75f : 0f;
            _imager.Pan = result.Pan;
        }

        public void SampleDiagnostics()
        {
            var level = _levelMeter.Level;
            bool speaking = level >= 0.004f;
            _samplesSinceStats++;
            _levelPeakSinceStats = Math.Max(_levelPeakSinceStats, level);
            _levelSumSinceStats += level;
            if (_currentRoute.Audible)
            {
                _audibleSamplesSinceStats++;
                if (!speaking)
                    _audibleSilentSamplesSinceStats++;
            }

            if (_hasSpeakingSample && speaking != _lastSpeaking)
                _speakingTransitionsSinceStats++;
            _hasSpeakingSample = true;
            _lastSpeaking = speaking;
        }

        public PeerDiagnostics ConsumeDiagnostics()
        {
            var result = new PeerDiagnostics(
                ClientId,
                _levelPeakSinceStats,
                _levelSumSinceStats,
                _samplesSinceStats,
                _audibleSamplesSinceStats,
                _audibleSilentSamplesSinceStats,
                _speakingTransitionsSinceStats,
                _routeChangesSinceStats);

            _levelPeakSinceStats = 0f;
            _levelSumSinceStats = 0f;
            _samplesSinceStats = 0;
            _audibleSamplesSinceStats = 0;
            _audibleSilentSamplesSinceStats = 0;
            _speakingTransitionsSinceStats = 0;
            _routeChangesSinceStats = 0;
            return result;
        }

        public VoiceRemoteOverlayState ToOverlayState()
        {
            var level = _levelMeter.Level;
            return new VoiceRemoteOverlayState(
                PlayerId,
                PlayerName,
                level,
                level >= 0.004f,
                _currentRoute.Audible,
                _currentRoute.Reason);
        }
    }
}
