using MiraAPI.GameOptions;
using MiraAPI.GameOptions.OptionTypes;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceRoleIntegrationOptions : AbstractOptionGroup
{
    public override string GroupName => "Perfect Comms: Role Voice Rules";
    public override Color GroupColor => Palette.CrewmateRoleHeaderBlue;
    public override uint GroupPriority => 1001;

    public ModdedToggleOption MuteBlackmailedInMeetings { get; } = new("Mute <color=#FF6666><b>Blackmailed</b></color> in Meetings", true);
    public ModdedToggleOption MuteBlackmailedNextRound { get; } = new("Mute <color=#FF6666><b>Blackmailed</b></color> Next Round", false);
    public ModdedToggleOption MuteJailedInMeetings { get; } = new("Mute <color=#909190><b>Jailee</b></color> in Meetings", true);
    public ModdedToggleOption JailorCanUnmuteJailed { get; } = new("Jailor Can Unmute <color=#909190><b>Jailee</b></color>", true);
    public ModdedToggleOption MuteParasiteControlled { get; } = new("Mute <color=#FF6666><b>Parasite</b></color>'s Victim", true);
    public ModdedToggleOption MutePuppeteerControlled { get; } = new("Mute <color=#FF6666><b>Puppeteer</b></color>'s Victim", true);
    public ModdedToggleOption MuteSwooperWhileSwooped { get; } = new("Mute <color=#FF6666><b>Swooper</b></color> While Swooped", true);
    public ModdedToggleOption MuteGlitchHacked { get; } = new("Mute <color=#FF6666><b>Glitch-Hacked</b></color> Players", true);
    public ModdedToggleOption CrewpostorUsesImpostorVoice { get; } = new("Crewpostor Uses Impostor Voice", true);
    public ModdedEnumOption MediumGhostVoice { get; } = new("Medium Ghost Voice",
        (int)MediumGhostVoiceMode.None,
        typeof(MediumGhostVoiceMode),
        ["None", "Medium -> Ghost", "Ghost -> Medium", "Both"]);

    internal static VoiceRoleIntegrationOptions GetInstance() =>
        OptionGroupSingleton<VoiceRoleIntegrationOptions>.Instance;
}

public enum MediumGhostVoiceMode
{
    None,
    MediumToGhost,
    GhostToMedium,
    Both,
}
