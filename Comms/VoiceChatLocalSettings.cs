using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public enum MicDeviceEnum
{
    Default = 0,
    Device1 = 1, Device2 = 2, Device3 = 3, Device4 = 4,
    Device5 = 5, Device6 = 6, Device7 = 7, Device8 = 8,
    Device9 = 9, Device10 = 10
}

public enum SpkDeviceEnum
{
    Default = 0,
    Device1 = 1, Device2 = 2, Device3 = 3, Device4 = 4,
    Device5 = 5, Device6 = 6, Device7 = 7, Device8 = 8,
    Device9 = 9, Device10 = 10
}

public enum SpeakingBarPosition
{
    TopLeft = 0,
    TopMiddle = 1,
    TopRight = 2,
    MiddleLeft = 6,
    MiddleRight = 7,
    BottomLeft = 3,
    BottomMiddle = 4,
    BottomRight = 5,
}

public enum VoiceControlsLayout
{
    Vertical = 0,
    Horizontal = 1,
}

public enum SpeakingBarNamePosition
{
    Bottom = 0,
    Top = 1,
    Left = 2,
    Right = 3,
    // Keep the four explicit positions at their legacy numeric values so existing
    // config files continue to represent a user override. Auto is the v4 default.
    Auto = 4,
}

public enum SpeakingBarAvatarFacing
{
    Right = 0,
    Left = 1,
}

public enum SpeakingBarSideLayout
{
    SingleLane = 0,
    Wrapped = 1,
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

internal readonly record struct VoiceHudFeatureVisibility(
    bool VoiceControlsHudVisible,
    bool SpeakingBarVisible)
{
    internal static VoiceHudFeatureVisibility Resolve(
        bool disableVoiceControlsHud,
        bool disableSpeakingBar)
        => new(!disableVoiceControlsHud, !disableSpeakingBar);
}
internal readonly record struct VoiceHudControlVisibility(
    bool PrimaryControlsVisible,
    bool JailUnmuteVisible);

internal static class VoiceHudControlVisibilityPolicy
{
    internal static VoiceHudControlVisibility Resolve(
        bool primaryControlsEnabled,
        bool canLocalJailorUnmute)
        => new(primaryControlsEnabled, canLocalJailorUnmute);
}


public class VoiceChatLocalSettings
{
    internal const bool SpeakingBarLivePreviewDefault = false;

    private static volatile VoiceDeviceInfo[] _micDevices = Array.Empty<VoiceDeviceInfo>();
    private static volatile string[] _micDeviceNames = Array.Empty<string>();
    private static volatile string _savedMicIdForPublication = string.Empty;
    private static volatile string _savedMicNameForPublication = string.Empty;
    private static volatile bool _micNamesFromSidecar;
    private static int _micDeviceListVersion;
#if WINDOWS
    private static Task? _sidecarProbeTask;
    private static volatile VoiceDeviceInfo[] _spkDevices = Array.Empty<VoiceDeviceInfo>();
    private static volatile string[] _spkDeviceNames = Array.Empty<string>();
    private static volatile string _savedSpkIdForPublication = string.Empty;
    private static volatile string _savedSpkNameForPublication = string.Empty;
    private static volatile bool _spkNamesFromSidecar;
    private static int _spkDeviceListVersion;
#endif

    internal static VoiceDeviceInfo[] MicDevices => _micDevices;
    public static string[] MicDeviceNames => _micDeviceNames;
    public static int MicDeviceListVersion => Volatile.Read(ref _micDeviceListVersion);

    internal static void SetMicDevicesFromSidecar(IReadOnlyList<VoiceDeviceInfo> devices)
    {
        var arr = new VoiceDeviceInfo[devices.Count + 1];
        arr[0] = new VoiceDeviceInfo(string.Empty, "Default", true);
        for (int i = 0; i < devices.Count; i++)
            arr[i + 1] = devices[i];
        arr = WithUnavailableSelection(
            arr, _savedMicIdForPublication, _savedMicNameForPublication,
            "Saved microphone");
        _micNamesFromSidecar = true;
        PublishMicDevices(arr);
    }

    // Kept for compatibility with older callers/tests. Desktop helper paths publish structured
    // entries; Android legitimately uses the Unity microphone name as its selection identifier.
    public static void SetMicDeviceNamesFromSidecar(IReadOnlyList<string> names)
        => SetMicDevicesFromSidecar(ToNameBasedDevices(names));

    public static int SpkDeviceListVersion =>
#if WINDOWS
        Volatile.Read(ref _spkDeviceListVersion);
#else
        0;
#endif
#if WINDOWS
    internal static VoiceDeviceInfo[] SpkDevices => _spkDevices;
    public static string[] SpkDeviceNames => _spkDeviceNames;
    internal static bool SidecarDeviceProbePending =>
        _sidecarProbeTask != null && !_sidecarProbeTask.IsCompleted;

    internal static void SetSpkDevicesFromSidecar(IReadOnlyList<VoiceDeviceInfo> devices)
    {
        var arr = new VoiceDeviceInfo[devices.Count + 1];
        arr[0] = new VoiceDeviceInfo(string.Empty, "Default", true);
        for (int i = 0; i < devices.Count; i++)
            arr[i + 1] = devices[i];
        arr = WithUnavailableSelection(
            arr, _savedSpkIdForPublication, _savedSpkNameForPublication,
            "Saved speaker");
        _spkNamesFromSidecar = true;
        PublishSpkDevices(arr);
    }

    internal static bool PublishSidecarDeviceEnumeration(
        SidecarDeviceEnumerationResult enumeration)
    {
        if (!enumeration.IsAuthoritative)
            return false;
        SetMicDevicesFromSidecar(enumeration.Input);
        SetSpkDevicesFromSidecar(enumeration.Output);
        return true;
    }

    public static void SetSpkDeviceNamesFromSidecar(IReadOnlyList<string> names)
        => SetSpkDevicesFromSidecar(ToNameBasedDevices(names));
#endif

    private static VoiceDeviceInfo[] ToNameBasedDevices(IReadOnlyList<string> names)
    {
        var devices = new VoiceDeviceInfo[names.Count];
        for (int i = 0; i < names.Count; i++)
            devices[i] = VoiceDeviceInfo.FromName(names[i]);
        return devices;
    }

    internal static VoiceDeviceInfo[] WithUnavailableSelection(
        VoiceDeviceInfo[] availableDevices,
        string savedId,
        string savedName,
        string fallbackName)
    {
        if (string.IsNullOrEmpty(savedId) && string.IsNullOrEmpty(savedName))
            return availableDevices;

        for (int i = 1; i < availableDevices.Length; i++)
        {
            bool matches = !string.IsNullOrEmpty(savedId)
                ? string.Equals(availableDevices[i].Id, savedId, StringComparison.Ordinal)
                : string.Equals(availableDevices[i].Name, savedName, StringComparison.OrdinalIgnoreCase);
            if (matches)
                return availableDevices;
        }

        Array.Resize(ref availableDevices, availableDevices.Length + 1);
        availableDevices[^1] = VoiceDeviceInfo.Unavailable(savedId, savedName, fallbackName);
        return availableDevices;
    }

    private static bool SameDevices(IReadOnlyList<VoiceDeviceInfo> first, IReadOnlyList<VoiceDeviceInfo> second)
    {
        if (first.Count != second.Count) return false;
        for (int i = 0; i < first.Count; i++)
            if (!first[i].Equals(second[i]))
                return false;
        return true;
    }

    private static string[] DeviceNames(IReadOnlyList<VoiceDeviceInfo> devices)
    {
        var names = new string[devices.Count];
        for (int i = 0; i < devices.Count; i++)
            names[i] = devices[i].IsAvailable
                ? devices[i].Name
                : devices[i].Name + " (unavailable)";
        return names;
    }

    private static void PublishMicDevices(VoiceDeviceInfo[] devices)
    {
        if (SameDevices(_micDevices, devices)) return;
        _micDevices = devices;
        _micDeviceNames = DeviceNames(devices);
        Interlocked.Increment(ref _micDeviceListVersion);
    }

#if WINDOWS
    private static void PublishSpkDevices(VoiceDeviceInfo[] devices)
    {
        if (SameDevices(_spkDevices, devices)) return;
        _spkDevices = devices;
        _spkDeviceNames = DeviceNames(devices);
        Interlocked.Increment(ref _spkDeviceListVersion);
    }
#endif

