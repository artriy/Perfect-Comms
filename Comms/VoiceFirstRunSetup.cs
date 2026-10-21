using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Revision-gated, staged first-run setup. Fresh installs and upgrades use the exact same clean
/// recommended draft; only Finish applies it, while Use existing settings preserves local values.
/// </summary>
internal static class VoiceFirstRunSetup
{
    private const int AudioPage = 0;
    private const int ControlsPage = 1;
    private const int HudPage = 2;
    private const int ReadyPage = 3;
    private const int PageCount = 4;

    private const float PanelWidth = 1060f;
    private const float PanelHeight = 760f;
    private const float FooterHeight = 80f;
    private const float ContentTopY = -96f;
    private const float HudPreviewScale = 1.15f;
    private const float ShellClipInset = 16f;
    private const float ContentSideInset = 12f;

    private static readonly Color32 SetupSurfaceRaised = VoiceUiKit.PanelInner;
    private static readonly Color32 SetupSurfaceHover = VoiceUiKit.ControlHover;
    private static readonly Color32 SetupBorder = VoiceUiKit.Divider;
    private static readonly Color32 SetupTextSecondary = VoiceUiKit.TextMuted;
    private static readonly Color32 SetupTextTertiary = VoiceUiKit.TextFaint;
    private static readonly Color32 SetupSuccess = new(77, 225, 141, 255);
    private static readonly Color32 SetupWarning = new(245, 184, 68, 255);

    private static readonly string[] StepNames =
        { "Audio", "Controls", "HUD", "Review" };

    private static VoiceUiKit.PanelShell? _shell;
    private static RectTransform? _chromeRoot;
    private static RectTransform? _hudPreviewHost;
    private static RectTransform? _pageRoot;
    private static CanvasGroup? _pageGroup;
    private static RectTransform? _closePrompt;
    private static RectTransform? _closePromptCard;
    private static CanvasGroup? _closePromptGroup;
    private static FirstRunSetupDraft? _draft;
    private static FirstRunAudioPreview? _audioPreview;
    private static SpeakingBarLivePreview? _hudPreview;
    private static VoiceUiKit.LiveLevelMeter? _micLevelMeter;
    private static readonly List<VoiceUiKit.Row> Rows = new();
    private static readonly List<VoiceUiKit.ActionButton> Buttons = new();
    private static readonly List<VoiceUiKit.ActionButton> PromptButtons = new();
    private static readonly List<HudPresetCard> HudCards = new();

    private static TextMeshProUGUI? _outputStatus;
    private static TextMeshProUGUI? _micModeHelpText;
    private static TextMeshProUGUI? _hudDescriptionText;
    private static VoiceUiKit.ActionButton? _micTestButton;
    private static VoiceUiKit.ActionButton? _speakerTestButton;
    private static TextMeshProUGUI? _finishError;
    private static TextMeshProUGUI? _promptMessage;
    private static VoiceUiKit.Row? _pushToTalkRow;
    private static CanvasGroup? _pushToTalkGroup;
    private static bool _builtHudSpeakingBarHidden;
    private static bool _returnToReview;

    private static int _page;
    private static int _inputLockedUntilFrame;
    private static int _micListVersion;
    private static int _speakerListVersion;
    private static SpeakingBarPreviewSettings? _hoveredHudSettings;
    private static string? _hoveredHudDescription;
    private static string _verifiedMicrophoneDevice = string.Empty;
    private static string _verifiedSpeakerDevice = string.Empty;
    private static float _appear;
    private static float _pageAppear;
    private static float _hudReveal;
    private static float _promptAppear;
    private static bool _microphoneSignalDetected;
    private static bool _outputTestCompleted;
    private static bool _shown;
    private static bool _completed;
    private static bool _automaticAttemptedThisSession;

    internal static bool IsOpen => _shown && _shell != null && _shell.Root != null;
    private static float ContentWidth => Mathf.Max(1f,
        (_shell?.PaneWidth ?? PanelWidth) - ShellClipInset - ContentSideInset * 2f);

    internal static bool ShouldDeferMainMenuPopups
    {
        get
        {
            if (IsOpen) return true;
            return !_automaticAttemptedThisSession &&
                   VoiceSettings.Instance?.NeedsFirstRunSetup == true;
        }
    }

    internal static bool TryShowAutomatic()
    {
        if (IsOpen) return true;
        var settings = VoiceSettings.Instance;
        if (settings == null || !settings.NeedsFirstRunSetup || _automaticAttemptedThisSession)
            return false;
        if (VoiceSettingsPanel.IsOpen || HostSettingsPanel.IsOpen || VoiceVolumeMenu.IsOpen ||
            VoiceLobbyBrowserUi.IsOpen)
            return false;

        bool shown = Show(settings, manual: false);
        if (shown) _automaticAttemptedThisSession = true;
        return shown;
    }

    internal static void ShowManual()
    {
        var settings = VoiceSettings.Instance;
        if (settings == null) return;

        VoiceSettingsPanel.ForceClose();
        HostSettingsPanel.ForceClose();
        VoiceVolumeMenu.ForceClose();
        VoiceLobbyBrowserUi.ForceClose();
        if (Show(settings, manual: true))
            _automaticAttemptedThisSession = true;
    }

    /// <summary>
    /// Session/scene boundaries cannot leave a persistent setup preview holding platform audio.
    /// This bypasses the interactive close confirmation because voice-room privacy and capture
    /// ownership take precedence once gameplay is starting or ending.
    /// </summary>
    internal static void ForceClose()
    {
        if (!_shown && _shell == null && _audioPreview == null && _hudPreview == null) return;
        CloseInternal(destroy: true);
    }

    private static bool Show(VoiceChatLocalSettings settings, bool manual)
    {
        try
        {
            if (IsOpen) CloseInternal(destroy: true);
            VoiceUiKit.EnsureCanvas();
            VoiceUiKit.EnsureDriver();
            VoiceChatLocalSettings.MaybeRefreshDeviceLists(resolveSavedIndices: false);

            // Automatic first-run setup starts from the recommended v4 baseline. A manual rerun
            // is an editor for the user's current configuration and must not silently reset every
            // page to defaults before they have chosen a change.
            _draft = manual
                ? FirstRunSetupDraft.CaptureExisting(settings)
                : FirstRunSetupDraft.CreateFresh(settings);
            _completed = false;
            _page = AudioPage;
            _returnToReview = false;
            _appear = 0f;
            _pageAppear = 0f;
            _hudReveal = 0f;
            _promptAppear = 0f;
            _microphoneSignalDetected = false;
            _outputTestCompleted = false;
            _verifiedMicrophoneDevice = string.Empty;
            _verifiedSpeakerDevice = string.Empty;
            _shown = true;
            VoiceChatPatches.ReleaseHeldTransmitInputs();
            _inputLockedUntilFrame = Time.frameCount + 2;

            _shell = new VoiceUiKit.PanelShell(
                "VC_FirstRunSetup",
                "Perfect Comms Setup",
                PanelWidth,
                PanelHeight,
                RequestClose,
                rail: false,
                backdrop: true,
                guided: true);
            var clipMin = _shell.PaneClip.offsetMin;
            _shell.PaneClip.offsetMin = new Vector2(clipMin.x, FooterHeight + 20f);
            EnsureHudPreview();
            BuildPage();
            VoiceUiKit.RaiseAbove(_shell.RootRect);
            return true;
        }
        catch (Exception ex)
        {
            VoiceChatPlugin.VoiceChatPluginMain.Logger.LogError(
                "[PC-SETUP] Could not open first-run setup: " + ex);
            CloseInternal(destroy: true);
            return false;
        }
    }

