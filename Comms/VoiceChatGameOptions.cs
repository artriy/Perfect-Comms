using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceChatGameOptions
{
    public const string GroupName = "Perfect Comms";
    public const uint GroupPriority = 1000;
    private const string Section = "Host.VoiceChat";

    public ToggleHolder PublicVoiceLobby { get; }
    public EnumHolder LobbyBrowserBackend { get; }
    public NumberHolder MaxChatDistance { get; }
    public EnumHolder FalloffMode { get; }
    public EnumHolder OcclusionMode { get; }
    public ToggleHolder WallsBlockSound { get; }
    public ToggleHolder OnlyHearInSight { get; }
    public ToggleHolder ImpostorHearGhosts { get; }
    public ToggleHolder HearInVent { get; }
    public ToggleHolder VentPrivateChat { get; }
    public ToggleHolder CommsSabDisables { get; }
    public ToggleHolder CameraCanHear { get; }
    public ToggleHolder TeamRadio { get; }
    public ToggleHolder TeamRadioImpostors { get; }
    public ToggleHolder TeamRadioVampires { get; }
    public ToggleHolder TeamRadioLovers { get; }
    public ToggleHolder TeamRadioInMeetings { get; }
    public ToggleHolder TeamRadioInTasks { get; }
    public ToggleHolder OnlyGhostsCanTalk { get; }
    public ToggleHolder GhostsHearEachOtherUnlimited { get; }
    public ToggleHolder OnlyMeetingOrLobby { get; }
    public ToggleHolder OnlyMeetingOrLobbyAffectsGhosts { get; }
    public ToggleHolder GracePeriodEnabled { get; }
    public NumberHolder GracePeriodSeconds { get; }

    private VoiceChatGameOptions(ConfigFile cfg)
    {
        PublicVoiceLobby = new ToggleHolder(cfg, Section, "PublicVoiceLobby", "Public Voice Lobby", false,
            "Publishes this voice-enabled lobby to the selected public lobby directory so other Perfect Comms players can find it.");
        LobbyBrowserBackend = new EnumHolder(cfg, Section, "LobbyBrowserBackend", "Public Lobby Directory",
            (int)VoiceLobbyBrowserSource.BetterCrewLink, typeof(VoiceLobbyBrowserSource),
            new[] { "BetterCrewLink Live", "Perfect Comms Registry" },
            "Chooses which public directory receives this lobby's listing when Public Voice Lobby is enabled.");
        MaxChatDistance = new NumberHolder(cfg, Section, "MaxChatDistance", "Max Distance", 6f, 1.5f, 20f, 0.5f, "0.0",
            "Sets the maximum task-phase distance at which nearby players can hear one another.");
        FalloffMode = new EnumHolder(cfg, Section, "FalloffMode", "Voice Falloff",
            (int)VoiceFalloffMode.Smooth, typeof(VoiceFalloffMode),
            new[] { "Linear", "Smooth", "Voice Focused" },
            "Chooses how voice volume fades as a player approaches the maximum hearing distance.");
        OcclusionMode = new EnumHolder(cfg, Section, "OcclusionMode", "Voice Occlusion",
            (int)VoiceOcclusionMode.VisionOnly, typeof(VoiceOcclusionMode),
            new[] { "Off", "Soft Muffle", "Soft Fade", "Hard Block", "Vision Only" },
            "Chooses how walls and lost line of sight affect nearby voice audio during tasks.");
        WallsBlockSound = new ToggleHolder(cfg, Section, "WallsBlockSound", "Walls Block Audio", true,
            "Lets map walls obstruct voice audio according to the selected Voice Occlusion mode.");
        OnlyHearInSight = new ToggleHolder(cfg, Section, "OnlyHearInSight", "Hear People in Vision Only", true,
            "Restricts normal task-phase voice to players inside the listener's current vision range.");
        ImpostorHearGhosts = new ToggleHolder(cfg, Section, "ImpostorHearGhosts", "Impostors Hear Dead", false,
            "Allows living impostors to hear dead players when the other ghost voice rules permit them to speak.");
        HearInVent = new ToggleHolder(cfg, Section, "HearInVent", "Hear Impostors in Vents", false,
            "Allows nearby players to hear an impostor who is currently inside a vent.");
        VentPrivateChat = new ToggleHolder(cfg, Section, "VentPrivateChat", "Private Talk in Vents", true,
            "Prevents players outside vents from hearing a player who is currently vented, keeping vent speech private.");
        CommsSabDisables = new ToggleHolder(cfg, Section, "CommsSabDisables", "Comms Sabotage Disables Voice", true,
            "Disables normal voice communication while the Communications sabotage is active.");
        CameraCanHear = new ToggleHolder(cfg, Section, "CameraCanHear", "Hear Through Cameras", true,
            "Lets a player using security cameras hear nearby voice around the active camera position.");
        TeamRadio = new ToggleHolder(cfg, Section, "TeamRadio", "Team Radio", true,
            "Enables private hold-to-talk radio channels for eligible teams and roles.");
        TeamRadioImpostors = new ToggleHolder(cfg, Section, "TeamRadioImpostors", "Team Radio - Impostors", true,
            "Enables the private impostor team-radio channel when Team Radio is on.")
        {
            Visible = TeamRadioSubOptionsVisible
        };
        TeamRadioVampires = new ToggleHolder(cfg, Section, "TeamRadioVampires", "Team Radio - <color=#A32929><b>Vampires</b></color>", true,
            "Enables the private vampire team-radio channel when Team Radio is on.")
        {
            Visible = TeamRadioSubOptionsVisible
        };
        TeamRadioLovers = new ToggleHolder(cfg, Section, "TeamRadioLovers", "Team Radio - <color=#FF66CC><b>Lovers</b></color>", true,
            "Enables the private lovers team-radio channel when Team Radio is on.")
        {
            Visible = TeamRadioSubOptionsVisible
        };
        TeamRadioInMeetings = new ToggleHolder(cfg, Section, "TeamRadioInMeetings", "Team Radio - Usable in Meetings", false,
            "Allows eligible players to use team radio during meetings.")
        {
            Visible = TeamRadioSubOptionsVisible
        };
        TeamRadioInTasks = new ToggleHolder(cfg, Section, "TeamRadioInTasks", "Team Radio - Usable in Tasks Phase", true,
            "Allows eligible players to use team radio during normal task gameplay.")
        {
            Visible = TeamRadioInMeetingsVisible
        };
        OnlyGhostsCanTalk = new ToggleHolder(cfg, Section, "OnlyGhostsCanTalk", "Only Ghosts can Talk/Hear", false,
            "Restricts task-phase voice communication to dead players only.");
        GhostsHearEachOtherUnlimited = new ToggleHolder(cfg, Section, "GhostsHearEachOtherUnlimited", "Ghosts Hear Each Other Anywhere", false,
            "Lets dead players hear one another across the entire map instead of using proximity distance.");
        OnlyMeetingOrLobby = new ToggleHolder(cfg, Section, "OnlyMeetingOrLobby", "Meetings/Lobby Only", false,
            "Disables living-player voice during tasks so normal voice is available only in lobbies and meetings.");
        OnlyMeetingOrLobbyAffectsGhosts = new ToggleHolder(cfg, Section, "OnlyMeetingOrLobbyAffectsGhosts", "Ghosts Also Meeting/Lobby Only", false,
            "Applies Meetings/Lobby Only to dead players as well as living players.")
        {
            Visible = MeetingLobbySubOptionsVisible
        };
        GracePeriodEnabled = new ToggleHolder(cfg, Section, "GracePeriodEnabled", "Meeting Floor Grace Period", false,
            "Gives the player who called the meeting an exclusive voice floor for a short time when the meeting begins.");
        GracePeriodSeconds = new NumberHolder(cfg, Section, "GracePeriodSeconds", "Grace Period Seconds", 5f, 0f, 15f, 1f, "0",
            "Sets how many seconds the meeting-floor grace period lasts after a meeting is called.")
        {
            Visible = GracePeriodSubOptionVisible
        };
    }

    private static VoiceChatGameOptions? _instance;
    public static VoiceChatGameOptions Instance => _instance ??= new VoiceChatGameOptions(VoiceChatPluginMain.PluginConfig);
    internal static VoiceChatGameOptions GetInstance() => Instance;

    private static bool TeamRadioSubOptionsVisible() => Instance.TeamRadio.Value;

    private static bool TeamRadioInMeetingsVisible() =>
        Instance.TeamRadio.Value && Instance.TeamRadioInMeetings.Value;

    private static bool MeetingLobbySubOptionsVisible() => Instance.OnlyMeetingOrLobby.Value;

    private static bool GracePeriodSubOptionVisible() => Instance.GracePeriodEnabled.Value;
}
