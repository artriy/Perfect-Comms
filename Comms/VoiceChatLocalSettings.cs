using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using MiraAPI.LocalSettings;
using MiraAPI.LocalSettings.Attributes;
using MiraAPI.Utilities.Assets;
#if WINDOWS
using NAudio.Wave;
using VoiceChatPlugin.Audio;
#endif

namespace VoiceChatPlugin.VoiceChat;
public enum MicDeviceEnum
{
    Default   =  0,
    Device1   =  1, Device2   =  2, Device3   =  3, Device4   =  4,
    Device5   =  5, Device6   =  6, Device7   =  7, Device8   =  8,
    Device9   =  9, Device10  = 10
}

public enum SpkDeviceEnum
{
    Default   =  0,
    Device1   =  1, Device2   =  2, Device3   =  3, Device4   =  4,
    Device5   =  5, Device6   =  6, Device7   =  7, Device8   =  8,
    Device9   =  9, Device10  = 10
}

public enum SpeakingBarPosition
{
    TopLeft      = 0,
    TopMiddle    = 1,
    TopRight     = 2,
    MiddleLeft   = 6,
    MiddleRight  = 7,
    BottomLeft   = 3,
    BottomMiddle = 4,
    BottomRight  = 5,
}

public enum VoiceControlsLayout
{
    Vertical = 0,
    Horizontal = 1,
}

public enum SpeakingBarNamePosition
{
    Bottom = 0,
    Top    = 1,
    Left   = 2,
    Right  = 3,
}

public enum JailUnmuteButtonPlacement
{
    VoiceHud = 0,
    MeetingCard = 1,
}

public enum VoiceMicMode
{
    OpenMic = 0,
    PushToTalk = 1,
}

public enum VoiceMouseBind
{
    Off = 0,
    MB4 = 1,
    MB5 = 2,
}

public class VoiceChatLocalSettings : LocalSettingsTab
{
    public static LoadableResourceAsset MicIcon { get; } = new("VoiceChatPlugin.Resources.miclogo.png");

    public override string TabName => "Perfect Comms";
    public override LocalSettingTabAppearance TabAppearance => new()
    {
        TabIcon = MicIcon
    };

    private static string[] _micDeviceNames = Array.Empty<string>();
#if WINDOWS
    private static string[] _spkDeviceNames = Array.Empty<string>();
#endif

    public static string[] MicDeviceNames => _micDeviceNames;
#if WINDOWS
    public static string[] SpkDeviceNames => _spkDeviceNames;
#endif

    // ── Settings ──────────────────────────────────────────────────────────────
    [LocalSliderSetting("Mic Volume", min: 0.1f, max: 2f,
        displayValue: true, formatString: "0.00")]
    public ConfigEntry<float> MicVolume { get; }

    [LocalSliderSetting("Mic Sensitivity", min: 0.25f, max: 2f,
        displayValue: true, formatString: "0.00")]
    public ConfigEntry<float> MicSensitivity { get; }

    [LocalSliderSetting("Speaker Volume", min: 0.1f, max: 2f,
        displayValue: true, formatString: "0.00")]
    public ConfigEntry<float> MasterVolume { get; }

    [LocalSliderSetting("Voice Falloff Softness", min: 0f, max: 1f,
        displayValue: true, formatString: "0%")]
    public ConfigEntry<float> VoiceFalloffSoftness { get; }

    [LocalEnumSetting("Mic Mode")]
    public ConfigEntry<VoiceMicMode> MicMode { get; }

    public ConfigEntry<float> NoiseGateThreshold { get; }

    public ConfigEntry<float> VadThreshold { get; }

    [LocalToggleSetting("Start Muted")]
    public ConfigEntry<bool> StartMuted { get; }

    [LocalToggleSetting("Start Deafened")]
    public ConfigEntry<bool> StartDeafened { get; }

    [LocalEnumSetting("Mute Mouse Bind")]
    public ConfigEntry<VoiceMouseBind> MuteMouseBind { get; }

    [LocalEnumSetting("Speaker Mouse Bind")]
    public ConfigEntry<VoiceMouseBind> SpeakerMouseBind { get; }

