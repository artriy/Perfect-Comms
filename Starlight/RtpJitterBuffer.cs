using System;

namespace PerfectComms.Starlight.Media;

public enum RtpJitterDecisionKind
{
    Wait,
    Packet,
    Fec,
    Plc,
    Discontinuity
}

public readonly struct RtpJitterDecision
{
    public RtpJitterDecisionKind Kind { get; }
    public long ExtendedSequence { get; }
    public uint Timestamp { get; }
    public int PayloadLength { get; }

    internal RtpJitterDecision(
        RtpJitterDecisionKind kind,
        long extendedSequence,
        uint timestamp,
        int payloadLength)
    {
        Kind = kind;
        ExtendedSequence = extendedSequence;
        Timestamp = timestamp;
        PayloadLength = payloadLength;
    }
}

public sealed class RtpJitterBuffer
{
    public const int DefaultCapacity = 32;
    public const int MaximumCapacity = 256;
    public const int DefaultMaximumPayloadBytes = ManagedOpusEncoder.MaxPacketBytes;

    private const int TimestampStep = ManagedOpusEncoder.FrameSamples;
    private const int MaximumForwardJump = 1_024;
    private const int MaximumRecoveryFrames = 5;
    private const long NoDeadline = long.MinValue;

    private readonly int _capacity;
    private readonly int _maximumPayloadBytes;
    private readonly int _primePackets;
    private readonly long _primeDeadlineMilliseconds;
    private readonly long _missingDeadlineMilliseconds;
    private readonly bool[] _occupied;
    private readonly long[] _sequences;
    private readonly uint[] _timestamps;
    private readonly int[] _lengths;
    private readonly byte[] _payloads;

    private int _count;
    private bool _hasHighestSequence;
    private long _highestSequence;
    private bool _started;
    private long _expectedSequence;
    private uint _nextTimestamp;
    private long _firstArrivalMilliseconds;
    private long _missingSinceMilliseconds = NoDeadline;
    private bool _backlogExceeded;

