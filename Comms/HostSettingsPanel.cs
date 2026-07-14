using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class HostSettingsPanel
{
    private const float PanelW = 908f;
    private const float PanelH = 554f;
    private const float PanelScale = 1.3f;
    private const float RowH = 70f;
    private const float TopPad = 12f;

    private static readonly string[] BuiltInCategories =
        { "PROXIMITY", "LOBBY", "MEETING & VOICE", "TEAM RADIO", "TOU MIRA" };

    private static readonly (int index, string title)[] RailSections =
        { (4, "MOD BEHAVIOUR") };

    // Built-in tabs plus one tab per third-party mod (PerfectComms.Api Primitive 5), appended in
    // registration order under the "MOD BEHAVIOUR" section that already precedes index 4.
    private static string[] Categories
    {
        get
        {
            var tabs = VoiceModRegistry.Tabs;
            if (tabs.Count == 0) return BuiltInCategories;
            var list = new System.Collections.Generic.List<string>(BuiltInCategories);
            for (int i = 0; i < tabs.Count; i++) list.Add(tabs[i].Label.ToUpperInvariant());
            return list.ToArray();
        }
    }

    private static VoiceUiKit.PanelShell? _shell;
    private static VoiceUiKit.CategoryRail? _rail;
    private static readonly List<VoiceUiKit.Row> _rows = new();
    private static VoiceUiKit.Row? _activeRow;
    private static float _scroll;
    private static float _contentHeight;
    private static float _animT;
    private static bool _hostNotice;
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
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();

        if (!ShellAlive)
        {
            Destroy();
            _shell = new VoiceUiKit.PanelShell("VC_HostPanel", "HOST VOICE SETTINGS", PanelW, PanelH,
                () => { VoiceUiKit.SwallowClick(); Hide(); });
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

        BuildContent();

        VoiceUiKit.RaiseAbove(_shell.RootRect);
    }

    private static void BuildContent()
    {
        VoiceUiKit.RebindRow.CancelCapture();
        for (int i = _shell!.PaneRoot.childCount - 1; i >= 0; i--)
            Object.Destroy(_shell.PaneRoot.GetChild(i).gameObject);
        for (int i = _shell.RailRoot.childCount - 1; i >= 0; i--)
            Object.Destroy(_shell.RailRoot.GetChild(i).gameObject);
        _rows.Clear();
        _activeRow = null;
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
        _rail.Build(_shell.RailRoot, _shell.RailWidth, Categories, RailSections);
        _rail.OnSelect = _ => RebuildRows();
        RebuildRows();
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

    public static void ForceClose() => Hide();

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
        _hostNotice = false;
        _shown = false;
        _visSignature = int.MinValue;
    }

    private static void RebuildRows()
    {
        if (_shell == null || _rail == null) return;
        for (int i = _shell.PaneRoot.childCount - 1; i >= 0; i--)
            Object.Destroy(_shell.PaneRoot.GetChild(i).gameObject);
        _rows.Clear();
        _activeRow = null;
        _scroll = 0f;
        _shell.PaneRoot.anchoredPosition = Vector2.zero;

        var holders = CollectVisible(_rail.Selected);
        float y = -TopPad;
        for (int i = 0; i < holders.Count; i++)
        {
            var row = BuildRow(holders[i], y);
            if (row != null) _rows.Add(row);
            if (i < holders.Count - 1)
            {
                var div = VoiceUiKit.Panel("Div", _shell.PaneRoot, VoiceUiKit.Divider, false);
                div.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                div.rectTransform.sizeDelta = new Vector2(-20f, 1f);
                div.rectTransform.anchoredPosition = new Vector2(0f, y - RowH + 1f);
            }
            y -= RowH;
        }
        _contentHeight = TopPad + holders.Count * RowH;
        _visSignature = Signature(holders);
    }

    private static VoiceUiKit.Row? BuildRow(OptionHolder holder, float y)
    {
        float paneW = _shell!.PaneWidth;
        switch (holder)
        {
            case ToggleHolder t:
                return new VoiceUiKit.ToggleRow(() => t.Value, v => t.Value = v)
                    .Build(_shell.PaneRoot, t.Label, paneW, y, RowH, t.HelpText);
            case ModToggleHolder mt:
                return new VoiceUiKit.ToggleRow(() => mt.Value, v => mt.Value = v)
                    .Build(_shell.PaneRoot, mt.Label, paneW, y, RowH, mt.HelpText);
            case EnumHolder e:
                return new VoiceUiKit.StepperRow(
                    () => e.Value,
                    i => e.Value = i,
                    () => e.Labels.Length,
                    i => e.Labels[Mathf.Clamp(i, 0, e.Labels.Length - 1)])
                    .Build(_shell.PaneRoot, e.Label, paneW, y, RowH, e.HelpText);
            case ModEnumHolder me:
                return new VoiceUiKit.StepperRow(
                    () => me.Value,
                    i => me.Value = i,
                    () => me.Labels.Length,
                    i => me.Labels[Mathf.Clamp(i, 0, me.Labels.Length - 1)])
                    .Build(_shell.PaneRoot, me.Label, paneW, y, RowH, me.HelpText);
            case NumberHolder n:
                return new VoiceUiKit.SliderRow(
                    () => n.Value, v => n.Value = v, n.Min, n.Max,
                    v => $"<color=#22D3EE>{v.ToString(n.Format, CultureInfo.InvariantCulture)}</color>")
                    .Build(_shell.PaneRoot, n.Label, paneW, y, RowH, n.HelpText);
        }
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
        var r = VoiceRoleIntegrationOptions.Instance;
        return cat switch
        {
            0 => new List<OptionHolder>
            {
                g.MaxChatDistance, g.FalloffMode, g.OcclusionMode, g.WallsBlockSound,
                g.OnlyHearInSight, g.CameraCanHear
            },
            1 => new List<OptionHolder>
            {
                g.PublicVoiceLobby, g.LobbyBrowserBackend
            },
            2 => new List<OptionHolder>
            {
                g.GracePeriodEnabled, g.GracePeriodSeconds,
                g.HearInVent, g.VentPrivateChat, g.ImpostorHearGhosts, g.CommsSabDisables,
                g.OnlyGhostsCanTalk, g.GhostsHearEachOtherUnlimited, g.OnlyMeetingOrLobby,
                g.OnlyMeetingOrLobbyAffectsGhosts
            },
            3 => new List<OptionHolder>
            {
                g.TeamRadio, g.TeamRadioImpostors,
                g.TeamRadioInMeetings, g.TeamRadioInTasks
            },
            4 => new List<OptionHolder>
            {
                r.MuteBlackmailedInMeetings, r.MuteBlackmailedNextRound, r.MuteParasiteControlled,
                r.ParasiteHearFromVictim, r.MutePuppeteerControlled, r.PuppeteerHearFromVictim,
                r.MuteSwooperWhileSwooped, r.MuffleBlindedOrFlashedHearing, r.MuffleHypnotizedDuringHysteria,
                r.CrewpostorUsesImpostorVoice, r.MuteGlitchHacked, r.MuteJailedInMeetings,
                r.JailPersistsAfterJailorDeath, r.JailorCanUnmuteJailed, r.MediumGhostVoice,
                g.TeamRadioVampires, g.TeamRadioLovers
            },
            // Built-in tabs are indices 0..4; everything past that is a registered mod tab.
            _ => VoiceModRegistry.HoldersForTab(cat - BuiltInCategories.Length)
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
        if (_hostNotice) return;

        _rail!.Tick();
        HandleScroll();
        HandleInput();
        for (int i = 0; i < _rows.Count; i++) _rows[i].Tick(dt);

        if (Time.frameCount % 20 == 0) RefreshVisibilityIfChanged();
    }

    private static void RefreshVisibilityIfChanged()
    {
        if (_rail == null) return;
        var holders = CollectVisible(_rail.Selected);
        if (Signature(holders) == _visSignature) return;
        RebuildRows();
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
