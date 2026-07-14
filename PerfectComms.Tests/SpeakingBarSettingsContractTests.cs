using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SpeakingBarSettingsContractTests
{
    [Fact]
    public void AutoDoesNotRenumberLegacyExplicitNamePositions()
    {
        Assert.Equal(0, (int)SpeakingBarNamePosition.Bottom);
        Assert.Equal(1, (int)SpeakingBarNamePosition.Top);
        Assert.Equal(2, (int)SpeakingBarNamePosition.Left);
        Assert.Equal(3, (int)SpeakingBarNamePosition.Right);
        Assert.Equal(4, (int)SpeakingBarNamePosition.Auto);
    }

    [Fact]
    public void V4OneHundredPercentUsesLegacyNinetyPercentRenderedSize()
    {
        Assert.Equal(0.90f, SpeakingBarScalePolicy.ToRenderedScale(1.00f), 4);
    }

    [Fact]
    public void LegacyNinetyPercentMigratesToV4OneHundredPercent()
    {
        float migrated = SpeakingBarScalePolicy.MigrateLegacyScale(0.90f);

        Assert.Equal(1.00f, migrated, 4);
        Assert.Equal(0.90f, SpeakingBarScalePolicy.ToRenderedScale(migrated), 4);
    }

    [Fact]
    public void LegacyMaximumKeepsItsRenderedSizeAfterMigration()
    {
        float migrated = SpeakingBarScalePolicy.MigrateLegacyScale(2.00f);

        Assert.InRange(migrated, 2.2221f, 2.2223f);
        Assert.Equal(2.00f, SpeakingBarScalePolicy.ToRenderedScale(migrated), 4);
        Assert.True(migrated <= SpeakingBarScalePolicy.MaximumUserScale);
    }

    [Fact]
    public void FirstV4MigrationTurnsBackdropOnAndThenStopsReapplying()
    {
        SpeakingBarV4MigrationPlan first = SpeakingBarScalePolicy.PlanV4Migration(
            storedVersion: 0,
            hadLegacyScaleSetting: true,
            currentScale: 0.90f,
            currentBackdrop: false,
            currentNamePosition: SpeakingBarNamePosition.Bottom);

        Assert.True(first.ShouldApply);
        Assert.True(first.Backdrop);
        Assert.Equal(SpeakingBarNamePosition.Auto, first.NamePosition);
        Assert.Equal(1.00f, first.Scale, 4);
        Assert.Equal(SpeakingBarScalePolicy.CurrentSettingsVersion, first.TargetVersion);

        SpeakingBarV4MigrationPlan later = SpeakingBarScalePolicy.PlanV4Migration(
            storedVersion: first.TargetVersion,
            hadLegacyScaleSetting: true,
            currentScale: first.Scale,
            currentBackdrop: false,
            currentNamePosition: SpeakingBarNamePosition.Left);

        Assert.False(later.ShouldApply);
        Assert.False(later.Backdrop);
        Assert.Equal(SpeakingBarNamePosition.Left, later.NamePosition);
        Assert.Equal(first.Scale, later.Scale);
    }

    [Fact]
    public void FreshV4ConfigKeepsTheNewOneHundredPercentDefault()
    {
        SpeakingBarV4MigrationPlan plan = SpeakingBarScalePolicy.PlanV4Migration(
            storedVersion: 0,
            hadLegacyScaleSetting: false,
            currentScale: 1.00f,
            currentBackdrop: true,
            currentNamePosition: SpeakingBarNamePosition.Auto);

        Assert.True(plan.ShouldApply);
        Assert.Equal(1.00f, plan.Scale);
        Assert.True(plan.Backdrop);
    }

    [Fact]
    public void LegacyScaleDetectionReadsOnlyTheRequestedConfigEntry()
    {
        const string config = """
            [UI]
            # SpeakingBarScale = 0.75
            OverlayScale = 1.3
            SpeakingBarScale = 0.9

            [Other]
            SpeakingBarScale = 2.0
            """;

        Assert.True(SpeakingBarScalePolicy.ConfigTextContainsSetting(config, "UI", "SpeakingBarScale"));
        Assert.False(SpeakingBarScalePolicy.ConfigTextContainsSetting(config, "UI", "MissingSetting"));
        Assert.False(SpeakingBarScalePolicy.ConfigTextContainsSetting(config, "Audio", "SpeakingBarScale"));
    }
}
