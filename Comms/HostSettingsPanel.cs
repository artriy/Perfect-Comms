using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class HostSettingsPanel
{
    private const float PanelW = 1180f;
    private const float PanelH = 780f;
    private const float PanelScale = 1f;
    private const float RowH = 96f;
    private const float TopPad = 8f;
    private const float SectionH = 40f;
    private const float GroupGap = 16f;

    private static readonly string[] BuiltInCategories =
        { "Proximity", "Lobby", "Voice rules", "Team radio" };

    private const int BuiltInCategoryCount = 4;

    private static readonly (int index, string title)[] RailSections =
        { (4, "Mod behaviour") };

    // Built-in tabs plus one tab per third-party mod (PerfectComms.Api Primitive 5), appended in
    // registration order under the "Mod behaviour" section that already precedes index 4.
    private static string[] Categories
    {
        get
        {
            var tabs = VoiceModRegistry.Tabs;
            int builtInCount = BuiltInCategoryCount;
            var list = new System.Collections.Generic.List<string>(builtInCount + tabs.Count);
            for (int i = 0; i < builtInCount; i++) list.Add(BuiltInCategories[i]);
            for (int i = 0; i < tabs.Count; i++) list.Add(tabs[i].Label);
            return list.ToArray();
        }
    }

    private static VoiceUiKit.PanelShell? _shell;
    private static VoiceUiKit.CategoryRail? _rail;
    private static readonly List<VoiceUiKit.Row> _rows = new();
    private static VoiceUiKit.Row? _activeRow;
    private static float _scroll;
    private static readonly VoiceSettingsScrollPolicy.SmoothScroller _smoothScroll = new();
    private static float _contentHeight;
    private static float _animT;
    private static bool _hostNotice;
    private static int _visSignature = int.MinValue;
    private static int _categorySignature = int.MinValue;
    private static RectTransform? _scrollbarRoot;
    private static RectTransform? _scrollbarThumb;
    private static UnityEngine.UI.Image? _scrollbarThumbImage;
    private static bool _scrollbarDragging;
    private static float _scrollbarDragOffset;

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
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

        VoiceSettingsPanel.ForceClose();
        VoiceVolumeMenu.ForceClose();
        VoiceLobbyBrowserUi.ForceClose();

        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();

        if (!ShellAlive)
        {
            Destroy();
            _shell = new VoiceUiKit.PanelShell("VC_HostPanel", "Host voice settings", PanelW, PanelH,
                () => { VoiceUiKit.SwallowClick(); Hide(); }, hostSettings: true);
        }

        _shell!.Root.SetActive(true);
        _shell.Group.alpha = 1f;
        _shell.Group.interactable = true;
        _shell.Group.blocksRaycasts = true;
        _scroll = 0f;
        _shell.PaneRoot.anchoredPosition = Vector2.zero;
        _animT = 0f;
        ApplyOpenAnim();
        _shown = true;
        VoiceChatPatches.ReleaseHeldTransmitInputs();

        BuildContent();

        VoiceUiKit.RaiseAbove(_shell.RootRect);
    }

    private static void BuildContent()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        CancelDrag();
        ClearChildren(_shell!.PaneRoot);
        ClearChildren(_shell.RailRoot);
        _rows.Clear();
        _rail = null;
        _visSignature = int.MinValue;

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
        {
            _hostNotice = true;
            var notice = VoiceUiKit.Text("HostOnly", _shell.PaneRoot,
                "<b>Host only</b>\n<size=80%><color=#8C9CB2>You must be the lobby host to change these options.</color></size>",
                26f, VoiceUiKit.TextPrimary, TMPro.TextAlignmentOptions.Center);
            notice.enableWordWrapping = true;
            notice.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            notice.rectTransform.sizeDelta = new Vector2(-60f, 160f);
            notice.rectTransform.anchoredPosition = new Vector2(0f, -_shell.PaneHeight * 0.4f);
            return;
        }

        _hostNotice = false;
        _rail = new VoiceUiKit.CategoryRail();
        _categorySignature = CategorySignature();
        _rail.Build(_shell.RailRoot, _shell.RailWidth, Categories, RailSections, scrollable: true);
        _rail.OnSelect = _ => RebuildRows(true);
        if (_scrollbarRoot == null) BuildScrollbar();
        RebuildRows(true);
    }

    public static void Hide()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        CancelDrag();
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

    public static void ForceClose() => Hide();

    internal static void RevokeForHostAuthorityChange(int newHostClientId, int localClientId)
    {
        if (!IsOpen || (newHostClientId >= 0 && newHostClientId == localClientId)) return;
        Hide();
        VoiceChatHudState.ShowCompactStatus("Host voice settings closed: host changed");
        try
        {
            VoiceDiagnostics.Log(
                "voice.ui.host_panel_revoked",
                $"newHost={newHostClientId} localClient={localClientId}");
        }
        catch { }
    }

    private static void Destroy()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        CancelDrag();
        if (_shell != null)
        {
            if (_shell.Root != null)
            {
                _shell.Root.SetActive(false);
                Object.Destroy(_shell.Root);
            }
            _shell = null;
        }
        _rail = null;
        _rows.Clear();
        _activeRow = null;
        _scroll = 0f;
        _hostNotice = false;
        _shown = false;
        _visSignature = int.MinValue;
        _categorySignature = int.MinValue;
        _scrollbarRoot = null;
        _scrollbarThumb = null;
        _scrollbarThumbImage = null;
    }

    private static void ClearChildren(RectTransform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i).gameObject;
            child.SetActive(false);
            Object.Destroy(child);
        }
    }

    private static void RebuildRows(bool resetScroll = false)
    {
        if (_shell == null || _rail == null) return;
        if (RefreshCategoriesIfChanged()) return;
        CancelDrag();
        ClearChildren(_shell.PaneRoot);
        _rows.Clear();
        if (resetScroll) _scroll = 0f;

        int category = _rail.Selected;
        var holders = CollectVisible(category);
        string title = category < BuiltInCategoryCount
            ? BuiltInCategories[category]
            : VoiceModRegistry.Tabs[category - BuiltInCategoryCount].Label;
        string subtitle = category switch
        {
            0 => "Set how distance, walls and vision shape nearby voice.",
            1 => "Choose whether other players can discover this voice lobby.",
            2 => "Set who can speak during tasks, meetings and after death.",
            3 => "Choose when private team channels are available.",
            _ => "Lobby-wide voice rules supplied by this mod."
        };
        _shell.SetPage(title, subtitle);

        float y = -TopPad;
        string? previousGroup = null;
        RectTransform? group = null;
        float groupTop = 0f;
        float groupHeight = 0f;
        for (int i = 0; i < holders.Count; i++)
        {
            var holder = holders[i];
            string heading = GroupFor(holder, category);
            if (heading != previousGroup)
            {
                if (group != null)
                {
                    group.sizeDelta = new Vector2(-24f, groupHeight);
                    y -= GroupGap;
                }
                VoiceUiKit.SectionHeader(heading, _shell.PaneRoot, heading,
                    _shell.PaneWidth - 24f, y, SectionH);
                y -= SectionH;
                var frame = VoiceUiKit.Panel("RuleGroup", _shell.PaneRoot, VoiceUiKit.Divider);
                group = frame.rectTransform;
                group.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                group.anchoredPosition = new Vector2(-12f, y);
                var fill = VoiceUiKit.Panel("Fill", frame.transform, VoiceUiKit.PanelInner);
                fill.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                fill.rectTransform.offsetMin = Vector2.one;
                fill.rectTransform.offsetMax = -Vector2.one;
                groupTop = y;
                groupHeight = 0f;
                previousGroup = heading;
            }
            float indent = DependencyIndent(holder);
            var row = BuildRow(holder, group!, _shell.PaneWidth - 24f - indent, y - groupTop);
            if (row != null)
            {
                row.Root.sizeDelta = new Vector2(-indent, RowH);
                row.Root.anchoredPosition = new Vector2(indent * 0.5f, y - groupTop);
                row.ShowDescription(SummaryFor(holder), expanded: true);
                _rows.Add(row);
            }
            if (i < holders.Count - 1 && GroupFor(holders[i + 1], category) == heading)
            {
                var div = VoiceUiKit.Panel("Div", group!, VoiceUiKit.Divider, false);
                div.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                div.rectTransform.sizeDelta = new Vector2(-20f, 1f);
                div.rectTransform.anchoredPosition = new Vector2(0f, y - groupTop - RowH + 1f);
            }
            y -= RowH;
            groupHeight += RowH;
        }
        if (group != null) group.sizeDelta = new Vector2(-24f, groupHeight);
        if (holders.Count == 0)
        {
            var empty = VoiceUiKit.Text("NoVisibleRules", _shell.PaneRoot,
                "No rules available\n<size=80%>This category has no visible voice options right now.</size>",
                22f, VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Center);
            empty.enableWordWrapping = true;
            empty.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            empty.rectTransform.sizeDelta = new Vector2(-48f, 120f);
            empty.rectTransform.anchoredPosition = new Vector2(-12f, -48f);
            y = -176f;
        }
        _contentHeight = -y + TopPad;
        _scroll = Mathf.Clamp(_scroll, 0f, MaxScroll());
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
        _visSignature = Signature(holders);
    }

    private static VoiceUiKit.Row? BuildRow(OptionHolder holder, RectTransform parent, float paneW, float y)
    {
        switch (holder)
        {
            case ToggleHolder t:
                return new VoiceUiKit.ToggleRow(() => t.Value, v => t.Value = v)
                    .Build(parent, t.Label, paneW, y, RowH, t.HelpText);
            case ModToggleHolder mt:
                return new VoiceUiKit.ToggleRow(() => mt.Value, v => mt.Value = v)
                    .Build(parent, mt.Label, paneW, y, RowH, mt.HelpText);
            case EnumHolder e:
                return new VoiceUiKit.StepperRow(
                    () => e.Value,
                    i => e.Value = i,
                    () => e.Labels.Length,
                    i => e.Labels[Mathf.Clamp(i, 0, e.Labels.Length - 1)])
                    .Build(parent, e.Label, paneW, y, RowH, e.HelpText);
            case ModEnumHolder me:
                return new VoiceUiKit.StepperRow(
                    () => me.Value,
                    i => me.Value = i,
                    () => me.Labels.Length,
                    i => me.Labels[Mathf.Clamp(i, 0, me.Labels.Length - 1)])
                    .Build(parent, me.Label, paneW, y, RowH, me.HelpText);
            case NumberHolder n:
                return new VoiceUiKit.SliderRow(
                    () => n.Value, v => n.Value = v, n.Min, n.Max,
                    v => v.ToString(n.Format, CultureInfo.InvariantCulture))
                    .Build(parent, n.Label, paneW, y, RowH, n.HelpText);
            case ModNumberHolder mn:
                return new VoiceUiKit.SliderRow(
                    () => mn.Value, v => mn.Value = v, mn.Min, mn.Max,
                    v => v.ToString(mn.Format, CultureInfo.InvariantCulture))
                    .Build(parent, mn.Label, paneW, y, RowH, mn.HelpText);
        }
        return null;
    }

    private static string GroupFor(OptionHolder holder, int category)
    {
        var g = VoiceChatGameOptions.Instance;
        if (category == 0)
            return holder == g.MaxChatDistance || holder == g.FalloffMode ? "Hearing range" : "Walls & vision";
        if (category == 1) return "Lobby discovery";
        if (category == 3)
            return holder == g.TeamRadio || holder == g.TeamRadioImpostors ? "Private channels" : "When radio is available";
        if (category >= BuiltInCategoryCount) return "Mod rules";
        if (holder == g.GracePeriodEnabled || holder == g.GracePeriodSeconds) return "Meeting floor";
        if (holder == g.OnlyMeetingOrLobby || holder == g.MeetingOnlyImpostorChat ||
            holder == g.OnlyMeetingOrLobbyAffectsGhosts) return "Meetings & lobby";
        if (holder == g.HearInVent || holder == g.VentPrivateChat) return "Vent privacy";
        if (holder == g.CommsSabDisables) return "Communications sabotage";
        return "Ghosts & impostors";
    }

    private static float DependencyIndent(OptionHolder holder)
    {
        var g = VoiceChatGameOptions.Instance;
        if (holder == g.TeamRadioInTasks) return 40f;
        return holder == g.GracePeriodSeconds || holder == g.MeetingOnlyImpostorChat ||
            holder == g.OnlyMeetingOrLobbyAffectsGhosts || holder == g.ImpostorsTalkAcrossDeathInMeetings ||
            holder == g.TeamRadioImpostors || holder == g.TeamRadioInMeetings ? 24f : 0f;
    }

    private static string? SummaryFor(OptionHolder holder)
    {
        var g = VoiceChatGameOptions.Instance;
        if (holder == g.PublicVoiceLobby) return "List this voice-enabled lobby in the Perfect Comms live directory.";
        if (holder == g.MaxChatDistance) return "Maximum hearing distance during tasks.";
        if (holder == g.FalloffMode) return "How voice fades toward the edge of hearing range.";
        if (holder == g.OcclusionMode) return "How walls and lost line of sight affect nearby voices.";
        if (holder == g.WallsBlockSound) return "Let map walls obstruct voice using the selected occlusion mode.";
        if (holder == g.OnlyHearInSight) return "During tasks, hear only players inside your current vision range.";
        if (holder == g.CameraCanHear) return "Hear nearby voices around the active security camera.";
        if (holder == g.GracePeriodEnabled) return "Give the meeting caller an exclusive voice floor when a meeting starts.";
        if (holder == g.GracePeriodSeconds) return "Duration of the meeting caller's exclusive voice floor.";
        if (holder == g.OnlyMeetingOrLobby) return "Living players use normal voice only in meetings and lobbies.";
        if (holder == g.OnlyMeetingOrLobbyAffectsGhosts) return "Apply the meetings-and-lobby restriction to dead players too.";
        if (holder == g.MeetingOnlyImpostorChat)
            return "During tasks, actual impostors speak globally to impostors, ghosts and spectators. Dead impostor replies need Across Death.";
        if (holder == g.OnlyGhostsCanTalk) return "Restrict task-phase voice to dead players.";
        if (holder == g.GhostsHearLivingDuringTasks)
            return "During Tasks, ghosts hear nearby living speech despite task restrictions. Does not enable living-to-living speech.";
        if (holder == g.GhostsHearEachOtherUnlimited) return "Dead players hear each other anywhere on the map.";
        if (holder == g.ImpostorHearGhosts) return "Living impostors hear dead players when other ghost rules allow speech.";
        if (holder == g.ImpostorsTalkAcrossDeath)
            return "Dead impostors can reply to living impostors in task-phase impostor chat and radio. Crew ghosts and spectators only listen.";
        if (holder == g.ImpostorsTalkAcrossDeathInMeetings)
            return "Radio-held private meeting chat for living and dead actual impostors. Ghosts and spectators listen; living crew are excluded.";
        if (holder == g.HearInVent) return "Allow nearby players to hear an impostor inside a vent.";
        if (holder == g.VentPrivateChat) return "Keep vented speech private from players outside vents.";
        if (holder == g.CommsSabDisables) return "Disable normal voice while Communications sabotage is active.";
        if (holder == g.TeamRadio) return "Enable private hold-to-talk channels for eligible teams and roles.";
        if (holder == g.TeamRadioImpostors) return "Enable the private impostor channel while Team Radio is on.";
        if (holder == g.TeamRadioInMeetings) return "Allow eligible players to use team radio during meetings.";
        if (holder == g.TeamRadioInTasks) return "Allow eligible players to use team radio during tasks.";
        return null;
    }

    private static List<OptionHolder> CollectVisible(int cat)
    {
        var all = CategoryHolders(cat);
        var list = new List<OptionHolder>();
        foreach (var h in all)
            if (h != null && h.IsVisible) list.Add(h);
        return list;
    }

    private static List<OptionHolder> CategoryHolders(int cat)
    {
        var g = VoiceChatGameOptions.Instance;
        return cat switch
        {
            0 => new List<OptionHolder>
            {
                g.MaxChatDistance, g.FalloffMode, g.OcclusionMode, g.WallsBlockSound,
                g.OnlyHearInSight, g.CameraCanHear
            },
            1 => new List<OptionHolder>
            {
                g.PublicVoiceLobby
            },
            2 => new List<OptionHolder>
            {
                g.GracePeriodEnabled, g.GracePeriodSeconds,
                g.OnlyMeetingOrLobby, g.MeetingOnlyImpostorChat, g.OnlyMeetingOrLobbyAffectsGhosts,
                g.OnlyGhostsCanTalk, g.GhostsHearLivingDuringTasks, g.GhostsHearEachOtherUnlimited, g.ImpostorHearGhosts,
                g.ImpostorsTalkAcrossDeath, g.ImpostorsTalkAcrossDeathInMeetings,
                g.HearInVent, g.VentPrivateChat, g.CommsSabDisables
            },
            3 => new List<OptionHolder>
            {
                g.TeamRadio, g.TeamRadioImpostors,
                g.TeamRadioInMeetings, g.TeamRadioInTasks
            },
            // Registered mod tabs start immediately after the currently visible built-in tabs.
            _ => VoiceModRegistry.HoldersForTab(cat - BuiltInCategoryCount)
        };
    }

    private static int Signature(List<OptionHolder> holders)
    {
        int sig = holders.Count;
        for (int i = 0; i < holders.Count; i++) sig = sig * 31 + holders[i].Label.GetHashCode();
        return sig;
    }

    public static void Tick()
    {
        if (_shell == null) return;
        if (_shell.Root == null) { Destroy(); return; }
        if (!_shown) return;

        // Host migration can occur without a scene change. Revoke editing immediately instead of
        // leaving a stale host panel capable of mutating local options after authority moved.
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
        {
            RevokeForHostAuthorityChange(newHostClientId: -1, localClientId: -1);
            return;
        }

        if (!VoiceUiKit.RebindRow.IsCapturing && Input.GetKeyDown(KeyCode.Escape))
        {
            VoiceUiKit.SwallowClick();
            Hide();
            return;
        }

        float dt = Time.deltaTime;
        if (_animT < 1f)
            _animT = Mathf.Min(1f, _animT + dt / 0.22f);
        ApplyOpenAnim();

        _shell.TickHeader();
        if (_shell == null || !_shown) return;
        if (_hostNotice) return;

        _rail!.Tick();
        bool pointerConsumed = HandleScroll();
        UpdateSmoothScroll();
        HandleInput(pointerConsumed);
        for (int i = 0; i < _rows.Count; i++) _rows[i].Tick(dt);

        if (Time.frameCount % 20 == 0) RefreshVisibilityIfChanged();
    }

    private static void RefreshVisibilityIfChanged()
    {
        if (_rail == null) return;
        if (RefreshCategoriesIfChanged()) return;
        var holders = CollectVisible(_rail.Selected);
        if (Signature(holders) == _visSignature) return;
        RebuildRows();
    }

    private static bool RefreshCategoriesIfChanged()
    {
        if (CategorySignature() == _categorySignature) return false;
        BuildContent();
        return true;
    }

    private static int CategorySignature()
    {
        var tabs = VoiceModRegistry.Tabs;
        int signature = tabs.Count;
        for (int i = 0; i < tabs.Count; i++)
        {
            signature = signature * 31 + tabs[i].ModId.GetHashCode();
            signature = signature * 31 + tabs[i].Label.GetHashCode();
        }
        return signature;
    }

    private static void ApplyOpenAnim()
    {
        if (_shell == null) return;
        Rect canvas = VoiceUiKit.CanvasRect.rect;
        float fit = Mathf.Min(PanelScale,
            Mathf.Min(Mathf.Max(0f, canvas.width - 64f) / PanelW,
                Mathf.Max(0f, canvas.height - 64f) / PanelH));
        float scale = fit * Mathf.Min(1f, VoiceUiKit.AppearScale(_animT));
        _shell.RootRect.localScale = new Vector3(scale, scale, 1f);
        _shell.SetLayoutOffset(Vector2.zero);
        _shell.Group.alpha = Mathf.Clamp01(_animT / 0.6f);
    }

    private static float MaxScroll()
        => _shell == null ? 0f : VoiceSettingsScrollPolicy.MaxScroll(_contentHeight, _shell.PaneClip.rect.height);

    private static void BuildScrollbar()
    {
        if (_shell == null) return;
        _scrollbarRoot = VoiceUiKit.Rect("HostScrollbar", _shell.PaneClip);
        _scrollbarRoot.Anchor(new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
        _scrollbarRoot.sizeDelta = new Vector2(18f, -16f);
        _scrollbarRoot.anchoredPosition = new Vector2(-9f, 0f);
        var track = VoiceUiKit.Panel("ScrollbarTrack", _scrollbarRoot, new Color32(26, 39, 55, 205), true, true);
        track.rectTransform.Anchor(new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f));
        track.rectTransform.sizeDelta = new Vector2(4f, -4f);
        _scrollbarThumb = VoiceUiKit.Rect("ScrollbarThumbHit", _scrollbarRoot);
        _scrollbarThumb.Anchor(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));
        _scrollbarThumbImage = VoiceUiKit.Panel("ScrollbarThumb", _scrollbarThumb, VoiceUiKit.TextMuted, true, true);
        _scrollbarThumbImage.rectTransform.Anchor(new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f));
        _scrollbarThumbImage.rectTransform.sizeDelta = new Vector2(7f, -2f);
        _scrollbarRoot.SetAsLastSibling();
    }

    private static void UpdateScrollbarVisual()
    {
        if (_shell == null || _scrollbarRoot == null || _scrollbarThumb == null) return;
        float maxScroll = MaxScroll();
        bool visible = maxScroll > 0.5f;
        _scrollbarRoot.gameObject.SetActive(visible);
        if (!visible)
        {
            _scrollbarDragging = false;
            return;
        }
        float trackHeight = Mathf.Max(1f, _scrollbarRoot.rect.height);
        float thumbHeight = VoiceSettingsScrollPolicy.ThumbHeight(trackHeight,
            _shell.PaneClip.rect.height, _contentHeight, 30f);
        _scrollbarThumb.sizeDelta = new Vector2(18f, thumbHeight);
        _scrollbarThumb.anchoredPosition = new Vector2(0f,
            -VoiceSettingsScrollPolicy.ThumbTopFromScroll(_scroll, maxScroll, trackHeight, thumbHeight));
        if (_scrollbarThumbImage != null)
            _scrollbarThumbImage.color = _scrollbarDragging ? VoiceUiKit.Accent
                : VoiceUiKit.Contains(_scrollbarRoot) ? VoiceUiKit.TextPrimary : VoiceUiKit.TextMuted;
    }

    private static bool HandleScroll()
    {
        if (_shell == null || _scrollbarRoot == null || _scrollbarThumb == null) return false;
        bool consumed = false;
        if (!Input.GetMouseButton(0)) _scrollbarDragging = false;
        if (MaxScroll() > 0f && _activeRow == null)
        {
            if (Input.GetMouseButtonDown(0) && VoiceUiKit.Contains(_scrollbarRoot) &&
                VoiceUiKit.LocalPoint(_scrollbarRoot, out var down))
            {
                float fromTop = _scrollbarRoot.rect.yMax - down.y;
                _scrollbarDragOffset = VoiceUiKit.Contains(_scrollbarThumb)
                    ? fromTop + _scrollbarThumb.anchoredPosition.y
                    : _scrollbarThumb.rect.height * 0.5f;
                _scrollbarDragging = true;
            }
            if (_scrollbarDragging && VoiceUiKit.LocalPoint(_scrollbarRoot, out var pointer))
            {
                _scroll = VoiceSettingsScrollPolicy.ScrollFromThumbTop(
                    _scrollbarRoot.rect.yMax - pointer.y - _scrollbarDragOffset,
                    MaxScroll(), _scrollbarRoot.rect.height, _scrollbarThumb.rect.height);
                consumed = true;
            }
            else if (VoiceUiKit.Contains(_shell.PaneClip))
                _smoothScroll.Scroll(_scroll, -Input.mouseScrollDelta.y * RowH, MaxScroll());
        }
        _scroll = Mathf.Clamp(_scroll, 0f, MaxScroll());
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
        return consumed;
    }

    private static void UpdateSmoothScroll()
    {
        if (_shell == null) return;
        _scroll = _smoothScroll.Step(_scroll, MaxScroll(), Time.unscaledDeltaTime);
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
    }

    private static void CancelDrag()
    {
        _shell?.CancelDrag();
        _rail?.CancelDrag();
        _activeRow?.OnMouseUp();
        _activeRow = null;
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
