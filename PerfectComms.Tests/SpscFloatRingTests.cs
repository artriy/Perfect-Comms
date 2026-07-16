using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SpscFloatRingTests
{
    [Fact]
    public void PreservesOrderAcrossWrapWithoutLocksOrAllocation()
    {
        var ring = new SpscFloatRing(8, channels: 2);
        Assert.True(ring.TryWrite(new float[] { 1, 2, 3, 4, 5, 6 }));
        var first = new float[4];
        Assert.Equal(4, ring.Read(first));
        Assert.Equal(new float[] { 1, 2, 3, 4 }, first);
        Assert.True(ring.TryWrite(new float[] { 7, 8, 9, 10 }));
        var second = new float[6];
        Assert.Equal(6, ring.Read(second));
        Assert.Equal(new float[] { 5, 6, 7, 8, 9, 10 }, second);
    }

    [Fact]
    public void ProducerDropMakesConsumerDiscardStaleBacklogAndCrossfadeFreshAudio()
    {
        var ring = new SpscFloatRing(8, channels: 2, fadeFrames: 2);
        Assert.True(ring.TryWrite(new float[] { 1, 2, 3, 4, 5, 6 }));
        Assert.False(ring.TryWrite(new float[] { 7, 8, 9, 10 }));
        Assert.Equal(4, ring.DroppedSamples);
        var recoveredGap = new float[4];
        Assert.Equal(0, ring.Read(recoveredGap));
        Assert.Equal(new float[] { 0, 0, 0, 0 }, recoveredGap);
        Assert.Equal(6, ring.SkippedSamples);

        Assert.True(ring.TryWrite(new float[] { 1, -1, 1, -1, 1, -1 }));
        var fresh = new float[6];
        Assert.Equal(6, ring.Read(fresh));
        Assert.Equal(new float[] { 0, 0, 0.5f, -0.5f, 1, -1 }, fresh);
    }

    [Fact]
    public void ExcessDepthFastForwardsToNewestAlignedTargetAndCrossfades()
    {
        var ring = new SpscFloatRing(
            24,
            channels: 2,
            fadeFrames: 2,
            targetLatencySamples: 8);
        Assert.True(ring.TryWrite(new float[]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
            11, 12, 13, 14, 15, 16, 17, 18, 19, 20
        }));

        var output = new float[8];
        Assert.Equal(8, ring.Read(output));
        Assert.Equal(12, ring.SkippedSamples);
        Assert.Equal(new float[] { 0, 0, 7.5f, 8, 17, 18, 19, 20 }, output);
        Assert.Equal(0, ring.DepthSamples);
    }

    [Fact]
    public void UnderrunUsesChannelSafeFadeThenSilence()
    {
        var ring = new SpscFloatRing(8, channels: 2, fadeFrames: 2);
        Assert.True(ring.TryWrite(new float[] { 0.6f, -0.3f }));
        var output = new float[8];
        Assert.Equal(2, ring.Read(output));
        Assert.Equal(0.4f, output[2], 4);
        Assert.Equal(-0.2f, output[3], 4);
        Assert.Equal(0.2f, output[4], 4);
        Assert.Equal(-0.1f, output[5], 4);
        Assert.Equal(0f, output[6]);
        Assert.Equal(0f, output[7]);
        Assert.Equal(6, ring.ZeroFilledSamples);

        var next = new float[4];
        Assert.Equal(0, ring.Read(next));
        Assert.Equal(new float[] { 0, 0, 0, 0 }, next);
    }

    [Fact]
    public void ResumeCrossfadesEachChannelFromRenderedSilence()
    {
        var ring = new SpscFloatRing(16, channels: 2, fadeFrames: 2);
        Assert.True(ring.TryWrite(new float[] { 0.6f, -0.3f }));
        var starved = new float[8];
        Assert.Equal(2, ring.Read(starved));
        Assert.Equal(new float[] { 0.6f, -0.3f, 0.4f, -0.2f, 0.2f, -0.1f, 0f, 0f }, starved);

        Assert.True(ring.TryWrite(new float[] { 1f, -1f, 1f, -1f, 1f, -1f }));
        var resumed = new float[6];
        Assert.Equal(6, ring.Read(resumed));
        Assert.Equal(new float[] { 0f, 0f, 0.5f, -0.5f, 1f, -1f }, resumed);
    }

    [Fact]
    public void ResumeFadeCarriesAcrossPartialStereoCallbacks()
    {
        var ring = new SpscFloatRing(16, channels: 2, fadeFrames: 2);
        var silence = new float[4];
        Assert.Equal(0, ring.Read(silence));
        Assert.True(ring.TryWrite(new float[] { 0.8f, -0.4f, 0.8f, -0.4f, 0.8f, -0.4f }));

        var first = new float[2];
        var second = new float[2];
        var third = new float[2];
        Assert.Equal(2, ring.Read(first));
        Assert.Equal(2, ring.Read(second));
        Assert.Equal(2, ring.Read(third));
        Assert.Equal(new float[] { 0f, 0f }, first);
        Assert.Equal(new float[] { 0.4f, -0.2f }, second);
        Assert.Equal(new float[] { 0.8f, -0.4f }, third);
    }

    [Fact]
    public void PlaybackWaitsForConfiguredPrimeWithoutConsumingEarlyAudio()
    {
        var ring = new SpscFloatRing(
            24,
            channels: 2,
            targetLatencySamples: 8,
            primeLatencySamples: 8,
            maximumLatencySamples: 16,
            enableClockDriftCorrection: true);
        Assert.True(ring.TryWrite(new float[] { 1, -1, 2, -2, 3, -3 }));

        var held = new float[4];
        Assert.Equal(0, ring.Read(held));
        Assert.Equal(new float[] { 0, 0, 0, 0 }, held);
        Assert.Equal(6, ring.DepthSamples);
        Assert.Equal(4, ring.PrimingZeroFilledSamples);
        Assert.False(ring.IsPrimed);

        Assert.True(ring.TryWrite(new float[] { 4, -4 }));
        var started = new float[4];
        Assert.Equal(4, ring.Read(started));
        Assert.Equal(new float[] { 1, -1, 2, -2 }, started);
        Assert.True(ring.IsPrimed);
    }

    [Fact]
    public void ClockDriftCorrectionConsumesOnlyOneHalfPercentPerCallback()
    {
        const int outputSamples = 400; // 200 stereo frames => one-frame correction.
        var ring = new SpscFloatRing(
            2_000,
            channels: 2,
            targetLatencySamples: 800,
            primeLatencySamples: 0,
            maximumLatencySamples: 1_600,
            enableClockDriftCorrection: true);
        Assert.True(ring.TryWrite(Enumerable.Range(0, 1_000).Select(i => i / 1_000f).ToArray()));

        var output = new float[outputSamples];
        Assert.Equal(outputSamples, ring.Read(output));
        Assert.Equal(2, ring.ClockCorrectionSamples);
        Assert.Equal(1, ring.ClockCorrectionCallbacks);
        Assert.Equal(0, ring.SkippedSamples);
        Assert.Equal(598, ring.DepthSamples);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void SustainedSchedulerStallFastForwardsInsteadOfLingeringForSeconds()
    {
        const int outputSamples = 400;
        var ring = new SpscFloatRing(
            2_000,
            channels: 2,
            targetLatencySamples: 800,
            primeLatencySamples: 0,
            maximumLatencySamples: 1_600,
            enableClockDriftCorrection: true);
        Assert.True(ring.TryWrite(Enumerable.Range(0, 1_000).Select(i => i / 1_000f).ToArray()));

        // The first high-depth observation remains smooth and bounded to the 0.5% drift path.
        Assert.Equal(outputSamples, ring.Read(new float[outputSamples]));
        Assert.Equal(2, ring.ClockCorrectionSamples);
        Assert.Equal(0, ring.SkippedSamples);

        // Equal producer/consumer cadence keeps the one-frame scheduler excess present. The next
        // callback must classify it as a stall and recover immediately, not over ~5 seconds.
        Assert.True(ring.TryWrite(new float[outputSamples]));
        Assert.Equal(outputSamples, ring.Read(new float[outputSamples]));
        Assert.Equal(198, ring.SkippedSamples);
        Assert.Equal(400, ring.DepthSamples);
    }

    [Fact]
    public void ClockDriftCorrectionStretchesWhenDepthFallsBelowTarget()
    {
        const int outputSamples = 400;
        var ring = new SpscFloatRing(
            2_000,
            channels: 2,
            targetLatencySamples: 800,
            primeLatencySamples: 0,
            maximumLatencySamples: 1_600,
            enableClockDriftCorrection: true);
        Assert.True(ring.TryWrite(Enumerable.Range(0, outputSamples).Select(i => i / 400f).ToArray()));

        var output = new float[outputSamples];
        Assert.Equal(outputSamples, ring.Read(output));
        Assert.Equal(-2, ring.ClockCorrectionSamples);
        Assert.Equal(1, ring.ClockCorrectionCallbacks);
        Assert.Equal(2, ring.DepthSamples);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void ClearAfterWorkersStopRestoresUnprimedLifecycleState()
    {
        var ring = new SpscFloatRing(
            24,
            channels: 2,
            targetLatencySamples: 8,
            primeLatencySamples: 8,
            maximumLatencySamples: 16,
            enableClockDriftCorrection: true);
        Assert.True(ring.TryWrite(new float[] { 1, -1, 2, -2, 3, -3, 4, -4 }));
        Assert.Equal(4, ring.Read(new float[4]));
        Assert.True(ring.IsPrimed);

        ring.Clear();

        Assert.Equal(0, ring.DepthSamples);
        Assert.False(ring.IsPrimed);
        Assert.True(ring.TryWrite(new float[] { 5, -5, 6, -6 }));
        Assert.Equal(0, ring.Read(new float[4]));
        Assert.Equal(4, ring.DepthSamples);
    }
}
