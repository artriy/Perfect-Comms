using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Canvas-native 15-player speaking-bar preview shared by local settings and first-run setup.
///
/// This renderer intentionally owns no Camera, RenderTexture, world-space TextMeshPro,
/// DontDestroyOnLoad object, real-HUD sprite, or real-HUD style cache. The previous off-screen
/// world renderer could outlive the panel and participate in the game's HUD lifecycle. Keeping
/// every preview object beneath the existing UI canvas makes closing or destroying the panel the
/// complete lifecycle boundary.
/// </summary>
internal sealed class SpeakingBarLivePreview
{
    private const float DefaultHeaderHeight = 76f;
    private const float DefaultViewportMaxWidth = 420f;
    private const float DefaultViewportMaxHeight = 236.25f;
    private const float DefaultViewportY = 34f;
    private const float DefaultRevealSlide = 28f;
    private const float EmbeddedCardWidth = 424f;
    private const float EmbeddedCardHeight = 342f;
    private const float EmbeddedHeaderHeight = 52f;
    private const float EmbeddedViewportMaxWidth = 380f;
    private const float EmbeddedViewportMaxHeight = 213.75f;
    private const float EmbeddedViewportY = 2f;
    private const float EmbeddedRevealSlide = 12f;
    private const float PixelsPerWorldUnit = 36f;
    private const int SlotsPerPrewarmTick = 3;

    private static readonly Color BackdropColor = new(0f, 0f, 0f, 0.5f);
    private static readonly Color32 SpeakingGreen = new(46, 204, 113, 255);

    // A private synthetic palette keeps the editor independent from Among Us palette
    // registration and Town of Us colour initialization.
    private static readonly Color32[] PreviewColors =
    {
        new(198, 17, 17, 255),
        new(19, 46, 210, 255),
        new(17, 127, 45, 255),
        new(238, 84, 187, 255),
        new(240, 125, 13, 255),
        new(246, 246, 87, 255),
        new(63, 71, 78, 255),
        new(215, 225, 241, 255),
        new(107, 47, 188, 255),
        new(113, 73, 30, 255),
        new(56, 255, 221, 255),
        new(80, 240, 57, 255),
        new(80, 80, 96, 255),
        new(255, 164, 190, 255),
        new(255, 210, 45, 255),
    };

    private sealed class PreviewSlot
    {
        internal readonly int Index;
        internal RectTransform Root = null!;
        internal RectTransform RingRoot = null!;
        internal RectTransform AvatarRoot = null!;
        internal Image Ring = null!;
        internal Image Backpack = null!;
        internal Image Body = null!;
        internal Image Visor = null!;
        internal TextMeshProUGUI Label = null!;
        internal float LabelWidth;
        internal float LabelHeight;
        internal float SmoothedLevel;

        internal PreviewSlot(int index)
        {
            Index = index;
            LabelWidth = Mathf.Min(
                SpeakingBarVisualMetrics.MaximumLabelWidth,
                0.18f + SpeakingBarPreviewRoster.Name(index).Length * 0.085f);
            LabelHeight = 0.28f;
            SmoothedLevel = global::VoiceChatPlugin.VoiceLevelVisual.NormalizeVoiceLevel(
                SpeakingBarPreviewRoster.VoiceLevel(index));
        }
    }

    internal readonly RectTransform CardRoot;

    private readonly CanvasGroup _cardGroup;
    private readonly RectTransform _viewportRoot;
    private readonly RectTransform _barRoot;
    private readonly Image _backdrop;
    private readonly TextMeshProUGUI _badgeText;
    private readonly TextMeshProUGUI _statusText;
    private readonly TextMeshProUGUI _detailText;
    private readonly List<PreviewSlot> _slots = new(SpeakingBarPreviewRoster.PlayerCount);
    private readonly Vector2 _previewLocalCenter;
    private readonly bool _embedded;
    private readonly float _viewportMaxWidth;
    private readonly float _viewportMaxHeight;
    private readonly float _revealSlide;
    private readonly float _presentationStartScale;
    private readonly string _liveBadgeLabel;

    private SpeakingBarPreviewSettings _lastSettings;
    private bool _hasLastSettings;
    private float _lastViewportWidth = -1f;
    private float _lastViewportHeight = -1f;
    private int _lastLineCount;
    private bool _shouldRender;
    private bool _disposed;

    internal bool IsUnavailable => _disposed || CardRoot == null;
    internal bool IsWarmupReady => !_disposed &&
        CardRoot != null &&
        _viewportRoot != null &&
        _barRoot != null &&
        _slots.Count == SpeakingBarPreviewRoster.PlayerCount;

