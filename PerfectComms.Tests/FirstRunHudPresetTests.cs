using System;
using System.Linq;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class FirstRunHudPresetTests
{
    [Fact]
    public void CatalogOffersTenDistinctPolishedChoices()
    {
        Assert.Equal(10, FirstRunHudPresets.All.Count);
        Assert.Equal(
            FirstRunHudPresets.All.Count,
            FirstRunHudPresets.All.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            FirstRunHudPresets.All.Count,
            FirstRunHudPresets.All
                .Select(p => (p.Position, p.SideLayout, p.NamePosition, p.Scale, p.Backdrop))
                .Distinct()
                .Count());
    }

    [Fact]
    public void MostCommonLayoutsLeadCatalogWithExpectedFlow()
    {
        Assert.Equal(3, FirstRunHudPresets.CommonCount);
        Assert.Equal(
            new[] { "Top Middle", "Middle Right", "Middle Left" },
            FirstRunHudPresets.All.Take(FirstRunHudPresets.CommonCount).Select(p => p.Name));

        var top = FirstRunHudPresets.All[0];
        Assert.Equal(SpeakingBarPosition.TopMiddle, top.Position);
        Assert.Equal(SpeakingBarSideLayout.Wrapped, top.SideLayout);
        Assert.Equal(1f, top.Scale);
        Assert.True(top.Backdrop);

        foreach (var side in FirstRunHudPresets.All.Skip(1).Take(2))
        {
            Assert.Equal(SpeakingBarSideLayout.SingleLane, side.SideLayout);
            Assert.Equal(1f, side.Scale);
            Assert.True(side.Backdrop);
            Assert.True(SpeakingBarLayoutPolicy.UsesSingleVerticalLaneFor(
                side.Position, side.SideLayout));
        }
    }

    [Fact]
    public void EveryPresetUsesSafeUserFacingValues()
    {
        foreach (var preset in FirstRunHudPresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.False(string.IsNullOrWhiteSpace(preset.Description));
            Assert.InRange(preset.Scale,
                SpeakingBarScalePolicy.MinimumUserScale,
                SpeakingBarScalePolicy.MaximumUserScale);
            Assert.Equal(SpeakingBarNamePosition.Auto, preset.NamePosition);
        }
    }

    [Fact]
    public void ApplyingPresetReplacesOnlyPresetOwnedAppearance()
    {
        var original = new SpeakingBarPreviewSettings(
            SpeakingBarPosition.BottomLeft,
            SpeakingBarSideLayout.SingleLane,
            ManualLayout: true,
            VoiceControlsLayout.Vertical,
            SpeakingBarAvatarFacing.Left,
            ManualX: 0.12f,
            ManualY: 0.34f,
            SpeakingBarNamePosition.Left,
            Scale: 1.17f,
            Backdrop: false);

        var applied = FirstRunHudPresets.Apply(original, FirstRunHudPresets.All[0]);

        Assert.False(applied.ManualLayout);
        Assert.Equal(FirstRunHudPresets.All[0].Position, applied.Position);
        Assert.Equal(FirstRunHudPresets.All[0].SideLayout, applied.SideLayout);
        Assert.Equal(FirstRunHudPresets.All[0].NamePosition, applied.NamePosition);
        Assert.Equal(FirstRunHudPresets.All[0].Scale, applied.Scale);
        Assert.Equal(FirstRunHudPresets.All[0].Backdrop, applied.Backdrop);
        Assert.Equal(original.ManualOrientation, applied.ManualOrientation);
        Assert.Equal(original.ManualAvatarFacing, applied.ManualAvatarFacing);
        Assert.Equal(original.ManualX, applied.ManualX);
        Assert.Equal(original.ManualY, applied.ManualY);
    }

    [Fact]
    public void MatchRejectsManualAndRecognizesAppliedPreset()
    {
        var original = new SpeakingBarPreviewSettings(
            SpeakingBarPosition.TopMiddle,
            SpeakingBarSideLayout.Wrapped,
            ManualLayout: true,
            VoiceControlsLayout.Horizontal,
            SpeakingBarAvatarFacing.Right,
            ManualX: 0.5f,
            ManualY: 0.85f,
            SpeakingBarNamePosition.Auto,
            Scale: 1f,
            Backdrop: true);

        Assert.Equal(-1, FirstRunHudPresets.Match(original));

        int presetIndex = Enumerable.Range(0, FirstRunHudPresets.All.Count)
            .Single(i => FirstRunHudPresets.All[i].Name == "Bottom Center");
        var applied = FirstRunHudPresets.Apply(original, FirstRunHudPresets.All[presetIndex]);
        Assert.Equal(presetIndex, FirstRunHudPresets.Match(applied));
    }

    [Fact]
    public void DraftCanPreviewPresetThenRestoreExactSavedHud()
    {
        var original = new SpeakingBarPreviewSettings(
            SpeakingBarPosition.MiddleLeft,
            SpeakingBarSideLayout.SingleLane,
            ManualLayout: true,
            VoiceControlsLayout.Vertical,
            SpeakingBarAvatarFacing.Left,
            ManualX: 0.17f,
            ManualY: 0.63f,
            SpeakingBarNamePosition.Right,
            Scale: 1.13f,
            Backdrop: false);
        var draft = new FirstRunSetupDraft
        {
            Hud = original,
            OriginalHud = original,
            OriginalHudSelected = true,
        };

        draft.SelectHudPreset(2);

        Assert.False(draft.OriginalHudSelected);
        Assert.Equal(2, draft.SelectedHudPreset);
        Assert.NotEqual(original, draft.Hud);

        draft.SelectOriginalHud();

        Assert.True(draft.OriginalHudSelected);
        Assert.Equal(original, draft.Hud);
        Assert.Equal(-1, draft.SelectedHudPreset);
    }

    [Fact]
    public void DraftTracksWhetherASelectedRouteDiffersFromTheCapturedRoute()
    {
        var draft = new FirstRunSetupDraft
        {
            MicrophoneDevice = "Mic A",
            OriginalMicrophoneDevice = "Mic A",
#if WINDOWS
            SpeakerDevice = "Speaker A",
            OriginalSpeakerDevice = "Speaker A",
#endif
        };

        Assert.False(draft.MicrophoneSelectionChanged);
#if WINDOWS
        Assert.False(draft.SpeakerSelectionChanged);
#endif

        draft.MicrophoneDevice = "Mic B";
#if WINDOWS
        draft.SpeakerDevice = "Speaker B";
#endif

        Assert.True(draft.MicrophoneSelectionChanged);
#if WINDOWS
        Assert.True(draft.SpeakerSelectionChanged);
#endif
    }

    [Fact]
    public void FreshDraftUsesNewInstallDefaultsRegardlessOfExistingChoices()
    {
        var existingHud = new SpeakingBarPreviewSettings(
            SpeakingBarPosition.BottomRight,
            SpeakingBarSideLayout.Wrapped,
            ManualLayout: true,
            VoiceControlsLayout.Vertical,
            SpeakingBarAvatarFacing.Left,
            ManualX: 0.18f,
            ManualY: 0.31f,
            SpeakingBarNamePosition.Left,
            Scale: 1.22f,
            Backdrop: false);
        var existing = new FirstRunSetupDraft
        {
            MicrophoneDevice = "Existing microphone",
            SpeakerDevice = "Existing speaker",
            MicVolume = 1.8f,
            MicSensitivity = 0.4f,
            BaseVadThreshold = 0.017f,
            MasterVolume = 0.6f,
            MicMode = VoiceMicMode.PushToTalk,
            NoiseSuppression = false,
            StrongerNoiseSuppression = true,
            EchoCancellation = false,
            StartMuted = true,
            StartDeafened = true,
            ToggleMute = new FirstRunSetupBinding(KeyCode.Q, KeyCode.None, VoiceModifierMatch.Exact),
            PushToTalk = new FirstRunSetupBinding(KeyCode.Mouse4, KeyCode.None, VoiceModifierMatch.Exact),
            ToggleSpeaker = new FirstRunSetupBinding(KeyCode.E, KeyCode.None, VoiceModifierMatch.Exact),
            OpenVoiceSettings = new FirstRunSetupBinding(KeyCode.F2, KeyCode.None, VoiceModifierMatch.Exact),
            Hud = existingHud,
        };

        var fresh = FirstRunSetupDraft.CreateFreshFrom(existing);

        Assert.Equal(string.Empty, fresh.MicrophoneDevice);
#if WINDOWS
        Assert.Equal(string.Empty, fresh.SpeakerDevice);
#endif
        Assert.Equal(1f, fresh.MicVolume);
        Assert.Equal(1f, fresh.MicSensitivity);
        Assert.Equal(1f, fresh.MasterVolume);
        Assert.Equal(VoiceMicMode.OpenMic, fresh.MicMode);
        Assert.True(fresh.NoiseSuppression);
        Assert.False(fresh.StrongerNoiseSuppression);
        Assert.True(fresh.EchoCancellation);
        Assert.False(fresh.StartMuted);
        Assert.False(fresh.StartDeafened);

        Assert.Equal(new FirstRunSetupBinding(
            KeyCode.M, KeyCode.LeftShift, VoiceModifierMatch.EitherSide), fresh.ToggleMute);
        Assert.Equal(new FirstRunSetupBinding(
            KeyCode.C, KeyCode.None, VoiceModifierMatch.Exact), fresh.PushToTalk);
        Assert.Equal(new FirstRunSetupBinding(
            KeyCode.N, KeyCode.LeftShift, VoiceModifierMatch.EitherSide), fresh.ToggleSpeaker);
        Assert.Equal(new FirstRunSetupBinding(
            KeyCode.F10, KeyCode.None, VoiceModifierMatch.Exact), fresh.OpenVoiceSettings);

        Assert.Equal(0, fresh.SelectedHudPreset);
        Assert.False(fresh.OriginalHudSelected);
        Assert.Equal("Top Middle", FirstRunHudPresets.All[0].Name);
        Assert.Equal(FirstRunHudPresets.All[0].Position, fresh.Hud.Position);
        Assert.Equal(FirstRunHudPresets.All[0].SideLayout, fresh.Hud.SideLayout);
        Assert.Equal(FirstRunHudPresets.All[0].Scale, fresh.Hud.Scale);
        Assert.Equal(FirstRunHudPresets.All[0].Backdrop, fresh.Hud.Backdrop);
    }

    [Fact]
    public void FreshDraftRetainsOnlyNonEditedRuntimeAndRollbackBaselines()
    {
        var existingHud = new SpeakingBarPreviewSettings(
            SpeakingBarPosition.MiddleLeft,
            SpeakingBarSideLayout.Wrapped,
            ManualLayout: true,
            VoiceControlsLayout.Vertical,
            SpeakingBarAvatarFacing.Left,
            ManualX: 0.24f,
            ManualY: 0.68f,
            SpeakingBarNamePosition.Right,
            Scale: 0.91f,
            Backdrop: false);
        var existing = new FirstRunSetupDraft
        {
            MicrophoneDevice = "Mic A",
            SpeakerDevice = "Speaker A",
            BaseVadThreshold = 0.012f,
            Hud = existingHud,
        };

        var fresh = FirstRunSetupDraft.CreateFreshFrom(existing);

        Assert.Equal("Mic A", fresh.OriginalMicrophoneDevice);
        Assert.Equal("Speaker A", fresh.OriginalSpeakerDevice);
        Assert.Equal(existingHud, fresh.OriginalHud);
        Assert.Equal(0.012f, fresh.BaseVadThreshold);
        Assert.True(fresh.MicrophoneSelectionChanged);
#if WINDOWS
        Assert.True(fresh.SpeakerSelectionChanged);
#endif
    }
}
