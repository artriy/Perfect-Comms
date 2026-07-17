namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct TransientVoiceToggleState(
    bool SpeakingBarLivePreview,
    bool ShowFake15Players,
    bool DebugVoiceStats,
    bool MicCalibrationDiagnostics,
    bool SyntheticMicTone);

internal static class TransientVoiceTogglePolicy
{
    internal static TransientVoiceToggleState ResetForLaunch(
        TransientVoiceToggleState persisted)
    {
        _ = persisted;
        return new TransientVoiceToggleState(
            SpeakingBarLivePreview: false,
            ShowFake15Players: false,
            DebugVoiceStats: false,
            MicCalibrationDiagnostics: false,
            SyntheticMicTone: false);
    }
}
