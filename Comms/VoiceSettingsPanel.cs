using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceSettingsPanel
{
    private enum MixSettingsExpansion
    {
        None,
        AliveFocus,
        DeadFocus,
    }

    private const float PanelW = 908f;
    private const float PanelH = 554f;
    private const float PanelScale = 1.3f;
    private const float RowH = 72f;
    private const float DeviceRowH = 142f;
    private const float HeaderH = 42f;
    private const float TopPad = 12f;
    private const float SmoothScrollRate = 18f;
    private const float ScrollbarMinThumbHeight = 30f;

    private static readonly string[] Categories =
        { "AUDIO", "DEVICES", "KEYBINDS", "HUD", "ADVANCED" };

    private static VoiceUiKit.PanelShell? _shell;
    private static VoiceUiKit.CategoryRail? _rail;
    private static readonly List<VoiceUiKit.Row> _rows = new();
    private static VoiceUiKit.Row? _activeRow;
    private static float _scroll;
    private static float _scrollTarget;
    private static float _contentHeight;
    private static float _animT;
    private static int _visSignature = int.MinValue;
    private static MixSettingsExpansion _expandedMixSettings;
    private static bool _rebuildRequested;
    private static bool _revealExpandedMix;
    private static RectTransform? _scrollbarRoot;
    private static RectTransform? _scrollbarThumb;
    private static Image? _scrollbarTrackImage;
    private static Image? _scrollbarThumbImage;
    private static bool _scrollbarDragging;
    private static float _scrollbarDragOffset;
    private static SpeakingBarLivePreview? _livePreview;
    private static bool _livePreviewUnavailable;
    private static bool _lastLivePreviewEnabled;
    private static float _livePreviewProgress;
    private static int _deferredShowFrame = -1;

    private static bool ShellAlive => _shell != null && _shell.Root != null;
    private static bool _shown;
    public static bool IsOpen => ShellAlive && _shown;

    public static void Toggle()
    {
        if (_shown) Hide();
        else Show();
    }

    public static void Show()
    {
        if (VoiceSettings.Instance == null) return;

        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();

        bool rebuilt = false;
        if (!ShellAlive)
        {
            Destroy();
            Build();
            rebuilt = true;
        }

        _shell!.Root.SetActive(true);
        _shell.Group.alpha = 1f;
        _shell.Group.interactable = true;
        _shell.Group.blocksRaycasts = true;
        _scroll = 0f;
        _scrollTarget = 0f;
        CancelScrollbarDrag();
        _shell.PaneRoot.anchoredPosition = Vector2.zero;
        _animT = 0f;
        _shown = true;
        VoiceChatPatches.ReleaseHeldTransmitInputs();

        bool previewEnabled = VoiceSettings.Instance.SpeakingBarLivePreview.Value;
        _lastLivePreviewEnabled = previewEnabled;
        _livePreviewProgress = previewEnabled ? 1f : 0f;
        // Build the light-weight preview card up front and warm its render world over the
        // next few frames. Toggling Live Preview later should only reveal already-ready
        // objects, never construct fifteen avatars and a render texture on the click frame.
        _livePreviewUnavailable = false;
        EnsureLivePreview();
        ApplyPanelPresentation();
        UpdateLivePreview(0f);

        if (!rebuilt) RebuildRows(true);

        VoiceUiKit.RaiseAbove(_shell.RootRect);
    }

    internal static void ShowDeferred()
    {
        _deferredShowFrame = Time.frameCount + 1;
    }

    private static void Build()
    {
        _shell = new VoiceUiKit.PanelShell("VC_SettingsPanel", "PERFECT COMMS", PanelW, PanelH, HeaderClose);
        _rail = new VoiceUiKit.CategoryRail();
        _rail.Build(_shell.RailRoot, _shell.RailWidth, Categories);
        _rail.OnSelect = _ =>
        {
            _expandedMixSettings = MixSettingsExpansion.None;
            _rebuildRequested = false;
            _revealExpandedMix = false;
            RebuildRows(true);
        };
        BuildScrollbar();
        RebuildRows(true);
    }

    private static void BuildScrollbar()
    {
        if (_shell == null) return;

        _scrollbarRoot = VoiceUiKit.Rect("SettingsScrollbar", _shell.PaneClip);
        _scrollbarRoot.Anchor(new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
        _scrollbarRoot.sizeDelta = new Vector2(18f, -16f);
        _scrollbarRoot.anchoredPosition = new Vector2(-9f, 0f);

        _scrollbarTrackImage = VoiceUiKit.Panel(
            "ScrollbarTrack",
            _scrollbarRoot,
            new Color32(26, 39, 55, 205),
            rounded: true,
            soft: true);
        _scrollbarTrackImage.rectTransform.Anchor(
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 0.5f));
        _scrollbarTrackImage.rectTransform.sizeDelta = new Vector2(4f, -4f);
        _scrollbarTrackImage.rectTransform.anchoredPosition = Vector2.zero;

        _scrollbarThumb = VoiceUiKit.Rect("ScrollbarThumbHit", _scrollbarRoot);
        _scrollbarThumb.Anchor(
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 1f));
        _scrollbarThumb.sizeDelta = new Vector2(18f, ScrollbarMinThumbHeight);
        _scrollbarThumb.anchoredPosition = Vector2.zero;

        _scrollbarThumbImage = VoiceUiKit.Panel(
            "ScrollbarThumb",
            _scrollbarThumb,
            VoiceUiKit.TextMuted,
            rounded: true,
            soft: true);
        _scrollbarThumbImage.rectTransform.Anchor(
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 1f),
            new Vector2(0.5f, 0.5f));
        _scrollbarThumbImage.rectTransform.sizeDelta = new Vector2(7f, -2f);
        _scrollbarThumbImage.rectTransform.anchoredPosition = Vector2.zero;

        _scrollbarRoot.SetAsLastSibling();
        UpdateScrollbarVisual();
    }

    public static void Hide()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        _expandedMixSettings = MixSettingsExpansion.None;
        _rebuildRequested = false;
        _revealExpandedMix = false;
        CancelScrollbarDrag();
        _shown = false;
        _animT = 0f;
        _activeRow = null;
        _livePreview?.Suspend();
        if (_shell != null && _shell.Root != null)
        {
            _shell.Group.alpha = 0f;
            _shell.Group.interactable = false;
            _shell.Group.blocksRaycasts = false;
            _shell.Root.SetActive(false);
        }
    }

    private static void HeaderClose()
    {
        VoiceUiKit.SwallowClick();
        Hide();
    }

    public static void ForceClose()
    {
        Hide();
    }

    private static void Destroy()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        CancelScrollbarDrag();
        if (_livePreview != null)
        {
            _livePreview.Dispose();
            _livePreview = null;
        }
        if (_shell != null)
        {
            if (_shell.Root != null) Object.Destroy(_shell.Root);
            _shell = null;
        }
        _rail = null;
        _rows.Clear();
        _activeRow = null;
        _scroll = 0f;
        _scrollTarget = 0f;
        _contentHeight = 0f;
        _shown = false;
        _visSignature = int.MinValue;
        _expandedMixSettings = MixSettingsExpansion.None;
        _rebuildRequested = false;
        _revealExpandedMix = false;
        _scrollbarRoot = null;
        _scrollbarThumb = null;
        _scrollbarTrackImage = null;
        _scrollbarThumbImage = null;
        _livePreviewUnavailable = false;
        _livePreviewProgress = 0f;
        _lastLivePreviewEnabled = false;
    }

    private static void RebuildRows(bool resetScroll)
    {
        if (_shell == null) return;
        VoiceUiKit.RebindRow.CancelCapture();
        CancelScrollbarDrag();
        for (int i = _shell.PaneRoot.childCount - 1; i >= 0; i--)
            Object.Destroy(_shell.PaneRoot.GetChild(i).gameObject);
        _rows.Clear();
        _activeRow = null;

        var visible = CollectVisible(_rail!.Selected);
        float? revealBottom = null;

        float y = -TopPad;
        for (int i = 0; i < visible.Count; i++)
        {
            var e = visible[i];
            var row = e.Build(_shell.PaneRoot, _shell.PaneWidth, y);
            if (row != null) _rows.Add(row);
            bool nextIsRow = i < visible.Count - 1 && !visible[i + 1].IsHeader;
            if (!e.IsHeader && nextIsRow)
            {
                var div = VoiceUiKit.Panel("Div", _shell.PaneRoot, VoiceUiKit.Divider, false);
                div.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                div.rectTransform.sizeDelta = new Vector2(-20f, 1f);
                div.rectTransform.anchoredPosition = new Vector2(0f, y - e.Height + 1f);
            }
            if (_revealExpandedMix
                && ((_expandedMixSettings == MixSettingsExpansion.AliveFocus
                        && e.Key == "AliveFocus.DeadPlayers")
                    || (_expandedMixSettings == MixSettingsExpansion.DeadFocus
                        && e.Key == "DeadFocus.DeadPlayers")))
                revealBottom = y - e.Height;
            y -= e.Height;
        }
        _contentHeight = -y;
        _visSignature = Signature(visible);

        ApplyScroll(resetScroll);
        if (_revealExpandedMix && revealBottom.HasValue)
            RevealContentBottom(revealBottom.Value);
        _revealExpandedMix = false;

        if (visible.Count == 0)
        {
            var empty = VoiceUiKit.Text("Empty", _shell.PaneRoot, "No options", 16f,
                VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Center);
            empty.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            empty.rectTransform.sizeDelta = new Vector2(0f, 40f);
            empty.rectTransform.anchoredPosition = new Vector2(0f, -30f);
        }
    }

    private sealed class Entry
    {
        public string Key = "";
        public float Height = RowH;
        public bool IsHeader;
        public Func<bool> Visible = () => true;
        public Func<RectTransform, float, float, VoiceUiKit.Row?> Build = (_, _, _) => null;
    }

    private static readonly Func<bool> Always = () => true;

    private static List<Entry> BuildCategory(int cat)
    {
        var defs = new List<Entry>();
        var s = VoiceSettings.Instance!;
        switch (cat)
        {
            case 0: BuildAudio(defs, s); break;
            case 1: BuildDevices(defs, s); break;
            case 2: BuildKeybinds(defs, s); break;
            case 3: BuildHud(defs, s); break;
            case 4: BuildAdvanced(defs, s); break;
        }
        return defs;
    }

    private static List<Entry> CollectVisible(int cat)
    {
        var all = BuildCategory(cat);
        var list = new List<Entry>();
        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            if (!e.IsHeader && !e.Visible()) continue;
            list.Add(e);
        }
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].IsHeader) break;
            list.RemoveAt(i);
        }
        return list;
    }

    private static int Signature(List<Entry> entries)
    {
        int sig = entries.Count;
        for (int i = 0; i < entries.Count; i++)
            sig = sig * 31 + entries[i].Key.GetHashCode();
        sig = sig * 31 + VoiceChatLocalSettings.MicDeviceListVersion;
        sig = sig * 31 + VoiceChatLocalSettings.SpkDeviceListVersion;
        return sig;
    }

    private static void ApplyScroll(bool reset)
    {
        float maxScroll = MaxScroll();
        if (reset)
        {
            _scroll = 0f;
            _scrollTarget = 0f;
        }
        else
        {
            _scroll = Mathf.Clamp(_scroll, 0f, maxScroll);
            _scrollTarget = Mathf.Clamp(_scrollTarget, 0f, maxScroll);
        }
        _shell!.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
    }

    private static void RevealContentBottom(float contentBottom)
    {
        if (_shell == null) return;
        float maxScroll = MaxScroll();
        float requiredScroll = -ViewHeight() + 12f - contentBottom;
        _scrollTarget = Mathf.Clamp(Mathf.Max(_scrollTarget, requiredScroll), 0f, maxScroll);
        _scroll = Mathf.Clamp(_scroll, 0f, maxScroll);
        UpdateScrollbarVisual();
    }

    private static Func<float, string> Pct => v => $"<color=#22D3EE>{Mathf.RoundToInt(v * 100f)}%</color>";
    private static Func<float, string> VolumePct => v => v <= 0.005f
        ? "<color=#8C9CB2>None</color>"
        : Pct(v);
    private static Func<float, string> Num2 => v => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static void Section(List<Entry> defs, string title)
    {
        defs.Add(new Entry
        {
            Key = "##" + title,
            Height = HeaderH,
            IsHeader = true,
            Visible = Always,
            Build = (pane, paneW, y) =>
            {
                VoiceUiKit.SectionHeader(title, pane, title, paneW, y, HeaderH);
                return null;
            }
        });
    }

    private static void Slider(List<Entry> defs, string label,
        BepInEx.Configuration.ConfigEntry<float> entry, Func<float, string> fmt,
        Func<bool>? visible = null, string? key = null)
    {
        var range = GetRange(entry);
        defs.Add(new Entry
        {
            Key = key ?? label,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.SliderRow(
                () => entry.Value, v => entry.Value = v, range.x, range.y, fmt)
                .Build(pane, label, paneW, y, RowH, SettingHelp(entry))
        });
    }

    private static void Toggle(List<Entry> defs, string label,
        BepInEx.Configuration.ConfigEntry<bool> entry, Func<bool>? visible = null)
    {
        defs.Add(new Entry
        {
            Key = label,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.ToggleRow(() => entry.Value, v => entry.Value = v)
                .Build(pane, label, paneW, y, RowH, SettingHelp(entry))
        });
    }

    private static void Toggle(List<Entry> defs, string label,
        Func<bool> get, Action<bool> set, string help, Func<bool>? visible = null)
    {
        defs.Add(new Entry
        {
            Key = label,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.ToggleRow(get, set)
                .Build(pane, label, paneW, y, RowH, help)
        });
    }

    private static void EnumStep<TEnum>(List<Entry> defs, string label,
        BepInEx.Configuration.ConfigEntry<TEnum> entry, string[] labels, Func<bool>? visible = null)
        where TEnum : struct, Enum
    {
        defs.Add(new Entry
        {
            Key = label,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                () => Convert.ToInt32(entry.Value),
                i => entry.Value = (TEnum)Enum.ToObject(typeof(TEnum), i),
                () => labels.Length,
                i => labels[Mathf.Clamp(i, 0, labels.Length - 1)])
                .Build(pane, label, paneW, y, RowH, SettingHelp(entry))
        });
    }

    private static void BuildAudio(List<Entry> defs, VoiceChatLocalSettings s)
    {
        Section(defs, "LEVELS");
        Slider(defs, "Mic Volume", s.MicVolume, Pct);
        Slider(defs, "Mic Sensitivity", s.MicSensitivity, Num2);
        Slider(defs, "Speaker Volume", s.MasterVolume, Pct);
        Section(defs, "PROCESSING");
        EnumStep(defs, "Mic Mode", s.MicMode, new[] { "Open Mic", "Push To Talk" });
#if WINDOWS
        Toggle(defs, "Noise Suppression", s.NoiseSuppressionEnabled);
        Toggle(defs, "Echo Cancellation", s.EchoCancellationEnabled);
#endif
        Slider(defs, "Voice Falloff Softness", s.VoiceFalloffSoftness, Pct);
        Section(defs, "STARTUP");
        Toggle(defs, "Start Muted", s.StartMuted);
        Toggle(defs, "Start Deafened", s.StartDeafened);
    }

    private static void BuildDevices(List<Entry> defs, VoiceChatLocalSettings s)
    {
        VoiceChatLocalSettings.MaybeRefreshDeviceLists();

        defs.Add(new Entry
        {
            Key = "Microphone",
            Height = DeviceRowH,
            Visible = Always,
            Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                () => (int)s.MicrophoneDeviceIndex.Value,
                i => s.MicrophoneDeviceIndex.Value = (MicDeviceEnum)i,
                () => VoiceChatLocalSettings.MicDeviceNames.Length,
                i => DeviceName(VoiceChatLocalSettings.MicDeviceNames, i),
                fullWidthValue: true)
                .Build(pane, "Microphone", paneW, y, DeviceRowH,
                    SettingHelp(s.MicrophoneDeviceIndex))
        });

