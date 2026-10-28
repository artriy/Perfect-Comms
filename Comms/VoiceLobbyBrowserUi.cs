using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceLobbyBrowserUi
{
    private const int SortBase = 32740;
    private const float PanelWidth = 1120f;
    private const float PanelHeight = 760f;
    private const float RowHeight = 116f;
    private const float RowGap = 8f;
    private const float PanelAnimationSeconds = 0.18f;
    private static VoiceUiKit.PanelShell? _shell;
    private static RectTransform? _directoryRoot;
    private static RectTransform? _editorRoot;
    private static RectTransform? _listClip;
    private static RectTransform? _rowsRoot;
    private static RectTransform? _scrollbar;
    private static RectTransform? _scrollThumb;
    private static Image? _scrollThumbImage;
    private static TextMeshProUGUI? _statusText;
    private static TextMeshProUGUI? _emptyTitle;
    private static TextMeshProUGUI? _emptyDescription;
    private static TextMeshProUGUI? _titleValue;
    private static TextMeshProUGUI? _languageValue;
    private static TextMeshProUGUI? _titleCount;
    private static TextMeshProUGUI? _languageCount;
    private static Image? _titleFocus;
    private static Image? _languageFocus;
    private static RectTransform? _titleField;
    private static RectTransform? _languageField;
    private static VoiceUiKit.ActionButton? _refreshButton;
    private static VoiceUiKit.ActionButton? _infoButton;
    private static VoiceUiKit.ActionButton? _saveButton;
    private static VoiceUiKit.ActionButton? _cancelButton;
    private static MainMenuManager? _menu;
    private static GameObject? _buttonObj;
    private static GameObject? _buttonVisualObj;
    private static bool _buttonInputCached;
    private static bool _buttonVisualsPrepared;
    private static Sprite? _voiceButtonSprite;
    private static readonly List<ListingRow> _rows = new();
    private static readonly Dictionary<string, ListingRow> _rowByIdentity = new(StringComparer.OrdinalIgnoreCase);
    private static bool _panelVisible;
    private static bool _panelClosing;
    private static float _panelAnimation;
    private static bool _editorOpen;
    private static bool _editingLanguage;
    private static string _editTitle = "";
    private static string _editLanguage = "";
    private static DateTime _nextLiveAgeTickUtc = DateTime.MinValue;
    private static float _scroll;
    private static readonly VoiceSettingsScrollPolicy.SmoothScroller _smoothScroll = new();
    private static bool _scrollDragging;
    private static float _scrollDragOffset;
    private static int _lastUpdateFrame = -1;
    private static int _openedFrame = -1;

    internal static bool IsOpen => _shell != null && _shell.Root != null && (_panelVisible || _panelClosing);

    internal static void EnsureMainMenuButton(MainMenuManager menu)
    {
        if (menu.PlayOnlineButton == null) return;
        _menu = menu;
        if (_buttonObj == null)
        {
            _buttonObj = Object.Instantiate(menu.PlayOnlineButton.gameObject, menu.PlayOnlineButton.transform.parent);
            _buttonObj.name = "VC_LobbyBrowserButton";
            foreach (var text in _buttonObj.GetComponentsInChildren<TextMeshPro>(true))
                text.text = "";
            HideButtonRenderers(_buttonObj);
            _buttonVisualsPrepared = false;
            _buttonInputCached = false;
            var button = _buttonObj.GetComponent<PassiveButton>();
            button.OnClick = new ButtonClickedEvent();
            button.OnClick.AddListener((Action)TogglePanel);
        }

        _buttonObj.transform.SetParent(menu.PlayOnlineButton.transform.parent, false);
        _buttonObj.transform.localPosition = menu.PlayOnlineButton.transform.localPosition + new Vector3(1.62f, 0f, -60f);
        _buttonObj.transform.localScale = new Vector3(
            menu.PlayOnlineButton.transform.localScale.x * 0.28f,
            menu.PlayOnlineButton.transform.localScale.y,
            menu.PlayOnlineButton.transform.localScale.z);
        var passive = _buttonObj.GetComponent<PassiveButton>();
        if (!_buttonInputCached)
        {
            var colliders = _buttonObj.GetComponentsInChildren<Collider2D>(true);
            if (colliders.Length > 0)
            {
                var colliderArray = new Il2CppReferenceArray<Collider2D>(colliders.Length);
                for (int i = 0; i < colliders.Length; i++) colliderArray[i] = colliders[i];
                passive.ClickMask = colliders[0];
                passive.Colliders = colliderArray;
            }
            _buttonInputCached = true;
        }
        passive.enabled = true;
        passive.SetButtonEnableState(true);
        HideButtonRenderers(_buttonObj);
        if (!_buttonVisualsPrepared)
        {
            foreach (var renderer in _buttonObj.GetComponentsInChildren<SpriteRenderer>(true))
            {
                renderer.sortingLayerName = "UI";
                renderer.sortingOrder = SortBase + 5;
            }
            foreach (var text in _buttonObj.GetComponentsInChildren<TextMeshPro>(true))
            {
                text.sortingLayerID = SortingLayer.NameToID("UI");
                text.sortingOrder = SortBase + 6;
            }
            _buttonVisualsPrepared = true;
        }
        EnsureButtonArtVisual(menu);
        UpdateMainMenuButtonVisibility();
    }

    private static void HideButtonRenderers(GameObject button)
    {
        foreach (var renderer in button.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
        foreach (var renderer in button.GetComponentsInChildren<SpriteRenderer>(true))
            renderer.color = Color.clear;
    }

    private static void EnsureButtonArtVisual(MainMenuManager menu)
    {
        if (_buttonObj == null) return;
        if (_buttonVisualObj == null)
        {
            if (_voiceButtonSprite == null)
                _voiceButtonSprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserButton.png", highQuality: true);
            if (_voiceButtonSprite == null) return;
            _buttonVisualObj = new GameObject("VC_LobbyBrowserButtonArt");
            var renderer = _buttonVisualObj.AddComponent<SpriteRenderer>();
            renderer.sprite = _voiceButtonSprite;
            renderer.sortingLayerName = "UI";
            renderer.sortingOrder = SortBase + 7;
        }
        _buttonVisualObj.transform.SetParent(menu.PlayOnlineButton.transform.parent, false);
        _buttonVisualObj.transform.localScale = new Vector3(5.4f, 5.4f, 1f);
        _buttonVisualObj.transform.localPosition = _buttonObj.transform.localPosition + new Vector3(0f, 0.175f, -0.5f);
    }

    private static void UpdateMainMenuButtonVisibility()
    {
        bool visible = _menu != null && _menu.PlayOnlineButton != null &&
            _menu.PlayOnlineButton.gameObject.activeInHierarchy &&
            !VoiceUiKit.AnyPanelOpen && !VoiceFirstRunSetup.ShouldDeferMainMenuPopups;
        if (_buttonObj != null) _buttonObj.SetActive(visible);
        if (_buttonVisualObj != null) _buttonVisualObj.SetActive(visible);
    }

    internal static void Clear()
    {
        ForceClose();
        if (_buttonObj != null) Object.Destroy(_buttonObj);
        if (_buttonVisualObj != null) Object.Destroy(_buttonVisualObj);
        if (_shell != null && _shell.Root != null) Object.Destroy(_shell.Root);
        _buttonObj = null;
        _buttonVisualObj = null;
        _buttonInputCached = false;
        _buttonVisualsPrepared = false;
        _menu = null;
        _shell = null;
        _directoryRoot = null;
        _editorRoot = null;
        _listClip = null;
        _rowsRoot = null;
        _scrollbar = null;
        _scrollThumb = null;
        _scrollThumbImage = null;
        _statusText = null;
        _emptyTitle = null;
        _emptyDescription = null;
        _titleValue = null;
        _languageValue = null;
        _titleCount = null;
        _languageCount = null;
        _titleFocus = null;
        _languageFocus = null;
        _titleField = null;
        _languageField = null;
        _refreshButton = null;
        _infoButton = null;
        _saveButton = null;
        _cancelButton = null;
        _rows.Clear();
        _rowByIdentity.Clear();
    }

    internal static void ForceClose()
    {
        _panelVisible = false;
        _panelClosing = false;
        _panelAnimation = 0f;
        _editorOpen = false;
        _scrollDragging = false;
        _editTitle = "";
        _editLanguage = "";
        VoiceLobbyLiveBrowserClient.Disconnect();
        ClearRows();
        if (_shell != null && _shell.Root != null)
        {
            _shell.CancelDrag();
            _shell.Group.alpha = 0f;
            _shell.Group.interactable = false;
            _shell.Group.blocksRaycasts = false;
            _shell.Root.SetActive(false);
        }
    }

    internal static void OpenInfoEditor()
    {
        if (VoiceFirstRunSetup.ShouldDeferMainMenuPopups) return;
        var settings = VoiceSettings.Instance;
        _editTitle = settings?.LobbyBrowserTitle.Value ?? "Perfect Comms";
        _editLanguage = settings?.LobbyBrowserLanguage.Value ?? "English";
        _editingLanguage = false;
        _editorOpen = true;
        VoiceLobbyLiveBrowserClient.Disconnect();
        ShowPanelForContent();
        RenderEditor();
    }

    internal static void Update()
    {
        if (_lastUpdateFrame == Time.frameCount) return;
        _lastUpdateFrame = Time.frameCount;
        if (_shell != null && _shell.Root == null) Clear();
        if (_buttonObj != null && _menu == null)
        {
            Clear();
            return;
        }
        UpdateMainMenuButtonVisibility();
        float dt = Mathf.Max(0f, Time.unscaledDeltaTime);
        if (!IsOpen) return;
        if (VoiceFirstRunSetup.IsOpen)
        {
            ForceClose();
            return;
        }
        if (_panelVisible && !_editorOpen)
        {
            EnsureLiveDirectory();
            if (VoiceLobbyLiveBrowserClient.TryConsumeSnapshot(out var listings, out var status))
            {
                _nextLiveAgeTickUtc = DateTime.UtcNow.AddSeconds(1);
                RenderListings(VisibleListings(listings));
                bool connecting = status.StartsWith("Connecting", StringComparison.Ordinal)
                                  || status.StartsWith("Reconnecting", StringComparison.Ordinal);
                bool unavailable = status.StartsWith("Live directory", StringComparison.Ordinal);
                if (connecting || unavailable)
                {
                    SetStatus(status);
                    if (_rows.Count == 0)
                        SetEmptyState(connecting ? "Connecting to directory" : "Directory unavailable", status);
                }
            }
            else if (_rows.Count > 0 && DateTime.UtcNow >= _nextLiveAgeTickUtc)
            {
                _nextLiveAgeTickUtc = DateTime.UtcNow.AddSeconds(1);
                for (int i = 0; i < _rows.Count; i++) _rows[i].UpdateAge();
            }
        }
        AnimatePanel(dt);
        if (!_panelVisible || _shell == null || Time.frameCount <= _openedFrame) return;
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            VoiceUiKit.SwallowClick();
            if (_editorOpen) CancelEditor();
            else ClosePanel();
            return;
        }
        _shell.TickHeader();
        if (!_panelVisible) return;
        if (_editorOpen)
        {
            if (Input.GetMouseButtonDown(0))
            {
                if (_titleField != null && VoiceUiKit.Contains(_titleField)) _editingLanguage = false;
                else if (_languageField != null && VoiceUiKit.Contains(_languageField)) _editingLanguage = true;
            }
            UpdateEditorInput();
            if (!_editorOpen) return;
            UpdateEditorPresentation();
            _saveButton?.Tick(dt);
            if (_editorOpen) _cancelButton?.Tick(dt);
        }
        else
        {
            bool scrollbarOwnsPointer = UpdateScroll();
            _refreshButton?.Tick(dt);
            if (!_panelVisible || _editorOpen) return;
            _infoButton?.Tick(dt);
            if (!_panelVisible || _editorOpen) return;
            for (int i = 0; i < _rows.Count && _panelVisible; i++)
                _rows[i].Tick(dt, !scrollbarOwnsPointer);
        }
    }

    private static void TogglePanel()
    {
        if (IsOpen) { ClosePanel(); return; }
        if (VoiceFirstRunSetup.ShouldDeferMainMenuPopups) return;
        _editorOpen = false;
        OpenPanel(refresh: true);
    }

    private static void OpenPanel(bool refresh)
    {
        ShowPanelForContent();
        if (refresh) Refresh();
    }

    private static void ShowPanelForContent()
    {
        VoiceSettingsPanel.ForceClose();
        HostSettingsPanel.ForceClose();
        VoiceVolumeMenu.ForceClose();
        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();
        EnsurePanel();
        _panelVisible = true;
        _panelClosing = false;
        _openedFrame = Time.frameCount;
        _shell!.Root.SetActive(true);
        _shell.Group.interactable = true;
        _shell.Group.blocksRaycasts = true;
        VoiceUiKit.RaiseAbove(_shell.RootRect);
        VoiceChatPatches.ReleaseHeldTransmitInputs();
        VoiceUiKit.SwallowClick();
        ApplyPanelTransform();
        UpdateMainMenuButtonVisibility();
    }

    private static void ClosePanel()
    {
        VoiceUiKit.SwallowClick();
        _panelVisible = false;
        _editorOpen = false;
        _editTitle = "";
        _editLanguage = "";
        _scrollDragging = false;
        VoiceLobbyLiveBrowserClient.Disconnect();
        _panelClosing = _shell != null && _shell.Root != null;
        if (_shell != null && _shell.Root != null)
        {
            _shell.CancelDrag();
            _shell.Group.interactable = false;
        }
    }

    private static void AnimatePanel(float dt)
    {
        if (_shell == null) return;
        _panelAnimation = Mathf.MoveTowards(_panelAnimation, _panelVisible ? 1f : 0f, dt / PanelAnimationSeconds);
        ApplyPanelTransform();
        if (!_panelVisible && _panelAnimation <= 0f && !Input.GetMouseButton(0) && !Input.GetKey(KeyCode.Escape))
            ForceClose();
    }

    private static void ApplyPanelTransform()
    {
        if (_shell == null) return;
        Rect canvas = VoiceUiKit.CanvasRect.rect;
        float fit = Mathf.Min(1f, Mathf.Min(canvas.width / (PanelWidth + 64f), canvas.height / (PanelHeight + 64f)));
        float eased = 1f - Mathf.Pow(1f - Mathf.Clamp01(_panelAnimation), 3f);
        float scale = fit * Mathf.Lerp(0.97f, 1f, eased);
        _shell.RootRect.localScale = new Vector3(scale, scale, 1f);
        _shell.Group.alpha = eased;
        _shell.SetLayoutOffset(Vector2.zero);
    }

    private static void EnsurePanel()
    {
        if (_shell != null && _shell.Root != null) return;
        _shell = new VoiceUiKit.PanelShell("VC_LobbyBrowserPanel", "Voice lobbies", PanelWidth, PanelHeight,
            ClosePanel, rail: false, subtitle: "Public directory  /  Find a lobby using Perfect Comms");
        float w = _shell.PaneClip.rect.width;
        float h = _shell.PaneClip.rect.height;
        _directoryRoot = VoiceUiKit.Rect("Directory", _shell.PaneRoot);
        Place(_directoryRoot, 0f, 0f, w, h);
        Label("LobbyColumn", _directoryRoot, "Public voice lobbies", 12f, VoiceUiKit.TextFaint, 0f, 0f, 500f, 24f);
        _statusText = Label("DirectoryStatus", _directoryRoot, "Connecting to Perfect Comms live lobbies...", 14f,
            VoiceUiKit.TextMuted, 0f, 28f, w - 8f, 44f);
        _statusText.enableWordWrapping = true;
        _listClip = VoiceUiKit.Rect("LobbyViewport", _directoryRoot);
        Place(_listClip, 0f, 84f, w, h - 172f);
        _listClip.gameObject.AddComponent<RectMask2D>();
        _rowsRoot = VoiceUiKit.Rect("LobbyRows", _listClip);
        Place(_rowsRoot, 0f, 0f, w - 24f, 0f);
        _emptyTitle = Label("EmptyTitle", _listClip, "Connecting to directory", 24f, VoiceUiKit.TextPrimary,
            32f, 112f, w - 64f, 40f, FontStyles.Bold);
        _emptyTitle.alignment = TextAlignmentOptions.Center;
        _emptyDescription = Label("EmptyDescription", _listClip, "Waiting for the live lobby list.", 16f,
            VoiceUiKit.TextMuted, 80f, 164f, w - 160f, 96f);
        _emptyDescription.alignment = TextAlignmentOptions.Top;
        _emptyDescription.enableWordWrapping = true;
        _scrollbar = VoiceUiKit.Rect("DirectoryScrollbar", _listClip);
        Place(_scrollbar, w - 20f, 0f, 20f, h - 172f);
        var track = VoiceUiKit.Panel("Track", _scrollbar, VoiceUiKit.TrackBg);
        Place(track.rectTransform, 8f, 0f, 4f, h - 172f);
        _scrollThumb = VoiceUiKit.Rect("ThumbHit", _scrollbar);
        Place(_scrollThumb, 0f, 0f, 20f, 40f);
        _scrollThumbImage = VoiceUiKit.Panel("Thumb", _scrollThumb, VoiceUiKit.TextFaint);
        _scrollThumbImage.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _scrollThumbImage.rectTransform.offsetMin = new Vector2(6f, 2f);
        _scrollThumbImage.rectTransform.offsetMax = new Vector2(-6f, -2f);
        var footerLine = VoiceUiKit.Panel("DirectoryFooterDivider", _directoryRoot, VoiceUiKit.Divider, false);
        Place(footerLine.rectTransform, 0f, h - 72f, w, 1f);
        _refreshButton = new VoiceUiKit.ActionButton(_directoryRoot, "Refresh", 144f, 48f, 0f, -h + 56f,
            VoiceUiKit.ButtonKind.Secondary, () => Refresh());
        _infoButton = new VoiceUiKit.ActionButton(_directoryRoot, "Edit lobby info", 192f, 48f, w - 200f, -h + 56f,
            VoiceUiKit.ButtonKind.Secondary, OpenInfoEditor);
        Label("DirectoryNote", _directoryRoot, "Host a lobby to publish it in game settings.", 12f,
            VoiceUiKit.TextFaint, 160f, h - 52f, w - 368f, 40f);
        BuildEditor(w, h);
        _shell.Root.SetActive(false);
        UpdateScrollVisual();
    }

    private static void BuildEditor(float w, float h)
    {
        _editorRoot = VoiceUiKit.Rect("LobbyInfoEditor", _shell!.PaneRoot);
        Place(_editorRoot, 0f, 0f, w, h);
        Label("EditorHeading", _editorRoot, "Lobby info", 30f, VoiceUiKit.TextPrimary, 8f, 0f, w - 16f, 44f, FontStyles.Bold);
        var explanation = Label("PublishingScope", _editorRoot,
            "These details appear in the public voice directory when you host a lobby and enable Public Voice Lobby.",
            16f, VoiceUiKit.TextMuted, 8f, 56f, w - 40f, 64f);
        explanation.enableWordWrapping = true;
        Label("TitleLabel", _editorRoot, "Lobby title", 16f, VoiceUiKit.TextPrimary, 8f, 144f, w - 24f, 28f, FontStyles.Bold);
        _titleCount = Label("TitleCount", _editorRoot, "", 12f, VoiceUiKit.TextFaint, w - 120f, 144f, 112f, 28f);
        _titleCount.alignment = TextAlignmentOptions.Right;
        _titleField = BuildEditorField("LobbyTitle", 180f, w, out _titleFocus, out _titleValue);
        Label("LanguageLabel", _editorRoot, "Language", 16f, VoiceUiKit.TextPrimary, 8f, 272f, w - 24f, 28f, FontStyles.Bold);
        _languageCount = Label("LanguageCount", _editorRoot, "", 12f, VoiceUiKit.TextFaint, w - 120f, 272f, 112f, 28f);
        _languageCount.alignment = TextAlignmentOptions.Right;
        _languageField = BuildEditorField("LobbyLanguage", 308f, w, out _languageFocus, out _languageValue);
        Label("EditorKeyboardHint", _editorRoot, "Tab: switch field     Enter: save     Esc: cancel", 13f,
            VoiceUiKit.TextFaint, 8f, 392f, w - 16f, 28f);
        var line = VoiceUiKit.Panel("EditorFooterDivider", _editorRoot, VoiceUiKit.Divider, false);
        Place(line.rectTransform, 0f, h - 72f, w, 1f);
        Label("EditorSaveScope", _editorRoot, "Only Save changes your lobby info.", 13f, VoiceUiKit.TextMuted,
            8f, h - 56f, w - 344f, 48f);
        _cancelButton = new VoiceUiKit.ActionButton(_editorRoot, "Cancel", 144f, 48f, w - 320f, -h + 56f,
            VoiceUiKit.ButtonKind.Secondary, CancelEditor);
        _saveButton = new VoiceUiKit.ActionButton(_editorRoot, "Save", 152f, 48f, w - 160f, -h + 56f,
            VoiceUiKit.ButtonKind.Primary, SaveEditor);
        _editorRoot.gameObject.SetActive(false);
    }

    private static RectTransform BuildEditorField(string name, float y, float w, out Image focus, out TextMeshProUGUI value)
    {
        focus = VoiceUiKit.Panel(name + "Focus", _editorRoot!, VoiceUiKit.Divider).Round(VoiceUiKit.RadiusField);
        Place(focus.rectTransform, 8f, y, w - 16f, 64f);
        var inset = VoiceUiKit.Panel("Inset", focus.transform, VoiceUiKit.PanelOuter).Round(VoiceUiKit.RadiusField - 2f);
        Place(inset.rectTransform, 2f, 2f, w - 20f, 60f);
        value = Label(name + "Value", inset.rectTransform, "", 22f, VoiceUiKit.TextPrimary,
            18f, 12f, w - 64f, 36f);
        value.overflowMode = TextOverflowModes.Ellipsis;
        return focus.rectTransform;
    }

    private static void Refresh(bool showLoading = true)
    {
        EnsurePanel();
        _editorOpen = false;
        _directoryRoot!.gameObject.SetActive(true);
        _editorRoot!.gameObject.SetActive(false);
        if (showLoading)
        {
            ClearRows();
            SetEmptyState("Connecting to directory", "Waiting for the live lobby list.");
            SetStatus("Connecting to Perfect Comms live lobbies...");
        }
        EnsureLiveDirectory();
        VoiceLobbyLiveBrowserClient.RequestSnapshot();
    }

    private static void EnsureLiveDirectory()
    {
        var registryUrl = VoiceSettings.Instance?.LobbyRegistryUrl.Value
                          ?? VoiceLobbyRegistryEndpoint.DefaultRegistryUrl;
        VoiceLobbyLiveBrowserClient.EnsureConnected(registryUrl);
    }

    private static void RenderListings(IReadOnlyList<VoiceLobbyListing> listings)
    {
        if (_rowsRoot == null) return;
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        for (int i = 0; i < listings.Count; i++)
        {
            var listing = listings[i];
            string identity = ListingIdentity(listing);
            retained.Add(identity);
            if (!_rowByIdentity.TryGetValue(identity, out var row))
            {
                row = new ListingRow(_rowsRoot, _rowsRoot.rect.width);
                _rowByIdentity.Add(identity, row);
            }
            row.Bind(listing, i);
            _rows.Add(row);
        }
        var removed = new List<string>();
        foreach (var entry in _rowByIdentity)
        {
            if (retained.Contains(entry.Key)) continue;
            entry.Value.Root.gameObject.SetActive(false);
            Object.Destroy(entry.Value.Root.gameObject);
            removed.Add(entry.Key);
        }
        foreach (string identity in removed) _rowByIdentity.Remove(identity);
        _rowsRoot.sizeDelta = new Vector2(_rowsRoot.sizeDelta.x, ContentHeight);
        if (listings.Count == 0)
        {
            SetEmptyState("No public voice lobbies", "Host a lobby and enable Public Voice Lobby in game settings to appear here.");
            SetStatus("No public voice lobbies listed.");
        }
        else
        {
            _emptyTitle!.gameObject.SetActive(false);
            _emptyDescription!.gameObject.SetActive(false);
            SetStatus($"{listings.Count} public voice {(listings.Count == 1 ? "lobby" : "lobbies")}  /  Join availability shown on each row");
        }
        UpdateScrollVisual();
    }

    private static float ContentHeight => Mathf.Max(0f, _rows.Count * (RowHeight + RowGap) - RowGap);
    private static float MaxScroll => Mathf.Max(0f, ContentHeight - (_listClip?.rect.height ?? 0f));

    private static bool UpdateScroll()
    {
        if (_listClip == null || _scrollbar == null || _scrollThumb == null) return false;
        bool consumed = _scrollDragging;
        if (!Input.GetMouseButton(0)) _scrollDragging = false;
        if (MaxScroll > 0f && Input.GetMouseButtonDown(0) && VoiceUiKit.Contains(_scrollbar))
        {
            consumed = true;
            if (VoiceUiKit.LocalPoint(_scrollbar, out var local))
            {
                float pointer = _scrollbar.rect.yMax - local.y;
                float thumbTop = -_scrollThumb.anchoredPosition.y;
                _scrollDragOffset = VoiceUiKit.Contains(_scrollThumb) ? pointer - thumbTop : _scrollThumb.rect.height * 0.5f;
                _scrollDragging = true;
            }
        }
        if (_scrollDragging && VoiceUiKit.LocalPoint(_scrollbar, out var point))
        {
            float top = _scrollbar.rect.yMax - point.y - _scrollDragOffset;
            float travel = Mathf.Max(1f, _scrollbar.rect.height - _scrollThumb.rect.height);
            _scroll = Mathf.Clamp01(top / travel) * MaxScroll;
            consumed = true;
        }
        if (!_scrollDragging && VoiceUiKit.Contains(_listClip))
            _smoothScroll.Scroll(_scroll, -Input.mouseScrollDelta.y * 72f, MaxScroll);
        _scroll = _smoothScroll.Step(_scroll, MaxScroll, Time.unscaledDeltaTime);
        UpdateScrollVisual();
        return consumed;
    }

    private static void UpdateScrollVisual()
    {
        if (_rowsRoot == null || _listClip == null || _scrollbar == null || _scrollThumb == null) return;
        _scroll = Mathf.Clamp(_scroll, 0f, MaxScroll);
        _rowsRoot.anchoredPosition = new Vector2(0f, _scroll);
        bool visible = MaxScroll > 0.5f;
        _scrollbar.gameObject.SetActive(visible);
        if (!visible) { _scrollDragging = false; return; }
        float track = _scrollbar.rect.height;
        float thumb = Mathf.Clamp(track * _listClip.rect.height / ContentHeight, 40f, track);
        _scrollThumb.sizeDelta = new Vector2(20f, thumb);
        _scrollThumb.anchoredPosition = new Vector2(0f, -(track - thumb) * _scroll / MaxScroll);
        if (_scrollThumbImage != null)
            _scrollThumbImage.color = _scrollDragging ? VoiceUiKit.Accent
                : VoiceUiKit.Contains(_scrollbar) ? VoiceUiKit.TextPrimary : VoiceUiKit.TextFaint;
    }

    private sealed class ListingRow
    {
        internal readonly RectTransform Root;
        private readonly TextMeshProUGUI _title;
        private readonly TextMeshProUGUI _details;
        private readonly TextMeshProUGUI _slots;
        private readonly TextMeshProUGUI _age;
        private readonly TextMeshProUGUI _availability;
        private readonly VoiceUiKit.ActionButton _join;
        private VoiceLobbyListing _listing = null!;
        private bool _joinEnabled;

        internal ListingRow(RectTransform parent, float width)
        {
            var background = VoiceUiKit.Panel("LobbyRow", parent, VoiceUiKit.PanelInner);
            Root = background.rectTransform;
            Place(Root, 0f, 0f, width, RowHeight);
            _title = Label("LobbyTitle", Root, "", 22f, VoiceUiKit.TextPrimary, 20f, 12f, width - 340f, 34f, FontStyles.Bold);
            _title.overflowMode = TextOverflowModes.Ellipsis;
            _details = Label("HostRegionLanguageCode", Root, "", 13f, VoiceUiKit.TextMuted,
                20f, 52f, width - 340f, 24f);
            _details.overflowMode = TextOverflowModes.Ellipsis;
            _age = Label("LobbyStateAge", Root, "", 12f, VoiceUiKit.TextFaint, 20f, 84f, width - 340f, 22f);
            _slots = Label("PlayerSlots", Root, "", 20f, VoiceUiKit.TextPrimary, width - 304f, 20f, 112f, 30f, FontStyles.Bold);
            _slots.alignment = TextAlignmentOptions.Center;
            var players = Label("PlayersLabel", Root, "players", 10f, VoiceUiKit.TextFaint, width - 304f, 52f, 112f, 20f);
            players.alignment = TextAlignmentOptions.Center;
            _join = new VoiceUiKit.ActionButton(Root, "Join", 144f, 44f, width - 164f, -16f,
                VoiceUiKit.ButtonKind.Primary, () => JoinListing(_listing), () => _joinEnabled);
            _availability = Label("JoinAvailability", Root, "", 11f, VoiceUiKit.TextMuted, width - 180f, 72f, 176f, 28f);
            _availability.alignment = TextAlignmentOptions.Center;
            _availability.overflowMode = TextOverflowModes.Ellipsis;
        }

        internal void Bind(VoiceLobbyListing listing, int index)
        {
            _listing = listing;
            Root.anchoredPosition = new Vector2(0f, -index * (RowHeight + RowGap));
            Root.SetSiblingIndex(index);
            _title.text = string.IsNullOrWhiteSpace(listing.Title) ? "?" : listing.Title.Trim();
            string host = string.IsNullOrWhiteSpace(listing.Host) ? "Unknown" : listing.Host.Trim();
            var details = new List<string> { "Host: " + host };
            if (!string.IsNullOrWhiteSpace(listing.Region)) details.Add(listing.Region.Trim());
            if (!string.IsNullOrWhiteSpace(listing.Language)) details.Add(listing.Language.Trim());
            if (!string.IsNullOrWhiteSpace(listing.Code)) details.Add(listing.Code.Trim());
            _details.text = string.Join("  /  ", details);
            _slots.text = $"{listing.Players}/{listing.MaxPlayers}";
            string status = JoinStatus(listing);
            _joinEnabled = IsJoinable(listing) && !string.IsNullOrWhiteSpace(listing.Region);
            if (status == "JOIN" && string.IsNullOrWhiteSpace(listing.Code)) status = "NO CODE";
            else if (status == "JOIN" && string.IsNullOrWhiteSpace(listing.Region)) status = "NO REGION";
            _join.SetText(_joinEnabled ? "Join" : JoinStatusText(status));
            _availability.text = "";
            UpdateAge();
            _join.Tick(1f, allowInput: false);
        }

        internal void UpdateAge() => _age.text = StateWithDuration(_listing);
        internal void Tick(float dt, bool allowInput) => _join.Tick(dt, allowInput);
    }

    private static string JoinStatusText(string status) => status switch
    {
        "FULL" => "Full",
        "VERSION" => "Wrong version",
        "IN GAME" => "In game",
        "NO CODE" => "No code",
        "NO REGION" => "No region",
        _ => "Unavailable",
    };

    private static bool IsJoinable(VoiceLobbyListing listing)
        => string.Equals(listing.State, "Lobby", StringComparison.OrdinalIgnoreCase)
           && listing.Players < listing.MaxPlayers
           && listing.ProtocolVersion == VoiceProtocol.ProtocolVersion
           && !string.IsNullOrWhiteSpace(listing.Code);

    private static string JoinStatus(VoiceLobbyListing listing)
    {
        var state = listing.State ?? "";
        if (listing.ProtocolVersion != VoiceProtocol.ProtocolVersion) return "VERSION";
        if (string.Equals(state, "InGame", StringComparison.OrdinalIgnoreCase)) return "IN GAME";
        if (!string.Equals(state, "Lobby", StringComparison.OrdinalIgnoreCase)) return string.IsNullOrWhiteSpace(state) ? "UNKNOWN" : state.ToUpperInvariant();
        if (listing.Players >= listing.MaxPlayers) return "FULL";
        return "JOIN";
    }

    private static string StateWithDuration(VoiceLobbyListing listing)
    {
        var label = string.Equals(listing.State, "InGame", StringComparison.OrdinalIgnoreCase) ? "In game" : "Lobby";
        var since = listing.StateChangedAt > 0 ? listing.StateChangedAt : listing.UpdatedAt;
        if (since <= 0) return label;
        var seconds = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - since);
        return $"{label} {FormatAge(seconds)}";
    }

    private static string FormatAge(long seconds)
    {
        if (seconds < 60) return seconds + "s";
        var minutes = seconds / 60;
        if (minutes < 60) return minutes + "m";
        var hours = minutes / 60;
        var rem = minutes % 60;
        return rem == 0 ? hours + "h" : hours + "h " + rem + "m";
    }

    private static IReadOnlyList<VoiceLobbyListing> VisibleListings(IReadOnlyList<VoiceLobbyListing> listings)
    {
        var byKey = new Dictionary<string, VoiceLobbyListing>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in listings)
        {
            var key = ListingIdentity(listing);
            if (string.IsNullOrEmpty(key)) continue;
            if (!byKey.TryGetValue(key, out var existing) || listing.UpdatedAt >= existing.UpdatedAt)
                byKey[key] = listing;
        }
        var result = new List<VoiceLobbyListing>(byKey.Values);
        result.Sort((a, b) =>
        {
            var lobbyOrder = Convert.ToInt32(string.Equals(b.State, "Lobby", StringComparison.OrdinalIgnoreCase))
                             - Convert.ToInt32(string.Equals(a.State, "Lobby", StringComparison.OrdinalIgnoreCase));
            return lobbyOrder != 0 ? lobbyOrder : b.UpdatedAt.CompareTo(a.UpdatedAt);
        });
        return result;
    }

    private static string ListingIdentity(VoiceLobbyListing listing)
    {
        var id = (listing.Id ?? "").Trim();
        if (!string.IsNullOrEmpty(id)) return "id:" + id;
        var code = (listing.Code ?? "").Trim();
        return string.IsNullOrEmpty(code) ? "" : "code:" + code;
    }

    private static bool TrySelectRegion(string regionName, out string error)
    {
        error = "";
        try
        {
            var manager = DestroyableSingleton<ServerManager>.Instance;
            if (manager == null)
            {
                error = "Among Us region manager is not available";
                return false;
            }
            var wanted = NormalizeRegionName(regionName);
            IRegionInfo? match = null;
            foreach (var region in manager.AvailableRegions)
            {
                if (region == null) continue;
                if (string.Equals(region.Name, regionName, StringComparison.OrdinalIgnoreCase)
                    || NormalizeRegionName(region.Name) == wanted)
                {
                    match = region;
                    break;
                }
            }
            if (match == null)
            {
                error = $"Required region is not installed: {regionName}. Install/enable that region, then retry.";
                return false;
            }
            if (!string.Equals(manager.CurrentRegion?.Name, match.Name, StringComparison.OrdinalIgnoreCase))
                manager.SetRegion(match);
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not select region " + regionName + ": " + ex.Message;
            return false;
        }
    }

    private static string NormalizeRegionName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var source = value.AsSpan();
        Span<char> buffer = stackalloc char[Math.Min(source.Length, 64)];
        var count = 0;
        foreach (var character in source)
        {
            if (!char.IsLetterOrDigit(character) || count >= buffer.Length) continue;
            buffer[count++] = char.ToLowerInvariant(character);
        }
        return new string(buffer[..count]);
    }

    private static void JoinListing(VoiceLobbyListing listing)
    {
        if (!IsJoinable(listing)) return;
        if (string.IsNullOrWhiteSpace(listing.Region))
        {
            SetStatus("Join failed: listing does not include an Among Us region");
            return;
        }
        if (!TrySelectRegion(listing.Region.Trim(), out var regionError))
        {
            SetStatus("Join failed: " + regionError);
            return;
        }
        try
        {
            int gameId = GameCode.GameNameToInt(listing.Code.Trim());
            AmongUsClient.Instance.StartCoroutine(AmongUsClient.Instance.CoFindGameInfoFromCodeAndJoin(gameId));
            ClosePanel();
        }
        catch (Exception ex)
        {
            SetStatus("Join failed: " + ex.Message);
        }
    }

    private static void RenderEditor()
    {
        EnsurePanel();
        _directoryRoot!.gameObject.SetActive(false);
        _editorRoot!.gameObject.SetActive(true);
        _scrollDragging = false;
        UpdateEditorPresentation();
    }

    private static void UpdateEditorPresentation()
    {
        bool caret = Mathf.Repeat(Time.unscaledTime, 1f) < 0.6f;
        _titleValue!.text = _editTitle + (!_editingLanguage && caret ? "|" : "");
        _languageValue!.text = _editLanguage + (_editingLanguage && caret ? "|" : "");
        _titleFocus!.color = !_editingLanguage ? VoiceUiKit.Accent : VoiceUiKit.Divider;
        _languageFocus!.color = _editingLanguage ? VoiceUiKit.Accent : VoiceUiKit.Divider;
        _titleCount!.text = $"{_editTitle.Length}/40";
        _languageCount!.text = $"{_editLanguage.Length}/16";
    }

    private static void UpdateEditorInput()
    {
        string input = Input.inputString ?? "";
        foreach (char c in input)
        {
            if (c == '\b')
            {
                if (_editingLanguage && _editLanguage.Length > 0) _editLanguage = _editLanguage[..^1];
                else if (!_editingLanguage && _editTitle.Length > 0) _editTitle = _editTitle[..^1];
            }
            else if (c is '\n' or '\r')
            {
                SaveEditor();
                return;
            }
            else if (!char.IsControl(c))
            {
                if (_editingLanguage && _editLanguage.Length < 16) _editLanguage += c;
                else if (!_editingLanguage && _editTitle.Length < 40) _editTitle += c;
            }
        }
        if (Input.GetKeyDown(KeyCode.Tab)) _editingLanguage = !_editingLanguage;
    }

    private static void CancelEditor()
    {
        _editorOpen = false;
        _editTitle = "";
        _editLanguage = "";
        _openedFrame = Time.frameCount;
        VoiceUiKit.SwallowClick();
        Refresh();
    }

    private static void SaveEditor()
    {
        var settings = VoiceSettings.Instance;
        if (settings != null)
        {
            settings.LobbyBrowserTitle.Value = string.IsNullOrWhiteSpace(_editTitle) ? "Perfect Comms" : _editTitle.Trim();
            settings.LobbyBrowserLanguage.Value = string.IsNullOrWhiteSpace(_editLanguage) ? "English" : _editLanguage.Trim();
        }
        _editorOpen = false;
        _editTitle = "";
        _editLanguage = "";
        _openedFrame = Time.frameCount;
        VoiceUiKit.SwallowClick();
        Refresh();
    }

    private static void ClearRows()
    {
        foreach (var row in _rows)
        {
            if (row.Root == null) continue;
            row.Root.gameObject.SetActive(false);
            Object.Destroy(row.Root.gameObject);
        }
        _rows.Clear();
        _rowByIdentity.Clear();
        _scroll = 0f;
        _scrollDragging = false;
        if (_rowsRoot != null) _rowsRoot.sizeDelta = new Vector2(_rowsRoot.sizeDelta.x, 0f);
        UpdateScrollVisual();
    }

    private static void SetEmptyState(string title, string description)
    {
        if (_emptyTitle == null || _emptyDescription == null) return;
        _emptyTitle.text = title;
        _emptyDescription.text = description;
        _emptyTitle.gameObject.SetActive(true);
        _emptyDescription.gameObject.SetActive(true);
    }

    private static void SetStatus(string text)
    {
        if (_statusText != null) _statusText.text = text;
    }

    private static TextMeshProUGUI Label(string name, RectTransform parent, string text, float size, Color32 color,
        float x, float y, float width, float height, FontStyles style = FontStyles.Normal)
    {
        var label = VoiceUiKit.Text(name, parent, text, size, color, TextAlignmentOptions.Left, style);
        label.richText = false;
        Place(label.rectTransform, x, y, width, height);
        return label;
    }

    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(x, -y);
    }
}

[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
internal static class VoiceLobbyMainMenuStartPatch
{
    private static void Postfix(MainMenuManager __instance)
        => VoiceLobbyBrowserUi.EnsureMainMenuButton(__instance);
}

[HarmonyPatch(typeof(MainMenuManager), "LateUpdate")]
internal static class VoiceLobbyMainMenuUpdatePatch
{
    private static void Postfix(MainMenuManager __instance)
    {
        VoiceLobbyBrowserUi.EnsureMainMenuButton(__instance);
        VoiceLobbyBrowserUi.Update();
    }
}
