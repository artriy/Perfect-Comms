using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceVolumeMenu
{
    private const float PanelW = 920f;
    private const float PanelH = 740f;
    private const float PanelScale = 1f;
    private const float RowH = 88f;
    private const float TopPad = 8f;
    private const float RosterWidth = PanelW - 88f;
    private const float VMin = 0f;
    private const float VMax = 2f;

    private static VoiceUiKit.PanelShell? _shell;
    private static readonly List<VoiceUiKit.PlayerVolumeRow> _rows = new();
    private static VoiceUiKit.PlayerVolumeRow? _activeRow;
    private static float _scroll;
    private static readonly VoiceSettingsScrollPolicy.SmoothScroller _smoothScroll = new();
    private static float _contentHeight;
    private static float _animT;
    private static int _rosterSignature;
    private static bool _shown;
    private static RectTransform? _scrollbarRoot;
    private static RectTransform? _scrollbarThumb;
    private static UnityEngine.UI.Image? _scrollbarThumbImage;
    private static bool _scrollbarDragging;
    private static float _scrollbarDragOffset;

    private static readonly Dictionary<string, float> _savedVolumes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _savedVolumesLoaded;

    private static bool ShellAlive => _shell != null && _shell.Root != null;
    public static bool IsOpen => ShellAlive && _shown;

    public static void Toggle()
    {
        if (_shown) Hide();
        else Show();
    }

    public static void Show()
    {
        var privacyPhase = VoiceSceneState.ResolvePhase();
        var privacy = VoiceIdentityPrivacyRuntime.Current(
            VoiceOverlayState.Current(VoiceChatRoom.Current),
            privacyPhase);
        if (privacy.HideAllForViewer || privacy.DimAll)
            return;

        VoiceSettingsPanel.ForceClose();
        HostSettingsPanel.ForceClose();
        VoiceLobbyBrowserUi.ForceClose();
        CancelDrag();

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
        _shell.PaneRoot.anchoredPosition = Vector2.zero;
        _animT = 0f;
        ApplyOpenAnim();
        _shown = true;
        VoiceChatPatches.ReleaseHeldTransmitInputs();

        if (!rebuilt) RebuildRows();

        VoiceUiKit.RaiseAbove(_shell.RootRect);
    }

    public static void Hide()
    {
        _shown = false;
        _animT = 0f;
        CancelDrag();
        if (_shell != null && _shell.Root != null)
        {
            _shell.Group.alpha = 0f;
            _shell.Group.interactable = false;
            _shell.Group.blocksRaycasts = false;
            _shell.Root.SetActive(false);
        }
    }

    public static void ForceClose() => Hide();

    private static void HeaderClose()
    {
        VoiceUiKit.SwallowClick();
        Hide();
    }

    private static void Build()
    {
        _shell = new VoiceUiKit.PanelShell("VC_VolumeMenu", "Player volumes", PanelW, PanelH,
            HeaderClose, rail: false, backdrop: false, subtitle: "Player volumes");

        var scope = VoiceUiKit.Rect("PersonalMixScope", _shell.HeaderRect);
        scope.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
        scope.sizeDelta = new Vector2(172f, 36f);
        scope.anchoredPosition = new Vector2(-92f, 0f);
        var scopeText = VoiceUiKit.Text("Scope", scope, "Personal mix", 14f,
            VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Right);
        scopeText.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        scopeText.rectTransform.offsetMin = Vector2.zero;
        scopeText.rectTransform.offsetMax = Vector2.zero;

        var heading = VoiceUiKit.Text("MixHeading", _shell.RootRect, "Your mix", 26f,
            VoiceUiKit.TextBright, TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);
        heading.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        heading.rectTransform.sizeDelta = new Vector2(300f, 40f);
        heading.rectTransform.anchoredPosition = new Vector2(32f, -116f);
        var range = VoiceUiKit.Text("VolumeRange", _shell.RootRect, "100% normal   /   0-200%", 13f,
            VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Right);
        range.rectTransform.Anchor(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f));
        range.rectTransform.sizeDelta = new Vector2(320f, 32f);
        range.rectTransform.anchoredPosition = new Vector2(-32f, -124f);
        var description = VoiceUiKit.Text("MixDescription", _shell.RootRect,
            "Adjust each player without changing lobby voice rules.", 15f,
            VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Left);
        description.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f));
        description.rectTransform.sizeDelta = new Vector2(-64f, 28f);
        description.rectTransform.anchoredPosition = new Vector2(32f, -160f);

        _shell.PaneClip.offsetMin = new Vector2(32f, 80f);
        _shell.PaneClip.offsetMax = new Vector2(-32f, -200f);
        _shell.PaneRoot.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        _shell.PaneRoot.sizeDelta = new Vector2(RosterWidth, 0f);

        var divider = VoiceUiKit.Panel("HelperDivider", _shell.RootRect, VoiceUiKit.Divider, false);
        divider.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f));
        divider.rectTransform.sizeDelta = new Vector2(-64f, 1f);
        divider.rectTransform.anchoredPosition = new Vector2(0f, 68f);
        var helper = VoiceUiKit.Text("PersonalMixNote", _shell.RootRect,
            "Only changes what you hear", 16f, VoiceUiKit.TextPrimary,
            TMPro.TextAlignmentOptions.Left, TMPro.FontStyles.Bold);
        helper.rectTransform.Anchor(Vector2.zero, new Vector2(1f, 0f), new Vector2(0f, 0f));
        helper.rectTransform.sizeDelta = new Vector2(-64f, 28f);
        helper.rectTransform.anchoredPosition = new Vector2(32f, 36f);
        var resetNote = VoiceUiKit.Text("ResetNote", _shell.RootRect,
            "Use the reset arrow to return a player to 100%.", 13f,
            VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Left);
        resetNote.rectTransform.Anchor(Vector2.zero, new Vector2(1f, 0f), new Vector2(0f, 0f));
        resetNote.rectTransform.sizeDelta = new Vector2(-64f, 24f);
        resetNote.rectTransform.anchoredPosition = new Vector2(32f, 12f);

        BuildScrollbar();
        RebuildRows();
    }

    private static void Destroy()
    {
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
        _rows.Clear();
        _scrollbarRoot = null;
        _scrollbarThumb = null;
        _scrollbarThumbImage = null;
        _scroll = 0f;
        _shown = false;
        _rosterSignature = 0;
    }

    private static void RebuildRows(List<PlayerEntry>? players = null)
    {
        if (_shell == null) return;
        int anchorIndex = _rows.Count == 0 ? -1
            : Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, _scroll - TopPad) / RowH), 0, _rows.Count - 1);
        byte anchorPlayerId = anchorIndex < 0 ? byte.MaxValue : _rows[anchorIndex].PlayerId;
        float anchorOffset = anchorIndex < 0 ? 0f : _scroll - anchorIndex * RowH;
        CancelDrag();
        for (int i = _shell.PaneRoot.childCount - 1; i >= 0; i--)
        {
            var child = _shell.PaneRoot.GetChild(i).gameObject;
            child.SetActive(false);
            Object.Destroy(child);
        }
        _rows.Clear();

        players ??= CollectPlayers();
        _rosterSignature = ComputeRosterSignature(players);

        float y = -TopPad;
        for (int i = 0; i < players.Count; i++)
        {
            var entry = players[i];
            float current = GetSavedVolume(entry);
            var pc = FindPlayerControl(entry.PlayerId);

            var row = new VoiceUiKit.PlayerVolumeRow(
                () => current,
                v => current = ApplyVolume(entry, v, persist: false),
                () => SaveVolume(entry, current),
                pc, VMin, VMax)
                .Build(_shell.PaneRoot, entry.Name, RosterWidth, y, RowH);
            row.PlayerId = entry.PlayerId;
            _rows.Add(row);
            if (entry.PlayerId == anchorPlayerId)
                _scroll = i * RowH + anchorOffset;
            y -= RowH;
        }

        _contentHeight = players.Count == 0 ? 0f : -y + TopPad;
        ApplyScroll();

        if (players.Count == 0)
        {
            var empty = VoiceUiKit.Text("Empty", _shell.PaneRoot, "No other players in the room yet", 22f,
                VoiceUiKit.TextPrimary, TMPro.TextAlignmentOptions.Center, TMPro.FontStyles.Bold);
            empty.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            empty.rectTransform.sizeDelta = new Vector2(0f, 40f);
            empty.rectTransform.anchoredPosition = new Vector2(0f, -144f);
            var emptyNote = VoiceUiKit.Text("EmptyNote", _shell.PaneRoot,
                "Player volume controls appear here when someone joins.", 15f,
                VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Center);
            emptyNote.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            emptyNote.rectTransform.sizeDelta = new Vector2(0f, 28f);
            emptyNote.rectTransform.anchoredPosition = new Vector2(0f, -192f);
        }
    }

    public static void Tick()
    {
        if (_shell == null) return;
        if (_shell.Root == null) { Destroy(); return; }
        if (!_shown) return;

        var privacyPhase = VoiceSceneState.ResolvePhase();
        var privacy = VoiceIdentityPrivacyRuntime.Current(
            VoiceOverlayState.Current(VoiceChatRoom.Current),
            privacyPhase);
        if (privacy.HideAllForViewer || privacy.DimAll)
        {
            FeedMeters(privacy, privacyPhase);
            Hide();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
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
        bool pointerConsumed = HandleScroll();
        _scroll = _smoothScroll.Step(_scroll, MaxScroll(), Time.unscaledDeltaTime);
        ApplyScroll();
        HandleInput(pointerConsumed);

        FeedMeters(privacy, privacyPhase);
        for (int i = 0; i < _rows.Count; i++) _rows[i].Tick(dt);

        if (Time.frameCount % 30 == 0) RefreshRosterIfChanged();
    }

    private static void FeedMeters(
        VoiceIdentityPrivacyFrame privacy,
        VoiceGamePhase privacyPhase)
    {
        if (_rows.Count == 0) return;
        if (privacy.HideAllForViewer || privacy.DimAll)
        {
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].ClearLevelImmediately();
            return;
        }

        var speakers = privacy.Speakers;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            float level = 0f;
            bool speaking = false;
            for (int j = 0; j < speakers.Count; j++)
            {
                var speaker = speakers[j];
                if (speaker.PresentationPlayerId == row.PlayerId)
                {
                    level = speaker.Level;
                    speaking = true;
                    break;
                }
            }
            if (speaking)
            {
                row.SetLevel(level, true);
                continue;
            }

            // Preserve the ordinary release animation when a speaker merely stops talking. If a
            // visible identity became concealed or was remapped, snap the stale meter off instead.
            // A currently active alias collision is protected by the speaking branch above.
            bool snapForPrivacy = VoiceIdentityPrivacyRuntime.ShouldSnapPresentation(row.PlayerId);
            if (!snapForPrivacy && row.HasVisibleMeter)
            {
                var current = VoiceIdentityPrivacyRuntime.Peek(row.PlayerId, privacyPhase);
                snapForPrivacy = !current.HasConcretePresentation
                                 || current.PresentationPlayerId != row.PlayerId;
            }

            if (snapForPrivacy)
                row.ClearLevelImmediately();
            else
                row.SetLevel(0f, false);
        }
    }

    private static void ApplyOpenAnim()
    {
        if (_shell == null) return;
        Rect canvas = VoiceUiKit.CanvasRect.rect;
        float fit = Mathf.Min(PanelScale,
            Mathf.Min(Mathf.Max(0f, canvas.width - 64f) / PanelW,
                Mathf.Max(0f, canvas.height - 64f) / PanelH));
        float scale = fit * Mathf.Lerp(0.96f, 1f, 1f - Mathf.Pow(1f - _animT, 3f));
        _shell.RootRect.localScale = new Vector3(scale, scale, 1f);
        _shell.SetLayoutOffset(Vector2.zero);
        _shell.Group.alpha = Mathf.Clamp01(_animT / 0.6f);
    }

    private static float MaxScroll()
        => _shell == null ? 0f
            : VoiceSettingsScrollPolicy.MaxScroll(_contentHeight, _shell.PaneClip.rect.height);

    private static void ApplyScroll()
    {
        if (_shell == null) return;
        _scroll = Mathf.Clamp(_scroll, 0f, MaxScroll());
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
        UpdateScrollbarVisual();
    }

    private static void BuildScrollbar()
    {
        if (_shell == null) return;
        _scrollbarRoot = VoiceUiKit.Rect("MixerScrollbar", _shell.PaneClip);
        _scrollbarRoot.Anchor(new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
        _scrollbarRoot.sizeDelta = new Vector2(20f, -16f);
        _scrollbarRoot.anchoredPosition = new Vector2(-10f, 0f);
        var track = VoiceUiKit.Panel("ScrollbarTrack", _scrollbarRoot, VoiceUiKit.TrackBg, true, true);
        track.rectTransform.Anchor(new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f));
        track.rectTransform.sizeDelta = new Vector2(4f, -4f);
        _scrollbarThumb = VoiceUiKit.Rect("ScrollbarThumbHit", _scrollbarRoot);
        _scrollbarThumb.Anchor(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));
        _scrollbarThumbImage = VoiceUiKit.Panel("ScrollbarThumb", _scrollbarThumb, VoiceUiKit.TextMuted, true, true);
        _scrollbarThumbImage.rectTransform.Anchor(new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f));
        _scrollbarThumbImage.rectTransform.sizeDelta = new Vector2(8f, -2f);
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
        float thumbHeight = VoiceSettingsScrollPolicy.ThumbHeight(
            trackHeight, _shell.PaneClip.rect.height, _contentHeight, 40f);
        _scrollbarThumb.sizeDelta = new Vector2(20f, thumbHeight);
        _scrollbarThumb.anchoredPosition = new Vector2(0f,
            -VoiceSettingsScrollPolicy.ThumbTopFromScroll(_scroll, maxScroll, trackHeight, thumbHeight));
        if (_scrollbarThumbImage != null)
            _scrollbarThumbImage.color = _scrollbarDragging ? VoiceUiKit.Accent
                : VoiceUiKit.Contains(_shell.PaneClip) && VoiceUiKit.Contains(_scrollbarRoot)
                    ? VoiceUiKit.TextPrimary : VoiceUiKit.TextMuted;
    }

    private static bool HandleScroll()
    {
        if (_shell == null || _scrollbarRoot == null || _scrollbarThumb == null) return false;
        if (!Input.GetMouseButton(0)) _scrollbarDragging = false;
        bool consumed = _scrollbarDragging;
        bool insideClip = VoiceUiKit.Contains(_shell.PaneClip);
        if (MaxScroll() > 0f && _activeRow == null && insideClip)
        {
            if (Input.GetMouseButtonDown(0) && VoiceUiKit.Contains(_scrollbarRoot)
                && VoiceUiKit.LocalPoint(_scrollbarRoot, out var down))
            {
                float fromTop = _scrollbarRoot.rect.yMax - down.y;
                _scrollbarDragOffset = VoiceUiKit.Contains(_scrollbarThumb)
                    ? fromTop + _scrollbarThumb.anchoredPosition.y
                    : _scrollbarThumb.rect.height * 0.5f;
                _scrollbarDragging = true;
                VoiceUiKit.RebindRow.CancelCaptureForExternalPointer();
                VoiceUiKit.SwallowClick();
            }
            if (_scrollbarDragging && VoiceUiKit.LocalPoint(_scrollbarRoot, out var pointer))
            {
                _scroll = VoiceSettingsScrollPolicy.ScrollFromThumbTop(
                    _scrollbarRoot.rect.yMax - pointer.y - _scrollbarDragOffset,
                    MaxScroll(), _scrollbarRoot.rect.height, _scrollbarThumb.rect.height);
                consumed = true;
            }
            else
                _smoothScroll.Scroll(_scroll, -Input.mouseScrollDelta.y * RowH, MaxScroll());
        }
        ApplyScroll();
        return consumed;
    }

    private static void CancelDrag()
    {
        _shell?.CancelDrag();
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
        if (_shell == null || !VoiceUiKit.Contains(_shell.PaneClip)) return;
        if (Input.GetMouseButtonDown(0))
        {
            if (pointerConsumed) return;
            for (int i = 0; i < _rows.Count; i++) _rows[i].OnMouseDown();
            _activeRow = FindDragging();
        }
        else if (_activeRow != null)
        {
            _activeRow.OnMouseDrag();
        }
    }

    private static VoiceUiKit.PlayerVolumeRow? FindDragging()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].IsDragging) return _rows[i];
        return null;
    }

    private static void RefreshRosterIfChanged()
    {
        var players = CollectPlayers();
        if (ComputeRosterSignature(players) != _rosterSignature)
            RebuildRows(players);
    }

    private static int ComputeRosterSignature(List<PlayerEntry> players)
    {
        int h = players.Count;
        for (int i = 0; i < players.Count; i++)
        {
            h = h * 31 + players[i].PlayerId;
            h = h * 31 + (players[i].Name?.GetHashCode() ?? 0);
        }
        return h;
    }

    private static float ApplyVolume(PlayerEntry entry, float v, bool persist)
    {
        v = Mathf.Clamp(v, VMin, VMax);
        if (persist) SaveVolume(entry, v);
        VoiceChatRoom.Current?.TrySetRemoteVolume(entry.PlayerId, entry.Name, v);
        return v;
    }

    private record PlayerEntry(byte PlayerId, string Name);

    private static float GetSavedVolume(PlayerEntry entry)
        => TryGetSavedVolume(entry.Name, out float volume) ? volume : 1f;

    internal static bool TryGetSavedVolume(string playerName, out float volume)
    {
        EnsureSavedVolumesLoaded();
        string key = GetVolumeKey(playerName);
        if (key.Length > 0 && _savedVolumes.TryGetValue(key, out volume))
        {
            volume = Mathf.Clamp(volume, VMin, VMax);
            return true;
        }

        volume = 1f;
        return false;
    }

    private static void SaveVolume(PlayerEntry entry, float volume)
    {
        EnsureSavedVolumesLoaded();
        string key = GetVolumeKey(entry.Name);
        if (key.Length == 0) return;

        if (Mathf.Abs(volume - 1f) < 0.005f)
            _savedVolumes.Remove(key);
        else
            _savedVolumes[key] = Mathf.Clamp(volume, VMin, VMax);

        PersistSavedVolumes();
    }

    private static void EnsureSavedVolumesLoaded()
    {
        if (_savedVolumesLoaded) return;
        var settings = VoiceSettings.Instance;
        if (settings == null) return;

        _savedVolumesLoaded = true;
        _savedVolumes.Clear();

        string raw = settings.PerPlayerVolumes.Value;
        foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = part.LastIndexOf('=');
            if (idx <= 0 || idx >= part.Length - 1) continue;

            string key = Uri.UnescapeDataString(part[..idx]);
            if (float.TryParse(part[(idx + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                _savedVolumes[GetVolumeKey(key)] = Mathf.Clamp(value, VMin, VMax);
        }
    }

    private static void PersistSavedVolumes()
    {
        var settings = VoiceSettings.Instance;
        if (settings == null) return;

        var parts = new List<string>();
        foreach (var kv in _savedVolumes)
        {
            string key = GetVolumeKey(kv.Key);
            if (key.Length == 0) continue;
            parts.Add($"{Uri.EscapeDataString(key)}={kv.Value.ToString("0.00", CultureInfo.InvariantCulture)}");
        }

        settings.PerPlayerVolumes.Value = string.Join(";", parts);
    }

    private static string GetVolumeKey(string name)
        => string.IsNullOrWhiteSpace(name) ? "" : name.Trim();

    private static List<PlayerEntry> CollectPlayers()
    {
        var list = new List<PlayerEntry>();
        if (AmongUsClient.Instance == null) return list;

        var seen = new HashSet<byte>();
        if (VoiceChatRoom.Current != null)
            foreach (var c in VoiceChatRoom.Current.RemoteOverlayStates)
            {
                if (c.PlayerId == byte.MaxValue) continue;
                if (!seen.Add(c.PlayerId)) continue;
                list.Add(new PlayerEntry(c.PlayerId, c.PlayerName));
            }

        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null || pc.Data == null) continue;
            if (pc == PlayerControl.LocalPlayer) continue;
            if (!seen.Add(pc.PlayerId)) continue;
            list.Add(new PlayerEntry(pc.PlayerId, pc.Data.PlayerName));
        }

        var snapshot = VoiceChatRoom.Current?.CurrentSnapshot;
        if (snapshot != null)
            list.RemoveAll(e => !VoiceOverlayState.IsLiveRemoteSpeaker(e.PlayerId, snapshot));

        return list;
    }

    private static PlayerControl? FindPlayerControl(byte playerId)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc != null && pc.PlayerId == playerId) return pc;
        }
        return null;
    }
}
