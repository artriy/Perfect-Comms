using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SpeakingBarGhostAppearancePolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AliveAlwaysUsesOpaqueLivingArtwork(bool vanillaGhostAvailable)
    {
        var appearance = SpeakingBarGhostAppearancePolicy.Resolve(
            publiclyDead: false,
            vanillaGhostAvailable);

        Assert.Equal(SpeakingBarGhostBodyArt.Alive, appearance.BodyArt);
        Assert.Equal(1f, appearance.Alpha);
    }

    [Fact]
    public void PublicGhostUsesVanillaArtworkWhenAvailable()
    {
        var appearance = SpeakingBarGhostAppearancePolicy.Resolve(
            publiclyDead: true,
            vanillaGhostAvailable: true);

        Assert.Equal(SpeakingBarGhostBodyArt.VanillaGhost, appearance.BodyArt);
        Assert.Equal(SpeakingBarGhostAppearancePolicy.GhostAlpha, appearance.Alpha);
    }

    [Fact]
    public void PublicGhostKeepsFadedAliveFallbackWhenArtworkIsUnavailable()
    {
        var appearance = SpeakingBarGhostAppearancePolicy.Resolve(
            publiclyDead: true,
            vanillaGhostAvailable: false);

        Assert.Equal(SpeakingBarGhostBodyArt.FadedAliveFallback, appearance.BodyArt);
        Assert.Equal(SpeakingBarGhostAppearancePolicy.GhostAlpha, appearance.Alpha);
    }

    [Fact]
    public void TaskDeathStaysLivingUntilTheMeetingPublishesIt()
    {
        var taskSnapshot = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            new HashSet<byte>(),
            VoiceGamePhase.Tasks,
            previousMeetingActive: false,
            VoiceGamePhase.Tasks,
            currentMeetingActive: false,
            new[] { new SpeakingBarRosterMember(2, IsDead: true) });

        var beforePublication = SpeakingBarGhostAppearancePolicy.Resolve(
            taskSnapshot.Contains(2),
            vanillaGhostAvailable: true);
        Assert.Equal(SpeakingBarGhostBodyArt.Alive, beforePublication.BodyArt);

        var meetingSnapshot = SpeakingBarRosterPolicy.NextPubliclyDeadSnapshot(
            taskSnapshot,
            VoiceGamePhase.Tasks,
            previousMeetingActive: false,
            VoiceGamePhase.Meeting,
            currentMeetingActive: true,
            new[] { new SpeakingBarRosterMember(2, IsDead: true) });

        var afterPublication = SpeakingBarGhostAppearancePolicy.Resolve(
            meetingSnapshot.Contains(2),
            vanillaGhostAvailable: true);
        Assert.Equal(SpeakingBarGhostBodyArt.VanillaGhost, afterPublication.BodyArt);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void RefreshesOnlyForInitialApplicationOrAStateTransition(
        bool hasAppliedRequest,
        bool appliedPubliclyDead,
        bool requestedPubliclyDead,
        bool expected)
    {
        Assert.Equal(
            expected,
            SpeakingBarGhostAppearancePolicy.RequiresBodyRefresh(
                hasAppliedRequest,
                appliedPubliclyDead,
                requestedPubliclyDead));
    }
}
