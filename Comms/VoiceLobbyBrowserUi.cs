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
    private const float PanelAnimationSeconds = 0.18f;
    private const float PanelPrewarmDelaySeconds = 0.10f;
    private static GameObject? _buttonObj;
    private static GameObject? _buttonVisualObj;
    private static float _buttonVisualYOffset;
    private static GameObject? _panelRoot;
    private static GameObject? _rowsRoot;
    private static TextMeshPro? _statusText;
    private static TextMeshPro? _editorText;
    private static TextMeshPro? _sourceButtonText;
    private static PassiveButton? _buttonTemplate;
    private static bool _panelVisible;
    private static bool _panelClosing;
    private static float _panelAnimation;
    private static bool _panelPrewarmScheduled;
    private static float _panelPrewarmAt;
    private static bool _buttonInputCached;
    private static bool _buttonVisualsPrepared;
    private static bool _editorOpen;
    private static bool _editingLanguage;
    private static string _editTitle = "";
    private static string _editLanguage = "";
    private static Task<IReadOnlyList<VoiceLobbyListing>>? _refreshTask;
    private static VoiceLobbyBrowserSource _refreshTaskSource = VoiceLobbyBrowserSource.CloudflareLimited;
    private static Task<BetterCrewLinkLobbyJoinResult>? _bclJoinTask;
    private static DateTime _nextAutoRefreshUtc = DateTime.MinValue;
    private static IReadOnlyList<VoiceLobbyListing> _lastBclLiveListings = Array.Empty<VoiceLobbyListing>();
    private static DateTime _nextBclLiveAgeTickUtc = DateTime.MinValue;
    private static Sprite? _panelSprite;
    private static Sprite? _rowSprite;
    private static Sprite? _voiceButtonSprite;
    private static Sprite? _normalButtonSprite;
    private static Sprite? _disabledButtonSprite;
    private static Sprite? _clearButtonSprite;

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
            _buttonVisualsPrepared = false;
            _buttonInputCached = false;

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
            KeepOnTop(_buttonObj, SortBase + 5);
            _buttonVisualsPrepared = true;
        }
        EnsureButtonArtVisual(menu);
        var panelBlockingButton = _panelVisible || _panelClosing;
        _buttonObj.SetActive(!panelBlockingButton);
        if (_buttonVisualObj != null) _buttonVisualObj.SetActive(!panelBlockingButton);
        SchedulePanelPrewarm();
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
        _sourceButtonText = null;
        _buttonTemplate = null;
        _panelVisible = false;
        _panelClosing = false;
        _panelAnimation = 0f;
        _panelPrewarmScheduled = false;
        _buttonInputCached = false;
        _buttonVisualsPrepared = false;
        _editorOpen = false;
        _refreshTask = null;
        _bclJoinTask = null;
        BetterCrewLinkLobbyBrowserClient.Disconnect();
    }

    internal static void OpenInfoEditor()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        _editTitle = settings?.LobbyBrowserTitle.Value ?? "Perfect Comms";
        _editLanguage = settings?.LobbyBrowserLanguage.Value ?? "English";
        _editingLanguage = false;
        _editorOpen = true;
        ShowPanelForContent();
        RenderEditor();
    }

    internal static void Update()
    {
        PrewarmPanelIfReady();
        UpdateSourceButtonLabel();
        CompleteBclJoinIfReady();

        if (_refreshTask is { IsCompleted: true })
        {
            var task = _refreshTask;
            var source = _refreshTaskSource;
            _refreshTask = null;
            _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(5);
            if (source == CurrentSource())
            {
                if (task.IsFaulted)
                    SetStatus("Failed to load lobbies: " + (task.Exception?.GetBaseException().Message ?? "unknown"));
                else
                    RenderListings(VisibleListings(task.Result));
            }
        }

        if (_panelVisible && !_editorOpen && CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink)
        {
            EnsureBclLive();
            if (BetterCrewLinkLobbyBrowserClient.TryConsumeSnapshot(out var listings, out var status))
            {
                _lastBclLiveListings = listings;
                _nextBclLiveAgeTickUtc = DateTime.UtcNow.AddSeconds(1);
                RenderListings(VisibleListings(listings));
                if (listings.Count == 0 && _statusText != null)
                    _statusText.text = status;
            }
            else if (_lastBclLiveListings.Count > 0 && DateTime.UtcNow >= _nextBclLiveAgeTickUtc)
            {
                _nextBclLiveAgeTickUtc = DateTime.UtcNow.AddSeconds(1);
                RenderListings(VisibleListings(_lastBclLiveListings));
            }
        }

        if (_panelVisible
            && !_editorOpen
            && CurrentSource() == VoiceLobbyBrowserSource.CloudflareLimited
            && _refreshTask == null
            && DateTime.UtcNow >= _nextAutoRefreshUtc)
            Refresh(false);

        if (_editorOpen)
            UpdateEditorInput();

        AnimatePanel();
    }

    private static void TogglePanel()
    {
        if (_panelVisible || _panelClosing)
        {
            ClosePanel();
            return;
        }

        _editorOpen = false;
        OpenPanel(refresh: true);
    }

    private static void OpenPanel(bool refresh)
    {
        _panelVisible = true;
        _panelClosing = false;
        EnsurePanel();
        ShowPanelForContent();
        if (refresh) Refresh();
    }

    private static void ShowPanelForContent()
    {
        _panelVisible = true;
        _panelClosing = false;
        if (_panelRoot == null) EnsurePanel();
        if (_panelRoot == null) return;

        _panelRoot.SetActive(true);
        if (_panelAnimation <= 0f) _panelAnimation = 0.001f;
        ApplyPanelTransform();
    }

    private static void ClosePanel()
    {
        _panelVisible = false;
        _editorOpen = false;
        _panelClosing = _panelRoot != null && _panelAnimation > 0f;
        if (_panelRoot == null) return;

        if (!_panelClosing)
        {
            _panelRoot.SetActive(false);
            return;
        }

        _panelRoot.SetActive(true);
        ApplyPanelTransform();
    }

    private static void SchedulePanelPrewarm()
    {
        if (_panelRoot != null || _panelPrewarmScheduled) return;
        _panelPrewarmScheduled = true;
        _panelPrewarmAt = Time.unscaledTime + PanelPrewarmDelaySeconds;
    }

    private static void PrewarmPanelIfReady()
    {
        if (!_panelPrewarmScheduled || _panelRoot != null || Time.unscaledTime < _panelPrewarmAt) return;

        _panelPrewarmScheduled = false;
        var wasVisible = _panelVisible;
        _panelVisible = false;
        EnsurePanel();
        _panelAnimation = wasVisible ? 1f : 0f;
        if (_panelRoot != null) _panelRoot.SetActive(wasVisible);
        _panelVisible = wasVisible;
    }

    private static void AnimatePanel()
    {
        if (_panelRoot == null) return;

        var target = _panelVisible ? 1f : 0f;
        if (Mathf.Approximately(_panelAnimation, target))
        {
            if (!_panelVisible && _panelClosing)
            {
                _panelClosing = false;
                _panelRoot.SetActive(false);
            }
            return;
        }

        var step = Time.unscaledDeltaTime / PanelAnimationSeconds;
        _panelAnimation = Mathf.MoveTowards(_panelAnimation, target, step);
        ApplyPanelTransform();

        if (!_panelVisible && Mathf.Approximately(_panelAnimation, 0f))
        {
            _panelClosing = false;
            _panelRoot.SetActive(false);
        }
    }

    private static void ApplyPanelTransform()
    {
        if (_panelRoot == null) return;

        var eased = EaseOutCubic(Mathf.Clamp01(_panelAnimation));
        var scale = Mathf.Lerp(0.965f, 1f, eased);
        _panelRoot.transform.localScale = new Vector3(scale, scale, 1f);
        _panelRoot.transform.localPosition = new Vector3(0f, Mathf.Lerp(-0.08f, 0f, eased), -30f);
    }

    private static float EaseOutCubic(float value)
    {
        value = 1f - value;
        return 1f - value * value * value;
    }

    private static void EnsurePanel()
    {
        if (_panelRoot != null)
        {
            _panelRoot.SetActive(_panelVisible || _panelClosing || _panelAnimation > 0f);
            ApplyPanelTransform();
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
        _panelRoot.SetActive(_panelVisible || _panelClosing);

        var panelArt = new GameObject("PanelArt");
        panelArt.transform.SetParent(_panelRoot.transform, false);
        panelArt.transform.localPosition = new Vector3(0f, 0f, -0.08f);
        var panelSr = panelArt.AddComponent<SpriteRenderer>();
        if (_panelSprite == null)
            _panelSprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserPanel.png");
        panelSr.sprite = _panelSprite;
        panelSr.sortingLayerName = "UI";
        panelSr.sortingOrder = SortBase + 1;
        if (panelSr.sprite != null)
        {
            var panelSize = panelSr.sprite.bounds.size;
            if (panelSize.x > 0f && panelSize.y > 0f)
                panelArt.transform.localScale = new Vector3(7.4f / panelSize.x, 4.25f / panelSize.y, 1f);
        }

        var titleText = CreateText("Title", _panelRoot.transform, new Vector3(0f, 1.35f, -0.2f),
            "Voice Lobbies", 1.70f, TextAlignmentOptions.Center, SortBase + 4);
        titleText.fontStyle = FontStyles.Bold;
        titleText.characterSpacing = 1.4f;
        titleText.color = new Color32(188, 247, 255, 255);

        _statusText = CreateText("Status", _panelRoot.transform, new Vector3(0f, 0.98f, -0.2f),
            "Loading...", 1.10f, TextAlignmentOptions.Center, SortBase + 4);
        _statusText.color = new Color32(184, 217, 232, 255);

        _rowsRoot = new GameObject("Rows");
        _rowsRoot.transform.SetParent(_panelRoot.transform, false);

        CreateTextButton("CloseX", _panelRoot.transform, new Vector3(2.26f, 1.43f, -0.2f),
            new Vector2(0.72f, 0.72f), "X", ClosePanel, transparentBackground: true);
        CreateTextButton("Refresh", _panelRoot.transform, new Vector3(-2.05f, -1.35f, -0.2f),
            new Vector2(1.08f, 0.44f), "Refresh", () => Refresh());
        CreateTextButton("Source", _panelRoot.transform, new Vector3(-2.55f, 1.28f, -0.2f),
            new Vector2(1.28f, 0.24f), SourceButtonLabel(), ToggleSource);
        CreateTextButton("Info", _panelRoot.transform, new Vector3(2.05f, -1.35f, -0.2f),
            new Vector2(1.14f, 0.44f), "Info", OpenInfoEditor);
        ApplyPanelTransform();
    }

    private static void Refresh(bool showLoading = true)
    {
        EnsurePanel();
        if (showLoading)
        {
            ClearRows();
            SetStatus(CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink
                ? "Connecting to BCL live lobbies..."
                : "Loading Cloudflare (Limited) lobbies...");
        }
        if (CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink)
        {
            _refreshTask = null;
            EnsureBclLive();
            BetterCrewLinkLobbyBrowserClient.RequestSnapshot();
            return;
        }

        BetterCrewLinkLobbyBrowserClient.Disconnect();
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        var url = settings?.LobbyRegistryUrl.Value ?? "";
        _refreshTaskSource = VoiceLobbyBrowserSource.CloudflareLimited;
        _refreshTask = VoiceLobbyRegistryClient.ListAsync(url);
        _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(5);
    }

    private static VoiceLobbyBrowserSource CurrentSource()
    {
        var source = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance?.LobbyBrowserSource.Value
                     ?? VoiceLobbyBrowserSource.BetterCrewLink;
        return Enum.IsDefined(typeof(VoiceLobbyBrowserSource), source)
            ? source
            : VoiceLobbyBrowserSource.BetterCrewLink;
    }

    private static string SourceButtonLabel()
        => CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink
            ? "BCL Live"
            : "Cloudflare (Limited)";

    private static void ToggleSource()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings == null) return;
        settings.LobbyBrowserSource.Value = CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink
            ? VoiceLobbyBrowserSource.CloudflareLimited
            : VoiceLobbyBrowserSource.BetterCrewLink;
        _refreshTask = null;
        _refreshTaskSource = settings.LobbyBrowserSource.Value;
        _lastBclLiveListings = Array.Empty<VoiceLobbyListing>();
        _nextBclLiveAgeTickUtc = DateTime.MinValue;
        _bclJoinTask = null;
        _nextAutoRefreshUtc = DateTime.MinValue;
        UpdateSourceButtonLabel();
        Refresh();
    }

    private static void UpdateSourceButtonLabel()
    {
        if (_sourceButtonText != null)
            _sourceButtonText.text = SourceButtonLabel();
    }

    private static void EnsureBclLive()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        BetterCrewLinkLobbyBrowserClient.EnsureConnected(settings?.BetterCrewLinkServerUrl.Value ?? "");
    }

    private static void CompleteBclJoinIfReady()
    {
        if (_bclJoinTask is not { IsCompleted: true }) return;
        var task = _bclJoinTask;
        _bclJoinTask = null;
        if (task.IsFaulted)
        {
            SetStatus("Join failed: " + (task.Exception?.GetBaseException().Message ?? "unknown"));
            return;
        }

        var result = task.Result;
        if (!result.Success || string.IsNullOrWhiteSpace(result.Code))
        {
            SetStatus("Join failed: " + (string.IsNullOrWhiteSpace(result.Error) ? "Lobby is not joinable" : result.Error));
            return;
        }

        try
        {
            int gameId = GameCode.GameNameToInt(result.Code.Trim());
            AmongUsClient.Instance.StartCoroutine(AmongUsClient.Instance.CoFindGameInfoFromCodeAndJoin(gameId));
            ClosePanel();
        }
        catch (Exception ex)
        {
            SetStatus("Join failed: " + ex.Message);
        }
    }

    private static void RenderListings(IReadOnlyList<VoiceLobbyListing> listings)
    {
        ClearRows();
        if (_rowsRoot == null) return;

        if (listings.Count == 0)
        {
            SetStatus("");
            var empty = CreateText("EmptyState", _rowsRoot.transform, new Vector3(0f, 0.05f, -0.2f),
                CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink
                    ? "No PerfectComms BCL live lobbies listed.\nHost a lobby and enable Public Voice Lobby in game settings."
                    : "No public voice lobbies listed.\nHost a lobby and enable Public Voice Lobby in game settings.",
                1.20f, TextAlignmentOptions.Center, SortBase + 4);
            empty.enableWordWrapping = true;
            empty.rectTransform.sizeDelta = new Vector2(6.0f, 1.6f);
            empty.color = new Color32(210, 231, 238, 255);
            return;
        }

        SetStatus(CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink
            ? $"{listings.Count} PerfectComms BCL live lobby/lobbies found"
            : $"{listings.Count} Cloudflare (Limited) lobby/lobbies found");
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
            title.fontStyle = FontStyles.Bold;
            title.color = new Color32(238, 252, 255, 255);

            var host = string.IsNullOrWhiteSpace(listing.Host) ? "Unknown" : listing.Host;
            var detailsText = BuildDetailsText(listing, host);
            var details = CreateText("RowDetails" + row, _rowsRoot.transform, new Vector3(-0.18f, y - 0.14f, -0.2f),
                detailsText, 0.68f, TextAlignmentOptions.Left, SortBase + 4);
            details.rectTransform.sizeDelta = new Vector2(5.68f, 0.32f);
            details.enableWordWrapping = false;
            details.overflowMode = TextOverflowModes.Ellipsis;
            details.color = new Color32(171, 210, 224, 255);

            CreateTextButton("Join" + row, _rowsRoot.transform, new Vector3(2.85f, y, -0.2f),
                new Vector2(0.92f, 0.36f), status, () => JoinListing(listing), status == "JOIN", true);
            row++;
        }
    }

    private static string BuildDetailsText(VoiceLobbyListing listing, string host)
    {
        var parts = new List<string>
        {
            StateWithDuration(listing),
            "Host: " + Truncate(host, 14),
        };

        if (!string.IsNullOrWhiteSpace(listing.Code))
            parts.Add(listing.Code.Trim());

        parts.Add($"{listing.Players}/{listing.MaxPlayers}");

        var region = (listing.Region ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(region)
            && (CurrentSource() != VoiceLobbyBrowserSource.BetterCrewLink
                || !string.Equals(region, "BCL", StringComparison.OrdinalIgnoreCase)))
            parts.Add(region);

        if (!string.IsNullOrWhiteSpace(listing.Language))
            parts.Add(Truncate(listing.Language, 12));

        return string.Join("  •  ", parts);
    }

    private static string Truncate(string? value, int max)
    {
        value = string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
    }

    private static void CreateRowBackground(string name, Transform parent, Vector3 pos)
    {
        if (_rowSprite == null)
            _rowSprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserRow.png");
        var sprite = _rowSprite;
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
           && listing.ProtocolVersion == VoiceProtocol.ProtocolVersion
           && (CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink
               ? BetterCrewLinkLobbyMetadata.TryGetLobbyId(listing, out _)
               : !string.IsNullOrWhiteSpace(listing.Code));

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
        result.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    private static string ListingIdentity(VoiceLobbyListing listing)
    {
        var code = (listing.Code ?? "").Trim();
        if (!string.IsNullOrEmpty(code))
            return "code:" + code;

        if (CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink
            && BetterCrewLinkLobbyMetadata.TryGetLobbyId(listing, out _))
            return "bcl:" + (listing.Id ?? "").Trim();

        return "";
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
        if (CurrentSource() == VoiceLobbyBrowserSource.BetterCrewLink)
        {
            if (_bclJoinTask is { IsCompleted: false }) return;
            if (!BetterCrewLinkLobbyMetadata.TryGetLobbyId(listing, out var lobbyId)) return;
            var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
            var url = settings?.BetterCrewLinkServerUrl.Value ?? "";
            SetStatus("Joining BCL live lobby...");
            _bclJoinTask = BetterCrewLinkLobbyBrowserClient.JoinLobbyAsync(url, lobbyId);
            return;
        }

        if (string.IsNullOrWhiteSpace(listing.Code)) return;
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
        ClearRows();
        SetStatus("Edit lobby info. Click field, type, Save.");
        if (_rowsRoot == null) return;

        _editorText = CreateText("EditorText", _rowsRoot.transform, new Vector3(0f, 0.45f, -0.2f),
            EditorText(), 0.90f, TextAlignmentOptions.Center, SortBase + 4);
        _editorText.enableWordWrapping = true;
        _editorText.rectTransform.sizeDelta = new Vector2(5.6f, 1.0f);
        _editorText.color = new Color32(224, 242, 248, 255);
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
        {
            var child = _rowsRoot.transform.GetChild(i).gameObject;
            Object.Destroy(child);
        }
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
        sr.sprite = ButtonBackgroundSprite(enabled, transparentBackground);
        sr.sortingLayerName = "UI";
        sr.sortingOrder = SortBase + 2;
        var backgroundScale = ButtonBackgroundScale(sr.sprite, size);
        go.transform.localScale = backgroundScale;

        var collider = go.AddComponent<BoxCollider2D>();
        collider.size = new Vector2(size.x / backgroundScale.x, size.y / backgroundScale.y);
        var button = go.AddComponent<PassiveButton>();
        button.ClickMask = collider;
        button.Colliders = new Collider2D[] { collider };
        button.OnClick = new ButtonClickedEvent();
        button.OnMouseOver = new UnityEvent();
        button.OnMouseOut = new UnityEvent();
        button.enabled = enabled;
        button.SetButtonEnableState(enabled);
        if (enabled) button.OnClick.AddListener((Action)action);

        var isCloseButton = name == "CloseX";
        var isSourceButton = name == "Source";
        var closeTextOffset = new Vector3(0.013f, -0.014f, 0f);
        if (isCloseButton)
        {
            var outline = CreateText("TextOutline", go.transform, new Vector3(closeTextOffset.x, closeTextOffset.y, -0.21f), label,
                1.85f, TextAlignmentOptions.Center, SortBase + 3);
            outline.transform.localScale = new Vector3(1f / backgroundScale.x, 1f / backgroundScale.y, 1f);
            outline.fontStyle = FontStyles.Bold;
            outline.characterSpacing = 0f;
            outline.color = new Color32(0, 0, 0, 255);
        }

        var textPosition = isCloseButton
            ? new Vector3(closeTextOffset.x, closeTextOffset.y, -0.2f)
            : new Vector3(0f, 0.01f, -0.2f);
        var txt = CreateText("Text", go.transform, textPosition, label,
            isCloseButton ? 1.62f : isSourceButton ? 0.68f : transparentBackground ? 0.86f : 0.78f, TextAlignmentOptions.Center, SortBase + 4);
        txt.transform.localScale = new Vector3(1f / backgroundScale.x, 1f / backgroundScale.y, 1f);
        txt.fontStyle = FontStyles.Bold;
        txt.characterSpacing = transparentBackground ? 0f : 0.6f;
        txt.color = enabled
            ? isCloseButton ? new Color32(255, 76, 76, 255) : transparentBackground ? new Color32(255, 248, 238, 255) : new Color32(238, 255, 252, 255)
            : new Color32(150, 164, 170, 255);
        if (isCloseButton)
        {
            txt.outlineColor = new Color32(0, 0, 0, 255);
            txt.outlineWidth = 0.30f;
        }
        if (name == "Source")
            _sourceButtonText = txt;
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
        foreach (var renderer in button.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = false;
        foreach (var sr in button.GetComponentsInChildren<SpriteRenderer>(true))
            sr.color = Color.clear;
    }

    private static void EnsureButtonArtVisual(MainMenuManager menu)
    {
        if (_buttonObj == null) return;
        if (_buttonVisualObj == null)
        {
            if (_voiceButtonSprite == null)
                _voiceButtonSprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserButton.png", highQuality: true);
            var sprite = _voiceButtonSprite;
            if (sprite == null) return;

            _buttonVisualObj = new GameObject("VC_LobbyBrowserButtonArt");
            _buttonVisualObj.transform.SetParent(menu.PlayOnlineButton.transform.parent, false);
            var sr = _buttonVisualObj.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingLayerName = "UI";
            sr.sortingOrder = SortBase + 7;
        }

        FitButtonArtToClone();
        _buttonVisualObj.SetActive(_buttonObj.activeSelf && !_panelVisible && !_panelClosing);
        _buttonVisualObj.transform.SetParent(menu.PlayOnlineButton.transform.parent, false);
        _buttonVisualObj.transform.localPosition = _buttonObj.transform.localPosition + new Vector3(0f, _buttonVisualYOffset, -0.5f);
    }

    private static void FitButtonArtToClone()
    {
        if (_buttonObj == null || _buttonVisualObj == null || _voiceButtonSprite == null) return;

        var spriteSize = _voiceButtonSprite.bounds.size;
        if (spriteSize.x <= 0f || spriteSize.y <= 0f) return;

        float targetWidth = 0.95f;
        float baseHeight = 3.55f;
        if (TryGetColliderBounds(_buttonObj, out var buttonBounds) || TryGetRendererBounds(_buttonObj, out buttonBounds))
        {
            targetWidth = buttonBounds.size.x;
            baseHeight = buttonBounds.size.y;
        }

        float targetHeight = baseHeight * VoiceButtonHeightScale;
        _buttonVisualYOffset = (targetHeight - baseHeight) * 0.5f;
        _buttonVisualObj.transform.localScale = new Vector3(targetWidth / spriteSize.x, targetHeight / spriteSize.y, 1f);
    }

    private static bool TryGetColliderBounds(GameObject obj, out Bounds bounds)
    {
        bounds = default;
        bool found = false;
        foreach (var collider in obj.GetComponentsInChildren<Collider2D>(true))
        {
            if (!found)
            {
                bounds = collider.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(collider.bounds);
            }
        }
        return found;
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
        tmp.richText = false;
        tmp.color = new Color32(236, 248, 255, 255);
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

    private static Sprite ButtonBackgroundSprite(bool enabled, bool transparentBackground)
    {
        if (transparentBackground)
        {
            if (_clearButtonSprite == null) _clearButtonSprite = SolidSprite(Color.clear);
            return _clearButtonSprite;
        }

        if (enabled)
        {
            if (_normalButtonSprite == null) _normalButtonSprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserRow.png", highQuality: true);
            return _normalButtonSprite;
        }

        if (_disabledButtonSprite == null) _disabledButtonSprite = VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.LobbyBrowserRow.png", highQuality: true);
        return _disabledButtonSprite;
    }

    private static Vector3 ButtonBackgroundScale(Sprite? sprite, Vector2 targetSize)
    {
        if (sprite == null)
            return new Vector3(targetSize.x, targetSize.y, 1f);
        var bounds = sprite.bounds.size;
        if (bounds.x <= 0f || bounds.y <= 0f)
            return new Vector3(targetSize.x, targetSize.y, 1f);
        return new Vector3(targetSize.x / bounds.x, targetSize.y / bounds.y, 1f);
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
