using BepInEx.Configuration;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatKeybinds
{
    public static VoiceKeybind ToggleMute { get; private set; } = null!;
    public static VoiceKeybind TeamRadio { get; private set; } = null!;
    public static VoiceKeybind CycleTeamRadioChannel { get; private set; } = null!;
    public static VoiceKeybind ImpostorRadio => TeamRadio;
    public static VoiceKeybind PushToTalk { get; private set; } = null!;
    public static VoiceKeybind ToggleMicMode { get; private set; } = null!;
    public static VoiceKeybind ToggleSpeaker { get; private set; } = null!;
    public static VoiceKeybind VolumeMenu { get; private set; } = null!;
    public static VoiceKeybind LocalVoiceRefresh { get; private set; } = null!;
    public static VoiceKeybind HostVoiceRefresh { get; private set; } = null!;
    public static VoiceKeybind OpenVoiceMenu { get; private set; } = null!;
    public static VoiceKeybind OpenHostVoiceSettings { get; private set; } = null!;

    public static void Initialize(ConfigFile config)
    {
        const string s = "Keybinds";
        ToggleMute = new VoiceKeybind(config, s, "Mute / Unmute Mic", KeyCode.M);
        TeamRadio = new VoiceKeybind(config, s, "Team Radio (Hold)", KeyCode.V);
        CycleTeamRadioChannel = new VoiceKeybind(config, s, "Cycle Team Radio Channel", KeyCode.G);
        PushToTalk = new VoiceKeybind(config, s, "Push To Talk (Hold)", KeyCode.C);
        ToggleMicMode = new VoiceKeybind(config, s, "Toggle Open Mic / Push To Talk", KeyCode.None);
        ToggleSpeaker = new VoiceKeybind(config, s, "Toggle Speaker", KeyCode.N);
        VolumeMenu = new VoiceKeybind(config, s, "Player Volumes", KeyCode.B);
        LocalVoiceRefresh = new VoiceKeybind(config, s, "Refresh Voice Connection", KeyCode.F7);
        HostVoiceRefresh = new VoiceKeybind(config, s, "Refresh Voice Connections (Host)", KeyCode.F8);
        OpenVoiceMenu = new VoiceKeybind(config, s, "Open Voice Menu", KeyCode.F10);
        OpenHostVoiceSettings = new VoiceKeybind(config, s, "Open Host Voice Settings", KeyCode.F11);

        var shiftDefaultsMigrated = config.Bind(s, "ShiftDefaultsMigrated", false,
            new ConfigDescription("Internal one-time flag: added Shift to the default Mute (M) and Toggle Speaker (N) keys. Do not edit."));
        if (!shiftDefaultsMigrated.Value)
        {
            if (ToggleMute.Value == KeyCode.M && ToggleMute.Modifier == KeyCode.None)
                ToggleMute.SetModifier(KeyCode.LeftShift);
            if (ToggleSpeaker.Value == KeyCode.N && ToggleSpeaker.Modifier == KeyCode.None)
                ToggleSpeaker.SetModifier(KeyCode.LeftShift);
            shiftDefaultsMigrated.Value = true;
        }
    }
}
