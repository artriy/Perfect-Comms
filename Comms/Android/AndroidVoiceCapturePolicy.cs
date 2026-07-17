#if ANDROID
namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android uses Unity capture plus pc-mobile and has no desktop APM/DSP path. Normalize ignored
/// desktop flags before they reach diagnostics or the mobile backend.
/// </summary>
internal static class AndroidVoiceCapturePolicy
{
    internal static VoiceCaptureRuntimeOptions Normalize(VoiceCaptureRuntimeOptions options)
        => options with
        {
            NoiseSuppressionEnabled = false,
            EchoCancellationEnabled = false,
        };
}
#endif
