using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using MiraAPI.LocalSettings;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceLobbyBrowserUi
{
    private const int SortBase = 32740;
    private const float VoiceButtonHeightScale = 497f / 433f;
    private static GameObject? _buttonObj;
    private static GameObject? _buttonVisualObj;
    private static float _buttonVisualYOffset;
    private static GameObject? _panelRoot;
    private static GameObject? _rowsRoot;
    private static TextMeshPro? _statusText;
    private static TextMeshPro? _editorText;
    private static PassiveButton? _buttonTemplate;
    private static bool _panelVisible;
    private static bool _editorOpen;
    private static bool _editingLanguage;
    private static string _editTitle = "";
    private static string _editLanguage = "";
    private static Task<IReadOnlyList<VoiceLobbyListing>>? _refreshTask;
    private static DateTime _nextAutoRefreshUtc = DateTime.MinValue;

    internal static void EnsureMainMenuButton(MainMenuManager menu)
    {
        if (menu.PlayOnlineButton == null) return;

        _buttonTemplate = menu.PlayOnlineButton;
        if (_buttonObj == null)
        {
            _buttonObj = Object.Instantiate(menu.PlayOnlineButton.gameObject, menu.PlayOnlineButton.transform.parent);
            _buttonObj.name = "VC_LobbyBrowserButton";
            SetButtonText(_buttonObj, "");
            HideButtonRenderers(_buttonObj);

            var button = _buttonObj.GetComponent<PassiveButton>();
            button.OnClick = new ButtonClickedEvent();
            button.OnClick.AddListener((Action)TogglePanel);
        }

        _buttonObj.SetActive(true);
        _buttonObj.transform.SetParent(menu.PlayOnlineButton.transform.parent, false);
        _buttonObj.transform.localPosition = menu.PlayOnlineButton.transform.localPosition + new Vector3(1.62f, 0f, -60f);
        _buttonObj.transform.localScale = new Vector3(
            menu.PlayOnlineButton.transform.localScale.x * 0.28f,
            menu.PlayOnlineButton.transform.localScale.y,
            menu.PlayOnlineButton.transform.localScale.z);
        var passive = _buttonObj.GetComponent<PassiveButton>();
        var colliders = _buttonObj.GetComponentsInChildren<Collider2D>(true);
        if (colliders.Length > 0)
        {
            var colliderArray = new Il2CppReferenceArray<Collider2D>(colliders.Length);
            for (int i = 0; i < colliders.Length; i++) colliderArray[i] = colliders[i];
            passive.ClickMask = colliders[0];
            passive.Colliders = colliderArray;
        }
        passive.enabled = true;
        passive.SetButtonEnableState(true);
        HideButtonRenderers(_buttonObj);
        EnsureButtonArtVisual(menu);
        KeepOnTop(_buttonObj, SortBase + 5);
        _buttonObj.SetActive(!_panelVisible);
        if (_buttonVisualObj != null) _buttonVisualObj.SetActive(!_panelVisible);
    }

    internal static void Clear()
    {
        if (_buttonObj != null) Object.Destroy(_buttonObj);
        if (_buttonVisualObj != null) Object.Destroy(_buttonVisualObj);
        if (_panelRoot != null) Object.Destroy(_panelRoot);
        _buttonObj = null;
        _buttonVisualObj = null;
        _panelRoot = null;
        _rowsRoot = null;
        _statusText = null;
        _editorText = null;
        _buttonTemplate = null;
        _panelVisible = false;
        _editorOpen = false;
        _refreshTask = null;
    }

    internal static void OpenInfoEditor()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        _editTitle = settings?.LobbyBrowserTitle.Value ?? "Perfect Comms";
        _editLanguage = settings?.LobbyBrowserLanguage.Value ?? "English";
        _editingLanguage = false;
        _editorOpen = true;
        _panelVisible = true;
        EnsurePanel();
        RenderEditor();
    }

    internal static void Update()
    {
        if (_refreshTask is { IsCompleted: true })
        {
            var task = _refreshTask;
            _refreshTask = null;
            _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(5);
            if (task.IsFaulted)
                SetStatus("Failed to load lobbies: " + (task.Exception?.GetBaseException().Message ?? "unknown"));
            else
                RenderListings(VisibleListings(task.Result));
        }

        if (_panelVisible && !_editorOpen && _refreshTask == null && DateTime.UtcNow >= _nextAutoRefreshUtc)
            Refresh(false);

        if (_editorOpen)
            UpdateEditorInput();
    }

    private static void TogglePanel()
    {
        _panelVisible = !_panelVisible;
        _editorOpen = false;
        EnsurePanel();
        _panelRoot?.SetActive(_panelVisible);
        if (_panelVisible) Refresh();
    }

    private static void EnsurePanel()
    {
        if (_panelRoot != null)
        {
            _panelRoot.SetActive(_panelVisible);
            return;
        }

        var parent = _buttonObj?.transform.parent
                     ?? _buttonTemplate?.transform.parent
                     ?? HudManager.Instance?.transform.parent
                     ?? HudManager.Instance?.transform;
        if (parent == null) return;

        _panelRoot = new GameObject("VC_LobbyBrowserPanel");
        _panelRoot.transform.SetParent(parent, false);
        _panelRoot.transform.localPosition = new Vector3(0f, 0f, -30f);
        _panelRoot.SetActive(_panelVisible);

        var panelArt = new GameObject("PanelArt");
        panelArt.transform.SetParent(_panelRoot.transform, false);
        panelArt.transform.localPosition = new Vector3(0f, 0f, -0.08f);
        var panelSr = panelArt.AddComponent<SpriteRenderer>();
        panelSr.sprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserPanel.png");
        panelSr.sortingLayerName = "UI";
        panelSr.sortingOrder = SortBase + 1;
        var panelSize = panelSr.sprite.bounds.size;
        if (panelSize.x > 0f && panelSize.y > 0f)
            panelArt.transform.localScale = new Vector3(7.4f / panelSize.x, 4.25f / panelSize.y, 1f);

        CreateText("Title", _panelRoot.transform, new Vector3(0f, 1.35f, -0.2f),
            "Voice Lobbies", 1.70f, TextAlignmentOptions.Center, SortBase + 4);

        _statusText = CreateText("Status", _panelRoot.transform, new Vector3(0f, 0.98f, -0.2f),
            "Loading...", 1.10f, TextAlignmentOptions.Center, SortBase + 4);

        _rowsRoot = new GameObject("Rows");
        _rowsRoot.transform.SetParent(_panelRoot.transform, false);

        CreateTextButton("Refresh", _panelRoot.transform, new Vector3(-2.0f, -1.35f, -0.2f),
            new Vector2(1.15f, 0.38f), "Refresh", () => Refresh());
        CreateTextButton("Info", _panelRoot.transform, new Vector3(0f, -1.35f, -0.2f),
            new Vector2(1.25f, 0.38f), "Lobby Info", OpenInfoEditor);
        CreateTextButton("Close", _panelRoot.transform, new Vector3(2.0f, -1.35f, -0.2f),
            new Vector2(1.0f, 0.38f), "Close", () =>
            {
                _panelVisible = false;
                _editorOpen = false;
                _panelRoot?.SetActive(false);
            });
    }

    private static void Refresh(bool showLoading = true)
    {
        EnsurePanel();
        if (showLoading)
        {
            ClearRows();
            SetStatus("Loading lobbies...");
        }
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        var url = settings?.LobbyRegistryUrl.Value ?? "";
        _refreshTask = VoiceLobbyRegistryClient.ListAsync(url);
        _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(5);
    }

    private static void RenderListings(IReadOnlyList<VoiceLobbyListing> listings)
    {
        ClearRows();
        if (_rowsRoot == null) return;

        if (listings.Count == 0)
        {
            SetStatus("");
            var empty = CreateText("EmptyState", _rowsRoot.transform, new Vector3(0f, 0.05f, -0.2f),
                "No public voice lobbies listed.\nHost a lobby and enable Public Voice Lobby in game settings.",
                1.20f, TextAlignmentOptions.Center, SortBase + 4);
            empty.enableWordWrapping = true;
            empty.rectTransform.sizeDelta = new Vector2(6.0f, 1.6f);
            return;
        }

        SetStatus($"{listings.Count} lobby/lobbies found");
        int row = 0;
        foreach (var listing in listings)
        {
            if (row >= 3) break;
            float y = 0.54f - row * 0.64f;
            string status = JoinStatus(listing);
            CreateRowBackground("RowBg" + row, _rowsRoot.transform, new Vector3(0.10f, y, -0.30f));

            var title = CreateText("RowTitle" + row, _rowsRoot.transform, new Vector3(-0.18f, y + 0.10f, -0.2f),
                Truncate(listing.Title, 24), 0.92f, TextAlignmentOptions.Left, SortBase + 4);
            title.rectTransform.sizeDelta = new Vector2(5.40f, 0.36f);
            title.enableWordWrapping = false;
            title.overflowMode = TextOverflowModes.Ellipsis;

            var host = string.IsNullOrWhiteSpace(listing.Host) ? "Unknown" : listing.Host;
            var detailsText = $"{StateWithDuration(listing)}  •  Host: {Truncate(host, 14)}  •  {listing.Code}  •  {listing.Players}/{listing.MaxPlayers}  •  {listing.Region}";
            if (!string.IsNullOrWhiteSpace(listing.Language)) detailsText += $"  •  {Truncate(listing.Language, 10)}";
            var details = CreateText("RowDetails" + row, _rowsRoot.transform, new Vector3(-0.18f, y - 0.14f, -0.2f),
                detailsText, 0.72f, TextAlignmentOptions.Left, SortBase + 4);
            details.rectTransform.sizeDelta = new Vector2(5.40f, 0.32f);
            details.enableWordWrapping = false;
            details.overflowMode = TextOverflowModes.Ellipsis;

            CreateTextButton("Join" + row, _rowsRoot.transform, new Vector3(2.85f, y, -0.2f),
                new Vector2(0.92f, 0.36f), status, () => JoinListing(listing), status == "JOIN", true);
            row++;
        }
    }

    private static string Truncate(string? value, int max)
    {
        value = string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
    }

    private static void CreateRowBackground(string name, Transform parent, Vector3 pos)
    {
        var sprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserRow.png");
        if (sprite == null) return;
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingLayerName = "UI";
        sr.sortingOrder = SortBase + 3;
        var size = sprite.bounds.size;
        if (size.x > 0f && size.y > 0f)
            go.transform.localScale = new Vector3(6.65f / size.x, 0.46f / size.y, 1f);
    }

    private static bool IsJoinable(VoiceLobbyListing listing)
        => string.Equals(listing.State, "Lobby", StringComparison.OrdinalIgnoreCase)
           && listing.Players < listing.MaxPlayers
           && listing.ProtocolVersion == VoiceProtocol.ProtocolVersion;

    private static string JoinStatus(VoiceLobbyListing listing)
    {
        if (listing.ProtocolVersion != VoiceProtocol.ProtocolVersion) return "VERSION";
        if (string.Equals(listing.State, "InGame", StringComparison.OrdinalIgnoreCase)) return "IN GAME";
        if (!string.Equals(listing.State, "Lobby", StringComparison.OrdinalIgnoreCase)) return listing.State.ToUpperInvariant();
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
        var byCode = new Dictionary<string, VoiceLobbyListing>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in listings)
        {
            var code = (listing.Code ?? "").Trim();
            if (string.IsNullOrEmpty(code)) continue;
            if (!byCode.TryGetValue(code, out var existing) || listing.UpdatedAt >= existing.UpdatedAt)
                byCode[code] = listing;
        }

        var result = new List<VoiceLobbyListing>(byCode.Values);
        result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    private static bool IsCurrentRegion(string region)
    {
        try
        {
            var current = DestroyableSingleton<ServerManager>.Instance?.CurrentRegion?.Name;
            return string.IsNullOrWhiteSpace(current)
                   || string.Equals(current, region, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static void JoinListing(VoiceLobbyListing listing)
    {
        if (!IsJoinable(listing)) return;
        try
        {
            int gameId = GameCode.GameNameToInt(listing.Code);
            AmongUsClient.Instance.StartCoroutine(AmongUsClient.Instance.CoFindGameInfoFromCodeAndJoin(gameId));
            _panelVisible = false;
            _panelRoot?.SetActive(false);
        }
        catch (Exception ex)
        {
            SetStatus("Join failed: " + ex.Message);
        }
    }

    private static void RenderEditor()
    {
        EnsurePanel();
        ClearRows();
        SetStatus("Edit lobby info. Click field, type, Save.");
        if (_rowsRoot == null) return;

        _editorText = CreateText("EditorText", _rowsRoot.transform, new Vector3(0f, 0.45f, -0.2f),
            EditorText(), 0.90f, TextAlignmentOptions.Center, SortBase + 4);
        _editorText.enableWordWrapping = true;
        _editorText.rectTransform.sizeDelta = new Vector2(5.6f, 1.0f);
        CreateTextButton("EditTitle", _rowsRoot.transform, new Vector3(-0.70f, -0.25f, -0.2f),
            new Vector2(1.25f, 0.34f), "Edit Title", () => { _editingLanguage = false; RenderEditor(); });
        CreateTextButton("EditLanguage", _rowsRoot.transform, new Vector3(0.70f, -0.25f, -0.2f),
            new Vector2(1.45f, 0.34f), "Edit Language", () => { _editingLanguage = true; RenderEditor(); });
        CreateTextButton("SaveInfo", _rowsRoot.transform, new Vector3(-0.70f, -0.75f, -0.2f),
            new Vector2(1.0f, 0.34f), "Save", SaveEditor);
        CreateTextButton("CancelInfo", _rowsRoot.transform, new Vector3(0.70f, -0.75f, -0.2f),
            new Vector2(1.0f, 0.34f), "Cancel", () => { _editorOpen = false; Refresh(); });
    }

    private static void UpdateEditorInput()
    {
        bool changed = false;
        string input = Input.inputString ?? "";
        foreach (char c in input)
        {
            if (c == '\b')
            {
                if (_editingLanguage && _editLanguage.Length > 0) _editLanguage = _editLanguage[..^1];
                else if (!_editingLanguage && _editTitle.Length > 0) _editTitle = _editTitle[..^1];
                changed = true;
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
                changed = true;
            }
        }

        if (Input.GetKeyDown(KeyCode.Tab))
        {
            _editingLanguage = !_editingLanguage;
            changed = true;
        }

        if (changed && _editorText != null)
            _editorText.text = EditorText();
    }

    private static string EditorText()
        => $"{(_editingLanguage ? "Title" : "> Title")}: {_editTitle}\n" +
           $"{(_editingLanguage ? "> Language" : "Language")}: {_editLanguage}";

    private static void SaveEditor()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings != null)
        {
            settings.LobbyBrowserTitle.Value = string.IsNullOrWhiteSpace(_editTitle) ? "Perfect Comms" : _editTitle.Trim();
            settings.LobbyBrowserLanguage.Value = string.IsNullOrWhiteSpace(_editLanguage) ? "English" : _editLanguage.Trim();
        }
        _editorOpen = false;
        Refresh();
    }

    private static void ClearRows()
    {
        if (_rowsRoot == null) return;
        for (int i = _rowsRoot.transform.childCount - 1; i >= 0; i--)
            Object.Destroy(_rowsRoot.transform.GetChild(i).gameObject);
        _editorText = null;
    }

    private static void SetStatus(string text)
    {
        if (_statusText != null) _statusText.text = text;
    }

    private static PassiveButton CreateTextButton(string name, Transform parent, Vector3 pos, Vector2 size,
        string label, Action action, bool enabled = true, bool transparentBackground = false)
    {
        var go = new GameObject("VC_" + name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = SolidSprite(transparentBackground
            ? Color.clear
            : enabled ? new Color(0.12f, 0.22f, 0.34f, 0.95f) : new Color(0.12f, 0.12f, 0.12f, 0.65f));
        sr.sortingLayerName = "UI";
        sr.sortingOrder = SortBase + 2;
        go.transform.localScale = new Vector3(go.transform.localScale.x * size.x, go.transform.localScale.y * size.y, 1f);

        var collider = go.AddComponent<BoxCollider2D>();
        collider.size = Vector2.one;
        var button = go.AddComponent<PassiveButton>();
        button.ClickMask = collider;
        button.Colliders = new Collider2D[] { collider };
        button.OnClick = new ButtonClickedEvent();
        button.OnMouseOver = new UnityEvent();
        button.OnMouseOut = new UnityEvent();
        if (enabled) button.OnClick.AddListener((Action)action);

        var txt = CreateText("Text", go.transform, new Vector3(0f, 0f, -0.2f), label,
            0.75f, TextAlignmentOptions.Center, SortBase + 4);
        txt.transform.localScale = new Vector3(1f / size.x, 1f / size.y, 1f);
        return button;
    }

    private static void SetButtonText(GameObject button, string text)
    {
        foreach (var tmp in button.GetComponentsInChildren<TextMeshPro>(true))
        {
            tmp.text = text;
            tmp.enableWordWrapping = true;
            tmp.fontSize = 1.05f;
            tmp.alignment = TextAlignmentOptions.Center;
        }
    }

    private static void HideButtonRenderers(GameObject button)
    {
        foreach (var sr in button.GetComponentsInChildren<SpriteRenderer>(true))
            sr.color = Color.clear;
    }

    private static void EnsureButtonArtVisual(MainMenuManager menu)
    {
        if (_buttonObj == null) return;
        if (_buttonVisualObj == null)
        {
            var sprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserButton.png");
            if (sprite == null) return;

            _buttonVisualObj = new GameObject("VC_LobbyBrowserButtonArt");
            _buttonVisualObj.transform.SetParent(menu.PlayOnlineButton.transform.parent, false);
            var sr = _buttonVisualObj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingLayerName = "UI";
            sr.sortingOrder = SortBase + 7;

            var size = sprite.bounds.size;
            if (size.x > 0f && size.y > 0f)
            {
                float targetWidth = 0.95f;
                float baseHeight = 3.55f;
                if (TryGetRendererBounds(menu.PlayOnlineButton.gameObject, out var playBounds))
                {
                    targetWidth = playBounds.size.x * 0.29f;
                    baseHeight = playBounds.size.y;
                }
                float targetHeight = baseHeight * VoiceButtonHeightScale;
                _buttonVisualYOffset = (targetHeight - baseHeight) * 0.5f;
                _buttonVisualObj.transform.localScale = new Vector3(targetWidth / size.x, targetHeight / size.y, 1f);
            }
        }

        _buttonVisualObj.SetActive(_buttonObj.activeSelf && !_panelVisible);
        _buttonVisualObj.transform.SetParent(menu.PlayOnlineButton.transform.parent, false);
        _buttonVisualObj.transform.localPosition = _buttonObj.transform.localPosition + new Vector3(0f, _buttonVisualYOffset, -0.5f);
    }

    private static bool TryGetRendererBounds(GameObject obj, out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr.sprite == null) continue;
            if (!found)
            {
                bounds = sr.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(sr.bounds);
            }
        }
        return found;
    }

    private static TextMeshPro CreateText(string name, Transform parent, Vector3 pos, string text,
        float size, TextAlignmentOptions align, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = Color.white;
        tmp.alignment = align;
        tmp.enableWordWrapping = false;
        tmp.sortingLayerID = SortingLayer.NameToID("UI");
        tmp.sortingOrder = order;
        tmp.rectTransform.sizeDelta = new Vector2(6.8f, 0.9f);
        return tmp;
    }

    private static void KeepOnTop(GameObject go, int order)
    {
        foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true))
        {
            sr.sortingLayerName = "UI";
            sr.sortingOrder = order;
        }
        foreach (var tmp in go.GetComponentsInChildren<TextMeshPro>(true))
        {
            tmp.sortingLayerID = SortingLayer.NameToID("UI");
            tmp.sortingOrder = order + 1;
        }
    }

    private static Sprite SolidSprite(Color color)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, color);
        tex.Apply(false, true);
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
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

