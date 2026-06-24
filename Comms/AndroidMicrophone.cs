#if ANDROID || WINDOWS
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android microphone capture backend.
///
/// Mirrors Nebula's ManualMicrophone + PushAudioData pattern from NoSVCRoom.cs.
///
/// Nebula's approach (from the commented-out section preserved in NoSVCRoom.cs):
/// <code>
///   Il2CppStructArray&lt;float&gt; audioData = new((long)sampleCount);
///   micAudioClip.GetData(audioData, lastPosition.Value);
///   unityMic?.PushAudioData(audioData);
/// </code>
///
/// SetMicrophone on Android calls:
/// <code>
///   interstellarRoom.Microphone = new ManualMicrophone();
/// </code>
/// which means the microphone object itself is created fresh (no device name needed).
/// Audio is pushed into it via PushAudioData each frame.
///
/// In VoiceChat (which uses Hazel transport instead of Interstellar) we replicate
/// this by reading Unity Microphone each frame and directly encoding + enqueuing.
/// </summary>
internal sealed class AndroidMicrophone : IDisposable, ICaptureSource
{
    private const int SampleRate  = 48000;
    private const int ClipSeconds = 1;     // Nebula uses 1 s looping clip

    private string    _device = "";
    private AudioClip? _clip;
    private int        _lastPos;
    private bool       _recording;
    private float      _volume = 1f;
    private long _lastAdvanceTicks;
    private static readonly long DeadAfterTicks = TimeSpan.FromSeconds(15).Ticks;
    private long       _lastProgressMs;
    private long       _lastRestartMs;
    private int        _restartCount;
    private const int  StallTimeoutMs      = 1500;
    private const int  RestartCooldownMs   = 2000;
    private const int  MaxRestartsPerStart = 8;

    // Fires on main thread (via Tick) with (float[] buf, int length)
    public event Action<float[], int>? DataAvailable;

    public event Action<float[], int>? OnFrame;

    public bool ReuseBuffer { get; set; }
    private float[] _pollBuf = Array.Empty<float>();
    private Il2CppStructArray<float>? _il2cppPollBuf;

    public void SetVolume(float v) => _volume = Math.Clamp(v, 0f, 4f);

    /// <summary>
    /// Start capture. Mirrors Nebula's SetUnityMicrophone():
    /// falls back to first available device if the given name is empty/invalid.
    /// Never passes null to Microphone.Start (IL2CPP does not accept null here).
    /// </summary>
    public void Start(string deviceName)
    {
        Stop();

        // Nebula: falls back to first enumerated device
        if (string.IsNullOrEmpty(deviceName) || !DeviceExists(deviceName))
            deviceName = Microphone.devices.Length > 0 ? Microphone.devices[0] : "";

        _device    = deviceName;
        _clip      = Microphone.Start(_device, true, ClipSeconds, SampleRate);
        _lastPos   = 0;
        _recording = true;
        _lastAdvanceTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastProgressMs = Environment.TickCount64;
        _restartCount   = 0;

        VoiceDiagnostics.DebugInfo(
            $"[VC] Android mic started: '{(string.IsNullOrEmpty(_device) ? "default" : _device)}'");
    }

    public void Stop()
    {
        _recording = false;
        if (!string.IsNullOrEmpty(_device))
            Microphone.End(_device);
        _clip = null;
        _il2cppPollBuf = null;
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
        if (!_recording || _clip == null) return;

        int pos = Microphone.GetPosition(_device);
        if (pos < 0) { MaybeRecoverFromStall(); return; }

        int newSamples = pos >= _lastPos
            ? pos - _lastPos
            : (_clip.samples - _lastPos) + pos;

        if (newSamples <= 0) { MaybeRecoverFromStall(); return; }

        _lastAdvanceTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastProgressMs = Environment.TickCount64;

        // Cap main-thread work: drop oldest backlog so a slow frame can't compound into a death spiral.
        const int MaxSamplesPerTick = SampleRate / 5;
        if (newSamples > MaxSamplesPerTick)
        {
            _lastPos = (_lastPos + (newSamples - MaxSamplesPerTick)) % _clip.samples;
            newSamples = MaxSamplesPerTick;
        }

        int start = _lastPos % _clip.samples;
        int firstRead = Math.Min(newSamples, _clip.samples - start);
        ReadAndPublish(start, firstRead);

        int remaining = newSamples - firstRead;
        if (remaining > 0)
            ReadAndPublish(0, remaining);

        _lastPos = pos;
    }

    private enum StallDecision { None, Restart, GiveUpLogOnce }

    private static StallDecision DecideStallAction(long now, long lastProgressMs, long lastRestartMs, int restartCount)
    {
        if (now - lastProgressMs < StallTimeoutMs) return StallDecision.None;
        if (now - lastRestartMs  < RestartCooldownMs) return StallDecision.None;
        if (restartCount >= MaxRestartsPerStart)
            return restartCount == MaxRestartsPerStart ? StallDecision.GiveUpLogOnce : StallDecision.None;
        return StallDecision.Restart;
    }

    private void MaybeRecoverFromStall()
    {
        if (!_recording || _clip == null) return;
        long now = Environment.TickCount64;
        switch (DecideStallAction(now, _lastProgressMs, _lastRestartMs, _restartCount))
        {
            case StallDecision.GiveUpLogOnce:
                _restartCount++;
                VoiceDiagnostics.Log("bcl.unity.mic.restart",
                    $"giveUp device=\"{(string.IsNullOrEmpty(_device) ? "default" : _device)}\" attempts={MaxRestartsPerStart} stallMs={now - _lastProgressMs}");
                return;
            case StallDecision.Restart:
                _restartCount++;
                _lastRestartMs = now;
                VoiceDiagnostics.Log("bcl.unity.mic.restart",
                    $"reacquire device=\"{(string.IsNullOrEmpty(_device) ? "default" : _device)}\" attempt={_restartCount} stallMs={now - _lastProgressMs} lastPos={_lastPos}");
                RestartCapture();
                return;
            default:
                return;
        }
    }

    private void RestartCapture()
    {
        try { if (!string.IsNullOrEmpty(_device)) Microphone.End(_device); } catch { }
        string dev = _device;
        if (string.IsNullOrEmpty(dev) || !DeviceExists(dev))
            dev = Microphone.devices.Length > 0 ? Microphone.devices[0] : "";
        _device = dev;
        try { _clip = Microphone.Start(_device, true, ClipSeconds, SampleRate); } catch { _clip = null; }
        _lastPos = 0;
        _il2cppPollBuf = null;
        _lastProgressMs = Environment.TickCount64;
        _recording = _clip != null;
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
        Start(deviceId ?? string.Empty);
        return _recording;
    }

    public CaptureHealth Health
        => ClassifyPosition(_recording, System.Diagnostics.Stopwatch.GetTimestamp(), _lastAdvanceTicks, DeadAfterTicks);

    public static CaptureHealth ClassifyPosition(bool recording, long now, long lastAdvanceTicks, long deadAfterTicks)
    {
        if (!recording) return CaptureHealth.Dead;
        var since = now - lastAdvanceTicks;
        if (since >= deadAfterTicks) return CaptureHealth.Dead;
        return CaptureHealth.Healthy;
    }

    public void Dispose() => Stop();

    public static string[] GetDeviceNames() => Microphone.devices;

    private static bool DeviceExists(string name)
    {
        foreach (var d in Microphone.devices)
            if (d == name) return true;
        return false;
    }
}
#endif
