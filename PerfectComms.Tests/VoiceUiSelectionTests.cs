using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceUiSelectionTests
{
    private static readonly (string ModId, string Label)[] ThreeMods =
        { ("alpha", "Alpha"), ("beta", "Beta"), ("gamma", "Gamma") };

    [Fact]
    public void HostTabSelectionFollowsItsModWhenAnEarlierTabIsRemoved()
    {
        string key = HostSettingsPanel.CategoryKey(6, ThreeMods);
        (string, string)[] withoutAlpha = { ("beta", "Beta"), ("gamma", "Gamma") };

        Assert.Equal(5, HostSettingsPanel.ResolveCategoryIndex(key, withoutAlpha));
    }

    [Fact]
    public void HostTabSelectionSurvivesTabsAddedBeforeItAndLabelChanges()
    {
        string key = HostSettingsPanel.CategoryKey(4, ThreeMods);
        (string, string)[] grown = { ("zeta", "Zeta"), ("alpha", "Alpha renamed"), ("beta", "Beta") };

        Assert.Equal(5, HostSettingsPanel.ResolveCategoryIndex(key, grown));
    }

    [Fact]
    public void HostTabSelectionIsLostOnlyWhenItsOwnTabIsRemoved()
    {
        string removed = HostSettingsPanel.CategoryKey(5, ThreeMods);
        string builtIn = HostSettingsPanel.CategoryKey(2, ThreeMods);
        (string, string)[] withoutBeta = { ("alpha", "Alpha"), ("gamma", "Gamma") };

        Assert.Equal(-1, HostSettingsPanel.ResolveCategoryIndex(removed, withoutBeta));
        Assert.Equal(2, HostSettingsPanel.ResolveCategoryIndex(builtIn, withoutBeta));
        Assert.Equal(-1, HostSettingsPanel.ResolveCategoryIndex(null, withoutBeta));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void PlayerVolumeDragKeepsFollowingThePointerOutsideTheList(bool insideClip, bool dragActive, bool expected)
    {
        Assert.Equal(expected, VoiceVolumeMenu.PointerReachesList(insideClip, dragActive));
    }
}
