using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Isolated 15-player speaking-bar renderer for the local-settings editor. It owns its
/// own world objects, camera, and render texture and never touches the real HUD roster.
/// </summary>
internal sealed class SpeakingBarLivePreview
{
    private const float HeaderHeight = 76f;
    private const float ViewportMaxWidth = 420f;
    private const float ViewportMaxHeight = 236.25f;
    private const float ViewportY = 34f;
    private const float FarWorldX = 20000f;
    private const float FarWorldY = 20000f;
    private const float FallbackOrthographicSize = 3f;
    private const float RevealSlide = 28f;
    private const int TextureMaxWidth = 960;
    private const int TextureBaseHeight = 540;
    private const int PreviewRenderingLayer = 30;
    private const float TextureRetryDelaySeconds = 1f;
    private const float WorldRetryBaseDelaySeconds = 0.75f;
    private const float WorldRetryMaximumDelaySeconds = 3f;
    private const int WorldFailureLimit = 3;

    private static readonly Color BackdropColor = new(0f, 0f, 0f, 0.5f);
    private static readonly Color32 SpeakingGreen = new(46, 204, 113, 255);

    private sealed class PreviewSlot
    {
        internal readonly int Index;
        internal GameObject? Icon;
        internal GameObject RingObject = null!;
        internal SpriteRenderer RingRenderer = null!;
        internal TextMeshPro Label = null!;
        internal float LabelWidth;
        internal float LabelHeight;
        internal float SmoothedLevel;
        internal bool StyleApplied;

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

    private readonly record struct SettingsSnapshot(
        SpeakingBarPosition Position,
        SpeakingBarSideLayout SideLayout,
        bool ManualLayout,
        VoiceControlsLayout ManualOrientation,
        SpeakingBarAvatarFacing ManualAvatarFacing,
        float ManualX,
        float ManualY,
        SpeakingBarNamePosition NamePosition,
        float Scale,
        bool Backdrop)
    {
        internal static SettingsSnapshot From(VoiceChatLocalSettings settings)
            => new(
                settings.SpeakingBarPosition.Value,
                settings.SpeakingBarSideLayout.Value,
                settings.SpeakingBarManualLayout.Value,
                settings.SpeakingBarLayout.Value,
                settings.SpeakingBarAvatarFacing.Value,
                settings.SpeakingBarX.Value,
                settings.SpeakingBarY.Value,
                settings.SpeakingBarNamePosition.Value,
                settings.SpeakingBarScale.Value,
                settings.SpeakingBarBackdrop.Value);
    }

    internal readonly RectTransform CardRoot;

    private readonly CanvasGroup _cardGroup;
    private readonly RectTransform _rawRect;
    private readonly RawImage _rawImage;
    private readonly TextMeshProUGUI _badgeText;
    private readonly TextMeshProUGUI _statusText;
    private readonly TextMeshProUGUI _detailText;
    private readonly List<PreviewSlot> _slots = new(SpeakingBarPreviewRoster.PlayerCount);

    private GameObject? _worldRoot;
    private GameObject? _barRoot;
    private GameObject? _cameraObject;
    private Camera? _camera;
    private RenderTexture? _renderTexture;
    private SpriteRenderer? _backdrop;
    private SettingsSnapshot _lastSettings;
    private bool _hasLastSettings;
    private float _lastWorldWidth = -1f;
    private float _lastWorldHeight = -1f;
    private float _lastAspect = -1f;
    private int _lastLineCount;
    private float _nextTextureRetryTime;
    private float _nextWorldRetryTime;
    private int _worldFailureCount;
    private bool _shouldRender;
    private bool _disposed;

    internal bool IsUnavailable => _disposed || _worldFailureCount >= WorldFailureLimit;

