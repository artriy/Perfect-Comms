using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceSettingsPanel
{
    private const float PanelW = 908f;
    private const float PanelH = 554f;
    private const float PanelScale = 1.3f;
    private const float RowH = 72f;
    private const float HeaderH = 42f;
    private const float TopPad = 12f;

    private static readonly string[] Categories =
        { "AUDIO", "DEVICES", "KEYBINDS", "HUD", "ADVANCED" };

    private static VoiceUiKit.PanelShell? _shell;
    private static VoiceUiKit.CategoryRail? _rail;
    private static readonly List<VoiceUiKit.Row> _rows = new();
    private static VoiceUiKit.Row? _activeRow;
    private static float _scroll;
    private static float _contentHeight;
    private static float _animT;
    private static int _visSignature = int.MinValue;

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
        _shell.RootRect.localScale = Vector3.one * PanelScale;
        _scroll = 0f;
        _shell.PaneRoot.anchoredPosition = Vector2.zero;
        _animT = 0f;
        _shown = true;

        if (!rebuilt) RebuildRows(true);

        VoiceUiKit.RaiseAbove(_shell.RootRect);
    }

    private static void Build()
    {
        _shell = new VoiceUiKit.PanelShell("VC_SettingsPanel", "PERFECT COMMS", PanelW, PanelH, HeaderClose);
        _rail = new VoiceUiKit.CategoryRail();
        _rail.Build(_shell.RailRoot, _shell.RailWidth, Categories);
        _rail.OnSelect = _ => RebuildRows(true);
        RebuildRows(true);
    }

    public static void Hide()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        _shown = false;
        _animT = 0f;
        _activeRow = null;
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
        if (_shell != null)
        {
            if (_shell.Root != null) Object.Destroy(_shell.Root);
            _shell = null;
        }
        _rail = null;
        _rows.Clear();
        _activeRow = null;
        _scroll = 0f;
        _shown = false;
        _visSignature = int.MinValue;
    }

    private static void RebuildRows(bool resetScroll)
    {
        if (_shell == null) return;
        VoiceUiKit.RebindRow.CancelCapture();
        for (int i = _shell.PaneRoot.childCount - 1; i >= 0; i--)
            Object.Destroy(_shell.PaneRoot.GetChild(i).gameObject);
        _rows.Clear();
        _activeRow = null;

        var visible = CollectVisible(_rail!.Selected);

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
            y -= e.Height;
        }
        _contentHeight = -y;
        _visSignature = Signature(visible);

        ApplyScroll(resetScroll);

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
        return sig;
    }

    private static void ApplyScroll(bool reset)
    {
        float viewH = _shell!.PaneHeight - 24f;
        float maxScroll = Mathf.Max(0f, _contentHeight - viewH);
        _scroll = reset ? 0f : Mathf.Clamp(_scroll, 0f, maxScroll);
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
    }

    private static Func<float, string> Pct => v => $"<color=#22D3EE>{Mathf.RoundToInt(v * 100f)}%</color>";
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
        BepInEx.Configuration.ConfigEntry<float> entry, Func<float, string> fmt, Func<bool>? visible = null)
    {
        var range = GetRange(entry);
        defs.Add(new Entry
        {
            Key = label,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.SliderRow(
                () => entry.Value, v => entry.Value = v, range.x, range.y, fmt)
                .Build(pane, label, paneW, y, RowH)
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
                .Build(pane, label, paneW, y, RowH)
        });
    }

    private static void Toggle(List<Entry> defs, string label,
        Func<bool> get, Action<bool> set, Func<bool>? visible = null)
    {
        defs.Add(new Entry
        {
            Key = label,
            Visible = visible ?? Always,
            Build = (pane, paneW, y) => new VoiceUiKit.ToggleRow(get, set)
                .Build(pane, label, paneW, y, RowH)
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
                .Build(pane, label, paneW, y, RowH)
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
        Toggle(defs, "Noise Suppression", s.NoiseSuppressionEnabled);
        Toggle(defs, "Echo Cancellation", s.EchoCancellationEnabled);
        Toggle(defs, "Auto Mic Gain", s.AutoMicGain);
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
            Visible = Always,
            Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                () => (int)s.MicrophoneDeviceIndex.Value,
                i => s.MicrophoneDeviceIndex.Value = (MicDeviceEnum)i,
                () => VoiceChatLocalSettings.MicDeviceNames.Length,
                i => DeviceName(VoiceChatLocalSettings.MicDeviceNames, i))
                .Build(pane, "Microphone", paneW, y, RowH)
        });

#if WINDOWS
        defs.Add(new Entry
        {
            Key = "Speaker",
            Visible = Always,
            Build = (pane, paneW, y) => new VoiceUiKit.StepperRow(
                () => (int)s.SpeakerDeviceIndex.Value,
                i => s.SpeakerDeviceIndex.Value = (SpkDeviceEnum)i,
                () => VoiceChatLocalSettings.SpkDeviceNames.Length,
                i => DeviceName(VoiceChatLocalSettings.SpkDeviceNames, i))
                .Build(pane, "Speaker", paneW, y, RowH)
        });
