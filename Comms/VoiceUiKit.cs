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
    public static readonly Color32 Accent         = new(106, 203, 236, 255);
    public static readonly Color32 AccentMiddle   = new(106, 127, 236, 255);
    public static readonly Color32 AccentSecondary = new(149, 106, 236, 255);
    public static readonly Color32 AccentSoft     = new(106, 127, 236, 90);
    public static readonly Color32 AccentFaint    = new(106, 127, 236, 28);
    public static readonly Color32 AccentGlow     = new(106, 127, 236, 16);
    public static readonly Color32 PanelOuter     = new(9, 11, 18, 255);
    public static readonly Color32 PanelInner     = new(16, 19, 29, 255);
    public static readonly Color32 PanelTop       = new(20, 22, 33, 255);
    public static readonly Color32 PanelBottom    = new(20, 22, 33, 255);
    public static readonly Color32 PanelShadow    = new(0, 0, 0, 140);
    public static readonly Color32 RailSurface    = new(17, 19, 30, 255);
    public static readonly Color32 HeaderSurface  = new(20, 22, 33, 255);
    public static readonly Color32 HeaderTop      = new(20, 22, 33, 255);
    public static readonly Color32 HeaderBottom   = new(20, 22, 33, 255);
    public static readonly Color32 TopHighlight   = new(106, 127, 236, 30);
    public static readonly Color32 Divider        = new(43, 46, 66, 255);
    public static readonly Color32 TextPrimary    = new(240, 240, 246, 255);
    public static readonly Color32 TextBright     = new(240, 240, 246, 255);
    public static readonly Color32 TextMuted      = new(166, 172, 195, 255);
    public static readonly Color32 TextFaint      = new(129, 138, 167, 255);
    public static readonly Color32 TrackBg        = new(43, 48, 69, 255);
    public static readonly Color32 ControlBg      = new(28, 32, 48, 255);
    public static readonly Color32 ControlHover   = new(42, 48, 69, 255);
    public static readonly Color32 RowHover       = new(106, 127, 236, 9);
    public static readonly Color32 KnobOff        = new(188, 195, 216, 255);
    public static readonly Color32 ToggleOffTrack = new(50, 56, 78, 255);
    public static readonly Color32 CloseHover      = new(230, 88, 96, 255);
    public static readonly Color32 Danger          = new(230, 88, 96, 255);
    public static readonly Color32 DangerDim       = new(70, 34, 38, 255);
    public static readonly Color32 Clear           = new(0, 0, 0, 0);

    private static readonly Color32 LevelMeterGreen  = new(77, 217, 107, 240);
    private static readonly Color32 LevelMeterYellow = new(242, 214, 64, 240);
    private static readonly Color32 LevelMeterRed    = new(245, 64, 56, 240);
    private const float LevelMeterRelease = 1.6f;

    private static readonly Dictionary<uint, Sprite> _solid = new();
    private static Sprite? _rounded;
    private static Sprite? _roundedSoft;
    private static Sprite? _glow;
    private static Sprite? _gradPanel;
    private static Sprite? _gradHeader;
    private static Sprite? _spectrum;
    private static TMP_FontAsset? _font;
    private static GameObject? _canvasRoot;
    private static Canvas? _canvas;
    private static RectTransform? _tooltipRoot;
    private static CanvasGroup? _tooltipGroup;
    private static TextMeshProUGUI? _tooltipText;
    private static bool _tooltipRequested;

    public static readonly Color32 Backdrop = new(0, 0, 0, 150);

    public static bool AnyPanelOpen =>
        VoiceFirstRunSetup.IsOpen || VoiceSettingsPanel.IsOpen ||
        HostSettingsPanel.IsOpen || VoiceVolumeMenu.IsOpen || VoiceLobbyBrowserUi.IsOpen;

    internal static void ClosePersistentPanels(string reason, bool preserveHeldTransmitInputs)
    {
        // These panels live on the persistent overlay canvas, so scene destruction cannot close
        // them for us. A modal that was actually open remains a privacy boundary even during a
        // continuous EndGame/lobby handoff; every panel also releases holds when it opens, and this
        // snapshot keeps the close path independently fail-closed.
        bool releaseHeldTransmitInputs =
            global::VoiceChatPlugin.VoiceSceneInputPolicy.ShouldReleaseHeldTransmitInputs(
                preserveHeldTransmitInputs,
                AnyPanelOpen);
        try { VoiceFirstRunSetup.ForceClose(); } catch { }
        try { VoiceSettingsPanel.ForceClose(); } catch { }
        try { HostSettingsPanel.ForceClose(); } catch { }
        try { VoiceVolumeMenu.ForceClose(); } catch { }
        try { VoiceLobbyBrowserUi.ForceClose(); } catch { }
        try
        {
            if (releaseHeldTransmitInputs)
                VoiceChatPatches.ReleaseHeldTransmitInputs();
        }
        catch { }
        try
        {
            VoiceDiagnostics.Log(
                "voice.ui.panels_closed",
                $"reason={reason} transmitHolds={(releaseHeldTransmitInputs ? "released" : "preserved")}");
        }
        catch { }
    }

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
        if (AnyPanelOpen)
        {
            // Modal input capture is authoritative for the whole frame. This also handles a panel
            // opened after KeyboardJoystick.Update, preventing a one-frame PTT/radio leak.
            try { VoiceChatPatches.ReleaseHeldTransmitInputs(); } catch { }
        }
        BeginTooltipFrame();

        try { VoiceOptionsMenuEntry.TickButton(); VoiceHostMenuEntry.TickHostButton(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] TickButton threw: " + e.Message); }
        try { VoiceFirstRunSetup.Tick(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] VoiceFirstRunSetup.Tick threw: " + e.Message); }
        try { VoiceSettingsPanel.Tick(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] VoiceSettingsPanel.Tick threw: " + e.Message); }
        try { HostSettingsPanel.Tick(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] HostSettingsPanel.Tick threw: " + e.Message); }
        try { VoiceVolumeMenu.Tick(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] VoiceVolumeMenu.Tick threw: " + e.Message); }
        try { VoiceLobbyBrowserUi.Update(); } catch (Exception e) { VoiceChatPlugin.VoiceChatPluginMain.Logger.LogWarning("[PC-UI] VoiceLobbyBrowserUi.Update threw: " + e.Message); }
        global::VoiceChatPlugin.PerfectCommsUpdateNotifier.TickUi();
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
        var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect);
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

    public static Sprite Spectrum()
    {
        if (_spectrum != null) return _spectrum;
        const int width = 128;
        const int height = 32;
        const int radius = height / 2;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
        };
        for (int x = 0; x < width; x++)
        {
            float t = x / (float)(width - 1);
            Color color = t < 0.48f
                ? Color.Lerp(Accent, AccentMiddle, t / 0.48f)
                : Color.Lerp(AccentMiddle, AccentSecondary, (t - 0.48f) / 0.52f);
            for (int y = 0; y < height; y++)
            {
                float centerX = Mathf.Clamp(x, radius, width - radius - 1);
                float centerY = (height - 1) * 0.5f;
                float dx = x - centerX;
                float dy = y - centerY;
                color.a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy));
                texture.SetPixel(x, y, color);
            }
        }
        texture.Apply(false, true);
        _spectrum = Sprite.Create(texture, new Rect(0f, 0f, width, height),
            new Vector2(0.5f, 0.5f), height, 0, SpriteMeshType.FullRect,
            new Vector4(radius, radius, radius, radius));
        _spectrum.hideFlags = HideFlags.HideAndDontSave;
        return _spectrum;
    }

    public static Image SpectrumImage(string name, Transform parent)
    {
        var image = Rect(name, parent).gameObject.AddComponent<Image>();
        image.sprite = Spectrum();
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        image.raycastTarget = false;
        return image;
    }

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
        if (_font != null) return VoiceUiBranding.Font(_font);
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
        if (_font == null) _font = TMP_Settings.defaultFontAsset;
        return VoiceUiBranding.Font(_font);
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

        var label = Text("SectionLabel", rt, content, 13f, TextMuted, TextAlignmentOptions.Left, FontStyles.Bold);
        label.characterSpacing = 1.5f;
        label.rectTransform.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0.5f));
        label.rectTransform.offsetMin = Vector2.zero;
        label.rectTransform.offsetMax = new Vector2(0f, -2f);
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

    public enum ButtonKind
    {
        Primary,
        Secondary,
        Ghost,
    }

    public sealed class ActionButton
    {
        private readonly RectTransform _root;
        private readonly Image _surface;
        private readonly Image? _spectrum;
        private readonly TextMeshProUGUI _label;
        private readonly ButtonKind _kind;
        private readonly Action _click;
        private readonly Func<bool> _enabled;
        private readonly RectTransform? _clip;

        public RectTransform Root => _root;

        public ActionButton(RectTransform parent, string text, float width, float height,
            float x, float y, ButtonKind kind, Action click, Func<bool>? enabled = null)
        {
            _kind = kind;
            _click = click;
            _enabled = enabled ?? (static () => true);
            _root = Rect("ActionButton", parent);
            _root.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
            _root.sizeDelta = new Vector2(width, height);
            _root.anchoredPosition = new Vector2(x, y);
            for (Transform? ancestor = parent; ancestor != null; ancestor = ancestor.parent)
            {
                if (ancestor.GetComponent<RectMask2D>() == null) continue;
                _clip = ancestor.GetComponent<RectTransform>();
                break;
            }
            _surface = _root.gameObject.AddComponent<Image>();
            _surface.sprite = Rounded(true);
            _surface.type = Image.Type.Sliced;
            _surface.color = RestColor();
            _surface.raycastTarget = false;
            if (kind == ButtonKind.Primary)
            {
                _root.gameObject.AddComponent<Mask>().showMaskGraphic = true;
                _spectrum = SpectrumImage("ButtonSpectrum", _root);
                _spectrum.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                _spectrum.rectTransform.offsetMin = Vector2.zero;
                _spectrum.rectTransform.offsetMax = Vector2.zero;
            }
            _label = Text("ButtonLabel", _root, text, 18f, LabelColor(),
                TextAlignmentOptions.Center, FontStyles.Bold);
            _label.characterSpacing = 0f;
            _label.richText = false;
            _label.enableAutoSizing = true;
            _label.fontSizeMax = 18f;
            _label.fontSizeMin = 15f;
            _label.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _label.rectTransform.offsetMin = new Vector2(8f, 0f);
            _label.rectTransform.offsetMax = new Vector2(-8f, 0f);
        }

        public void SetText(string text)
        {
            if (_label.text != text) _label.text = text;
        }

        public bool IsPointerOver => _root != null && _root.gameObject.activeInHierarchy
            && _enabled() && (_clip == null || Contains(_clip)) && Contains(_root);

        public void Tick(float dt, bool allowInput = true)
        {
            if (_root == null || !_root.gameObject.activeInHierarchy) return;
            bool enabled = _enabled();
            bool over = enabled && (_clip == null || Contains(_clip)) && Contains(_root);
            bool down = over && Input.GetMouseButton(0);
            Color32 target = !enabled ? ToggleOffTrack : over ? HoverColor() : RestColor();
            _surface.color = Lerp(_surface.color, target, Mathf.Clamp01(dt * 15f));
            if (_spectrum != null)
                _spectrum.color = Lerp(_spectrum.color,
                    !enabled ? Clear : over ? new Color32(224, 232, 255, 255) : new Color32(255, 255, 255, 255),
                    Mathf.Clamp01(dt * 14f));
            _label.color = Lerp(_label.color,
                enabled ? LabelColor() : TextFaint, Mathf.Clamp01(dt * 14f));
            float targetScale = down ? 0.98f : over ? 1.015f : 1f;
            float scale = Mathf.Lerp(_root.localScale.x, targetScale, Mathf.Clamp01(dt * 18f));
            _root.localScale = new Vector3(scale, scale, 1f);
            if (allowInput && over && Input.GetMouseButtonDown(0))
            {
                SwallowClick();
                _click();
            }
        }

        private Color32 RestColor() => _kind == ButtonKind.Ghost ? Clear : ControlBg;
        private Color32 HoverColor() => _kind == ButtonKind.Ghost ? AccentFaint : ControlHover;
        private Color32 LabelColor() => _kind == ButtonKind.Primary ? PanelOuter : TextPrimary;
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
        private readonly bool _guided;
        private readonly bool _clampToCanvas;
        private readonly Color32 _closeRestColor;
        private readonly TextMeshProUGUI? _pageTitle;
        private readonly TextMeshProUGUI? _pageSubtitle;
#if WINDOWS
        private readonly TextMeshProUGUI? _menuKey;
        private readonly TextMeshProUGUI? _pushToTalkKey;
        private readonly TextMeshProUGUI? _muteKey;
#endif
        private bool _dragging;
        private Vector2 _dragOffset;
        private Vector2 _userPosition;
        private Vector2 _layoutOffset;

        public PanelShell(
            string objName,
            string title,
            float w,
            float h,
            Action onClose,
            bool rail = true,
            bool backdrop = true,
            bool guided = false,
            bool personalSettings = false,
            bool hostSettings = false,
            string? subtitle = null)
        {
            Width = w; Height = h;
            _onClose = onClose;
            _guided = guided;
            _clampToCanvas = hostSettings || subtitle != null;
            bool settingsStyle = personalSettings || hostSettings;
            bool branded = settingsStyle || guided || subtitle != null;
            _closeRestColor = ControlBg;

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

            var shadow = GlowImage("DropShadow", RootRect,
                guided ? new Color32(0, 0, 0, 112) : PanelShadow);
            shadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            shadow.rectTransform.offsetMin = guided
                ? new Vector2(-26f, -30f)
                : new Vector2(-46f, -54f);
            shadow.rectTransform.offsetMax = guided
                ? new Vector2(26f, 22f)
                : new Vector2(46f, 38f);


            var frame = Panel("Frame", RootRect, Divider);
            frame.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            frame.rectTransform.offsetMin = Vector2.zero;
            frame.rectTransform.offsetMax = Vector2.zero;

            var surface = Rect("Surface", RootRect);
            surface.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            surface.offsetMin = Vector2.one;
            surface.offsetMax = -Vector2.one;
            var surfaceImg = surface.gameObject.AddComponent<Image>();
            surfaceImg.sprite = PanelGradient();
            surfaceImg.type = Image.Type.Sliced;
            surfaceImg.color = Color.white;
            surfaceImg.raycastTarget = false;

            float headerH = branded ? 98f : 76f;
            float footerH = settingsStyle ? 48f : 0f;
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

            if (branded)
            {
                var logo = Rect("Logo", HeaderRect).gameObject.AddComponent<Image>();
                logo.sprite = VoiceUiBranding.Logo();
                logo.preserveAspect = true;
                logo.raycastTarget = false;
                logo.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                logo.rectTransform.sizeDelta = new Vector2(52f, 64f);
                logo.rectTransform.anchoredPosition = new Vector2(28f, 0f);

                var brand = Text("Title", HeaderRect, "Perfect<font-weight=400>Comms</font-weight>", 27f,
                    TextBright, TextAlignmentOptions.Left, FontStyles.Bold);
                brand.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                brand.rectTransform.sizeDelta = new Vector2(320f, 38f);
                brand.rectTransform.anchoredPosition = new Vector2(94f, 11f);
                var subtitleText = Text("Subtitle", HeaderRect,
                    subtitle ?? (guided ? "FIRST-TIME SETUP" : hostSettings ? "HOST VOICE SETTINGS" : "VOICE SETTINGS"),
                    11f, TextMuted, TextAlignmentOptions.Left);
                subtitleText.characterSpacing = 4f;
                subtitleText.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                subtitleText.rectTransform.sizeDelta = new Vector2(320f, 22f);
                subtitleText.rectTransform.anchoredPosition = new Vector2(95f, -21f);

                var spectrum = SpectrumImage("Spectrum", HeaderRect);
                spectrum.rectTransform.Anchor(new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f));
                spectrum.rectTransform.sizeDelta = new Vector2(-48f, 2f);
                spectrum.rectTransform.anchoredPosition = Vector2.zero;
                if (hostSettings)
                {
                    var scope = Panel("LobbyScope", HeaderRect, ControlBg);
                    scope.rectTransform.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
                    scope.rectTransform.sizeDelta = new Vector2(226f, 36f);
                    scope.rectTransform.anchoredPosition = new Vector2(-94f, 0f);
                    var scopeText = Text("Scope", scope.transform, "APPLIES TO EVERYONE", 12f,
                        TextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
                    scopeText.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                    scopeText.rectTransform.offsetMin = Vector2.zero;
                    scopeText.rectTransform.offsetMax = Vector2.zero;
                }
#if WINDOWS
                if (personalSettings)
                {
                    _menuKey = Text("MenuShortcut", HeaderRect, "", 13f, TextMuted, TextAlignmentOptions.Right);
                    _menuKey.rectTransform.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
                    _menuKey.rectTransform.sizeDelta = new Vector2(300f, 30f);
                    _menuKey.rectTransform.anchoredPosition = new Vector2(-92f, 0f);
                }
#endif
            }
            else
            {
                var titleTmp = Text("Title", HeaderRect, title, 28f, TextBright,
                    TextAlignmentOptions.Left, FontStyles.Bold);
                titleTmp.overflowMode = TextOverflowModes.Ellipsis;
                titleTmp.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0.5f));
                titleTmp.rectTransform.sizeDelta = new Vector2(-200f, headerH);
                titleTmp.rectTransform.anchoredPosition = new Vector2(38f, 0f);
            }

            _closeRt = Rect("Close", HeaderRect);
            _closeRt.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _closeRt.sizeDelta = new Vector2(46f, 46f);
            _closeRt.anchoredPosition = new Vector2(-24f, 0f);
            _closeImg = _closeRt.gameObject.AddComponent<Image>();
            _closeImg.sprite = Rounded();
            _closeImg.type = Image.Type.Sliced;
            _closeImg.color = _closeRestColor;
            _closeImg.raycastTarget = false;
            _closeBar1 = CloseBar(_closeRt, 45f);
            _closeBar2 = CloseBar(_closeRt, -45f);

            float pad = settingsStyle ? 32f : 20f;
            float bottomPad = settingsStyle ? 8f : pad;
            float clipPad = settingsStyle ? 0f : 8f;
            float pageHeadingH = settingsStyle ? 92f : 0f;
            RailWidth = rail ? (hostSettings ? 248f : personalSettings ? 226f : Mathf.Round(w * 0.25f)) : 0f;

            RailRoot = Rect("Rail", RootRect);
            RailRoot.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
            RailRoot.offsetMin = new Vector2(0f, footerH);
            RailRoot.offsetMax = new Vector2(RailWidth, -headerH);
            if (rail)
            {
                var railBg = RailRoot.gameObject.AddComponent<Image>();
                railBg.sprite = Solid(Color.white);
                railBg.type = Image.Type.Simple;
                railBg.color = RailSurface;
                railBg.raycastTarget = false;

                var railDiv = Rect("RailDivider", RootRect);
                railDiv.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
                railDiv.offsetMin = new Vector2(RailWidth, footerH);
                railDiv.offsetMax = new Vector2(RailWidth + 1.5f, -headerH);
                var railDivImg = railDiv.gameObject.AddComponent<Image>();
                railDivImg.sprite = Solid(Color.white);
                railDivImg.color = Divider;
                railDivImg.raycastTarget = false;
            }

            PaneWidth = w - RailWidth - pad * 2f;
            PaneHeight = h - headerH - footerH - pageHeadingH - pad - bottomPad;

            if (!branded)
            {
                var inner = Panel("InnerPane", RootRect, PanelInner, true);
                inner.color = new Color32(0, 0, 0, 70);
                inner.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                inner.rectTransform.offsetMin = new Vector2(RailWidth + pad, pad);
                inner.rectTransform.offsetMax = new Vector2(-pad, -headerH - pad);
            }
            else if (settingsStyle)
            {
                _pageTitle = Text("PageTitle", RootRect, "", 31f, TextBright, TextAlignmentOptions.Left, FontStyles.Bold);
                _pageTitle.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                _pageTitle.rectTransform.sizeDelta = new Vector2(PaneWidth, 42f);
                _pageTitle.rectTransform.anchoredPosition = new Vector2(RailWidth + pad, -headerH - 22f);
                _pageSubtitle = Text("PageSubtitle", RootRect, "", 14f, TextMuted, TextAlignmentOptions.Left);
                _pageSubtitle.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                _pageSubtitle.rectTransform.sizeDelta = new Vector2(PaneWidth, 26f);
                _pageSubtitle.rectTransform.anchoredPosition = new Vector2(RailWidth + pad, -headerH - 66f);

                var railHeading = Text("RailHeading", hostSettings ? RootRect : RailRoot,
                    hostSettings ? "LOBBY RULES" : "YOUR SETTINGS", 11f, TextFaint, TextAlignmentOptions.Left);
                railHeading.characterSpacing = 3f;
                railHeading.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                railHeading.rectTransform.sizeDelta = new Vector2(RailWidth - 40f, 24f);
                railHeading.rectTransform.anchoredPosition = new Vector2(30f, hostSettings ? -headerH - 24f : -24f);
#if WINDOWS
                if (personalSettings)
                {
                    _pushToTalkKey = Shortcut(RailRoot, "Push to talk", 76f);
                    _muteKey = Shortcut(RailRoot, "Mute mic", 42f);
                }
#endif
                var footer = Panel("Footer", RootRect, RailSurface, false);
                footer.rectTransform.Anchor(Vector2.zero, new Vector2(1f, 0f), new Vector2(0.5f, 0f));
                footer.rectTransform.sizeDelta = new Vector2(-2f, footerH);
                footer.rectTransform.anchoredPosition = new Vector2(0f, 1f);
                var footerDivider = Panel("Divider", footer.transform, Divider, false);
                footerDivider.rectTransform.Anchor(new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f));
                footerDivider.rectTransform.sizeDelta = new Vector2(0f, 1f);
                var footerBrand = Text("Brand", footer.transform, "Perfect Comms", 12f,
                    TextMuted, TextAlignmentOptions.Left);
                footerBrand.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0f, 0.5f));
                footerBrand.rectTransform.offsetMin = new Vector2(56f, 0f);
                footerBrand.rectTransform.offsetMax = new Vector2(-32f, 0f);
                var footerMark = SpectrumImage("SpectrumMark", footer.transform);
                footerMark.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                footerMark.rectTransform.sizeDelta = new Vector2(22f, 3f);
                footerMark.rectTransform.anchoredPosition = new Vector2(28f, 0f);
                var footerNote = Text("Note", footer.transform,
                    hostSettings ? "Changes apply immediately to the lobby" : "Changes apply immediately", 12f,
                    TextMuted, TextAlignmentOptions.Right);
                footerNote.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(1f, 0.5f));
                footerNote.rectTransform.offsetMin = new Vector2(300f, 0f);
                footerNote.rectTransform.offsetMax = new Vector2(-28f, 0f);
            }

            PaneClip = Rect("PaneClip", RootRect);
            PaneClip.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            PaneClip.offsetMin = new Vector2(RailWidth + pad + clipPad, footerH + bottomPad + clipPad);
            PaneClip.offsetMax = new Vector2(-pad - clipPad, -headerH - pageHeadingH - pad - clipPad);
            PaneClip.gameObject.AddComponent<RectMask2D>();

            PaneRoot = Rect("PaneContent", PaneClip);
            PaneRoot.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            PaneRoot.offsetMin = new Vector2(0f, 0f);
            PaneRoot.offsetMax = new Vector2(0f, 0f);
            PaneRoot.sizeDelta = new Vector2(0f, 0f);
            PaneRoot.anchoredPosition = Vector2.zero;
        }

        private static TextMeshProUGUI Shortcut(Transform rail, string title, float y)
        {
            var label = Text("ShortcutLabel", rail, title, 12f, TextMuted, TextAlignmentOptions.Left);
            label.rectTransform.Anchor(Vector2.zero, Vector2.zero, new Vector2(0f, 0.5f));
            label.rectTransform.sizeDelta = new Vector2(84f, 24f);
            label.rectTransform.anchoredPosition = new Vector2(30f, y);
            var key = Text("ShortcutKey", rail, "", 12f, TextPrimary, TextAlignmentOptions.Right);
            key.enableAutoSizing = true;
            key.fontSizeMin = 9f;
            key.fontSizeMax = 12f;
            key.overflowMode = TextOverflowModes.Ellipsis;
            key.rectTransform.Anchor(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0.5f));
            key.rectTransform.sizeDelta = new Vector2(80f, 24f);
            key.rectTransform.anchoredPosition = new Vector2(-24f, y);
            return key;
        }
        public void SetPage(string title, string subtitle)
        {
            if (_pageTitle == null || _pageSubtitle == null) return;
            _pageTitle.richText = false;
            _pageTitle.enableAutoSizing = true;
            _pageTitle.fontSizeMax = 31f;
            _pageTitle.fontSizeMin = 20f;
            _pageTitle.overflowMode = TextOverflowModes.Ellipsis;
            _pageTitle.text = title;
            _pageSubtitle.richText = false;
            _pageSubtitle.text = subtitle;
        }


        public void SetPage(VoiceSettingsCategory category)
        {
            if (_pageTitle == null || _pageSubtitle == null) return;
            (_pageTitle.text, _pageSubtitle.text) = category switch
            {
                VoiceSettingsCategory.Audio => ("Audio", "Tune your microphone and what you hear."),
                VoiceSettingsCategory.Devices => ("Devices", "Choose your microphone and speakers."),
                VoiceSettingsCategory.Keybinds => ("Keybinds", "Keep your voice controls within reach."),
                VoiceSettingsCategory.Hud => ("HUD", "Make your voice indicators feel at home."),
                VoiceSettingsCategory.Advanced => ("Advanced", "Fine-tune your voice connection and processing."),
                _ => throw new ArgumentOutOfRangeException(nameof(category)),
            };
#if WINDOWS
            if (_menuKey != null) _menuKey.text = VoiceChatKeybinds.OpenVoiceMenu.Label + "   Voice settings";
            if (_pushToTalkKey != null) _pushToTalkKey.text = VoiceChatKeybinds.PushToTalk.Label;
            if (_muteKey != null) _muteKey.text = VoiceChatKeybinds.ToggleMute.Label;
#endif
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
            _closeImg.color = Lerp(_closeImg.color, overClose ? CloseHover : _closeRestColor, 0.3f);
            var barColor = Lerp(_closeBar1.color, overClose ? TextBright : TextMuted, 0.3f);
            _closeBar1.color = barColor;
            _closeBar2.color = barColor;
            _closeScale = Mathf.Lerp(_closeScale, overClose ? 1.14f : 1f, 0.3f);
            _closeRt.localScale = new Vector3(_closeScale, _closeScale, 1f);

            if (Input.GetMouseButtonDown(0))
            {
                if (overClose) { _onClose(); return; }
                if (!_guided && Contains(HeaderRect) && LocalPoint(CanvasRect, out var lp))
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

        public void CancelDrag() => _dragging = false;

        public void SetLayoutOffset(Vector2 offset)
        {
            _layoutOffset = offset;
            ApplyRootPosition();
        }

        private void ApplyRootPosition()
        {
            if (RootRect != null)
            {
                if (_clampToCanvas)
                {
                    Rect canvas = CanvasRect.rect;
                    float limitX = Mathf.Max(0f, (canvas.width - Width * RootRect.localScale.x) * 0.5f - 24f);
                    float limitY = Mathf.Max(0f, (canvas.height - Height * RootRect.localScale.y) * 0.5f - 24f);
                    _userPosition.x = Mathf.Clamp(_userPosition.x, -limitX, limitX);
                    _userPosition.y = Mathf.Clamp(_userPosition.y, -limitY, limitY);
                }
                RootRect.anchoredPosition = _userPosition + _layoutOffset;
            }
        }
    }

    public sealed class CategoryRail
    {
        private sealed class Item
        {
            public RectTransform Root = null!;
            public Image Hover = null!;
            public TextMeshProUGUI Label = null!;
            public Image? Icon;
            public float Y;
        }

        private readonly List<Item> _items = new();
        private int _selected;
        public int Selected => _selected;
        public Action<int>? OnSelect;

        private RectTransform _highlight = null!;
        private float _hiY;
        private RectTransform? _scrollView;
        private RectTransform? _scrollContent;
        private RectTransform? _scrollbar;
        private RectTransform? _scrollThumb;
        private Image? _scrollThumbImage;
        private float _contentHeight;
        private float _scroll;
        private bool _scrollbarDragging;
        private float _scrollbarDragOffset;
        private const float RowH = 62f;
        private const float ItemH = RowH - 8f;

        public void Build(RectTransform railRoot, float railWidth, string[] labels,
            (int index, string title)[]? sections = null,
            VoiceSettingsCategory[]? categories = null,
            bool scrollable = false)
        {
            var parent = railRoot;
            if (scrollable)
            {
                _scrollView = Rect("RailClip", railRoot);
                _scrollView.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                _scrollView.offsetMin = new Vector2(0f, 8f);
                _scrollView.offsetMax = new Vector2(0f, -56f);
                _scrollView.gameObject.AddComponent<RectMask2D>();
                _scrollContent = Rect("RailContent", _scrollView);
                _scrollContent.Anchor(new Vector2(0f, 1f), Vector2.one, new Vector2(0.5f, 1f));
                _scrollContent.sizeDelta = Vector2.zero;
                parent = _scrollContent;
            }
            float top = scrollable ? -4f : categories != null ? -64f : -22f;
            const float sectionGap = 30f;

            _highlight = Rect("RailHighlight", parent);
            _highlight.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            _highlight.sizeDelta = new Vector2(-32f, ItemH);
            var hiImg = _highlight.gameObject.AddComponent<Image>();
            hiImg.sprite = Spectrum();
            hiImg.type = Image.Type.Sliced;
            hiImg.color = new Color32(255, 255, 255, 28);
            hiImg.raycastTarget = false;

            var hiBar = Rect("RailBar", _highlight);
            hiBar.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            hiBar.sizeDelta = new Vector2(2f, ItemH - 24f);
            var barImg = hiBar.gameObject.AddComponent<Image>();
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
                        BuildSection(parent, sections[sidx].title, y + ItemH * 0.5f);
                        break;
                    }
                }

                var rt = Rect("Cat_" + labels[i], parent);
                rt.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
                rt.sizeDelta = new Vector2(-32f, ItemH);
                rt.anchoredPosition = new Vector2(0f, y);

                var hover = rt.gameObject.AddComponent<Image>();
                hover.sprite = Rounded();
                hover.type = Image.Type.Sliced;
                hover.color = Clear;
                hover.raycastTarget = false;

                Image? icon = null;
                string caption = labels[i];
                if (categories != null)
                {
                    var category = categories[i];
                    caption = category == VoiceSettingsCategory.Hud ? "HUD" : category.ToString();
                    icon = Rect("Icon", rt).gameObject.AddComponent<Image>();
                    icon.sprite = VoiceUiBranding.CategoryIcon(category);
                    icon.color = TextFaint;
                    icon.raycastTarget = false;
                    icon.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                    icon.rectTransform.sizeDelta = new Vector2(20f, 20f);
                    icon.rectTransform.anchoredPosition = new Vector2(16f, 0f);
                }
                var label = Text("Label", rt, caption, categories != null ? 17f : scrollable ? 18f : 19f,
                    TextMuted, TextAlignmentOptions.Left, FontStyles.Bold);
                label.overflowMode = TextOverflowModes.Ellipsis;
                if (scrollable)
                {
                    label.richText = false;
                    label.enableAutoSizing = true;
                    label.fontSizeMax = 18f;
                    label.fontSizeMin = 13f;
                }
                label.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0.5f));
                label.rectTransform.sizeDelta = new Vector2(categories != null ? -66f : -32f, RowH);
                label.rectTransform.anchoredPosition = new Vector2(categories != null ? 49f : 22f, 0f);

                _items.Add(new Item { Root = rt, Hover = hover, Label = label, Icon = icon, Y = y });
                y -= categories != null ? 59f : RowH;
            }

            _hiY = _items.Count > 0 ? _items[_selected].Y : top;
            _highlight.anchoredPosition = new Vector2(0f, _hiY);
            _contentHeight = -y + 4f;
            if (scrollable)
            {
                _scrollbar = Rect("RailScrollbar", _scrollView!);
                _scrollbar.Anchor(new Vector2(1f, 0f), Vector2.one, new Vector2(1f, 0.5f));
                _scrollbar.sizeDelta = new Vector2(18f, -8f);
                _scrollbar.anchoredPosition = new Vector2(0f, 0f);
                var track = Panel("Track", _scrollbar, TrackBg, true, true);
                track.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                track.rectTransform.offsetMin = new Vector2(7f, 0f);
                track.rectTransform.offsetMax = new Vector2(-7f, 0f);
                _scrollThumb = Rect("RailScrollThumb", _scrollbar);
                _scrollThumb.Anchor(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));
                _scrollThumb.sizeDelta = new Vector2(18f, 30f);
                _scrollThumbImage = Panel("Thumb", _scrollThumb, TextMuted, true, true);
                _scrollThumbImage.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                _scrollThumbImage.rectTransform.offsetMin = new Vector2(6f, 0f);
                _scrollThumbImage.rectTransform.offsetMax = new Vector2(-6f, 0f);
                ApplyScroll();
            }
            Apply();
        }

        public void Tick()
        {
            bool scrollbarInput = HandleScroll();
            bool withinClip = _scrollView == null || Contains(_scrollView);
            for (int i = 0; i < _items.Count; i++)
            {
                bool sel = i == _selected;
                var it = _items[i];
                bool over = !scrollbarInput && withinClip && Contains(it.Root);
                bool hover = !sel && over;
                it.Hover.color = Lerp(it.Hover.color, hover ? RowHover : Clear, 0.25f);
                it.Label.color = Lerp(it.Label.color, sel || hover ? TextPrimary : TextMuted, 0.25f);
                if (it.Icon != null)
                    it.Icon.color = Lerp(it.Icon.color, sel ? Accent : TextFaint, 0.25f);
                if (_scrollView != null && over)
                    RequestTooltip(it.Label.text, "Lobby-wide voice rules.", plainTextDescription: true);
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
            if (_items.Count == 0) return;
            int selected = Mathf.Clamp(idx, 0, _items.Count - 1);
            if (selected == _selected) return;
            _selected = selected;
            if (_scrollView != null)
            {
                float top = -_items[_selected].Y;
                float bottom = top + ItemH;
                if (top < _scroll + 4f) _scroll = top - 4f;
                else if (bottom > _scroll + _scrollView.rect.height - 4f)
                    _scroll = bottom - _scrollView.rect.height + 4f;
                ApplyScroll();
            }
            Apply();
            OnSelect?.Invoke(_selected);
        }

        public void CancelDrag()
        {
            _scrollbarDragging = false;
            _scrollbarDragOffset = 0f;
        }

        private float MaxScroll() => _scrollView == null ? 0f
            : VoiceSettingsScrollPolicy.MaxScroll(_contentHeight, _scrollView.rect.height);

        private void ApplyScroll()
        {
            if (_scrollView == null || _scrollContent == null || _scrollbar == null || _scrollThumb == null) return;
            float maxScroll = MaxScroll();
            _scroll = VoiceSettingsScrollPolicy.Clamp(_scroll, maxScroll);
            _scrollContent.anchoredPosition = new Vector2(0f, _scroll);
            bool visible = maxScroll > 0.5f;
            if (_scrollbar.gameObject.activeSelf != visible) _scrollbar.gameObject.SetActive(visible);
            if (!visible)
            {
                _scrollbarDragging = false;
                return;
            }
            float trackHeight = _scrollbar.rect.height;
            float thumbHeight = VoiceSettingsScrollPolicy.ThumbHeight(
                trackHeight, _scrollView.rect.height, _contentHeight, 30f);
            _scrollThumb.sizeDelta = new Vector2(18f, thumbHeight);
            _scrollThumb.anchoredPosition = new Vector2(0f,
                -VoiceSettingsScrollPolicy.ThumbTopFromScroll(_scroll, maxScroll, trackHeight, thumbHeight));
        }

        private void SetScrollFromThumbTop(float top)
        {
            if (_scrollbar == null || _scrollThumb == null) return;
            _scroll = VoiceSettingsScrollPolicy.ScrollFromThumbTop(
                top, MaxScroll(), _scrollbar.rect.height, _scrollThumb.rect.height);
            ApplyScroll();
        }

        private bool HandleScroll()
        {
            if (_scrollView == null || _scrollbar == null || _scrollThumb == null) return false;
            ApplyScroll();
            if (!_scrollbar.gameObject.activeSelf) return false;
            bool overBar = Contains(_scrollbar);
            bool overThumb = Contains(_scrollThumb);
            if (_scrollThumbImage != null)
                _scrollThumbImage.color = Lerp(_scrollThumbImage.color,
                    _scrollbarDragging ? Accent : overBar ? TextPrimary : TextMuted, 0.24f);
            if (_scrollbarDragging)
            {
                if (!Input.GetMouseButton(0)) _scrollbarDragging = false;
                else if (LocalPoint(_scrollbar, out var local))
                    SetScrollFromThumbTop(_scrollbar.rect.yMax - local.y - _scrollbarDragOffset);
                return true;
            }
            if (overBar && Input.GetMouseButtonDown(0))
            {
                if (LocalPoint(_scrollbar, out var local))
                {
                    float top = _scrollbar.rect.yMax - local.y;
                    if (overThumb)
                    {
                        _scrollbarDragging = true;
                        _scrollbarDragOffset = top + _scrollThumb.anchoredPosition.y;
                    }
                    else SetScrollFromThumbTop(top - _scrollThumb.rect.height * 0.5f);
                }
                SwallowClick();
                return true;
            }
            if (Contains(_scrollView) && Mathf.Abs(Input.mouseScrollDelta.y) > 0.01f)
            {
                _scroll -= Input.mouseScrollDelta.y * RowH;
                ApplyScroll();
            }
            return overBar;
        }

        private void Apply()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Label.color = i == _selected ? TextPrimary : TextMuted;
                if (_items[i].Icon != null)
                    _items[i].Icon!.color = i == _selected ? Accent : TextFaint;
            }
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
            label.characterSpacing = 1.5f;
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
        // Never manufacture width past the allocated control column. The previous 120px
        // floor made narrow rows draw underneath their value pill instead of getting smaller.
        protected float ControlColW => Mathf.Max(1f, ControlRight - ControlLeft);

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
            // Rows are usually direct children of PaneRoot, but setup groups them inside cards.
            // Resolve the real RectMask2D ancestor instead of assuming the immediate parent is
            // the clip; a zero-height intermediate page root otherwise disables hover/help hit
            // testing for every nested row.
            _clip = null;
            Transform? ancestor = pane;
            while (ancestor != null)
            {
                if (ancestor is RectTransform rt && rt.GetComponent<RectMask2D>() != null)
                {
                    _clip = rt;
                    break;
                }
                ancestor = ancestor.parent;
            }
            Root = Rect("Row", pane);
            Root.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            Root.sizeDelta = new Vector2(0f, height);
            Root.anchoredPosition = new Vector2(0f, y);

            Hover = Panel("Hover", Root, Clear, true);
            Hover.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            Hover.rectTransform.offsetMin = new Vector2(0f, 3f);
            Hover.rectTransform.offsetMax = new Vector2(0f, -3f);

            var title = Text("RowLabel", Root, label, 22f, TextPrimary, TextAlignmentOptions.Left);
            title.overflowMode = TextOverflowModes.Ellipsis;
            title.enableAutoSizing = true;
            title.fontSizeMax = 22f;
            title.fontSizeMin = 17f;
            title.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            title.rectTransform.sizeDelta = new Vector2(LabelColW, height);
            title.rectTransform.anchoredPosition = new Vector2(EdgePad, 0f);
            Title = title;

            if (!string.IsNullOrWhiteSpace(helpText))
                BuildHelp(helpText);
        }

        public void ShowDescription(string? summary = null, bool expanded = false)
        {
            string? content = summary ?? _helpText;
            if (Height > 100f || string.IsNullOrWhiteSpace(content)) return;
            Title.fontSizeMax = 18f;
            Title.fontSizeMin = 15f;
            Title.fontSize = 18f;
            if (expanded)
            {
                for (int i = 0; i < Root.childCount; i++)
                {
                    var child = Root.GetChild(i).GetComponent<RectTransform>();
                    if (child == Title.rectTransform || (Hover != null && child == Hover.rectTransform)) continue;
                    child.anchoredPosition += new Vector2(0f, 18f);
                }
            }
            Title.rectTransform.sizeDelta = new Vector2(Title.rectTransform.sizeDelta.x, 28f);
            Title.rectTransform.anchoredPosition = new Vector2(EdgePad, expanded ? 18f : 11f);
            var description = Text("Description", Title.rectTransform, content!, 13f, TextMuted, TextAlignmentOptions.Left);
            description.richText = false;
            description.overflowMode = TextOverflowModes.Ellipsis;
            description.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            description.enableWordWrapping = expanded;
            description.rectTransform.sizeDelta = new Vector2(
                expanded ? PaneW - EdgePad * 2f : LabelColW, expanded ? 36f : 23f);
            description.rectTransform.anchoredPosition = new Vector2(0f, expanded ? -42f : -26f);
            if (_helpRt != null)
            {
                float preferredWidth = Title.GetPreferredValues(Title.text).x;
                _helpRt.anchoredPosition = new Vector2(
                    Mathf.Min(preferredWidth, Title.rectTransform.sizeDelta.x) + 19f, 0f);
            }
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

            _helpGlyph = Text("Glyph", badge, "?", 17f, Accent,
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

#if WINDOWS
    public sealed class ObsRecordingStatusRow : Row
    {
        private TextMeshProUGUI _value = null!;
        private TextMeshProUGUI _message = null!;

        public ObsRecordingStatusRow Build(RectTransform pane, float width, float y)
        {
            BuildBase(pane, "OBS Recording Status", width, y, 104f);
            Title.rectTransform.anchoredPosition = new Vector2(EdgePad, 19f);
            Title.rectTransform.sizeDelta = new Vector2(LabelColW, 32f);
            _value = Text("RecordingState", Root, "", 18f, Accent, TextAlignmentOptions.Right);
            _value.richText = false;
            _value.rectTransform.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _value.rectTransform.anchoredPosition = new Vector2(-EdgePad, 19f);
            _value.rectTransform.sizeDelta = new Vector2(width - LabelColW - EdgePad * 2f, 32f);
            _message = Text("RecordingMessage", Root, "", 14f, TextMuted, TextAlignmentOptions.Left);
            _message.richText = false;
            _message.enableWordWrapping = true;
            _message.overflowMode = TextOverflowModes.Ellipsis;
            _message.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _message.rectTransform.anchoredPosition = new Vector2(EdgePad, -21f);
            _message.rectTransform.sizeDelta = new Vector2(width - EdgePad * 2f, 44f);
            Tick(0f);
            return this;
        }

        public override void Tick(float dt)
        {
            var status = VoiceObsRecording.Status;
            _value.text = status.Label;
            _value.color = status.State == "error" ? Danger : Accent;
            _message.text = status.Message;
        }
    }
#endif

    public sealed class ActionRow : Row
    {
        private const float ButtonWidth = 220f;
        private readonly Action _onClick;
        private readonly Func<bool> _enabled;
        private RectTransform _buttonRt = null!;
        private Image _button = null!;
        private Image _buttonGlow = null!;
        private TextMeshProUGUI _buttonLabel = null!;

        public ActionRow(Action onClick, Func<bool>? enabled = null)
        {
            _onClick = onClick ?? throw new ArgumentNullException(nameof(onClick));
            _enabled = enabled ?? (() => true);
        }

        protected override float LabelColW =>
            Mathf.Round(PaneW - EdgePad * 2f - ColGap - ButtonWidth);

        public ActionRow Build(
            RectTransform pane,
            string label,
            string buttonText,
            float width,
            float y,
            float height,
            string? helpText = null)
        {
            BuildBase(pane, label, width, y, height, helpText);

            _buttonRt = Rect("ActionButton", Root);
            _buttonRt.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            _buttonRt.sizeDelta = new Vector2(ButtonWidth, 40f);
            _buttonRt.anchoredPosition = new Vector2(-EdgePad, 0f);

            _buttonGlow = GlowImage("ActionButtonGlow", _buttonRt, Clear);
            _buttonGlow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _buttonGlow.rectTransform.offsetMin = new Vector2(-10f, -10f);
            _buttonGlow.rectTransform.offsetMax = new Vector2(10f, 10f);

            _button = _buttonRt.gameObject.AddComponent<Image>();
            _button.sprite = Rounded();
            _button.type = Image.Type.Sliced;
            _button.color = ControlBg;
            _button.raycastTarget = false;

            _buttonLabel = Text("ActionButtonLabel", _buttonRt, buttonText, 17f,
                TextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
            _buttonLabel.characterSpacing = 1f;
            _buttonLabel.enableAutoSizing = true;
            _buttonLabel.fontSizeMax = 17f;
            _buttonLabel.fontSizeMin = 12f;
            _buttonLabel.overflowMode = TextOverflowModes.Ellipsis;
            _buttonLabel.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _buttonLabel.rectTransform.offsetMin = new Vector2(10f, 0f);
            _buttonLabel.rectTransform.offsetMax = new Vector2(-10f, 0f);
            return this;
        }

        public override void OnMouseDown()
        {
            if (!_enabled() || !PointerWithinClip || !Contains(_buttonRt)) return;
            RebindRow.CancelCaptureForExternalPointer();
            _onClick();
        }

        public override void Tick(float dt)
        {
            TickHover();
            bool enabled = _enabled();
            bool over = enabled && PointerWithinClip && Contains(_buttonRt);
            bool pressed = over && Input.GetMouseButton(0);

            _button.color = Lerp(
                _button.color,
                !enabled ? ToggleOffTrack : (over ? ControlHover : ControlBg),
                0.25f);
            _buttonLabel.color = Lerp(
                _buttonLabel.color,
                !enabled ? TextFaint : (over ? TextBright : TextPrimary),
                0.25f);
            _buttonGlow.color = Lerp(
                _buttonGlow.color,
                over ? AccentGlow : Clear,
                0.25f);

            float targetScale = pressed ? 0.95f : (over ? 1.03f : 1f);
            float scale = Mathf.Lerp(_buttonRt.localScale.x, targetScale, 0.35f);
            _buttonRt.localScale = new Vector3(scale, scale, 1f);
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
        private bool _stacked;

        public ToggleRow(Func<bool> get, Action<bool> set) { _get = get; _set = set; }

        protected override float LabelColW => _stacked
            ? Mathf.Round(PaneW - 24f)
            : Mathf.Round(PaneW - EdgePad * 2f - ColGap - 66f);

        public ToggleRow Build(
            RectTransform pane,
            string label,
            float width,
            float y,
            float height,
            string? helpText = null,
            bool stacked = false)
        {
            _stacked = stacked;
            BuildBase(pane, label, width, y, height, helpText);
            if (_stacked)
            {
                Title.fontSizeMin = 15f;
                Title.rectTransform.Anchor(
                    new Vector2(0f, 1f),
                    new Vector2(0f, 1f),
                    new Vector2(0f, 1f));
                Title.rectTransform.sizeDelta =
                    new Vector2(Title.rectTransform.sizeDelta.x, 20f);
                Title.rectTransform.anchoredPosition = new Vector2(12f, 0f);
            }

            var trackRt = Rect("Track", Root);
            if (_stacked)
            {
                trackRt.Anchor(
                    new Vector2(0.5f, 0f),
                    new Vector2(0.5f, 0f),
                    new Vector2(0.5f, 0f));
                trackRt.sizeDelta = new Vector2(66f, 34f);
                trackRt.anchoredPosition = Vector2.zero;
            }
            else
            {
                trackRt.Anchor(
                    new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f));
                trackRt.sizeDelta = new Vector2(66f, 34f);
                trackRt.anchoredPosition = new Vector2(ControlLeft, 0f);
            }

            _glow = SpectrumImage("ActiveTrack", trackRt);
            _glow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _glow.rectTransform.offsetMin = Vector2.zero;
            _glow.rectTransform.offsetMax = Vector2.zero;

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
            _track.color = ToggleOffTrack;
            _knobImg.color = Lerp(KnobOff, TextBright, _knobT);
            _glow.color = new Color32(255, 255, 255, (byte)(255f * _knobT));
        }

        public override void OnMouseDown()
        {
            if (Contains(Root) && !HelpHovered)
            {
                RebindRow.CancelCaptureForExternalPointer();
                _set(!_get());
            }
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

    public sealed class MicModeRow : Row
    {
        private readonly Func<VoiceMicMode> _get;
        private readonly Action<VoiceMicMode> _set;
        private readonly Image[] _buttons = new Image[2];

        public MicModeRow(Func<VoiceMicMode> get, Action<VoiceMicMode> set)
        {
            _get = get;
            _set = set;
        }

        public MicModeRow Build(RectTransform pane, float width, float y, float height,
            string helpText)
        {
            BuildBase(pane, "Mic Mode", width, y, height, helpText);
            var frame = Panel("MicMode", Root, PanelOuter);
            frame.rectTransform.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
            frame.rectTransform.sizeDelta = new Vector2(Mathf.Min(300f, PaneW - EdgePad - ControlLeft), 44f);
            frame.rectTransform.anchoredPosition = new Vector2(-EdgePad, 0f);
            for (int i = 0; i < _buttons.Length; i++)
            {
                var button = SpectrumImage(i == 0 ? "OpenMic" : "PushToTalk", frame.transform);
                button.rectTransform.Anchor(new Vector2(i * 0.5f, 0f), new Vector2((i + 1) * 0.5f, 1f), new Vector2(0.5f, 0.5f));
                button.rectTransform.offsetMin = new Vector2(4f, 4f);
                button.rectTransform.offsetMax = new Vector2(-4f, -4f);
                button.color = _get() == (VoiceMicMode)i ? new Color32(255, 255, 255, 38) : Clear;
                var label = Text("Label", button.transform, i == 0 ? "Open Mic" : "Push To Talk",
                    16f, TextPrimary, TextAlignmentOptions.Center);
                label.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
                label.rectTransform.offsetMin = Vector2.zero;
                label.rectTransform.offsetMax = Vector2.zero;
                _buttons[i] = button;
            }
            return this;
        }

        public override void OnMouseDown()
        {
            if (!PointerWithinClip) return;
            for (int i = 0; i < _buttons.Length; i++)
            {
                if (!Contains(_buttons[i].rectTransform)) continue;
                RebindRow.CancelCaptureForExternalPointer();
                _set((VoiceMicMode)i);
                return;
            }
        }

        public override void Tick(float dt)
        {
            TickHover();
            var mode = _get();
            for (int i = 0; i < _buttons.Length; i++)
            {
                bool selected = mode == (VoiceMicMode)i;
                bool hover = PointerWithinClip && Contains(_buttons[i].rectTransform);
                _buttons[i].color = Lerp(_buttons[i].color,
                    selected ? new Color32(255, 255, 255, 38) :
                    hover ? new Color32(255, 255, 255, 14) : Clear, 0.25f);
            }
        }
    }

    public sealed class SliderRow : Row
    {
        private readonly Func<float> _get;
        private readonly Action<float> _set;
        private readonly float _min, _max;
        private readonly Func<float, string> _fmt;
        private readonly bool _stacked;
        private RectTransform _hitBand = null!;
        private RectTransform _track = null!;
        private RectTransform _fill = null!;
        private RectTransform _knob = null!;
        private TextMeshProUGUI _value = null!;
        private bool _dragging;
        public override bool IsDragging => _dragging;

        protected override float LabelColW => _stacked
            ? Mathf.Max(40f, PaneW - EdgePad * 2f - ValueColW - 14f)
            : base.LabelColW;

        public SliderRow(
            Func<float> get,
            Action<float> set,
            float min,
            float max,
            Func<float, string> fmt,
            bool stacked = false)
        {
            _get = get;
            _set = set;
            _min = min;
            _max = max;
            _fmt = fmt;
            _stacked = stacked;
        }

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
            if (_stacked)
            {
                const float topControlHeight = 28f;
                Title.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                Title.rectTransform.sizeDelta = new Vector2(Title.rectTransform.sizeDelta.x, topControlHeight);
                Title.rectTransform.anchoredPosition = new Vector2(EdgePad, -1f);
                pill.Anchor(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f));
                pill.sizeDelta = new Vector2(ValueColW, topControlHeight);
                pill.anchoredPosition = new Vector2(-EdgePad, -1f);
            }
            else
            {
                pill.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));
                pill.sizeDelta = new Vector2(ValueColW, 36f);
                pill.anchoredPosition = new Vector2(-EdgePad, 0f);
            }
            var pillImg = pill.gameObject.AddComponent<Image>();
            pillImg.sprite = Rounded();
            pillImg.type = Image.Type.Sliced;
            pillImg.color = ControlBg;
            pillImg.raycastTarget = false;

            _value = Text("Value", pill, "", 17f, TextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
            if (_stacked)
            {
                _value.enableAutoSizing = true;
                _value.fontSizeMin = 13f;
                _value.fontSizeMax = 18f;
                _value.overflowMode = TextOverflowModes.Ellipsis;
            }
            _value.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _value.rectTransform.offsetMin = new Vector2(6f, 0f);
            _value.rectTransform.offsetMax = new Vector2(-6f, 0f);

            _track = Rect("Track", Root);
            _track.Anchor(
                _stacked ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f),
                _stacked ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f));
            float trackW = _stacked ? Mathf.Max(1f, PaneW - EdgePad * 2f) : ControlColW;
            const float trackHeight = 6f;
            _track.sizeDelta = new Vector2(trackW, trackHeight);
            _track.anchoredPosition = _stacked
                ? new Vector2(EdgePad, -Mathf.Max(38f, height - 10f))
                : new Vector2(ControlLeft, 0f);
            var trackImg = _track.gameObject.AddComponent<Image>();
            trackImg.sprite = Rounded(true);
            trackImg.type = Image.Type.Sliced;
            trackImg.color = TrackBg;
            trackImg.raycastTarget = false;

            // Keep the visual track slim, but give it a forgiving pointer/touch target.
            // The old 7px stacked track was needlessly difficult to grab at 720p/768p.
            _hitBand = Rect("TrackHitBand", Root);
            _hitBand.Anchor(
                _stacked ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f),
                _stacked ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f));
            _hitBand.sizeDelta = new Vector2(trackW, _stacked ? 30f : 40f);
            _hitBand.anchoredPosition = _track.anchoredPosition;


            _fill = Rect("Fill", _track);
            _fill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _fill.sizeDelta = new Vector2(0f, trackHeight);
            var fillImg = _fill.gameObject.AddComponent<Image>();
            fillImg.sprite = Spectrum();
            fillImg.type = Image.Type.Sliced;
            fillImg.color = Color.white;
            fillImg.raycastTarget = false;

            _knob = Rect("Knob", _track);
            _knob.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            const float knobSize = 18f;
            _knob.sizeDelta = new Vector2(knobSize, knobSize);
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
            _fill.sizeDelta = new Vector2(w * t, 6f);
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
            if (Contains(_hitBand) || Contains(_knob))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _dragging = true;
                ApplyFromMouse();
            }
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
        private readonly bool _compactFullWidth;
        private Image _left = null!;
        private Image _right = null!;
        private TextMeshProUGUI _value = null!;
        private RectTransform _valuePill = null!;

        public StepperRow(
            Func<int> getIndex,
            Action<int> setIndex,
            Func<int> count,
            Func<int, string> labelOf,
            bool fullWidthValue = false,
            bool compactFullWidth = false)
        {
            _getIndex = getIndex;
            _setIndex = setIndex;
            _count = count;
            _labelOf = labelOf;
            _compactFullWidth = compactFullWidth;
            // Compact full-width is a distinct layout, so selecting it also selects the
            // wide value treatment even when the caller omits fullWidthValue: true.
            _fullWidthValue = fullWidthValue || compactFullWidth;
        }

        protected override float LabelColW => _compactFullWidth
            ? Mathf.Max(40f, PaneW - EdgePad * 2f)
            : base.LabelColW;

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
            if (_compactFullWidth)
            {
                const float groupHeight = 44f;
                float groupTop = Mathf.Min(40f, Mathf.Max(36f, height - groupHeight - 2f));
                group.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                group.sizeDelta = new Vector2(groupW, groupHeight);
                group.anchoredPosition = new Vector2(EdgePad, -groupTop);

                Title.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                Title.rectTransform.sizeDelta = new Vector2(Title.rectTransform.sizeDelta.x, 34f);
                Title.rectTransform.anchoredPosition = new Vector2(EdgePad, -2f);
            }
            else
            {
                group.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                group.sizeDelta = new Vector2(groupW, _fullWidthValue ? 76f : 40f);
                group.anchoredPosition = _fullWidthValue
                    ? new Vector2(EdgePad, -height * 0.23f)
                    : new Vector2(ControlLeft, 0f);
            }

            if (_fullWidthValue && !_compactFullWidth)
            {
                Title.rectTransform.sizeDelta = new Vector2(Title.rectTransform.sizeDelta.x, 42f);
                Title.rectTransform.anchoredPosition = new Vector2(EdgePad, height * 0.25f);
            }

            _valuePill = Rect("ValuePill", group);
            _valuePill.Anchor(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f));
            _valuePill.offsetMin = new Vector2(44f, 2f);
            _valuePill.offsetMax = new Vector2(-44f, -2f);
            var valuePillImg = _valuePill.gameObject.AddComponent<Image>();
            valuePillImg.sprite = Rounded();
            valuePillImg.type = Image.Type.Sliced;
            valuePillImg.color = ControlBg;
            valuePillImg.raycastTarget = false;

            float valueSize = _compactFullWidth ? 20f : 18f;
            _value = Text("Value", _valuePill, "", valueSize, TextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
            _value.overflowMode = _fullWidthValue
                ? TextOverflowModes.Overflow
                : TextOverflowModes.Ellipsis;
            _value.enableAutoSizing = _fullWidthValue;
            _value.fontSizeMax = valueSize;
            _value.fontSizeMin = _compactFullWidth ? 16f : 10f;
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
                _value.color = TextPrimary;
            }
            else
            {
                _value.text = "--";
                _value.color = TextFaint;
            }
        }

        public override void OnMouseDown()
        {
            int n = _count();
            if (n <= 0) return;
            int cur = Mathf.Clamp(_getIndex(), 0, n - 1);
            if (Contains(_left.GetComponent<RectTransform>()))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _setIndex((cur - 1 + n) % n);
                Refresh();
            }
            else if (Contains(_right.GetComponent<RectTransform>()))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _setIndex((cur + 1) % n);
                Refresh();
            }
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
                    Title.text, _labelOf(i), plainTextDescription: true);
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
        private readonly Func<bool>? _getAllowWhileChatOpen;
        private readonly Action<bool>? _setAllowWhileChatOpen;
        private readonly bool _showAllowWhileChatOpen;
        private Image _btn = null!;
        private RectTransform _btnRt = null!;
        private TextMeshProUGUI _label = null!;
        private RectTransform? _settingsRt;
        private Image? _settingsBtn;
        private RectTransform? _chatCheckRt;
        private Image? _chatCheckButton;
        private Image? _chatCheckGlow;
        private Image? _chatCheckBox;
        private Image? _chatCheckShort;
        private Image? _chatCheckLong;
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
        private const float ChatCheckW = 40f;
        private const string ChatCheckTooltipTitle = "Use While Chat Is Open";
        private const string ChatCheckTooltip =
            "Works only while Among Us chat is open. It does not bypass tasks/minigames, the Friends List, or modals.";

        public RebindRow(
            Func<KeyCode> get,
            Action<KeyCode, KeyCode, VoiceModifierMatch> set,
            Action clear,
            Func<KeyCode>? getMod = null,
            Func<VoiceModifierMatch>? getModMatch = null,
            Action? configure = null,
            Func<bool>? configureActive = null,
            Func<bool>? getAllowWhileChatOpen = null,
            Action<bool>? setAllowWhileChatOpen = null,
            bool showAllowWhileChatOpen = false)
        {
            _get = get;
            _set = set;
            _clear = clear;
            _getMod = getMod;
            _getModMatch = getModMatch;
            _configure = configure;
            _configureActive = configureActive;
            _getAllowWhileChatOpen = getAllowWhileChatOpen;
            _setAllowWhileChatOpen = setAllowWhileChatOpen;
            _showAllowWhileChatOpen = showAllowWhileChatOpen
                && getAllowWhileChatOpen != null
                && setAllowWhileChatOpen != null;
        }

        protected override float LabelColW => Mathf.Round(
            PaneW - EdgePad * 2f - ColGap - NormalBtnW
            - (_configure == null ? 0f : SettingsW + SettingsGap)
            - (_showAllowWhileChatOpen ? ChatCheckW + SettingsGap : 0f));

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

        public static void CancelCaptureForExternalPointer()
        {
            if (_active == null) return;
            _active.EndCapture();
            ConsumeCaptureInput(KeyCode.Mouse0);
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
            float auxiliaryRight = _normalLeft - SettingsGap;
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
            if (_showAllowWhileChatOpen)
            {
                _chatCheckRt = Rect("AllowWhileChatOpen", Root);
                _chatCheckRt.Anchor(
                    new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f),
                    new Vector2(0f, 0.5f));
                _chatCheckRt.sizeDelta = new Vector2(ChatCheckW, ChatCheckW);
                _chatCheckRt.anchoredPosition =
                    new Vector2(auxiliaryRight - ChatCheckW, 0f);
                _chatCheckButton = _chatCheckRt.gameObject.AddComponent<Image>();
                _chatCheckButton.sprite = Rounded();
                _chatCheckButton.type = Image.Type.Sliced;
                _chatCheckButton.color = ControlBg;
                _chatCheckButton.raycastTarget = false;
                BuildChatCheckmark(_chatCheckRt);
                if (_getAllowWhileChatOpen?.Invoke() == true)
                {
                    _chatCheckButton.color = AccentFaint;
                    _chatCheckGlow!.color = AccentGlow;
                    _chatCheckBox!.color = Accent;
                    _chatCheckShort!.color = PanelInner;
                    _chatCheckLong!.color = PanelInner;
                }
                auxiliaryRight -= ChatCheckW + SettingsGap;
            }

            if (_configure != null)
            {
                _settingsRt = Rect("Configure", Root);
                _settingsRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
                _settingsRt.sizeDelta = new Vector2(SettingsW, SettingsW);
                _settingsRt.anchoredPosition = new Vector2(auxiliaryRight - SettingsW, 0f);
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
                if (_chatCheckRt != null) _chatCheckRt.gameObject.SetActive(false);
                if (Title != null) Title.gameObject.SetActive(false);
            }
            else
            {
                _btnRt.anchoredPosition = new Vector2(_normalLeft, 0f);
                _btnRt.sizeDelta = new Vector2(NormalBtnW, 40f);
                _capRow.gameObject.SetActive(false);
                if (_settingsRt != null) _settingsRt.gameObject.SetActive(true);
                if (_chatCheckRt != null) _chatCheckRt.gameObject.SetActive(true);
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

        private void BuildChatCheckmark(RectTransform parent)
        {
            _chatCheckGlow = GlowImage("ChatCheckGlow", parent, Clear);
            _chatCheckGlow.rectTransform.Anchor(
                Vector2.zero,
                Vector2.one,
                new Vector2(0.5f, 0.5f));
            _chatCheckGlow.rectTransform.offsetMin = new Vector2(-8f, -8f);
            _chatCheckGlow.rectTransform.offsetMax = new Vector2(8f, 8f);

            var boxRt = Rect("Checkbox", parent);
            boxRt.Anchor(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f));
            boxRt.sizeDelta = new Vector2(20f, 20f);
            _chatCheckBox = boxRt.gameObject.AddComponent<Image>();
            _chatCheckBox.sprite = Rounded(true);
            _chatCheckBox.type = Image.Type.Sliced;
            _chatCheckBox.color = ToggleOffTrack;
            _chatCheckBox.raycastTarget = false;

            _chatCheckShort = Panel("CheckShort", boxRt, Clear, true, true);
            _chatCheckShort.rectTransform.Anchor(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f));
            _chatCheckShort.rectTransform.sizeDelta = new Vector2(8f, 3f);
            _chatCheckShort.rectTransform.anchoredPosition = new Vector2(-3f, -1f);
            _chatCheckShort.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -43f);

            _chatCheckLong = Panel("CheckLong", boxRt, Clear, true, true);
            _chatCheckLong.rectTransform.Anchor(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f));
            _chatCheckLong.rectTransform.sizeDelta = new Vector2(13f, 3f);
            _chatCheckLong.rectTransform.anchoredPosition = new Vector2(3f, -2f);
            _chatCheckLong.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 46f);
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
            if (_chatCheckRt != null
                && _chatCheckRt.gameObject.activeInHierarchy
                && PointerWithinClip
                && Contains(_chatCheckRt))
            {
                CancelCaptureForExternalPointer();
                bool allowed = _getAllowWhileChatOpen?.Invoke() == true;
                _setAllowWhileChatOpen?.Invoke(!allowed);
                return;
            }
            if (_configure != null && _settingsRt != null
                && PointerWithinClip && Contains(_settingsRt))
            {
                CancelCaptureForExternalPointer();
                _configure();
                return;
            }
            if (PointerWithinClip && Contains(_btnRt))
            {
                CancelCaptureForExternalPointer();
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
            bool bindingOver = !_capturing && PointerWithinClip && Contains(_btnRt);
            _btn.color = Lerp(
                _btn.color,
                _capturing ? AccentFaint : (bindingOver ? ControlHover : ControlBg),
                0.25f);
            TickChatCheck();
            if (_settingsBtn != null && _settingsRt != null)
            {
                bool settingsOver = !_capturing
                    && PointerWithinClip
                    && Contains(_settingsRt);
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

        private void TickChatCheck()
        {
            if (_chatCheckRt == null
                || _chatCheckButton == null
                || _chatCheckGlow == null
                || _chatCheckBox == null
                || _chatCheckShort == null
                || _chatCheckLong == null
                || !_chatCheckRt.gameObject.activeInHierarchy)
                return;

            bool allowed = _getAllowWhileChatOpen?.Invoke() == true;
            bool over = !_capturing && PointerWithinClip && Contains(_chatCheckRt);
            bool pressed = over && Input.GetMouseButton(0);
            Color buttonTarget = allowed
                ? (over ? AccentSoft : AccentFaint)
                : (over ? ControlHover : ControlBg);

            _chatCheckButton.color = Lerp(_chatCheckButton.color, buttonTarget, 0.25f);
            _chatCheckGlow.color = Lerp(
                _chatCheckGlow.color,
                allowed || over ? AccentGlow : Clear,
                0.25f);
            _chatCheckBox.color = Lerp(
                _chatCheckBox.color,
                allowed ? Accent : ToggleOffTrack,
                0.25f);
            Color checkColor = allowed ? PanelInner : Clear;
            _chatCheckShort.color = Lerp(_chatCheckShort.color, checkColor, 0.25f);
            _chatCheckLong.color = Lerp(_chatCheckLong.color, checkColor, 0.25f);

            float targetScale = pressed ? 0.9f : 1f;
            float scale = Mathf.Lerp(_chatCheckRt.localScale.x, targetScale, 0.35f);
            _chatCheckRt.localScale = new Vector3(scale, scale, 1f);

            if (over)
                RequestTooltip(ChatCheckTooltipTitle, ChatCheckTooltip);
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

    public sealed class LiveLevelMeter
    {
        private readonly RectTransform _root;
        private readonly RectTransform _fill;
        private readonly Image _fillImage;
        private readonly CanvasGroup? _group;
        private readonly float _width;
        private readonly float _height;
        private readonly bool _hideWhenInactive;
        private float _display;
        private float _target;
        private float _visibility;
        private bool _speaking;

        public LiveLevelMeter(
            RectTransform parent,
            string name,
            float width,
            float height = 6f,
            bool hideWhenInactive = false)
        {
            _width = Mathf.Max(1f, width);
            _height = Mathf.Max(2f, height);
            _hideWhenInactive = hideWhenInactive;

            _root = Rect(name, parent);
            _root.sizeDelta = new Vector2(_width, _height);
            var trackImage = _root.gameObject.AddComponent<Image>();
            trackImage.sprite = Rounded(true);
            trackImage.type = Image.Type.Sliced;
            trackImage.color = TrackBg;
            trackImage.raycastTarget = false;

            _fill = Rect("Fill", _root);
            _fill.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _fill.sizeDelta = new Vector2(0f, _height);
            _fill.anchoredPosition = Vector2.zero;
            _fillImage = _fill.gameObject.AddComponent<Image>();
            _fillImage.sprite = Rounded(true);
            _fillImage.type = Image.Type.Sliced;
            _fillImage.color = LevelMeterGreen;
            _fillImage.raycastTarget = false;

            if (_hideWhenInactive)
            {
                _group = _root.gameObject.AddComponent<CanvasGroup>();
                _group.alpha = 0f;
                _root.gameObject.SetActive(false);
            }
        }

        public RectTransform Root => _root;
        public float DisplayLevel => _display;
        public bool IsSpeaking => _speaking;
        public bool HasVisibleSignal => _speaking || _target > 0.001f || _display > 0.001f;

        public void SetLevel(float level, bool speaking)
        {
            _target = Mathf.Sqrt(Mathf.Clamp01(level));
            _speaking = speaking;
        }

        public void ClearImmediately()
        {
            _target = 0f;
            _display = 0f;
            _speaking = false;
            _visibility = 0f;
            _fill.sizeDelta = new Vector2(0f, _height);
            _fillImage.color = LevelMeterGreen;
            if (_group != null) _group.alpha = 0f;
            if (_hideWhenInactive) _root.gameObject.SetActive(false);
        }

        public void Tick(float dt, bool visible = true)
        {
            dt = Mathf.Max(0f, dt);
            if (!visible)
            {
                _target = 0f;
                _speaking = false;
            }
            else if (_hideWhenInactive && !_root.gameObject.activeSelf)
            {
                _root.gameObject.SetActive(true);
            }

            _display = _target >= _display
                ? _target
                : Mathf.Max(_target, _display - LevelMeterRelease * dt);
            _fill.sizeDelta = new Vector2(_width * _display, _height);
            _fillImage.color = LevelMeterColor(_display);

            if (!_hideWhenInactive) return;
            _visibility = Mathf.MoveTowards(_visibility, visible ? 1f : 0f,
                dt * (visible ? 10f : 7f));
            if (_group != null) _group.alpha = _visibility;
            if (!visible && _visibility <= 0.001f && _display <= 0.001f)
                _root.gameObject.SetActive(false);
        }
    }

    private static Color LevelMeterColor(float t) => t < 0.55f
        ? Color.Lerp(LevelMeterGreen, LevelMeterYellow, t / 0.55f)
        : Color.Lerp(LevelMeterYellow, LevelMeterRed, (t - 0.55f) / 0.45f);

    public sealed class PlayerVolumeRow : Row
    {
        private readonly Func<float> _get;
        private readonly Action<float> _onChange;
        private readonly Action _onCommit;
        private readonly PlayerControl? _pc;
        private readonly float _min, _max;

        public byte PlayerId;

        private RectTransform _track = null!;
        private RectTransform _hitBand = null!;
        private RectTransform _fill = null!;
        private Image _fillImg = null!;
        private RectTransform _knob = null!;
        private LiveLevelMeter _levelMeter = null!;
        private Image _avatarGlow = null!;
        private RectTransform _resetRt = null!;
        private Image _resetImg = null!;
        private TextMeshProUGUI _value = null!;
        private Color _glowColor;
        private float _trackW;
        private bool _dragging;
        private bool _changed;
        public override bool IsDragging => _dragging;

        private static readonly Color32 FillBoost   = new(235, 171, 61, 235);

        private static Sprite? _resetIcon;
        private static Sprite ResetIcon =>
            _resetIcon ??= VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.VolumeResetIcon.png", highQuality: true);

        public PlayerVolumeRow(Func<float> get, Action<float> onChange, Action onCommit,
            PlayerControl? pc, float min, float max)
        { _get = get; _onChange = onChange; _onCommit = onCommit; _pc = pc; _min = min; _max = max; }

        public PlayerVolumeRow Build(RectTransform pane, string name, float width, float y, float height)
        {
            BuildBase(pane, name, width, y, height);

            const float avatarD = 44f;
            float avatarCx = EdgePad + avatarD * 0.5f;
            float textLeft = EdgePad + avatarD + 16f;
            float textColW = Mathf.Clamp(Mathf.Round(PaneW * 0.30f), 156f, 252f);

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

            Title.fontSize = 19f;
            Title.fontSizeMax = 19f;
            Title.fontSizeMin = 15f;
            Title.fontStyle = FontStyles.Bold;
            Title.richText = false;
            Title.overflowMode = TextOverflowModes.Ellipsis;
            Title.rectTransform.sizeDelta = new Vector2(textColW, 30f);
            Title.rectTransform.anchoredPosition = new Vector2(textLeft, 13f);

            _levelMeter = new LiveLevelMeter(Root, "MeterTrack", textColW);
            _levelMeter.Root.Anchor(
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _levelMeter.Root.anchoredPosition = new Vector2(textLeft, -15f);

            const float resetD = 44f;
            const float pillW = 76f;
            const float gap = 14f;
            float sliderLeft = textLeft + textColW + ColGap;
            float resetLeft = PaneW - EdgePad - resetD;
            float pillLeft = resetLeft - gap - pillW;
            float sliderRight = pillLeft - gap;
            _trackW = Mathf.Max(1f, sliderRight - sliderLeft);

            _track = Rect("Track", Root);
            _track.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _track.sizeDelta = new Vector2(_trackW, 6f);
            _track.anchoredPosition = new Vector2(sliderLeft, 0f);
            var trackImg = _track.gameObject.AddComponent<Image>();
            trackImg.sprite = Rounded(true);
            trackImg.type = Image.Type.Sliced;
            trackImg.color = TrackBg;
            trackImg.raycastTarget = false;
            _hitBand = Rect("TrackHitBand", Root);
            _hitBand.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            _hitBand.sizeDelta = new Vector2(_trackW, 44f);
            _hitBand.anchoredPosition = _track.anchoredPosition;

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
            _fill.sizeDelta = new Vector2(0f, 6f);
            _fillImg = _fill.gameObject.AddComponent<Image>();
            _fillImg.sprite = Spectrum();
            _fillImg.type = Image.Type.Sliced;
            _fillImg.color = Color.white;
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
            pillImg.color = ControlBg;
            pillImg.raycastTarget = false;
            _value = Text("Value", pill, "", 18f, TextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
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


        private int _lastPct = int.MinValue;
        private void ApplyVisual()
        {
            float v = Mathf.Clamp(_get(), _min, _max);
            float t = Mathf.Approximately(_max, _min) ? 0f : (v - _min) / (_max - _min);
            _fill.sizeDelta = new Vector2(_trackW * t, 6f);
            bool boosted = v > 1.005f;
            var fillSprite = boosted ? Rounded(true) : Spectrum();
            if (_fillImg.sprite != fillSprite) _fillImg.sprite = fillSprite;
            _fillImg.color = boosted ? FillBoost : Color.white;
            _knob.anchoredPosition = new Vector2(_trackW * t, 0f);
            int pct = Mathf.RoundToInt(v * 100f);
            _value.color = v < 0.005f ? TextMuted : boosted ? FillBoost : TextPrimary;
            if (pct != _lastPct)
            {
                _lastPct = pct;
                _value.text = $"{pct}%";
            }
        }

        public void SetLevel(float level, bool speaking)
        {
            _levelMeter.SetLevel(level, speaking);
        }

        public bool HasVisibleMeter => _levelMeter.HasVisibleSignal;

        /// <summary>
        /// Privacy transitions must not use the normal release animation: even a short decaying
        /// meter would identify the speaker that just became concealed or was remapped.
        /// </summary>
        public void ClearLevelImmediately()
        {
            _levelMeter.ClearImmediately();
            var glow = _glowColor;
            glow.a = 0f;
            _avatarGlow.color = glow;
        }

        public override void OnMouseDown()
        {
            if (!PointerWithinClip) return;
            if (Contains(_resetRt))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _onChange(1f);
                _onCommit();
                ApplyVisual();
                return;
            }
            if (Contains(_hitBand) || Contains(_knob))
            {
                RebindRow.CancelCaptureForExternalPointer();
                _dragging = true;
                ApplyFromMouse();
            }
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
            bool overReset = PointerWithinClip && Contains(_resetRt);
            _resetImg.color = Lerp(_resetImg.color, overReset ? ControlHover : ControlBg, 0.25f);
            if (overReset)
                RequestTooltip("Reset volume", "Restore this player's playback volume to 100%.", plainTextDescription: true);

            _levelMeter.Tick(dt);
            float shown = _levelMeter.DisplayLevel;
            var g = _glowColor; g.a = _levelMeter.IsSpeaking ? 0.30f + 0.45f * shown : 0f;
            _avatarGlow.color = g;
        }
    }
}
