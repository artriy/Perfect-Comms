using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class CommsChipButton
{
    private const float Width = 320f;
    private const float Height = 80f;

    public GameObject? Root;
    private RectTransform? _rootRt;
    private Image? _surface;
    private Image? _border;
    private TextMeshProUGUI? _label;

    private Vector2 _position;
    private float _scale = 1f;
    private float _baseScale = 1f;
    private float _appearT = 1f;
    private bool _pressedLast;
    private int _shownFrame;

    private Action _onClick = static () => { };
    private Func<bool> _menuAlive = static () => true;
    private Func<bool> _gate = static () => true;

    public bool Built => Root != null;
    public bool Visible => Root != null && Root.activeInHierarchy;
    public bool CursorOver => Visible && _rootRt != null && _menuAlive() && _gate() &&
        !VoiceUiKit.AnyPanelOpen && VoiceUiKit.Contains(_rootRt);

    public void Build(string eyebrowText, string labelText,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos,
        Action onClick, Func<bool> menuAlive, Func<bool> gate, float baseScale = 1f)
    {
        if (Root != null) return;

        _onClick = onClick;
        _menuAlive = menuAlive;
        _gate = gate;
        _baseScale = baseScale;
        _position = pos;

        var rt = VoiceUiKit.Rect("PerfectComms_Chip", VoiceUiKit.Canvas.transform);
        rt.Anchor(anchorMin, anchorMax, pivot);
        rt.sizeDelta = new Vector2(Width, Height);
        rt.anchoredPosition = pos;
        Root = rt.gameObject;
        _rootRt = rt;

        _border = rt.gameObject.AddComponent<Image>();
        _border.sprite = VoiceUiKit.Rounded();
        _border.type = Image.Type.Sliced;
        _border.Round(VoiceUiKit.RadiusCard);
        _border.color = VoiceUiKit.Divider;
        _border.raycastTarget = false;

        _surface = VoiceUiKit.Panel("Inner", rt, VoiceUiKit.PanelInner).Round(VoiceUiKit.RadiusCard - 1f);
        _surface.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _surface.rectTransform.offsetMin = Vector2.one;
        _surface.rectTransform.offsetMax = -Vector2.one;

        var logo = VoiceUiKit.Rect("Logo", rt).gameObject.AddComponent<Image>();
        logo.sprite = VoiceUiBranding.Logo();
        logo.preserveAspect = true;
        logo.raycastTarget = false;
        logo.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
        logo.rectTransform.sizeDelta = new Vector2(28f, 40f);
        logo.rectTransform.anchoredPosition = new Vector2(16f, 0f);

        _label = VoiceUiKit.Text("Label", rt, labelText, 20f,
            VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold);
        _label.overflowMode = TextOverflowModes.Ellipsis;
        _label.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
        _label.rectTransform.sizeDelta = new Vector2(224f, 28f);
        _label.rectTransform.anchoredPosition = new Vector2(60f, 12f);

        var eyebrow = VoiceUiKit.Text("Eyebrow", rt, eyebrowText, 12f,
            VoiceUiKit.TextMuted, TextAlignmentOptions.Left);
        eyebrow.overflowMode = TextOverflowModes.Ellipsis;
        eyebrow.rectTransform.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
        eyebrow.rectTransform.sizeDelta = new Vector2(224f, 20f);
        eyebrow.rectTransform.anchoredPosition = new Vector2(60f, -16f);

        var chevronTop = VoiceUiKit.Panel("ChevronTop", rt, VoiceUiKit.TextMuted, false);
        chevronTop.rectTransform.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f));
        chevronTop.rectTransform.sizeDelta = new Vector2(8f, 2f);
        chevronTop.rectTransform.anchoredPosition = new Vector2(-21f, 2.5f);
        chevronTop.rectTransform.localEulerAngles = new Vector3(0f, 0f, -45f);
        var chevronBottom = VoiceUiKit.Panel("ChevronBottom", rt, VoiceUiKit.TextMuted, false);
        chevronBottom.rectTransform.Anchor(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f));
        chevronBottom.rectTransform.sizeDelta = new Vector2(8f, 2f);
        chevronBottom.rectTransform.anchoredPosition = new Vector2(-21f, -2.5f);
        chevronBottom.rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);

        ApplyLayout();
        Root.SetActive(false);
    }

    public void ShowWithPop()
    {
        if (Root == null || !_menuAlive() || !_gate()) return;
        _appearT = 0f;
        _scale = 1f;
        _pressedLast = Input.GetMouseButton(0);
        _shownFrame = Time.frameCount;
        ApplyLayout();
        Root.SetActive(true);
    }

    public void Hide()
    {
        _pressedLast = Input.GetMouseButton(0);
        if (Root != null) Root.SetActive(false);
    }

    public void Tick()
    {
        bool held = Input.GetMouseButton(0);
        if (Root == null || !Root.activeInHierarchy)
        {
            _pressedLast = held;
            return;
        }
        if (!_menuAlive() || !_gate())
        {
            Hide();
            return;
        }

        if (_appearT < 1f) _appearT = Mathf.Min(1f, _appearT + Time.deltaTime / 0.16f);
        ApplyLayout();
        bool over = CursorOver;
        bool press = over && held;
        float blend = 1f - Mathf.Exp(-20f * Time.deltaTime);
        if (_label != null)
            _label.color = VoiceUiKit.Lerp(_label.color, over ? VoiceUiKit.Accent : VoiceUiKit.TextBright, blend);
        if (_surface != null)
            _surface.color = VoiceUiKit.Lerp(_surface.color, over ? VoiceUiKit.ControlBg : VoiceUiKit.PanelInner, blend);
        if (_border != null)
            _border.color = VoiceUiKit.Lerp(_border.color, over ? VoiceUiKit.AccentSoft : VoiceUiKit.Divider, blend);
        _scale = Mathf.Lerp(_scale, press ? 0.985f : 1f, blend);

        bool click = Input.GetMouseButtonDown(0) && over && !_pressedLast && Time.frameCount > _shownFrame;
        _pressedLast = held || VoiceUiKit.AnyPanelOpen;
        if (click)
        {
            try { _onClick(); }
            catch (Exception e) { VoiceDiagnostics.DebugWarning($"[PerfectComms] Chip click failed: {e.Message}"); }
        }
    }

    private void ApplyLayout()
    {
        if (_rootRt == null) return;
        Rect canvas = VoiceUiKit.CanvasRect.rect;
        Rect safe = canvas;
        Rect screenSafe = Screen.safeArea;
        if (Screen.width > 0 && Screen.height > 0 && screenSafe.width > 0f && screenSafe.height > 0f)
        {
            safe = Rect.MinMaxRect(
                canvas.xMin + screenSafe.xMin / Screen.width * canvas.width,
                canvas.yMin + screenSafe.yMin / Screen.height * canvas.height,
                canvas.xMin + screenSafe.xMax / Screen.width * canvas.width,
                canvas.yMin + screenSafe.yMax / Screen.height * canvas.height);
        }
        float margin = Mathf.Min(16f, Mathf.Min(safe.width, safe.height) * 0.1f);
        float fit = Mathf.Min(_baseScale,
            Mathf.Min((safe.width - margin * 2f) / Width, (safe.height - margin * 2f) / Height));
        var pivot = _rootRt.pivot;
        float anchorX = canvas.xMin + canvas.width *
            Mathf.Lerp(_rootRt.anchorMin.x, _rootRt.anchorMax.x, pivot.x);
        float anchorY = canvas.yMin + canvas.height *
            Mathf.Lerp(_rootRt.anchorMin.y, _rootRt.anchorMax.y, pivot.y);
        float x = Mathf.Clamp(anchorX + _position.x,
            safe.xMin + margin + Width * fit * pivot.x,
            safe.xMax - margin - Width * fit * (1f - pivot.x));
        float y = Mathf.Clamp(anchorY + _position.y,
            safe.yMin + margin + Height * fit * pivot.y,
            safe.yMax - margin - Height * fit * (1f - pivot.y));
        _rootRt.anchoredPosition = new Vector2(x - anchorX, y - anchorY);
        float appear = 1f - Mathf.Pow(1f - _appearT, 3f);
        float scale = fit * _scale * Mathf.Lerp(0.98f, 1f, appear);
        _rootRt.localScale = new Vector3(scale, scale, 1f);
    }
}
