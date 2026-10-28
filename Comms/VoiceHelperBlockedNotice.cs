#if WINDOWS
using System;
using System.IO;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Centered card that explains how to restore the native audio helper after antivirus removed or
/// blocked it. Opens once per <see cref="VoiceHelperBlockState.Episode"/> in the main menu or
/// lobby; Close hides it until the next episode.
/// </summary>
internal static class VoiceHelperBlockedNotice
{
    internal const string GuideUrl =
        "https://github.com/artriy/Perfect-Comms/wiki/Installing-Perfect-Comms#antivirus-blocks-voice-chat";

    private const float PanelWidth = 600f;
    private const float Pad = 24f;
    private const float StepTextLeft = 60f;
    private const float StepTextWidth = PanelWidth - StepTextLeft - Pad;
    private const float StepGap = 12f;
    private const string MutedHex = "#A6ACC3";

    private static GameObject? _root;
    private static RectTransform? _rootRect;
    private static CanvasGroup? _group;
    private static VoiceUiKit.ActionButton? _openFolderButton;
    private static VoiceUiKit.ActionButton? _retryButton;
    private static VoiceUiKit.ActionButton? _guideButton;
    private static VoiceUiKit.ActionButton? _closeButton;
    private static float _panelHeight;
    private static float _appear;
    private static int _tickFrame = -1;
    private static int _autoShownEpisode;
    private static VoiceHelperBlockKind _builtKind;
    private static string _builtRoot = string.Empty;

    internal static RectTransform? Root => _rootRect;

    internal static bool CursorOverNotice =>
        _rootRect != null && _rootRect.gameObject.activeInHierarchy && VoiceUiKit.Contains(_rootRect);

    internal static bool CanShowInCurrentPhase =>
        VoiceSceneState.ResolvePhase() is VoiceGamePhase.Menu or VoiceGamePhase.Lobby;

    internal static void Show()
    {
        var kind = VoiceHelperBlockState.Kind;
        if (kind == VoiceHelperBlockKind.None)
            return;
        _autoShownEpisode = VoiceHelperBlockState.Episode;
        if (_root != null && _builtKind == kind && _builtRoot == VoiceHelperBlockState.CacheRoot)
            return;
        Clear();
        Build(kind, VoiceHelperBlockState.CacheRoot);
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
        _openFolderButton = null;
        _retryButton = null;
        _guideButton = null;
        _closeButton = null;
        _appear = 0f;
    }

    internal static void TickUi()
    {
        if (_tickFrame == Time.frameCount) return;
        _tickFrame = Time.frameCount;

        var kind = VoiceHelperBlockState.Kind;
        if (kind == VoiceHelperBlockKind.None)
        {
            if (_root != null) Clear();
            return;
        }

        bool phaseAllowed = CanShowInCurrentPhase;
        if (_root == null)
        {
            if (phaseAllowed && !VoiceUiKit.AnyPanelOpen &&
                _autoShownEpisode != VoiceHelperBlockState.Episode)
                Show();
            if (_root == null) return;
        }
        else if (_builtKind != kind || _builtRoot != VoiceHelperBlockState.CacheRoot)
        {
            Clear();
            Build(kind, VoiceHelperBlockState.CacheRoot);
        }

        bool visible = phaseAllowed && !VoiceUiKit.AnyPanelOpen;
        if (_root!.activeSelf != visible) _root.SetActive(visible);
        if (!visible) return;
        _appear = Mathf.MoveTowards(_appear, 1f, Time.unscaledDeltaTime / 0.18f);
        ApplyPresentation();
        bool input = _appear >= 1f;
        _openFolderButton?.Tick(Time.unscaledDeltaTime, input);
        _retryButton?.Tick(Time.unscaledDeltaTime, input);
        _guideButton?.Tick(Time.unscaledDeltaTime, input);
        _closeButton?.Tick(Time.unscaledDeltaTime, input);
    }

    internal static bool CanRetry => !VoiceChatRoom.IsLocalVoiceRefreshCoolingDown;

    internal static void Retry()
    {
        bool hasRoom = VoiceChatRoom.Current != null;
        bool started = hasRoom && VoiceChatRoom.RequestLocalVoiceRefreshFromHelperNotice();
        var outcome = HelperRetryPolicy.Decide(hasRoom, started);
        VoiceDiagnostics.Log(
            "voice.helper_blocked.retry",
            $"outcome={outcome} kind={VoiceHelperBlockState.Kind} episode={VoiceHelperBlockState.Episode}");
        switch (outcome)
        {
            case HelperRetryOutcome.RefreshStarted:
                Clear();
                break;
            case HelperRetryOutcome.ClearedWithoutRoom:
                VoiceHelperBlockState.Clear();
                Clear();
                break;
        }
    }

