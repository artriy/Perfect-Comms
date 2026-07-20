using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct FirstRunSetupBinding(
    KeyCode Key,
    KeyCode Modifier,
    VoiceModifierMatch ModifierMatch)
{
    internal static FirstRunSetupBinding From(VoiceKeybind binding)
        => new(binding.Value, binding.Modifier, binding.ModifierMatch);

    internal string Label
        => Key == KeyCode.None
            ? "None"
            : Modifier == KeyCode.None
                ? VoiceKeybind.FormatKey(Key)
                : VoiceKeybind.FormatModifier(Modifier, ModifierMatch) + "+" +
                  VoiceKeybind.FormatKey(Key);

    internal void ApplyTo(VoiceKeybind binding)
        => binding.SetBinding(Key, Modifier, ModifierMatch);
}

internal readonly record struct FirstRunHudPreset(
    string Name,
    string Description,
    SpeakingBarPosition Position,
    SpeakingBarSideLayout SideLayout,
    SpeakingBarNamePosition NamePosition,
    float Scale,
    bool Backdrop);

internal static class FirstRunHudPresets
{
    internal const int CommonCount = 3;

    internal static readonly IReadOnlyList<FirstRunHudPreset> All =
        new FirstRunHudPreset[]
        {
            new("Top Middle", "The familiar default above the action, wrapping cleanly in full lobbies",
                SpeakingBarPosition.TopMiddle, SpeakingBarSideLayout.Wrapped,
                SpeakingBarNamePosition.Auto, 1.00f, true),
            new("Middle Right", "A single vertical lane on the right with no wrapping",
                SpeakingBarPosition.MiddleRight, SpeakingBarSideLayout.SingleLane,
                SpeakingBarNamePosition.Auto, 1.00f, true),
            new("Middle Left", "A single vertical lane on the left with no wrapping",
                SpeakingBarPosition.MiddleLeft, SpeakingBarSideLayout.SingleLane,
                SpeakingBarNamePosition.Auto, 1.00f, true),
            new("Compact", "A smaller clean bar with no backdrop",
                SpeakingBarPosition.TopMiddle, SpeakingBarSideLayout.Wrapped,
                SpeakingBarNamePosition.Auto, 0.78f, false),
            new("Top Left", "A familiar corner layout with vertical flow",
                SpeakingBarPosition.TopLeft, SpeakingBarSideLayout.Wrapped,
                SpeakingBarNamePosition.Auto, 0.90f, true),
            new("Top Right", "Corner layout beside the voice controls",
                SpeakingBarPosition.TopRight, SpeakingBarSideLayout.Wrapped,
                SpeakingBarNamePosition.Auto, 0.90f, true),
            new("Left Stack", "A wrapped side stack that keeps the center clear",
                SpeakingBarPosition.MiddleLeft, SpeakingBarSideLayout.Wrapped,
                SpeakingBarNamePosition.Auto, 0.84f, true),
            new("Right Stack", "A wrapped side stack near the HUD edge",
                SpeakingBarPosition.MiddleRight, SpeakingBarSideLayout.Wrapped,
                SpeakingBarNamePosition.Auto, 0.84f, true),
            new("Bottom Center", "Centered below the action with wrapped rows",
                SpeakingBarPosition.BottomMiddle, SpeakingBarSideLayout.Wrapped,
                SpeakingBarNamePosition.Auto, 0.90f, true),
            new("Minimal", "Small, quiet, and tucked into the bottom right",
                SpeakingBarPosition.BottomRight, SpeakingBarSideLayout.SingleLane,
                SpeakingBarNamePosition.Auto, 0.75f, false),
        };

    internal static SpeakingBarPreviewSettings Apply(
        SpeakingBarPreviewSettings current,
        FirstRunHudPreset preset)
        => current with
        {
            Position = preset.Position,
            SideLayout = preset.SideLayout,
            ManualLayout = false,
            NamePosition = preset.NamePosition,
            Scale = preset.Scale,
            Backdrop = preset.Backdrop,
        };

    internal static int Match(SpeakingBarPreviewSettings settings)
    {
        if (settings.ManualLayout) return -1;
        for (int i = 0; i < All.Count; i++)
        {
            var preset = All[i];
            if (settings.Position == preset.Position &&
                settings.SideLayout == preset.SideLayout &&
                settings.NamePosition == preset.NamePosition &&
                Math.Abs(settings.Scale - preset.Scale) < 0.005f &&
                settings.Backdrop == preset.Backdrop)
                return i;
        }
        return -1;
    }
}

