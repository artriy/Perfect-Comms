using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal enum SignalMsgType : byte
{
    Hello = 0,
    Offer = 1,
    Answer = 2,
    Candidate = 3,
    Bye = 4,
    IceMode = 5,
    Restart = 6,
}

internal readonly record struct SignalingSubscription(long Id)
{
    public bool IsValid => Id > 0;
}

internal static class AmongUsRpcSignaling
{
    public const byte SignalingRpcId = 209;
    internal const int MaxDecompressedPayloadBytes = 256 * 1024;
    private static readonly object SubscriberSync = new();
    private static readonly SignalingHelloMailbox DeferredHellos = new();
    private static Action<int, SignalMsgType, byte[]>? _subscriber;
    private static long _subscriberGeneration;

    internal static SignalingSubscription RegisterSubscriber(Action<int, SignalMsgType, byte[]> subscriber)
    {
        if (subscriber == null) throw new ArgumentNullException(nameof(subscriber));
        lock (SubscriberSync)
        {
            var generation = NextSubscriberGenerationLocked();
            var replaced = _subscriber != null;
            _subscriber = subscriber;
            VoiceDiagnostics.Log(
                "signaling.rpc.subscriber",
                $"action=register generation={generation} replaced={replaced.ToString().ToLowerInvariant()} deferredHellos={DeferredHellos.Count}");
            return new SignalingSubscription(generation);
        }
    }

    internal static void UnregisterSubscriber(SignalingSubscription subscription)
    {
        if (!subscription.IsValid) return;
        lock (SubscriberSync)
        {
            if (subscription.Id != _subscriberGeneration)
            {
                VoiceDiagnostics.Log(
                    "signaling.rpc.subscriber",
                    $"action=unregister-ignored generation={subscription.Id} currentGeneration={_subscriberGeneration} reason=stale-owner");
                return;
            }

            _subscriber = null;
            VoiceDiagnostics.Log(
                "signaling.rpc.subscriber",
                $"action=unregister generation={subscription.Id} deferredHellos={DeferredHellos.Count}");
        }
    }

    internal static void ReplayDeferredHellos(SignalingSubscription subscription)
    {
        if (!subscription.IsValid || !TryGetCurrentScope(out var scope)) return;

        Action<int, SignalMsgType, byte[]>? subscriber;
        lock (SubscriberSync)
        {
            if (subscription.Id != _subscriberGeneration) return;
            subscriber = _subscriber;
        }
        if (subscriber == null) return;

        var replay = DeferredHellos.Drain(
            scope,
            IsCurrentRosterClient,
            Environment.TickCount64,
            out var expired,
            out var wrongSession,
            out var absent);
        if (replay.Count == 0 && expired == 0 && wrongSession == 0 && absent == 0) return;

        VoiceDiagnostics.Log(
            "signaling.rpc.deferred-drain",
            $"generation={subscription.Id} replay={replay.Count} expired={expired} wrongSession={wrongSession} absent={absent}");

        for (var index = 0; index < replay.Count; index++)
        {
            var hello = replay[index];
            lock (SubscriberSync)
            {
                if (subscription.Id != _subscriberGeneration || _subscriber == null)
                {
                    // Drain removed the whole batch. If ownership changed during replay, restore
                    // every not-yet-dispatched Hello while registration is blocked by this lock;
                    // restoring only the current item would silently lose the rest of the lobby.
                    var restoreNowMs = Environment.TickCount64;
                    for (var remaining = index; remaining < replay.Count; remaining++)
                    {
                        var pending = replay[remaining];
                        DeferredHellos.Store(
                            pending.Scope,
                            pending.SenderClientId,
                            pending.SenderNetId,
                            pending.Payload,
                            restoreNowMs,
                            out _);
                    }
                    return;
                }
                subscriber = _subscriber;
            }

            try
            {
                subscriber.Invoke(hello.SenderClientId, SignalMsgType.Hello, hello.Payload);
                VoiceDiagnostics.Log(
                    "signaling.rpc.deferred-replay",
                    $"sender={hello.SenderClientId} target={scope.LocalClientId} type=Hello ageMs={Math.Max(0, Environment.TickCount64 - hello.ReceivedAtMs)}");
            }
            catch (Exception ex)
            {
                // A transient backend/transport failure must not consume the only bootstrap Hello.
                // The mailbox remains bounded and session-scoped, so retrying it is safe.
                DeferredHellos.Store(
                    hello.Scope,
                    hello.SenderClientId,
                    hello.SenderNetId,
                    hello.Payload,
                    Environment.TickCount64,
                    out _);
                VoiceDiagnostics.Log(
                    "signaling.rpc.error",
                    $"stage=deferred-replay sender={hello.SenderClientId} action=requeued errorType={ex.GetType().Name} error=\"{LogSafe(ex.Message)}\"");
            }
        }
    }

