#if ANDROID
using System;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Unity playback surface for the Android pc-mobile engine. Unity pulls interleaved stereo
/// directly from Rust; no legacy managed voice graph or external backend is involved.
/// </summary>
internal sealed class AndroidEnginePcmSpeaker : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int HealthCheckIntervalMs = 500;
    private const int CallbackStallTimeoutMs = 3_000;
    private const int InitialRetryDelayMs = 250;
    private const int MaxRetryDelayMs = 10_000;
    private readonly AudioSource _source;
    private readonly AudioClip _clip;
    private readonly MobileVoiceClient _voice;
    private int _readCallbacks;
    private int _readErrors;
    private int _recoveryAttempt;
    private long _lastReadMs;
    private long _nextHealthCheckMs;
    private long _nextRecoveryMs;
    private volatile bool _disposed;

    public bool IsPlaying => _source != null && _source.isPlaying;
    public int ReadCallbacks => Volatile.Read(ref _readCallbacks);

    public AndroidEnginePcmSpeaker(MobileVoiceClient voice)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));

        var host = VoiceChatPluginMain.ResidentObject
            ?? throw new InvalidOperationException("[VC] ResidentObject is null");

        var clipSamples = SampleRate / 2;
        _source = host.AddComponent<AudioSource>();
        _source.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        _source.spatialBlend = 0f;
        _source.volume = 1f;

        _clip = AudioClip.Create(
            "VCPcMobile",
            clipSamples,
            Channels,
            SampleRate,
            true,
            (AudioClip.PCMReaderCallback)Read);

        _source.clip = _clip;
        _source.loop = true;
        _source.Play();
        _lastReadMs = Environment.TickCount64;

        VoiceDiagnostics.DebugInfo($"[VC] Android pc-mobile speaker initialised ({SampleRate} Hz, {Channels} ch).");
    }

    private float[] _scratch = Array.Empty<float>();

    private void Read(Il2CppStructArray<float> data)
    {
        Interlocked.Increment(ref _readCallbacks);
        Volatile.Write(ref _lastReadMs, Environment.TickCount64);
        Interlocked.Exchange(ref _recoveryAttempt, 0);
        Volatile.Write(ref _nextRecoveryMs, 0);
        var count = data.Length;
        if (_scratch.Length != count) _scratch = new float[count];
        if (_disposed)
        {
            Array.Clear(_scratch, 0, count);
        }
        else
        {
            try { _voice.ReadPlayback(_scratch); }
            catch (Exception ex)
            {
                Array.Clear(_scratch, 0, count);
                if (Interlocked.Increment(ref _readErrors) == 1)
                    VoiceDiagnostics.DebugWarning($"[VC] Android pc-mobile speaker read failed; emitting silence: {ex.Message}");
            }
        }
        for (var i = 0; i < count; i++) data[i] = _scratch[i];
    }

    /// <summary>
    /// Revalidates Unity playback on the main thread. Android may stop an AudioSource when audio
    /// focus is lost, the app is suspended, or the output route changes. Recovery continues with
    /// capped backoff and also detects a source that claims to play while PCM callbacks are stalled.
    /// </summary>
    public bool Tick()
    {
        if (_disposed || _source == null) return false;

        var now = Environment.TickCount64;
        if (now < _nextHealthCheckMs) return IsPlaying;
        _nextHealthCheckMs = now + HealthCheckIntervalMs;

        bool focused;
        try { focused = Application.isFocused; }
        catch { focused = true; }

        bool playing;
        try { playing = _source.isPlaying; }
        catch { playing = false; }

        // Do not fight Android while it deliberately owns audio focus. A focused Tick after resume
        // observes the stale callback timestamp and immediately rebuilds the source/clip binding.
        if (!focused) return playing;

        var callbackAgeMs = Math.Max(0, now - Volatile.Read(ref _lastReadMs));
        if (playing && callbackAgeMs < CallbackStallTimeoutMs) return true;
        if (now < _nextRecoveryMs) return false;

        var reason = playing ? $"callback-stall-{callbackAgeMs}ms" : "source-stopped";
        return RestartPlayback(now, reason);
    }

    private bool RestartPlayback(long now, string reason)
    {
        var attempt = Math.Min(Interlocked.Increment(ref _recoveryAttempt), 30);
        try
        {
            _source.Stop();
            _source.clip = null;
            _source.clip = _clip;
            _source.loop = true;
            _source.Play();
            if (_source.isPlaying)
            {
                // Allow the audio thread a full stall window to deliver its first callback. The
                // callback resets the retry state; if it never arrives, subsequent stop/play
                // attempts back off instead of churning the Unity output route every three seconds.
                var retryDelayMs = AndroidMicrophone.RecoveryDelayMilliseconds(
                    attempt, InitialRetryDelayMs, MaxRetryDelayMs, CallbackStallTimeoutMs);
                Volatile.Write(ref _lastReadMs, now);
                Volatile.Write(ref _nextRecoveryMs, now + retryDelayMs);
                VoiceDiagnostics.Log("voice.unity.speaker.restart",
                    $"state=play-issued reason={reason} attempt={attempt} retryAfterMs={retryDelayMs}");
                return true;
            }
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("voice.unity.speaker.restart",
                $"state=play-failed reason={reason} attempt={attempt} error=\"{ex.Message.Replace('"', '\'')}\"");
        }

        var delayMs = AndroidMicrophone.RecoveryDelayMilliseconds(attempt, InitialRetryDelayMs, MaxRetryDelayMs);
        _nextRecoveryMs = now + delayMs;
        VoiceDiagnostics.Log("voice.unity.speaker.restart",
            $"state=retry-scheduled reason={reason} attempt={attempt} delayMs={delayMs}");
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _source.Stop(); } catch { }
        try { if (_source != null) UnityEngine.Object.Destroy(_source); } catch { }
        try { if (_clip != null) UnityEngine.Object.Destroy(_clip); } catch { }
        VoiceDiagnostics.DebugInfo("[VC] Android pc-mobile speaker disposed.");
    }
}
#endif