    internal static void Tick()
    {
        if (!_shown || _shell == null) return;
        if (_shell.Root == null)
        {
            CloseInternal(destroy: false);
            return;
        }

        float dt = Mathf.Max(0f, Time.unscaledDeltaTime);
        _appear = Mathf.MoveTowards(_appear, 1f, dt / 0.22f);
        _pageAppear = Mathf.MoveTowards(_pageAppear, 1f, dt / 0.20f);
        _hudReveal = Mathf.MoveTowards(
            _hudReveal,
            !_completed && _page == HudPage && _draft?.HideSpeakingBar != true ? 1f : 0f,
            dt / 0.28f);

        // Keep local audio cleanup and Android tone completion running even while the close
        // confirmation owns input.
        _audioPreview?.Tick();
        ApplyPresentation();
        if (_closePrompt == null) _shell.TickHeader();
        if (!_shown || _shell == null) return;

        if (_closePrompt != null)
        {
            _promptAppear = Mathf.MoveTowards(_promptAppear, 1f, dt / 0.16f);
            if (_closePromptGroup != null) _closePromptGroup.alpha = Smooth(_promptAppear);
            if (_closePromptCard != null)
            {
                float promptScale = Mathf.Lerp(0.96f, 1f, Smooth(_promptAppear));
                _closePromptCard.localScale = new Vector3(promptScale, promptScale, 1f);
            }
            if (Input.GetKeyDown(KeyCode.Escape)) HideClosePrompt();
            TickButtons(PromptButtons, dt);
            TickHudPreview(dt, render: false);
            return;
        }

        if (!VoiceUiKit.RebindRow.IsCapturing && Input.GetKeyDown(KeyCode.Escape))
        {
            RequestClose();
            return;
        }

        if (_pageGroup != null && _pageRoot != null)
        {
            float eased = Smooth(_pageAppear);
            _pageGroup.alpha = eased;
            _pageRoot.anchoredPosition = new Vector2((1f - eased) * 14f, 0f);
        }

        if (VoiceUiKit.RebindRow.IsCapturing && Input.GetMouseButtonDown(0) &&
            PointerOverAnyButton(Buttons))
            VoiceUiKit.RebindRow.CancelCaptureForExternalPointer();

        HandleRows();
        for (int i = 0; i < Rows.Count; i++) Rows[i].Tick(dt);
        TickButtons(Buttons, dt);

        if (_page == AudioPage) TickAudioPage(dt);
        else if (_page == ControlsPage) TickControlsPage();
        else if (_page == HudPage) TickHudCards(dt);

        TickHudPreview(dt, render: _page == HudPage && !_completed);
    }

    internal static void CloseForSceneChange()
    {
        if (!IsOpen) return;
        CloseInternal(destroy: true);
    }

    private static void BuildPage()
    {
        if (_shell == null || _draft == null) return;

        VoiceUiKit.RebindRow.CancelCapture();
        Rows.Clear();
        Buttons.Clear();
        HudCards.Clear();
        _outputStatus = null;
        _micModeHelpText = null;
        _hudDescriptionText = null;
        _micLevelMeter = null;
        _micTestButton = null;
        _speakerTestButton = null;
        _finishError = null;
        _pushToTalkRow = null;
        _pushToTalkGroup = null;
        _hoveredHudSettings = null;
        _hoveredHudDescription = null;

        if (_chromeRoot != null)
        {
            _chromeRoot.gameObject.SetActive(false);
            Object.Destroy(_chromeRoot.gameObject);
        }
        if (_pageRoot != null)
        {
            _pageRoot.gameObject.SetActive(false);
            Object.Destroy(_pageRoot.gameObject);
        }
        _chromeRoot = VoiceUiKit.Rect("SetupChrome", _shell.RootRect);
        _chromeRoot.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _chromeRoot.offsetMin = Vector2.zero;
        _chromeRoot.offsetMax = Vector2.zero;

        _pageRoot = VoiceUiKit.Rect("SetupPage", _shell.PaneRoot);
        _pageRoot.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        _pageRoot.sizeDelta = new Vector2(-ContentSideInset * 2f, 0f);
        _pageRoot.anchoredPosition = new Vector2(14f, 0f);
        _pageGroup = _pageRoot.gameObject.AddComponent<CanvasGroup>();
        _pageGroup.alpha = 0f;
        _pageAppear = 0f;
        _inputLockedUntilFrame = Time.frameCount + 1;

        BuildStepProgress();
        if (_completed)
            BuildCompletedPage();
        else
        {
            switch (_page)
            {
                case AudioPage: BuildAudioPage(); break;
                case ControlsPage: BuildControlsPage(); break;
                case HudPage: BuildHudPage(); break;
                default: BuildReadyPage(); break;
            }
        }
        BuildFooter();
    }

    private static void BuildStepProgress()
    {
        if (_chromeRoot == null) return;
        var progress = VoiceUiKit.Rect("SetupProgress", _chromeRoot);
        PlaceTopLeft(progress, PanelWidth - 348f, -20f, 240f, 60f);
        AddText(progress, _completed ? "Setup saved" : StepNames[_page], 18f,
            VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold,
            Vector2.zero, new Vector2(174f, 28f));
        AddText(progress, $"{(_completed ? PageCount : _page + 1)} / {PageCount}", 14f,
            SetupTextSecondary, TextAlignmentOptions.Right, FontStyles.Normal,
            new Vector2(176f, 0f), new Vector2(64f, 28f));
        const float gap = 6f;
        float width = (240f - gap * (PageCount - 1)) / PageCount;
        for (int i = 0; i < PageCount; i++)
        {
            var track = VoiceUiKit.Panel("StepTrack", progress, VoiceUiKit.TrackBg, rounded: false);
            PlaceTopLeft(track.rectTransform, i * (width + gap), -40f, width, 3f);
            if (!_completed && i > _page) continue;
            var accent = VoiceUiKit.SpectrumImage("StepProgress", track.transform);
            accent.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            accent.rectTransform.offsetMin = Vector2.zero;
            accent.rectTransform.offsetMax = Vector2.zero;
        }
    }


    private static void BuildAudioPage()
    {
        if (_pageRoot == null || _shell == null || _draft == null) return;
        BuildPageHeading("Let's get you heard.", "Choose your devices first. Nothing changes until the final save.");
        VoiceChatLocalSettings.MaybeRefreshDeviceLists(resolveSavedIndices: false);
        _micListVersion = VoiceChatLocalSettings.MicDeviceListVersion;
        _speakerListVersion = VoiceChatLocalSettings.SpkDeviceListVersion;
        _audioPreview ??= new FirstRunAudioPreview();

        float channelWidth = (ContentWidth - 32f) * 0.5f;
        var input = VoiceUiKit.Rect("InputChannel", _pageRoot);
        PlaceTopLeft(input, 0f, ContentTopY, channelWidth, 196f);
        var output = VoiceUiKit.Rect("OutputChannel", _pageRoot);
        PlaceTopLeft(output, channelWidth + 32f, ContentTopY, channelWidth, 196f);
        AddText(input, "YOUR VOICE", 13f, VoiceUiKit.Accent, TextAlignmentOptions.Left,
            FontStyles.Bold, new Vector2(22f, 0f), new Vector2(channelWidth - 44f, 22f));
        AddText(output, "YOUR LOBBY", 13f, VoiceUiKit.Accent, TextAlignmentOptions.Left,
            FontStyles.Bold, new Vector2(22f, 0f), new Vector2(channelWidth - 44f, 22f));
        AddRow(new VoiceUiKit.StepperRow(
                DraftMicrophoneIndexForUi,
                i =>
                {
                    if (_audioPreview?.IsSpeakerTestBusy == true ||
                        i >= VoiceChatLocalSettings.MicDeviceNames.Length) return;
                    _draft?.SetMicrophoneIndex(i);
                    if (_draft == null) return;
                    _microphoneSignalDetected = false;
                    _verifiedMicrophoneDevice = string.Empty;
                    _audioPreview?.InvalidateMicrophoneVerification();
                    _audioPreview?.RestartForDeviceChange(_draft);
                },
                DraftMicrophoneCountForUi, DraftMicrophoneNameForUi,
                compactFullWidth: true)
            .Build(input, "Microphone", channelWidth, -30f, 86f,
                "The microphone Perfect Comms captures."));
        _micTestButton = AddButton(input, "Test mic", 154f, 44f,
            22f, -136f, VoiceUiKit.ButtonKind.Secondary,
            () =>
            {
                if (_audioPreview == null || _draft == null) return;
                if (_audioPreview.IsMicrophoneTestActive) _audioPreview.StopMicrophone();
                else _audioPreview.StartMicrophone(_draft);
            },
            () => _audioPreview?.IsSpeakerTestBusy != true);
        _micLevelMeter = new VoiceUiKit.LiveLevelMeter(
            input, "MicLiveLevel", channelWidth - 222f, 8f, hideWhenInactive: true);
        PlaceTopLeft(_micLevelMeter.Root, 198f, -154f, channelWidth - 222f, 8f);
#if WINDOWS
        AddRow(new VoiceUiKit.StepperRow(
                DraftSpeakerIndexForUi,
                i =>
                {
                    if (_audioPreview?.IsSpeakerTestBusy == true ||
                        i >= VoiceChatLocalSettings.SpkDeviceNames.Length) return;
                    _draft?.SetSpeakerIndex(i);
                    _outputTestCompleted = false;
                    _verifiedSpeakerDevice = string.Empty;
                    _audioPreview?.InvalidateOutputVerification();
                },
                DraftSpeakerCountForUi, DraftSpeakerNameForUi,
                compactFullWidth: true)
            .Build(output, "Headphones or speakers", channelWidth, -30f, 86f,
                "Where you hear other players."));
#else
        BuildInlineNotice(output, 22f, -30f, channelWidth - 44f, 86f,
            "Output follows your current Android audio route.", VoiceUiKit.Accent);
#endif
        _speakerTestButton = AddButton(output, "Test output", 154f, 44f,
            22f, -136f, VoiceUiKit.ButtonKind.Secondary,
            () => _audioPreview?.PlayTestSound(_draft),
            () => _audioPreview is { IsSpeakerTestBusy: false, IsMicrophoneTestStarting: false });
        _outputStatus = AddText(_pageRoot, "Mic and output tests are optional", 16f,
            SetupTextSecondary, TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(22f, -302f), new Vector2(ContentWidth - 44f, 42f), wrap: true);
        var divider = VoiceUiKit.Panel("AudioDivider", _pageRoot, VoiceUiKit.Divider, rounded: false);
        PlaceTopLeft(divider.rectTransform, 22f, -356f, ContentWidth - 44f, 1f);
        AddRow(new VoiceUiKit.SliderRow(
                () => _draft.MasterVolume, v => _draft.MasterVolume = v,
                0.1f, 2f, v => $"{Mathf.RoundToInt(v * 100f)}%", stacked: true)
            .Build(_pageRoot, "Voice playback volume", ContentWidth, -374f, 76f,
                "Overall volume for other players."));
        AddText(_pageRoot, "Tests are optional. You can change these choices later.", 14f,
            SetupTextTertiary, TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(22f, -476f), new Vector2(ContentWidth - 44f, 24f));
    }