    internal SpeakingBarLivePreview(RectTransform settingsRoot)
    {
        CardRoot = VoiceUiKit.Rect("VC_SpeakingBarLivePreview", settingsRoot);
        CardRoot.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        CardRoot.sizeDelta = new Vector2(
            SpeakingBarLivePreviewWorkspacePolicy.PreviewWidth,
            SpeakingBarLivePreviewWorkspacePolicy.PreviewHeight);
        CardRoot.anchoredPosition = new Vector2(PreviewLocalCenterX, 0f);
        CardRoot.localScale = new Vector3(0.96f, 0.96f, 1f);

        _cardGroup = CardRoot.gameObject.AddComponent<CanvasGroup>();
        _cardGroup.alpha = 0f;
        _cardGroup.interactable = false;
        _cardGroup.blocksRaycasts = false;

        var shadow = VoiceUiKit.GlowImage("PreviewShadow", CardRoot, VoiceUiKit.PanelShadow);
        shadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        shadow.rectTransform.offsetMin = new Vector2(-38f, -46f);
        shadow.rectTransform.offsetMax = new Vector2(38f, 34f);

        var rim = VoiceUiKit.GlowImage("PreviewRim", CardRoot, VoiceUiKit.AccentGlow);
        rim.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        rim.rectTransform.offsetMin = new Vector2(-18f, -18f);
        rim.rectTransform.offsetMax = new Vector2(18f, 18f);

        var surface = VoiceUiKit.Rect("PreviewSurface", CardRoot);
        surface.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        surface.offsetMin = Vector2.zero;
        surface.offsetMax = Vector2.zero;
        var surfaceImage = surface.gameObject.AddComponent<Image>();
        surfaceImage.sprite = VoiceUiKit.PanelGradient();
        surfaceImage.type = Image.Type.Sliced;
        surfaceImage.color = Color.white;
        surfaceImage.raycastTarget = false;

        var header = VoiceUiKit.Rect("PreviewHeader", CardRoot);
        header.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        header.sizeDelta = new Vector2(0f, HeaderHeight);
        header.anchoredPosition = Vector2.zero;
        var headerImage = header.gameObject.AddComponent<Image>();
        headerImage.sprite = VoiceUiKit.HeaderGradient();
        headerImage.type = Image.Type.Sliced;
        headerImage.color = Color.white;
        headerImage.raycastTarget = false;

        var divider = VoiceUiKit.Panel("PreviewHeaderDivider", header, VoiceUiKit.Divider, rounded: false);
        divider.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f));
        divider.rectTransform.sizeDelta = new Vector2(0f, 1.5f);
        divider.rectTransform.anchoredPosition = Vector2.zero;

