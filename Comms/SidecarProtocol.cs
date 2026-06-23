using System;
using System.Buffers.Binary;

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
}
