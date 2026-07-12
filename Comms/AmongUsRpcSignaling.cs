using System;
using System.IO;
using System.IO.Compression;
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
        if (targetClientId < 0 || AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
        try
        {
            var frame = Frame(type, payload);
            var writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                SignalingRpcId,
                SendOption.Reliable,
                targetClientId);
            writer.WriteBytesAndSize(frame);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            // A peer can disappear between roster polling and this reliable send. Never let a
            // signaling race escape into the game's main loop.
            VoiceDiagnostics.Log(
                "signaling.rpc.send-error",
                $"target={targetClientId} type={type} error=\"{ex.Message}\"");
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
    {
        type = SignalMsgType.Hello;
        payload = Array.Empty<byte>();
        if (frame == null || frame.Length < 3) return false;

        var rawType = frame[0];
        if (rawType > (byte)SignalMsgType.Restart) return false;

        var length = (frame[1] << 8) | frame[2];
        if (frame.Length != 3 + length) return false;

        var body = new byte[length];
        Buffer.BlockCopy(frame, 3, body, 0, length);

        type = (SignalMsgType)rawType;
        try
        {
            payload = IsCompressed(type) ? Gunzip(body) : body;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static bool IsCompressed(SignalMsgType type)
        => type == SignalMsgType.Offer || type == SignalMsgType.Answer;

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
                if (frame == null) return;

                var senderClientId = -1;
                if (AmongUsClient.Instance != null)
                {
                    var client = AmongUsClient.Instance.GetClientFromCharacter(__instance);
                    if (client != null) senderClientId = client.Id;
                }

                if (TryParse(frame, out var type, out var payload))
                    OnMessage?.Invoke(senderClientId, type, payload);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("signaling.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
