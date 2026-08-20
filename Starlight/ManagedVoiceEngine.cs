using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PerfectComms.Starlight.Media;

public sealed class ManagedVoiceEngine : IDisposable
{
    private const int FrameSamples = ManagedOpusEncoder.FrameSamples;
    private const int StereoFrameSamples = FrameSamples * 2;
    private const int PlaybackCapacitySamples = StereoFrameSamples * 16;
    private const int PlaybackPrimeSamples = StereoFrameSamples * 4;
    private const int PlaybackMaximumLatencySamples = StereoFrameSamples * 8;
    private const int MaximumPeers = 32;
    private const int MaximumConcealFrames = 5;
    private const int ShutdownTimeoutMilliseconds = 2_000;
    private const int EventQueueCapacity = 256;

    private readonly object _lifecycleGate = new();
    private readonly object _playbackGate = new();
    private readonly object _shutdownGate = new();
    private readonly object _peerGate = new();
    private readonly object _micGate = new();
    private readonly object _mixerGate = new();
    private readonly Dictionary<string, PeerContext> _peers = new(StringComparer.Ordinal);
    private readonly ManagedVoiceMixer _mixer = new();
    private readonly PlaybackRing _playback = new(PlaybackCapacitySamples);
    private readonly ConcurrentQueue<EngineEvent> _events = new();
    private readonly AutoResetEvent _eventReady = new(false);
    private readonly float[] _micFrame = new float[FrameSamples];
    private readonly float[] _silence = new float[FrameSamples];
    private readonly byte[] _encoded = new byte[ManagedOpusEncoder.MaxPacketBytes];
    private readonly float[] _mixedStereo = new float[StereoFrameSamples];
    private readonly List<DecodedPeerFrame> _decodedFrames = new(32);
    private readonly List<ManagedPeerLevel> _peerLevels = new(32);
    private ManagedWebRtcPeer[] _sendSnapshot = new ManagedWebRtcPeer[8];
    private ManagedIceServer[] _iceServers = Array.Empty<ManagedIceServer>();
    private CancellationTokenSource? _stopSource;
    private ManagedOpusEncoder? _encoder;
    private Thread? _playbackThread;
    private Thread? _eventThread;
    private int _micFill;
    private ulong _pendingCaptureGap;
    private float _inputGain = 1f;
    private float _vadThreshold = 0.015f;
    private float _noiseGateThreshold = 0.005f;
    private double _syntheticPhase;
    private float _levelPeak;
    private bool _levelSpeaking;
    private int _levelFrameCount;
    private bool _deafened;
    private long _playbackVersion;
    private int _running;
    private int _micActive;
    private int _synthetic;
    private int _eventDispatchRunning;
    private int _eventCount;
    private int _eventReadyDisposed;
    private int _disposeEventReadyOnExit;
    private int _diagnosticsEnabled;
    private int _disposed;
    private int _failed;
    private long _playbackPumpLateCycles;
    private long _playbackEmptyPulls;
    private long _encodedFrames;
    private long _sentPackets;
    private long _sendFailures;
    private long _receivedPackets;
    private long _rejectedPackets;

    public event Action<string, int, string, string>? LocalSdp;
    public event Action<string, int, string>? LocalCandidate;
    public event Action<string, int, string>? PeerState;
    public event Action<float, bool>? LocalLevel;
    public event Action<IReadOnlyList<ManagedPeerLevel>>? PeerLevels;

    public bool IsRunning => Volatile.Read(ref _running) != 0 && Volatile.Read(ref _disposed) == 0;
    public int PlaybackDepthSamples => _playback.DepthSamples;
    public long PlaybackHighWaterSamples => _playback.HighWaterSamples;
    public long PlaybackDroppedSamples => _playback.DroppedSamples;
    public long PlaybackZeroFilledSamples => _playback.ZeroFilledSamples;
    public long PlaybackSkippedSamples => _playback.SkippedSamples;
    public long PlaybackPrimingZeroFilledSamples => _playback.PrimingZeroFilledSamples;
    public long PlaybackClockCorrectionSamples => _playback.ClockCorrectionSamples;
    public long PlaybackClockCorrectionCallbacks => _playback.ClockCorrectionCallbacks;
    public long PlaybackPumpLateCycles => Interlocked.Read(ref _playbackPumpLateCycles);
    public long PlaybackEmptyPulls => Interlocked.Read(ref _playbackEmptyPulls);
    public bool DiagnosticsEnabled => Volatile.Read(ref _diagnosticsEnabled) != 0;
    internal static int MaximumPendingEvents => EventQueueCapacity;
    internal int PendingEventCount => Volatile.Read(ref _eventCount);
    public long EncodedFrames => Interlocked.Read(ref _encodedFrames);
    public long SentPackets => Interlocked.Read(ref _sentPackets);
    public long SendFailures => Interlocked.Read(ref _sendFailures);
    public long ReceivedPackets => Interlocked.Read(ref _receivedPackets);
    public long RejectedPackets => Interlocked.Read(ref _rejectedPackets);

