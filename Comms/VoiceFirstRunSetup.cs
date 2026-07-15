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
    private const int WelcomePage = 0;
    private const int AudioPage = 1;
    private const int ControlsPage = 2;
    private const int HudPage = 3;
    private const int ReadyPage = 4;
    private const int PageCount = 5;

    private const float PanelWidth = 1040f;
    private const float PanelHeight = 680f;
    private const float FooterY = -496f;
    private const float ContentTopY = -130f;
    private const float ShellClipInset = 16f;
    private const float ContentSideInset = 12f;

    private static readonly Color32 SetupSurface = new(18, 24, 33, 255);
    private static readonly Color32 SetupSurfaceRaised = new(23, 31, 42, 255);
    private static readonly Color32 SetupSurfaceHover = new(29, 39, 52, 255);
    private static readonly Color32 SetupBorder = new(69, 86, 108, 105);
    private static readonly Color32 SetupTextSecondary = new(194, 204, 218, 255);
    private static readonly Color32 SetupTextTertiary = new(156, 173, 196, 255);
    private static readonly Color32 SetupSuccess = new(77, 225, 141, 255);
    private static readonly Color32 SetupWarning = new(245, 184, 68, 255);

    private static readonly string[] StepNames =
        { "Welcome", "Audio", "Controls", "HUD", "Review" };

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
    private static readonly List<SetupButton> Buttons = new();
    private static readonly List<SetupButton> PromptButtons = new();
    private static readonly List<HudPresetCard> HudCards = new();
    private static readonly List<CelebrationDot> CelebrationDots = new();

    private static TextMeshProUGUI? _outputStatus;
    private static TextMeshProUGUI? _micModeHelpText;
    private static TextMeshProUGUI? _hudDescriptionText;
    private static SetupButton? _micTestButton;
    private static SetupButton? _speakerTestButton;
    private static TextMeshProUGUI? _finishError;
    private static TextMeshProUGUI? _promptMessage;
    private static VoiceUiKit.Row? _pushToTalkRow;
    private static CanvasGroup? _pushToTalkGroup;

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
        if (VoiceSettingsPanel.IsOpen || HostSettingsPanel.IsOpen || VoiceVolumeMenu.IsOpen)
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
        if (Show(settings, manual: true))
            _automaticAttemptedThisSession = true;
    }

    private static bool Show(VoiceChatLocalSettings settings, bool manual)
    {
        try
        {
            if (IsOpen) CloseInternal(destroy: true);
            VoiceUiKit.EnsureCanvas();
            VoiceUiKit.EnsureDriver();
            VoiceChatLocalSettings.MaybeRefreshDeviceLists(resolveSavedIndices: false);

            _draft = FirstRunSetupDraft.CreateFresh(settings);
            _completed = false;
            _page = WelcomePage;
            _appear = 0f;
            _pageAppear = 0f;
            _hudReveal = 0f;
            _promptAppear = 0f;
            _microphoneSignalDetected = false;
            _outputTestCompleted = false;
            _verifiedMicrophoneDevice = string.Empty;
            _verifiedSpeakerDevice = string.Empty;
            _shown = true;
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
            !_completed && _page == HudPage ? 1f : 0f,
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
        else if (_completed) TickCelebration(dt);

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
        CelebrationDots.Clear();
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

        if (_chromeRoot != null) Object.Destroy(_chromeRoot.gameObject);
        _chromeRoot = VoiceUiKit.Rect("SetupChrome", _shell.PaneRoot);
        _chromeRoot.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        _chromeRoot.sizeDelta = new Vector2(-ContentSideInset * 2f, 0f);
        _chromeRoot.anchoredPosition = Vector2.zero;

        _pageRoot = VoiceUiKit.Rect("SetupPage", _chromeRoot);
        _pageRoot.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        _pageRoot.sizeDelta = Vector2.zero;
        _pageRoot.anchoredPosition = new Vector2(14f, 0f);
        _pageGroup = _pageRoot.gameObject.AddComponent<CanvasGroup>();
        _pageGroup.alpha = 0f;
        _pageAppear = 0f;
        _inputLockedUntilFrame = Time.frameCount + 1;

        if (_completed)
        {
            BuildCompletedPage();
            return;
        }

        BuildStepProgress();
        switch (_page)
        {
            case WelcomePage: BuildWelcomePage(); break;
            case AudioPage: BuildAudioPage(); break;
            case ControlsPage: BuildControlsPage(); break;
            case HudPage: BuildHudPage(); break;
            default: BuildReadyPage(); break;
        }
        BuildFooter();
    }

    private static void BuildStepProgress()
    {
        if (_chromeRoot == null || _shell == null) return;
        AddText(_chromeRoot, $"Step {_page + 1} of {PageCount}", 17f, VoiceUiKit.Accent,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(2f, -5f), new Vector2(150f, 24f));
        AddText(_chromeRoot, StepNames[_page], 17f, SetupTextSecondary,
            TextAlignmentOptions.Right, FontStyles.Bold,
            new Vector2(ContentWidth - 180f, -5f), new Vector2(178f, 24f));

        const float gap = 8f;
        float segment = (ContentWidth - gap * (PageCount - 1)) / PageCount;
        for (int i = 0; i < PageCount; i++)
        {
            bool done = i < _page;
            bool current = i == _page;
            Color32 color = current
                ? VoiceUiKit.Accent
                : done ? new Color32(34, 211, 238, 135) : new Color32(54, 66, 84, 210);
            var segmentImage = VoiceUiKit.Panel("StepSegment", _chromeRoot, color,
                rounded: true, soft: true);
            PlaceTopLeft(segmentImage.rectTransform, i * (segment + gap), -35f, segment, 5f);
        }
    }

    private static void BuildWelcomePage()
    {
        if (_pageRoot == null || _shell == null) return;
        const float leftW = 568f;
        const float gap = 26f;
        float rightW = ContentWidth - leftW - gap;

        AddText(_pageRoot, "Welcome", 17f, VoiceUiKit.Accent,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(4f, -78f), new Vector2(leftW, 26f));
        AddText(_pageRoot, "Set up Perfect Comms.", 40f,
            VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(4f, -111f), new Vector2(leftW, 58f));
        AddText(_pageRoot,
            "Choose your audio devices, controls, and HUD. Everyone starts from the same clean recommended setup.",
            20f, SetupTextSecondary, TextAlignmentOptions.TopLeft, FontStyles.Normal,
            new Vector2(4f, -181f), new Vector2(leftW - 18f, 92f), wrap: true);

        var recommended = BuildCard(_pageRoot, "RecommendedStart", 4f, -306f, leftW - 22f, 92f);
        BuildCheckBadge(recommended, new Vector2(24f, -24f), 38f, SetupSuccess);
        AddText(recommended, "Recommended defaults are ready", 21f, VoiceUiKit.TextBright,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(76f, -18f), new Vector2(leftW - 122f, 28f));
        AddText(recommended, "Adjust what matters to you, then save everything together.", 17f,
            SetupTextSecondary, TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(76f, -49f), new Vector2(leftW - 122f, 26f));

        var time = VoiceUiKit.Panel("SetupTime", _pageRoot,
            new Color32(34, 211, 238, 20), rounded: true, soft: true);
        PlaceTopLeft(time.rectTransform, 4f, -421f, 286f, 42f);
        AddText(time.rectTransform, "About 2 minutes", 17f, VoiceUiKit.Accent,
            TextAlignmentOptions.Center, FontStyles.Bold,
            Vector2.zero, new Vector2(286f, 42f));

        var journey = BuildCard(_pageRoot, "SetupJourney", leftW + gap, -82f, rightW, 368f);
        AddCardTitle(journey, "What you'll set up", "Three focused steps, then one review.");
        BuildJourneyRow(journey, -76f, "A", "Audio", "Choose your input, output, and playback level.");
        BuildJourneyRow(journey, -167f, "C", "Controls", "Talk mode, startup behavior, and shortcuts.");
        BuildJourneyRow(journey, -258f, "H", "HUD", "Choose a layout using the live lobby preview.");
    }

    private static void BuildAudioPage()
    {
        if (_pageRoot == null || _shell == null || _draft == null) return;
        BuildPageHeading("Choose your audio", "Select where your voice comes from and where you hear other players.");
        VoiceChatLocalSettings.MaybeRefreshDeviceLists(resolveSavedIndices: false);
        _micListVersion = VoiceChatLocalSettings.MicDeviceListVersion;
        _speakerListVersion = VoiceChatLocalSettings.SpkDeviceListVersion;
        _audioPreview ??= new FirstRunAudioPreview();

        const float cardW = 780f;
        float cardX = (ContentWidth - cardW) * 0.5f;
        const float cardH = 338f;
        var devices = BuildCard(_pageRoot, "DevicesCard", cardX, ContentTopY, cardW, cardH);

        AddCardTitle(devices, "Input & output", "Use system defaults, or choose a specific device.", 344f);
        _micTestButton = AddButton(devices, "Test mic", 146f, 44f,
            cardW - 324f, -11f, ButtonKind.Secondary,
            () =>
            {
                if (_audioPreview == null || _draft == null) return;
                if (_audioPreview.IsMicrophoneTestActive) _audioPreview.StopMicrophone();
                else _audioPreview.StartMicrophone(_draft);
            },
            () => _audioPreview?.IsSpeakerTestBusy != true);
        _micLevelMeter = new VoiceUiKit.LiveLevelMeter(
            devices, "MicLiveLevel", 146f, 6f, hideWhenInactive: true);
        PlaceTopLeft(_micLevelMeter.Root, cardW - 324f, -58f, 146f, 6f);
        _speakerTestButton = AddButton(devices, "Test output", 146f, 44f,
            cardW - 166f, -11f, ButtonKind.Secondary,
            () => _audioPreview?.PlayTestSound(_draft),
            () => _audioPreview?.IsSpeakerTestBusy != true);
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
                DraftMicrophoneCountForUi,
                DraftMicrophoneNameForUi,
                compactFullWidth: true)
            .Build(devices, "Input device", cardW, -66f, 86f,
                "The microphone Perfect Comms captures."));

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
                DraftSpeakerCountForUi,
                DraftSpeakerNameForUi,
                compactFullWidth: true)
            .Build(devices, "Output device", cardW, -154f, 86f,
                "The headphones or speakers used for voice playback."));
