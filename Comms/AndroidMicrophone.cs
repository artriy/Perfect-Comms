#if ANDROID || WINDOWS
#if STARLIGHT
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class AndroidMicrophone : IDisposable, ICaptureSource
{
    private const int OutputSampleRate = 48_000;
    private const int RequestedBufferFrames = 960;
    private const int HostRingMilliseconds = 120;
    private const int HostReadMilliseconds = 10;
    private const int MaterialGapMilliseconds = 20;
    private const int StallTimeoutMs = 1_500;
    private const int InitialRetryDelayMs = 250;
    private const int MaxRetryDelayMs = 30_000;
    private static readonly long DeadAfterTicks = 15L * Stopwatch.Frequency;
    private static readonly ExclusiveCaptureLease CaptureLease = new();
    private static long _nextCaptureOwner;

    private readonly long _captureOwner = Interlocked.Increment(ref _nextCaptureOwner);
    private string _device = "";
    private bool _recording;
    private bool _physicalCaptureNeedsStop;
    private bool _captureRequested;
    private bool _retryLeaseWhenUnavailable;
    private float _volume = 1f;
    private long _lastAdvanceTicks;
    private long _lastPollTicks;
    private long _lastProgressMs;
    private long _nextRecoveryMs;
    private int _recoveryAttempt;
    private int _sourceSampleRate = OutputSampleRate;
    private int _hostBufferFrames;
    private float[] _nativeReadBuffer = Array.Empty<float>();
    private float[] _nativeDrainBuffer = Array.Empty<float>();
    private float[] _resampledBuffer = Array.Empty<float>();
    private float[] _pollBuf = Array.Empty<float>();
    private StarlightCaptureResampler? _resampler;

    public event Action<float[], int>? DataAvailable;
    public event Action<int>? SamplesDropped;
    public event Action<float[], int>? OnFrame;

    public bool ReuseBuffer { get; set; }
    public bool IsCapturing => _recording && IsNativeCaptureRunning();
    public bool CaptureRequested => _captureRequested;
    public bool LeaseUnavailable { get; private set; }

    public void SetVolume(float value) => _volume = Math.Clamp(value, 0f, 4f);

    public bool Start(string deviceName, bool retryLeaseWhenUnavailable = false)
    {
        Stop();

        _device = string.Empty;
        _captureRequested = true;
        _retryLeaseWhenUnavailable = retryLeaseWhenUnavailable;
        _recoveryAttempt = 0;
        _nextRecoveryMs = 0;
        if (!CaptureLease.TryAcquire(_captureOwner))
        {
            LeaseUnavailable = true;
            VoiceDiagnostics.Log(
                "voice.starlight.capture.lease",
                $"acquired=false device=\"{DescribeDevice()}\" reason=already-owned retry={retryLeaseWhenUnavailable.ToString().ToLowerInvariant()}");
            if (retryLeaseWhenUnavailable)
                ScheduleRecovery(Environment.TickCount64, "capture lease unavailable");
            else
                _captureRequested = false;
            return false;
        }

        LeaseUnavailable = false;
        return TryStartCapture(Environment.TickCount64, "initial");
    }

    public void Stop()
    {
        _captureRequested = false;
        EndCurrentCapture(releaseLease: true);
        _recording = false;
        _nativeReadBuffer = Array.Empty<float>();
        _nativeDrainBuffer = Array.Empty<float>();
        _resampledBuffer = Array.Empty<float>();
        _resampler = null;
        _nextRecoveryMs = 0;
        _recoveryAttempt = 0;
        _retryLeaseWhenUnavailable = false;
        LeaseUnavailable = false;
    }

    public void Tick()
    {
        if (!_captureRequested) return;
        if (!_recording)
        {
            MaybeRetryCapture();
            return;
        }

        if (!IsNativeCaptureRunning())
        {
            RecoverFromReadFailure("host capture is not running");
            return;
        }

        var pollTicks = Stopwatch.GetTimestamp();
        var elapsedTicks = Math.Max(0, pollTicks - _lastPollTicks);
        _lastPollTicks = pollTicks;
        int delivered = 0;
        int drainLimit = _nativeDrainBuffer.Length;

        try
        {
            while (delivered < drainLimit)
            {
                int capacity = Math.Min(_nativeReadBuffer.Length, drainLimit - delivered);
                if (capacity <= 0) break;
                int count = NativeCapture.ReadFloat(_nativeReadBuffer, capacity);
                if (count < 0)
                    throw new InvalidOperationException(
                        $"host read failed ({count}): {GetLastNativeError()}");
                if (count == 0) break;
                count = Math.Min(count, capacity);
                Array.Copy(
                    _nativeReadBuffer,
                    0,
                    _nativeDrainBuffer,
                    delivered,
                    count);
                delivered += count;
            }

            if (delivered > 0)
            {
                int droppedSourceSamples = InferMaterialGapSamples(
                    elapsedTicks,
                    Stopwatch.Frequency,
                    _sourceSampleRate,
                    delivered);
                if (droppedSourceSamples > 0)
                {
                    _resampler?.Reset();
                    SamplesDropped?.Invoke(ScaleToOutputSamples(
                        droppedSourceSamples,
                        _sourceSampleRate));
                }

                PublishSourceSamples(_nativeDrainBuffer, delivered);
            }
        }
        catch (Exception ex)
        {
            RecoverFromReadFailure(ex.Message);
            return;
        }

        if (delivered <= 0)
        {
            MaybeRecoverFromStall();
            return;
        }

        Volatile.Write(ref _lastAdvanceTicks, pollTicks);
        _lastProgressMs = Environment.TickCount64;
        _recoveryAttempt = 0;
        _nextRecoveryMs = 0;
    }

    private void PublishSourceSamples(float[] source, int count)
    {
        if (count <= 0) return;

        if (_sourceSampleRate == OutputSampleRate)
        {
            Publish(source, count);
            return;
        }

        var resampler = _resampler;
        if (resampler == null) return;
        int required = resampler.GetMaximumOutputSamples(count);
        if (_resampledBuffer.Length < required)
            _resampledBuffer = new float[required];
        int outputCount = resampler.Convert(
            source.AsSpan(0, count),
            _resampledBuffer);
        Publish(_resampledBuffer, outputCount);
    }

    private void Publish(float[] source, int count)
    {
        if (count <= 0) return;
        float[] published;
        if (ReuseBuffer)
        {
            if (_pollBuf.Length < count)
                _pollBuf = new float[count];
            published = _pollBuf;
        }
        else
        {
            published = new float[count];
        }

        if (_volume == 1f)
        {
            Array.Copy(source, published, count);
        }
        else
        {
            for (int i = 0; i < count; i++)
                published[i] = source[i] * _volume;
        }

        DataAvailable?.Invoke(published, count);
        OnFrame?.Invoke(published, count);
    }

    private void MaybeRetryCapture()
    {
        var now = Environment.TickCount64;
        if (!ShouldAttemptRecovery(_captureRequested, _recording, now, _nextRecoveryMs))
            return;

        if (!CaptureLease.IsOwnedBy(_captureOwner))
        {
            if (!CaptureLease.TryAcquire(_captureOwner))
            {
                LeaseUnavailable = true;
                if (_retryLeaseWhenUnavailable)
                    ScheduleRecovery(now, "capture lease unavailable");
                else
                    _captureRequested = false;
                return;
            }

            LeaseUnavailable = false;
            _recoveryAttempt = 0;
            _nextRecoveryMs = 0;
            VoiceDiagnostics.Log(
                "voice.starlight.capture.lease",
                $"acquired=true device=\"{DescribeDevice()}\" reason=retry");
        }

        TryStartCapture(now, "retry");
    }

    private bool TryStartCapture(long now, string reason)
    {
        if (!CaptureLease.IsOwnedBy(_captureOwner))
        {
            LeaseUnavailable = true;
            if (_retryLeaseWhenUnavailable)
                ScheduleRecovery(now, "capture lease lost");
            else
                _captureRequested = false;
            return false;
        }

        int actualRate = -1;
        int hostBufferFrames = 0;
        string failure;
        try
        {
            if (!NativeCapture.HasRecordAudioPermission())
            {
                failure = "record audio permission is not granted";
            }
            else
            {
                _physicalCaptureNeedsStop = true;
                actualRate = NativeCapture.StartCapture(
                    OutputSampleRate,
                    RequestedBufferFrames);
                failure = actualRate > 0
                    ? string.Empty
                    : $"host start failed ({actualRate}): {GetLastNativeError()}";
                if (actualRate > 0)
                {
                    int reportedRate = NativeCapture.GetSampleRate();
                    if (reportedRate > 0)
                        actualRate = reportedRate;
                    hostBufferFrames = Math.Max(0, NativeCapture.GetBufferFrames());
                }
            }
        }
        catch (Exception ex)
        {
            actualRate = -1;
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (actualRate > 0 && IsSupportedSourceRate(actualRate))
        {
            _sourceSampleRate = actualRate;
            _hostBufferFrames = hostBufferFrames;
            _nativeReadBuffer = new float[
                SourceSamplesForMilliseconds(actualRate, HostRingMilliseconds)];
            _nativeDrainBuffer = new float[
                SourceSamplesForMilliseconds(
                    actualRate,
                    HostRingMilliseconds + HostReadMilliseconds)];
            _resampledBuffer = Array.Empty<float>();
            _resampler = actualRate == OutputSampleRate
                ? null
                : new StarlightCaptureResampler(actualRate);
            _lastProgressMs = now;
            _lastPollTicks = Stopwatch.GetTimestamp();
            Volatile.Write(ref _lastAdvanceTicks, _lastPollTicks);
            _recording = true;
            _nextRecoveryMs = 0;
            VoiceDiagnostics.Log(
                "voice.starlight.capture.restart",
                $"started device=\"{DescribeDevice()}\" reason={reason} attempt={_recoveryAttempt} rate={actualRate} bufferFrames={_hostBufferFrames}");
            return true;
        }

        if (actualRate > 0)
            failure = $"unsupported host sample rate {actualRate}";
        EndCurrentCapture(releaseLease: false);
        ScheduleRecovery(now, failure);
        return false;
    }

    private void RecoverFromReadFailure(string failure)
    {
        var now = Environment.TickCount64;
        VoiceDiagnostics.Log(
            "voice.starlight.capture.restart",
            $"read-failed device=\"{DescribeDevice()}\" attempt={_recoveryAttempt + 1} error=\"{SafeDiagnostic(failure)}\"");
        EndCurrentCapture(releaseLease: false);
        _recording = false;
        _resampler?.Reset();
        ScheduleRecovery(now, "read failed");
    }

    private void MaybeRecoverFromStall()
    {
        if (!_captureRequested || !_recording) return;
        long now = Environment.TickCount64;
        if (now - _lastProgressMs < StallTimeoutMs) return;

        VoiceDiagnostics.Log(
            "voice.starlight.capture.restart",
            $"stalled device=\"{DescribeDevice()}\" attempt={_recoveryAttempt + 1} stallMs={now - _lastProgressMs}");
        EndCurrentCapture(releaseLease: false);
        _recording = false;
        _resampler?.Reset();
        ScheduleRecovery(now, "stalled");
    }

    private void ScheduleRecovery(long now, string failure)
    {
        _recoveryAttempt = Math.Min(_recoveryAttempt + 1, 30);
        int delayMs = RecoveryDelayMilliseconds(
            _recoveryAttempt,
            InitialRetryDelayMs,
            MaxRetryDelayMs);
        _nextRecoveryMs = now + delayMs;
        VoiceDiagnostics.Log(
            "voice.starlight.capture.restart",
            $"retry-scheduled device=\"{DescribeDevice()}\" attempt={_recoveryAttempt} delayMs={delayMs} error=\"{SafeDiagnostic(failure)}\"");
    }

    private void EndCurrentCapture(bool releaseLease)
    {
        if (!CaptureLease.IsOwnedBy(_captureOwner)) return;

        try
        {
            if (_physicalCaptureNeedsStop)
                NativeCapture.StopCapture();
        }
        catch
        {
        }
        finally
        {
            _physicalCaptureNeedsStop = false;
            _recording = false;
            if (releaseLease)
                CaptureLease.Release(_captureOwner);
        }
    }

    bool ICaptureSource.Start(string? deviceId) => Start(deviceId ?? string.Empty);

    public CaptureHealth Health
        => ClassifyPosition(
            _recording,
            Stopwatch.GetTimestamp(),
            Volatile.Read(ref _lastAdvanceTicks),
            DeadAfterTicks);

    public void Dispose() => Stop();

    public static string[] GetDeviceNames() => Array.Empty<string>();

    internal static bool ShouldAttemptRecovery(
        bool requested,
        bool recording,
        long nowMs,
        long nextRecoveryMs)
        => requested && !recording && nowMs >= nextRecoveryMs;

    internal static bool ShouldRecoverDestroyedClip(bool recording, bool clipAvailable)
        => recording && !clipAvailable;

    internal static int RecoveryDelayMilliseconds(
        int attempt,
        int initialDelayMs,
        int maximumDelayMs)
    {
        var initial = Math.Max(1, initialDelayMs);
        var maximum = Math.Max(initial, maximumDelayMs);
        var shift = Math.Clamp(attempt - 1, 0, 20);
        var delay = (long)initial << shift;
        return (int)Math.Min(delay, maximum);
    }

    internal static int RecoveryDelayMilliseconds(
        int attempt,
        int initialDelayMs,
        int maximumDelayMs,
        int minimumDelayMs)
    {
        var effectiveMaximum = Math.Max(Math.Max(1, initialDelayMs), maximumDelayMs);
        var minimum = Math.Clamp(minimumDelayMs, 1, effectiveMaximum);
        return Math.Max(
            minimum,
            RecoveryDelayMilliseconds(attempt, initialDelayMs, maximumDelayMs));
    }

    public static CaptureHealth ClassifyPosition(
        bool recording,
        long now,
        long lastAdvanceTicks,
        long deadAfterTicks)
    {
        if (!recording) return CaptureHealth.Dead;
        return now - lastAdvanceTicks >= deadAfterTicks
            ? CaptureHealth.Dead
            : CaptureHealth.Healthy;
    }

    internal static int InferMaterialGapSamples(
        long elapsedTicks,
        long tickFrequency,
        int sourceSampleRate,
        int deliveredSourceSamples)
    {
        if (elapsedTicks <= 0 ||
            tickFrequency <= 0 ||
            sourceSampleRate <= 0 ||
            deliveredSourceSamples < 0)
            return 0;

        long expected = (long)Math.Floor(
            elapsedTicks * (double)sourceSampleRate / tickFrequency);
        long missing = expected - deliveredSourceSamples;
        int material = SourceSamplesForMilliseconds(
            sourceSampleRate,
            MaterialGapMilliseconds);
        if (missing < material) return 0;
        return (int)Math.Min(missing, int.MaxValue);
    }

    internal static int ScaleToOutputSamples(int sourceSamples, int sourceSampleRate)
    {
        if (sourceSamples <= 0 || sourceSampleRate <= 0) return 0;
        long scaled =
            ((long)sourceSamples * OutputSampleRate + sourceSampleRate - 1) /
            sourceSampleRate;
        return (int)Math.Min(scaled, int.MaxValue);
    }

    private static int SourceSamplesForMilliseconds(int sampleRate, int milliseconds)
        => Math.Max(1, (int)(((long)sampleRate * milliseconds + 999) / 1_000));

    private static bool IsSupportedSourceRate(int sampleRate)
        => sampleRate is 48_000 or 44_100 or 32_000 or 16_000;

    private bool IsNativeCaptureRunning()
    {
        if (!_recording || !CaptureLease.IsOwnedBy(_captureOwner)) return false;
        try { return NativeCapture.IsCaptureRunning(); }
        catch { return false; }
    }

    private string DescribeDevice() => VoiceDiagnostics.DescribeDevice(_device);

    private static string SafeDiagnostic(string value)
        => (value ?? string.Empty)
            .Replace('"', '\'')
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static string GetLastNativeError()
    {
        try
        {
            IntPtr value = NativeCapture.GetLastError();
            return value == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(value) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static class NativeCapture
    {
        private const string Library = "libstarlight.so";

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_has_record_audio_permission",
            CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool HasRecordAudioPermission();

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_request_record_audio_permission",
            CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool RequestRecordAudioPermission();

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_start_capture",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int StartCapture(int requestedRate, int requestedBufferFrames);

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_read_float",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ReadFloat(
            [Out] float[] destination,
            int capacity);

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_stop_capture",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void StopCapture();

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_is_capture_running",
            CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool IsCaptureRunning();

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_get_sample_rate",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetSampleRate();

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_get_buffer_frames",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetBufferFrames();

        [DllImport(
            Library,
            EntryPoint = "starlight_voice_get_last_error",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr GetLastError();
    }
}

internal sealed class StarlightCaptureResampler
{
    private const int OutputSampleRate = 48_000;
    private readonly int _sourceSampleRate;
    private int _phase;
    private float _previous;
    private bool _hasPrevious;

    internal StarlightCaptureResampler(int sourceSampleRate)
    {
        if (sourceSampleRate is not (44_100 or 32_000 or 16_000))
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
        _sourceSampleRate = sourceSampleRate;
    }

    internal int GetMaximumOutputSamples(int inputSamples)
    {
        if (inputSamples <= 0) return 0;
        return (int)Math.Min(
            ((long)inputSamples * OutputSampleRate +
             _sourceSampleRate - 1) /
            _sourceSampleRate,
            int.MaxValue);
    }

    internal int Convert(ReadOnlySpan<float> input, Span<float> output)
    {
        int required = GetMaximumOutputSamples(input.Length);
        if (output.Length < required)
            throw new ArgumentException("Output buffer is too small.", nameof(output));

        int written = 0;
        foreach (float current in input)
        {
            if (!_hasPrevious)
            {
                _previous = current;
                _hasPrevious = true;
            }

            int phaseBefore = _phase;
            int phaseTotal = phaseBefore + OutputSampleRate;
            for (int threshold = _sourceSampleRate;
                 threshold <= phaseTotal;
                 threshold += _sourceSampleRate)
            {
                float fraction =
                    (threshold - phaseBefore) / (float)OutputSampleRate;
                output[written++] =
                    _previous + (current - _previous) * fraction;
            }

            _phase = phaseTotal % _sourceSampleRate;
            _previous = current;
        }

        return written;
    }

    internal void Reset()
    {
        _phase = 0;
        _previous = 0f;
        _hasPrevious = false;
    }
}
#else
using System;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android microphone capture backend.
///
/// Uses Unity's looping Microphone clip as the platform capture surface:
/// <code>
///   Il2CppStructArray&lt;float&gt; audioData = new((long)sampleCount);
///   micAudioClip.GetData(audioData, lastPosition.Value);
/// </code>
///
/// Perfect Comms forwards newly captured frames directly into the managed Starlight pipeline for
/// Opus encoding and WebRTC transport.
/// </summary>
internal sealed class AndroidMicrophone : IDisposable, ICaptureSource
{
    private const int SampleRate = 48000;
    private const int ClipSeconds = 1;     // Nebula uses 1 s looping clip

    private static readonly ExclusiveCaptureLease CaptureLease = new();
    private static long _nextCaptureOwner;
    private readonly long _captureOwner = Interlocked.Increment(ref _nextCaptureOwner);

    private string _device = "";
    private AudioClip? _clip;
    private int _lastPos;
    private bool _recording;
    private bool _physicalCaptureNeedsEnd;
    private bool _captureRequested;
    private bool _retryLeaseWhenUnavailable;
    private float _volume = 1f;
    private long _lastAdvanceTicks;
    private static readonly long DeadAfterTicks = 15L * System.Diagnostics.Stopwatch.Frequency;
    private long _lastProgressMs;
    private long _nextRecoveryMs;
    private int _recoveryAttempt;
    private const int StallTimeoutMs = 1500;
    private const int InitialRetryDelayMs = 250;
    private const int MaxRetryDelayMs = 30_000;

    // Fires on main thread (via Tick) with (float[] buf, int length)
    public event Action<float[], int>? DataAvailable;
    public event Action<int>? SamplesDropped;

    public event Action<float[], int>? OnFrame;

    public bool ReuseBuffer { get; set; }
    private float[] _pollBuf = Array.Empty<float>();
    private Il2CppStructArray<float>? _il2cppPollBuf;

    public bool IsCapturing => _recording && _clip != null;
    public bool CaptureRequested => _captureRequested;
    public bool LeaseUnavailable { get; private set; }

    public void SetVolume(float v) => _volume = Math.Clamp(v, 0f, 4f);

    /// <summary>
    /// Start capture. Mirrors Nebula's SetUnityMicrophone():
    /// falls back to first available device if the given name is empty/invalid.
    /// Never passes null to Microphone.Start (IL2CPP does not accept null here).
    /// </summary>
    public bool Start(string deviceName, bool retryLeaseWhenUnavailable = false)
    {
        Stop();

        _device = ResolveDevice(deviceName);
        _captureRequested = true;
        _retryLeaseWhenUnavailable = retryLeaseWhenUnavailable;
        _recoveryAttempt = 0;
        _nextRecoveryMs = 0;
        if (!CaptureLease.TryAcquire(_captureOwner))
        {
            LeaseUnavailable = true;
            VoiceDiagnostics.Log("voice.unity.mic.lease",
                $"acquired=false device=\"{DescribeDevice()}\" reason=already-owned retry={retryLeaseWhenUnavailable.ToString().ToLowerInvariant()}");
            if (retryLeaseWhenUnavailable)
                ScheduleRecovery(Environment.TickCount64, "capture lease unavailable");
            else
                _captureRequested = false;
            return false;
        }

        LeaseUnavailable = false;
        return TryStartCapture(Environment.TickCount64, "initial");
    }

    public void Stop()
    {
        _captureRequested = false;
        EndCurrentCapture(releaseLease: true);
        _recording = false;
        _clip = null;
        _il2cppPollBuf = null;
        _nextRecoveryMs = 0;
        _recoveryAttempt = 0;
        _retryLeaseWhenUnavailable = false;
        LeaseUnavailable = false;
    }

    /// <summary>
    /// Poll for new samples — call once per frame from the main thread.
    ///
    /// Mirrors Nebula's PushAudioData():
    /// <code>
    ///   int currentPosition = Microphone.GetPosition(currentMic);
    ///   Il2CppStructArray&lt;float&gt; audioData = new((long)sampleCount);
    ///   micAudioClip.GetData(audioData, lastPosition.Value);
    ///   unityMic?.PushAudioData(audioData);
    /// </code>
    /// We skip the Il2CppStructArray here because AudioClip.GetData accepts float[]
    /// in the IL2CPP interop layer — the array is marshalled automatically.
    /// </summary>
    public void Tick()
    {
        if (!_captureRequested) return;
        if (ShouldRecoverDestroyedClip(_recording, _clip != null))
        {
            var now = Environment.TickCount64;
            VoiceDiagnostics.Log("voice.unity.mic.restart",
                $"clip-destroyed device=\"{DescribeDevice()}\" attempt={_recoveryAttempt + 1}");
            EndCurrentCapture(releaseLease: false);
            _clip = null;
            _recording = false;
            ScheduleRecovery(now, "microphone clip destroyed");
        }
        if (!_recording || _clip == null)
        {
            MaybeRetryCapture();
            return;
        }

        int clipSamples;
        try
        {
            clipSamples = _clip.samples;
            if (clipSamples <= 0)
                throw new InvalidOperationException("microphone clip has no samples");
        }
        catch (Exception ex)
        {
            RecoverFromReadFailure(ex.Message);
            return;
        }

        int pos;
        try { pos = Microphone.GetPosition(_device); }
        catch { pos = -1; }
        if (pos < 0) { MaybeRecoverFromStall(); return; }

        int newSamples = pos >= _lastPos
            ? pos - _lastPos
            : (clipSamples - _lastPos) + pos;

        if (newSamples <= 0) { MaybeRecoverFromStall(); return; }

        Volatile.Write(ref _lastAdvanceTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        _lastProgressMs = Environment.TickCount64;
        _recoveryAttempt = 0;
        _nextRecoveryMs = 0;

        // Cap main-thread work: drop oldest backlog so a slow frame can't compound into a death spiral.
        const int MaxSamplesPerTick = SampleRate / 5;
        if (newSamples > MaxSamplesPerTick)
        {
            int droppedSamples = newSamples - MaxSamplesPerTick;
            _lastPos = (_lastPos + droppedSamples) % clipSamples;
            newSamples = MaxSamplesPerTick;
            SamplesDropped?.Invoke(droppedSamples);
        }

        try
        {
            int start = _lastPos % clipSamples;
            int firstRead = Math.Min(newSamples, clipSamples - start);
            ReadAndPublish(start, firstRead);

            int remaining = newSamples - firstRead;
            if (remaining > 0)
                ReadAndPublish(0, remaining);
        }
        catch (Exception ex)
        {
            RecoverFromReadFailure(ex.Message);
            return;
        }

        _lastPos = pos;
    }

    private void RecoverFromReadFailure(string failure)
    {
        var now = Environment.TickCount64;
        VoiceDiagnostics.Log("voice.unity.mic.restart",
            $"read-failed device=\"{DescribeDevice()}\" attempt={_recoveryAttempt + 1} error=\"{SafeDiagnostic(failure)}\"");
        EndCurrentCapture(releaseLease: false);
        _clip = null;
        _recording = false;
        ScheduleRecovery(now, "read failed");
    }

    private void MaybeRecoverFromStall()
    {
        if (!_captureRequested || !_recording || _clip == null) return;
        long now = Environment.TickCount64;
        if (now - _lastProgressMs < StallTimeoutMs) return;

        VoiceDiagnostics.Log("voice.unity.mic.restart",
            $"stalled device=\"{DescribeDevice()}\" attempt={_recoveryAttempt + 1} stallMs={now - _lastProgressMs} lastPos={_lastPos}");
        EndCurrentCapture(releaseLease: false);
        _clip = null;
        _recording = false;
        ScheduleRecovery(now, "stalled");
    }

    private void MaybeRetryCapture()
    {
        var now = Environment.TickCount64;
        if (!ShouldAttemptRecovery(_captureRequested, _recording, now, _nextRecoveryMs)) return;
        _device = ResolveDevice(_device);

        if (!CaptureLease.IsOwnedBy(_captureOwner))
        {
            if (!CaptureLease.TryAcquire(_captureOwner))
            {
                LeaseUnavailable = true;
                if (_retryLeaseWhenUnavailable)
                    ScheduleRecovery(now, "capture lease unavailable");
                else
                    _captureRequested = false;
                return;
            }

            LeaseUnavailable = false;
            _recoveryAttempt = 0;
            _nextRecoveryMs = 0;
            VoiceDiagnostics.Log("voice.unity.mic.lease",
                $"acquired=true device=\"{DescribeDevice()}\" reason=retry");
        }

        TryStartCapture(now, "retry");
    }

    private bool TryStartCapture(long now, string reason)
    {
        if (!CaptureLease.IsOwnedBy(_captureOwner))
        {
            LeaseUnavailable = true;
            if (_retryLeaseWhenUnavailable)
                ScheduleRecovery(now, "capture lease lost");
            else
                _captureRequested = false;
            return false;
        }

        AudioClip? clip = null;
        string failure = "Microphone.Start returned null";
        try
        {
            // Unity may reserve its process-global microphone surface even when the returned
            // AudioClip is immediately destroyed or the managed call throws. Remember that the
            // physical Start entry point was entered independently of Unity's overloaded null
            // comparison so this owner always issues the matching End.
            _physicalCaptureNeedsEnd = true;
            clip = Microphone.Start(_device, true, ClipSeconds, SampleRate);
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }

        _clip = clip;
        _lastPos = 0;
        _il2cppPollBuf = null;
        _lastProgressMs = now;
        Volatile.Write(ref _lastAdvanceTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        _recording = clip != null;
        if (_recording)
        {
            _nextRecoveryMs = 0;
            VoiceDiagnostics.Log("voice.unity.mic.restart",
                $"started device=\"{DescribeDevice()}\" reason={reason} attempt={_recoveryAttempt}");
            return true;
        }

        // Unity can fail after touching its global microphone state. Close this owner's physical
        // attempt before the scheduled retry, while retaining the process-local logical lease.
        EndCurrentCapture(releaseLease: false);
        ScheduleRecovery(now, failure);
        return false;
    }

    private void ScheduleRecovery(long now, string failure)
    {
        _recoveryAttempt = Math.Min(_recoveryAttempt + 1, 30);
        var delayMs = RecoveryDelayMilliseconds(_recoveryAttempt, InitialRetryDelayMs, MaxRetryDelayMs);
        _nextRecoveryMs = now + delayMs;
        VoiceDiagnostics.Log("voice.unity.mic.restart",
            $"retry-scheduled device=\"{DescribeDevice()}\" attempt={_recoveryAttempt} delayMs={delayMs} error=\"{SafeDiagnostic(failure)}\"");
    }

    private void EndCurrentCapture(bool releaseLease)
    {
        if (!CaptureLease.IsOwnedBy(_captureOwner)) return;

        // Empty string is the IL2CPP-safe representation of Unity's default device and is the
        // same value passed to Start, so it must also be passed to End to release the mic lease.
        try
        {
            // Do not call the process-global End API for an instance that never started a clip.
            // In particular, a blocked setup preview must not stop the active room microphone.
            if (_physicalCaptureNeedsEnd)
                Microphone.End(_device);
        }
        catch { }
        finally
        {
            _physicalCaptureNeedsEnd = false;
            // A stalled capture retains logical ownership while it retries. Only an explicit Stop
            // hands the global Unity microphone surface to another Perfect Comms consumer.
            if (releaseLease)
                CaptureLease.Release(_captureOwner);
        }
    }

    internal static bool ShouldAttemptRecovery(bool requested, bool recording, long nowMs, long nextRecoveryMs)
        => requested && !recording && nowMs >= nextRecoveryMs;

    internal static bool ShouldRecoverDestroyedClip(bool recording, bool clipAvailable)
        => recording && !clipAvailable;

    internal static int RecoveryDelayMilliseconds(int attempt, int initialDelayMs, int maximumDelayMs)
    {
        var initial = Math.Max(1, initialDelayMs);
        var maximum = Math.Max(initial, maximumDelayMs);
        var shift = Math.Clamp(attempt - 1, 0, 20);
        var delay = (long)initial << shift;
        return (int)Math.Min(delay, maximum);
    }

    internal static int RecoveryDelayMilliseconds(
        int attempt,
        int initialDelayMs,
        int maximumDelayMs,
        int minimumDelayMs)
    {
        var effectiveMaximum = Math.Max(Math.Max(1, initialDelayMs), maximumDelayMs);
        var minimum = Math.Clamp(minimumDelayMs, 1, effectiveMaximum);
        return Math.Max(
            minimum,
            RecoveryDelayMilliseconds(attempt, initialDelayMs, maximumDelayMs));
    }

    private void ReadAndPublish(int start, int count)
    {
        if (_clip == null || count <= 0) return;

        // Reuse the IL2CPP buffer across frames to keep GC pressure off the render
        // thread; reallocate only when the per-Tick sample count changes (steady state
        // is a stable per-frame delta, so most frames hit the reuse path).
        if (_il2cppPollBuf == null || _il2cppPollBuf.Length != count)
            _il2cppPollBuf = new Il2CppStructArray<float>(count);
        var samples = _il2cppPollBuf;
        _clip.GetData(samples, start);

        float[] buf;
        if (ReuseBuffer)
        {
            if (_pollBuf.Length < count) _pollBuf = new float[count];
            buf = _pollBuf;
        }
        else
        {
            buf = new float[count];
        }

        samples.CopyTo(buf, 0);
        if (_volume != 1f)
            for (int i = 0; i < count; i++) buf[i] *= _volume;

        DataAvailable?.Invoke(buf, count);
        OnFrame?.Invoke(buf, count);
    }

    bool ICaptureSource.Start(string? deviceId)
    {
        return Start(deviceId ?? string.Empty);
    }

    public CaptureHealth Health
        => ClassifyPosition(_recording, System.Diagnostics.Stopwatch.GetTimestamp(), Volatile.Read(ref _lastAdvanceTicks), DeadAfterTicks);

    public static CaptureHealth ClassifyPosition(bool recording, long now, long lastAdvanceTicks, long deadAfterTicks)
    {
        if (!recording) return CaptureHealth.Dead;
        var since = now - lastAdvanceTicks;
        if (since >= deadAfterTicks) return CaptureHealth.Dead;
        return CaptureHealth.Healthy;
    }

    public void Dispose() => Stop();

    public static string[] GetDeviceNames()
    {
        try { return Microphone.devices; }
        catch { return Array.Empty<string>(); }
    }

    private string DescribeDevice() => VoiceDiagnostics.DescribeDevice(_device);

    private static string SafeDiagnostic(string value)
        => (value ?? string.Empty).Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    private static string ResolveDevice(string name)
    {
        var devices = GetDeviceNames();
        if (!string.IsNullOrEmpty(name))
        {
            foreach (var device in devices)
                if (device == name) return name;
        }
        return devices.Length > 0 ? devices[0] : string.Empty;
    }

}
#endif
#endif
