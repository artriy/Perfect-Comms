using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SpeakingBarVisualMetricsTests
{
    [Theory]
    [InlineData(0.12f)]
    [InlineData(0.28f)]
    [InlineData(0.33f)]
    [InlineData(0.45f)]
    public void BottomNameClearsTheFollowingRing(float labelHeight)
    {
        float ringHalfHeight = SpeakingBarVisualMetrics.RingScale * 0.5f;
        float labelBottom = -SpeakingBarVisualMetrics.LabelOffset - labelHeight * 0.5f;
        float footprintHeight = ringHalfHeight - labelBottom;

        float pitch = SpeakingBarVisualMetrics.VerticalPitch(
            SpeakingBarNamePosition.Bottom, footprintHeight);
        float followingRingTop = -pitch + ringHalfHeight;

        Assert.True(labelBottom - followingRingTop >= SpeakingBarVisualMetrics.NameGap - 0.00001f);
    }

    [Theory]
    [InlineData(0.12f)]
    [InlineData(0.28f)]
    [InlineData(0.45f)]
    public void TopNameClearsThePrecedingRing(float labelHeight)
    {
        float ringHalfHeight = SpeakingBarVisualMetrics.RingScale * 0.5f;
        float labelTop = SpeakingBarVisualMetrics.LabelOffset + labelHeight * 0.5f;
        float footprintHeight = labelTop + ringHalfHeight;

        float pitch = SpeakingBarVisualMetrics.VerticalPitch(
            SpeakingBarNamePosition.Top, footprintHeight);
        float precedingRingBottom = pitch - ringHalfHeight;

        Assert.True(precedingRingBottom - labelTop >= SpeakingBarVisualMetrics.NameGap - 0.00001f);
    }
}
