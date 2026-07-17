#if ANDROID
namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android-only settings layout and user-facing touch guidance. Keeping this policy out of the
/// shared panels prevents desktop-only controls from leaking into the mobile experience.
/// </summary>
internal static class AndroidVoiceUiPolicy
{
    internal static readonly string[] SettingsCategories =
        { "AUDIO", "DEVICES", "HUD", "ADVANCED" };

    internal static readonly VoiceSettingsCategory[] SettingsCategoryOrder =
    {
        VoiceSettingsCategory.Audio,
        VoiceSettingsCategory.Devices,
        VoiceSettingsCategory.Hud,
        VoiceSettingsCategory.Advanced,
    };

    internal const string MicrophoneControlHelp =
        "Open Mic: tap to mute. Push To Talk: hold the mic to speak.";

    internal const string ControlsJourneyHelp =
        "Talk mode, touch controls, and startup behavior.";

    internal const string TeamRadioControlHelp =
        "When the radio button appears, tap to change channel or hold to transmit.";

    internal static string MicModeHelp(bool pushToTalk)
        => pushToTalk
            ? "Hold the microphone button while you speak.\nRelease it to stop transmitting."
            : "Open Mic sends speech automatically.\nTap the microphone button to mute.";

    internal static string MicrophoneTooltipAction(bool pushToTalk, bool radioVisible)
    {
        string microphoneAction = pushToTalk ? "Hold mic: talk" : "Tap mic: mute";
        return radioVisible
            ? microphoneAction + "  |  Radio: tap to change channel / hold to talk"
            : microphoneAction;
    }

    internal const string SpeakerTooltipAction =
        "Tap the speaker button to mute or unmute incoming voice.";
}
#endif