    private static void BuildControlsPage()
    {
        if (_pageRoot == null || _draft == null) return;
        BuildPageHeading("Your voice, your controls.", "Pick a talk mode, then put the essentials within reach.");
        string microphoneModeHelp =
#if ANDROID
            "Open Mic uses voice activation. Push To Talk sends only while you hold the microphone button.";
#else
            "Open Mic uses voice activation. Push To Talk only sends while its key is held.";
#endif
        AddRow(new VoiceUiKit.MicModeRow(
                () => _draft.MicMode, mode => _draft.MicMode = mode)
            .Build(_pageRoot, ContentWidth, ContentTopY, 64f, microphoneModeHelp));
        _micModeHelpText = AddText(_pageRoot, "", 15f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(22f, -164f), new Vector2(ContentWidth - 44f, 42f), wrap: true);
        AddText(_pageRoot, "JOINING & SHORTCUTS", 13f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(22f, -214f), new Vector2(ContentWidth - 44f, 22f));
#if WINDOWS
        float startupWidth = (ContentWidth - 32f) / 3f;
#else
        float startupWidth = (ContentWidth - 16f) * 0.5f;
#endif
        var muted = VoiceUiKit.Rect("StartMuted", _pageRoot);
        PlaceTopLeft(muted, 0f, -238f, startupWidth, 58f);
        AddRow(new VoiceUiKit.ToggleRow(() => _draft.StartMuted, value => _draft.StartMuted = value)
            .Build(muted, "Start muted", startupWidth, 0f, 58f,
                "Join each voice room with your microphone muted.", stacked: true));
        var deafened = VoiceUiKit.Rect("StartDeafened", _pageRoot);
        PlaceTopLeft(deafened, startupWidth + 16f, -238f, startupWidth, 58f);
        AddRow(new VoiceUiKit.ToggleRow(() => _draft.StartDeafened, value => _draft.StartDeafened = value)
            .Build(deafened, "Start deafened", startupWidth, 0f, 58f,
                "Join with playback muted and microphone transmission paused until you undeafen.", stacked: true));
#if WINDOWS
        var chat = VoiceUiKit.Rect("ChatShortcuts", _pageRoot);
        PlaceTopLeft(chat, (startupWidth + 16f) * 2f, -238f, startupWidth, 58f);
        AddRow(new VoiceUiKit.ToggleRow(
                () => _draft.AllowKeybindsWhileChatOpen, value => _draft.AllowKeybindsWhileChatOpen = value)
            .Build(chat, "Shortcuts in chat", startupWidth, 0f, 58f,
                "Keeps shortcuts active while typing. Printable keys can also type into chat.", stacked: true));
        AddText(_pageRoot, "YOUR SHORTCUTS", 13f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(22f, -318f), new Vector2(ContentWidth - 44f, 22f));
        float width = (ContentWidth - 32f) * 0.5f;
        var left = VoiceUiKit.Rect("PrimaryShortcuts", _pageRoot);
        PlaceTopLeft(left, 0f, -344f, width, 162f);
        var right = VoiceUiKit.Rect("OtherShortcuts", _pageRoot);
        PlaceTopLeft(right, width + 32f, -344f, width, 162f);
        AddBindingRow(left, width, 0f, "Mute mic",
            () => _draft.ToggleMute, value => _draft.ToggleMute = value, "Toggle microphone transmission.");
        AddBindingRow(left, width, -54f, "Hold to mute",
            () => _draft.PushToMute, value => _draft.PushToMute = value, "Mute only while held.");
        _pushToTalkRow = AddBindingRow(left, width, -108f, "Push to talk",
            () => _draft.PushToTalk, value => _draft.PushToTalk = value, "Required in Push To Talk mode.");
        _pushToTalkGroup = _pushToTalkRow.Root.gameObject.AddComponent<CanvasGroup>();
        AddBindingRow(right, width, 0f, "Deafen",
            () => _draft.ToggleSpeaker, value => _draft.ToggleSpeaker = value, "Toggle playback and microphone pause.");
        AddBindingRow(right, width, -54f, "Open voice menu",
            () => _draft.OpenVoiceSettings, value => _draft.OpenVoiceSettings = value, null);
        AddText(right, "Select a shortcut, then press a key or chord.", 14f, SetupTextTertiary,
            TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(22f, -112f), new Vector2(width - 44f, 42f), wrap: true);
#else
        AddText(_pageRoot, "IN-GAME TOUCH CONTROLS", 13f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(22f, -318f), new Vector2(ContentWidth - 44f, 22f));
        float width = (ContentWidth - 32f) / 3f;
        BuildTouchGuide(0f, width, VoiceUiBranding.Logo(), "Microphone", AndroidVoiceUiPolicy.MicrophoneControlHelp);
        BuildTouchGuide(width + 16f, width, VoiceUiBranding.CategoryIcon(VoiceSettingsCategory.Audio),
            "Team Radio", AndroidVoiceUiPolicy.TeamRadioControlHelp);
        BuildTouchGuide((width + 16f) * 2f, width, VoiceUiBranding.CategoryIcon(VoiceSettingsCategory.Devices),
            "Deafen", "Tap the speaker button to mute playback and pause microphone transmission.");
#endif
        TickControlsPage();
    }

