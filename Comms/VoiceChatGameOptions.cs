using MiraAPI.GameOptions;
using MiraAPI.GameOptions.OptionTypes;
using MiraAPI.Utilities;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceChatGameOptions : AbstractOptionGroup
{
    public override string GroupName => "Perfect Comms";

    public ModdedToggleOption PublicVoiceLobby { get; } = new("Public Voice Lobby", false);

    public ModdedNumberOption MaxChatDistance { get; } =
        new("Max Distance", 6f, 1.5f, 20f, 0.5f, MiraNumberSuffixes.None, "0.0");

    public ModdedEnumOption FalloffMode { get; } = new("Voice Falloff", (int)VoiceFalloffMode.Smooth,
        typeof(VoiceFalloffMode), ["Linear", "Smooth", "Voice Focused"]);

    public ModdedEnumOption OcclusionMode { get; } = new("Voice Occlusion", (int)VoiceOcclusionMode.VisionOnly,
        typeof(VoiceOcclusionMode), ["Off", "Soft Muffle", "Soft Fade", "Hard Block", "Vision Only"]);

    public ModdedToggleOption WallsBlockSound      { get; } = new("Walls Block Audio",              true);
    public ModdedToggleOption OnlyHearInSight       { get; } = new("Hear People in Vision Only",     true);
    public ModdedToggleOption ImpostorHearGhosts    { get; } = new("Impostors Hear Dead",            false);
    public ModdedToggleOption HearInVent            { get; } = new("Hear Impostors in Vents",        false);
    public ModdedToggleOption VentPrivateChat       { get; } = new("Private Talk in Vents",          true);
    public ModdedToggleOption CommsSabDisables      { get; } = new("Comms Sabotage Disables Voice",  true);
    public ModdedToggleOption CameraCanHear         { get; } = new("Hear Through Cameras",           true);

    // Renamed from "Impostor Radio" to "Team Chat Radio" — Vampires now also get access when enabled.
    public ModdedToggleOption ImpostorPrivateRadio  { get; } = new("Team Chat Radio",                false);

    public ModdedToggleOption OnlyGhostsCanTalk     { get; } = new("Only Ghosts can Talk/Hear",      false);
    public ModdedToggleOption OnlyMeetingOrLobby    { get; } = new("Meetings/Lobby Only",            false);

    // ── Role-specific options ─────────────────────────────────────────────────

    // Medium: can speak to/hear ghosts while in spiritual state during meetings.
    public ModdedToggleOption MediumSpiritualVoice  { get; } = new("Medium can talk to Ghosts when Mediating",        true);

    // Parasite: the overtaken victim is muted for as long as the Parasite is in control.
    public ModdedToggleOption ParasiteVictimMuted   { get; } = new("Parasite's Overtaken Victim is Muted",         true);

    // Blackmailer: muted the round they are blackmailed (already enforced by VoiceRoleMuteState;
    // this toggle lets hosts turn it off).
    public ModdedToggleOption BlackmailMutesRound   { get; } = new("Blackmailed Player Muted Current Round", true);

    // Blackmailer: muted the following round as well.
    public ModdedToggleOption BlackmailMutesNextRound { get; } = new("Blackmailed Player Muted Following Round",  false);

    // Jailor: can unmute/mute their jailed victim during meetings (already implemented;
    // this toggle lets hosts disable the mechanic entirely).
    public ModdedToggleOption JailorCanControlVoice { get; } = new("Jailor Can Control Jailee's Voice",   true);

    // Vampire: gets access to the Team Chat Radio when ImpostorPrivateRadio is enabled.
    public ModdedToggleOption VampireTeamChatRadio  { get; } = new("Vampire gets Team Chat Radio",       true);

    public static VoiceChatGameOptions Instance =>
        OptionGroupSingleton<VoiceChatGameOptions>.Instance;
}