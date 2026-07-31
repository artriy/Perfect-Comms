using BepInEx;
using BepInEx.Configuration;
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

    [Theory]
    [InlineData((int)KeyCode.RightAlt, true)]
    [InlineData((int)KeyCode.RightControl, true)]
    [InlineData((int)KeyCode.Mouse3, true)]
    [InlineData((int)KeyCode.Mouse4, true)]
    [InlineData((int)KeyCode.Mouse5, true)]
    [InlineData((int)KeyCode.Mouse6, true)]
    [InlineData((int)KeyCode.A, false)]
    [InlineData((int)KeyCode.Alpha1, false)]
    [InlineData((int)KeyCode.Space, false)]
    [InlineData((int)KeyCode.Mouse0, false)]
    [InlineData((int)KeyCode.Mouse1, false)]
    [InlineData((int)KeyCode.Mouse2, false)]
    public void NewBindingsRecommendChatPermissionFromTheirPrimaryKey(
        int keyValue,
        bool expected)
    {
        WithTemporaryConfig((config, _) =>
        {
            var binding = new VoiceKeybind(
                config,
                "Keybinds",
                "Chat Permission Defaults",
                (KeyCode)keyValue);

            Assert.Equal(expected, binding.AllowWhileChatOpen);
        });
    }

    [Fact]
    public void ManualChatPermissionChoicePersistsUntilThePrimaryIsRebound()
    {
        WithTemporaryConfig((config, path) =>
        {
            var binding = new VoiceKeybind(
                config,
                "Keybinds",
                "Manual Chat Permission",
                KeyCode.RightAlt);
            Assert.True(binding.AllowWhileChatOpen);

            binding.SetAllowWhileChatOpen(false);
            config.Save();

            var reloadedConfig = new ConfigFile(path, saveOnInit: false, Metadata);
            var reloadedBinding = new VoiceKeybind(
                reloadedConfig,
                "Keybinds",
                "Manual Chat Permission",
                KeyCode.RightAlt);
            Assert.False(reloadedBinding.AllowWhileChatOpen);

            reloadedBinding.Set(KeyCode.RightAlt);
            Assert.True(reloadedBinding.AllowWhileChatOpen);
        });
    }

    [Fact]
    public void RebindingRecalculatesChatPermissionFromTheNewPrimary()
    {
        WithTemporaryConfig((config, _) =>
        {
            var binding = new VoiceKeybind(
                config,
                "Keybinds",
                "Recalculated Chat Permission",
                KeyCode.RightAlt);

            binding.SetBinding(
                KeyCode.A,
                KeyCode.LeftShift,
                VoiceModifierMatch.EitherSide);
            Assert.False(binding.AllowWhileChatOpen);

            binding.SetBinding(
                KeyCode.Mouse4,
                KeyCode.LeftControl,
                VoiceModifierMatch.Exact);
            Assert.True(binding.AllowWhileChatOpen);
        });
    }

    [Theory]
    [InlineData((int)KeyCode.RightControl, false)]
    [InlineData((int)KeyCode.A, true)]
    public void ModifierOnlyChangesPreserveManualChatPermission(
        int keyValue,
        bool manualPermission)
    {
        WithTemporaryConfig((config, _) =>
        {
            var binding = new VoiceKeybind(
                config,
                "Keybinds",
                "Modifier-Only Chat Permission",
                (KeyCode)keyValue);
            binding.SetAllowWhileChatOpen(manualPermission);

            binding.SetModifier(
                KeyCode.LeftShift,
                VoiceModifierMatch.EitherSide);

            Assert.Equal(manualPermission, binding.AllowWhileChatOpen);
        });
    }

    [Fact]
    public void ClearUnchecksChatPermission()
    {
        WithTemporaryConfig((config, _) =>
        {
            var binding = new VoiceKeybind(
                config,
                "Keybinds",
                "Cleared Chat Permission",
                KeyCode.Mouse3);
            Assert.True(binding.AllowWhileChatOpen);

            binding.Clear();

            Assert.Equal(KeyCode.None, binding.Value);
            Assert.Equal(KeyCode.None, binding.Modifier);
            Assert.Equal(VoiceModifierMatch.Exact, binding.ModifierMatch);
            Assert.False(binding.AllowWhileChatOpen);
        });
    }

    [Fact]
    public void RightModifierMigrationReplacesCustomBindingsOnlyOnce()
    {
        WithTemporaryConfig((config, path) =>
        {
            SetPersistedBinding(
                config,
                "Mute / Unmute Mic",
                KeyCode.M,
                KeyCode.LeftAlt,
                VoiceModifierMatch.EitherSide,
                allowWhileChatOpen: false);
            SetPersistedBinding(
                config,
                "Toggle Speaker",
                KeyCode.D,
                KeyCode.RightShift,
                VoiceModifierMatch.EitherSide,
                allowWhileChatOpen: false);

            VoiceChatKeybinds.Initialize(config);

            AssertBinding(
                VoiceChatKeybinds.ToggleMute,
                KeyCode.RightAlt,
                KeyCode.None,
                VoiceModifierMatch.Exact,
                allowWhileChatOpen: true);
            AssertBinding(
                VoiceChatKeybinds.ToggleSpeaker,
                KeyCode.RightControl,
                KeyCode.None,
                VoiceModifierMatch.Exact,
                allowWhileChatOpen: true);

            VoiceChatKeybinds.ToggleMute.SetBinding(
                KeyCode.A,
                KeyCode.LeftShift,
                VoiceModifierMatch.EitherSide);
            VoiceChatKeybinds.ToggleSpeaker.SetBinding(
                KeyCode.Mouse5,
                KeyCode.LeftAlt,
                VoiceModifierMatch.EitherSide);
            config.Save();

            var reloadedConfig = new ConfigFile(path, saveOnInit: false, Metadata);
            VoiceChatKeybinds.Initialize(reloadedConfig);

            AssertBinding(
                VoiceChatKeybinds.ToggleMute,
                KeyCode.A,
                KeyCode.LeftShift,
                VoiceModifierMatch.EitherSide,
                allowWhileChatOpen: false);
            AssertBinding(
                VoiceChatKeybinds.ToggleSpeaker,
                KeyCode.Mouse5,
                KeyCode.LeftAlt,
                VoiceModifierMatch.EitherSide,
                allowWhileChatOpen: true);
        });
    }

    [Fact]
    public void ReloadedDeafenBindingUsesTheLegacyPersistedConfigKey()
    {
        WithTemporaryConfig((config, path) =>
        {
            MarkRightModifierMigrationComplete(config);
            SetPersistedBinding(
                config,
                "Toggle Speaker",
                KeyCode.K,
                KeyCode.LeftAlt,
                VoiceModifierMatch.Exact,
                allowWhileChatOpen: true);
            SetPersistedBinding(
                config,
                "Toggle Deafen",
                KeyCode.L,
                KeyCode.None,
                VoiceModifierMatch.Exact,
                allowWhileChatOpen: false);
            config.Save();

            var reloadedConfig = new ConfigFile(path, saveOnInit: false, Metadata);
            VoiceChatKeybinds.Initialize(reloadedConfig);

            AssertBinding(
                VoiceChatKeybinds.ToggleSpeaker,
                KeyCode.K,
                KeyCode.LeftAlt,
                VoiceModifierMatch.Exact,
                allowWhileChatOpen: true);
        });
    }

    [Fact]
    public void ReloadedPushToMuteBindingUsesTheLegacyPersistedConfigKey()
    {
        WithTemporaryConfig((config, path) =>
        {
            MarkRightModifierMigrationComplete(config);
            SetPersistedBinding(
                config,
                "Hold To Mute",
                KeyCode.Mouse6,
                KeyCode.RightShift,
                VoiceModifierMatch.EitherSide,
                allowWhileChatOpen: false);
            SetPersistedBinding(
                config,
                "Push To Mute",
                KeyCode.P,
                KeyCode.None,
                VoiceModifierMatch.Exact,
                allowWhileChatOpen: true);
            config.Save();

            var reloadedConfig = new ConfigFile(path, saveOnInit: false, Metadata);
            VoiceChatKeybinds.Initialize(reloadedConfig);

            AssertBinding(
                VoiceChatKeybinds.PushToMute,
                KeyCode.Mouse6,
                KeyCode.RightShift,
                VoiceModifierMatch.EitherSide,
                allowWhileChatOpen: false);
        });
    }

    private static void MarkRightModifierMigrationComplete(ConfigFile config)
        => config.Bind(
            "Keybinds",
            "RightModifierDefaultsMigrated",
            false).Value = true;

    private static readonly BepInPlugin Metadata = new(
        "com.edgetel.perfectcomms.keybind-tests",
        "Perfect Comms Keybind Tests",
        "1.0.0");

    private static void WithTemporaryConfig(Action<ConfigFile, string> test)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"PerfectComms-KeybindTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "keybinds.cfg");
            test(new ConfigFile(path, saveOnInit: false, Metadata), path);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static void SetPersistedBinding(
        ConfigFile config,
        string configKey,
        KeyCode primary,
        KeyCode modifier,
        VoiceModifierMatch modifierMatch,
        bool allowWhileChatOpen)
    {
        const string section = "Keybinds";
        config.Bind(section, configKey, KeyCode.None).Value = primary;
        config.Bind(section, configKey + " Modifier", KeyCode.None).Value = modifier;
        config.Bind(
            section,
            configKey + " Modifier Match",
            VoiceModifierMatch.Exact).Value = modifierMatch;
        config.Bind(
            section,
            configKey + " Allow While Chat Open",
            false).Value = allowWhileChatOpen;
    }

    private static void AssertBinding(
        VoiceKeybind binding,
        KeyCode primary,
        KeyCode modifier,
        VoiceModifierMatch modifierMatch,
        bool allowWhileChatOpen)
    {
        Assert.Equal(primary, binding.Value);
        Assert.Equal(modifier, binding.Modifier);
        Assert.Equal(modifierMatch, binding.ModifierMatch);
        Assert.Equal(allowWhileChatOpen, binding.AllowWhileChatOpen);
    }
}
