#if WINDOWS
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedBass;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BassRecorder : IDisposable, ICaptureSource
{
    private readonly RecordProcedure _proc;
    private readonly Action<float[], int> _onFrame;
    private readonly object _gate = new();
    private float[] _buffer = Array.Empty<float>();
    private int _stream;
    private bool _started;
    private long _lastFrameTicks;
    private static readonly long DeadAfterTicks = TimeSpan.FromSeconds(15).Ticks;

    public event Action<float[], int>? OnFrame;

    public BassRecorder(Action<float[], int> onFrame)
    {
        _onFrame = onFrame;
        _proc = RecordProc;
    }

    public bool Start(int device)
    {
        lock (_gate)
        {
            StopLocked();
            if (!Bass.RecordInit(device) && Bass.LastError != Errors.Already)
                return false;
            _stream = Bass.RecordStart(AudioHelpers.ClockRate, 1, BassFlags.Float, _proc);
            if (_stream == 0)
            {
                try { Bass.RecordFree(); } catch { }
                return false;
            }
            _started = true;
            Volatile.Write(ref _lastFrameTicks, Stopwatch.GetTimestamp());
            return true;
        }
    }

    public bool Start(string? deviceId)
        => Start(ResolveDevice(deviceId));

    public CaptureHealth Health
        => ClassifyRecency(Stopwatch.GetTimestamp(), Volatile.Read(ref _lastFrameTicks), DeadAfterTicks, _started);

    public static CaptureHealth ClassifyRecency(long now, long lastFrameTicks, long deadAfterTicks, bool started)
    {
        if (!started) return CaptureHealth.Dead;
        if (lastFrameTicks == 0)
            return now >= deadAfterTicks ? CaptureHealth.Dead : CaptureHealth.Silent;
        var since = now - lastFrameTicks;
        if (since >= deadAfterTicks) return CaptureHealth.Dead;
        return CaptureHealth.Healthy;
    }

    private static int ResolveDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return -1;
        for (var i = 0; Bass.RecordGetDeviceInfo(i, out var info); i++)
            if (string.Equals(info.Name, deviceId, StringComparison.Ordinal))
                return i;
        return -1;
    }

    private bool RecordProc(int handle, IntPtr buffer, int length, IntPtr user)
    {
        var samples = length / 4;
        if (samples > 0)
        {
            if (_buffer.Length < samples)
                _buffer = new float[samples];
            Marshal.Copy(buffer, _buffer, 0, samples);
            Volatile.Write(ref _lastFrameTicks, Stopwatch.GetTimestamp());
            try { _onFrame(_buffer, samples); } catch { }
            try { OnFrame?.Invoke(_buffer, samples); } catch { }
        }
        return true;
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
    }

    private void StopLocked()
    {
        _started = false;
        var h = _stream;
        _stream = 0;
        if (h != 0)
        {
            try { Bass.ChannelStop(h); } catch { }
            try { Bass.RecordFree(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
#endif