    private static void BuildHudPage()
    {
        if (_pageRoot == null || _draft == null) return;
        _hudDescriptionText = BuildPageHeading("Make it yours.", "Preview a layout, then choose what's visible in game.");
        EnsureHudPreview();
        _builtHudSpeakingBarHidden = _draft.HideSpeakingBar;
        float previewWidth = SpeakingBarLivePreview.EmbeddedCardSize.x * HudPreviewScale;
        float pickerX = 22f + previewWidth + 32f;
        float pickerWidth = ContentWidth - pickerX - 22f;
        var picker = VoiceUiKit.Rect("LayoutChoices", _pageRoot);
        PlaceTopLeft(picker, pickerX, ContentTopY, pickerWidth, 302f);
        picker.gameObject.AddComponent<CanvasGroup>().alpha = _draft.HideSpeakingBar ? 0.38f : 1f;
        AddText(picker, "LAYOUTS", 13f, SetupTextSecondary, TextAlignmentOptions.Left, FontStyles.Bold,
            Vector2.zero, new Vector2(pickerWidth, 24f));
        Buttons.Add(new VoiceUiKit.ActionButton(picker, "Use existing layout", 200f, 32f,
            pickerWidth - 200f, 4f, VoiceUiKit.ButtonKind.Ghost,
            () =>
            {
                _draft.SelectOriginalHud();
                BuildPage();
            }, () => !_draft.HideSpeakingBar));
        float cardWidth = (pickerWidth - 12f) * 0.5f;
        for (int i = 0; i < FirstRunHudPresets.All.Count; i++)
        {
            var preset = FirstRunHudPresets.All[i];
            var card = new HudPresetCard(i, preset.Name, preset.Description,
                FirstRunHudPresets.Apply(_draft.Hud, preset));
            card.Build(picker, i % 2 * (cardWidth + 12f), -44f - i / 2 * 52f,
                cardWidth, 44f);
            HudCards.Add(card);
        }
        if (_draft.HideSpeakingBar)
        {
            var placeholder = VoiceUiKit.Panel("HiddenSpeakingBar", _pageRoot, SetupSurfaceRaised);
            PlaceTopLeft(placeholder.rectTransform, 22f, ContentTopY,
                previewWidth, SpeakingBarLivePreview.EmbeddedCardSize.y * HudPreviewScale);
            AddText(placeholder.transform, "Speaking bar hidden", 24f, VoiceUiKit.TextBright,
                TextAlignmentOptions.Center, FontStyles.Bold,
                new Vector2(24f, -100f), new Vector2(previewWidth - 48f, 38f));
            AddText(placeholder.transform, "Enable it below to preview layouts.", 16f, SetupTextSecondary,
                TextAlignmentOptions.Center, FontStyles.Normal,
                new Vector2(24f, -146f), new Vector2(previewWidth - 48f, 28f));
        }
        float width = (ContentWidth - 32f) * 0.5f;
        var left = VoiceUiKit.Rect("LeftHudVisibility", _pageRoot);
        PlaceTopLeft(left, 0f, -416f, width, 108f);
        var right = VoiceUiKit.Rect("RightHudVisibility", _pageRoot);
        PlaceTopLeft(right, width + 32f, -416f, width, 108f);
        AddRow(new VoiceUiKit.ToggleRow(
                () => !_draft.HideVoiceControls, value => _draft.HideVoiceControls = !value)
            .Build(left, "Voice controls", width, 0f, 54f,
                "Microphone, deafen, and radio buttons. Keyboard shortcuts remain available."))
            .ShowDescription("Microphone, speaker and radio buttons.");
        AddRow(new VoiceUiKit.ToggleRow(
                () => !_draft.HideSpeakingBar, value => _draft.HideSpeakingBar = !value)
            .Build(left, "Speaking bar", width, -54f, 54f, "Show speaking players and their avatars."))
            .ShowDescription("The player list shown while talking.");
        AddRow(new VoiceUiKit.ToggleRow(
                () => !_draft.HideMeetingOverlay, value => _draft.HideMeetingOverlay = !value)
            .Build(right, "Meeting glow", width, 0f, 54f, "Speaking glow around meeting cards."))
            .ShowDescription("Show who is talking in meetings.");
        AddRow(new VoiceUiKit.ToggleRow(
                () => !_draft.HideConnectionStatus, value => _draft.HideConnectionStatus = !value)
            .Build(right, "Connection status", width, -54f, 54f, "Lobby connection progress and retry messages."))
            .ShowDescription("Connection progress and retry messages.");
        if (_hudPreviewHost != null) _hudPreviewHost.SetAsLastSibling();
    }

    private static void BuildReadyPage()
    {
        if (_pageRoot == null || _shell == null || _draft == null) return;
        BuildPageHeading("Review and save", "Check the essentials, then apply everything together.");

        BuildSummaryRow(ContentTopY, VoiceSettingsCategory.Devices, "Audio", new[]
        {
            "Input: " + CompactDevice(_draft.MicrophoneDisplayName()),
#if WINDOWS
            "Output: " + CompactDevice(_draft.SpeakerDisplayName()),
#else
            "Output: Android audio route",
#endif
            $"Playback volume: {Mathf.RoundToInt(_draft.MasterVolume * 100f)}%",
            AudioVerificationSummary(),
        }, () => GoTo(AudioPage));

        string talkMode = _draft.MicMode == VoiceMicMode.OpenMic ? "Open Mic" : "Push to Talk";
        BuildSummaryRow(ContentTopY - 116f, VoiceSettingsCategory.Keybinds, "Controls", new[]
        {
            "Mode: " + talkMode,
#if ANDROID
            _draft.MicMode == VoiceMicMode.PushToTalk
                ? "PTT: hold the microphone button"
                : "Mic button: tap to mute",
            "Radio (when available): tap to change channel / hold to talk",
#else
            _draft.MicMode == VoiceMicMode.PushToTalk
                ? "PTT: " + _draft.PushToTalk.Label
                : "Mute toggle: " + _draft.ToggleMute.Label,
            $"Push to Mute: {_draft.PushToMute.Label} / Menu: {_draft.OpenVoiceSettings.Label}",
#endif
            $"Start muted: {YesNo(_draft.StartMuted)} / Start deafened: {YesNo(_draft.StartDeafened)}",
        }, () => GoTo(ControlsPage));

        string hudName = _draft.HideSpeakingBar
            ? "Speaking bar hidden"
            : _draft.SelectedHudPreset >= 0
                ? FirstRunHudPresets.All[_draft.SelectedHudPreset].Name
                : "Custom layout";
        BuildSummaryRow(ContentTopY - 232f, VoiceSettingsCategory.Hud, "HUD", new[]
        {
            hudName,
            $"Controls {ShownHidden(_draft.HideVoiceControls)} / Connection {ShownHidden(_draft.HideConnectionStatus)}",
            _draft.HideSpeakingBar
                ? $"Meeting glow {ShownHidden(_draft.HideMeetingOverlay)} / Speaking bar hidden"
                : $"Meeting glow {ShownHidden(_draft.HideMeetingOverlay)} / {HudPlacementSummary(_draft.Hud)}",
            _draft.HideSpeakingBar
                ? "Layout can be restored from HUD settings"
                : $"Scale {Mathf.RoundToInt(_draft.Hud.Scale * 100f)}% / Backdrop {(_draft.Hud.Backdrop ? "on" : "off")}",
        }, () => GoTo(HudPage));

        string? issue = SetupValidationIssue();
        Color32 noticeColor = issue == null ? SetupSuccess : SetupWarning;
        var notice = BuildInlineNotice(_pageRoot, 22f, -466f, ContentWidth - 44f, 60f,
            issue ?? "Save these choices on this device. You can adjust them later in Settings.",
            noticeColor);
        _finishError = notice.GetComponentInChildren<TextMeshProUGUI>();
    }

    private static void BuildCompletedPage()
    {
        if (_pageRoot == null) return;
        float center = ContentWidth * 0.5f;
        BuildCheckBadge(_pageRoot, new Vector2(center - 34f, -92f), 68f, VoiceUiKit.Accent);
        AddText(_pageRoot, "Your setup is saved.", 34f, VoiceUiKit.TextBright,
            TextAlignmentOptions.Center, FontStyles.Bold,
            new Vector2(22f, -190f), new Vector2(ContentWidth - 44f, 52f));
        AddText(_pageRoot, "Join a lobby to start using Perfect Comms.", 18f, SetupTextSecondary,
            TextAlignmentOptions.Center, FontStyles.Normal,
            new Vector2(100f, -254f), new Vector2(ContentWidth - 200f, 32f));
        AddText(_pageRoot, "Audio, controls and HUD saved on this device.", 15f, SetupTextTertiary,
            TextAlignmentOptions.Center, FontStyles.Normal,
            new Vector2(100f, -310f), new Vector2(ContentWidth - 200f, 30f));
    }

