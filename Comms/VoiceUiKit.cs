using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class VoiceUiDriver : MonoBehaviour
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        ClassInjector.RegisterTypeInIl2Cpp<VoiceUiDriver>();
    }

    public VoiceUiDriver(IntPtr ptr) : base(ptr) { }

    void Update()
    {
        VoiceUiKit.Tick();
    }
}

internal static class VoiceUiKit
{
    public static readonly Color32 Accent       = new(34, 211, 238, 255);
    public static readonly Color32 AccentSoft    = new(34, 211, 238, 90);
    public static readonly Color32 AccentFaint   = new(34, 211, 238, 28);
    public static readonly Color32 AccentGlow     = new(34, 211, 238, 64);
    public static readonly Color32 PanelOuter     = new(12, 15, 20, 235);
    public static readonly Color32 PanelInner     = new(20, 25, 33, 235);
    public static readonly Color32 PanelTop        = new(31, 39, 51, 240);
    public static readonly Color32 PanelBottom     = new(15, 19, 26, 240);
    public static readonly Color32 PanelShadow     = new(0, 0, 0, 170);
    public static readonly Color32 RailSurface    = new(9, 11, 15, 235);
    public static readonly Color32 HeaderSurface  = new(16, 20, 27, 245);
    public static readonly Color32 HeaderTop       = new(26, 33, 44, 250);
    public static readonly Color32 HeaderBottom    = new(14, 18, 25, 250);
    public static readonly Color32 TopHighlight    = new(150, 170, 195, 32);
    public static readonly Color32 Divider         = new(120, 138, 160, 26);
    public static readonly Color32 TextPrimary     = new(228, 235, 245, 255);
    public static readonly Color32 TextBright      = new(244, 249, 255, 255);
    public static readonly Color32 TextMuted       = new(140, 156, 178, 255);
    public static readonly Color32 TextFaint       = new(96, 110, 130, 255);
    public static readonly Color32 TrackBg         = new(38, 46, 60, 255);
    public static readonly Color32 ControlBg       = new(28, 34, 44, 255);
    public static readonly Color32 ControlHover    = new(44, 53, 67, 255);
    public static readonly Color32 RowHover        = new(255, 255, 255, 12);
    public static readonly Color32 KnobOff         = new(150, 162, 180, 255);
    public static readonly Color32 ToggleOffTrack  = new(46, 54, 68, 255);
    public static readonly Color32 CloseHover      = new(230, 88, 96, 255);
    public static readonly Color32 Danger          = new(230, 88, 96, 255);
    public static readonly Color32 DangerDim       = new(70, 34, 38, 255);
    public static readonly Color32 Clear           = new(0, 0, 0, 0);

    private static readonly Dictionary<uint, Sprite> _solid = new();
    private static Sprite? _rounded;
    private static Sprite? _roundedSoft;
    private static Sprite? _glow;
    private static Sprite? _gradPanel;
    private static Sprite? _gradHeader;
    private static TMP_FontAsset? _font;
    private static GameObject? _canvasRoot;
    private static Canvas? _canvas;
    private static RectTransform? _tooltipRoot;
    private static CanvasGroup? _tooltipGroup;
    private static TextMeshProUGUI? _tooltipText;
    private static bool _tooltipRequested;

    public static readonly Color32 Backdrop = new(0, 0, 0, 150);

    public static bool AnyPanelOpen =>
        VoiceSettingsPanel.IsOpen || HostSettingsPanel.IsOpen || VoiceVolumeMenu.IsOpen;

    private static bool _swallowActive;
    private static bool _swallowSawRelease;
    private static int _swallowFrame = -1000;
    private const int SwallowMaxFrames = 20;
    public static void SwallowClick() { _swallowActive = true; _swallowSawRelease = false; _swallowFrame = Time.frameCount; }
    private static bool SwallowBlocking => _swallowActive && Time.frameCount - _swallowFrame < SwallowMaxFrames;
    public static bool BlockGameInput => AnyPanelOpen || SwallowBlocking;
    private static void UpdateSwallow()
    {
        if (!_swallowActive) return;
        if (Time.frameCount - _swallowFrame >= SwallowMaxFrames) { _swallowActive = false; return; }
        if (Input.GetMouseButton(0)) return;
        if (_swallowSawRelease) _swallowActive = false;
        else _swallowSawRelease = true;
    }

    private static int _tickFrame = -1;

    public static void Tick()
    {
        int frame = Time.frameCount;
        if (frame == _tickFrame) return;
        _tickFrame = frame;
        UpdateSwallow();
        BeginTooltipFrame();

        try { VoiceOptionsMenuEntry.TickButton(); VoiceHostMenuEntry.TickHostButton(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] TickButton threw: " + e.Message); }
        try { VoiceSettingsPanel.Tick(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] VoiceSettingsPanel.Tick threw: " + e.Message); }
        try { HostSettingsPanel.Tick(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] HostSettingsPanel.Tick threw: " + e.Message); }
        try { VoiceVolumeMenu.Tick(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] VoiceVolumeMenu.Tick threw: " + e.Message); }
        EndTooltipFrame();
    }

    private static void BeginTooltipFrame() => _tooltipRequested = false;

    private static void EndTooltipFrame()
    {
        if (_tooltipRoot == null || _tooltipGroup == null) return;
        if (!_tooltipRequested)
        {
            _tooltipGroup.alpha = 0f;
            _tooltipRoot.gameObject.SetActive(false);
            return;
        }

        _tooltipRoot.SetAsLastSibling();
    }

    internal static void RequestTooltip(
        string title,
        string description,
        bool plainTextDescription = false)
    {
        if (string.IsNullOrWhiteSpace(description)) return;
        EnsureTooltip();
        if (_tooltipRoot == null || _tooltipGroup == null || _tooltipText == null) return;

        _tooltipRequested = true;
        _tooltipRoot.gameObject.SetActive(true);
        _tooltipGroup.alpha = 1f;
        string body = plainTextDescription
            ? $"<noparse>{description}</noparse>"
            : description;
        _tooltipText.text = string.IsNullOrWhiteSpace(title)
            ? body
            : $"<b>{title}</b>\n<size=86%><color=#B5C2D4>{body}</color></size>";

        _tooltipRoot.sizeDelta = new Vector2(420f, 80f);
        _tooltipText.ForceMeshUpdate();
        float height = Mathf.Clamp(_tooltipText.preferredHeight + 28f, 62f, 260f);
        _tooltipRoot.sizeDelta = new Vector2(420f, height);

        if (!LocalPoint(CanvasRect, out var mouse)) return;
        var canvasRect = CanvasRect.rect;
        float halfW = _tooltipRoot.sizeDelta.x * 0.5f;
        float halfH = _tooltipRoot.sizeDelta.y * 0.5f;
        float x = mouse.x + 22f + halfW;
        if (x + halfW > canvasRect.xMax - 12f)
            x = mouse.x - 22f - halfW;
        float y = mouse.y - 18f - halfH;
        x = Mathf.Clamp(x, canvasRect.xMin + halfW + 12f, canvasRect.xMax - halfW - 12f);
        y = Mathf.Clamp(y, canvasRect.yMin + halfH + 12f, canvasRect.yMax - halfH - 12f);
        _tooltipRoot.anchoredPosition = new Vector2(x, y);
    }

    private static void EnsureTooltip()
    {
        EnsureCanvas();
        if (_tooltipRoot != null && _tooltipGroup != null && _tooltipText != null) return;

        _tooltipRoot = Rect("PerfectComms_Tooltip", Canvas.transform);
        _tooltipRoot.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        _tooltipRoot.sizeDelta = new Vector2(420f, 80f);

        var shadow = GlowImage("TooltipShadow", _tooltipRoot, new Color32(0, 0, 0, 165));
        shadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        shadow.rectTransform.offsetMin = new Vector2(-18f, -22f);
        shadow.rectTransform.offsetMax = new Vector2(18f, 14f);

        var backgroundRt = Rect("TooltipBackground", _tooltipRoot);
        backgroundRt.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        backgroundRt.offsetMin = Vector2.zero;
        backgroundRt.offsetMax = Vector2.zero;
        var background = backgroundRt.gameObject.AddComponent<Image>();
        background.sprite = Rounded();
        background.type = Image.Type.Sliced;
        background.color = new Color32(18, 23, 31, 252);
        background.raycastTarget = false;

        var accent = Panel("TooltipAccent", _tooltipRoot, Accent, true);
        accent.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
        accent.rectTransform.sizeDelta = new Vector2(3f, -20f);
        accent.rectTransform.anchoredPosition = new Vector2(7f, 0f);

        _tooltipText = Text("TooltipText", _tooltipRoot, "", 18f,
            TextPrimary, TextAlignmentOptions.TopLeft);
        _tooltipText.enableWordWrapping = true;
        _tooltipText.overflowMode = TextOverflowModes.Overflow;
        _tooltipText.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _tooltipText.rectTransform.offsetMin = new Vector2(20f, 14f);
        _tooltipText.rectTransform.offsetMax = new Vector2(-16f, -14f);

        _tooltipGroup = _tooltipRoot.gameObject.AddComponent<CanvasGroup>();
        _tooltipGroup.interactable = false;
        _tooltipGroup.blocksRaycasts = false;
        _tooltipGroup.alpha = 0f;
        _tooltipRoot.gameObject.SetActive(false);
    }

    public static void RaiseAbove(Transform panel, Transform? extra = null)
    {
        panel.SetAsLastSibling();
        if (extra != null) extra.SetAsLastSibling();
    }

    public static Canvas Canvas
    {
        get { EnsureCanvas(); return _canvas!; }
    }

    public static RectTransform CanvasRect
    {
        get { EnsureCanvas(); return _canvas!.GetComponent<RectTransform>(); }
    }

    public static void EnsureCanvas()
    {
        if (_canvasRoot != null && _canvas != null)
        {
            EnsureDriver();
            return;
        }
        _canvas = null;
        _canvasRoot = null;

        _canvasRoot = new GameObject("PerfectComms_UICanvas");
        Object.DontDestroyOnLoad(_canvasRoot);
        _canvasRoot.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        _canvasRoot.layer = LayerMask.NameToLayer("UI");

        _canvas = _canvasRoot.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1000;

        var scaler = _canvasRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        _canvasRoot.AddComponent<GraphicRaycaster>();
        EnsureDriver();
    }

    public static void EnsureDriver()
    {
        if (_canvasRoot == null) return;
        if (!_canvasRoot.activeSelf) _canvasRoot.SetActive(true);
        VoiceUiDriver.Register();
        var driver = _canvasRoot.GetComponent<VoiceUiDriver>();
        if (driver == null)
            driver = _canvasRoot.AddComponent<VoiceUiDriver>();
        if (!driver.enabled) driver.enabled = true;
    }

    public static Sprite Solid(Color32 c)
    {
        uint key = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
        if (_solid.TryGetValue(key, out var cached) && cached != null) return cached;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixel(0, 0, c);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        _solid[key] = sprite;
        return sprite;
    }

    public static Sprite Rounded(bool soft = false)
    {
        if (soft && _roundedSoft != null) return _roundedSoft;
        if (!soft && _rounded != null) return _rounded;

        const int s = 64;
        int rad = soft ? 32 : 16;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float a = 1f;
            int cx = x < rad ? rad : (x > s - 1 - rad ? s - 1 - rad : x);
            int cy = y < rad ? rad : (y > s - 1 - rad ? s - 1 - rad : y);
            if (cx != x || cy != y)
            {
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                a = Mathf.Clamp01(rad - d + 0.5f);
            }
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), s, 0,
            SpriteMeshType.FullRect, new Vector4(rad, rad, rad, rad));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        if (soft) _roundedSoft = sprite; else _rounded = sprite;
        return sprite;
    }

    public static Sprite Glow()
    {
        if (_glow != null) return _glow;
        const int s = 64;
        float half = (s - 1) * 0.5f;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < s; y++)
        for (int x = 0; x < s; x++)
        {
            float dx = Mathf.Abs(x - half) / half;
            float dy = Mathf.Abs(y - half) / half;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(1f - d);
            a = a * a;
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), s, 0,
            SpriteMeshType.FullRect, new Vector4(24, 24, 24, 24));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        _glow = sprite;
        return sprite;
    }

