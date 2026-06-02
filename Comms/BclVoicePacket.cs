using System;
using System.Collections.Generic;
using System.Linq;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

[Flags]
internal enum BclVoicePacketFlags : byte
{
    None = 0,
    Radio = 1 << 0,
    LossResistant = 1 << 1,
    Synthetic = 1 << 2,
}

internal enum BclVoicePlayoutKind
{
    Audio,
    Plc,
    Fec,
}

internal readonly struct BclVoicePacket
{
    private const byte Magic0 = (byte)'P';
    private const byte Magic1 = (byte)'V';
    private const byte Version = 2;
    private const byte CodecOpus = 1;
    public const int HeaderBytes = 14;

    public BclVoicePacket(ushort sequence, uint timestamp, ushort duration, BclVoicePacketFlags flags, byte level, byte[] payload)
    {
        Sequence = sequence;
        Timestamp = timestamp;
        Duration = duration;
        Flags = flags;
        Level = level;
        Payload = payload;
    }

    public ushort Sequence { get; }
    public uint Timestamp { get; }
    public ushort Duration { get; }
    public BclVoicePacketFlags Flags { get; }
    public byte Level { get; }
    public byte[] Payload { get; }
    public bool HasLossResistantFec => Flags.HasFlag(BclVoicePacketFlags.LossResistant);

    public static byte QuantizeLevel(float level)
        => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(level, 0f, 1f) * byte.MaxValue), 0, byte.MaxValue);

    public static bool HasMagic(byte[] packet)
        => packet.Length >= 2 && packet[0] == Magic0 && packet[1] == Magic1;

    public static byte[] Wrap(byte[] opusPayload, ushort sequence, uint timestamp, ushort duration, BclVoicePacketFlags flags, byte level)
        => Wrap(opusPayload, opusPayload.Length, sequence, timestamp, duration, flags, level);

    // Length-aware overload: frames only the first payloadLength bytes of opusPayload. Lets the sender wrap
    // directly from its reusable encode scratch buffer (which is larger than the encoded frame) without an
    // intermediate trimmed-copy allocation per transmitted frame.
    public static byte[] Wrap(byte[] opusPayload, int payloadLength, ushort sequence, uint timestamp, ushort duration, BclVoicePacketFlags flags, byte level)
    {
        if (payloadLength <= 0) throw new ArgumentException("Payload must not be empty.", nameof(opusPayload));
        if (payloadLength > opusPayload.Length) throw new ArgumentOutOfRangeException(nameof(payloadLength));
        if ((flags & ~AllowedFlags) != 0) throw new ArgumentOutOfRangeException(nameof(flags));
        duration = duration == 0 ? (ushort)AudioHelpers.FrameSize : duration;

        var packet = new byte[HeaderBytes + payloadLength];
        packet[0] = Magic0;
        packet[1] = Magic1;
        packet[2] = Version;
        packet[3] = CodecOpus;
        WriteUInt16(packet, 4, sequence);
        WriteUInt32(packet, 6, timestamp);
        WriteUInt16(packet, 10, duration);
        packet[12] = (byte)flags;
        packet[13] = level;
        Array.Copy(opusPayload, 0, packet, HeaderBytes, payloadLength);
        return packet;
    }

    public static bool TryRead(byte[] packet, out BclVoicePacket voicePacket)
    {
        voicePacket = default;
        if (packet.Length <= HeaderBytes || !HasMagic(packet)) return false;
        if (packet[2] != Version || packet[3] != CodecOpus) return false;

        var flags = (BclVoicePacketFlags)packet[12];
        if ((flags & ~AllowedFlags) != 0) return false;

        var duration = ReadUInt16(packet, 10);
        if (duration == 0 || duration > 5760) return false;

        var payload = new byte[packet.Length - HeaderBytes];
        Array.Copy(packet, HeaderBytes, payload, 0, payload.Length);
        voicePacket = new BclVoicePacket(ReadUInt16(packet, 4), ReadUInt32(packet, 6), duration, flags, packet[13], payload);
        return true;
    }

    private static BclVoicePacketFlags AllowedFlags =>
        BclVoicePacketFlags.Radio |
        BclVoicePacketFlags.LossResistant |
        BclVoicePacketFlags.Synthetic;

    private static ushort ReadUInt16(byte[] buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    private static uint ReadUInt32(byte[] buffer, int offset)
        => ((uint)buffer[offset] << 24) |
           ((uint)buffer[offset + 1] << 16) |
           ((uint)buffer[offset + 2] << 8) |
           buffer[offset + 3];

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}

