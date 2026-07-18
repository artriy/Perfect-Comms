using Xunit;

namespace VoiceChatPlugin.VoiceChat.Tests;

public sealed class MicrophoneTestLifecyclePolicyTests
{
    [Fact]
    public void MicrophoneTestOnlyRemainsActiveOnDevicesTab()
    {
        Assert.True(MicrophoneTestLifecyclePolicy.ShouldDisableForCategory(VoiceSettingsCategory.Audio));
        Assert.False(MicrophoneTestLifecyclePolicy.ShouldDisableForCategory(VoiceSettingsCategory.Devices));
        Assert.True(MicrophoneTestLifecyclePolicy.ShouldDisableForCategory(VoiceSettingsCategory.Keybinds));
        Assert.True(MicrophoneTestLifecyclePolicy.ShouldDisableForCategory(VoiceSettingsCategory.Hud));
        Assert.True(MicrophoneTestLifecyclePolicy.ShouldDisableForCategory(VoiceSettingsCategory.Advanced));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public void RoomMonitorRequestsPermissionBeforeUsingLiveCapture(
        bool monitorPlayback,
        bool roomPresent,
        bool permissionGranted,
        bool expected)
    {
        Assert.Equal(
            expected,
            MicrophoneTestLifecyclePolicy.RequiresRoomMicrophonePermission(
                monitorPlayback,
                roomPresent,
                permissionGranted));
    }
}
