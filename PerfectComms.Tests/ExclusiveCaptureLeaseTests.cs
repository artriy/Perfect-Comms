using VoiceChatPlugin.VoiceChat;
using Xunit;

namespace PerfectComms.Tests;

public sealed class ExclusiveCaptureLeaseTests
{
    [Fact]
    public void SecondOwnerCannotReplaceActiveCapture()
    {
        var lease = new ExclusiveCaptureLease();

        Assert.True(lease.TryAcquire(1));
        Assert.False(lease.TryAcquire(2));
        Assert.True(lease.IsOwnedBy(1));
        Assert.False(lease.IsOwnedBy(2));
    }

    [Fact]
    public void NonOwnerCannotReleaseActiveCapture()
    {
        var lease = new ExclusiveCaptureLease();
        Assert.True(lease.TryAcquire(10));

        Assert.False(lease.Release(11));
        Assert.True(lease.IsOwnedBy(10));
    }

    [Fact]
    public void ExplicitOwnerReleaseAllowsNextConsumer()
    {
        var lease = new ExclusiveCaptureLease();
        Assert.True(lease.TryAcquire(20));

        Assert.True(lease.Release(20));
        Assert.True(lease.TryAcquire(21));
        Assert.True(lease.IsOwnedBy(21));
    }

    [Fact]
    public void SameOwnerCanIdempotentlyAcquire()
    {
        var lease = new ExclusiveCaptureLease();

        Assert.True(lease.TryAcquire(30));
        Assert.True(lease.TryAcquire(30));
    }
}