    internal static void OpenGuide() => Application.OpenURL(GuideUrl);

    internal static void OpenHelperFolder()
    {
        var folder = VoiceHelperBlockState.CacheRoot;
        if (string.IsNullOrWhiteSpace(folder))
            return;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning("[VC] Could not create the helper folder: " + ex.Message);
            folder = Path.GetDirectoryName(folder) ?? folder;
        }
        var explorer = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        explorer.ArgumentList.Add(folder);
        try
        {
            System.Diagnostics.Process.Start(explorer)?.Dispose();
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning("[VC] Could not open the helper folder: " + ex.Message);
        }
    }

    private static void Build(VoiceHelperBlockKind kind, string cacheRoot)
    {
        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();
        _builtKind = kind;
        _builtRoot = cacheRoot;
        bool confirmed = kind == VoiceHelperBlockKind.Confirmed;

        _rootRect = VoiceUiKit.Rect("PerfectComms_HelperBlockedNotice", VoiceUiKit.Canvas.transform);
        _rootRect.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        _root = _rootRect.gameObject;
        _group = _root.AddComponent<CanvasGroup>();
        _appear = 0f;

        var border = _root.AddComponent<UnityEngine.UI.Image>();
        border.sprite = VoiceUiKit.Rounded();
        border.Round(VoiceUiKit.RadiusCard);
        border.type = UnityEngine.UI.Image.Type.Sliced;
        border.color = VoiceUiKit.Divider;
        border.raycastTarget = false;
        var surface = VoiceUiKit.Panel("Surface", _rootRect, VoiceUiKit.PanelInner).Round(VoiceUiKit.RadiusCard - 1f);
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
        TopLeft(logo.rectTransform, 20f, -24f, 28f, 40f);

        const float badgeWidth = 82f;
        const float headerTextWidth = PanelWidth - 62f - Pad - badgeWidth - 12f;
        var title = VoiceUiKit.Text("Title", _rootRect,
            VoiceConnectionStatusPolicy.HelperBlockedTitle(kind),
            20f, VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold);
        title.richText = false;
        title.enableAutoSizing = true;
        title.fontSizeMax = 20f;
        title.fontSizeMin = 15f;
        title.overflowMode = TextOverflowModes.Ellipsis;
        TopLeft(title.rectTransform, 62f, -20f, headerTextWidth, 28f);

        var subtitle = Wrapped("Subtitle", confirmed
                ? "Your antivirus removed or blocked the Perfect Comms audio helper."
                : "The Perfect Comms audio helper keeps stopping before voice can start.",
            14f, VoiceUiKit.TextMuted, headerTextWidth);
        float subtitleHeight = Preferred(subtitle, headerTextWidth);
        TopLeft(subtitle.rectTransform, 62f, -50f, headerTextWidth, subtitleHeight);

        var badge = VoiceUiKit.Panel("Badge", _rootRect, VoiceUiKit.DangerDim, true, true);
        TopLeft(badge.rectTransform, PanelWidth - Pad - badgeWidth, -26f, badgeWidth, 26f);
        var badgeLabel = VoiceUiKit.Text("BadgeLabel", badge.rectTransform, "Voice off", 12f,
            VoiceUiKit.Danger, TextAlignmentOptions.Center, FontStyles.Bold);
        badgeLabel.richText = false;
        badgeLabel.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        badgeLabel.rectTransform.offsetMin = Vector2.zero;
        badgeLabel.rectTransform.offsetMax = Vector2.zero;

        float y = -Mathf.Max(74f, 50f + subtitleHeight) - 18f;
        y = Step(1, y,
            "Open <b>Windows Security</b> > Virus & threat protection > <b>Protection history</b>, " +
            "then restore or allow <b>PerfectCommsAudio.exe</b>. " +
            $"<color={MutedHex}>Other antivirus: check its quarantine.</color>");

        y = Step(2, y, "Add this folder as an antivirus exclusion:") + StepGap - 8f;
        const float folderButtonWidth = 120f;
        const float fieldHeight = 36f;
        float fieldWidth = StepTextWidth - folderButtonWidth - 10f;
        var field = VoiceUiKit.Panel("FolderField", _rootRect, VoiceUiKit.SegmentWell).Round(VoiceUiKit.RadiusField);
        TopLeft(field.rectTransform, StepTextLeft, y, fieldWidth, fieldHeight);
        var folder = VoiceUiKit.Text("FolderPath", field.rectTransform, cacheRoot, 13f,
            VoiceUiKit.TextMuted, TextAlignmentOptions.Left);
        folder.richText = false;
        folder.overflowMode = TextOverflowModes.Ellipsis;
        folder.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        folder.rectTransform.offsetMin = new Vector2(12f, 0f);
        folder.rectTransform.offsetMax = new Vector2(-12f, 0f);
        _openFolderButton = new VoiceUiKit.ActionButton(_rootRect, "Open folder", folderButtonWidth, 34f,
            StepTextLeft + fieldWidth + 10f, y - 1f, VoiceUiKit.ButtonKind.Secondary, OpenHelperFolder,
            () => !string.IsNullOrWhiteSpace(VoiceHelperBlockState.CacheRoot), maxLabelSize: 15f);
        y -= fieldHeight + StepGap + 4f;

        y = Step(3, y, "Press <b>Retry</b>. Voice starts as soon as the helper runs.");

        float buttonsY = y - 6f;
        _retryButton = new VoiceUiKit.ActionButton(_rootRect, "Retry", 96f, 40f,
            Pad, buttonsY, VoiceUiKit.ButtonKind.Primary, Retry, () => CanRetry);
        _guideButton = new VoiceUiKit.ActionButton(_rootRect, "Open guide", 132f, 40f,
            Pad + 96f + 10f, buttonsY, VoiceUiKit.ButtonKind.Secondary, OpenGuide);
        _closeButton = new VoiceUiKit.ActionButton(_rootRect, "Close", 92f, 40f,
            PanelWidth - Pad - 92f, buttonsY, VoiceUiKit.ButtonKind.Secondary, Clear);

        _panelHeight = -(buttonsY - 40f) + 20f;
        _rootRect.sizeDelta = new Vector2(PanelWidth, _panelHeight);
        ApplyPresentation();
        VoiceUiKit.RaiseAbove(_rootRect);
        VoiceDiagnostics.Log(
            "voice.helper_blocked.notice",
            $"event=shown kind={kind} episode={VoiceHelperBlockState.Episode}");
    }

    private static float Step(int number, float y, string richText)
    {
        var circle = VoiceUiKit.Panel("StepNumber" + number, _rootRect!, VoiceUiKit.ControlBg, true, true);
        TopLeft(circle.rectTransform, Pad, y - 1f, 24f, 24f);
        var digit = VoiceUiKit.Text("Digit", circle.rectTransform, number.ToString(), 13f,
            VoiceUiKit.Accent, TextAlignmentOptions.Center, FontStyles.Bold);
        digit.richText = false;
        digit.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        digit.rectTransform.offsetMin = Vector2.zero;
        digit.rectTransform.offsetMax = Vector2.zero;

        var text = Wrapped("StepText" + number, richText, 15f, VoiceUiKit.TextPrimary, StepTextWidth);
        text.richText = true;
        float height = Mathf.Max(24f, Preferred(text, StepTextWidth));
        TopLeft(text.rectTransform, StepTextLeft, y, StepTextWidth, height);
        return y - height - StepGap;
    }

    private static TextMeshProUGUI Wrapped(string name, string content, float size, Color32 color, float width)
    {
        var text = VoiceUiKit.Text(name, _rootRect!, content, size, color, TextAlignmentOptions.TopLeft);
        text.enableWordWrapping = true;
        text.lineSpacing = 4f;
        text.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        text.rectTransform.sizeDelta = new Vector2(width, 0f);
        return text;
    }

    private static float Preferred(TextMeshProUGUI text, float width)
        => Mathf.Ceil(text.GetPreferredValues(text.text, width, 0f).y);

    private static void TopLeft(RectTransform rect, float x, float y, float width, float height)
    {
        rect.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(x, y);
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
                Mathf.Max(0f, safe.height - 48f) / Mathf.Max(1f, _panelHeight)));
        float eased = 1f - Mathf.Pow(1f - _appear, 3f);
        float scale = fit * Mathf.Lerp(0.98f, 1f, eased);
        _rootRect.localScale = new Vector3(scale, scale, 1f);
        _rootRect.anchoredPosition = new Vector2(
            safe.center.x - canvas.center.x, safe.center.y - canvas.center.y);
        _group.alpha = eased;
    }
}

