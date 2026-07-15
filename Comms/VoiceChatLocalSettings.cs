using System;
using System.Collections.Generic;
using System.IO;
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
    // Keep the four explicit positions at their legacy numeric values so existing
    // config files continue to represent a user override. Auto is the v4 default.
    Auto   = 4,
}

public enum SpeakingBarAvatarFacing
{
    Right = 0,
    Left  = 1,
}

public enum SpeakingBarSideLayout
{
    SingleLane = 0,
    Wrapped    = 1,
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
    internal const bool SpeakingBarLivePreviewDefault = false;

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
        _micNamesFromSidecar = true;
        PublishMicDeviceNames(arr);
    }

    public static int SpkDeviceListVersion =>
#if WINDOWS
        Volatile.Read(ref _spkDeviceListVersion);
#else
        0;
#endif
#if WINDOWS
    public static string[] SpkDeviceNames => _spkDeviceNames;
    internal static bool SidecarDeviceProbePending =>
        _sidecarProbeTask != null && !_sidecarProbeTask.IsCompleted;

    public static void SetSpkDeviceNamesFromSidecar(IReadOnlyList<string> names)
    {
        var arr = new string[names.Count + 1];
        arr[0] = "Default";
        for (int i = 0; i < names.Count; i++)
            arr[i + 1] = names[i];
        _spkNamesFromSidecar = true;
        PublishSpkDeviceNames(arr);
    }
