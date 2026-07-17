#if ANDROID
using System;
using VoiceChatPlugin.VoiceChat;
using Xunit;

namespace PerfectComms.Tests;

public sealed class AndroidTouchInputDecisionTests
{
    [Theory]
    [InlineData(true, 10f, 0f, true)]
    [InlineData(false, 10f, 10.35f, true)]
    [InlineData(false, 10.35f, 10.35f, true)]
    [InlineData(false, 10.36f, 10.35f, false)]
    [InlineData(false, 10f, 0f, false)]
    public void HandledTouchSuppressesOnlyItsSyntheticClick(
        bool touchStillTracked,
        float now,
        float suppressUntil,
        bool expected)
    {
        Assert.Equal(
            expected,
            VoiceChatHudState.ShouldSuppressHandledTouchClick(
                touchStillTracked,
                now,
                suppressUntil));
    }

    [Fact]
    public void SharedMicTooltipRefreshPreservesTheOwningControl()
    {
        Assert.Equal(
            VoiceChatHudState.SharedMicTooltipOwner.Radio,
            VoiceChatHudState.ResolveSharedMicTooltipRefreshOwner(
                tooltipActive: true,
                owner: VoiceChatHudState.SharedMicTooltipOwner.Radio));
        Assert.Equal(
            VoiceChatHudState.SharedMicTooltipOwner.Microphone,
            VoiceChatHudState.ResolveSharedMicTooltipRefreshOwner(
                tooltipActive: true,
                owner: VoiceChatHudState.SharedMicTooltipOwner.Microphone));
        Assert.Equal(
            VoiceChatHudState.SharedMicTooltipOwner.None,
            VoiceChatHudState.ResolveSharedMicTooltipRefreshOwner(
                tooltipActive: false,
                owner: VoiceChatHudState.SharedMicTooltipOwner.Radio));
    }

    [Theory]
    [InlineData((int)VoiceGamePhase.Tasks, true)]
    [InlineData((int)VoiceGamePhase.Meeting, true)]
    [InlineData((int)VoiceGamePhase.Exile, true)]
    [InlineData((int)VoiceGamePhase.Menu, false)]
    [InlineData((int)VoiceGamePhase.Lobby, false)]
    [InlineData((int)VoiceGamePhase.Intro, false)]
    [InlineData((int)VoiceGamePhase.EndGame, false)]
    [InlineData((int)VoiceGamePhase.Unknown, false)]
    public void TeamRadioTouchAppearsOnlyWherePrivateRadioRoutingExists(
        int phase,
        bool expected)
    {
        Assert.Equal(
            expected,
            VoiceChatHudState.AndroidTeamRadioPhaseSupportsPrivateRouting((VoiceGamePhase)phase));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void TeamRadioButtonRequiresEligibilityRoutingAndAllowedPhase(
        bool eligible,
        bool routingSupported,
        bool blockedByPhasePolicy,
        bool expected)
    {
        Assert.Equal(
            expected,
            VoiceChatHudState.AndroidShouldShowTeamRadioButton(
                eligible,
                routingSupported,
                blockedByPhasePolicy));
    }

    [Theory]
    [InlineData(true, true, false, false, false, false, true)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false, false)]
    [InlineData(true, true, false, true, false, false, false)]
    [InlineData(true, true, false, false, true, false, false)]
    [InlineData(true, true, false, false, false, true, false)]
    public void TeamRadioInputFailsClosedForEveryAndroidBlock(
        bool eligible,
        bool routingSupported,
        bool speakerMuted,
        bool microphoneMuted,
        bool transmitBlocked,
        bool blockedByPhasePolicy,
        bool expected)
    {
        Assert.Equal(
            expected,
            VoiceChatHudState.AndroidTeamRadioInputAvailable(
                eligible,
                routingSupported,
                speakerMuted,
                microphoneMuted,
                transmitBlocked,
                blockedByPhasePolicy));
    }

