using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace VoiceChatPlugin.VoiceChat;

internal readonly struct IceServer
{
    public readonly string Urls;
    public readonly string Username;
    public readonly string Credential;

    public IceServer(string urls, string username = "", string credential = "")
    {
        Urls = urls ?? "";
        Username = username ?? "";
        Credential = credential ?? "";
    }
}

internal static class SidecarProtocol
{
    public const byte TypeControl = 0x01;
    public const byte TypeAudio = 0x02;
    public const byte TypeAudioOut = 0x03;
    public const int AudioSamples = 960;
    public const int AudioPayloadBytes = 8 + AudioSamples * 4;
    public const int AudioOutFrames = 960;
    public const int AudioOutSamples = AudioOutFrames * 2;
    public const int AudioOutPayloadBytes = AudioOutSamples * 4;
    public const int HeaderBytes = 5;
    public const int MaxPayloadBytes = 1 << 20;
    public const int MaxPeerLevelsPerBatch = 32;
    public const int MobileAbi = 3;
    private const int MaxPeerIdChars = 32;
    private const float DefaultInputGain = 1f;
    private const float DefaultVadThreshold = 0.004f;

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

    public static byte[] SelectOutputDeviceFrame(string id)
        => EncodeControl($"{{\"op\":\"select-output-device\",\"id\":{JsonString(id)}}}");

    public static byte[] StartFrame() => EncodeControl("{\"op\":\"start\"}");
    public static byte[] StopFrame() => EncodeControl("{\"op\":\"stop\"}");
    public static byte[] PingFrame() => EncodeControl("{\"op\":\"ping\"}");

    public static byte[] SetDspFrame(bool aec, bool agc, bool ns, bool hpf)
        => EncodeControl($"{{\"op\":\"set-dsp\",\"aec\":{JsonBool(aec)},\"agc\":{JsonBool(agc)},\"ns\":{JsonBool(ns)},\"hpf\":{JsonBool(hpf)}}}");

    public static byte[] SetSyntheticFrame(bool enabled)
        => EncodeControl($"{{\"op\":\"set-synthetic\",\"enabled\":{JsonBool(enabled)}}}");

    public static byte[] SetInputFrame(float gain, float vadThreshold)
    {
        gain = NormalizeInputGain(gain);
        vadThreshold = NormalizeVadThreshold(vadThreshold);
        return EncodeControl(
            $"{{\"op\":\"set-input\",\"gain\":{gain.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},\"vad_threshold\":{vadThreshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}}");
    }

    internal static float NormalizeInputGain(float gain)
        => float.IsFinite(gain) ? Math.Clamp(gain, 0f, 2f) : DefaultInputGain;

    internal static float NormalizeVadThreshold(float threshold)
        => float.IsFinite(threshold) ? Math.Clamp(threshold, 0.0001f, 1f) : DefaultVadThreshold;

