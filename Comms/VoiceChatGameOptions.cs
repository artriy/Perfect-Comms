using MiraAPI.GameOptions;
using MiraAPI.GameOptions.OptionTypes;
using MiraAPI.Utilities;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceChatGameOptions : AbstractOptionGroup
{
    public override string GroupName => "Perfect Comms";

    public ModdedToggleOption PublicVoiceLobby { get; } = new("Public Voice Lobby", false);

    public ModdedEnumOption VoiceBackend { get; } = new("Voice Backend", (int)VoiceTransportBackend.BetterCrewLink,
        typeof(VoiceTransportBackend), ["BetterCrewLink", "Interstellar"]);

    public ModdedEnumOption LobbyBrowserBackend { get; } = new("Lobby Browser Backend", (int)VoiceLobbyBrowserSource.BetterCrewLink,
        typeof(VoiceLobbyBrowserSource), ["BCL Live", "Cloudflare (Limited)"]);

    public ModdedNumberOption MaxChatDistance { get; } =
        new("Max Distance", 6f, 1.5f, 20f, 0.5f, MiraNumberSuffixes.None, "0.0");

    public ModdedEnumOption FalloffMode { get; } = new("Voice Falloff", (int)VoiceFalloffMode.Smooth,
        typeof(VoiceFalloffMode), ["Linear", "Smooth", "Voice Focused"]);

    public ModdedEnumOption OcclusionMode { get; } = new("Voice Occlusion", (int)VoiceOcclusionMode.VisionOnly,
        typeof(VoiceOcclusionMode), ["Off", "Soft Muffle", "Soft Fade", "Hard Block", "Vision Only"]);

    public ModdedToggleOption WallsBlockSound     { get; } = new("Walls Block Audio",              true);
    public ModdedToggleOption OnlyHearInSight      { get; } = new("Hear People in Vision Only",     true);
    public ModdedToggleOption ImpostorHearGhosts   { get; } = new("Impostors Hear Dead",            false);
    public ModdedToggleOption HearInVent           { get; } = new("Hear Impostors in Vents",        false);
    public ModdedToggleOption VentPrivateChat      { get; } = new("Private Talk in Vents",          true);
    public ModdedToggleOption CommsSabDisables     { get; } = new("Comms Sabotage Disables Voice",  true);
    public ModdedToggleOption CameraCanHear        { get; } = new("Hear Through Cameras",           true);
    public ModdedToggleOption ImpostorPrivateRadio { get; } = new("Impostor Radio",                 false);
    public ModdedToggleOption OnlyGhostsCanTalk    { get; } = new("Only Ghosts can Talk/Hear",      false);
    public ModdedToggleOption OnlyMeetingOrLobby   { get; } = new("Meetings/Lobby Only",            false);

    internal static VoiceChatGameOptions GetInstance() =>
        OptionGroupSingleton<VoiceChatGameOptions>.Instance;
}