    [Theory]
    [InlineData((int)VoiceTeamRadioChannel.Impostors, "I", "Team Radio: Impostors")]
    [InlineData((int)VoiceTeamRadioChannel.Vampires, "V", "Team Radio: Vampires")]
    [InlineData((int)VoiceTeamRadioChannel.Lovers, "L", "Team Radio: Lovers")]
    [InlineData((int)VoiceTeamRadioChannel.All, "A", "Team Radio: All Teams")]
    [InlineData((int)VoiceTeamRadioChannel.None, "R", "Team Radio unavailable")]
    [InlineData(42, "R", "Team Radio unavailable")]
    public void TeamRadioTouchFeedbackNamesTheSelectedChannel(
        int channel,
        string expectedBadge,
        string expectedStatus)
    {
        var radioChannel = (VoiceTeamRadioChannel)channel;
        Assert.Equal(expectedBadge, VoiceChatHudState.AndroidTeamRadioChannelBadge(radioChannel));
        Assert.Equal(expectedStatus, VoiceChatHudState.AndroidTeamRadioChannelStatus(radioChannel));
    }

    [Fact]
    public void AndroidSettingsHideDesktopKeybindsCategory()
    {
        string[] expected = { "AUDIO", "DEVICES", "HUD", "ADVANCED" };
        VoiceSettingsCategory[] expectedOrder =
        {
            VoiceSettingsCategory.Audio,
            VoiceSettingsCategory.Devices,
            VoiceSettingsCategory.Hud,
            VoiceSettingsCategory.Advanced,
        };

        Assert.Equal(expected.Length, VoiceSettingsPanel.CategoryCountForCurrentPlatform);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], VoiceSettingsPanel.CategoryNameForCurrentPlatform(i));
            Assert.Equal(expectedOrder[i], VoiceSettingsPanel.CategoryForCurrentPlatform(i));
        }
    }

    [Fact]
    public void FirstRunCopyExplainsAndroidTouchControls()
    {
        Assert.Contains("Open Mic", AndroidVoiceUiPolicy.MicrophoneControlHelp);
        Assert.Contains("hold", AndroidVoiceUiPolicy.MicrophoneControlHelp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("radio button", AndroidVoiceUiPolicy.TeamRadioControlHelp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shortcut", AndroidVoiceUiPolicy.ControlsJourneyHelp, StringComparison.OrdinalIgnoreCase);

        string pushToTalkHelp = AndroidVoiceUiPolicy.MicModeHelp(pushToTalk: true);
        Assert.Contains("microphone button", pushToTalkHelp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release", pushToTalkHelp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shortcut", pushToTalkHelp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", pushToTalkHelp, StringComparison.OrdinalIgnoreCase);

        string pttTooltip = AndroidVoiceUiPolicy.MicrophoneTooltipAction(pushToTalk: true, radioVisible: true);
        string openMicTooltip = AndroidVoiceUiPolicy.MicrophoneTooltipAction(pushToTalk: false, radioVisible: true);
        string radioHiddenTooltip = AndroidVoiceUiPolicy.MicrophoneTooltipAction(pushToTalk: true, radioVisible: false);
        Assert.Contains("Hold mic", pttTooltip);
        Assert.Contains("Tap mic", openMicTooltip);
        Assert.Contains("Radio", pttTooltip);
        Assert.DoesNotContain("Radio", radioHiddenTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shortcut", pttTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hotkey", AndroidVoiceUiPolicy.SpeakerTooltipAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndroidCapturePolicyDisablesDesktopDspFlags()
    {
        var source = new VoiceCaptureRuntimeOptions(
            SyntheticMicToneEnabled: true,
            MicCalibrationDiagnostics: true,
            NoiseSuppressionEnabled: true,
            EchoCancellationEnabled: true,
            MicSensitivity: 1.5f);

        var normalized = AndroidVoiceCapturePolicy.Normalize(source);

        Assert.True(normalized.SyntheticMicToneEnabled);
        Assert.True(normalized.MicCalibrationDiagnostics);
        Assert.False(normalized.NoiseSuppressionEnabled);
        Assert.False(normalized.EchoCancellationEnabled);
        Assert.Equal(1.5f, normalized.MicSensitivity);
    }
}
#endif
