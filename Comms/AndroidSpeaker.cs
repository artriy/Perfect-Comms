#if ANDROID || WINDOWS
using System;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Interstellar.VoiceChat;
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

    private int _readErrors;
    private float[] _scratch = Array.Empty<float>();
    private void Read(Il2CppStructArray<float> data)
    {
        Interlocked.Increment(ref _readCallbacks);
        int n = data.Length;
        if (_scratch.Length != n) _scratch = new float[n];
        try { _speaker.Read(_scratch); }
        catch (Exception)
        {
            Array.Clear(_scratch, 0, n);
            if (Interlocked.Increment(ref _readErrors) == 1)
                VoiceDiagnostics.DebugWarning("[VC] Android speaker read error; emitting silence");
        }
        for (int i = 0; i < n; i++) data[i] = _scratch[i];
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _source.Stop();
        // Nebula: if (audioSource) GameObject.Destroy(audioSource)
        if (_source != null) UnityEngine.Object.Destroy(_source);
        if (_clip != null) UnityEngine.Object.Destroy(_clip); // clip holds a this-capturing PCMReaderCallback (ML2)
        VoiceDiagnostics.DebugInfo("[VC] Android speaker disposed.");
    }
}

internal sealed class AndroidSampleProviderSpeaker : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private readonly AudioSource _source;
    private readonly AudioClip _clip;
    private readonly VoiceMixer _mixer;
    private int _readCallbacks;

    public bool IsPlaying => _source != null && _source.isPlaying;
    public int ReadCallbacks => Volatile.Read(ref _readCallbacks);

    public AndroidSampleProviderSpeaker(VoiceMixer mixer)
    {
        _mixer = mixer ?? throw new ArgumentNullException(nameof(mixer));

        var host = VoiceChatPluginMain.ResidentObject
            ?? throw new InvalidOperationException("[VC] ResidentObject is null");

        int clipSamples = SampleRate / 2;

        _source = host.AddComponent<AudioSource>();
        _source.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        _source.spatialBlend = 0f;
        _source.volume = 1f;

        _clip = AudioClip.Create(
            "VCBclAudio",
            clipSamples,
            Channels,
            SampleRate,
            true,
            (AudioClip.PCMReaderCallback)(ary => Read(ary)));

        _source.clip = _clip;
        _source.loop = true;
        _source.Play();

        VoiceDiagnostics.DebugInfo($"[VC] Android BCL speaker initialised ({SampleRate} Hz, {Channels} ch, managed mixer).");
    }

    private int _readErrors;
    private float[] _scratch = Array.Empty<float>();
    private void Read(Il2CppStructArray<float> data)
    {
        Interlocked.Increment(ref _readCallbacks);
        int n = data.Length;
        if (_scratch.Length != n) _scratch = new float[n];
        try { _mixer.Read(_scratch); }
        catch (Exception)
        {
            Array.Clear(_scratch, 0, n);
            if (Interlocked.Increment(ref _readErrors) == 1)
                VoiceDiagnostics.DebugWarning("[VC] Android BCL speaker read error; emitting silence");
        }
        for (int i = 0; i < n; i++) data[i] = _scratch[i];
    }

    public void Dispose()
    {
        _source.Stop();
        if (_source != null) UnityEngine.Object.Destroy(_source);
        if (_clip != null) UnityEngine.Object.Destroy(_clip); // clip holds a this-capturing PCMReaderCallback (ML1)
        VoiceDiagnostics.DebugInfo("[VC] Android BCL speaker disposed.");
    }
}
#endif
