using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;
using SIPSorcery.Net;
using SocketIOClient;
using UnityEngine;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BetterCrewLinkVoiceBackend : IVoiceBackend
{
    private const int DataControlPrefixLength = 4;
    private static readonly int BclOpusBitrate = 48_000;
    private static readonly bool BclOpusUseConstrainedVbr = true;
    private static readonly bool BclOpusUseInbandFec = false;
    private static readonly int BclOpusPacketLossPercent = 0;
    private const int BclPlaybackLatencyMs = 60;
    private const int BclJitterTargetDelayFrames = 2;
    private const int BclJitterMaxBufferedFrames = 8;
    private const float RemoteSpeakingThreshold = 0.004f;
    private const double SyntheticToneFrequency = 220.0;
    private const float SyntheticToneAmplitude = 0.012f;
    private static readonly TimeSpan RemoteActivityHold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MicCalibrationLogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BclQuietTailFlushDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan BclQuietTailFlushTimerDelay = BclQuietTailFlushDelay + TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan SignalRejectLogInterval = TimeSpan.FromSeconds(5);
    private static readonly byte[] DataControlPrefix = [(byte)'P', (byte)'C', (byte)'B', (byte)'C'];
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
    private List<RTCIceServer> _iceServers = DefaultIceServers.ToList();
    private static readonly TimeSpan JoinRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OfferRetryInterval = TimeSpan.FromSeconds(3);
    private DateTime _lastStatsLogUtc = DateTime.MinValue;
    private DateTime _lastJoinAttemptUtc = DateTime.MinValue;
    private DateTime _lastOfferRetryUtc = DateTime.MinValue;
    private byte _lastPlayerId = byte.MaxValue;
    private byte _joinedPlayerId = byte.MaxValue;
    private int _joinedClientId = -1;
    private bool _joinedIsHost;
    private string _localSocketId = string.Empty;
    private string _lastPlayerName = string.Empty;
    private bool _lastLocalRadioActive;
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
    private int _captureFrameSamples;

    private SocketIOClient.SocketIO? _socket;
#if WINDOWS
    private IWaveIn? _waveIn;
    private IWavePlayer? _waveOut;
    private readonly object _captureWorkerSync = new();
    private Task _captureWorker = Task.CompletedTask;
    private bool _captureDesiredRunning;
    private string _captureDesiredReason = "init";
    private int _captureTransitionVersion;
#endif
#if ANDROID
    private AndroidMicrophone? _androidMicrophone;
    private AndroidSampleProviderSpeaker? _androidSpeaker;
#endif
#if MACOS
    private MacOsFullDuplexAudioEngine? _macAudioEngine;
    private bool _macSpeakerConfigured;
    private string _lastSpeakerDeviceName = string.Empty;
#endif
    private OpusEncoder _encoder = CreateEncoder();
    private Timer? _syntheticMicTimer;
    private string _lastMicDeviceName = string.Empty;
    private float _micVolume = 1f;
    private float _noiseGateThreshold;
    private float _vadThreshold = 0.004f;
    private float _localLevel;
    private bool _localSpeaking;
    private bool _microphoneReady;
    private bool _speakerReady;
    private VoiceCaptureRuntimeOptions _captureOptions;
    private double _syntheticTonePhase;
    private int _syntheticFrames;
    private DateTime _lastMicCalibrationLogUtc = DateTime.MinValue;
    private string _lastGateReason = "none";
    private float _lastGatePeak;
    private float _lastGateRms;
    private float _lastGateThreshold;
    private float _lastTransmitGain = 1f;
    private float _lastTransmitPeak;
    private bool _disposed;

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
        _playbackProvider = new BclStereoPlaybackProvider(_leftPlayback.Endpoint, _rightPlayback.Endpoint);

        ConnectSocket();
        VoiceDiagnostics.Log("bcl.created", $"room={RoomCode} region={Region} endpoint={ServerUrl}");
    }

    public string RoomCode { get; }
    public string Region { get; }
    public string ServerUrl { get; }
    public bool UsingMicrophone => _microphoneReady;
    public bool UsingSpeaker => _speakerReady;
    public bool Mute { get; private set; }
    public float LocalLevel => _localLevel;
    public bool LocalSpeaking => _localSpeaking;
    public int PeerCount => SnapshotPeers().Length;
    public IEnumerable<VoiceRemoteOverlayState> RemoteOverlayStates => SnapshotPeers()
        .Where(peer => peer.PlayerId != byte.MaxValue)
        .Select(peer => peer.ToOverlayState());

    public int CountMappedRemotePeers(VoiceGameStateSnapshot snapshot)
        => SnapshotPeers().Count(peer => snapshot.Players.Any(player =>
            !player.IsLocal &&
            !player.Disconnected &&
            !player.IsDummy &&
            player.PlayerId == peer.PlayerId));

    public void SetMute(bool mute)
    {
        if (Mute == mute) return;
        Mute = mute;
#if WINDOWS
        QueueMicrophoneTransition(!mute, mute ? "muted" : "unmuted");
#elif ANDROID
        if (mute) StopAndroidMicrophone("muted");
        else StartAndroidMicrophone("unmuted");
#elif MACOS
        if (!mute)
            RestartMacAudio("unmuted");
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
        lock (_captureFrameSync)
            _micPreprocessor.SetNoiseSuppressionEnabled(options.NoiseSuppressionEnabled);

#if WINDOWS
        if (restartCapture && !Mute && _microphoneReady)
            QueueMicrophoneTransition(true, "capture-options");
#elif ANDROID
        if (restartCapture && !Mute && _microphoneReady)
            StartAndroidMicrophone("capture-options");
#elif MACOS
        _micPreprocessor.SetNoiseSuppressionEnabled(false);
        if (restartCapture && _microphoneReady)
            RestartMacAudio("capture-options");
        if (options.NoiseSuppressionEnabled)
            VoiceDiagnostics.Log("mac.rnnoise", "state=disabled reason=macos-native-library-not-shipped windowsRnnoise=false");
#endif
        VoiceDiagnostics.Log("bcl.capture-options",
            $"capture={DescribeCaptureMode()} syntheticTone={options.SyntheticMicToneEnabled} noiseSuppression={options.NoiseSuppressionEnabled} calibration={options.MicCalibrationDiagnostics} sensitivity={options.MicSensitivity:0.00}");
    }

    public void SetMicrophone(string deviceName, float volume)
    {
        _lastMicDeviceName = deviceName ?? string.Empty;
        _micVolume = Mathf.Clamp(volume, 0f, 2f);
#if WINDOWS
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
#elif MACOS
        RestartMacAudio("settings");
#else
        _microphoneReady = false;
#endif
    }

