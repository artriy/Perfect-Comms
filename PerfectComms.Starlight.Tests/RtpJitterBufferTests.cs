using PerfectComms.Starlight.Media;
using Xunit;

namespace PerfectComms.Starlight.Tests;

public sealed class RtpJitterBufferTests
{
    [Fact]
    public void SequenceAndTimestampWrapRemainOrdered()
    {
        var jitter = new RtpJitterBuffer(primePackets: 3);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];
        byte[] first = [1];
        byte[] second = [2];
        byte[] third = [3];
        uint firstTimestamp = uint.MaxValue - ((uint)ManagedOpusEncoder.FrameSamples - 1u);

        Assert.True(jitter.Push(ushort.MaxValue, firstTimestamp, first, 0));
        Assert.True(jitter.Push(0, 0, second, 1));
        Assert.True(jitter.Push(1, ManagedOpusEncoder.FrameSamples, third, 2));

        RtpJitterDecision firstDecision = jitter.GetDecision(2, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, firstDecision.Kind);
        Assert.Equal(65_535L, firstDecision.ExtendedSequence);
        Assert.Equal(firstTimestamp, firstDecision.Timestamp);
        Assert.Equal(first, output.AsSpan(0, firstDecision.PayloadLength).ToArray());

        RtpJitterDecision secondDecision = jitter.GetDecision(2, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, secondDecision.Kind);
        Assert.Equal(65_536L, secondDecision.ExtendedSequence);
        Assert.Equal(0u, secondDecision.Timestamp);
        Assert.Equal(second, output.AsSpan(0, secondDecision.PayloadLength).ToArray());