/// <summary>
/// A complete staged copy of every value the setup edits. Nothing is written to BepInEx config
/// until Finish is pressed, so hover previews, Back, Use Existing Settings, crashes, and scene
/// changes cannot leave a half-applied setup behind.
/// </summary>
internal sealed class FirstRunSetupDraft
{
    // Audio and control choices mirror the effective defaults of a newly generated Perfect Comms
    // config. HUD starts on the explicit Top Middle onboarding preset for a clear initial selection.
    private const float DefaultMicVolume = 1f;
    private const float DefaultMicSensitivity = 1f;
    private const float DefaultMasterVolume = 1f;

    internal string MicrophoneDevice = string.Empty;
    internal string MicrophoneDeviceName = string.Empty;
    internal string SpeakerDevice = string.Empty;
    internal string SpeakerDeviceName = string.Empty;
    internal string OriginalMicrophoneDevice = string.Empty;
    internal string OriginalSpeakerDevice = string.Empty;
    internal float MicVolume;
    internal float MicSensitivity;
    // The advanced VAD base is not edited by setup, but the local mic preview must use the
    // same activation point that production voice will use after setup finishes.
    internal float BaseVadThreshold;
    internal float MasterVolume;
    internal VoiceMicMode MicMode;
    internal bool NoiseSuppression;
    internal bool StrongerNoiseSuppression;
    internal bool EchoCancellation;
    internal bool StartMuted;
    internal bool StartDeafened;
    internal FirstRunSetupBinding ToggleMute;
    internal FirstRunSetupBinding PushToTalk;
    internal FirstRunSetupBinding ToggleSpeaker;
    internal FirstRunSetupBinding OpenVoiceSettings;
    internal SpeakingBarPreviewSettings Hud;
    internal SpeakingBarPreviewSettings OriginalHud;
    internal int SelectedHudPreset = -1;
    internal bool OriginalHudSelected = true;

    /// <summary>
    /// Creates the clean, recommended setup shown to every user. Existing values are retained
    /// only as a rollback/comparison baseline; no setting is written until setup is finished.
    /// </summary>
    internal static FirstRunSetupDraft CreateFresh(VoiceChatLocalSettings settings)
        => CreateFreshFrom(CaptureExisting(settings));

    /// <summary>
    /// Pure factory used by tests and by <see cref="CreateFresh"/> after the current settings have
    /// been captured. The advanced VAD threshold is preserved because setup neither displays nor
    /// commits that setting.
    /// </summary>
    internal static FirstRunSetupDraft CreateFreshFrom(FirstRunSetupDraft existing)
    {
        if (existing == null) throw new ArgumentNullException(nameof(existing));

        var recommendedHud = RecommendedHud();
        return new FirstRunSetupDraft
        {
            MicrophoneDevice = string.Empty,
#if WINDOWS
            SpeakerDevice = string.Empty,
#endif
            OriginalMicrophoneDevice = existing.MicrophoneDevice,
            OriginalSpeakerDevice = existing.SpeakerDevice,
            MicVolume = DefaultMicVolume,
            MicSensitivity = DefaultMicSensitivity,
            BaseVadThreshold = existing.BaseVadThreshold,
            MasterVolume = DefaultMasterVolume,
            MicMode = VoiceMicMode.OpenMic,
            NoiseSuppression = true,
            StrongerNoiseSuppression = false,
            EchoCancellation = true,
            StartMuted = false,
            StartDeafened = false,
            ToggleMute = new FirstRunSetupBinding(
                KeyCode.M, KeyCode.LeftShift, VoiceModifierMatch.EitherSide),
            PushToTalk = new FirstRunSetupBinding(
                KeyCode.C, KeyCode.None, VoiceModifierMatch.Exact),
            ToggleSpeaker = new FirstRunSetupBinding(
                KeyCode.N, KeyCode.LeftShift, VoiceModifierMatch.EitherSide),
            OpenVoiceSettings = new FirstRunSetupBinding(
                KeyCode.F10, KeyCode.None, VoiceModifierMatch.Exact),
            Hud = recommendedHud,
            OriginalHud = existing.Hud,
            SelectedHudPreset = FirstRunHudPresets.Match(recommendedHud),
            OriginalHudSelected = false,
        };
    }