    private static void BuildFooter()
    {
        if (_chromeRoot == null) return;
        var footer = VoiceUiKit.Panel("SetupFooter", _chromeRoot, VoiceUiKit.RailSurface, rounded: false);
        footer.rectTransform.Anchor(Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f));
        footer.rectTransform.sizeDelta = new Vector2(-2f, FooterHeight);
        footer.rectTransform.anchoredPosition = new Vector2(0f, 1f);
        var divider = VoiceUiKit.Panel("FooterDivider", footer.transform, VoiceUiKit.Divider, rounded: false);
        PlaceTopLeft(divider.rectTransform, 0f, 0f, PanelWidth, 1f);
        float nextX = PanelWidth - 210f;
        if (_completed)
        {
            AddButton(footer.rectTransform, "Advanced settings", 210f, 48f,
                28f, -16f, VoiceUiKit.ButtonKind.Secondary, () =>
                {
                    CloseInternal(destroy: true);
                    VoiceSettingsPanel.ShowDeferred();
                });
            AddButton(footer.rectTransform, "Done", 182f, 48f,
                nextX, -16f, VoiceUiKit.ButtonKind.Primary, () => CloseInternal(destroy: true));
            return;
        }
        AddButton(footer.rectTransform, "Use existing settings", 210f, 48f,
            28f, -16f, VoiceUiKit.ButtonKind.Ghost, UseExistingSettings);
        if (_page > AudioPage && !_returnToReview)
            AddButton(footer.rectTransform, "Back", 108f, 48f,
                nextX - 124f, -16f, VoiceUiKit.ButtonKind.Secondary, () => GoTo(_page - 1));
        if (_returnToReview)
            AddButton(footer.rectTransform, "Back to review", 182f, 48f,
                nextX, -16f, VoiceUiKit.ButtonKind.Primary, () => GoTo(ReadyPage));
        else if (_page < ReadyPage)
            AddButton(footer.rectTransform, "Continue", 182f, 48f,
                nextX, -16f, VoiceUiKit.ButtonKind.Primary, () => GoTo(_page + 1));
        else
            AddButton(footer.rectTransform, "Save & finish", 182f, 48f,
                nextX, -16f, VoiceUiKit.ButtonKind.Primary, FinishSetup, () => SetupValidationIssue() == null);
    }

    private static void UseExistingSettings()
    {
        try
        {
            VoiceUiKit.RebindRow.CancelCapture();
            VoiceSettings.Instance?.UseExistingSettingsForFirstRunSetup();
            DisposeAudioPreview();
            CloseInternal(destroy: true);
        }
        catch (Exception ex)
        {
            VoiceChatPlugin.VoiceChatPluginMain.Logger.LogError(
                "[PC-SETUP] Could not keep existing settings: " + ex);
            ShowClosePrompt();
            if (_promptMessage != null)
            {
                _promptMessage.color = VoiceUiKit.Danger;
                _promptMessage.text =
                    "Existing settings were not changed, but the setup choice could not be saved. Please try again.";
            }
        }
    }

    private static void GoTo(int page)
    {
        page = Mathf.Clamp(page, AudioPage, ReadyPage);
        if (page == _page) return;
        if (page == ReadyPage) _returnToReview = false;
        else if (_page == ReadyPage) _returnToReview = true;
        if (_page == AudioPage)
        {
            CaptureAudioCheckResults();
            DisposeAudioPreview();
        }
        if (_page == HudPage) _hudPreview?.Suspend();
        if (_page == HudPage) _hudReveal = 0f;
        VoiceUiKit.RebindRow.CancelCapture();
        _page = page;
        BuildPage();
    }

    private static void FinishSetup()
    {
        if (_draft == null || VoiceSettings.Instance == null) return;
        string? issue = SetupValidationIssue();
        if (issue != null)
        {
            if (_finishError != null)
            {
                _finishError.color = SetupWarning;
                _finishError.text = issue;
            }
            return;
        }
        try
        {
            VoiceUiKit.RebindRow.CancelCapture();
            DisposeAudioPreview();
            VoiceSettings.Instance.CommitFirstRunSetup(_draft);
            _completed = true;
            _page = ReadyPage;
            BuildPage();
        }
        catch (Exception ex)
        {
            VoiceChatPlugin.VoiceChatPluginMain.Logger.LogError(
                "[PC-SETUP] Could not save setup: " + ex);
            if (_finishError != null)
                _finishError.text = "Could not save your setup. Your previous settings are still active. Try again.";
        }
    }

    private static void RequestClose()
    {
        if (_completed)
        {
            CloseInternal(destroy: true);
            return;
        }
        ShowClosePrompt();
    }

    private static void ShowClosePrompt()
    {
        if (_shell == null || _closePrompt != null) return;
        VoiceUiKit.RebindRow.CancelCapture();
        CaptureAudioCheckResults();
        _audioPreview?.StopAllTests();
        _closePrompt = VoiceUiKit.Rect("SetupClosePrompt", _shell.RootRect);
        _closePrompt.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _closePrompt.offsetMin = Vector2.zero;
        _closePrompt.offsetMax = Vector2.zero;
        _closePrompt.SetAsLastSibling();

        var shade = _closePrompt.gameObject.AddComponent<Image>();
        shade.sprite = VoiceUiKit.Solid(Color.white);
        shade.color = new Color32(0, 0, 0, 205);
        shade.raycastTarget = true;
        _closePromptGroup = _closePrompt.gameObject.AddComponent<CanvasGroup>();
        _closePromptGroup.alpha = 0f;
        _promptAppear = 0f;

        var card = VoiceUiKit.Panel("PromptCard", _closePrompt, SetupSurfaceRaised, rounded: true);
        card.rectTransform.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        card.rectTransform.sizeDelta = new Vector2(620f, 292f);
        card.rectTransform.anchoredPosition = Vector2.zero;
        _closePromptCard = card.rectTransform;
        AddPanelBorder(card.rectTransform, SetupBorder);
        var spectrum = VoiceUiKit.SpectrumImage("PromptSpectrum", card.rectTransform);
        PlaceTopLeft(spectrum.rectTransform, 24f, 0f, 572f, 2f);

        AddText(card.rectTransform, "Leave setup?", 28f,
            VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(28f, -24f), new Vector2(564f, 42f));
        _promptMessage = AddText(card.rectTransform,
            "Your changes haven't been saved. Keep setting up, or keep your current settings.",
            18f, SetupTextSecondary, TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(28f, -82f), new Vector2(564f, 64f), wrap: true);
        AddText(card.rectTransform, "You can run setup again from Settings.", 14f,
            SetupTextTertiary, TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(28f, -160f), new Vector2(564f, 28f));
        PromptButtons.Clear();
        PromptButtons.Add(new VoiceUiKit.ActionButton(card.rectTransform, "Use existing settings", 230f, 48f,
            28f, -216f, VoiceUiKit.ButtonKind.Secondary, UseExistingSettings));
        PromptButtons.Add(new VoiceUiKit.ActionButton(card.rectTransform, "Continue setup", 230f, 48f,
            362f, -216f, VoiceUiKit.ButtonKind.Primary, HideClosePrompt));
        VoiceUiKit.SwallowClick();
        _inputLockedUntilFrame = Time.frameCount + 1;
    }

    private static void HideClosePrompt()
    {
        if (_closePrompt != null) Object.Destroy(_closePrompt.gameObject);
        _closePrompt = null;
        _closePromptCard = null;
        _closePromptGroup = null;
        _promptMessage = null;
        PromptButtons.Clear();
        _inputLockedUntilFrame = Time.frameCount + 1;
    }

    private static void CloseInternal(bool destroy)
    {
        _shown = false;
        _returnToReview = false;
        VoiceUiKit.RebindRow.CancelCapture();
        DisposeAudioPreview();
        if (_hudPreview != null)
        {
            try { _hudPreview.Dispose(); } catch { }
            _hudPreview = null;
        }
        if (destroy && _shell?.Root != null) Object.Destroy(_shell.Root);
        _shell = null;
        _chromeRoot = null;
        _hudPreviewHost = null;
        _pageRoot = null;
        _pageGroup = null;
        _closePrompt = null;
        _closePromptCard = null;
        _closePromptGroup = null;
        _promptMessage = null;
        _draft = null;
        Rows.Clear();
        Buttons.Clear();
        PromptButtons.Clear();
        HudCards.Clear();
        VoiceUiKit.SwallowClick();
    }

    private static void TickAudioPage(float dt)
    {
        if (_draft == null || _audioPreview == null) return;
        VoiceChatLocalSettings.MaybeRefreshDeviceLists(resolveSavedIndices: false);
        _audioPreview.RefreshMicrophoneSettings(_draft);
        _micLevelMeter?.SetLevel(_audioPreview.LiveLevel, _audioPreview.IsListening);
        _micLevelMeter?.Tick(dt, _audioPreview.IsMicrophoneTestActive);
        if (_audioPreview.MicrophoneSignalDetected)
        {
            _microphoneSignalDetected = true;
            _verifiedMicrophoneDevice = _draft.MicrophoneDevice;
        }
        if (_audioPreview.OutputTestCompleted)
        {
            _outputTestCompleted = true;
            _verifiedSpeakerDevice = _draft.SpeakerDevice;
        }
        if (_outputStatus != null)
        {
            _outputStatus.text = _audioPreview.Status;
            _outputStatus.color = _audioPreview.MicrophoneSignalDetected ||
                                  _audioPreview.OutputTestCompleted
                ? SetupSuccess
                : SetupTextSecondary;
        }
        _micTestButton?.SetText(_audioPreview.IsMicrophoneTestStarting
            ? "Cancel mic start"
            : _audioPreview.IsMicrophoneTestActive ? "Stop mic test" : "Test mic");
        _speakerTestButton?.SetText(_audioPreview.IsPlayingTone
            ? "Playing..."
            : _audioPreview.IsPreparingTone ? "Opening output..." : "Test output");

        if (_audioPreview.ConsumeUiRefresh() ||
            (!VoiceUiKit.RebindRow.IsCapturing &&
             (_micListVersion != VoiceChatLocalSettings.MicDeviceListVersion ||
              _speakerListVersion != VoiceChatLocalSettings.SpkDeviceListVersion)))
        {
            BuildPage();
        }
    }

    private static void TickControlsPage()
    {
        if (_draft == null) return;
        bool pushToTalk = _draft.MicMode == VoiceMicMode.PushToTalk;
        if (_micModeHelpText != null)
        {
#if ANDROID
            _micModeHelpText.text = AndroidVoiceUiPolicy.MicModeHelp(pushToTalk);
#else
            _micModeHelpText.text = pushToTalk
                ? "Voice is sent only while your\nPush to Talk shortcut is held."
                : "Voice activation sends speech\nautomatically when you talk.";
#endif
            _micModeHelpText.color = SetupTextSecondary;
        }
        if (_pushToTalkRow != null)
            _pushToTalkRow.Title.color = pushToTalk ? VoiceUiKit.TextPrimary : SetupTextTertiary;
        if (_pushToTalkGroup != null)
            _pushToTalkGroup.alpha = Mathf.MoveTowards(
                _pushToTalkGroup.alpha, pushToTalk ? 1f : 0.52f,
                Mathf.Max(0f, Time.unscaledDeltaTime) * 5f);
    }

    private static void TickHudCards(float dt)
    {
        if (_draft != null && _builtHudSpeakingBarHidden != _draft.HideSpeakingBar)
        {
            BuildPage();
            return;
        }
        _hoveredHudSettings = null;
        _hoveredHudDescription = null;
        if (_draft?.HideSpeakingBar == true)
        {
            if (_hudDescriptionText != null)
                _hudDescriptionText.text = "Enable the speaking bar below to choose and preview a layout.";
            return;
        }
        for (int i = 0; i < HudCards.Count; i++) HudCards[i].Tick(dt);
        if (_hudDescriptionText != null)
        {
            _hudDescriptionText.text = _hoveredHudDescription ?? SelectedHudDescription();
            _hudDescriptionText.color = _hoveredHudDescription != null
                ? VoiceUiKit.TextPrimary
                : SetupTextSecondary;
        }
    }

    private static void EnsureHudPreview()
    {
        if (_hudPreview != null || _shell == null) return;
        try
        {
            _hudPreviewHost = VoiceUiKit.Rect("SetupHudPreviewHost", _shell.PaneRoot);
            Vector2 previewSize = SpeakingBarLivePreview.EmbeddedCardSize;
            PlaceTopLeft(_hudPreviewHost, ContentSideInset + 22f, ContentTopY,
                previewSize.x, previewSize.y);
            _hudPreviewHost.localScale = new Vector3(HudPreviewScale, HudPreviewScale, 1f);
            _hudPreview = new SpeakingBarLivePreview(_hudPreviewHost, Vector2.zero, embedded: true);
        }
        catch (Exception ex)
        {
            VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning(
                "[PC-SETUP] HUD live preview is unavailable: " + ex.Message);
            if (_hudPreviewHost != null)
            {
                _hudPreviewHost.gameObject.SetActive(false);
                Object.Destroy(_hudPreviewHost.gameObject);
                _hudPreviewHost = null;
            }
            _hudPreview = null;
        }
    }

    private static void TickHudPreview(float dt, bool render)
    {
        if (_hudPreview == null || _draft == null) return;
        float reveal = render ? Smooth(_hudReveal) : 0f;
        try
        {
            if (_draft.HideSpeakingBar)
            {
                _hudPreview.SetPresentation(0f, shouldRender: false);
                return;
            }
            var settings = _hoveredHudSettings ?? _draft.Hud;
            if (!_hudPreview.IsWarmupReady)
                _hudPreview.Prewarm(settings, dt);
            bool visible = render && reveal > 0.01f;
            _hudPreview.SetPresentation(reveal, visible);
            if (visible)
                _hudPreview.Tick(settings, dt);
        }
        catch (Exception ex)
        {
            VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning(
                "[PC-SETUP] HUD preview was disabled: " + ex.Message);
            try { _hudPreview.Dispose(); } catch { }
            _hudPreview = null;
        }
    }

    private static void ApplyPresentation()
    {
        if (_shell == null) return;
        Rect canvas = VoiceUiKit.CanvasRect.rect;
        float availableW = canvas.width > 1f ? canvas.width : 1920f;
        float availableH = canvas.height > 1f ? canvas.height : 1080f;
        float maxScale = Mathf.Min(1f,
            Mathf.Min(availableW / (PanelWidth + 64f), availableH / (PanelHeight + 64f)));
        float scale = maxScale * Mathf.Lerp(0.96f, 1f, Smooth(_appear));
        _shell.RootRect.localScale = new Vector3(scale, scale, 1f);
        _shell.Group.alpha = Smooth(_appear);
        _shell.SetLayoutOffset(Vector2.zero);
    }

    private static void HandleRows()
    {
        if (Time.frameCount <= _inputLockedUntilFrame) return;
        if (!Input.GetMouseButton(0))
        {
            for (int i = 0; i < Rows.Count; i++) Rows[i].OnMouseUp();
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
            for (int i = 0; i < Rows.Count; i++) Rows[i].OnMouseDown();
        }
        else
        {
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i].IsDragging) Rows[i].OnMouseDrag();
        }
    }

    private static void TickButtons(List<VoiceUiKit.ActionButton> buttons, float dt)
    {
        for (int i = 0; i < buttons.Count; i++)
            buttons[i].Tick(dt, Time.frameCount > _inputLockedUntilFrame);
    }

    private static bool PointerOverAnyButton(List<VoiceUiKit.ActionButton> buttons)
    {
        for (int i = 0; i < buttons.Count; i++)
            if (buttons[i].IsPointerOver) return true;
        return false;
    }


    private static void DisposeAudioPreview()
    {
        if (_audioPreview == null) return;
        try { _audioPreview.Dispose(); } catch { }
        _audioPreview = null;
    }

    private static void CaptureAudioCheckResults()
    {
        if (_audioPreview == null || _draft == null) return;
        if (_audioPreview.MicrophoneSignalDetected)
        {
            _microphoneSignalDetected = true;
            _verifiedMicrophoneDevice = _draft.MicrophoneDevice;
        }
        if (_audioPreview.OutputTestCompleted)
        {
            _outputTestCompleted = true;
            _verifiedSpeakerDevice = _draft.SpeakerDevice;
        }
    }

    private static TextMeshProUGUI? BuildPageHeading(string title, string subtitle)
    {
        if (_pageRoot == null || _shell == null) return null;
        var heading = AddText(_pageRoot, title, 31f, VoiceUiKit.TextBright,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(22f, 0f), new Vector2(ContentWidth - 44f, 46f));
        heading.characterSpacing = 0f;
        return AddText(_pageRoot, subtitle, 16f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(22f, -52f), new Vector2(ContentWidth - 44f, 28f));
    }

    private static void AddPanelBorder(RectTransform panel, Color32 color)
    {
        var outline = panel.gameObject.AddComponent<Outline>();
        outline.effectColor = color;
        outline.effectDistance = new Vector2(1f, -1f);
        outline.useGraphicAlpha = true;
    }

    private static RectTransform BuildInlineNotice(
        RectTransform parent, float x, float y, float width, float height,
        string text, Color32 accent)
    {
        var notice = VoiceUiKit.Panel("InlineNotice", parent,
            new Color32(accent.r, accent.g, accent.b, 18), rounded: true, soft: true);
        PlaceTopLeft(notice.rectTransform, x, y, width, height);
        var bar = VoiceUiKit.Panel("NoticeBar", notice.rectTransform, accent, rounded: true, soft: true);
        bar.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
        bar.rectTransform.sizeDelta = new Vector2(4f, -18f);
        bar.rectTransform.anchoredPosition = new Vector2(8f, 0f);
        AddText(notice.rectTransform, text, 17f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(22f, -8f), new Vector2(width - 36f, height - 16f), wrap: true);
        return notice.rectTransform;
    }

