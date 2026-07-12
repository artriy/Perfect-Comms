using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

public class VoiceChatGameOptions
{
    public const string GroupName = "Perfect Comms";
    public const uint GroupPriority = 1000;
    private const string Section = "Host.VoiceChat";

    public ToggleHolder PublicVoiceLobby { get; }
    public EnumHolder VoiceBackend { get; }
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
        PublicVoiceLobby = new ToggleHolder(cfg, Section, "PublicVoiceLobby", "Public Voice Lobby", false);
        VoiceBackend = new EnumHolder(cfg, Section, "VoiceBackend", "Voice Backend",
            (int)VoiceTransportBackend.BetterCrewLink, typeof(VoiceTransportBackend),
            new[] { "BetterCrewLink", "Interstellar" })
        {
            Visible = () => VoiceEndpointSettings.InterstellarEnabled,
        };
        if (!VoiceEndpointSettings.InterstellarEnabled)
            VoiceBackend.Value = (int)VoiceTransportBackend.BetterCrewLink;
        LobbyBrowserBackend = new EnumHolder(cfg, Section, "LobbyBrowserBackend", "Lobby Browser Backend",
            (int)VoiceLobbyBrowserSource.BetterCrewLink, typeof(VoiceLobbyBrowserSource),
            new[] { "BCL Live", "Cloudflare (Limited)" });
        MaxChatDistance = new NumberHolder(cfg, Section, "MaxChatDistance", "Max Distance", 6f, 1.5f, 20f, 0.5f, "0.0");
        FalloffMode = new EnumHolder(cfg, Section, "FalloffMode", "Voice Falloff",
            (int)VoiceFalloffMode.Smooth, typeof(VoiceFalloffMode),
            new[] { "Linear", "Smooth", "Voice Focused" });
        OcclusionMode = new EnumHolder(cfg, Section, "OcclusionMode", "Voice Occlusion",
            (int)VoiceOcclusionMode.VisionOnly, typeof(VoiceOcclusionMode),
            new[] { "Off", "Soft Muffle", "Soft Fade", "Hard Block", "Vision Only" });
        WallsBlockSound = new ToggleHolder(cfg, Section, "WallsBlockSound", "Walls Block Audio", true);
        OnlyHearInSight = new ToggleHolder(cfg, Section, "OnlyHearInSight", "Hear People in Vision Only", true);
        ImpostorHearGhosts = new ToggleHolder(cfg, Section, "ImpostorHearGhosts", "Impostors Hear Dead", false);
        HearInVent = new ToggleHolder(cfg, Section, "HearInVent", "Hear Impostors in Vents", false);
        VentPrivateChat = new ToggleHolder(cfg, Section, "VentPrivateChat", "Private Talk in Vents", true);
        CommsSabDisables = new ToggleHolder(cfg, Section, "CommsSabDisables", "Comms Sabotage Disables Voice", true);
        CameraCanHear = new ToggleHolder(cfg, Section, "CameraCanHear", "Hear Through Cameras", true);
        TeamRadio = new ToggleHolder(cfg, Section, "TeamRadio", "Team Radio", true);
        TeamRadioImpostors = new ToggleHolder(cfg, Section, "TeamRadioImpostors", "Team Radio - Impostors", true)
        {
            Visible = TeamRadioSubOptionsVisible
        };
        TeamRadioVampires = new ToggleHolder(cfg, Section, "TeamRadioVampires", "Team Radio - <color=#A32929><b>Vampires</b></color>", true)
        {
            Visible = TeamRadioSubOptionsVisible
        };
        TeamRadioLovers = new ToggleHolder(cfg, Section, "TeamRadioLovers", "Team Radio - <color=#FF66CC><b>Lovers</b></color>", true)
        {
            Visible = TeamRadioSubOptionsVisible
        };
        TeamRadioInMeetings = new ToggleHolder(cfg, Section, "TeamRadioInMeetings", "Team Radio - Usable in Meetings", false)
        {
            Visible = TeamRadioSubOptionsVisible
        };
        TeamRadioInTasks = new ToggleHolder(cfg, Section, "TeamRadioInTasks", "Team Radio - Usable in Tasks Phase", true)
        {
            Visible = TeamRadioInMeetingsVisible
        };
        OnlyGhostsCanTalk = new ToggleHolder(cfg, Section, "OnlyGhostsCanTalk", "Only Ghosts can Talk/Hear", false);
        GhostsHearEachOtherUnlimited = new ToggleHolder(cfg, Section, "GhostsHearEachOtherUnlimited", "Ghosts Hear Each Other Anywhere", false);
        OnlyMeetingOrLobby = new ToggleHolder(cfg, Section, "OnlyMeetingOrLobby", "Meetings/Lobby Only", false);
        OnlyMeetingOrLobbyAffectsGhosts = new ToggleHolder(cfg, Section, "OnlyMeetingOrLobbyAffectsGhosts", "Ghosts Also Meeting/Lobby Only", false)
        {
            Visible = MeetingLobbySubOptionsVisible
        };
        GracePeriodEnabled = new ToggleHolder(cfg, Section, "GracePeriodEnabled", "Meeting Floor Grace Period", false);
        GracePeriodSeconds = new NumberHolder(cfg, Section, "GracePeriodSeconds", "Grace Period Seconds", 5f, 0f, 15f, 1f, "0")
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
