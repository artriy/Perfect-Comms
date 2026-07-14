using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceSettingsScrollPolicyTests
{
    [Fact]
    public void OverflowAndShrinkClampingUseTheVisibleViewport()
    {
        Assert.Equal(0f, VoiceSettingsScrollPolicy.MaxScroll(300f, 400f));
        Assert.Equal(600f, VoiceSettingsScrollPolicy.MaxScroll(1000f, 400f));
        Assert.Equal(250f, VoiceSettingsScrollPolicy.Clamp(500f, 250f));
        Assert.Equal(0f, VoiceSettingsScrollPolicy.Clamp(-10f, 250f));
    }

    [Fact]
    public void ExponentialScrollFeelsTheSameAcrossFrameRates()
    {
        float sixtyFps = 0f;
        for (int i = 0; i < 60; i++)
            sixtyFps = VoiceSettingsScrollPolicy.Advance(sixtyFps, 100f, 2f, 1f / 60f);

        float tenFps = 0f;
        for (int i = 0; i < 10; i++)
            tenFps = VoiceSettingsScrollPolicy.Advance(tenFps, 100f, 2f, 0.1f);

        Assert.Equal(sixtyFps, tenFps, 3);
        Assert.InRange(sixtyFps, 86.4f, 86.6f);
    }

    [Fact]
    public void ZeroTimeDoesNotMoveAndLargeFramesSettleWithoutOvershoot()
    {
        Assert.Equal(20f, VoiceSettingsScrollPolicy.Advance(20f, 100f, 18f, 0f));
        Assert.Equal(99.98f, VoiceSettingsScrollPolicy.Advance(99.98f, 100f, 18f, 0f));
        Assert.Equal(100f, VoiceSettingsScrollPolicy.Advance(20f, 100f, 18f, 10f));
        Assert.Equal(20f, VoiceSettingsScrollPolicy.Advance(20f, 100f, 0f, 1f));
        Assert.Equal(99.98f, VoiceSettingsScrollPolicy.Advance(99.98f, 100f, 0f, 1f));
    }

    [Fact]
    public void ThumbIsProportionalUntilItReachesItsMinimum()
    {
        Assert.Equal(200f, VoiceSettingsScrollPolicy.ThumbHeight(400f, 400f, 800f, 30f), 4);
        Assert.Equal(30f, VoiceSettingsScrollPolicy.ThumbHeight(400f, 400f, 10000f, 30f), 4);
        Assert.Equal(400f, VoiceSettingsScrollPolicy.ThumbHeight(400f, 400f, 300f, 30f), 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(150f)]
    [InlineData(300f)]
    [InlineData(600f)]
    public void ScrollAndThumbMappingsRoundTrip(float scroll)
    {
        float thumbTop = VoiceSettingsScrollPolicy.ThumbTopFromScroll(
            scroll, maxScroll: 600f, trackHeight: 400f, thumbHeight: 160f);
        float roundTrip = VoiceSettingsScrollPolicy.ScrollFromThumbTop(
            thumbTop, maxScroll: 600f, trackHeight: 400f, thumbHeight: 160f);

        Assert.Equal(scroll, roundTrip, 4);
    }

    [Fact]
    public void TrackAndThumbEndpointsClampAndZeroTravelIsSafe()
    {
        Assert.Equal(0f, VoiceSettingsScrollPolicy.ScrollFromThumbTop(
            -100f, 600f, 400f, 160f));
        Assert.Equal(600f, VoiceSettingsScrollPolicy.ScrollFromThumbTop(
            999f, 600f, 400f, 160f));
        Assert.Equal(0f, VoiceSettingsScrollPolicy.ScrollFromThumbTop(
            100f, 600f, 160f, 160f));
        Assert.Equal(0f, VoiceSettingsScrollPolicy.ThumbTopFromScroll(
            300f, 600f, 160f, 160f));
    }
}
