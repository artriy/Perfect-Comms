using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using MiraAPI.LocalSettings;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceVolumeMenu
{
    // ── Singleton window ──────────────────────────────────────────────────────
    private static GameObject?            _window;
    private static ToggleButtonBehaviour? _btnPrefab;
    private static float                  _scrollOffset;    // vertical scroll

    private const float WindowW    = 5.6f;
    private const float WindowH    = 4.6f;
    private const float RowH       = 0.68f;
    private const float SliderW    = 1.80f;
    private const float IconScale  = 0.38f;
    private const float VMin       = 0f;
    private const float VMax       = 2f;
    private static readonly Dictionary<string, float> _savedVolumes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _savedVolumesLoaded;


    public static void Toggle()
    {
        if (_window == null)
        {
            Build();
        }
        else
        {
            bool next = !_window.activeSelf;
            _window.SetActive(next);
            if (next) RebuildRows();
        }
    }

    public static void Close()
    {
        if (_window != null) _window.SetActive(false);
    }


    private static void Build()
    {
        if (HudManager.Instance == null) return;

        _btnPrefab ??= FindButtonPrefab();
        if (_btnPrefab == null) return;

        _window = new GameObject("VC_VolumeMenu");
        _window.transform.SetParent(HudManager.Instance.transform, false);
        _window.transform.localPosition = new Vector3(0f, 0f, -870f);

        var bgGO = new GameObject("BG");
        bgGO.transform.SetParent(_window.transform, false);
        var bgSr         = bgGO.AddComponent<SpriteRenderer>();
        bgSr.sprite      = Create1x1Sprite(new Color32(10, 13, 22, 240));
        bgSr.drawMode    = SpriteDrawMode.Sliced;
        bgSr.size        = new Vector2(WindowW, WindowH);
        bgSr.sortingOrder = 20;

        // Title
        var titleGO = new GameObject("Title");
        titleGO.transform.SetParent(_window.transform, false);
        titleGO.transform.localPosition = new Vector3(0f, WindowH * 0.5f - 0.35f, -0.1f);
        var titleTmp          = titleGO.AddComponent<TextMeshPro>();
        titleTmp.text         = "<b>Player Volumes</b>";
        titleTmp.fontSize     = 1.8f;
        titleTmp.alignment    = TextAlignmentOptions.Center;
        titleTmp.sortingOrder = 22;
        titleTmp.color        = new Color32(175, 215, 255, 255);
        titleTmp.rectTransform.sizeDelta = new Vector2(WindowW - 0.4f, 0.5f);

        // Close button  (top-right 'X')
        var closeBtn = CreateSmallTextButton("✕", new Vector3(WindowW * 0.5f - 0.3f, WindowH * 0.5f - 0.28f, -0.2f),
            () => _window!.SetActive(false));

        // Scroll up / down arrows
        CreateSmallTextButton("▲", new Vector3(WindowW * 0.5f - 0.28f, 0.5f, -0.2f),
            () => { _scrollOffset = Mathf.Max(0f, _scrollOffset - RowH); RebuildRows(); });
        CreateSmallTextButton("▼", new Vector3(WindowW * 0.5f - 0.28f, -0.5f, -0.2f),
            () => { _scrollOffset += RowH; RebuildRows(); });

        // Scrollable content root
        var contentGO = new GameObject("Content");
        contentGO.transform.SetParent(_window.transform, false);
        contentGO.transform.localPosition = new Vector3(0f, WindowH * 0.5f - 0.8f, -0.1f);

        // Add a SpriteMask so rows outside the window are clipped
        var maskGO = new GameObject("Mask");
        maskGO.transform.SetParent(_window.transform, false);
        maskGO.transform.localPosition = new Vector3(0f, 0f, -0.05f);
        var mask = maskGO.AddComponent<SpriteMask>();
        mask.sprite = Create1x1Sprite(Color.white);
        maskGO.transform.localScale = new Vector3(WindowW - 0.2f, WindowH - 1.0f, 1f);

        _window.SetActive(true);
        RebuildRows();
    }

    // ── Rebuild the player rows (called on open, scroll, or room change) ──────

    private static void RebuildRows()
    {
        if (_window == null) return;

        // Destroy old rows
        var content = _window.transform.Find("Content");
        if (content == null) return;
        for (int i = content.childCount - 1; i >= 0; i--)
            Object.Destroy(content.GetChild(i).gameObject);

        // Collect players
        var players = CollectPlayers();

        const float visibleH  = WindowH - 1.0f;
        int         maxRows   = Mathf.FloorToInt(visibleH / RowH);
        int         startIdx  = Mathf.FloorToInt(_scrollOffset / RowH);
        startIdx = Mathf.Clamp(startIdx, 0, Mathf.Max(0, players.Count - maxRows));
        _scrollOffset = startIdx * RowH;

        float y = 0f;
        for (int ri = startIdx; ri < players.Count && ri < startIdx + maxRows; ri++)
        {
            BuildRow(content, players[ri], y);
            y -= RowH;
        }

        // If no players, show a hint
        if (players.Count == 0)
        {
            var hint = new GameObject("NoPlayers");
            hint.transform.SetParent(content, false);
            hint.transform.localPosition = new Vector3(0f, -0.2f, 0f);
            var tmp          = hint.AddComponent<TextMeshPro>();
            tmp.text         = "No players in room";
            tmp.fontSize     = 1.4f;
            tmp.alignment    = TextAlignmentOptions.Center;
            tmp.sortingOrder = 23;
            tmp.color        = new Color32(140, 160, 200, 200);
            tmp.rectTransform.sizeDelta = new Vector2(WindowW - 0.6f, 0.6f);
        }
    }

    // ── One player row ────────────────────────────────────────────────────────
    //
    //  X offset:
    //   -2.4   icon
    //   -1.55  name
    //    0.35  slider track (centre)
    //    1.40  vol% label
    // ─────────────────────────────────────────────────────────────────────────

    private static void BuildRow(Transform parent, PlayerEntry entry, float y)
    {
        var rowGO = new GameObject($"Row_{entry.Name}");
        rowGO.transform.SetParent(parent, false);
        rowGO.transform.localPosition = new Vector3(0f, y, 0f);

        // ── PlayerIcon ────────────────────────────────────────────────────────
        bool gotIcon = false;
        if (MeetingHud.Instance != null)
        {
            foreach (var state in MeetingHud.Instance.playerStates)
            {
                if (state == null || state.TargetPlayerId != entry.PlayerId) continue;
                if (state.PlayerIcon == null) break;
                var clone = Object.Instantiate(state.PlayerIcon.gameObject, rowGO.transform);
                clone.SetActive(true);
                clone.transform.localPosition = new Vector3(-2.4f, 0.08f, -0.1f);
                clone.transform.localScale    = Vector3.one * IconScale;
                foreach (var sr in clone.GetComponentsInChildren<SpriteRenderer>())
                    sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                gotIcon = true;
                break;
            }
        }

        if (!gotIcon)
        {
            // Fallback coloured circle
            var circleGO = new GameObject("Circle");
            circleGO.transform.SetParent(rowGO.transform, false);
            circleGO.transform.localPosition = new Vector3(-2.4f, 0.08f, -0.1f);
            circleGO.transform.localScale    = Vector3.one * (IconScale * 0.6f);
            var sr        = circleGO.AddComponent<SpriteRenderer>();
            sr.sprite     = GetCircleSprite();
            sr.color      = entry.Color;
            sr.sortingOrder = 23;
            sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
        }

        // ── Player name ───────────────────────────────────────────────────────
        var nameGO   = new GameObject("Name");
        nameGO.transform.SetParent(rowGO.transform, false);
        nameGO.transform.localPosition = new Vector3(-1.50f, 0f, -0.1f);
        var nameTmp  = nameGO.AddComponent<TextMeshPro>();
        nameTmp.text = entry.Name.Length > 14 ? entry.Name[..12] + "…" : entry.Name;
        nameTmp.fontSize     = 1.25f;
        nameTmp.alignment    = TextAlignmentOptions.Left;
        nameTmp.sortingOrder = 23;
        nameTmp.color        = Color.white;
        nameTmp.enableWordWrapping  = false;
        //nameTmp.maskInteraction     = TMP_SpriteAsset.defaultSpriteAsset ? 0 : 0;
        nameTmp.rectTransform.sizeDelta = new Vector2(1.5f, 0.4f);

        // ── Volume % label (updated by slider) ────────────────────────────────
        var volGO   = new GameObject("VolLabel");
        volGO.transform.SetParent(rowGO.transform, false);
        volGO.transform.localPosition = new Vector3(1.75f, 0f, -0.1f);
        var volTmp  = volGO.AddComponent<TextMeshPro>();
        volTmp.sortingOrder = 23;
        volTmp.fontSize     = 1.2f;
        volTmp.alignment    = TextAlignmentOptions.Left;
        volTmp.color        = Color.white;
        volTmp.enableWordWrapping   = false;
        volTmp.rectTransform.sizeDelta = new Vector2(0.7f, 0.4f);

        float current = GetSavedVolume(entry);
        void SetVolLabel(float v) => volTmp.text = $"<color=#ffdd88>{Mathf.RoundToInt(v * 100f)}%</color>";
        SetVolLabel(current);

        // ── Slider track ──────────────────────────────────────────────────────
        var trackGO = new GameObject("SliderTrack");
        trackGO.transform.SetParent(rowGO.transform, false);
        trackGO.transform.localPosition = new Vector3(0.35f, 0f, -0.1f);

        var trackSr         = trackGO.AddComponent<SpriteRenderer>();
        trackSr.sprite      = Create1x1Sprite(new Color32(55, 65, 100, 200));
        trackSr.drawMode    = SpriteDrawMode.Sliced;
        trackSr.size        = new Vector2(SliderW, 0.10f);
        trackSr.sortingOrder = 22;
        trackSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        // 50% mark (visual guide for 100%)
        var midGO = new GameObject("Mid");
        midGO.transform.SetParent(trackGO.transform, false);
        midGO.transform.localPosition = new Vector3(0f, 0f, -0.02f);
        var midSr = midGO.AddComponent<SpriteRenderer>();
        midSr.sprite      = Create1x1Sprite(new Color32(100, 120, 180, 150));
        midSr.drawMode    = SpriteDrawMode.Sliced;
        midSr.size        = new Vector2(0.02f, 0.20f);
        midSr.sortingOrder = 22;
        midSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        // Fill (coloured portion)
        var fillGO  = new GameObject("Fill");
        fillGO.transform.SetParent(trackGO.transform, false);
        var fillSr  = fillGO.AddComponent<SpriteRenderer>();
        fillSr.sprite      = Create1x1Sprite(new Color32(80, 160, 235, 220));
        fillSr.drawMode    = SpriteDrawMode.Sliced;
        fillSr.sortingOrder = 23;
        fillSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        // Knob
        var knobGO = new GameObject("Knob");
        knobGO.transform.SetParent(trackGO.transform, false);
        var knobSr  = knobGO.AddComponent<SpriteRenderer>();
        knobSr.sprite       = GetCircleSprite();
        knobSr.color        = Color.white;
        knobSr.sortingOrder = 24;
        knobSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
        knobGO.transform.localScale = Vector3.one * 0.16f;

        void PositionSlider(float v)
        {
            float t    = Mathf.InverseLerp(VMin, VMax, v);
            float kX   = (t - 0.5f) * SliderW;
            float fW   = t * SliderW;
            knobGO.transform.localPosition = new Vector3(kX, 0f, -0.12f);
            fillSr.size = new Vector2(fW, 0.10f);
            fillGO.transform.localPosition = new Vector3((fW - SliderW) * 0.5f, 0f, -0.11f);
        }
        PositionSlider(current);

        // Box collider for drag
        var col   = trackGO.AddComponent<BoxCollider2D>();
        col.size  = new Vector2(SliderW, 0.40f);

        var pb    = trackGO.AddComponent<PassiveButton>();
        pb.OnClick     = new ButtonClickedEvent();
        pb.OnMouseOut  = new UnityEvent();
        pb.OnMouseOver = new UnityEvent();

        pb.OnClick.AddListener((Action)(() =>
        {
            var cam    = Camera.main;
            if (cam == null) return;
            var mWorld = cam.ScreenToWorldPoint(Input.mousePosition);
            var mLocal = trackGO.transform.InverseTransformPoint(mWorld);
            float t    = Mathf.InverseLerp(-SliderW * 0.5f, SliderW * 0.5f, mLocal.x);
            float v    = (float)Math.Round(Mathf.Lerp(VMin, VMax, t), 2);
            current = ApplyVolume(entry, v, PositionSlider, SetVolLabel);
        }));

        var upd = trackGO.AddComponent<PlayerSliderDragUpdater>();
        upd.Init(trackGO, SliderW, VMin, VMax,
            v =>
            {
                v = (float)Math.Round(v, 2);
                current = ApplyVolume(entry, v, PositionSlider, SetVolLabel);
            },
            () => current);

        CreateRowResetButton(rowGO, new Vector3(2.45f, 0f, -0.1f),
            () => current = ApplyVolume(entry, 1f, PositionSlider, SetVolLabel));

        // Divider line
        var divGO = new GameObject("Div");
        divGO.transform.SetParent(rowGO.transform, false);
        divGO.transform.localPosition = new Vector3(0f, -RowH * 0.5f + 0.04f, -0.08f);
        var divSr = divGO.AddComponent<SpriteRenderer>();
        divSr.sprite      = Create1x1Sprite(new Color32(50, 60, 90, 120));
        divSr.drawMode    = SpriteDrawMode.Sliced;
        divSr.size        = new Vector2(WindowW - 0.4f, 0.012f);
        divSr.sortingOrder = 21;
        divSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
    }

    // ── Apply volume to remote Interstellar peers + config ───────────────────

    private static float ApplyVolume(PlayerEntry entry, float v,
        Action<float> positionSlider, Action<float> setLabel)
    {
        v = Mathf.Clamp(v, VMin, VMax);
        positionSlider(v);
        setLabel(v);
        SaveVolume(entry, v);

        VoiceChatRoom.Current?.TrySetRemoteVolume(entry.PlayerId, entry.Name, v);

        return v;
    }

    // ── HUD lifecycle ─────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    [HarmonyPostfix]
    static void HudStart(HudManager __instance)
    {
        if (_window != null) { Object.Destroy(_window); _window = null; }
        _btnPrefab    = null;
        _scrollOffset = 0f;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private record PlayerEntry(byte PlayerId, string Name, Color Color);

    private static float GetSavedVolume(PlayerEntry entry)
    {
        return TryGetSavedVolume(entry.Name, out float volume) ? volume : 1f;
    }

    internal static bool TryGetSavedVolume(string playerName, out float volume)
    {
        EnsureSavedVolumesLoaded();
        string key = GetVolumeKey(playerName);
        if (key.Length > 0 && _savedVolumes.TryGetValue(key, out volume))
        {
            volume = Mathf.Clamp(volume, VMin, VMax);
            return true;
        }

        volume = 1f;
        return false;
    }

    private static void SaveVolume(PlayerEntry entry, float volume)
    {
        EnsureSavedVolumesLoaded();
        string key = GetVolumeKey(entry.Name);
        if (key.Length == 0) return;

        if (Mathf.Abs(volume - 1f) < 0.005f)
            _savedVolumes.Remove(key);
        else
            _savedVolumes[key] = Mathf.Clamp(volume, VMin, VMax);

        PersistSavedVolumes();
    }

    private static void EnsureSavedVolumesLoaded()
    {
        if (_savedVolumesLoaded) return;
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings == null) return;

        _savedVolumesLoaded = true;
        _savedVolumes.Clear();

        string raw = settings.PerPlayerVolumes.Value;
        foreach (string part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = part.LastIndexOf('=');
            if (idx <= 0 || idx >= part.Length - 1) continue;

            string key = Uri.UnescapeDataString(part[..idx]);
            if (float.TryParse(part[(idx + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                _savedVolumes[GetVolumeKey(key)] = Mathf.Clamp(value, VMin, VMax);
        }
    }

    private static void PersistSavedVolumes()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings == null) return;

        var parts = new List<string>();
        foreach (var kv in _savedVolumes)
        {
            string key = GetVolumeKey(kv.Key);
            if (key.Length == 0) continue;
            parts.Add($"{Uri.EscapeDataString(key)}={kv.Value.ToString("0.00", CultureInfo.InvariantCulture)}");
        }

        settings.PerPlayerVolumes.Value = string.Join(";", parts);
    }

    private static string GetVolumeKey(string name)
        => string.IsNullOrWhiteSpace(name) ? "" : name.Trim();

    private static List<PlayerEntry> CollectPlayers()
    {
        var list = new List<PlayerEntry>();
        if (AmongUsClient.Instance == null) return list;

        var playersById = new Dictionary<byte, PlayerControl>();
        foreach (var pc in PlayerControl.AllPlayerControls)
            if (pc != null && !playersById.ContainsKey(pc.PlayerId))
                playersById.Add(pc.PlayerId, pc);

        var seen = new HashSet<byte>();
        // Prefer active Interstellar peers (they have confirmed voice profiles)
        if (VoiceChatRoom.Current != null)
            foreach (var c in VoiceChatRoom.Current.InterstellarRemoteOverlayStates)
            {
                if (c.PlayerId == byte.MaxValue) continue;
                if (!seen.Add(c.PlayerId)) continue;
                playersById.TryGetValue(c.PlayerId, out var pc);
                var col = GetPaletteColor(pc);
                list.Add(new PlayerEntry(c.PlayerId, c.PlayerName, col));
            }

        // Fill any remaining in-game players from AllPlayerControls
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null || pc.Data == null) continue;
            if (pc == PlayerControl.LocalPlayer) continue;  // skip self
            if (!seen.Add(pc.PlayerId)) continue;
            var col = GetPaletteColor(pc);
            list.Add(new PlayerEntry(pc.PlayerId, pc.Data.PlayerName, col));
        }

        return list;
    }

    private static Color GetPaletteColor(PlayerControl? pc)
    {
        if (pc?.Data == null) return new Color(0.18f, 0.80f, 0.44f, 1f);
        int cid = pc.Data.DefaultOutfit.ColorId;
        if (cid >= 0 && cid < Palette.PlayerColors.Length) return Palette.PlayerColors[cid];
        return Color.white;
    }

    private static ToggleButtonBehaviour? FindButtonPrefab()
    {
        // Re-use the cached prefab from VoiceChatOptionsPatches if available,
        // otherwise clone from OptionsMenuBehaviour.
        var optMenu = Object.FindObjectOfType<OptionsMenuBehaviour>();
        if (optMenu?.CensorChatButton == null) return null;
        var prefab = Object.Instantiate(optMenu.CensorChatButton);
        Object.DontDestroyOnLoad(prefab);
        prefab.name = "VC_VolumeMenu_BtnPrefab";
        prefab.gameObject.SetActive(false);
        return prefab;
    }

    private static GameObject CreateSmallTextButton(string label, Vector3 pos, Action onClick)
    {
        var go = new GameObject($"Btn_{label}");
        go.transform.SetParent(_window!.transform, false);
        go.transform.localPosition = pos;

        var sr         = go.AddComponent<SpriteRenderer>();
        sr.sprite      = Create1x1Sprite(new Color32(52, 64, 98, 220));
        sr.drawMode    = SpriteDrawMode.Sliced;
        sr.size        = new Vector2(0.34f, 0.34f);
        sr.sortingOrder = 25;

        var textGO     = new GameObject("T");
        textGO.transform.SetParent(go.transform, false);
        textGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        var tmp        = textGO.AddComponent<TextMeshPro>();
        tmp.text       = label;
        tmp.fontSize   = 1.4f;
        tmp.alignment  = TextAlignmentOptions.Center;
        tmp.sortingOrder = 26;
        tmp.color      = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(0.34f, 0.34f);

        var col    = go.AddComponent<BoxCollider2D>();
        col.size   = new Vector2(0.34f, 0.34f);
        var pb     = go.AddComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((Action)(() => onClick()));
        pb.OnMouseOut  = new UnityEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOver.AddListener((Action)(() => sr.color = new Color32(75, 90, 130, 255)));
        pb.OnMouseOut.AddListener((Action)(() => sr.color  = Color.white));
        return go;
    }

    private static void CreateRowResetButton(GameObject parent, Vector3 pos, Action onClick)
    {
        var go = new GameObject("ResetVolume");
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Create1x1Sprite(new Color32(52, 64, 98, 220));
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = new Vector2(0.34f, 0.30f);
        sr.sortingOrder = 23;
        sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        var textGO = new GameObject("T");
        textGO.transform.SetParent(go.transform, false);
        textGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        var tmp = textGO.AddComponent<TextMeshPro>();
        tmp.text = "R";
        tmp.fontSize = 1.0f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.sortingOrder = 24;
        tmp.color = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(0.34f, 0.30f);

        var col = go.AddComponent<BoxCollider2D>();
        col.size = new Vector2(0.34f, 0.30f);
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((Action)(() => onClick()));
        pb.OnMouseOut = new UnityEvent();
        pb.OnMouseOver = new UnityEvent();
    }

    private static Sprite? _circleSprite;
    private static Sprite GetCircleSprite()
    {
        if (_circleSprite != null) return _circleSprite;
        const int S = 64;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        float r = S * 0.5f;
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            float dx = x - r + 0.5f, dy = y - r + 0.5f;
            float a  = Mathf.Clamp01((r - Mathf.Sqrt(dx * dx + dy * dy)) * 2f);
            tex.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        tex.Apply();
        _circleSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        return _circleSprite;
    }

    private static Sprite Create1x1Sprite(Color32 c)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, c);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
    }
}

