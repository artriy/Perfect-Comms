using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace VoiceChatPlugin.VoiceChat;

internal static class SidecarProtocol
{
    public const byte TypeControl = 0x01;
    public const byte TypeAudio = 0x02;
    public const int AudioSamples = 960;
    public const int AudioPayloadBytes = 8 + AudioSamples * 4;
    public const int HeaderBytes = 5;
    public const int MaxPayloadBytes = 1 << 20;

    public static byte[] EncodeFrame(byte type, byte[] payload)
    {
        var frame = new byte[HeaderBytes + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        Buffer.BlockCopy(payload, 0, frame, HeaderBytes, payload.Length);
        return frame;
    }

    public static bool TryParseFrame(byte[] buffer, int available, out byte type, out int payloadOffset, out int payloadLength, out int frameLength)
    {
        type = 0;
        payloadOffset = 0;
        payloadLength = 0;
        frameLength = 0;
        if (available < HeaderBytes)
            return false;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(1, 4));
        if (len > MaxPayloadBytes)
            return false;
        int total = HeaderBytes + (int)len;
        if (available < total)
            return false;
        type = buffer[0];
        payloadOffset = HeaderBytes;
        payloadLength = (int)len;
        frameLength = total;
        return true;
    }

    public static byte[] EncodeControl(string json)
        => EncodeFrame(TypeControl, Encoding.UTF8.GetBytes(json));

    public static byte[] HelloFrame(int proto, string token)
        => EncodeControl($"{{\"op\":\"hello\",\"proto\":{proto},\"token\":{JsonString(token)}}}");

    public static byte[] SelectDeviceFrame(string id)
        => EncodeControl($"{{\"op\":\"select-device\",\"id\":{JsonString(id)}}}");

    public static byte[] StartFrame() => EncodeControl("{\"op\":\"start\"}");
    public static byte[] StopFrame() => EncodeControl("{\"op\":\"stop\"}");
    public static byte[] PingFrame() => EncodeControl("{\"op\":\"ping\"}");

    public static string ReadOp(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("op", out var op) ? (op.GetString() ?? "") : "";
        }
        catch
        {
            return "";
        }
    }

    public static bool TryReadReady(string json, out int proto, out int rate, out int channels, out string sample)
    {
        proto = 0;
        rate = 0;
        channels = 0;
        sample = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "ready")
                return false;
            proto = root.TryGetProperty("proto", out var p) ? p.GetInt32() : 0;
            if (root.TryGetProperty("format", out var fmt))
            {
                rate = fmt.TryGetProperty("rate", out var r) ? r.GetInt32() : 0;
                channels = fmt.TryGetProperty("channels", out var c) ? c.GetInt32() : 0;
                sample = fmt.TryGetProperty("sample", out var s) ? (s.GetString() ?? "") : "";
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadPong(string json, out ulong capTs)
    {
        capTs = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "pong")
                return false;
            capTs = root.TryGetProperty("capTs", out var c) ? c.GetUInt64() : 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadError(string json, out string code, out string msg)
    {
        code = "";
        msg = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "error")
                return false;
            code = root.TryGetProperty("code", out var c) ? (c.GetString() ?? "") : "";
            msg = root.TryGetProperty("msg", out var m) ? (m.GetString() ?? "") : "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDecodeAudio(byte[] buffer, int payloadOffset, int payloadLength, float[] destination, out ulong captureTsNs, out int sampleCount)
    {
        captureTsNs = 0;
        sampleCount = 0;
        if (payloadLength != AudioPayloadBytes)
            return false;
        if (destination.Length < AudioSamples)
            return false;
        captureTsNs = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(payloadOffset, 8));
        int samplesOffset = payloadOffset + 8;
        for (int i = 0; i < AudioSamples; i++)
            destination[i] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(samplesOffset + i * 4, 4));
        sampleCount = AudioSamples;
        return true;
    }

    private static string JsonString(string value) => JsonSerializer.Serialize(value);
}
