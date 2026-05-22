#if ANDROID
using System;
using System.Threading;
using Interstellar.VoiceChat;
using NAudio.Wave;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android audio output backend.
///
/// Mirrors Nebula's NoSVCRoom.cs exactly:
///
///   var audioSource = ModSingleton&lt;ResidentBehaviour&gt;.Instance.gameObject.AddComponent&lt;AudioSource&gt;();
///   audioSource.MarkDontUnload();
///   var speaker = new ManualSpeaker(() => { if (audioSource) GameObject.Destroy(audioSource); });
///   AudioClip myClip = AudioClip.Create("VCAudio", (int)(sampleRate * 0.5f), 2, sampleRate, true,
///       (AudioClip.PCMReaderCallback)(ary => speaker.Read(ary)));
///   audioSource.clip = myClip;
///   audioSource.loop = true;
///   audioSource.Play();
///
/// Nebula's ManualSpeaker.Read(ary) is driven by Unity's audio thread via PCMReaderCallback.
/// In Nebula, ManualSpeaker internally calls _endpoint.Read() (the Interstellar audio graph
/// endpoint) to pull rendered PCM through the full volume/pan/effects routing graph.
///
/// We replicate this exactly: PCMReaderCallback calls _endpoint.Read() directly,
/// pulling audio through the full AudioManager routing graph (volume, stereo pan,
/// ghost reverb, radio filter, etc.) — not from a separate ring buffer.
///
/// This is the key fix: previously WriteMono() bypassed the graph entirely.
/// Now the PCMReaderCallback IS the graph's consumer, just like Nebula's ManualSpeaker.
/// </summary>
internal sealed class AndroidSpeaker : IDisposable
{
    // Match Nebula: (int)(interstellarRoom.SampleRate * 0.5f) samples, 2 channels
    private const int   SampleRate = 48000;
    private const int   Channels   = 2;
    private const float ClipSecs   = 0.5f;

    private readonly AudioSource   _source;
    private readonly AudioClip     _clip;
    private readonly ManualSpeaker _speaker;
    private readonly float[]       _readBuf;
    private int _readCallbacks;

    public bool IsPlaying => _source != null && _source.isPlaying;
    public int ReadCallbacks => Volatile.Read(ref _readCallbacks);

    /// <summary>
    /// Create the speaker. The <paramref name="speaker"/> is Interstellar's manual
    /// speaker endpoint. Unity pulls PCM from it in the AudioClip reader callback.
    /// </summary>
    public AndroidSpeaker(ManualSpeaker speaker)
    {
        _speaker = speaker ?? throw new ArgumentNullException(nameof(speaker));

        var host = VoiceChatPluginMain.ResidentObject
            ?? throw new InvalidOperationException("[VC] ResidentObject is null");

        int clipSamples = (int)(SampleRate * ClipSecs); // Nebula: sampleRate * 0.5f
        _readBuf = new float[clipSamples * Channels];

        // Add AudioSource to ResidentObject — Nebula: ResidentBehaviour.gameObject.AddComponent<AudioSource>()
        _source = host.AddComponent<AudioSource>();
        // Nebula: audioSource.MarkDontUnload()
        _source.hideFlags  |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        _source.spatialBlend = 0f; // 2D
        _source.volume       = 1f;

        // Nebula: AudioClip.Create("VCAudio", (int)(sampleRate * 0.5f), 2, sampleRate, true,
        //             (PCMReaderCallback)(ary => speaker.Read(ary)))
        _clip = AudioClip.Create(
            "VCAudio",
            clipSamples,
            Channels,
            SampleRate,
            true,
            (AudioClip.PCMReaderCallback)(ary => Read(ary)));

        _source.clip = _clip;
        _source.loop = true;
        _source.Play();

        VoiceDiagnostics.DebugInfo("[VC] Android speaker initialised (Nebula pattern, graph-driven).");
    }

    // ── PCMReaderCallback — called by Unity audio thread ────────────────────
    // Mirrors Nebula's ManualSpeaker.Read(ary): pulls audio from Interstellar.

    private void Read(float[] data)
    {
        Interlocked.Increment(ref _readCallbacks);
        _speaker.Read(data);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _source.Stop();
        // Nebula: if (audioSource) GameObject.Destroy(audioSource)
        if (_source != null) UnityEngine.Object.Destroy(_source);
        VoiceDiagnostics.DebugInfo("[VC] Android speaker disposed.");
    }
}

internal sealed class AndroidSampleProviderSpeaker : IDisposable
{
    private readonly AudioSource _source;
    private readonly AudioClip _clip;
    private readonly ISampleProvider _provider;
    private int _readCallbacks;

    public bool IsPlaying => _source != null && _source.isPlaying;
    public int ReadCallbacks => Volatile.Read(ref _readCallbacks);

    public AndroidSampleProviderSpeaker(ISampleProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        var host = VoiceChatPluginMain.ResidentObject
            ?? throw new InvalidOperationException("[VC] ResidentObject is null");

        var format = _provider.WaveFormat;
        int channels = Math.Max(1, format.Channels);
        int sampleRate = format.SampleRate > 0 ? format.SampleRate : 48000;
        int clipSamples = Math.Max(sampleRate / 2, 1);

        _source = host.AddComponent<AudioSource>();
        _source.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        _source.spatialBlend = 0f;
        _source.volume = 1f;

        _clip = AudioClip.Create(
            "VCBclAudio",
            clipSamples,
            channels,
            sampleRate,
            true,
            (AudioClip.PCMReaderCallback)(ary => Read(ary)));

        _source.clip = _clip;
        _source.loop = true;
        _source.Play();

        VoiceDiagnostics.DebugInfo($"[VC] Android BCL speaker initialised ({sampleRate} Hz, {channels} ch).");
    }

    private void Read(float[] data)
    {
        Interlocked.Increment(ref _readCallbacks);
        int read = _provider.Read(data, 0, data.Length);
        if (read < data.Length)
            Array.Clear(data, Math.Max(read, 0), data.Length - Math.Max(read, 0));
    }

    public void Dispose()
    {
        _source.Stop();
        if (_source != null) UnityEngine.Object.Destroy(_source);
        VoiceDiagnostics.DebugInfo("[VC] Android BCL speaker disposed.");
    }
}
#endif
