using System;
using System.Threading.Tasks;
using HarmonyLib;
using TMPro;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

internal static class PerfectCommsUpdateNotifier
{
    private const float PanelWidth = 420f;
    private const float PanelHeight = 160f;

    private static GameObject? _root;
    private static RectTransform? _rootRect;
    private static CanvasGroup? _group;
    private static MainMenuManager? _owner;
    private static VoiceUiKit.ActionButton? _downloadButton;
    private static VoiceUiKit.ActionButton? _dismissButton;
    private static float _appear;
    private static int _tickFrame = -1;
    private static Task<PerfectCommsUpdateInfo?>? _checkTask;
    private static int _mainMenuLoadId;
    private static int _shownLoadId;

    internal static bool CursorOverNotice =>
        _rootRect != null && _rootRect.gameObject.activeInHierarchy && VoiceUiKit.Contains(_rootRect);

    internal static void OnMainMenuLoaded(MainMenuManager menu)
    {
        _mainMenuLoadId++;
        _shownLoadId = -1;
        Clear();
        _checkTask = null;

        var settings = VoiceSettings.Instance;
        if (settings?.UpdateNotificationsEnabled.Value == false) return;

        var url = settings?.UpdateNotificationUrl.Value ?? "";
        _checkTask = PerfectCommsUpdateClient.GetLatestAsync(url);
    }

    internal static void Update(MainMenuManager menu)
    {
        TickUi();
        if (VoiceFirstRunSetup.ShouldDeferMainMenuPopups || VoiceUiKit.AnyPanelOpen) return;
        if (_root != null) return;
        if (_shownLoadId == _mainMenuLoadId) return;
        if (_checkTask == null || !_checkTask.IsCompleted) return;

        _shownLoadId = _mainMenuLoadId;
        PerfectCommsUpdateInfo? info;
        try
        {
            info = _checkTask.Result;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning("[VC] Update notification check failed: " + ex.Message);
            info = null;
        }

        if (!ShouldShow(info)) return;
        Show(menu, info!);
    }

    internal static void Clear()
    {
        if (_root != null)
        {
            _root.SetActive(false);
            Object.Destroy(_root);
        }
        _root = null;
        _rootRect = null;
        _group = null;
        _owner = null;
        _downloadButton = null;
        _dismissButton = null;
        _appear = 0f;
    }

    private static bool ShouldShow(PerfectCommsUpdateInfo? info)
    {
        if (info == null || !info.Enabled) return false;

        if (info.ShowEveryMainMenu) return true;
        return PerfectCommsUpdateClient.IsNewerThanCurrent(info.LatestVersion);
    }

    private static void Show(MainMenuManager menu, PerfectCommsUpdateInfo info)
    {
        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();
        _owner = menu;
        _rootRect = VoiceUiKit.Rect("PerfectComms_UpdateNotification", VoiceUiKit.Canvas.transform);
        _rootRect.Anchor(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f));
        _rootRect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
        _root = _rootRect.gameObject;
        _group = _root.AddComponent<CanvasGroup>();
        _appear = 0f;

