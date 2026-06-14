using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceOptionsMenuEntry
{
    private static OptionsMenuBehaviour? _menu;
    private static GameObject? _button;
    private static RectTransform? _buttonRt;
    private static Image? _glow;
    private static TextMeshProUGUI? _label;
    private static bool _pressedLast;
    private static float _scale = 1f;
    private static float _appearT = 1f;

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
    [HarmonyPostfix]
    static void PerfectComms_OnOptionsOpen(OptionsMenuBehaviour __instance)
    {
        _menu = __instance;
        EnsureButton();
        if (_button != null && !_button.activeSelf) { _appearT = 0f; _button.SetActive(true); }
    }

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockOptionsClose() => !VoiceUiKit.AnyPanelOpen;

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close))]
    [HarmonyPostfix]
    static void PerfectComms_OnOptionsClose()
    {
        _menu = null;
        if (_button != null) _button.SetActive(false);
        try { if (!HudManager.InstanceExists) VoiceSettingsPanel.ForceClose(); }
        catch { }
    }

    public static void NotifyOptionsActive(OptionsMenuBehaviour menu)
    {
        _menu = menu;
        EnsureButton();
        if (_button != null && !_button.activeSelf && !VoiceUiKit.AnyPanelOpen
            && menu != null && menu.gameObject.activeInHierarchy)
        { _appearT = 0f; _button.SetActive(true); }
    }

    private static void EnsureButton()
    {
        if (_button != null) return;
        try
        {
            var rt = VoiceUiKit.Rect("PerfectComms_OptionsButton", VoiceUiKit.Canvas.transform);
            rt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f));
            rt.sizeDelta = new Vector2(420f, 92f);
            rt.anchoredPosition = new Vector2(224f, 0f);
            _button = rt.gameObject;
            _buttonRt = rt;

            _glow = VoiceUiKit.GlowImage("Glow", rt, VoiceUiKit.AccentGlow);
            _glow.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _glow.rectTransform.offsetMin = new Vector2(-26f, -26f);
            _glow.rectTransform.offsetMax = new Vector2(26f, 26f);

            var borderImg = rt.gameObject.AddComponent<Image>();
            borderImg.sprite = VoiceUiKit.Rounded(true);
            borderImg.type = Image.Type.Sliced;
            borderImg.color = VoiceUiKit.Accent;
            borderImg.raycastTarget = false;

            var fillRt = VoiceUiKit.Rect("Fill", rt);
            fillRt.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            fillRt.offsetMin = new Vector2(3f, 3f);
            fillRt.offsetMax = new Vector2(-3f, -3f);
            var fill = fillRt.gameObject.AddComponent<Image>();
            fill.sprite = VoiceUiKit.HeaderGradient();
            fill.type = Image.Type.Sliced;
            fill.color = Color.white;
            fill.raycastTarget = false;

            var iconRt = VoiceUiKit.Rect("Icon", rt);
            iconRt.Anchor(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));
            iconRt.sizeDelta = new Vector2(52f, 52f);
            iconRt.anchoredPosition = new Vector2(46f, 0f);
            var icon = iconRt.gameObject.AddComponent<Image>();
            icon.sprite = VoiceUiKit.Rounded(true);
            icon.type = Image.Type.Sliced;
            icon.color = VoiceUiKit.Accent;
            icon.raycastTarget = false;
            var iconGlyph = VoiceUiKit.Text("Glyph", iconRt, "●", 30f,
                VoiceUiKit.PanelBottom, TextAlignmentOptions.Center, FontStyles.Bold);
            iconGlyph.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            iconGlyph.rectTransform.offsetMin = Vector2.zero;
            iconGlyph.rectTransform.offsetMax = Vector2.zero;

            _label = VoiceUiKit.Text("Label", rt, "PERFECT COMMS", 26f,
                VoiceUiKit.TextBright, TextAlignmentOptions.Center, FontStyles.Bold);
            _label.characterSpacing = 6f;
            _label.rectTransform.Anchor(Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f));
            _label.rectTransform.offsetMin = new Vector2(86f, 0f);
            _label.rectTransform.offsetMax = new Vector2(-18f, 0f);

            _button.SetActive(false);
        }
        catch (Exception e)
        {
            VoiceDiagnostics.DebugWarning($"[PerfectComms] Failed to build Options button: {e.Message}");
        }
    }

    public static void TickButton()
    {
        if (_button == null || !_button.activeSelf) return;
        if (_menu == null || _menu.gameObject == null || !_menu.gameObject.activeInHierarchy)
        {
            _button.SetActive(false);
            _menu = null;
            return;
        }

        bool over = _buttonRt != null && !VoiceUiKit.AnyPanelOpen && VoiceUiKit.Contains(_buttonRt);
        bool press = over && Input.GetMouseButton(0);
        if (_label != null)
            _label.color = VoiceUiKit.Lerp(_label.color, over ? VoiceUiKit.Accent : VoiceUiKit.TextBright, 0.25f);
        if (_glow != null)
        {
            var g = VoiceUiKit.AccentGlow; g.a = (byte)(over ? 150 : 70);
            _glow.color = VoiceUiKit.Lerp(_glow.color, g, 0.2f);
        }
        _scale = Mathf.Lerp(_scale, press ? 0.97f : (over ? 1.04f : 1f), 0.25f);
        if (_appearT < 1f) _appearT = Mathf.Min(1f, _appearT + Time.deltaTime / 0.2f);
        float appear = AppearScale(_appearT);
        float s = _scale * appear;
        _button.transform.localScale = new Vector3(s, s, 1f);

        bool pressed = Input.GetMouseButtonDown(0);
        if (pressed && over && !_pressedLast)
        {
            try { VoiceSettingsPanel.Show(); }
            catch (Exception e) { VoiceDiagnostics.DebugWarning($"[PerfectComms] Options button open failed: {e.Message}"); }
        }
        _pressedLast = pressed;
    }

    private static float AppearScale(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float eased = 1f + c3 * (t - 1f) * (t - 1f) * (t - 1f) + c1 * (t - 1f) * (t - 1f);
        return Mathf.LerpUnclamped(0.6f, 1f, eased);
    }
}