        var title = VoiceUiKit.Text("PreviewTitle", header, "HUD LIVE PREVIEW", 25f,
            VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold);
        title.characterSpacing = 3f;
        title.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0.5f));
        title.rectTransform.sizeDelta = new Vector2(-155f, HeaderHeight);
        title.rectTransform.anchoredPosition = new Vector2(26f, 0f);

        var badge = VoiceUiKit.Panel("LiveBadge", header, new Color32(18, 64, 44, 230), soft: true);
        badge.rectTransform.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
        badge.rectTransform.sizeDelta = new Vector2(104f, 32f);
        badge.rectTransform.anchoredPosition = new Vector2(-22f, 0f);
        _badgeText = VoiceUiKit.Text("LiveBadgeText", badge.rectTransform, "●  LIVE", 14f,
            new Color32(118, 239, 165, 255), TextAlignmentOptions.Center, FontStyles.Bold);
        _badgeText.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _badgeText.rectTransform.offsetMin = Vector2.zero;
        _badgeText.rectTransform.offsetMax = Vector2.zero;

        var viewportGlow = VoiceUiKit.GlowImage("ViewportGlow", CardRoot, new Color32(34, 211, 238, 34));
        viewportGlow.rectTransform.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        viewportGlow.rectTransform.sizeDelta = new Vector2(ViewportMaxWidth + 24f, ViewportMaxHeight + 24f);
        viewportGlow.rectTransform.anchoredPosition = new Vector2(0f, ViewportY);

        var viewportFrame = VoiceUiKit.Panel("GameViewportFrame", CardRoot,
            new Color32(4, 7, 11, 255), rounded: true, soft: true);
        viewportFrame.rectTransform.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        viewportFrame.rectTransform.sizeDelta = new Vector2(ViewportMaxWidth + 4f, ViewportMaxHeight + 4f);
        viewportFrame.rectTransform.anchoredPosition = new Vector2(0f, ViewportY);
        viewportFrame.rectTransform.gameObject.AddComponent<RectMask2D>();

        _rawRect = VoiceUiKit.Rect("GameViewport", viewportFrame.rectTransform);
        _rawRect.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        _rawRect.sizeDelta = new Vector2(ViewportMaxWidth, ViewportMaxHeight);
        _rawRect.anchoredPosition = Vector2.zero;
        _rawImage = _rawRect.gameObject.AddComponent<RawImage>();
        _rawImage.color = Color.white;
        _rawImage.raycastTarget = false;

        _statusText = VoiceUiKit.Text("PreviewStatus", CardRoot, "", 18f,
            VoiceUiKit.TextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
        _statusText.rectTransform.Anchor(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f));
        _statusText.rectTransform.sizeDelta = new Vector2(410f, 32f);
        _statusText.rectTransform.anchoredPosition = new Vector2(0f, 70f);

        _detailText = VoiceUiKit.Text("PreviewDetails", CardRoot,
            "10 alive  •  5 ghosts  •  synthetic voice activity", 14f,
            VoiceUiKit.TextMuted, TextAlignmentOptions.Center);
        _detailText.rectTransform.Anchor(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f));
        _detailText.rectTransform.sizeDelta = new Vector2(410f, 28f);
        _detailText.rectTransform.anchoredPosition = new Vector2(0f, 39f);

        var note = VoiceUiKit.Text("PreviewNote", CardRoot,
            "Full virtual game viewport • preview is isolated from the real HUD", 12f,
            VoiceUiKit.TextFaint, TextAlignmentOptions.Center);
        note.rectTransform.Anchor(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f));
        note.rectTransform.sizeDelta = new Vector2(410f, 24f);
        note.rectTransform.anchoredPosition = new Vector2(0f, 15f);

        CardRoot.gameObject.SetActive(false);
    }

    private static float PreviewLocalCenterX
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
        CardRoot.anchoredPosition = new Vector2(
            PreviewLocalCenterX + (1f - reveal) * RevealSlide,
            0f);
        float cardScale = Mathf.Lerp(0.96f, 1f, reveal);
        CardRoot.localScale = new Vector3(cardScale, cardScale, 1f);

        if (_camera != null)
            _camera.enabled = shouldRender &&
                              _renderTexture != null &&
                              _renderTexture.IsCreated() &&
                              _camera.targetTexture == _renderTexture;
        if (_worldRoot != null)
            _worldRoot.SetActive(shouldRender);
    }

    internal void Tick(VoiceChatLocalSettings settings, float unscaledDeltaTime)
    {
        if (_disposed || !CardRoot.gameObject.activeInHierarchy) return;
        if (!EnsureWorld()) return;

        UpdateCameraFrame(out float worldWidth, out float worldHeight);
        bool measurementsChanged = RefreshLabelMeasurements();
        RefreshMissingAvatars();

        var snapshot = SettingsSnapshot.From(settings);
        if (!_hasLastSettings || snapshot != _lastSettings || measurementsChanged ||
            Mathf.Abs(worldWidth - _lastWorldWidth) > 0.0001f ||
            Mathf.Abs(worldHeight - _lastWorldHeight) > 0.0001f)
        {
            Layout(snapshot, worldWidth, worldHeight);
            _lastSettings = snapshot;
            _hasLastSettings = true;
            _lastWorldWidth = worldWidth;
            _lastWorldHeight = worldHeight;
        }

        AnimateRings(Mathf.Max(0f, unscaledDeltaTime));
    }

    internal void Suspend()
    {
        _shouldRender = false;
        if (_camera != null) _camera.enabled = false;
        if (_worldRoot != null) _worldRoot.SetActive(false);
    }

    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Suspend();

        if (_rawImage != null) _rawImage.texture = null;
        if (_camera != null) _camera.targetTexture = null;
        if (_renderTexture != null)
        {
            _renderTexture.Release();
            Object.Destroy(_renderTexture);
            _renderTexture = null;
        }
        if (_cameraObject != null) Object.Destroy(_cameraObject);
        if (_worldRoot != null) Object.Destroy(_worldRoot);
        if (CardRoot != null) Object.Destroy(CardRoot.gameObject);
        _camera = null;
        _cameraObject = null;
        _worldRoot = null;
        _barRoot = null;
        _backdrop = null;
        _slots.Clear();
    }

    private bool EnsureWorld()
    {
        if (_camera != null && _cameraObject != null && _worldRoot != null && _barRoot != null)
            return true;
        if (IsUnavailable || Time.unscaledTime < _nextWorldRetryTime)
            return false;

        DestroyBrokenWorld();
        try
        {
            _worldRoot = new GameObject("VC_SettingsPreviewWorld");
            _worldRoot.layer = PreviewRenderingLayer;
            Object.DontDestroyOnLoad(_worldRoot);
            _worldRoot.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            _worldRoot.transform.position = new Vector3(FarWorldX, FarWorldY, 0f);

            _barRoot = new GameObject("VC_SettingsPreviewBar");
            _barRoot.layer = PreviewRenderingLayer;
            _barRoot.transform.SetParent(_worldRoot.transform, false);

            var backdropObject = new GameObject("VC_SettingsPreviewBackdrop");
            backdropObject.layer = PreviewRenderingLayer;
            backdropObject.transform.SetParent(_barRoot.transform, false);
            _backdrop = backdropObject.AddComponent<SpriteRenderer>();
            _backdrop.sprite = global::VoiceChatPlugin.PingTrackerPatch.GetPreviewBackdropSprite();
            _backdrop.drawMode = SpriteDrawMode.Sliced;
            _backdrop.color = BackdropColor;
            _backdrop.sortingLayerName = global::VoiceChatPlugin.VCSorting.Layer;
            _backdrop.sortingOrder = global::VoiceChatPlugin.VCSorting.Backdrop;
            _backdrop.maskInteraction = SpriteMaskInteraction.None;

            for (int i = 0; i < SpeakingBarPreviewRoster.PlayerCount; i++)
                _slots.Add(CreateSlot(i));

            _cameraObject = new GameObject("VC_SettingsPreviewCamera");
            Object.DontDestroyOnLoad(_cameraObject);
            _cameraObject.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            _cameraObject.transform.position = new Vector3(FarWorldX, FarWorldY, -10f);
            _camera = _cameraObject.AddComponent<Camera>();
            // A target texture is installed atomically by EnsureRenderTexture before this
            // camera can render. It must never fall back to clearing the game display.
            _camera.enabled = false;
            _camera.orthographic = true;
            _camera.orthographicSize = FallbackOrthographicSize;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.025f, 0.04f, 0.065f, 1f);
            _camera.cullingMask = 1 << PreviewRenderingLayer;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 100f;

            _hasLastSettings = false;
            _lastWorldWidth = -1f;
            _lastWorldHeight = -1f;
            _worldFailureCount = 0;
            _nextWorldRetryTime = 0f;
            SetWorldAvailabilityStatus(unavailable: false);
            return true;
        }
        catch (Exception ex)
        {
            _worldFailureCount++;
            bool unavailable = _worldFailureCount >= WorldFailureLimit;
            if (!unavailable)
            {
                float backoff = WorldRetryBaseDelaySeconds *
                    Mathf.Pow(2f, _worldFailureCount - 1);
                _nextWorldRetryTime = Time.unscaledTime +
                    Mathf.Min(backoff, WorldRetryMaximumDelaySeconds);
            }
            else
            {
                _nextWorldRetryTime = float.PositiveInfinity;
            }

            DestroyBrokenWorld();
            SetWorldAvailabilityStatus(unavailable);
            string disposition = unavailable
                ? " Preview disabled after repeated failures."
                : $" Retrying in {Mathf.Max(0f, _nextWorldRetryTime - Time.unscaledTime):0.0}s.";
            VoiceChatPluginMain.Logger.LogWarning(
                $"[PC-UI] Could not create speaking-bar live preview " +
                $"({_worldFailureCount}/{WorldFailureLimit}): {ex.Message}.{disposition}");
            return false;
        }
    }

    private void SetWorldAvailabilityStatus(bool unavailable)
    {
        try
        {
            if (unavailable)
            {
                _badgeText.text = "OFFLINE";
                _badgeText.color = new Color32(255, 145, 145, 255);
                _statusText.text = "PREVIEW UNAVAILABLE";
                _detailText.text = "Preview resources could not be initialized";
                return;
            }

            if (_worldFailureCount > 0)
            {
                _badgeText.text = "RETRYING";
                _badgeText.color = new Color32(255, 205, 112, 255);
                _statusText.text = "PREVIEW RETRYING";
                _detailText.text =
                    $"Initialization attempt {_worldFailureCount + 1} of {WorldFailureLimit}";
                return;
            }

            _badgeText.text = "●  LIVE";
            _badgeText.color = new Color32(118, 239, 165, 255);
        }
        catch
        {
            // Status decoration is best-effort; availability state must still reach the panel.
        }
    }

    private PreviewSlot CreateSlot(int index)
    {
        var slot = new PreviewSlot(index);
        TryCreateAvatar(slot);

        slot.RingObject = new GameObject($"VC_SettingsPreviewRing_{index}");
        slot.RingObject.layer = PreviewRenderingLayer;
        slot.RingObject.transform.SetParent(_barRoot!.transform, false);
        slot.RingObject.transform.localScale = Vector3.one * SpeakingBarVisualMetrics.RingScale;
        slot.RingRenderer = slot.RingObject.AddComponent<SpriteRenderer>();
        slot.RingRenderer.sprite = global::VoiceChatPlugin.PingTrackerPatch.GetPreviewRingSprite();
        slot.RingRenderer.sortingLayerName = global::VoiceChatPlugin.VCSorting.Layer;
        slot.RingRenderer.sortingOrder = global::VoiceChatPlugin.VCSorting.Ring;
        slot.RingRenderer.maskInteraction = SpriteMaskInteraction.None;

        var labelObject = new GameObject($"VC_SettingsPreviewName_{index}");
        labelObject.layer = PreviewRenderingLayer;
        labelObject.transform.SetParent(_barRoot.transform, false);
        slot.Label = labelObject.AddComponent<TextMeshPro>();
        slot.Label.text = SpeakingBarPreviewRoster.Name(index);
        slot.Label.fontSize = SpeakingBarVisualMetrics.LabelSize;
        slot.Label.alignment = TextAlignmentOptions.Center;
        slot.Label.enableWordWrapping = false;
        slot.Label.overflowMode = TextOverflowModes.Ellipsis;
        slot.Label.sortingLayerID = SortingLayer.NameToID(global::VoiceChatPlugin.VCSorting.Layer);
        slot.Label.sortingOrder = global::VoiceChatPlugin.VCSorting.Text;
        slot.Label.color = Color.white;
        slot.Label.alpha = 1f;
        slot.Label.rectTransform.sizeDelta = new Vector2(
            SpeakingBarVisualMetrics.MaximumLabelWidth,
            SpeakingBarVisualMetrics.MaximumLabelHeight);
        return slot;
    }

    private void TryCreateAvatar(PreviewSlot slot)
    {
        if (_barRoot == null) return;
        if (slot.Icon != null) Object.Destroy(slot.Icon);
        slot.Icon = null;
        if (global::VoiceChatPlugin.CrewmateAvatarRenderer.TryCreatePreview(
                slot.Index,
                _barRoot.transform,
                PreviewRenderingLayer,
                attachToOverlayCamera: false,
                out var icon) && icon != null)
        {
            slot.Icon = icon;
            SetAvatarAlpha(icon, SpeakingBarPreviewRoster.IsGhost(slot.Index) ? 0.45f : 1f);
        }
    }

    private void RefreshMissingAvatars()
    {
        if (Time.frameCount % 30 != 0) return;
        bool recreated = false;
        foreach (var slot in _slots)
        {
            var body = slot.Icon != null ? slot.Icon.GetComponentInChildren<SpriteRenderer>(true) : null;
            if (body != null && body.sprite != null) continue;
            TryCreateAvatar(slot);
            recreated = true;
        }
        if (recreated) _hasLastSettings = false;
    }

    private static void SetAvatarAlpha(GameObject icon, float alpha)
    {
        foreach (var renderer in icon.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var color = renderer.color;
            color.a = alpha;
            renderer.color = color;
        }
    }

    private void UpdateCameraFrame(out float worldWidth, out float worldHeight)
    {
        float aspect = 16f / 9f;
        float orthographicSize = FallbackOrthographicSize;
        var main = Camera.main;
        if (main != null)
        {
            if (main.aspect > 0.1f && !float.IsNaN(main.aspect) && !float.IsInfinity(main.aspect))
                aspect = main.aspect;
            if (main.orthographic && main.orthographicSize > 0.1f)
                orthographicSize = main.orthographicSize;
        }
        else if (Screen.height > 0)
        {
            aspect = Mathf.Max(0.75f, (float)Screen.width / Screen.height);
        }
        aspect = Mathf.Clamp(aspect, 0.75f, 3.6f);

        if (_camera != null)
        {
            _camera.orthographicSize = orthographicSize;
            _camera.aspect = aspect;
        }
        EnsureRenderTexture(aspect);

        worldHeight = orthographicSize * 2f;
        worldWidth = worldHeight * aspect;
    }

    private void EnsureRenderTexture(float aspect)
    {
        if (_camera == null) return;
        bool currentIsUsable = _renderTexture != null && _renderTexture.IsCreated();
        if (currentIsUsable && Mathf.Abs(aspect - _lastAspect) <= 0.01f)
        {
            // Recover from a camera target or RawImage reference being cleared independently.
            if (_camera.targetTexture != _renderTexture)
                _camera.targetTexture = _renderTexture;
            if (_rawImage.texture != _renderTexture)
                _rawImage.texture = _renderTexture;
            _camera.enabled = _shouldRender;
            return;
        }

        if (Time.unscaledTime < _nextTextureRetryTime)
        {
            // Keep a still-valid previous target alive while a replacement is rate-limited.
            if (currentIsUsable)
            {
                _camera.targetTexture = _renderTexture;
                _rawImage.texture = _renderTexture;
                _camera.enabled = _shouldRender;
            }
            else
            {
                _camera.enabled = false;
                _camera.targetTexture = null;
                _rawImage.texture = null;
            }
            return;
        }

        int textureHeight = TextureBaseHeight;
        int textureWidth = Mathf.RoundToInt(textureHeight * aspect);
        if (textureWidth > TextureMaxWidth)
        {
            textureWidth = TextureMaxWidth;
            textureHeight = Mathf.Max(256, Mathf.RoundToInt(textureWidth / aspect));
        }

        RenderTexture? replacement = null;
        try
        {
            replacement = new RenderTexture(textureWidth, textureHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "VC_SettingsSpeakingBarPreviewRT",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            if (!replacement.Create() || !replacement.IsCreated())
                throw new InvalidOperationException("Unity could not allocate the preview render texture.");

            float width = ViewportMaxWidth;
            float height = width / aspect;
            if (height > ViewportMaxHeight)
            {
                height = ViewportMaxHeight;
                width = height * aspect;
            }

            // Swap only after the replacement is fully created. Keeping the camera disabled
            // during the handoff guarantees it can never render its clear color to the game.
            var previous = _renderTexture;
            _camera.enabled = false;
            _camera.targetTexture = replacement;
            _rawImage.texture = replacement;
            _renderTexture = replacement;
            replacement = null;

            _rawRect.sizeDelta = new Vector2(width, height);
            _lastAspect = aspect;
            _nextTextureRetryTime = 0f;
            _hasLastSettings = false;
            _camera.enabled = _shouldRender;

            if (previous != null)
            {
                previous.Release();
                Object.Destroy(previous);
            }
        }
        catch (Exception ex)
        {
            if (replacement != null)
            {
                replacement.Release();
                Object.Destroy(replacement);
            }

            currentIsUsable = _renderTexture != null && _renderTexture.IsCreated();
            if (currentIsUsable)
            {
                _camera.targetTexture = _renderTexture;
                _rawImage.texture = _renderTexture;
                _camera.enabled = _shouldRender;
            }
            else
            {
                _camera.enabled = false;
                _camera.targetTexture = null;
                _rawImage.texture = null;
            }

            _nextTextureRetryTime = Time.unscaledTime + TextureRetryDelaySeconds;
            VoiceChatPluginMain.Logger.LogWarning(
                "[PC-UI] Could not allocate speaking-bar preview texture: " + ex.Message);
        }
    }

    private bool RefreshLabelMeasurements()
    {
        bool changed = false;
        foreach (var slot in _slots)
        {
            if (!slot.StyleApplied || slot.Label.font == null)
                slot.StyleApplied = global::VoiceChatPlugin.PingTrackerPatch.ApplyPreviewNameStyle(slot.Label);

            float width = slot.Label.preferredWidth;
            float height = slot.Label.preferredHeight;
            if (float.IsNaN(width) || float.IsInfinity(width) || width < 0f ||
                float.IsNaN(height) || float.IsInfinity(height) || height < 0f)
                continue;

            width = Mathf.Min(width, SpeakingBarVisualMetrics.MaximumLabelWidth);
            height = Mathf.Min(height, SpeakingBarVisualMetrics.MaximumLabelHeight);
            if (Mathf.Abs(width - slot.LabelWidth) <= 0.01f &&
                Mathf.Abs(height - slot.LabelHeight) <= 0.01f)
                continue;
            slot.LabelWidth = width;
            slot.LabelHeight = height;
            changed = true;
        }
        return changed;
    }

    private void Layout(SettingsSnapshot settings, float availableWidth, float availableHeight)
    {
        if (_barRoot == null || _backdrop == null || _slots.Count == 0) return;

        bool vertical = settings.ManualLayout
            ? settings.ManualOrientation == VoiceControlsLayout.Vertical
            : SpeakingBarLayoutPolicy.OrientationFor(settings.Position) == VoiceControlsLayout.Vertical;
        var namePosition = SpeakingBarLayoutPolicy.ResolveNamePosition(
            settings.NamePosition,
            settings.ManualLayout,
            settings.ManualOrientation,
            settings.ManualX,
            settings.ManualY,
            settings.Position);

        float crossPitch = SpeakingBarVisualMetrics.SlotWidth;
        float itemMinX = float.MaxValue, itemMaxX = float.MinValue;
        float itemMinY = float.MaxValue, itemMaxY = float.MinValue;
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
            ? SpeakingBarVisualMetrics.SlotHeight + SpeakingBarVisualMetrics.TopNameExtraPitch
            : SpeakingBarVisualMetrics.SlotHeight;
        float outerPad = SpeakingBarVisualMetrics.BackdropPad * 2f;
        float itemWidth = itemMaxX - itemMinX + outerPad;
        float itemHeight = itemMaxY - itemMinY + outerPad;
        float requestedScale = SpeakingBarScalePolicy.ToRenderedScale(settings.Scale);
        bool singleLane = !settings.ManualLayout &&
            SpeakingBarLayoutPolicy.UsesSingleVerticalLaneFor(settings.Position, settings.SideLayout);
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

        float contentMinX = 0f, contentMaxX = 0f, contentMinY = 0f, contentMaxY = 0f;
        bool any = false;
        bool avatarFacesLeft = SpeakingBarLayoutPolicy.ResolveAvatarFacesLeft(
            settings.ManualLayout,
            settings.Position,
            settings.ManualAvatarFacing);

        for (int i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var placement = SpeakingBarLayoutPolicy.GetPlacement(i, _slots.Count, solution.LineCount);
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
            GetSlotXExtents(slot, namePosition, coordinates.X, out float minX, out float maxX);
            GetSlotYExtents(slot, namePosition, coordinates.Y, out float minY, out float maxY);
            if (!any)
            {
                contentMinX = minX; contentMaxX = maxX;
                contentMinY = minY; contentMaxY = maxY;
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

        _barRoot.transform.localScale = Vector3.one * solution.EffectiveScale;
        _backdrop.enabled = settings.Backdrop;
        _backdrop.color = BackdropColor;
        _backdrop.size = new Vector2(
            contentMaxX - contentMinX + SpeakingBarVisualMetrics.BackdropPad * 2f,
            contentMaxY - contentMinY + SpeakingBarVisualMetrics.BackdropPad * 2f);
        _backdrop.transform.localPosition = new Vector3(
            (contentMinX + contentMaxX) * 0.5f,
            (contentMinY + contentMaxY) * 0.5f,
            0f);

        Vector2 anchor = settings.ManualLayout
            ? new Vector2(settings.ManualX, settings.ManualY)
            : PresetViewportAnchor(settings.Position);
        float rootX = (anchor.x - 0.5f) * availableWidth;
        float rootY = (anchor.y - 0.5f) * availableHeight;
        float clampMinX = contentMinX - (settings.Backdrop ? SpeakingBarVisualMetrics.BackdropPad : 0f);
        float clampMaxX = contentMaxX + (settings.Backdrop ? SpeakingBarVisualMetrics.BackdropPad : 0f);
        float clampMinY = contentMinY - (settings.Backdrop ? SpeakingBarVisualMetrics.BackdropPad : 0f);
        float clampMaxY = contentMaxY + (settings.Backdrop ? SpeakingBarVisualMetrics.BackdropPad : 0f);
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
        _barRoot.transform.localPosition = new Vector3(rootX, rootY, 0f);

        _lastLineCount = solution.LineCount;
        UpdateStatus(settings, solution.LineCount);
    }

    private static float RequiredSlotPitch(PreviewSlot slot, SpeakingBarNamePosition namePosition)
    {
        const float ringHalf = SpeakingBarVisualMetrics.RingScale * 0.5f;
        return namePosition switch
        {
            SpeakingBarNamePosition.Left or SpeakingBarNamePosition.Right
                => SpeakingBarVisualMetrics.LabelSideOffset + slot.LabelWidth + ringHalf + SpeakingBarVisualMetrics.NameGap,
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
        if (slot.Icon != null)
        {
            var scale = slot.Icon.transform.localScale;
            float width = Mathf.Abs(scale.x);
            scale.x = avatarFacesLeft ? -width : width;
            slot.Icon.transform.localScale = scale;
            slot.Icon.transform.localPosition = new Vector3(x, y, 0f);
        }
        slot.RingObject.transform.localPosition = new Vector3(x, y, 0f);

        Vector3 labelPosition;
        TextAlignmentOptions alignment;
        Vector2 pivot;
        switch (namePosition)
        {
            case SpeakingBarNamePosition.Top:
                labelPosition = new Vector3(x, y + SpeakingBarVisualMetrics.LabelOffset, 0f);
                alignment = TextAlignmentOptions.Center;
                pivot = new Vector2(0.5f, 0.5f);
                break;
            case SpeakingBarNamePosition.Left:
                labelPosition = new Vector3(x - SpeakingBarVisualMetrics.LabelSideOffset, y, 0f);
                alignment = TextAlignmentOptions.Right;
                pivot = new Vector2(1f, 0.5f);
                break;
            case SpeakingBarNamePosition.Right:
                labelPosition = new Vector3(x + SpeakingBarVisualMetrics.LabelSideOffset, y, 0f);
                alignment = TextAlignmentOptions.Left;
                pivot = new Vector2(0f, 0.5f);
                break;
            default:
                labelPosition = new Vector3(x, y - SpeakingBarVisualMetrics.LabelOffset, 0f);
                alignment = TextAlignmentOptions.Center;
                pivot = new Vector2(0.5f, 0.5f);
                break;
        }
        slot.Label.transform.localPosition = labelPosition;
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

    private static float CalculateAxisShift(float min, float max, float allowedMin, float allowedMax)
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
            if (!speaking)
            {
                slot.RingRenderer.enabled = false;
                continue;
            }

            float wave = 0.82f + 0.18f * Mathf.Sin(now * 3.1f + slot.Index * 0.73f);
            float target = baseLevel * wave;
            slot.SmoothedLevel = global::VoiceChatPlugin.VoiceLevelVisual.SmoothLevel(
                slot.SmoothedLevel,
                target,
                deltaTime);
            float brightness = Mathf.SmoothStep(0f, 1f, slot.SmoothedLevel);
            Color color = SpeakingGreen;
            color.a = Mathf.Lerp(0.22f, 0.92f, brightness);
            slot.RingRenderer.color = color;
            slot.RingRenderer.enabled = true;
            slot.RingObject.transform.localScale = Vector3.one * SpeakingBarVisualMetrics.RingScale;
        }
    }

    private void UpdateStatus(SettingsSnapshot settings, int lineCount)
    {
        string placement = settings.ManualLayout
            ? $"MANUAL {settings.ManualOrientation.ToString().ToUpperInvariant()}"
            : PositionLabel(settings.Position);
        string layout = lineCount == 1 ? "1 LANE" : $"{lineCount} LINES";
        int percent = Mathf.RoundToInt(settings.Scale * 100f);
        _statusText.text = $"{placement}  •  {layout}  •  {percent}%";
        _detailText.text = settings.Backdrop
            ? "10 alive  •  5 ghosts  •  backdrop on"
            : "10 alive  •  5 ghosts  •  backdrop off";
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

    private void DestroyBrokenWorld()
    {
        // Unity destroys objects at the end of the frame. Disable before detaching the
        // target so this camera cannot briefly become a screen-rendering camera.
        if (_camera != null)
        {
            _camera.enabled = false;
            _camera.targetTexture = null;
        }
        if (_rawImage != null) _rawImage.texture = null;
        if (_renderTexture != null)
        {
            _renderTexture.Release();
            Object.Destroy(_renderTexture);
        }
        if (_cameraObject != null) Object.Destroy(_cameraObject);
        if (_worldRoot != null) Object.Destroy(_worldRoot);
        _camera = null;
        _cameraObject = null;
        _renderTexture = null;
        _worldRoot = null;
        _barRoot = null;
        _backdrop = null;
        _slots.Clear();
        _lastAspect = -1f;
        _nextTextureRetryTime = 0f;
        _hasLastSettings = false;
    }
}
