using MiraAPI.GameOptions;
using MiraAPI.GameOptions.OptionTypes;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceRoleIntegrationOptions : AbstractOptionGroup
{
    public override string GroupName => "Perfect Comms: Role Voice Rules";
    public override Color GroupColor => Palette.CrewmateRoleHeaderBlue;
    public override uint GroupPriority => 1001;

    public ModdedToggleOption MuteBlackmailedInMeetings { get; } = new("<color=#FF0000><b>Blackmailer</b></color>: Mute Blackmailed in Meetings", true);
    public ModdedToggleOption MuteBlackmailedNextRound { get; } = new("<color=#FF0000><b>Blackmailer</b></color>: Mute Blackmailed Next Round", false);
    public ModdedToggleOption MuteParasiteControlled { get; } = new("<color=#FF0000><b>Parasite</b></color>: Mute Controlled Victim", true);
    public ModdedToggleOption ParasiteHearFromVictim { get; } = new("<color=#FF0000><b>Parasite</b></color>: Also Hear Controlled Victim", true);
    public ModdedToggleOption MutePuppeteerControlled { get; } = new("<color=#FF0000><b>Puppeteer</b></color>: Mute Controlled Victim", true);
    public ModdedToggleOption PuppeteerHearFromVictim { get; } = new("<color=#FF0000><b>Puppeteer</b></color>: Hear From Controlled Victim", true);
    public ModdedToggleOption MuteSwooperWhileSwooped { get; } = new("<color=#FF0000><b>Swooper</b></color>: Mute While Swooped", true);
    public ModdedToggleOption MuffleBlindedOrFlashedHearing { get; } = new("<color=#FF0000><b>Eclipsal/Grenadier</b></color>: Muffle Blinded/Flashed Hearing", true);
    public ModdedToggleOption MuffleHypnotizedDuringHysteria { get; } = new("<color=#FF0000><b>Hypnotist</b></color>: Muffle Hypnotized During Hysteria", true);
    public ModdedToggleOption CrewpostorUsesImpostorVoice { get; } = new("<color=#FF0000><b>Crewpostor</b></color>: Use Impostor Voice", true);
    public ModdedToggleOption MuteGlitchHacked { get; } = new("<color=#00FF00><b>Glitch</b></color>: Mute Hacked Players", true);
    public ModdedToggleOption MuteJailedInMeetings { get; } = new("<color=#A6A6A6><b>Jailor</b></color>: Mute Jailee in Meetings", true);
    public ModdedToggleOption JailPersistsAfterJailorDeath { get; } = new("<color=#A6A6A6><b>Jailor</b></color>: Jail Persists If Jailor Dies", false)
    {
        Visible = JailSubOptionVisible
    };
    public ModdedToggleOption JailorCanUnmuteJailed { get; } = new("<color=#A6A6A6><b>Jailor</b></color>: Can Unmute Jailee", true);
    public ModdedEnumOption MediumGhostVoice { get; } = new("<color=#A680FF><b>Medium</b></color>: Ghost Voice",
        (int)MediumGhostVoiceMode.None,
        typeof(MediumGhostVoiceMode),
        ["None", "Medium -> Ghost", "Ghost -> Medium", "Both"]);

    private static bool JailSubOptionVisible() =>
        OptionGroupSingleton<VoiceRoleIntegrationOptions>.Instance.MuteJailedInMeetings;

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
