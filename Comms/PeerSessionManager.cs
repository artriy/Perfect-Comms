using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VoiceChatPlugin.VoiceChat;

internal interface IVoiceTransport
{
    void AddPeer(int clientId, bool isOfferer, bool relayOnly, int generation);
    void RemovePeer(int clientId);
    void SetRemoteSdp(int clientId, string sdpType, string sdp);
    void AddIceCandidate(int clientId, string candidate);
}

internal interface ISignalingSender
{
    void Send(int targetClientId, SignalMsgType type, byte[] payload);
}

internal enum PeerState
{
    Idle,
    Greeted,
    Offering,
    Answering,
    Connected,
    Established,
}

internal static class SignalPayload
{
    public static byte[] Hello(int protocolVersion, int minCompatibleVersion)
    {
        var buffer = new byte[8];
        WriteInt32(buffer, 0, protocolVersion);
        WriteInt32(buffer, 4, minCompatibleVersion);
        return buffer;
    }

    public static bool TryReadHello(byte[] payload, out int protocolVersion, out int minCompatibleVersion)
    {
        protocolVersion = 0;
        minCompatibleVersion = 0;
        if (payload == null || payload.Length != 8) return false;
        protocolVersion = ReadInt32(payload, 0);
        minCompatibleVersion = ReadInt32(payload, 4);
        return true;
    }

    public static byte[] Sdp(long negotiationId, string sdpType, string sdp)
    {
        using var stream = new MemoryStream();
        WriteInt64(stream, negotiationId);
        WriteString(stream, sdpType);
        WriteString(stream, sdp);
        return stream.ToArray();
    }

    public static bool TryReadSdp(byte[] payload, out long negotiationId, out string sdpType, out string sdp)
    {
        negotiationId = 0;
        sdpType = string.Empty;
        sdp = string.Empty;
        var offset = 0;
        return TryReadInt64(payload, ref offset, out negotiationId)
               && TryReadString(payload, ref offset, out sdpType)
               && TryReadString(payload, ref offset, out sdp)
               && offset == payload.Length;
    }

    public static byte[] Candidate(long negotiationId, string candidate)
    {
        using var stream = new MemoryStream();
        WriteInt64(stream, negotiationId);
        WriteString(stream, candidate);
        return stream.ToArray();
    }

    public static bool TryReadCandidate(byte[] payload, out long negotiationId, out string candidate)
    {
        negotiationId = 0;
        candidate = string.Empty;
        var offset = 0;
        return TryReadInt64(payload, ref offset, out negotiationId)
               && TryReadString(payload, ref offset, out candidate)
               && offset == payload.Length;
    }

    public static byte[] IceMode(bool relayOnly) => [relayOnly ? (byte)1 : (byte)0];

