using VoiceChatPlugin.VoiceChat;
using Xunit;

namespace PerfectComms.Tests;

public sealed class VoiceHudGeometryTests
{
    [Theory]
    [InlineData(0.20f, 0.70f, 0.10f, 0.90f, 0f)]
    [InlineData(0.02f, 0.40f, 0.10f, 0.90f, 0.08f)]
    [InlineData(0.60f, 0.98f, 0.10f, 0.90f, -0.08f)]
    [InlineData(-0.20f, 1.40f, 0.10f, 0.90f, -0.10f)]
    public void BoundsShiftStaysInsideSafeIntervalOrCentersOversizeContent(
        float min,
        float max,
        float safeMin,
        float safeMax,
        float expected)
    {
        Assert.Equal(
            expected,
            VoiceChatHudState.CalculateViewportShift(min, max, safeMin, safeMax),
            precision: 4);
    }
}
