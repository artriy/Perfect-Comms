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
    private static float                  _scrollOffset;    // vertical scroll

    private const float WindowW    = 5.6f;
    private const float WindowH    = 4.6f;
    private const float RowH       = 0.68f;
    private const float SliderW    = 1.80f;
    private const float SliderHitH  = 0.50f;
    private const float VMin       = 0f;
    private const float VMax       = 2f;
    private static readonly Dictionary<string, float> _savedVolumes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PlayerVolumeSlider> _sliders = new();
    private static PlayerVolumeSlider? _activeSlider;
    private static bool _savedVolumesLoaded;
    // Cache solid-colour 1x1 sprites so RebuildRows (run on every open/scroll) reuses textures
    // instead of leaking a fresh Texture2D+Sprite per row per rebuild.
    private static readonly Dictionary<uint, Sprite> _solidSprites = new();


    public static void Toggle()
    {
        if (_window == null)
        {
            Build();
        }
        else
        {
            bool next = !_window.activeSelf;
            if (!next) CommitActiveSlider();
            _window.SetActive(next);
            if (next) RebuildRows();
        }
    }

    public static void Close()
    {
        if (_window != null)
        {
            CommitActiveSlider();
            _window.SetActive(false);
        }
    }


    private static void Build()
    {
        if (HudManager.Instance == null)
        {
            VoiceDiagnostics.DebugWarning("[VC] Volume menu not opened: HUD is not ready.");
            return;
        }

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

        CreateCloseButton(new Vector3(WindowW * 0.5f - 0.3f, WindowH * 0.5f - 0.28f, -0.2f),
            Close);

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
        _sliders.Clear();
        _activeSlider = null;

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
    //   -2.05  name
    //    0.45  slider track (centre)
    //    1.75  vol% label
    // ─────────────────────────────────────────────────────────────────────────

    private static void BuildRow(Transform parent, PlayerEntry entry, float y)
    {
        var rowGO = new GameObject($"Row_{entry.Name}");
        rowGO.transform.SetParent(parent, false);
        rowGO.transform.localPosition = new Vector3(0f, y, 0f);

        // ── PlayerIcon ────────────────────────────────────────────────────────
        // ── Player name ───────────────────────────────────────────────────────
        var nameGO   = new GameObject("Name");
        nameGO.transform.SetParent(rowGO.transform, false);
        nameGO.transform.localPosition = new Vector3(-2.05f, 0f, -0.1f);
        var nameTmp  = nameGO.AddComponent<TextMeshPro>();
        nameTmp.text = entry.Name.Length > 13 ? entry.Name[..11] + "…" : entry.Name;
        nameTmp.fontSize     = 1.25f;
        nameTmp.alignment    = TextAlignmentOptions.Left;
        nameTmp.sortingOrder = 23;
        nameTmp.color        = Color.white;
        nameTmp.enableWordWrapping  = false;
        //nameTmp.maskInteraction     = TMP_SpriteAsset.defaultSpriteAsset ? 0 : 0;
        nameTmp.rectTransform.sizeDelta = new Vector2(1.20f, 0.4f);

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
        trackGO.transform.localPosition = new Vector3(0.45f, 0f, -0.1f);

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

        _sliders.Add(new PlayerVolumeSlider(trackGO.transform, SliderW,
            v =>
            {
                v = (float)Math.Round(v, 2);
                current = ApplyVolume(entry, v, PositionSlider, SetVolLabel, persist: false);
            },
            () => current,
            () => SaveVolume(entry, current)));

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
        Action<float> positionSlider, Action<float> setLabel, bool persist = true)
    {
        v = Mathf.Clamp(v, VMin, VMax);
        positionSlider(v);
        setLabel(v);
        if (persist)
            SaveVolume(entry, v);

        VoiceChatRoom.Current?.TrySetRemoteVolume(entry.PlayerId, entry.Name, v);

        return v;
    }

    // ── HUD lifecycle ─────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    [HarmonyPostfix]
    static void HudStart(HudManager __instance)
    {
        CommitActiveSlider();
        _sliders.Clear();
        if (_window != null) { Object.Destroy(_window); _window = null; }
        _scrollOffset = 0f;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private record PlayerEntry(byte PlayerId, string Name);

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

        var seen = new HashSet<byte>();
        // Prefer active Interstellar peers (they have confirmed voice profiles)
        if (VoiceChatRoom.Current != null)
            foreach (var c in VoiceChatRoom.Current.InterstellarRemoteOverlayStates)
            {
                if (c.PlayerId == byte.MaxValue) continue;
                if (!seen.Add(c.PlayerId)) continue;
                list.Add(new PlayerEntry(c.PlayerId, c.PlayerName));
            }

        // Fill any remaining in-game players from AllPlayerControls
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null || pc.Data == null) continue;
            if (pc == PlayerControl.LocalPlayer) continue;  // skip self
            if (!seen.Add(pc.PlayerId)) continue;
            list.Add(new PlayerEntry(pc.PlayerId, pc.Data.PlayerName));
        }

        return list;
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    [HarmonyPostfix]
    static void HudUpdate()
    {
        HandleSliderInput();
    }

    private static void HandleSliderInput()
    {
        if (_window == null || !_window.activeSelf)
        {
            CommitActiveSlider();
            return;
        }

        if (!Input.GetMouseButton(0))
        {
            CommitActiveSlider();
            return;
        }

        if (Input.GetMouseButtonDown(0))
            _activeSlider = FindSliderAtMouse();

        if (_activeSlider != null)
            ApplySliderFromMouse(_activeSlider);
    }

    private static PlayerVolumeSlider? FindSliderAtMouse()
    {
        if (!TryGetMouseWorld(out var mouseWorld)) return null;
        for (int i = _sliders.Count - 1; i >= 0; i--)
        {
            var slider = _sliders[i];
            if (slider.Track == null)
            {
                _sliders.RemoveAt(i);
                continue;
            }

            var local = slider.Track.InverseTransformPoint(mouseWorld);
            if (Mathf.Abs(local.x) <= slider.TrackW * 0.5f && Mathf.Abs(local.y) <= SliderHitH * 0.5f)
                return slider;
        }
        return null;
    }

    private static void ApplySliderFromMouse(PlayerVolumeSlider slider)
    {
        if (!TryGetMouseWorld(out var mouseWorld)) return;
        var local = slider.Track.InverseTransformPoint(mouseWorld);
        float t = Mathf.Clamp01(Mathf.InverseLerp(-slider.TrackW * 0.5f, slider.TrackW * 0.5f, local.x));
        float value = Mathf.Lerp(VMin, VMax, t);
        if (Math.Abs(value - slider.GetCurrent()) <= 0.001f) return;

        slider.OnChange(value);
        slider.Changed = true;
    }

    private static void CommitActiveSlider()
    {
        if (_activeSlider == null) return;
        if (_activeSlider.Changed)
        {
            _activeSlider.OnCommit();
            _activeSlider.Changed = false;
        }
        _activeSlider = null;
    }

    private static bool TryGetMouseWorld(out Vector3 mouseWorld)
    {
        var cam = Camera.main;
        if (cam == null)
        {
            mouseWorld = default;
            return false;
        }

        mouseWorld = cam.ScreenToWorldPoint(Input.mousePosition);
        return true;
    }

    private sealed class PlayerVolumeSlider
    {
        public readonly Transform Track;
        public readonly float TrackW;
        public readonly Action<float> OnChange;
        public readonly Func<float> GetCurrent;
        public readonly Action OnCommit;
        public bool Changed;

        public PlayerVolumeSlider(Transform track, float trackW, Action<float> onChange, Func<float> getCurrent, Action onCommit)
        {
            Track = track;
            TrackW = trackW;
            OnChange = onChange;
            GetCurrent = getCurrent;
            OnCommit = onCommit;
        }
    }

    private static GameObject CreateSmallTextButton(string label, Vector3 pos, Action onClick)
    {
        var go = new GameObject($"Btn_{label}");
        go.transform.SetParent(_window!.transform, false);
        go.transform.localPosition = pos;

        var baseColor = new Color32(52, 64, 98, 220);
        var hoverColor = new Color32(75, 90, 130, 255);
        var sr         = go.AddComponent<SpriteRenderer>();
        sr.sprite      = Create1x1Sprite(Color.white);
        sr.color       = baseColor;
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
        pb.OnMouseOver.AddListener((Action)(() => sr.color = hoverColor));
        pb.OnMouseOut.AddListener((Action)(() => sr.color  = baseColor));
        return go;
    }

    private static GameObject CreateCloseButton(Vector3 pos, Action onClick)
    {
        var go = new GameObject("Btn_Close");
        go.transform.SetParent(_window!.transform, false);
        go.transform.localPosition = pos;

        var baseColor = new Color32(196, 55, 66, 245);
        var hoverColor = new Color32(232, 74, 86, 255);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = GetCircleSprite();
        sr.color = baseColor;
        sr.sortingOrder = 25;
        go.transform.localScale = Vector3.one * 0.42f;

        CreateButtonLine(go.transform, "CloseLineA", 45f);
        CreateButtonLine(go.transform, "CloseLineB", -45f);

        var col = go.AddComponent<BoxCollider2D>();
        col.size = new Vector2(1f, 1f);
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((Action)(() => onClick()));
        pb.OnMouseOut = new UnityEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOver.AddListener((Action)(() => sr.color = hoverColor));
        pb.OnMouseOut.AddListener((Action)(() => sr.color = baseColor));
        return go;
    }

    private static void CreateButtonLine(Transform parent, string name, float rotationZ)
    {
        var lineGO = new GameObject(name);
        lineGO.transform.SetParent(parent, false);
        lineGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        lineGO.transform.localRotation = Quaternion.Euler(0f, 0f, rotationZ);
        var sr = lineGO.AddComponent<SpriteRenderer>();
        sr.sprite = Create1x1Sprite(Color.white);
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = new Vector2(0.62f, 0.10f);
        sr.sortingOrder = 26;
    }

    private static void CreateRowResetButton(GameObject parent, Vector3 pos, Action onClick)
    {
        var go = new GameObject("ResetVolume");
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = pos;

        var baseColor = new Color32(52, 64, 98, 220);
        var hoverColor = new Color32(75, 90, 130, 255);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Create1x1Sprite(Color.white);
        sr.color = baseColor;
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = new Vector2(0.34f, 0.30f);
        sr.sortingOrder = 23;
        sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        var iconGO = new GameObject("ResetIcon");
        iconGO.transform.SetParent(go.transform, false);
        iconGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        iconGO.transform.localScale = Vector3.one * 3.0f;
        var iconSr = iconGO.AddComponent<SpriteRenderer>();
        iconSr.sprite = ResetIconSprite;
        iconSr.sortingOrder = 24;
        iconSr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

        var col = go.AddComponent<BoxCollider2D>();
        col.size = new Vector2(0.34f, 0.30f);
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new ButtonClickedEvent();
        pb.OnClick.AddListener((Action)(() => onClick()));
        pb.OnMouseOut = new UnityEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOver.AddListener((Action)(() => sr.color = hoverColor));
        pb.OnMouseOut.AddListener((Action)(() => sr.color = baseColor));
    }

    private static Sprite? _resetIconSprite;
    private static Sprite ResetIconSprite
        => _resetIconSprite ??= VoiceChatHudState.LoadSprite("VoiceChatPlugin.Resources.VolumeResetIcon.png", highQuality: true);

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
        uint key = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
        if (_solidSprites.TryGetValue(key, out var cached) && cached != null)
            return cached;

        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixel(0, 0, c);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        _solidSprites[key] = sprite;
        return sprite;
    }
}
