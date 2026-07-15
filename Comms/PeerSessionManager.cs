using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

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
    bool Send(int targetClientId, SignalMsgType type, byte[] payload);
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

internal readonly struct PeerSessionDiagnosticsSnapshot
{
    public static readonly PeerSessionDiagnosticsSnapshot Empty = new(
        0, 0, 0, 0,
        0, 0, 0, 0,
        "none");

    public PeerSessionDiagnosticsSnapshot(
        int knownPeers,
        int compatiblePeers,
        int negotiatingPeers,
        int establishedPeers,
        long localCandidatesAttempted,
        long remoteCandidatesReceived,
        long remoteCandidatesForwarded,
        long rejectedCandidates,
        string peerStates)
    {
        KnownPeers = knownPeers;
        CompatiblePeers = compatiblePeers;
        NegotiatingPeers = negotiatingPeers;
        EstablishedPeers = establishedPeers;
        LocalCandidatesAttempted = localCandidatesAttempted;
        RemoteCandidatesReceived = remoteCandidatesReceived;
        RemoteCandidatesForwarded = remoteCandidatesForwarded;
        RejectedCandidates = rejectedCandidates;
        PeerStates = peerStates;
    }

    public int KnownPeers { get; }
    public int CompatiblePeers { get; }
    public int NegotiatingPeers { get; }
    public int EstablishedPeers { get; }
    public long LocalCandidatesAttempted { get; }
    public long RemoteCandidatesReceived { get; }
    public long RemoteCandidatesForwarded { get; }
    public long RejectedCandidates { get; }
    public string PeerStates { get; }
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
    private const long FailedHelloAckRetryIntervalMs = 500;
    internal const long HandshakeTimeoutMs = 12000;
    private const long ReinitThrottleMs = 3000;
    private const long DisconnectedGraceMs = 8000;
    private const int MaxPendingRemoteCandidates = 64;
    private const long PendingRemoteCandidateTtlMs = 12000;
    private const long FailedLocalSignalRetryIntervalMs = 500;
    private const int MaxPendingLocalCandidates = 64;

    private readonly record struct PendingRemoteCandidate(long NegotiationId, string Candidate, long ReceivedAtMs);
    private readonly record struct PendingLocalSignal(SignalMsgType Type, byte[] Payload);

    private sealed class PeerEntry
    {
        public PeerState State = PeerState.Idle;
        public bool HelloSent;
        public bool HelloReceived;
        public bool RemoteHelloAcknowledged;
        public long LastHelloAcknowledgementMs;
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
        public long LocalCandidatesAttempted;
        public long RemoteCandidatesReceived;
        public long RemoteCandidatesForwarded;
        public long RejectedCandidates;
        public readonly List<PendingRemoteCandidate> PendingRemoteCandidates = new();
        public PendingLocalSignal? PendingLocalSdp;
        public readonly List<PendingLocalSignal> PendingLocalCandidates = new();
        public long LastLocalSignalAttemptMs;
    }

    private readonly int _localClientId;
    private readonly IVoiceTransport _transport;
    private readonly ISignalingSender _sender;
    private readonly Func<bool> _relayAvailable;
    private readonly Func<bool> _forceRelay;
    private readonly Action<int> _requestRelay;
    private readonly Dictionary<int, PeerEntry> _peers = new();
    // Native callbacks can arrive after a reusable helper has handed off to a new backend. Keep
    // generations process-wide so a delayed close/state event from the previous lobby can never
    // collide with a new manager's first generation and disturb its fresh peer.
    private static int _nextProcessGeneration;
    // Negotiation ids travel over RPC and can outlive a particular backend instance. Keep them
    // process-wide just like generations so a delayed offer/answer from a replaced manager can
    // never collide with the new manager's first negotiation.
    private static long _nextProcessNegotiationId;
    private long _localCandidatesAttempted;
    private long _remoteCandidatesReceived;
    private long _remoteCandidatesForwarded;
    private long _rejectedCandidates;

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

    /// <summary>
    /// Returns true while at least one peer is inside the manager-owned ICE/SDP negotiation
    /// deadline. Room-level recovery must not tear down the backend during this interval: doing
    /// so discards a negotiation which the native engine is still legitimately completing.
    /// </summary>
    internal bool HasActiveNegotiation(long nowMs)
    {
        foreach (var peer in _peers.Values)
        {
            if (peer.State is not (PeerState.Offering or PeerState.Answering or PeerState.Connected))
                continue;
            if (peer.LastProgressMs <= 0)
                continue;

            var elapsedMs = nowMs - peer.LastProgressMs;
            if (elapsedMs >= 0 && elapsedMs < HandshakeTimeoutMs)
                return true;
        }

        return false;
    }

    internal PeerSessionDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        var peerStates = _peers.Count == 0
            ? "none"
            : string.Join(
                ",",
                _peers.OrderBy(pair => pair.Key).Select(pair =>
                {
                    var peer = pair.Value;
                    return $"{pair.Key}:{peer.State}/g{peer.Generation}/n{peer.NegotiationId}/relay{Bool(peer.RelayOnly)}/added{Bool(peer.Added)}/candAttempts{peer.LocalCandidatesAttempted}-rx{peer.RemoteCandidatesReceived}-forwarded{peer.RemoteCandidatesForwarded}-rejected{peer.RejectedCandidates}";
                }));

        return new PeerSessionDiagnosticsSnapshot(
            _peers.Count,
            CompatiblePeerCount,
            _peers.Values.Count(peer => peer.State is PeerState.Offering or PeerState.Answering or PeerState.Connected),
            EstablishedPeerCount,
            _localCandidatesAttempted,
            _remoteCandidatesReceived,
            _remoteCandidatesForwarded,
            _rejectedCandidates,
            peerStates);
    }

    internal bool TryRecoverPeer(int clientId, long nowMs)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("recover", clientId, null, "peer-unknown");
            return false;
        }
        if (peer.State == PeerState.Established)
        {
            LogReject("recover", clientId, peer, "already-established");
            return false;
        }
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs)
        {
            LogReject("recover", clientId, peer, "reinit-throttled", $"nowMs={nowMs} lastReinitMs={peer.LastReinitMs}");
            return false;
        }

        if (peer.State is PeerState.Idle or PeerState.Greeted)
        {
            peer.LastReinitMs = nowMs;
            LogEvent("recover", clientId, peer, "action=resend-hello");
            return SendHello(clientId, peer, nowMs, force: true, reason: "targeted-recovery");
        }

        if (!peer.HelloReceived)
        {
            LogReject("recover", clientId, peer, "hello-not-received");
            return false;
        }
        LogEvent("recover", clientId, peer, "action=reinitiate");
        RecoverPeer(clientId, peer, nowMs);
        return true;
    }

    public void OnPlayerJoined(int clientId, long nowMs = 0)
    {
        if (clientId == _localClientId)
        {
            LogReject("player-joined", clientId, null, "local-client");
            return;
        }
        if (clientId < 0)
        {
            LogReject("player-joined", clientId, null, "invalid-client");
            return;
        }
        var peer = GetOrCreate(clientId);
        LogEvent("player-joined", clientId, peer, $"nowMs={nowMs}");
        SendHelloIfNeeded(clientId, peer, nowMs);
        TryStartSession(clientId, peer, nowMs);
    }

    public void Tick(long nowMs)
    {
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            PruneExpiredPendingCandidates(pair.Key, peer, nowMs);
            if (peer.State is PeerState.Idle or PeerState.Greeted)
            {
                var acknowledgementPending = peer.HelloReceived && !peer.RemoteHelloAcknowledged;
                var firstHelloFailed = !peer.HelloSent;
                var retryInterval = acknowledgementPending || firstHelloFailed
                    ? FailedHelloAckRetryIntervalMs
                    : HelloResendIntervalMs;
                if (peer.LastHelloSentMs != 0 && nowMs - peer.LastHelloSentMs < retryInterval) continue;
                var reason = acknowledgementPending
                    ? "remote-hello-ack-retry"
                    : firstHelloFailed ? "initial-hello-send-retry" : "hello-resend-timeout";
                // Commit the acknowledgement guard before SendHello: test transports and the
                // in-process two-client harness can deliver synchronously and re-enter HandleHello.
                if (acknowledgementPending)
                {
                    peer.RemoteHelloAcknowledged = true;
                    peer.LastHelloAcknowledgementMs = nowMs;
                }
                var sent = SendHello(pair.Key, peer, nowMs, force: true, reason: reason);
                if (acknowledgementPending && !sent)
                {
                    peer.RemoteHelloAcknowledged = false;
                    peer.LastHelloAcknowledgementMs = 0;
                }
                else if (acknowledgementPending)
                {
                    TryStartSession(pair.Key, peer, nowMs);
                }
                if (peer.RestartRequested)
                    SendSignal(pair.Key, peer, SignalMsgType.Restart, Array.Empty<byte>(), "restart-resend-with-hello");
            }
            else if (peer.State is PeerState.Offering or PeerState.Answering or PeerState.Connected)
            {
                RetryPendingLocalSignals(pair.Key, peer, nowMs);
                if (peer.LastProgressMs == 0) continue;
                if (nowMs - peer.LastProgressMs < HandshakeTimeoutMs) continue;
                if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) continue;
                LogEvent("timeout", pair.Key, peer, $"kind=handshake nowMs={nowMs} lastProgressMs={peer.LastProgressMs}");
                RecoverPeer(pair.Key, peer, nowMs);
            }
            else if (peer.State == PeerState.Established && peer.DegradedSinceMs != 0)
            {
                // Backstop for a peer stuck in ICE "disconnected" that never escalates to "failed"
                // (so OnPeerConnectionLost never fires): re-initiate after a grace period that lets
                // transient blips self-heal. The reinit throttle below prevents re-offer storms.
                if (nowMs - peer.DegradedSinceMs < DisconnectedGraceMs) continue;
                if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs) continue;
                LogEvent("timeout", pair.Key, peer, $"kind=degraded nowMs={nowMs} degradedSinceMs={peer.DegradedSinceMs}");
                RecoverPeer(pair.Key, peer, nowMs);
            }
        }
    }

    public void OnPeerConnected(int clientId, int generation)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer))
        {
            LogGenerationReject("peer-connected", clientId, generation);
            return;
        }
        SetState(clientId, peer, PeerState.Established, "native-peer-connected");
        peer.LastProgressMs = 0;
        peer.LastReinitMs = 0;
        peer.DegradedSinceMs = 0;
        peer.RelayRequested = false;
        LogEvent("peer-connected", clientId, peer, "accepted=true");
    }

    public void OnPeerConnectionLost(int clientId, int generation, long nowMs = 0)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer))
        {
            LogGenerationReject("peer-lost", clientId, generation);
            return;
        }
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs)
        {
            LogReject("peer-lost", clientId, peer, "reinit-throttled", $"nowMs={nowMs} lastReinitMs={peer.LastReinitMs}");
            return;
        }
        LogEvent("peer-lost", clientId, peer, $"nowMs={nowMs} action=recover");
        RecoverPeer(clientId, peer, nowMs);
    }

    // ICE reported "disconnected" (a transient/soft drop, not "failed"). Mark when it began so
    // Tick can re-initiate if it does not recover within the grace period.
    public void OnPeerConnectionDegraded(int clientId, int generation, long nowMs = 0)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer))
        {
            LogGenerationReject("peer-degraded", clientId, generation);
            return;
        }
        if (peer.State == PeerState.Established && peer.DegradedSinceMs == 0)
        {
            peer.DegradedSinceMs = nowMs;
            LogEvent("peer-degraded", clientId, peer, $"accepted=true nowMs={nowMs}");
        }
        else
        {
            LogReject(
                "peer-degraded",
                clientId,
                peer,
                peer.State != PeerState.Established ? "not-established" : "already-degraded",
                $"nowMs={nowMs} degradedSinceMs={peer.DegradedSinceMs}");
        }
    }

    /// Marks one failed direct peer for relay. If credentials are already available the peer is
    /// recreated immediately; otherwise the backend fetches them and calls EscalatePeer when ready.
    public void RequestRelayForPeer(int clientId, long nowMs = 0)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("relay-request", clientId, null, "peer-unknown");
            return;
        }
        if (peer.RelayOnly)
        {
            LogReject("relay-request", clientId, peer, "already-relay-only");
            return;
        }
        peer.RelayRequested = true;
        var relayAvailable = RelayAvailable();
        LogEvent("relay-request", clientId, peer, $"nowMs={nowMs} credentialsAvailable={Bool(relayAvailable)}");
        if (relayAvailable)
            SetPeerRelayPolicy(clientId, peer, relayOnly: true, notifyRemote: true, nowMs: nowMs);
        else
            _requestRelay(clientId);
    }

    public void EscalatePeer(int clientId, long nowMs = 0)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("relay-escalate", clientId, null, "peer-unknown");
            return;
        }
        if (!peer.RelayRequested)
        {
            LogReject("relay-escalate", clientId, peer, "relay-not-requested");
            return;
        }
        if (!RelayAvailable())
        {
            LogEvent("relay-escalate", clientId, peer, "action=request-credentials");
            _requestRelay(clientId);
            return;
        }
        if (peer.RelayOnly)
        {
            peer.RelayRequested = false;
            LogEvent("relay-escalate", clientId, peer, "action=reinitiate-existing-relay-policy");
            Reinitiate(clientId, peer, nowMs);
            return;
        }
        LogEvent("relay-escalate", clientId, peer, "action=set-relay-policy");
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
            var relayAvailable = RelayAvailable();
            LogEvent("relay-escalate-all", pair.Key, peer, $"nowMs={nowMs} credentialsAvailable={Bool(relayAvailable)}");
            if (relayAvailable)
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
                SendSignal(pair.Key, peer, SignalMsgType.IceMode, SignalPayload.IceMode(relayOnly), "rebuild-all-policy-change");
            }
            LogEvent("rebuild", pair.Key, peer, $"forceRelay={Bool(forceRelay)} effectiveRelayOnly={Bool(relayOnly)} nowMs={nowMs}");
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
            SendSignal(pair.Key, peer, SignalMsgType.IceMode, SignalPayload.IceMode(true), "relay-credentials-refreshed");
            LogEvent("relay-refresh", pair.Key, peer, $"nowMs={nowMs}");
            Reinitiate(pair.Key, peer, nowMs);
        }
        return refreshed;
    }

    private void RecoverPeer(int clientId, PeerEntry peer, long nowMs)
    {
        LogEvent("recover", clientId, peer, $"begin=true nowMs={nowMs}");
        if (!peer.RelayOnly)
        {
            peer.RelayRequested = true;
            if (RelayAvailable())
            {
                LogEvent("recover", clientId, peer, "action=escalate-to-relay");
                SetPeerRelayPolicy(clientId, peer, relayOnly: true, notifyRemote: true, nowMs: nowMs);
                return;
            }
            LogEvent("recover", clientId, peer, "action=request-relay-credentials");
            _requestRelay(clientId);
        }
        else if (!RelayAvailable())
        {
            // Keep the negotiated relay-only policy while credentials refresh. Falling back to a
            // direct generation here can both leak a TURN-only user's topology and desynchronize
            // the two endpoints' ICE policies.
            peer.RelayRequested = true;
            LogEvent("recover", clientId, peer, "action=refresh-relay-credentials");
            _requestRelay(clientId);
            return;
        }
        LogEvent("recover", clientId, peer, "action=reinitiate");
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
        LogEvent(
            "relay-policy",
            clientId,
            peer,
            $"relayOnly={Bool(relayOnly)} notifyRemote={Bool(notifyRemote)} nowMs={nowMs}");
        if (notifyRemote)
            SendSignal(clientId, peer, SignalMsgType.IceMode, SignalPayload.IceMode(relayOnly), "relay-policy-change");
        Reinitiate(clientId, peer, nowMs);
    }

    private void Reinitiate(int clientId, PeerEntry peer, long nowMs)
    {
        var previousGeneration = peer.Generation;
        var previousNegotiationId = peer.NegotiationId;
        ClearPendingLocalSignals(peer);
        peer.Generation = NextGeneration();
        peer.LastReinitMs = nowMs;
        peer.DegradedSinceMs = 0;
        LogEvent(
            "reinitiate",
            clientId,
            peer,
            $"previousGeneration={previousGeneration} previousNegotiation={previousNegotiationId} nowMs={nowMs}");
        if (peer.Added)
        {
            TransportRemovePeer(clientId, peer, "reinitiate");
            peer.Added = false;
        }
        if (LocalIsOfferer(clientId))
        {
            peer.NegotiationId = NextNegotiationId();
            peer.Added = true;
            SetState(clientId, peer, PeerState.Offering, "reinitiate-local-offerer");
            peer.LastProgressMs = nowMs;
            TransportAddPeer(clientId, peer, isOfferer: true, "reinitiate-local-offerer");
        }
        else
        {
            peer.RestartRequested = true;
            SetState(clientId, peer, PeerState.Greeted, "reinitiate-request-remote-offer");
            peer.LastProgressMs = 0;
            SendHello(clientId, peer, nowMs, force: true, reason: "reinitiate-answerer-hello");
            SendSignal(clientId, peer, SignalMsgType.Restart, Array.Empty<byte>(), "reinitiate-answerer-restart");
        }
    }

    public void OnPlayerLeft(int clientId)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("player-left", clientId, null, "peer-unknown");
            return;
        }
        LogEvent("player-left", clientId, peer, "action=send-bye-and-drop");
        SendSignal(clientId, peer, SignalMsgType.Bye, Array.Empty<byte>(), "player-left");
        DropPeer(clientId);
    }

    public void Reset()
        => ResetCore(notifyRemote: false, reason: "local-reset");

    public void ResetAndNotify(string reason)
        => ResetCore(notifyRemote: true, reason: reason);

    private void ResetCore(bool notifyRemote, string reason)
    {
        VoiceDiagnostics.Log(
            "signaling.session.reset",
            $"local={_localClientId} peers={_peers.Count} notifyRemote={Bool(notifyRemote)} reason={LogSafe(reason)}");
        foreach (var clientId in _peers.Keys.ToList())
        {
            var peer = _peers[clientId];
            if (notifyRemote)
                SendSignal(clientId, peer, SignalMsgType.Bye, Array.Empty<byte>(), $"reset:{reason}");
            DropPeer(clientId);
        }
    }

    public void OnSignal(int senderClientId, SignalMsgType type, byte[] payload, long nowMs = 0)
    {
        payload ??= Array.Empty<byte>();
        var payloadBytes = payload.Length;
        if (senderClientId == _localClientId)
        {
            LogReject("signal-rx", senderClientId, null, "sender-is-local", $"type={type} payloadBytes={payloadBytes}");
            return;
        }
        if (senderClientId < 0)
        {
            LogReject("signal-rx", senderClientId, null, "sender-unresolved", $"type={type} payloadBytes={payloadBytes}");
            return;
        }

        _peers.TryGetValue(senderClientId, out var existingPeer);
        LogEvent("signal-rx", senderClientId, existingPeer, $"type={type} payloadBytes={payloadBytes} nowMs={nowMs}");

        switch (type)
        {
            case SignalMsgType.Hello: HandleHello(senderClientId, payload, nowMs); break;
            case SignalMsgType.Offer: HandleOffer(senderClientId, payload, nowMs); break;
            case SignalMsgType.Answer: HandleAnswer(senderClientId, payload, nowMs); break;
            case SignalMsgType.Candidate: HandleCandidate(senderClientId, payload, nowMs); break;
            case SignalMsgType.Bye:
                LogEvent("signal-accepted", senderClientId, existingPeer, $"type={type} payloadBytes={payloadBytes}");
                DropPeer(senderClientId);
                break;
            case SignalMsgType.IceMode: HandleIceMode(senderClientId, payload, nowMs); break;
            case SignalMsgType.Restart: HandleRestart(senderClientId, payload, nowMs); break;
            default:
                LogReject("signal-rx", senderClientId, existingPeer, "unknown-type", $"type={(byte)type} payloadBytes={payloadBytes}");
                break;
        }
    }

    public void OnLocalSdp(int clientId, int generation, string sdpType, string sdp)
        => OnLocalSdp(clientId, generation, sdpType, sdp, Environment.TickCount64);

    internal void OnLocalSdp(int clientId, int generation, string sdpType, string sdp, long nowMs)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer))
        {
            LogGenerationReject("local-sdp", clientId, generation, $"sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        if (peer.NegotiationId <= 0)
        {
            LogReject("local-sdp", clientId, peer, "negotiation-missing", $"sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        var isOffer = string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase);
        LogEvent(
            "local-sdp",
            clientId,
            peer,
            $"accepted=true sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)} signalType={(isOffer ? SignalMsgType.Offer : SignalMsgType.Answer)}");
        var signal = new PendingLocalSignal(
            isOffer ? SignalMsgType.Offer : SignalMsgType.Answer,
            SignalPayload.Sdp(peer.NegotiationId, sdpType, sdp));
        peer.LastLocalSignalAttemptMs = nowMs;
        var sent = SendSignal(
            clientId,
            peer,
            signal.Type,
            signal.Payload,
            "local-sdp");
        if (!sent)
        {
            peer.PendingLocalSdp = signal;
            peer.LastProgressMs = nowMs;
            return;
        }

        peer.PendingLocalSdp = null;
        // SDP creation can be delayed by device/RTC startup. A successful Offer/Answer is fresh
        // handshake progress, so give the remote side the full timeout window from this point instead
        // of retaining the deadline from AddPeer/the incoming Offer.
        peer.LastProgressMs = nowMs;
        if (!isOffer) SetState(clientId, peer, PeerState.Connected, "local-answer-sent");
    }

    public void OnLocalCandidate(int clientId, int generation, string candidate)
        => OnLocalCandidate(clientId, generation, candidate, Environment.TickCount64);

    internal void OnLocalCandidate(int clientId, int generation, string candidate, long nowMs)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer))
        {
            LogGenerationReject("local-candidate", clientId, generation, $"candidateBytes={Utf8Length(candidate)}");
            return;
        }
        if (peer.NegotiationId <= 0)
        {
            LogReject("local-candidate", clientId, peer, "negotiation-missing", $"candidateBytes={Utf8Length(candidate)}");
            return;
        }
        peer.LocalCandidatesAttempted++;
        _localCandidatesAttempted++;
        LogEvent(
            "local-candidate",
            clientId,
            peer,
            $"accepted=true candidateBytes={Utf8Length(candidate)} peerCandidateAttempt={peer.LocalCandidatesAttempted} totalCandidateAttempts={_localCandidatesAttempted}");
        var signal = new PendingLocalSignal(
            SignalMsgType.Candidate,
            SignalPayload.Candidate(peer.NegotiationId, candidate));
        if (peer.PendingLocalSdp.HasValue || peer.PendingLocalCandidates.Count > 0)
        {
            QueuePendingLocalCandidate(
                clientId,
                peer,
                signal,
                peer.PendingLocalSdp.HasValue ? "waiting-for-sdp" : "preserve-candidate-order");
            return;
        }

        peer.LastLocalSignalAttemptMs = nowMs;
        if (!SendSignal(clientId, peer, signal.Type, signal.Payload, "local-candidate"))
            QueuePendingLocalCandidate(clientId, peer, signal, "send-failed");
    }

    private void QueuePendingLocalCandidate(
        int clientId,
        PeerEntry peer,
        PendingLocalSignal signal,
        string reason)
    {
        if (peer.PendingLocalCandidates.Any(item => item.Payload.AsSpan().SequenceEqual(signal.Payload)))
        {
            LogEvent(
                "local-candidate-buffer",
                clientId,
                peer,
                $"action=duplicate-skip reason={reason} pending={peer.PendingLocalCandidates.Count}");
            return;
        }

        if (peer.PendingLocalCandidates.Count >= MaxPendingLocalCandidates)
        {
            peer.PendingLocalCandidates.RemoveAt(0);
            LogReject(
                "local-candidate-buffer",
                clientId,
                peer,
                "capacity-evicted-oldest",
                $"capacity={MaxPendingLocalCandidates}");
        }

        peer.PendingLocalCandidates.Add(signal);
        LogEvent(
            "local-candidate-buffer",
            clientId,
            peer,
            $"action=queued reason={reason} pending={peer.PendingLocalCandidates.Count}");
    }

    private void RetryPendingLocalSignals(int clientId, PeerEntry peer, long nowMs)
    {
        if (!peer.PendingLocalSdp.HasValue && peer.PendingLocalCandidates.Count == 0) return;
        if (peer.LastLocalSignalAttemptMs != 0
            && nowMs - peer.LastLocalSignalAttemptMs < FailedLocalSignalRetryIntervalMs)
            return;

        peer.LastLocalSignalAttemptMs = nowMs;
        if (peer.PendingLocalSdp is { } sdp)
        {
            if (!SendSignal(clientId, peer, sdp.Type, sdp.Payload, "local-sdp-retry"))
                return;

            peer.PendingLocalSdp = null;
            peer.LastProgressMs = nowMs;
            if (sdp.Type == SignalMsgType.Answer)
                SetState(clientId, peer, PeerState.Connected, "local-answer-retry-sent");
        }

        while (peer.PendingLocalCandidates.Count > 0)
        {
            var candidate = peer.PendingLocalCandidates[0];
            if (!SendSignal(clientId, peer, candidate.Type, candidate.Payload, "local-candidate-retry"))
                return;
            peer.PendingLocalCandidates.RemoveAt(0);
        }
    }

    private static void ClearPendingLocalSignals(PeerEntry peer)
    {
        peer.PendingLocalSdp = null;
        peer.PendingLocalCandidates.Clear();
        peer.LastLocalSignalAttemptMs = 0;
    }

    private void HandleHello(int clientId, byte[] payload, long nowMs)
    {
        if (!SignalPayload.TryReadHello(payload, out var version, out var minCompatible))
        {
            LogReject("hello", clientId, FindPeer(clientId), "payload-invalid", $"payloadBytes={payload.Length}");
            return;
        }
        if (!IsCompatible(version, minCompatible))
        {
            LogReject(
                "hello",
                clientId,
                FindPeer(clientId),
                "protocol-incompatible",
                $"protocol={version} minCompatible={minCompatible} localProtocol={ProtocolVersion} localMinCompatible={MinCompatibleVersion}");
            return;
        }

        var peer = GetOrCreate(clientId);
        var firstCompatibleHello = !peer.HelloReceived;
        peer.HelloReceived = true;
        LogEvent(
            "signal-accepted",
            clientId,
            peer,
            $"type=Hello protocol={version} minCompatible={minCompatible} firstCompatible={Bool(firstCompatibleHello)}");

        // Even if OnPlayerJoined already sent a Hello, the remote may have attached its RPC
        // subscriber later and never observed it. Acknowledge the first compatible remote Hello
        // before AddPeer can synchronously emit Offer/candidates. A bounded refresh also lets a
        // replacement manager recover when its predecessor's Bye was lost, without turning two
        // healthy peers into an unbounded Hello echo loop.
        var acknowledgementAlreadySent = peer.RemoteHelloAcknowledged;
        var previousAcknowledgementMs = peer.LastHelloAcknowledgementMs;
        var acknowledgementRefreshDue = acknowledgementAlreadySent
                                        && nowMs >= previousAcknowledgementMs
                                        && nowMs - previousAcknowledgementMs >= HelloResendIntervalMs;
        var stateBeforeAcknowledgement = peer.State;
        var generationBeforeAcknowledgement = peer.Generation;
        var negotiationBeforeAcknowledgement = peer.NegotiationId;
        var addedBeforeAcknowledgement = peer.Added;
        if (!acknowledgementAlreadySent || acknowledgementRefreshDue)
        {
            var reason = acknowledgementRefreshDue
                ? "duplicate-remote-hello-ack"
                : peer.HelloSent ? "first-remote-hello-ack" : "initial-hello-response";
            // Set this before the send because a synchronous/reentrant signaling transport can
            // deliver the reciprocal Hello back into this manager before SendHello returns.
            peer.RemoteHelloAcknowledged = true;
            peer.LastHelloAcknowledgementMs = nowMs;
            if (!SendHello(clientId, peer, nowMs, force: true, reason: reason))
            {
                peer.RemoteHelloAcknowledged = acknowledgementAlreadySent;
                peer.LastHelloAcknowledgementMs = previousAcknowledgementMs;
                LogReject(
                    "hello",
                    clientId,
                    peer,
                    "ack-send-failed",
                    $"firstCompatible={Bool(firstCompatibleHello)} refresh={Bool(acknowledgementRefreshDue)}");
                return;
            }
        }

        // SendHello may synchronously feed a response back into this manager in tests and local
        // harnesses. Never mutate a peer that was replaced or removed during that callback.
        if (!_peers.TryGetValue(clientId, out var currentPeer) || !ReferenceEquals(currentPeer, peer))
            return;

        var activeGenerationStillUnchanged = peer.State == stateBeforeAcknowledgement
                                             && peer.Generation == generationBeforeAcknowledgement
                                             && peer.NegotiationId == negotiationBeforeAcknowledgement
                                             && peer.Added == addedBeforeAcknowledgement;
        if (acknowledgementRefreshDue
            && activeGenerationStillUnchanged
            && (peer.Added || peer.State is PeerState.Offering or PeerState.Connected or PeerState.Established))
        {
            RebootstrapAfterDuplicateHello(clientId, peer, nowMs);
            return;
        }
        TryStartSession(clientId, peer, nowMs);
    }

    private void RebootstrapAfterDuplicateHello(int clientId, PeerEntry peer, long nowMs)
    {
        var previousGeneration = peer.Generation;
        var previousNegotiationId = peer.NegotiationId;
        ClearPendingLocalSignals(peer);
        peer.PendingRemoteCandidates.Clear();
        peer.Generation = NextGeneration();
        peer.LastReinitMs = nowMs;
        peer.DegradedSinceMs = 0;
        peer.RestartRequested = false;
        if (peer.Added)
        {
            TransportRemovePeer(clientId, peer, "replacement-manager-hello");
            peer.Added = false;
        }

        if (LocalIsOfferer(clientId))
        {
            peer.NegotiationId = NextNegotiationId();
            peer.Added = true;
            SetState(clientId, peer, PeerState.Offering, "replacement-manager-hello-local-offerer");
            peer.LastProgressMs = nowMs;
            TransportAddPeer(clientId, peer, isOfferer: true, "replacement-manager-hello");
        }
        else
        {
            // A replacement process can restart its monotonic counter. Once its fresh Hello has
            // been acknowledged, forget the predecessor's negotiation id so the first new Offer
            // cannot be mistaken for a delayed/stale one.
            peer.NegotiationId = 0;
            SetState(clientId, peer, PeerState.Answering, "replacement-manager-hello-wait-for-offer");
            peer.LastProgressMs = nowMs;
        }

        LogEvent(
            "recover",
            clientId,
            peer,
            $"action=rebootstrap-replacement-manager previousGeneration={previousGeneration} previousNegotiation={previousNegotiationId} nowMs={nowMs}");
    }

    private void HandleOffer(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
        if (LocalIsOfferer(clientId))
        {
            LogReject("offer", clientId, knownPeer, "local-role-is-offerer", $"payloadBytes={payload.Length}");
            return;
        }
        if (!SignalPayload.TryReadSdp(payload, out var negotiationId, out var sdpType, out var sdp))
        {
            LogReject("offer", clientId, knownPeer, "payload-invalid", $"payloadBytes={payload.Length}");
            return;
        }
        if (negotiationId <= 0)
        {
            LogReject("offer", clientId, knownPeer, "negotiation-invalid", $"negotiation={negotiationId} sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        if (!string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase))
        {
            LogReject("offer", clientId, knownPeer, "sdp-type-mismatch", $"negotiation={negotiationId} sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("offer", clientId, null, "peer-unknown", $"negotiation={negotiationId} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        if (!peer.HelloReceived)
        {
            LogReject("offer", clientId, peer, "hello-not-received", $"negotiation={negotiationId} sdpBytes={Utf8Length(sdp)}");
            return;
        }

        // The offerer's id is monotonically increasing for this process. A duplicate offer must
        // not recreate the native peer, and a delayed offer from an older negotiation must never
        // replace the current one.
        if (negotiationId <= peer.NegotiationId)
        {
            LogReject("offer", clientId, peer, "duplicate-or-stale-negotiation", $"receivedNegotiation={negotiationId}");
            return;
        }

        if (peer.Added)
        {
            ClearPendingLocalSignals(peer);
            peer.Generation = NextGeneration();
            TransportRemovePeer(clientId, peer, "newer-remote-offer");
            peer.Added = false;
        }
        peer.NegotiationId = negotiationId;
        peer.RestartRequested = false;
        peer.Added = true;
        TransportAddPeer(clientId, peer, isOfferer: false, "remote-offer");
        SetState(clientId, peer, PeerState.Answering, "remote-offer-accepted");
        peer.LastProgressMs = nowMs;
        TransportSetRemoteSdp(clientId, peer, sdpType, sdp, "remote-offer");
        ReplayPendingCandidates(clientId, peer, negotiationId, nowMs);
        LogEvent("signal-accepted", clientId, peer, $"type=Offer sdpBytes={Utf8Length(sdp)} nowMs={nowMs}");
    }

    private void HandleAnswer(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
        if (!LocalIsOfferer(clientId))
        {
            LogReject("answer", clientId, knownPeer, "local-role-is-answerer", $"payloadBytes={payload.Length}");
            return;
        }
        if (!SignalPayload.TryReadSdp(payload, out var negotiationId, out var sdpType, out var sdp))
        {
            LogReject("answer", clientId, knownPeer, "payload-invalid", $"payloadBytes={payload.Length}");
            return;
        }
        if (!string.Equals(sdpType, "answer", StringComparison.OrdinalIgnoreCase))
        {
            LogReject("answer", clientId, knownPeer, "sdp-type-mismatch", $"negotiation={negotiationId} sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("answer", clientId, null, "peer-unknown", $"negotiation={negotiationId} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        if (!peer.HelloReceived)
        {
            LogReject("answer", clientId, peer, "hello-not-received", $"negotiation={negotiationId} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        if (negotiationId <= 0 || negotiationId != peer.NegotiationId)
        {
            LogReject("answer", clientId, peer, "negotiation-mismatch", $"receivedNegotiation={negotiationId}");
            return;
        }
        if (!peer.Added)
        {
            LogReject("answer", clientId, peer, "native-peer-not-added", $"receivedNegotiation={negotiationId}");
            return;
        }
        if (peer.State != PeerState.Offering)
        {
            LogReject("answer", clientId, peer, "state-not-offering", $"receivedNegotiation={negotiationId}");
            return;
        }
        TransportSetRemoteSdp(clientId, peer, sdpType, sdp, "remote-answer");
        SetState(clientId, peer, PeerState.Connected, "remote-answer-accepted");
        peer.LastProgressMs = nowMs;
        LogEvent("signal-accepted", clientId, peer, $"type=Answer sdpBytes={Utf8Length(sdp)} nowMs={nowMs}");
    }

    private void HandleCandidate(int clientId, byte[] payload, long nowMs)
    {
        _remoteCandidatesReceived++;
        var knownPeer = FindPeer(clientId);
        if (knownPeer != null)
        {
            knownPeer.RemoteCandidatesReceived++;
        }
        if (!SignalPayload.TryReadCandidate(payload, out var negotiationId, out var candidate))
        {
            CountRejectedCandidate(knownPeer);
            LogReject(
                "candidate",
                clientId,
                knownPeer,
                "payload-invalid",
                $"payloadBytes={payload.Length} peerCandidateRx={knownPeer?.RemoteCandidatesReceived ?? 0} totalCandidateRx={_remoteCandidatesReceived}");
            return;
        }

        if (!_peers.TryGetValue(clientId, out var peer))
        {
            CountRejectedCandidate(null);
            LogReject("candidate", clientId, null, "peer-unknown", $"negotiation={negotiationId} candidateBytes={Utf8Length(candidate)} totalCandidateRx={_remoteCandidatesReceived}");
            return;
        }
        if (!peer.HelloReceived)
        {
            CountRejectedCandidate(peer);
            LogReject("candidate", clientId, peer, "hello-not-received", $"receivedNegotiation={negotiationId} candidateBytes={Utf8Length(candidate)}");
            return;
        }
        if (negotiationId <= 0)
        {
            CountRejectedCandidate(peer);
            LogReject("candidate", clientId, peer, "negotiation-invalid", $"receivedNegotiation={negotiationId} candidateBytes={Utf8Length(candidate)}");
            return;
        }
        if (negotiationId != peer.NegotiationId)
        {
            // Native ICE can race slightly ahead of the SDP callback. On the answerer, retain only
            // candidates for a strictly newer negotiation until its Offer arrives; never attach
            // them to the current generation. The bounded queue mirrors the native pending-ICE cap.
            if (!LocalIsOfferer(clientId) && negotiationId > peer.NegotiationId)
            {
                QueuePendingCandidate(clientId, peer, negotiationId, candidate, nowMs);
                return;
            }
            CountRejectedCandidate(peer);
            LogReject("candidate", clientId, peer, "negotiation-mismatch", $"receivedNegotiation={negotiationId} candidateBytes={Utf8Length(candidate)}");
            return;
        }
        if (!peer.Added)
        {
            CountRejectedCandidate(peer);
            LogReject("candidate", clientId, peer, "native-peer-not-added", $"receivedNegotiation={negotiationId} candidateBytes={Utf8Length(candidate)}");
            return;
        }
        ForwardRemoteCandidate(clientId, peer, candidate, "live");
    }

    private void QueuePendingCandidate(
        int clientId,
        PeerEntry peer,
        long negotiationId,
        string candidate,
        long nowMs)
    {
        if (peer.PendingRemoteCandidates.Any(item =>
                item.NegotiationId == negotiationId
                && string.Equals(item.Candidate, candidate, StringComparison.Ordinal)))
        {
            LogEvent(
                "candidate-buffer",
                clientId,
                peer,
                $"action=duplicate-skip negotiation={negotiationId} pending={peer.PendingRemoteCandidates.Count}");
            return;
        }

        if (peer.PendingRemoteCandidates.Count >= MaxPendingRemoteCandidates)
        {
            var evicted = peer.PendingRemoteCandidates[0];
            peer.PendingRemoteCandidates.RemoveAt(0);
            CountRejectedCandidate(peer);
            LogReject(
                "candidate-buffer",
                clientId,
                peer,
                "capacity-evicted-oldest",
                $"evictedNegotiation={evicted.NegotiationId} capacity={MaxPendingRemoteCandidates}");
        }

        peer.PendingRemoteCandidates.Add(new PendingRemoteCandidate(negotiationId, candidate, nowMs));
        LogEvent(
            "candidate-buffer",
            clientId,
            peer,
            $"action=queued negotiation={negotiationId} candidateBytes={Utf8Length(candidate)} pending={peer.PendingRemoteCandidates.Count}");
    }

    private void ReplayPendingCandidates(int clientId, PeerEntry peer, long negotiationId, long nowMs)
    {
        if (peer.PendingRemoteCandidates.Count == 0) return;

        for (var index = peer.PendingRemoteCandidates.Count - 1; index >= 0; index--)
        {
            var pending = peer.PendingRemoteCandidates[index];
            if (HasPendingCandidateExpired(pending, nowMs) || pending.NegotiationId < negotiationId)
            {
                peer.PendingRemoteCandidates.RemoveAt(index);
                CountRejectedCandidate(peer);
                LogReject(
                    "candidate-buffer",
                    clientId,
                    peer,
                    HasPendingCandidateExpired(pending, nowMs) ? "expired" : "stale-negotiation",
                    $"candidateNegotiation={pending.NegotiationId} activeNegotiation={negotiationId}");
            }
        }

        // Preserve original candidate order when forwarding the matching negotiation.
        for (var index = 0; index < peer.PendingRemoteCandidates.Count;)
        {
            var pending = peer.PendingRemoteCandidates[index];
            if (pending.NegotiationId != negotiationId)
            {
                index++;
                continue;
            }

            peer.PendingRemoteCandidates.RemoveAt(index);
            ForwardRemoteCandidate(clientId, peer, pending.Candidate, "buffered-after-offer");
        }
    }

    private void PruneExpiredPendingCandidates(int clientId, PeerEntry peer, long nowMs)
    {
        for (var index = peer.PendingRemoteCandidates.Count - 1; index >= 0; index--)
        {
            var pending = peer.PendingRemoteCandidates[index];
            if (!HasPendingCandidateExpired(pending, nowMs)) continue;
            peer.PendingRemoteCandidates.RemoveAt(index);
            CountRejectedCandidate(peer);
            LogReject(
                "candidate-buffer",
                clientId,
                peer,
                "expired",
                $"candidateNegotiation={pending.NegotiationId} pending={peer.PendingRemoteCandidates.Count}");
        }
    }

    private static bool HasPendingCandidateExpired(PendingRemoteCandidate pending, long nowMs)
        => nowMs >= pending.ReceivedAtMs && nowMs - pending.ReceivedAtMs > PendingRemoteCandidateTtlMs;

    private void ForwardRemoteCandidate(int clientId, PeerEntry peer, string candidate, string source)
    {
        TransportAddIceCandidate(clientId, peer, candidate);
        peer.RemoteCandidatesForwarded++;
        _remoteCandidatesForwarded++;
        LogEvent(
            "signal-accepted",
            clientId,
            peer,
            $"type=Candidate source={source} candidateBytes={Utf8Length(candidate)} peerCandidateRx={peer.RemoteCandidatesReceived} peerCandidateForwarded={peer.RemoteCandidatesForwarded} totalCandidateRx={_remoteCandidatesReceived} totalCandidateForwarded={_remoteCandidatesForwarded}");
    }

    private void HandleRestart(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
        if (payload == null || payload.Length != 0)
        {
            LogReject("restart", clientId, knownPeer, "payload-invalid", $"payloadBytes={(payload == null ? 0 : payload.Length)}");
            return;
        }
        if (!LocalIsOfferer(clientId))
        {
            LogReject("restart", clientId, knownPeer, "local-role-is-answerer");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("restart", clientId, null, "peer-unknown");
            return;
        }
        if (!peer.HelloReceived)
        {
            LogReject("restart", clientId, peer, "hello-not-received");
            return;
        }
        if (peer.State == PeerState.Offering)
        {
            LogReject("restart", clientId, peer, "already-offering");
            return;
        }
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs)
        {
            LogReject("restart", clientId, peer, "reinit-throttled", $"nowMs={nowMs} lastReinitMs={peer.LastReinitMs}");
            return;
        }
        LogEvent("signal-accepted", clientId, peer, $"type=Restart nowMs={nowMs}");
        Reinitiate(clientId, peer, nowMs);
    }

    private void HandleIceMode(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
        if (!SignalPayload.TryReadIceMode(payload, out var requestedRelay))
        {
            LogReject("ice-mode", clientId, knownPeer, "payload-invalid", $"payloadBytes={payload.Length}");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("ice-mode", clientId, null, "peer-unknown", $"requestedRelay={Bool(requestedRelay)}");
            return;
        }
        if (!peer.HelloReceived)
        {
            LogReject("ice-mode", clientId, peer, "hello-not-received", $"requestedRelay={Bool(requestedRelay)}");
            return;
        }
        if (requestedRelay && !RelayAvailable())
        {
            // The other endpoint has identified this pair as needing TURN. Fetch credentials for
            // this peer, but keep any currently healthy direct generation alive until they arrive.
            peer.RelayRequested = true;
            LogEvent("signal-accepted", clientId, peer, "type=IceMode requestedRelay=true action=request-credentials");
            _requestRelay(clientId);
            return;
        }

        // A remote direct-mode reset must not override this client's automatic relay-only session
        // latch. One relay endpoint can still pair with a direct endpoint.
        var relayOnly = requestedRelay || (ForceRelay() && RelayAvailable());
        var changed = peer.RelayOnly != relayOnly;
        peer.RelayOnly = relayOnly;
        peer.RelayRequested = false;

        // A duplicate request is useful only for an already connected generation. During an
        // active offer/answer it is most likely the other side seeing the same failure, so let the
        // in-flight recreation finish instead of starting an offer storm.
        LogEvent(
            "signal-accepted",
            clientId,
            peer,
            $"type=IceMode requestedRelay={Bool(requestedRelay)} effectiveRelayOnly={Bool(relayOnly)} changed={Bool(changed)} nowMs={nowMs}");
        if (!changed && peer.State is not (PeerState.Connected or PeerState.Established))
        {
            LogReject("ice-mode", clientId, peer, "duplicate-during-negotiation", $"requestedRelay={Bool(requestedRelay)}");
            return;
        }
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs)
        {
            LogReject("ice-mode", clientId, peer, "reinit-throttled", $"nowMs={nowMs} lastReinitMs={peer.LastReinitMs}");
            return;
        }
        Reinitiate(clientId, peer, nowMs);
    }

    private void TryStartSession(int clientId, PeerEntry peer, long nowMs)
    {
        if (!peer.HelloReceived)
        {
            LogReject("start-session", clientId, peer, "hello-not-received");
            return;
        }
        if (peer.State is PeerState.Connected or PeerState.Established)
        {
            LogReject("start-session", clientId, peer, "already-connected");
            return;
        }

        if (LocalIsOfferer(clientId))
        {
            if (peer.Added)
            {
                LogReject("start-session", clientId, peer, "native-peer-already-added");
                return;
            }
            peer.NegotiationId = NextNegotiationId();
            peer.Added = true;
            SetState(clientId, peer, PeerState.Offering, "compatible-hello-local-offerer");
            peer.LastProgressMs = nowMs;
            TransportAddPeer(clientId, peer, isOfferer: true, "start-session");
        }
        else if (peer.State is PeerState.Idle or PeerState.Greeted)
        {
            SetState(clientId, peer, PeerState.Answering, "compatible-hello-wait-for-offer");
            peer.LastProgressMs = nowMs;
        }
        else
        {
            LogReject("start-session", clientId, peer, "state-not-startable");
        }
    }

    private bool SendHelloIfNeeded(int clientId, PeerEntry peer, long nowMs)
        => SendHello(clientId, peer, nowMs, force: false, reason: "initial-hello");

    private bool SendHello(int clientId, PeerEntry peer, long nowMs, bool force, string reason)
    {
        if (!force && peer.HelloSent)
        {
            LogEvent("hello-skip", clientId, peer, "reason=already-sent");
            return true;
        }

        var previouslySent = peer.HelloSent;
        // Set before invoking the sender because tests and local transports can synchronously feed
        // the remote response back into this manager. Revert only a first-send failure.
        peer.HelloSent = true;
        peer.LastHelloSentMs = nowMs;
        if (peer.State == PeerState.Idle) SetState(clientId, peer, PeerState.Greeted, "send-initial-hello");
        var sent = SendSignal(
            clientId,
            peer,
            SignalMsgType.Hello,
            SignalPayload.Hello(ProtocolVersion, MinCompatibleVersion),
            reason);
        if (!sent && !previouslySent)
            peer.HelloSent = false;
        if (sent && peer.RelayOnly)
            SendSignal(clientId, peer, SignalMsgType.IceMode, SignalPayload.IceMode(true), "initial-relay-policy");
        return sent;
    }

    private void DropPeer(int clientId)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("drop-peer", clientId, null, "peer-unknown");
            return;
        }

        LogEvent("drop-peer", clientId, peer, "begin=true");
        TransportRemovePeer(clientId, peer, "drop-peer");
        _peers.Remove(clientId);
        VoiceDiagnostics.Log("signaling.session.peer-removed", $"local={_localClientId} peer={clientId} remainingPeers={_peers.Count}");
    }

    private bool LocalIsOfferer(int clientId) => _localClientId < clientId;

    private long NextNegotiationId()
    {
        while (true)
        {
            var current = Interlocked.Read(ref _nextProcessNegotiationId);
            var next = current == long.MaxValue ? 1 : current + 1;
            if (Interlocked.CompareExchange(ref _nextProcessNegotiationId, next, current) == current)
                return next;
        }
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
            LogEvent("peer-created", clientId, peer, $"localOfferer={Bool(LocalIsOfferer(clientId))}");
        }
        return peer;
    }

    private bool RelayAvailable()
    {
        try { return _relayAvailable(); }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "signaling.session.error",
                $"local={_localClientId} operation=relay-available errorType={ex.GetType().Name} error=\"{LogSafe(ex.Message)}\"");
            return false;
        }
    }

    private bool ForceRelay()
    {
        try { return _forceRelay(); }
        catch (Exception ex)
        {
            VoiceDiagnostics.Log(
                "signaling.session.error",
                $"local={_localClientId} operation=force-relay errorType={ex.GetType().Name} error=\"{LogSafe(ex.Message)}\"");
            return false;
        }
    }

    private static int NextGeneration()
    {
        while (true)
        {
            var current = Volatile.Read(ref _nextProcessGeneration);
            var next = current == int.MaxValue ? 1 : current + 1;
            if (Interlocked.CompareExchange(ref _nextProcessGeneration, next, current) == current)
                return next;
        }
    }

    private PeerEntry? FindPeer(int clientId)
        => _peers.TryGetValue(clientId, out var peer) ? peer : null;

    private bool SendSignal(
        int clientId,
        PeerEntry peer,
        SignalMsgType type,
        byte[] payload,
        string reason)
    {
        LogEvent(
            "signal-tx",
            clientId,
            peer,
            $"type={type} payloadBytes={payload.Length} reason={reason}");
        try
        {
            var sent = _sender.Send(clientId, type, payload);
            if (!sent)
                LogReject("signal-tx", clientId, peer, "send-failed", $"type={type} reason={reason}");
            return sent;
        }
        catch (Exception ex)
        {
            LogError("signal-tx", clientId, peer, ex, $"type={type} reason={reason}");
            return false;
        }
    }

    private void SetState(int clientId, PeerEntry peer, PeerState state, string reason)
    {
        var previous = peer.State;
        peer.State = state;
        VoiceDiagnostics.Log(
            "signaling.session.state",
            $"{PeerFields(clientId, peer)} previous={previous} current={state} changed={Bool(previous != state)} reason={reason}");
    }

    private void TransportAddPeer(int clientId, PeerEntry peer, bool isOfferer, string reason)
    {
        LogEvent(
            "transport-command",
            clientId,
            peer,
            $"command=add-peer isOfferer={Bool(isOfferer)} relayOnly={Bool(peer.RelayOnly)} reason={reason}");
        try
        {
            _transport.AddPeer(clientId, isOfferer, peer.RelayOnly, peer.Generation);
            LogEvent("transport-command-returned", clientId, peer, $"command=add-peer reason={reason} nativeAck=false");
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, $"command=add-peer reason={reason}");
            throw;
        }
    }

    private void TransportRemovePeer(int clientId, PeerEntry peer, string reason)
    {
        LogEvent("transport-command", clientId, peer, $"command=remove-peer reason={reason}");
        try
        {
            _transport.RemovePeer(clientId);
            LogEvent("transport-command-returned", clientId, peer, $"command=remove-peer reason={reason} nativeAck=false");
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, $"command=remove-peer reason={reason}");
            throw;
        }
    }

    private void TransportSetRemoteSdp(int clientId, PeerEntry peer, string sdpType, string sdp, string reason)
    {
        LogEvent(
            "transport-command",
            clientId,
            peer,
            $"command=set-remote-sdp sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)} reason={reason}");
        try
        {
            _transport.SetRemoteSdp(clientId, sdpType, sdp);
            LogEvent(
                "transport-command-returned",
                clientId,
                peer,
                $"command=set-remote-sdp sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)} reason={reason} nativeAck=false");
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, $"command=set-remote-sdp sdpType={LogSafe(sdpType)} reason={reason}");
            throw;
        }
    }

    private void TransportAddIceCandidate(int clientId, PeerEntry peer, string candidate)
    {
        LogEvent(
            "transport-command",
            clientId,
            peer,
            $"command=add-ice-candidate candidateBytes={Utf8Length(candidate)} peerCandidateRx={peer.RemoteCandidatesReceived}");
        try
        {
            _transport.AddIceCandidate(clientId, candidate);
            LogEvent(
                "transport-command-returned",
                clientId,
                peer,
                $"command=add-ice-candidate candidateBytes={Utf8Length(candidate)} nativeAck=false");
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, "command=add-ice-candidate");
            throw;
        }
    }

    private void CountRejectedCandidate(PeerEntry? peer)
    {
        _rejectedCandidates++;
        if (peer != null) peer.RejectedCandidates++;
    }

    private void LogGenerationReject(string operation, int clientId, int receivedGeneration, string detail = "")
    {
        var peer = FindPeer(clientId);
        var reason = peer == null ? "peer-unknown" : "generation-mismatch";
        LogReject(
            operation,
            clientId,
            peer,
            reason,
            $"receivedGeneration={receivedGeneration} currentGeneration={peer?.Generation ?? -1} {detail}".TrimEnd());
    }

    private void LogEvent(string operation, int clientId, PeerEntry? peer, string detail)
    {
        VoiceDiagnostics.Log(
            $"signaling.session.{operation}",
            $"{PeerFields(clientId, peer)} {detail}".TrimEnd());
    }

    private void LogReject(string operation, int clientId, PeerEntry? peer, string reason, string detail = "")
    {
        VoiceDiagnostics.Log(
            "signaling.session.reject",
            $"{PeerFields(clientId, peer)} operation={operation} reason={reason} {detail}".TrimEnd());
    }

    private void LogError(string operation, int clientId, PeerEntry? peer, Exception ex, string detail)
    {
        VoiceDiagnostics.Log(
            "signaling.session.error",
            $"{PeerFields(clientId, peer)} operation={operation} {detail} errorType={ex.GetType().Name} error=\"{LogSafe(ex.Message)}\"");
    }

    private string PeerFields(int clientId, PeerEntry? peer)
    {
        if (peer == null)
            return $"local={_localClientId} peer={clientId} state=none generation=-1 negotiation=0 relayOnly=false added=false";
        return $"local={_localClientId} peer={clientId} state={peer.State} generation={peer.Generation} negotiation={peer.NegotiationId} relayOnly={Bool(peer.RelayOnly)} added={Bool(peer.Added)} helloSent={Bool(peer.HelloSent)} helloReceived={Bool(peer.HelloReceived)} restartRequested={Bool(peer.RestartRequested)}";
    }

    private static int Utf8Length(string? value)
        => Encoding.UTF8.GetByteCount(value ?? string.Empty);

    private static string LogSafe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var sanitized = value.Replace('\\', '/').Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= 96 ? sanitized : sanitized.Substring(0, 96);
    }

    private static string Bool(bool value) => value ? "true" : "false";

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
