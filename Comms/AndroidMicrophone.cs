#if ANDROID || WINDOWS
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
/// Perfect Comms reads newly captured frames and pushes them directly into the native pc-mobile
/// engine for WebRTC encoding and transport.
/// </summary>
internal sealed class AndroidMicrophone : IDisposable, ICaptureSource
{
    private const int SampleRate  = 48000;
    private const int ClipSeconds = 1;     // Nebula uses 1 s looping clip

    private string    _device = "";
    private AudioClip? _clip;
    private int        _lastPos;
    private bool       _recording;
    private bool       _captureRequested;
    private float      _volume = 1f;
    private long _lastAdvanceTicks;
    private static readonly long DeadAfterTicks = 15L * System.Diagnostics.Stopwatch.Frequency;
    private long       _lastProgressMs;
    private long       _nextRecoveryMs;
    private int        _recoveryAttempt;
    private const int  StallTimeoutMs      = 1500;
    private const int  InitialRetryDelayMs = 250;
    private const int  MaxRetryDelayMs     = 30_000;

    // Fires on main thread (via Tick) with (float[] buf, int length)
    public event Action<float[], int>? DataAvailable;

    public event Action<float[], int>? OnFrame;

    public bool ReuseBuffer { get; set; }
    private float[] _pollBuf = Array.Empty<float>();
    private Il2CppStructArray<float>? _il2cppPollBuf;

    public bool IsCapturing => _recording && _clip != null;
    public bool CaptureRequested => _captureRequested;

    public void SetVolume(float v) => _volume = Math.Clamp(v, 0f, 4f);

    /// <summary>
    /// Start capture. Mirrors Nebula's SetUnityMicrophone():
    /// falls back to first available device if the given name is empty/invalid.
    /// Never passes null to Microphone.Start (IL2CPP does not accept null here).
    /// </summary>
    public bool Start(string deviceName)
    {
        Stop();

        _device = ResolveDevice(deviceName);
        _captureRequested = true;
        _recoveryAttempt = 0;
        _nextRecoveryMs = 0;
        return TryStartCapture(Environment.TickCount64, "initial");
    }

    public void Stop()
    {
        _captureRequested = false;
        _recording = false;
        EndCurrentCapture();
        _clip = null;
        _il2cppPollBuf = null;
        _nextRecoveryMs = 0;
        _recoveryAttempt = 0;
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
            _lastPos = (_lastPos + (newSamples - MaxSamplesPerTick)) % clipSamples;
            newSamples = MaxSamplesPerTick;
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
        EndCurrentCapture();
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
        EndCurrentCapture();
        _clip = null;
        _recording = false;
        ScheduleRecovery(now, "stalled");
    }

    private void MaybeRetryCapture()
    {
        var now = Environment.TickCount64;
        if (!ShouldAttemptRecovery(_captureRequested, _recording, now, _nextRecoveryMs)) return;
        _device = ResolveDevice(_device);
        TryStartCapture(now, "retry");
    }

    private bool TryStartCapture(long now, string reason)
    {
        AudioClip? clip = null;
        string failure = "Microphone.Start returned null";
        try
        {
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

    private void EndCurrentCapture()
    {
        // Empty string is the IL2CPP-safe representation of Unity's default device and is the
        // same value passed to Start, so it must also be passed to End to release the mic lease.
        try { Microphone.End(_device); } catch { }
    }

    internal static bool ShouldAttemptRecovery(bool requested, bool recording, long nowMs, long nextRecoveryMs)
        => requested && !recording && nowMs >= nextRecoveryMs;

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
