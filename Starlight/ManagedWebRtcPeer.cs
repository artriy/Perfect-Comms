using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace PerfectComms.Starlight.Media;

public sealed class ManagedWebRtcPeer : IDisposable
{
    private const int MaximumPendingCandidates = 256;
    private static readonly byte[] EmptyAudio = Array.Empty<byte>();

    private readonly object _signalGate = new();
    private readonly object _negotiationQueueGate = new();
    private readonly SemaphoreSlim _negotiationGate = new(1, 1);
    private readonly RTCPeerConnection _connection;
    private readonly string?[] _pendingRemoteCandidates = new string[MaximumPendingCandidates];
    private readonly string?[] _pendingLocalCandidates = new string[MaximumPendingCandidates];
    private readonly string?[] _pendingLocalCandidateUfrags = new string[MaximumPendingCandidates];
    private int _pendingRemoteCandidateCount;
    private int _pendingLocalCandidateCount;
    private bool _remoteDescriptionSet;
    private int _localSignalState;
    private string? _currentLocalUfrag;
    private int _connected;
    private int _pendingRemoteDescriptions;
    private int _sendFailed;
    private int _appliedRemoteCandidatesForTest;
    private Task _negotiationTail = Task.CompletedTask;
    private int _disposed;

    public ManagedWebRtcPeer(
        string peerId,
        int generation,
        IReadOnlyList<ManagedIceServer> iceServers)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        ArgumentNullException.ThrowIfNull(iceServers);

        PeerId = peerId;
        Generation = generation;

        var configuration = new RTCConfiguration
        {
            iceServers = MapIceServers(iceServers),
            iceTransportPolicy = RTCIceTransportPolicy.all,
            bundlePolicy = RTCBundlePolicy.max_bundle,
            rtcpMuxPolicy = RTCRtcpMuxPolicy.require
        };
        _connection = new RTCPeerConnection(configuration);
        _connection.onicecandidate += OnIceCandidate;
        _connection.onconnectionstatechange += OnConnectionStateChanged;
        _connection.OnRtpPacketReceived += OnRtpPacketReceived;

