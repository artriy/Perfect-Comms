using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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

internal static class AmongUsRpcSignaling
{
    public const byte SignalingRpcId = 209;
    internal const int MaxDecompressedPayloadBytes = 256 * 1024;

    public static event Action<int, SignalMsgType, byte[]>? OnMessage;

    public static void Send(int targetClientId, SignalMsgType type, byte[] payload)
    {
        payload ??= Array.Empty<byte>();
        var client = AmongUsClient.Instance;
        var localPlayer = PlayerControl.LocalPlayer;
        var payloadBytes = payload.Length;
        if (targetClientId < 0)
        {
            LogReject("tx", -1, targetClientId, type, payloadBytes, 0, "invalid-target");
            return;
        }
        if (client == null)
        {
            LogReject("tx", -1, targetClientId, type, payloadBytes, 0, "client-unavailable");
            return;
        }
        if (localPlayer == null)
        {
            LogReject("tx", client.ClientId, targetClientId, type, payloadBytes, 0, "local-player-unavailable");
            return;
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
        }
        catch (Exception ex)
        {
            // A peer can disappear between roster polling and this reliable send. Never let a
            // signaling race escape into the game's main loop.
            VoiceDiagnostics.Log(
                "signaling.rpc.send-error",
                $"sender={client.ClientId} target={targetClientId} type={type} payloadBytes={payloadBytes} stage=send errorType={ex.GetType().Name} error=\"{LogSafe(ex.Message)}\"");
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
                return SignalPayload.TryReadHello(payload, out var version, out var minCompatible)
                    ? $"protocol={version} minCompatible={minCompatible}"
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
                return SignalPayload.TryReadIceMode(payload, out var relayOnly)
                    ? $"relayOnly={relayOnly.ToString().ToLowerInvariant()}"
                    : "metadata=unparseable";
            case SignalMsgType.Bye:
            case SignalMsgType.Restart:
                return payload.Length == 0 ? "metadata=empty" : "metadata=unexpected-payload";
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

                var handler = OnMessage;
                if (handler == null)
                {
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
