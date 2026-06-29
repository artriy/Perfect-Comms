using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VoiceChatPlugin.VoiceChat;

internal interface IVoiceTransport
{
    void AddPeer(int clientId, bool isOfferer);
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
        if (payload == null || payload.Length < 8) return false;
        protocolVersion = ReadInt32(payload, 0);
        minCompatibleVersion = ReadInt32(payload, 4);
        return true;
    }

    public static byte[] Sdp(string sdpType, string sdp)
    {
        using var stream = new MemoryStream();
        WriteString(stream, sdpType);
        WriteString(stream, sdp);
        return stream.ToArray();
    }

    public static bool TryReadSdp(byte[] payload, out string sdpType, out string sdp)
    {
        sdpType = string.Empty;
        sdp = string.Empty;
        var offset = 0;
        return TryReadString(payload, ref offset, out sdpType) && TryReadString(payload, ref offset, out sdp);
    }

    public static byte[] Candidate(string candidate)
    {
        using var stream = new MemoryStream();
        WriteString(stream, candidate);
        return stream.ToArray();
    }

    public static bool TryReadCandidate(byte[] payload, out string candidate)
    {
        candidate = string.Empty;
        var offset = 0;
        return TryReadString(payload, ref offset, out candidate);
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
    public const int ProtocolVersion = 1;
    public const int MinCompatibleVersion = 1;
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
    }

    private readonly int _localClientId;
    private readonly IVoiceTransport _transport;
    private readonly ISignalingSender _sender;
    private readonly Dictionary<int, PeerEntry> _peers = new();

    public PeerSessionManager(int localClientId, IVoiceTransport transport, ISignalingSender sender)
    {
        _localClientId = localClientId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
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
            }
            else if (peer.State is PeerState.Offering or PeerState.Answering)
            {
                if (peer.LastProgressMs == 0) continue;
                if (nowMs - peer.LastProgressMs < HandshakeTimeoutMs) continue;
                if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) continue;
                Reinitiate(pair.Key, peer, nowMs);
            }
            else if (peer.State == PeerState.Established && peer.DegradedSinceMs != 0)
            {
                // Backstop for a peer stuck in ICE "disconnected" that never escalates to "failed"
                // (so OnPeerConnectionLost never fires): re-initiate after a grace period that lets
                // transient blips self-heal. The reinit throttle below prevents re-offer storms.
                if (nowMs - peer.DegradedSinceMs < DisconnectedGraceMs) continue;
                if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) continue;
                Reinitiate(pair.Key, peer, nowMs);
            }
        }
    }

    public void OnPeerConnected(int clientId)
    {
        if (!_peers.TryGetValue(clientId, out var peer)) return;
        peer.State = PeerState.Established;
        peer.LastProgressMs = 0;
        peer.LastReinitMs = 0;
        peer.DegradedSinceMs = 0;
    }

    public void OnPeerConnectionLost(int clientId, long nowMs = 0)
    {
        if (!_peers.TryGetValue(clientId, out var peer)) return;
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) return;
        Reinitiate(clientId, peer, nowMs);
    }

    // ICE reported "disconnected" (a transient/soft drop, not "failed"). Mark when it began so
    // Tick can re-initiate if it does not recover within the grace period.
    public void OnPeerConnectionDegraded(int clientId, long nowMs = 0)
    {
        if (!_peers.TryGetValue(clientId, out var peer)) return;
        if (peer.State == PeerState.Established && peer.DegradedSinceMs == 0)
            peer.DegradedSinceMs = nowMs;
    }

    private void Reinitiate(int clientId, PeerEntry peer, long nowMs)
    {
        peer.LastReinitMs = nowMs;
        peer.DegradedSinceMs = 0;
        if (peer.Added)
        {
            _transport.RemovePeer(clientId);
            peer.Added = false;
        }
        if (LocalIsOfferer(clientId))
        {
            peer.Added = true;
            peer.State = PeerState.Offering;
            peer.LastProgressMs = nowMs;
            _transport.AddPeer(clientId, isOfferer: true);
        }
        else
        {
            peer.State = PeerState.Greeted;
            peer.LastProgressMs = 0;
            peer.LastHelloSentMs = nowMs;
            _sender.Send(clientId, SignalMsgType.Hello, SignalPayload.Hello(ProtocolVersion, MinCompatibleVersion));
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
            case SignalMsgType.Answer: HandleAnswer(senderClientId, payload); break;
            case SignalMsgType.Candidate: HandleCandidate(senderClientId, payload); break;
            case SignalMsgType.Bye: DropPeer(senderClientId); break;
        }
    }

    public void OnLocalSdp(int clientId, string sdpType, string sdp)
    {
        if (!_peers.TryGetValue(clientId, out var peer)) return;
        var isOffer = string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase);
        _sender.Send(clientId, isOffer ? SignalMsgType.Offer : SignalMsgType.Answer, SignalPayload.Sdp(sdpType, sdp));
        if (!isOffer) peer.State = PeerState.Connected;
    }

    public void OnLocalCandidate(int clientId, string candidate)
    {
        if (!_peers.ContainsKey(clientId)) return;
        _sender.Send(clientId, SignalMsgType.Candidate, SignalPayload.Candidate(candidate));
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
        if (!SignalPayload.TryReadSdp(payload, out var sdpType, out var sdp)) return;

        var peer = GetOrCreate(clientId);

        if (peer.Added)
        {
            _transport.RemovePeer(clientId);
            peer.Added = false;
        }
        peer.Added = true;
        _transport.AddPeer(clientId, isOfferer: false);
        peer.State = PeerState.Answering;
        peer.LastProgressMs = nowMs;
        _transport.SetRemoteSdp(clientId, sdpType, sdp);
    }

    private void HandleAnswer(int clientId, byte[] payload)
    {
        if (!LocalIsOfferer(clientId)) return;
        if (!SignalPayload.TryReadSdp(payload, out var sdpType, out var sdp)) return;
        if (!_peers.TryGetValue(clientId, out var peer)) return;
        _transport.SetRemoteSdp(clientId, sdpType, sdp);
        peer.State = PeerState.Connected;
    }

    private void HandleCandidate(int clientId, byte[] payload)
    {
        if (!SignalPayload.TryReadCandidate(payload, out var candidate)) return;

        if (!_peers.ContainsKey(clientId)) return;
        _transport.AddIceCandidate(clientId, candidate);
    }

    private void TryStartSession(int clientId, PeerEntry peer, long nowMs)
    {
        if (!peer.HelloReceived || peer.State is PeerState.Connected or PeerState.Established) return;

        if (LocalIsOfferer(clientId))
        {
            if (peer.Added) return;
            peer.Added = true;
            peer.State = PeerState.Offering;
            peer.LastProgressMs = nowMs;
            _transport.AddPeer(clientId, isOfferer: true);
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
    }

    private void DropPeer(int clientId)
    {
        if (!_peers.ContainsKey(clientId)) return;

        _transport.RemovePeer(clientId);
        _peers.Remove(clientId);
    }

    private bool LocalIsOfferer(int clientId) => _localClientId < clientId;

    private PeerEntry GetOrCreate(int clientId)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            peer = new PeerEntry();
            _peers[clientId] = peer;
        }
        return peer;
    }
}