/// <summary>
/// Per-slider drag MonoBehaviour for the volume menu.
/// Separate from SliderDragUpdater so it doesn't pollute VoiceChatOptionsPatches.
/// </summary>
public class PlayerSliderDragUpdater : MonoBehaviour
{
    private float       _min, _max, _trackW;
    private bool        _dragging;
    private Action<float>? _onChange;
    private Func<float>?   _getCurrent;

    public void Init(GameObject track, float trackW, float min, float max,
        Action<float> onChange, Func<float> getCurrent)
    {
        _trackW     = trackW;
        _min        = min;
        _max        = max;
        _onChange   = onChange;
        _getCurrent = getCurrent;
    }

    void OnMouseDown() => _dragging = true;
    void OnMouseUp()   => _dragging = false;

    void Update()
    {
        if (!_dragging) return;
        var cam = Camera.main;
        if (cam == null) return;
        var mWorld = cam.ScreenToWorldPoint(Input.mousePosition);
        var mLocal = transform.InverseTransformPoint(mWorld);
        float t    = Mathf.InverseLerp(-_trackW * 0.5f, _trackW * 0.5f, mLocal.x);
        float v    = Mathf.Lerp(_min, _max, t);
        if (Math.Abs(v - (_getCurrent?.Invoke() ?? v)) > 0.005f)
            _onChange?.Invoke(v);
    }
}
