using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;

namespace VoiceChatPlugin.Audio;

internal sealed unsafe class RnNoiseSuppressor : IDisposable
{
    private const string NativeFileName = "rnnoise.dll";
    private static string ResourceName => Environment.Is64BitProcess ? "Lib.rnnoise.x64.dll" : "Lib.rnnoise.x86.dll";
    private static string ArchitectureLabel => Environment.Is64BitProcess ? "x64" : "x86";
    private static int ExpectedSampleRate => AudioHelpers.ClockRate;

    private static readonly object LoadLock = new();
    private static NativeApi? _api;
    private static IntPtr _nativeHandle;
    private static string? _loadError;

    private readonly NativeApi _native;
    private readonly int _frameSize;
    private readonly float[] _input;
    private readonly float[] _output;
    // Guards the native _state handle: TryProcessInPlace dereferences it while Reset/Dispose destroy it. The
    // outer _captureFrameSync in the backend already serializes callers, so this is uncontended in steady
    // state; it exists so the native wrapper is self-safe regardless of caller discipline.
    private readonly object _stateLock = new();
    private IntPtr _state;
    private bool _disposed;

    private RnNoiseSuppressor(NativeApi native, IntPtr state, int frameSize)
    {
        _native = native;
        _state = state;
        _frameSize = frameSize;
        _input = new float[frameSize];
        _output = new float[frameSize];
    }

    public int FrameSize => _frameSize;
    public string NativePath => _native.NativePath;

    public static bool TryCreate(out RnNoiseSuppressor? suppressor, out string error)
    {
        suppressor = null;
        if (!TryLoadNative(out var native, out error))
            return false;

        int frameSize;
        try
        {
            frameSize = native.GetFrameSize();
        }
        catch (Exception ex)
        {
            error = $"frame-size:{ex.Message}";
            return false;
        }

        if (AudioHelpers.ClockRate != ExpectedSampleRate)
        {
            error = $"unsupported-sample-rate:{AudioHelpers.ClockRate}";
            return false;
        }

        if (frameSize <= 0 || AudioHelpers.FrameSize % frameSize != 0)
        {
            error = $"unsupported-frame-size:{frameSize}";
            return false;
        }

        var state = native.Create(IntPtr.Zero);
        if (state == IntPtr.Zero)
        {
            error = "create-failed";
            return false;
        }

        suppressor = new RnNoiseSuppressor(native, state, frameSize);
        error = string.Empty;
        return true;
    }

    public bool TryProcessInPlace(float[] pcm, int sampleCount, out int processedFrames, out float speechProbabilityMax)
    {
        processedFrames = 0;
        speechProbabilityMax = 0f;
        lock (_stateLock)
        {
            if (_disposed || _state == IntPtr.Zero) return false;

            var count = Math.Min(sampleCount, pcm.Length);
            var processed = 0;
            while (processed + _frameSize <= count)
            {
                for (var i = 0; i < _frameSize; i++)
                    _input[i] = Math.Clamp(pcm[processed + i], -1f, 1f) * short.MaxValue;

                fixed (float* input = _input)
                fixed (float* output = _output)
                {
                    speechProbabilityMax = Math.Max(speechProbabilityMax, _native.ProcessFrame(_state, output, input));
                }

                for (var i = 0; i < _frameSize; i++)
                    pcm[processed + i] = Math.Clamp(_output[i] / short.MaxValue, -1f, 1f);

                processed += _frameSize;
                processedFrames++;
            }

            return processed > 0;
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            if (_disposed) return;

            var replacement = _native.Create(IntPtr.Zero);
            if (replacement == IntPtr.Zero) return;

            var previous = _state;
            _state = replacement;
            if (previous != IntPtr.Zero)
                _native.Destroy(previous);
        }
    }

    public void Dispose()
    {
        DisposeNative();
        GC.SuppressFinalize(this);
    }

    ~RnNoiseSuppressor() => DisposeNative();

    private void DisposeNative()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;

            var state = _state;
            _state = IntPtr.Zero;
            if (state != IntPtr.Zero)
                _native.Destroy(state);
        }
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
                    GetExport<RnNoiseGetFrameSize>("rnnoise_get_frame_size"),
                    GetExport<RnNoiseCreate>("rnnoise_create"),
                    GetExport<RnNoiseDestroy>("rnnoise_destroy"),
                    GetExport<RnNoiseProcessFrame>("rnnoise_process_frame"),
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
        var root = Paths.BepInExRootPath;
        if (string.IsNullOrWhiteSpace(root))
            root = AppContext.BaseDirectory;

        var dir = Path.Combine(root, "cache", "PerfectComms", "native", ArchitectureLabel);
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

    private sealed record NativeApi(
        RnNoiseGetFrameSize GetFrameSize,
        RnNoiseCreate Create,
        RnNoiseDestroy Destroy,
        RnNoiseProcessFrame ProcessFrame,
        string NativePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RnNoiseGetFrameSize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RnNoiseCreate(IntPtr model);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RnNoiseDestroy(IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float RnNoiseProcessFrame(IntPtr state, float* output, float* input);
}
