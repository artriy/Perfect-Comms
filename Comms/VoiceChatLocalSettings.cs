using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using UnityEngine;

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

public class VoiceChatLocalSettings
{
    private static volatile string[] _micDeviceNames = Array.Empty<string>();
    private static volatile bool _micNamesFromSidecar;
    private static int _micDeviceListVersion;
#if WINDOWS
    private static Task? _sidecarProbeTask;
    private static volatile string[] _spkDeviceNames = Array.Empty<string>();
    private static volatile bool _spkNamesFromSidecar;
    private static int _spkDeviceListVersion;
#endif

    public static string[] MicDeviceNames => _micDeviceNames;
    public static int MicDeviceListVersion => Volatile.Read(ref _micDeviceListVersion);

    public static void SetMicDeviceNamesFromSidecar(IReadOnlyList<string> names)
    {
        var arr = new string[names.Count + 1];
        arr[0] = "Default";
        for (int i = 0; i < names.Count; i++)
            arr[i + 1] = names[i];
        _micDeviceNames = arr;
        _micNamesFromSidecar = true;
        Interlocked.Increment(ref _micDeviceListVersion);
    }

    public static int SpkDeviceListVersion =>
#if WINDOWS
        Volatile.Read(ref _spkDeviceListVersion);
#else
        0;
#endif
#if WINDOWS
    public static string[] SpkDeviceNames => _spkDeviceNames;

    public static void SetSpkDeviceNamesFromSidecar(IReadOnlyList<string> names)
    {
        var arr = new string[names.Count + 1];
        arr[0] = "Default";
        for (int i = 0; i < names.Count; i++)
            arr[i + 1] = names[i];
        _spkDeviceNames = arr;
        _spkNamesFromSidecar = true;
        Interlocked.Increment(ref _spkDeviceListVersion);
    }
#endif

    // ── Settings ──────────────────────────────────────────────────────────────
    public ConfigEntry<float> MicVolume { get; }
    public ConfigEntry<float> MicSensitivity { get; }
    public ConfigEntry<float> MasterVolume { get; }
    public ConfigEntry<float> VoiceFalloffSoftness { get; }
    public ConfigEntry<VoiceMicMode> MicMode { get; }
    public ConfigEntry<bool> NoiseSuppressionEnabled { get; }
    public ConfigEntry<bool> EchoCancellationEnabled { get; }
    public ConfigEntry<bool> AutoMicGain { get; }
    public ConfigEntry<bool> StartMuted { get; }
    public ConfigEntry<bool> StartDeafened { get; }
    public ConfigEntry<MicDeviceEnum> MicrophoneDeviceIndex { get; }
#if WINDOWS
    public ConfigEntry<SpkDeviceEnum> SpeakerDeviceIndex { get; }
#endif
    public ConfigEntry<float> ButtonPositionX { get; }
    public ConfigEntry<float> ButtonPositionY { get; }
    public ConfigEntry<VoiceControlsLayout> VoiceControlsLayout { get; }
    public ConfigEntry<SpeakingBarPosition> SpeakingBarPosition { get; }
    public ConfigEntry<VoiceControlsLayout> SpeakingBarLayout { get; }
    public ConfigEntry<SpeakingBarNamePosition> SpeakingBarNamePosition { get; }
    public ConfigEntry<bool> SpeakingBarManualLayout { get; }
    public ConfigEntry<bool> SpeakingBarBackdrop { get; }
    public ConfigEntry<float> SpeakingBarScale { get; }
    public ConfigEntry<bool> SpeakingBarFixedAllPlayers { get; }
    public ConfigEntry<bool> MeetingSpeakingOverlay { get; }
    public ConfigEntry<JailUnmuteButtonPlacement> JailUnmuteButtonPlacement { get; }
    public ConfigEntry<float> SpeakingBarX { get; }
    public ConfigEntry<float> SpeakingBarY { get; }
    public ConfigEntry<float> OverlayScale { get; }

    // User-facing toggle (default on). When on, the BetterCrewLink backend offers a TURN relay alongside
    // STUN so peers that can't establish a direct connection (strict/symmetric NAT, firewalls) still get
    // audio. Only the peers that actually need it relay; everyone else stays direct. BCL backend only.
    public ConfigEntry<bool> NatFix { get; }

