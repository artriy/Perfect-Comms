using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceVolumeMathTests
{
    private static VoiceProximityResult Audible(float normal = 1f)
        => new(normal, 0f, 0f, 0f, VoiceAudioFilterMode.None, true,
            VoiceProximityReason.MeetingLiving, 1f);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void GroupMixOnlyActivatesForADeadListenerInAMeeting(
        bool meetingActive,
        bool localDead,
        bool expected)
    {
        Assert.Equal(expected,
            VoiceVolumeMath.ShouldApplyDeadMeetingMix(meetingActive, localDead));
    }

    [Fact]
    public void AliveAndDeadGroupsAreSelectedIndependently()
    {
        Assert.Equal(0.5f, VoiceVolumeMath.SelectGroupVolume(true, false, 0.5f, 2f));
        Assert.Equal(2f, VoiceVolumeMath.SelectGroupVolume(true, true, 0.5f, 2f));
        Assert.Equal(1f, VoiceVolumeMath.SelectGroupVolume(false, true, 0.5f, 2f));
        Assert.Equal(1f, VoiceVolumeMath.SelectGroupVolume(true, null, 0.5f, 2f));
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
