using VoiceChatPlugin;
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

    [Theory]
    [InlineData(false, false, false, false, (int)SpeakingBarGhostCosmeticRefresh.None)]
    [InlineData(false, false, false, true, (int)SpeakingBarGhostCosmeticRefresh.Ghost)]
    [InlineData(false, true, false, true, (int)SpeakingBarGhostCosmeticRefresh.Ghost)]
    [InlineData(true, true, true, true, (int)SpeakingBarGhostCosmeticRefresh.Ghost)]
    [InlineData(false, true, true, true, (int)SpeakingBarGhostCosmeticRefresh.None)]
    [InlineData(false, true, true, false, (int)SpeakingBarGhostCosmeticRefresh.Living)]
    public void CosmeticRefreshTracksPublicGhostTransitionsAndRecreatedIcons(
        bool iconChanged,
        bool hasAppliedRequest,
        bool appliedPubliclyDead,
        bool requestedPubliclyDead,
        int expectedValue)
    {
        Assert.Equal(
            (SpeakingBarGhostCosmeticRefresh)expectedValue,
            SpeakingBarGhostAppearancePolicy.ResolveCosmeticRefresh(
                iconChanged,
                hasAppliedRequest,
                appliedPubliclyDead,
                requestedPubliclyDead));
    }

    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(false, true, false, true, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, false, true)]
    public void CosmeticVisibilitySwitchesWholeSetsAndSuppressesFallbackSkin(
        bool useGhostBody,
        bool hasExactGhostCosmetics,
        bool ghostLayer,
        bool skinLayer,
        bool expected)
    {
        Assert.Equal(
            expected,
            SpeakingBarGhostAppearancePolicy.ShouldShowCosmetic(
                useGhostBody,
                hasExactGhostCosmetics,
                ghostLayer,
                skinLayer));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 9)]
    [InlineData(-8, 3)]
    public void GhostSortingPreservesEverySourceRendererDelta(int bodyOrder, int cosmeticOrder)
    {
        int mappedBody = CrewmateAvatarRenderer.MapGhostSortingOrder(bodyOrder, bodyOrder);
        int mappedCosmetic = CrewmateAvatarRenderer.MapGhostSortingOrder(cosmeticOrder, bodyOrder);

        Assert.Equal(cosmeticOrder - bodyOrder, mappedCosmetic - mappedBody);
    }

    [Fact]
    public void LivingBackCosmeticsRenderBehindTheBody()
    {
        Assert.True(CrewmateAvatarRenderer.BackCosmeticOrder < CrewmateAvatarRenderer.BodyOrder);
    }

    [Theory]
    [InlineData(-1f, 1f)]
    [InlineData(-0.5f, 0.5f)]
    [InlineData(0.75f, 0.75f)]
    public void LivingCosmeticCaptureRemovesTheSourceFacingSign(float sourceScale, float expected)
    {
        Assert.Equal(expected, CrewmateAvatarRenderer.CanonicalCosmeticScaleX(sourceScale));
    }

    [Fact]
    public void VanillaGhostFallbackMatchesLivingHudFootprint()
    {
        Assert.Equal(1f, CrewmateAvatarRenderer.VanillaGhostFallbackScale);
        Assert.Equal(2f, CrewmateAvatarRenderer.GhostHudNormalizationScale);
    }
}
