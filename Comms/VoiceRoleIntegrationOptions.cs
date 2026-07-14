using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceRoleIntegrationOptions
{
    public const string GroupName = "Perfect Comms: Role Voice Rules";
    public const uint GroupPriority = 1001;
    private const string Section = "Host.VoiceChat.Roles";

    public ToggleHolder MuteBlackmailedInMeetings { get; }
    public ToggleHolder MuteBlackmailedNextRound { get; }
    public ToggleHolder MuteParasiteControlled { get; }
    public ToggleHolder ParasiteHearFromVictim { get; }
    public ToggleHolder MutePuppeteerControlled { get; }
    public ToggleHolder PuppeteerHearFromVictim { get; }
    public ToggleHolder MuteSwooperWhileSwooped { get; }
    public ToggleHolder MuffleBlindedOrFlashedHearing { get; }
    public ToggleHolder MuffleHypnotizedDuringHysteria { get; }
    public ToggleHolder CrewpostorUsesImpostorVoice { get; }
    public ToggleHolder MuteGlitchHacked { get; }
    public ToggleHolder MuteJailedInMeetings { get; }
    public ToggleHolder JailPersistsAfterJailorDeath { get; }
    public ToggleHolder JailorCanUnmuteJailed { get; }
    public EnumHolder MediumGhostVoice { get; }

    private VoiceRoleIntegrationOptions(ConfigFile cfg)
    {
        MuteBlackmailedInMeetings = new ToggleHolder(cfg, Section, "MuteBlackmailedInMeetings", "<color=#FF0000><b>Blackmailer</b></color>: Mute Blackmailed in Meetings", true,
            "Prevents the currently blackmailed player from transmitting voice during meetings.");
        MuteBlackmailedNextRound = new ToggleHolder(cfg, Section, "MuteBlackmailedNextRound", "<color=#FF0000><b>Blackmailer</b></color>: Mute Blackmailed Next Round", false,
            "Keeps a meeting-blackmailed player voice-muted during the following task round.");
        MuteParasiteControlled = new ToggleHolder(cfg, Section, "MuteParasiteControlled", "<color=#FF0000><b>Parasite</b></color>: Mute Controlled Victim", true,
            "Prevents a player marked by the Parasite from transmitting their own voice while the effect is active.");
        ParasiteHearFromVictim = new ToggleHolder(cfg, Section, "ParasiteHearFromVictim", "<color=#FF0000><b>Parasite</b></color>: Also Hear Controlled Victim", true,
            "Lets the Parasite also hear the voices audible around its marked victim while remaining at the Parasite's own position.");
        MutePuppeteerControlled = new ToggleHolder(cfg, Section, "MutePuppeteerControlled", "<color=#FF0000><b>Puppeteer</b></color>: Mute Controlled Victim", true,
            "Prevents a Puppeteer-controlled player from transmitting their own voice while controlled.");
        PuppeteerHearFromVictim = new ToggleHolder(cfg, Section, "PuppeteerHearFromVictim", "<color=#FF0000><b>Puppeteer</b></color>: Hear From Controlled Victim", true,
            "Lets the Puppeteer hear the voices audible around the player it currently controls.");
        MuteSwooperWhileSwooped = new ToggleHolder(cfg, Section, "MuteSwooperWhileSwooped", "<color=#FF0000><b>Swooper</b></color>: Mute While Swooped", true,
            "Prevents an invisible Swooper from transmitting voice until the swoop ends.");
        MuffleBlindedOrFlashedHearing = new ToggleHolder(cfg, Section, "MuffleBlindedOrFlashedHearing", "<color=#FF0000><b>Eclipsal/Grenadier</b></color>: Muffle Blinded/Flashed Hearing", true,
            "Muffles incoming voice only for players currently blinded by Eclipsal or flashed by Grenadier.");
        MuffleHypnotizedDuringHysteria = new ToggleHolder(cfg, Section, "MuffleHypnotizedDuringHysteria", "<color=#FF0000><b>Hypnotist</b></color>: Muffle Hypnotized During Hysteria", true,
            "Muffles incoming voice only for affected hypnotized players while Mass Hysteria is active.");
        CrewpostorUsesImpostorVoice = new ToggleHolder(cfg, Section, "CrewpostorUsesImpostorVoice", "<color=#FF0000><b>Crewpostor</b></color>: Use Impostor Voice", true,
            "Treats Crewpostor as an impostor for private impostor voice and team-radio routing.");
        MuteGlitchHacked = new ToggleHolder(cfg, Section, "MuteGlitchHacked", "<color=#00FF00><b>Glitch</b></color>: Mute Hacked Players", true,
            "Prevents a player affected by the Glitch's Hack ability from transmitting voice until the hack ends.");
        MuteJailedInMeetings = new ToggleHolder(cfg, Section, "MuteJailedInMeetings", "<color=#A6A6A6><b>Jailor</b></color>: Mute Jailee in Meetings", true,
            "Prevents the jailed player from transmitting voice during meetings unless the Jailor temporarily unmutes them.");
        JailPersistsAfterJailorDeath = new ToggleHolder(cfg, Section, "JailPersistsAfterJailorDeath", "<color=#A6A6A6><b>Jailor</b></color>: Jail Persists If Jailor Dies", false,
            "Keeps the meeting voice jail active even if the Jailor is dead.")
        {
            Visible = JailSubOptionVisible
        };
        JailorCanUnmuteJailed = new ToggleHolder(cfg, Section, "JailorCanUnmuteJailed", "<color=#A6A6A6><b>Jailor</b></color>: Can Unmute Jailee", true,
            "Lets the Jailor temporarily allow the jailed player to speak during a meeting.");
        MediumGhostVoice = new EnumHolder(cfg, Section, "MediumGhostVoice", "<color=#A680FF><b>Medium</b></color>: Ghost Voice",
            (int)MediumGhostVoiceMode.None, typeof(MediumGhostVoiceMode),
            new[] { "None", "Medium -> Ghost", "Ghost -> Medium", "Both" },
            "Chooses which voice direction is allowed between a Medium and dead players during tasks.");
    }

    private static VoiceRoleIntegrationOptions? _instance;
    public static VoiceRoleIntegrationOptions Instance => _instance ??= new VoiceRoleIntegrationOptions(VoiceChatPluginMain.PluginConfig);
    internal static VoiceRoleIntegrationOptions GetInstance() => Instance;

    private static bool JailSubOptionVisible() => Instance.MuteJailedInMeetings.Value;
}

public enum MediumGhostVoiceMode
{
    None,
    MediumToGhost,
    GhostToMedium,
    Both,
}