    public static bool TryReadIceMode(byte[] payload, out bool relayOnly)
    {
        relayOnly = false;
        if (payload == null || payload.Length != 1 || payload[0] > 1) return false;
        relayOnly = payload[0] == 1;
        return true;
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > ushort.MaxValue)
            throw new ArgumentException($"signal string too large: {bytes.Length}");
        stream.WriteByte((byte)(bytes.Length >> 8));
        stream.WriteByte((byte)(bytes.Length & 0xFF));
        stream.Write(bytes, 0, bytes.Length);
    }

    private static bool TryReadString(byte[] payload, ref int offset, out string value)
    {
        value = string.Empty;
        if (payload == null || offset + 2 > payload.Length) return false;
        var length = (payload[offset] << 8) | payload[offset + 1];
        offset += 2;
        if (offset + length > payload.Length) return false;
        value = Encoding.UTF8.GetString(payload, offset, length);
        offset += length;
        return true;
    }

    private static void WriteInt64(Stream stream, long value)
    {
        for (var shift = 56; shift >= 0; shift -= 8)
            stream.WriteByte((byte)(value >> shift));
    }

    private static bool TryReadInt64(byte[] payload, ref int offset, out long value)
    {
        value = 0;
        if (payload == null || offset + 8 > payload.Length) return false;
        for (var i = 0; i < 8; i++)
            value = (value << 8) | payload[offset + i];
        offset += 8;
        return true;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static int ReadInt32(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
}

internal sealed class PeerSessionManager
{
    public const int ProtocolVersion = 3;
    public const int MinCompatibleVersion = 3;
    private const long HelloResendIntervalMs = 3000;
    private const long HandshakeTimeoutMs = 12000;
    private const long ReinitThrottleMs = 3000;
    private const long DisconnectedGraceMs = 8000;

    private sealed class PeerEntry
    {
        public PeerState State = PeerState.Idle;
        public bool HelloSent;
        public bool HelloReceived;
        public bool Added;
        public long LastHelloSentMs;
        public long LastProgressMs;
        public long LastReinitMs;
        public long DegradedSinceMs;
        public bool RelayOnly;
        public bool RelayRequested;
        public int Generation;
        public long NegotiationId;
        public bool RestartRequested;
    }

    private readonly int _localClientId;
    private readonly IVoiceTransport _transport;
    private readonly ISignalingSender _sender;
    private readonly Func<bool> _relayAvailable;
    private readonly Func<bool> _forceRelay;
    private readonly Action<int> _requestRelay;
    private readonly Dictionary<int, PeerEntry> _peers = new();
    private int _nextGeneration;
    private long _nextNegotiationId;

    public PeerSessionManager(
        int localClientId,
        IVoiceTransport transport,
        ISignalingSender sender,
        Func<bool>? relayAvailable = null,
        Action<int>? requestRelay = null,
        Func<bool>? forceRelay = null)
    {
        _localClientId = localClientId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _relayAvailable = relayAvailable ?? (() => false);
        _requestRelay = requestRelay ?? (_ => { });
        _forceRelay = forceRelay ?? (() => false);
    }

    public static bool IsCompatible(int remoteProtocolVersion, int remoteMinCompatibleVersion)
        => remoteProtocolVersion >= MinCompatibleVersion && ProtocolVersion >= remoteMinCompatibleVersion;

    public bool TryGetPeerState(int clientId, out PeerState state)
    {
        if (_peers.TryGetValue(clientId, out var peer))
        {
            state = peer.State;
            return true;
        }
        state = PeerState.Idle;
        return false;
    }

    internal bool TryGetPeerRelayOnly(int clientId, out bool relayOnly)
    {
        if (_peers.TryGetValue(clientId, out var peer))
        {
            relayOnly = peer.RelayOnly;
            return true;
        }
        relayOnly = false;
        return false;
    }

    internal bool HasRelayPeers => _peers.Values.Any(peer => peer.RelayOnly);

    internal bool IsCompatiblePeer(int clientId)
        => _peers.TryGetValue(clientId, out var peer) && peer.HelloReceived;

    internal bool IsPeerEstablished(int clientId)
        => _peers.TryGetValue(clientId, out var peer) && peer.State == PeerState.Established;

    internal int CompatiblePeerCount => _peers.Values.Count(peer => peer.HelloReceived);

    internal int EstablishedPeerCount => _peers.Values.Count(peer => peer.State == PeerState.Established);

    internal bool TryRecoverPeer(int clientId, long nowMs)
    {
        if (!_peers.TryGetValue(clientId, out var peer) || peer.State == PeerState.Established)
            return false;
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs)
            return false;

        if (peer.State is PeerState.Idle or PeerState.Greeted)
        {
            peer.HelloSent = true;
            peer.LastHelloSentMs = nowMs;
            peer.LastReinitMs = nowMs;
            _sender.Send(clientId, SignalMsgType.Hello, SignalPayload.Hello(ProtocolVersion, MinCompatibleVersion));
            return true;
        }

        if (!peer.HelloReceived) return false;
        RecoverPeer(clientId, peer, nowMs);
        return true;
    }

    public void OnPlayerJoined(int clientId, long nowMs = 0)
    {
        if (clientId == _localClientId || clientId < 0) return;
        var peer = GetOrCreate(clientId);
        SendHelloIfNeeded(clientId, peer);
        peer.LastHelloSentMs = nowMs;
        TryStartSession(clientId, peer, nowMs);
    }

    public void Tick(long nowMs)
    {
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            if (peer.State is PeerState.Idle or PeerState.Greeted)
            {
                if (!peer.HelloSent) continue;
                if (nowMs - peer.LastHelloSentMs < HelloResendIntervalMs) continue;
                peer.LastHelloSentMs = nowMs;
                _sender.Send(pair.Key, SignalMsgType.Hello, SignalPayload.Hello(ProtocolVersion, MinCompatibleVersion));
                if (peer.RestartRequested)
                    _sender.Send(pair.Key, SignalMsgType.Restart, Array.Empty<byte>());
            }
            else if (peer.State is PeerState.Offering or PeerState.Answering or PeerState.Connected)
            {
                if (peer.LastProgressMs == 0) continue;
                if (nowMs - peer.LastProgressMs < HandshakeTimeoutMs) continue;
                if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) continue;
                RecoverPeer(pair.Key, peer, nowMs);
            }
            else if (peer.State == PeerState.Established && peer.DegradedSinceMs != 0)
            {
                // Backstop for a peer stuck in ICE "disconnected" that never escalates to "failed"
                // (so OnPeerConnectionLost never fires): re-initiate after a grace period that lets
                // transient blips self-heal. The reinit throttle below prevents re-offer storms.
                if (nowMs - peer.DegradedSinceMs < DisconnectedGraceMs) continue;
                if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) continue;
                RecoverPeer(pair.Key, peer, nowMs);
            }
        }
    }

    public void OnPeerConnected(int clientId, int generation)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer)) return;
        peer.State = PeerState.Established;
        peer.LastProgressMs = 0;
        peer.LastReinitMs = 0;
        peer.DegradedSinceMs = 0;
        peer.RelayRequested = false;
    }

    public void OnPeerConnectionLost(int clientId, int generation, long nowMs = 0)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer)) return;
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) return;
        RecoverPeer(clientId, peer, nowMs);
    }

    // ICE reported "disconnected" (a transient/soft drop, not "failed"). Mark when it began so
    // Tick can re-initiate if it does not recover within the grace period.
    public void OnPeerConnectionDegraded(int clientId, int generation, long nowMs = 0)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer)) return;
        if (peer.State == PeerState.Established && peer.DegradedSinceMs == 0)
            peer.DegradedSinceMs = nowMs;
    }

    /// Marks one failed direct peer for relay. If credentials are already available the peer is
    /// recreated immediately; otherwise the backend fetches them and calls EscalatePeer when ready.
    public void RequestRelayForPeer(int clientId, long nowMs = 0)
    {
        if (!_peers.TryGetValue(clientId, out var peer) || peer.RelayOnly) return;
        peer.RelayRequested = true;
        if (RelayAvailable())
            SetPeerRelayPolicy(clientId, peer, relayOnly: true, notifyRemote: true, nowMs: nowMs);
        else
            _requestRelay(clientId);
    }

    public void EscalatePeer(int clientId, long nowMs = 0)
    {
        if (!_peers.TryGetValue(clientId, out var peer) || !peer.RelayRequested)
            return;
        if (!RelayAvailable())
        {
            _requestRelay(clientId);
            return;
        }
        if (peer.RelayOnly)
        {
            peer.RelayRequested = false;
            Reinitiate(clientId, peer, nowMs);
            return;
        }
        SetPeerRelayPolicy(clientId, peer, relayOnly: true, notifyRemote: true, nowMs: nowMs);
    }

    public int EscalateAllToRelay(long nowMs = 0)
    {
        var requested = 0;
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            if (peer.RelayOnly) continue;
            peer.RelayRequested = true;
            requested++;
            if (RelayAvailable())
                SetPeerRelayPolicy(pair.Key, peer, relayOnly: true, notifyRemote: true, nowMs: nowMs);
            else
                _requestRelay(pair.Key);
        }
        return requested;
    }

    /// Recreates every current peer after an explicit ICE setting change. Turning force-relay off
    /// intentionally returns peers to direct-first so TURN allocations are not kept unnecessarily.
    public void RebuildAll(bool forceRelay, long nowMs = 0)
    {
        var relayOnly = forceRelay && RelayAvailable();
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            peer.RelayRequested = false;
            if (peer.RelayOnly != relayOnly)
            {
                peer.RelayOnly = relayOnly;
                _sender.Send(pair.Key, SignalMsgType.IceMode, SignalPayload.IceMode(relayOnly));
            }
            Reinitiate(pair.Key, peer, nowMs);
        }
    }

    /// Recreates only relay-backed peers after their ephemeral TURN credentials are refreshed.
    /// Healthy direct peers are deliberately untouched.
    public int RefreshRelayPeers(long nowMs = 0)
    {
        var refreshed = 0;
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            if (!peer.RelayOnly) continue;
            refreshed++;
            _sender.Send(pair.Key, SignalMsgType.IceMode, SignalPayload.IceMode(true));
            Reinitiate(pair.Key, peer, nowMs);
        }
        return refreshed;
    }

    private void RecoverPeer(int clientId, PeerEntry peer, long nowMs)
    {
        if (!peer.RelayOnly)
        {
            peer.RelayRequested = true;
            if (RelayAvailable())
            {
                SetPeerRelayPolicy(clientId, peer, relayOnly: true, notifyRemote: true, nowMs: nowMs);
                return;
            }
            _requestRelay(clientId);
        }
        else if (!RelayAvailable())
        {
            // Keep the negotiated relay-only policy while credentials refresh. Falling back to a
            // direct generation here can both leak a TURN-only user's topology and desynchronize
            // the two endpoints' ICE policies.
            peer.RelayRequested = true;
            _requestRelay(clientId);
            return;
        }
        Reinitiate(clientId, peer, nowMs);
    }

    private void SetPeerRelayPolicy(
        int clientId,
        PeerEntry peer,
        bool relayOnly,
        bool notifyRemote,
        long nowMs)
    {
        peer.RelayOnly = relayOnly;
        peer.RelayRequested = false;
        if (notifyRemote)
            _sender.Send(clientId, SignalMsgType.IceMode, SignalPayload.IceMode(relayOnly));
        Reinitiate(clientId, peer, nowMs);
    }

    private void Reinitiate(int clientId, PeerEntry peer, long nowMs)
    {
        peer.Generation = NextGeneration();
        peer.LastReinitMs = nowMs;
        peer.DegradedSinceMs = 0;
        if (peer.Added)
        {
            _transport.RemovePeer(clientId);
            peer.Added = false;
        }
        if (LocalIsOfferer(clientId))
        {
            peer.NegotiationId = NextNegotiationId();
            peer.Added = true;
            peer.State = PeerState.Offering;
            peer.LastProgressMs = nowMs;
            _transport.AddPeer(clientId, isOfferer: true, relayOnly: peer.RelayOnly, generation: peer.Generation);
        }
        else
        {
            peer.RestartRequested = true;
            peer.State = PeerState.Greeted;
            peer.LastProgressMs = 0;
            peer.LastHelloSentMs = nowMs;
            _sender.Send(clientId, SignalMsgType.Hello, SignalPayload.Hello(ProtocolVersion, MinCompatibleVersion));
            _sender.Send(clientId, SignalMsgType.Restart, Array.Empty<byte>());
        }
    }

    public void OnPlayerLeft(int clientId)
    {
        if (!_peers.ContainsKey(clientId)) return;
        _sender.Send(clientId, SignalMsgType.Bye, Array.Empty<byte>());
        DropPeer(clientId);
    }

    public void Reset()
    {
        foreach (var clientId in _peers.Keys.ToList())
            DropPeer(clientId);
    }

    public void OnSignal(int senderClientId, SignalMsgType type, byte[] payload, long nowMs = 0)
    {
        if (senderClientId == _localClientId || senderClientId < 0) return;

        switch (type)
        {
            case SignalMsgType.Hello: HandleHello(senderClientId, payload, nowMs); break;
            case SignalMsgType.Offer: HandleOffer(senderClientId, payload, nowMs); break;
            case SignalMsgType.Answer: HandleAnswer(senderClientId, payload, nowMs); break;
            case SignalMsgType.Candidate: HandleCandidate(senderClientId, payload); break;
            case SignalMsgType.Bye: DropPeer(senderClientId); break;
            case SignalMsgType.IceMode: HandleIceMode(senderClientId, payload, nowMs); break;
            case SignalMsgType.Restart: HandleRestart(senderClientId, payload, nowMs); break;
        }
    }

    public void OnLocalSdp(int clientId, int generation, string sdpType, string sdp)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer)) return;
        if (peer.NegotiationId <= 0) return;
        var isOffer = string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase);
        _sender.Send(
            clientId,
            isOffer ? SignalMsgType.Offer : SignalMsgType.Answer,
            SignalPayload.Sdp(peer.NegotiationId, sdpType, sdp));
        if (!isOffer) peer.State = PeerState.Connected;
    }

    public void OnLocalCandidate(int clientId, int generation, string candidate)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer) || peer.NegotiationId <= 0) return;
        _sender.Send(clientId, SignalMsgType.Candidate, SignalPayload.Candidate(peer.NegotiationId, candidate));
    }

    private void HandleHello(int clientId, byte[] payload, long nowMs)
    {
        if (!SignalPayload.TryReadHello(payload, out var version, out var minCompatible)) return;
        if (!IsCompatible(version, minCompatible)) return;

        var peer = GetOrCreate(clientId);
        peer.HelloReceived = true;
        SendHelloIfNeeded(clientId, peer);
        TryStartSession(clientId, peer, nowMs);
    }

    private void HandleOffer(int clientId, byte[] payload, long nowMs)
    {
        if (LocalIsOfferer(clientId)) return;
        if (!SignalPayload.TryReadSdp(payload, out var negotiationId, out var sdpType, out var sdp)) return;
        if (negotiationId <= 0) return;
        if (!string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase)) return;
        if (!_peers.TryGetValue(clientId, out var peer) || !peer.HelloReceived) return;

        // The offerer's id is monotonically increasing for this process. A duplicate offer must
        // not recreate the native peer, and a delayed offer from an older negotiation must never
        // replace the current one.
        if (negotiationId <= peer.NegotiationId) return;

        if (peer.Added)
        {
            peer.Generation = NextGeneration();
            _transport.RemovePeer(clientId);
            peer.Added = false;
        }
        peer.NegotiationId = negotiationId;
        peer.RestartRequested = false;
        peer.Added = true;
        _transport.AddPeer(clientId, isOfferer: false, relayOnly: peer.RelayOnly, generation: peer.Generation);
        peer.State = PeerState.Answering;
        peer.LastProgressMs = nowMs;
        _transport.SetRemoteSdp(clientId, sdpType, sdp);
    }

    private void HandleAnswer(int clientId, byte[] payload, long nowMs)
    {
        if (!LocalIsOfferer(clientId)) return;
        if (!SignalPayload.TryReadSdp(payload, out var negotiationId, out var sdpType, out var sdp)) return;
        if (!string.Equals(sdpType, "answer", StringComparison.OrdinalIgnoreCase)) return;
        if (!_peers.TryGetValue(clientId, out var peer) || !peer.HelloReceived) return;
        if (negotiationId <= 0 || negotiationId != peer.NegotiationId) return;
        if (!peer.Added || peer.State != PeerState.Offering) return;
        _transport.SetRemoteSdp(clientId, sdpType, sdp);
        peer.State = PeerState.Connected;
        peer.LastProgressMs = nowMs;
    }

    private void HandleCandidate(int clientId, byte[] payload)
    {
        if (!SignalPayload.TryReadCandidate(payload, out var negotiationId, out var candidate)) return;

        if (!_peers.TryGetValue(clientId, out var peer) || !peer.HelloReceived) return;
        if (negotiationId <= 0 || negotiationId != peer.NegotiationId || !peer.Added) return;
        _transport.AddIceCandidate(clientId, candidate);
    }

    private void HandleRestart(int clientId, byte[] payload, long nowMs)
    {
        if (payload == null || payload.Length != 0 || !LocalIsOfferer(clientId)) return;
        if (!_peers.TryGetValue(clientId, out var peer) || !peer.HelloReceived) return;
        if (peer.State == PeerState.Offering) return;
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) return;
        Reinitiate(clientId, peer, nowMs);
    }

    private void HandleIceMode(int clientId, byte[] payload, long nowMs)
    {
        if (!SignalPayload.TryReadIceMode(payload, out var requestedRelay)) return;
        if (!_peers.TryGetValue(clientId, out var peer) || !peer.HelloReceived) return;
        if (requestedRelay && !RelayAvailable())
        {
            // The other endpoint has identified this pair as needing TURN. Fetch credentials for
            // this peer, but keep any currently healthy direct generation alive until they arrive.
            peer.RelayRequested = true;
            _requestRelay(clientId);
            return;
        }

        // A remote direct-mode reset must not override this client's explicit Wine force-relay
        // preference. One relay endpoint can still pair with a direct endpoint.
        var relayOnly = requestedRelay || (ForceRelay() && RelayAvailable());
        var changed = peer.RelayOnly != relayOnly;
        peer.RelayOnly = relayOnly;
        peer.RelayRequested = false;

        // A duplicate request is useful only for an already connected generation. During an
        // active offer/answer it is most likely the other side seeing the same failure, so let the
        // in-flight recreation finish instead of starting an offer storm.
        if (!changed && peer.State is not (PeerState.Connected or PeerState.Established)) return;
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) return;
        Reinitiate(clientId, peer, nowMs);
    }

    private void TryStartSession(int clientId, PeerEntry peer, long nowMs)
    {
        if (!peer.HelloReceived || peer.State is PeerState.Connected or PeerState.Established) return;

        if (LocalIsOfferer(clientId))
        {
            if (peer.Added) return;
            peer.NegotiationId = NextNegotiationId();
            peer.Added = true;
            peer.State = PeerState.Offering;
            peer.LastProgressMs = nowMs;
            _transport.AddPeer(clientId, isOfferer: true, relayOnly: peer.RelayOnly, generation: peer.Generation);
        }
        else if (peer.State is PeerState.Idle or PeerState.Greeted)
        {
            peer.State = PeerState.Answering;
            peer.LastProgressMs = nowMs;
        }
    }

    private void SendHelloIfNeeded(int clientId, PeerEntry peer)
    {
        if (peer.HelloSent) return;
        peer.HelloSent = true;
        if (peer.State == PeerState.Idle) peer.State = PeerState.Greeted;
        _sender.Send(clientId, SignalMsgType.Hello, SignalPayload.Hello(ProtocolVersion, MinCompatibleVersion));
        if (peer.RelayOnly)
            _sender.Send(clientId, SignalMsgType.IceMode, SignalPayload.IceMode(true));
    }

    private void DropPeer(int clientId)
    {
        if (!_peers.ContainsKey(clientId)) return;

        _transport.RemovePeer(clientId);
        _peers.Remove(clientId);
    }

    private bool LocalIsOfferer(int clientId) => _localClientId < clientId;

    private long NextNegotiationId()
    {
        if (_nextNegotiationId == long.MaxValue) _nextNegotiationId = 0;
        return ++_nextNegotiationId;
    }

    private PeerEntry GetOrCreate(int clientId)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            peer = new PeerEntry
            {
                RelayOnly = RelayAvailable() && ForceRelay(),
                Generation = NextGeneration(),
            };
            _peers[clientId] = peer;
        }
        return peer;
    }

    private bool RelayAvailable()
    {
        try { return _relayAvailable(); }
        catch { return false; }
    }

    private bool ForceRelay()
    {
        try { return _forceRelay(); }
        catch { return false; }
    }

    private int NextGeneration()
    {
        _nextGeneration = _nextGeneration == int.MaxValue ? 1 : _nextGeneration + 1;
        return _nextGeneration;
    }

    private bool TryGetCurrentPeer(int clientId, int generation, out PeerEntry peer)
    {
        if (_peers.TryGetValue(clientId, out var found) && found.Generation == generation)
        {
            peer = found;
            return true;
        }
        peer = null!;
        return false;
    }
}
