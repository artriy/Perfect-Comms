#if ANDROID
using VoiceChatPlugin.VoiceChat;
using Xunit;

namespace PerfectComms.Tests;

public sealed class AndroidTouchInputDecisionTests
{
    [Theory]
    [InlineData(true, 10f, 0f, true)]
    [InlineData(false, 10f, 10.35f, true)]
    [InlineData(false, 10.35f, 10.35f, true)]
    [InlineData(false, 10.36f, 10.35f, false)]
    [InlineData(false, 10f, 0f, false)]
    public void HandledTouchSuppressesOnlyItsSyntheticClick(
        bool touchStillTracked,
        float now,
        float suppressUntil,
        bool expected)
    {
        Assert.Equal(
            expected,
            VoiceChatHudState.ShouldSuppressHandledTouchClick(
                touchStillTracked,
                now,
                suppressUntil));
    }

    [Fact]
    public void SharedMicTooltipRefreshPreservesTheOwningControl()
    {
        Assert.Equal(
            VoiceChatHudState.SharedMicTooltipOwner.Radio,
            VoiceChatHudState.ResolveSharedMicTooltipRefreshOwner(
                tooltipActive: true,
                owner: VoiceChatHudState.SharedMicTooltipOwner.Radio));
        Assert.Equal(
            VoiceChatHudState.SharedMicTooltipOwner.Microphone,
            VoiceChatHudState.ResolveSharedMicTooltipRefreshOwner(
                tooltipActive: true,
                owner: VoiceChatHudState.SharedMicTooltipOwner.Microphone));
        Assert.Equal(
            VoiceChatHudState.SharedMicTooltipOwner.None,
            VoiceChatHudState.ResolveSharedMicTooltipRefreshOwner(
                tooltipActive: false,
                owner: VoiceChatHudState.SharedMicTooltipOwner.Radio));
    }
}
#endif
