using System;
using System.Threading;
using System.Threading.Tasks;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceDiagnosticsProductionTests
{
    [Fact]
    public void BufferIsBoundedAndReportsDrops()
    {
        var buffer = new DiagnosticBuffer<int>(2);

        Assert.True(buffer.TryEnqueue(1));
        Assert.True(buffer.TryEnqueue(2));
        Assert.False(buffer.TryEnqueue(3));
        Assert.Equal(2, buffer.PendingCount);
        Assert.Equal(1, buffer.DroppedSinceReport);
        Assert.Equal(1, buffer.DroppedTotal);

        Assert.True(buffer.TryDequeue(out var first));
        Assert.Equal(1, first);
        Assert.True(buffer.TryEnqueue(4));
        Assert.Equal(2, buffer.PendingCount);

        buffer.AcknowledgeDrops(1);
        Assert.Equal(0, buffer.DroppedSinceReport);
        Assert.Equal(1, buffer.DroppedTotal);
    }

    [Fact]
    public void BufferStaysWithinCapacityUnderConcurrentProducers()
    {
        const int capacity = 64;
        const int attempts = 10000;
        var buffer = new DiagnosticBuffer<int>(capacity);
        int accepted = 0;

        Parallel.For(0, attempts, i =>
        {
            if (buffer.TryEnqueue(i))
                Interlocked.Increment(ref accepted);
        });

        Assert.InRange(buffer.PendingCount, 0, capacity);
        Assert.Equal(buffer.PendingCount, accepted);
        Assert.Equal(attempts, accepted + buffer.DroppedTotal);
    }

    [Fact]
    public void StoppedBufferRejectsLateEntriesWithoutChangingDropTotals()
    {
        var buffer = new DiagnosticBuffer<string>(1);
        Assert.True(buffer.TryEnqueue("kept"));

        Assert.True(buffer.StopAcceptingAndWait(100));
        Assert.False(buffer.TryEnqueue("late"));
        Assert.Equal(1, buffer.PendingCount);
        Assert.Equal(0, buffer.DroppedTotal);
    }

    [Fact]
    public void FormatterProducesSingleLineWithSessionSequenceAndContextAge()
    {
        var entry = new DiagnosticEntry(
            new DateTime(2026, 7, 13, 1, 2, 3, DateTimeKind.Utc),
            1.25,
            1.5,
            42,
            "client=7 player=2 name=\"test\nname\"",
            19,
            "background",
            375,
            "signal\nrpc",
            "severity=error message=\"bad\r\nnews\"");

        string line = DiagnosticLogFormatter.FormatLine("abc123", 17, entry);

        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.Contains("session=abc123 seq=17", line);
        Assert.Contains("thread=19 contextThread=background mainContextAgeMs=375", line);
        Assert.Contains("signal_rpc", line);
        Assert.Contains("bad  news", line);
    }

    [Fact]
    public void QuotedValuesCannotBreakTheirQuotedFieldOrCreateExtraLines()
    {
        string safe = DiagnosticLogFormatter.SanitizeQuotedValue("a\"b\r\nc");

        Assert.Equal("a'b  c", safe);
        Assert.DoesNotContain('"', safe);
        Assert.DoesNotContain('\r', safe);
        Assert.DoesNotContain('\n', safe);
    }

    [Fact]
    public void ShareableDiagnosticsKeyHashLobbyIdentifiers()
    {
        const string room = "ABCDEF";
        const string region = "Custom Server 10.0.0.25:22023";

        string roomDescription = VoiceDiagnostics.DescribeRoom(room);
        string regionDescription = VoiceDiagnostics.DescribeRegion(region);

        Assert.Equal(roomDescription, VoiceDiagnostics.DescribeRoom(room));
        Assert.Contains("roomHash=", roomDescription);
        Assert.Contains("regionHash=", regionDescription);
        Assert.False(roomDescription.Contains(room, StringComparison.Ordinal));
        Assert.False(regionDescription.Contains(region, StringComparison.Ordinal));
        Assert.NotEqual(roomDescription, VoiceDiagnostics.DescribeRoom("FEDCBA"));
    }

    [Fact]
    public void MissingSensitiveDiagnosticValuesDoNotInventIdentifiers()
    {
        Assert.Equal("roomPresent=false", VoiceDiagnostics.DescribeRoom(null));
        Assert.Equal("regionPresent=false", VoiceDiagnostics.DescribeRegion(""));
    }
}
