using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceKeybindTests
{
    [Theory]
    [InlineData((int)KeyCode.LeftShift, "Left Shift")]
    [InlineData((int)KeyCode.RightShift, "Right Shift")]
    [InlineData((int)KeyCode.LeftControl, "Left Ctrl")]
    [InlineData((int)KeyCode.RightControl, "Right Ctrl")]
    [InlineData((int)KeyCode.LeftAlt, "Left Alt")]
    [InlineData((int)KeyCode.RightAlt, "Right Alt")]
    public void ExactModifierNamesPreserveTheirSide(int keyValue, string expected)
    {
        var key = (KeyCode)keyValue;
        Assert.True(VoiceKeybind.IsModifierKey(key));
        Assert.Equal(expected, VoiceKeybind.FormatKey(key));
        Assert.Equal(expected, VoiceKeybind.FormatModifier(key, VoiceModifierMatch.Exact));
    }

    [Theory]
    [InlineData((int)KeyCode.LeftShift, "Shift")]
    [InlineData((int)KeyCode.RightShift, "Shift")]
    [InlineData((int)KeyCode.LeftControl, "Ctrl")]
    [InlineData((int)KeyCode.RightControl, "Ctrl")]
    public void LegacyEitherSideModifiersKeepGenericLabels(int keyValue, string expected)
    {
        Assert.Equal(expected,
            VoiceKeybind.FormatModifier((KeyCode)keyValue, VoiceModifierMatch.EitherSide));
    }

    [Theory]
    [InlineData((int)KeyCode.LeftShift, VoiceModifierMatch.EitherSide)]
    [InlineData((int)KeyCode.RightControl, VoiceModifierMatch.EitherSide)]
    [InlineData((int)KeyCode.LeftAlt, VoiceModifierMatch.EitherSide)]
    [InlineData((int)KeyCode.AltGr, VoiceModifierMatch.Exact)]
    [InlineData((int)KeyCode.LeftCommand, VoiceModifierMatch.Exact)]
    [InlineData((int)KeyCode.RightWindows, VoiceModifierMatch.Exact)]
    [InlineData((int)KeyCode.None, VoiceModifierMatch.Exact)]
    public void MissingMatchModePreservesLegacyModifierSemantics(
        int keyValue,
        VoiceModifierMatch expected)
    {
        Assert.Equal(expected, VoiceKeybind.LegacyModifierMatch((KeyCode)keyValue));
    }

    [Theory]
    [InlineData((int)KeyCode.LeftShift, VoiceModifierMatch.EitherSide, (int)KeyCode.RightShift, true)]
    [InlineData((int)KeyCode.LeftControl, VoiceModifierMatch.EitherSide, (int)KeyCode.RightControl, true)]
    [InlineData((int)KeyCode.LeftShift, VoiceModifierMatch.Exact, (int)KeyCode.RightShift, false)]
    [InlineData((int)KeyCode.RightAlt, VoiceModifierMatch.Exact, (int)KeyCode.RightAlt, true)]
    [InlineData((int)KeyCode.LeftCommand, VoiceModifierMatch.Exact, (int)KeyCode.RightCommand, false)]
    public void ChordArbitrationRespectsExactAndEitherSideModifiers(
        int configuredValue,
        VoiceModifierMatch match,
        int actualValue,
        bool expected)
    {
        Assert.Equal(expected, VoiceKeybind.ModifierMatchesKey(
            (KeyCode)configuredValue,
            match,
            (KeyCode)actualValue));
    }

    [Fact]
    public void ModifierPairsAndPlayerVolumeMigrationAreNarrowlyDefined()
    {
        Assert.True(VoiceKeybind.TryGetModifierPair(
            KeyCode.RightControl, out var left, out var right));
        Assert.Equal(KeyCode.LeftControl, left);
        Assert.Equal(KeyCode.RightControl, right);

        Assert.True(VoiceChatKeybinds.ShouldMigratePlayerVolumeDefault(KeyCode.B, KeyCode.None));
        Assert.False(VoiceChatKeybinds.ShouldMigratePlayerVolumeDefault(KeyCode.F, KeyCode.None));
        Assert.False(VoiceChatKeybinds.ShouldMigratePlayerVolumeDefault(KeyCode.B, KeyCode.LeftShift));
    }
}