    // ── Settings ──────────────────────────────────────────────────────────────
    public ConfigEntry<float> MicVolume { get; }
    public ConfigEntry<float> MicSensitivity { get; }
    public ConfigEntry<float> MasterVolume { get; }
    public ConfigEntry<float> AliveFocusAliveVolume { get; }
    public ConfigEntry<float> AliveFocusDeadVolume { get; }
    public ConfigEntry<float> DeadFocusAliveVolume { get; }
    public ConfigEntry<float> DeadFocusDeadVolume { get; }
    internal VoiceAliveDeadMixProfile AliveFocusProfile =>
        new(AliveFocusAliveVolume.Value, AliveFocusDeadVolume.Value);
    internal VoiceAliveDeadMixProfile DeadFocusProfile =>
        new(DeadFocusAliveVolume.Value, DeadFocusDeadVolume.Value);
    public ConfigEntry<float> VoiceFalloffSoftness { get; }
    public ConfigEntry<VoiceMicMode> MicMode { get; }
    public ConfigEntry<bool> NoiseSuppressionEnabled { get; }
    public ConfigEntry<bool> StrongerNoiseSuppressionEnabled { get; }
    public ConfigEntry<bool> EchoCancellationEnabled { get; }
    public ConfigEntry<bool> StartMuted { get; }
    public ConfigEntry<bool> StartDeafened { get; }
    public ConfigEntry<MicDeviceEnum> MicrophoneDeviceIndex { get; }
#if WINDOWS
    public ConfigEntry<SpkDeviceEnum> SpeakerDeviceIndex { get; }
#endif
    public ConfigEntry<float> ButtonPositionX { get; }
    public ConfigEntry<float> ButtonPositionY { get; }
    public ConfigEntry<bool> ShowMuteDeafenStatusAlerts { get; }
    public ConfigEntry<bool> DisableVoiceControlsHud { get; }
    public ConfigEntry<VoiceControlsLayout> VoiceControlsLayout { get; }
    public ConfigEntry<bool> DisableSpeakingBar { get; }
    public ConfigEntry<SpeakingBarPosition> SpeakingBarPosition { get; }
    public ConfigEntry<SpeakingBarSideLayout> SpeakingBarSideLayout { get; }
    public ConfigEntry<VoiceControlsLayout> SpeakingBarLayout { get; }
    public ConfigEntry<SpeakingBarNamePosition> SpeakingBarNamePosition { get; }
    public ConfigEntry<SpeakingBarAvatarFacing> SpeakingBarAvatarFacing { get; }
    public ConfigEntry<bool> SpeakingBarManualLayout { get; }
    public ConfigEntry<bool> SpeakingBarBackdrop { get; }
    public ConfigEntry<float> SpeakingBarScale { get; }
    public ConfigEntry<bool> SpeakingBarFixedAllPlayers { get; }
    public ConfigEntry<bool> SpeakingBarLivePreview { get; }
    public ConfigEntry<bool> ShowFake15Players { get; }
    public ConfigEntry<bool> MeetingSpeakingOverlay { get; }
    public ConfigEntry<JailUnmuteButtonPlacement> JailUnmuteButtonPlacement { get; }
    public ConfigEntry<float> SpeakingBarX { get; }
    public ConfigEntry<float> SpeakingBarY { get; }
    public ConfigEntry<float> OverlayScale { get; }

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

    // Config-file only (not shown in the in-game menu): optional custom TURN credentials for automatic fallback.
    // Empty values use short-lived managed credentials from the configured Perfect Comms registry.
    public ConfigEntry<string> TurnServerUrl { get; }
    public ConfigEntry<string> TurnUsername { get; }
    public ConfigEntry<string> TurnCredential { get; }
    public ConfigEntry<bool> UpdateNotificationsEnabled { get; }
    public ConfigEntry<string> UpdateNotificationUrl { get; }
    internal ConfigEntry<int> CompletedSetupRevision { get; }

    private readonly ConfigFile _config;
    private readonly ConfigEntry<int> _speakingBarSettingsVersion;
    private readonly ConfigEntry<string> _savedMicDeviceId;
    private readonly ConfigEntry<string> _savedMicDeviceName;
#if WINDOWS
    private readonly ConfigEntry<string> _savedSpkDeviceId;
    private readonly ConfigEntry<string> _savedSpkDeviceName;
#endif

    private bool _correcting;
    private bool _suppressInternalDeviceSelectionDispatch;

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

    internal static void ResetTransientTogglesForLaunch(
        ConfigEntry<bool> speakingBarLivePreview,
        ConfigEntry<bool> showFake15Players,
        ConfigEntry<bool> debugVoiceStats,
        ConfigEntry<bool> micCalibrationDiagnostics,
        ConfigEntry<bool> syntheticMicTone)
    {
        var reset = TransientVoiceTogglePolicy.ResetForLaunch(new TransientVoiceToggleState(
            speakingBarLivePreview.Value,
            showFake15Players.Value,
            debugVoiceStats.Value,
            micCalibrationDiagnostics.Value,
            syntheticMicTone.Value));
        speakingBarLivePreview.Value = reset.SpeakingBarLivePreview;
        showFake15Players.Value = reset.ShowFake15Players;
        debugVoiceStats.Value = reset.DebugVoiceStats;
        micCalibrationDiagnostics.Value = reset.MicCalibrationDiagnostics;
        syntheticMicTone.Value = reset.SyntheticMicTone;
    }

    /// <summary>The opaque helper/Unity selection identifier. Never use it as UI text.</summary>
    public string MicrophoneDevice => _savedMicDeviceId?.Value ?? "";
    internal string MicrophoneDeviceName => _savedMicDeviceName?.Value ?? "";

    private VoiceDeviceInfo MicDeviceAtCurrentIndex()
    {
        var devices = _micDevices;
        int idx = (int)MicrophoneDeviceIndex.Value;
        return idx >= 0 && idx < devices.Length ? devices[idx] : default;
    }

#if WINDOWS
    /// <summary>The opaque helper selection identifier. Never use it as UI text.</summary>
    public string SpeakerDevice => _savedSpkDeviceId?.Value ?? "";
    internal string SpeakerDeviceName => _savedSpkDeviceName?.Value ?? "";

    private VoiceDeviceInfo SpkDeviceAtCurrentIndex()
    {
        var devices = _spkDevices;
        int idx = (int)SpeakerDeviceIndex.Value;
        return idx >= 0 && idx < devices.Length ? devices[idx] : default;
    }
#endif