    internal static bool DeferHello(int senderClientId, uint senderNetId, byte[] payload, string reason)
    {
        if (!SignalPayload.TryReadHello(payload, out var version, out var minCompatible, out _)
            || !PeerSessionManager.IsCompatible(version, minCompatible)
            || !TryGetCurrentScope(out var scope))
            return false;

        var stored = DeferredHellos.Store(
            scope,
            senderClientId,
            senderNetId,
            payload,
            Environment.TickCount64,
            out var result);
        VoiceDiagnostics.Log(
            "signaling.rpc.deferred",
            $"sender={senderClientId} target={scope.LocalClientId} type=Hello protocol={version} minCompatible={minCompatible} reason={reason} stored={stored.ToString().ToLowerInvariant()} result={result} pending={DeferredHellos.Count}");
        return stored;
    }

    internal static void ClearDeferredHellos(string reason)
    {
        var count = DeferredHellos.Count;
        DeferredHellos.Clear();
        if (count > 0)
            VoiceDiagnostics.Log("signaling.rpc.deferred-clear", $"reason={LogSafe(reason)} cleared={count}");
    }

    public static bool Send(int targetClientId, SignalMsgType type, byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        var client = AmongUsClient.Instance;
        var localPlayer = PlayerControl.LocalPlayer;
        var payloadBytes = payload.Length;
        if (targetClientId < 0)
        {
            LogReject("tx", -1, targetClientId, type, payloadBytes, 0, "invalid-target");
            return false;
        }
        if (client == null)
        {
            LogReject("tx", -1, targetClientId, type, payloadBytes, 0, "client-unavailable");
            return false;
        }
        if (localPlayer == null)
        {
            LogReject("tx", client.ClientId, targetClientId, type, payloadBytes, 0, "local-player-unavailable");
            return false;
        }

        try
        {
            var frame = Frame(type, payload);
            var writer = client.StartRpcImmediately(
                localPlayer.NetId,
                SignalingRpcId,
                SendOption.Reliable,
                targetClientId);
            writer.WriteBytesAndSize(frame);
            client.FinishRpcImmediately(writer);
            VoiceDiagnostics.Log(
                "signaling.rpc.tx",
                $"sender={client.ClientId} target={targetClientId} type={type} payloadBytes={payloadBytes} wireBytes={frame.Length} {DescribePayload(type, payload)}");
            return true;
        }
        catch (Exception ex)
        {
            // A peer can disappear between roster polling and this reliable send. Never let a
            // signaling race escape into the game's main loop.
            VoiceDiagnostics.Log(
                "signaling.rpc.send-error",
                $"sender={client.ClientId} target={targetClientId} type={type} payloadBytes={payloadBytes} stage=send errorType={ex.GetType().Name} error=\"{LogSafe(ex.Message)}\"");
            return false;
        }
    }

    public static byte[] Frame(SignalMsgType type, byte[] payload)
    {
        var body = IsCompressed(type) ? Gzip(payload ?? Array.Empty<byte>()) : (payload ?? Array.Empty<byte>());
        if (body.Length > ushort.MaxValue)
            throw new ArgumentException($"signaling payload too large: {body.Length}");

        var frame = new byte[3 + body.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)(body.Length >> 8);
        frame[2] = (byte)(body.Length & 0xFF);
        Buffer.BlockCopy(body, 0, frame, 3, body.Length);
        return frame;
    }

