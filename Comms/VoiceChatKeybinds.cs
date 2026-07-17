using System;
using BepInEx.Configuration;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatKeybinds
{
    internal const string ToggleDeafenDisplayName = "Toggle Deafen";
    internal const string ToggleDeafenHelpText =
        "Deafens or undeafens Perfect Comms. Deafening mutes voice playback and pauses microphone transmission.";
    internal const string HostLocalRefreshDisplayName = "Refresh Host Voice Connection";
    internal const string HostLocalRefreshHelpText =
        "Host only. Rebuilds your local voice session. It has a 10-second cooldown.";

    private static VoiceKeybind[] _allBindings = Array.Empty<VoiceKeybind>();

    public static VoiceKeybind ToggleMute { get; private set; } = null!;
    public static VoiceKeybind TeamRadio { get; private set; } = null!;
    public static VoiceKeybind CycleTeamRadioChannel { get; private set; } = null!;
    public static VoiceKeybind ImpostorRadio => TeamRadio;
    public static VoiceKeybind PushToTalk { get; private set; } = null!;
    public static VoiceKeybind ToggleMicMode { get; private set; } = null!;
    public static VoiceKeybind ToggleSpeaker { get; private set; } = null!;
    public static VoiceKeybind VolumeMenu { get; private set; } = null!;
    public static VoiceKeybind AliveLouderDeadQuieter { get; private set; } = null!;
    public static VoiceKeybind AliveQuieterDeadLouder { get; private set; } = null!;
    public static VoiceKeybind LocalVoiceRefresh { get; private set; } = null!;
    public static VoiceKeybind HostVoiceRefresh { get; private set; } = null!;
    public static VoiceKeybind OpenVoiceMenu { get; private set; } = null!;
    public static VoiceKeybind OpenHostVoiceSettings { get; private set; } = null!;

    public static void Initialize(ConfigFile config)
    {
        const string s = "Keybinds";
        ToggleMute = new VoiceKeybind(config, s, "Mute / Unmute Mic", KeyCode.M,
            helpText: "Toggles whether your microphone sends voice.");
        TeamRadio = new VoiceKeybind(config, s, "Team Radio (Hold)", KeyCode.V,
            helpText: "While held, transmits over your selected private team channel when your role and the host settings allow it.");
        CycleTeamRadioChannel = new VoiceKeybind(config, s, "Cycle Team Radio Channel", KeyCode.G,
            helpText: "Cycles through the team-radio channels available to you, such as Impostors, Vampires, or Lovers.");
        PushToTalk = new VoiceKeybind(config, s, "Push To Talk (Hold)", KeyCode.C,
            helpText: "While held, transmits your microphone when Mic Mode is set to Push To Talk.");
        ToggleMicMode = new VoiceKeybind(config, s, "Toggle Open Mic / Push To Talk", KeyCode.None,
            helpText: "Switches your microphone between Open Mic and Push To Talk mode.");
        // Keep the pre-v4 persisted key so the clearer deafen label does not reset existing binds.
        ToggleSpeaker = new VoiceKeybind(
            config, s, ToggleDeafenDisplayName, "Toggle Speaker", KeyCode.N,
            helpText: ToggleDeafenHelpText);
        VolumeMenu = new VoiceKeybind(
            config, s, "Player Volumes", KeyCode.B, KeyCode.LeftShift, VoiceModifierMatch.EitherSide,
            helpText: "Opens the local per-player volume mixer. Its adjustments affect only what you hear.");
        AliveLouderDeadQuieter = new VoiceKeybind(
            config, s, "Alive Louder / Dead Quieter (Hold)", KeyCode.None,
            helpText: "While held, applies this binding's configured Alive and Dead volume levels. Releasing restores both groups to 100%. If both mix bindings are held, neither profile is applied.");
        AliveQuieterDeadLouder = new VoiceKeybind(
            config, s, "Alive Quieter / Dead Louder (Hold)", KeyCode.None,
            helpText: "While held, applies this binding's separately configured Alive and Dead volume levels. Releasing restores both groups to 100%. If both mix bindings are held, neither profile is applied.");
        LocalVoiceRefresh = new VoiceKeybind(config, s, "Refresh Voice Connection", KeyCode.F7,
            helpText: "Rejoins only your local voice session to repair stuck audio. It has a 10-second cooldown.");
        // The old plural config key is retained for binding compatibility. The action is local-only
        // because PlayerControl.HandleRpc does not expose authenticated packet sender provenance.
        HostVoiceRefresh = new VoiceKeybind(
            config, s, HostLocalRefreshDisplayName, "Refresh Voice Connections (Host)", KeyCode.F8,
            helpText: HostLocalRefreshHelpText);
        OpenVoiceMenu = new VoiceKeybind(config, s, "Open Voice Menu", KeyCode.F10,
            helpText: "Opens or closes this local Perfect Comms settings menu.");
        OpenHostVoiceSettings = new VoiceKeybind(config, s, "Open Host Voice Settings", KeyCode.F11,
            helpText: "Opens the host-only voice rules for the current lobby. It does nothing if you are not the host.");
        _allBindings = new[]
        {
            ToggleMute,
            TeamRadio,
            CycleTeamRadioChannel,
            PushToTalk,
            ToggleMicMode,
            ToggleSpeaker,
            VolumeMenu,
            AliveLouderDeadQuieter,
            AliveQuieterDeadLouder,
            LocalVoiceRefresh,
            HostVoiceRefresh,
            OpenVoiceMenu,
            OpenHostVoiceSettings,
        };

        var shiftDefaultsMigrated = config.Bind(s, "ShiftDefaultsMigrated", false,
            new ConfigDescription("Internal one-time flag: added Shift to the default Mute (M) and deafen (N) keys. Do not edit."));
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