    public VoiceChatLocalSettings(ConfigFile config)
    {
        _config = config;
        string existingConfigText = ReadExistingConfigText(config.ConfigFilePath);
        bool hadLegacySpeakingBarScale = SpeakingBarScalePolicy.ConfigTextContainsSetting(
            existingConfigText,
            "UI",
            "SpeakingBarScale");
        bool hadRetiredNatFix = SpeakingBarScalePolicy.ConfigTextContainsSetting(
            existingConfigText,
            "Voice Server",
            "NatFix");
        bool hadRetiredWineForceRelay = SpeakingBarScalePolicy.ConfigTextContainsSetting(
            existingConfigText,
            "Voice Server",
            "WineForceRelay");
        RefreshDeviceLists();

        MicVolume = config.Bind("Audio", "MicVolume", 1f,
            new ConfigDescription("Adjusts how loudly your microphone is sent to other players. This does not change when the mic counts as speaking.",
                new AcceptableValueRange<float>(0.1f, 2f)));

        MicSensitivity = config.Bind("Audio", "MicSensitivity", 1f,
            new ConfigDescription("Controls how easily your mic detects speech. Higher values pick up quieter speech; lower values ignore more room noise.",
                new AcceptableValueRange<float>(0.25f, 2f)));

        MasterVolume = config.Bind("Audio", "MasterVolume", 1f,
            new ConfigDescription("Adjusts the overall volume of all Perfect Comms voice audio you hear.",
                new AcceptableValueRange<float>(0.1f, 2f)));

        AliveFocusAliveVolume = config.Bind("Audio.HoldMix", "AliveFocusAliveVolume",
            VoiceVolumeMath.DefaultLouderVolume,
            new ConfigDescription(
                "Sets the volume of audible living players while Alive Louder / Dead Quieter is held. 100% is normal; 0% mutes that group.",
                new AcceptableValueRange<float>(0f, 2f)));

        AliveFocusDeadVolume = config.Bind("Audio.HoldMix", "AliveFocusDeadVolume",
            VoiceVolumeMath.DefaultQuieterVolume,
            new ConfigDescription(
                "Sets the volume of audible dead players while Alive Louder / Dead Quieter is held. 100% is normal; 0% mutes that group.",
                new AcceptableValueRange<float>(0f, 2f)));

        DeadFocusAliveVolume = config.Bind("Audio.HoldMix", "DeadFocusAliveVolume",
            VoiceVolumeMath.DefaultQuieterVolume,
            new ConfigDescription(
                "Sets the volume of audible living players while Alive Quieter / Dead Louder is held. 100% is normal; 0% mutes that group.",
                new AcceptableValueRange<float>(0f, 2f)));

        DeadFocusDeadVolume = config.Bind("Audio.HoldMix", "DeadFocusDeadVolume",
            VoiceVolumeMath.DefaultLouderVolume,
            new ConfigDescription(
                "Sets the volume of audible dead players while Alive Quieter / Dead Louder is held. 100% is normal; 0% mutes that group.",
                new AcceptableValueRange<float>(0f, 2f)));

        VoiceFalloffSoftness = config.Bind("Audio", "VoiceFalloffSoftness", 0.30f,
            new ConfigDescription(
                "How gently voices fade near the edge of vision/range. 0% keeps the original fade; higher keeps voices clear across most of your vision and fades only near the edge. Layers on top of the host's falloff and never extends hearing range.",
                new AcceptableValueRange<float>(0f, 1f)));
        VoiceAudioOcclusion.ProximitySoftness01 = VoiceFalloffSoftness.Value;

        MicMode = config.Bind("Audio", "MicMode", VoiceMicMode.OpenMic,
            new ConfigDescription("Chooses whether your mic transmits automatically when you speak or only while Push To Talk is held."));

        NoiseGateThreshold = config.Bind("Audio.Advanced", "NoiseGateThreshold", 0.003f,
            new ConfigDescription("Advanced base gate threshold. Effective value is divided by MicSensitivity.",
                new AcceptableValueRange<float>(0.003f, 0.10f)));

        VadThreshold = config.Bind("Audio.Advanced", "VadThreshold", 0.004f,
            new ConfigDescription("Advanced base speaking indicator threshold. Effective value is divided by MicSensitivity.",
                new AcceptableValueRange<float>(0.002f, 0.080f)));

        StartMuted = config.Bind("Audio", "StartMuted", false,
            new ConfigDescription("Starts each voice session with your microphone muted."));

        StartDeafened = config.Bind("Audio", "StartDeafened", false,
            new ConfigDescription("Starts each voice session deafened: voice playback is muted and microphone transmission is paused until you undeafen."));

        _savedMicDeviceId = config.Bind("Audio", "MicDeviceId", "",
            "Stable microphone device identifier. Display names are stored separately and are not used for selection.");
        _savedMicDeviceName = config.Bind("Audio", "MicDeviceName", "",
            "Last known microphone display name (also used once to migrate pre-v4 settings)");

#if WINDOWS
        _savedSpkDeviceId = config.Bind("Audio", "SpkDeviceId", "",
            "Stable speaker device identifier. Display names are stored separately and are not used for selection.");
        _savedSpkDeviceName = config.Bind("Audio", "SpkDeviceName", "",
            "Last known speaker display name (also used once to migrate pre-v4 settings)");
#endif

        MicrophoneDeviceIndex = config.Bind("Audio", "Microphone",
            MicDeviceEnum.Default,
            new ConfigDescription("Selects the recording device Perfect Comms uses. Default follows the system's default input device."));
#if WINDOWS
        SpeakerDeviceIndex = config.Bind("Audio", "Speaker",
            SpkDeviceEnum.Default,
            new ConfigDescription("Selects the playback device Perfect Comms uses for voice audio. Default follows the system's default output device."));
#endif

        ApplyLegacyDefaultCanonicalization(
            (int)MicrophoneDeviceIndex.Value, _savedMicDeviceId, _savedMicDeviceName);
#if WINDOWS
        ApplyLegacyDefaultCanonicalization(
            (int)SpeakerDeviceIndex.Value, _savedSpkDeviceId, _savedSpkDeviceName);
#endif

        _savedMicIdForPublication = _savedMicDeviceId.Value;
        _savedMicNameForPublication = _savedMicDeviceName.Value;
#if WINDOWS
        _savedSpkIdForPublication = _savedSpkDeviceId.Value;
        _savedSpkNameForPublication = _savedSpkDeviceName.Value;
#endif

        PublishMicDevices(WithUnavailableSelection(
            _micDevices, _savedMicDeviceId.Value, _savedMicDeviceName.Value,
            "Saved microphone"));
#if WINDOWS
        PublishSpkDevices(WithUnavailableSelection(
            _spkDevices, _savedSpkDeviceId.Value, _savedSpkDeviceName.Value,
            "Saved speaker"));
#endif

        MicrophoneDeviceIndex.Value = ResolveDeviceIndex<MicDeviceEnum>(
            _savedMicDeviceId.Value, _savedMicDeviceName.Value, _micDevices,
            MicrophoneDeviceIndex.Value, MicDeviceListIsAuthoritative,
            recoverMissingStableId: ShouldRecoverLegacyDesktopDeviceId(_savedMicDeviceId.Value),
            out var resolvedMic);
        MigrateLegacyDeviceSelection(_savedMicDeviceId, _savedMicDeviceName, resolvedMic);
        _savedMicIdForPublication = _savedMicDeviceId.Value;
        _savedMicNameForPublication = _savedMicDeviceName.Value;
        _lastResolvedMicId = resolvedMic?.Id ?? _savedMicDeviceId.Value;
        _lastResolvedMicAvailable = resolvedMic is { IsAvailable: true };

        MicrophoneDeviceIndex.SettingChanged += (_, _) =>
        {
            if (_correcting) return;
            int newIdx = (int)MicrophoneDeviceIndex.Value;
            int count = _micDevices.Length;
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
        SpeakerDeviceIndex.Value = ResolveDeviceIndex<SpkDeviceEnum>(
            _savedSpkDeviceId.Value, _savedSpkDeviceName.Value, _spkDevices,
            SpeakerDeviceIndex.Value, _spkNamesFromSidecar,
            recoverMissingStableId: true,
            out var resolvedSpeaker);
        MigrateLegacyDeviceSelection(_savedSpkDeviceId, _savedSpkDeviceName, resolvedSpeaker);
        _savedSpkIdForPublication = _savedSpkDeviceId.Value;
        _savedSpkNameForPublication = _savedSpkDeviceName.Value;
        _lastResolvedSpkId = resolvedSpeaker?.Id ?? _savedSpkDeviceId.Value;
        _lastResolvedSpkAvailable = resolvedSpeaker is { IsAvailable: true };

        SpeakerDeviceIndex.SettingChanged += (_, _) =>
        {
            if (_correcting) return;
            int newIdx = (int)SpeakerDeviceIndex.Value;
            int count = _spkDevices.Length;
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

        ShowMuteDeafenStatusAlerts = config.Bind("UI", "ShowMuteDeafenStatusAlerts", true,
            new ConfigDescription("Shows a small persistent HUD reminder while the microphone is muted or voice playback is deafened."));

        DisableVoiceControlsHud = config.Bind("UI", "DisableVoiceControlsHud", false,
            new ConfigDescription("Hides the microphone, speaker, and mobile radio controls while keeping their keybinds and the Jailor unmute button active."));

        VoiceControlsLayout = config.Bind("UI", "VoiceControlsLayout",
            VoiceChatPlugin.VoiceChat.VoiceControlsLayout.Vertical,
            new ConfigDescription("Arranges the microphone, speaker, and role voice controls vertically or horizontally."));

        DisableSpeakingBar = config.Bind("UI", "DisableSpeakingBar", false,
            new ConfigDescription("Hides the in-game speaking bar completely."));

        SpeakingBarPosition = config.Bind("UI", "SpeakingBarPosition",
            VoiceChatPlugin.VoiceChat.SpeakingBarPosition.TopMiddle,
            new ConfigDescription("Chooses the speaking bar's screen preset while manual layout is disabled."));

        SpeakingBarSideLayout = config.Bind("UI", "SpeakingBarSideLayout",
            VoiceChatPlugin.VoiceChat.SpeakingBarSideLayout.SingleLane,
            new ConfigDescription("Chooses whether left/right speaking-bar presets use one vertical lane or wrap into additional columns. Top Middle and Bottom Middle always wrap."));

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

        SpeakingBarAvatarFacing = config.Bind("UI", "SpeakingBarAvatarFacing",
            VoiceChatPlugin.VoiceChat.SpeakingBarAvatarFacing.Right,
            new ConfigDescription("Chooses whether speaking-bar avatars face left or right while manual layout is enabled."));

        _speakingBarSettingsVersion = config.Bind("UI.Internal", "SpeakingBarSettingsVersion", 0,
            new ConfigDescription("Internal migration version for speaking-bar appearance settings."));

        SpeakingBarNamePosition = config.Bind("UI", "SpeakingBarNamePosition",
            VoiceChatPlugin.VoiceChat.SpeakingBarNamePosition.Auto,
            new ConfigDescription("Where the player name sits relative to its speaking-bar icon. Auto keeps names inside the screen based on the bar position."));

        SpeakingBarBackdrop = config.Bind("UI", "SpeakingBarBackdrop", true,
            new ConfigDescription("Show a translucent dark backdrop behind the speaking bar."));

        SpeakingBarScale = config.Bind("UI", "SpeakingBarScale", 1.0f,
            new ConfigDescription("Changes the size of the speaking bar, including its player icons and names. In v4, 100% is the same rendered size as 90% in earlier versions.",
                new AcceptableValueRange<float>(
                    SpeakingBarScalePolicy.MinimumUserScale,
                    SpeakingBarScalePolicy.MaximumUserScale)));

        SpeakingBarFixedAllPlayers = config.Bind("UI", "SpeakingBarFixedAllPlayers", false,
            new ConfigDescription("Keeps a stable speaking-bar slot for every connected player, including across meeting transitions, instead of showing only current speakers."));

        SpeakingBarLivePreview = config.Bind("UI", "SpeakingBarLivePreview", SpeakingBarLivePreviewDefault,
            new ConfigDescription("Temporarily moves the local settings panel aside and shows an isolated, realistic 15-player live preview of the speaking bar. It turns off when the panel closes, when you leave the HUD tab, and on every game launch."));

        JailUnmuteButtonPlacement = config.Bind("UI", "JailUnmuteButtonPlacement",
            VoiceChatPlugin.VoiceChat.JailUnmuteButtonPlacement.MeetingCard,
            new ConfigDescription("Chooses whether the Jailor's temporary unmute control appears on the voice HUD or the jailed player's meeting card."));

        // Meeting overlay — on by default.
        MeetingSpeakingOverlay = config.Bind("UI", "MeetingSpeakingOverlay", true,
            new ConfigDescription(
                "Shows a coloured glow around a player's meeting card while they are speaking, subject to concealment and blindness privacy rules."));

        OverlayScale = config.Bind("UI", "OverlayScale", 1.30f,
            new ConfigDescription("Changes the size of the voice HUD buttons.",
                new AcceptableValueRange<float>(0.75f, 3.00f)));

        NoiseSuppressionEnabled = config.Bind("Audio", "NoiseSuppressionEnabled", true,
            new ConfigDescription("Use WebRTC noise suppression on outgoing microphone audio while preserving quiet speech."));

        StrongerNoiseSuppressionEnabled = config.Bind("Audio", "StrongerNoiseSuppressionEnabled", false,
            new ConfigDescription("Use stronger WebRTC noise suppression. This can remove more background noise but may make quiet speech sound less natural."));

        EchoCancellationEnabled = config.Bind("Audio", "EchoCancellationEnabled", true,
            new ConfigDescription("Cancel echo/feedback of incoming voice picked up by your microphone."));

        DebugVoiceStats = config.Bind("Debug", "DebugVoiceStats", false,
            new ConfigDescription("Enable Perfect Comms diagnostic files and debug log output."));

        SyntheticMicTone = config.Bind("Debug.Advanced", "SyntheticMicTone", false,
            new ConfigDescription("Transmit a quiet generated 48 kHz mono test tone through the native voice engine instead of relying on physical microphone audio."));
        ShowFake15Players = config.Bind("Debug.Advanced", "ShowFake15Players", false,
            new ConfigDescription("Temporarily show a 15-player fake roster in the speaking bar for layout testing. It resets off on every game launch."));
        MicCalibrationDiagnostics = config.Bind("Debug", "MicCalibrationDiagnostics", false,
            new ConfigDescription("Log live microphone peak/RMS/gate calibration diagnostics for the native voice engine."));

        // Temporary editor and troubleshooting toggles always start OFF on every game launch, even if a
        // previous session left one on. They still work when turned on mid-session; they just never carry
        // into the next run, so previews, fake roster data, diagnostics, and test audio cannot linger.
        ResetTransientTogglesForLaunch(
            SpeakingBarLivePreview,
            ShowFake15Players,
            DebugVoiceStats,
            MicCalibrationDiagnostics,
            SyntheticMicTone);

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
            BetterCrewLinkLobbyEndpoint.DefaultServerUrl,
            new ConfigDescription("Optional BetterCrewLink public-lobby directory endpoint. Voice audio and signaling do not use this service."));

        TurnServerUrl = config.Bind("Voice Server", "TurnServerUrl",
            "",
            new ConfigDescription("Optional custom TURN relay for automatic fallback (turn: over UDP/TCP, turns: over TLS/TCP, or turns: over DTLS/UDP). Leave empty to use the project's managed TURN credentials fetched at runtime."));
        TurnUsername = config.Bind("Voice Server", "TurnUsername",
            "",
            new ConfigDescription("Username for a custom TURN relay (only used when TurnServerUrl is set)."));
        TurnCredential = config.Bind("Voice Server", "TurnCredential",
            "",
            new ConfigDescription("Credential (password) for a custom TURN relay (only used when TurnServerUrl is set)."));

        UpdateNotificationsEnabled = config.Bind("Updates", "NotificationsEnabled", true,
            new ConfigDescription("Show Perfect Comms update notifications on the main menu"));

        UpdateNotificationUrl = config.Bind("Updates", "NotificationUrl",
            "https://api.github.com/repos/artriy/Perfect-Comms/releases/latest",
            new ConfigDescription("Perfect Comms GitHub latest-release API endpoint"));

        CompletedSetupRevision = config.Bind("Setup.Internal", "CompletedSetupRevision", 0,
            new ConfigDescription(
                "Internal one-time setup revision. Zero means the Perfect Comms setup has not been completed."));

        PerPlayerVolumes = config.Bind("Audio", "PerPlayerVolumes", "",
            "Saved per-player voice volumes keyed by player name");

        // Run after every current entry is bound so migration saves write a complete config file
        // and cannot disturb still-unbound legacy entries.
        if (hadRetiredNatFix || hadRetiredWineForceRelay)
        {
            try
            {
                RemoveRetiredRelaySettings(
                    config,
                    removeNatFix: hadRetiredNatFix,
                    removeWineForceRelay: hadRetiredWineForceRelay);
            }
            catch (Exception ex)
            {
                // These settings are no longer read anywhere, so a read-only config can safely retain
                // inert legacy lines without changing automatic direct-to-TURN behavior.
                VoiceDiagnostics.DebugWarning($"[VC] Could not remove retired relay settings: {ex.Message}");
            }
        }
        ApplySpeakingBarV4Migration(hadLegacySpeakingBarScale);

        VoiceDiagnostics.SetEnabled(DebugVoiceStats.Value);
    }

    internal bool NeedsFirstRunSetup
        => FirstRunSetupPolicy.NeedsAutomaticSetup(CompletedSetupRevision.Value);

    internal void CommitFirstRunSetup(FirstRunSetupDraft draft)
    {
        if (draft == null) throw new ArgumentNullException(nameof(draft));

        var before = FirstRunSetupDraft.CaptureExisting(this);
        int previousRevision = CompletedSetupRevision.Value;
        using IDisposable batch = PerfectCommsConfigStore.BeginBatch(_config);
        try
        {
            draft.ApplyTo(this);
            // The marker is deliberately last: a crash or exception before this assignment
            // causes setup to be offered again instead of treating a partial draft as complete.
            CompletedSetupRevision.Value =
                FirstRunSetupPolicy.RevisionToStoreOnCompletion(previousRevision);
            PerfectCommsConfigStore.Save(_config);
        }
        catch
        {
            try
            {
                before.ApplyTo(this);
                CompletedSetupRevision.Value = previousRevision;
                PerfectCommsConfigStore.Save(_config);
            }
            catch
            {
                // Preserve the original exception. A later launch will still see the old marker
                // from disk unless the final atomic save above succeeded.
            }
            throw;
        }
    }

    /// <summary>
    /// Keeps every persisted setting exactly as-is and records only that onboarding is complete.
    /// This backs the explicit "Use existing settings" escape for both automatic and manual runs.
    /// </summary>
    internal void UseExistingSettingsForFirstRunSetup()
    {
        int previousRevision = CompletedSetupRevision.Value;
        int revision = FirstRunSetupPolicy.RevisionToStoreOnCompletion(previousRevision);
        if (previousRevision == revision) return;

        using IDisposable batch = PerfectCommsConfigStore.BeginBatch(_config);
        try
        {
            CompletedSetupRevision.Value = revision;
            PerfectCommsConfigStore.Save(_config);
        }
        catch
        {
            try
            {
                CompletedSetupRevision.Value = previousRevision;
                PerfectCommsConfigStore.Save(_config);
            }
            catch
            {
                // Preserve the original save error. The in-memory marker is restored above, and
                // the previous on-disk value remains the only revision considered trustworthy.
            }
            throw;
        }
    }

    private static string ReadExistingConfigText(string? configPath)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath)
                ? File.ReadAllText(configPath)
                : string.Empty;
        }
        catch
        {
            // A missing/unreadable snapshot must not prevent settings from loading.
            // The new defaults still apply; only legacy scale preservation is skipped.
            return string.Empty;
        }
    }

    private static void RemoveRetiredRelaySettings(
        ConfigFile config,
        bool removeNatFix,
        bool removeWineForceRelay)
    {
        using IDisposable batch = PerfectCommsConfigStore.BeginBatch(config);
        // Binding consumes BepInEx's private orphan entries. Removing the resulting public entries
        // before saving deletes only the retired keys while preserving every unrelated setting.
        if (removeNatFix)
        {
            ConfigEntry<bool> retiredNatFix = config.Bind(
                "Voice Server",
                "NatFix",
                true,
                new ConfigDescription("Retired: TURN fallback is automatic."));
            PerfectCommsConfigStore.Remove(config, retiredNatFix.Definition);
        }
        if (removeWineForceRelay)
        {
            ConfigEntry<bool> retiredWineForceRelay = config.Bind(
                "Voice Server",
                "WineForceRelay",
                false,
                new ConfigDescription("Retired: Wine uses automatic direct-to-TURN fallback."));
            PerfectCommsConfigStore.Remove(config, retiredWineForceRelay.Definition);
        }
        PerfectCommsConfigStore.Save(config);
    }

    private void ApplySpeakingBarV4Migration(bool hadLegacySpeakingBarScale)
    {
        SpeakingBarV4MigrationPlan plan = SpeakingBarScalePolicy.PlanV4Migration(
            _speakingBarSettingsVersion.Value,
            hadLegacySpeakingBarScale,
            SpeakingBarScale.Value,
            SpeakingBarBackdrop.Value,
            SpeakingBarNamePosition.Value);
        if (!plan.ShouldApply)
            return;

        // Commit the four related values through one cross-process merge. The version marker remains
        // last so a failure before the atomic replacement cannot apply the conversion twice.
        using IDisposable batch = PerfectCommsConfigStore.BeginBatch(_config);
        SpeakingBarScale.Value = plan.Scale;
        SpeakingBarBackdrop.Value = plan.Backdrop;
        SpeakingBarNamePosition.Value = plan.NamePosition;
        _speakingBarSettingsVersion.Value = plan.TargetVersion;
        PerfectCommsConfigStore.Save(_config);
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

    private static T ResolveDeviceIndex<T>(
        string savedId,
        string legacyName,
        IReadOnlyList<VoiceDeviceInfo> devices,
        T fallback,
        bool authoritativeDeviceList,
        bool recoverMissingStableId,
        out VoiceDeviceInfo? resolvedDevice)
        where T : struct, Enum
        => (T)(object)ResolveDeviceIndex(
            savedId, legacyName, devices, (int)(object)fallback,
            authoritativeDeviceList, recoverMissingStableId, out resolvedDevice);

    internal static int ResolveDeviceIndex(
        string savedId,
        string legacyName,
        IReadOnlyList<VoiceDeviceInfo> devices,
        int fallback,
        out VoiceDeviceInfo? resolvedDevice)
        => ResolveDeviceIndex(
            savedId, legacyName, devices, fallback,
            authoritativeDeviceList: true,
            recoverMissingStableId: false,
            out resolvedDevice);

    internal static int ResolveDeviceIndex(
        string savedId,
        string legacyName,
        IReadOnlyList<VoiceDeviceInfo> devices,
        int fallback,
        bool authoritativeDeviceList,
        bool recoverMissingStableId,
        out VoiceDeviceInfo? resolvedDevice)
    {
        resolvedDevice = null;
        int unavailableMatch = -1;
        if (!string.IsNullOrEmpty(savedId))
        {
            for (int i = 1; i < devices.Count; i++)
            {
                if (!string.Equals(devices[i].Id, savedId, StringComparison.Ordinal)) continue;
                if (!devices[i].IsAvailable)
                    unavailableMatch = i;
                else
                {
                    resolvedDevice = devices[i];
                    return i;
                }
            }

            // The synthetic list published before the first desktop helper probe is intentionally
            // non-authoritative. Preserve the placeholder only until a real enumeration arrives;
            // otherwise startup would erase a valid hot-plugged selection before it can be seen.
            if (!authoritativeDeviceList && unavailableMatch >= 0)
            {
                resolvedDevice = devices[unavailableMatch];
                return unavailableMatch;
            }

            // Microphone selections deliberately remain fail-closed: once a stable identity exists,
            // never redirect capture to a merely similar name or to the system default. Speaker
            // playback opts into recovery explicitly because silence cannot leak microphone audio.
            if (!recoverMissingStableId)
            {
                if (unavailableMatch >= 0)
                {
                    resolvedDevice = devices[unavailableMatch];
                    return unavailableMatch;
                }
                return 0;
            }

            // A legacy host-prefixed ID can often be matched exactly to Cubeb's versioned raw ID.
            // If that is unavailable, recover by display name only when it identifies exactly one
            // live endpoint; duplicate friendly names must never silently retarget audio.
            int exactLegacyMatch = UniqueLegacyDeviceIdIndex(savedId, devices);
            if (exactLegacyMatch >= 0)
            {
                resolvedDevice = devices[exactLegacyMatch];
                return exactLegacyMatch;
            }

            int recovered = UniqueAvailableDeviceNameIndex(legacyName, devices);
            if (recovered >= 0)
            {
                resolvedDevice = devices[recovered];
                return recovered;
            }

            // Keep an unavailable saved endpoint as the user's preference. Playback can use a
            // temporary Default fallback, while a later hot-plug or driver enumeration can still
            // recover the choice instead of permanently erasing it.
            if (unavailableMatch >= 0)
            {
                resolvedDevice = devices[unavailableMatch];
                return unavailableMatch;
            }

            return ResolveAvailableDefault(devices, out resolvedDevice);
        }
        if (!string.IsNullOrEmpty(legacyName))
        {
            int recovered = UniqueAvailableDeviceNameIndex(legacyName, devices);
            if (recovered >= 0)
            {
                resolvedDevice = devices[recovered];
                return recovered;
            }

            if (!authoritativeDeviceList)
            {
                for (int i = 1; i < devices.Count; i++)
                {
                    if (devices[i].IsAvailable ||
                        !string.Equals(devices[i].Name, legacyName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    resolvedDevice = devices[i];
                    return i;
                }
            }

            return ResolveAvailableDefault(devices, out resolvedDevice);
        }

        if (fallback >= 0 && fallback < devices.Count && devices[fallback].IsAvailable)
        {
            resolvedDevice = devices[fallback];
            return fallback;
        }
        return ResolveAvailableDefault(devices, out resolvedDevice);
    }

    private static int UniqueAvailableDeviceNameIndex(
        string name,
        IReadOnlyList<VoiceDeviceInfo> devices)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        int match = -1;
        for (int i = 1; i < devices.Count; i++)
        {
            if (!devices[i].IsAvailable ||
                !string.Equals(devices[i].Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match >= 0) return -1;
            match = i;
        }
        return match;
    }

    private static int UniqueLegacyDeviceIdIndex(
        string savedId,
        IReadOnlyList<VoiceDeviceInfo> devices)
    {
        int match = -1;
        for (int i = 1; i < devices.Count; i++)
        {
            if (!devices[i].IsAvailable ||
                !LegacyDesktopDeviceIdMatchesCubeb(savedId, devices[i].Id))
                continue;
            if (match >= 0) return -1;
            match = i;
        }
        return match;
    }

    internal static bool LegacyDesktopDeviceIdMatchesCubeb(
        string savedDeviceId,
        string cubebDeviceId)
    {
        byte[]? expectedFamily;
        byte[] expectedRaw;
        if (TryExtractLegacyBackendDeviceId(
                savedDeviceId,
                out string legacyHost,
                out string legacyBackendId))
        {
            string? family = CompatibleCubebFamily(legacyHost);
            if (family == null) return false;
            expectedFamily = Encoding.UTF8.GetBytes(family);
            expectedRaw = Encoding.UTF8.GetBytes(legacyBackendId);
        }
        else if (!TryParseCubebDeviceId(savedDeviceId, out expectedFamily, out expectedRaw))
        {
            return false;
        }
        if (!TryParseCubebDeviceId(cubebDeviceId, out byte[]? actualFamily, out byte[] actualRaw) ||
            !expectedRaw.AsSpan().SequenceEqual(actualRaw))
            return false;

        // v1 did not carry a backend family. Preserve its one-time raw-ID migration, but once
        // both sides are v2 never let equal raw strings retarget a saved ALSA endpoint to Pulse,
        // a CoreAudio endpoint to WASAPI, or any other cross-backend combination.
        return expectedFamily == null || actualFamily == null ||
               expectedFamily.AsSpan().SequenceEqual(actualFamily);
    }

    private const int MaxDesktopDeviceIdBytes = 4_096;

    private static bool TryParseCubebDeviceId(
        string cubebDeviceId,
        out byte[]? backendFamily,
        out byte[] rawId)
    {
        backendFamily = null;
        rawId = Array.Empty<byte>();
        if (string.IsNullOrEmpty(cubebDeviceId)) return false;
        string rawHex;
        if (cubebDeviceId.StartsWith("cubeb-v1:", StringComparison.Ordinal))
        {
            rawHex = cubebDeviceId.Substring("cubeb-v1:".Length);
        }
        else if (cubebDeviceId.StartsWith("cubeb-v2:", StringComparison.Ordinal))
        {
            int separator = cubebDeviceId.IndexOf(':', "cubeb-v2:".Length);
            if (separator < 0 || separator >= cubebDeviceId.Length - 1) return false;
            string familyHex = cubebDeviceId.Substring(
                "cubeb-v2:".Length,
                separator - "cubeb-v2:".Length);
            if (!TryDecodeDeviceIdHex(familyHex, out byte[] parsedFamily)) return false;
            backendFamily = parsedFamily;
            rawHex = cubebDeviceId.Substring(separator + 1);
        }
        else
        {
            return false;
        }
        if (TryDecodeDeviceIdHex(rawHex, out rawId)) return true;
        backendFamily = null;
        rawId = Array.Empty<byte>();
        return false;
    }

    private static bool TryDecodeDeviceIdHex(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (hex.Length == 0 ||
            hex.Length > MaxDesktopDeviceIdBytes * 2 ||
            (hex.Length & 1) != 0)
            return false;
        bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            int high = HexNibble(hex[i * 2]);
            int low = HexNibble(hex[(i * 2) + 1]);
            if (high < 0 || low < 0)
            {
                bytes = Array.Empty<byte>();
                return false;
            }
            bytes[i] = (byte)((high << 4) | low);
        }
        return true;
    }

    private static bool TryExtractLegacyBackendDeviceId(
        string savedDeviceId,
        out string host,
        out string backendDeviceId)
    {
        host = string.Empty;
        backendDeviceId = string.Empty;
        if (string.IsNullOrEmpty(savedDeviceId)) return false;
        int separator = savedDeviceId.IndexOf(':');
        if (separator <= 0 || separator >= savedDeviceId.Length - 1) return false;
        host = savedDeviceId.Substring(0, separator);
        if (host is not ("wasapi" or "coreaudio" or "pulseaudio" or "pipewire" or
            "alsa" or "jack" or "asio"))
        {
            host = string.Empty;
            return false;
        }
        backendDeviceId = savedDeviceId.Substring(separator + 1);
        if (Encoding.UTF8.GetByteCount(backendDeviceId) <= MaxDesktopDeviceIdBytes)
            return true;
        host = string.Empty;
        backendDeviceId = string.Empty;
        return false;
    }

    private static string? CompatibleCubebFamily(string legacyHost)
        => legacyHost switch
        {
            "wasapi" => "wasapi",
            "coreaudio" => "coreaudio",
            "pulseaudio" => "pulse",
            "alsa" => "alsa",
            _ => null,
        };

    private static int HexNibble(char value)
        => value is >= '0' and <= '9' ? value - '0'
         : value is >= 'a' and <= 'f' ? value - 'a' + 10
         : value is >= 'A' and <= 'F' ? value - 'A' + 10
         : -1;

    private static int ResolveAvailableDefault(
        IReadOnlyList<VoiceDeviceInfo> devices,
        out VoiceDeviceInfo? resolvedDevice)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            if (!devices[i].IsAvailable || !devices[i].IsDefault) continue;
            resolvedDevice = devices[i];
            return i;
        }
        if (devices.Count > 0 && devices[0].IsAvailable)
        {
            resolvedDevice = devices[0];
            return 0;
        }
        resolvedDevice = null;
        return 0;
    }

    internal static bool ShouldReapplyResolvedDevice(
        string previousId,
        bool previousAvailable,
        VoiceDeviceInfo? resolvedDevice)
        => resolvedDevice is { IsAvailable: true } device &&
           (!previousAvailable ||
            !string.Equals(previousId, device.Id, StringComparison.Ordinal));

    internal static bool ShouldReapplyResolvedDeviceAfterListPublication(
        bool firstAuthoritativeList,
        string previousId,
        bool previousAvailable,
        VoiceDeviceInfo? resolvedDevice)
        => ShouldReapplyResolvedDeviceAfterListPublication(
            firstAuthoritativeList,
            previousId,
            previousAvailable,
            previousId,
            resolvedDevice);

    internal static bool ShouldReapplyResolvedDeviceAfterListPublication(
        bool firstAuthoritativeList,
        string previousId,
        bool previousAvailable,
        string persistedSavedId,
        VoiceDeviceInfo? resolvedDevice)
    {
        if (firstAuthoritativeList && string.IsNullOrEmpty(previousId))
            previousId = persistedSavedId ?? string.Empty;
        if (!ShouldReapplyResolvedDevice(previousId, previousAvailable, resolvedDevice))
            return false;
        // An unchanged stable ID is already the identity passed to the native backend at startup,
        // so the first helper enumeration only establishes its availability baseline. A CPAL or
        // Cubeb-v1 -> v2 migration changes the actual selection string and must be applied even on
        // that first list; otherwise a running room can remain stuck on the retired identifier.
        return !firstAuthoritativeList ||
               resolvedDevice is { } device &&
               !string.Equals(previousId, device.Id, StringComparison.Ordinal);
    }

    internal static bool ShouldProcessDeviceSelectionDispatch(
        bool internalIndexCorrection)
        => !internalIndexCorrection;

    internal static bool IsPersistedDefaultSelection(
        int selectedIndex,
        string savedId,
        string savedName)
        => selectedIndex == 0 &&
           string.IsNullOrEmpty(savedId) &&
           string.IsNullOrEmpty(savedName);

    internal static (string Id, string Name) CanonicalizeLegacyPersistedSelection(
        int selectedIndex,
        string savedId,
        string savedName)
    {
        string id = savedId ?? string.Empty;
        string name = savedName ?? string.Empty;
        if (selectedIndex == 0 &&
            string.IsNullOrEmpty(id) &&
            string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase))
            return (string.Empty, string.Empty);
        return (id, name);
    }

    internal static (string Id, string Name) PersistedSelectionForDevice(
        int selectedIndex,
        VoiceDeviceInfo device)
    {
        string id = device.Id ?? string.Empty;
        if (selectedIndex == 0 && device.IsDefault && string.IsNullOrEmpty(id))
            return (string.Empty, string.Empty);
        return (id, device.Name ?? string.Empty);
    }

    private static void ApplyLegacyDefaultCanonicalization(
        int selectedIndex,
        ConfigEntry<string> savedId,
        ConfigEntry<string> savedName)
    {
        var canonical = CanonicalizeLegacyPersistedSelection(
            selectedIndex, savedId.Value, savedName.Value);
        if (!string.Equals(savedId.Value, canonical.Id, StringComparison.Ordinal))
            savedId.Value = canonical.Id;
        if (!string.Equals(savedName.Value, canonical.Name, StringComparison.Ordinal))
            savedName.Value = canonical.Name;
    }

    internal static string DefaultAvailableDeviceId(IReadOnlyList<VoiceDeviceInfo> devices)
    {
        for (int i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            if (device.IsAvailable && device.IsDefault && !string.IsNullOrEmpty(device.Id))
                return device.Id;
        }
        return string.Empty;
    }

    internal static bool ShouldReapplyDefaultDevice(
        bool defaultSelected,
        string previousDefaultId,
        string currentDefaultId)
        => defaultSelected &&
           !string.IsNullOrEmpty(currentDefaultId) &&
           !string.Equals(previousDefaultId, currentDefaultId, StringComparison.Ordinal);

    internal static bool ShouldRecoverLegacyDesktopDeviceId(string savedDeviceId)
    {
#if WINDOWS
        return TryExtractLegacyBackendDeviceId(savedDeviceId, out _, out _) ||
               (TryParseCubebDeviceId(savedDeviceId, out byte[]? family, out _) &&
                family == null);
#else
        return false;
#endif
    }

    private static void MigrateLegacyDeviceSelection(
        ConfigEntry<string> savedId,
        ConfigEntry<string> savedName,
        VoiceDeviceInfo? resolvedDevice)
    {
        if (!resolvedDevice.HasValue) return;
        var device = resolvedDevice.Value;
        var persisted = PersistedSelectionForDevice(
            device.IsDefault && string.IsNullOrEmpty(device.Id) ? 0 : 1,
            device);
        if (!string.Equals(savedId.Value, persisted.Id, StringComparison.Ordinal))
            savedId.Value = persisted.Id;
        if (!string.Equals(savedName.Value, persisted.Name, StringComparison.Ordinal))
            savedName.Value = persisted.Name;
    }

    public static void RefreshDeviceLists()
    {
        if (!_micNamesFromSidecar)
        {
            var mics = new List<VoiceDeviceInfo>
            {
                new(string.Empty, "Default", true),
            };
            try
            {
#if ANDROID
                if (Application.HasUserAuthorization(UserAuthorization.Microphone))
                {
                    foreach (var dev in AndroidMicrophone.GetDeviceNames())
                    {
                        string n = dev?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(n))
                            mics.Add(VoiceDeviceInfo.FromName(n));
                    }
                }
#endif
            }
            catch { }
            var micDevices = mics.ToArray();
            micDevices = WithUnavailableSelection(
                micDevices, _savedMicIdForPublication, _savedMicNameForPublication,
                "Saved microphone");
            PublishMicDevices(micDevices);
        }