#if MACOS
    private void RestartMacAudio(string reason)
    {
        if (_disposed) return;
        if (!_macSpeakerConfigured)
        {
            VoiceDiagnostics.Log("mac.bcl.audio", $"ready=false stage=waiting-for-speaker reason={reason} input=\"{MacOsAudioDeviceSelection.Describe(_lastMicDeviceName)}\"");
            return;
        }

        try
        {
            StopSyntheticMicTone();
            _macAudioEngine ??= new MacOsFullDuplexAudioEngine("BetterCrewLink");
            var result = _macAudioEngine.Start(
                new MacOsVoiceAudioConfig(
                    "BetterCrewLink",
                    _lastMicDeviceName,
                    _lastSpeakerDeviceName,
                    AudioHelpers.ClockRate,
                    AudioHelpers.Channels,
                    2),
                OnMacMicrophoneData,
                ReadMacPlayback);

            _microphoneReady = result.Success;
            _speakerReady = result.Success;
            if (result.Success)
            {
                VoiceDiagnostics.Log("mac.bcl.audio",
                    $"ready=true reason={reason} sampleRate={AudioHelpers.ClockRate} inputChannels={AudioHelpers.Channels} outputChannels=2 input=\"{MacOsAudioDeviceSelection.Describe(_lastMicDeviceName)}\" output=\"{MacOsAudioDeviceSelection.Describe(_lastSpeakerDeviceName)}\" nativeBackend=CoreAudio");
            }
            else
            {
                VoiceDiagnostics.Log("mac.bcl.audio",
                    $"ready=false reason={reason} stage={result.Stage} code={result.Code} error=\"{result.Message}\"");
            }
        }
        catch (Exception ex)
        {
            StopMacAudio($"failed:{reason}");
            VoiceDiagnostics.Log("mac.bcl.audio", $"ready=false reason={reason} stage=managed-start error=\"{ex.Message}\"");
        }
    }

    private void StopMacAudio(string reason)
    {
        var hadAudio = _microphoneReady || _speakerReady || _macAudioEngine != null;
        try { _macAudioEngine?.Stop(reason); } catch { }
        _microphoneReady = false;
        _speakerReady = false;
        lock (_captureFrameSync)
            _captureFrameSamples = 0;
        _micPreprocessor.Reset();
        _localLevel = 0f;
        _localSpeaking = false;
        if (hadAudio)
            VoiceDiagnostics.Log("mac.bcl.audio", $"ready=false reason={reason} callbacks={Volatile.Read(ref _micCallbacks)} bytes={Volatile.Read(ref _micBytes)} samples={Volatile.Read(ref _micSamples)}");
    }

    private void OnMacMicrophoneData(float[] buffer, int samples)
    {
        if (_disposed || buffer.Length == 0) return;
        Interlocked.Increment(ref _micCallbacks);
        samples = Math.Min(Math.Max(samples, 0), buffer.Length);
        Interlocked.Add(ref _micBytes, samples * sizeof(float));
        if (Mute)
        {
            Interlocked.Increment(ref _micMutedDrops);
            return;
        }
        if (samples <= 0) return;
        for (var i = 0; i < samples; i++)
            buffer[i] = Math.Clamp(buffer[i] * _micVolume, -1f, 1f);
        Interlocked.Add(ref _micSamples, samples);
        ProcessMicrophoneCaptureSamples(buffer, samples);
    }

    private int ReadMacPlayback(float[] buffer, int count)
        => _playbackProvider.Read(buffer, 0, count);
