namespace VoiceChatPlugin.VoiceChat;

internal static class SidecarVersion
{
    public const int Protocol = 1;

    public static bool IsCompatible(int helperProto) => helperProto == Protocol;

    public static bool ShouldReExtract(int helperProto) => !IsCompatible(helperProto);
}
