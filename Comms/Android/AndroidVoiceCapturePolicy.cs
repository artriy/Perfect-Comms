#if ANDROID
namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android uses Unity capture with managed Starlight and does not expose the desktop APM/DSP path.
/// Normalize unsupported desktop flags before they reach diagnostics or the managed backend.
/// </summary>
internal static class AndroidVoiceCapturePolicy
{
    internal static VoiceCaptureRuntimeOptions Normalize(VoiceCaptureRuntimeOptions options)
        => options with
        {
            NoiseSuppressionEnabled = false,
            StrongerNoiseSuppressionEnabled = false,
            EchoCancellationEnabled = false,
        };
}
#endif
