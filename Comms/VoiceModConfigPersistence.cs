using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Keeps third-party option persistence independent of the IL2CPP BasePlugin type so the API
/// registry remains usable by tooling and tests that load only BepInEx.Core.
/// </summary>
internal static class VoiceModConfigPersistence
{
    internal static ConfigFile? Config { get; set; }
}
