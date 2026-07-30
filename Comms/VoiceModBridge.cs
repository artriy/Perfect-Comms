using PerfectComms.Api;

namespace VoiceChatPlugin.VoiceChat;

// Maps between the public PerfectComms.Api enums and the internal engine enums, in one place,
// so internal types stay free to change without touching the public API.
internal static class VoiceModBridge
{
    public static VoicePhaseKind ToApiPhase(VoiceGamePhase phase) => phase switch
    {
        VoiceGamePhase.Meeting => VoicePhaseKind.Meeting,
        VoiceGamePhase.Exile => VoicePhaseKind.Exile,
        VoiceGamePhase.Tasks => VoicePhaseKind.Tasks,
        _ => VoicePhaseKind.Lobby,
    };
}