    // Wine/Proton (Linux) only. When on, the BCL backend forces TURN-relay-only ICE and adds a TURN-over-TCP
    // candidate, because Wine's local ICE candidate gathering is unreliable and direct/STUN never connect.
    // Ignored on native Windows. Default on.
    public ConfigEntry<bool> WineForceRelay { get; }

    public ConfigEntry<bool> DebugVoiceStats { get; }
    public ConfigEntry<bool> MicCalibrationDiagnostics { get; }

    public ConfigEntry<float> NoiseGateThreshold { get; }
    public ConfigEntry<float> VadThreshold { get; }
    public ConfigEntry<bool> SyntheticMicTone { get; }

    public ConfigEntry<string> PerPlayerVolumes { get; }
    public ConfigEntry<string> LobbyBrowserTitle { get; }
    public ConfigEntry<string> LobbyBrowserLanguage { get; }
    public ConfigEntry<VoiceLobbyBrowserSource> LobbyBrowserSource { get; }
    public ConfigEntry<string> LobbyRegistryUrl { get; }
    public ConfigEntry<string> BetterCrewLinkServerUrl { get; }
    public ConfigEntry<string> InterstellarServerUrl { get; }

    // Config-file only (not shown in the in-game menu): the TURN relay used by Nat Fix. Defaults to
    // BetterCrewLink's public relay; power users can point these at their own coturn server.
    public ConfigEntry<string> TurnServerUrl { get; }
    public ConfigEntry<string> TurnUsername { get; }
    public ConfigEntry<string> TurnCredential { get; }
    public ConfigEntry<bool> UpdateNotificationsEnabled { get; }
    public ConfigEntry<string> UpdateNotificationUrl { get; }

    private readonly ConfigFile _config;
    private readonly ConfigEntry<string> _savedMicDeviceName;
#if WINDOWS
    private readonly ConfigEntry<string> _savedSpkDeviceName;
#endif

    private bool _correcting;

    private bool _applyingDiagnosticsToggle;

    // Writes both diagnostics entries under one suppression flag so the pair triggers a single APM rebuild, not two.
    public void ApplyDiagnosticsToggle(bool enabled)
    {
        _applyingDiagnosticsToggle = true;
        try
        {
            DebugVoiceStats.Value = enabled;
            MicCalibrationDiagnostics.Value = enabled;
        }
        finally { _applyingDiagnosticsToggle = false; }
        VoiceDiagnostics.SetEnabled(enabled);
        VoiceChatRoom.Current?.RefreshLocalAudioSettings();
    }

    public string MicrophoneDevice => _savedMicDeviceName?.Value ?? "";

    private string MicDeviceNameAtCurrentIndex()
    {
        var names = _micDeviceNames;
        int idx = (int)MicrophoneDeviceIndex.Value;
        return idx > 0 && idx < names.Length ? names[idx] : "";
    }

#if WINDOWS
    public string SpeakerDevice => _savedSpkDeviceName?.Value ?? "";

    private string SpkDeviceNameAtCurrentIndex()
    {
        var names = _spkDeviceNames;
        int idx = (int)SpeakerDeviceIndex.Value;
        return idx > 0 && idx < names.Length ? names[idx] : "";
    }
#endif