internal readonly struct BclVoicePlayoutFrame
{
    public BclVoicePlayoutFrame(BclVoicePlayoutKind kind, BclVoicePacket? packet, ushort sequence, ushort duration)
    {
        Kind = kind;
        Packet = packet;
        Sequence = sequence;
        Duration = duration;
    }

    public BclVoicePlayoutKind Kind { get; }
    public BclVoicePacket? Packet { get; }
    public ushort Sequence { get; }
    public ushort Duration { get; }
}

internal readonly struct BclVoiceJitterWindowStats
{
    public BclVoiceJitterWindowStats(int v2Packets, int legacyPackets, int lateDrops, int duplicateDrops, int reorderedPackets, int lostFrames, int plcFrames, int fecFrames, int maxDepth, int currentDepth)
    {
        V2Packets = v2Packets;
        LegacyPackets = legacyPackets;
        LateDrops = lateDrops;
        DuplicateDrops = duplicateDrops;
        ReorderedPackets = reorderedPackets;
        LostFrames = lostFrames;
        PlcFrames = plcFrames;
        FecFrames = fecFrames;
        MaxDepth = maxDepth;
        CurrentDepth = currentDepth;
    }

    public int V2Packets { get; }
    public int LegacyPackets { get; }
    public int LateDrops { get; }
    public int DuplicateDrops { get; }
    public int ReorderedPackets { get; }
    public int LostFrames { get; }
    public int PlcFrames { get; }
    public int FecFrames { get; }
    public int MaxDepth { get; }
    public int CurrentDepth { get; }

    public string ToCompactString()
        => $"v2:{V2Packets} legacy:{LegacyPackets} late:{LateDrops} dup:{DuplicateDrops} reorder:{ReorderedPackets} lost:{LostFrames} plc:{PlcFrames} fec:{FecFrames} depth:{CurrentDepth}/{MaxDepth}";
}

internal sealed class BclVoiceJitterBuffer
{
    public const int DefaultTargetDelayFrames = 3;
    public const int DefaultMaxBufferedFrames = 12;
    private const int MaxDrainFramesPerPacket = 8;

    private readonly int _targetDelayFrames;
    private readonly int _maxBufferedFrames;
    private readonly Dictionary<ushort, BclVoicePacket> _packets = new();
    private bool _hasExpected;
    private ushort _expectedSequence;
    private int _v2Packets;
    private int _legacyPackets;
    private int _lateDrops;
    private int _duplicateDrops;
    private int _reorderedPackets;
    private int _lostFrames;
    private int _plcFrames;
    private int _fecFrames;
    private int _maxDepth;
    private DateTime _lastEnqueueUtc = DateTime.MinValue;
    // Reused scratch: Enqueue/DrainDue run under PeerConnection._sync and results are consumed
    // before the lock releases, so sharing a list avoids a per-packet alloc with no aliasing risk.
    private readonly List<BclVoicePlayoutFrame> _enqueueScratch = new(MaxDrainFramesPerPacket);
    private readonly List<BclVoicePlayoutFrame> _drainScratch = new(MaxDrainFramesPerPacket);

    public BclVoiceJitterBuffer(int targetDelayFrames = DefaultTargetDelayFrames, int maxBufferedFrames = DefaultMaxBufferedFrames)
    {
        _targetDelayFrames = Math.Clamp(targetDelayFrames, 0, maxBufferedFrames);
        _maxBufferedFrames = Math.Max(1, maxBufferedFrames);
    }

