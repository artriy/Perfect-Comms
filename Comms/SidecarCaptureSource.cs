using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class SidecarCaptureSource : ICaptureSource, IDisposable
{
    public const int Proto = 2;
    private const int HandshakeTimeoutMs = 4000;

    private readonly Func<string, string, SidecarLaunchResult> _launch;
    private readonly object _gate = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private SidecarLaunchResult? _launchResult;
    private int _health = (int)CaptureHealth.Dead;
    private volatile bool _running;
    private readonly float[] _frameScratch = new float[SidecarProtocol.AudioSamples];
    private Thread? _reader;
    internal int PingIntervalMs = 1000;
    internal int MissedPongLimit = 3;
    private long _lastPongTick;
    private Thread? _heartbeat;
    private int _startGeneration;

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
        var generation = Volatile.Read(ref _startGeneration);
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

        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "SidecarCaptureReader" };
        var heartbeat = new Thread(HeartbeatLoop) { IsBackground = true, Name = "SidecarCaptureHeartbeat" };
        lock (_gate)
        {
            if (Volatile.Read(ref _startGeneration) != generation)
            {
                try { client.Close(); } catch { }
                KillLaunch();
                SetHealth(CaptureHealth.Dead);
                return false;
            }
            _client = client;
            _stream = stream;
            _reader = reader;
            _heartbeat = heartbeat;
        }
        _running = true;
        SetHealth(CaptureHealth.Healthy);
        reader.Start(stream);
        Volatile.Write(ref _lastPongTick, Environment.TickCount64);
        heartbeat.Start(stream);
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
        Interlocked.Increment(ref _startGeneration);
        _running = false;
        TcpClient? client;
        NetworkStream? stream;
        Thread? reader;
        Thread? heartbeat;
        lock (_gate)
        {
            client = _client;
            stream = _stream;
            reader = _reader;
            heartbeat = _heartbeat;
            _client = null;
            _stream = null;
            _reader = null;
            _heartbeat = null;
        }
        if (stream != null)
        {
            try
            {
                var stop = SidecarProtocol.StopFrame();
                stream.Write(stop, 0, stop.Length);
                stream.Flush();
            }
            catch { }
        }
        try { client?.Close(); } catch { }
        KillLaunch();
        JoinWorker(reader);
        JoinWorker(heartbeat);
        SetHealth(CaptureHealth.Dead);
    }

    private static void JoinWorker(Thread? thread)
    {
        if (thread == null || thread == Thread.CurrentThread) return;
        try { thread.Join(2000); } catch { }
    }

    internal bool AnyWorkerAlive()
    {
        Thread? reader;
        Thread? heartbeat;
        lock (_gate)
        {
            reader = _reader;
            heartbeat = _heartbeat;
        }
        return (reader != null && reader.IsAlive) || (heartbeat != null && heartbeat.IsAlive);
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
        else if (op == "pong")
        {
            Volatile.Write(ref _lastPongTick, Environment.TickCount64);
        }
    }

    private void HeartbeatLoop(object? state)
    {
        var ping = SidecarProtocol.PingFrame();
        while (_running)
        {
            Thread.Sleep(PingIntervalMs);
            if (!_running) break;
            try
            {
                lock (_gate)
                {
                    if (_stream == null) break;
                    _stream.Write(ping, 0, ping.Length);
                    _stream.Flush();
                }
            }
            catch
            {
                SetHealth(CaptureHealth.Dead);
                break;
            }
            var sincePong = Environment.TickCount64 - Volatile.Read(ref _lastPongTick);
            if (sincePong > (long)PingIntervalMs * MissedPongLimit)
            {
                VoiceDiagnostics.Log("sidecar", $"heartbeat: {sincePong}ms since last pong -> Dead");
                SetHealth(CaptureHealth.Dead);
                break;
            }
        }
    }

    private void SetHealth(CaptureHealth health) => Volatile.Write(ref _health, (int)health);

    public void Dispose() => Stop();
}