#endif

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
            var captureKind = "wavein";
            var captureDevice = "mapper";
            int waveInDevice = -1;
            if (!_captureOptions.SyntheticMicToneEnabled)
            {
                waveInDevice = ResolveWaveInDevice(_lastMicDeviceName);
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
        lock (_captureFrameSync)
        {
            _captureFrameSamples = 0;
        }
        _micPreprocessor.Reset();
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
            _captureFrameSamples = 0;
        }
        _micPreprocessor.Reset();
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
        ProcessMicrophoneCaptureSamples(buffer, samples);
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
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            var outputDevice = ResolveWaveOutDevice(deviceName);
            _waveOut = new WaveOutEvent
            {
                DeviceNumber = outputDevice,
                DesiredLatency = BclPlaybackLatencyMs,
                NumberOfBuffers = 3,
            };
            _waveOut.Init(_playbackProvider.ToWaveProvider());
            _waveOut.Play();
            _speakerReady = _waveOut.PlaybackState == PlaybackState.Playing;
            VoiceDiagnostics.Log("bcl.speaker", $"ready={_speakerReady} device=\"{deviceName}\" outputDevice=\"{DescribeWaveOutDevice(outputDevice)}\" outputDeviceNumber={outputDevice} sourceFormat=\"left={_leftPlayback.Endpoint.WaveFormat};right={_rightPlayback.Endpoint.WaveFormat}\" graphFormat=\"{_playbackProvider.WaveFormat}\" outputDevices=\"{DescribeWaveOutDevices()}\" latencyMs={BclPlaybackLatencyMs} splitMonoGraphs=true");
        }
        catch (Exception ex)
        {
            try { _waveOut?.Stop(); } catch { }
            try { _waveOut?.Dispose(); } catch { }
            _waveOut = null;
            _speakerReady = false;
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
#elif MACOS
        _lastSpeakerDeviceName = deviceName ?? string.Empty;
        _macSpeakerConfigured = true;
        RestartMacAudio("speaker");
#else
        _speakerReady = false;
#endif
#endif
    }

    public void UpdateProfile(byte playerId, string playerName)
    {
        _lastPlayerId = playerId;
        _lastPlayerName = string.IsNullOrWhiteSpace(playerName) ? "Unknown" : playerName;
    }

    public void SendRadioState(byte playerId, bool active)
    {
        if (_lastLocalRadioActive == active) return;
        _lastLocalRadioActive = active;
        SendCustomMessage([RadioStateMagic[0], RadioStateMagic[1], RadioStateMagic[2], RadioStateMagic[3], playerId, active ? (byte)1 : (byte)0]);
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

    public void ApplyRemoteRadioState(byte playerId, bool active)
    {
        foreach (var peer in SnapshotPeers())
        {
            if (peer.PlayerId == playerId)
            {
                peer.RadioActive = active;
                VoiceDiagnostics.Log("bcl.radio.rx", $"client={peer.ClientId} player={playerId} active={active}");
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
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); } catch (Exception ex) { VoiceDiagnostics.Log("bcl.error", $"stage=mainThread error=\"{ex.Message}\""); }
        }

#if ANDROID
        _androidMicrophone?.Tick();
#endif

        if (snapshot == null)
        {
            foreach (var peer in SnapshotPeers()) peer.MuteAll();
            MaybeLogStats(snapshot, "no-snapshot");
            return;
        }

        _ = JoinAsync(snapshot);
        RetryClosedDataChannels();

        var localPlayer = snapshot.TryGetLocalPlayer(out var local) ? local : (VoicePlayerSnapshot?)null;
        var listenerPos = localPlayer?.Position;
        bool inLobby = snapshot.Phase == VoiceGamePhase.Lobby;
        bool inMeeting = snapshot.Phase == VoiceGamePhase.Meeting;
        bool inTask = snapshot.Phase == VoiceGamePhase.Tasks;

        foreach (var peer in SnapshotPeers())
        {
            var target = FindTarget(snapshot, peer);
            if (target.HasValue && !target.Value.Disconnected && !target.Value.IsDummy && peer.UpdateProfile(target.Value.PlayerId, target.Value.PlayerName))
                ApplySavedVolume(peer);

            VoiceProximityResult result;
            if (inLobby || !inTask && !inMeeting)
                result = VoiceProximityCalculator.CalculateLobby(target, listenerPos);
            else if (inMeeting)
                result = VoiceProximityCalculator.CalculateMeeting(localPlayer, target, peer.RadioActive);
            else
                result = VoiceProximityCalculator.CalculateTaskPhase(localPlayer, target, listenerPos, snapshot.LocalLightRadius, snapshot.MapId, snapshot.CameraViewActive, snapshot.ActiveCameraIndex, snapshot.ActiveCameraPosition, speakerCache, virtualMicrophones, localInVent, peer.RadioActive, commsSabActive, peer.WallCoefficient);

            peer.Apply(result);
            if (!peer.TryFlushBufferedVoice(out var flushError, out var flushedFrames))
            {
                if (!string.IsNullOrEmpty(flushError))
                {
                    Interlocked.Increment(ref _audioDecodeFailures);
                    VoiceDiagnostics.Log("bcl.audio.drop", $"client={peer.ClientId} bytes=0 error=\"{flushError}\"");
                }
            }
            else if (flushedFrames > 0)
            {
                Interlocked.Add(ref _encodedRx, flushedFrames);
            }
            peer.SampleDiagnostics();
        }

        MaybeLogStats(snapshot, "ok");
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
#elif MACOS
        StopMacAudio("dispose");
        try { _macAudioEngine?.Dispose(); } catch { }
        _macAudioEngine = null;
#endif
        _micPreprocessor.Dispose();
        try { _socket?.DisconnectAsync(); } catch { }
        ClearPeers();
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
            }
            await Task.CompletedTask;
        });
        _socket.On("VAD", async _ => await Task.CompletedTask);
        _ = _socket.ConnectAsync();
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
        lock (_peerSync)
        {
            changed = !_socketToClient.TryGetValue(socketId, out var oldClientId) || oldClientId != client.clientId;
            _clientToSocket[client.clientId] = socketId;
            _socketToClient[socketId] = client.clientId;
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

    private void RetryClosedDataChannels()
    {
        var now = DateTime.UtcNow;
        if (now - _lastOfferRetryUtc < OfferRetryInterval) return;
        var retryPeers = SnapshotPeers()
            .Where(peer => IsRetryableDataChannelState(peer.DataChannel?.readyState) && ShouldInitiateOffer(peer.SocketId))
            .ToArray();
        if (retryPeers.Length == 0) return;
        _lastOfferRetryUtc = now;
        foreach (var peer in retryPeers)
        {
            VoiceDiagnostics.Log("bcl.offer", $"reason=retry socket={peer.SocketId} client={peer.ClientId} state={peer.DataChannel?.readyState.ToString() ?? "none"}");
            _ = StartOfferAsync(peer.SocketId);
        }
    }

    private PeerConnection? EnsurePeer(string socketId)
    {
        lock (_peerSync)
        {
            if (IsLocalSocket(socketId)) return null;
            if (_peersBySocket.TryGetValue(socketId, out var existing)) return existing;
            var clientId = _socketToClient.TryGetValue(socketId, out var mappedClientId) ? mappedClientId : -1;
            var playbackGroupId = clientId >= 0 ? clientId : _nextProvisionalPeerGroupId--;
            var leftRoute = _leftPlayback.Generate(playbackGroupId);
            var rightRoute = _rightPlayback.Generate(playbackGroupId);
            var peer = new PeerConnection(socketId, clientId, playbackGroupId, leftRoute, rightRoute);
            var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = _iceServers });
            peer.Connection = pc;
            pc.ondatachannel += dc =>
            {
                lock (_peerSync)
                    peer.DataChannel = dc;
                dc.onopen += () => VoiceDiagnostics.Log("bcl.channel", $"socket={socketId} client={peer.ClientId} state=open inbound=true");
                dc.onmessage += (_, _, data) => OnDataChannelMessage(peer, data);
            };
            pc.onicecandidate += candidate =>
            {
                if (candidate == null || _socket == null) return;
                var signalData = JsonSerializer.Serialize(new { candidate = candidate.candidate, sdpMid = candidate.sdpMid, sdpMLineIndex = candidate.sdpMLineIndex });
                _ = _socket.EmitAsync("signal", new object[] { new { to = socketId, data = signalData } });
            };
            _peersBySocket[socketId] = peer;
            VoiceDiagnostics.Log("bcl.peer.created", $"socket={socketId} client={clientId} playbackGroup={playbackGroupId} provisional={(clientId < 0).ToString().ToLowerInvariant()}");
            VoiceDiagnostics.Log("bcl.peer-connected", $"socket={socketId} client={clientId}");
            return peer;
        }
    }

    private async Task StartOfferAsync(string socketId)
    {
        PeerConnection? peer;
        SocketIOClient.SocketIO? socket;
        lock (_peerSync)
        {
            if (!_peersBySocket.TryGetValue(socketId, out peer)) return;
            if (!ShouldInitiateOffer(socketId)) return;
            if (!IsRetryableDataChannelState(peer.DataChannel?.readyState)) return;
            socket = _socket;
        }
        if (socket == null || peer.Connection == null) return;
        VoiceDiagnostics.Log("bcl.offer", $"reason=start socket={socketId} client={peer.ClientId} state={peer.DataChannel?.readyState.ToString() ?? "none"}");
        var channel = await peer.Connection.createDataChannel("audio", new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
        lock (_peerSync)
            peer.DataChannel = channel;
        channel.onopen += () => VoiceDiagnostics.Log("bcl.channel", $"socket={socketId} client={peer.ClientId} state=open inbound=false");
        channel.onmessage += (_, _, data) => OnDataChannelMessage(peer, data);
        var offer = peer.Connection.createOffer(null);
        await peer.Connection.setLocalDescription(offer);
        var sdpJson = JsonSerializer.Serialize(new { type = "offer", sdp = offer.sdp });
        await socket.EmitAsync("signal", new object[] { new { to = socketId, data = sdpJson } });
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
        if (signal.Kind == "offer")
        {
            peer.Connection.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = signal.Sdp });
            var answer = peer.Connection.createAnswer(null);
            await peer.Connection.setLocalDescription(answer);
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
        if (data.Length >= DataControlPrefixLength && DataControlPrefix.SequenceEqual(data.Take(DataControlPrefixLength)))
        {
            var payload = data.Skip(DataControlPrefixLength).ToArray();
            if (TryHandleRadioState(payload)) return;
            Interlocked.Increment(ref _customRx);
            CustomMessageReceived?.Invoke(new VoiceBackendCustomMessage(payload, peer.ClientId, peer.PlayerId, peer.SocketId));
            return;
        }

        if (TryHandleRadioState(data)) return;

        if (!peer.TryReceiveVoicePacket(data, out var error, out var decodedFrames))
        {
            if (!string.IsNullOrEmpty(error))
            {
                Interlocked.Increment(ref _audioDecodeFailures);
                VoiceDiagnostics.Log("bcl.audio.drop", $"client={peer.ClientId} bytes={data.Length} error=\"{error}\"");
            }
            return;
        }
        if (decodedFrames > 0)
            Interlocked.Add(ref _encodedRx, decodedFrames);
    }

    private bool TryHandleRadioState(byte[] payload)
    {
        if (payload.Length != 6
            || payload[0] != RadioStateMagic[0]
            || payload[1] != RadioStateMagic[1]
            || payload[2] != RadioStateMagic[2]
            || payload[3] != RadioStateMagic[3])
        {
            return false;
        }

        var playerId = payload[4];
        var active = payload[5] != 0;
        ApplyRemoteRadioState(playerId, active);
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
        var format = (sender as IWaveIn)?.WaveFormat ?? _waveIn?.WaveFormat;
        if (format == null) return;
        var samples = ConvertMicrophoneBufferToMonoFloat(buffer, recordedBytes, format, out var floatPcm);
        if (samples <= 0) return;
        Interlocked.Add(ref _micSamples, samples);
        ProcessMicrophoneCaptureSamples(floatPcm, samples);
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
        floatPcm = new float[frames];
        var dominantChannel = SelectDominantIeeeFloat32Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * sizeof(float);
            floatPcm[frame] = Math.Clamp(ReadIeeeFloat32Sample(buffer, offset) * _micVolume, -1f, 1f);
        }
        return frames;
    }

    private int ConvertPcm16ToMonoFloat(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        var frames = recordedBytes / (sizeof(short) * channels);
        floatPcm = new float[frames];
        var dominantChannel = SelectDominantPcm16Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * sizeof(short);
            var sample = BitConverter.ToInt16(buffer, offset) / (float)short.MaxValue;
            floatPcm[frame] = Math.Clamp(sample * _micVolume, -1f, 1f);
        }
        return frames;
    }

    private int ConvertPcm24ToMonoFloat(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        const int bytesPerSample = 3;
        var frames = recordedBytes / (bytesPerSample * channels);
        floatPcm = new float[frames];
        var dominantChannel = SelectDominantPcm24Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * bytesPerSample;
            var sample = ReadPcm24Sample(buffer, offset);
            floatPcm[frame] = Math.Clamp(sample * _micVolume, -1f, 1f);
        }
        return frames;
    }

    private int ConvertPcm32ToMonoFloat(byte[] buffer, int recordedBytes, int channels, out float[] floatPcm)
    {
        var frames = recordedBytes / (sizeof(int) * channels);
        floatPcm = new float[frames];
        var dominantChannel = SelectDominantPcm32Channel(buffer, frames, channels);
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = (frame * channels + dominantChannel) * sizeof(int);
            var sample = BitConverter.ToInt32(buffer, offset) / 2147483648f;
            floatPcm[frame] = Math.Clamp(sample * _micVolume, -1f, 1f);
        }
        return frames;
    }

    private static int SelectDominantIeeeFloat32Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var bestChannel = 0;
        var bestEnergy = 0.0;
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = (frame * channels + channel) * sizeof(float);
                var sample = ReadIeeeFloat32Sample(buffer, offset);
                energy += (double)sample * sample;
            }
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestChannel = channel;
            }
        }
        return bestChannel;
    }

    private static int SelectDominantPcm16Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var bestChannel = 0;
        var bestEnergy = 0.0;
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = (frame * channels + channel) * sizeof(short);
                var sample = BitConverter.ToInt16(buffer, offset) / (float)short.MaxValue;
                energy += (double)sample * sample;
            }
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestChannel = channel;
            }
        }
        return bestChannel;
    }

    private static int SelectDominantPcm24Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var bestChannel = 0;
        var bestEnergy = 0.0;
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
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestChannel = channel;
            }
        }
        return bestChannel;
    }

    private static int SelectDominantPcm32Channel(byte[] buffer, int frames, int channels)
    {
        if (channels <= 1) return 0;
        var bestChannel = 0;
        var bestEnergy = 0.0;
        for (var channel = 0; channel < channels; channel++)
        {
            var energy = 0.0;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = (frame * channels + channel) * sizeof(int);
                var sample = BitConverter.ToInt32(buffer, offset) / 2147483648f;
                energy += (double)sample * sample;
            }
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestChannel = channel;
            }
        }
        return bestChannel;
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

                var frame = new float[AudioHelpers.FrameSize];
                Array.Copy(_captureFrameBuffer, frame, frame.Length);
                _captureFrameSamples = 0;
                ProcessMicrophoneFrameLocked(frame, frame.Length, "capture");
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

        if (_captureOptions.NoiseSuppressionEnabled && !IsSyntheticSource(source))
            _micPreprocessor.TryApplyNoiseSuppression(floatPcm, samples);

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

        var decision = _micPreprocessor.PrepareFrameForEncode(floatPcm, samples, _noiseGateThreshold, _vadThreshold);
        _lastGateReason = decision.Reason;
        _lastGatePeak = decision.Peak;
        _lastGateRms = decision.Rms;
        _lastGateThreshold = decision.Threshold;

        if (_captureOptions.MicCalibrationDiagnostics)
            MaybeLogMicCalibration(source, decision, false, speakingThreshold, nearClipSamples, zeroCrossings, samples);

        var frameTimestamp = _sendTimestamp;
        unchecked { _sendTimestamp += (uint)samples; }

        var transmitGain = 1f;
        var transmitPeak = 0f;
        double transmitSquareSum = 0.0;
        var pcm = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            var scaled = Math.Clamp(floatPcm[i] * transmitGain, -1f, 1f);
            var pcmSample = (short)(scaled * short.MaxValue);
            pcm[i] = pcmSample;
            transmitSquareSum += (double)(scaled * short.MaxValue) * (scaled * short.MaxValue);
            var abs = Math.Abs(scaled);
            if (abs > transmitPeak) transmitPeak = abs;
        }
        _lastTransmitGain = transmitGain;
        _lastTransmitPeak = transmitPeak;

        var packet = new byte[1024];
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
        var trimmed = new byte[encoded];
        Array.Copy(packet, trimmed, encoded);
        var voiceFlags = BclVoicePacketFlags.None;
        if (_lastLocalRadioActive) voiceFlags |= BclVoicePacketFlags.Radio;
        if (BclOpusUseInbandFec) voiceFlags |= BclVoicePacketFlags.LossResistant;
        if (IsSyntheticSource(source)) voiceFlags |= BclVoicePacketFlags.Synthetic;
        var framed = BclVoicePacket.Wrap(trimmed, _sendSequence++, frameTimestamp, (ushort)samples, voiceFlags, BclVoicePacket.QuantizeLevel(transmitPeak));
        var sent = false;
        foreach (var channel in SnapshotOpenChannels())
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

    private void MaybeLogMicCalibration(string source, MicFrameDecision decision, bool bypassGate, float speakingThreshold, int nearClipSamples, int zeroCrossings, int samples)
    {
        var now = DateTime.UtcNow;
        if (now - _lastMicCalibrationLogUtc < MicCalibrationLogInterval) return;
        _lastMicCalibrationLogUtc = now;
        var zeroCrossRate = samples <= 1 ? 0f : zeroCrossings / (float)(samples - 1);
        var crest = decision.Rms <= 0f ? 0f : decision.Peak / decision.Rms;
        VoiceDiagnostics.Log("bcl.mic.calibration",
            $"source={source} peak={decision.Peak:0.000000} rms={decision.Rms:0.000000} crest={crest:0.00} nearClipSamples={nearClipSamples} zeroCrossRate={zeroCrossRate:0.0000} gateThreshold={decision.Threshold:0.000000} vadThreshold={_vadThreshold:0.000000} effectiveSpeakingThreshold={speakingThreshold:0.000000} reason={decision.Reason} bypass={bypassGate} syntheticFrames={Volatile.Read(ref _syntheticFrames)}");
    }

    private PeerConnection[] SnapshotPeers()
    {
        lock (_peerSync)
            return _peersBySocket.Values.ToArray();
    }

    private string[] SnapshotMappedSocketIds()
    {
        lock (_peerSync)
            return _clientToSocket.Values.ToArray();
    }

    private RTCDataChannel[] SnapshotOpenChannels()
    {
        lock (_peerSync)
            return _peersBySocket.Values
                .Select(peer => peer.DataChannel)
                .Where(channel => channel?.readyState == RTCDataChannelState.open)
                .Cast<RTCDataChannel>()
                .ToArray();
    }

    private void RemovePeer(string socketId)
    {
        PeerConnection? peer;
        lock (_peerSync)
        {
            if (!_peersBySocket.Remove(socketId, out peer)) return;
            if (_socketToClient.TryGetValue(socketId, out var clientId))
                _clientToSocket.Remove(clientId);
            _socketToClient.Remove(socketId);
            _pendingSignalsBySocket.Remove(socketId);
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
            $"peers={peers.Length} openChannels={openChannels} localSocket={GetEffectiveLocalSocketId()} joinedClient={_joinedClientId} joinInFlight={Volatile.Read(ref _joinInFlight)} joinRetryAgeMs={(DateTime.UtcNow - _lastJoinAttemptUtc).TotalMilliseconds:0} audible={peers.Count(peer => peer.CurrentRoute.Audible)} speaking={peers.Count(peer => peer.IsSpeaking)} " +
            $"localLevel={LocalLevel:0.000} localSpeaking={LocalSpeaking} mute={Mute} remoteLevelMax={remoteMax:0.000} " +
            $"audibleTicks={audibleTicks} audibleSilentTicks={audibleSilentTicks} silentPct={silentPct:0.0} peerWindows={peerWindows} peerJitter={peerJitter} peerBuffers={peerBuffers} " +
            $"encodedTx={Volatile.Read(ref _encodedTx)} encodedRx={Volatile.Read(ref _encodedRx)} customTx={Volatile.Read(ref _customTx)} customRx={Volatile.Read(ref _customRx)} " +
            $"micCallbacks={Volatile.Read(ref _micCallbacks)} micBytes={Volatile.Read(ref _micBytes)} micSamples={Volatile.Read(ref _micSamples)} micWindowSamples={micWindowSamples} micPeak={micPeak:0.000000} micRms={micRms:0.000000} micCrest={micCrest:0.00} micNonZeroSamples={micNonZeroSamples} micSilentCallbacks={micSilentCallbacks} micNearClipSamples={micNearClipSamples} micClipPct={micClipPct:0.000} micZeroCrossRate={micZeroCrossRate:0.0000} " +
            $"micMutedDrops={Volatile.Read(ref _micMutedDrops)} micEncodeFailures={Volatile.Read(ref _micEncodeFailures)} micEncodedFrames={Volatile.Read(ref _micEncodedFrames)} micNoOpenChannelDrops={Volatile.Read(ref _micNoOpenChannelDrops)} audioDecodeFailures={Volatile.Read(ref _audioDecodeFailures)} " +
            $"noiseGate={noiseGateThreshold:0.000000} vadThreshold={vadThreshold:0.000000} gateReason={_lastGateReason} gatePeak={_lastGatePeak:0.000000} gateRms={_lastGateRms:0.000000} gateThreshold={_lastGateThreshold:0.000000} txGain={_lastTransmitGain:0.000} txPeak={_lastTransmitPeak:0.000000} txPeakMax={txPeakMax:0.000000} txRms={txRms:0.000000} txSamples={txSamples} opusBytesAvg={opusAvgBytes:0.0} opusBytesMin={opusMinBytes} opusBytesMax={opusMaxBytes} " +
            $"syntheticTone={_captureOptions.SyntheticMicToneEnabled} noiseSuppression={_captureOptions.NoiseSuppressionEnabled} {rnnoiseSummary} syntheticFrames={Volatile.Read(ref _syntheticFrames)} capture={DescribeCaptureMode()} calibration={_captureOptions.MicCalibrationDiagnostics} sensitivity={_captureOptions.MicSensitivity:0.00} micReady={_microphoneReady} speakerReady={_speakerReady}");
        if (_captureOptions.NoiseSuppressionEnabled || rnnoise.Attempts > 0 || rnnoise.UnavailableFrames > 0)
            LogNoiseSuppressionStats(rnnoiseSummary);
#if MACOS
        if (_macAudioEngine != null)
            MacOsAudioDiagnostics.LogStats("BetterCrewLink", _macAudioEngine.GetDiagnosticsSnapshot());
#endif
    }

    private static void LogNoiseSuppressionStats(string message)
    {
        VoiceDiagnostics.Log("bcl.rnnoise.stats", message);
        try
        {
            global::VoiceChatPlugin.VoiceChatPluginMain.Logger.LogInfo("[VC] bcl.rnnoise.stats " + message);
        }
        catch
        {
        }
    }

    private string DescribeCaptureMode()
    {
        if (_captureOptions.SyntheticMicToneEnabled) return "synthetic";
#if ANDROID
        return "android-unity-microphone";
#elif MACOS
        return "macos-coreaudio";
#else
        return "wavein-only";
#endif
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
        encoder.UseDTX = false;
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
        if (peer.PlayerId != byte.MaxValue && snapshot.TryGetPlayer(peer.PlayerId, out var byPlayer)) return byPlayer;
        if (snapshot.TryGetClient(peer.ClientId, out var byClient)) return byClient;
        return null;
    }

    private sealed class PeerConnection : IDisposable
    {
        private readonly object _sync = new();
        private readonly BclPeerPlaybackRoute _leftRoute;
        private readonly BclPeerPlaybackRoute _rightRoute;
        private readonly BclVoiceJitterBuffer _jitterBuffer = new(targetDelayFrames: BclJitterTargetDelayFrames, maxBufferedFrames: BclJitterMaxBufferedFrames);
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
        private bool _disposed;

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
            _tailFlushTimer = new Timer(static state => ((PeerConnection)state!).FlushBufferedVoiceFromTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            MuteAll();
        }

        public string SocketId { get; }
        public int ClientId { get; private set; }
        public int PlaybackGroupId { get; }
        public RTCPeerConnection? Connection { get; set; }
        public RTCDataChannel? DataChannel { get; set; }
#pragma warning disable CS0618
        public OpusDecoder Decoder { get; } = new(AudioHelpers.ClockRate, AudioHelpers.Channels);
#pragma warning restore CS0618
        public byte PlayerId { get; private set; } = byte.MaxValue;
        public string PlayerName { get; private set; } = "Unknown";
        public bool RadioActive { get; set; }
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
            PlayerId = playerId;
            PlayerName = normalized;
            return true;
        }

        public void ResetMappingNoMute() => PlayerId = byte.MaxValue;
        public void SetVolume(float volume)
        {
            var clamped = Mathf.Clamp(volume, 0f, 2f);
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
            var wasAudible = _currentRoute.Audible;
            _currentRoute = result;
            WallCoefficient = result.WallCoefficient;
            _appliedPan = result.Pan;
            var clearBufferedAudio = !result.Audible || !wasAudible && result.Audible;
            var bufferedSamples = clearBufferedAudio ? Math.Max(_leftRoute.BufferedSamples, _rightRoute.BufferedSamples) : 0;
            if (bufferedSamples > 0)
            {
                _leftRoute.ClearBufferedSamples();
                _rightRoute.ClearBufferedSamples();
                _routeClearsSinceStats++;
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
                    return DecodeLegacyPacket(data, out error, out decodedFrames);

                RadioActive = packet.Flags.HasFlag(BclVoicePacketFlags.Radio);
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
                    var frameSize = Math.Max(AudioHelpers.FrameSize, (int)frame.Duration);
                    if (!DecodeAndAddSamples(payload, decodeFec, frameSize, out error, out var decoded))
                        return false;
                    decodedFrames += decoded > 0 ? 1 : 0;
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
                    var payload = frame.Packet?.Payload ?? Array.Empty<byte>();
                    var decodeFec = frame.Kind == BclVoicePlayoutKind.Fec;
                    var frameSize = Math.Max(AudioHelpers.FrameSize, (int)frame.Duration);
                    if (!DecodeAndAddSamples(payload, decodeFec, frameSize, out error, out var decoded))
                        return false;
                    decodedFrames += decoded > 0 ? 1 : 0;
                }

                return true;
            }
        }

        private void ScheduleTailFlushLocked()
        {
            if (_disposed) return;
            try { _tailFlushTimer.Change(BclQuietTailFlushTimerDelay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        private void FlushBufferedVoiceFromTimer()
        {
            if (!TryFlushBufferedVoice(out var error, out _) && !string.IsNullOrEmpty(error))
                VoiceDiagnostics.Log("bcl.audio.drop", $"client={ClientId} bytes=0 error=\"{error}\" source=tail-timer");
        }

        private bool DecodeLegacyPacket(byte[] data, out string? error, out int decodedFrames)
        {
            _jitterBuffer.CountLegacyPacket();
            return DecodeAndAddSamples(data, false, AudioHelpers.FrameSize, out error, out decodedFrames);
        }

        private bool DecodeAndAddSamples(byte[] data, bool decodeFec, int frameSize, out string? error, out int decodedFrames)
        {
            error = null;
            decodedFrames = 0;

            var pcm = new short[Math.Max(AudioHelpers.FrameSize * 2, frameSize * AudioHelpers.Channels)];
            int decoded;
            try
            {
                decoded = Decoder.Decode(data.AsSpan(0, data.Length), pcm.AsSpan(0, pcm.Length), frameSize, decodeFec);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (decoded <= 0) return false;
            var samples = new float[decoded];
            var peak = 0f;
            for (var i = 0; i < decoded; i++)
            {
                var sample = pcm[i] / (float)short.MaxValue;
                samples[i] = sample;
                var abs = Math.Abs(sample);
                if (abs > peak) peak = abs;
            }
            ObserveVoiceLevel(peak);
            _leftRoute.AddSamples(samples, 0, samples.Length);
            _rightRoute.AddSamples(samples, 0, samples.Length);
            decodedFrames = 1;
            return true;
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
        public static DecodedSignal Session(string kind, string sdp)
            => new(kind, sdp, null, null, 0);

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
