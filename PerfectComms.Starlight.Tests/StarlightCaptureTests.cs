using VoiceChatPlugin.VoiceChat;
using Xunit;

namespace PerfectComms.Starlight.Tests;

public sealed class StarlightCaptureTests
{
    [Theory]
    [InlineData(44_100)]
    [InlineData(32_000)]
    [InlineData(16_000)]
    public void OneSecondAtFallbackRateProducesExactlyOneSecondAt48K(int sourceRate)
    {
        var resampler = new StarlightCaptureResampler(sourceRate);
        var source = new float[sourceRate];
        for (int i = 0; i < source.Length; i++)
            source[i] = 0.75f * MathF.Sin(2f * MathF.PI * 997f * i / sourceRate);

        int totalOutput = 0;
        int offset = 0;
        int[] chunkSizes = [1, 7, 83, 441, 960, 37, 1_333];
        int chunkIndex = 0;
        while (offset < source.Length)
        {
            int count = Math.Min(chunkSizes[chunkIndex++ % chunkSizes.Length], source.Length - offset);
            var output = new float[resampler.GetMaximumOutputSamples(count)];
            int written = resampler.Convert(source.AsSpan(offset, count), output);

            Assert.InRange(written, 1, output.Length);
            for (int i = 0; i < written; i++)
            {
                Assert.True(float.IsFinite(output[i]));
                Assert.InRange(output[i], -0.75f, 0.75f);
            }

            totalOutput += written;
            offset += count;
        }

        Assert.Equal(48_000, totalOutput);
    }

    [Fact]
    public void ResetDiscardsFractionalPhaseAndPriorSample()
    {
        var resumed = new StarlightCaptureResampler(44_100);
        var interrupted = new float[113];
        Array.Fill(interrupted, -1f);
        resumed.Convert(
            interrupted,
            new float[resumed.GetMaximumOutputSamples(interrupted.Length)]);
        resumed.Reset();

        float[] source = [0.25f, 0.5f, 0.75f, 1f];
        var resumedOutput = new float[resumed.GetMaximumOutputSamples(source.Length)];
        int resumedCount = resumed.Convert(source, resumedOutput);
        var fresh = new StarlightCaptureResampler(44_100);
        var freshOutput = new float[fresh.GetMaximumOutputSamples(source.Length)];
        int freshCount = fresh.Convert(source, freshOutput);

        Assert.Equal(freshCount, resumedCount);
        for (int i = 0; i < freshCount; i++)
            Assert.Equal(freshOutput[i], resumedOutput[i]);
    }

    [Theory]
    [InlineData(16, 480, 0)]
    [InlineData(100, 4_320, 0)]
    [InlineData(140, 5_760, 960)]
    [InlineData(250, 5_760, 6_240)]
    public void GapClassificationRequiresAMaterialElapsedSampleDeficit(
        int elapsedMilliseconds,
        int deliveredSamples,
        int expectedDroppedSamples)
    {
        int dropped = AndroidMicrophone.InferMaterialGapSamples(
            elapsedMilliseconds,
            1_000,
            48_000,
            deliveredSamples);

        Assert.Equal(expectedDroppedSamples, dropped);
    }

    [Fact]
    public void CombinedBoundedReadsPreventAFalsePollingGap()
    {
        int firstRead = 5_760;
        int secondRead = 480;

        int dropped = AndroidMicrophone.InferMaterialGapSamples(
            130,
            1_000,
            48_000,
            firstRead + secondRead);

        Assert.Equal(0, dropped);
    }

    [Fact]
    public void FallbackRateGapIsReportedIn48KOutputSamples()
    {
        int droppedSource = AndroidMicrophone.InferMaterialGapSamples(
            250,
            1_000,
            44_100,
            5_292);

        Assert.Equal(5_733, droppedSource);
        Assert.Equal(6_240, AndroidMicrophone.ScaleToOutputSamples(droppedSource, 44_100));
    }

    [Theory]
    [InlineData(true, true, true, false, 1)]
    [InlineData(false, true, true, false, 2)]
    [InlineData(true, false, false, true, 0)]
    [InlineData(false, true, false, false, 0)]
    [InlineData(true, true, true, true, 0)]
    public void PermissionCompletionOnlyChangesTheLiveUnmutedRoom(
        bool granted,
        bool roomAvailable,
        bool roomIsCurrent,
        bool roomIsMuted,
        int expected)
    {
        Assert.Equal(
            (MicrophonePermissionCompletionAction)expected,
            MicrophonePermissionDecisions.DecideCompletion(
                granted,
                roomAvailable,
                roomIsCurrent,
                roomIsMuted));
    }

    [Fact]
    public void DenialRestoresAStateThatCanRetryAndStartAfterGrant()
    {
        var denial = MicrophonePermissionDecisions.DecideCompletion(
            granted: false,
            roomAvailable: true,
            roomIsCurrent: true,
            roomIsMuted: false);
        Assert.Equal(MicrophonePermissionCompletionAction.RestoreMuted, denial);

        var laterGrant = MicrophonePermissionDecisions.DecideCompletion(
            granted: true,
            roomAvailable: true,
            roomIsCurrent: true,
            roomIsMuted: false);
        Assert.Equal(MicrophonePermissionCompletionAction.StartCapture, laterGrant);
    }

    [Fact]
    public void NonOwnerCannotReleaseOrReplaceAnActiveCaptureLease()
    {
        var lease = new ExclusiveCaptureLease();

        Assert.True(lease.TryAcquire(101));
        Assert.False(lease.TryAcquire(202));
        Assert.False(lease.Release(202));
        Assert.True(lease.IsOwnedBy(101));
    }
}