    /// <summary>
    /// Captures the actual persisted values. This is intentionally separate from
    /// <see cref="CreateFresh"/> so transaction rollback can never restore recommended defaults
    /// over an existing user's settings.
    /// </summary>
    internal static FirstRunSetupDraft CaptureExisting(VoiceChatLocalSettings settings)
    {
        var draft = new FirstRunSetupDraft
        {
            MicrophoneDevice = settings.MicrophoneDevice,
            MicrophoneDeviceName = settings.MicrophoneDeviceName,
#if WINDOWS
            SpeakerDevice = settings.SpeakerDevice,
            SpeakerDeviceName = settings.SpeakerDeviceName,
#endif
            MicVolume = settings.MicVolume.Value,
            MicSensitivity = settings.MicSensitivity.Value,
            BaseVadThreshold = settings.VadThreshold.Value,
            MasterVolume = settings.MasterVolume.Value,
            MicMode = settings.MicMode.Value,
            NoiseSuppression = settings.NoiseSuppressionEnabled.Value,
            StrongerNoiseSuppression = settings.StrongerNoiseSuppressionEnabled.Value,
            EchoCancellation = settings.EchoCancellationEnabled.Value,
            StartMuted = settings.StartMuted.Value,
            StartDeafened = settings.StartDeafened.Value,
            ToggleMute = FirstRunSetupBinding.From(VoiceChatKeybinds.ToggleMute),
            PushToTalk = FirstRunSetupBinding.From(VoiceChatKeybinds.PushToTalk),
            ToggleSpeaker = FirstRunSetupBinding.From(VoiceChatKeybinds.ToggleSpeaker),
            OpenVoiceSettings = FirstRunSetupBinding.From(VoiceChatKeybinds.OpenVoiceMenu),
            Hud = SpeakingBarPreviewSettings.From(settings),
        };
        draft.OriginalHud = draft.Hud;
        draft.OriginalMicrophoneDevice = draft.MicrophoneDevice;
        draft.OriginalSpeakerDevice = draft.SpeakerDevice;
        draft.SelectedHudPreset = FirstRunHudPresets.Match(draft.Hud);
        return draft;
    }

    // Compatibility for rollback callers while the setup flow migrates to CreateFresh.
    internal static FirstRunSetupDraft Capture(VoiceChatLocalSettings settings)
        => CaptureExisting(settings);

    private static SpeakingBarPreviewSettings RecommendedHud()
        => new(
            SpeakingBarPosition.TopMiddle,
            SpeakingBarSideLayout.Wrapped,
            ManualLayout: false,
            VoiceControlsLayout.Horizontal,
            SpeakingBarAvatarFacing.Right,
            ManualX: 0.5f,
            ManualY: 0.85f,
            SpeakingBarNamePosition.Auto,
            Scale: 1f,
            Backdrop: true);

    internal void SelectHudPreset(int index)
    {
        if (index < 0 || index >= FirstRunHudPresets.All.Count) return;
        Hud = FirstRunHudPresets.Apply(Hud, FirstRunHudPresets.All[index]);
        SelectedHudPreset = index;
        OriginalHudSelected = false;
    }

    internal void SelectOriginalHud()
    {
        Hud = OriginalHud;
        SelectedHudPreset = FirstRunHudPresets.Match(Hud);
        OriginalHudSelected = true;
    }

    internal int MicrophoneIndex()
        => FindDeviceIndex(MicrophoneDevice, VoiceChatLocalSettings.MicDevices);

    internal string MicrophoneDisplayName()
        => FindDeviceName(MicrophoneDevice, MicrophoneDeviceName, VoiceChatLocalSettings.MicDevices);

    internal bool MicrophoneSelectionChanged
        => !string.Equals(MicrophoneDevice, OriginalMicrophoneDevice,
            StringComparison.Ordinal);

#if WINDOWS
    internal int SpeakerIndex()
        => FindDeviceIndex(SpeakerDevice, VoiceChatLocalSettings.SpkDevices);

    internal string SpeakerDisplayName()
        => FindDeviceName(SpeakerDevice, SpeakerDeviceName, VoiceChatLocalSettings.SpkDevices);

    internal bool SpeakerSelectionChanged
        => !string.Equals(SpeakerDevice, OriginalSpeakerDevice,
            StringComparison.Ordinal);
#endif

    internal void SetMicrophoneIndex(int index)
    {
        if (!CanSelectDeviceIndex(index, VoiceChatLocalSettings.MicDevices))
        {
            MicrophoneDevice = string.Empty;
            MicrophoneDeviceName = string.Empty;
            return;
        }
        var device = DeviceAt(index, VoiceChatLocalSettings.MicDevices);
        MicrophoneDevice = device.Id ?? string.Empty;
        MicrophoneDeviceName = device.Name ?? string.Empty;
    }

#if WINDOWS
    internal void SetSpeakerIndex(int index)
    {
        if (!CanSelectDeviceIndex(index, VoiceChatLocalSettings.SpkDevices))
        {
            SpeakerDevice = string.Empty;
            SpeakerDeviceName = string.Empty;
            return;
        }
        var device = DeviceAt(index, VoiceChatLocalSettings.SpkDevices);
        SpeakerDevice = device.Id ?? string.Empty;
        SpeakerDeviceName = device.Name ?? string.Empty;
    }
#endif

