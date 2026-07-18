namespace VoiceChatPlugin.VoiceChat;

internal static class MicrophoneTestLifecyclePolicy
{
    internal static bool ShouldDisableForCategory(VoiceSettingsCategory category)
        => category != VoiceSettingsCategory.Devices;

    internal static bool RequiresRoomMicrophonePermission(
        bool monitorPlayback,
        bool roomPresent,
        bool permissionGranted)
        => monitorPlayback && roomPresent && !permissionGranted;
}