    public static bool TryParse(byte[] frame, out SignalMsgType type, out byte[] payload)
        => TryParse(frame, out type, out payload, out _);

    private static bool TryParse(
        byte[] frame,
        out SignalMsgType type,
        out byte[] payload,
        out string failureReason)
    {
        type = SignalMsgType.Hello;
        payload = Array.Empty<byte>();
        failureReason = string.Empty;
        if (frame == null)
        {
            failureReason = "frame-null";
            return false;
        }
        if (frame.Length < 3)
        {
            failureReason = "header-too-short";
            return false;
        }

        var rawType = frame[0];
        if (rawType > (byte)SignalMsgType.Restart)
        {
            failureReason = $"unknown-type-{rawType}";
            return false;
        }

        var length = (frame[1] << 8) | frame[2];
        if (frame.Length != 3 + length)
        {
            failureReason = $"length-mismatch-declared-{length}-actual-{frame.Length - 3}";
            return false;
        }

        var body = new byte[length];
        Buffer.BlockCopy(frame, 3, body, 0, length);

        type = (SignalMsgType)rawType;
        try
        {
            payload = IsCompressed(type) ? Gunzip(body) : body;
        }
        catch (Exception ex)
        {
            failureReason = $"decompress-{ex.GetType().Name}";
            return false;
        }

        return true;
    }

    private static bool IsCompressed(SignalMsgType type)
        => type == SignalMsgType.Offer || type == SignalMsgType.Answer;

    private static string DescribePayload(SignalMsgType type, byte[]? payload)
    {
        payload ??= Array.Empty<byte>();
        switch (type)
        {
            case SignalMsgType.Hello:
                return SignalPayload.TryReadHello(payload, out var version, out var minCompatible, out var sessionId)
                    ? $"protocol={version} minCompatible={minCompatible} session={sessionId}"
                    : "metadata=unparseable";
            case SignalMsgType.Offer:
            case SignalMsgType.Answer:
                return SignalPayload.TryReadSdp(payload, out var negotiationId, out var sdpType, out var sdp)
                    ? $"negotiation={negotiationId} sdpType={LogSafe(sdpType)} sdpBytes={Encoding.UTF8.GetByteCount(sdp)}"
                    : "metadata=unparseable";
            case SignalMsgType.Candidate:
                return SignalPayload.TryReadCandidate(payload, out var candidateNegotiationId, out var candidate)
                    ? $"negotiation={candidateNegotiationId} candidateBytes={Encoding.UTF8.GetByteCount(candidate)}"
                    : "metadata=unparseable";
            case SignalMsgType.IceMode:
                if (SignalPayload.TryReadIceMode(payload, out var iceSession, out var iceNegotiation, out var scopedRelayOnly))
                    return $"session={iceSession} negotiation={iceNegotiation} relayOnly={scopedRelayOnly.ToString().ToLowerInvariant()}";
                return SignalPayload.TryReadIceMode(payload, out var relayOnly)
                    ? $"relayOnly={relayOnly.ToString().ToLowerInvariant()} legacy=true"
                    : "metadata=unparseable";
            case SignalMsgType.Bye:
            case SignalMsgType.Restart:
                if (SignalPayload.TryReadControl(payload, out var controlSession, out var controlNegotiation))
                    return $"session={controlSession} negotiation={controlNegotiation}";
                return payload.Length == 0 ? "metadata=empty legacy=true" : "metadata=unexpected-payload";
            default:
                return "metadata=unknown-type";
        }
    }

    private static void LogReject(
        string direction,
        int senderClientId,
        int targetClientId,
        SignalMsgType type,
        int payloadBytes,
        int wireBytes,
        string reason)
        => LogReject(direction, senderClientId, targetClientId, type.ToString(), payloadBytes, wireBytes, reason);