    private static Sprite Gradient(Color32 top, Color32 bottom, ref Sprite? cache)
    {
        if (cache != null) return cache;
        const int s = 64;
        int rad = 18;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < s; y++)
        {
            float t = y / (float)(s - 1);
            Color col = Color.Lerp(bottom, top, t);
            for (int x = 0; x < s; x++)
            {
                float a = 1f;
                int cx = x < rad ? rad : (x > s - 1 - rad ? s - 1 - rad : x);
                int cy = y < rad ? rad : (y > s - 1 - rad ? s - 1 - rad : y);
                if (cx != x || cy != y)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    a = Mathf.Clamp01(rad - d + 0.5f);
                }
                var c = col; c.a *= a;
                tex.SetPixel(x, y, c);
            }
        }
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), s, 0,
            SpriteMeshType.FullRect, new Vector4(rad, rad, rad, rad));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        cache = sprite;
        return sprite;
    }

    public static Sprite PanelGradient() => Gradient(PanelTop, PanelBottom, ref _gradPanel);
    public static Sprite HeaderGradient() => Gradient(HeaderTop, HeaderBottom, ref _gradHeader);

    public static Image GlowImage(string name, Transform parent, Color32 color)
    {
        var rt = Rect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Glow();
        img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static TMP_FontAsset? GameFont()
    {
        if (_font != null) return _font;
        if (HudManager.InstanceExists)
        {
            foreach (var t in HudManager.Instance.GetComponentsInChildren<TextMeshPro>(true))
                if (t != null && t.font != null) { _font = t.font; break; }
        }
        if (_font == null)
        {
            foreach (var t in Object.FindObjectsOfType<TextMeshPro>())
                if (t != null && t.font != null) { _font = t.font; break; }
        }
        return _font;
    }

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name);
        go.layer = LayerMask.NameToLayer("UI");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    public static RectTransform Anchor(this RectTransform rt, Vector2 min, Vector2 max, Vector2 pivot)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.pivot = pivot;
        return rt;
    }

    public static Image Panel(string name, Transform parent, Color32 color, bool rounded = true, bool soft = false)
    {
        var rt = Rect(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = rounded ? Rounded(soft) : Solid(Color.white);
        if (rounded) img.type = Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static TextMeshProUGUI Text(string name, Transform parent, string content, float size,
        Color32 color, TextAlignmentOptions align, FontStyles style = FontStyles.Normal)
    {
        var rt = Rect(name, parent);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = GameFont();
        if (font != null) tmp.font = font;
        tmp.text = content;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.alignment = align;
        tmp.fontStyle = style;
        tmp.richText = true;
        tmp.enableWordWrapping = false;
        tmp.raycastTarget = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        return tmp;
    }

    public static RectTransform SectionHeader(string name, RectTransform pane, string content,
        float paneW, float y, float height)
    {
        var rt = Rect("Section_" + name, pane);
        rt.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        rt.sizeDelta = new Vector2(0f, height);
        rt.anchoredPosition = new Vector2(0f, y);

        var label = Text("SectionLabel", rt, content, 15f, TextMuted, TextAlignmentOptions.Left, FontStyles.Bold);
        label.characterSpacing = 4f;
        label.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0.5f));
        label.rectTransform.offsetMin = new Vector2(Row.EdgePad, 0f);
        label.rectTransform.offsetMax = new Vector2(-Row.EdgePad, -2f);
        return rt;
    }

    public static bool Contains(RectTransform rt)
    {
        if (rt == null) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, null);
    }

    public static bool LocalPoint(RectTransform rt, out Vector2 local)
    {
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(
            rt, Input.mousePosition, null, out local);
    }

    public static float AppearScale(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float eased = 1f + c3 * (t - 1f) * (t - 1f) * (t - 1f) + c1 * (t - 1f) * (t - 1f);
        return Mathf.LerpUnclamped(0.6f, 1f, eased);
    }

    public static Color32 Lerp(Color32 a, Color32 b, float t)
    {
        t = Mathf.Clamp01(t);
        return new Color32(
            (byte)Mathf.Lerp(a.r, b.r, t),
            (byte)Mathf.Lerp(a.g, b.g, t),
            (byte)Mathf.Lerp(a.b, b.b, t),
            (byte)Mathf.Lerp(a.a, b.a, t));
    }

    public sealed class PanelShell
    {
        public readonly GameObject Root;
        public readonly RectTransform RootRect;
        public readonly CanvasGroup Group;
        public readonly RectTransform HeaderRect;
        public readonly RectTransform RailRoot;
        public readonly RectTransform PaneRoot;
        public readonly RectTransform PaneClip;
        public readonly float Width;
        public readonly float Height;
        public readonly float RailWidth;
        public readonly float PaneWidth;
        public readonly float PaneHeight;

        private readonly Image _closeImg;
        private readonly RectTransform _closeRt;
        private readonly Image _closeBar1;
        private readonly Image _closeBar2;
        private float _closeScale = 1f;
        private readonly Action _onClose;
        private bool _dragging;
        private Vector2 _dragOffset;
        private Vector2 _userPosition;
        private Vector2 _layoutOffset;

        public PanelShell(string objName, string title, float w, float h, Action onClose, bool rail = true, bool backdrop = true)
        {
            Width = w; Height = h;
            _onClose = onClose;

            Root = Rect(objName, Canvas.transform).gameObject;
            RootRect = Root.GetComponent<RectTransform>();
            RootRect.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            RootRect.sizeDelta = new Vector2(w, h);
            _userPosition = Vector2.zero;
            _layoutOffset = Vector2.zero;
            ApplyRootPosition();

            Group = Root.AddComponent<CanvasGroup>();

            if (backdrop)
            {
                var backdropRt = Rect("Backdrop", RootRect);
                backdropRt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                backdropRt.sizeDelta = new Vector2(8000f, 8000f);
                backdropRt.anchoredPosition = Vector2.zero;
                backdropRt.SetAsFirstSibling();
                var backdropImg = backdropRt.gameObject.AddComponent<Image>();
                backdropImg.sprite = Solid(Color.white);
                backdropImg.color = Backdrop;
                backdropImg.raycastTarget = true;
            }

            var shadow = GlowImage("DropShadow", RootRect, PanelShadow);
            shadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            shadow.rectTransform.offsetMin = new Vector2(-46f, -54f);
            shadow.rectTransform.offsetMax = new Vector2(46f, 38f);

            var rimGlow = GlowImage("RimGlow", RootRect, AccentGlow);
            rimGlow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            rimGlow.rectTransform.offsetMin = new Vector2(-22f, -22f);
            rimGlow.rectTransform.offsetMax = new Vector2(22f, 22f);

            var surface = Rect("Surface", RootRect);
            surface.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            surface.offsetMin = Vector2.zero;
            surface.offsetMax = Vector2.zero;
            var surfaceImg = surface.gameObject.AddComponent<Image>();
            surfaceImg.sprite = PanelGradient();
            surfaceImg.type = Image.Type.Sliced;
            surfaceImg.color = Color.white;
            surfaceImg.raycastTarget = false;

            const float headerH = 76f;
            HeaderRect = Rect("Header", RootRect);
            HeaderRect.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            HeaderRect.sizeDelta = new Vector2(0f, headerH);
            HeaderRect.anchoredPosition = Vector2.zero;
            var headerBg = HeaderRect.gameObject.AddComponent<Image>();
            headerBg.sprite = HeaderGradient();
            headerBg.type = Image.Type.Sliced;
            headerBg.color = Color.white;
            headerBg.raycastTarget = false;

            var headerDivider = Panel("HeaderDivider", HeaderRect, Divider, false);
            headerDivider.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f));
            headerDivider.rectTransform.sizeDelta = new Vector2(0f, 1.5f);
            headerDivider.rectTransform.anchoredPosition = new Vector2(0f, 0f);

            var titleTmp = Text("Title", HeaderRect, title, 32f, TextBright, TextAlignmentOptions.Left, FontStyles.Bold);
            titleTmp.characterSpacing = 6f;
            titleTmp.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0.5f));
            titleTmp.rectTransform.sizeDelta = new Vector2(-200f, headerH);
            titleTmp.rectTransform.anchoredPosition = new Vector2(38f, 0f);

            _closeRt = Rect("Close", HeaderRect);
            _closeRt.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _closeRt.sizeDelta = new Vector2(46f, 46f);
            _closeRt.anchoredPosition = new Vector2(-24f, 0f);
            _closeImg = _closeRt.gameObject.AddComponent<Image>();
            _closeImg.sprite = Rounded(true);
            _closeImg.type = Image.Type.Sliced;
            _closeImg.color = ControlBg;
            _closeImg.raycastTarget = false;
            _closeBar1 = CloseBar(_closeRt, 45f);
            _closeBar2 = CloseBar(_closeRt, -45f);

            const float pad = 20f;
            const float clipPad = 8f;
            RailWidth = rail ? Mathf.Round(w * 0.25f) : 0f;

            RailRoot = Rect("Rail", RootRect);
            RailRoot.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
            RailRoot.offsetMin = new Vector2(0f, 0f);
            RailRoot.offsetMax = new Vector2(RailWidth, -headerH);
            if (rail)
            {
                var railBg = RailRoot.gameObject.AddComponent<Image>();
                railBg.sprite = Rounded(true);
                railBg.type = Image.Type.Sliced;
                railBg.color = RailSurface;
                railBg.raycastTarget = false;

                var railDiv = Rect("RailDivider", RootRect);
                railDiv.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
                railDiv.offsetMin = new Vector2(RailWidth, 0f);
                railDiv.offsetMax = new Vector2(RailWidth + 1.5f, -headerH);
                var railDivImg = railDiv.gameObject.AddComponent<Image>();
                railDivImg.sprite = Solid(Color.white);
                railDivImg.color = Divider;
                railDivImg.raycastTarget = false;
            }

            PaneWidth = w - RailWidth - pad * 2f;
            PaneHeight = h - headerH - pad * 2f;

            var inner = Panel("InnerPane", RootRect, PanelInner, true);
            inner.color = new Color32(0, 0, 0, 70);
            inner.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            inner.rectTransform.offsetMin = new Vector2(RailWidth + pad, pad);
            inner.rectTransform.offsetMax = new Vector2(-pad, -headerH - pad);

            PaneClip = Rect("PaneClip", RootRect);
            PaneClip.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            PaneClip.offsetMin = new Vector2(RailWidth + pad + clipPad, pad + clipPad);
            PaneClip.offsetMax = new Vector2(-pad - clipPad, -headerH - pad - clipPad);
            PaneClip.gameObject.AddComponent<RectMask2D>();

            PaneRoot = Rect("PaneContent", PaneClip);
            PaneRoot.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            PaneRoot.offsetMin = new Vector2(0f, 0f);
            PaneRoot.offsetMax = new Vector2(0f, 0f);
            PaneRoot.sizeDelta = new Vector2(0f, 0f);
            PaneRoot.anchoredPosition = Vector2.zero;
        }

        private static Image CloseBar(RectTransform parent, float angle)
        {
            var rt = Rect("CloseBar", parent);
            rt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            rt.sizeDelta = new Vector2(22f, 3.4f);
            rt.anchoredPosition = Vector2.zero;
            rt.localRotation = Quaternion.Euler(0f, 0f, angle);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Rounded(true);
            img.type = Image.Type.Sliced;
            img.color = TextMuted;
            img.raycastTarget = false;
            return img;
        }

        public void TickHeader()
        {
            bool overClose = Contains(_closeRt);
            _closeImg.color = Lerp(_closeImg.color, overClose ? CloseHover : ControlBg, 0.3f);
            var barColor = Lerp(_closeBar1.color, overClose ? TextBright : TextMuted, 0.3f);
            _closeBar1.color = barColor;
            _closeBar2.color = barColor;
            _closeScale = Mathf.Lerp(_closeScale, overClose ? 1.14f : 1f, 0.3f);
            _closeRt.localScale = new Vector3(_closeScale, _closeScale, 1f);

            if (Input.GetMouseButtonDown(0))
            {
                if (overClose) { _onClose(); return; }
                if (Contains(HeaderRect) && LocalPoint(CanvasRect, out var lp))
                {
                    _dragging = true;
                    _dragOffset = _userPosition - lp;
                }
            }
            else if (Input.GetMouseButton(0) && _dragging)
            {
                if (LocalPoint(CanvasRect, out var lp))
                {
                    _userPosition = lp + _dragOffset;
                    ApplyRootPosition();
                }
            }
            else
            {
                _dragging = false;
            }
        }

        public void SetLayoutOffset(Vector2 offset)
        {
            _layoutOffset = offset;
            ApplyRootPosition();
        }

        private void ApplyRootPosition()
        {
            if (RootRect != null)
                RootRect.anchoredPosition = _userPosition + _layoutOffset;
        }
    }

    public sealed class CategoryRail
    {
        private sealed class Item
        {
            public RectTransform Root = null!;
            public Image Hover = null!;
            public TextMeshProUGUI Label = null!;
            public float Y;
        }

        private readonly List<Item> _items = new();
        private int _selected;
        public int Selected => _selected;
        public Action<int>? OnSelect;

        private RectTransform _highlight = null!;
        private RectTransform _hiBar = null!;
        private float _hiY;
        private const float RowH = 62f;
        private const float ItemH = RowH - 8f;

        public void Build(RectTransform railRoot, float railWidth, string[] labels,
            (int index, string title)[]? sections = null)
        {
            const float top = -22f;
            const float sectionGap = 30f;

            var hiGlow = GlowImage("RailGlow", railRoot, AccentGlow);
            _highlight = Rect("RailHighlight", railRoot);
            _highlight.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            _highlight.sizeDelta = new Vector2(-16f, ItemH);
            var hiImg = _highlight.gameObject.AddComponent<Image>();
            hiImg.sprite = Rounded();
            hiImg.type = Image.Type.Sliced;
            hiImg.color = AccentFaint;
            hiImg.raycastTarget = false;
            hiGlow.rectTransform.SetParent(_highlight, false);
            hiGlow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            hiGlow.rectTransform.offsetMin = new Vector2(-6f, -6f);
            hiGlow.rectTransform.offsetMax = new Vector2(6f, 6f);

            _hiBar = Rect("RailBar", _highlight);
            _hiBar.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _hiBar.sizeDelta = new Vector2(4f, ItemH - 14f);
            _hiBar.anchoredPosition = new Vector2(6f, 0f);
            var barImg = _hiBar.gameObject.AddComponent<Image>();
            barImg.sprite = Rounded(true);
            barImg.type = Image.Type.Sliced;
            barImg.color = Accent;
            barImg.raycastTarget = false;

            float y = top;
            for (int i = 0; i < labels.Length; i++)
            {
                if (sections != null)
                {
                    for (int sidx = 0; sidx < sections.Length; sidx++)
                    {
                        if (sections[sidx].index != i) continue;
                        y -= sectionGap;
                        BuildSection(railRoot, sections[sidx].title, y + ItemH * 0.5f);
                        break;
                    }
                }

                var rt = Rect("Cat_" + labels[i], railRoot);
                rt.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                rt.sizeDelta = new Vector2(-16f, ItemH);
                rt.anchoredPosition = new Vector2(0f, y);

                var hover = rt.gameObject.AddComponent<Image>();
                hover.sprite = Rounded();
                hover.type = Image.Type.Sliced;
                hover.color = Clear;
                hover.raycastTarget = false;

                var label = Text("Label", rt, labels[i], 19f, TextMuted, TextAlignmentOptions.Left, FontStyles.Bold);
                label.characterSpacing = 3f;
                label.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0.5f));
                label.rectTransform.sizeDelta = new Vector2(-32f, RowH);
                label.rectTransform.anchoredPosition = new Vector2(22f, 0f);

                _items.Add(new Item { Root = rt, Hover = hover, Label = label, Y = y });
                y -= RowH;
            }

            _hiY = _items.Count > 0 ? _items[_selected].Y : top;
            _highlight.anchoredPosition = new Vector2(0f, _hiY);
            Apply();
        }

        public void Tick()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                bool sel = i == _selected;
                var it = _items[i];
                bool hover = !sel && Contains(it.Root);
                it.Hover.color = Lerp(it.Hover.color, hover ? RowHover : Clear, 0.25f);
                it.Label.color = Lerp(it.Label.color, sel ? Accent : (hover ? TextPrimary : TextMuted), 0.25f);
                if (hover && Input.GetMouseButtonDown(0)) Select(i);
            }

            if (_items.Count > 0)
            {
                float target = _items[_selected].Y;
                _hiY = Mathf.Lerp(_hiY, target, 0.3f);
                _highlight.anchoredPosition = new Vector2(0f, _hiY);
            }
        }

        public void Select(int idx)
        {
            if (idx < 0 || idx >= _items.Count || idx == _selected) { if (idx == _selected) return; }
            _selected = Mathf.Clamp(idx, 0, _items.Count - 1);
            Apply();
            OnSelect?.Invoke(_selected);
        }

        private void Apply()
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i].Label.color = i == _selected ? Accent : TextMuted;
        }

        private static void BuildSection(RectTransform railRoot, string title, float y)
        {
            var div = Rect("RailSectionDiv", railRoot);
            div.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            div.sizeDelta = new Vector2(-32f, 1f);
            div.anchoredPosition = new Vector2(0f, y + 13f);
            var divImg = div.gameObject.AddComponent<Image>();
            divImg.sprite = Solid(Color.white);
            divImg.color = Divider;
            divImg.raycastTarget = false;

            var label = Text("RailSectionLabel", railRoot, title, 13f, TextFaint, TextAlignmentOptions.Left, FontStyles.Bold);
            label.characterSpacing = 4f;
            label.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            label.rectTransform.sizeDelta = new Vector2(-44f, 20f);
            label.rectTransform.anchoredPosition = new Vector2(22f, y - 4f);
        }
    }

    public abstract class Row
    {
        public RectTransform Root = null!;
        public Image? Hover;
        public TextMeshProUGUI Title = null!;
        private RectTransform? _helpRt;
        private Image? _helpBadge;
        private TextMeshProUGUI? _helpGlyph;
        private RectTransform? _clip;
        private string? _helpText;
        public float Height = 72f;
        protected float PaneW;
        public virtual void Tick(float dt) { }
        public virtual bool IsDragging => false;
        public virtual void OnMouseDown() { }
        public virtual void OnMouseDrag() { }
        public virtual void OnMouseUp() { }

        public const float EdgePad = 22f;
        public const float ColGap = 24f;
        public const float ValueColW = 110f;

        protected virtual float LabelColW => Mathf.Round(PaneW * 0.42f);
        protected float ControlLeft => EdgePad + LabelColW + ColGap;
        protected float ControlRight => PaneW - EdgePad - ValueColW - ColGap;
        protected float ControlColW => Mathf.Max(120f, ControlRight - ControlLeft);

        protected bool PointerWithinClip => _clip == null || Contains(_clip);
        protected bool HelpHovered => _helpRt != null
            && _helpRt.gameObject.activeInHierarchy
            && PointerWithinClip
            && Contains(_helpRt);

        protected void BuildBase(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            Height = height;
            PaneW = width;
            _clip = pane.parent as RectTransform;
            Root = Rect("Row", pane);
            Root.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            Root.sizeDelta = new Vector2(0f, height);
            Root.anchoredPosition = new Vector2(0f, y);

            Hover = Panel("Hover", Root, Clear, true);
            Hover.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            Hover.rectTransform.offsetMin = new Vector2(0f, 3f);
            Hover.rectTransform.offsetMax = new Vector2(0f, -3f);

            var title = Text("RowLabel", Root, label, 20f, TextPrimary, TextAlignmentOptions.Left);
            title.overflowMode = TextOverflowModes.Ellipsis;
            title.enableAutoSizing = true;
            title.fontSizeMax = 20f;
            title.fontSizeMin = 15f;
            title.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            title.rectTransform.sizeDelta = new Vector2(LabelColW, height);
            title.rectTransform.anchoredPosition = new Vector2(EdgePad, 0f);
            Title = title;

            if (!string.IsNullOrWhiteSpace(helpText))
                BuildHelp(helpText);
        }

        private void BuildHelp(string helpText)
        {
            const float hitD = 26f;
            const float badgeD = 20f;
            const float gap = 6f;
            _helpText = helpText;

            float availableTitleW = Mathf.Max(40f, LabelColW - hitD - gap);
            Title.rectTransform.sizeDelta = new Vector2(availableTitleW, Height);
            float preferredW = Title.GetPreferredValues(Title.text).x;
            float iconX = Mathf.Min(preferredW, availableTitleW) + gap + hitD * 0.5f;

            _helpRt = Rect("Help", Title.rectTransform);
            _helpRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _helpRt.sizeDelta = new Vector2(hitD, hitD);
            _helpRt.anchoredPosition = new Vector2(iconX, 0f);

            var badge = Rect("Badge", _helpRt);
            badge.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            badge.sizeDelta = new Vector2(badgeD, badgeD);
            _helpBadge = badge.gameObject.AddComponent<Image>();
            _helpBadge.sprite = Rounded(true);
            _helpBadge.type = Image.Type.Sliced;
            _helpBadge.color = ControlBg;
            _helpBadge.raycastTarget = false;

            _helpGlyph = Text("Glyph", badge, "!", 17f, Accent,
                TextAlignmentOptions.Center, FontStyles.Bold);
            _helpGlyph.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _helpGlyph.rectTransform.offsetMin = Vector2.zero;
            _helpGlyph.rectTransform.offsetMax = Vector2.zero;
        }

        protected void TickHover()
        {
            if (Hover == null) return;
            bool over = PointerWithinClip && Contains(Root);
            Hover.color = Lerp(Hover.color, over ? RowHover : Clear, 0.22f);
            TickHelp();
        }

        private void TickHelp()
        {
            if (_helpRt == null || _helpBadge == null || _helpGlyph == null
                || string.IsNullOrWhiteSpace(_helpText)) return;
            bool over = HelpHovered;
            _helpBadge.color = Lerp(
                _helpBadge.color,
                over ? AccentFaint : ControlBg,
                0.28f);
            _helpGlyph.color = Lerp(
                _helpGlyph.color,
                over ? TextBright : Accent,
                0.28f);
            float scale = Mathf.Lerp(_helpRt.localScale.x, over ? 1.12f : 1f, 0.3f);
            _helpRt.localScale = new Vector3(scale, scale, 1f);
            if (over) RequestTooltip(Title.text, _helpText!);
        }
    }

    public sealed class ToggleRow : Row
    {
        private readonly Func<bool> _get;
        private readonly Action<bool> _set;
        private Image _track = null!;
        private Image _glow = null!;
        private RectTransform _knob = null!;
        private Image _knobImg = null!;
        private Image _knobShadow = null!;
        private float _knobT;

        public ToggleRow(Func<bool> get, Action<bool> set) { _get = get; _set = set; }

        protected override float LabelColW => Mathf.Round(PaneW - EdgePad * 2f - ColGap - 66f);

        public ToggleRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            var trackRt = Rect("Track", Root);
            trackRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            trackRt.sizeDelta = new Vector2(66f, 34f);
            trackRt.anchoredPosition = new Vector2(ControlLeft, 0f);

            _glow = GlowImage("TrackGlow", trackRt, Clear);
            _glow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _glow.rectTransform.offsetMin = new Vector2(-10f, -10f);
            _glow.rectTransform.offsetMax = new Vector2(10f, 10f);

            _track = trackRt.gameObject.AddComponent<Image>();
            _track.sprite = Rounded(true);
            _track.type = Image.Type.Sliced;
            _track.color = ToggleOffTrack;
            _track.raycastTarget = false;

            _knob = Rect("Knob", trackRt);
            _knob.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _knob.sizeDelta = new Vector2(26f, 26f);

            _knobShadow = GlowImage("KnobShadow", _knob, new Color32(0, 0, 0, 130));
            _knobShadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _knobShadow.rectTransform.offsetMin = new Vector2(-5f, -7f);
            _knobShadow.rectTransform.offsetMax = new Vector2(5f, 3f);

            _knobImg = Rect("KnobFill", _knob).gameObject.AddComponent<Image>();
            _knobImg.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _knobImg.rectTransform.offsetMin = Vector2.zero;
            _knobImg.rectTransform.offsetMax = Vector2.zero;
            _knobImg.sprite = Rounded(true);
            _knobImg.type = Image.Type.Sliced;
            _knobImg.color = KnobOff;
            _knobImg.raycastTarget = false;

            _knobT = _get() ? 1f : 0f;
            ApplyKnob();
            return this;
        }

        private void ApplyKnob()
        {
            float e = _knobT * _knobT * (3f - 2f * _knobT);
            _knob.anchoredPosition = new Vector2(Mathf.Lerp(20f, 46f, e), 0f);
            _track.color = Lerp(ToggleOffTrack, Accent, _knobT);
            _knobImg.color = Lerp(KnobOff, TextBright, _knobT);
            var g = AccentGlow; g.a = (byte)(AccentGlow.a * _knobT); _glow.color = g;
        }

        public override void OnMouseDown()
        {
            if (Contains(Root) && !HelpHovered) _set(!_get());
        }

        public override void Tick(float dt)
        {
            TickHover();
            float target = _get() ? 1f : 0f;
            if (Mathf.Abs(_knobT - target) > 0.001f)
            {
                _knobT = Mathf.MoveTowards(_knobT, target, dt * 8f);
                ApplyKnob();
            }
        }
    }

    public sealed class SliderRow : Row
    {
        private readonly Func<float> _get;
        private readonly Action<float> _set;
        private readonly float _min, _max;
        private readonly Func<float, string> _fmt;
        private RectTransform _track = null!;
        private RectTransform _fill = null!;
        private Image _fillGlow = null!;
        private RectTransform _knob = null!;
        private TextMeshProUGUI _value = null!;
        private bool _dragging;
        public override bool IsDragging => _dragging;

        public SliderRow(Func<float> get, Action<float> set, float min, float max, Func<float, string> fmt)
        { _get = get; _set = set; _min = min; _max = max; _fmt = fmt; }

        public SliderRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            var pill = Rect("Pill", Root);
            pill.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            pill.sizeDelta = new Vector2(ValueColW, 36f);
            pill.anchoredPosition = new Vector2(-EdgePad, 0f);
            var pillImg = pill.gameObject.AddComponent<Image>();
            pillImg.sprite = Rounded(true);
            pillImg.type = Image.Type.Sliced;
            pillImg.color = AccentFaint;
            pillImg.raycastTarget = false;

            _value = Text("Value", pill, "", 19f, Accent, TextAlignmentOptions.Center, FontStyles.Bold);
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(6f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-6f, 0f);

            _track = Rect("Track", Root);
            _track.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            float trackW = ControlColW;
            _track.sizeDelta = new Vector2(trackW, 9f);
            _track.anchoredPosition = new Vector2(ControlLeft, 0f);
            var trackImg = _track.gameObject.AddComponent<Image>();
            trackImg.sprite = Rounded(true);
            trackImg.type = Image.Type.Sliced;
            trackImg.color = TrackBg;
            trackImg.raycastTarget = false;

            _fillGlow = GlowImage("FillGlow", _track, AccentGlow);
            _fillGlow.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));

            _fill = Rect("Fill", _track);
            _fill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _fill.sizeDelta = new Vector2(0f, 9f);
            var fillImg = _fill.gameObject.AddComponent<Image>();
            fillImg.sprite = Rounded(true);
            fillImg.type = Image.Type.Sliced;
            fillImg.color = Accent;
            fillImg.raycastTarget = false;

            _knob = Rect("Knob", _track);
            _knob.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _knob.sizeDelta = new Vector2(22f, 22f);
            var knobShadow = GlowImage("KnobShadow", _knob, new Color32(0, 0, 0, 140));
            knobShadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            knobShadow.rectTransform.offsetMin = new Vector2(-5f, -7f);
            knobShadow.rectTransform.offsetMax = new Vector2(5f, 3f);
            var knobImg = Rect("KnobFill", _knob).gameObject.AddComponent<Image>();
            knobImg.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            knobImg.rectTransform.offsetMin = Vector2.zero;
            knobImg.rectTransform.offsetMax = Vector2.zero;
            knobImg.sprite = Rounded(true);
            knobImg.type = Image.Type.Sliced;
            knobImg.color = TextBright;
            knobImg.raycastTarget = false;

            ApplyVisual(Normalized());
            return this;
        }

        private float Normalized() => Mathf.Approximately(_max, _min) ? 0f : Mathf.Clamp01((_get() - _min) / (_max - _min));

        private float _lastRaw = float.NaN;
        private void ApplyVisual(float t)
        {
            float w = _track.sizeDelta.x;
            _fill.sizeDelta = new Vector2(w * t, 9f);
            _fillGlow.rectTransform.sizeDelta = new Vector2(w * t + 14f, 22f);
            _knob.anchoredPosition = new Vector2(w * t, 0f);
            float raw = _get();
            if (raw != _lastRaw)
            {
                _lastRaw = raw;
                _value.text = _fmt(raw);
            }
        }

        public override void OnMouseDown()
        {
            if (Contains(_track) || Contains(_knob)) { _dragging = true; ApplyFromMouse(); }
        }

        public override void OnMouseDrag()
        {
            if (_dragging) ApplyFromMouse();
        }

        public override void OnMouseUp() => _dragging = false;

        private void ApplyFromMouse()
        {
            if (!LocalPoint(_track, out var lp)) return;
            float w = _track.sizeDelta.x;
            float t = Mathf.Clamp01(lp.x / w);
            _set(_min + t * (_max - _min));
            ApplyVisual(t);
        }

        public override void Tick(float dt)
        {
            TickHover();
            if (!_dragging) ApplyVisual(Normalized());
        }
    }

    public sealed class StepperRow : Row
    {
        private readonly Func<int> _getIndex;
        private readonly Action<int> _setIndex;
        private readonly Func<int> _count;
        private readonly Func<int, string> _labelOf;
        private readonly bool _fullWidthValue;
        private Image _left = null!;
        private Image _right = null!;
        private TextMeshProUGUI _value = null!;
        private RectTransform _valuePill = null!;

        public StepperRow(
            Func<int> getIndex,
            Action<int> setIndex,
            Func<int> count,
            Func<int, string> labelOf,
            bool fullWidthValue = false)
        {
            _getIndex = getIndex;
            _setIndex = setIndex;
            _count = count;
            _labelOf = labelOf;
            _fullWidthValue = fullWidthValue;
        }

        public StepperRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            float groupW = _fullWidthValue
                ? PaneW - EdgePad * 2f
                : (PaneW - EdgePad) - ControlLeft;
            var group = Rect("Stepper", Root);
            group.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            group.sizeDelta = new Vector2(groupW, _fullWidthValue ? 76f : 40f);
            group.anchoredPosition = _fullWidthValue
                ? new Vector2(EdgePad, -height * 0.23f)
                : new Vector2(ControlLeft, 0f);

            if (_fullWidthValue)
            {
                Title.rectTransform.sizeDelta = new Vector2(Title.rectTransform.sizeDelta.x, 42f);
                Title.rectTransform.anchoredPosition = new Vector2(EdgePad, height * 0.25f);
            }

            _valuePill = Rect("ValuePill", group);
            _valuePill.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            _valuePill.offsetMin = new Vector2(44f, 2f);
            _valuePill.offsetMax = new Vector2(-44f, -2f);
            var valuePillImg = _valuePill.gameObject.AddComponent<Image>();
            valuePillImg.sprite = Rounded(true);
            valuePillImg.type = Image.Type.Sliced;
            valuePillImg.color = AccentFaint;
            valuePillImg.raycastTarget = false;

            _value = Text("Value", _valuePill, "", 18f, Accent, TextAlignmentOptions.Center, FontStyles.Bold);
            _value.overflowMode = _fullWidthValue
                ? TextOverflowModes.Overflow
                : TextOverflowModes.Ellipsis;
            _value.enableAutoSizing = _fullWidthValue;
            _value.fontSizeMax = 18f;
            _value.fontSizeMin = 10f;
            _value.enableWordWrapping = _fullWidthValue;
            _value.richText = !_fullWidthValue;
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(8f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-8f, 0f);

            _left = Arrow(group, "<", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(2f, 0f));
            _right = Arrow(group, ">", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-2f, 0f));

            Refresh();
            return this;
        }

        private Image Arrow(RectTransform parent, string glyph, Vector2 aMin, Vector2 aMax, Vector2 pos)
        {
            var rt = Rect("Arrow", parent);
            rt.Anchor(aMin, aMax, new Vector2(aMin.x, 0.5f));
            rt.sizeDelta = new Vector2(38f, 38f);
            rt.anchoredPosition = pos;
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Rounded();
            img.type = Image.Type.Sliced;
            img.color = ControlBg;
            img.raycastTarget = false;
            var t = Text("G", rt, glyph, 24f, TextMuted, TextAlignmentOptions.Center, FontStyles.Bold);
            t.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            t.rectTransform.offsetMin = Vector2.zero;
            t.rectTransform.offsetMax = Vector2.zero;
            return img;
        }

        public void Refresh()
        {
            int n = _count();
            int i = n > 0 ? Mathf.Clamp(_getIndex(), 0, n - 1) : 0;
            if (n > 0)
            {
                _value.text = _labelOf(i);
                _value.color = Accent;
            }
            else
            {
                _value.text = _fullWidthValue ? "--" : "<color=#607282>--</color>";
                _value.color = _fullWidthValue ? TextFaint : Accent;
            }
        }

        public override void OnMouseDown()
        {
            int n = _count();
            if (n <= 0) return;
            int cur = Mathf.Clamp(_getIndex(), 0, n - 1);
            if (Contains(_left.GetComponent<RectTransform>())) { _setIndex((cur - 1 + n) % n); Refresh(); }
            else if (Contains(_right.GetComponent<RectTransform>())) { _setIndex((cur + 1) % n); Refresh(); }
        }

        public override void Tick(float dt)
        {
            TickHover();
            ArrowTick(_left);
            ArrowTick(_right);
            if (_fullWidthValue && PointerWithinClip && Contains(_valuePill))
            {
                int n = _count();
                int i = n > 0 ? Mathf.Clamp(_getIndex(), 0, n - 1) : 0;
                if (n > 0) RequestTooltip(
                    "Selected device", _labelOf(i), plainTextDescription: true);
            }
        }

        private static void ArrowTick(Image arrow)
        {
            var rt = arrow.GetComponent<RectTransform>();
            bool over = Contains(rt);
            bool press = over && Input.GetMouseButton(0);
            arrow.color = Lerp(arrow.color, over ? ControlHover : ControlBg, 0.25f);
            float s = Mathf.Lerp(rt.localScale.x, press ? 0.9f : 1f, 0.35f);
            rt.localScale = new Vector3(s, s, 1f);
        }
    }

    public sealed class RebindRow : Row
    {
        private readonly Func<KeyCode> _get;
        private readonly Action<KeyCode, KeyCode, VoiceModifierMatch> _set;
        private readonly Action _clear;
        private readonly Func<KeyCode>? _getMod;
        private readonly Func<VoiceModifierMatch>? _getModMatch;
        private readonly Action? _configure;
        private readonly Func<bool>? _configureActive;
        private Image _btn = null!;
        private RectTransform _btnRt = null!;
        private TextMeshProUGUI _label = null!;
        private RectTransform? _settingsRt;
        private Image? _settingsBtn;
        private RectTransform _capRow = null!;
        private Image _clearBtn = null!;
        private Image _cancelBtn = null!;
        private float _normalLeft;
        private float _capLeft;
        private bool _capturing;
        private bool _armed;
        private KeyCode _pendingModifier;
        private static RebindRow? _active;
        private static int _lastConsumedInputFrame = -1;
        private static KeyCode _suppressUntilPrimaryReleased;
        private static KeyCode _suppressUntilModifierReleased;

        private const float CapW = 170f;
        private const float NormalBtnW = 185f;
        private const float CapBtnW = 150f;
        private const float SettingsW = 40f;
        private const float SettingsGap = 10f;

        public RebindRow(
            Func<KeyCode> get,
            Action<KeyCode, KeyCode, VoiceModifierMatch> set,
            Action clear,
            Func<KeyCode>? getMod = null,
            Func<VoiceModifierMatch>? getModMatch = null,
            Action? configure = null,
            Func<bool>? configureActive = null)
        {
            _get = get;
            _set = set;
            _clear = clear;
            _getMod = getMod;
            _getModMatch = getModMatch;
            _configure = configure;
            _configureActive = configureActive;
        }

        protected override float LabelColW => Mathf.Round(
            PaneW - EdgePad * 2f - ColGap - NormalBtnW
            - (_configure == null ? 0f : SettingsW + SettingsGap));

        public static bool IsCapturing => _active != null;
        public static bool ShouldSuppressKeybinds
        {
            get
            {
                if (_active != null || _lastConsumedInputFrame == Time.frameCount) return true;
                if (_suppressUntilPrimaryReleased == KeyCode.None
                    && _suppressUntilModifierReleased == KeyCode.None) return false;
                if (IsHeld(_suppressUntilPrimaryReleased)
                    || IsHeld(_suppressUntilModifierReleased)) return true;

                _suppressUntilPrimaryReleased = KeyCode.None;
                _suppressUntilModifierReleased = KeyCode.None;
                return false;
            }
        }
        public static void CancelCapture()
        {
            if (_active != null) { _active.EndCapture(); }
        }

        public RebindRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            _normalLeft = (PaneW - EdgePad) - NormalBtnW;
            _capLeft = (PaneW - EdgePad) - (CapBtnW + ColGap + CapW);
            _btnRt = Rect("Bind", Root);
            _btnRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _btnRt.sizeDelta = new Vector2(NormalBtnW, 40f);
            _btnRt.anchoredPosition = new Vector2(_normalLeft, 0f);
            _btn = _btnRt.gameObject.AddComponent<Image>();
            _btn.sprite = Rounded();
            _btn.type = Image.Type.Sliced;
            _btn.color = ControlBg;
            _btn.raycastTarget = false;

            _label = Text("Key", _btnRt, "", 18f, TextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
            _label.overflowMode = TextOverflowModes.Ellipsis;
            _label.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _label.rectTransform.offsetMin = new Vector2(10f, 0f);
            _label.rectTransform.offsetMax = new Vector2(-10f, 0f);

            if (_configure != null)
            {
                _settingsRt = Rect("Configure", Root);
                _settingsRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                _settingsRt.sizeDelta = new Vector2(SettingsW, SettingsW);
                _settingsRt.anchoredPosition = new Vector2(_normalLeft - SettingsGap - SettingsW, 0f);
                _settingsBtn = _settingsRt.gameObject.AddComponent<Image>();
                _settingsBtn.sprite = Rounded();
                _settingsBtn.type = Image.Type.Sliced;
                _settingsBtn.color = ControlBg;
                _settingsBtn.raycastTarget = false;
                BuildSettingsGlyph(_settingsRt);
            }

            _capRow = Rect("CapControls", Root);
            _capRow.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _capRow.sizeDelta = new Vector2(CapW, 36f);
            _capRow.anchoredPosition = new Vector2((PaneW - EdgePad) - CapW * 0.5f, 0f);
            _capRow.pivot = new Vector2(0.5f, 0.5f);

            _clearBtn = CapButton(_capRow, "Clear", new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), DangerDim);
            _cancelBtn = CapButton(_capRow, "Cancel", new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0f), ControlBg);
            _capRow.gameObject.SetActive(false);

            RefreshLabel();
            return this;
        }

        private void SetCapLayout(bool cap)
        {
            if (cap)
            {
                _btnRt.anchoredPosition = new Vector2(_capLeft, 0f);
                _btnRt.sizeDelta = new Vector2(CapBtnW, 40f);
                _capRow.gameObject.SetActive(true);
                if (_settingsRt != null) _settingsRt.gameObject.SetActive(false);
                if (Title != null) Title.gameObject.SetActive(false);
            }
            else
            {
                _btnRt.anchoredPosition = new Vector2(_normalLeft, 0f);
                _btnRt.sizeDelta = new Vector2(NormalBtnW, 40f);
                _capRow.gameObject.SetActive(false);
                if (_settingsRt != null) _settingsRt.gameObject.SetActive(true);
                if (Title != null) Title.gameObject.SetActive(true);
            }
        }

        private static void BuildSettingsGlyph(RectTransform parent)
        {
            float[] ys = { 7f, 0f, -7f };
            float[] knobXs = { -5f, 6f, -1f };
            for (int i = 0; i < ys.Length; i++)
            {
                var bar = Rect("TuneBar", parent);
                bar.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                bar.sizeDelta = new Vector2(22f, 2.5f);
                bar.anchoredPosition = new Vector2(0f, ys[i]);
                var barImg = bar.gameObject.AddComponent<Image>();
                barImg.sprite = Rounded(true);
                barImg.type = Image.Type.Sliced;
                barImg.color = TextMuted;
                barImg.raycastTarget = false;

                var knob = Rect("TuneKnob", parent);
                knob.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                knob.sizeDelta = new Vector2(6f, 6f);
                knob.anchoredPosition = new Vector2(knobXs[i], ys[i]);
                var knobImg = knob.gameObject.AddComponent<Image>();
                knobImg.sprite = Rounded(true);
                knobImg.type = Image.Type.Sliced;
                knobImg.color = TextBright;
                knobImg.raycastTarget = false;
            }
        }

        private static Image CapButton(RectTransform parent, string text, Vector2 aMin, Vector2 aMax, Vector2 pos, Color32 col)
        {
            var rt = Rect("Cap_" + text, parent);
            rt.Anchor(aMin, aMax, new Vector2(aMin.x, 0.5f));
            rt.sizeDelta = new Vector2(78f, 36f);
            rt.anchoredPosition = pos;
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Rounded(true);
            img.type = Image.Type.Sliced;
            img.color = col;
            img.raycastTarget = false;
            var t = Text("T", rt, text, 16f, TextBright, TextAlignmentOptions.Center, FontStyles.Bold);
            t.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            t.rectTransform.offsetMin = Vector2.zero;
            t.rectTransform.offsetMax = Vector2.zero;
            return img;
        }

        private void RefreshLabel()
        {
            if (_capturing)
            {
                _label.text = !_armed
                    ? "<color=#8C9CB2>Release to bind...</color>"
                    : _pendingModifier != KeyCode.None
                        ? $"<color=#22D3EE>{VoiceKeybind.FormatKey(_pendingModifier)} + key, or release</color>"
                        : "<color=#22D3EE>Press any key...</color>";
                return;
            }
            var mod = _getMod?.Invoke() ?? KeyCode.None;
            var key = _get();
            if (key == KeyCode.None)
                _label.text = "<color=#607282>None</color>";
            else if (mod == KeyCode.None)
                _label.text = VoiceKeybind.FormatKey(key);
            else
                _label.text = VoiceKeybind.FormatModifier(
                    mod,
                    _getModMatch?.Invoke() ?? VoiceModifierMatch.EitherSide)
                    + "+" + VoiceKeybind.FormatKey(key);
        }

        private void EndCapture()
        {
            _capturing = false;
            _armed = false;
            _pendingModifier = KeyCode.None;
            if (_active == this) _active = null;
            SetCapLayout(false);
            RefreshLabel();
        }

        public override void OnMouseDown()
        {
            if (_capturing) return;
            if (_configure != null && _settingsRt != null && Contains(_settingsRt))
            {
                if (_active != null) _active.EndCapture();
                _configure();
                return;
            }
            if (Contains(_btnRt))
            {
                if (_active != null && _active != this) _active.EndCapture();
                _capturing = true;
                _armed = false;
                _pendingModifier = KeyCode.None;
                _active = this;
                SetCapLayout(true);
                RefreshLabel();
            }
        }

        public override void Tick(float dt)
        {
            TickHover();
            _btn.color = Lerp(_btn.color,
                _capturing ? AccentFaint : (Contains(_btnRt) ? ControlHover : ControlBg), 0.25f);
            if (_settingsBtn != null && _settingsRt != null)
            {
                bool settingsOver = Contains(_settingsRt);
                bool settingsPressed = settingsOver && Input.GetMouseButton(0);
                bool settingsActive = _configureActive?.Invoke() == true;
                _settingsBtn.color = Lerp(
                    _settingsBtn.color,
                    settingsOver
                        ? (settingsActive ? AccentSoft : ControlHover)
                        : (settingsActive ? AccentFaint : ControlBg),
                    0.25f);
                float settingsScale = Mathf.Lerp(
                    _settingsRt.localScale.x,
                    settingsPressed ? 0.9f : 1f,
                    0.35f);
                _settingsRt.localScale = new Vector3(settingsScale, settingsScale, 1f);
            }

            if (!_capturing) return;

            _clearBtn.color = Lerp(_clearBtn.color, Contains(_clearBtn.GetComponent<RectTransform>()) ? Danger : DangerDim, 0.25f);
            _cancelBtn.color = Lerp(_cancelBtn.color, Contains(_cancelBtn.GetComponent<RectTransform>()) ? ControlHover : ControlBg, 0.25f);

            if (!_armed)
            {
                if (Input.GetMouseButtonUp(0)) { _armed = true; RefreshLabel(); }
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            { ConsumeCaptureInput(KeyCode.Escape); EndCapture(); return; }
            if (Input.GetKeyDown(KeyCode.Delete))
            { _clear(); ConsumeCaptureInput(KeyCode.Delete); EndCapture(); return; }
            if (Input.GetKeyDown(KeyCode.Backspace))
            { _clear(); ConsumeCaptureInput(KeyCode.Backspace); EndCapture(); return; }

            for (int m = 0; m <= 6; m++)
            {
                if (!Input.GetMouseButtonDown(m)) continue;
                if (Contains(_clearBtn.GetComponent<RectTransform>())) { _clear(); ConsumeCaptureInput(MouseToKey(m)); EndCapture(); return; }
                if (Contains(_cancelBtn.GetComponent<RectTransform>())) { ConsumeCaptureInput(MouseToKey(m)); EndCapture(); return; }
                Commit(MouseToKey(m), PendingOrHeldModifier());
                return;
            }

            if (_pendingModifier == KeyCode.None)
            {
                var pressedModifier = PressedModifier();
                if (pressedModifier != KeyCode.None)
                {
                    _pendingModifier = pressedModifier;
                    RefreshLabel();
                }
            }

            foreach (var kc in _keyCandidates)
            {
                if (kc == KeyCode.Escape || kc == KeyCode.Delete || kc == KeyCode.Backspace) continue;
                if (IsModifierKey(kc)) continue;
                if (Input.GetKeyDown(kc)) { Commit(kc, PendingOrHeldModifier()); return; }
            }

            if (_pendingModifier != KeyCode.None && Input.GetKeyUp(_pendingModifier))
            {
                Commit(_pendingModifier, KeyCode.None);
            }
        }

        private void Commit(KeyCode key, KeyCode modifier)
        {
            _set(key, modifier, VoiceModifierMatch.Exact);
            ConsumeCaptureInput(key, modifier);
            EndCapture();
        }

        private KeyCode PendingOrHeldModifier()
            => _pendingModifier != KeyCode.None ? _pendingModifier : HeldModifier();

        private static void ConsumeCaptureInput(
            KeyCode primary = KeyCode.None,
            KeyCode modifier = KeyCode.None)
        {
            _lastConsumedInputFrame = Time.frameCount;
            _suppressUntilPrimaryReleased = primary;
            _suppressUntilModifierReleased = modifier;
        }

        private static bool IsHeld(KeyCode key)
            => key != KeyCode.None && Input.GetKey(key);

        private static bool IsModifierKey(KeyCode k) => VoiceKeybind.IsModifierKey(k);

        private static KeyCode PressedModifier()
        {
            foreach (var modifier in _modifierCandidates)
                if (Input.GetKeyDown(modifier)) return modifier;
            return KeyCode.None;
        }

        private static KeyCode HeldModifier()
        {
            foreach (var modifier in _modifierCandidates)
                if (Input.GetKey(modifier)) return modifier;
            return KeyCode.None;
        }

        private static readonly KeyCode[] _modifierCandidates =
        {
            KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr,
            KeyCode.LeftCommand, KeyCode.RightCommand,
            KeyCode.LeftWindows, KeyCode.RightWindows,
        };

        private static KeyCode MouseToKey(int m) => m switch
        {
            0 => KeyCode.Mouse0,
            1 => KeyCode.Mouse1,
            2 => KeyCode.Mouse2,
            3 => KeyCode.Mouse3,
            4 => KeyCode.Mouse4,
            5 => KeyCode.Mouse5,
            6 => KeyCode.Mouse6,
            _ => KeyCode.None
        };

        private static readonly KeyCode[] _keyCandidates = BuildKeyCandidates();
        private static KeyCode[] BuildKeyCandidates()
        {
            var list = new List<KeyCode>();
            foreach (var v in Enum.GetValues(typeof(KeyCode)))
            {
                var kc = (KeyCode)v;
                if ((int)kc >= (int)KeyCode.Mouse0 && (int)kc <= (int)KeyCode.Mouse6) continue;
                list.Add(kc);
            }
            return list.ToArray();
        }
    }

    public sealed class PlayerVolumeRow : Row
    {
        private readonly Func<float> _get;
        private readonly Action<float> _onChange;
        private readonly Action _onCommit;
        private readonly PlayerControl? _pc;
        private readonly float _min, _max;

        public byte PlayerId;

        private RectTransform _track = null!;
        private RectTransform _fill = null!;
        private Image _fillImg = null!;
        private RectTransform _knob = null!;
        private RectTransform _meterFill = null!;
        private Image _meterFillImg = null!;
        private Image _avatarGlow = null!;
        private RectTransform _resetRt = null!;
        private Image _resetImg = null!;
        private TextMeshProUGUI _value = null!;
        private Color _glowColor;
        private float _trackW;
        private float _meterW;
        private float _meterDisplay;
        private float _meterTarget;
        private bool _meterSpeaking;
        private bool _dragging;
        private bool _changed;
        public override bool IsDragging => _dragging;

        private static readonly Color32 MeterGreen  = new(77, 217, 107, 240);
        private static readonly Color32 MeterYellow = new(242, 214, 64, 240);
        private static readonly Color32 MeterRed    = new(245, 64, 56, 240);
        private static readonly Color32 FillLow     = new(46, 82, 140, 235);
        private static readonly Color32 FillBoost   = new(235, 171, 61, 235);
        private const float MeterRelease = 1.6f;

        private static Sprite? _resetIcon;
        private static Sprite ResetIcon =>
            _resetIcon ??= VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.VolumeResetIcon.png", highQuality: true);

        public PlayerVolumeRow(Func<float> get, Action<float> onChange, Action onCommit,
            PlayerControl? pc, float min, float max)
        { _get = get; _onChange = onChange; _onCommit = onCommit; _pc = pc; _min = min; _max = max; }

        public PlayerVolumeRow Build(RectTransform pane, string name, float width, float y, float height)
        {
            BuildBase(pane, "", width, y, height);

            const float avatarD = 50f;
            float avatarCx = EdgePad + avatarD * 0.5f;
            float textLeft = EdgePad + avatarD + 14f;
            const float textColW = 156f;

            _avatarGlow = GlowImage("AvatarGlow", Root, Clear);
            _avatarGlow.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _avatarGlow.rectTransform.sizeDelta = new Vector2(avatarD + 22f, avatarD + 22f);
            _avatarGlow.rectTransform.anchoredPosition = new Vector2(avatarCx, 0f);

            _glowColor = CrewmateAvatarRenderer.GetStableIdentityPaletteColor(_pc);

            var avatarRt = Rect("Avatar", Root);
            avatarRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            avatarRt.sizeDelta = new Vector2(avatarD, avatarD);
            avatarRt.anchoredPosition = new Vector2(avatarCx, 0f);
            var avatarImg = avatarRt.gameObject.AddComponent<Image>();
            var body = CrewmateAvatarRenderer.GetStableIdentityBodySpriteFor(_pc);
            if (body != null)
            {
                avatarImg.sprite = body;
                avatarImg.preserveAspect = true;
                avatarImg.color = Color.white;
            }
            else
            {
                avatarImg.sprite = Rounded(true);
                avatarImg.type = Image.Type.Sliced;
                avatarImg.color = _glowColor;
            }
            avatarImg.raycastTarget = false;

            var nameTmp = Text("Name", Root, name, 19f, TextPrimary, TextAlignmentOptions.Left, FontStyles.Bold);
            nameTmp.overflowMode = TextOverflowModes.Ellipsis;
            nameTmp.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            nameTmp.rectTransform.sizeDelta = new Vector2(textColW, 30f);
            nameTmp.rectTransform.anchoredPosition = new Vector2(textLeft, 13f);

            _meterW = textColW;
            var meterTrack = Rect("MeterTrack", Root);
            meterTrack.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            meterTrack.sizeDelta = new Vector2(_meterW, 6f);
            meterTrack.anchoredPosition = new Vector2(textLeft, -15f);
            var meterTrackImg = meterTrack.gameObject.AddComponent<Image>();
            meterTrackImg.sprite = Rounded(true);
            meterTrackImg.type = Image.Type.Sliced;
            meterTrackImg.color = TrackBg;
            meterTrackImg.raycastTarget = false;

            _meterFill = Rect("MeterFill", meterTrack);
            _meterFill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _meterFill.sizeDelta = new Vector2(0f, 6f);
            _meterFill.anchoredPosition = Vector2.zero;
            _meterFillImg = _meterFill.gameObject.AddComponent<Image>();
            _meterFillImg.sprite = Rounded(true);
            _meterFillImg.type = Image.Type.Sliced;
            _meterFillImg.color = MeterGreen;
            _meterFillImg.raycastTarget = false;

            const float resetD = 36f;
            const float pillW = 60f;
            const float gap = 14f;
            float sliderLeft = textLeft + textColW + ColGap;
            float resetLeft = PaneW - EdgePad - resetD;
            float pillLeft = resetLeft - gap - pillW;
            float sliderRight = pillLeft - gap;
            _trackW = Mathf.Max(120f, sliderRight - sliderLeft);

            _track = Rect("Track", Root);
            _track.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _track.sizeDelta = new Vector2(_trackW, 9f);
            _track.anchoredPosition = new Vector2(sliderLeft, 0f);
            var trackImg = _track.gameObject.AddComponent<Image>();
            trackImg.sprite = Rounded(true);
            trackImg.type = Image.Type.Sliced;
            trackImg.color = TrackBg;
            trackImg.raycastTarget = false;

            float midT = Mathf.Approximately(_max, _min) ? 0.5f : (1f - _min) / (_max - _min);
            var mid = Rect("Mid", _track);
            mid.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            mid.sizeDelta = new Vector2(2f, 18f);
            mid.anchoredPosition = new Vector2(_trackW * midT, 0f);
            var midImg = mid.gameObject.AddComponent<Image>();
            midImg.sprite = Solid(Color.white);
            midImg.color = AccentSoft;
            midImg.raycastTarget = false;

            _fill = Rect("Fill", _track);
            _fill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _fill.sizeDelta = new Vector2(0f, 9f);
            _fillImg = _fill.gameObject.AddComponent<Image>();
            _fillImg.sprite = Rounded(true);
            _fillImg.type = Image.Type.Sliced;
            _fillImg.color = Accent;
            _fillImg.raycastTarget = false;

            _knob = Rect("Knob", _track);
            _knob.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            _knob.sizeDelta = new Vector2(22f, 22f);
            var knobShadow = GlowImage("KnobShadow", _knob, new Color32(0, 0, 0, 140));
            knobShadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            knobShadow.rectTransform.offsetMin = new Vector2(-5f, -7f);
            knobShadow.rectTransform.offsetMax = new Vector2(5f, 3f);
            var knobImg = Rect("KnobFill", _knob).gameObject.AddComponent<Image>();
            knobImg.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            knobImg.rectTransform.offsetMin = Vector2.zero;
            knobImg.rectTransform.offsetMax = Vector2.zero;
            knobImg.sprite = Rounded(true);
            knobImg.type = Image.Type.Sliced;
            knobImg.color = TextBright;
            knobImg.raycastTarget = false;

            var pill = Rect("Pill", Root);
            pill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            pill.sizeDelta = new Vector2(pillW, 34f);
            pill.anchoredPosition = new Vector2(pillLeft, 0f);
            var pillImg = pill.gameObject.AddComponent<Image>();
            pillImg.sprite = Rounded(true);
            pillImg.type = Image.Type.Sliced;
            pillImg.color = AccentFaint;
            pillImg.raycastTarget = false;
            _value = Text("Value", pill, "", 18f, Accent, TextAlignmentOptions.Center, FontStyles.Bold);
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(4f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-4f, 0f);

            _resetRt = Rect("Reset", Root);
            _resetRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _resetRt.sizeDelta = new Vector2(resetD, resetD);
            _resetRt.anchoredPosition = new Vector2(resetLeft, 0f);
            _resetImg = _resetRt.gameObject.AddComponent<Image>();
            _resetImg.sprite = Rounded();
            _resetImg.type = Image.Type.Sliced;
            _resetImg.color = ControlBg;
            _resetImg.raycastTarget = false;
            var icon = ResetIcon;
            if (icon != null)
            {
                var iconRt = Rect("ResetIcon", _resetRt);
                iconRt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                iconRt.sizeDelta = new Vector2(22f, 22f);
                var iconImg = iconRt.gameObject.AddComponent<Image>();
                iconImg.sprite = icon;
                iconImg.preserveAspect = true;
                iconImg.color = TextPrimary;
                iconImg.raycastTarget = false;
            }

            ApplyVisual();
            return this;
        }

        private static Color FillColor(float v)
        {
            if (v <= 1f) return Color.Lerp(FillLow, Accent, Mathf.Clamp01(v));
            return Color.Lerp(Accent, FillBoost, Mathf.Clamp01(v - 1f));
        }

        private static Color MeterColor(float t) => t < 0.55f
            ? Color.Lerp(MeterGreen, MeterYellow, t / 0.55f)
            : Color.Lerp(MeterYellow, MeterRed, (t - 0.55f) / 0.45f);

        private static string ValueColor(float v)
            => v < 0.005f ? "#8C9CB2" : v > 1.005f ? "#F2AB3D" : "#22D3EE";

        private int _lastPct = int.MinValue;
        private string? _lastValueColor;
        private void ApplyVisual()
        {
            float v = Mathf.Clamp(_get(), _min, _max);
            float t = Mathf.Approximately(_max, _min) ? 0f : (v - _min) / (_max - _min);
            _fill.sizeDelta = new Vector2(_trackW * t, 9f);
            _fillImg.color = FillColor(v);
            _knob.anchoredPosition = new Vector2(_trackW * t, 0f);
            int pct = Mathf.RoundToInt(v * 100f);
            string c = ValueColor(v);
            if (pct != _lastPct || !ReferenceEquals(c, _lastValueColor))
            {
                _lastPct = pct;
                _lastValueColor = c;
                _value.text = $"<color={c}>{pct}%</color>";
            }
        }

        public void SetLevel(float level, bool speaking)
        {
            _meterTarget = Mathf.Sqrt(Mathf.Clamp01(level));
            _meterSpeaking = speaking;
        }

        public bool HasVisibleMeter => _meterSpeaking || _meterTarget > 0.001f || _meterDisplay > 0.001f;

        /// <summary>
        /// Privacy transitions must not use the normal release animation: even a short decaying
        /// meter would identify the speaker that just became concealed or was remapped.
        /// </summary>
        public void ClearLevelImmediately()
        {
            _meterTarget = 0f;
            _meterDisplay = 0f;
            _meterSpeaking = false;
            _meterFill.sizeDelta = new Vector2(0f, 6f);
            _meterFillImg.color = MeterGreen;
            var glow = _glowColor;
            glow.a = 0f;
            _avatarGlow.color = glow;
        }

        public override void OnMouseDown()
        {
            if (Contains(_resetRt)) { _onChange(1f); _onCommit(); ApplyVisual(); return; }
            if (Contains(_track) || Contains(_knob)) { _dragging = true; ApplyFromMouse(); }
        }

        public override void OnMouseDrag()
        {
            if (_dragging) ApplyFromMouse();
        }

        public override void OnMouseUp()
        {
            if (_dragging && _changed) { _onCommit(); _changed = false; }
            _dragging = false;
        }

        private void ApplyFromMouse()
        {
            if (!LocalPoint(_track, out var lp)) return;
            float t = Mathf.Clamp01(lp.x / _trackW);
            float v = (float)Math.Round(_min + t * (_max - _min), 2);
            if (Mathf.Abs(v - _get()) < 0.0001f) return;
            _onChange(v);
            _changed = true;
            ApplyVisual();
        }

        public override void Tick(float dt)
        {
            TickHover();
            if (!_dragging) ApplyVisual();
            _resetImg.color = Lerp(_resetImg.color, Contains(_resetRt) ? ControlHover : ControlBg, 0.25f);

            float shown = _meterTarget >= _meterDisplay
                ? _meterTarget
                : Mathf.Max(_meterTarget, _meterDisplay - MeterRelease * dt);
            _meterDisplay = shown;
            _meterFill.sizeDelta = new Vector2(_meterW * shown, 6f);
            _meterFillImg.color = MeterColor(shown);
            var g = _glowColor; g.a = _meterSpeaking ? 0.30f + 0.45f * shown : 0f;
            _avatarGlow.color = g;
        }
    }
}