#if WINDOWS
        if (!_spkNamesFromSidecar)
        {
            var speakerDevices = new[] { new VoiceDeviceInfo(string.Empty, "Default", true) };
            speakerDevices = WithUnavailableSelection(
                speakerDevices, _savedSpkIdForPublication, _savedSpkNameForPublication,
                "Saved speaker");
            PublishSpkDevices(speakerDevices);
        }
#endif
    }

    internal static readonly TimeSpan ActiveDeviceProbeInterval = TimeSpan.FromSeconds(30);
    private static DateTime _nextDeviceRefreshUtc = DateTime.MinValue;
#if WINDOWS
    private static DateTime _nextActiveDeviceProbeUtc = DateTime.MinValue;
#endif

    private static bool MicDeviceListIsAuthoritative
    {
        get
        {
#if WINDOWS
            return _micNamesFromSidecar;
#else
            return true;
#endif
        }
    }

    // Re-enumerate devices (throttled to every 2s) so hot-plugged or removed mics/speakers show up in the
    // in-game device pickers without a game restart. Called from the settings panel's device rows.
    public static void MaybeRefreshDeviceLists(bool resolveSavedIndices = true)
    {
        var now = DateTime.UtcNow;
        if (now >= _nextDeviceRefreshUtc)
        {
            _nextDeviceRefreshUtc = now.AddSeconds(2);
            RefreshDeviceLists();
            MaybeProbeSidecarDevices();
        }
        if (resolveSavedIndices)
        {
            VoiceSettings.Instance?.ResolveMicIndexIfListChanged();
            VoiceSettings.Instance?.ResolveSpkIndexIfListChanged();
        }
    }

    internal static void MaybeRefreshActiveRoomDeviceLists()
    {
        // Resolving an already-published version is just two integer comparisons, so do that on
        // every room tick. Enumeration is deliberately much slower because the desktop helper's
        // safe device probe is a short-lived process, not an operation for the frame loop.
        VoiceSettings.Instance?.ResolveMicIndexIfListChanged();
        VoiceSettings.Instance?.ResolveSpkIndexIfListChanged();
#if WINDOWS
        var now = DateTime.UtcNow;
        if (now < _nextActiveDeviceProbeUtc) return;
        _nextActiveDeviceProbeUtc = now.Add(ActiveDeviceProbeInterval);
        MaybeProbeSidecarDevices();
#endif
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
                PublishSidecarDeviceEnumeration(SidecarLauncher.EnumerateDevices());
            }
            catch { }
        });