/// <summary>Voice Settings row shown at the top of every tab while the helper is blocked.</summary>
internal sealed class HelperBlockedBannerRow : VoiceUiKit.Row
{
    internal const float RowHeight = 80f;
    private const float CardHeight = 72f;
    private const float RetryWidth = 80f;
    private const float HowToFixWidth = 112f;
    private const float ButtonHeight = 34f;

    private VoiceUiKit.ActionButton _howToFix = null!;
    private VoiceUiKit.ActionButton _retry = null!;

    internal HelperBlockedBannerRow Build(RectTransform pane, float width, float y)
    {
        var kind = VoiceHelperBlockState.Kind;
        BuildBase(pane, VoiceConnectionStatusPolicy.HelperBlockedTitle(kind), width, y, RowHeight);

        var card = VoiceUiKit.Panel("BannerBorder", Root, VoiceUiKit.Divider);
        card.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        card.rectTransform.sizeDelta = new Vector2(0f, CardHeight);
        card.rectTransform.anchoredPosition = Vector2.zero;
        card.rectTransform.SetAsFirstSibling();
        var fill = VoiceUiKit.Panel("Fill", card.transform, VoiceUiKit.PanelInner);
        fill.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        fill.rectTransform.offsetMin = Vector2.one;
        fill.rectTransform.offsetMax = -Vector2.one;

        var dot = VoiceUiKit.Panel("Dot", Root, VoiceUiKit.Danger, true, true);
        dot.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f));
        dot.rectTransform.sizeDelta = new Vector2(8f, 8f);
        dot.rectTransform.anchoredPosition = new Vector2(EdgePad + 4f, -CardHeight * 0.5f);

        float textLeft = EdgePad + 20f;
        float textWidth = Mathf.Max(80f, width - textLeft - EdgePad - HowToFixWidth - RetryWidth - 24f);
        Title.fontSize = 16f;
        Title.fontSizeMax = 16f;
        Title.fontSizeMin = 13f;
        Title.fontStyle = FontStyles.Bold;
        Title.color = VoiceUiKit.TextBright;
        Title.richText = false;
        Title.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        Title.rectTransform.sizeDelta = new Vector2(textWidth, 22f);
        Title.rectTransform.anchoredPosition = new Vector2(textLeft, -14f);

        var detail = VoiceUiKit.Text("BannerDetail", Root,
            "Restore or allow PerfectCommsAudio.exe in your antivirus, then press Retry.",
            13f, VoiceUiKit.TextMuted, TextAlignmentOptions.Left);
        detail.richText = false;
        detail.overflowMode = TextOverflowModes.Ellipsis;
        detail.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        detail.rectTransform.sizeDelta = new Vector2(textWidth, 20f);
        detail.rectTransform.anchoredPosition = new Vector2(textLeft, -38f);

        float buttonY = -(CardHeight - ButtonHeight) * 0.5f;
        float retryX = width - EdgePad - RetryWidth;
        _retry = new VoiceUiKit.ActionButton(Root, "Retry", RetryWidth, ButtonHeight,
            retryX, buttonY, VoiceUiKit.ButtonKind.Primary, VoiceHelperBlockedNotice.Retry,
            () => VoiceHelperBlockedNotice.CanRetry);
        _howToFix = new VoiceUiKit.ActionButton(Root, "How to fix", HowToFixWidth, ButtonHeight,
            retryX - 10f - HowToFixWidth, buttonY, VoiceUiKit.ButtonKind.Secondary, HowToFix);
        return this;
    }

    private static void HowToFix()
    {
        if (!VoiceHelperBlockedNotice.CanShowInCurrentPhase)
        {
            VoiceHelperBlockedNotice.OpenGuide();
            return;
        }
        VoiceSettingsPanel.ForceClose();
        VoiceHelperBlockedNotice.Show();
    }

    public override void OnMouseDown()
    {
        if (_howToFix.IsPointerOver)
        {
            VoiceUiKit.RebindRow.CancelCaptureForExternalPointer();
            HowToFix();
        }
        else if (_retry.IsPointerOver)
        {
            VoiceUiKit.RebindRow.CancelCaptureForExternalPointer();
            VoiceHelperBlockedNotice.Retry();
        }
    }

    public override void Tick(float dt)
    {
        var kind = VoiceHelperBlockState.Kind;
        if (kind != VoiceHelperBlockKind.None)
        {
            var title = VoiceConnectionStatusPolicy.HelperBlockedTitle(kind);
            if (Title.text != title) Title.text = title;
        }
        _howToFix.Tick(dt, allowInput: false);
        _retry.Tick(dt, allowInput: false);
    }
}
#endif
