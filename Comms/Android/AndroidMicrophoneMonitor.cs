#if ANDROID
using System;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Lock-free local microphone monitor shared by Unity capture and Unity's realtime playback
/// callback. The delayed mode holds roughly one second of mono PCM before playback begins.
/// </summary>
internal sealed class AndroidMicrophoneMonitor
{
    private const int SampleRate = 48_000;
    private const int ImmediateLatencySamples = 1_920;
    private const int DelayedLatencySamples = SampleRate;
    private const int CapacitySamples = SampleRate * 2;
    private readonly float[] _monoScratch = new float[AndroidEnginePcmSpeaker.MaximumCallbackSamples / 2];
    private SpscFloatRing? _ring;
    private volatile float _gain = 1f;
    private int _enabled;
    private int _delayed;

    internal bool Enabled => Volatile.Read(ref _enabled) != 0;

    internal void Configure(bool enabled, bool delayed, float gain)
    {
        delayed &= enabled;
        float boundedGain = float.IsFinite(gain) ? Math.Clamp(gain, 0f, 2f) : 1f;
        if (enabled && Enabled &&
            Volatile.Read(ref _delayed) == (delayed ? 1 : 0) &&
            Volatile.Read(ref _ring) != null)
        {
            _gain = boundedGain;
            return;
        }

        Volatile.Write(ref _enabled, 0);
        _gain = boundedGain;
        if (!enabled)
        {
            Volatile.Write(ref _ring, null);
            Volatile.Write(ref _delayed, 0);
            return;
        }

        int latency = delayed ? DelayedLatencySamples : ImmediateLatencySamples;
        var ring = new SpscFloatRing(
            CapacitySamples,
            channels: 1,
            fadeFrames: 96,
            targetLatencySamples: latency,
            primeLatencySamples: latency,
            maximumLatencySamples: delayed ? SampleRate + SampleRate / 2 : 9_600,
            enableClockDriftCorrection: true);
        Volatile.Write(ref _ring, ring);
        Volatile.Write(ref _delayed, delayed ? 1 : 0);
        Volatile.Write(ref _enabled, 1);
    }

    internal void Write(float[] samples, int count)
    {
        if (!Enabled || samples == null || count <= 0) return;
        var ring = Volatile.Read(ref _ring);
        if (ring == null) return;
        count = Math.Min(count, samples.Length);
        ring.TryWrite(samples.AsSpan(0, count));
    }

    internal void MixInto(float[] interleavedStereo, int count)
    {
        if (!Enabled || interleavedStereo == null || count < 2) return;
        var ring = Volatile.Read(ref _ring);
        if (ring == null) return;
        int frames = Math.Min(count / 2, _monoScratch.Length);
        ring.Read(_monoScratch.AsSpan(0, frames));
        float gain = _gain;
        for (int frame = 0; frame < frames; frame++)
        {
            float sample = _monoScratch[frame] * gain;
            int index = frame * 2;
            interleavedStereo[index] = Math.Clamp(interleavedStereo[index] + sample, -1f, 1f);
            interleavedStereo[index + 1] = Math.Clamp(interleavedStereo[index + 1] + sample, -1f, 1f);
        }
    }
}

internal sealed class AndroidMicrophoneMonitorOutput : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int ClipFrames = SampleRate / 2;
    private readonly AndroidMicrophoneMonitor _monitor;
    private readonly float[] _scratch = new float[ClipFrames * Channels];
    private readonly AudioSource _source;
    private readonly AudioClip _clip;
    private bool _disposed;

    internal AndroidMicrophoneMonitorOutput(AndroidMicrophoneMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        var host = VoiceChatPluginMain.ResidentObject
            ?? throw new InvalidOperationException("[VC] ResidentObject is null");
        _source = host.AddComponent<AudioSource>();
        _source.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        _source.spatialBlend = 0f;
        _source.volume = 1f;
        _clip = AudioClip.Create(
            "PerfectComms Mic Monitor",
            ClipFrames,
            Channels,
            SampleRate,
            true,
            (AudioClip.PCMReaderCallback)Read);
        _source.clip = _clip;
        _source.loop = true;
        _source.Play();
    }

    private void Read(Il2CppStructArray<float> data)
    {
        int count = Math.Min(data.Length, _scratch.Length);
        Array.Clear(_scratch, 0, count);
        if (!_disposed) _monitor.MixInto(_scratch, count);
        for (int i = 0; i < count; i++) data[i] = _scratch[i];
        for (int i = count; i < data.Length; i++) data[i] = 0f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _source.Stop(); } catch { }
        try { UnityEngine.Object.Destroy(_source); } catch { }
        try { UnityEngine.Object.Destroy(_clip); } catch { }
    }
}
#endif