    public RtpJitterBuffer(
        int capacity = DefaultCapacity,
        int maxPayloadBytes = DefaultMaximumPayloadBytes,
        int primePackets = 3,
        long primeDeadlineMilliseconds = 60,
        long missingDeadlineMilliseconds = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaximumCapacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayloadBytes, ManagedOpusEncoder.MaxPacketBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(primePackets, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(primePackets, capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(primeDeadlineMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(missingDeadlineMilliseconds);

        _capacity = capacity;
        _maximumPayloadBytes = maxPayloadBytes;
        _primePackets = primePackets;
        _primeDeadlineMilliseconds = primeDeadlineMilliseconds;
        _missingDeadlineMilliseconds = missingDeadlineMilliseconds;
        _occupied = new bool[capacity];
        _sequences = new long[capacity];
        _timestamps = new uint[capacity];
        _lengths = new int[capacity];
        _payloads = new byte[checked(capacity * maxPayloadBytes)];
    }

    public int Count => _count;

    public bool IsPrimed => _started;

    public bool Push(
        ushort sequence,
        uint timestamp,
        ReadOnlySpan<byte> payload,
        long arrivalMilliseconds)
    {
        if (payload.IsEmpty || payload.Length > _maximumPayloadBytes)
        {
            return false;
        }

        long extendedSequence = ExtendSequence(sequence);
        if (_hasHighestSequence && extendedSequence > _highestSequence + MaximumForwardJump)
        {
            ClearState();
            _hasHighestSequence = true;
            _highestSequence = extendedSequence;
        }

        if (_started && extendedSequence < _expectedSequence)
        {
            return false;
        }

        if (FindSequence(extendedSequence) >= 0)
        {
            return false;
        }
        if (_count == _capacity)
        {
            if (!_started)
                return false;
            int oldestSlot = FindLowestSequenceSlot();
            if (extendedSequence <= _sequences[oldestSlot])
                return false;
            Remove(oldestSlot);
            _backlogExceeded = true;
        }

        int slot = FindFreeSlot();
        _occupied[slot] = true;
        _sequences[slot] = extendedSequence;
        _timestamps[slot] = timestamp;
        _lengths[slot] = payload.Length;
        payload.CopyTo(_payloads.AsSpan(slot * _maximumPayloadBytes, payload.Length));
        _count++;

        if (!_hasHighestSequence || extendedSequence > _highestSequence)
        {
            _hasHighestSequence = true;
            _highestSequence = extendedSequence;
        }

        if (_count == 1)
        {
            _firstArrivalMilliseconds = arrivalMilliseconds;
        }

        return true;
    }

    public RtpJitterDecision GetDecision(long nowMilliseconds, Span<byte> payload)
    {
        if (_count == 0)
        {
            if (!_started)
                _missingSinceMilliseconds = NoDeadline;
            _backlogExceeded = false;
            return new RtpJitterDecision(
                RtpJitterDecisionKind.Wait,
                _started ? _expectedSequence : -1,
                _started ? _nextTimestamp : 0,
                0);
        }

        if (_backlogExceeded || _count > _primePackets + MaximumRecoveryFrames)
        {
            FastForwardBacklog(nowMilliseconds);
            return new RtpJitterDecision(
                RtpJitterDecisionKind.Discontinuity,
                _expectedSequence,
                _nextTimestamp,
                0);
        }

        if (!_started)
        {
            if (_count < _primePackets &&
                !DeadlineReached(nowMilliseconds, _firstArrivalMilliseconds, _primeDeadlineMilliseconds))
            {
                return new RtpJitterDecision(RtpJitterDecisionKind.Wait, -1, 0, 0);
            }

            int firstSlot = FindLowestSequenceSlot();
            _expectedSequence = _sequences[firstSlot];
            _nextTimestamp = _timestamps[firstSlot];
            _started = true;
            _missingSinceMilliseconds = nowMilliseconds;
        }

        int packetSlot = FindSequence(_expectedSequence);
        if (packetSlot >= 0)
        {
            uint packetTimestamp = _timestamps[packetSlot];
            uint timestampGap = unchecked(packetTimestamp - _nextTimestamp);
            if (timestampGap != 0)
            {
                if (timestampGap >= 0x80000000u ||
                    timestampGap % TimestampStep != 0 ||
                    timestampGap / TimestampStep > MaximumRecoveryFrames)
                {
                    _nextTimestamp = packetTimestamp;
                    _missingSinceMilliseconds = nowMilliseconds;
                    return new RtpJitterDecision(
                        RtpJitterDecisionKind.Discontinuity,
                        _expectedSequence,
                        packetTimestamp,
                        0);
                }

                uint missingTimestamp = _nextTimestamp;
                uint remainingGapFrames = timestampGap / TimestampStep;
                _nextTimestamp = unchecked(_nextTimestamp + TimestampStep);
                _missingSinceMilliseconds = nowMilliseconds;
                if (remainingGapFrames == 1)
                {
                    int fecLength = _lengths[packetSlot];
                    CopyPayload(packetSlot, payload, fecLength);
                    return new RtpJitterDecision(
                        RtpJitterDecisionKind.Fec,
                        _expectedSequence,
                        missingTimestamp,
                        fecLength);
                }
                return new RtpJitterDecision(
                    RtpJitterDecisionKind.Plc,
                    _expectedSequence,
                    missingTimestamp,
                    0);
            }

            int length = _lengths[packetSlot];
            CopyPayload(packetSlot, payload, length);
            long sequence = _expectedSequence;
            Remove(packetSlot);
            _expectedSequence++;
            _nextTimestamp = unchecked(packetTimestamp + TimestampStep);
            _missingSinceMilliseconds = nowMilliseconds;
            return new RtpJitterDecision(RtpJitterDecisionKind.Packet, sequence, packetTimestamp, length);
        }

        if (_missingSinceMilliseconds == NoDeadline)
        {
            _missingSinceMilliseconds = nowMilliseconds;
        }

        if (!DeadlineReached(nowMilliseconds, _missingSinceMilliseconds, _missingDeadlineMilliseconds))
        {
            return new RtpJitterDecision(
                RtpJitterDecisionKind.Wait,
                _expectedSequence,
                _nextTimestamp,
                0);
        }

        long missingSequence = _expectedSequence;
        uint missingSequenceTimestamp = _nextTimestamp;
        int nextPacketSlot = FindSequence(missingSequence + 1);
        if (nextPacketSlot >= 0 &&
            unchecked(_timestamps[nextPacketSlot] - missingSequenceTimestamp) == TimestampStep)
        {
            int length = _lengths[nextPacketSlot];
            CopyPayload(nextPacketSlot, payload, length);
            AdvanceMissing(nowMilliseconds);
            return new RtpJitterDecision(
                RtpJitterDecisionKind.Fec,
                missingSequence,
                missingSequenceTimestamp,
                length);
        }

        AdvanceMissing(nowMilliseconds);
        return new RtpJitterDecision(
            RtpJitterDecisionKind.Plc,
            missingSequence,
            missingSequenceTimestamp,
            0);
    }

    private void AdvanceMissing(long nowMilliseconds)
    {
        _expectedSequence++;
        _nextTimestamp = unchecked(_nextTimestamp + TimestampStep);
        _missingSinceMilliseconds = nowMilliseconds;
    }

    private void FastForwardBacklog(long nowMilliseconds)
    {
        while (_count > _primePackets)
            Remove(FindLowestSequenceSlot());
        int firstSlot = FindLowestSequenceSlot();
        _expectedSequence = _sequences[firstSlot];
        _nextTimestamp = _timestamps[firstSlot];
        _started = true;
        _missingSinceMilliseconds = nowMilliseconds;
        _backlogExceeded = false;
    }

    public void Reset()
    {
        ClearState();
    }

    private long ExtendSequence(ushort sequence)
    {
        if (!_hasHighestSequence)
        {
            return sequence;
        }

        long cycle = _highestSequence & ~0xffffL;
        long candidate = cycle | sequence;
        long difference = candidate - _highestSequence;
        if (difference > short.MaxValue)
        {
            candidate -= 1L << 16;
        }
        else if (difference < short.MinValue)
        {
            candidate += 1L << 16;
        }

        return candidate;
    }

    private int FindSequence(long sequence)
    {
        for (int i = 0; i < _capacity; i++)
        {
            if (_occupied[i] && _sequences[i] == sequence)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindFreeSlot()
    {
        for (int i = 0; i < _capacity; i++)
        {
            if (!_occupied[i])
            {
                return i;
            }
        }

        throw new InvalidOperationException("The RTP jitter buffer is full.");
    }

    private int FindLowestSequenceSlot()
    {
        int lowestSlot = -1;
        long lowestSequence = long.MaxValue;
        for (int i = 0; i < _capacity; i++)
        {
            if (_occupied[i] && _sequences[i] < lowestSequence)
            {
                lowestSequence = _sequences[i];
                lowestSlot = i;
            }
        }

        if (lowestSlot < 0)
        {
            throw new InvalidOperationException("The RTP jitter buffer is empty.");
        }

        return lowestSlot;
    }

    private void CopyPayload(int slot, Span<byte> destination, int length)
    {
        if (destination.Length < length)
        {
            throw new ArgumentException($"Payload output must hold at least {length} bytes.", nameof(destination));
        }

        _payloads.AsSpan(slot * _maximumPayloadBytes, length).CopyTo(destination);
    }

    private void Remove(int slot)
    {
        _occupied[slot] = false;
        _lengths[slot] = 0;
        _count--;
    }

    private void ClearState()
    {
        Array.Clear(_occupied);
        Array.Clear(_lengths);
        Array.Clear(_payloads);
        _count = 0;
        _hasHighestSequence = false;
        _highestSequence = 0;
        _started = false;
        _expectedSequence = 0;
        _nextTimestamp = 0;
        _firstArrivalMilliseconds = 0;
        _missingSinceMilliseconds = NoDeadline;
        _backlogExceeded = false;
    }

    private static bool DeadlineReached(long nowMilliseconds, long startMilliseconds, long timeoutMilliseconds)
    {
        return nowMilliseconds >= startMilliseconds &&
               nowMilliseconds - startMilliseconds >= timeoutMilliseconds;
    }
}