#if ANDROID
    private static void BuildTouchGuide(float x, float width, Sprite sprite, string title, string description)
    {
        if (_pageRoot == null) return;
        var guide = VoiceUiKit.Rect("TouchGuide", _pageRoot);
        PlaceTopLeft(guide, x, -348f, width, 172f);
        var icon = VoiceUiKit.Rect("ControlIcon", guide).gameObject.AddComponent<Image>();
        icon.sprite = sprite;
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        PlaceTopLeft(icon.rectTransform, 22f, 0f, 28f, 28f);
        AddText(guide, title, 18f, VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(60f, 0f), new Vector2(width - 82f, 28f));
        AddText(guide, description, 15f, SetupTextSecondary, TextAlignmentOptions.TopLeft, FontStyles.Normal,
            new Vector2(22f, -42f), new Vector2(width - 44f, 126f), wrap: true);
    }
#endif

    private static RectTransform BuildCheckBadge(
        RectTransform parent, Vector2 topLeft, float size, Color32 color)
    {
        var badge = VoiceUiKit.Panel("CheckBadge", parent, color, rounded: true, soft: true);
        PlaceTopLeft(badge.rectTransform, topLeft.x, topLeft.y, size, size);
        Color32 checkColor = VoiceUiKit.PanelOuter;
        var shortBar = VoiceUiKit.Panel("CheckShort", badge.rectTransform, checkColor, true, true);
        shortBar.rectTransform.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        shortBar.rectTransform.sizeDelta = new Vector2(size * 0.26f, Mathf.Max(3f, size * 0.07f));
        shortBar.rectTransform.anchoredPosition = new Vector2(-size * 0.11f, size * -0.02f);
        shortBar.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -43f);
        var longBar = VoiceUiKit.Panel("CheckLong", badge.rectTransform, checkColor, true, true);
        longBar.rectTransform.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        longBar.rectTransform.sizeDelta = new Vector2(size * 0.46f, Mathf.Max(3f, size * 0.07f));
        longBar.rectTransform.anchoredPosition = new Vector2(size * 0.09f, size * -0.05f);
        longBar.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 46f);
        return badge.rectTransform;
    }

    private static VoiceUiKit.Row AddBindingRow(
        RectTransform parent,
        float width,
        float y,
        string label,
        Func<FirstRunSetupBinding> get,
        Action<FirstRunSetupBinding> set,
        string? help)
    {
        return AddRow(new VoiceUiKit.RebindRow(
                () => get().Key,
                (key, modifier, match) => set(new FirstRunSetupBinding(key, modifier, match)),
                () => set(new FirstRunSetupBinding(KeyCode.None, KeyCode.None, VoiceModifierMatch.Exact)),
                () => get().Modifier,
                () => get().ModifierMatch)
            .Build(parent, label, width, y, 54f, help));
    }

    private static void BuildSummaryRow(
        float y, VoiceSettingsCategory category, string title, IReadOnlyList<string> lines, Action edit)
    {
        if (_pageRoot == null) return;
        var row = VoiceUiKit.Rect("Review" + title, _pageRoot);
        PlaceTopLeft(row, 0f, y, ContentWidth, 108f);
        var icon = VoiceUiKit.Rect("ReviewIcon", row).gameObject.AddComponent<Image>();
        icon.sprite = VoiceUiBranding.CategoryIcon(category);
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        PlaceTopLeft(icon.rectTransform, 22f, -6f, 34f, 34f);
        AddText(row, title, 20f, VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(80f, 0f), new Vector2(ContentWidth - 210f, 32f));
        AddButton(row, "Edit", 76f, 44f, ContentWidth - 98f, 0f, VoiceUiKit.ButtonKind.Ghost, edit);
        float width = (ContentWidth - 118f) * 0.5f;
        for (int i = 0; i < lines.Count && i < 4; i++)
        {
            var line = AddText(row, lines[i], 16f, SetupTextSecondary, TextAlignmentOptions.Left,
                FontStyles.Normal, new Vector2(80f + i / 2 * (width + 16f), -38f - i % 2 * 28f),
                new Vector2(width, 26f));
            line.enableAutoSizing = true;
            line.fontSizeMax = 16f;
            line.fontSizeMin = 13f;
        }
        var divider = VoiceUiKit.Panel("ReviewDivider", row, VoiceUiKit.Divider, rounded: false);
        PlaceTopLeft(divider.rectTransform, 22f, -108f, ContentWidth - 44f, 1f);
    }


    private static VoiceUiKit.Row AddRow(VoiceUiKit.Row row)
    {
        Rows.Add(row);
        return row;
    }

    private static VoiceUiKit.ActionButton AddButton(
        RectTransform parent,
        string text,
        float width,
        float height,
        float x,
        float y,
        VoiceUiKit.ButtonKind kind,
        Action click,
        Func<bool>? enabled = null)
    {
        var button = new VoiceUiKit.ActionButton(parent, text, width, height, x, y, kind, click, enabled);
        Buttons.Add(button);
        return button;
    }

    private static TextMeshProUGUI AddText(
        Transform parent,
        string text,
        float size,
        Color32 color,
        TextAlignmentOptions alignment,
        FontStyles style,
        Vector2 position,
        Vector2 dimensions,
        bool wrap = false)
    {
        var tmp = VoiceUiKit.Text("SetupText", parent, text, size, color, alignment, style);
        PlaceTopLeft(tmp.rectTransform, position.x, position.y, dimensions.x, dimensions.y);
        tmp.enableWordWrapping = wrap;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        return tmp;
    }

    private static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
    {
        rt.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, y);
    }

    private static string DeviceName(IReadOnlyList<string> names, int index)
        => names.Count == 0
            ? "No devices found"
            : names[Mathf.Clamp(index, 0, names.Count - 1)];

    private static int DraftMicrophoneIndexForUi()
    {
        if (_draft == null) return 0;
        int resolved = _draft.MicrophoneIndex();
        return resolved >= 0 ? resolved : VoiceChatLocalSettings.MicDeviceNames.Length;
    }

    private static int DraftMicrophoneCountForUi()
    {
        int count = VoiceChatLocalSettings.MicDeviceNames.Length;
        return _draft != null && !string.IsNullOrEmpty(_draft.MicrophoneDevice) &&
               _draft.MicrophoneIndex() < 0 ? count + 1 : count;
    }

    private static string DraftMicrophoneNameForUi(int index)
        => index < VoiceChatLocalSettings.MicDeviceNames.Length
            ? DeviceName(VoiceChatLocalSettings.MicDeviceNames, index)
            : (_draft?.MicrophoneDisplayName() ?? "Saved device") + " (saved device)";