    private static void LogReject(
        string direction,
        int senderClientId,
        int targetClientId,
        string type,
        int payloadBytes,
        int wireBytes,
        string reason)
    {
        VoiceDiagnostics.Log(
            "signaling.rpc.reject",
            $"direction={direction} sender={senderClientId} target={targetClientId} type={type} payloadBytes={payloadBytes} wireBytes={wireBytes} reason={reason}");
    }

    private static string LogSafe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "none";
        var sanitized = value.Replace('\\', '/').Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= 160 ? sanitized : sanitized.Substring(0, 160);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaxDecompressedPayloadBytes)
                throw new InvalidDataException("signaling payload exceeds decompression limit");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static long NextSubscriberGenerationLocked()
    {
        if (_subscriberGeneration == long.MaxValue) _subscriberGeneration = 0;
        return ++_subscriberGeneration;
    }

    private static bool TryGetCurrentScope(out SignalingSessionScope scope)
    {
        scope = default;
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null) return false;
            scope = new SignalingSessionScope(client.GameId, client.ClientId);
            return scope.IsValid;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurrentRosterClient(int clientId)
    {
        if (clientId < 0) return false;
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null) return false;
            foreach (var remote in client.allClients)
                if (remote != null && remote.Id == clientId)
                    return true;
        }
        catch
        {
        }
        return false;
    }

    private static void RemoveDeferredHello(int senderClientId, string reason)
    {
        if (!TryGetCurrentScope(out var scope)) return;
        if (DeferredHellos.Remove(scope, senderClientId))
            VoiceDiagnostics.Log(
                "signaling.rpc.deferred-remove",
                $"sender={senderClientId} target={scope.LocalClientId} reason={reason} pending={DeferredHellos.Count}");
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    private static class PlayerControlHandleRpcPatch
    {
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != SignalingRpcId) return;

            try
            {
                var frame = reader.ReadBytesAndSize();
                var senderClientId = -1;
                var clientInstance = AmongUsClient.Instance;
                var targetClientId = clientInstance?.ClientId ?? -1;
                if (clientInstance != null)
                {
                    var client = clientInstance.GetClientFromCharacter(__instance);
                    if (client != null) senderClientId = client.Id;
                }

                if (frame == null)
                {
                    LogReject("rx", senderClientId, targetClientId, "unknown", 0, 0, "frame-null");
                    return;
                }
                if (senderClientId < 0)
                {
                    LogReject("rx", senderClientId, targetClientId, frame.Length > 0 ? $"raw-{frame[0]}" : "unknown", 0, frame.Length, "sender-unresolved");
                }

                if (!TryParse(frame, out var type, out var payload, out var failureReason))
                {
                    LogReject("rx", senderClientId, targetClientId, frame.Length > 0 ? $"raw-{frame[0]}" : "unknown", 0, frame.Length, $"frame-parse-{failureReason}");
                    return;
                }

                VoiceDiagnostics.Log(
                    "signaling.rpc.rx",
                    $"sender={senderClientId} target={targetClientId} type={type} payloadBytes={payload.Length} wireBytes={frame.Length} {DescribePayload(type, payload)}");

                if (type == SignalMsgType.Bye)
                    RemoveDeferredHello(senderClientId, "bye-received");

                Action<int, SignalMsgType, byte[]>? handler;
                lock (SubscriberSync)
                    handler = _subscriber;
                if (handler == null)
                {
                    if (type == SignalMsgType.Hello
                        && senderClientId >= 0
                        && DeferHello(senderClientId, unchecked((uint)__instance.NetId), payload, "no-subscriber"))
                        return;
                    LogReject("rx", senderClientId, targetClientId, type, payload.Length, frame.Length, "no-subscriber");
                    return;
                }
                handler.Invoke(senderClientId, type, payload);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log(
                    "signaling.rpc.error",
                    $"stage=read-resolve-parse-dispatch errorType={ex.GetType().Name} error=\"{LogSafe(ex.Message)}\"");
            }
        }
    }
}
