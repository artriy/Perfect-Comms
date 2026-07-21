using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

internal interface IVoiceTransport
{
    bool RequiresNativeOperationAcknowledgements { get; }
    bool AddPeer(int clientId, bool isOfferer, int generation);
    bool RemovePeer(int clientId, int generation);
    bool SetRemoteSdp(int clientId, int generation, string sdpType, string sdp);
    bool AddIceCandidate(int clientId, int generation, string candidate);
    bool RestartIce(int clientId, int generation, bool createOffer);
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

    public static byte[] Hello(int protocolVersion, int minCompatibleVersion, long sessionId)
    {
        using var stream = new MemoryStream();
        var header = Hello(protocolVersion, minCompatibleVersion);
        stream.Write(header, 0, header.Length);
        WriteInt64(stream, sessionId);
        return stream.ToArray();
    }

    public static bool TryReadHello(byte[] payload, out int protocolVersion, out int minCompatibleVersion)
        => TryReadHello(payload, out protocolVersion, out minCompatibleVersion, out _);

    public static bool TryReadHello(
        byte[] payload,
        out int protocolVersion,
        out int minCompatibleVersion,
        out long sessionId)
    {
        protocolVersion = 0;
        minCompatibleVersion = 0;
        sessionId = 0;
        if (payload == null || payload.Length is not (8 or 16)) return false;
        protocolVersion = ReadInt32(payload, 0);
        minCompatibleVersion = ReadInt32(payload, 4);
        if (payload.Length == 16)
        {
            var offset = 8;
            if (!TryReadInt64(payload, ref offset, out sessionId) || offset != payload.Length)
                return false;
        }
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

    public static byte[] Sdp(long sessionId, long negotiationId, string sdpType, string sdp)
    {
        using var stream = new MemoryStream();
        WriteInt64(stream, sessionId);
        WriteInt64(stream, negotiationId);
        WriteString(stream, sdpType);
        WriteString(stream, sdp);
        return stream.ToArray();
    }

    public static bool TryReadSdp(byte[] payload, out long negotiationId, out string sdpType, out string sdp)
    {
        if (TryReadLegacySdp(payload, out negotiationId, out sdpType, out sdp))
            return true;
        return TryReadScopedSdp(payload, out _, out negotiationId, out sdpType, out sdp);
    }

    public static bool TryReadScopedSdp(
        byte[] payload,
        out long sessionId,
        out long negotiationId,
        out string sdpType,
        out string sdp)
    {
        sessionId = 0;
        negotiationId = 0;
        sdpType = string.Empty;
        sdp = string.Empty;
        var offset = 0;
        return TryReadInt64(payload, ref offset, out sessionId)
               && TryReadInt64(payload, ref offset, out negotiationId)
               && TryReadString(payload, ref offset, out sdpType)
               && TryReadString(payload, ref offset, out sdp)
               && offset == payload.Length;
    }

    public static bool TryReadLegacySdp(byte[] payload, out long negotiationId, out string sdpType, out string sdp)
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

    public static byte[] Candidate(long sessionId, long negotiationId, string candidate)
    {
        using var stream = new MemoryStream();
        WriteInt64(stream, sessionId);
        WriteInt64(stream, negotiationId);
        WriteString(stream, candidate);
        return stream.ToArray();
    }

    public static bool TryReadCandidate(byte[] payload, out long negotiationId, out string candidate)
    {
        if (TryReadLegacyCandidate(payload, out negotiationId, out candidate))
            return true;
        return TryReadScopedCandidate(payload, out _, out negotiationId, out candidate);
    }

    public static bool TryReadScopedCandidate(
        byte[] payload,
        out long sessionId,
        out long negotiationId,
        out string candidate)
    {
        sessionId = 0;
        negotiationId = 0;
        candidate = string.Empty;
        var offset = 0;
        return TryReadInt64(payload, ref offset, out sessionId)
               && TryReadInt64(payload, ref offset, out negotiationId)
               && TryReadString(payload, ref offset, out candidate)
               && offset == payload.Length;
    }

    public static bool TryReadLegacyCandidate(byte[] payload, out long negotiationId, out string candidate)
    {
        negotiationId = 0;
        candidate = string.Empty;
        var offset = 0;
        return TryReadInt64(payload, ref offset, out negotiationId)
               && TryReadString(payload, ref offset, out candidate)
               && offset == payload.Length;
    }

    public static byte[] Control(long sessionId, long negotiationId)
    {
        using var stream = new MemoryStream();
        WriteInt64(stream, sessionId);
        WriteInt64(stream, negotiationId);
        return stream.ToArray();
    }

    public static bool TryReadControl(byte[] payload, out long sessionId, out long negotiationId)
    {
        sessionId = 0;
        negotiationId = 0;
        var offset = 0;
        return TryReadInt64(payload, ref offset, out sessionId)
               && TryReadInt64(payload, ref offset, out negotiationId)
               && offset == payload.Length;
    }

    public static byte[] IceMode(bool relayOnly) => [relayOnly ? (byte)1 : (byte)0];

    public static byte[] IceMode(long sessionId, long negotiationId, bool relayOnly)
    {
        using var stream = new MemoryStream();
        WriteInt64(stream, sessionId);
        WriteInt64(stream, negotiationId);
        stream.WriteByte(relayOnly ? (byte)1 : (byte)0);
        return stream.ToArray();
    }

    public static bool TryReadIceMode(byte[] payload, out bool relayOnly)
    {
        relayOnly = false;
        if (payload == null || payload.Length != 1 || payload[0] > 1) return false;
        relayOnly = payload[0] == 1;
        return true;
    }

    public static bool TryReadIceMode(
        byte[] payload,
        out long sessionId,
        out long negotiationId,
        out bool relayOnly)
    {
        sessionId = 0;
        negotiationId = 0;
        relayOnly = false;
        var offset = 0;
        if (!TryReadInt64(payload, ref offset, out sessionId)
            || !TryReadInt64(payload, ref offset, out negotiationId)
            || offset + 1 != payload.Length
            || payload[offset] > 1)
            return false;
        relayOnly = payload[offset] == 1;
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
    public const int ProtocolVersion = 5;
    public const int MinCompatibleVersion = 3;
    private const int ScopedControlProtocolVersion = 4;
    private const int IceRestartProtocolVersion = 5;
    private const long HelloResendIntervalMs = 3000;
    private const long FailedHelloAckRetryIntervalMs = 500;
    internal const long DirectHandshakeTimeoutMs = 8000;
    internal const long RelayHandshakeTimeoutMs = 12000;
    // Retained for callers/tests that need the maximum manager-owned negotiation ceiling.
    internal const long HandshakeTimeoutMs = RelayHandshakeTimeoutMs;
    private const long ReinitThrottleMs = 3000;
    private const long DisconnectedGraceMs = 8000;
    private const int MaxPendingRemoteCandidates = 64;
    private const long PendingRemoteCandidateTtlMs = 12000;
    private const long FailedLocalSignalRetryIntervalMs = 500;
    private const long IceRestartOfferTimeoutMs = 5000;
    private const int MaxPendingLocalCandidates = 64;
    internal const long NativeOperationAckTimeoutMs = 3000;
    private const long FailedTransportCommandRetryIntervalMs = 500;
    private const long RelayCredentialRetryIntervalMs = 5000;
    private const long MaxHelloBackoffMs = 60000;
    private const int MaxRetiredRemoteSessions = 8;
    private const int SynchronousResetRemovalRetryAttempts = 2;

    private readonly record struct PendingRemoteCandidate(long NegotiationId, string Candidate, long ReceivedAtMs);
    private readonly record struct PendingRemoteSdp(long NegotiationId, string SdpType, string Sdp);
    private readonly record struct PendingLocalSignal(SignalMsgType Type, byte[] Payload);

    private enum AfterRemoveAction
    {
        None,
        Reinitiate,
        Rebootstrap,
    }

    private sealed class PendingRemoval
    {
        public PendingRemoval(PeerEntry peer, long lastAttemptMs)
        {
            Peer = peer;
            LastAttemptMs = lastAttemptMs;
        }

        public PeerEntry Peer { get; }
        public long LastAttemptMs { get; set; }
        public bool RejoinRequested { get; set; }
    }

    private sealed class PeerEntry
    {
        public PeerState State = PeerState.Idle;
        public bool HelloSent;
        public bool ScopedHelloSent;
        public bool HelloReceived;
        public bool RemoteHelloAcknowledged;
        public long LastHelloAcknowledgementMs;
        public int UnansweredHelloCount;
        public bool Incompatible;
        public int RemoteProtocolVersion;
        public long RemoteSessionId;
        public readonly List<long> RetiredRemoteSessionIds = new();
        public bool RebootstrapAckPending;
        public bool Added;
        public long LastHelloSentMs;
        public long LastProgressMs;
        public long LastReinitMs;
        public long DegradedSinceMs;
        public long LastRelayRequestMs;
        public bool RelayRequested;
        public bool RelayCapable;
        public int Generation;
        public long NegotiationId;
        public bool NegotiationShared;
        public bool RestartRequested;
        public bool IceRestartInProgress;
        public bool IceRestartRequestPending;
        public long IceRestartRequestStartedMs;
        public long LastIceRestartRequestMs;
        public long LocalCandidatesAttempted;
        public long RemoteCandidatesReceived;
        public long RemoteCandidatesForwarded;
        public long RejectedCandidates;
        public readonly List<PendingRemoteCandidate> PendingRemoteCandidates = new();
        public bool PendingTransportAdd;
        public bool PendingTransportAddOfferer;
        public bool PendingTransportRemove;
        public AfterRemoveAction AfterRemove;
        public bool NativePeerApplied;
        public bool NativeRemoteDescriptionApplied;
        public long NativePeerAddRequestedAtMs;
        public long NativeRemoteSdpRequestedAtMs;
        public bool NativeAckTimeoutReported;
        public PendingRemoteSdp? PendingTransportSdp;
        public long LastTransportCommandAttemptMs;
        public PendingLocalSignal? PendingLocalSdp;
        public readonly List<PendingLocalSignal> PendingLocalCandidates = new();
        public long LastLocalSignalAttemptMs;
    }

    private readonly int _localClientId;
    private readonly IVoiceTransport _transport;
    private readonly ISignalingSender _sender;
    private readonly Func<bool> _relayAvailable;
    private readonly Action<string> _nativeOperationTimeout;
    private readonly Action<int> _requestRelay;
    private readonly Dictionary<int, PeerEntry> _peers = new();
    private readonly Dictionary<int, PendingRemoval> _pendingRemovals = new();
    private readonly long _localSessionId;
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
        Action<string>? nativeOperationTimeout = null)
    {
        _localClientId = localClientId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _relayAvailable = relayAvailable ?? (() => false);
        _requestRelay = requestRelay ?? (_ => { });
        _nativeOperationTimeout = nativeOperationTimeout ?? (_ => { });
        _localSessionId = CreateSessionNonce();
    }

    public static bool IsCompatible(int remoteProtocolVersion, int remoteMinCompatibleVersion)
        => remoteProtocolVersion >= MinCompatibleVersion && ProtocolVersion >= remoteMinCompatibleVersion;

    internal int LocalClientId => _localClientId;

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

    internal bool HasRelayPeers => _peers.Values.Any(peer => peer.RelayCapable);

    internal bool IsCompatiblePeer(int clientId)
        => _peers.TryGetValue(clientId, out var peer) && peer.HelloReceived;

    internal bool IsPeerIncompatible(int clientId)
        => _peers.TryGetValue(clientId, out var peer) && peer.Incompatible;

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
            if (IsWithinNativeOperationDeadline(peer.NativePeerAddRequestedAtMs, nowMs)
                || IsWithinNativeOperationDeadline(peer.NativeRemoteSdpRequestedAtMs, nowMs))
                return true;
            if (peer.IceRestartRequestPending)
            {
                var restartWaitElapsedMs = nowMs - peer.IceRestartRequestStartedMs;
                if (peer.IceRestartRequestStartedMs > 0
                    && restartWaitElapsedMs >= 0
                    && restartWaitElapsedMs < IceRestartOfferTimeoutMs)
                    return true;
            }
            if (peer.State is not (PeerState.Offering or PeerState.Answering or PeerState.Connected))
                continue;
            if (peer.LastProgressMs <= 0)
                continue;

            var elapsedMs = nowMs - peer.LastProgressMs;
            if (elapsedMs >= 0 && elapsedMs < HandshakeTimeoutFor(peer))
                return true;
        }

