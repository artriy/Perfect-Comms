#if WINDOWS
using System;
using System.Runtime.InteropServices;
using ManagedBass;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BassStereoOutput : IDisposable
{
    private readonly StreamProcedure _proc;
    private readonly Action<float[]> _fill;
    private float[] _block = Array.Empty<float>();
    private int _device = int.MinValue;
    private int _stream;

    public BassStereoOutput(Action<float[]> fill)
    {
        _fill = fill;
        _proc = StreamProc;
    }

    public bool IsPlaying => _stream != 0 && Bass.ChannelIsActive(_stream) == PlaybackState.Playing;

    public bool Start(int device)
    {
        Stop();
        Bass.Configure(Configuration.UpdatePeriod, 10);
        Bass.Configure(Configuration.PlaybackBufferLength, 100);
        if (!Bass.Init(device, AudioHelpers.ClockRate, DeviceInitFlags.Default) && Bass.LastError != Errors.Already)
            return false;
        Bass.CurrentDevice = device;
        _device = device;
        _stream = Bass.CreateStream(AudioHelpers.ClockRate, 2, BassFlags.Float, _proc);
        if (_stream == 0)
            return false;
        Bass.ChannelPlay(_stream);
        return Bass.ChannelIsActive(_stream) == PlaybackState.Playing;
    }

    private int StreamProc(int handle, IntPtr buffer, int length, IntPtr user)
    {
        var floats = length / 4;
        if (floats <= 0)
            return 0;
        if (_block.Length != floats)
            _block = new float[floats];
        Array.Clear(_block, 0, floats);
        try { _fill(_block); } catch { }
        Marshal.Copy(_block, 0, buffer, floats);
        return length;
    }

    public void Stop()
    {
        if (_stream != 0)
        {
            try { Bass.StreamFree(_stream); } catch { }
            _stream = 0;
        }
        if (_device != int.MinValue)
        {
            try { Bass.CurrentDevice = _device; Bass.Free(); } catch { }
            _device = int.MinValue;
        }
    }

    public void Dispose() => Stop();
}
#endif
