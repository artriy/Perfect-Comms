using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class CommsChipButton
{
    public GameObject? Root;
    private RectTransform? _rootRt;
    private Image? _underglow;
    private TextMeshProUGUI? _label;

    private float _scale = 1f;
    private float _baseScale = 1f;
    private float _appearT = 1f;
    private bool _pressedLast;

    private Action _onClick = static () => { };
    private Func<bool> _menuAlive = static () => true;
    private Func<bool> _gate = static () => true;

    public bool Built => Root != null;
    public bool Visible => Root != null && Root.activeSelf;

    public void Build(string eyebrowText, string labelText,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos,
        Action onClick, Func<bool> menuAlive, Func<bool> gate, float baseScale = 1f)
    {
        if (Root != null) return;

        _onClick = onClick;
        _menuAlive = menuAlive;
        _gate = gate;
        _baseScale = baseScale;

        var rt = VoiceUiKit.Rect("PerfectComms_Chip", VoiceUiKit.Canvas.transform);
        rt.Anchor(anchorMin, anchorMax, pivot);
        rt.sizeDelta = new Vector2(420f, 92f);
        rt.anchoredPosition = pos;
        Root = rt.gameObject;
        _rootRt = rt;

        var shadow = VoiceUiKit.GlowImage("Shadow", rt, VoiceUiKit.PanelShadow);
        shadow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        shadow.rectTransform.offsetMin = new Vector2(-18f, -22f);
        shadow.rectTransform.offsetMax = new Vector2(18f, 10f);

        var underglowColor = VoiceUiKit.AccentGlow; underglowColor.a = 0;
        _underglow = VoiceUiKit.GlowImage("Underglow", rt, underglowColor);
        _underglow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _underglow.rectTransform.offsetMin = new Vector2(-30f, -30f);
        _underglow.rectTransform.offsetMax = new Vector2(30f, 30f);

        var surface = rt.gameObject.AddComponent<Image>();
        surface.sprite = VoiceUiKit.Rounded(false);
        surface.type = Image.Type.Sliced;
        surface.color = VoiceUiKit.PanelOuter;
        surface.raycastTarget = false;

        var borderRt = VoiceUiKit.Rect("AccentBorder", rt);
        borderRt.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        borderRt.offsetMin = Vector2.zero;
        borderRt.offsetMax = Vector2.zero;
        var border = borderRt.gameObject.AddComponent<Image>();
        border.sprite = VoiceUiKit.Rounded(false);
        border.type = Image.Type.Sliced;
        border.color = VoiceUiKit.Accent;
        border.raycastTarget = false;

        var innerRt = VoiceUiKit.Rect("Inner", rt);
        innerRt.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        innerRt.offsetMin = new Vector2(2.5f, 2.5f);
        innerRt.offsetMax = new Vector2(-2.5f, -2.5f);
        var inner = innerRt.gameObject.AddComponent<Image>();
        inner.sprite = VoiceUiKit.Rounded(false);
        inner.type = Image.Type.Sliced;
        inner.color = VoiceUiKit.PanelOuter;
        inner.raycastTarget = false;

        var eyebrowRt = VoiceUiKit.Rect("Eyebrow", rt);
        eyebrowRt.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        eyebrowRt.offsetMin = new Vector2(34f, 0f);
        eyebrowRt.offsetMax = new Vector2(-20f, 0f);
        eyebrowRt.anchoredPosition = new Vector2(0f, -22f);
        var eyebrow = eyebrowRt.gameObject.AddComponent<TextMeshProUGUI>();
        var font = VoiceUiKit.GameFont();
        if (font != null) eyebrow.font = font;
        eyebrow.text = eyebrowText;
        eyebrow.fontSize = 13f;
        eyebrow.color = VoiceUiKit.TextMuted;
        eyebrow.alignment = TextAlignmentOptions.TopLeft;
        eyebrow.fontStyle = FontStyles.Bold;
        eyebrow.characterSpacing = 5f;
        eyebrow.richText = true;
        eyebrow.enableWordWrapping = false;
        eyebrow.raycastTarget = false;
        eyebrow.overflowMode = TextOverflowModes.Overflow;

        _label = VoiceUiKit.Text("Label", rt, labelText, 24f,
            VoiceUiKit.TextBright, TextAlignmentOptions.Left, FontStyles.Bold);
        _label.characterSpacing = 4f;
        _label.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        _label.rectTransform.offsetMin = new Vector2(34f, 0f);
        _label.rectTransform.offsetMax = new Vector2(-20f, 0f);
        _label.rectTransform.anchoredPosition = new Vector2(0f, -4f);

        Root.SetActive(false);
    }

    public void ShowWithPop()
    {
        if (Root == null) return;
        _appearT = 0f;
        Root.SetActive(true);
    }

    public void Hide()
    {
        if (Root != null) Root.SetActive(false);
    }

    public void Tick()
    {
        if (Root == null || !Root.activeSelf) return;
        if (!_menuAlive() || !_gate())
        {
            Root.SetActive(false);
            return;
        }

        bool over = _rootRt != null && !VoiceUiKit.AnyPanelOpen && VoiceUiKit.Contains(_rootRt);
        bool press = over && Input.GetMouseButton(0);

        if (_label != null)
            _label.color = VoiceUiKit.Lerp(_label.color, over ? VoiceUiKit.Accent : VoiceUiKit.TextBright, 0.25f);
        if (_underglow != null)
        {
            var g = VoiceUiKit.AccentGlow; g.a = (byte)(over ? 150 : 0);
            _underglow.color = VoiceUiKit.Lerp(_underglow.color, g, 0.2f);
        }
        _scale = Mathf.Lerp(_scale, press ? 0.97f : (over ? 1.04f : 1f), 0.25f);
        if (_appearT < 1f) _appearT = Mathf.Min(1f, _appearT + Time.deltaTime / 0.2f);
        float s = _scale * _baseScale * VoiceUiKit.AppearScale(_appearT);
        if (_rootRt != null) _rootRt.localScale = new Vector3(s, s, 1f);

        bool pressed = Input.GetMouseButtonDown(0);
        if (pressed && over && !_pressedLast)
        {
            try { _onClick(); }
            catch (Exception e) { VoiceDiagnostics.DebugWarning($"[PerfectComms] Chip click failed: {e.Message}"); }
        }
        _pressedLast = pressed;
    }
}
