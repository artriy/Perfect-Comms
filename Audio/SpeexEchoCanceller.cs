using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;

namespace VoiceChatPlugin.Audio;

// Native acoustic echo canceller (speexdsp MDF) + residual-echo preprocess. Removes the speaker output that
// bleeds back into the microphone for open-speaker users, using the played far-end signal as a reference.
// Lifetime/loading mirrors RnNoiseSuppressor: one process-wide native load, per-instance state under a lock.
internal sealed unsafe class SpeexEchoCanceller : IDisposable
{
    private const string NativeFileName = "speexdsp.dll";
    private static string ResourceName => Environment.Is64BitProcess ? "Lib.speexdsp.x64.dll" : "Lib.speexdsp.x86.dll";
    private static string ArchitectureLabel => Environment.Is64BitProcess ? "x64" : "x86";

    // Adaptive filter tail in samples (~200 ms @ 48 kHz): covers the WaveOut render latency plus room reverb.
    private const int FilterTaps = 9600;

    private const int SPEEX_ECHO_SET_SAMPLING_RATE = 24;
    private const int SPEEX_PREPROCESS_SET_DENOISE = 0;
    private const int SPEEX_PREPROCESS_SET_AGC = 2;
    private const int SPEEX_PREPROCESS_SET_VAD = 4;
    private const int SPEEX_PREPROCESS_SET_DEREVERB = 8;
    private const int SPEEX_PREPROCESS_SET_ECHO_SUPPRESS = 20;
    private const int SPEEX_PREPROCESS_SET_ECHO_SUPPRESS_ACTIVE = 22;
    private const int SPEEX_PREPROCESS_SET_ECHO_STATE = 24;

    private static readonly object LoadLock = new();
    private static NativeApi? _api;
    private static IntPtr _nativeHandle;
    private static string? _loadError;

    private readonly NativeApi _native;
    private readonly int _frameSize;
    private readonly short[] _mic;
    private readonly short[] _reference;
    private readonly short[] _out;
    private readonly object _stateLock = new();
    private IntPtr _echoState;
    private IntPtr _preprocessState;
    private bool _disposed;

    private SpeexEchoCanceller(NativeApi native, IntPtr echoState, IntPtr preprocessState, int frameSize)
    {
        _native = native;
        _echoState = echoState;
        _preprocessState = preprocessState;
        _frameSize = frameSize;
        _mic = new short[frameSize];
        _reference = new short[frameSize];
        _out = new short[frameSize];
    }

    public int FrameSize => _frameSize;
    public string NativePath => _native.NativePath;

    public static bool TryCreate(int frameSize, out SpeexEchoCanceller? canceller, out string error)
    {
        canceller = null;
        if (frameSize <= 0)
        {
            error = $"bad-frame-size:{frameSize}";
            return false;
        }

        if (!TryLoadNative(out var native, out error))
            return false;

        IntPtr echo;
        IntPtr preprocess;
        try
        {
            echo = native.EchoInit(frameSize, FilterTaps);
            if (echo == IntPtr.Zero)
            {
                error = "echo-init-failed";
                return false;
            }

            int rate = AudioHelpers.ClockRate;
            native.EchoCtl(echo, SPEEX_ECHO_SET_SAMPLING_RATE, &rate);

            preprocess = native.PreprocessInit(frameSize, AudioHelpers.ClockRate);
            if (preprocess == IntPtr.Zero)
            {
                native.EchoDestroy(echo);
                error = "preprocess-init-failed";
                return false;
            }

            // The preprocess stage only suppresses residual (nonlinear) echo here; denoise/AGC/VAD stay off so
            // the existing RNNoise stage keeps owning noise suppression and nothing double-processes the frame.
            int off = 0;
            int echoSuppress = -45;
            int echoSuppressActive = -25;
            native.PreprocessCtl(preprocess, SPEEX_PREPROCESS_SET_DENOISE, &off);
            native.PreprocessCtl(preprocess, SPEEX_PREPROCESS_SET_AGC, &off);
            native.PreprocessCtl(preprocess, SPEEX_PREPROCESS_SET_VAD, &off);
            native.PreprocessCtl(preprocess, SPEEX_PREPROCESS_SET_DEREVERB, &off);
            native.PreprocessCtl(preprocess, SPEEX_PREPROCESS_SET_ECHO_SUPPRESS, &echoSuppress);
            native.PreprocessCtl(preprocess, SPEEX_PREPROCESS_SET_ECHO_SUPPRESS_ACTIVE, &echoSuppressActive);
            native.PreprocessCtl(preprocess, SPEEX_PREPROCESS_SET_ECHO_STATE, (void*)echo);
        }
        catch (Exception ex)
        {
            error = $"init:{ex.Message}";
            return false;
        }

        canceller = new SpeexEchoCanceller(native, echo, preprocess, frameSize);
        error = string.Empty;
        return true;
    }

    // Cancels the far-end echo present in mic[] using reference[] (the played audio). Both are float [-1,1] of
    // the same length, a multiple of FrameSize. mic[] is rewritten in place with the echo-removed near-end.
    public bool TryCancelInPlace(float[] mic, float[] reference, int sampleCount)
    {
        lock (_stateLock)
        {
            if (_disposed || _echoState == IntPtr.Zero) return false;

            int count = Math.Min(sampleCount, Math.Min(mic.Length, reference.Length));
            int processed = 0;
            while (processed + _frameSize <= count)
            {
                for (int i = 0; i < _frameSize; i++)
                {
                    _mic[i] = FloatToPcm(mic[processed + i]);
                    _reference[i] = FloatToPcm(reference[processed + i]);
                }

                fixed (short* rec = _mic)
                fixed (short* play = _reference)
                fixed (short* outBuf = _out)
                {
                    _native.EchoCancel(_echoState, rec, play, outBuf);
                    if (_preprocessState != IntPtr.Zero)
                        _native.PreprocessRun(_preprocessState, outBuf);
                }

                for (int i = 0; i < _frameSize; i++)
                    mic[processed + i] = _out[i] / (float)short.MaxValue;

                processed += _frameSize;
            }

            return processed > 0;
        }
    }