#endif

    private static bool SameDeviceNames(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first.Count != second.Count) return false;
        for (int i = 0; i < first.Count; i++)
            if (!string.Equals(first[i], second[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static void PublishMicDeviceNames(string[] names)
    {
        if (SameDeviceNames(_micDeviceNames, names)) return;
        _micDeviceNames = names;
        Interlocked.Increment(ref _micDeviceListVersion);
    }

#if WINDOWS
    private static void PublishSpkDeviceNames(string[] names)
    {
        if (SameDeviceNames(_spkDeviceNames, names)) return;
        _spkDeviceNames = names;
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
    public ConfigEntry<bool> EchoCancellationEnabled { get; }
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

    // Wine/Proton (Linux) only. When on, the native engine forces TURN-relay-only ICE and adds a TURN-over-TCP
    // candidate for Wine setups whose local ICE gathering still cannot establish a direct/STUN connection.
    // Ignored on native Windows. Default off; automatic direct-first/TURN fallback remains active either way.
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
        string existingConfigText = ReadExistingConfigText(config.ConfigFilePath);
        bool hadLegacySpeakingBarScale = SpeakingBarScalePolicy.ConfigTextContainsSetting(
            existingConfigText,
            "UI",
            "SpeakingBarScale");
        bool hadRetiredNatFix = SpeakingBarScalePolicy.ConfigTextContainsSetting(
            existingConfigText,
            "Voice Server",
            "NatFix");
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
            new ConfigDescription("Starts each voice session with incoming voice audio muted."));

        _savedMicDeviceName = config.Bind("Audio", "MicDeviceName", "",
            "Saved microphone device name (used to restore selection across sessions)");

#if WINDOWS
        _savedSpkDeviceName = config.Bind("Audio", "SpkDeviceName", "",
            "Saved speaker device name (used to restore selection across sessions)");
#endif

        MicrophoneDeviceIndex = config.Bind("Audio", "Microphone",
            MicDeviceEnum.Default,
            new ConfigDescription("Selects the recording device Perfect Comms uses. Default follows the system's default input device."));

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
            new ConfigDescription("Selects the playback device Perfect Comms uses for voice audio. Default follows the system's default output device."));

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
            new ConfigDescription("Arranges the microphone, speaker, and role voice controls vertically or horizontally."));

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
            new ConfigDescription("Moves the local settings panel aside and shows an isolated, realistic 15-player live preview of the speaking bar."));

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

        EchoCancellationEnabled = config.Bind("Audio", "EchoCancellationEnabled", true,
            new ConfigDescription("Cancel echo/feedback of incoming voice picked up by your microphone."));

        DebugVoiceStats = config.Bind("Debug", "DebugVoiceStats", false,
            new ConfigDescription("Enable Perfect Comms diagnostic files and debug log output."));

        SyntheticMicTone = config.Bind("Debug.Advanced", "SyntheticMicTone", false,
            new ConfigDescription("Transmit a quiet generated 48 kHz mono test tone through the native voice engine instead of relying on physical microphone audio."));
        ShowFake15Players = config.Bind("Debug.Advanced", "ShowFake15Players", false,
            new ConfigDescription("Show a 15-player fake roster in the speaking bar for layout testing."));
        MicCalibrationDiagnostics = config.Bind("Debug", "MicCalibrationDiagnostics", false,
            new ConfigDescription("Log live microphone peak/RMS/gate calibration diagnostics for the native voice engine."));

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
            BetterCrewLinkLobbyEndpoint.DefaultServerUrl,
            new ConfigDescription("Optional BetterCrewLink public-lobby directory endpoint. Voice audio and signaling do not use this service."));

        TurnServerUrl = config.Bind("Voice Server", "TurnServerUrl",
            "",
            new ConfigDescription("Optional custom TURN relay for automatic fallback. Leave empty to use the project's managed TURN credentials (fetched at runtime); set your own coturn/TURN server here to override."));
        TurnUsername = config.Bind("Voice Server", "TurnUsername",
            "",
            new ConfigDescription("Username for a custom TURN relay (only used when TurnServerUrl is set)."));
        TurnCredential = config.Bind("Voice Server", "TurnCredential",
            "",
            new ConfigDescription("Credential (password) for a custom TURN relay (only used when TurnServerUrl is set)."));
        WineForceRelay = config.Bind("Voice Server", "WineForceRelay", false,
            new ConfigDescription("Wine/Proton (Linux) only opt-in: force TURN-relay-only voice (and add TURN-over-TCP). Off by default - Wine uses the same automatic ICE selection as native Windows, using TURN only when direct/STUN fail. Enable only if your Wine setup still cannot connect. Ignored on native Windows and requires valid TURN credentials."));

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
        if (hadRetiredNatFix)
        {
            try { RemoveRetiredNatFixSetting(config); }
            catch (Exception ex)
            {
                // The setting is no longer read anywhere, so a read-only config can safely retain
                // the inert legacy line without disabling automatic TURN fallback.
                VoiceDiagnostics.DebugWarning($"[VC] Could not remove retired NatFix setting: {ex.Message}");
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
        bool saveOnConfigSet = _config.SaveOnConfigSet;
        try
        {
            _config.SaveOnConfigSet = false;
            draft.ApplyTo(this);
            // The marker is deliberately last: a crash or exception before this assignment
            // causes setup to be offered again instead of treating a partial draft as complete.
            CompletedSetupRevision.Value =
                FirstRunSetupPolicy.RevisionToStoreOnCompletion(previousRevision);
            _config.Save();
        }
        catch
        {
            try
            {
                before.ApplyTo(this);
                CompletedSetupRevision.Value = previousRevision;
                _config.Save();
            }
            catch
            {
                // Preserve the original exception. A later launch will still see the old marker
                // from disk unless the final atomic save above succeeded.
            }
            throw;
        }
        finally
        {
            _config.SaveOnConfigSet = saveOnConfigSet;
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

        bool saveOnConfigSet = _config.SaveOnConfigSet;
        try
        {
            _config.SaveOnConfigSet = false;
            CompletedSetupRevision.Value = revision;
            _config.Save();
        }
        catch
        {
            try
            {
                CompletedSetupRevision.Value = previousRevision;
                _config.Save();
            }
            catch
            {
                // Preserve the original save error. The in-memory marker is restored above, and
                // the previous on-disk value remains the only revision considered trustworthy.
            }
            throw;
        }
        finally
        {
            _config.SaveOnConfigSet = saveOnConfigSet;
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

    private static void RemoveRetiredNatFixSetting(ConfigFile config)
    {
        bool saveOnConfigSet = config.SaveOnConfigSet;
        try
        {
            // Binding consumes BepInEx's private orphan entry. Removing the resulting public entry
            // before saving deletes only the retired key while preserving every unrelated setting.
            config.SaveOnConfigSet = false;
            ConfigEntry<bool> retired = config.Bind(
                "Voice Server",
                "NatFix",
                true,
                new ConfigDescription("Retired: TURN fallback is automatic."));
            config.Remove(retired.Definition);
            config.Save();
        }
        finally
        {
            config.SaveOnConfigSet = saveOnConfigSet;
        }
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

        // Commit the four related values together. Otherwise SaveOnConfigSet could
        // persist the converted scale before the version marker and a crash during
        // startup would apply the conversion a second time on the next launch.
        bool saveOnConfigSet = _config.SaveOnConfigSet;
        try
        {
            _config.SaveOnConfigSet = false;
            SpeakingBarScale.Value = plan.Scale;
            SpeakingBarBackdrop.Value = plan.Backdrop;
            SpeakingBarNamePosition.Value = plan.NamePosition;
            _speakingBarSettingsVersion.Value = plan.TargetVersion;
        }
        finally
        {
            _config.SaveOnConfigSet = saveOnConfigSet;
        }
        _config.Save();
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
                if (Application.HasUserAuthorization(UserAuthorization.Microphone))
                {
                    foreach (var dev in AndroidMicrophone.GetDeviceNames())
                    {
                        string n = dev?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(n))
                            mics.Add(n);
                    }
                }
#endif
            }
            catch { }
            PublishMicDeviceNames(mics.ToArray());
        }

#if WINDOWS
        if (!_spkNamesFromSidecar)
            PublishSpkDeviceNames(new[] { "Default" });
#endif
    }

    private static DateTime _nextDeviceRefreshUtc = DateTime.MinValue;

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
                 configEntry == NoiseSuppressionEnabled || configEntry == EchoCancellationEnabled ||
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
                 configEntry == TurnCredential ||
                 configEntry == WineForceRelay)
        {
            // Recreate the native peer generation so custom TURN/relay policy changes take effect immediately.
            // This does not leave or rejoin the game lobby.
            VoiceChatRoom.Current?.RebuildIceConnectionPool();
        }
    }

}