        var border = _root.AddComponent<UnityEngine.UI.Image>();
        border.sprite = VoiceUiKit.Rounded();
        border.type = UnityEngine.UI.Image.Type.Sliced;
        border.color = VoiceUiKit.Divider;
        border.raycastTarget = false;
        var surface = VoiceUiKit.Panel("Surface", _rootRect, VoiceUiKit.PanelInner);
        surface.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        surface.rectTransform.offsetMin = Vector2.one;
        surface.rectTransform.offsetMax = -Vector2.one;
        var mark = VoiceUiKit.SpectrumImage("Spectrum", _rootRect);
        mark.rectTransform.Anchor(new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f));
        mark.rectTransform.sizeDelta = new Vector2(-32f, 2f);

        var logo = VoiceUiKit.Rect("Logo", _rootRect).gameObject.AddComponent<UnityEngine.UI.Image>();
        logo.sprite = VoiceUiBranding.Logo();
        logo.preserveAspect = true;
        logo.raycastTarget = false;
        logo.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        logo.rectTransform.sizeDelta = new Vector2(28f, 40f);
        logo.rectTransform.anchoredPosition = new Vector2(20f, -22f);
        var title = VoiceUiKit.Text("Title", _rootRect,
            string.IsNullOrWhiteSpace(info.Title) ? "Update available" : info.Title.Trim(),
            18f, VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold);
        title.richText = false;
        title.enableAutoSizing = true;
        title.fontSizeMax = 18f;
        title.fontSizeMin = 14f;
        title.overflowMode = TextOverflowModes.Ellipsis;
        title.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        title.rectTransform.sizeDelta = new Vector2(328f, 30f);
        title.rectTransform.anchoredPosition = new Vector2(68f, -20f);
        var version = VoiceUiKit.Text("Version", _rootRect,
            string.IsNullOrWhiteSpace(info.LatestVersion) ? "View the latest release."
                : "Available version " + info.LatestVersion.Trim(),
            14f, VoiceUiKit.TextMuted, TextAlignmentOptions.Left);
        version.richText = false;
        version.overflowMode = TextOverflowModes.Ellipsis;
        version.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        version.rectTransform.sizeDelta = new Vector2(328f, 24f);
        version.rectTransform.anchoredPosition = new Vector2(68f, -54f);

        string releaseUrl = info.ReleaseUrl;
        _downloadButton = new VoiceUiKit.ActionButton(_rootRect, "View release", 156f, 40f,
            20f, -104f, VoiceUiKit.ButtonKind.Primary, () =>
            {
                if (!string.IsNullOrWhiteSpace(releaseUrl)) Application.OpenURL(releaseUrl);
                Clear();
            }, () => !string.IsNullOrWhiteSpace(releaseUrl));
        _dismissButton = new VoiceUiKit.ActionButton(_rootRect, "Dismiss", 108f, 40f,
            190f, -104f, VoiceUiKit.ButtonKind.Ghost, Clear);
        ApplyPresentation();
        VoiceDiagnostics.DebugInfo($"[VC] Showing update notification: {info.Title}");
    }

    internal static void TickUi()
    {
        if (_tickFrame == Time.frameCount) return;
        _tickFrame = Time.frameCount;
        if (_root == null) return;
        if (_owner == null)
        {
            Clear();
            return;
        }
        bool visible = _owner.gameObject.activeInHierarchy &&
            !VoiceFirstRunSetup.ShouldDeferMainMenuPopups && !VoiceUiKit.AnyPanelOpen;
        if (_root.activeSelf != visible) _root.SetActive(visible);
        if (!visible) return;
        _appear = Mathf.MoveTowards(_appear, 1f, Time.unscaledDeltaTime / 0.18f);
        ApplyPresentation();
        _downloadButton?.Tick(Time.unscaledDeltaTime, _appear >= 1f);
        _dismissButton?.Tick(Time.unscaledDeltaTime, _appear >= 1f);
    }

    private static void ApplyPresentation()
    {
        if (_rootRect == null || _group == null) return;
        Rect canvas = VoiceUiKit.CanvasRect.rect;
        Rect safe = canvas;
        Rect screenSafe = Screen.safeArea;
        if (Screen.width > 0 && Screen.height > 0 && screenSafe.width > 0f && screenSafe.height > 0f)
            safe = Rect.MinMaxRect(
                canvas.xMin + screenSafe.xMin / Screen.width * canvas.width,
                canvas.yMin + screenSafe.yMin / Screen.height * canvas.height,
                canvas.xMin + screenSafe.xMax / Screen.width * canvas.width,
                canvas.yMin + screenSafe.yMax / Screen.height * canvas.height);
        float fit = Mathf.Min(1f,
            Mathf.Min(Mathf.Max(0f, safe.width - 48f) / PanelWidth,
                Mathf.Max(0f, safe.height - 48f) / PanelHeight));
        float eased = 1f - Mathf.Pow(1f - _appear, 3f);
        float scale = fit * Mathf.Lerp(0.98f, 1f, eased);
        _rootRect.localScale = new Vector3(scale, scale, 1f);
        _rootRect.anchoredPosition = new Vector2(
            safe.xMax - canvas.xMax - 24f, safe.yMin - canvas.yMin + 24f);
        _group.alpha = eased;
    }

}

[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
internal static class PerfectCommsUpdateMainMenuStartPatch
{
    private static void Postfix(MainMenuManager __instance)
        => PerfectCommsUpdateNotifier.OnMainMenuLoaded(__instance);
}

[HarmonyPatch(typeof(MainMenuManager), "LateUpdate")]
internal static class PerfectCommsUpdateMainMenuUpdatePatch
{
    private static void Postfix(MainMenuManager __instance)
        => PerfectCommsUpdateNotifier.Update(__instance);
}