    internal SpeakingBarLivePreview(RectTransform settingsRoot, float? previewLocalCenterX = null)
        : this(
            settingsRoot,
            new Vector2(previewLocalCenterX ?? DefaultPreviewLocalCenterX, 0f),
            embedded: false)
    {
    }

    internal SpeakingBarLivePreview(RectTransform parent, Vector2 localCenter, bool embedded)
    {
        _previewLocalCenter = localCenter;
        _embedded = embedded;
        _viewportMaxWidth = embedded ? EmbeddedViewportMaxWidth : DefaultViewportMaxWidth;
        _viewportMaxHeight = embedded ? EmbeddedViewportMaxHeight : DefaultViewportMaxHeight;
        _revealSlide = embedded ? EmbeddedRevealSlide : DefaultRevealSlide;
        _presentationStartScale = embedded ? 1f : 0.96f;
        _liveBadgeLabel = embedded ? "LIVE" : "\u25CF  LIVE";

        float cardWidth = embedded
            ? EmbeddedCardWidth
            : SpeakingBarLivePreviewWorkspacePolicy.PreviewWidth;
        float cardHeight = embedded
            ? EmbeddedCardHeight
            : SpeakingBarLivePreviewWorkspacePolicy.PreviewHeight;
        float headerHeight = embedded ? EmbeddedHeaderHeight : DefaultHeaderHeight;
        float viewportY = embedded ? EmbeddedViewportY : DefaultViewportY;

        CardRoot = VoiceUiKit.Rect("VC_SpeakingBarLivePreview", parent);
        CardRoot.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        CardRoot.sizeDelta = new Vector2(cardWidth, cardHeight);
        CardRoot.anchoredPosition = _previewLocalCenter;
        CardRoot.localScale = new Vector3(
            _presentationStartScale,
            _presentationStartScale,
            1f);

        _cardGroup = CardRoot.gameObject.AddComponent<CanvasGroup>();
        _cardGroup.alpha = 0f;
        _cardGroup.interactable = false;
        _cardGroup.blocksRaycasts = false;

        if (!embedded)
        {
            var shadow = VoiceUiKit.GlowImage("PreviewShadow", CardRoot, VoiceUiKit.PanelShadow);
            shadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            shadow.rectTransform.offsetMin = new Vector2(-38f, -46f);
            shadow.rectTransform.offsetMax = new Vector2(38f, 34f);

            var rim = VoiceUiKit.GlowImage("PreviewRim", CardRoot, VoiceUiKit.AccentGlow);
            rim.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            rim.rectTransform.offsetMin = new Vector2(-18f, -18f);
            rim.rectTransform.offsetMax = new Vector2(18f, 18f);
        }

        var surface = VoiceUiKit.Rect("PreviewSurface", CardRoot);
        surface.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        surface.offsetMin = Vector2.zero;
        surface.offsetMax = Vector2.zero;
        var surfaceImage = surface.gameObject.AddComponent<Image>();
        surfaceImage.sprite = embedded ? VoiceUiKit.Rounded(true) : VoiceUiKit.PanelGradient();
        surfaceImage.type = Image.Type.Sliced;
        surfaceImage.color = embedded ? new Color32(17, 22, 30, 248) : Color.white;
        surfaceImage.raycastTarget = false;

        var header = VoiceUiKit.Rect("PreviewHeader", CardRoot);
        header.Anchor(
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(0.5f, 1f));
        header.sizeDelta = new Vector2(0f, headerHeight);
        header.anchoredPosition = Vector2.zero;
        var headerImage = header.gameObject.AddComponent<Image>();
        headerImage.sprite = VoiceUiKit.HeaderGradient();
        headerImage.type = Image.Type.Sliced;
        headerImage.color = Color.white;
        headerImage.raycastTarget = false;

        var divider = VoiceUiKit.Panel(
            "PreviewHeaderDivider",
            header,
            VoiceUiKit.Divider,
            rounded: false);
        divider.rectTransform.Anchor(
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0.5f, 0f));
        divider.rectTransform.sizeDelta = new Vector2(0f, embedded ? 1f : 1.5f);
        divider.rectTransform.anchoredPosition = Vector2.zero;

        var title = VoiceUiKit.Text(
            "PreviewTitle",
            header,
            embedded ? "HUD PREVIEW" : "HUD LIVE PREVIEW",
            embedded ? 20f : 25f,
            VoiceUiKit.TextBright,
            TextAlignmentOptions.Left,
            FontStyles.Bold);
        title.characterSpacing = embedded ? 1.25f : 3f;
        title.rectTransform.Anchor(
            new Vector2(0f, 0.5f),
            new Vector2(1f, 0.5f),
            new Vector2(0f, 0.5f));
        title.rectTransform.sizeDelta = new Vector2(embedded ? -118f : -155f, headerHeight);
        title.rectTransform.anchoredPosition = new Vector2(embedded ? 18f : 26f, 0f);