#endif
    }

    private int _lastResolvedMicVersion = -1;
    private string _lastResolvedMicId = string.Empty;
    private bool _lastResolvedMicAvailable;
    private string _lastObservedDefaultMicId = DefaultAvailableDeviceId(_micDevices);
    private bool _hasResolvedAuthoritativeMicList;

    internal static bool MarkAndDetectFirstAuthoritativeList(
        bool authoritative,
        ref bool hasResolvedAuthoritativeList)
    {
        if (!authoritative || hasResolvedAuthoritativeList) return false;
        hasResolvedAuthoritativeList = true;
        return true;
    }

    internal void ResolveMicIndexIfListChanged()
    {
        int version = MicDeviceListVersion;
        if (version == _lastResolvedMicVersion) return;
        _lastResolvedMicVersion = version;
        bool firstAuthoritative = MarkAndDetectFirstAuthoritativeList(
            MicDeviceListIsAuthoritative,
            ref _hasResolvedAuthoritativeMicList);
        string currentDefaultId = DefaultAvailableDeviceId(_micDevices);
        bool defaultChanged = !firstAuthoritative && ShouldReapplyDefaultDevice(
            IsPersistedDefaultSelection(
                (int)MicrophoneDeviceIndex.Value,
                _savedMicDeviceId.Value,
                _savedMicDeviceName.Value),
            _lastObservedDefaultMicId,
            currentDefaultId);
        _lastObservedDefaultMicId = currentDefaultId;
        var resolved = ResolveDeviceIndex<MicDeviceEnum>(
            _savedMicDeviceId.Value, _savedMicDeviceName.Value, _micDevices,
            MicrophoneDeviceIndex.Value, MicDeviceListIsAuthoritative,
            recoverMissingStableId: ShouldRecoverLegacyDesktopDeviceId(_savedMicDeviceId.Value),
            out var resolvedDevice);
        bool becameAvailable = ShouldReapplyResolvedDeviceAfterListPublication(
            firstAuthoritative,
            _lastResolvedMicId,
            _lastResolvedMicAvailable,
            _savedMicDeviceId.Value,
            resolvedDevice);
        bool indexChanged = !resolved.Equals(MicrophoneDeviceIndex.Value);
        if (indexChanged)
        {
            _suppressInternalDeviceSelectionDispatch = true;
            try { MicrophoneDeviceIndex.Value = resolved; }
            finally { _suppressInternalDeviceSelectionDispatch = false; }
        }
        MigrateLegacyDeviceSelection(_savedMicDeviceId, _savedMicDeviceName, resolvedDevice);
        _savedMicIdForPublication = _savedMicDeviceId.Value;
        _savedMicNameForPublication = _savedMicDeviceName.Value;
        _lastResolvedMicId = resolvedDevice?.Id ?? _savedMicDeviceId.Value;
        _lastResolvedMicAvailable = resolvedDevice is { IsAvailable: true };
        if (becameAvailable && resolvedDevice is { } availableMicrophone)
            VoiceChatRoom.Current?.SetMicrophone(availableMicrophone.Id);
        else if (defaultChanged)
            VoiceChatRoom.Current?.ReapplyDefaultMicrophone();
    }