        RtpJitterDecision thirdDecision = jitter.GetDecision(2, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, thirdDecision.Kind);
        Assert.Equal(65_537L, thirdDecision.ExtendedSequence);
        Assert.Equal((uint)ManagedOpusEncoder.FrameSamples, thirdDecision.Timestamp);
        Assert.Equal(third, output.AsSpan(0, thirdDecision.PayloadLength).ToArray());
    }

    [Fact]
    public void PrePrimeOverflowRetainsTheNewestBoundedTail()
    {
        var jitter = new RtpJitterBuffer(capacity: 2, primePackets: 2);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];

        Assert.True(jitter.Push(10, 1_000, [10], 0));
        Assert.False(jitter.Push(10, 1_000, [10], 1));
        Assert.True(jitter.Push(11, 1_960, [11], 2));
        Assert.True(jitter.Push(12, 2_920, [12], 3));
        Assert.Equal(2, jitter.Count);

        RtpJitterDecision discontinuity = jitter.GetDecision(3, output);
        Assert.Equal(RtpJitterDecisionKind.Discontinuity, discontinuity.Kind);
        Assert.Equal(11L, discontinuity.ExtendedSequence);
        Assert.Equal(2, jitter.Count);

        RtpJitterDecision firstRetained = jitter.GetDecision(3, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, firstRetained.Kind);
        Assert.Equal(11L, firstRetained.ExtendedSequence);
        Assert.Equal((byte)11, output[0]);
        Assert.False(jitter.Push(10, 1_000, [10], 4));

        RtpJitterDecision secondRetained = jitter.GetDecision(3, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, secondRetained.Kind);
        Assert.Equal(12L, secondRetained.ExtendedSequence);
        Assert.Equal((byte)12, output[0]);
    }

    [Fact]
    public void PrimeThresholdAndDeadlineBothStartPlayback()
    {
        var threshold = new RtpJitterBuffer(primePackets: 2, primeDeadlineMilliseconds: 60);
        var deadline = new RtpJitterBuffer(primePackets: 3, primeDeadlineMilliseconds: 60);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];
        byte[] payload = [1];

        Assert.True(threshold.Push(20, 10_000, payload, 100));
        Assert.Equal(RtpJitterDecisionKind.Wait, threshold.GetDecision(159, output).Kind);
        Assert.True(threshold.Push(21, 10_960, payload, 159));
        Assert.Equal(RtpJitterDecisionKind.Packet, threshold.GetDecision(159, output).Kind);

        Assert.True(deadline.Push(30, 20_000, payload, 200));
        Assert.Equal(RtpJitterDecisionKind.Wait, deadline.GetDecision(259, output).Kind);
        RtpJitterDecision expired = deadline.GetDecision(260, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, expired.Kind);
        Assert.Equal(30L, expired.ExtendedSequence);
    }

    [Fact]
    public void IdleQueueStartsAFreshMissingDeadlineBeforeFecAndPlc()
    {
        var jitter = new RtpJitterBuffer(
            primePackets: 1,
            primeDeadlineMilliseconds: 0,
            missingDeadlineMilliseconds: 20);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];
        byte[] first = [1, 1];
        byte[] following = [7, 7, 7];

        Assert.True(jitter.Push(5, 500, first, 0));
        Assert.Equal(RtpJitterDecisionKind.Packet, jitter.GetDecision(0, output).Kind);
        RtpJitterDecision emptyQueueWait = jitter.GetDecision(19, output);
        Assert.Equal(RtpJitterDecisionKind.Wait, emptyQueueWait.Kind);
        Assert.Equal(6L, emptyQueueWait.ExtendedSequence);
        Assert.Equal(1_460u, emptyQueueWait.Timestamp);
        Assert.True(jitter.Push(7, 2_420, following, 19));

        Assert.Equal(RtpJitterDecisionKind.Wait, jitter.GetDecision(20, output).Kind);
        Assert.Equal(RtpJitterDecisionKind.Wait, jitter.GetDecision(39, output).Kind);
        RtpJitterDecision fec = jitter.GetDecision(40, output);
        Assert.Equal(RtpJitterDecisionKind.Fec, fec.Kind);
        Assert.Equal(6L, fec.ExtendedSequence);
        Assert.Equal(1_460u, fec.Timestamp);
        Assert.Equal(following.Length, fec.PayloadLength);
        Assert.Equal(following, output.AsSpan(0, fec.PayloadLength).ToArray());
        Assert.Equal(1, jitter.Count);

        RtpJitterDecision followingPacket = jitter.GetDecision(40, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, followingPacket.Kind);
        Assert.Equal(7L, followingPacket.ExtendedSequence);
        Assert.Equal(2_420u, followingPacket.Timestamp);
        Assert.Equal(0, jitter.Count);
        Assert.True(jitter.Push(10, 5_300, following, 59));

        Assert.Equal(RtpJitterDecisionKind.Wait, jitter.GetDecision(59, output).Kind);
        Assert.Equal(RtpJitterDecisionKind.Wait, jitter.GetDecision(78, output).Kind);
        RtpJitterDecision plc = jitter.GetDecision(79, output);
        Assert.Equal(RtpJitterDecisionKind.Plc, plc.Kind);
        Assert.Equal(8L, plc.ExtendedSequence);
        Assert.Equal(3_380u, plc.Timestamp);
        Assert.Equal(0, plc.PayloadLength);
    }

    [Fact]
    public void ImplausibleForwardJumpDropsTheOldEpochAndReprimes()
    {
        var jitter = new RtpJitterBuffer(primePackets: 1);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];
        byte[] oldPayload = [1];
        byte[] newPayload = [2];

        Assert.True(jitter.Push(100, 1_000, oldPayload, 0));
        Assert.True(jitter.Push(2_000, 2_000, newPayload, 1));
        Assert.Equal(1, jitter.Count);

        RtpJitterDecision decision = jitter.GetDecision(1, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, decision.Kind);
        Assert.Equal(2_000L, decision.ExtendedSequence);
        Assert.Equal(newPayload, output.AsSpan(0, decision.PayloadLength).ToArray());
    }

    [Fact]
    public void EmptyQueueWaitsWithoutAdvancingExpectedSequence()
    {
        var jitter = new RtpJitterBuffer(
            primePackets: 1,
            primeDeadlineMilliseconds: 0,
            missingDeadlineMilliseconds: 0);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];
        byte[] first = [1];
        byte[] second = [2];

        Assert.True(jitter.Push(10, 1_000, first, 0));
        Assert.Equal(RtpJitterDecisionKind.Packet, jitter.GetDecision(0, output).Kind);

        RtpJitterDecision firstWait = jitter.GetDecision(1_000, output);
        RtpJitterDecision secondWait = jitter.GetDecision(2_000, output);
        Assert.Equal(RtpJitterDecisionKind.Wait, firstWait.Kind);
        Assert.Equal(11L, firstWait.ExtendedSequence);
        Assert.Equal(1_960u, firstWait.Timestamp);
        Assert.Equal(RtpJitterDecisionKind.Wait, secondWait.Kind);
        Assert.Equal(firstWait.ExtendedSequence, secondWait.ExtendedSequence);
        Assert.Equal(firstWait.Timestamp, secondWait.Timestamp);

        Assert.True(jitter.Push(11, 1_960, second, 2_001));
        RtpJitterDecision packet = jitter.GetDecision(2_001, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, packet.Kind);
        Assert.Equal(11L, packet.ExtendedSequence);
    }

    [Fact]
    public void TimestampGapAcrossWrapConcealsFramesAndRetainsContiguousPacket()
    {
        var jitter = new RtpJitterBuffer(primePackets: 1, primeDeadlineMilliseconds: 0);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];
        byte[] first = [1];
        byte[] following = [2];
        uint firstTimestamp = uint.MaxValue - ((uint)ManagedOpusEncoder.FrameSamples - 1u);

        Assert.True(jitter.Push(ushort.MaxValue, firstTimestamp, first, 0));
        Assert.Equal(RtpJitterDecisionKind.Packet, jitter.GetDecision(0, output).Kind);
        Assert.True(jitter.Push(0, (uint)ManagedOpusEncoder.FrameSamples * 2u, following, 1));

        RtpJitterDecision plc = jitter.GetDecision(1, output);
        Assert.Equal(RtpJitterDecisionKind.Plc, plc.Kind);
        Assert.Equal(0u, plc.Timestamp);
        Assert.Equal(1, jitter.Count);

        RtpJitterDecision fec = jitter.GetDecision(1, output);
        Assert.Equal(RtpJitterDecisionKind.Fec, fec.Kind);
        Assert.Equal((uint)ManagedOpusEncoder.FrameSamples, fec.Timestamp);
        Assert.Equal(following, output.AsSpan(0, fec.PayloadLength).ToArray());
        Assert.Equal(1, jitter.Count);

        RtpJitterDecision packet = jitter.GetDecision(1, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, packet.Kind);
        Assert.Equal(65_536L, packet.ExtendedSequence);
        Assert.Equal((uint)ManagedOpusEncoder.FrameSamples * 2u, packet.Timestamp);
        Assert.Equal(0, jitter.Count);
    }

    [Fact]
    public void LargeTimestampGapRebasesTimelineBeforeCurrentPacket()
    {
        var jitter = new RtpJitterBuffer(primePackets: 1, primeDeadlineMilliseconds: 0);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];
        byte[] payload = [4];

        Assert.True(jitter.Push(20, 10_000, payload, 0));
        Assert.Equal(RtpJitterDecisionKind.Packet, jitter.GetDecision(0, output).Kind);
        uint laterTimestamp = 10_000u + (uint)ManagedOpusEncoder.FrameSamples * 7u;
        Assert.True(jitter.Push(21, laterTimestamp, payload, 1));

        RtpJitterDecision discontinuity = jitter.GetDecision(1, output);
        Assert.Equal(RtpJitterDecisionKind.Discontinuity, discontinuity.Kind);
        Assert.Equal(laterTimestamp, discontinuity.Timestamp);
        Assert.Equal(1, jitter.Count);

        RtpJitterDecision packet = jitter.GetDecision(1, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, packet.Kind);
        Assert.Equal(21L, packet.ExtendedSequence);
        Assert.Equal(laterTimestamp, packet.Timestamp);
    }

    [Fact]
    public void LongSequenceLossFastForwardsWithoutDeletingRetainedPackets()
    {
        var jitter = new RtpJitterBuffer(
            capacity: 16,
            primePackets: 1,
            primeDeadlineMilliseconds: 0,
            missingDeadlineMilliseconds: 20);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];

        Assert.True(jitter.Push(1, 1_000, [1], 0));
        Assert.Equal(RtpJitterDecisionKind.Packet, jitter.GetDecision(0, output).Kind);
        Assert.True(jitter.Push(9, 8_680, [9], 1));
        Assert.True(jitter.Push(10, 9_640, [10], 2));

        RtpJitterDecision discontinuity = jitter.GetDecision(2, output);
        Assert.Equal(RtpJitterDecisionKind.Discontinuity, discontinuity.Kind);
        Assert.Equal(9L, discontinuity.ExtendedSequence);
        Assert.Equal(2, jitter.Count);

        RtpJitterDecision retained = jitter.GetDecision(2, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, retained.Kind);
        Assert.Equal(9L, retained.ExtendedSequence);
        Assert.Equal((byte)9, output[0]);
        Assert.Equal(1, jitter.Count);
    }

    [Fact]
    public void ExcessBacklogFastForwardsToNewestRecoveryWindow()
    {
        var jitter = new RtpJitterBuffer(
            capacity: 16,
            primePackets: 2,
            primeDeadlineMilliseconds: 0);
        var output = new byte[ManagedOpusEncoder.MaxPacketBytes];

        for (ushort sequence = 100; sequence < 110; sequence++)
        {
            uint timestamp = 1_000u + (uint)(sequence - 100) * (uint)ManagedOpusEncoder.FrameSamples;
            Assert.True(jitter.Push(sequence, timestamp, [(byte)sequence], sequence));
        }

        RtpJitterDecision discontinuity = jitter.GetDecision(110, output);
        Assert.Equal(RtpJitterDecisionKind.Discontinuity, discontinuity.Kind);
        Assert.Equal(108L, discontinuity.ExtendedSequence);
        Assert.Equal(2, jitter.Count);

        RtpJitterDecision packet = jitter.GetDecision(110, output);
        Assert.Equal(RtpJitterDecisionKind.Packet, packet.Kind);
        Assert.Equal(108L, packet.ExtendedSequence);
        Assert.Equal(1, jitter.Count);
    }

}
