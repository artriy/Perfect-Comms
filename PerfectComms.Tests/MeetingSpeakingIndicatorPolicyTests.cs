using VoiceChatPlugin;
using Xunit;

namespace PerfectComms.Tests;

public sealed class MeetingSpeakingIndicatorPolicyTests : IDisposable
{
    public MeetingSpeakingIndicatorPolicyTests()
    {
        MeetingSpeakingIndicatorPatch.ClearDestroyedMeetingState(static () => { });
    }

    public void Dispose()
    {
        MeetingSpeakingIndicatorPatch.ClearDestroyedMeetingState(static () => { });
    }

    [Fact]
    public void NullBackgroundUsesFallbackMode()
    {
        var mode = MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(1, hasBackground: false, hasSprite: false);
        Assert.False(MeetingSpeakingIndicatorPatch.UsesBackgroundMode(hasBackground: false, hasSprite: true));

        Assert.False(mode.UseBackground);
        Assert.False(mode.SwitchingFromFallback);
        Assert.False(mode.SwitchingToFallback);
        Assert.True(MeetingSpeakingIndicatorPatch.ManagedStateEntryCount > 0);
    }

    [Fact]
    public void NullSpriteSwitchesToBackgroundWhenSpriteBecomesReady()
    {
        var fallback = MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(2, hasBackground: true, hasSprite: false);
        var ready = MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(2, hasBackground: true, hasSprite: true);

        Assert.False(fallback.UseBackground);
        Assert.True(ready.UseBackground);
        Assert.True(ready.SwitchingFromFallback);
        Assert.False(ready.SwitchingToFallback);
    }

    [Fact]
    public void ReadyBackgroundStartsWithoutFallbackTransition()
    {
        var mode = MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(3, hasBackground: true, hasSprite: true);
        var unchanged = MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(3, hasBackground: true, hasSprite: true);

        Assert.True(mode.UseBackground);
        Assert.False(mode.SwitchingFromFallback);
        Assert.False(mode.SwitchingToFallback);
        Assert.False(unchanged.SwitchingFromFallback);
        Assert.False(unchanged.SwitchingToFallback);
    }

    [Fact]
    public void ReadyBackgroundCanFallBackAndReturnToBackground()
    {
        MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(5, hasBackground: true, hasSprite: true);
        var fallback = MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(5, hasBackground: true, hasSprite: false);
        var readyAgain = MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(5, hasBackground: true, hasSprite: true);

        Assert.False(fallback.UseBackground);
        Assert.True(fallback.SwitchingToFallback);
        Assert.True(readyAgain.UseBackground);
        Assert.True(readyAgain.SwitchingFromFallback);
        Assert.False(readyAgain.SwitchingToFallback);
    }

    [Fact]
    public void TeardownClearsManagedStateWhenVisualRestorationThrows()
    {
        MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(4, hasBackground: false, hasSprite: false);
        MeetingSpeakingIndicatorPatch.TrackTransformInitialization(4);
        bool vanillaHighlightRestored = false;

        var finalizerException = MeetingSpeakingIndicatorPatch.FinalizeDestroyedMeetingState(null, () =>
        {
            vanillaHighlightRestored = true;
            throw new InvalidOperationException("IL2CPP traversal failed");
        });

        Assert.True(vanillaHighlightRestored);
        Assert.Null(finalizerException);
        Assert.Equal(0, MeetingSpeakingIndicatorPatch.ManagedStateEntryCount);
        Assert.False(MeetingSpeakingIndicatorPatch.ResolveBackgroundMode(4, hasBackground: true, hasSprite: true).SwitchingFromFallback);
        Assert.True(MeetingSpeakingIndicatorPatch.TrackTransformInitialization(4));
    }
}