    public VoiceChatLocalSettings(ConfigFile config)
    {
        _config = config;
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

        SpeakingBarBackdrop = config.Bind("UI", "SpeakingBarBackdrop", false,
            new ConfigDescription("Show a translucent dark backdrop behind the speaking bar."));

        SpeakingBarScale = config.Bind("UI", "SpeakingBarScale", 1.0f,
            new ConfigDescription("Scale of the speaking bar",
                new AcceptableValueRange<float>(0.5f, 2.0f)));

        SpeakingBarFixedAllPlayers = config.Bind("UI", "SpeakingBarFixedAllPlayers", false,
            new ConfigDescription("Show a fixed slot for every player during tasks instead of only current speakers."));

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

        EchoCancellationEnabled = config.Bind("Audio", "EchoCancellationEnabled", true,
            new ConfigDescription("Cancel echo/feedback of incoming voice picked up by your microphone."));

        AutoMicGain = config.Bind("Audio", "AutoMicGain", true,
            new ConfigDescription("Automatically boost quiet microphones toward a consistent speech level before noise suppression and the noise gate."));

        DebugVoiceStats = config.Bind("Debug", "DebugVoiceStats", false,
            new ConfigDescription("Enable Perfect Comms diagnostic files and debug log output."));

        SyntheticMicTone = config.Bind("Debug.Advanced", "SyntheticMicTone", false,
            new ConfigDescription("Transmit a quiet generated 48 kHz mono test tone through the active voice backend instead of relying on physical microphone audio."));
        MicCalibrationDiagnostics = config.Bind("Debug", "MicCalibrationDiagnostics", false,
            new ConfigDescription("Log live microphone peak/RMS/gate calibration diagnostics for BetterCrewLink."));

        // Debug toggles always start OFF on every game launch, even if a previous session left one on. They
        // still work when turned on mid-session; they just never persist across a restart, so diagnostic
        // logging, the frame profiler, and the synthetic test tone can't be accidentally left running.
        DebugVoiceStats.Value = false;
        MicCalibrationDiagnostics.Value = false;
        SyntheticMicTone.Value = false;

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

        NatFix = config.Bind("Voice Server", "NatFix", true,
            new ConfigDescription("Route voice through a TURN relay when a direct peer-to-peer connection can't be established (fixes no/garbled audio behind strict or symmetric NATs and firewalls). Only peers that actually need it relay; everyone else stays direct. BetterCrewLink backend only."));

        TurnServerUrl = config.Bind("Voice Server", "TurnServerUrl",
            "",
            new ConfigDescription("Optional custom TURN relay for Nat Fix. Leave empty to use the project's managed TURN credentials (fetched at runtime); set your own coturn/TURN server here to override."));
        TurnUsername = config.Bind("Voice Server", "TurnUsername",
            "",
            new ConfigDescription("Username for a custom Nat Fix TURN relay (only used when TurnServerUrl is set)."));
        TurnCredential = config.Bind("Voice Server", "TurnCredential",
            "",
            new ConfigDescription("Credential (password) for a custom Nat Fix TURN relay (only used when TurnServerUrl is set)."));
        WineForceRelay = config.Bind("Voice Server", "WineForceRelay", false,
            new ConfigDescription("Wine/Proton (Linux) only opt-in: force TURN-relay-only voice (and add TURN-over-TCP). Off by default - Wine now uses the same automatic ICE selection as native Windows, using TURN only when direct/STUN fail. Enable only if your Wine setup still cannot connect. Ignored on native Windows. Requires Nat Fix on with valid TURN credentials."));

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

    // Subscribe AFTER construction (so the ctor's own initial .Value assignments don't dispatch). A single
    // global ConfigFile.SettingChanged subscription routes every value change (from the in-game panel OR a
    // config-file edit) into the same runtime-apply dispatch that MiraAPI used to drive.
    public void WireRuntimeHandlers()
    {
        _config.SettingChanged += (_, args) =>
        {
            if (args is SettingChangedEventArgs changed)
            {
                try { Dispatch(changed.ChangedSetting); }
                catch (Exception ex) { VoiceDiagnostics.DebugWarning($"[VC] Setting dispatch failed: {ex.Message}"); }
            }
        };
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
        if (!_micNamesFromSidecar)
        {
            var mics = new List<string> { "Default" };
            try
            {
#if ANDROID
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
        }

#if WINDOWS
        if (!_spkNamesFromSidecar)
            _spkDeviceNames = new[] { "Default" };
#endif
    }

    private static DateTime _nextDeviceRefreshUtc = DateTime.MinValue;

    // Re-enumerate devices (throttled to every 2s) so hot-plugged or removed mics/speakers show up in the
    // in-game device pickers without a game restart. Called from the settings panel's device rows.
    public static void MaybeRefreshDeviceLists()
    {
        var now = DateTime.UtcNow;
        if (now >= _nextDeviceRefreshUtc)
        {
            _nextDeviceRefreshUtc = now.AddSeconds(2);
            RefreshDeviceLists();
            MaybeProbeSidecarDevices();
        }
        VoiceSettings.Instance?.ResolveMicIndexIfListChanged();
        VoiceSettings.Instance?.ResolveSpkIndexIfListChanged();
    }

    private static void MaybeProbeSidecarDevices()
    {
#if WINDOWS
        if (!SidecarLauncher.IsHelperAvailable()) return;
        if (_sidecarProbeTask != null && !_sidecarProbeTask.IsCompleted) return;
        _sidecarProbeTask = Task.Run(() =>
        {
            try
            {
                var (inputs, outputs) = SidecarLauncher.EnumerateDevices();
                if (inputs.Count > 0)
                    SetMicDeviceNamesFromSidecar(inputs);
                if (outputs.Count > 0)
                    SetSpkDeviceNamesFromSidecar(outputs);
            }
            catch { }
        });
#endif
    }

    private int _lastResolvedMicVersion = -1;

    internal void ResolveMicIndexIfListChanged()
    {
        int version = MicDeviceListVersion;
        if (version == _lastResolvedMicVersion) return;
        _lastResolvedMicVersion = version;
        var resolved = ResolveDeviceIndex<MicDeviceEnum>(_savedMicDeviceName.Value, _micDeviceNames, MicrophoneDeviceIndex.Value);
        if (!string.IsNullOrEmpty(_savedMicDeviceName.Value) && (int)(object)resolved <= 0)
            return;
        if (!resolved.Equals(MicrophoneDeviceIndex.Value))
            MicrophoneDeviceIndex.Value = resolved;
    }

#if WINDOWS
    private int _lastResolvedSpkVersion = -1;

    internal void ResolveSpkIndexIfListChanged()
    {
        int version = SpkDeviceListVersion;
        if (version == _lastResolvedSpkVersion) return;
        _lastResolvedSpkVersion = version;
        var resolved = ResolveDeviceIndex<SpkDeviceEnum>(_savedSpkDeviceName.Value, _spkDeviceNames, SpeakerDeviceIndex.Value);
        if (!string.IsNullOrEmpty(_savedSpkDeviceName.Value) && (int)(object)resolved <= 0)
            return;
        if (!resolved.Equals(SpeakerDeviceIndex.Value))
            SpeakerDeviceIndex.Value = resolved;
    }
#else
    internal void ResolveSpkIndexIfListChanged() { }
#endif

    internal void Dispatch(ConfigEntryBase configEntry)
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
            if (_applyingDiagnosticsToggle) return;
            VoiceDiagnostics.SetEnabled(DebugVoiceStats.Value);
            VoiceChatRoom.Current?.RefreshLocalAudioSettings();
        }
        else if (configEntry == NoiseGateThreshold || configEntry == VadThreshold ||
                 configEntry == NoiseSuppressionEnabled || configEntry == EchoCancellationEnabled || configEntry == AutoMicGain ||
                 configEntry == SyntheticMicTone ||
                 configEntry == MicCalibrationDiagnostics)
        {
            if (_applyingDiagnosticsToggle) return;
            VoiceChatRoom.Current?.RefreshLocalAudioSettings();
        }
        else if (configEntry == MicrophoneDeviceIndex)
        {
            var name = MicDeviceNameAtCurrentIndex();
            bool deviceChanged = !string.Equals(_savedMicDeviceName.Value, name, StringComparison.Ordinal);
            _savedMicDeviceName.Value = name;
            if (deviceChanged)
                VoiceChatRoom.Current?.SetMicrophone(name);
            VoiceChatRoom.Current?.SetMicVolume(MicVolume.Value);
        }
#if WINDOWS
        else if (configEntry == SpeakerDeviceIndex)
        {
            var name = SpkDeviceNameAtCurrentIndex();
            bool deviceChanged = !string.Equals(_savedSpkDeviceName.Value, name, StringComparison.Ordinal);
            _savedSpkDeviceName.Value = name;
            if (deviceChanged)
                VoiceChatRoom.Current?.SetSpeaker(name);
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
                 configEntry == SpeakingBarNamePosition || configEntry == SpeakingBarBackdrop ||
                 configEntry == SpeakingBarScale)
        {
            PingTrackerPatch.ApplySpeakingBarLayoutSettings();
        }
        else if (configEntry == SpeakingBarFixedAllPlayers)
        {
            PingTrackerPatch.ApplySpeakingBarLayoutSettings();
            PingTrackerPatch.ClearSpeakingBarSlots();
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
        else if (configEntry == NatFix || configEntry == TurnServerUrl ||
                 configEntry == TurnUsername || configEntry == TurnCredential ||
                 configEntry == WineForceRelay)
        {
            // Recreate the native peer generation so the new direct/relay policy takes effect immediately.
            // This does not leave or rejoin the game lobby.
            VoiceChatRoom.Current?.RebuildIceConnectionPool();
        }
    }

}
