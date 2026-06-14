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
    private Image? _rail;
    private TextMeshProUGUI? _label;

    private float _scale = 1f;
    private float _appearT = 1f;
    private bool _pressedLast;

    private Action _onClick = static () => { };
    private Func<bool> _menuAlive = static () => true;
    private Func<bool> _gate = static () => true;

    public bool Built => Root != null;

    public void Build(string eyebrowText, string labelText,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 pos,
        Action onClick, Func<bool> menuAlive, Func<bool> gate)
    {
        if (Root != null) return;

        _onClick = onClick;
        _menuAlive = menuAlive;
        _gate = gate;

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

        var railRt = VoiceUiKit.Rect("AccentRail", rt);
        railRt.Anchor(new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f));
        railRt.sizeDelta = new Vector2(5f, -22f);
        railRt.anchoredPosition = new Vector2(12f, 0f);
        _rail = railRt.gameObject.AddComponent<Image>();
        _rail.sprite = VoiceUiKit.Rounded(false);
        _rail.type = Image.Type.Sliced;
        _rail.color = VoiceUiKit.Accent;
        _rail.raycastTarget = false;

        var topRt = VoiceUiKit.Rect("TopEdge", rt);
        topRt.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
        topRt.sizeDelta = new Vector2(-26f, 2f);
        topRt.anchoredPosition = new Vector2(0f, -2f);
        var topEdge = topRt.gameObject.AddComponent<Image>();
        topEdge.sprite = VoiceUiKit.Solid(Color.white);
        topEdge.color = VoiceUiKit.Accent;
        topEdge.raycastTarget = false;

        BuildIcon(rt);

        var eyebrowRt = VoiceUiKit.Rect("Eyebrow", rt);
        eyebrowRt.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
        eyebrowRt.offsetMin = new Vector2(104f, 0f);
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
        _label.rectTransform.offsetMin = new Vector2(104f, 0f);
        _label.rectTransform.offsetMax = new Vector2(-20f, 0f);
        _label.rectTransform.anchoredPosition = new Vector2(0f, -4f);

        Root.SetActive(false);
    }

    private static void BuildIcon(RectTransform parent)
    {
        var iconRt = VoiceUiKit.Rect("Icon", parent);
        iconRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
        iconRt.sizeDelta = new Vector2(52f, 52f);
        iconRt.anchoredPosition = new Vector2(40f, 0f);

        var bodyRt = VoiceUiKit.Rect("Body", iconRt);
        bodyRt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        bodyRt.sizeDelta = new Vector2(30f, 40f);
        bodyRt.anchoredPosition = new Vector2(3f, -2f);
        var body = bodyRt.gameObject.AddComponent<Image>();
        body.sprite = VoiceUiKit.Rounded(false);
        body.type = Image.Type.Sliced;
        body.color = VoiceUiKit.Accent;
        body.raycastTarget = false;

        var visorRt = VoiceUiKit.Rect("Visor", iconRt);
        visorRt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        visorRt.sizeDelta = new Vector2(18f, 12f);
        visorRt.anchoredPosition = new Vector2(7f, 4f);
        var visor = visorRt.gameObject.AddComponent<Image>();
        visor.sprite = VoiceUiKit.Rounded(false);
        visor.type = Image.Type.Sliced;
        visor.color = new Color32(150, 170, 195, 255);
        visor.raycastTarget = false;

        var shineRt = VoiceUiKit.Rect("VisorShine", visorRt);
        shineRt.Anchor(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        shineRt.sizeDelta = new Vector2(4f, 4f);
        shineRt.anchoredPosition = new Vector2(3f, 7f);
        var shine = shineRt.gameObject.AddComponent<Image>();
        shine.sprite = VoiceUiKit.Solid(Color.white);
        var shineColor = VoiceUiKit.TextBright; shineColor.a = 180;
        shine.color = shineColor;
        shine.raycastTarget = false;
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
        if (_rail != null)
        {
            var railRt = _rail.rectTransform;
            float w = Mathf.Lerp(railRt.offsetMax.x, over ? 6f : 5f, 0.2f);
            railRt.offsetMax = new Vector2(w, railRt.offsetMax.y);
        }

        _scale = Mathf.Lerp(_scale, press ? 0.97f : (over ? 1.04f : 1f), 0.25f);
        if (_appearT < 1f) _appearT = Mathf.Min(1f, _appearT + Time.deltaTime / 0.2f);
        float s = _scale * VoiceUiKit.AppearScale(_appearT);
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