#if WINDOWS
    private int _lastResolvedSpkVersion = -1;
    private string _lastResolvedSpkId = string.Empty;
    private bool _lastResolvedSpkAvailable;
    private string _lastObservedDefaultSpkId = DefaultAvailableDeviceId(_spkDevices);
    private bool _hasResolvedAuthoritativeSpkList;

    internal void ResolveSpkIndexIfListChanged()
    {
        int version = SpkDeviceListVersion;
        if (version == _lastResolvedSpkVersion) return;
        _lastResolvedSpkVersion = version;
        bool firstAuthoritative = MarkAndDetectFirstAuthoritativeList(
            _spkNamesFromSidecar,
            ref _hasResolvedAuthoritativeSpkList);
        string currentDefaultId = DefaultAvailableDeviceId(_spkDevices);
        bool defaultChanged = !firstAuthoritative && ShouldReapplyDefaultDevice(
            IsPersistedDefaultSelection(
                (int)SpeakerDeviceIndex.Value,
                _savedSpkDeviceId.Value,
                _savedSpkDeviceName.Value),
            _lastObservedDefaultSpkId,
            currentDefaultId);
        _lastObservedDefaultSpkId = currentDefaultId;
        var resolved = ResolveDeviceIndex<SpkDeviceEnum>(
            _savedSpkDeviceId.Value, _savedSpkDeviceName.Value, _spkDevices,
            SpeakerDeviceIndex.Value, _spkNamesFromSidecar,
            recoverMissingStableId: true,
            out var resolvedDevice);
        bool becameAvailable = ShouldReapplyResolvedDeviceAfterListPublication(
            firstAuthoritative,
            _lastResolvedSpkId,
            _lastResolvedSpkAvailable,
            _savedSpkDeviceId.Value,
            resolvedDevice);
        bool indexChanged = !resolved.Equals(SpeakerDeviceIndex.Value);
        if (indexChanged)
        {
            _suppressInternalDeviceSelectionDispatch = true;
            try { SpeakerDeviceIndex.Value = resolved; }
            finally { _suppressInternalDeviceSelectionDispatch = false; }
        }
        MigrateLegacyDeviceSelection(_savedSpkDeviceId, _savedSpkDeviceName, resolvedDevice);
        _savedSpkIdForPublication = _savedSpkDeviceId.Value;
        _savedSpkNameForPublication = _savedSpkDeviceName.Value;
        _lastResolvedSpkId = resolvedDevice?.Id ?? _savedSpkDeviceId.Value;
        _lastResolvedSpkAvailable = resolvedDevice is { IsAvailable: true };
        if (becameAvailable && resolvedDevice is { } availableSpeaker)
            VoiceChatRoom.Current?.SetSpeaker(availableSpeaker.Id);
        else if (defaultChanged)
            VoiceChatRoom.Current?.ReapplyDefaultSpeaker();
    }