#if WINDOWS
        defs.Add(new Entry
        {
            Key = "Speaker",
            Height = DeviceRowH,
            Visible = Always,
            Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                () => (int)s.SpeakerDeviceIndex.Value,
                i => s.SpeakerDeviceIndex.Value = (SpkDeviceEnum)i,
                () => VoiceChatLocalSettings.SpkDeviceNames.Length,
                i => DeviceName(VoiceChatLocalSettings.SpkDeviceNames, i),
                fullWidthValue: true)
                .Build(pane, "Speaker", paneW, y, DeviceRowH,
                    SettingHelp(s.SpeakerDeviceIndex))
        });
#endif
    }

    private static string DeviceName(string[] names, int i)
    {
        if (names.Length == 0) return "<color=#607282>No devices found</color>";
        return names[Mathf.Clamp(i, 0, names.Length - 1)];
    }

    private static void Rebind(
        List<Entry> defs,
        VoiceKeybind bind,
        Action? configure = null,
        Func<bool>? configureActive = null)
    {
        defs.Add(new Entry
        {
            Key = bind.DisplayName,
            Visible = Always,
            Build = (pane, paneW, y) => new VoiceUiKit.RebindRow(
                () => bind.CurrentKey,
                (key, modifier, match) => bind.SetBinding(key, modifier, match),
                () => bind.Clear(),
                () => bind.Modifier,
                () => bind.ModifierMatch,
                configure,
                configureActive)
                .Build(pane, bind.DisplayName, paneW, y, RowH, bind.HelpText)
        });
    }

    private static void Action(
        List<Entry> defs,
        string label,
        string buttonText,
        System.Action onClick,
        string help)
    {
        defs.Add(new Entry
        {
            Key = label,
            Visible = Always,
            Build = (pane, paneW, y) => new VoiceUiKit.ActionRow(onClick)
                .Build(pane, label, buttonText, paneW, y, RowH, help)
        });
    }

    private static void SpeakingBarNamePositionStep(List<Entry> defs, VoiceChatLocalSettings settings)
    {
        // Auto was appended to the persisted enum to preserve the legacy 0-3 values,
        // but it belongs first in the user-facing stepper because it is the v4 default.
        var order = new[]
        {
            SpeakingBarNamePosition.Auto,
            SpeakingBarNamePosition.Bottom,
            SpeakingBarNamePosition.Top,
            SpeakingBarNamePosition.Left,
            SpeakingBarNamePosition.Right,
        };
        var labels = new[] { "Auto", "Bottom", "Top", "Left", "Right" };

        defs.Add(new Entry
        {
            Key = "Speaking Bar Name Position",
            Visible = Always,
            Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                () => Array.IndexOf(order, settings.SpeakingBarNamePosition.Value) switch
                {
                    < 0 => 0,
                    var index => index,
                },
                i => settings.SpeakingBarNamePosition.Value = order[Mathf.Clamp(i, 0, order.Length - 1)],
                () => order.Length,
                i => labels[Mathf.Clamp(i, 0, labels.Length - 1)])
                .Build(pane, "Speaking Bar Name Position", paneW, y, RowH,
                    SettingHelp(settings.SpeakingBarNamePosition))
        });
    }

    private static void ToggleMixSettings(MixSettingsExpansion expansion)
    {
        bool opening = _expandedMixSettings != expansion;
        _expandedMixSettings = opening
            ? expansion
            : MixSettingsExpansion.None;
        _revealExpandedMix = opening;
        _rebuildRequested = true;
    }

    private static void BuildKeybinds(List<Entry> defs, VoiceChatLocalSettings s)
    {
        Rebind(defs, VoiceChatKeybinds.OpenVoiceMenu);
        Rebind(defs, VoiceChatKeybinds.OpenHostVoiceSettings);
        Rebind(defs, VoiceChatKeybinds.ToggleMute);
        Rebind(defs, VoiceChatKeybinds.PushToTalk);
        Rebind(defs, VoiceChatKeybinds.TeamRadio);
        Rebind(defs, VoiceChatKeybinds.CycleTeamRadioChannel);
        Rebind(defs, VoiceChatKeybinds.ToggleMicMode);
        Rebind(defs, VoiceChatKeybinds.ToggleSpeaker);
        Rebind(defs, VoiceChatKeybinds.VolumeMenu);
        Rebind(defs, VoiceChatKeybinds.AliveLouderDeadQuieter,
            () => ToggleMixSettings(MixSettingsExpansion.AliveFocus),
            () => _expandedMixSettings == MixSettingsExpansion.AliveFocus);
        if (_expandedMixSettings == MixSettingsExpansion.AliveFocus)
        {
            Slider(defs, "Alive Players", s.AliveFocusAliveVolume, VolumePct,
                key: "AliveFocus.AlivePlayers");
            Slider(defs, "Dead Players", s.AliveFocusDeadVolume, VolumePct,
                key: "AliveFocus.DeadPlayers");
        }
        Rebind(defs, VoiceChatKeybinds.AliveQuieterDeadLouder,
            () => ToggleMixSettings(MixSettingsExpansion.DeadFocus),
            () => _expandedMixSettings == MixSettingsExpansion.DeadFocus);
        if (_expandedMixSettings == MixSettingsExpansion.DeadFocus)
        {
            Slider(defs, "Alive Players", s.DeadFocusAliveVolume, VolumePct,
                key: "DeadFocus.AlivePlayers");
            Slider(defs, "Dead Players", s.DeadFocusDeadVolume, VolumePct,
                key: "DeadFocus.DeadPlayers");
        }
        Rebind(defs, VoiceChatKeybinds.LocalVoiceRefresh);
        Rebind(defs, VoiceChatKeybinds.HostVoiceRefresh);
    }

    private static void BuildHud(List<Entry> defs, VoiceChatLocalSettings s)
    {
        Section(defs, "VOICE CONTROLS");
        EnumStep(defs, "Controls Layout", s.VoiceControlsLayout, new[] { "Vertical", "Horizontal" });
        Slider(defs, "Button Position X", s.ButtonPositionX, Pct);
        Slider(defs, "Button Position Y", s.ButtonPositionY, Pct);
        Slider(defs, "Button Scale", s.OverlayScale, Num2);

        Section(defs, "SPEAKING BAR");
        Toggle(defs, "Show All Players", s.SpeakingBarFixedAllPlayers);
        Toggle(defs, "Live Preview", s.SpeakingBarLivePreview);
        EnumStep(defs, "Speaking Bar Position", s.SpeakingBarPosition, new[]
        {
            "Top Left", "Top Middle", "Top Right", "Bottom Left", "Bottom Middle", "Bottom Right",
            "Middle Left", "Middle Right"
        });
        EnumStep(defs, "Side Layout", s.SpeakingBarSideLayout, new[] { "Single Lane", "Wrapped" },
            () => !s.SpeakingBarManualLayout.Value &&
                  SpeakingBarLayoutPolicy.IsSidePreset(s.SpeakingBarPosition.Value));
        SpeakingBarNamePositionStep(defs, s);
        Slider(defs, "Speaking Bar Scale", s.SpeakingBarScale, Pct);
        Toggle(defs, "Speaking Bar Backdrop", s.SpeakingBarBackdrop);
        Toggle(defs, "Speaking Bar Manual Layout", s.SpeakingBarManualLayout);
        EnumStep(defs, "Speaking Bar Layout", s.SpeakingBarLayout, new[] { "Vertical", "Horizontal" },
            () => s.SpeakingBarManualLayout.Value);
        EnumStep(defs, "Avatar Facing", s.SpeakingBarAvatarFacing, new[] { "Right", "Left" },
            () => s.SpeakingBarManualLayout.Value);
        Slider(defs, "Speaking Bar X", s.SpeakingBarX, Pct, () => s.SpeakingBarManualLayout.Value);
        Slider(defs, "Speaking Bar Y", s.SpeakingBarY, Pct, () => s.SpeakingBarManualLayout.Value);

        Section(defs, "MEETING OVERLAY");
        Toggle(defs, "Meeting Speaking Overlay", s.MeetingSpeakingOverlay);

        Section(defs, "OTHER");
        EnumStep(defs, "Jail Unmute Placement", s.JailUnmuteButtonPlacement, new[] { "Voice HUD", "Meeting Card" });
    }

    private static void BuildAdvanced(List<Entry> defs, VoiceChatLocalSettings s)
    {
        Section(defs, "SETUP");
        Action(defs, "First-Time Setup", "RUN SETUP AGAIN", ShowFirstRunSetup,
            "Reopens the guided audio, controls, and HUD setup. Your current settings are kept unless you finish with changes.");

        Section(defs, "TROUBLESHOOTING");
        Toggle(defs, "Show Fake 15 Players", s.ShowFake15Players);
        Toggle(defs, "Diagnostics",
            () => s.DebugVoiceStats.Value || s.MicCalibrationDiagnostics.Value,
            v => s.ApplyDiagnosticsToggle(v),
            "Writes detailed voice logs and microphone calibration data for troubleshooting. Leave this off unless you are diagnosing an issue.");
    }

    private static void ShowFirstRunSetup()
    {
        Hide();
        VoiceUiKit.SwallowClick();
        VoiceFirstRunSetup.ShowManual();
    }

    private static Vector2 GetRange(BepInEx.Configuration.ConfigEntryBase entry)
    {
        var desc = entry.Description;
        if (desc?.AcceptableValues is BepInEx.Configuration.AcceptableValueRange<float> r)
            return new Vector2((float)r.MinValue, (float)r.MaxValue);
        return new Vector2(0f, 1f);
    }

    private static string SettingHelp(BepInEx.Configuration.ConfigEntryBase entry)
        => entry.Description?.Description ?? string.Empty;

    public static void Tick()
    {
        if (_deferredShowFrame >= 0 && Time.frameCount >= _deferredShowFrame)
        {
            _deferredShowFrame = -1;
            Show();
        }
        if (_shell == null) return;
        if (_shell.Root == null) { Destroy(); return; }
        if (!_shown) return;

        if (!VoiceUiKit.RebindRow.IsCapturing && Input.GetKeyDown(KeyCode.Escape))
        {
            VoiceUiKit.SwallowClick();
            Hide();
            return;
        }

        float dt = Mathf.Max(0f, Time.unscaledDeltaTime);
        UpdateLivePreview(dt);
        if (_animT < 1f)
            _animT = Mathf.Min(1f, _animT + dt / 0.22f);
        ApplyPanelPresentation();

        _shell.TickHeader();
        if (_shell == null || !_shown) return;
        _rail!.Tick();
        bool scrollbarOwnsPointer = HandleScrollInput();
        UpdateSmoothScroll(dt);
        HandleInput(scrollbarOwnsPointer);
        for (int i = 0; i < _rows.Count; i++) _rows[i].Tick(dt);

        if (_rebuildRequested)
        {
            _rebuildRequested = false;
            RebuildRows(false);
            return;
        }

        if (Time.frameCount % 20 == 0) RefreshVisibilityIfChanged();
    }

    private static void RefreshVisibilityIfChanged()
    {
        if (_rail == null) return;
        var visible = CollectVisible(_rail.Selected);
        if (Signature(visible) == _visSignature) return;
        RebuildRows(false);
    }

    private static void EnsureLivePreview()
    {
        if (_livePreview != null || _livePreviewUnavailable || _shell == null) return;
        try
        {
            _livePreview = new SpeakingBarLivePreview(_shell.RootRect);
        }
        catch (Exception ex)
        {
            // A constructor that fails midway cannot return an instance for Dispose(). Roll
            // back any partially-created card directly from the shell hierarchy.
            for (int i = _shell.RootRect.childCount - 1; i >= 0; i--)
            {
                var child = _shell.RootRect.GetChild(i);
                if (child.name == "VC_SpeakingBarLivePreview")
                {
                    child.gameObject.SetActive(false);
                    Object.Destroy(child.gameObject);
                }
            }
            _livePreviewUnavailable = true;
            global::VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning(
                "[PC-UI] Could not build speaking-bar live preview: " + ex.Message);
        }
    }

    private static void UpdateLivePreview(float unscaledDeltaTime)
    {
        var settings = VoiceSettings.Instance;
        if (settings == null) return;

        bool enabled = settings.SpeakingBarLivePreview.Value;
        if (enabled != _lastLivePreviewEnabled && _livePreviewUnavailable)
            _livePreviewUnavailable = false;
        _lastLivePreviewEnabled = enabled;
        _livePreviewProgress = SpeakingBarLivePreviewTransitionPolicy.Advance(
            _livePreviewProgress,
            enabled,
            unscaledDeltaTime);

        bool shouldRender = _shown && (enabled || _livePreviewProgress > 0.001f);
        if (_shown) EnsureLivePreview();
        if (_livePreview == null)
        {
            if (_livePreviewUnavailable) _livePreviewProgress = 0f;
            return;
        }
        var preview = _livePreview;

        try
        {
            if (!preview.IsWarmupReady)
                preview.Prewarm(settings, unscaledDeltaTime);
            var transition = SpeakingBarLivePreviewTransitionPolicy.Resolve(_livePreviewProgress);
            preview.SetPresentation(transition.Reveal, shouldRender);
            if (shouldRender && preview.IsWarmupReady)
                preview.Tick(settings, unscaledDeltaTime);
            if (preview.IsUnavailable)
            {
                preview.Dispose();
                _livePreview = null;
                _livePreviewUnavailable = true;
                _livePreviewProgress = 0f;
                return;
            }
            preview.SetPresentation(transition.Reveal, shouldRender);
        }
        catch (Exception ex)
        {
            // The preview is optional editor UI. A broken scene asset or graphics resource
            // must not trap the user in a frame-by-frame exception before they can turn it off.
            global::VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning(
                "[PC-UI] Speaking-bar live preview was disabled after an error: " + ex.Message);
            try { preview.Dispose(); }
            catch
            {
                try { preview.Suspend(); }
                catch { }
            }
            _livePreview = null;
            _livePreviewUnavailable = true;
            _livePreviewProgress = 0f;
        }
    }

    private static void ApplyPanelPresentation()
    {
        if (_shell == null) return;

        var workspace = CurrentPreviewWorkspace();
        float move = SpeakingBarLivePreviewTransitionPolicy.Resolve(_livePreviewProgress).Move;
        float targetScale = Mathf.Lerp(PanelScale, workspace.Scale, move);
        float appearScale = VoiceUiKit.AppearScale(_animT);
        if (move > 0.001f)
            appearScale = Mathf.Min(1f, appearScale);
        float actualScale = targetScale * appearScale;
        _shell.RootRect.localScale = new Vector3(actualScale, actualScale, 1f);
        _shell.Group.alpha = Mathf.Clamp01(_animT / 0.6f);

        float addedWidth = SpeakingBarLivePreviewWorkspacePolicy.Gap +
            SpeakingBarLivePreviewWorkspacePolicy.PreviewWidth;
        float desiredOffset = -addedWidth * 0.5f * actualScale * move;
        _shell.SetLayoutOffset(new Vector2(desiredOffset, 0f));
    }

    private static SpeakingBarPreviewWorkspace CurrentPreviewWorkspace()
    {
        Rect rect = VoiceUiKit.CanvasRect.rect;
        float width = rect.width > 1f ? rect.width : Mathf.Max(1f, Screen.width);
        float height = rect.height > 1f ? rect.height : Mathf.Max(1f, Screen.height);
        return SpeakingBarLivePreviewWorkspacePolicy.Compute(width, height);
    }

    private static float ViewHeight()
        => _shell != null ? _shell.PaneHeight - 24f : 0f;

    private static float MaxScroll()
        => VoiceSettingsScrollPolicy.MaxScroll(_contentHeight, ViewHeight());

    private static bool HandleScrollInput()
    {
        if (_shell == null || _scrollbarRoot == null || !_scrollbarRoot.gameObject.activeSelf)
        {
            CancelScrollbarDrag();
            return false;
        }

        if (_scrollbarDragging)
        {
            if (!Input.GetMouseButton(0))
            {
                CancelScrollbarDrag();
                return false;
            }
            if (TryGetScrollbarPointerFromTop(out float pointerFromTop))
                SetScrollFromThumbTop(pointerFromTop - _scrollbarDragOffset, snap: true);
            return true;
        }

        bool inputBusy = _activeRow != null || VoiceUiKit.RebindRow.IsCapturing;
        if (inputBusy)
            _scrollTarget = _scroll;
        if (!inputBusy && Input.GetMouseButtonDown(0) && VoiceUiKit.Contains(_scrollbarRoot))
        {
            if (!TryGetScrollbarPointerFromTop(out float pointerFromTop)) return true;
            float thumbTop = _scrollbarThumb != null ? -_scrollbarThumb.anchoredPosition.y : 0f;
            if (_scrollbarThumb != null && VoiceUiKit.Contains(_scrollbarThumb))
            {
                _scrollbarDragging = true;
                _scrollbarDragOffset = pointerFromTop - thumbTop;
            }
            else
            {
                float thumbHeight = _scrollbarThumb != null
                    ? _scrollbarThumb.rect.height
                    : ScrollbarMinThumbHeight;
                SetScrollFromThumbTop(pointerFromTop - thumbHeight * 0.5f, snap: false);
            }
            return true;
        }

        if (!inputBusy && VoiceUiKit.Contains(_shell.PaneClip))
        {
            if (Input.GetMouseButtonDown(0))
                _scrollTarget = _scroll;
            float delta = Input.mouseScrollDelta.y;
            if (Mathf.Abs(delta) > 0.01f)
                _scrollTarget = Mathf.Clamp(_scrollTarget - delta * RowH, 0f, MaxScroll());
        }
        return false;
    }

    private static bool TryGetScrollbarPointerFromTop(out float pointerFromTop)
    {
        pointerFromTop = 0f;
        if (_scrollbarRoot == null || !VoiceUiKit.LocalPoint(_scrollbarRoot, out var local))
            return false;
        pointerFromTop = _scrollbarRoot.rect.yMax - local.y;
        return true;
    }

    private static void SetScrollFromThumbTop(float thumbTop, bool snap)
    {
        if (_scrollbarRoot == null || _scrollbarThumb == null) return;
        _scrollTarget = VoiceSettingsScrollPolicy.ScrollFromThumbTop(
            thumbTop,
            MaxScroll(),
            _scrollbarRoot.rect.height,
            _scrollbarThumb.rect.height);
        if (!snap) return;
        _scroll = _scrollTarget;
        if (_shell != null)
            _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
    }

    private static void UpdateSmoothScroll(float unscaledDeltaTime)
    {
        if (_shell == null) return;
        float maxScroll = MaxScroll();
        _scrollTarget = VoiceSettingsScrollPolicy.Clamp(_scrollTarget, maxScroll);
        _scroll = VoiceSettingsScrollPolicy.Clamp(_scroll, maxScroll);

        if (!_scrollbarDragging)
            _scroll = VoiceSettingsScrollPolicy.Advance(
                _scroll,
                _scrollTarget,
                SmoothScrollRate,
                unscaledDeltaTime);

        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
    }

    private static void UpdateScrollbarVisual()
    {
        if (_scrollbarRoot == null || _scrollbarThumb == null) return;
        float maxScroll = MaxScroll();
        bool visible = maxScroll > 0.5f;
        if (_scrollbarRoot.gameObject.activeSelf != visible)
            _scrollbarRoot.gameObject.SetActive(visible);
        if (!visible)
        {
            CancelScrollbarDrag();
            return;
        }

        float trackHeight = Mathf.Max(1f, _scrollbarRoot.rect.height);
        float viewHeight = Mathf.Max(1f, ViewHeight());
        float thumbHeight = VoiceSettingsScrollPolicy.ThumbHeight(
            trackHeight,
            viewHeight,
            _contentHeight,
            ScrollbarMinThumbHeight);
        _scrollbarThumb.sizeDelta = new Vector2(18f, thumbHeight);
        float thumbTop = VoiceSettingsScrollPolicy.ThumbTopFromScroll(
            _scroll,
            maxScroll,
            trackHeight,
            thumbHeight);
        _scrollbarThumb.anchoredPosition = new Vector2(0f, -thumbTop);

        bool hover = VoiceUiKit.Contains(_scrollbarThumb) || VoiceUiKit.Contains(_scrollbarRoot);
        if (_scrollbarThumbImage != null)
        {
            Color target = _scrollbarDragging
                ? VoiceUiKit.Accent
                : hover ? VoiceUiKit.TextPrimary : VoiceUiKit.TextMuted;
            _scrollbarThumbImage.color = Color.Lerp(_scrollbarThumbImage.color, target, 0.24f);
        }
        if (_scrollbarTrackImage != null)
        {
            Color target = hover
                ? new Color32(38, 58, 78, 225)
                : new Color32(26, 39, 55, 205);
            _scrollbarTrackImage.color = Color.Lerp(_scrollbarTrackImage.color, target, 0.20f);
        }
    }

    private static void CancelScrollbarDrag()
    {
        _scrollbarDragging = false;
        _scrollbarDragOffset = 0f;
    }

    private static void HandleInput(bool pointerConsumed)
    {
        if (!Input.GetMouseButton(0))
        {
            if (_activeRow != null) { _activeRow.OnMouseUp(); _activeRow = null; }
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
            if (pointerConsumed) return;
            if (_shell == null || !VoiceUiKit.Contains(_shell.PaneClip)) return;
            for (int i = 0; i < _rows.Count; i++) _rows[i].OnMouseDown();
            _activeRow = FindDragging();
        }
        else if (_activeRow != null)
        {
            _activeRow.OnMouseDrag();
        }
    }

    private static VoiceUiKit.Row? FindDragging()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].IsDragging) return _rows[i];
        return null;
    }
}