#if WINDOWS
    private static int DraftSpeakerIndexForUi()
    {
        if (_draft == null) return 0;
        int resolved = _draft.SpeakerIndex();
        return resolved >= 0 ? resolved : VoiceChatLocalSettings.SpkDeviceNames.Length;
    }

    private static int DraftSpeakerCountForUi()
    {
        int count = VoiceChatLocalSettings.SpkDeviceNames.Length;
        return _draft != null && !string.IsNullOrEmpty(_draft.SpeakerDevice) &&
               _draft.SpeakerIndex() < 0 ? count + 1 : count;
    }

    private static string DraftSpeakerNameForUi(int index)
        => index < VoiceChatLocalSettings.SpkDeviceNames.Length
            ? DeviceName(VoiceChatLocalSettings.SpkDeviceNames, index)
            : (_draft?.SpeakerDisplayName() ?? "Saved device") + " (saved device)";
#endif

    private static string CompactDevice(string displayName)
    {
        string value = string.IsNullOrWhiteSpace(displayName) ? "System Default" : displayName;
        return value.Length <= 30 ? value : value.Substring(0, 27) + "...";
    }

    private static bool OutputVerifiedForCurrentDraft
        => _draft != null && _outputTestCompleted &&
           string.Equals(_verifiedSpeakerDevice, _draft.SpeakerDevice,
               StringComparison.OrdinalIgnoreCase);

    private static bool MicrophoneVerifiedForCurrentDraft
        => _draft != null && _microphoneSignalDetected &&
           string.Equals(_verifiedMicrophoneDevice, _draft.MicrophoneDevice,
               StringComparison.OrdinalIgnoreCase);

    private static string AudioVerificationSummary()
    {
        if (MicrophoneVerifiedForCurrentDraft && OutputVerifiedForCurrentDraft)
            return "Mic and output tested";
        if (MicrophoneVerifiedForCurrentDraft) return "Mic tested / output optional";
        if (OutputVerifiedForCurrentDraft) return "Output tested / mic optional";
        return "Audio tests optional";
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";
    private static string ShownHidden(bool hidden) => hidden ? "hidden" : "shown";

    private static string FriendlyPosition(SpeakingBarPosition position) => position switch
    {
        SpeakingBarPosition.TopLeft => "Top left",
        SpeakingBarPosition.TopMiddle => "Top center",
        SpeakingBarPosition.TopRight => "Top right",
        SpeakingBarPosition.MiddleLeft => "Middle left",
        SpeakingBarPosition.MiddleRight => "Middle right",
        SpeakingBarPosition.BottomLeft => "Bottom left",
        SpeakingBarPosition.BottomMiddle => "Bottom center",
        SpeakingBarPosition.BottomRight => "Bottom right",
        _ => position.ToString(),
    };

    private static string HudPlacementSummary(SpeakingBarPreviewSettings settings)
        => settings.ManualLayout
            ? $"Manual {settings.ManualOrientation.ToString().ToLowerInvariant()} / " +
              $"X {Mathf.RoundToInt(settings.ManualX * 100f)}% / Y {Mathf.RoundToInt(settings.ManualY * 100f)}%"
            : FriendlyPosition(settings.Position);

    private static string SelectedHudDescription()
    {
        if (_draft == null) return "Hover a layout to preview it live. Select one to use it.";
        int index = _draft.SelectedHudPreset;
        return index >= 0 && index < FirstRunHudPresets.All.Count
            ? FirstRunHudPresets.All[index].Description
            : "Choose a HUD layout to continue.";
    }

    private static string? SetupValidationIssue()
    {
        if (_draft == null) return "Setup is still loading.";
        if (_draft.MicrophoneSelectionChanged &&
            !string.IsNullOrEmpty(_draft.MicrophoneDevice) && _draft.MicrophoneIndex() < 0)
            return "The selected microphone is no longer available. Return to Audio and choose another device or Default.";
#if WINDOWS
        if (_draft.SpeakerSelectionChanged &&
            !string.IsNullOrEmpty(_draft.SpeakerDevice) && _draft.SpeakerIndex() < 0)
            return "The selected output device is no longer available. Return to Audio and choose another device or Default.";
#endif
#if WINDOWS
        if (_draft.MicMode == VoiceMicMode.PushToTalk && _draft.PushToTalk.Key == KeyCode.None)
            return "Choose a Push to Talk shortcut before saving, or switch to Open Mic.";

        string? bindingIssue = BindingValidationIssue(_draft);
        if (bindingIssue != null) return bindingIssue;
#endif
        return null;
    }

    internal static string? BindingValidationIssue(FirstRunSetupDraft draft)
    {
        var bindings = new (string Name, FirstRunSetupBinding Binding)[]
        {
            ("Mute microphone", draft.ToggleMute),
            ("Push to Mute", draft.PushToMute),
            ("Push to Talk", draft.PushToTalk),
            ("Mute playback", draft.ToggleSpeaker),
            ("Open voice menu", draft.OpenVoiceSettings),
        };
        for (int i = 0; i < bindings.Length; i++)
            for (int j = i + 1; j < bindings.Length; j++)
            {
                if (!BindingsConflict(bindings[i].Binding, bindings[j].Binding)) continue;
                return $"{bindings[i].Name} and {bindings[j].Name} use the same shortcut. Edit Controls before saving.";
            }
        return null;
    }

    private static bool BindingsConflict(FirstRunSetupBinding a, FirstRunSetupBinding b)
        => a.Key != KeyCode.None && a.Key == b.Key && a.Modifier == b.Modifier;

    private static float Smooth(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }



    private sealed class HudPresetCard
    {
        private readonly int _index;
        private readonly string _name;
        private readonly string _description;
        private readonly SpeakingBarPreviewSettings _previewSettings;
        private RectTransform _root = null!;
        private RectTransform _visual = null!;
        private Image _surface = null!;
        private Image _border = null!;
        private TextMeshProUGUI _title = null!;
        private RectTransform _check = null!;
        private float _hover;
        private float _selected;

        internal HudPresetCard(
            int index,
            string name,
            string description,
            SpeakingBarPreviewSettings previewSettings)
        {
            _index = index;
            _name = name;
            _description = description;
            _previewSettings = previewSettings;
        }

        internal void Build(
            RectTransform parent, float x, float y, float width, float height)
        {
            _root = VoiceUiKit.Rect("HudPresetHit", parent);
            PlaceTopLeft(_root, x, y, width, height);
            _border = _root.gameObject.AddComponent<Image>();
            _border.sprite = VoiceUiKit.Rounded();
            _border.type = Image.Type.Sliced;
            _border.color = SetupBorder;
            _border.raycastTarget = false;

            _visual = VoiceUiKit.Rect("HudPresetVisual", _root);
            _visual.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _visual.offsetMin = new Vector2(1.5f, 1.5f);
            _visual.offsetMax = new Vector2(-1.5f, -1.5f);
            _surface = _visual.gameObject.AddComponent<Image>();
            _surface.sprite = VoiceUiKit.Rounded();
            _surface.type = Image.Type.Sliced;
            _surface.color = SetupSurfaceRaised;
            _surface.raycastTarget = false;

            _title = AddText(_visual, _name, 18f, VoiceUiKit.TextPrimary,
                TextAlignmentOptions.Center, FontStyles.Bold,
                new Vector2(10f, -(height - 24f) * 0.5f),
                new Vector2(width - 48f, 24f));
            _title.enableAutoSizing = true;
            _title.fontSizeMax = 18f;
            _title.fontSizeMin = 15f;
            _check = BuildCheckBadge(_visual,
                new Vector2(width - 26f, -(height - 18f) * 0.5f), 18f, VoiceUiKit.Accent);
            _check.gameObject.SetActive(_draft != null && _draft.SelectedHudPreset == _index);
        }

        internal void Tick(float dt)
        {
            if (_draft == null || _root == null || _draft.HideSpeakingBar) return;
            bool over = VoiceUiKit.Contains(_root);
            bool selected = _draft.SelectedHudPreset == _index;
            if (over)
            {
                _hoveredHudSettings = _previewSettings;
                _hoveredHudDescription = _description;
                VoiceUiKit.RequestTooltip(_name, _description, plainTextDescription: true);
            }
            if (over && Time.frameCount > _inputLockedUntilFrame && Input.GetMouseButtonDown(0))
            {
                _draft.SelectHudPreset(_index);
                selected = true;
                VoiceUiKit.SwallowClick();
            }

            _hover = Mathf.MoveTowards(_hover, over ? 1f : 0f, dt * 10f);
            _selected = Mathf.MoveTowards(_selected, selected ? 1f : 0f, dt * 8f);
            _check.gameObject.SetActive(selected);
            _visual.anchoredPosition = new Vector2(0f, _hover * 1.5f);
            _surface.color = VoiceUiKit.Lerp(
                SetupSurfaceRaised,
                VoiceUiKit.Lerp(SetupSurfaceHover,
                    VoiceUiKit.Lerp(SetupSurfaceRaised, VoiceUiKit.AccentMiddle, 0.18f), _selected),
                Mathf.Max(_hover, _selected));
            _border.color = VoiceUiKit.Lerp(
                SetupBorder,
                VoiceUiKit.Lerp(VoiceUiKit.ControlHover, VoiceUiKit.AccentSecondary, _selected),
                Mathf.Max(_hover, _selected));
            _title.color = VoiceUiKit.Lerp(VoiceUiKit.TextPrimary, VoiceUiKit.TextBright,
                Mathf.Max(_hover, _selected));
        }

    }

}