#endif
    }

    private static string DeviceName(string[] names, int i)
    {
        if (names.Length == 0) return "<color=#607282>No devices found</color>";
        return names[Mathf.Clamp(i, 0, names.Length - 1)];
    }

    private static void Rebind(List<Entry> defs, VoiceKeybind bind)
    {
        defs.Add(new Entry
        {
            Key = bind.DisplayName,
            Visible = Always,
            Build = (pane, paneW, y) => new VoiceUiKit.RebindRow(
                () => bind.CurrentKey, k => bind.Set(k), () => bind.Clear())
                .Build(pane, bind.DisplayName, paneW, y, RowH)
        });
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
        Toggle(defs, "Fixed All Players", s.SpeakingBarFixedAllPlayers);
        Slider(defs, "Speaking Bar Scale", s.SpeakingBarScale, Num2);
        EnumStep(defs, "Speaking Bar Position", s.SpeakingBarPosition, new[]
        {
            "Top Left", "Top Middle", "Top Right", "Bottom Left", "Bottom Middle", "Bottom Right",
            "Middle Left", "Middle Right"
        });
        EnumStep(defs, "Speaking Bar Name Pos", s.SpeakingBarNamePosition, new[] { "Bottom", "Top", "Left", "Right" });
        Toggle(defs, "Speaking Bar Manual Layout", s.SpeakingBarManualLayout);
        EnumStep(defs, "Speaking Bar Layout", s.SpeakingBarLayout, new[] { "Vertical", "Horizontal" },
            () => s.SpeakingBarManualLayout.Value);
        Slider(defs, "Speaking Bar X", s.SpeakingBarX, Pct, () => s.SpeakingBarManualLayout.Value);
        Slider(defs, "Speaking Bar Y", s.SpeakingBarY, Pct, () => s.SpeakingBarManualLayout.Value);
        Toggle(defs, "Speaking Bar Backdrop", s.SpeakingBarBackdrop);

        Section(defs, "MEETING OVERLAY");
        Toggle(defs, "Meeting Speaking Overlay", s.MeetingSpeakingOverlay);

        Section(defs, "OTHER");
        EnumStep(defs, "Jail Unmute Placement", s.JailUnmuteButtonPlacement, new[] { "Voice HUD", "Meeting Card" });
    }

    private static void BuildAdvanced(List<Entry> defs, VoiceChatLocalSettings s)
    {
        Toggle(defs, "Nat Fix", s.NatFix);
        Toggle(defs, "Diagnostics",
            () => s.DebugVoiceStats.Value || s.MicCalibrationDiagnostics.Value,
            v => { s.DebugVoiceStats.Value = v; s.MicCalibrationDiagnostics.Value = v; });
    }

    private static Vector2 GetRange(BepInEx.Configuration.ConfigEntryBase entry)
    {
        var desc = entry.Description;
        if (desc?.AcceptableValues is BepInEx.Configuration.AcceptableValueRange<float> r)
            return new Vector2((float)r.MinValue, (float)r.MaxValue);
        return new Vector2(0f, 1f);
    }

    public static void Tick()
    {
        if (_shell == null) return;
        if (_shell.Root == null) { Destroy(); return; }
        if (!_shown) return;

        if (!VoiceUiKit.RebindRow.IsCapturing && Input.GetKeyDown(KeyCode.Escape))
        {
            VoiceUiKit.SwallowClick();
            Hide();
            return;
        }

        float dt = Time.deltaTime;
        if (_animT < 1f)
        {
            _animT = Mathf.Min(1f, _animT + dt / 0.22f);
            ApplyOpenAnim();
        }

        _shell.TickHeader();
        if (_shell == null) return;
        _rail!.Tick();
        HandleScroll();
        HandleInput();
        for (int i = 0; i < _rows.Count; i++) _rows[i].Tick(dt);

        if (Time.frameCount % 20 == 0) RefreshVisibilityIfChanged();
    }

    private static void RefreshVisibilityIfChanged()
    {
        if (_rail == null) return;
        var visible = CollectVisible(_rail.Selected);
        if (Signature(visible) == _visSignature) return;
        RebuildRows(false);
    }

    private static void ApplyOpenAnim()
    {
        if (_shell == null) return;
        float t = _animT;
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float eased = 1f + c3 * (t - 1f) * (t - 1f) * (t - 1f) + c1 * (t - 1f) * (t - 1f);
        float scale = Mathf.LerpUnclamped(0.6f, 1f, eased) * PanelScale;
        _shell.RootRect.localScale = new Vector3(scale, scale, 1f);
        _shell.Group.alpha = Mathf.Clamp01(t / 0.6f);
    }

    private static void HandleScroll()
    {
        if (_shell == null) return;
        float viewH = _shell.PaneHeight - 24f;
        float maxScroll = Mathf.Max(0f, _contentHeight - viewH);
        if (maxScroll <= 0f) return;
        if (!VoiceUiKit.Contains(_shell.PaneClip)) return;
        float dy = Input.mouseScrollDelta.y;
        if (dy > -0.01f && dy < 0.01f) return;
        _scroll = Mathf.Clamp(_scroll - dy * RowH, 0f, maxScroll);
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
    }

    private static void HandleInput()
    {
        if (!Input.GetMouseButton(0))
        {
            if (_activeRow != null) { _activeRow.OnMouseUp(); _activeRow = null; }
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
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
