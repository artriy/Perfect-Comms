using VoiceChatPlugin.VoiceChat;
using Xunit;

namespace PerfectComms.Tests;

public sealed class VoiceCompactStatusPolicyTests
{
    [Fact]
    public void HiddenWhenThereIsNoExistingStatusOrEnabledStateWarning()
    {
        Assert.Equal(string.Empty, VoiceCompactStatusPolicy.Compose(null, null, false, false, true));
    }

    [Fact]
    public void ExistingTransientKeepsItsCurrentMessageAndShowsDeafenStateBelowIt()
    {
        string text = VoiceCompactStatusPolicy.Compose(
            "Voice connection refreshed", null, false, true, true);

        Assert.Equal(
            "<color=#FFCC66>Voice connection refreshed</color>\n<color=#FF7373>Deafened</color>",
            text);
    }

    [Fact]
    public void OperationalWarningIsRetainedWithCombinedManualStates()
    {
        string text = VoiceCompactStatusPolicy.Compose(
            null, "caller has the floor (5s)", true, true, true);

        Assert.Equal(
            "<color=#FFCC66>caller has the floor (5s)</color>\n<color=#FF7373>Muted / Deafened</color>",
            text);
    }

    [Fact]
    public void ExistingOperationalWarningRemainsVisibleDuringTransientStatus()
    {
        string text = VoiceCompactStatusPolicy.Compose(
            "Voice connection refreshed", "caller has the floor (5s)", false, true, true);

        Assert.Equal(
            "<color=#FFCC66>Voice connection refreshed\ncaller has the floor (5s)</color>\n<color=#FF7373>Deafened</color>",
            text);
    }

    [Fact]
    public void MuteAndDeafenWarningsCanBeDisabledWithoutHidingExistingWarnings()
    {
        Assert.Equal(
            "<color=#FFCC66>voice connecting</color>",
            VoiceCompactStatusPolicy.Compose(null, "voice connecting", true, true, false));
        Assert.Equal(string.Empty, VoiceCompactStatusPolicy.Compose(null, null, true, true, false));
    }

    [Theory]
    [InlineData(true, false, "Muted")]
    [InlineData(false, true, "Deafened")]
    [InlineData(true, true, "Muted / Deafened")]
    public void StateWordingIsExplicit(bool muted, bool deafened, string expected)
    {
        Assert.Equal(expected, VoiceCompactStatusPolicy.StateText(muted, deafened));
    }
}
