using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SIPSorcery.Net;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class SidecarVoiceClient : IDisposable
{
    public const int Proto = 4;
    private const int HandshakeTimeoutMs = 4000;
    private const int WriteTimeoutMs = 250;

    private readonly Func<string, string, SidecarLaunchResult> _launch;
    private readonly object _gate = new();
    private readonly object _writeLock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private SidecarLaunchResult? _launchResult;
    private int _health = (int)CaptureHealth.Dead;
    private volatile bool _running;
    private readonly float[] _frameScratch = new float[SidecarProtocol.AudioSamples];
    private string[] _outputDevices = Array.Empty<string>();
    private Thread? _reader;
    internal int PingIntervalMs = 1000;
    internal int MissedPongLimit = 3;
    private long _lastPongTick;
    private Thread? _heartbeat;
    private int _startGeneration;

    public event Action<float[], int>? OnFrame;
    public event Action<string>? OnDead;
    public event Action<string, string, string>? OnLocalSdp;
    public event Action<string, string>? OnLocalCandidate;
    public event Action<string, string>? OnPeerState;
    public event Action<float, bool>? OnLevel;
    private int _deadRaised;
    public CaptureHealth Health => (CaptureHealth)Volatile.Read(ref _health);
    public IReadOnlyList<string> OutputDevices => _outputDevices;

    public SidecarVoiceClient(Func<string, string, SidecarLaunchResult> launch)
    {
        _launch = launch;
    }

    public bool Start(string? micDevice, string? spkDevice)
    {
        Stop();
        _running = false;
        var generation = Volatile.Read(ref _startGeneration);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        SidecarLaunchResult launch;
        try
        {
            launch = _launch(token, micDevice ?? "");
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
        Volatile.Write(ref _launchResult, launch);

        TcpClient client;
        NetworkStream stream;
        try
        {
            client = new TcpClient();
            client.Connect(System.Net.IPAddress.Loopback, launch.Port);
            stream = client.GetStream();
            stream.ReadTimeout = HandshakeTimeoutMs;
            stream.WriteTimeout = WriteTimeoutMs;
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

            if (!ReadReady(stream, out var outputDevices, out var error))
            {
                VoiceDiagnostics.Log("sidecar", "handshake failed: " + error);
                try { client.Close(); } catch { }
                KillLaunch();
                SetHealth(CaptureHealth.Dead);
                return false;
            }
            _outputDevices = outputDevices;

            if (!string.IsNullOrEmpty(micDevice))
            {
                var sel = SidecarProtocol.SelectDeviceFrame(micDevice!);
                stream.Write(sel, 0, sel.Length);
                stream.Flush();
            }

            if (!string.IsNullOrEmpty(spkDevice))
            {
                var selOut = SidecarProtocol.SelectOutputDeviceFrame(spkDevice!);
                stream.Write(selOut, 0, selOut.Length);
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

        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "SidecarVoiceReader" };
        var heartbeat = new Thread(HeartbeatLoop) { IsBackground = true, Name = "SidecarVoiceHeartbeat" };
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

    private void Write(byte[] frame)
    {
        NetworkStream? s;
        lock (_gate) { s = _stream; }
        if (s == null) throw new System.IO.IOException("sidecar stream closed");
        lock (_writeLock)
        {
            s.Write(frame, 0, frame.Length);
            s.Flush();
        }
    }

    public void SendPlayback(float[] stereoBlock)
    {
        if (!_running) return;
        try
        {
            Write(SidecarProtocol.EncodeAudioOut(stereoBlock, stereoBlock.Length));
        }
        catch
        {
            RaiseDead("playback write failed");
        }
    }

    public void SetDsp(bool aec, bool agc, bool ns, bool hpf)
    {
        if (!_running) return;
        try { Write(SidecarProtocol.SetDspFrame(aec, agc, ns, hpf)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "set-dsp write failed: " + ex.Message); }
    }

    public void SetMicActive(bool active)
    {
        if (!_running) return;
        try { Write(active ? SidecarProtocol.StartFrame() : SidecarProtocol.StopFrame()); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "set-mic-active write failed: " + ex.Message); }
    }

    public void SelectMicDevice(string deviceId)
    {
        if (!_running || string.IsNullOrEmpty(deviceId)) return;
        try { Write(SidecarProtocol.SelectDeviceFrame(deviceId)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "select-device write failed: " + ex.Message); }
    }

    public void SelectOutputDevice(string deviceId)
    {
        if (!_running || string.IsNullOrEmpty(deviceId)) return;
        try { Write(SidecarProtocol.SelectOutputDeviceFrame(deviceId)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "select-output-device write failed: " + ex.Message); }
    }

    public void AddPeer(string peerId)
    {
        if (!_running || string.IsNullOrEmpty(peerId)) return;
        try { Write(SidecarProtocol.AddPeerFrame(peerId)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "peer-add write failed: " + ex.Message); }
    }

    public void RemovePeer(string peerId)
    {
        if (!_running || string.IsNullOrEmpty(peerId)) return;
        try { Write(SidecarProtocol.RemovePeerFrame(peerId)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "peer-remove write failed: " + ex.Message); }
    }

    public void SetRemoteSdp(string peerId, string sdpType, string sdp)
    {
        if (!_running || string.IsNullOrEmpty(peerId)) return;
        try { Write(SidecarProtocol.SetRemoteSdpFrame(peerId, sdpType, sdp)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "set-remote-sdp write failed: " + ex.Message); }
    }

    public void AddIceCandidate(string peerId, string candidate)
    {
        if (!_running || string.IsNullOrEmpty(peerId)) return;
        try { Write(SidecarProtocol.AddIceCandidateFrame(peerId, candidate)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "add-ice-candidate write failed: " + ex.Message); }
    }

    public void SetIceServers(IEnumerable<RTCIceServer> servers)
    {
        if (!_running || servers == null) return;
        try { Write(SidecarProtocol.SetIceServersFrame(servers)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "set-ice-servers write failed: " + ex.Message); }
    }

    public void SendGameState(
        float lx,
        float ly,
        float facing,
        bool deaf,
        float master,
        float maxDistance,
        int falloff,
        IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
    {
        if (!_running) return;
        try { Write(SidecarProtocol.GameStateFrame(lx, ly, facing, deaf, master, maxDistance, falloff, peers)); }
        catch (Exception ex) { VoiceDiagnostics.Log("sidecar", "game-state write failed: " + ex.Message); }
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
            if (have == buffer.Length)
            {
                if (buffer.Length >= SidecarProtocol.HeaderBytes + SidecarProtocol.MaxPayloadBytes)
                { error = "ready frame too large"; return false; }
                Array.Resize(ref buffer, buffer.Length * 2);
            }
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
                if (SidecarProtocol.TryReadOutputDevices(json, out var devs))
                    outputDevices = devs.ToArray();
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
                lock (_writeLock)
                {
                    stream.Write(stop, 0, stop.Length);
                    stream.Flush();
                }
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
        var launch = Interlocked.Exchange(ref _launchResult, null);
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
        else if (op == "local-sdp")
        {
            if (SidecarProtocol.TryReadLocalSdp(json, out var peerId, out var sdpType, out var sdp))
            {
                try { OnLocalSdp?.Invoke(peerId, sdpType, sdp); } catch { }
            }
        }
        else if (op == "local-candidate")
        {
            if (SidecarProtocol.TryReadLocalCandidate(json, out var peerId, out var candidate))
            {
                try { OnLocalCandidate?.Invoke(peerId, candidate); } catch { }
            }
        }
        else if (op == "peer-state")
        {
            if (SidecarProtocol.TryReadPeerState(json, out var peerId, out var state))
            {
                try { OnPeerState?.Invoke(peerId, state); } catch { }
            }
        }
        else if (op == "level")
        {
            if (SidecarProtocol.TryReadLevel(json, out var peak, out var speaking))
            {
                try { OnLevel?.Invoke(peak, speaking); } catch { }
            }
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
                Write(ping);
            }
            catch
            {
                RaiseDead("heartbeat write failed");
                break;
            }
            var sincePong = Environment.TickCount64 - Volatile.Read(ref _lastPongTick);
            if (sincePong > (long)PingIntervalMs * MissedPongLimit)
            {
                VoiceDiagnostics.Log("sidecar", $"heartbeat: {sincePong}ms since last pong -> Dead");
                RaiseDead("heartbeat pong timeout");
                break;
            }
        }
    }

    private void SetHealth(CaptureHealth health) => Volatile.Write(ref _health, (int)health);

    private void RaiseDead(string reason)
    {
        SetHealth(CaptureHealth.Dead);
        if (Interlocked.Exchange(ref _deadRaised, 1) == 0)
        {
            try { OnDead?.Invoke(reason); } catch { }
        }
    }

    public void Dispose() => Stop();
}
