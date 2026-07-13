using System.Linq;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SignalingHelloMailboxTests
{
    [Fact]
    public void StoreDeduplicatesBySessionAndSenderAndKeepsLatestClone()
    {
        var mailbox = new SignalingHelloMailbox(capacity: 4, ttlMs: 1000);
        var scope = new SignalingSessionScope(55, 7);
        var first = new byte[] { 1, 2 };
        var latest = new byte[] { 3, 4 };

        Assert.True(mailbox.Store(scope, 11, 101, first, 100, out var firstResult));
        Assert.Equal("stored", firstResult);
        first[0] = 99;
        Assert.True(mailbox.Store(scope, 11, 202, latest, 200, out var secondResult));
        Assert.Equal("replaced", secondResult);
        latest[0] = 88;
        Assert.Equal(1, mailbox.Count);

        var drained = mailbox.Drain(scope, _ => true, 250, out var expired, out var wrongSession, out var absent);

        var hello = Assert.Single(drained);
        Assert.Equal(11, hello.SenderClientId);
        Assert.Equal((uint)202, hello.SenderNetId);
        Assert.Equal(new byte[] { 3, 4 }, hello.Payload);
        Assert.Equal(200, hello.ReceivedAtMs);
        Assert.Equal(0, expired);
        Assert.Equal(0, wrongSession);
        Assert.Equal(0, absent);
        Assert.Equal(0, mailbox.Count);
    }

    [Fact]
    public void DrainRejectsExpiredWrongSessionAndAbsentRosterEntries()
    {
        var mailbox = new SignalingHelloMailbox(capacity: 8, ttlMs: 100);
        var active = new SignalingSessionScope(55, 7);
        var other = new SignalingSessionScope(56, 7);

        Assert.True(mailbox.Store(active, 11, 11, new byte[] { 11 }, 100, out _));
        Assert.True(mailbox.Store(other, 12, 12, new byte[] { 12 }, 100, out _));
        Assert.True(mailbox.Store(active, 13, 13, new byte[] { 13 }, 100, out _));
        Assert.True(mailbox.Store(active, 14, 14, new byte[] { 14 }, 0, out _));

        var drained = mailbox.Drain(
            active,
            sender => sender == 11,
            110,
            out var expired,
            out var wrongSession,
            out var absent);

        Assert.Equal(new[] { 11 }, drained.Select(hello => hello.SenderClientId).ToArray());
        Assert.Equal(1, expired);
        Assert.Equal(1, wrongSession);
        Assert.Equal(1, absent);
        Assert.Equal(0, mailbox.Count);
    }

    [Fact]
    public void CapacityEvictsOldestWhileReplacementBecomesNewest()
    {
        var mailbox = new SignalingHelloMailbox(capacity: 2, ttlMs: 1000);
        var scope = new SignalingSessionScope(55, 7);

        Assert.True(mailbox.Store(scope, 11, 11, new byte[] { 11 }, 100, out _));
        Assert.True(mailbox.Store(scope, 12, 12, new byte[] { 12 }, 110, out _));
        Assert.True(mailbox.Store(scope, 11, 111, new byte[] { 111 }, 120, out var replaceResult));
        Assert.Equal("replaced", replaceResult);
        Assert.True(mailbox.Store(scope, 13, 13, new byte[] { 13 }, 130, out var capacityResult));
        Assert.Equal("stored-after-oldest-evicted", capacityResult);

        var drained = mailbox.Drain(scope, _ => true, 150, out var expired, out var wrongSession, out var absent);

        Assert.Equal(new[] { 11, 13 }, drained.Select(hello => hello.SenderClientId).ToArray());
        Assert.Equal(new uint[] { 111, 13 }, drained.Select(hello => hello.SenderNetId).ToArray());
        Assert.Equal(0, expired);
        Assert.Equal(0, wrongSession);
        Assert.Equal(0, absent);
    }

    [Fact]
    public void RemoveAndClearPreventDeferredHelloReplay()
    {
        var mailbox = new SignalingHelloMailbox(capacity: 4, ttlMs: 1000);
        var scope = new SignalingSessionScope(55, 7);
        Assert.True(mailbox.Store(scope, 11, 11, new byte[] { 11 }, 100, out _));
        Assert.True(mailbox.Store(scope, 12, 12, new byte[] { 12 }, 100, out _));

        Assert.True(mailbox.Remove(scope, 11));
        Assert.False(mailbox.Remove(scope, 11));
        Assert.Equal(1, mailbox.Count);
        mailbox.Clear();
        Assert.Equal(0, mailbox.Count);
    }
}
