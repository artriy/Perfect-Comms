using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceVolumeMathTests
{
    private static VoiceProximityResult Audible(float normal = 1f)
        => new(normal, 0f, 0f, 0f, VoiceAudioFilterMode.None, true,
            VoiceProximityReason.MeetingLiving, 1f);

    [Theory]
    [InlineData(1, false, 0.25f)]
    [InlineData(1, true, 1.75f)]
    [InlineData(2, false, 1.25f)]
    [InlineData(2, true, 0f)]
    public void EachHeldFocusSelectsItsOwnIndependentProfile(
        int focusValue,
        bool targetDead,
        float expected)
    {
        var focus = (VoiceAliveDeadMixFocus)focusValue;
        var aliveFocus = new VoiceAliveDeadMixProfile(0.25f, 1.75f);
        var deadFocus = new VoiceAliveDeadMixProfile(1.25f, 0f);

        Assert.Equal(expected, VoiceVolumeMath.SelectGroupVolume(
            focus, targetDead, aliveFocus, deadFocus));
    }

    [Fact]
    public void DefaultProfilesMatchTheTwoAdvertisedHoldMixes()
    {
        Assert.Equal(2f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Alive, false,
            VoiceVolumeMath.DefaultAliveFocusProfile,
            VoiceVolumeMath.DefaultDeadFocusProfile));
        Assert.Equal(0.5f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Alive, true,
            VoiceVolumeMath.DefaultAliveFocusProfile,
            VoiceVolumeMath.DefaultDeadFocusProfile));
        Assert.Equal(0.5f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Dead, false,
            VoiceVolumeMath.DefaultAliveFocusProfile,
            VoiceVolumeMath.DefaultDeadFocusProfile));
        Assert.Equal(2f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Dead, true,
            VoiceVolumeMath.DefaultAliveFocusProfile,
            VoiceVolumeMath.DefaultDeadFocusProfile));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(99, false)]
    [InlineData(99, true)]
    public void NeutralInvalidAndUnknownTargetsUseTheNormalMix(
        int focusValue,
        bool targetDead)
    {
        var focus = (VoiceAliveDeadMixFocus)focusValue;
        var aliveFocus = new VoiceAliveDeadMixProfile(0f, 2f);
        var deadFocus = new VoiceAliveDeadMixProfile(2f, 0f);

        Assert.Equal(1f, VoiceVolumeMath.SelectGroupVolume(
            focus, targetDead, aliveFocus, deadFocus));
        Assert.Equal(1f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Alive, null, aliveFocus, deadFocus));
        Assert.Equal(1f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Dead, null, aliveFocus, deadFocus));
    }

    [Fact]
    public void SelectedProfileValuesAreNormalizedBeforePlayback()
    {
        var aliveFocus = new VoiceAliveDeadMixProfile(float.NaN, -1f);
        var deadFocus = new VoiceAliveDeadMixProfile(float.PositiveInfinity, 99f);

        Assert.Equal(1f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Alive, false, aliveFocus, deadFocus));
        Assert.Equal(0f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Alive, true, aliveFocus, deadFocus));
        Assert.Equal(1f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Dead, false, aliveFocus, deadFocus));
        Assert.Equal(2f, VoiceVolumeMath.SelectGroupVolume(
            VoiceAliveDeadMixFocus.Dead, true, aliveFocus, deadFocus));
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    public void MixFocusExistsOnlyWhileExactlyOneBindingIsHeld(
        bool aliveLouderHeld,
        bool deadLouderHeld,
        int expectedValue)
    {
        var expected = (VoiceAliveDeadMixFocus)expectedValue;
        Assert.Equal(expected,
            VoiceVolumeMath.ResolveAliveDeadMixFocus(aliveLouderHeld, deadLouderHeld));
    }

    [Fact]
    public void GroupAndPerPlayerBoostsComposeWithoutRevivingMutedRoutes()
    {
        Assert.Equal(4f, VoiceVolumeMath.ResolvePeerGain(Audible(), 2f, 2f));
        Assert.Equal(0.5f, VoiceVolumeMath.ResolvePeerGain(Audible(0.5f), 1f, 1f));
        Assert.Equal(0f, VoiceVolumeMath.ResolvePeerGain(
            VoiceProximityResult.Muted(VoiceProximityReason.TargetDeadMuted), 2f, 2f));
    }

    [Fact]
    public void InvalidVolumeValuesCannotReachTheNativeMixer()
    {
        Assert.Equal(1f, VoiceVolumeMath.NormalizeUserVolume(float.NaN));
        Assert.Equal(0f, VoiceVolumeMath.NormalizeUserVolume(-1f));
        Assert.Equal(2f, VoiceVolumeMath.NormalizeUserVolume(99f));
        Assert.Equal(1f, VoiceVolumeMath.ResolvePeerGain(Audible(), float.NaN, float.PositiveInfinity));
        Assert.Equal(0f, VoiceVolumeMath.ResolvePeerGain(Audible(float.NaN), 2f, 2f));
    }
}