#else
        var route = BuildInlineNotice(devices, 22f, -158f, cardW - 44f, 68f,
            "Output follows your current Android audio route.", VoiceUiKit.Accent);
        route.gameObject.name = "AndroidOutputRoute";
#endif
        AddRow(new VoiceUiKit.SliderRow(
                () => _draft.MasterVolume,
                v => _draft.MasterVolume = v,
                0.1f, 2f, v => $"{Mathf.RoundToInt(v * 100f)}%",
                stacked: true)
            .Build(devices, "Voice playback volume", cardW, -244f, 58f,
                "Overall volume for other players."));

        _outputStatus = AddText(devices, "Mic and output tests are optional", 17f,
            SetupTextSecondary, TextAlignmentOptions.Center, FontStyles.Normal,
            new Vector2(22f, -304f), new Vector2(cardW - 44f, 26f), wrap: true);
    }

    private static void BuildControlsPage()
    {
        if (_pageRoot == null || _shell == null || _draft == null) return;
        BuildPageHeading("Choose how you talk", "Set your talk mode and the controls you will use in a lobby.");

        const float gap = 18f;
        float leftW = 388f;
        float rightW = ContentWidth - leftW - gap;
        const float cardH = 338f;
        var talk = BuildCard(_pageRoot, "TalkCard", 0f, ContentTopY, leftW, cardH);
        var binds = BuildCard(_pageRoot, "BindingsCard", leftW + gap, ContentTopY, rightW, cardH);

        AddCardTitle(talk, "Talk behavior", "These can be changed later in Settings.");
        AddRow(new VoiceUiKit.StepperRow(
                () => (int)_draft.MicMode,
                i => _draft.MicMode = (VoiceMicMode)Mathf.Clamp(i, 0, 1),
                () => 2,
                i => i == 0 ? "Open Mic" : "Push To Talk",
                compactFullWidth: true)
            .Build(talk, "Microphone mode", leftW, -63f, 84f,
                "Open Mic uses voice activation. Push To Talk only sends while its key is held."));
        AddRow(new VoiceUiKit.ToggleRow(
                () => _draft.StartMuted,
                v => _draft.StartMuted = v)
            .Build(talk, "Start with mic muted", leftW, -153f, 54f,
                "Join each voice room with your microphone muted."));
        AddRow(new VoiceUiKit.ToggleRow(
                () => _draft.StartDeafened,
                v => _draft.StartDeafened = v)
            .Build(talk, "Start with playback muted", leftW, -207f, 54f,
                "Join each voice room without hearing incoming voice."));

        var modeNotice = BuildInlineNotice(talk, 20f, -270f, leftW - 40f, 59f,
            "", VoiceUiKit.Accent);
        _micModeHelpText = modeNotice.GetComponentInChildren<TextMeshProUGUI>();
        if (_micModeHelpText != null)
        {
            _micModeHelpText.enableWordWrapping = true;
            _micModeHelpText.overflowMode = TextOverflowModes.Overflow;
        }

#if WINDOWS
        AddCardTitle(binds, "Keyboard shortcuts", "Select a shortcut, then press a key or chord.");
        AddBindingRow(binds, rightW, -63f, "Mute / unmute microphone",
            () => _draft.ToggleMute, v => _draft.ToggleMute = v,
            null);
        _pushToTalkRow = AddBindingRow(binds, rightW, -128f, "Push to Talk (hold)",
            () => _draft.PushToTalk, v => _draft.PushToTalk = v,
            "Required only when Push to Talk mode is selected.");
        _pushToTalkGroup = _pushToTalkRow.Root.gameObject.AddComponent<CanvasGroup>();
        AddBindingRow(binds, rightW, -193f, "Mute / unmute playback",
            () => _draft.ToggleSpeaker, v => _draft.ToggleSpeaker = v,
            null);
        AddBindingRow(binds, rightW, -258f, "Open voice menu",
            () => _draft.OpenVoiceSettings, v => _draft.OpenVoiceSettings = v,
            null);
#else
        AddCardTitle(binds, "In-game controls", "Touch controls stay visible while you play.");
        BuildJourneyRow(binds, -74f, "M", "Microphone", "Tap the mic control to mute or unmute yourself.");
        BuildJourneyRow(binds, -160f, "S", "Voice playback", "Tap the speaker control to mute incoming voice.");
        BuildJourneyRow(binds, -246f, "V", "Voice menu", "Open the voice panel for volume and player controls.");
#endif
    }

    private static void BuildHudPage()
    {
        if (_pageRoot == null || _shell == null || _draft == null) return;
        _hudDescriptionText = BuildPageHeading(
            "Choose your HUD",
            "Hover a layout to preview it live. Select one to use it.");
        EnsureHudPreview();

        const float previewW = 424f;
        const float previewGap = 18f;
        float pickerW = ContentWidth - previewW - previewGap;

        AddText(_pageRoot, "MOST COMMON", 16f, VoiceUiKit.Accent,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(1f, ContentTopY), new Vector2(pickerW - 2f, 20f));

        const float commonGap = 8f;
        const float commonCardH = 78f;
        const float commonY = ContentTopY - 24f;
        float commonCardW = (pickerW - commonGap * 2f) / 3f;
        for (int i = 0; i < FirstRunHudPresets.CommonCount; i++)
        {
            var preset = FirstRunHudPresets.All[i];
            var previewSettings = FirstRunHudPresets.Apply(_draft.Hud, preset);
            var card = new HudPresetCard(i, preset.Name, preset.Description,
                previewSettings, original: false);
            card.Build(_pageRoot,
                i * (commonCardW + commonGap), commonY,
                commonCardW, commonCardH, prominent: true);
            HudCards.Add(card);
        }

        const float moreLabelY = commonY - commonCardH - 8f;
        AddText(_pageRoot, "MORE LAYOUTS", 16f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(1f, moreLabelY), new Vector2(pickerW - 2f, 20f));

        const float moreGapX = 10f;
        const float moreGapY = 6f;
        const float moreCardH = 45f;
        const float moreY = moreLabelY - 26f;
        float moreCardW = (pickerW - moreGapX) * 0.5f;
        for (int i = FirstRunHudPresets.CommonCount; i < FirstRunHudPresets.All.Count; i++)
        {
            int visualIndex = i - FirstRunHudPresets.CommonCount;
            int col = visualIndex % 2;
            int row = visualIndex / 2;
            var preset = FirstRunHudPresets.All[i];
            var previewSettings = FirstRunHudPresets.Apply(_draft.Hud, preset);
            var card = new HudPresetCard(i, preset.Name, preset.Description,
                previewSettings, original: false);
            card.Build(_pageRoot,
                col * (moreCardW + moreGapX),
                moreY - row * (moreCardH + moreGapY),
                moreCardW, moreCardH, prominent: false);
            HudCards.Add(card);
        }

        if (_hudPreviewHost != null) _hudPreviewHost.SetAsLastSibling();
    }

    private static void BuildReadyPage()
    {
        if (_pageRoot == null || _shell == null || _draft == null) return;
        BuildPageHeading("Review and save", "Check the essentials, then apply everything together.");

        const float gap = 14f;
        float w = (ContentWidth - gap * 2f) / 3f;
        BuildSummaryCard(0f, w, "Audio", new[]
        {
            "Input: " + CompactDevice(_draft.MicrophoneDevice),
#if WINDOWS
            "Output: " + CompactDevice(_draft.SpeakerDevice),
#else
            "Output: Android audio route",
#endif
            $"Playback volume: {Mathf.RoundToInt(_draft.MasterVolume * 100f)}%",
            AudioVerificationSummary(),
        }, () => GoTo(AudioPage));

        string talkMode = _draft.MicMode == VoiceMicMode.OpenMic ? "Open Mic" : "Push to Talk";
        BuildSummaryCard(w + gap, w, "Controls", new[]
        {
            "Mode: " + talkMode,
            _draft.MicMode == VoiceMicMode.PushToTalk
                ? "PTT: " + _draft.PushToTalk.Label
                : "Mute mic: " + _draft.ToggleMute.Label,
            "Voice menu: " + _draft.OpenVoiceSettings.Label,
            $"Start muted: {YesNo(_draft.StartMuted)} / Playback muted: {YesNo(_draft.StartDeafened)}",
        }, () => GoTo(ControlsPage));

        string hudName = _draft.SelectedHudPreset >= 0
            ? FirstRunHudPresets.All[_draft.SelectedHudPreset].Name
            : "Custom layout";
        BuildSummaryCard((w + gap) * 2f, w, "HUD", new[]
        {
            hudName,
            HudPlacementSummary(_draft.Hud),
            $"Scale {Mathf.RoundToInt(_draft.Hud.Scale * 100f)}%",
            _draft.Hud.Backdrop ? "Backdrop on" : "Backdrop off",
        }, () => GoTo(HudPage));

        string? issue = SetupValidationIssue();
        Color32 noticeColor = issue == null ? SetupSuccess : SetupWarning;
        var notice = BuildInlineNotice(_pageRoot, 84f, -390f, ContentWidth - 168f, 82f,
            issue ?? "Everything is ready. Save & finish will apply this setup in one clean save.",
            noticeColor);
        _finishError = notice.GetComponentInChildren<TextMeshProUGUI>();
    }

    private static void BuildCompletedPage()
    {
        if (_pageRoot == null || _shell == null) return;
        float center = ContentWidth * 0.5f;
        for (int i = 0; i < 10; i++)
        {
            float angle = i * (Mathf.PI * 2f / 10f);
            float radius = 108f + (i % 2) * 34f;
            var dot = new CelebrationDot();
            dot.Build(_pageRoot, new Vector2(
                center + Mathf.Cos(angle) * radius,
                -132f + Mathf.Sin(angle) * radius), i);
            CelebrationDots.Add(dot);
        }

        var glow = VoiceUiKit.GlowImage("SuccessGlow", _pageRoot, new Color32(77, 225, 141, 54));
        glow.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f));
        glow.rectTransform.sizeDelta = new Vector2(154f, 154f);
        glow.rectTransform.anchoredPosition = new Vector2(center, -132f);
        BuildCheckBadge(_pageRoot, new Vector2(center - 47f, -85f), 94f, SetupSuccess);

        var title = AddText(_pageRoot, "You're all set", 38f, VoiceUiKit.TextBright,
            TextAlignmentOptions.Center, FontStyles.Bold,
            new Vector2(0f, -205f), new Vector2(ContentWidth, 54f));
        AddText(_pageRoot,
            "Your Perfect Comms setup has been saved and is ready for the next lobby.",
            19f, SetupTextSecondary, TextAlignmentOptions.Center, FontStyles.Normal,
            new Vector2(110f, -258f), new Vector2(ContentWidth - 220f, 46f), wrap: true);

        var checklist = BuildCard(_pageRoot, "CompletionChecklist",
            center - 310f, -326f, 620f, 76f);
        BuildCheckBadge(checklist, new Vector2(20f, -19f), 34f, SetupSuccess);
        AddText(checklist, "Audio, controls, and HUD saved", 18f, VoiceUiKit.TextBright,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(70f, -14f), new Vector2(520f, 26f));
        AddText(checklist, "Run this setup again anytime from Perfect Comms Settings.", 17f,
            SetupTextSecondary, TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(70f, -41f), new Vector2(520f, 24f));

        AddButton(_pageRoot, "Advanced settings", 220f, 50f, center - 230f, -430f,
            ButtonKind.Secondary, () =>
            {
                CloseInternal(destroy: true);
                VoiceSettingsPanel.ShowDeferred();
            });
        AddButton(_pageRoot, "Done", 220f, 50f, center + 10f, -430f,
            ButtonKind.Primary, () => CloseInternal(destroy: true));
    }

    private static void BuildFooter()
    {
        if (_chromeRoot == null || _shell == null) return;
        var divider = VoiceUiKit.Panel("FooterDivider", _chromeRoot,
            new Color32(105, 124, 150, 32), rounded: false);
        PlaceTopLeft(divider.rectTransform, 0f, FooterY + 17f, ContentWidth, 1f);

        AddButton(_chromeRoot, "Use existing settings", 220f, 48f,
            0f, FooterY, ButtonKind.Secondary, UseExistingSettings);

        if (_page > WelcomePage)
            AddButton(_chromeRoot, "Back", 140f, 48f,
                ContentWidth - 350f, FooterY, ButtonKind.Secondary,
                () => GoTo(_page - 1));

        if (_page < ReadyPage)
        {
            string text = _page == WelcomePage ? "Start setup" : "Continue";
            AddButton(_chromeRoot, text, 190f, 48f,
                ContentWidth - 190f, FooterY, ButtonKind.Primary,
                () => GoTo(_page + 1));
        }
        else
        {
            AddButton(_chromeRoot, "Save & finish", 190f, 48f,
                ContentWidth - 190f, FooterY, ButtonKind.Primary, FinishSetup,
                () => SetupValidationIssue() == null);
        }
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
        page = Mathf.Clamp(page, WelcomePage, ReadyPage);
        if (page == _page) return;
        if (_page == AudioPage)
        {
            CaptureAudioCheckResults();
            DisposeAudioPreview();
        }
        if (_page == HudPage) _hudPreview?.Suspend();
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
        card.rectTransform.sizeDelta = new Vector2(620f, 300f);
        card.rectTransform.anchoredPosition = Vector2.zero;
        _closePromptCard = card.rectTransform;
        AddPanelBorder(card.rectTransform, SetupBorder);

        AddText(card.rectTransform, "Leave setup?", 31f,
            VoiceUiKit.TextBright, TextAlignmentOptions.Center, FontStyles.Bold,
            new Vector2(42f, -35f), new Vector2(536f, 44f));
        _promptMessage = AddText(card.rectTransform,
            "Continue with the recommended setup, or keep the settings already on this device.",
            20f, SetupTextSecondary, TextAlignmentOptions.Center, FontStyles.Normal,
            new Vector2(62f, -90f), new Vector2(496f, 62f), wrap: true);

        AddText(card.rectTransform,
            "Use existing settings completes this one-time setup without changing anything.",
            17f, SetupTextTertiary, TextAlignmentOptions.Center, FontStyles.Normal,
            new Vector2(58f, -159f), new Vector2(504f, 42f), wrap: true);

        PromptButtons.Clear();
        PromptButtons.Add(new SetupButton(card.rectTransform, "Continue setup", 230f, 50f,
            70f, -222f, ButtonKind.Primary, HideClosePrompt));
        PromptButtons.Add(new SetupButton(card.rectTransform, "Use existing settings", 230f, 50f,
            320f, -222f, ButtonKind.Secondary, UseExistingSettings));
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
        CelebrationDots.Clear();
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
        _micTestButton?.SetText(_audioPreview.IsMicrophoneTestActive ? "Stop mic test" : "Test mic");
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
            _micModeHelpText.text = pushToTalk
                ? "Voice is sent only while your\nPush to Talk shortcut is held."
                : "Voice activation sends speech\nautomatically when you talk.";
            _micModeHelpText.color = pushToTalk ? SetupWarning : VoiceUiKit.Accent;
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
        _hoveredHudSettings = null;
        _hoveredHudDescription = null;
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
            _hudPreviewHost.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            _hudPreviewHost.sizeDelta = new Vector2(-ContentSideInset * 2f, 0f);
            _hudPreviewHost.anchoredPosition = Vector2.zero;

            const float previewWidth = 424f;
            const float previewHeight = 342f;
            const float previewGap = 18f;
            float pickerWidth = ContentWidth - previewWidth - previewGap;
            var center = new Vector2(
                pickerWidth + previewGap + previewWidth * 0.5f - ContentWidth * 0.5f,
                ContentTopY - previewHeight * 0.5f);
            _hudPreview = new SpeakingBarLivePreview(_hudPreviewHost, center, embedded: true);
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
        float reveal = Smooth(_hudReveal);
        try
        {
            var settings = _hoveredHudSettings ?? _draft.Hud;
            if (!_hudPreview.IsWarmupReady)
                _hudPreview.Prewarm(settings, dt);
            _hudPreview.SetPresentation(reveal, render && reveal > 0.01f);
            if (render && reveal > 0.01f)
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
        maxScale = Mathf.Clamp(maxScale, 0.74f, 1f);
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

    private static void TickButtons(List<SetupButton> buttons, float dt)
    {
        for (int i = 0; i < buttons.Count; i++)
            buttons[i].Tick(dt, Time.frameCount > _inputLockedUntilFrame);
    }

    private static bool PointerOverAnyButton(List<SetupButton> buttons)
    {
        for (int i = 0; i < buttons.Count; i++)
            if (buttons[i].IsPointerOver) return true;
        return false;
    }

    private static void TickCelebration(float dt)
    {
        for (int i = 0; i < CelebrationDots.Count; i++) CelebrationDots[i].Tick(dt);
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
        var heading = AddText(_pageRoot, title, 33f, VoiceUiKit.TextBright,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(2f, -56f), new Vector2(ContentWidth - 4f, 38f));
        heading.characterSpacing = 0f;
        return AddText(_pageRoot, subtitle, 19f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(2f, -98f), new Vector2(ContentWidth - 4f, 24f));
    }

    private static RectTransform BuildCard(
        RectTransform parent, string name, float x, float y, float w, float h)
    {
        var card = VoiceUiKit.Panel(name, parent, SetupSurfaceRaised, rounded: true);
        PlaceTopLeft(card.rectTransform, x, y, w, h);
        AddPanelBorder(card.rectTransform, SetupBorder);
        return card.rectTransform;
    }

    private static void AddPanelBorder(RectTransform panel, Color32 color)
    {
        var outline = panel.gameObject.AddComponent<Outline>();
        outline.effectColor = color;
        outline.effectDistance = new Vector2(1f, -1f);
        outline.useGraphicAlpha = true;
    }

    private static void AddCardTitle(
        RectTransform card, string title, string subtitle, float rightInset = 20f)
    {
        float width = Mathf.Max(80f, card.rect.width - 20f - rightInset);
        AddText(card, title, 23f, VoiceUiKit.TextBright, TextAlignmentOptions.Left,
            FontStyles.Bold, new Vector2(20f, -13f), new Vector2(width, 24f));
        AddText(card, subtitle, 16f, SetupTextSecondary, TextAlignmentOptions.Left,
            FontStyles.Normal, new Vector2(20f, -40f), new Vector2(width, 22f));
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

    private static void BuildJourneyRow(
        RectTransform parent, float y, string glyph, string title, string description)
    {
        var icon = VoiceUiKit.Panel("JourneyIcon", parent,
            new Color32(34, 211, 238, 28), rounded: true, soft: true);
        PlaceTopLeft(icon.rectTransform, 22f, y, 46f, 46f);
        AddText(icon.rectTransform, glyph, 18f, VoiceUiKit.Accent,
            TextAlignmentOptions.Center, FontStyles.Bold,
            Vector2.zero, new Vector2(46f, 46f));
        AddText(parent, title, 21f, VoiceUiKit.TextBright,
            TextAlignmentOptions.Left, FontStyles.Bold,
            new Vector2(84f, y + 1f), new Vector2(parent.rect.width - 108f, 26f));
        AddText(parent, description, 17f, SetupTextSecondary,
            TextAlignmentOptions.Left, FontStyles.Normal,
            new Vector2(84f, y - 26f), new Vector2(parent.rect.width - 108f, 42f), wrap: true);
    }

    private static void BuildCheckBadge(
        RectTransform parent, Vector2 topLeft, float size, Color32 color)
    {
        var badge = VoiceUiKit.Panel("CheckBadge", parent, color, rounded: true, soft: true);
        PlaceTopLeft(badge.rectTransform, topLeft.x, topLeft.y, size, size);
        Color32 checkColor = new(9, 40, 29, 255);
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
            .Build(parent, label, width, y, 65f, help));
    }

    private static void BuildSummaryCard(
        float x, float width, string title, IReadOnlyList<string> lines, Action edit)
    {
        if (_pageRoot == null) return;
        var card = BuildCard(_pageRoot, "Summary" + title, x, ContentTopY, width, 232f);
        BuildCheckBadge(card, new Vector2(18f, -16f), 30f, SetupSuccess);
        AddText(card, title, 21f, VoiceUiKit.TextBright, TextAlignmentOptions.Left,
            FontStyles.Bold, new Vector2(60f, -15f), new Vector2(width - 150f, 32f));
        AddButton(card, "Edit", 70f, 34f, width - 88f, -13f,
            ButtonKind.Ghost, edit);
        for (int i = 0; i < lines.Count && i < 4; i++)
        {
            var line = AddText(card, lines[i], i == 0 ? 18f : 17f,
                i == 0 ? VoiceUiKit.TextPrimary : SetupTextSecondary,
                TextAlignmentOptions.Left, i == 0 ? FontStyles.Bold : FontStyles.Normal,
                new Vector2(20f, -66f - i * 38f), new Vector2(width - 40f, 32f), wrap: true);
            line.enableAutoSizing = true;
            line.fontSizeMax = i == 0 ? 18f : 17f;
            line.fontSizeMin = 15f;
        }
    }

    private static void BuildMicGlyph(RectTransform root)
    {
        var capsule = VoiceUiKit.Panel("MicCapsule", root, VoiceUiKit.Accent, rounded: true, soft: true);
        capsule.rectTransform.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        capsule.rectTransform.sizeDelta = new Vector2(25f, 43f);
        capsule.rectTransform.anchoredPosition = new Vector2(0f, 5f);
        var stem = VoiceUiKit.Panel("MicStem", root, VoiceUiKit.Accent, rounded: true, soft: true);
        stem.rectTransform.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        stem.rectTransform.sizeDelta = new Vector2(5f, 22f);
        stem.rectTransform.anchoredPosition = new Vector2(0f, -23f);
        var baseBar = VoiceUiKit.Panel("MicBase", root, VoiceUiKit.Accent, rounded: true, soft: true);
        baseBar.rectTransform.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        baseBar.rectTransform.sizeDelta = new Vector2(36f, 5f);
        baseBar.rectTransform.anchoredPosition = new Vector2(0f, -33f);
    }

    private static VoiceUiKit.Row AddRow(VoiceUiKit.Row row)
    {
        Rows.Add(row);
        return row;
    }

    private static SetupButton AddButton(
        RectTransform parent,
        string text,
        float width,
        float height,
        float x,
        float y,
        ButtonKind kind,
        Action click,
        Func<bool>? enabled = null)
    {
        var button = new SetupButton(parent, text, width, height, x, y, kind, click, enabled);
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
            : DisplayDevice(_draft?.MicrophoneDevice ?? string.Empty) + " (saved device)";

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
            : DisplayDevice(_draft?.SpeakerDevice ?? string.Empty) + " (saved device)";
#endif

    private static string DisplayDevice(string saved)
        => string.IsNullOrWhiteSpace(saved) ? "System Default" : saved;

    private static string CompactDevice(string saved)
    {
        string value = DisplayDevice(saved);
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

        var bindings = new (string Name, FirstRunSetupBinding Binding)[]
        {
            ("Mute microphone", _draft.ToggleMute),
            ("Push to Talk", _draft.PushToTalk),
            ("Mute playback", _draft.ToggleSpeaker),
            ("Open voice menu", _draft.OpenVoiceSettings),
        };
        for (int i = 0; i < bindings.Length; i++)
        for (int j = i + 1; j < bindings.Length; j++)
        {
            if (!BindingsConflict(bindings[i].Binding, bindings[j].Binding)) continue;
            return $"{bindings[i].Name} and {bindings[j].Name} use the same shortcut. Edit Controls before saving.";
        }
#endif
        return null;
    }

    private static bool BindingsConflict(FirstRunSetupBinding a, FirstRunSetupBinding b)
        => a.Key != KeyCode.None && a.Key == b.Key && a.Modifier == b.Modifier;

    private static float Smooth(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    private enum ButtonKind
    {
        Primary,
        Secondary,
        Ghost,
        Danger,
        DangerGhost,
    }

    private sealed class SetupButton
    {
        private readonly RectTransform _root;
        private readonly Image _surface;
        private readonly Image _glow;
        private readonly TextMeshProUGUI _label;
        private readonly ButtonKind _kind;
        private readonly Action _click;
        private readonly Func<bool> _enabled;

        internal SetupButton(
            RectTransform parent,
            string text,
            float width,
            float height,
            float x,
            float y,
            ButtonKind kind,
            Action click,
            Func<bool>? enabled = null)
        {
            _kind = kind;
            _click = click;
            _enabled = enabled ?? (() => true);
            _root = VoiceUiKit.Rect("SetupButton", parent);
            PlaceTopLeft(_root, x, y, width, height);
            _glow = VoiceUiKit.GlowImage("ButtonGlow", _root, VoiceUiKit.Clear);
            _glow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _glow.rectTransform.offsetMin = new Vector2(-8f, -4f);
            _glow.rectTransform.offsetMax = new Vector2(8f, 8f);
            _surface = _root.gameObject.AddComponent<Image>();
            _surface.sprite = VoiceUiKit.Rounded(true);
            _surface.type = Image.Type.Sliced;
            _surface.color = RestColor();
            _surface.raycastTarget = false;
            _label = VoiceUiKit.Text("ButtonLabel", _root, text, 18f,
                LabelColor(),
                TextAlignmentOptions.Center, FontStyles.Bold);
            _label.characterSpacing = 0f;
            _label.enableAutoSizing = true;
            _label.fontSizeMax = 18f;
            _label.fontSizeMin = 15f;
            _label.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _label.rectTransform.offsetMin = new Vector2(8f, 0f);
            _label.rectTransform.offsetMax = new Vector2(-8f, 0f);
        }

        internal void SetText(string text)
        {
            if (_label.text != text) _label.text = text;
        }

        internal bool IsPointerOver => _root != null && _root.gameObject.activeInHierarchy &&
                                       _enabled() && VoiceUiKit.Contains(_root);

        internal void Tick(float dt, bool allowInput)
        {
            if (_root == null || !_root.gameObject.activeInHierarchy) return;
            bool enabled = _enabled();
            bool over = enabled && VoiceUiKit.Contains(_root);
            bool down = over && Input.GetMouseButton(0);
            Color32 target = !enabled
                ? VoiceUiKit.ToggleOffTrack
                : over ? HoverColor() : RestColor();
            _surface.color = VoiceUiKit.Lerp(_surface.color, target, Mathf.Clamp01(dt * 15f));
            Color32 glow = VoiceUiKit.Clear;
            if (over && _kind == ButtonKind.Primary) glow = VoiceUiKit.AccentGlow;
            else if (over && (_kind == ButtonKind.Danger || _kind == ButtonKind.DangerGhost))
                glow = new Color32(230, 88, 96, 42);
            _glow.color = VoiceUiKit.Lerp(_glow.color, glow, Mathf.Clamp01(dt * 14f));
            _label.color = VoiceUiKit.Lerp(_label.color,
                enabled ? (over ? LabelHoverColor() : LabelColor()) : VoiceUiKit.TextFaint,
                Mathf.Clamp01(dt * 14f));
            float targetScale = down ? 0.98f : over ? 1.015f : 1f;
            float scale = Mathf.Lerp(_root.localScale.x, targetScale, Mathf.Clamp01(dt * 18f));
            _root.localScale = new Vector3(scale, scale, 1f);
            if (allowInput && over && Input.GetMouseButtonDown(0))
            {
                VoiceUiKit.SwallowClick();
                _click();
            }
        }

        private Color32 RestColor() => _kind switch
        {
            ButtonKind.Primary => VoiceUiKit.Accent,
            ButtonKind.Secondary => new Color32(34, 43, 56, 255),
            ButtonKind.Danger => new Color32(91, 39, 45, 255),
            _ => VoiceUiKit.Clear,
        };

        private Color32 HoverColor() => _kind switch
        {
            ButtonKind.Primary => new Color32(93, 231, 248, 255),
            ButtonKind.Secondary => SetupSurfaceHover,
            ButtonKind.Danger => new Color32(126, 48, 57, 255),
            ButtonKind.DangerGhost => new Color32(74, 34, 39, 230),
            _ => new Color32(34, 211, 238, 18),
        };

        private Color32 LabelColor() => _kind switch
        {
            ButtonKind.Primary => new Color32(6, 28, 36, 255),
            ButtonKind.DangerGhost => new Color32(242, 143, 149, 255),
            _ => VoiceUiKit.TextPrimary,
        };

        private Color32 LabelHoverColor() => _kind switch
        {
            ButtonKind.Primary => new Color32(4, 24, 31, 255),
            ButtonKind.DangerGhost => new Color32(255, 184, 188, 255),
            _ => VoiceUiKit.TextBright,
        };
    }

    private sealed class HudPresetCard
    {
        private readonly int _index;
        private readonly string _name;
        private readonly string _description;
        private readonly SpeakingBarPreviewSettings _previewSettings;
        private readonly bool _original;
        private RectTransform _root = null!;
        private RectTransform _visual = null!;
        private Image _surface = null!;
        private Image _border = null!;
        private TextMeshProUGUI _title = null!;
        private Image _selectedIndicator = null!;
        private float _hover;
        private float _selected;

        internal HudPresetCard(
            int index,
            string name,
            string description,
            SpeakingBarPreviewSettings previewSettings,
            bool original)
        {
            _index = index;
            _name = name;
            _description = description;
            _previewSettings = previewSettings;
            _original = original;
        }

        internal void Build(
            RectTransform parent, float x, float y, float width, float height, bool prominent)
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

            float miniX = prominent ? 8f : 6f;
            float miniY = prominent ? -5f : -6f;
            float miniW = prominent ? width - 16f : 44f;
            float miniH = prominent ? 34f : height - 12f;
            var viewport = VoiceUiKit.Panel("MiniViewport", _visual,
                new Color32(8, 13, 21, 255), rounded: true, soft: true);
            PlaceTopLeft(viewport.rectTransform, miniX, miniY, miniW, miniH);
            viewport.rectTransform.gameObject.AddComponent<RectMask2D>();
            BuildMiniLayout(viewport.rectTransform, _previewSettings, miniW, miniH);

            float textX = prominent ? 8f : miniW + 14f;
            float titleY = prominent ? -39f : -3f;
            float titleW = prominent ? width - 16f : width - textX - 28f;
            const float titleSize = 18f;
            _title = AddText(_visual, _name, titleSize, VoiceUiKit.TextPrimary,
                prominent ? TextAlignmentOptions.Center : TextAlignmentOptions.Left, FontStyles.Bold,
                new Vector2(textX, titleY), new Vector2(titleW, prominent ? 20f : 22f));
            _title.enableAutoSizing = true;
            _title.fontSizeMax = titleSize;
            _title.fontSizeMin = 15f;
            var trait = AddText(_visual, HudTrait(_previewSettings, _index == 0), 15f,
                SetupTextSecondary,
                prominent ? TextAlignmentOptions.Center : TextAlignmentOptions.Left, FontStyles.Normal,
                new Vector2(textX, prominent ? -58f : -25f),
                new Vector2(prominent ? width - 16f : width - textX - 8f, prominent ? 16f : 17f));
            trait.enableAutoSizing = true;
            trait.fontSizeMax = 15f;
            trait.fontSizeMin = 13f;
            var selected = VoiceUiKit.Panel("SelectedIndicator", _visual,
                VoiceUiKit.Clear, rounded: true, soft: true);
            PlaceTopLeft(selected.rectTransform, width - 22f, -7f, 13f, 13f);
            _selectedIndicator = selected;
        }

        internal void Tick(float dt)
        {
            if (_draft == null || _root == null) return;
            bool over = VoiceUiKit.Contains(_root);
            bool selected = _original
                ? _draft.OriginalHudSelected
                : !_draft.OriginalHudSelected && _draft.SelectedHudPreset == _index;
            if (over)
            {
                _hoveredHudSettings = _previewSettings;
                _hoveredHudDescription = _description;
                VoiceUiKit.RequestTooltip(_name, _description, plainTextDescription: true);
            }
            if (over && Time.frameCount > _inputLockedUntilFrame && Input.GetMouseButtonDown(0))
            {
                if (_original) _draft.SelectOriginalHud();
                else _draft.SelectHudPreset(_index);
                selected = true;
                VoiceUiKit.SwallowClick();
            }

            _hover = Mathf.MoveTowards(_hover, over ? 1f : 0f, dt * 10f);
            _selected = Mathf.MoveTowards(_selected, selected ? 1f : 0f, dt * 8f);
            _visual.anchoredPosition = new Vector2(0f, _hover * 1.5f);
            _surface.color = VoiceUiKit.Lerp(
                SetupSurfaceRaised,
                VoiceUiKit.Lerp(SetupSurfaceHover, new Color32(20, 49, 59, 255), _selected),
                Mathf.Max(_hover, _selected));
            _border.color = VoiceUiKit.Lerp(
                SetupBorder,
                VoiceUiKit.Lerp(new Color32(102, 122, 148, 180), VoiceUiKit.Accent, _selected),
                Mathf.Max(_hover, _selected));
            _title.color = VoiceUiKit.Lerp(VoiceUiKit.TextPrimary, VoiceUiKit.TextBright,
                Mathf.Max(_hover, _selected));
            var selectedColor = VoiceUiKit.Accent;
            selectedColor.a = (byte)Mathf.RoundToInt(255f * _selected);
            _selectedIndicator.color = selectedColor;
        }

        private static string HudTrait(SpeakingBarPreviewSettings settings, bool defaultChoice)
        {
            if (settings.ManualLayout)
                return $"Manual {settings.ManualOrientation.ToString().ToLowerInvariant()} / " +
                       $"{Mathf.RoundToInt(settings.Scale * 100f)}%";
            bool singleLane = SpeakingBarLayoutPolicy.UsesSingleVerticalLaneFor(
                settings.Position, settings.SideLayout);
            string flow = singleLane ? "No wrap" : "Wrapped";
            return defaultChoice
                ? $"Default / {flow}"
                : $"{flow} / {Mathf.RoundToInt(settings.Scale * 100f)}%";
        }

        private static void BuildMiniLayout(
            RectTransform viewport,
            SpeakingBarPreviewSettings settings,
            float width,
            float height)
        {
            var policy = SpeakingBarLayoutPolicy.ForPreset(settings.Position);
            bool horizontal = settings.ManualLayout
                ? settings.ManualOrientation == VoiceControlsLayout.Horizontal
                : policy.Orientation == VoiceControlsLayout.Horizontal;
            bool singleLane = !settings.ManualLayout &&
                              SpeakingBarLayoutPolicy.UsesSingleVerticalLaneFor(
                                  settings.Position, settings.SideLayout);
            // One TMP object paints the whole miniature roster. The previous version created
            // 15 Images per card (135 objects on HUD entry), which was a visible page hitch.
            const int dotCount = 7;
            int primaryCount = singleLane ? dotCount : 4;
            int laneCount = Mathf.CeilToInt(dotCount / (float)primaryCount);
            float primarySpace = horizontal ? width - 16f : height - 14f;
            float crossSpace = horizontal ? height - 14f : width - 16f;
            float primaryStep = primaryCount <= 1 ? 0f :
                Mathf.Min(7f, primarySpace / (primaryCount - 1));
            float crossStep = laneCount <= 1 ? 0f : Mathf.Min(7f, crossSpace / (laneCount - 1));
            float dotSize = Mathf.Clamp(5.4f * settings.Scale, 4.2f, 6.8f);
            float clusterW = horizontal
                ? (primaryCount - 1) * primaryStep + dotSize
                : (laneCount - 1) * crossStep + dotSize;
            float clusterH = horizontal
                ? (laneCount - 1) * crossStep + dotSize
                : (primaryCount - 1) * primaryStep + dotSize;

            float centerX;
            float centerY;
            if (settings.ManualLayout)
            {
                // ManualX/Y use viewport-normalized coordinates (0 = left/bottom,
                // 1 = right/top). Clamp the miniature like the real HUD so an upgrader's
                // saved custom layout is recognizable instead of borrowing the preset enum.
                float minX = 7f + clusterW * 0.5f;
                float maxX = width - 7f - clusterW * 0.5f;
                float topY = 7f + clusterH * 0.5f;
                float bottomY = height - 7f - clusterH * 0.5f;
                centerX = Mathf.Lerp(minX, maxX, Mathf.Clamp01(settings.ManualX));
                centerY = -Mathf.Lerp(bottomY, topY, Mathf.Clamp01(settings.ManualY));
            }
            else
            {
                centerX = settings.Position switch
                {
                    SpeakingBarPosition.TopLeft or SpeakingBarPosition.MiddleLeft or SpeakingBarPosition.BottomLeft
                        => 7f + clusterW * 0.5f,
                    SpeakingBarPosition.TopRight or SpeakingBarPosition.MiddleRight or SpeakingBarPosition.BottomRight
                        => width - 7f - clusterW * 0.5f,
                    _ => width * 0.5f,
                };
                centerY = settings.Position switch
                {
                    SpeakingBarPosition.TopLeft or SpeakingBarPosition.TopMiddle or SpeakingBarPosition.TopRight
                        => -(7f + clusterH * 0.5f),
                    SpeakingBarPosition.BottomLeft or SpeakingBarPosition.BottomMiddle or SpeakingBarPosition.BottomRight
                        => -(height - 7f - clusterH * 0.5f),
                    _ => -height * 0.5f,
                };
            }

            if (settings.Backdrop)
            {
                var backdrop = VoiceUiKit.Panel("MiniBackdrop", viewport,
                    new Color32(0, 0, 0, 150), rounded: true, soft: true);
                PlaceTopLeft(backdrop.rectTransform,
                    centerX - clusterW * 0.5f - 4f,
                    centerY + clusterH * 0.5f + 4f,
                    clusterW + 8f,
                    clusterH + 8f);
            }

            string Dot(int index)
                => index % 4 == 1
                    ? "<color=#22D3EE>●</color>"
                    : "<color=#687D97>●</color>";
            var lines = new List<string>();
            if (horizontal)
            {
                for (int lane = 0; lane < laneCount; lane++)
                {
                    string line = string.Empty;
                    for (int primary = 0; primary < primaryCount; primary++)
                    {
                        int index = lane * primaryCount + primary;
                        if (index >= dotCount) break;
                        line += Dot(index);
                    }
                    lines.Add(line);
                }
            }
            else
            {
                for (int primary = 0; primary < primaryCount; primary++)
                {
                    string line = string.Empty;
                    for (int lane = 0; lane < laneCount; lane++)
                    {
                        int index = lane * primaryCount + primary;
                        if (index < dotCount) line += Dot(index);
                    }
                    lines.Add(line);
                }
            }

            var roster = AddText(viewport, string.Join("\n", lines), dotSize * 1.45f,
                VoiceUiKit.TextMuted, TextAlignmentOptions.Center, FontStyles.Bold,
                new Vector2(centerX - clusterW * 0.5f - 3f,
                    centerY + clusterH * 0.5f + 3f),
                new Vector2(clusterW + 6f, clusterH + 6f), wrap: false);
            roster.richText = true;
            roster.characterSpacing = 8f;
            roster.lineSpacing = -20f;
            roster.overflowMode = TextOverflowModes.Overflow;
        }
    }

    private sealed class CelebrationDot
    {
        private RectTransform _root = null!;
        private Image _image = null!;
        private Vector2 _base;
        private float _phase;

        internal void Build(RectTransform parent, Vector2 position, int index)
        {
            _base = position;
            _phase = index * 0.61f;
            _root = VoiceUiKit.Rect("CelebrationDot", parent);
            _root.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f));
            float size = 5f + index % 4 * 2f;
            _root.sizeDelta = new Vector2(size, size);
            _root.anchoredPosition = position;
            _image = _root.gameObject.AddComponent<Image>();
            _image.sprite = VoiceUiKit.Rounded(true);
            _image.type = Image.Type.Sliced;
            _image.color = index % 3 == 0 ? VoiceUiKit.TextBright : VoiceUiKit.Accent;
            _image.raycastTarget = false;
        }

        internal void Tick(float dt)
        {
            _phase += dt * 1.8f;
            _root.anchoredPosition = _base + new Vector2(
                Mathf.Sin(_phase) * 5f,
                Mathf.Cos(_phase * 0.83f) * 7f);
            float alpha = 0.35f + (Mathf.Sin(_phase * 1.4f) + 1f) * 0.325f;
            var c = _image.color;
            c.a = alpha;
            _image.color = c;
        }
    }
}