    internal void ApplyTo(VoiceChatLocalSettings settings)
    {
        settings.MicVolume.Value = MicVolume;
        settings.MicSensitivity.Value = MicSensitivity;
        settings.MasterVolume.Value = MasterVolume;
        settings.MicMode.Value = MicMode;
        settings.NoiseSuppressionEnabled.Value = NoiseSuppression;
        settings.StrongerNoiseSuppressionEnabled.Value = StrongerNoiseSuppression;
        settings.EchoCancellationEnabled.Value = EchoCancellation;
        settings.StartMuted.Value = StartMuted;
        settings.StartDeafened.Value = StartDeafened;

        ApplyMicrophoneIfResolved(settings);
#if WINDOWS
        ApplySpeakerIfResolved(settings);
#endif

        settings.SpeakingBarPosition.Value = Hud.Position;
        settings.SpeakingBarSideLayout.Value = Hud.SideLayout;
        settings.SpeakingBarManualLayout.Value = Hud.ManualLayout;
        settings.SpeakingBarLayout.Value = Hud.ManualOrientation;
        settings.SpeakingBarAvatarFacing.Value = Hud.ManualAvatarFacing;
        settings.SpeakingBarX.Value = Hud.ManualX;
        settings.SpeakingBarY.Value = Hud.ManualY;
        settings.SpeakingBarNamePosition.Value = Hud.NamePosition;
        settings.SpeakingBarScale.Value = Hud.Scale;
        settings.SpeakingBarBackdrop.Value = Hud.Backdrop;

        ToggleMute.ApplyTo(VoiceChatKeybinds.ToggleMute);
        PushToTalk.ApplyTo(VoiceChatKeybinds.PushToTalk);
        ToggleSpeaker.ApplyTo(VoiceChatKeybinds.ToggleSpeaker);
        OpenVoiceSettings.ApplyTo(VoiceChatKeybinds.OpenVoiceMenu);
    }

    private void ApplyMicrophoneIfResolved(VoiceChatLocalSettings settings)
    {
        if (string.Equals(MicrophoneDevice, settings.MicrophoneDevice, StringComparison.Ordinal))
            return;
        int index = MicrophoneIndex();
        if (index >= 0)
            settings.MicrophoneDeviceIndex.Value = (MicDeviceEnum)index;
    }

#if WINDOWS
    private void ApplySpeakerIfResolved(VoiceChatLocalSettings settings)
    {
        if (string.Equals(SpeakerDevice, settings.SpeakerDevice, StringComparison.Ordinal))
            return;
        int index = SpeakerIndex();
        if (index >= 0)
            settings.SpeakerDeviceIndex.Value = (SpkDeviceEnum)index;
    }
#endif

    private static int FindDeviceIndex(string savedId, IReadOnlyList<VoiceDeviceInfo> devices)
    {
        if (string.IsNullOrEmpty(savedId)) return devices.Count > 0 ? 0 : -1;
        for (int i = 1; i < devices.Count; i++)
            if (devices[i].IsAvailable &&
                string.Equals(savedId, devices[i].Id, StringComparison.Ordinal))
                return i;
        return -1;
    }

    internal static bool CanSelectDeviceIndex(
        int index,
        IReadOnlyList<VoiceDeviceInfo> devices)
        => index == 0
            ? devices.Count > 0 && devices[0].IsAvailable
            : index > 0 && index < devices.Count && devices[index].IsAvailable;

    private static VoiceDeviceInfo DeviceAt(int index, IReadOnlyList<VoiceDeviceInfo> devices)
        => index > 0 && index < devices.Count ? devices[index] : default;

    private static string FindDeviceName(
        string savedId,
        string savedName,
        IReadOnlyList<VoiceDeviceInfo> devices)
    {
        if (string.IsNullOrEmpty(savedId)) return "System Default";
        for (int i = 1; i < devices.Count; i++)
            if (string.Equals(savedId, devices[i].Id, StringComparison.Ordinal))
                return devices[i].Name;
        return string.IsNullOrWhiteSpace(savedName) ? "Saved device" : savedName;
    }
}