#else
    internal void ResolveSpkIndexIfListChanged() { }
#endif

    internal void Dispatch(ConfigEntryBase configEntry)
    {
        // The editor preview polls this value while the local panel is open. It must never
        // clear, rebuild, or otherwise mutate the real in-game speaking bar.
        if (configEntry == SpeakingBarLivePreview) return;

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
                 configEntry == NoiseSuppressionEnabled || configEntry == StrongerNoiseSuppressionEnabled ||
                 configEntry == EchoCancellationEnabled ||
                 configEntry == SyntheticMicTone ||
                 configEntry == MicCalibrationDiagnostics)
        {
            if (_applyingDiagnosticsToggle) return;
            VoiceChatRoom.Current?.RefreshLocalAudioSettings();
        }
        else if (configEntry == MicrophoneDeviceIndex)
        {
            if (!ShouldProcessDeviceSelectionDispatch(
                    _suppressInternalDeviceSelectionDispatch))
                return;
            var device = MicDeviceAtCurrentIndex();
            var persisted = PersistedSelectionForDevice(
                (int)MicrophoneDeviceIndex.Value, device);
            var id = persisted.Id;
            var name = persisted.Name;
            _savedMicDeviceId.Value = id;
            _savedMicDeviceName.Value = name;
            _savedMicIdForPublication = id;
            _savedMicNameForPublication = name;
            _lastResolvedMicId = id;
            _lastResolvedMicAvailable = device.IsAvailable;
            if (device.IsAvailable)
                VoiceChatRoom.Current?.SetMicrophone(id);
            VoiceChatRoom.Current?.SetMicVolume(MicVolume.Value);
        }