        var badge = VoiceUiKit.Panel(
            "LiveBadge",
            header,
            embedded ? new Color32(22, 55, 40, 210) : new Color32(18, 64, 44, 230),
            soft: true);
        badge.rectTransform.Anchor(
            new Vector2(1f, 0.5f),
            new Vector2(1f, 0.5f),
            new Vector2(1f, 0.5f));
        badge.rectTransform.sizeDelta = new Vector2(embedded ? 82f : 104f, embedded ? 24f : 32f);
        badge.rectTransform.anchoredPosition = new Vector2(embedded ? -16f : -22f, 0f);
        _badgeText = VoiceUiKit.Text(
            "LiveBadgeText",
            badge.rectTransform,
            _liveBadgeLabel,
            embedded ? 15f : 14f,
            new Color32(118, 239, 165, 255),
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        _badgeText.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _badgeText.rectTransform.offsetMin = Vector2.zero;
        _badgeText.rectTransform.offsetMax = Vector2.zero;

        if (!embedded)
        {
            var viewportGlow = VoiceUiKit.GlowImage(
                "ViewportGlow",
                CardRoot,
                new Color32(34, 211, 238, 34));
            viewportGlow.rectTransform.Anchor(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f));
            viewportGlow.rectTransform.sizeDelta = new Vector2(
                _viewportMaxWidth + 24f,
                _viewportMaxHeight + 24f);
            viewportGlow.rectTransform.anchoredPosition = new Vector2(0f, viewportY);
        }

        var viewportFrame = VoiceUiKit.Panel(
            "GameViewportFrame",
            CardRoot,
            new Color32(4, 7, 11, 255),
            rounded: true,
            soft: true);
        viewportFrame.rectTransform.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        viewportFrame.rectTransform.sizeDelta = new Vector2(
            _viewportMaxWidth + 4f,
            _viewportMaxHeight + 4f);
        viewportFrame.rectTransform.anchoredPosition = new Vector2(0f, viewportY);
        viewportFrame.rectTransform.gameObject.AddComponent<RectMask2D>();

        _viewportRoot = VoiceUiKit.Rect("CanvasPreviewViewport", viewportFrame.rectTransform);
        _viewportRoot.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        _viewportRoot.sizeDelta = new Vector2(_viewportMaxWidth, _viewportMaxHeight);
        _viewportRoot.anchoredPosition = Vector2.zero;

        _barRoot = VoiceUiKit.Rect("CanvasSpeakingBar", _viewportRoot);
        _barRoot.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        _barRoot.sizeDelta = Vector2.zero;
        _barRoot.anchoredPosition = Vector2.zero;

        var backdropRect = VoiceUiKit.Rect("CanvasSpeakingBarBackdrop", _barRoot);
        backdropRect.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        _backdrop = backdropRect.gameObject.AddComponent<Image>();
        _backdrop.sprite = VoiceUiKit.Rounded(true);
        _backdrop.type = Image.Type.Sliced;
        _backdrop.color = BackdropColor;
        _backdrop.raycastTarget = false;

        _statusText = VoiceUiKit.Text(
            "PreviewStatus",
            CardRoot,
            "",
            18f,
            VoiceUiKit.TextPrimary,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        _statusText.rectTransform.Anchor(
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0.5f));
        _statusText.rectTransform.sizeDelta =
            new Vector2(embedded ? 396f : 410f, embedded ? 22f : 32f);
        _statusText.rectTransform.anchoredPosition =
            new Vector2(0f, embedded ? 45f : 70f);

        _detailText = VoiceUiKit.Text(
            "PreviewDetails",
            CardRoot,
            "10 alive / 5 ghosts / synthetic voice activity",
            14f,
            VoiceUiKit.TextMuted,
            TextAlignmentOptions.Center);
        if (embedded) _detailText.fontSize = 16f;
        _detailText.rectTransform.Anchor(
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(0.5f, 0.5f));
        _detailText.rectTransform.sizeDelta =
            new Vector2(embedded ? 396f : 410f, embedded ? 20f : 28f);
        _detailText.rectTransform.anchoredPosition =
            new Vector2(0f, embedded ? 20f : 39f);

