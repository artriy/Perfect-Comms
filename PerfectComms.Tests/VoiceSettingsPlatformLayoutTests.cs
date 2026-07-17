#if WINDOWS
using VoiceChatPlugin.VoiceChat;
using Xunit;

namespace PerfectComms.Tests;

public sealed class VoiceSettingsPlatformLayoutTests
{
    [Fact]
    public void DesktopSettingsKeepExistingCategoryOrder()
    {
        string[] expectedNames = { "AUDIO", "DEVICES", "KEYBINDS", "HUD", "ADVANCED" };
        VoiceSettingsCategory[] expectedOrder =
        {
            VoiceSettingsCategory.Audio,
            VoiceSettingsCategory.Devices,
            VoiceSettingsCategory.Keybinds,
            VoiceSettingsCategory.Hud,
            VoiceSettingsCategory.Advanced,
        };

        Assert.Equal(expectedNames.Length, VoiceSettingsPanel.CategoryCountForCurrentPlatform);
        for (int i = 0; i < expectedNames.Length; i++)
        {
            Assert.Equal(expectedNames[i], VoiceSettingsPanel.CategoryNameForCurrentPlatform(i));
            Assert.Equal(expectedOrder[i], VoiceSettingsPanel.CategoryForCurrentPlatform(i));
        }
    }
}
#endif