        return false;
    }

    private static bool IsWithinNativeOperationDeadline(long startedAtMs, long nowMs)
        => startedAtMs > 0
           && nowMs >= startedAtMs
           && nowMs - startedAtMs < NativeOperationAckTimeoutMs;

    private bool CheckNativeOperationDeadline(int clientId, PeerEntry peer, long nowMs)
    {
        if (!_transport.RequiresNativeOperationAcknowledgements || peer.NativeAckTimeoutReported)
            return peer.NativeAckTimeoutReported;

        string? operation = null;
        long startedAtMs = 0;
        if (peer.NativePeerAddRequestedAtMs > 0)
        {
            operation = "peer-add";
            startedAtMs = peer.NativePeerAddRequestedAtMs;
        }
        else if (peer.NativeRemoteSdpRequestedAtMs > 0)
        {
            operation = "set-remote-sdp";
            startedAtMs = peer.NativeRemoteSdpRequestedAtMs;
        }
        if (operation == null
            || nowMs < startedAtMs
            || nowMs - startedAtMs < NativeOperationAckTimeoutMs)
            return false;

        peer.NativeAckTimeoutReported = true;
        var detail = $"operation={operation} client={clientId} generation={peer.Generation} waitMs={nowMs - startedAtMs}";
        LogReject("native-operation-ack", clientId, peer, "timeout", detail);
        _nativeOperationTimeout(detail);
        return true;
    }

    public void OnNativeOperationApplied(int clientId, int generation, string operation)
        => OnNativeOperationApplied(clientId, generation, operation, Environment.TickCount64);

    internal void OnNativeOperationApplied(
        int clientId,
        int generation,
        string operation,
        long nowMs)
    {
        if (!TryGetCurrentPeer(clientId, generation, out var peer))
        {
            LogGenerationReject("native-operation-ack", clientId, generation, $"operation={LogSafe(operation)}");
            return;
        }

        if (string.Equals(operation, "native-peer-added", StringComparison.Ordinal))
        {
            if (!peer.Added)
            {
                LogReject("native-operation-ack", clientId, peer, "peer-not-added", $"operation={operation}");
                return;
            }
            CompleteNativePeerAdd(clientId, peer, nowMs, operation);
            return;
        }
        if (string.Equals(operation, "native-remote-sdp-applied", StringComparison.Ordinal))
        {
            if (peer.NativeRemoteSdpRequestedAtMs <= 0 || !peer.PendingTransportSdp.HasValue)
            {
                LogReject("native-operation-ack", clientId, peer, "no-pending-operation", $"operation={operation}");
                return;
            }
            CompletePendingRemoteSdp(clientId, peer, nowMs, operation);
            ReplayPendingCandidates(clientId, peer, peer.NegotiationId, nowMs);
            return;
        }
        if (operation is "native-peer-removed" or "native-ice-restarted")
        {
            LogEvent("native-operation-ack", clientId, peer, $"operation={operation} accepted=true");
            return;
        }

        LogReject("native-operation-ack", clientId, peer, "operation-unknown", $"operation={LogSafe(operation)}");
    }

    private void CompleteNativePeerAdd(int clientId, PeerEntry peer, long nowMs, string source)
    {
        peer.NativePeerApplied = true;
        peer.NativePeerAddRequestedAtMs = 0;
        peer.NativeAckTimeoutReported = false;
        peer.LastProgressMs = nowMs;
        LogEvent("native-operation-ack", clientId, peer, $"operation=native-peer-added source={source} accepted=true");
        if (peer.PendingTransportSdp.HasValue
            && TryWritePendingRemoteSdp(clientId, peer, nowMs, "native-peer-added"))
            ReplayPendingCandidates(clientId, peer, peer.NegotiationId, nowMs);
    }

    private void CompletePendingRemoteSdp(int clientId, PeerEntry peer, long nowMs, string source)
    {
        if (peer.PendingTransportSdp is not { } pending)
            return;
        peer.NativeRemoteDescriptionApplied = true;
        peer.PendingTransportSdp = null;
        peer.NativeRemoteSdpRequestedAtMs = 0;
        peer.NativeAckTimeoutReported = false;
        peer.LastProgressMs = nowMs;
        if (string.Equals(pending.SdpType, "answer", StringComparison.OrdinalIgnoreCase))
            SetState(clientId, peer, PeerState.Connected, "remote-answer-applied");
        LogEvent(
            "native-operation-ack",
            clientId,
            peer,
            $"operation=native-remote-sdp-applied source={source} accepted=true sdpType={LogSafe(pending.SdpType)}");
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
                    return $"{pair.Key}:{peer.State}/g{peer.Generation}/n{peer.NegotiationId}/turn{Bool(peer.RelayCapable)}/added{Bool(peer.Added)}/candAttempts{peer.LocalCandidatesAttempted}-rx{peer.RemoteCandidatesReceived}-forwarded{peer.RemoteCandidatesForwarded}-rejected{peer.RejectedCandidates}";
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
        if (peer.Incompatible)
        {
            LogReject("recover", clientId, peer, "protocol-incompatible");
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
        if (_pendingRemovals.TryGetValue(clientId, out var pendingRemoval))
        {
            pendingRemoval.RejoinRequested = true;
            LogReject("player-joined", clientId, pendingRemoval.Peer, "native-removal-pending", $"nowMs={nowMs}");
            return;
        }
        var peer = GetOrCreate(clientId);
        LogEvent("player-joined", clientId, peer, $"nowMs={nowMs}");
        SendHelloIfNeeded(clientId, peer, nowMs);
        TryStartSession(clientId, peer, nowMs);
    }

    public void Tick(long nowMs)
    {
        RetryPendingRemovals(nowMs);
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            PruneExpiredPendingCandidates(pair.Key, peer, nowMs);
            if (CheckNativeOperationDeadline(pair.Key, peer, nowMs))
                continue;
            RetryPendingTransportCommands(pair.Key, peer, nowMs);
            if (peer.PendingTransportRemove)
                continue;
            if (peer.RebootstrapAckPending)
            {
                RetryPendingRebootstrapAcknowledgement(pair.Key, peer, nowMs);
                continue;
            }
            if (peer.Incompatible)
                continue;
            if (peer.IceRestartRequestPending
                && peer.Added
                && peer.State == PeerState.Established)
            {
                if (peer.IceRestartRequestStartedMs > 0
                    && nowMs >= peer.IceRestartRequestStartedMs
                    && nowMs - peer.IceRestartRequestStartedMs >= IceRestartOfferTimeoutMs)
                {
                    LogEvent(
                        "ice-restart",
                        pair.Key,
                        peer,
                        $"mode=recreate reason=restart-offer-timeout waitMs={nowMs - peer.IceRestartRequestStartedMs}");
                    Reinitiate(pair.Key, peer, nowMs);
                    continue;
                }
                if (peer.LastIceRestartRequestMs == 0
                    || nowMs - peer.LastIceRestartRequestMs >= FailedLocalSignalRetryIntervalMs)
                {
                    peer.LastIceRestartRequestMs = nowMs;
                    SendSignal(
                        pair.Key,
                        peer,
                        SignalMsgType.IceRestartRequest,
                        ControlPayload(peer, SignalMsgType.IceRestartRequest),
                        "ice-restart-request-retry");
                }
            }
            if (peer.State is PeerState.Idle or PeerState.Greeted)
            {
                var acknowledgementPending = peer.HelloReceived && !peer.RemoteHelloAcknowledged;
                var firstHelloFailed = !peer.HelloSent;
                var retryInterval = acknowledgementPending || firstHelloFailed
                    ? FailedHelloAckRetryIntervalMs
                    : HelloRetryIntervalMs(peer.UnansweredHelloCount);
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
                    SendSignal(pair.Key, peer, SignalMsgType.Restart, ControlPayload(peer, SignalMsgType.Restart), "restart-resend-with-hello");
            }
            else if (peer.State is PeerState.Offering or PeerState.Answering or PeerState.Connected)
            {
                RetryPendingLocalSignals(pair.Key, peer, nowMs);
                if (peer.LastProgressMs == 0) continue;
                if (nowMs - peer.LastProgressMs < HandshakeTimeoutFor(peer)) continue;
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
        if (peer.Incompatible)
        {
            LogReject("peer-connected", clientId, peer, "protocol-incompatible");
            return;
        }
        SetState(clientId, peer, PeerState.Established, "native-peer-connected");
        peer.LastProgressMs = 0;
        peer.LastReinitMs = 0;
        peer.DegradedSinceMs = 0;
        peer.LastRelayRequestMs = 0;
        peer.RelayRequested = false;
        peer.IceRestartInProgress = false;
        peer.IceRestartRequestPending = false;
        peer.IceRestartRequestStartedMs = 0;
        peer.LastIceRestartRequestMs = 0;
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

    /// Marks one failed direct peer for a mixed direct-plus-relay retry. If credentials are already
    /// available the peer restarts immediately; otherwise the backend fetches them and calls
    /// EscalatePeer when ready. Automatic recovery never forces relay-only policy.
    public void RequestRelayForPeer(int clientId, long nowMs = 0)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("relay-request", clientId, null, "peer-unknown");
            return;
        }
        if (peer.Incompatible || !peer.HelloReceived)
        {
            LogReject("relay-request", clientId, peer, peer.Incompatible ? "protocol-incompatible" : "hello-not-received");
            return;
        }
        peer.RelayRequested = true;
        var relayAvailable = RelayAvailable();
        LogEvent("relay-request", clientId, peer, $"nowMs={nowMs} credentialsAvailable={Bool(relayAvailable)}");
        if (relayAvailable)
            RestartWithMixedIce(clientId, peer, nowMs, "automatic-relay-ready");
        else
            _requestRelay(clientId);
    }

    public bool EscalatePeer(int clientId, long nowMs = 0)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("relay-escalate", clientId, null, "peer-unknown");
            return false;
        }
        if (peer.Incompatible || !peer.HelloReceived)
        {
            LogReject("relay-escalate", clientId, peer, peer.Incompatible ? "protocol-incompatible" : "hello-not-received");
            return false;
        }
        if (!peer.RelayRequested)
        {
            LogReject("relay-escalate", clientId, peer, "relay-not-requested");
            return false;
        }
        if (!RelayAvailable())
        {
            LogEvent("relay-escalate", clientId, peer, "action=request-credentials");
            _requestRelay(clientId);
            return false;
        }
        // EscalatePeer is called after the backend has installed a freshly fetched TURN snapshot.
        // Recreate the logical session here so the underlying RTC peer is guaranteed to be built
        // from that snapshot even when the credential callback arrives outside an active restart.
        peer.RelayRequested = false;
        peer.RelayCapable = true;
        peer.IceRestartInProgress = false;
        peer.IceRestartRequestPending = false;
        peer.IceRestartRequestStartedMs = 0;
        peer.LastIceRestartRequestMs = 0;
        LogEvent("relay-escalate", clientId, peer, "action=recreate-for-new-server-snapshot mixedIce=true");
        Reinitiate(clientId, peer, nowMs);
        return true;
    }

    /// Recreates every current peer after an ICE-server setting change. The product always uses
    /// automatic mixed ICE and lets candidate priority prefer direct paths over TURN.
    public void RebuildAll(long nowMs = 0)
    {
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            if (peer.Incompatible || !peer.HelloReceived) continue;
            peer.RelayRequested = false;
            peer.RelayCapable = RelayAvailable();
            LogEvent("rebuild", pair.Key, peer, $"mixedIce=true relayAvailable={Bool(peer.RelayCapable)} nowMs={nowMs}");
            Reinitiate(pair.Key, peer, nowMs);
        }
    }

    /// Restarts peers whose current ICE configuration can use ephemeral TURN credentials. Peers
    /// that have only ever used STUN remain untouched.
    public int RefreshRelayPeers(long nowMs = 0, ISet<int>? excludedClientIds = null)
    {
        var refreshed = 0;
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            if (peer.Incompatible || !peer.HelloReceived) continue;
            if (excludedClientIds?.Contains(pair.Key) == true) continue;
            if (!peer.RelayCapable) continue;
            refreshed++;
            LogEvent("relay-refresh", pair.Key, peer, $"action=recreate-for-new-server-snapshot nowMs={nowMs}");
            Reinitiate(pair.Key, peer, nowMs);
        }
        return refreshed;
    }

    /// Restarts established ICE agents after an operating-system network-address change. Protocol
    /// v5 peers keep their media peer and renegotiate in place; older compatible peers use the
    /// proven remove/recreate fallback.
    public int RestartIceAfterNetworkChange(long nowMs = 0)
    {
        var restarted = 0;
        foreach (var pair in _peers)
        {
            var peer = pair.Value;
            if (peer.Incompatible || !peer.HelloReceived || !peer.Added) continue;
            if (peer.State != PeerState.Established) continue;
            if (peer.LastReinitMs != 0 && nowMs >= peer.LastReinitMs && nowMs - peer.LastReinitMs < ReinitThrottleMs)
                continue;
            restarted++;
            LogEvent("network-change", pair.Key, peer, $"action=restart-ice nowMs={nowMs}");
            RestartPeerPreferInPlace(pair.Key, peer, nowMs, "network-address-changed");
        }
        return restarted;
    }

    private void RecoverPeer(int clientId, PeerEntry peer, long nowMs)
    {
        if (peer.Incompatible || !peer.HelloReceived)
        {
            LogReject("recover", clientId, peer, peer.Incompatible ? "protocol-incompatible" : "hello-not-received");
            return;
        }
        LogEvent("recover", clientId, peer, $"begin=true nowMs={nowMs}");
        peer.RelayRequested = true;
        if (RelayAvailable())
        {
            LogEvent("recover", clientId, peer, "action=restart-mixed-ice");
            RestartWithMixedIce(clientId, peer, nowMs, "automatic-recovery");
            return;
        }
        LogEvent("recover", clientId, peer, "action=request-relay-credentials");
        EnsureRelayCredentialsRequested(clientId, peer, nowMs, "automatic-recovery");
        LogEvent("recover", clientId, peer, "action=restart-direct-while-relay-pending");
        RestartPeerPreferInPlace(clientId, peer, nowMs, "direct-retry-while-relay-pending");
    }

    private bool EnsureRelayCredentialsRequested(int clientId, PeerEntry peer, long nowMs, string reason)
    {
        if (peer.LastRelayRequestMs != 0
            && nowMs >= peer.LastRelayRequestMs
            && nowMs - peer.LastRelayRequestMs < RelayCredentialRetryIntervalMs)
            return false;

        peer.LastRelayRequestMs = nowMs;
        LogEvent("relay-request", clientId, peer, $"action=request-credentials reason={reason} nowMs={nowMs}");
        try
        {
            _requestRelay(clientId);
            return true;
        }
        catch (Exception ex)
        {
            LogError("relay-request", clientId, peer, ex, $"reason={reason}");
            return false;
        }
    }

    private static long HandshakeTimeoutFor(PeerEntry peer)
        => peer.RelayCapable ? RelayHandshakeTimeoutMs : DirectHandshakeTimeoutMs;

    private void RestartWithMixedIce(int clientId, PeerEntry peer, long nowMs, string reason)
    {
        var currentAgentHasTurn = peer.RelayCapable;
        peer.RelayRequested = false;
        LogEvent(
            "relay-policy",
            clientId,
            peer,
            $"relayOnly=false mixedIce=true currentAgentHasTurn={Bool(currentAgentHasTurn)} reason={reason} nowMs={nowMs}");
        if (currentAgentHasTurn)
            RestartPeerPreferInPlace(clientId, peer, nowMs, reason);
        else
            Reinitiate(clientId, peer, nowMs);
    }

    private void RestartPeerPreferInPlace(int clientId, PeerEntry peer, long nowMs, string reason)
    {
        var canRestartInPlace = peer.RemoteProtocolVersion >= IceRestartProtocolVersion
                                && peer.Added
                                && !peer.PendingTransportRemove
                                && peer.State == PeerState.Established;
        if (!canRestartInPlace)
        {
            LogEvent("ice-restart", clientId, peer, $"mode=recreate reason={reason} nowMs={nowMs}");
            Reinitiate(clientId, peer, nowMs);
            return;
        }

        peer.LastReinitMs = nowMs;
        peer.DegradedSinceMs = 0;
        peer.LastRelayRequestMs = 0;
        if (LocalIsOfferer(clientId))
        {
            StartIceRestartOffer(clientId, peer, nowMs, reason);
            return;
        }

        peer.LastIceRestartRequestMs = nowMs;
        if (!peer.IceRestartRequestPending)
            peer.IceRestartRequestStartedMs = nowMs;
        peer.IceRestartRequestPending = true;
        var sent = SendSignal(
            clientId,
            peer,
            SignalMsgType.IceRestartRequest,
            ControlPayload(peer, SignalMsgType.IceRestartRequest),
            reason);
        LogEvent("ice-restart", clientId, peer, $"mode=in-place role=answerer requestSent={Bool(sent)} reason={reason}");
    }

    private void StartIceRestartOffer(int clientId, PeerEntry peer, long nowMs, string reason)
    {
        var previousNegotiationId = peer.NegotiationId;
        ClearPendingLocalSignals(peer);
        ClearPendingTransportCommands(peer);
        peer.NegotiationId = NextNegotiationId();
        peer.NegotiationShared = false;
        peer.RestartRequested = false;
        peer.IceRestartRequestPending = false;
        peer.IceRestartRequestStartedMs = 0;
        peer.LastIceRestartRequestMs = 0;
        peer.IceRestartInProgress = true;
        peer.LastProgressMs = nowMs;
        SetState(clientId, peer, PeerState.Offering, "ice-restart-local-offerer");
        LogEvent(
            "ice-restart",
            clientId,
            peer,
            $"mode=in-place role=offerer previousNegotiation={previousNegotiationId} reason={reason}");
        if (TransportRestartIce(clientId, peer, createOffer: true, reason))
        {
            peer.RelayCapable = RelayAvailable();
            return;
        }

        peer.IceRestartInProgress = false;
        LogEvent("ice-restart", clientId, peer, $"mode=recreate reason=transport-write-failed source={reason}");
        Reinitiate(clientId, peer, nowMs);
    }

    private void Reinitiate(int clientId, PeerEntry peer, long nowMs)
    {
        if (peer.Incompatible || !peer.HelloReceived)
        {
            LogReject("reinitiate", clientId, peer, peer.Incompatible ? "protocol-incompatible" : "hello-not-received");
            return;
        }
        var previousGeneration = peer.Generation;
        var previousNegotiationId = peer.NegotiationId;
        ClearPendingLocalSignals(peer);
        ClearPendingTransportCommands(peer);
        peer.IceRestartInProgress = false;
        peer.IceRestartRequestPending = false;
        peer.IceRestartRequestStartedMs = 0;
        peer.LastIceRestartRequestMs = 0;
        peer.Generation = NextGeneration();
        peer.LastReinitMs = nowMs;
        peer.DegradedSinceMs = 0;
        peer.LastRelayRequestMs = 0;
        LogEvent(
            "reinitiate",
            clientId,
            peer,
            $"previousGeneration={previousGeneration} previousNegotiation={previousNegotiationId} nowMs={nowMs}");
        if (peer.PendingTransportRemove)
        {
            peer.AfterRemove = AfterRemoveAction.Reinitiate;
            SetState(clientId, peer, PeerState.Greeted, "reinitiate-awaiting-native-remove");
            peer.LastProgressMs = 0;
            return;
        }
        if (peer.Added)
        {
            peer.Added = false;
            if (!TryRemoveNativePeer(clientId, peer, nowMs, "reinitiate"))
            {
                peer.AfterRemove = AfterRemoveAction.Reinitiate;
                SetState(clientId, peer, PeerState.Greeted, "reinitiate-awaiting-native-remove");
                peer.LastProgressMs = 0;
                return;
            }
        }
        ContinueReinitiateAfterRemoval(clientId, peer, nowMs);
    }

    private void ContinueReinitiateAfterRemoval(int clientId, PeerEntry peer, long nowMs)
    {
        if (LocalIsOfferer(clientId))
        {
            peer.NegotiationId = NextNegotiationId();
            peer.NegotiationShared = false;
            SetState(clientId, peer, PeerState.Offering, "reinitiate-local-offerer");
            peer.LastProgressMs = nowMs;
            TryAddNativePeer(clientId, peer, isOfferer: true, nowMs, "reinitiate-local-offerer");
        }
        else
        {
            peer.RestartRequested = true;
            SetState(clientId, peer, PeerState.Greeted, "reinitiate-request-remote-offer");
            peer.LastProgressMs = 0;
            SendHello(clientId, peer, nowMs, force: true, reason: "reinitiate-answerer-hello");
            SendSignal(clientId, peer, SignalMsgType.Restart, ControlPayload(peer, SignalMsgType.Restart), "reinitiate-answerer-restart");
        }
    }

    public void OnPlayerLeft(int clientId)
    {
        if (_pendingRemovals.TryGetValue(clientId, out var pendingRemoval))
        {
            pendingRemoval.RejoinRequested = false;
            LogEvent("player-left", clientId, pendingRemoval.Peer, "action=keep-native-removal-tombstone");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("player-left", clientId, null, "peer-unknown");
            return;
        }
        LogEvent("player-left", clientId, peer, "action=send-bye-and-drop");
        SendSignal(clientId, peer, SignalMsgType.Bye, ControlPayload(peer, SignalMsgType.Bye), "player-left");
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
                SendSignal(clientId, peer, SignalMsgType.Bye, ControlPayload(peer, SignalMsgType.Bye), $"reset:{reason}");
            DropPeer(clientId);
        }
        RetryPendingRemovalsForReset(reason);
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

        if (_pendingRemovals.TryGetValue(senderClientId, out var pendingRemoval))
        {
            if (type != SignalMsgType.Bye)
                pendingRemoval.RejoinRequested = true;
            LogReject(
                "signal-rx",
                senderClientId,
                pendingRemoval.Peer,
                "native-removal-pending",
                $"type={type} payloadBytes={payloadBytes} rejoinRequested={Bool(pendingRemoval.RejoinRequested)}");
            return;
        }

        _peers.TryGetValue(senderClientId, out var existingPeer);
        LogEvent("signal-rx", senderClientId, existingPeer, $"type={type} payloadBytes={payloadBytes} nowMs={nowMs}");
        if (type != SignalMsgType.Hello && existingPeer?.RebootstrapAckPending == true)
        {
            LogReject("signal-rx", senderClientId, existingPeer, "replacement-ack-pending", $"type={type}");
            return;
        }
        if (type != SignalMsgType.Hello && existingPeer?.Incompatible == true)
        {
            LogReject("signal-rx", senderClientId, existingPeer, "protocol-incompatible", $"type={type}");
            return;
        }

        switch (type)
        {
            case SignalMsgType.Hello: HandleHello(senderClientId, payload, nowMs); break;
            case SignalMsgType.Offer: HandleOffer(senderClientId, payload, nowMs); break;
            case SignalMsgType.Answer: HandleAnswer(senderClientId, payload, nowMs); break;
            case SignalMsgType.Candidate: HandleCandidate(senderClientId, payload, nowMs); break;
            case SignalMsgType.Bye: HandleBye(senderClientId, payload); break;
            case SignalMsgType.IceMode: HandleIceMode(senderClientId, payload, nowMs); break;
            case SignalMsgType.Restart: HandleRestart(senderClientId, payload, nowMs); break;
            case SignalMsgType.IceRestartRequest: HandleIceRestartRequest(senderClientId, payload, nowMs); break;
            case SignalMsgType.IceRestartOffer: HandleIceRestartOffer(senderClientId, payload, nowMs); break;
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
        if (_transport.RequiresNativeOperationAcknowledgements && !peer.NativePeerApplied)
            CompleteNativePeerAdd(clientId, peer, nowMs, "implicit-local-sdp");
        if (_transport.RequiresNativeOperationAcknowledgements
            && peer.NativeRemoteSdpRequestedAtMs > 0
            && peer.PendingTransportSdp.HasValue)
        {
            CompletePendingRemoteSdp(clientId, peer, nowMs, "implicit-local-sdp");
            ReplayPendingCandidates(clientId, peer, peer.NegotiationId, nowMs);
        }
        if (peer.Incompatible)
        {
            LogReject("local-sdp", clientId, peer, "protocol-incompatible");
            return;
        }
        if (peer.NegotiationId <= 0)
        {
            LogReject("local-sdp", clientId, peer, "negotiation-missing", $"sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        var isOffer = string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase);
        var signalType = isOffer && peer.IceRestartInProgress
            ? SignalMsgType.IceRestartOffer
            : isOffer ? SignalMsgType.Offer : SignalMsgType.Answer;
        LogEvent(
            "local-sdp",
            clientId,
            peer,
            $"accepted=true sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)} signalType={signalType}");
        var signal = new PendingLocalSignal(
            signalType,
            peer.RemoteProtocolVersion >= ScopedControlProtocolVersion
                ? SignalPayload.Sdp(_localSessionId, peer.NegotiationId, sdpType, sdp)
                : SignalPayload.Sdp(peer.NegotiationId, sdpType, sdp));
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
        if (isOffer) peer.NegotiationShared = true;
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
        if (_transport.RequiresNativeOperationAcknowledgements && !peer.NativePeerApplied)
            CompleteNativePeerAdd(clientId, peer, nowMs, "implicit-local-candidate");
        if (peer.Incompatible)
        {
            LogReject("local-candidate", clientId, peer, "protocol-incompatible");
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
            peer.RemoteProtocolVersion >= ScopedControlProtocolVersion
                ? SignalPayload.Candidate(_localSessionId, peer.NegotiationId, candidate)
                : SignalPayload.Candidate(peer.NegotiationId, candidate));
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
            if (sdp.Type is SignalMsgType.Offer or SignalMsgType.IceRestartOffer)
                peer.NegotiationShared = true;
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

    private void RetryPendingTransportCommands(int clientId, PeerEntry peer, long nowMs)
    {
        var hasCurrentCandidates = peer.PendingRemoteCandidates.Any(candidate =>
            candidate.NegotiationId == peer.NegotiationId);
        if (!peer.PendingTransportRemove
            && !peer.PendingTransportAdd
            && !peer.PendingTransportSdp.HasValue
            && !hasCurrentCandidates)
            return;
        if (peer.LastTransportCommandAttemptMs != 0
            && nowMs >= peer.LastTransportCommandAttemptMs
            && nowMs - peer.LastTransportCommandAttemptMs < FailedTransportCommandRetryIntervalMs)
            return;

        if (peer.PendingTransportRemove)
        {
            peer.LastTransportCommandAttemptMs = nowMs;
            if (!TransportRemovePeer(clientId, peer, "retry-pending-remove"))
                return;
            peer.PendingTransportRemove = false;
            var afterRemove = peer.AfterRemove;
            peer.AfterRemove = AfterRemoveAction.None;
            if (afterRemove == AfterRemoveAction.Reinitiate)
                ContinueReinitiateAfterRemoval(clientId, peer, nowMs);
            else if (afterRemove == AfterRemoveAction.Rebootstrap)
                ContinueRebootstrapAfterRemoval(clientId, peer, nowMs);
        }
        if (peer.PendingTransportAdd
            && !TryAddNativePeer(
                clientId,
                peer,
                peer.PendingTransportAddOfferer,
                nowMs,
                "retry-pending-add"))
            return;
        if (peer.PendingTransportSdp.HasValue
            && !TryWritePendingRemoteSdp(clientId, peer, nowMs, "retry-pending-sdp"))
            return;
        if (peer.Added && !peer.PendingTransportSdp.HasValue)
            ReplayPendingCandidates(clientId, peer, peer.NegotiationId, nowMs);

        if (!peer.PendingTransportRemove
            && !peer.PendingTransportAdd
            && !peer.PendingTransportSdp.HasValue
            && !peer.PendingRemoteCandidates.Any(candidate => candidate.NegotiationId == peer.NegotiationId))
            peer.LastTransportCommandAttemptMs = 0;
    }

    private bool TryAddNativePeer(
        int clientId,
        PeerEntry peer,
        bool isOfferer,
        long nowMs,
        string reason)
    {
        if (peer.PendingTransportRemove)
        {
            peer.PendingTransportAdd = true;
            peer.PendingTransportAddOfferer = isOfferer;
            LogEvent("transport-retry", clientId, peer, $"command=add-peer reason={reason} blockedBy=pending-remove");
            return false;
        }
        // Commit before invoking the transport because the in-process test transport and the native
        // adapters can synchronously emit SDP/candidate callbacks from AddPeer.
        var wasPending = peer.PendingTransportAdd;
        peer.Added = true;
        peer.NativePeerApplied = !_transport.RequiresNativeOperationAcknowledgements;
        peer.NativeRemoteDescriptionApplied = false;
        peer.NativePeerAddRequestedAtMs = 0;
        peer.NativeAckTimeoutReported = false;
        peer.PendingTransportAdd = false;
        peer.PendingTransportAddOfferer = isOfferer;
        peer.LastTransportCommandAttemptMs = nowMs;
        if (TransportAddPeer(clientId, peer, isOfferer, reason))
        {
            peer.RelayCapable = RelayAvailable();
            if (_transport.RequiresNativeOperationAcknowledgements && !peer.NativePeerApplied)
            {
                peer.NativePeerAddRequestedAtMs = Math.Max(1, nowMs);
                peer.LastProgressMs = 0;
                return false;
            }
            if (wasPending) peer.LastProgressMs = nowMs;
            return true;
        }

        peer.Added = false;
        peer.NativePeerApplied = false;
        peer.NativePeerAddRequestedAtMs = 0;
        peer.RelayCapable = false;
        peer.PendingTransportAdd = true;
        LogEvent("transport-retry", clientId, peer, $"command=add-peer reason={reason} retryMs={FailedTransportCommandRetryIntervalMs}");
        return false;
    }

    private bool TryRemoveNativePeer(
        int clientId,
        PeerEntry peer,
        long nowMs,
        string reason)
    {
        peer.LastTransportCommandAttemptMs = nowMs;
        if (TransportRemovePeer(clientId, peer, reason))
        {
            peer.NativePeerApplied = false;
            peer.NativePeerAddRequestedAtMs = 0;
            peer.NativeRemoteSdpRequestedAtMs = 0;
            peer.PendingTransportRemove = false;
            return true;
        }

        peer.PendingTransportRemove = true;
        LogEvent(
            "transport-retry",
            clientId,
            peer,
            $"command=remove-peer reason={reason} retryMs={FailedTransportCommandRetryIntervalMs}");
        return false;
    }

    private bool TryWritePendingRemoteSdp(int clientId, PeerEntry peer, long nowMs, string reason)
    {
        if (peer.PendingTransportSdp is not { } pending)
            return true;
        if (pending.NegotiationId != peer.NegotiationId)
        {
            peer.PendingTransportSdp = null;
            LogReject(
                "transport-retry",
                clientId,
                peer,
                "stale-remote-sdp",
                $"pendingNegotiation={pending.NegotiationId}");
            return false;
        }
        if (!peer.Added || !peer.NativePeerApplied)
            return false;
        if (peer.NativeRemoteSdpRequestedAtMs > 0)
            return false;

        peer.LastTransportCommandAttemptMs = nowMs;
        if (!TransportSetRemoteSdp(clientId, peer, pending.SdpType, pending.Sdp, reason))
        {
            LogEvent("transport-retry", clientId, peer, $"command=set-remote-sdp reason={reason} retryMs={FailedTransportCommandRetryIntervalMs}");
            return false;
        }
        if (_transport.RequiresNativeOperationAcknowledgements)
        {
            peer.NativeRemoteSdpRequestedAtMs = Math.Max(1, nowMs);
            peer.NativeAckTimeoutReported = false;
            peer.LastProgressMs = 0;
            return false;
        }

        CompletePendingRemoteSdp(clientId, peer, nowMs, "synchronous-transport");
        return true;
    }

    private static void ClearPendingTransportCommands(PeerEntry peer)
    {
        peer.PendingTransportAdd = false;
        peer.PendingTransportAddOfferer = false;
        peer.PendingTransportSdp = null;
        peer.NativeRemoteDescriptionApplied = false;
        peer.NativePeerAddRequestedAtMs = 0;
        peer.NativeRemoteSdpRequestedAtMs = 0;
        peer.NativeAckTimeoutReported = false;
        if (!peer.PendingTransportRemove)
            peer.LastTransportCommandAttemptMs = 0;
    }

    private void RetryPendingRebootstrapAcknowledgement(int clientId, PeerEntry peer, long nowMs)
    {
        if (!peer.RebootstrapAckPending) return;
        if (peer.LastHelloSentMs != 0
            && nowMs >= peer.LastHelloSentMs
            && nowMs - peer.LastHelloSentMs < FailedHelloAckRetryIntervalMs)
            return;

        var stateBefore = peer.State;
        var generationBefore = peer.Generation;
        var negotiationBefore = peer.NegotiationId;
        var addedBefore = peer.Added;
        peer.RemoteHelloAcknowledged = true;
        peer.LastHelloAcknowledgementMs = nowMs;
        if (!SendHello(clientId, peer, nowMs, force: true, reason: "replacement-remote-session-ack-retry"))
        {
            peer.RemoteHelloAcknowledged = false;
            peer.LastHelloAcknowledgementMs = 0;
            return;
        }
        if (!_peers.TryGetValue(clientId, out var currentPeer) || !ReferenceEquals(currentPeer, peer))
            return;
        if (peer.State != stateBefore
            || peer.Generation != generationBefore
            || peer.NegotiationId != negotiationBefore
            || peer.Added != addedBefore)
            return;

        peer.RebootstrapAckPending = false;
        RebootstrapAfterDuplicateHello(clientId, peer, nowMs);
    }

    private void HandleHello(int clientId, byte[] payload, long nowMs)
    {
        if (!SignalPayload.TryReadHello(payload, out var version, out var minCompatible, out var remoteSessionId))
        {
            LogReject("hello", clientId, FindPeer(clientId), "payload-invalid", $"payloadBytes={payload.Length}");
            return;
        }
        var knownPeer = FindPeer(clientId);
        if (version >= ScopedControlProtocolVersion
            && remoteSessionId > 0
            && knownPeer?.RetiredRemoteSessionIds.Contains(remoteSessionId) == true)
        {
            LogReject(
                "hello",
                clientId,
                knownPeer,
                "retired-session",
                $"receivedSession={remoteSessionId} currentRemoteSession={knownPeer.RemoteSessionId} protocol={version} minCompatible={minCompatible}");
            return;
        }
        if (!IsCompatible(version, minCompatible))
        {
            var incompatiblePeer = knownPeer;
            if (incompatiblePeer != null)
                MarkPeerIncompatible(clientId, incompatiblePeer, version, minCompatible, remoteSessionId, nowMs);
            LogReject(
                "hello",
                clientId,
                incompatiblePeer,
                "protocol-incompatible",
                $"protocol={version} minCompatible={minCompatible} localProtocol={ProtocolVersion} localMinCompatible={MinCompatibleVersion}");
            return;
        }
        if (version >= ScopedControlProtocolVersion && remoteSessionId <= 0)
        {
            // v4 starts with the legacy 8-byte discovery Hello so a v3 peer can still parse and
            // answer it. A v4 peer responds with the scoped 16-byte form before negotiation starts.
            var discoveryPeer = GetOrCreate(clientId);
            discoveryPeer.Incompatible = false;
            discoveryPeer.RemoteProtocolVersion = version;
            discoveryPeer.UnansweredHelloCount = 0;
            LogEvent(
                "signal-accepted",
                clientId,
                discoveryPeer,
                $"type=Hello protocol={version} minCompatible={minCompatible} session=discovery action=send-scoped-hello");
            if (!discoveryPeer.ScopedHelloSent
                || discoveryPeer.LastHelloSentMs == 0
                || nowMs < discoveryPeer.LastHelloSentMs
                || nowMs - discoveryPeer.LastHelloSentMs >= HelloResendIntervalMs)
                SendHello(clientId, discoveryPeer, nowMs, force: true, reason: "scoped-session-discovery-response");
            return;
        }

        var peer = GetOrCreate(clientId);
        if (version >= ScopedControlProtocolVersion
            && peer.RetiredRemoteSessionIds.Contains(remoteSessionId))
        {
            LogReject(
                "hello",
                clientId,
                peer,
                "retired-session",
                $"receivedSession={remoteSessionId} currentRemoteSession={peer.RemoteSessionId}");
            return;
        }
        var firstCompatibleHello = !peer.HelloReceived;
        var previousRemoteSessionId = peer.RemoteSessionId;
        var remoteSessionChanged = version >= ScopedControlProtocolVersion
                                   && previousRemoteSessionId > 0
                                   && previousRemoteSessionId != remoteSessionId;
        if (remoteSessionChanged)
            RetireRemoteSession(peer, previousRemoteSessionId);
        peer.Incompatible = false;
        peer.RemoteProtocolVersion = version;
        peer.RemoteSessionId = remoteSessionId;
        peer.UnansweredHelloCount = 0;
        peer.HelloReceived = true;
        LogEvent(
            "signal-accepted",
            clientId,
            peer,
            $"type=Hello protocol={version} minCompatible={minCompatible} remoteSession={remoteSessionId} firstCompatible={Bool(firstCompatibleHello)} sessionChanged={Bool(remoteSessionChanged)}");

        // Even if OnPlayerJoined already sent a Hello, the remote may have attached its RPC
        // subscriber later and never observed it. Acknowledge the first compatible remote Hello
        // before AddPeer can synchronously emit Offer/candidates. A bounded refresh also lets a
        // replacement manager recover when its predecessor's Bye was lost, without turning two
        // healthy peers into an unbounded Hello echo loop.
        var previousAcknowledgementSent = peer.RemoteHelloAcknowledged;
        var acknowledgementAlreadySent = previousAcknowledgementSent && !remoteSessionChanged;
        var previousAcknowledgementMs = peer.LastHelloAcknowledgementMs;
        var acknowledgementRefreshDue = remoteSessionChanged
                                        || (acknowledgementAlreadySent
                                            && nowMs >= previousAcknowledgementMs
                                            && nowMs - previousAcknowledgementMs >= HelloResendIntervalMs);
        var stateBeforeAcknowledgement = peer.State;
        var generationBeforeAcknowledgement = peer.Generation;
        var negotiationBeforeAcknowledgement = peer.NegotiationId;
        var addedBeforeAcknowledgement = peer.Added;
        var shouldRebootstrap = peer.RebootstrapAckPending
                                || remoteSessionChanged
                                || (version < ScopedControlProtocolVersion
                                    && acknowledgementRefreshDue
                                    && (peer.Added || peer.State is PeerState.Offering or PeerState.Connected or PeerState.Established));
        if (!acknowledgementAlreadySent || acknowledgementRefreshDue)
        {
            var reason = remoteSessionChanged
                ? "replacement-remote-session-ack"
                : acknowledgementRefreshDue ? "duplicate-remote-hello-ack"
                : peer.HelloSent ? "first-remote-hello-ack" : "initial-hello-response";
            // Set this before the send because a synchronous/reentrant signaling transport can
            // deliver the reciprocal Hello back into this manager before SendHello returns.
            peer.RebootstrapAckPending = shouldRebootstrap;
            peer.RemoteHelloAcknowledged = true;
            peer.LastHelloAcknowledgementMs = nowMs;
            if (!SendHello(clientId, peer, nowMs, force: true, reason: reason))
            {
                peer.RemoteHelloAcknowledged = remoteSessionChanged ? false : previousAcknowledgementSent;
                peer.LastHelloAcknowledgementMs = remoteSessionChanged ? 0 : previousAcknowledgementMs;
                LogReject(
                    "hello",
                    clientId,
                    peer,
                    "ack-send-failed",
                    $"firstCompatible={Bool(firstCompatibleHello)} refresh={Bool(acknowledgementRefreshDue)} rebootstrapPending={Bool(peer.RebootstrapAckPending)}");
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
        if (shouldRebootstrap && activeGenerationStillUnchanged)
        {
            peer.RebootstrapAckPending = false;
            RebootstrapAfterDuplicateHello(clientId, peer, nowMs);
            return;
        }
        if (!shouldRebootstrap)
            peer.RebootstrapAckPending = false;
        TryStartSession(clientId, peer, nowMs);
    }

    private void MarkPeerIncompatible(
        int clientId,
        PeerEntry peer,
        int version,
        int minCompatible,
        long remoteSessionId,
        long nowMs)
    {
        if (version >= ScopedControlProtocolVersion && remoteSessionId > 0)
        {
            if (peer.RemoteSessionId > 0 && peer.RemoteSessionId != remoteSessionId)
                RetireRemoteSession(peer, peer.RemoteSessionId);
            peer.RemoteSessionId = remoteSessionId;
        }
        var wasActive = peer.Added
                        || peer.PendingTransportAdd
                        || peer.PendingTransportRemove
                        || peer.State is PeerState.Offering or PeerState.Answering or PeerState.Connected or PeerState.Established;
        ClearPendingLocalSignals(peer);
        ClearPendingTransportCommands(peer);
        peer.PendingRemoteCandidates.Clear();
        peer.Incompatible = true;
        peer.HelloReceived = false;
        peer.RemoteHelloAcknowledged = false;
        peer.RebootstrapAckPending = false;
        peer.RemoteProtocolVersion = version;
        peer.UnansweredHelloCount = 0;
        peer.LastProgressMs = 0;
        peer.RestartRequested = false;
        peer.AfterRemove = AfterRemoveAction.None;
        if (wasActive)
            peer.Generation = NextGeneration();
        peer.Added = false;
        if (wasActive && !peer.PendingTransportRemove)
        {
            TryRemoveNativePeer(clientId, peer, nowMs, "protocol-incompatible");
        }
        SetState(clientId, peer, PeerState.Greeted, "protocol-incompatible");
        LogEvent(
            "hello-terminal",
            clientId,
            peer,
            $"protocol={version} minCompatible={minCompatible} action=quiesce-and-suppress-until-compatible-hello-or-rejoin");
    }

    private static void RetireRemoteSession(PeerEntry peer, long sessionId)
    {
        if (sessionId <= 0 || peer.RetiredRemoteSessionIds.Contains(sessionId)) return;
        if (peer.RetiredRemoteSessionIds.Count >= MaxRetiredRemoteSessions)
            peer.RetiredRemoteSessionIds.RemoveAt(0);
        peer.RetiredRemoteSessionIds.Add(sessionId);
    }

    private void RebootstrapAfterDuplicateHello(int clientId, PeerEntry peer, long nowMs)
    {
        var previousGeneration = peer.Generation;
        var previousNegotiationId = peer.NegotiationId;
        ClearPendingLocalSignals(peer);
        ClearPendingTransportCommands(peer);
        peer.PendingRemoteCandidates.Clear();
        peer.Generation = NextGeneration();
        peer.LastReinitMs = nowMs;
        peer.DegradedSinceMs = 0;
        peer.RestartRequested = false;
        LogEvent(
            "recover",
            clientId,
            peer,
            $"action=rebootstrap-replacement-manager previousGeneration={previousGeneration} previousNegotiation={previousNegotiationId} nowMs={nowMs}");
        if (peer.PendingTransportRemove)
        {
            peer.AfterRemove = AfterRemoveAction.Rebootstrap;
            SetState(clientId, peer, PeerState.Greeted, "replacement-manager-awaiting-native-remove");
            peer.LastProgressMs = 0;
            return;
        }
        if (peer.Added)
        {
            peer.Added = false;
            if (!TryRemoveNativePeer(clientId, peer, nowMs, "replacement-manager-hello"))
            {
                peer.AfterRemove = AfterRemoveAction.Rebootstrap;
                SetState(clientId, peer, PeerState.Greeted, "replacement-manager-awaiting-native-remove");
                peer.LastProgressMs = 0;
                return;
            }
        }
        ContinueRebootstrapAfterRemoval(clientId, peer, nowMs);
    }

    private void ContinueRebootstrapAfterRemoval(int clientId, PeerEntry peer, long nowMs)
    {
        if (LocalIsOfferer(clientId))
        {
            peer.NegotiationId = NextNegotiationId();
            peer.NegotiationShared = false;
            SetState(clientId, peer, PeerState.Offering, "replacement-manager-hello-local-offerer");
            peer.LastProgressMs = nowMs;
            TryAddNativePeer(clientId, peer, isOfferer: true, nowMs, "replacement-manager-hello");
        }
        else
        {
            // A replacement process can restart its monotonic counter. Once its fresh Hello has
            // been acknowledged, forget the predecessor's negotiation id so the first new Offer
            // cannot be mistaken for a delayed/stale one.
            peer.NegotiationId = 0;
            peer.NegotiationShared = false;
            SetState(clientId, peer, PeerState.Answering, "replacement-manager-hello-wait-for-offer");
            peer.LastProgressMs = nowMs;
        }

    }

    private bool TryReadRemoteSdp(
        int clientId,
        PeerEntry peer,
        string operation,
        byte[] payload,
        out long negotiationId,
        out string sdpType,
        out string sdp)
    {
        negotiationId = 0;
        sdpType = string.Empty;
        sdp = string.Empty;
        if (peer.RemoteProtocolVersion >= ScopedControlProtocolVersion)
        {
            if (!SignalPayload.TryReadScopedSdp(
                    payload,
                    out var sessionId,
                    out negotiationId,
                    out sdpType,
                    out sdp))
            {
                LogReject(operation, clientId, peer, "payload-invalid", $"payloadBytes={payload.Length} scoped=true");
                return false;
            }
            if (sessionId != peer.RemoteSessionId)
            {
                LogReject(
                    operation,
                    clientId,
                    peer,
                    "session-mismatch",
                    $"receivedSession={sessionId} currentRemoteSession={peer.RemoteSessionId} negotiation={negotiationId}");
                return false;
            }
            return true;
        }

        if (SignalPayload.TryReadLegacySdp(payload, out negotiationId, out sdpType, out sdp))
            return true;
        LogReject(operation, clientId, peer, "payload-invalid", $"payloadBytes={payload.Length} scoped=false");
        return false;
    }

    private bool TryReadRemoteCandidate(
        int clientId,
        PeerEntry peer,
        byte[] payload,
        out long negotiationId,
        out string candidate)
    {
        negotiationId = 0;
        candidate = string.Empty;
        if (peer.RemoteProtocolVersion >= ScopedControlProtocolVersion)
        {
            if (!SignalPayload.TryReadScopedCandidate(
                    payload,
                    out var sessionId,
                    out negotiationId,
                    out candidate))
            {
                LogReject("candidate", clientId, peer, "payload-invalid", $"payloadBytes={payload.Length} scoped=true");
                return false;
            }
            if (sessionId != peer.RemoteSessionId)
            {
                LogReject(
                    "candidate",
                    clientId,
                    peer,
                    "session-mismatch",
                    $"receivedSession={sessionId} currentRemoteSession={peer.RemoteSessionId} negotiation={negotiationId}");
                return false;
            }
            return true;
        }

        if (SignalPayload.TryReadLegacyCandidate(payload, out negotiationId, out candidate))
            return true;
        LogReject("candidate", clientId, peer, "payload-invalid", $"payloadBytes={payload.Length} scoped=false");
        return false;
    }

    private void HandleOffer(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
        if (LocalIsOfferer(clientId))
        {
            LogReject("offer", clientId, knownPeer, "local-role-is-offerer", $"payloadBytes={payload.Length}");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("offer", clientId, null, "peer-unknown", $"payloadBytes={payload.Length}");
            return;
        }
        if (!peer.HelloReceived)
        {
            LogReject("offer", clientId, peer, "hello-not-received", $"payloadBytes={payload.Length}");
            return;
        }
        if (!TryReadRemoteSdp(clientId, peer, "offer", payload, out var negotiationId, out var sdpType, out var sdp))
            return;
        if (negotiationId <= 0)
        {
            LogReject("offer", clientId, peer, "negotiation-invalid", $"negotiation={negotiationId} sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
            return;
        }
        if (!string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase))
        {
            LogReject("offer", clientId, peer, "sdp-type-mismatch", $"negotiation={negotiationId} sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
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

        ClearPendingLocalSignals(peer);
        ClearPendingTransportCommands(peer);
        peer.AfterRemove = AfterRemoveAction.None;
        if (peer.Added)
        {
            peer.Generation = NextGeneration();
            peer.Added = false;
            TryRemoveNativePeer(clientId, peer, nowMs, "newer-remote-offer");
        }
        peer.NegotiationId = negotiationId;
        peer.NegotiationShared = true;
        peer.RestartRequested = false;
        SetState(clientId, peer, PeerState.Answering, "remote-offer-accepted");
        peer.LastProgressMs = nowMs;
        peer.PendingTransportSdp = new PendingRemoteSdp(negotiationId, sdpType, sdp);
        LogEvent("signal-accepted", clientId, peer, $"type=Offer sdpBytes={Utf8Length(sdp)} nowMs={nowMs}");
        if (peer.PendingTransportRemove)
        {
            peer.PendingTransportAdd = true;
            peer.PendingTransportAddOfferer = false;
            LogEvent("transport-retry", clientId, peer, "command=add-peer reason=remote-offer blockedBy=pending-remove");
            return;
        }
        if (TryAddNativePeer(clientId, peer, isOfferer: false, nowMs, "remote-offer")
            && TryWritePendingRemoteSdp(clientId, peer, nowMs, "remote-offer"))
            ReplayPendingCandidates(clientId, peer, negotiationId, nowMs);
    }

    private void HandleAnswer(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
        if (!LocalIsOfferer(clientId))
        {
            LogReject("answer", clientId, knownPeer, "local-role-is-answerer", $"payloadBytes={payload.Length}");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("answer", clientId, null, "peer-unknown", $"payloadBytes={payload.Length}");
            return;
        }
        if (!peer.HelloReceived)
        {
            LogReject("answer", clientId, peer, "hello-not-received", $"payloadBytes={payload.Length}");
            return;
        }
        if (!TryReadRemoteSdp(clientId, peer, "answer", payload, out var negotiationId, out var sdpType, out var sdp))
            return;
        if (!string.Equals(sdpType, "answer", StringComparison.OrdinalIgnoreCase))
        {
            LogReject("answer", clientId, peer, "sdp-type-mismatch", $"negotiation={negotiationId} sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)}");
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
        peer.PendingTransportSdp = new PendingRemoteSdp(negotiationId, sdpType, sdp);
        if (TryWritePendingRemoteSdp(clientId, peer, nowMs, "remote-answer"))
            ReplayPendingCandidates(clientId, peer, negotiationId, nowMs);
        LogEvent("signal-accepted", clientId, peer, $"type=Answer sdpBytes={Utf8Length(sdp)} nowMs={nowMs}");
    }

    private void HandleCandidate(int clientId, byte[] payload, long nowMs)
    {
        _remoteCandidatesReceived++;
        var knownPeer = FindPeer(clientId);
        if (knownPeer != null)
            knownPeer.RemoteCandidatesReceived++;
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            CountRejectedCandidate(null);
            LogReject(
                "candidate",
                clientId,
                null,
                "peer-unknown",
                $"payloadBytes={payload.Length} totalCandidateRx={_remoteCandidatesReceived}");
            return;
        }
        if (!peer.HelloReceived)
        {
            CountRejectedCandidate(peer);
            LogReject("candidate", clientId, peer, "hello-not-received", $"payloadBytes={payload.Length}");
            return;
        }
        if (!TryReadRemoteCandidate(clientId, peer, payload, out var negotiationId, out var candidate))
        {
            CountRejectedCandidate(peer);
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
            if (peer.PendingTransportAdd)
            {
                QueuePendingCandidate(clientId, peer, negotiationId, candidate, nowMs);
                return;
            }
            CountRejectedCandidate(peer);
            LogReject("candidate", clientId, peer, "native-peer-not-added", $"receivedNegotiation={negotiationId} candidateBytes={Utf8Length(candidate)}");
            return;
        }
        if (!peer.NativePeerApplied
            || !peer.NativeRemoteDescriptionApplied
            || peer.PendingTransportSdp.HasValue)
        {
            QueuePendingCandidate(clientId, peer, negotiationId, candidate, nowMs);
            return;
        }
        if (peer.PendingRemoteCandidates.Any(pending => pending.NegotiationId == negotiationId))
        {
            QueuePendingCandidate(clientId, peer, negotiationId, candidate, nowMs);
            return;
        }
        if (!ForwardRemoteCandidate(clientId, peer, candidate, "live", nowMs))
            QueuePendingCandidate(clientId, peer, negotiationId, candidate, nowMs);
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

            if (!peer.Added
                || !peer.NativePeerApplied
                || !peer.NativeRemoteDescriptionApplied
                || peer.PendingTransportSdp.HasValue)
                break;
            if (!ForwardRemoteCandidate(clientId, peer, pending.Candidate, "buffered-after-offer", nowMs))
                break;
            peer.PendingRemoteCandidates.RemoveAt(index);
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

    private bool ForwardRemoteCandidate(int clientId, PeerEntry peer, string candidate, string source, long nowMs)
    {
        peer.LastTransportCommandAttemptMs = nowMs;
        if (!TransportAddIceCandidate(clientId, peer, candidate))
            return false;
        peer.RemoteCandidatesForwarded++;
        _remoteCandidatesForwarded++;
        LogEvent(
            "signal-accepted",
            clientId,
            peer,
            $"type=Candidate source={source} candidateBytes={Utf8Length(candidate)} peerCandidateRx={peer.RemoteCandidatesReceived} peerCandidateForwarded={peer.RemoteCandidatesForwarded} totalCandidateRx={_remoteCandidatesReceived} totalCandidateForwarded={_remoteCandidatesForwarded}");
        return true;
    }

    private void HandleBye(int clientId, byte[] payload)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("bye", clientId, null, "peer-unknown", $"payloadBytes={payload.Length}");
            return;
        }
        if (!TryValidateControlPayload(clientId, peer, SignalMsgType.Bye, payload, requireActiveNegotiation: false))
            return;

        LogEvent("signal-accepted", clientId, peer, $"type=Bye payloadBytes={payload.Length}");
        DropPeer(clientId);
    }

    private void HandleRestart(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
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
        if (!TryValidateControlPayload(clientId, peer, SignalMsgType.Restart, payload, requireActiveNegotiation: true))
            return;
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

    private void HandleIceRestartRequest(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
        if (!LocalIsOfferer(clientId))
        {
            LogReject("ice-restart-request", clientId, knownPeer, "local-role-is-answerer");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("ice-restart-request", clientId, null, "peer-unknown");
            return;
        }
        if (peer.RemoteProtocolVersion < IceRestartProtocolVersion)
        {
            LogReject("ice-restart-request", clientId, peer, "protocol-too-old");
            return;
        }
        if (!TryValidateControlPayload(
                clientId,
                peer,
                SignalMsgType.IceRestartRequest,
                payload,
                requireActiveNegotiation: true))
            return;
        if (!peer.HelloReceived || !peer.Added || peer.State != PeerState.Established)
        {
            LogReject(
                "ice-restart-request",
                clientId,
                peer,
                !peer.HelloReceived ? "hello-not-received" : !peer.Added ? "native-peer-not-added" : "peer-not-established");
            return;
        }
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs)
        {
            LogReject("ice-restart-request", clientId, peer, "restart-throttled", $"nowMs={nowMs} lastRestartMs={peer.LastReinitMs}");
            return;
        }

        LogEvent("signal-accepted", clientId, peer, $"type=IceRestartRequest nowMs={nowMs}");
        RestartPeerPreferInPlace(clientId, peer, nowMs, "remote-ice-restart-request");
    }

    private void HandleIceRestartOffer(int clientId, byte[] payload, long nowMs)
    {
        var knownPeer = FindPeer(clientId);
        if (LocalIsOfferer(clientId))
        {
            LogReject("ice-restart-offer", clientId, knownPeer, "local-role-is-offerer", $"payloadBytes={payload.Length}");
            return;
        }
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("ice-restart-offer", clientId, null, "peer-unknown", $"payloadBytes={payload.Length}");
            return;
        }
        if (peer.RemoteProtocolVersion < IceRestartProtocolVersion || !peer.HelloReceived)
        {
            LogReject(
                "ice-restart-offer",
                clientId,
                peer,
                peer.RemoteProtocolVersion < IceRestartProtocolVersion ? "protocol-too-old" : "hello-not-received");
            return;
        }
        if (!TryReadRemoteSdp(
                clientId,
                peer,
                "ice-restart-offer",
                payload,
                out var negotiationId,
                out var sdpType,
                out var sdp))
            return;
        if (negotiationId <= peer.NegotiationId
            || !string.Equals(sdpType, "offer", StringComparison.OrdinalIgnoreCase))
        {
            LogReject(
                "ice-restart-offer",
                clientId,
                peer,
                negotiationId <= peer.NegotiationId ? "duplicate-or-stale-negotiation" : "sdp-type-mismatch",
                $"receivedNegotiation={negotiationId} sdpType={LogSafe(sdpType)}");
            return;
        }
        if (!peer.Added || peer.PendingTransportRemove || peer.State != PeerState.Established)
        {
            LogReject(
                "ice-restart-offer",
                clientId,
                peer,
                !peer.Added ? "native-peer-not-added" : peer.PendingTransportRemove ? "native-remove-pending" : "peer-not-established");
            return;
        }

        ClearPendingLocalSignals(peer);
        ClearPendingTransportCommands(peer);
        var keptNativePeer = TransportRestartIce(
            clientId,
            peer,
            createOffer: false,
            reason: "remote-ice-restart-offer");
        if (!keptNativePeer)
        {
            peer.Generation = NextGeneration();
            peer.Added = false;
            TryRemoveNativePeer(clientId, peer, nowMs, "ice-restart-configure-fallback");
        }

        peer.NegotiationId = negotiationId;
        peer.NegotiationShared = true;
        peer.RestartRequested = false;
        peer.IceRestartRequestPending = false;
        peer.IceRestartRequestStartedMs = 0;
        peer.LastIceRestartRequestMs = 0;
        peer.IceRestartInProgress = keptNativePeer;
        peer.LastReinitMs = nowMs;
        peer.LastProgressMs = nowMs;
        peer.RelayCapable = keptNativePeer && RelayAvailable();
        peer.PendingTransportSdp = new PendingRemoteSdp(negotiationId, sdpType, sdp);
        SetState(clientId, peer, PeerState.Answering, "remote-ice-restart-offer");
        LogEvent(
            "signal-accepted",
            clientId,
            peer,
            $"type=IceRestartOffer inPlace={Bool(keptNativePeer)} sdpBytes={Utf8Length(sdp)} nowMs={nowMs}");

        if (!keptNativePeer)
        {
            if (peer.PendingTransportRemove)
            {
                peer.PendingTransportAdd = true;
                peer.PendingTransportAddOfferer = false;
                return;
            }
            if (!TryAddNativePeer(clientId, peer, isOfferer: false, nowMs, "ice-restart-fallback"))
                return;
        }
        if (TryWritePendingRemoteSdp(clientId, peer, nowMs, "remote-ice-restart-offer"))
            ReplayPendingCandidates(clientId, peer, negotiationId, nowMs);
    }

    private byte[] ControlPayload(PeerEntry peer, SignalMsgType type)
        => peer.RemoteProtocolVersion >= ScopedControlProtocolVersion
            ? SignalPayload.Control(
                _localSessionId,
                type == SignalMsgType.Bye && !peer.NegotiationShared ? 0 : peer.NegotiationId)
            : Array.Empty<byte>();

    private bool TryValidateControlPayload(
        int clientId,
        PeerEntry peer,
        SignalMsgType type,
        byte[] payload,
        bool requireActiveNegotiation)
    {
        if (peer.RemoteProtocolVersion < ScopedControlProtocolVersion)
        {
            if (payload.Length == 0)
                return true;
            LogReject(
                "control",
                clientId,
                peer,
                "legacy-payload-invalid",
                $"type={type} payloadBytes={payload.Length}");
            return false;
        }

        if (!SignalPayload.TryReadControl(payload, out var sessionId, out var negotiationId))
        {
            LogReject(
                "control",
                clientId,
                peer,
                "payload-invalid",
                $"type={type} payloadBytes={payload.Length}");
            return false;
        }
        return ValidateControlScope(
            clientId,
            peer,
            type,
            sessionId,
            negotiationId,
            requireActiveNegotiation);
    }

    private bool ValidateControlScope(
        int clientId,
        PeerEntry peer,
        SignalMsgType type,
        long sessionId,
        long negotiationId,
        bool requireActiveNegotiation)
    {
        if (sessionId != peer.RemoteSessionId)
        {
            LogReject(
                "control",
                clientId,
                peer,
                "session-mismatch",
                $"type={type} receivedSession={sessionId} currentRemoteSession={peer.RemoteSessionId}");
            return false;
        }
        var negotiationMatches = requireActiveNegotiation
            ? negotiationId > 0 && negotiationId == peer.NegotiationId
            : peer.NegotiationShared
                ? negotiationId > 0 && negotiationId == peer.NegotiationId
                : negotiationId == 0;
        if (!negotiationMatches)
        {
            LogReject(
                "control",
                clientId,
                peer,
                "negotiation-mismatch",
                $"type={type} receivedNegotiation={negotiationId}");
            return false;
        }
        return true;
    }

    private void HandleIceMode(int clientId, byte[] payload, long nowMs)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("ice-mode", clientId, null, "peer-unknown", $"payloadBytes={payload.Length}");
            return;
        }
        bool requestedRelay;
        if (peer.RemoteProtocolVersion >= ScopedControlProtocolVersion)
        {
            if (!SignalPayload.TryReadIceMode(
                    payload,
                    out var sessionId,
                    out var negotiationId,
                    out requestedRelay))
            {
                LogReject("ice-mode", clientId, peer, "payload-invalid", $"payloadBytes={payload.Length}");
                return;
            }
            if (!ValidateControlScope(
                    clientId,
                    peer,
                    SignalMsgType.IceMode,
                    sessionId,
                    negotiationId,
                    requireActiveNegotiation: false))
                return;
        }
        else if (!SignalPayload.TryReadIceMode(payload, out requestedRelay))
        {
            LogReject("ice-mode", clientId, peer, "payload-invalid", $"payloadBytes={payload.Length}");
            return;
        }
        if (!peer.HelloReceived)
        {
            LogReject("ice-mode", clientId, peer, "hello-not-received", $"requestedRelay={Bool(requestedRelay)}");
            return;
        }
        if (requestedRelay && !RelayAvailable())
        {
            // Protocol v3/v4 peers may still ask for relay-only mode. New clients keep the automatic
            // mixed policy, fetch TURN credentials, and leave any healthy direct generation alive.
            peer.RelayRequested = true;
            LogEvent("signal-accepted", clientId, peer, "type=IceMode legacy=true policy=mixed action=request-credentials");
            _requestRelay(clientId);
            return;
        }

        peer.RelayRequested = false;

        LogEvent(
            "signal-accepted",
            clientId,
            peer,
            $"type=IceMode legacy=true requestedRelay={Bool(requestedRelay)} policy=mixed nowMs={nowMs}");
        if (peer.State is not (PeerState.Connected or PeerState.Established))
        {
            LogReject("ice-mode", clientId, peer, "duplicate-during-negotiation", $"requestedRelay={Bool(requestedRelay)}");
            return;
        }
        if (peer.LastReinitMs != 0 && nowMs - peer.LastReinitMs < ReinitThrottleMs)
        {
            LogReject("ice-mode", clientId, peer, "reinit-throttled", $"nowMs={nowMs} lastReinitMs={peer.LastReinitMs}");
            return;
        }
        RestartPeerPreferInPlace(clientId, peer, nowMs, "legacy-ice-mode-request");
    }

    private void TryStartSession(int clientId, PeerEntry peer, long nowMs)
    {
        if (peer.Incompatible)
        {
            LogReject("start-session", clientId, peer, "protocol-incompatible");
            return;
        }
        if (!peer.HelloReceived)
        {
            LogReject("start-session", clientId, peer, "hello-not-received");
            return;
        }
        if (peer.PendingTransportRemove)
        {
            peer.AfterRemove = AfterRemoveAction.Rebootstrap;
            LogReject("start-session", clientId, peer, "native-peer-remove-pending");
            return;
        }
        if (peer.State is PeerState.Connected or PeerState.Established)
        {
            LogReject("start-session", clientId, peer, "already-connected");
            return;
        }

        if (LocalIsOfferer(clientId))
        {
            if (peer.Added || peer.PendingTransportAdd)
            {
                LogReject(
                    "start-session",
                    clientId,
                    peer,
                    peer.Added ? "native-peer-already-added" : "native-peer-add-pending");
                return;
            }
            peer.NegotiationId = NextNegotiationId();
            peer.NegotiationShared = false;
            SetState(clientId, peer, PeerState.Offering, "compatible-hello-local-offerer");
            peer.LastProgressMs = nowMs;
            TryAddNativePeer(clientId, peer, isOfferer: true, nowMs, "start-session");
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
        var awaitingResponse = !peer.HelloReceived;
        var scopedHello = peer.RemoteProtocolVersion >= ScopedControlProtocolVersion;
        var previouslySentScopedHello = peer.ScopedHelloSent;
        // Set before invoking the sender because tests and local transports can synchronously feed
        // the remote response back into this manager. Revert only a first-send failure.
        peer.HelloSent = true;
        if (scopedHello) peer.ScopedHelloSent = true;
        peer.LastHelloSentMs = nowMs;
        if (peer.State == PeerState.Idle) SetState(clientId, peer, PeerState.Greeted, "send-initial-hello");
        var sent = SendSignal(
            clientId,
            peer,
            SignalMsgType.Hello,
            scopedHello
                ? SignalPayload.Hello(ProtocolVersion, MinCompatibleVersion, _localSessionId)
                : SignalPayload.Hello(ProtocolVersion, MinCompatibleVersion),
            reason);
        if (!sent && !previouslySent)
            peer.HelloSent = false;
        if (!sent && scopedHello && !previouslySentScopedHello)
            peer.ScopedHelloSent = false;
        if (sent && awaitingResponse && !peer.HelloReceived)
            peer.UnansweredHelloCount = Math.Min(peer.UnansweredHelloCount + 1, 30);
        return sent;
    }

    private static long HelloRetryIntervalMs(int unansweredHelloCount)
    {
        if (unansweredHelloCount <= 3)
            return HelloResendIntervalMs;
        var shift = Math.Min(unansweredHelloCount - 3, 5);
        return Math.Min(MaxHelloBackoffMs, HelloResendIntervalMs * (1L << shift));
    }

    private void DropPeer(int clientId)
    {
        if (!_peers.TryGetValue(clientId, out var peer))
        {
            LogReject("drop-peer", clientId, null, "peer-unknown");
            return;
        }

        LogEvent("drop-peer", clientId, peer, "begin=true");
        var removed = !peer.PendingTransportRemove
                      && TransportRemovePeer(clientId, peer, "drop-peer");
        if (!removed)
        {
            _pendingRemovals[clientId] = new PendingRemoval(peer, lastAttemptMs: 0);
            LogEvent("transport-retry", clientId, peer, $"command=remove-peer reason=drop-peer tombstone=true retryMs={FailedTransportCommandRetryIntervalMs}");
        }
        _peers.Remove(clientId);
        VoiceDiagnostics.Log("signaling.session.peer-removed", $"local={_localClientId} peer={clientId} remainingPeers={_peers.Count}");
    }

    private void RetryPendingRemovals(long nowMs)
    {
        if (_pendingRemovals.Count == 0) return;
        var completed = new List<(int ClientId, bool Rejoin)>();
        foreach (var pair in _pendingRemovals)
        {
            var pending = pair.Value;
            if (pending.LastAttemptMs != 0
                && nowMs >= pending.LastAttemptMs
                && nowMs - pending.LastAttemptMs < FailedTransportCommandRetryIntervalMs)
                continue;
            pending.LastAttemptMs = nowMs;
            if (!TransportRemovePeer(pair.Key, pending.Peer, "retry-drop-peer"))
                continue;
            completed.Add((pair.Key, pending.RejoinRequested));
        }
        foreach (var item in completed)
        {
            _pendingRemovals.Remove(item.ClientId);
            if (item.Rejoin)
                OnPlayerJoined(item.ClientId, nowMs);
        }
    }

    private void RetryPendingRemovalsForReset(string reason)
    {
        foreach (var pending in _pendingRemovals.Values)
            pending.RejoinRequested = false;

        // Reset is used immediately before a local-id manager rollover and during backend teardown,
        // where no later Tick may ever reach this manager. Give transient IPC write failures a
        // small synchronous retry budget. If every write still fails, the Sidecar lease continues
        // tracking the peer and will retry during quiesce; a later AddPeer also replaces the native
        // peer identity rather than retaining two generations.
        for (var attempt = 1;
             attempt <= SynchronousResetRemovalRetryAttempts && _pendingRemovals.Count > 0;
             attempt++)
        {
            foreach (var clientId in _pendingRemovals.Keys.ToList())
            {
                var pending = _pendingRemovals[clientId];
                if (!TransportRemovePeer(clientId, pending.Peer, $"reset-retry-{attempt}:{reason}"))
                    continue;
                _pendingRemovals.Remove(clientId);
            }
        }

        if (_pendingRemovals.Count > 0)
        {
            VoiceDiagnostics.Log(
                "signaling.session.reset-remove-pending",
                $"local={_localClientId} peers={_pendingRemovals.Count} retries={SynchronousResetRemovalRetryAttempts} reason={LogSafe(reason)} fallback=transport-replacement-or-host-quiesce");
        }
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
            peer = new PeerEntry { Generation = NextGeneration() };
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

    private bool TransportAddPeer(int clientId, PeerEntry peer, bool isOfferer, string reason)
    {
        LogEvent(
            "transport-command",
            clientId,
            peer,
            $"command=add-peer isOfferer={Bool(isOfferer)} policy=automatic-mixed reason={reason}");
        try
        {
            var written = _transport.AddPeer(clientId, isOfferer, peer.Generation);
            LogEvent("transport-command-returned", clientId, peer, $"command=add-peer reason={reason} written={Bool(written)} nativeAck=false");
            if (!written)
                LogReject("transport-command", clientId, peer, "write-failed", $"command=add-peer reason={reason}");
            return written;
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, $"command=add-peer reason={reason}");
            return false;
        }
    }

    private static long CreateSessionNonce()
    {
        using var random = RandomNumberGenerator.Create();
        var bytes = new byte[sizeof(long)];
        while (true)
        {
            random.GetBytes(bytes);
            var nonce = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            if (nonce != 0) return nonce;
        }
    }

    private bool TransportRestartIce(int clientId, PeerEntry peer, bool createOffer, string reason)
    {
        LogEvent(
            "transport-command",
            clientId,
            peer,
            $"command=restart-ice createOffer={Bool(createOffer)} mixedIce=true reason={reason}");
        try
        {
            var written = _transport.RestartIce(clientId, peer.Generation, createOffer);
            LogEvent(
                "transport-command-returned",
                clientId,
                peer,
                $"command=restart-ice createOffer={Bool(createOffer)} reason={reason} written={Bool(written)} nativeAck=false");
            if (!written)
                LogReject("transport-command", clientId, peer, "write-failed", $"command=restart-ice reason={reason}");
            return written;
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, $"command=restart-ice reason={reason}");
            return false;
        }
    }

    private bool TransportRemovePeer(int clientId, PeerEntry peer, string reason)
    {
        LogEvent("transport-command", clientId, peer, $"command=remove-peer reason={reason}");
        try
        {
            var written = _transport.RemovePeer(clientId, peer.Generation);
            LogEvent("transport-command-returned", clientId, peer, $"command=remove-peer reason={reason} written={Bool(written)} nativeAck=false");
            if (!written)
                LogReject("transport-command", clientId, peer, "write-failed", $"command=remove-peer reason={reason}");
            return written;
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, $"command=remove-peer reason={reason}");
            return false;
        }
    }

    private bool TransportSetRemoteSdp(int clientId, PeerEntry peer, string sdpType, string sdp, string reason)
    {
        LogEvent(
            "transport-command",
            clientId,
            peer,
            $"command=set-remote-sdp sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)} reason={reason}");
        try
        {
            var written = _transport.SetRemoteSdp(clientId, peer.Generation, sdpType, sdp);
            LogEvent(
                "transport-command-returned",
                clientId,
                peer,
                $"command=set-remote-sdp sdpType={LogSafe(sdpType)} sdpBytes={Utf8Length(sdp)} reason={reason} written={Bool(written)} nativeAck=false");
            if (!written)
                LogReject("transport-command", clientId, peer, "write-failed", $"command=set-remote-sdp sdpType={LogSafe(sdpType)} reason={reason}");
            return written;
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, $"command=set-remote-sdp sdpType={LogSafe(sdpType)} reason={reason}");
            return false;
        }
    }

    private bool TransportAddIceCandidate(int clientId, PeerEntry peer, string candidate)
    {
        LogEvent(
            "transport-command",
            clientId,
            peer,
            $"command=add-ice-candidate candidateBytes={Utf8Length(candidate)} peerCandidateRx={peer.RemoteCandidatesReceived}");
        try
        {
            var written = _transport.AddIceCandidate(clientId, peer.Generation, candidate);
            LogEvent(
                "transport-command-returned",
                clientId,
                peer,
                $"command=add-ice-candidate candidateBytes={Utf8Length(candidate)} written={Bool(written)} nativeAck=false");
            if (!written)
                LogReject("transport-command", clientId, peer, "write-failed", $"command=add-ice-candidate candidateBytes={Utf8Length(candidate)}");
            return written;
        }
        catch (Exception ex)
        {
            LogError("transport-command", clientId, peer, ex, "command=add-ice-candidate");
            return false;
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
        return $"local={_localClientId} peer={clientId} state={peer.State} generation={peer.Generation} negotiation={peer.NegotiationId} policy=automatic-mixed turnCapable={Bool(peer.RelayCapable)} added={Bool(peer.Added)} helloSent={Bool(peer.HelloSent)} helloReceived={Bool(peer.HelloReceived)} restartRequested={Bool(peer.RestartRequested)}";
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