    [LocalEnumSetting("Push To Talk Mouse Bind")]
    public ConfigEntry<VoiceMouseBind> PushToTalkMouseBind { get; }

    [LocalEnumSetting("Team Radio Mouse Bind")]
    public ConfigEntry<VoiceMouseBind> ImpostorRadioMouseBind { get; }

    [LocalEnumSetting("Microphone Device")]
    public ConfigEntry<MicDeviceEnum> MicrophoneDeviceIndex { get; }

#if WINDOWS
    [LocalEnumSetting("Speaker Device")]
    public ConfigEntry<SpkDeviceEnum> SpeakerDeviceIndex { get; }
#endif

    [LocalSliderSetting("Voice Controls X", min: 0f, max: 1f,
        displayValue: true, formatString: "0.00")]
    public ConfigEntry<float> ButtonPositionX { get; }

    [LocalSliderSetting("Voice Controls Y", min: 0f, max: 1f,
        displayValue: true, formatString: "0.00")]
    public ConfigEntry<float> ButtonPositionY { get; }

    [LocalEnumSetting("Voice Controls Layout")]
    public ConfigEntry<VoiceControlsLayout> VoiceControlsLayout { get; }

    [LocalEnumSetting("Speaking Bar Position")]
    public ConfigEntry<SpeakingBarPosition> SpeakingBarPosition { get; }

    [LocalToggleSetting("Manual Speaking Bar Layout")]
    public ConfigEntry<bool> SpeakingBarManualLayout { get; }

    [LocalSliderSetting("Speaking Bar X", min: 0f, max: 1f,
        displayValue: true, formatString: "0.00")]
    public ConfigEntry<float> SpeakingBarX { get; }

    [LocalSliderSetting("Speaking Bar Y", min: 0f, max: 1f,
        displayValue: true, formatString: "0.00")]
    public ConfigEntry<float> SpeakingBarY { get; }

    [LocalEnumSetting("Speaking Bar Layout")]
    public ConfigEntry<VoiceControlsLayout> SpeakingBarLayout { get; }

    [LocalEnumSetting("Speaking Bar Name Position")]
    public ConfigEntry<SpeakingBarNamePosition> SpeakingBarNamePosition { get; }

    [LocalEnumSetting("Jail Unmute Button")]
    public ConfigEntry<JailUnmuteButtonPlacement> JailUnmuteButtonPlacement { get; }

    [LocalToggleSetting("Meeting Speaking Overlay")]
    public ConfigEntry<bool> MeetingSpeakingOverlay { get; }

    [LocalSliderSetting("Overlay Scale", min: 0.75f, max: 3.00f,
        displayValue: true, formatString: "0.00")]
    public ConfigEntry<float> OverlayScale { get; }

    [LocalToggleSetting("Noise Suppression")]
    public ConfigEntry<bool> NoiseSuppressionEnabled { get; }

    [LocalToggleSetting("Debug Voice Stats")]
    public ConfigEntry<bool> DebugVoiceStats { get; }

    public ConfigEntry<bool> SyntheticMicTone { get; }
    [LocalToggleSetting("Mic Calibration Logs")]
    public ConfigEntry<bool> MicCalibrationDiagnostics { get; }

    public ConfigEntry<string> PerPlayerVolumes { get; }
    public ConfigEntry<string> LobbyBrowserTitle { get; }
    public ConfigEntry<string> LobbyBrowserLanguage { get; }
    public ConfigEntry<VoiceLobbyBrowserSource> LobbyBrowserSource { get; }
    public ConfigEntry<string> LobbyRegistryUrl { get; }
    public ConfigEntry<string> BetterCrewLinkServerUrl { get; }
    public ConfigEntry<string> InterstellarServerUrl { get; }
    public ConfigEntry<bool> UpdateNotificationsEnabled { get; }
    public ConfigEntry<string> UpdateNotificationUrl { get; }

    private readonly ConfigEntry<string> _savedMicDeviceName;
#if WINDOWS
    private readonly ConfigEntry<string> _savedSpkDeviceName;
#endif

    private bool _correcting;