        if (!embedded)
        {
            var note = VoiceUiKit.Text(
                "PreviewNote",
                CardRoot,
                "Canvas-only preview / isolated from the real game HUD",
                12f,
                VoiceUiKit.TextFaint,
                TextAlignmentOptions.Center);
            note.rectTransform.Anchor(
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0.5f));
            note.rectTransform.sizeDelta = new Vector2(410f, 24f);
            note.rectTransform.anchoredPosition = new Vector2(0f, 15f);
        }

        SetWarmupStatus();
        CardRoot.gameObject.SetActive(false);
    }

    private static float DefaultPreviewLocalCenterX
        => SpeakingBarLivePreviewWorkspacePolicy.SettingsWidth * 0.5f
           + SpeakingBarLivePreviewWorkspacePolicy.Gap
           + SpeakingBarLivePreviewWorkspacePolicy.PreviewWidth * 0.5f;

    internal void SetPresentation(float reveal, bool shouldRender)
    {
        if (_disposed) return;
        _shouldRender = shouldRender;
        reveal = Mathf.Clamp01(reveal);
        bool visible = reveal > 0.001f || shouldRender;
        if (CardRoot.gameObject.activeSelf != visible)
            CardRoot.gameObject.SetActive(visible);

        _cardGroup.alpha = reveal;
        CardRoot.anchoredPosition = _previewLocalCenter +
            new Vector2((1f - reveal) * _revealSlide, 0f);
        float cardScale = _embedded
            ? 1f
            : Mathf.Lerp(_presentationStartScale, 1f, reveal);
        CardRoot.localScale = new Vector3(cardScale, cardScale, 1f);
    }

    internal void Tick(VoiceChatLocalSettings settings, float unscaledDeltaTime)
        => Tick(SpeakingBarPreviewSettings.From(settings), unscaledDeltaTime);

    internal bool Prewarm(VoiceChatLocalSettings settings, float unscaledDeltaTime)
        => Prewarm(SpeakingBarPreviewSettings.From(settings), unscaledDeltaTime);

    internal bool Prewarm(SpeakingBarPreviewSettings settings, float unscaledDeltaTime)
    {
        if (_disposed) return false;
        _ = unscaledDeltaTime;
        AdvanceSlotBuild();
        if (!IsWarmupReady) return false;
        _badgeText.text = _liveBadgeLabel;
        _badgeText.color = new Color32(118, 239, 165, 255);
        UpdatePreview(settings);
        return true;
    }

    internal void Tick(SpeakingBarPreviewSettings settings, float unscaledDeltaTime)
    {
        if (_disposed || !_shouldRender || !CardRoot.gameObject.activeInHierarchy) return;
        UpdatePreview(settings);
        AnimateRings(Mathf.Max(0f, unscaledDeltaTime));
    }

    internal void Suspend()
    {
        _shouldRender = false;
        if (CardRoot != null) CardRoot.gameObject.SetActive(false);
    }

    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shouldRender = false;
        if (CardRoot != null) CardRoot.gameObject.SetActive(false);
        _slots.Clear();
        if (CardRoot != null) Object.Destroy(CardRoot.gameObject);
    }

    private void SetWarmupStatus()
    {
        _badgeText.text = "LOADING";
        _badgeText.color = VoiceUiKit.TextMuted;
        _statusText.text = "PREPARING PREVIEW";
        _detailText.text = "Building the canvas HUD preview";
    }

    private void AdvanceSlotBuild()
    {
        int remaining = SlotsPerPrewarmTick;
        while (remaining-- > 0 && _slots.Count < SpeakingBarPreviewRoster.PlayerCount)
            _slots.Add(CreateSlot(_slots.Count));
    }

    private PreviewSlot CreateSlot(int index)
    {
        var slot = new PreviewSlot(index);
        bool ghost = SpeakingBarPreviewRoster.IsGhost(index);
        Color32 bodyColor = PreviewColors[index % PreviewColors.Length];
        Color32 shadowColor = Darken(bodyColor, 0.52f);
        if (ghost)
        {
            bodyColor.a = 118;
            shadowColor.a = 105;
        }

        slot.Root = VoiceUiKit.Rect($"CanvasPreviewSlot_{index}", _barRoot);
        slot.Root.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        slot.Root.sizeDelta = Vector2.zero;

        slot.RingRoot = VoiceUiKit.Rect("SpeakingRing", slot.Root);
        slot.RingRoot.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        slot.RingRoot.sizeDelta = Vector2.one *
            (SpeakingBarVisualMetrics.RingScale * PixelsPerWorldUnit);
        slot.Ring = slot.RingRoot.gameObject.AddComponent<Image>();
        slot.Ring.sprite = VoiceUiKit.Rounded(true);
        slot.Ring.type = Image.Type.Sliced;
        slot.Ring.color = SpeakingGreen;
        slot.Ring.raycastTarget = false;

        var ringCutout = VoiceUiKit.Rect("RingCutout", slot.RingRoot);
        ringCutout.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        ringCutout.sizeDelta = slot.RingRoot.sizeDelta * 0.68f;
        var ringCutoutImage = ringCutout.gameObject.AddComponent<Image>();
        ringCutoutImage.sprite = VoiceUiKit.Rounded(true);
        ringCutoutImage.type = Image.Type.Sliced;
        ringCutoutImage.color = new Color32(4, 7, 11, 255);
        ringCutoutImage.raycastTarget = false;
        slot.RingRoot.gameObject.SetActive(SpeakingBarPreviewRoster.VoiceLevel(index) > 0f);

        slot.AvatarRoot = VoiceUiKit.Rect("SyntheticCrewmate", slot.Root);
        slot.AvatarRoot.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        slot.AvatarRoot.sizeDelta = Vector2.zero;
        slot.AvatarRoot.anchoredPosition = Vector2.zero;

        slot.Backpack = CreateAvatarPart(
            "Backpack",
            slot.AvatarRoot,
            new Vector2(6.2f, 13.2f),
            new Vector2(-6.4f, -0.6f),
            shadowColor);
        slot.Body = CreateAvatarPart(
            "Body",
            slot.AvatarRoot,
            new Vector2(13.2f, 18.4f),
            Vector2.zero,
            bodyColor);
        Color32 visorColor = new(174, 231, 241, ghost ? (byte)132 : (byte)255);
        slot.Visor = CreateAvatarPart(
            "Visor",
            slot.AvatarRoot,
            new Vector2(7.6f, 4.8f),
            new Vector2(3.4f, 3.6f),
            visorColor);

        var labelRect = VoiceUiKit.Rect("Name", slot.Root);
        slot.Label = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
        if (_statusText.font != null) slot.Label.font = _statusText.font;
        slot.Label.text = SpeakingBarPreviewRoster.Name(index);
        slot.Label.fontSize = 9f;
        slot.Label.color = ghost ? new Color32(224, 234, 245, 158) : Color.white;
        slot.Label.alignment = TextAlignmentOptions.Center;
        slot.Label.fontStyle = FontStyles.Normal;
        slot.Label.enableWordWrapping = false;
        slot.Label.overflowMode = TextOverflowModes.Ellipsis;
        slot.Label.raycastTarget = false;
        slot.Label.rectTransform.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        slot.Label.rectTransform.sizeDelta = new Vector2(
            SpeakingBarVisualMetrics.MaximumLabelWidth * PixelsPerWorldUnit,
            SpeakingBarVisualMetrics.MaximumLabelHeight * PixelsPerWorldUnit);

        return slot;
    }

    private static Image CreateAvatarPart(
        string name,
        Transform parent,
        Vector2 size,
        Vector2 position,
        Color32 color)
    {
        var rect = VoiceUiKit.Rect(name, parent);
        rect.Anchor(
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f));
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = VoiceUiKit.Rounded(true);
        image.type = Image.Type.Sliced;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static Color32 Darken(Color32 color, float amount)
        => new(
            (byte)Mathf.RoundToInt(color.r * amount),
            (byte)Mathf.RoundToInt(color.g * amount),
            (byte)Mathf.RoundToInt(color.b * amount),
            color.a);

    private void UpdatePreview(SpeakingBarPreviewSettings settings)
    {
        float width = _viewportRoot.rect.width;
        float height = _viewportRoot.rect.height;
        if (width <= 1f) width = _viewportMaxWidth;
        if (height <= 1f) height = _viewportMaxHeight;

        if (!_hasLastSettings ||
            settings != _lastSettings ||
            Mathf.Abs(width - _lastViewportWidth) > 0.01f ||
            Mathf.Abs(height - _lastViewportHeight) > 0.01f)
        {
            Layout(settings, width / PixelsPerWorldUnit, height / PixelsPerWorldUnit);
            _lastSettings = settings;
            _hasLastSettings = true;
            _lastViewportWidth = width;
            _lastViewportHeight = height;
        }
    }

    private void Layout(
        SpeakingBarPreviewSettings settings,
        float availableWidth,
        float availableHeight)
    {
        if (_slots.Count == 0) return;

        bool vertical = settings.ManualLayout
            ? settings.ManualOrientation == VoiceControlsLayout.Vertical
            : SpeakingBarLayoutPolicy.OrientationFor(settings.Position) ==
              VoiceControlsLayout.Vertical;
        var namePosition = SpeakingBarLayoutPolicy.ResolveNamePosition(
            settings.NamePosition,
            settings.ManualLayout,
            settings.ManualOrientation,
            settings.ManualX,
            settings.ManualY,
            settings.Position);

        float crossPitch = SpeakingBarVisualMetrics.SlotWidth;
        float itemMinX = float.MaxValue;
        float itemMaxX = float.MinValue;
        float itemMinY = float.MaxValue;
        float itemMaxY = float.MinValue;
        foreach (var slot in _slots)
        {
            crossPitch = Mathf.Max(crossPitch, RequiredSlotPitch(slot, namePosition));
            GetSlotXExtents(slot, namePosition, 0f, out float minX, out float maxX);
            GetSlotYExtents(slot, namePosition, 0f, out float minY, out float maxY);
            itemMinX = Mathf.Min(itemMinX, minX);
            itemMaxX = Mathf.Max(itemMaxX, maxX);
            itemMinY = Mathf.Min(itemMinY, minY);
            itemMaxY = Mathf.Max(itemMaxY, maxY);
        }

        float primaryPitch = namePosition == SpeakingBarNamePosition.Top
            ? SpeakingBarVisualMetrics.SlotHeight +
              SpeakingBarVisualMetrics.TopNameExtraPitch
            : SpeakingBarVisualMetrics.SlotHeight;
        float outerPad = SpeakingBarVisualMetrics.BackdropPad * 2f;
        float itemWidth = itemMaxX - itemMinX + outerPad;
        float itemHeight = itemMaxY - itemMinY + outerPad;
        float requestedScale = SpeakingBarScalePolicy.ToRenderedScale(settings.Scale);
        bool singleLane = !settings.ManualLayout &&
            SpeakingBarLayoutPolicy.UsesSingleVerticalLaneFor(
                settings.Position,
                settings.SideLayout);
        var solution = SpeakingBarLayoutPolicy.SolveTwoDimensional(
            _slots.Count,
            vertical,
            itemWidth,
            itemHeight,
            crossPitch,
            primaryPitch,
            availableWidth,
            availableHeight,
            requestedScale,
            maxItemsPerLine: singleLane
                ? _slots.Count
                : SpeakingBarLayoutPolicy.PreferredMaxItemsPerLine,
            requiredLineCount: singleLane ? 1 : null);

        var primaryDirection = SpeakingBarLayoutPolicy.ResolvePrimaryDirection(
            settings.ManualLayout,
            settings.Position,
            vertical,
            settings.ManualY);
        var overflowDirection = SpeakingBarLayoutPolicy.ResolveOverflowDirection(
            settings.ManualLayout,
            settings.Position,
            vertical,
            settings.ManualX,
            settings.ManualY);
        bool centerVertical = SpeakingBarLayoutPolicy.CentersVerticalPrimaryLine(
            settings.ManualLayout,
            settings.Position,
            settings.ManualY);
        bool centerOverflow = SpeakingBarLayoutPolicy.CentersOverflowLines(
            settings.ManualLayout,
            vertical,
            settings.ManualX,
            settings.ManualY);

        float contentMinX = 0f;
        float contentMaxX = 0f;
        float contentMinY = 0f;
        float contentMaxY = 0f;
        bool any = false;
        bool avatarFacesLeft = SpeakingBarLayoutPolicy.ResolveAvatarFacesLeft(
            settings.ManualLayout,
            settings.Position,
            settings.ManualAvatarFacing);

        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var placement = SpeakingBarLayoutPolicy.GetPlacement(
                i,
                _slots.Count,
                solution.LineCount);
            var coordinates = SpeakingBarLayoutPolicy.CoordinatesFor(
                placement,
                solution.LineCount,
                vertical,
                primaryDirection,
                overflowDirection,
                centerVertical,
                centerOverflow,
                crossPitch,
                primaryPitch);

            PositionSlot(slot, namePosition, coordinates.X, coordinates.Y, avatarFacesLeft);
            GetSlotXExtents(
                slot,
                namePosition,
                coordinates.X,
                out float minX,
                out float maxX);
            GetSlotYExtents(
                slot,
                namePosition,
                coordinates.Y,
                out float minY,
                out float maxY);
            if (!any)
            {
                contentMinX = minX;
                contentMaxX = maxX;
                contentMinY = minY;
                contentMaxY = maxY;
                any = true;
            }
            else
            {
                contentMinX = Mathf.Min(contentMinX, minX);
                contentMaxX = Mathf.Max(contentMaxX, maxX);
                contentMinY = Mathf.Min(contentMinY, minY);
                contentMaxY = Mathf.Max(contentMaxY, maxY);
            }
        }

        _barRoot.localScale = Vector3.one * solution.EffectiveScale;
        _backdrop.enabled = settings.Backdrop;
        _backdrop.color = BackdropColor;
        _backdrop.rectTransform.sizeDelta = new Vector2(
            contentMaxX - contentMinX + SpeakingBarVisualMetrics.BackdropPad * 2f,
            contentMaxY - contentMinY + SpeakingBarVisualMetrics.BackdropPad * 2f) *
            PixelsPerWorldUnit;
        _backdrop.rectTransform.anchoredPosition = new Vector2(
            (contentMinX + contentMaxX) * 0.5f,
            (contentMinY + contentMaxY) * 0.5f) * PixelsPerWorldUnit;

        Vector2 anchor = settings.ManualLayout
            ? new Vector2(settings.ManualX, settings.ManualY)
            : PresetViewportAnchor(settings.Position);
        float rootX = (anchor.x - 0.5f) * availableWidth;
        float rootY = (anchor.y - 0.5f) * availableHeight;
        float clampMinX = contentMinX -
            (settings.Backdrop ? SpeakingBarVisualMetrics.BackdropPad : 0f);
        float clampMaxX = contentMaxX +
            (settings.Backdrop ? SpeakingBarVisualMetrics.BackdropPad : 0f);
        float clampMinY = contentMinY -
            (settings.Backdrop ? SpeakingBarVisualMetrics.BackdropPad : 0f);
        float clampMaxY = contentMaxY +
            (settings.Backdrop ? SpeakingBarVisualMetrics.BackdropPad : 0f);
        rootX += CalculateAxisShift(
            rootX + clampMinX * solution.EffectiveScale,
            rootX + clampMaxX * solution.EffectiveScale,
            -availableWidth * 0.5f,
            availableWidth * 0.5f);
        rootY += CalculateAxisShift(
            rootY + clampMinY * solution.EffectiveScale,
            rootY + clampMaxY * solution.EffectiveScale,
            -availableHeight * 0.5f,
            availableHeight * 0.5f);
        _barRoot.anchoredPosition = new Vector2(rootX, rootY) * PixelsPerWorldUnit;

        _lastLineCount = solution.LineCount;
        UpdateStatus(settings, solution.LineCount);
    }

    private static float RequiredSlotPitch(
        PreviewSlot slot,
        SpeakingBarNamePosition namePosition)
    {
        const float ringHalf = SpeakingBarVisualMetrics.RingScale * 0.5f;
        return namePosition switch
        {
            SpeakingBarNamePosition.Left or SpeakingBarNamePosition.Right
                => SpeakingBarVisualMetrics.LabelSideOffset +
                   slot.LabelWidth +
                   ringHalf +
                   SpeakingBarVisualMetrics.NameGap,
            _ => slot.LabelWidth + SpeakingBarVisualMetrics.NameGap,
        };
    }

    private static void GetSlotXExtents(
        PreviewSlot slot,
        SpeakingBarNamePosition namePosition,
        float iconX,
        out float min,
        out float max)
    {
        const float ringHalf = SpeakingBarVisualMetrics.RingScale * 0.5f;
        switch (namePosition)
        {
            case SpeakingBarNamePosition.Left:
                min = iconX - SpeakingBarVisualMetrics.LabelSideOffset - slot.LabelWidth;
                max = iconX + ringHalf;
                break;
            case SpeakingBarNamePosition.Right:
                min = iconX - ringHalf;
                max = iconX + SpeakingBarVisualMetrics.LabelSideOffset + slot.LabelWidth;
                break;
            default:
                float halfWidth = Mathf.Max(ringHalf, slot.LabelWidth * 0.5f);
                min = iconX - halfWidth;
                max = iconX + halfWidth;
                break;
        }
    }

    private static void GetSlotYExtents(
        PreviewSlot slot,
        SpeakingBarNamePosition namePosition,
        float iconY,
        out float min,
        out float max)
    {
        const float ringHalf = SpeakingBarVisualMetrics.RingScale * 0.5f;
        float labelHalf = slot.LabelHeight * 0.5f;
        switch (namePosition)
        {
            case SpeakingBarNamePosition.Top:
                min = iconY - ringHalf;
                max = iconY + SpeakingBarVisualMetrics.LabelOffset + labelHalf;
                break;
            case SpeakingBarNamePosition.Bottom:
                min = iconY - SpeakingBarVisualMetrics.LabelOffset - labelHalf;
                max = iconY + ringHalf;
                break;
            default:
                float halfHeight = Mathf.Max(ringHalf, labelHalf);
                min = iconY - halfHeight;
                max = iconY + halfHeight;
                break;
        }
    }

    private static void PositionSlot(
        PreviewSlot slot,
        SpeakingBarNamePosition namePosition,
        float x,
        float y,
        bool avatarFacesLeft)
    {
        slot.Root.anchoredPosition = new Vector2(x, y) * PixelsPerWorldUnit;
        slot.AvatarRoot.localScale = new Vector3(avatarFacesLeft ? -1f : 1f, 1f, 1f);

        Vector2 labelPosition;
        TextAlignmentOptions alignment;
        Vector2 pivot;
        switch (namePosition)
        {
            case SpeakingBarNamePosition.Top:
                labelPosition = new Vector2(0f, SpeakingBarVisualMetrics.LabelOffset);
                alignment = TextAlignmentOptions.Center;
                pivot = new Vector2(0.5f, 0.5f);
                break;
            case SpeakingBarNamePosition.Left:
                labelPosition = new Vector2(-SpeakingBarVisualMetrics.LabelSideOffset, 0f);
                alignment = TextAlignmentOptions.Right;
                pivot = new Vector2(1f, 0.5f);
                break;
            case SpeakingBarNamePosition.Right:
                labelPosition = new Vector2(SpeakingBarVisualMetrics.LabelSideOffset, 0f);
                alignment = TextAlignmentOptions.Left;
                pivot = new Vector2(0f, 0.5f);
                break;
            default:
                labelPosition = new Vector2(0f, -SpeakingBarVisualMetrics.LabelOffset);
                alignment = TextAlignmentOptions.Center;
                pivot = new Vector2(0.5f, 0.5f);
                break;
        }

        slot.Label.rectTransform.anchoredPosition = labelPosition * PixelsPerWorldUnit;
        slot.Label.alignment = alignment;
        slot.Label.rectTransform.pivot = pivot;
    }

    private static Vector2 PresetViewportAnchor(SpeakingBarPosition position) => position switch
    {
        SpeakingBarPosition.TopLeft => new Vector2(0f, 1f),
        SpeakingBarPosition.TopMiddle => new Vector2(0.5f, 1f),
        SpeakingBarPosition.TopRight => new Vector2(1f, 1f),
        SpeakingBarPosition.MiddleLeft => new Vector2(0f, 0.5f),
        SpeakingBarPosition.MiddleRight => new Vector2(1f, 0.5f),
        SpeakingBarPosition.BottomLeft => new Vector2(0f, 0f),
        SpeakingBarPosition.BottomMiddle => new Vector2(0.5f, 0f),
        SpeakingBarPosition.BottomRight => new Vector2(1f, 0f),
        _ => new Vector2(0.5f, 1f),
    };

    private static float CalculateAxisShift(
        float min,
        float max,
        float allowedMin,
        float allowedMax)
    {
        float currentSize = max - min;
        float allowedSize = allowedMax - allowedMin;
        if (currentSize > allowedSize)
            return (allowedMin + allowedMax) * 0.5f - (min + max) * 0.5f;
        if (min < allowedMin) return allowedMin - min;
        if (max > allowedMax) return allowedMax - max;
        return 0f;
    }

    private void AnimateRings(float deltaTime)
    {
        float now = Time.unscaledTime;
        foreach (var slot in _slots)
        {
            float baseLevel = SpeakingBarPreviewRoster.VoiceLevel(slot.Index);
            bool speaking = baseLevel > 0f;
            slot.RingRoot.gameObject.SetActive(speaking);
            if (!speaking) continue;

            float wave = 0.82f + 0.18f * Mathf.Sin(now * 3.1f + slot.Index * 0.73f);
            float target = baseLevel * wave;
            slot.SmoothedLevel = global::VoiceChatPlugin.VoiceLevelVisual.SmoothLevel(
                slot.SmoothedLevel,
                target,
                deltaTime);
            float brightness = Mathf.SmoothStep(0f, 1f, slot.SmoothedLevel);
            Color color = SpeakingGreen;
            color.a = Mathf.Lerp(0.24f, 0.90f, brightness);
            slot.Ring.color = color;
            float scale = Mathf.Lerp(0.92f, 1.12f, brightness);
            slot.RingRoot.localScale = new Vector3(scale, scale, 1f);
        }
    }

    private void UpdateStatus(SpeakingBarPreviewSettings settings, int lineCount)
    {
        string placement = settings.ManualLayout
            ? $"MANUAL {settings.ManualOrientation.ToString().ToUpperInvariant()}"
            : PositionLabel(settings.Position);
        string layout = lineCount == 1 ? "1 LANE" : $"{lineCount} LINES";
        int percent = Mathf.RoundToInt(settings.Scale * 100f);
        _statusText.text = $"{placement} / {layout} / {percent}%";
        _detailText.text = settings.Backdrop
            ? "10 alive / 5 ghosts / backdrop on"
            : "10 alive / 5 ghosts / backdrop off";
    }

    private static string PositionLabel(SpeakingBarPosition position) => position switch
    {
        SpeakingBarPosition.TopLeft => "TOP LEFT",
        SpeakingBarPosition.TopMiddle => "TOP MIDDLE",
        SpeakingBarPosition.TopRight => "TOP RIGHT",
        SpeakingBarPosition.MiddleLeft => "MIDDLE LEFT",
        SpeakingBarPosition.MiddleRight => "MIDDLE RIGHT",
        SpeakingBarPosition.BottomLeft => "BOTTOM LEFT",
        SpeakingBarPosition.BottomMiddle => "BOTTOM MIDDLE",
        SpeakingBarPosition.BottomRight => "BOTTOM RIGHT",
        _ => "SPEAKING BAR",
    };
}