    public static byte[] AddPeerFrame(string peerId, bool isOfferer, bool relayOnly, int generation)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        return EncodeControl($"{{\"op\":\"peer-add\",\"peer_id\":{JsonString(peerId)},\"offerer\":{JsonBool(isOfferer)},\"relay_only\":{JsonBool(relayOnly)},\"generation\":{generation}}}");
    }

    public static byte[] RemovePeerFrame(string peerId)
        => EncodeControl($"{{\"op\":\"peer-remove\",\"peer_id\":{JsonString(peerId)}}}");

    public static byte[] SetRemoteSdpFrame(string peerId, string sdpType, string sdp)
        => EncodeControl($"{{\"op\":\"set-remote-sdp\",\"peer_id\":{JsonString(peerId)},\"sdp_type\":{JsonString(sdpType)},\"sdp\":{JsonString(sdp)}}}");

    public static byte[] AddIceCandidateFrame(string peerId, string candidate)
        => EncodeControl($"{{\"op\":\"add-ice-candidate\",\"peer_id\":{JsonString(peerId)},\"candidate\":{JsonString(candidate)}}}");

    public static byte[] SetIceServersFrame(IEnumerable<IceServer> servers)
    {
        using var stream = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("op", "set-ice-servers");
            w.WriteStartArray("servers");
            foreach (var s in servers)
            {
                if (string.IsNullOrEmpty(s.Urls)) continue;
                w.WriteStartObject();
                w.WriteStartArray("urls");
                w.WriteStringValue(s.Urls);
                w.WriteEndArray();
                if (!string.IsNullOrEmpty(s.Username)) w.WriteString("username", s.Username);
                if (!string.IsNullOrEmpty(s.Credential)) w.WriteString("credential", s.Credential);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return EncodeFrame(TypeControl, stream.ToArray());
    }

    public readonly struct GameStatePeerInput
    {
        public readonly string Id;
        public readonly float Gain;
        public readonly float Pan;
        public readonly int Mode;

        public GameStatePeerInput(string id, float gain, float pan, int mode)
        {
            Id = id;
            Gain = gain;
            Pan = pan;
            Mode = mode;
        }
    }

    public static byte[] GameStateFrame(
        bool deaf,
        float master,
        IReadOnlyList<GameStatePeerInput> peers)
    {
        using var stream = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("op", "game-state");
            w.WriteBoolean("deaf", deaf);
            w.WriteNumber("master", master);
            w.WriteStartArray("peers");
            for (int i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteNumber("gain", p.Gain);
                w.WriteNumber("pan", p.Pan);
                w.WriteNumber("mode", p.Mode);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return EncodeFrame(TypeControl, stream.ToArray());
    }

    private static string JsonBool(bool value) => value ? "true" : "false";

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

    public static bool TryReadDevices(string json, out List<string> names)
    {
        names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var device in devices.EnumerateArray())
            {
                if (device.TryGetProperty("name", out var n))
                {
                    var name = n.GetString();
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name!);
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadOutputDevices(string json, out List<string> names)
    {
        names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("outputDevices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var device in devices.EnumerateArray())
            {
                if (device.TryGetProperty("name", out var n))
                {
                    var name = n.GetString();
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name!);
                }
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

    public static bool TryReadLevel(string json, out float peak, out bool speaking)
    {
        peak = 0f;
        speaking = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "level")
                return false;
            peak = root.TryGetProperty("peak", out var p) ? p.GetSingle() : 0f;
            if (!float.IsFinite(peak)) return false;
            peak = Math.Clamp(peak, 0f, 1f);
            speaking = root.TryGetProperty("speaking", out var s) && s.GetBoolean();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public readonly struct PeerLevel
    {
        public PeerLevel(string peerId, float peak)
        {
            PeerId = peerId;
            Peak = peak;
        }

        public string PeerId { get; }
        public float Peak { get; }
    }

    public static bool TryReadPeerLevels(string json, out List<PeerLevel> levels)
    {
        levels = new List<PeerLevel>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "peer-levels" ||
                !root.TryGetProperty("levels", out var values) || values.ValueKind != JsonValueKind.Array)
                return false;
            if (values.GetArrayLength() > MaxPeerLevelsPerBatch)
                return false;

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("peer_id", out var idElement) ||
                    idElement.ValueKind != JsonValueKind.String ||
                    !value.TryGetProperty("peak", out var peakElement) ||
                    peakElement.ValueKind != JsonValueKind.Number ||
                    !peakElement.TryGetSingle(out var peak) ||
                    !float.IsFinite(peak))
                    continue;

                var peerId = idElement.GetString() ?? string.Empty;
                if (peerId.Length == 0 || peerId.Length > MaxPeerIdChars)
                    continue;
                levels.Add(new PeerLevel(peerId, Math.Clamp(peak, 0f, 1f)));
            }
            return true;
        }
        catch
        {
            levels.Clear();
            return false;
        }
    }

    public static bool TryReadLocalSdp(string json, out string peerId, out int generation, out string sdpType, out string sdp)
    {
        peerId = "";
        generation = 0;
        sdpType = "";
        sdp = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "local-sdp")
                return false;
            peerId = root.TryGetProperty("peer_id", out var p) ? (p.GetString() ?? "") : "";
            if (!root.TryGetProperty("generation", out var g) || !g.TryGetInt32(out generation) || generation <= 0)
                return false;
            sdpType = root.TryGetProperty("sdp_type", out var t) ? (t.GetString() ?? "") : "";
            sdp = root.TryGetProperty("sdp", out var s) ? (s.GetString() ?? "") : "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadLocalCandidate(string json, out string peerId, out int generation, out string candidate)
    {
        peerId = "";
        generation = 0;
        candidate = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "local-candidate")
                return false;
            peerId = root.TryGetProperty("peer_id", out var p) ? (p.GetString() ?? "") : "";
            if (!root.TryGetProperty("generation", out var g) || !g.TryGetInt32(out generation) || generation <= 0)
                return false;
            candidate = root.TryGetProperty("candidate", out var c) ? (c.GetString() ?? "") : "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadPeerState(string json, out string peerId, out int generation, out string state)
    {
        peerId = "";
        generation = 0;
        state = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetString() != "peer-state")
                return false;
            peerId = root.TryGetProperty("peer_id", out var p) ? (p.GetString() ?? "") : "";
            if (!root.TryGetProperty("generation", out var g) || !g.TryGetInt32(out generation) || generation <= 0)
                return false;
            state = root.TryGetProperty("state", out var s) ? (s.GetString() ?? "") : "";
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
