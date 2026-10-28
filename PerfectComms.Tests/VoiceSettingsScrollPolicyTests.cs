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
    public void SpringScrollFeelsTheSameAcrossFrameRates()
    {
        float sixtyFps = 0f, sixtyVelocity = 0f;
        for (int i = 0; i < 60; i++)
            sixtyFps = VoiceSettingsScrollPolicy.Follow(sixtyFps, ref sixtyVelocity, 100f, 1f / 60f, response: 2f);

        float tenFps = 0f, tenVelocity = 0f;
        for (int i = 0; i < 10; i++)
            tenFps = VoiceSettingsScrollPolicy.Follow(tenFps, ref tenVelocity, 100f, 0.1f, response: 2f);

        Assert.Equal(sixtyFps, tenFps, 2);
        Assert.Equal(sixtyVelocity, tenVelocity, 2);
        Assert.InRange(sixtyFps, 59.3f, 59.5f);
    }

    [Fact]
    public void SpringNeverOvershootsFromRestAndSettlesExactly()
    {
        float position = 0f, velocity = 0f, previous = 0f;
        for (int i = 0; i < 120; i++)
        {
            position = VoiceSettingsScrollPolicy.Follow(position, ref velocity, 100f, 1f / 60f);
            Assert.InRange(position, previous, 100f);
            previous = position;
        }
        Assert.Equal(100f, position);
        Assert.Equal(0f, velocity);

        velocity = 0f;
        Assert.Equal(20f, VoiceSettingsScrollPolicy.Follow(20f, ref velocity, 100f, 0f));
        Assert.Equal(100f, VoiceSettingsScrollPolicy.Follow(20f, ref velocity, 100f, 10f));
        Assert.Equal(0f, velocity);
    }

    [Fact]
    public void ScrollerKeepsMomentumAcrossWheelNotchesButJumpsOnDirectWrites()
    {
        var scroller = new VoiceSettingsScrollPolicy.SmoothScroller();
        float scroll = 0f;
        scroller.Scroll(scroll, 80f, 1000f);
        scroll = scroller.Step(scroll, 1000f, 1f / 60f);
        float firstStep = scroll;
        scroller.Scroll(scroll, 80f, 1000f);
        Assert.Equal(160f, scroller.Target);
        scroll = scroller.Step(scroll, 1000f, 1f / 60f);
        Assert.True(scroll - firstStep > firstStep, "the second notch should build on the first notch's speed");

        scroll = 400f;
        Assert.Equal(400f, scroller.Step(scroll, 1000f, 1f / 60f));
        Assert.Equal(400f, scroller.Target);

        scroller.Scroll(400f, 10000f, 500f);
        Assert.Equal(500f, scroller.Target);
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