    // Drop adaptation state (mic stop/start, speaker reopen) so a stale echo path is not carried forward.
    public void Reset()
    {
        lock (_stateLock)
        {
            if (_disposed || _echoState == IntPtr.Zero) return;
            _native.EchoReset(_echoState);
        }
    }

    public void Dispose()
    {
        DisposeNative();
        GC.SuppressFinalize(this);
    }

    ~SpeexEchoCanceller() => DisposeNative();

    private void DisposeNative()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;

            var preprocess = _preprocessState;
            var echo = _echoState;
            _preprocessState = IntPtr.Zero;
            _echoState = IntPtr.Zero;
            if (preprocess != IntPtr.Zero) _native.PreprocessDestroy(preprocess);
            if (echo != IntPtr.Zero) _native.EchoDestroy(echo);
        }
    }

    private static short FloatToPcm(float value)
    {
        float scaled = Math.Clamp(value, -1f, 1f) * short.MaxValue;
        return (short)(scaled >= 0f ? scaled + 0.5f : scaled - 0.5f);
    }

    private static bool TryLoadNative(out NativeApi native, out string error)
    {
        lock (LoadLock)
        {
            if (_api != null)
            {
                native = _api;
                error = string.Empty;
                return true;
            }

            if (!string.IsNullOrEmpty(_loadError))
            {
                native = default!;
                error = _loadError;
                return false;
            }

            try
            {
                var nativePath = ExtractNativeLibrary();
                _nativeHandle = NativeLibrary.Load(nativePath);
                _api = new NativeApi(
                    GetExport<SpeexEchoStateInit>("speex_echo_state_init"),
                    GetExport<SpeexEchoStateReset>("speex_echo_state_reset"),
                    GetExport<SpeexEchoStateDestroy>("speex_echo_state_destroy"),
                    GetExport<SpeexEchoCancellation>("speex_echo_cancellation"),
                    GetExport<SpeexEchoCtl>("speex_echo_ctl"),
                    GetExport<SpeexPreprocessStateInit>("speex_preprocess_state_init"),
                    GetExport<SpeexPreprocessStateDestroy>("speex_preprocess_state_destroy"),
                    GetExport<SpeexPreprocessRun>("speex_preprocess_run"),
                    GetExport<SpeexPreprocessCtl>("speex_preprocess_ctl"),
                    nativePath);
                native = _api;
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                native = default!;
                error = _loadError;
                return false;
            }
        }
    }

    private static T GetExport<T>(string name) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_nativeHandle, name));

    private static string ExtractNativeLibrary()
    {
        var dir = Path.Combine(ResolveBaseDirectory(), "cache", "PerfectComms", "native", ArchitectureLabel);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, NativeFileName);

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"Missing embedded resource {ResourceName}");

        if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
            return target;

        // Per-process temp name so two game instances extracting at once don't collide on one .tmp (FileShare.None).
        var temp = $"{target}.{Environment.ProcessId}.tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            stream.CopyTo(output);

        try
        {
            File.Move(temp, target, true);
        }
        catch (IOException)
        {
            // Another instance already published an identical target (and may hold it open); reuse it.
            if (!(File.Exists(target) && new FileInfo(target).Length == stream.Length))
                throw;
            try { File.Delete(temp); } catch { }
        }

        return target;
    }

    // Degrade to the app base directory when BepInEx.Core is absent (headless / unit tests). The Paths access
    // lives in a separate non-inlined method so its JIT-time assembly-load failure surfaces at the call site
    // here and is caught, instead of faulting this method before the try block runs.
    private static string ResolveBaseDirectory()
    {
        try
        {
            var root = ProbeBepInExRoot();
            if (!string.IsNullOrWhiteSpace(root)) return root;
        }
        catch
        {
        }

        return AppContext.BaseDirectory;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ProbeBepInExRoot() => Paths.BepInExRootPath;

    private sealed record NativeApi(
        SpeexEchoStateInit EchoInit,
        SpeexEchoStateReset EchoReset,
        SpeexEchoStateDestroy EchoDestroy,
        SpeexEchoCancellation EchoCancel,
        SpeexEchoCtl EchoCtl,
        SpeexPreprocessStateInit PreprocessInit,
        SpeexPreprocessStateDestroy PreprocessDestroy,
        SpeexPreprocessRun PreprocessRun,
        SpeexPreprocessCtl PreprocessCtl,
        string NativePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SpeexEchoStateInit(int frameSize, int filterLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SpeexEchoStateReset(IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SpeexEchoStateDestroy(IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SpeexEchoCancellation(IntPtr state, short* rec, short* play, short* outBuf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SpeexEchoCtl(IntPtr state, int request, void* ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SpeexPreprocessStateInit(int frameSize, int samplingRate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SpeexPreprocessStateDestroy(IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SpeexPreprocessRun(IntPtr state, short* x);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SpeexPreprocessCtl(IntPtr state, int request, void* ptr);
}
