#if ANDROID
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SIPSorcery.Net;

namespace VoiceChatPlugin.VoiceChat;

// Android voice transport backed by the in-process Rust core (libpc_mobile.so). Mirrors the
// role SidecarVoiceClient plays on desktop, but calls the engine over FFI instead of TCP and
// reuses SidecarProtocol verbatim: control JSON is the builder payload minus its frame header,
// and incoming signals are parsed by the same SidecarProtocol.TryRead* helpers.
internal sealed class MobileVoiceClient : IDisposable
{
    private const int MicFrame = SidecarProtocol.AudioSamples;         // 960 mono @ 48k (20ms)
    private const int PlaybackFrame = SidecarProtocol.AudioOutSamples; // 1920 interleaved stereo
    private const int SignalBufBytes = 64 * 1024;

    private IntPtr _h;
    private volatile bool _running;
    private Thread? _pollThread;
    private Thread? _pumpThread;

    private readonly object _ctrlLock = new();

    private readonly float[] _micAccum = new float[MicFrame];
    private int _micFill;
    private readonly object _micLock = new();

    // Playback ring (interleaved stereo): the pump thread fills it from pc_pull_playback at 20ms,
    // the Unity audio callback drains it via ReadPlayback. ponytail: one short lock, fine for audio.
    private readonly float[] _ring = new float[PlaybackFrame * 8]; // ~160ms cushion
    private int _ringRead, _ringWrite, _ringCount;
    private readonly object _ringLock = new();
    private readonly float[] _pumpBuf = new float[PlaybackFrame];

    public event Action<string, string, string>? OnLocalSdp;
    public event Action<string, string>? OnLocalCandidate;
    public event Action<string, string>? OnPeerState;
    public event Action<float, bool>? OnLevel;

    public bool IsRunning => _running && _h != IntPtr.Zero;

    public bool Start()
    {
        if (!PcMobileLoader.EnsureLoaded()) return false;
        _h = PcMobileNative.pc_engine_new();
        if (_h == IntPtr.Zero)
        {
            VoiceDiagnostics.DebugWarning("[VC] pc_engine_new returned null");
            return false;
        }
        _running = true;
        _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "PcMobilePoll" };
        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "PcMobilePump" };
        _pollThread.Start();
        _pumpThread.Start();
        VoiceDiagnostics.DebugInfo("[VC] MobileVoiceClient started");
        return true;
    }

    private void Control(byte[] framed)
    {
        var h = _h;
        if (h == IntPtr.Zero) return;
        int jsonLen = framed.Length - SidecarProtocol.HeaderBytes;
        if (jsonLen <= 0) return;
        var json = new byte[jsonLen + 1];
        Array.Copy(framed, SidecarProtocol.HeaderBytes, json, 0, jsonLen);
        json[jsonLen] = 0;
        lock (_ctrlLock) PcMobileNative.pc_control(h, json);
    }

    public void AddPeer(string peerId, bool isOfferer) => Control(SidecarProtocol.AddPeerFrame(peerId, isOfferer));
    public void RemovePeer(string peerId) => Control(SidecarProtocol.RemovePeerFrame(peerId));
    public void SetRemoteSdp(string peerId, string sdpType, string sdp) => Control(SidecarProtocol.SetRemoteSdpFrame(peerId, sdpType, sdp));
    public void AddIceCandidate(string peerId, string candidate) => Control(SidecarProtocol.AddIceCandidateFrame(peerId, candidate));
    public void SetIceServers(IEnumerable<RTCIceServer> servers) => Control(SidecarProtocol.SetIceServersFrame(servers));
    public void SetDsp(bool aec, bool agc, bool ns, bool hpf) => Control(SidecarProtocol.SetDspFrame(aec, agc, ns, hpf));
    public void SendGameState(bool deaf, float master, IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
        => Control(SidecarProtocol.GameStateFrame(deaf, master, peers));

    // Mic floats (mono 48k); accumulated into 960-sample frames and pushed to the engine, which
    // runs DSP + Opus + WebRTC send. Returns nothing; OnLevel fires per pushed frame.
    public void PushMic(float[] mono, int count)
    {
        var h = _h;
        if (h == IntPtr.Zero || mono == null || count <= 0) return;
        lock (_micLock)
        {
            int i = 0;
            while (i < count)
            {
                int take = Math.Min(MicFrame - _micFill, count - i);
                Array.Copy(mono, i, _micAccum, _micFill, take);
                _micFill += take;
                i += take;
                if (_micFill == MicFrame)
                {
                    float peak = PcMobileNative.pc_push_mic(h, _micAccum, MicFrame);
                    _micFill = 0;
                    OnLevel?.Invoke(peak, peak > 0.02f);
                }
            }
        }
    }

    // Drain the playback ring into an interleaved-stereo buffer for the Unity audio callback.
    public void ReadPlayback(float[] interleavedStereo)
    {
        int n = interleavedStereo.Length;
        lock (_ringLock)
        {
            int avail = Math.Min(n, _ringCount);
            for (int i = 0; i < avail; i++)
            {
                interleavedStereo[i] = _ring[_ringRead];
                _ringRead = (_ringRead + 1) % _ring.Length;
            }
            _ringCount -= avail;
            for (int i = avail; i < n; i++) interleavedStereo[i] = 0f;
        }
    }

    private void PumpLoop()
    {
        while (_running)
        {
            var h = _h;
            int got = h != IntPtr.Zero ? PcMobileNative.pc_pull_playback(h, _pumpBuf, PlaybackFrame) : 0;
            if (got > 0)
            {
                lock (_ringLock)
                {
                    for (int i = 0; i < got; i++)
                    {
                        if (_ringCount >= _ring.Length)
                        {
                            _ringRead = (_ringRead + 1) % _ring.Length;
                            _ringCount--;
                        }
                        _ring[_ringWrite] = _pumpBuf[i];
                        _ringWrite = (_ringWrite + 1) % _ring.Length;
                        _ringCount++;
                    }
                }
            }
            Thread.Sleep(got > 0 ? 10 : 5);
        }
    }

    private void PollLoop()
    {
        var buf = new byte[SignalBufBytes];
        while (_running)
        {
            var h = _h;
            int got = h != IntPtr.Zero ? PcMobileNative.pc_poll_signal(h, buf, buf.Length) : 0;
            if (got > 0)
            {
                Dispatch(Encoding.UTF8.GetString(buf, 0, got));
                continue;
            }
            Thread.Sleep(5);
        }
    }

    private void Dispatch(string json)
    {
        switch (SidecarProtocol.ReadOp(json))
        {
            case "local-sdp":
                if (SidecarProtocol.TryReadLocalSdp(json, out var sp, out var st, out var sd)) OnLocalSdp?.Invoke(sp, st, sd);
                break;
            case "local-candidate":
                if (SidecarProtocol.TryReadLocalCandidate(json, out var cp, out var cc)) OnLocalCandidate?.Invoke(cp, cc);
                break;
            case "peer-state":
                if (SidecarProtocol.TryReadPeerState(json, out var pp, out var ps)) OnPeerState?.Invoke(pp, ps);
                break;
            case "level":
                if (SidecarProtocol.TryReadLevel(json, out var peak, out var speaking)) OnLevel?.Invoke(peak, speaking);
                break;
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _pollThread?.Join(250); } catch { }
        try { _pumpThread?.Join(250); } catch { }
        var h = _h;
        _h = IntPtr.Zero;
        if (h != IntPtr.Zero) PcMobileNative.pc_engine_free(h);
        VoiceDiagnostics.DebugInfo("[VC] MobileVoiceClient disposed");
    }
}
#endif