    public IReadOnlyList<BclVoicePlayoutFrame> Enqueue(BclVoicePacket packet)
    {
        _v2Packets++;
        var frames = _enqueueScratch;
        frames.Clear();
        if (!_hasExpected)
        {
            _expectedSequence = packet.Sequence;
            _hasExpected = true;
        }

        if (IsBefore(packet.Sequence, _expectedSequence))
        {
            _lateDrops++;
            return frames;
        }

        // Large forward gap (e.g. after a data-channel rebuild where the sender's sequence advanced
        // across the closed-channel outage): the absent intermediate frames can never arrive within
        // the buffer window, so treat it as a stream discontinuity and snap to the new sequence
        // instead of synthesizing a multi-second PLC concealment catch-up one frame at a time.
        if (Distance(_expectedSequence, packet.Sequence) > _maxBufferedFrames)
        {
            _packets.Clear();
            _expectedSequence = packet.Sequence;
        }

        if (_packets.ContainsKey(packet.Sequence))
        {
            _duplicateDrops++;
            return frames;
        }

        _lastEnqueueUtc = DateTime.UtcNow;

        // Manual scan instead of LINQ .Any(): avoids per-packet iterator + closure allocation.
        bool anyBufferedAfter = false;
        foreach (var existing in _packets.Keys)
        {
            if (IsBefore(packet.Sequence, existing)) { anyBufferedAfter = true; break; }
        }
        if (anyBufferedAfter ||
            (Distance(_expectedSequence, packet.Sequence) > 0 && !_packets.ContainsKey(_expectedSequence)))
            _reorderedPackets++;

        _packets[packet.Sequence] = packet;
        _maxDepth = Math.Max(_maxDepth, _packets.Count);

        // On overflow drop the newest (furthest-future) frame, not the playout head, to avoid
        // an extra concealment/dropout per overflow.
        while (_packets.Count > _maxBufferedFrames)
        {
            var newest = FindHighestSequence();
            if (newest == null) break;
            _packets.Remove(newest.Value);
            _lateDrops++;
        }

        Drain(frames, false);
        return frames;
    }

    public IReadOnlyList<BclVoicePlayoutFrame> DrainDue(DateTime nowUtc, TimeSpan quietDelay)
    {
        var frames = _drainScratch;
        frames.Clear();
        if (!_hasExpected || _packets.Count == 0 || nowUtc - _lastEnqueueUtc < quietDelay)
            return frames;

        Drain(frames, true);
        return frames;
    }

    public void CountLegacyPacket() => _legacyPackets++;

    public BclVoiceJitterWindowStats ConsumeStats()
    {
        var stats = new BclVoiceJitterWindowStats(_v2Packets, _legacyPackets, _lateDrops, _duplicateDrops, _reorderedPackets, _lostFrames, _plcFrames, _fecFrames, _maxDepth, _packets.Count);
        _v2Packets = 0;
        _legacyPackets = 0;
        _lateDrops = 0;
        _duplicateDrops = 0;
        _reorderedPackets = 0;
        _lostFrames = 0;
        _plcFrames = 0;
        _fecFrames = 0;
        _maxDepth = _packets.Count;
        return stats;
    }

    private void Drain(List<BclVoicePlayoutFrame> frames, bool force)
    {
        while ((_packets.Count > _targetDelayFrames || force && _packets.Count > 0) && frames.Count < MaxDrainFramesPerPacket)
        {
            if (_packets.Remove(_expectedSequence, out var packet))
            {
                frames.Add(new BclVoicePlayoutFrame(BclVoicePlayoutKind.Audio, packet, packet.Sequence, packet.Duration));
                _expectedSequence++;
                continue;
            }

            var nextSequence = FindNextSequence();
            if (nextSequence == null) return;

            var next = _packets[nextSequence.Value];
            _lostFrames++;
            if (next.HasLossResistantFec)
            {
                _fecFrames++;
                frames.Add(new BclVoicePlayoutFrame(BclVoicePlayoutKind.Fec, next, _expectedSequence, next.Duration));
            }
            else
            {
                _plcFrames++;
                frames.Add(new BclVoicePlayoutFrame(BclVoicePlayoutKind.Plc, null, _expectedSequence, next.Duration));
            }
            _expectedSequence++;
        }
    }

    private ushort? FindNextSequence()
    {
        ushort? best = null;
        var bestDistance = int.MaxValue;
        foreach (var sequence in _packets.Keys)
        {
            if (IsBefore(sequence, _expectedSequence)) continue;
            var distance = Distance(_expectedSequence, sequence);
            if (distance < bestDistance)
            {
                best = sequence;
                bestDistance = distance;
            }
        }
        return best;
    }

    private ushort? FindHighestSequence()
    {
        ushort? best = null;
        var bestDistance = -1;
        foreach (var sequence in _packets.Keys)
        {
            if (IsBefore(sequence, _expectedSequence)) continue;
            var distance = Distance(_expectedSequence, sequence);
            if (distance > bestDistance)
            {
                best = sequence;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static bool IsBefore(ushort sequence, ushort expected)
        => unchecked((short)(sequence - expected)) < 0;

    private static int Distance(ushort from, ushort to)
        => unchecked((ushort)(to - from));
}
