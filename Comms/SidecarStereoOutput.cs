using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class SidecarStereoOutput : IDisposable
{
    public const int Proto = 2;
    private const int HandshakeTimeoutMs = 4000;
    private const int FrameSamples = SidecarProtocol.AudioOutSamples;
    private const int FrameMs = 20;

    private readonly Func<string, string, SidecarLaunchResult> _launch;
    private readonly Action<float[]> _fill;
    private readonly object _gate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private SidecarLaunchResult? _launchResult;
    private Thread? _pump;
    private volatile bool _running;
    private string[] _outputDevices = Array.Empty<string>();
    private readonly float[] _block = new float[FrameSamples];

    public bool Ready { get; private set; }
    public IReadOnlyList<string> OutputDevices => _outputDevices;

    public SidecarStereoOutput(Func<string, string, SidecarLaunchResult> launch, Action<float[]> fill)
    {
        _launch = launch;
        _fill = fill;
    }

    public bool Start(string? deviceId)
    {
        Stop();
        var token = Guid.NewGuid().ToString("N");
        SidecarLaunchResult launch;
        try
        {
            launch = _launch(token, deviceId ?? "");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar.out", "launch threw: " + ex.Message);
            return false;
        }
        if (launch == null || !launch.Success)
        {
            VoiceDiagnostics.Log("sidecar.out", "launch failed: " + (launch?.FailureReason ?? "null"));
            return false;
        }
        _launchResult = launch;

        TcpClient client;
        NetworkStream stream;
        try
        {
            client = new TcpClient();
            client.Connect(IPAddress.Loopback, launch.Port);
            stream = client.GetStream();
            stream.ReadTimeout = HandshakeTimeoutMs;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar.out", "connect failed: " + ex.Message);
            KillLaunch();
            return false;
        }

        try
        {
            var hello = SidecarProtocol.HelloFrame(Proto, token);
            stream.Write(hello, 0, hello.Length);
            stream.Flush();

            if (!ReadReady(stream, out var devices, out var error))
            {
                VoiceDiagnostics.Log("sidecar.out", "handshake failed: " + error);
                try { client.Close(); } catch { }
                KillLaunch();
                return false;
            }
            _outputDevices = devices;

            if (!string.IsNullOrEmpty(deviceId))
            {
                var sel = SidecarProtocol.SelectOutputDeviceFrame(deviceId!);
                stream.Write(sel, 0, sel.Length);
                stream.Flush();
            }
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar.out", "handshake exception: " + ex.Message);
            try { client.Close(); } catch { }
            KillLaunch();
            return false;
        }

        lock (_gate)
        {
            _client = client;
            _stream = stream;
        }
        _running = true;
        Ready = true;
        var pump = new Thread(PumpLoop) { IsBackground = true, Name = "SidecarStereoOutput" };
        _pump = pump;
        pump.Start();
        return true;
    }

    private bool ReadReady(NetworkStream stream, out string[] outputDevices, out string error)
    {
        outputDevices = Array.Empty<string>();
        error = "";
        var buffer = new byte[8192];
        var have = 0;
        var deadline = Environment.TickCount + HandshakeTimeoutMs;
        while (Environment.TickCount < deadline)
        {
            int read;
            try { read = stream.Read(buffer, have, buffer.Length - have); }
            catch (Exception ex) { error = "read: " + ex.Message; return false; }
            if (read <= 0) { error = "stream closed before ready"; return false; }
            have += read;

            if (SidecarProtocol.TryParseFrame(buffer, have, out var type, out var off, out var len, out _))
            {
                if (type != SidecarProtocol.TypeControl) { error = "expected control frame"; return false; }
                var json = Encoding.UTF8.GetString(buffer, off, len);
                if (SidecarProtocol.ReadOp(json) == "error")
                {
                    SidecarProtocol.TryReadError(json, out var code, out var msg);
                    error = $"helper error {code}: {msg}";
                    return false;
                }
                if (!SidecarProtocol.TryReadReady(json, out var proto, out _, out _, out _))
                {
                    error = "first frame was not ready";
                    return false;
                }
                if (proto != Proto) { error = $"proto mismatch helper={proto} mod={Proto}"; return false; }
                if (SidecarProtocol.TryReadOutputDevices(json, out var devs))
                    outputDevices = devs.ToArray();
                return true;
            }
        }
        error = "ready timeout";
        return false;
    }

    private void PumpLoop()
    {
        var sw = Stopwatch.StartNew();
        long nextMs = 0;
        while (_running)
        {
            try { _fill(_block); }
            catch { Array.Clear(_block, 0, _block.Length); }

            var frame = SidecarProtocol.EncodeAudioOut(_block, FrameSamples);
            try
            {
                lock (_gate)
                {
                    if (_stream == null) break;
                    _stream.Write(frame, 0, frame.Length);
                    _stream.Flush();
                }
            }
            catch
            {
                Ready = false;
                break;
            }

            nextMs += FrameMs;
            var sleep = nextMs - sw.ElapsedMilliseconds;
            if (sleep > 1)
                Thread.Sleep((int)sleep);
            else if (sleep < -200)
                nextMs = sw.ElapsedMilliseconds;
        }
    }

    public void Stop()
    {
        _running = false;
        Ready = false;
        TcpClient? client;
        Thread? pump;
        lock (_gate)
        {
            client = _client;
            pump = _pump;
            _client = null;
            _stream = null;
            _pump = null;
        }
        try { client?.Close(); } catch { }
        KillLaunch();
        if (pump != null && pump != Thread.CurrentThread)
        {
            try { pump.Join(1000); } catch { }
        }
    }

    private void KillLaunch()
    {
        var launch = _launchResult;
        _launchResult = null;
        if (launch == null) return;
        try { if (launch.Process != null && !launch.Process.HasExited) launch.Process.Kill(); } catch { }
        try { if (!string.IsNullOrEmpty(launch.HandshakePath) && System.IO.File.Exists(launch.HandshakePath)) System.IO.File.Delete(launch.HandshakePath); } catch { }
    }

    public void Dispose() => Stop();
}