    public string MicrophoneDevice
    {
        get
        {
            int idx = (int)MicrophoneDeviceIndex.Value;
            return idx > 0 && idx < _micDeviceNames.Length ? _micDeviceNames[idx] : "";
        }
    }

#if WINDOWS
    public string SpeakerDevice
    {
        get
        {
            int idx = (int)SpeakerDeviceIndex.Value;
            return idx > 0 && idx < _spkDeviceNames.Length ? _spkDeviceNames[idx] : "";
        }
    }
#endif

    public VoiceChatLocalSettings(ConfigFile config) : base(config)
    {
        RefreshDeviceLists();

        MicVolume = config.Bind("Audio", "MicVolume", 1f,
            new ConfigDescription("Mic input volume",
                new AcceptableValueRange<float>(0.1f, 2f)));

        MicSensitivity = config.Bind("Audio", "MicSensitivity", 1f,
            new ConfigDescription("How easily the mic is treated as speaking. Higher is more sensitive; lower ignores more room noise.",
                new AcceptableValueRange<float>(0.25f, 2f)));

        MasterVolume = config.Bind("Audio", "MasterVolume", 1f,
            new ConfigDescription("Master output volume",
                new AcceptableValueRange<float>(0.1f, 2f)));

        VoiceFalloffSoftness = config.Bind("Audio", "VoiceFalloffSoftness", 0.30f,
            new ConfigDescription(
                "How gently voices fade near the edge of vision/range. 0% keeps the original fade; higher keeps voices clear across most of your vision and fades only near the edge. Layers on top of the host's falloff and never extends hearing range.",
                new AcceptableValueRange<float>(0f, 1f)));
        VoiceAudioOcclusion.ProximitySoftness01 = VoiceFalloffSoftness.Value;

        MicMode = config.Bind("Audio", "MicMode", VoiceMicMode.OpenMic,
            new ConfigDescription("Microphone activation mode"));

        NoiseGateThreshold = config.Bind("Audio.Advanced", "NoiseGateThreshold", 0.003f,
            new ConfigDescription("Advanced base gate threshold. Effective value is divided by MicSensitivity.",
                new AcceptableValueRange<float>(0.003f, 0.10f)));

        VadThreshold = config.Bind("Audio.Advanced", "VadThreshold", 0.004f,
            new ConfigDescription("Advanced base speaking indicator threshold. Effective value is divided by MicSensitivity.",
                new AcceptableValueRange<float>(0.002f, 0.080f)));

        StartMuted = config.Bind("Audio", "StartMuted", false,
            new ConfigDescription("Start each session with microphone muted"));

        StartDeafened = config.Bind("Audio", "StartDeafened", false,
            new ConfigDescription("Start each session with speaker muted"));

        MuteMouseBind = config.Bind("Input", "MuteMouseBind", VoiceMouseBind.Off,
            new ConfigDescription("Optional mouse button for mute / unmute"));

        SpeakerMouseBind = config.Bind("Input", "SpeakerMouseBind", VoiceMouseBind.Off,
            new ConfigDescription("Optional mouse button for speaker mute / unmute"));

        PushToTalkMouseBind = config.Bind("Input", "PushToTalkMouseBind", VoiceMouseBind.Off,
            new ConfigDescription("Optional mouse button to hold for push to talk"));

        ImpostorRadioMouseBind = config.Bind("Input", "ImpostorRadioMouseBind", VoiceMouseBind.Off,
            new ConfigDescription("Optional mouse button to hold for team radio"));

        NormalizeMouseBind(MuteMouseBind);
        NormalizeMouseBind(SpeakerMouseBind);
        NormalizeMouseBind(PushToTalkMouseBind);
        NormalizeMouseBind(ImpostorRadioMouseBind);

        _savedMicDeviceName = config.Bind("Audio", "MicDeviceName", "",
            "Saved microphone device name (used to restore selection across sessions)");

#if WINDOWS
        _savedSpkDeviceName = config.Bind("Audio", "SpkDeviceName", "",
            "Saved speaker device name (used to restore selection across sessions)");
#endif

        MicrophoneDeviceIndex = config.Bind("Audio", "Microphone",
            MicDeviceEnum.Default,
            new ConfigDescription("Selected microphone device"));

        MicrophoneDeviceIndex.Value = ResolveDeviceIndex<MicDeviceEnum>(
            _savedMicDeviceName.Value, _micDeviceNames, MicrophoneDeviceIndex.Value);

        MicrophoneDeviceIndex.SettingChanged += (_, _) =>
        {
            if (_correcting) return;
            int newIdx = (int)MicrophoneDeviceIndex.Value;
            int count  = _micDeviceNames.Length;
            if (newIdx < count) return;

            _correcting = true;
            try
            {
                bool steppedForward = newIdx <= count + 4;
                int corrected = steppedForward ? 0 : count - 1;
                MicrophoneDeviceIndex.Value = (MicDeviceEnum)corrected;
            }
            finally { _correcting = false; }
        };

#if WINDOWS
        SpeakerDeviceIndex = config.Bind("Audio", "Speaker",
            SpkDeviceEnum.Default,
            new ConfigDescription("Selected speaker device"));

        SpeakerDeviceIndex.Value = ResolveDeviceIndex<SpkDeviceEnum>(
            _savedSpkDeviceName.Value, _spkDeviceNames, SpeakerDeviceIndex.Value);

        SpeakerDeviceIndex.SettingChanged += (_, _) =>
        {
            if (_correcting) return;
            int newIdx = (int)SpeakerDeviceIndex.Value;
            int count  = _spkDeviceNames.Length;
            if (newIdx < count) return;

            _correcting = true;
            try
            {
                bool steppedForward = newIdx <= count + 4;
                int corrected = steppedForward ? 0 : count - 1;
                SpeakerDeviceIndex.Value = (SpkDeviceEnum)corrected;
            }
            finally { _correcting = false; }
        };
#endif

        ButtonPositionX = config.Bind("UI", "ButtonPositionX", 0.99f,
            new ConfigDescription("Horizontal position of voice buttons (0 = left edge, 1 = right edge)",
                new AcceptableValueRange<float>(0f, 1f)));

        ButtonPositionY = config.Bind("UI", "ButtonPositionY", 0.10f,
            new ConfigDescription("Vertical position of voice buttons (0 = bottom, 1 = top)",
                new AcceptableValueRange<float>(0f, 1f)));

        VoiceControlsLayout = config.Bind("UI", "VoiceControlsLayout",
            VoiceChatPlugin.VoiceChat.VoiceControlsLayout.Vertical,
            new ConfigDescription("Direction used to place the microphone and speaker controls"));

        SpeakingBarPosition = config.Bind("UI", "SpeakingBarPosition",
            VoiceChatPlugin.VoiceChat.SpeakingBarPosition.TopMiddle,
            new ConfigDescription("Position of the speaking bar"));

        SpeakingBarManualLayout = config.Bind("UI", "SpeakingBarManualLayout", false,
            new ConfigDescription("Use the sliders and layout below instead of the position preset."));

        SpeakingBarX = config.Bind("UI", "SpeakingBarX", 0.5f,
            new ConfigDescription("Speaking bar horizontal position (0 = left, 1 = right).",
                new AcceptableValueRange<float>(0f, 1f)));

        SpeakingBarY = config.Bind("UI", "SpeakingBarY", 0.85f,
            new ConfigDescription("Speaking bar vertical position (0 = bottom, 1 = top).",
                new AcceptableValueRange<float>(0f, 1f)));

        SpeakingBarLayout = config.Bind("UI", "SpeakingBarLayout",
            VoiceChatPlugin.VoiceChat.VoiceControlsLayout.Horizontal,
            new ConfigDescription("Speaking bar icon direction."));

        SpeakingBarNamePosition = config.Bind("UI", "SpeakingBarNamePosition",
            VoiceChatPlugin.VoiceChat.SpeakingBarNamePosition.Bottom,
            new ConfigDescription("Where the player name sits relative to its speaking-bar icon."));

        JailUnmuteButtonPlacement = config.Bind("UI", "JailUnmuteButtonPlacement",
            VoiceChatPlugin.VoiceChat.JailUnmuteButtonPlacement.MeetingCard,
            new ConfigDescription("Jailor unmute button: Voice HUD or the jailee's meeting card."));

        // Meeting overlay — on by default.
        MeetingSpeakingOverlay = config.Bind("UI", "MeetingSpeakingOverlay", true,
            new ConfigDescription(
                "Show smooth coloured card glows around talking players during meetings"));

        OverlayScale = config.Bind("UI", "OverlayScale", 1.30f,
            new ConfigDescription("Scale for voice HUD buttons",
                new AcceptableValueRange<float>(0.75f, 3.00f)));

        NoiseSuppressionEnabled = config.Bind("Audio", "NoiseSuppressionEnabled", true,
            new ConfigDescription("Use RNNoise to suppress outgoing microphone background noise."));

        DebugVoiceStats = config.Bind("Debug", "DebugVoiceStats", false,
            new ConfigDescription("Enable Perfect Comms diagnostic files and debug log output."));

        SyntheticMicTone = config.Bind("Debug.Advanced", "SyntheticMicTone", false,
            new ConfigDescription("Transmit a quiet generated 48 kHz mono test tone through the active voice backend instead of relying on physical microphone audio."));
        MicCalibrationDiagnostics = config.Bind("Debug", "MicCalibrationDiagnostics", false,
            new ConfigDescription("Log live microphone peak/RMS/gate calibration diagnostics for BetterCrewLink."));

        LobbyBrowserTitle = config.Bind("Lobby Browser", "Title", "Perfect Comms",
            new ConfigDescription("Title shown in the voice lobby browser"));

        LobbyBrowserLanguage = config.Bind("Lobby Browser", "Language", "English",
            new ConfigDescription("Language shown in the voice lobby browser"));

        LobbyBrowserSource = config.Bind("Lobby Browser", "Source",
            VoiceLobbyBrowserSource.BetterCrewLink,
            new ConfigDescription("Main-menu browser view source only. Hosted lobby publishing uses the in-game Lobby Browser Backend option."));

        LobbyRegistryUrl = config.Bind("Lobby Browser", "RegistryUrl",
            "https://perfect-comms-lobbies.edgetel.workers.dev",
            new ConfigDescription("Voice lobby registry endpoint"));

        BetterCrewLinkServerUrl = config.Bind("Voice Server", "BetterCrewLinkServerUrl",
            VoiceEndpointSettings.DefaultBetterCrewLinkServerUrl,
            new ConfigDescription("BetterCrewLink Socket.IO signaling server URL."));

        InterstellarServerUrl = config.Bind("Voice Server", "InterstellarServerUrl",
            VoiceEndpointSettings.DefaultInterstellarServerUrl,
            new ConfigDescription("Interstellar voice server URL. FangkuaiYa's public server is the default fallback."));

        UpdateNotificationsEnabled = config.Bind("Updates", "NotificationsEnabled", true,
            new ConfigDescription("Show Perfect Comms update notifications on the main menu"));

        UpdateNotificationUrl = config.Bind("Updates", "NotificationUrl",
            "https://api.github.com/repos/artriy/Perfect-Comms/releases/latest",
            new ConfigDescription("Perfect Comms GitHub latest-release API endpoint"));

        PerPlayerVolumes = config.Bind("Audio", "PerPlayerVolumes", "",
            "Saved per-player voice volumes keyed by player name");

        VoiceDiagnostics.SetEnabled(DebugVoiceStats.Value);
    }