    public bool Start()
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _failed) != 0)
                return false;
            if (IsRunning)
                return true;

            try
            {
                lock (_micGate)
                {
                    _encoder = new ManagedOpusEncoder();
                    _encoder.Configure(_inputGain, _vadThreshold, _noiseGateThreshold);
                    _encoder.Reset();
                    ResetMicInputLocked();
                }
                lock (_playbackGate)
                {
                    _playback.Reset();
                    _deafened = false;
                }
                _stopSource = new CancellationTokenSource();
                Volatile.Write(ref _eventDispatchRunning, 1);
                Volatile.Write(ref _running, 1);
                _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "PerfectCommsManagedEvents" };
                _playbackThread = new Thread(PlaybackLoop) { IsBackground = true, Name = "PerfectCommsManagedPlayback" };
                _eventThread.Start();
                _playbackThread.Start();
                return true;
            }
            catch
            {
                Volatile.Write(ref _micActive, 0);
                Volatile.Write(ref _running, 0);
                Volatile.Write(ref _eventDispatchRunning, 0);
                _stopSource?.Cancel();
                _eventReady.Set();
                bool playbackStopped = JoinBounded(_playbackThread);
                bool eventStopped = JoinBounded(_eventThread);
                if (playbackStopped) _playbackThread = null;
                if (eventStopped) _eventThread = null;
                if (!playbackStopped || !eventStopped) Volatile.Write(ref _failed, 1);
                if (playbackStopped)
                {
                    _stopSource?.Dispose();
                    _stopSource = null;
                }
                lock (_micGate)
                {
                    _encoder?.Dispose();
                    _encoder = null;
                    ResetMicInputLocked();
                }
                return false;
            }
        }
    }

    public bool AddPeer(string peerId, bool isOfferer, int generation)
    {
        if (!IsRunning || string.IsNullOrEmpty(peerId) || generation <= 0)
            return false;

        ManagedIceServer[] servers;
        lock (_peerGate)
        {
            if (_peers.TryGetValue(peerId, out PeerContext? existing))
            {
                if (existing.Peer.Generation == generation)
                    return true;
                if (existing.Peer.Generation > generation)
                    return false;
            }
            else if (_peers.Count >= MaximumPeers)
            {
                return false;
            }
            servers = _iceServers;
        }

        ManagedWebRtcPeer peer;
        try { peer = new ManagedWebRtcPeer(peerId, generation, servers); }
        catch { return false; }

        var context = new PeerContext(peer);
        peer.LocalSdp += context.OnLocalSdp = (type, sdp) =>
            Enqueue(EngineEvent.ForSdp(peerId, generation, type, sdp));
        peer.LocalCandidate += context.OnLocalCandidate = candidate =>
            Enqueue(EngineEvent.ForCandidate(peerId, generation, candidate));
        peer.StateChanged += context.OnStateChanged = state =>
        {
            if (!string.Equals(state, "connected", StringComparison.Ordinal))
                context.RequestMediaReset();
            Enqueue(EngineEvent.ForState(peerId, generation, state));
        };
        peer.OpusPacketReceived += context.OnOpusPacket = (ssrc, sequence, timestamp, payload) =>
        {
            bool accepted;
            bool dropped = false;
            lock (_peerGate)
            {
                accepted = _peers.TryGetValue(peerId, out PeerContext? current) &&
                    ReferenceEquals(current, context) &&
                    context.TryAcceptOpus(ssrc, sequence, timestamp, payload, out dropped);
            }
            if (accepted)
                Interlocked.Increment(ref _receivedPackets);
            if (!accepted || dropped)
                Interlocked.Increment(ref _rejectedPackets);
        };

        bool added = false;
        bool duplicate = false;
        PeerContext? replaced = null;
        lock (_micGate)
        {
            if (IsRunning)
            {
                lock (_peerGate)
                {
                    if (ReferenceEquals(servers, _iceServers))
                    {
                        if (_peers.TryGetValue(peerId, out PeerContext? current))
                        {
                            if (current.Peer.Generation == generation)
                            {
                                duplicate = true;
                            }
                            else if (current.Peer.Generation < generation)
                            {
                                _encoder!.Reset();
                                _peers[peerId] = context;
                                replaced = current;
                                added = true;
                            }
                        }
                        else if (_peers.Count < MaximumPeers)
                        {
                            EnsureSendSnapshotCapacity(_peers.Count + 1);
                            _encoder!.Reset();
                            _peers.Add(peerId, context);
                            added = true;
                        }
                    }
                }
            }
        }
        if (!added)
        {
            context.Dispose();
            return duplicate;
        }
        replaced?.Dispose();

        if (peer.Start(isOfferer))
            return true;
        RemovePeer(peerId, generation);
        return false;
    }

    public bool RemovePeer(string peerId, int generation)
    {
        if (string.IsNullOrEmpty(peerId) || generation <= 0)
            return false;

        PeerContext? removed;
        bool transmissionClosed;
        lock (_micGate)
        {
            lock (_peerGate)
            {
                if (!_peers.TryGetValue(peerId, out removed) || removed.Peer.Generation != generation)
                    return false;
                _peers.Remove(peerId);
                transmissionClosed = !AnyConnectedPeerLocked();
            }
            if (transmissionClosed) _encoder?.Reset();
        }

        lock (_mixerGate) _mixer.RemovePeer(peerId);
        removed.Dispose();
        return true;
    }

    public bool RestartIce(string peerId, int generation, bool createOffer) => FindPeer(peerId, generation)?.RestartIce(createOffer) == true;
    public bool SetRemoteSdp(string peerId, int generation, string sdpType, string sdp) => FindPeer(peerId, generation)?.SetRemoteSdp(sdpType, sdp) == true;
    public bool AddIceCandidate(string peerId, int generation, string candidate) => FindPeer(peerId, generation)?.AddIceCandidate(candidate) == true;

    public void SetIceServers(IReadOnlyList<ManagedIceServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var copy = new ManagedIceServer[servers.Count];
        for (int i = 0; i < servers.Count; i++) copy[i] = servers[i];

        ManagedWebRtcPeer[] affectedPeers;
        lock (_peerGate)
        {
            if (IceServersEqual(_iceServers, copy))
                return;
            _iceServers = copy;
            if (_peers.Count == 0)
                return;
            affectedPeers = new ManagedWebRtcPeer[_peers.Count];
            int index = 0;
            foreach (PeerContext context in _peers.Values)
                affectedPeers[index++] = context.Peer;
        }
        for (int i = 0; i < affectedPeers.Length; i++)
            affectedPeers[i].FailForIceServerChange();
    }

    public void SetDiagnostics(bool enabled) => Volatile.Write(ref _diagnosticsEnabled, enabled ? 1 : 0);

    public void SetMicActive(bool active)
    {
        Volatile.Write(ref _micActive, 0);
        lock (_micGate)
        {
            ResetMicInputLocked();
            _encoder?.Reset();
            if (active && IsRunning) Volatile.Write(ref _micActive, 1);
        }
    }

    public void SetInput(float gain, float vadThreshold, float noiseGateThreshold)
    {
        if (!float.IsFinite(gain) || !float.IsFinite(vadThreshold) || !float.IsFinite(noiseGateThreshold) || gain < 0f || vadThreshold < 0f || vadThreshold > 1f || noiseGateThreshold < 0f || noiseGateThreshold > 1f)
            throw new ArgumentOutOfRangeException(nameof(gain));
        lock (_micGate)
        {
            _inputGain = gain;
            _vadThreshold = vadThreshold;
            _noiseGateThreshold = noiseGateThreshold;
            _encoder?.Configure(gain, vadThreshold, noiseGateThreshold);
        }
    }

    public void SetSynthetic(bool enabled) => Volatile.Write(ref _synthetic, enabled ? 1 : 0);

    public void ConfigureGameState(bool deafened, float master, IReadOnlyList<ManagedPeerRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        lock (_mixerGate) _mixer.Configure(deafened, master, routes);
        lock (_playbackGate)
        {
            if (deafened && !_deafened)
                _playback.DiscardQueued();
            _deafened = deafened;
            Interlocked.Increment(ref _playbackVersion);
        }
    }

    public void PushMic(float[] samples, int count, ulong skippedBeforeCurrent)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if ((uint)count > (uint)samples.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count <= 0 || Volatile.Read(ref _micActive) == 0 || !IsRunning) return;
        lock (_micGate)
        {
            if (Volatile.Read(ref _micActive) == 0 || !IsRunning || _encoder is null) return;
            if (skippedBeforeCurrent != 0)
            {
                _micFrame.AsSpan().Clear();
                _micFill = 0;
                _pendingCaptureGap = SaturatingAdd(_pendingCaptureGap, skippedBeforeCurrent);
            }
            int sourceOffset = 0;
            while (sourceOffset < count)
            {
                int take = Math.Min(FrameSamples - _micFill, count - sourceOffset);
                samples.AsSpan(sourceOffset, take).CopyTo(_micFrame.AsSpan(_micFill, take));
                sourceOffset += take;
                _micFill += take;
                if (_micFill != FrameSamples) continue;
                if (Volatile.Read(ref _synthetic) != 0) FillSyntheticFrame();
                EncodeAndSend(_pendingCaptureGap);
                _pendingCaptureGap = 0;
                _micFill = 0;
            }
        }
    }

    public void ReadPlayback(float[] buffer, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)count > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count <= 0) return;
        lock (_playbackGate)
            if (_playback.Read(buffer.AsSpan(0, count))) Interlocked.Increment(ref _playbackEmptyPulls);
    }

    internal void QueuePlaybackForTest(float[] samples)
    {
        lock (_playbackGate) _playback.Write(samples);
    }

    internal bool EnqueueLocalLevelForTest() => Enqueue(EngineEvent.ForLocalLevel(0f, false));
    internal bool EnqueuePeerStateForTest() => Enqueue(EngineEvent.ForState("event-overflow", 1, "failed"));
    internal Action? BeforeMixerWorkForTest { get; set; }
    internal Action? BeforePlaybackWriteForTest { get; set; }
    internal void PumpPlaybackFrameForTest() => PumpPlaybackFrame();
    internal void AddPeerForTest(string peerId)
    {
        var peer = new ManagedWebRtcPeer(peerId, 1, Array.Empty<ManagedIceServer>());
        lock (_peerGate) _peers.Add(peerId, new PeerContext(peer));
    }
    internal bool IngestOpusForTest(
        string peerId,
        int generation,
        uint ssrc,
        ushort sequence,
        uint timestamp,
        byte[] payload)
    {
        lock (_peerGate)
        {
            if (!_peers.TryGetValue(peerId, out PeerContext? context) ||
                context.Peer.Generation != generation)
                return false;
            return context.TryAcceptOpus(
                ssrc,
                sequence,
                timestamp,
                new ArraySegment<byte>(payload),
                out _);
        }
    }

    internal float PeerDecodedPeakForTest(string peerId)
    {
        lock (_peerGate)
        {
            if (!_peers.TryGetValue(peerId, out PeerContext? context))
                return 0f;
            float peak = 0f;
            for (int i = 0; i < context.DecodedSamples.Length; i++)
                peak = Math.Max(peak, Math.Abs(context.DecodedSamples[i]));
            return peak;
        }
    }

    internal int PeerSourceEpochForTest(string peerId)
    {
        lock (_peerGate)
            return _peers.TryGetValue(peerId, out PeerContext? context)
                ? context.SourceEpoch
                : -1;
    }

    public void ResetMicInput() { lock (_micGate) ResetMicInputLocked(); }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_shutdownGate)
            {
                Volatile.Write(ref _disposeEventReadyOnExit, 1);
                Volatile.Write(ref _eventDispatchRunning, 0);
                SignalEventLoop();
                _stopSource?.Cancel();
            }
            Volatile.Write(ref _micActive, 0);
            Volatile.Write(ref _running, 0);
            bool playbackStopped = JoinBounded(_playbackThread);
            if (playbackStopped) _playbackThread = null;

            PeerContext[] peers;
            lock (_peerGate)
            {
                peers = new PeerContext[_peers.Count];
                _peers.Values.CopyTo(peers, 0);
                _peers.Clear();
            }
            for (int i = 0; i < peers.Length; i++) peers[i].Dispose();

            bool eventStopped = JoinBounded(_eventThread);
            if (eventStopped)
                _eventThread = null;
            lock (_micGate)
            {
                _encoder?.Reset();
                _encoder?.Dispose();
                _encoder = null;
                ResetMicInputLocked();
            }
            lock (_playbackGate)
            {
                lock (_mixerGate) _mixer.Reset();
                _playback.DiscardQueued();
                _deafened = false;
            }
            lock (_shutdownGate)
            {
                if (playbackStopped)
                {
                    _stopSource?.Dispose();
                    _stopSource = null;
                }
                if (eventStopped) DisposeEventReady();
            }
        }
    }

    private ManagedWebRtcPeer? FindPeer(string peerId, int generation)
    {
        if (string.IsNullOrEmpty(peerId) || generation <= 0 || !IsRunning) return null;
        lock (_peerGate)
            return _peers.TryGetValue(peerId, out PeerContext? context) && context.Peer.Generation == generation ? context.Peer : null;
    }

    private void EnsureSendSnapshotCapacity(int required)
    {
        if (_sendSnapshot.Length >= required) return;
        int size = _sendSnapshot.Length;
        while (size < required) size *= 2;
        _sendSnapshot = new ManagedWebRtcPeer[size];
    }

    private void EncodeAndSend(ulong skippedBeforeCurrent)
    {
        ManagedOpusEncoder encoder = _encoder!;
        int peerCount = SnapshotConnectedPeers();
        if (skippedBeforeCurrent > MaximumConcealFrames)
        {
            encoder.Reset();
        }
        else
        {
            for (ulong gap = 0; gap < skippedBeforeCurrent; gap++)
                encoder.Encode(_silence, _encoded, out _, out _);
        }
        if (skippedBeforeCurrent != 0)
            AdvanceSnapshotTimestamps(peerCount, CaptureGapDuration(skippedBeforeCurrent));

        int length = encoder.Encode(_micFrame, _encoded, out float peak, out bool speaking);
        Interlocked.Increment(ref _encodedFrames);
        SendToSnapshot(peerCount, length);
        ObserveLocalLevel(peak, speaking);
    }

    private int SnapshotConnectedPeers()
    {
        int count = 0;
        lock (_peerGate)
            foreach (PeerContext context in _peers.Values)
                if (context.Peer.IsConnected) _sendSnapshot[count++] = context.Peer;
        return count;
    }

    private void AdvanceSnapshotTimestamps(int peerCount, uint durationRtpUnits)
    {
        for (int i = 0; i < peerCount; i++)
        {
            if (!_sendSnapshot[i].AdvanceAudioTimestamp(durationRtpUnits))
                Interlocked.Increment(ref _sendFailures);
        }
    }

    private void SendToSnapshot(int peerCount, int length)
    {
        for (int i = 0; i < peerCount; i++)
        {
            if (_sendSnapshot[i].SendEncodedOpus(_encoded, length)) Interlocked.Increment(ref _sentPackets);
            else Interlocked.Increment(ref _sendFailures);
            _sendSnapshot[i] = null!;
        }
    }

    private void ObserveLocalLevel(float peak, bool speaking)
    {
        _levelPeak = Math.Max(_levelPeak, peak);
        _levelSpeaking |= speaking;
        if (++_levelFrameCount < 5) return;
        Enqueue(EngineEvent.ForLocalLevel(_levelPeak, _levelSpeaking));
        _levelPeak = 0f;
        _levelSpeaking = false;
        _levelFrameCount = 0;
    }

    private void FillSyntheticFrame()
    {
        double increment = Math.Tau * 440d / ManagedOpusEncoder.SampleRate;
        for (int i = 0; i < FrameSamples; i++)
        {
            _micFrame[i] = (float)(Math.Sin(_syntheticPhase) * 0.1d);
            _syntheticPhase += increment;
            if (_syntheticPhase >= Math.Tau) _syntheticPhase -= Math.Tau;
        }
    }

    private void ResetMicInputLocked()
    {
        _micFrame.AsSpan().Clear();
        _micFill = 0;
        _pendingCaptureGap = 0;
        _syntheticPhase = 0d;
        _levelPeak = 0f;
        _levelSpeaking = false;
        _levelFrameCount = 0;
    }

    private void PlaybackLoop()
    {
        CancellationToken stop = _stopSource!.Token;
        long interval = Stopwatch.Frequency / 50;
        long next = Stopwatch.GetTimestamp();
        int levelCadence = 0;
        while (!stop.IsCancellationRequested)
        {
            next += interval;
            PumpPlaybackFrame();
            if (++levelCadence == 5) { levelCadence = 0; PublishPeerLevels(); }
            long remaining = next - Stopwatch.GetTimestamp();
            if (remaining > 0)
            {
                int milliseconds = (int)(remaining * 1_000 / Stopwatch.Frequency);
                if (milliseconds > 0) stop.WaitHandle.WaitOne(milliseconds);
                while (!stop.IsCancellationRequested && Stopwatch.GetTimestamp() < next) Thread.SpinWait(32);
            }
            else
            {
                Interlocked.Increment(ref _playbackPumpLateCycles);
                next = Stopwatch.GetTimestamp();
            }
        }
    }

    private void PumpPlaybackFrame()
    {
        _decodedFrames.Clear();
        long now = Environment.TickCount64;
        lock (_peerGate)
        {
            foreach (PeerContext context in _peers.Values)
            {
                context.DrainIngress(now);
                RtpJitterDecision decision = context.Jitter.GetDecision(now, context.PacketScratch);
                int decoded;
                bool measurementEligible;
                try
                {
                    if (decision.Kind == RtpJitterDecisionKind.Discontinuity)
                    {
                        context.ResetDecoder();
                        decision = context.Jitter.GetDecision(now, context.PacketScratch);
                    }
                    switch (decision.Kind)
                    {
                        case RtpJitterDecisionKind.Packet:
                            context.ConcealmentFrames = 0;
                            decoded = context.Decoder.Decode(context.PacketScratch.AsSpan(0, decision.PayloadLength), context.DecodedSamples);
                            measurementEligible = true;
                            break;
                        case RtpJitterDecisionKind.Fec:
                            context.ConcealmentFrames++;
                            decoded = context.Decoder.DecodeFec(context.PacketScratch.AsSpan(0, decision.PayloadLength), context.DecodedSamples);
                            measurementEligible = false;
                            break;
                        case RtpJitterDecisionKind.Plc:
                            if (++context.ConcealmentFrames > MaximumConcealFrames)
                            {
                                context.ResetMedia();
                                continue;
                            }
                            decoded = context.Decoder.DecodePlc(context.DecodedSamples);
                            measurementEligible = false;
                            break;
                        case RtpJitterDecisionKind.Discontinuity:
                            context.ResetDecoder();
                            continue;
                        default: continue;
                    }
                }
                catch { context.ResetMedia(); continue; }
                _decodedFrames.Add(new DecodedPeerFrame(context.Peer.PeerId, context.DecodedSamples, decoded, measurementEligible));
            }
        }
        long playbackVersion = Interlocked.Read(ref _playbackVersion);
        lock (_mixerGate)
        {
            BeforeMixerWorkForTest?.Invoke();
            _mixer.Mix(_decodedFrames, _mixedStereo, _peerLevels);
        }
        BeforePlaybackWriteForTest?.Invoke();
        lock (_playbackGate)
        {
            if (playbackVersion == Interlocked.Read(ref _playbackVersion))
                _playback.Write(_mixedStereo);
        }
    }

    private void PublishPeerLevels()
    {
        if (_peerLevels.Count == 0) return;
        ManagedPeerLevel[] copy = ArrayPool<ManagedPeerLevel>.Shared.Rent(_peerLevels.Count);
        _peerLevels.CopyTo(copy, 0);
        if (!Enqueue(EngineEvent.ForPeerLevels(copy, _peerLevels.Count)))
            ArrayPool<ManagedPeerLevel>.Shared.Return(copy, clearArray: true);
    }

    private void EventLoop()
    {
        try
        {
            while (Volatile.Read(ref _eventDispatchRunning) != 0 ||
                   Volatile.Read(ref _eventCount) != 0)
            {
                if (!_events.TryDequeue(out EngineEvent e))
                {
                    _eventReady.WaitOne(100);
                    continue;
                }
                Interlocked.Decrement(ref _eventCount);
                if (Volatile.Read(ref _eventDispatchRunning) == 0 ||
                    Volatile.Read(ref _disposed) != 0)
                {
                    ReleaseEvent(e);
                    continue;
                }
                try
                {
                    switch (e.Kind)
                    {
                        case EngineEventKind.LocalSdp:
                            if (IsCurrentGeneration(e.PeerId!, e.Generation))
                                LocalSdp?.Invoke(e.PeerId!, e.Generation, e.Value!, e.Sdp!);
                            break;
                        case EngineEventKind.LocalCandidate:
                            if (IsCurrentGeneration(e.PeerId!, e.Generation))
                                LocalCandidate?.Invoke(e.PeerId!, e.Generation, e.Value!);
                            break;
                        case EngineEventKind.PeerState:
                            if (IsCurrentGeneration(e.PeerId!, e.Generation))
                            {
                                ResetEncoderIfTransmissionClosed();
                                PeerState?.Invoke(e.PeerId!, e.Generation, e.Value!);
                            }
                            break;
                        case EngineEventKind.LocalLevel:
                            LocalLevel?.Invoke(e.Peak, e.Speaking);
                            break;
                        case EngineEventKind.PeerLevels:
                            PeerLevels?.Invoke(e.Levels);
                            break;
                    }
                }
                catch
                {
                }
                finally
                {
                    ReleaseEvent(e);
                }
            }
        }
        finally
        {
            while (_events.TryDequeue(out EngineEvent e))
            {
                Interlocked.Decrement(ref _eventCount);
                ReleaseEvent(e);
            }
            if (Volatile.Read(ref _disposeEventReadyOnExit) != 0)
                DisposeEventReady();
        }
    }

    private static void ReleaseEvent(EngineEvent e)
    {
        if (e.Kind == EngineEventKind.PeerLevels && e.Levels.Array is not null)
            ArrayPool<ManagedPeerLevel>.Shared.Return(e.Levels.Array, clearArray: true);
    }

    private bool IsCurrentGeneration(string peerId, int generation)
    {
        lock (_peerGate)
            return _peers.TryGetValue(peerId, out PeerContext? context) &&
                context.Peer.Generation == generation;
    }

    private void ResetEncoderIfTransmissionClosed()
    {
        lock (_micGate)
        {
            lock (_peerGate) if (AnyConnectedPeerLocked()) return;
            _encoder?.Reset();
        }
    }

    private bool AnyConnectedPeerLocked()
    {
        foreach (PeerContext context in _peers.Values) if (context.Peer.IsConnected) return true;
        return false;
    }

    private bool Enqueue(EngineEvent e)
    {
        if (Volatile.Read(ref _eventDispatchRunning) == 0) return false;
        while (true)
        {
            int count = Volatile.Read(ref _eventCount);
            if (count >= EventQueueCapacity)
            {
                if (!e.IsTelemetry) FailEventQueue();
                return false;
            }
            if (Interlocked.CompareExchange(ref _eventCount, count + 1, count) == count)
                break;
        }
        if (Volatile.Read(ref _eventDispatchRunning) == 0)
        {
            Interlocked.Decrement(ref _eventCount);
            return false;
        }
        _events.Enqueue(e);
        SignalEventLoop();
        return true;
    }

    private void FailEventQueue()
    {
        lock (_shutdownGate)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _failed, 1);
            Volatile.Write(ref _micActive, 0);
            Volatile.Write(ref _running, 0);
            _stopSource?.Cancel();
            Volatile.Write(ref _eventDispatchRunning, 0);
            SignalEventLoop();
        }
    }

    private static bool IceServersEqual(ManagedIceServer[] left, ManagedIceServer[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int i = 0; i < left.Length; i++)
            if (left[i] != right[i])
                return false;
        return true;
    }

    private static uint CaptureGapDuration(ulong skippedFrames)
    {
        ulong maximumFrames = uint.MaxValue / (uint)FrameSamples;
        return skippedFrames > maximumFrames
            ? uint.MaxValue
            : (uint)skippedFrames * (uint)FrameSamples;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) { ulong sum = left + right; return sum < left ? ulong.MaxValue : sum; }
    private static bool JoinBounded(Thread? thread)
    {
        if (thread is null || !thread.IsAlive) return true;
        if (thread == Thread.CurrentThread) return false;
        return thread.Join(ShutdownTimeoutMilliseconds);
    }

    private void SignalEventLoop()
    {
        lock (_shutdownGate)
        {
            if (Volatile.Read(ref _eventReadyDisposed) == 0)
                _eventReady.Set();
        }
    }

    private void DisposeEventReady()
    {
        lock (_shutdownGate)
        {
            if (Interlocked.Exchange(ref _eventReadyDisposed, 1) == 0)
                _eventReady.Dispose();
        }
    }

    private sealed class PeerContext : IDisposable
    {
        public readonly ManagedWebRtcPeer Peer;
        public readonly ManagedOpusDecoder Decoder = new();
        public readonly RtpJitterBuffer Jitter = new();
        public readonly RtpIngressRing Ingress = new(64, ManagedOpusEncoder.MaxPacketBytes);
        public readonly byte[] PacketScratch = new byte[ManagedOpusEncoder.MaxPacketBytes];
        public readonly float[] DecodedSamples = new float[FrameSamples];
        public int ConcealmentFrames;
        private int _resetMediaRequested;
        public int SourceEpoch;
        private int _sourceResetRequested;
        public Action<string, string>? OnLocalSdp;
        public Action<string>? OnLocalCandidate;
        public Action<string>? OnStateChanged;
        public Action<uint, ushort, uint, ArraySegment<byte>>? OnOpusPacket;
        private readonly HashSet<uint> _retiredSources = new();
        private uint _activeSource;
        private bool _hasActiveSource;
        public PeerContext(ManagedWebRtcPeer peer) => Peer = peer;
        public bool TryAcceptOpus(
            uint ssrc,
            ushort sequence,
            uint timestamp,
            ArraySegment<byte> payload,
            out bool dropped)
        {
            dropped = false;
            if (!_hasActiveSource)
            {
                _activeSource = ssrc;
                _hasActiveSource = true;
            }
            else if (_activeSource != ssrc)
            {
                if (_retiredSources.Contains(ssrc) || _retiredSources.Count >= MaximumPeers)
                    return false;
                _retiredSources.Add(_activeSource);
                Interlocked.Exchange(ref _resetMediaRequested, 0);
                Ingress.Reset();
                Volatile.Write(ref _sourceResetRequested, 1);
                _activeSource = ssrc;
            }
            return Ingress.TryWrite(sequence, timestamp, payload, out dropped);
        }
        public void DrainIngress(long now)
        {
            if (Interlocked.Exchange(ref _resetMediaRequested, 0) != 0)
            {
                ResetMedia();
                _retiredSources.Clear();
                _hasActiveSource = false;
                Volatile.Write(ref _sourceResetRequested, 0);
            }
            else if (Interlocked.Exchange(ref _sourceResetRequested, 0) != 0)
            {
                ResetDecoder();
                Jitter.Reset();
                SourceEpoch++;
            }
            while (Ingress.TryRead(PacketScratch, out ushort sequence, out uint timestamp, out int length))
                Jitter.Push(sequence, timestamp, PacketScratch.AsSpan(0, length), now);
        }
        public void RequestMediaReset() => Volatile.Write(ref _resetMediaRequested, 1);
        public void ResetDecoder()
        {
            Decoder.Reset();
            DecodedSamples.AsSpan().Clear();
            ConcealmentFrames = 0;
        }
        public void ResetMedia()
        {
            ResetDecoder();
            Jitter.Reset();
            Ingress.Reset();
        }
        public void Dispose()
        {
            if (OnLocalSdp is not null) Peer.LocalSdp -= OnLocalSdp;
            if (OnLocalCandidate is not null) Peer.LocalCandidate -= OnLocalCandidate;
            if (OnStateChanged is not null) Peer.StateChanged -= OnStateChanged;
            if (OnOpusPacket is not null) Peer.OpusPacketReceived -= OnOpusPacket;
            Peer.Dispose(); Decoder.Dispose(); Jitter.Reset(); Ingress.Reset(); _retiredSources.Clear();
        }
    }

    internal sealed class RtpIngressRing
    {
        private readonly object _gate = new();
        private readonly int _capacity, _payloadCapacity;
        private readonly byte[] _payloads;
        private readonly ushort[] _sequences;
        private readonly uint[] _timestamps;
        private readonly int[] _lengths;
        private long _read, _write, _dropped;
        public RtpIngressRing(int capacity, int payloadCapacity) { _capacity = capacity; _payloadCapacity = payloadCapacity; _payloads = new byte[checked(capacity * payloadCapacity)]; _sequences = new ushort[capacity]; _timestamps = new uint[capacity]; _lengths = new int[capacity]; }
        public long DroppedPackets => Interlocked.Read(ref _dropped);
        public bool TryWrite(ushort sequence, uint timestamp, ArraySegment<byte> payload, out bool dropped)
        {
            dropped = false;
            if (payload.Array is null || payload.Count <= 0 || payload.Count > _payloadCapacity) return false;
            lock (_gate)
            {
                if (_write - _read >= _capacity)
                {
                    _read++;
                    dropped = true;
                    Interlocked.Increment(ref _dropped);
                }
                int slot = (int)(_write % _capacity);
                payload.AsSpan().CopyTo(_payloads.AsSpan(slot * _payloadCapacity, payload.Count));
                _sequences[slot] = sequence; _timestamps[slot] = timestamp; _lengths[slot] = payload.Count;
                _write++;
                return true;
            }
        }
        public bool TryRead(Span<byte> payload, out ushort sequence, out uint timestamp, out int length)
        {
            lock (_gate)
            {
                if (_read == _write) { sequence = 0; timestamp = 0; length = 0; return false; }
                int slot = (int)(_read % _capacity);
                sequence = _sequences[slot]; timestamp = _timestamps[slot]; length = _lengths[slot];
                _payloads.AsSpan(slot * _payloadCapacity, length).CopyTo(payload);
                _read++;
                return true;
            }
        }
        public void Reset()
        {
            lock (_gate) _read = _write;
        }
    }

    private sealed class PlaybackRing
    {
        private readonly float[] _samples;
        private long _read, _write;
        private int _primed;
        private long _highWater, _dropped, _zeroFilled, _skipped, _primingZeroFilled, _clockCorrectionSamples, _clockCorrectionCallbacks;
        public PlaybackRing(int capacity) => _samples = new float[capacity];
        public int DepthSamples => (int)Math.Clamp(Volatile.Read(ref _write) - Volatile.Read(ref _read), 0, _samples.Length);
        public long HighWaterSamples => Interlocked.Read(ref _highWater);
        public long DroppedSamples => Interlocked.Read(ref _dropped);
        public long ZeroFilledSamples => Interlocked.Read(ref _zeroFilled);
        public long SkippedSamples => Interlocked.Read(ref _skipped);
        public long PrimingZeroFilledSamples => Interlocked.Read(ref _primingZeroFilled);
        public long ClockCorrectionSamples => Interlocked.Read(ref _clockCorrectionSamples);
        public long ClockCorrectionCallbacks => Interlocked.Read(ref _clockCorrectionCallbacks);
        public void Write(ReadOnlySpan<float> samples)
        {
            long write = Volatile.Read(ref _write), read = Volatile.Read(ref _read);
            int sourceOffset = Math.Max(0, samples.Length - _samples.Length);
            int count = samples.Length - sourceOffset;
            int depth = (int)Math.Clamp(write - read, 0, _samples.Length);
            int overflow = Math.Max(0, depth + count - _samples.Length);
            int evicted = (overflow + 1) & ~1;
            read += evicted;
            for (int i = 0; i < count; i++) _samples[(int)((write + i) % _samples.Length)] = samples[sourceOffset + i];
            Volatile.Write(ref _read, read);
            Volatile.Write(ref _write, write + count);
            if (sourceOffset != 0 || evicted != 0) Interlocked.Add(ref _dropped, sourceOffset + evicted);
            UpdateHighWater((int)Math.Clamp(write + count - read, 0, _samples.Length));
        }
        public bool Read(Span<float> destination)
        {
            long read = Volatile.Read(ref _read), write = Volatile.Read(ref _write);
            int depth = (int)Math.Clamp(write - read, 0, _samples.Length);
            if (Volatile.Read(ref _primed) == 0)
            {
                if (depth < PlaybackPrimeSamples) { destination.Clear(); Interlocked.Add(ref _zeroFilled, destination.Length); Interlocked.Add(ref _primingZeroFilled, destination.Length); return true; }
                Volatile.Write(ref _primed, 1);
            }
            if (depth > PlaybackMaximumLatencySamples)
            {
                int skip = (depth - PlaybackPrimeSamples) & ~1;
                read += skip; depth -= skip;
                Interlocked.Add(ref _skipped, skip); Interlocked.Add(ref _clockCorrectionSamples, skip); Interlocked.Increment(ref _clockCorrectionCallbacks);
            }
            int count = Math.Min(depth, destination.Length);
            for (int i = 0; i < count; i++) destination[i] = _samples[(int)((read + i) % _samples.Length)];
            Volatile.Write(ref _read, read + count);
            if (count < destination.Length) { destination[count..].Clear(); Interlocked.Add(ref _zeroFilled, destination.Length - count); Volatile.Write(ref _primed, 0); }
            return count == 0;
        }
        public void DiscardQueued()
        {
            Volatile.Write(ref _read, Volatile.Read(ref _write));
            Volatile.Write(ref _primed, 0);
        }
        public void Reset()
        {
            Array.Clear(_samples); Volatile.Write(ref _read, 0); Volatile.Write(ref _write, 0); Volatile.Write(ref _primed, 0);
            Interlocked.Exchange(ref _highWater, 0); Interlocked.Exchange(ref _dropped, 0); Interlocked.Exchange(ref _zeroFilled, 0); Interlocked.Exchange(ref _skipped, 0); Interlocked.Exchange(ref _primingZeroFilled, 0); Interlocked.Exchange(ref _clockCorrectionSamples, 0); Interlocked.Exchange(ref _clockCorrectionCallbacks, 0);
        }
        private void UpdateHighWater(int depth)
        {
            long current = Interlocked.Read(ref _highWater);
            while (depth > current) { long observed = Interlocked.CompareExchange(ref _highWater, depth, current); if (observed == current) return; current = observed; }
        }
    }

    private enum EngineEventKind { LocalSdp, LocalCandidate, PeerState, LocalLevel, PeerLevels }
    private readonly record struct EngineEvent(EngineEventKind Kind, string? PeerId, int Generation, string? Value, string? Sdp, float Peak, bool Speaking, ArraySegment<ManagedPeerLevel> Levels)
    {
        public bool IsTelemetry => Kind is EngineEventKind.LocalLevel or EngineEventKind.PeerLevels;
        public static EngineEvent ForSdp(string peerId, int generation, string type, string sdp) => new(EngineEventKind.LocalSdp, peerId, generation, type, sdp, 0f, false, default);
        public static EngineEvent ForCandidate(string peerId, int generation, string candidate) => new(EngineEventKind.LocalCandidate, peerId, generation, candidate, null, 0f, false, default);
        public static EngineEvent ForState(string peerId, int generation, string state) => new(EngineEventKind.PeerState, peerId, generation, state, null, 0f, false, default);
        public static EngineEvent ForLocalLevel(float peak, bool speaking) => new(EngineEventKind.LocalLevel, null, 0, null, null, peak, speaking, default);
        public static EngineEvent ForPeerLevels(ManagedPeerLevel[] levels, int count) => new(EngineEventKind.PeerLevels, null, 0, null, null, 0f, false, new ArraySegment<ManagedPeerLevel>(levels, 0, count));
    }
}