        AudioFormat opus = AudioCommonlyUsedFormats.OpusWebRTC;
        opus.Parameters = "minptime=10;useinbandfec=1";
        _connection.addTrack(new MediaStreamTrack(opus, MediaStreamStatusEnum.SendRecv));
    }

    public event Action<string, string>? LocalSdp;
    public event Action<string>? LocalCandidate;
    public event Action<string>? StateChanged;
    public event Action<uint, ushort, uint, ArraySegment<byte>>? OpusPacketReceived;

    public string PeerId { get; }
    public int Generation { get; }
    public bool IsConnected => Volatile.Read(ref _sendFailed) == 0 &&
        Volatile.Read(ref _connected) != 0;

    public bool Start(bool createOffer)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        if (createOffer)
            QueueNegotiation(CreateOfferAsync);
        return true;
    }

    public bool SetRemoteSdp(string sdpType, string sdp)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(sdp))
            return false;
        if (!TryParseSdpType(sdpType, out RTCSdpType type) || type is RTCSdpType.rollback or RTCSdpType.pranswer)
            return false;

        lock (_signalGate)
        {
            _pendingRemoteDescriptions++;
            _remoteDescriptionSet = false;
        }
        QueueNegotiation(() => ApplyRemoteSdpAsync(type, sdp));
        return true;
    }

    public bool AddIceCandidate(string candidate)
    {
        if (Volatile.Read(ref _disposed) != 0 || candidate is null)
            return false;
        if (candidate.Length == 0)
            return true;

        lock (_signalGate)
        {
            if (!_remoteDescriptionSet)
            {
                for (int i = 0; i < _pendingRemoteCandidateCount; i++)
                {
                    if (string.Equals(_pendingRemoteCandidates[i], candidate, StringComparison.Ordinal))
                        return true;
                }
                if (_pendingRemoteCandidateCount == MaximumPendingCandidates)
                    return false;
                _pendingRemoteCandidates[_pendingRemoteCandidateCount++] = candidate;
                return true;
            }
        }

        return ApplyIceCandidate(candidate);
    }

    public bool RestartIce(bool createOffer)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        lock (_signalGate) _remoteDescriptionSet = false;
        Interlocked.Exchange(ref _sendFailed, 0);
        QueueNegotiation(() => RestartIceAsync(createOffer));
        return true;
    }

    public bool SendEncodedOpus(byte[] packet, int length)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _sendFailed) != 0 ||
            !IsConnected || packet is null || length <= 0 || length > packet.Length)
            return false;

        try
        {
            if (!TrySendAudio(ManagedOpusEncoder.FrameSamples, new ArraySegment<byte>(packet, 0, length)))
                return false;
            return true;
        }
        catch
        {
            FailSend();
            return false;
        }
    }
    public bool AdvanceAudioTimestamp(uint durationRtpUnits)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _sendFailed) != 0 ||
            !IsConnected || durationRtpUnits == 0)
            return false;

        try
        {
            if (!TrySendAudio(durationRtpUnits, new ArraySegment<byte>(EmptyAudio)))
                return false;
            return true;
        }
        catch
        {
            FailSend();
            return false;
        }
    }

    public void FailForIceServerChange()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        Volatile.Write(ref _connected, 0);
        PublishState("failed");
        try
        {
            _connection.Close("ice-server-configuration-changed");
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _connected, 0);
        _connection.onicecandidate -= OnIceCandidate;
        _connection.onconnectionstatechange -= OnConnectionStateChanged;
        _connection.OnRtpPacketReceived -= OnRtpPacketReceived;
        try
        {
            _connection.Close("disposed");
        }
        catch
        {
        }
        _connection.Dispose();
        _negotiationGate.Dispose();
        lock (_signalGate)
        {
            Array.Clear(_pendingRemoteCandidates);
            Array.Clear(_pendingLocalCandidates);
            Array.Clear(_pendingLocalCandidateUfrags);
            _pendingRemoteCandidateCount = 0;
            _pendingLocalCandidateCount = 0;
        }
    }

    internal Action<uint, ArraySegment<byte>>? SendAudioForTest { get; set; }
    internal int PendingRemoteCandidateCountForTest
    {
        get { lock (_signalGate) return _pendingRemoteCandidateCount; }
    }
    internal bool RemoteDescriptionPendingForTest
    {
        get { lock (_signalGate) return _pendingRemoteDescriptions != 0; }
    }
    internal bool RemoteDescriptionReadyForTest
    {
        get { lock (_signalGate) return _remoteDescriptionSet; }
    }
    internal void SetConnectedForTest() =>
        OnConnectionStateChanged(RTCPeerConnectionState.connected);
    internal int AppliedRemoteCandidateCountForTest =>
        Volatile.Read(ref _appliedRemoteCandidatesForTest);
    internal int LocalSignalStateForTest
    {
        get { lock (_signalGate) return _localSignalState; }
    }
    internal void SetLocalSignalsPublishedForTest()
    {
        lock (_signalGate) _localSignalState = 2;
    }
    internal void SetRemoteDescriptionReadyForTest()
    {
        lock (_signalGate) _remoteDescriptionSet = true;
    }
    internal Task WaitForNegotiationForTest()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueNegotiation(() =>
        {
            completed.TrySetResult();
            return Task.CompletedTask;
        });
        return completed.Task;
    }
    internal Task HoldNegotiationForTest(Task release)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueNegotiation(async () =>
        {
            entered.TrySetResult();
            await release.ConfigureAwait(false);
        });
        return entered.Task;
    }

    private static List<RTCIceServer> MapIceServers(IReadOnlyList<ManagedIceServer> servers)
    {
        var mapped = new List<RTCIceServer>(servers.Count);
        for (int i = 0; i < servers.Count; i++)
        {
            ManagedIceServer server = servers[i];
            if (string.IsNullOrWhiteSpace(server.Urls))
                continue;
            mapped.Add(new RTCIceServer
            {
                urls = server.Urls,
                username = string.IsNullOrEmpty(server.Username) ? null : server.Username,
                credential = string.IsNullOrEmpty(server.Credential) ? null : server.Credential,
                credentialType = RTCIceCredentialType.password
            });
        }
        return mapped;
    }

    private static bool TryParseSdpType(string value, out RTCSdpType type)
    {
        if (string.Equals(value, "offer", StringComparison.OrdinalIgnoreCase))
        {
            type = RTCSdpType.offer;
            return true;
        }
        if (string.Equals(value, "answer", StringComparison.OrdinalIgnoreCase))
        {
            type = RTCSdpType.answer;
            return true;
        }
        if (string.Equals(value, "pranswer", StringComparison.OrdinalIgnoreCase))
        {
            type = RTCSdpType.pranswer;
            return true;
        }
        if (string.Equals(value, "rollback", StringComparison.OrdinalIgnoreCase))
        {
            type = RTCSdpType.rollback;
            return true;
        }
        type = default;
        return false;
    }
    private void QueueNegotiation(Func<Task> negotiation)
    {
        lock (_negotiationQueueGate)
            _negotiationTail = RunQueuedNegotiationAsync(_negotiationTail, negotiation);
    }

    private async Task RunQueuedNegotiationAsync(Task previous, Func<Task> negotiation)
    {
        await Task.Yield();
        await previous.ConfigureAwait(false);
        await RunNegotiationAsync(negotiation).ConfigureAwait(false);
    }


    private async Task RunNegotiationAsync(Func<Task> negotiation)
    {
        try
        {
            await _negotiationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                    await negotiation().ConfigureAwait(false);
            }
            finally
            {
                _negotiationGate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
            PublishState("failed");
        }
    }

    private async Task RestartIceAsync(bool createOffer)
    {
        ResetLocalSignals();
        _connection.restartIce();
        if (createOffer)
            await CreateOfferAsync().ConfigureAwait(false);
    }

    private async Task CreateOfferAsync()
    {
        RTCSessionDescriptionInit offer = _connection.createOffer(new RTCOfferOptions
        {
            X_ExcludeIceCandidates = true
        });
        SetCurrentLocalUfrag(offer.sdp);
        await _connection.setLocalDescription(offer).ConfigureAwait(false);
        PublishLocalSdp("offer", offer.sdp);
    }

    private async Task ApplyRemoteSdpAsync(RTCSdpType type, string sdp)
    {
        if (type == RTCSdpType.offer)
            PrepareAnswerSignals();

        try
        {
            SetDescriptionResultEnum setResult = _connection.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = type,
                sdp = sdp
            });
            if (setResult != SetDescriptionResultEnum.OK)
                throw new InvalidOperationException($"Remote SDP was rejected: {setResult}.");
        }
        catch
        {
            CompleteRemoteDescription(success: false);
            throw;
        }

        if (CompleteRemoteDescription(success: true))
            FlushRemoteCandidates();
        if (type != RTCSdpType.offer)
            return;

        RTCSessionDescriptionInit answer = _connection.createAnswer(new RTCAnswerOptions
        {
            X_ExcludeIceCandidates = true
        });
        SetCurrentLocalUfrag(answer.sdp);
        await _connection.setLocalDescription(answer).ConfigureAwait(false);
        PublishLocalSdp("answer", answer.sdp);
    }

    private void FlushRemoteCandidates()
    {
        int count;
        lock (_signalGate)
        {
            _remoteDescriptionSet = true;
            count = _pendingRemoteCandidateCount;
            _pendingRemoteCandidateCount = 0;
        }

        for (int i = 0; i < count; i++)
        {
            string? candidate = _pendingRemoteCandidates[i];
            _pendingRemoteCandidates[i] = null;
            if (candidate is not null && !ApplyIceCandidate(candidate))
                PublishState("failed");
        }
    }

    private bool CompleteRemoteDescription(bool success)
    {
        lock (_signalGate)
        {
            if (_pendingRemoteDescriptions > 0)
                _pendingRemoteDescriptions--;
            return success && _pendingRemoteDescriptions == 0;
        }
    }

    private bool ApplyIceCandidate(string candidate)
    {
        try
        {
            _connection.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate,
                sdpMid = "0",
                sdpMLineIndex = 0
            });
            Interlocked.Increment(ref _appliedRemoteCandidatesForTest);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ResetLocalSignals()
    {
        lock (_signalGate)
        {
            _localSignalState = 0;
            _currentLocalUfrag = null;
            _remoteDescriptionSet = false;
            Array.Clear(_pendingLocalCandidates, 0, _pendingLocalCandidateCount);
            Array.Clear(_pendingLocalCandidateUfrags, 0, _pendingLocalCandidateCount);
            _pendingLocalCandidateCount = 0;
        }
    }

    private void PrepareAnswerSignals()
    {
        lock (_signalGate)
        {
            if (_localSignalState != 2)
                return;
            _localSignalState = 0;
            _currentLocalUfrag = null;
            Array.Clear(_pendingLocalCandidates, 0, _pendingLocalCandidateCount);
            Array.Clear(_pendingLocalCandidateUfrags, 0, _pendingLocalCandidateCount);
            _pendingLocalCandidateCount = 0;
        }
    }

    private void SetCurrentLocalUfrag(string sdp)
    {
        const string prefix = "a=ice-ufrag:";
        int start = sdp.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("Local SDP does not contain an ICE username fragment.");
        start += prefix.Length;
        int end = start;
        while (end < sdp.Length && sdp[end] != '\r' && sdp[end] != '\n')
            end++;
        string usernameFragment = sdp[start..end].Trim();
        if (usernameFragment.Length == 0)
            throw new InvalidOperationException("Local SDP contains an empty ICE username fragment.");
        lock (_signalGate)
        {
            _currentLocalUfrag = usernameFragment;
            int retained = 0;
            for (int i = 0; i < _pendingLocalCandidateCount; i++)
            {
                string? candidate = _pendingLocalCandidates[i];
                string? candidateUfrag = _pendingLocalCandidateUfrags[i];
                _pendingLocalCandidates[i] = null;
                _pendingLocalCandidateUfrags[i] = null;
                if (candidate is null ||
                    !string.Equals(candidateUfrag, usernameFragment, StringComparison.Ordinal))
                    continue;
                _pendingLocalCandidates[retained] = candidate;
                _pendingLocalCandidateUfrags[retained] = candidateUfrag;
                retained++;
            }
            _pendingLocalCandidateCount = retained;
        }

    }

    private void PublishLocalSdp(string type, string sdp)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        LocalSdp?.Invoke(type, sdp);
        while (Volatile.Read(ref _disposed) == 0)
        {
            string[] candidates;
            lock (_signalGate)
            {
                if (_pendingLocalCandidateCount == 0)
                {
                    _localSignalState = 2;
                    return;
                }
                _localSignalState = 1;
                candidates = new string[_pendingLocalCandidateCount];
                for (int i = 0; i < candidates.Length; i++)
                {
                    candidates[i] = _pendingLocalCandidates[i]!;
                    _pendingLocalCandidates[i] = null;
                    _pendingLocalCandidateUfrags[i] = null;
                }
                _pendingLocalCandidateCount = 0;
            }
            for (int i = 0; i < candidates.Length && Volatile.Read(ref _disposed) == 0; i++)
                LocalCandidate?.Invoke(candidates[i]);
        }
    }

    private void OnIceCandidate(RTCIceCandidate candidate)
    {
        if (Volatile.Read(ref _disposed) != 0 || candidate is null)
            return;
        string? candidateValue = candidate.candidate;
        if (string.IsNullOrEmpty(candidateValue))
            return;
        PublishLocalCandidate(candidateValue, candidate.usernameFragment);
    }

    private void PublishLocalCandidate(string candidate, string? usernameFragment)
    {
        bool publishNow;
        bool overflow = false;
        lock (_signalGate)
        {
            if (string.IsNullOrEmpty(usernameFragment))
                return;
            if (_currentLocalUfrag is not null &&
                !string.Equals(usernameFragment, _currentLocalUfrag, StringComparison.Ordinal))
                return;
            publishNow = _localSignalState == 2 && _currentLocalUfrag is not null;
            if (!publishNow)
            {
                if (_pendingLocalCandidateCount == MaximumPendingCandidates)
                {
                    overflow = true;
                }
                else
                {
                    _pendingLocalCandidates[_pendingLocalCandidateCount] = candidate;
                    _pendingLocalCandidateUfrags[_pendingLocalCandidateCount] = usernameFragment;
                    _pendingLocalCandidateCount++;
                }
            }
        }
        if (overflow)
            PublishState("failed");
        else if (publishNow && Volatile.Read(ref _disposed) == 0)
            LocalCandidate?.Invoke(candidate);
    }

    private void OnConnectionStateChanged(RTCPeerConnectionState state)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        bool connected = state == RTCPeerConnectionState.connected;
        if (connected)
            Interlocked.Exchange(ref _sendFailed, 0);
        Volatile.Write(ref _connected, connected ? 1 : 0);
        if (connected || Volatile.Read(ref _sendFailed) == 0)
            PublishState(state.ToString());
    }

    private bool TrySendAudio(uint durationRtpUnits, ArraySegment<byte> packet)
    {
        Action<uint, ArraySegment<byte>>? testSender = SendAudioForTest;
        if (testSender is not null)
        {
            testSender(durationRtpUnits, packet);
            return true;
        }
        AudioStream? stream = _connection.AudioStream;
        if (stream is null)
            return false;
        stream.SendAudio(durationRtpUnits, packet);
        return true;
    }

    private void FailSend()
    {
        bool publish = Interlocked.Exchange(ref _sendFailed, 1) == 0;
        Volatile.Write(ref _connected, 0);
        if (publish)
            PublishState("failed");
    }

    private void PublishState(string state)
    {
        if (Volatile.Read(ref _disposed) == 0)
            StateChanged?.Invoke(state);
    }

    private void OnRtpPacketReceived(
        System.Net.IPEndPoint remoteEndPoint,
        SDPMediaTypesEnum mediaType,
        RTPPacket packet)
    {
        if (Volatile.Read(ref _disposed) != 0 || mediaType != SDPMediaTypesEnum.audio ||
            packet.Header.PayloadType != 111)
            return;

        int payloadLength = checked((int)packet.GetPayloadLength());
        if (payloadLength <= 0 || payloadLength > ManagedOpusEncoder.MaxPacketBytes)
            return;
        OpusPacketReceived?.Invoke(
            packet.Header.SyncSource,
            packet.Header.SequenceNumber,
            packet.Header.Timestamp,
            packet.GetPayloadSegment(0, payloadLength));
    }
}