    private static T ResolveDeviceIndex<T>(string savedName, string[] names, T fallback)
        where T : struct, Enum
    {
        if (!string.IsNullOrEmpty(savedName))
        {
            for (int i = 1; i < names.Length; i++)
            {
                if (DeviceEntryMatches(savedName, names, i))
                    return (T)(object)i;
            }
            return default;
        }
        int idx = (int)(object)fallback;
        return (idx >= 0 && idx < names.Length) ? fallback : default;
    }

    private static bool DeviceEntryMatches(string savedName, string[] names, int index)
    {
        if (string.Equals(names[index], savedName, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static void RefreshDeviceLists()
    {
        var mics = new List<string> { "Default" };
        try
        {
#if WINDOWS
            int count = WaveInEvent.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                var cap  = WaveInEvent.GetCapabilities(i);
                string n = cap.ProductName?.Trim() ?? "";
                if (!string.IsNullOrEmpty(n) && n != "Microsoft Sound Mapper")
                    mics.Add(n);
            }
#elif ANDROID
            foreach (var dev in AndroidMicrophone.GetDeviceNames())
            {
                string n = dev?.Trim() ?? "";
                if (!string.IsNullOrEmpty(n))
                    mics.Add(n);
            }
#endif
        }
        catch { }
        _micDeviceNames = mics.ToArray();

#if WINDOWS
        var spks = new List<string> { "Default" };
        try
        {
            int count = WinMmOutputDevices.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                string n = WinMmOutputDevices.GetProductName(i).Trim();
                if (string.Equals(n, "Microsoft Sound Mapper", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(n))
                    spks.Add(n);
            }
        }
        catch { }
        _spkDeviceNames = spks.ToArray();
#endif
    }

    public override void OnOptionChanged(ConfigEntryBase configEntry)
    {
        if (configEntry == MicVolume)
        {
            VoiceChatRoom.Current?.SetMicVolume(MicVolume.Value);
        }
        else if (configEntry == MicSensitivity)
        {
            VoiceChatRoom.Current?.RefreshLocalAudioSettings();
        }
        else if (configEntry == MasterVolume)
        {
            VoiceChatHudState.ApplySpeakerState();
        }
        else if (configEntry == VoiceFalloffSoftness)
        {
            VoiceAudioOcclusion.ProximitySoftness01 = VoiceFalloffSoftness.Value;
        }
        else if (configEntry == MicMode)
        {
            VoiceChatHudState.ApplyMicState();
        }
        else if (configEntry == DebugVoiceStats)
        {
            VoiceDiagnostics.SetEnabled(DebugVoiceStats.Value);
            VoiceChatRoom.Current?.RefreshLocalAudioSettings();
        }
        else if (configEntry == NoiseGateThreshold || configEntry == VadThreshold ||
                 configEntry == NoiseSuppressionEnabled ||
                 configEntry == SyntheticMicTone ||
                 configEntry == MicCalibrationDiagnostics)
        {
            VoiceChatRoom.Current?.RefreshLocalAudioSettings();
        }
        else if (configEntry == MicrophoneDeviceIndex)
        {
            _savedMicDeviceName.Value = MicrophoneDevice;
            VoiceChatRoom.Current?.SetMicrophone(MicrophoneDevice);
            VoiceChatRoom.Current?.SetMicVolume(MicVolume.Value);
        }
#if WINDOWS
        else if (configEntry == SpeakerDeviceIndex)
        {
            _savedSpkDeviceName.Value = SpeakerDevice;
            VoiceChatRoom.Current?.SetSpeaker(SpeakerDevice);
        }
#endif
        else if (configEntry == ButtonPositionX || configEntry == ButtonPositionY ||
                 configEntry == VoiceControlsLayout)
        {
            VoiceChatHudState.RefreshButtonLayout();
        }
        else if (configEntry == SpeakingBarPosition)
        {
            PingTrackerPatch.ApplySpeakingBarPosition(SpeakingBarPosition.Value);
        }
        else if (configEntry == SpeakingBarManualLayout || configEntry == SpeakingBarX ||
                 configEntry == SpeakingBarY || configEntry == SpeakingBarLayout ||
                 configEntry == SpeakingBarNamePosition)
        {
            PingTrackerPatch.ApplySpeakingBarLayoutSettings();
        }
        else if (configEntry == JailUnmuteButtonPlacement)
        {
            VoiceChatHudState.RefreshButtonLayout();
        }
        else if (configEntry == OverlayScale)
        {
            VoiceChatHudState.ApplyOverlayScale(OverlayScale.Value);
        }
        else if (configEntry == StartMuted)
        {
            VoiceChatHudState.SetMuted(StartMuted.Value);
        }
        else if (configEntry == StartDeafened)
        {
            VoiceChatHudState.SetSpeakerMuted(StartDeafened.Value);
        }
        else if (configEntry == BetterCrewLinkServerUrl || configEntry == InterstellarServerUrl)
        {
            VoiceChatRoom.Current?.Rejoin();
        }
    }

    private static void NormalizeMouseBind(ConfigEntry<VoiceMouseBind> entry)
    {
        int value = Convert.ToInt32(entry.Value);
        if (value < (int)VoiceMouseBind.Off || value > (int)VoiceMouseBind.MB5 ||
            !Enum.IsDefined(typeof(VoiceMouseBind), entry.Value))
        {
            entry.Value = VoiceMouseBind.Off;
        }
    }
}

// No [HarmonyPatch]: target is resolved by reflection and may be absent on a mismatched MiraAPI
// version, which would make auto-discovery throw "Undefined target method". Applied manually from Load().
public static class DeviceLabelPatch
{
    internal static void TryApply(Harmony harmony)
    {
        var target = TargetMethod();
        if (target == null) return; // TargetMethod already logged the reason

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(DeviceLabelPatch), nameof(Postfix)));
            VoiceDiagnostics.DebugInfo("[VC] DeviceLabelPatch applied.");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning($"[VC] DeviceLabelPatch failed to apply: {ex.Message}");
        }
    }

    static System.Reflection.MethodBase? TargetMethod()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("MiraAPI.LocalSettings.SettingTypes.LocalEnumSetting");
            if (t == null) continue;

            foreach (string name in new[]
            {
                "GetValueString", "GetDisplayString", "GetCurrentValueText",
                "GetValue",       "GetLabel",         "GetCurrentValue",
                "ValueToString",  "GetOptionString"
            })
            {
                var m = t.GetMethod(name,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (m != null && m.ReturnType == typeof(string))
                {
                    VoiceDiagnostics.DebugInfo(
                        $"[VC] DeviceLabelPatch targeting {t.Name}.{name}");
                    return m;
                }
            }

            foreach (var m in t.GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (m.ReturnType == typeof(string) && m.GetParameters().Length == 0)
                {
                    VoiceDiagnostics.DebugInfo(
                        $"[VC] DeviceLabelPatch fallback targeting {t.Name}.{m.Name}");
                    return m;
                }
            }
        }

        VoiceDiagnostics.DebugWarning(
            "[VC] DeviceLabelPatch: could not find LocalEnumSetting display method.");
        return null;
    }

    private static DateTime _nextDeviceRefreshUtc = DateTime.MinValue;

    private static void MaybeRefreshDeviceLists()
    {
        var now = DateTime.UtcNow;
        if (now < _nextDeviceRefreshUtc) return;
        _nextDeviceRefreshUtc = now.AddSeconds(2);
        VoiceChatLocalSettings.RefreshDeviceLists();
    }

    static void Postfix(object __instance, ref string __result)
    {
        try
        {
            ConfigEntryBase? entry = null;
            var t = __instance.GetType();

            foreach (string fname in new[] { "_configEntry", "ConfigEntry", "Entry", "_entry" })
            {
                var fi = t.GetField(fname,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (fi?.GetValue(__instance) is ConfigEntryBase e) { entry = e; break; }
            }

            if (entry == null)
            {
                foreach (var pi in t.GetProperties(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance))
                {
                    if (typeof(ConfigEntryBase).IsAssignableFrom(pi.PropertyType))
                    {
                        entry = pi.GetValue(__instance) as ConfigEntryBase;
                        if (entry != null) break;
                    }
                }
            }

            if (entry == null) return;

            int idx = Convert.ToInt32(entry.BoxedValue);

            bool isMic = entry.SettingType == typeof(MicDeviceEnum);
#if WINDOWS
            bool isSpk = entry.SettingType == typeof(SpkDeviceEnum);
#else
            const bool isSpk = false;
#endif
            // Re-enumerate devices (throttled) whenever a device dropdown is rendered so hot-plugged
            // or removed mics/speakers are reflected without a game restart.
            if (isMic || isSpk)
                MaybeRefreshDeviceLists();

            if (isMic)
            {
                var names = VoiceChatLocalSettings.MicDeviceNames;
                __result = idx == 0           ? "Default"
                         : idx < names.Length ? names[idx]
                         : "Default";
            }
#if WINDOWS
            else if (isSpk)
            {
                var names = VoiceChatLocalSettings.SpkDeviceNames;
                __result = idx == 0           ? "Default"
                         : idx < names.Length ? names[idx]
                         : "Default";
            }
#endif
        }
        catch {}
    }
}
