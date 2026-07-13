using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class SidecarVoiceClient : ISidecarVoiceClient
{
    // Protocol 7 adds native input gain/VAD, runtime synthetic capture, and batched remote levels.
    // Reject older helpers so managed settings and speaking telemetry cannot silently become no-ops.
    public const int Proto = 7;
    private const int HandshakeTimeoutMs = 4000;
    private const int WriteTimeoutMs = 250;
    private const int GameStateLogIntervalMs = 5000;

    private readonly Func<string, string, SidecarLaunchResult> _launch;
    private readonly object _gate = new();
    private readonly object _writeLock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private SidecarLaunchResult? _launchResult;
    private int _health = (int)CaptureHealth.Dead;
    private volatile bool _running;
    private readonly float[] _frameScratch = new float[SidecarProtocol.AudioSamples];
    private volatile string[] _outputDevices = Array.Empty<string>();
    private Thread? _reader;
    internal int PingIntervalMs = 1000;
    internal int MissedPongLimit = 3;
    private long _lastPongTick;
    private Thread? _heartbeat;
    private int _startGeneration;
    private long _lastGameStateLogTick;
    private int _suppressedGameStateLogs;

    public event Action<float[], int>? OnFrame;
    public event Action<string>? OnDead;
    public event Action<string, string>? OnRecoverableError;
    public event Action<string, int, string, string>? OnLocalSdp;
    public event Action<string, int, string>? OnLocalCandidate;
    public event Action<string, int, string>? OnPeerState;
    public event Action<float, bool>? OnLevel;
    public event Action<IReadOnlyList<SidecarProtocol.PeerLevel>>? OnPeerLevels;
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
        VoiceDiagnostics.Log(
            "sidecar.lifecycle",
            $"event=start-request proto={Proto} generation={generation} input={DescribeDeviceForDiagnostics(micDevice)} output={DescribeDeviceForDiagnostics(spkDevice)}");
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
        VoiceDiagnostics.Log(
            "sidecar.lifecycle",
            $"event=launch-ready proto={Proto} pid={launch.Pid} port={launch.Port} handshake=\"{SafeDiagnosticText(launch.HandshakePath, 320)}\"");

        TcpClient client;
        NetworkStream stream;
        try
        {
            VoiceDiagnostics.Log("sidecar.lifecycle", $"event=connect-begin pid={launch.Pid} port={launch.Port}");
            client = new TcpClient();
            client.Connect(System.Net.IPAddress.Loopback, launch.Port);
            stream = client.GetStream();
            stream.ReadTimeout = HandshakeTimeoutMs;
            stream.WriteTimeout = WriteTimeoutMs;
            VoiceDiagnostics.Log("sidecar.lifecycle", $"event=connect-ok pid={launch.Pid} port={launch.Port}");
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
            WriteHandshakeCommand(stream, hello, "hello", $"proto={Proto}");

            if (!ReadReady(stream, out var outputDevices, out var error))
            {
                VoiceDiagnostics.Log("sidecar", "handshake failed: " + error);
                try { client.Close(); } catch { }
                KillLaunch();
                SetHealth(CaptureHealth.Dead);
                return false;
            }
            _outputDevices = outputDevices;
            VoiceDiagnostics.Log(
                "sidecar.lifecycle",
                $"event=protocol-ready proto={Proto} rate=48000 sample=f32 outputDevices={outputDevices.Length}");

            // The authenticated control/media engine is useful even when this client is muted or
            // has no microphone. Device/output selection and capture start are applied exactly once
            // by TryConfigureInitialCapture after current policy is known; doing any of them during
            // the handshake could block control readiness on a wedged host audio API.
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar", "handshake exception: " + ex.Message);
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
        VoiceDiagnostics.Log("sidecar.lifecycle", $"event=running proto={Proto} pid={launch.Pid}");
        return true;
    }

    private static void WriteHandshakeCommand(NetworkStream stream, byte[] frame, string op, string details)
    {
        try
        {
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
            LogCommand(op, "written", frame.Length, details);
        }
        catch (Exception ex)
        {
            LogCommand(op, "failed", frame.Length, details + " error=" + ExceptionDiagnostic(ex));
            throw;
        }
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

    private bool SendCommand(
        string op,
        Func<byte[]> frameFactory,
        string details,
        bool logSuccess = true,
        bool logFailure = true)
    {
        if (!_running)
        {
            if (logFailure)
                LogCommand(op, "rejected", 0, details + " reason=not-running");
            return false;
        }

        byte[]? frame = null;
        try
        {
            frame = frameFactory();
            Write(frame);
            if (logSuccess)
                LogCommand(op, "written", frame.Length, details);
            return true;
        }
        catch (Exception ex)
        {
            if (logFailure)
                LogCommand(op, "failed", frame?.Length ?? 0, details + " error=" + ExceptionDiagnostic(ex));
            return false;
        }
    }

    public void SetDsp(bool aec, bool agc, bool ns, bool hpf)
    {
        SendCommand(
            "set-dsp",
            () => SidecarProtocol.SetDspFrame(aec, agc, ns, hpf),
            $"aec={aec} agc={agc} ns={ns} hpf={hpf}");
    }

    public void SetSynthetic(bool enabled)
    {
        SendCommand("set-synthetic", () => SidecarProtocol.SetSyntheticFrame(enabled), $"enabled={enabled}");
    }

    public void SetInput(float gain, float vadThreshold)
    {
        SendCommand(
            "set-input",
            () => SidecarProtocol.SetInputFrame(gain, vadThreshold),
            $"gain={FormatFloat(gain)} vadThreshold={FormatFloat(vadThreshold)}");
    }

    public bool TryConfigureInitialCapture(
        string micDevice,
        string outputDevice,
        bool aec,
        bool agc,
        bool ns,
        bool hpf,
        float gain,
        float vadThreshold,
        bool synthetic,
        bool micActive,
        IEnumerable<IceServer>? iceServers)
    {
        if (!_running)
        {
            LogCommand("initial-config", "rejected", 0, "reason=not-running");
            return false;
        }

        // Start() opens capture as part of the existing helper lifecycle. Quiesce it before
        // applying the latest settings so a device change made during handshake cannot be
        // lost and no frame is encoded with stale gain/VAD/synthetic state.
        if (!SendCommand("stop", SidecarProtocol.StopFrame, "phase=initial-config")) return false;
        // Empty ids explicitly mean the OS default device; they are not "no change".
        if (!SendCommand(
                "select-device",
                () => SidecarProtocol.SelectDeviceFrame(micDevice ?? string.Empty),
                DescribeDeviceForDiagnostics(micDevice))) return false;
        if (!SendCommand(
                "select-output-device",
                () => SidecarProtocol.SelectOutputDeviceFrame(outputDevice ?? string.Empty),
                DescribeDeviceForDiagnostics(outputDevice))) return false;
        if (!SendCommand(
                "set-dsp",
                () => SidecarProtocol.SetDspFrame(aec, agc, ns, hpf),
                $"aec={aec} agc={agc} ns={ns} hpf={hpf}")) return false;
        if (!SendCommand(
                "set-input",
                () => SidecarProtocol.SetInputFrame(gain, vadThreshold),
                $"gain={FormatFloat(gain)} vadThreshold={FormatFloat(vadThreshold)}")) return false;
        if (!SendCommand(
                "set-synthetic",
                () => SidecarProtocol.SetSyntheticFrame(synthetic),
                $"enabled={synthetic}")) return false;
        if (iceServers != null)
        {
            var snapshot = SnapshotIceServers(iceServers);
            if (!SendCommand(
                    "set-ice-servers",
                    () => SidecarProtocol.SetIceServersFrame(snapshot),
                    DescribeIceServers(snapshot))) return false;
        }
        if (micActive && !SendCommand("start", SidecarProtocol.StartFrame, "phase=initial-config")) return false;

        VoiceDiagnostics.Log(
            "sidecar.command",
            $"op=initial-config result=complete micActive={micActive} input={DescribeDeviceForDiagnostics(micDevice)} output={DescribeDeviceForDiagnostics(outputDevice)}");
        return true;
    }

    public void SetMicActive(bool active)
    {
        SendCommand(active ? "start" : "stop", active ? SidecarProtocol.StartFrame : SidecarProtocol.StopFrame, $"micActive={active}");
    }

    public void SelectMicDevice(string deviceId)
    {
        SendCommand(
            "select-device",
            () => SidecarProtocol.SelectDeviceFrame(deviceId ?? string.Empty),
            DescribeDeviceForDiagnostics(deviceId));
    }

    public void SelectOutputDevice(string deviceId)
    {
        SendCommand(
            "select-output-device",
            () => SidecarProtocol.SelectOutputDeviceFrame(deviceId ?? string.Empty),
            DescribeDeviceForDiagnostics(deviceId));
    }

    public void AddPeer(string peerId, bool isOfferer, bool relayOnly, int generation)
    {
        if (string.IsNullOrEmpty(peerId))
        {
            LogCommand("peer-add", "rejected", 0, $"generation={generation} reason=missing-peer");
            return;
        }
        SendCommand(
            "peer-add",
            () => SidecarProtocol.AddPeerFrame(peerId, isOfferer, relayOnly, generation),
            $"{DescribePeer(peerId)} generation={generation} offerer={isOfferer} relayOnly={relayOnly}");
    }

    public void RemovePeer(string peerId)
    {
        if (string.IsNullOrEmpty(peerId))
        {
            LogCommand("peer-remove", "rejected", 0, "reason=missing-peer");
            return;
        }
        SendCommand("peer-remove", () => SidecarProtocol.RemovePeerFrame(peerId), DescribePeer(peerId));
    }

    public void SetRemoteSdp(string peerId, string sdpType, string sdp)
    {
        if (string.IsNullOrEmpty(peerId))
        {
            LogCommand("set-remote-sdp", "rejected", 0, $"type=\"{SafeDiagnosticText(sdpType, 32)}\" sdpBytes={Utf8Bytes(sdp)} reason=missing-peer");
            return;
        }
        SendCommand(
            "set-remote-sdp",
            () => SidecarProtocol.SetRemoteSdpFrame(peerId, sdpType, sdp),
            $"{DescribePeer(peerId)} type=\"{SafeDiagnosticText(sdpType, 32)}\" sdpBytes={Utf8Bytes(sdp)}");
    }

    public void AddIceCandidate(string peerId, string candidate)
    {
        if (string.IsNullOrEmpty(peerId))
        {
            LogCommand("add-ice-candidate", "rejected", 0, $"candidateBytes={Utf8Bytes(candidate)} reason=missing-peer");
            return;
        }
        SendCommand(
            "add-ice-candidate",
            () => SidecarProtocol.AddIceCandidateFrame(peerId, candidate),
            $"{DescribePeer(peerId)} candidateBytes={Utf8Bytes(candidate)}");
    }

    public void SetIceServers(IEnumerable<IceServer> servers)
    {
        if (servers == null)
        {
            LogCommand("set-ice-servers", "rejected", 0, "reason=null-servers");
            return;
        }
        var snapshot = SnapshotIceServers(servers);
        SendCommand(
            "set-ice-servers",
            () => SidecarProtocol.SetIceServersFrame(snapshot),
            DescribeIceServers(snapshot));
    }

    public void SendGameState(
        bool deaf,
        float master,
        IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
    {
        var diagnosticsEnabled = VoiceDiagnostics.IsEnabled;
        if (!_running)
        {
            if (diagnosticsEnabled && ShouldLogGameState(out var notRunningSuppressed))
                LogCommand(
                    "game-state",
                    "rejected",
                    0,
                    $"reason=not-running suppressedSinceLast={notRunningSuppressed} rateLimitMs={GameStateLogIntervalMs}");
            return;
        }

        var suppressed = 0;
        var shouldLog = diagnosticsEnabled && ShouldLogGameState(out suppressed);
        var details = shouldLog ? DescribeGameState(deaf, master, peers) : string.Empty;
        byte[] frame;
        try
        {
            frame = SidecarProtocol.GameStateFrame(deaf, master, peers);
        }
        catch (Exception ex)
        {
            if (shouldLog)
                LogCommand("game-state", "failed", 0, details + " error=" + ExceptionDiagnostic(ex));
            return;
        }
        if (!SendCommand(
                "game-state",
                () => frame,
                details,
                logSuccess: false,
                logFailure: shouldLog)) return;
        if (shouldLog)
            LogCommand("game-state", "written", frame.Length, details + $" suppressedSinceLast={suppressed} rateLimitMs={GameStateLogIntervalMs}");
    }

    private bool ShouldLogGameState(out int suppressed)
    {
        suppressed = 0;
        var now = Environment.TickCount64;
        while (true)
        {
            var last = Volatile.Read(ref _lastGameStateLogTick);
            if (last != 0 && now - last < GameStateLogIntervalMs)
            {
                Interlocked.Increment(ref _suppressedGameStateLogs);
                return false;
            }
            if (Interlocked.CompareExchange(ref _lastGameStateLogTick, now, last) == last)
            {
                suppressed = Interlocked.Exchange(ref _suppressedGameStateLogs, 0);
                return true;
            }
        }
    }

    private static string DescribeGameState(
        bool deaf,
        float master,
        IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
    {
        var audible = 0;
        var muted = 0;
        for (var i = 0; i < peers.Count; i++)
        {
            if (peers[i].Gain > 0f) audible++;
            else muted++;
        }
        return $"deaf={deaf} master={FormatFloat(master)} peers={peers.Count} audible={audible} muted={muted}";
    }

    private static List<IceServer> SnapshotIceServers(IEnumerable<IceServer> servers)
    {
        var snapshot = new List<IceServer>();
        foreach (var server in servers)
            snapshot.Add(server);
        return snapshot;
    }

    private static string DescribeIceServers(IReadOnlyList<IceServer> servers)
    {
        var usable = 0;
        var authenticated = 0;
        for (var i = 0; i < servers.Count; i++)
        {
            if (string.IsNullOrEmpty(servers[i].Urls)) continue;
            usable++;
            if (!string.IsNullOrEmpty(servers[i].Username) || !string.IsNullOrEmpty(servers[i].Credential))
                authenticated++;
        }
        return $"servers={usable} authenticated={authenticated}";
    }

    internal static string DescribeDeviceForDiagnostics(string? deviceId)
        => VoiceDiagnostics.DescribeDevice(deviceId);

    private static string DescribePeer(string peerId)
        => $"peer=\"{SafeDiagnosticText(peerId, 64)}\"";

    private static string DescribeControlMetadata(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = string.Empty;
            if (root.TryGetProperty("peer_id", out var peer) && peer.ValueKind == JsonValueKind.String)
                result += " " + DescribePeer(peer.GetString() ?? string.Empty);
            if (root.TryGetProperty("generation", out var generation) && generation.TryGetInt32(out var generationValue))
                result += $" generation={generationValue}";
            if (root.TryGetProperty("sdp_type", out var sdpType) && sdpType.ValueKind == JsonValueKind.String)
                result += $" type=\"{SafeDiagnosticText(sdpType.GetString(), 32)}\"";
            return result;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void LogCommand(string op, string result, int frameBytes, string details)
        => VoiceDiagnostics.Log(
            "sidecar.command",
            $"op={SafeDiagnosticText(op, 48)} result={SafeDiagnosticText(result, 24)} frameBytes={frameBytes} {details}");

    private static int Utf8Bytes(string? value)
        => string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

    private static string FormatFloat(float value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static string ExceptionDiagnostic(Exception ex)
        => $"{SafeDiagnosticText(ex.GetType().Name, 80)}:\"{SafeDiagnosticText(ex.Message, 240)}\"";

    private static string SafeDiagnosticText(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var length = Math.Min(value.Length, maxChars);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) || c == '"' ? ' ' : c);
        }
        if (value.Length > maxChars)
            builder.Append("...");
        return builder.ToString();
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
        var wasRunning = _running;
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
                LogCommand("stop", "written", stop.Length, "phase=shutdown");
            }
            catch (Exception ex)
            {
                LogCommand("stop", "failed", 0, "phase=shutdown error=" + ExceptionDiagnostic(ex));
            }
        }
        try { client?.Close(); } catch { }
        KillLaunch();
        JoinWorker(reader);
        JoinWorker(heartbeat);
        SetHealth(CaptureHealth.Dead);
        if (wasRunning || stream != null)
            VoiceDiagnostics.Log("sidecar.lifecycle", "event=stopped health=Dead");
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
        var killed = false;
        var exited = false;
        var hostTerminationRequested = false;
        try
        {
            if (launch.Process != null && !launch.Process.HasExited)
            {
                launch.Process.Kill();
                killed = true;
                try { exited = launch.Process.WaitForExit(2000); } catch { }
            }
            else
                exited = launch.Process?.HasExited ?? true;

            // Under Wine/CrossOver, launch.Process is start.exe rather than the native Unix
            // helper. The authenticated stop frame above is graceful; SIGTERM is the final
            // process-level backstop so a broken IPC connection cannot leave pc-capture behind
            // after the room lease ends. launch.Pid comes from the helper-owned handshake file.
            if (WineEnvironment.IsWine && launch.Pid > 0)
            {
                WineEnvironment.HostExec("/bin/kill", $"-TERM {launch.Pid}");
                hostTerminationRequested = true;
            }
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar.lifecycle", $"event=process-kill-failed pid={launch.Pid} error={ExceptionDiagnostic(ex)}");
        }
        finally
        {
            launch.Diagnostics?.Complete("client-stop");
        }
        try
        {
            if (!string.IsNullOrEmpty(launch.HandshakePath) && System.IO.File.Exists(launch.HandshakePath))
                System.IO.File.Delete(launch.HandshakePath);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar.lifecycle", $"event=handshake-cleanup-failed pid={launch.Pid} error={ExceptionDiagnostic(ex)}");
        }
        VoiceDiagnostics.Log(
            "sidecar.lifecycle",
            $"event=process-release pid={launch.Pid} launcherKilled={killed} launcherExited={exited} hostTerminationRequested={hostTerminationRequested}");
    }

    private void ReadLoop(object? state)
    {
        var stream = (NetworkStream)state!;
        try { stream.ReadTimeout = Timeout.Infinite; } catch { }
        var buffer = new byte[1 << 16];
        var have = 0;
        var exitReason = "stopped";
        while (_running)
        {
            int read;
            try { read = stream.Read(buffer, have, buffer.Length - have); }
            catch (Exception ex)
            {
                exitReason = "read-exception";
                if (_running)
                    VoiceDiagnostics.Log("sidecar.reader", $"event=read-failed bufferedBytes={have} error={ExceptionDiagnostic(ex)}");
                break;
            }
            if (read <= 0)
            {
                exitReason = "end-of-stream";
                if (_running)
                    VoiceDiagnostics.Log("sidecar.reader", $"event=stream-closed bufferedBytes={have}");
                break;
            }
            have += read;

            if (have >= SidecarProtocol.HeaderBytes)
            {
                var declaredPayloadBytes = (uint)(buffer[1] | (buffer[2] << 8) | (buffer[3] << 16) | (buffer[4] << 24));
                if (declaredPayloadBytes > SidecarProtocol.MaxPayloadBytes)
                {
                    exitReason = "payload-too-large";
                    VoiceDiagnostics.Log(
                        "sidecar.frame.reject",
                        $"reason=payload-too-large type={buffer[0]} declaredPayloadBytes={declaredPayloadBytes} bufferedBytes={have} cap={SidecarProtocol.MaxPayloadBytes}");
                    break;
                }
            }

            while (SidecarProtocol.TryParseFrame(buffer, have, out var type, out var off, out var len, out var flen))
            {
                if (type == SidecarProtocol.TypeAudio)
                {
                    if (SidecarProtocol.TryDecodeAudio(buffer, off, len, _frameScratch, out _, out var count))
                    {
                        try { OnFrame?.Invoke(_frameScratch, count); }
                        catch (Exception ex)
                        {
                            VoiceDiagnostics.Log("sidecar.event", $"op=audio event=handler-failed samples={count} error={ExceptionDiagnostic(ex)}");
                        }
                    }
                    else
                        VoiceDiagnostics.Log("sidecar.frame.reject", $"reason=invalid-audio payloadBytes={len}");
                }
                else if (type == SidecarProtocol.TypeControl)
                {
                    var json = Encoding.UTF8.GetString(buffer, off, len);
                    HandleStreamingControl(json, len);
                }
                else
                    VoiceDiagnostics.Log("sidecar.frame.reject", $"reason=unknown-type type={type} payloadBytes={len}");
                var remaining = have - flen;
                if (remaining > 0)
                    Buffer.BlockCopy(buffer, flen, buffer, 0, remaining);
                have = remaining;
            }

            if (have == buffer.Length)
            {

                var cap = SidecarProtocol.HeaderBytes + SidecarProtocol.MaxPayloadBytes;
                if (buffer.Length >= cap)
                {
                    exitReason = "frame-buffer-cap";
                    VoiceDiagnostics.Log(
                        "sidecar.frame.reject",
                        $"reason=incomplete-frame-at-cap bufferedBytes={have} cap={cap} type={buffer[0]}");
                    break;
                }
                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, cap));
            }
        }
        if (_running)
        {
            VoiceDiagnostics.Log("sidecar.lifecycle", $"event=reader-ended reason={exitReason} health=Dead bufferedBytes={have}");
            SetHealth(CaptureHealth.Dead);
        }
    }

    private void HandleStreamingControl(string json, int payloadBytes)
    {
        var op = SidecarProtocol.ReadOp(json);
        if (string.IsNullOrEmpty(op))
        {
            VoiceDiagnostics.Log(
                "sidecar.control.reject",
                $"reason=invalid-json-or-missing-op payloadBytes={payloadBytes}{DescribeControlMetadata(json)}");
            return;
        }
        if (op == "error")
        {
            if (!SidecarProtocol.TryReadError(json, out var code, out var msg))
            {
                VoiceDiagnostics.Log("sidecar.control.reject", $"op=error reason=invalid-fields payloadBytes={payloadBytes}");
                return;
            }
            VoiceDiagnostics.Log(
                "sidecar.helper-error",
                $"code=\"{SafeDiagnosticText(code, 64)}\" message=\"{SafeDiagnosticText(msg, 240)}\" payloadBytes={payloadBytes}{DescribeControlMetadata(json)}");
            // The native capture loop reports mic-error once per outage, then keeps retrying the
            // input device while the WebRTC/control connection and speaker remain healthy. Treating
            // that recoverable device outage as transport death poisoned Health without raising the
            // restart callback, permanently stopping managed signaling even after capture recovered.
            if (IsRecoverableHelperError(code))
            {
                VoiceDiagnostics.Log(
                    "sidecar.helper-error.recoverable",
                    $"code=\"{SafeDiagnosticText(code, 64)}\" action=keep-control-transport-alive nativeRetry=true");
                try { OnRecoverableError?.Invoke(code, msg); }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log(
                        "sidecar.event",
                        $"op=recoverable-error event=handler-failed error={ExceptionDiagnostic(ex)}");
                }
                return;
            }

            // Unknown streaming errors are fatal unless the protocol explicitly classifies them as
            // recoverable. RaiseDead (rather than merely changing Health) gives the host/backend one
            // coherent restart path and preserves the exactly-once dead notification contract.
            RaiseDead($"helper error code={SafeDiagnosticText(code, 64)}");
        }
        else if (op == "pong")
        {
            Volatile.Write(ref _lastPongTick, Environment.TickCount64);
        }
        else if (op == "local-sdp")
        {
            if (SidecarProtocol.TryReadLocalSdp(json, out var peerId, out var generation, out var sdpType, out var sdp))
            {
                VoiceDiagnostics.Log(
                    "sidecar.control.rx",
                    $"op=local-sdp result=parsed {DescribePeer(peerId)} generation={generation} type=\"{SafeDiagnosticText(sdpType, 32)}\" sdpBytes={Utf8Bytes(sdp)} payloadBytes={payloadBytes}");
                try { OnLocalSdp?.Invoke(peerId, generation, sdpType, sdp); }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log(
                        "sidecar.event",
                        $"op=local-sdp event=handler-failed {DescribePeer(peerId)} generation={generation} error={ExceptionDiagnostic(ex)}");
                }
            }
            else
                VoiceDiagnostics.Log(
                    "sidecar.control.reject",
                    $"op=local-sdp reason=invalid-fields payloadBytes={payloadBytes}{DescribeControlMetadata(json)}");
        }
        else if (op == "local-candidate")
        {
            if (SidecarProtocol.TryReadLocalCandidate(json, out var peerId, out var generation, out var candidate))
            {
                VoiceDiagnostics.Log(
                    "sidecar.control.rx",
                    $"op=local-candidate result=parsed {DescribePeer(peerId)} generation={generation} candidateBytes={Utf8Bytes(candidate)} payloadBytes={payloadBytes}");
                try { OnLocalCandidate?.Invoke(peerId, generation, candidate); }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log(
                        "sidecar.event",
                        $"op=local-candidate event=handler-failed {DescribePeer(peerId)} generation={generation} error={ExceptionDiagnostic(ex)}");
                }
            }
            else
                VoiceDiagnostics.Log(
                    "sidecar.control.reject",
                    $"op=local-candidate reason=invalid-fields payloadBytes={payloadBytes}{DescribeControlMetadata(json)}");
        }
        else if (op == "peer-state")
        {
            if (SidecarProtocol.TryReadPeerState(json, out var peerId, out var generation, out var state))
            {
                VoiceDiagnostics.Log(
                    "sidecar.control.rx",
                    $"op=peer-state result=parsed {DescribePeer(peerId)} generation={generation} state=\"{SafeDiagnosticText(state, 48)}\" payloadBytes={payloadBytes}");
                try { OnPeerState?.Invoke(peerId, generation, state); }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log(
                        "sidecar.event",
                        $"op=peer-state event=handler-failed {DescribePeer(peerId)} generation={generation} state=\"{SafeDiagnosticText(state, 48)}\" error={ExceptionDiagnostic(ex)}");
                }
            }
            else
                VoiceDiagnostics.Log(
                    "sidecar.control.reject",
                    $"op=peer-state reason=invalid-fields payloadBytes={payloadBytes}{DescribeControlMetadata(json)}");
        }
        else if (op == "level")
        {
            if (SidecarProtocol.TryReadLevel(json, out var peak, out var speaking))
            {
                try { OnLevel?.Invoke(peak, speaking); }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("sidecar.event", $"op=level event=handler-failed error={ExceptionDiagnostic(ex)}");
                }
            }
            else
                VoiceDiagnostics.Log("sidecar.control.reject", $"op=level reason=invalid-fields payloadBytes={payloadBytes}");
        }
        else if (op == "peer-levels")
        {
            if (SidecarProtocol.TryReadPeerLevels(json, out var levels))
            {
                try { OnPeerLevels?.Invoke(levels); }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("sidecar.event", $"op=peer-levels event=handler-failed peers={levels.Count} error={ExceptionDiagnostic(ex)}");
                }
            }
            else
                VoiceDiagnostics.Log("sidecar.control.reject", $"op=peer-levels reason=invalid-fields payloadBytes={payloadBytes}");
        }
        else if (op == "devices")
        {
            if (!TryReadDeviceUpdate(json, out var inputDevices, out var outputDevices))
            {
                VoiceDiagnostics.Log(
                    "sidecar.control.reject",
                    $"op=devices reason=invalid-fields payloadBytes={payloadBytes}{DescribeControlMetadata(json)}");
                return;
            }

            _outputDevices = outputDevices;
#if WINDOWS
            // Device-selection and synthetic-capture changes can alter the live lists. Publish the
            // helper's authoritative response instead of leaving the settings UI on its startup probe.
            VoiceChatLocalSettings.SetMicDeviceNamesFromSidecar(inputDevices);
            VoiceChatLocalSettings.SetSpkDeviceNamesFromSidecar(outputDevices);
#endif
            VoiceDiagnostics.Log(
                "sidecar.control.rx",
                $"op=devices result=parsed inputs={inputDevices.Length} outputs={outputDevices.Length} payloadBytes={payloadBytes}");
        }
        else if (op == "stats")
        {
            HandleNativeStats(json, payloadBytes);
        }
        else
            VoiceDiagnostics.Log(
                "sidecar.control.reject",
                $"op=\"{SafeDiagnosticText(op, 64)}\" reason=unknown-op payloadBytes={payloadBytes}{DescribeControlMetadata(json)}");
    }

    internal static bool TryReadDeviceUpdate(string json, out string[] inputDevices, out string[] outputDevices)
    {
        inputDevices = Array.Empty<string>();
        outputDevices = Array.Empty<string>();
        if (!SidecarProtocol.TryReadDevices(json, out var inputs)
            || !SidecarProtocol.TryReadOutputDevices(json, out var outputs))
            return false;

        inputDevices = inputs.ToArray();
        outputDevices = outputs.ToArray();
        return true;
    }

    private static void HandleNativeStats(string json, int payloadBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!TryReadU64(root, "capture_frames", out var captureFrames) ||
                !TryReadU64(root, "opus_encoded", out var opusEncoded) ||
                !TryReadU64(root, "rtp_tx_ok", out var rtpTxOk) ||
                !TryReadU64(root, "rtp_rx_packets", out var rtpRxPackets) ||
                !TryReadU64(root, "decode_frames", out var decodeFrames) ||
                !TryReadU64(root, "mix_rounds", out var mixRounds) ||
                !TryReadU64(root, "playback_queued_pairs", out var playbackQueuedPairs))
            {
                VoiceDiagnostics.Log("sidecar.control.reject", $"op=stats reason=missing-required-fields payloadBytes={payloadBytes}");
                return;
            }

            VoiceDiagnostics.Log(
                "sidecar.native.stats",
                $"captureFrames={captureFrames} captureDropped={ReadU64(root, "capture_ring_dropped")} " +
                $"opusEncoded={opusEncoded} opusEmpty={ReadU64(root, "opus_empty")} opusErrors={ReadU64(root, "opus_errors")} " +
                $"rtpTxAttempts={ReadU64(root, "rtp_tx_attempts")} rtpTxOk={rtpTxOk} rtpTxErrors={ReadU64(root, "rtp_tx_errors")} " +
                $"rtpRxPackets={rtpRxPackets} rtpRxBytes={ReadU64(root, "rtp_rx_bytes")} staleRtpDropped={ReadU64(root, "stale_rtp_rx_dropped")} " +
                $"decodePackets={ReadU64(root, "decode_packets")} decodeFrames={decodeFrames} decodeEmpty={ReadU64(root, "decode_empty")} decodeErrors={ReadU64(root, "decode_errors")} " +
                $"peerLevelBatches={ReadU64(root, "peer_level_batches")} mixRounds={mixRounds} mixedPeerFrames={ReadU64(root, "mixed_peer_frames")} " +
                $"mixNonzeroRounds={ReadU64(root, "mix_nonzero_rounds")} mixSilentRounds={ReadU64(root, "mix_silent_rounds")} mixSamples={ReadU64(root, "mix_samples")} mixNonzeroSamples={ReadU64(root, "mix_nonzero_samples")} mixPeak={ReadDouble(root, "mix_peak"):0.000000} mixRms={ReadDouble(root, "mix_rms"):0.000000} jitterIdleTicks={ReadU64(root, "jitter_idle_ticks")} " +
                $"gameStateUpdates={ReadU64(root, "game_state_updates")} appliedDeaf={ReadBool(root, "applied_deaf").ToString().ToLowerInvariant()} appliedMaster={ReadDouble(root, "applied_master"):0.000} appliedPeers={ReadU64(root, "applied_peer_count")} appliedNonzeroGainPeers={ReadU64(root, "applied_nonzero_gain_peers")} " +
                $"playbackQueuedPairs={playbackQueuedPairs} playbackSpawnAttempts={ReadU64(root, "playback_spawn_attempts")} playbackStarts={ReadU64(root, "playback_starts")} playbackStops={ReadU64(root, "playback_stops")} playbackErrors={ReadU64(root, "playback_errors")} playbackCallbackErrors={ReadU64(root, "playback_callback_errors")} " +
                $"playbackCallbacks={ReadU64(root, "playback_callbacks")} playbackRequestedPairs={ReadU64(root, "playback_requested_pairs")} playbackConsumedPairs={ReadU64(root, "playback_consumed_pairs")} playbackUnderrunPairs={ReadU64(root, "playback_underrun_pairs")} " +
                $"playbackLockContentionCallbacks={ReadU64(root, "playback_lock_contention_callbacks")} playbackLockContentionSilencePairs={ReadU64(root, "playback_lock_contention_silence_pairs")} playbackOutputNonzeroSamples={ReadU64(root, "playback_output_nonzero_samples")} playbackOutputPeak={ReadDouble(root, "playback_output_peak"):0.000000} " +
                $"playbackRingLen={ReadU64(root, "playback_ring_len")} playbackDropped={ReadU64(root, "playback_ring_dropped")} payloadBytes={payloadBytes}");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log("sidecar.control.reject", $"op=stats reason=invalid-json payloadBytes={payloadBytes} error={ExceptionDiagnostic(ex)}");
        }
    }

    private static bool TryReadU64(JsonElement root, string name, out ulong value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.TryGetUInt64(out value);
    }

    private static ulong ReadU64(JsonElement root, string name)
        => TryReadU64(root, name, out var value) ? value : 0;

    private static double ReadDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var property) && property.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : 0;

    private static bool ReadBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var property) &&
           (property.ValueKind == JsonValueKind.True ||
            (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) && value != 0));

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
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("sidecar.heartbeat", $"event=write-failed error={ExceptionDiagnostic(ex)}");
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

    internal static bool IsRecoverableHelperError(string? code)
        => string.Equals(code, "mic-error", StringComparison.OrdinalIgnoreCase);

    private void RaiseDead(string reason)
    {
        SetHealth(CaptureHealth.Dead);
        VoiceDiagnostics.Log("sidecar.lifecycle", $"event=dead reason=\"{SafeDiagnosticText(reason, 160)}\"");
        if (Interlocked.Exchange(ref _deadRaised, 1) == 0)
        {
            try { OnDead?.Invoke(reason); }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("sidecar.event", $"op=dead event=handler-failed error={ExceptionDiagnostic(ex)}");
            }
        }
    }

    public void Dispose() => Stop();
}
