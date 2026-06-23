using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class SidecarCaptureSource : ICaptureSource, IDisposable
{
    public const int Proto = 1;
    private const int HandshakeTimeoutMs = 4000;

    private readonly Func<string, string, SidecarLaunchResult> _launch;
    private readonly object _gate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private SidecarLaunchResult? _launchResult;
    private volatile int _health = (int)CaptureHealth.Dead;
    private volatile bool _running;
    private readonly float[] _frameScratch = new float[SidecarProtocol.AudioSamples];
    private Thread? _reader;

    public event Action<float[], int>? OnFrame;
    public CaptureHealth Health => (CaptureHealth)Volatile.Read(ref _health);

    public SidecarCaptureSource(Func<string, string, SidecarLaunchResult> launch)
    {
        _launch = launch;
    }

    public bool Start(string? deviceId)
    {
        Stop();
        _running = false;
        var token = Guid.NewGuid().ToString("N");
        SidecarLaunchResult launch;
        try
        {
            launch = _launch(token, deviceId ?? "");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar", "launch threw: " + ex.Message);
            SetHealth(CaptureHealth.Dead);
            return false;
        }
        if (launch == null || !launch.Success)
        {
            VoiceDiagnostics.Log("sidecar", "launch failed: " + (launch?.FailureReason ?? "null"));
            SetHealth(CaptureHealth.Dead);
            return false;
        }
        _launchResult = launch;

        TcpClient client;
        NetworkStream stream;
        try
        {
            client = new TcpClient();
            client.Connect(System.Net.IPAddress.Loopback, launch.Port);
            stream = client.GetStream();
            stream.ReadTimeout = HandshakeTimeoutMs;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar", "connect failed: " + ex.Message);
            KillLaunch();
            SetHealth(CaptureHealth.Dead);
            return false;
        }

        try
        {
            var hello = SidecarProtocol.HelloFrame(Proto, token);
            stream.Write(hello, 0, hello.Length);
            stream.Flush();

            if (!ReadReady(stream, out var error))
            {
                VoiceDiagnostics.Log("sidecar", "handshake failed: " + error);
                try { client.Close(); } catch { }
                KillLaunch();
                SetHealth(CaptureHealth.Dead);
                return false;
            }

            if (!string.IsNullOrEmpty(deviceId))
            {
                var sel = SidecarProtocol.SelectDeviceFrame(deviceId!);
                stream.Write(sel, 0, sel.Length);
                stream.Flush();
            }

            var start = SidecarProtocol.StartFrame();
            stream.Write(start, 0, start.Length);
            stream.Flush();
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar", "handshake/start exception: " + ex.Message);
            try { client.Close(); } catch { }
            KillLaunch();
            SetHealth(CaptureHealth.Dead);
            return false;
        }

        lock (_gate)
        {
            _client = client;
            _stream = stream;
        }
        _running = true;
        SetHealth(CaptureHealth.Healthy);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "SidecarCaptureReader" };
        _reader.Start(stream);
        return true;
    }

    private bool ReadReady(NetworkStream stream, out string error)
    {
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
                if (!SidecarProtocol.TryReadReady(json, out var proto, out var rate, out _, out var sample))
                {
                    error = "first frame was not ready";
                    return false;
                }
                if (proto != Proto) { error = $"proto mismatch helper={proto} mod={Proto}"; return false; }
                if (rate != 48000 || sample != "f32") { error = $"format mismatch rate={rate} sample={sample}"; return false; }
                return true;
            }
        }
        error = "ready timeout";
        return false;
    }

    public void Stop()
    {
        _running = false;
        TcpClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            _stream = null;
        }
        try { client?.Close(); } catch { }
        KillLaunch();
        SetHealth(CaptureHealth.Dead);
    }

    private void KillLaunch()
    {
        var launch = _launchResult;
        _launchResult = null;
        if (launch == null) return;
        try { if (launch.Process != null && !launch.Process.HasExited) launch.Process.Kill(); } catch { }
        try { if (!string.IsNullOrEmpty(launch.HandshakePath) && System.IO.File.Exists(launch.HandshakePath)) System.IO.File.Delete(launch.HandshakePath); } catch { }
    }

    private void ReadLoop(object? state)
    {
        var stream = (NetworkStream)state!;
        try { stream.ReadTimeout = Timeout.Infinite; } catch { }
        var buffer = new byte[1 << 16];
        var have = 0;
        while (_running)
        {
            int read;
            try { read = stream.Read(buffer, have, buffer.Length - have); }
            catch { break; }
            if (read <= 0) break;
            have += read;

            while (SidecarProtocol.TryParseFrame(buffer, have, out var type, out var off, out var len, out var flen))
            {
                if (type == SidecarProtocol.TypeAudio)
                {
                    if (SidecarProtocol.TryDecodeAudio(buffer, off, len, _frameScratch, out _, out var count))
                    {
                        try { OnFrame?.Invoke(_frameScratch, count); } catch { }
                    }
                }
                else if (type == SidecarProtocol.TypeControl)
                {
                    var json = Encoding.UTF8.GetString(buffer, off, len);
                    HandleStreamingControl(json);
                }
                var remaining = have - flen;
                if (remaining > 0)
                    Buffer.BlockCopy(buffer, flen, buffer, 0, remaining);
                have = remaining;
            }

            if (have == buffer.Length)
                break;
        }
        if (_running)
            SetHealth(CaptureHealth.Dead);
    }

    private void HandleStreamingControl(string json)
    {
        var op = SidecarProtocol.ReadOp(json);
        if (op == "error")
        {
            SidecarProtocol.TryReadError(json, out var code, out var msg);
            VoiceDiagnostics.Log("sidecar", $"helper error {code}: {msg}");
            SetHealth(CaptureHealth.Dead);
        }
    }

    private void SetHealth(CaptureHealth health) => Volatile.Write(ref _health, (int)health);

    public void Dispose() => Stop();
}