#if WINDOWS
        else if (configEntry == SpeakerDeviceIndex)
        {
            if (!ShouldProcessDeviceSelectionDispatch(
                    _suppressInternalDeviceSelectionDispatch))
                return;
            var device = SpkDeviceAtCurrentIndex();
            var persisted = PersistedSelectionForDevice(
                (int)SpeakerDeviceIndex.Value, device);
            var id = persisted.Id;
            var name = persisted.Name;
            _savedSpkDeviceId.Value = id;
            _savedSpkDeviceName.Value = name;
            _savedSpkIdForPublication = id;
            _savedSpkNameForPublication = name;
            _lastResolvedSpkId = id;
            _lastResolvedSpkAvailable = device.IsAvailable;
            if (device.IsAvailable)
                VoiceChatRoom.Current?.SetSpeaker(id);
        }
#endif
        else if (configEntry == DisableVoiceControlsHud ||
                 configEntry == ButtonPositionX || configEntry == ButtonPositionY ||
                 configEntry == VoiceControlsLayout)
        {
            VoiceChatHudState.RefreshButtonLayout();
        }
        else if (configEntry == DisableSpeakingBar)
        {
            PingTrackerPatch.ClearSpeakingBarSlots();
        }
        else if (configEntry == SpeakingBarPosition)
        {
            PingTrackerPatch.ApplySpeakingBarPosition(SpeakingBarPosition.Value);
            PingTrackerPatch.ApplySpeakingBarLayoutSettings();
        }
        else if (configEntry == SpeakingBarManualLayout || configEntry == SpeakingBarX ||
                 configEntry == SpeakingBarY || configEntry == SpeakingBarLayout ||
                 configEntry == SpeakingBarAvatarFacing ||
                 configEntry == SpeakingBarSideLayout ||
                 configEntry == SpeakingBarBackdrop || configEntry == SpeakingBarScale)
        {
            PingTrackerPatch.ApplySpeakingBarLayoutSettings();
        }
        else if (configEntry == SpeakingBarNamePosition ||
                 configEntry == SpeakingBarFixedAllPlayers || configEntry == ShowFake15Players)
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
        else if (configEntry == TurnServerUrl || configEntry == TurnUsername ||
                 configEntry == TurnCredential)
        {
            // Recreate the native peer generation so custom TURN/relay policy changes take effect immediately.
            // This does not leave or rejoin the game lobby.
            VoiceChatRoom.Current?.RebuildIceConnectionPool();
        }
    }

}
