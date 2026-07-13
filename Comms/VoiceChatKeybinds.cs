using System;
using BepInEx.Configuration;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatKeybinds
{
    private static VoiceKeybind[] _allBindings = Array.Empty<VoiceKeybind>();

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
        VolumeMenu = new VoiceKeybind(
            config, s, "Player Volumes", KeyCode.B, KeyCode.LeftShift, VoiceModifierMatch.EitherSide);
        LocalVoiceRefresh = new VoiceKeybind(config, s, "Refresh Voice Connection", KeyCode.F7);
        HostVoiceRefresh = new VoiceKeybind(config, s, "Refresh Voice Connections (Host)", KeyCode.F8);
        OpenVoiceMenu = new VoiceKeybind(config, s, "Open Voice Menu", KeyCode.F10);
        OpenHostVoiceSettings = new VoiceKeybind(config, s, "Open Host Voice Settings", KeyCode.F11);
        _allBindings = new[]
        {
            ToggleMute,
            TeamRadio,
            CycleTeamRadioChannel,
            PushToTalk,
            ToggleMicMode,
            ToggleSpeaker,
            VolumeMenu,
            LocalVoiceRefresh,
            HostVoiceRefresh,
            OpenVoiceMenu,
            OpenHostVoiceSettings,
        };

        var shiftDefaultsMigrated = config.Bind(s, "ShiftDefaultsMigrated", false,
            new ConfigDescription("Internal one-time flag: added Shift to the default Mute (M) and Toggle Speaker (N) keys. Do not edit."));
        if (!shiftDefaultsMigrated.Value)
        {
            if (ToggleMute.Value == KeyCode.M && ToggleMute.Modifier == KeyCode.None)
                ToggleMute.SetModifier(KeyCode.LeftShift, VoiceModifierMatch.EitherSide);
            if (ToggleSpeaker.Value == KeyCode.N && ToggleSpeaker.Modifier == KeyCode.None)
                ToggleSpeaker.SetModifier(KeyCode.LeftShift, VoiceModifierMatch.EitherSide);
            shiftDefaultsMigrated.Value = true;
        }

        var playerVolumeShiftDefaultMigrated = config.Bind(s, "PlayerVolumeShiftDefaultMigrated", false,
            new ConfigDescription("Internal one-time flag: changed the untouched Player Volumes default from B to Shift+B. Do not edit."));
        if (!playerVolumeShiftDefaultMigrated.Value)
        {
            if (ShouldMigratePlayerVolumeDefault(VolumeMenu.Value, VolumeMenu.Modifier))
                VolumeMenu.SetModifier(KeyCode.LeftShift, VoiceModifierMatch.EitherSide);
            playerVolumeShiftDefaultMigrated.Value = true;
        }
    }

    internal static bool ShouldMigratePlayerVolumeDefault(KeyCode key, KeyCode modifier)
        => key == KeyCode.B && modifier == KeyCode.None;

    internal static bool HasConfiguredChordUsingModifier(
        KeyCode modifier,
        VoiceKeybind except)
    {
        foreach (var bind in _allBindings)
        {
            if (ReferenceEquals(bind, except) || bind.Value == KeyCode.None
                || bind.Modifier == KeyCode.None) continue;
            if (bind.MatchesModifierKey(modifier)) return true;
        }
        return false;
    }

    internal static bool HasActiveChordUsingModifier(
        KeyCode modifier,
        VoiceKeybind except)
    {
        foreach (var bind in _allBindings)
        {
            if (ReferenceEquals(bind, except) || bind.Value == KeyCode.None
                || bind.Modifier == KeyCode.None) continue;
            if (bind.MatchesModifierKey(modifier) && bind.IsPrimaryHeldRaw()) return true;
        }
        return false;
    }

    internal static bool HasActiveChordForPrimary(
        KeyCode primary,
        VoiceKeybind except)
    {
        foreach (var bind in _allBindings)
        {
            if (ReferenceEquals(bind, except) || bind.Value != primary
                || bind.Modifier == KeyCode.None) continue;
            if (bind.IsModifierSatisfied()) return true;
        }
        return false;
    }
}
