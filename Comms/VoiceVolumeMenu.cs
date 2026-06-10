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
    private const float KnobScale       = 0.16f;
    private const float KnobScaleActive = 0.21f;

    private const float ListTopY = 1.08f;
    private const float ListH    = 3.52f;

    private const float MeterW     = 1.30f;
    private const float MeterH     = 0.06f;
    private const float MeterLeftX = -2.15f;
    private const float MeterY     = -0.16f;
    private const float MeterReleasePerSecond = 1.6f;

    private static readonly Color MeterGreen  = new(0.30f, 0.85f, 0.42f, 0.95f);
    private static readonly Color MeterYellow = new(0.95f, 0.84f, 0.25f, 0.95f);
    private static readonly Color MeterRed    = new(0.96f, 0.25f, 0.22f, 0.95f);
    private static readonly Color FillLow     = new(0.18f, 0.32f, 0.55f, 0.86f);
    private static readonly Color FillFull    = new(0.31f, 0.63f, 0.92f, 0.86f);
    private static readonly Color FillBoost   = new(0.92f, 0.67f, 0.24f, 0.90f);
    private static readonly Color32 AccentBorder    = new(0, 229, 255, 90);
    private static readonly Color32 AccentUnderline = new(0, 229, 255, 140);

    private static readonly Dictionary<string, float> _savedVolumes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PlayerVolumeSlider> _sliders = new();
    private static readonly List<RowMeter> _meters = new();
    private static PlayerVolumeSlider? _activeSlider;
    private static bool _savedVolumesLoaded;
    private static TextMeshPro? _subtitleTmp;
    private static GameObject? _arrowUp;
    private static GameObject? _arrowDown;
    private static int _rowsStartIdx;
    private static int _rowsMaxStart;
    private static int _rosterSignature;
    private static int _lastSubtitleCount = -1;
    private static float _fillUnit;
    // Cache solid-colour 1x1 sprites so RebuildRows (run on every open/scroll) reuses textures
    // instead of leaking a fresh Texture2D+Sprite per row per rebuild.
    private static readonly Dictionary<uint, Sprite> _solidSprites = new();

    private sealed class RowMeter
    {
        public byte PlayerId;
        public SpriteRenderer Fill = null!;
        public Transform FillTr = null!;
        public SpriteRenderer? Glow;
        public Color GlowColor;
        public float Display;
    }


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

        CreateAccentStrip("BorderTop",    new Vector3(0f, WindowH * 0.5f - 0.015f, -0.12f), new Vector2(WindowW, 0.03f), AccentBorder);
        CreateAccentStrip("BorderBottom", new Vector3(0f, -WindowH * 0.5f + 0.015f, -0.12f), new Vector2(WindowW, 0.03f), AccentBorder);
        CreateAccentStrip("BorderLeft",   new Vector3(-WindowW * 0.5f + 0.015f, 0f, -0.12f), new Vector2(0.03f, WindowH), AccentBorder);
        CreateAccentStrip("BorderRight",  new Vector3(WindowW * 0.5f - 0.015f, 0f, -0.12f), new Vector2(0.03f, WindowH), AccentBorder);

        // Title
        var titleGO = new GameObject("Title");
        titleGO.transform.SetParent(_window.transform, false);
        titleGO.transform.localPosition = new Vector3(0f, WindowH * 0.5f - 0.33f, -0.1f);
        var titleTmp          = titleGO.AddComponent<TextMeshPro>();
        titleTmp.text         = "<b>Player Volumes</b>";
        titleTmp.fontSize     = 1.7f;
        titleTmp.alignment    = TextAlignmentOptions.Center;
        titleTmp.sortingOrder = 22;
        titleTmp.color        = new Color32(175, 215, 255, 255);
        titleTmp.rectTransform.sizeDelta = new Vector2(WindowW - 0.4f, 0.5f);

        var subGO = new GameObject("Subtitle");
        subGO.transform.SetParent(_window.transform, false);
        subGO.transform.localPosition = new Vector3(0f, WindowH * 0.5f - 0.64f, -0.1f);
        _subtitleTmp              = subGO.AddComponent<TextMeshPro>();
        _subtitleTmp.fontSize     = 0.85f;
        _subtitleTmp.alignment    = TextAlignmentOptions.Center;
        _subtitleTmp.sortingOrder = 22;
        _subtitleTmp.color        = new Color32(130, 160, 200, 230);
        _subtitleTmp.rectTransform.sizeDelta = new Vector2(WindowW - 0.4f, 0.3f);
        _lastSubtitleCount = -1;

        CreateAccentStrip("TitleUnderline", new Vector3(0f, 1.50f, -0.1f), new Vector2(WindowW - 0.8f, 0.02f), AccentUnderline);

        CreateCloseButton(new Vector3(WindowW * 0.5f - 0.3f, WindowH * 0.5f - 0.28f, -0.2f),
            Close);

        // Scroll up / down arrows
        _arrowUp = CreateSmallTextButton("▲", new Vector3(WindowW * 0.5f - 0.28f, 0.5f, -0.2f),
            () => { _scrollOffset = Mathf.Max(0f, _scrollOffset - RowH); RebuildRows(); });
        _arrowDown = CreateSmallTextButton("▼", new Vector3(WindowW * 0.5f - 0.28f, -0.5f, -0.2f),
            () => { _scrollOffset += RowH; RebuildRows(); });

        // Scrollable content root
        var contentGO = new GameObject("Content");
        contentGO.transform.SetParent(_window.transform, false);
        contentGO.transform.localPosition = new Vector3(0f, ListTopY, -0.1f);

        _window.SetActive(true);
        RebuildRows();
    }

    // ── Rebuild the player rows (called on open, scroll, or room change) ──────

    private static void RebuildRows()
    {
        if (_window == null) return;

        var content = _window.transform.Find("Content");
        if (content == null) return;
        for (int i = content.childCount - 1; i >= 0; i--)
            Object.Destroy(content.GetChild(i).gameObject);
        _sliders.Clear();
        _meters.Clear();
        _activeSlider = null;

        var players = CollectPlayers();
        _rosterSignature = ComputeRosterSignature(players);

        int maxRows  = Mathf.FloorToInt(ListH / RowH);
        _rowsMaxStart = Mathf.Max(0, players.Count - maxRows);
        int startIdx = Mathf.Clamp(Mathf.FloorToInt(_scrollOffset / RowH), 0, _rowsMaxStart);
        _scrollOffset = startIdx * RowH;
        _rowsStartIdx = startIdx;

        bool scrollable = players.Count > maxRows;
        if (_arrowUp != null) _arrowUp.SetActive(scrollable);
        if (_arrowDown != null) _arrowDown.SetActive(scrollable);

        if (_subtitleTmp != null && players.Count != _lastSubtitleCount)
        {
            _lastSubtitleCount = players.Count;
            _subtitleTmp.text = players.Count == 1 ? "1 player" : $"{players.Count} players";
        }

        float y = 0f;
        for (int ri = startIdx; ri < players.Count && ri < startIdx + maxRows; ri++)
        {
            BuildRow(content, players[ri], y);
            y -= RowH;
        }

        if (players.Count == 0)
        {
            var hint = new GameObject("NoPlayers");
            hint.transform.SetParent(content, false);
            hint.transform.localPosition = new Vector3(0f, -0.6f, 0f);
            var tmp          = hint.AddComponent<TextMeshPro>();
            tmp.text         = "No other players in the room yet";
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
    //   -2.42  avatar + speaking glow
    //   -1.50  name (top) / live level meter (bottom)
    //    0.55  slider track (centre)
    //    1.82  vol% label
    //    2.48  reset
    // ─────────────────────────────────────────────────────────────────────────

    private static void BuildRow(Transform parent, PlayerEntry entry, float y)
    {
        var rowGO = new GameObject($"Row_{entry.Name}");
        rowGO.transform.SetParent(parent, false);
        rowGO.transform.localPosition = new Vector3(0f, y, 0f);

        var pc = FindPlayerControl(entry.PlayerId);
        var paletteColor = CrewmateAvatarRenderer.GetPaletteColor(pc);

        var glowGO = new GameObject("Glow");
        glowGO.transform.SetParent(rowGO.transform, false);
        glowGO.transform.localPosition = new Vector3(-2.42f, 0f, -0.09f);
        glowGO.transform.localScale = Vector3.one * 0.55f;
        var glowSr = glowGO.AddComponent<SpriteRenderer>();
        glowSr.sprite       = GetGlowSprite();
        glowSr.color        = new Color(paletteColor.r, paletteColor.g, paletteColor.b, 0f);
        glowSr.sortingOrder = 22;

        var bodySprite = CrewmateAvatarRenderer.GetBodySpriteFor(pc);
        var avatarGO = new GameObject("Avatar");
        avatarGO.transform.SetParent(rowGO.transform, false);
        avatarGO.transform.localPosition = new Vector3(-2.42f, 0f, -0.1f);
        var avatarSr = avatarGO.AddComponent<SpriteRenderer>();
        avatarSr.sortingOrder = 23;
        if (bodySprite != null)
        {
            avatarSr.sprite = bodySprite;
            float bodyH = bodySprite.bounds.size.y;
            if (bodyH > 0.0001f)
                avatarGO.transform.localScale = Vector3.one * (0.46f / bodyH);
        }
        else
        {
            avatarSr.sprite = GetCircleSprite();
            avatarSr.color  = paletteColor;
            avatarGO.transform.localScale = Vector3.one * 0.30f;
        }

        // ── Player name ───────────────────────────────────────────────────────
        var nameGO   = new GameObject("Name");
        nameGO.transform.SetParent(rowGO.transform, false);
        nameGO.transform.localPosition = new Vector3(-1.50f, 0.13f, -0.1f);
        var nameTmp  = nameGO.AddComponent<TextMeshPro>();
        nameTmp.text = entry.Name.Length > 12 ? entry.Name[..10] + "…" : entry.Name;
        nameTmp.fontSize     = 1.15f;
        nameTmp.alignment    = TextAlignmentOptions.Left;
        nameTmp.sortingOrder = 23;
        nameTmp.color        = Color.white;
        nameTmp.enableWordWrapping  = false;
        nameTmp.rectTransform.sizeDelta = new Vector2(MeterW, 0.34f);

        // ── Live level meter (fed by VoiceOverlayState each frame) ────────────
        var meterTrackGO = new GameObject("MeterTrack");
        meterTrackGO.transform.SetParent(rowGO.transform, false);
        meterTrackGO.transform.localPosition = new Vector3(-1.50f, MeterY, -0.10f);
        var meterTrackSr = meterTrackGO.AddComponent<SpriteRenderer>();
        meterTrackSr.sprite      = Create1x1Sprite(new Color32(38, 46, 70, 200));
        meterTrackSr.drawMode    = SpriteDrawMode.Sliced;
        meterTrackSr.size        = new Vector2(MeterW, MeterH);
        meterTrackSr.sortingOrder = 22;

        var meterFillGO = new GameObject("MeterFill");
        meterFillGO.transform.SetParent(rowGO.transform, false);
        meterFillGO.transform.localPosition = new Vector3(MeterLeftX, MeterY, -0.12f);
        var meterFillSr = meterFillGO.AddComponent<SpriteRenderer>();
        var fillSprite  = Create1x1Sprite(Color.white);
        meterFillSr.sprite       = fillSprite;
        meterFillSr.color        = MeterGreen;
        meterFillSr.sortingOrder = 23;
        if (_fillUnit <= 0f && fillSprite.bounds.size.x > 0f)
            _fillUnit = 1f / fillSprite.bounds.size.x;
        meterFillGO.transform.localScale = new Vector3(0f, MeterH * _fillUnit, 1f);

        _meters.Add(new RowMeter
        {
            PlayerId  = entry.PlayerId,
            Fill      = meterFillSr,
            FillTr    = meterFillGO.transform,
            Glow      = glowSr,
            GlowColor = paletteColor,
        });

        // ── Volume % label (updated by slider) ────────────────────────────────
        var volGO   = new GameObject("VolLabel");
        volGO.transform.SetParent(rowGO.transform, false);
        volGO.transform.localPosition = new Vector3(1.82f, 0f, -0.1f);
        var volTmp  = volGO.AddComponent<TextMeshPro>();
        volTmp.sortingOrder = 23;
        volTmp.fontSize     = 1.2f;
        volTmp.alignment    = TextAlignmentOptions.Left;
        volTmp.color        = Color.white;
        volTmp.enableWordWrapping   = false;
        volTmp.rectTransform.sizeDelta = new Vector2(0.7f, 0.4f);

        float current = GetSavedVolume(entry);
        void SetVolLabel(float v)
        {
            string hex = v < 0.005f ? "#8a93a8" : v > 1.005f ? "#ffb347" : "#ffdd88";
            volTmp.text = $"<color={hex}>{Mathf.RoundToInt(v * 100f)}%</color>";
        }
        SetVolLabel(current);

        // ── Slider track ──────────────────────────────────────────────────────
        var trackGO = new GameObject("SliderTrack");
        trackGO.transform.SetParent(rowGO.transform, false);
        trackGO.transform.localPosition = new Vector3(0.55f, 0f, -0.1f);

        var trackSr         = trackGO.AddComponent<SpriteRenderer>();
        trackSr.sprite      = Create1x1Sprite(new Color32(55, 65, 100, 200));
        trackSr.drawMode    = SpriteDrawMode.Sliced;
        trackSr.size        = new Vector2(SliderW, 0.10f);
        trackSr.sortingOrder = 22;

        // 50% mark (visual guide for 100%)
        var midGO = new GameObject("Mid");
        midGO.transform.SetParent(trackGO.transform, false);
        midGO.transform.localPosition = new Vector3(0f, 0f, -0.02f);
        var midSr = midGO.AddComponent<SpriteRenderer>();
        midSr.sprite      = Create1x1Sprite(new Color32(100, 120, 180, 150));
        midSr.drawMode    = SpriteDrawMode.Sliced;
        midSr.size        = new Vector2(0.02f, 0.20f);
        midSr.sortingOrder = 22;

        // Fill (coloured portion)
        var fillGO  = new GameObject("Fill");
        fillGO.transform.SetParent(trackGO.transform, false);
        var fillSr  = fillGO.AddComponent<SpriteRenderer>();
        fillSr.sprite      = Create1x1Sprite(Color.white);
        fillSr.color       = FillFull;
        fillSr.drawMode    = SpriteDrawMode.Sliced;
        fillSr.sortingOrder = 23;

        // Knob
        var knobGO = new GameObject("Knob");
        knobGO.transform.SetParent(trackGO.transform, false);
        var knobSr  = knobGO.AddComponent<SpriteRenderer>();
        knobSr.sprite       = GetCircleSprite();
        knobSr.color        = Color.white;
        knobSr.sortingOrder = 24;
        knobGO.transform.localScale = Vector3.one * KnobScale;

        void PositionSlider(float v)
        {
            float t    = Mathf.InverseLerp(VMin, VMax, v);
            float kX   = (t - 0.5f) * SliderW;
            float fW   = t * SliderW;
            knobGO.transform.localPosition = new Vector3(kX, 0f, -0.12f);
            fillSr.size = new Vector2(fW, 0.10f);
            fillSr.color = SliderFillColor(v);
            fillGO.transform.localPosition = new Vector3((fW - SliderW) * 0.5f, 0f, -0.11f);
        }
        PositionSlider(current);

        _sliders.Add(new PlayerVolumeSlider(trackGO.transform, SliderW, knobGO.transform,
            v =>
            {
                v = (float)Math.Round(v, 2);
                current = ApplyVolume(entry, v, PositionSlider, SetVolLabel, persist: false);
            },
            () => current,
            () => SaveVolume(entry, current)));

        CreateRowResetButton(rowGO, new Vector3(2.48f, 0f, -0.1f),
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
    }

    private static Color SliderFillColor(float v)
        => v <= 1f ? Color.Lerp(FillLow, FillFull, v) : Color.Lerp(FillFull, FillBoost, v - 1f);

    private static Color MeterColor(float t)
        => t < 0.55f
            ? Color.Lerp(MeterGreen, MeterYellow, t / 0.55f)
            : Color.Lerp(MeterYellow, MeterRed, (t - 0.55f) / 0.45f);

    // ── Live meter update (runs only while the window is open) ───────────────

    private static void UpdateLiveMeters()
    {
        if (_meters.Count == 0) return;
        var overlay = VoiceOverlayState.Current(VoiceChatRoom.Current);
        var remotes = overlay.RemotePlayers;
        float dt = Time.deltaTime;
        for (int i = 0; i < _meters.Count; i++)
        {
            var m = _meters[i];
            if (m.Fill == null) continue;

            float level = 0f;
            bool speaking = false;
            for (int j = 0; j < remotes.Count; j++)
            {
                var r = remotes[j];
                if (r.PlayerId == m.PlayerId)
                {
                    level = r.Level;
                    speaking = r.IsSpeaking;
                    break;
                }
            }

            float target = Mathf.Sqrt(Mathf.Clamp01(level));
            float shown = target >= m.Display ? target : Mathf.Max(target, m.Display - MeterReleasePerSecond * dt);
            m.Display = shown;

            float w = shown * MeterW;
            m.FillTr.localScale = new Vector3(w * _fillUnit, MeterH * _fillUnit, 1f);
            m.FillTr.localPosition = new Vector3(MeterLeftX + w * 0.5f, MeterY, -0.12f);
            m.Fill.color = MeterColor(shown);

            if (m.Glow != null)
            {
                var c = m.GlowColor;
                c.a = speaking ? 0.30f + 0.45f * shown : 0f;
                m.Glow.color = c;
            }
        }
    }

    private static void RefreshRosterIfChanged()
    {
        var players = CollectPlayers();
        if (ComputeRosterSignature(players) != _rosterSignature)
            RebuildRows();
    }

    private static int ComputeRosterSignature(List<PlayerEntry> players)
    {
        int h = players.Count;
        for (int i = 0; i < players.Count; i++)
        {
            h = h * 31 + players[i].PlayerId;
            h = h * 31 + (players[i].Name?.GetHashCode() ?? 0);
        }
        return h;
    }

    private static void HandleWheelScroll()
    {
        if (_rowsMaxStart <= 0) return;
        float dy = Input.mouseScrollDelta.y;
        if (dy > -0.01f && dy < 0.01f) return;
        if (!TryGetMouseWorld(out var mouseWorld)) return;
        var local = _window!.transform.InverseTransformPoint(mouseWorld);
        if (Mathf.Abs(local.x) > WindowW * 0.5f || Mathf.Abs(local.y) > WindowH * 0.5f) return;
        int next = Mathf.Clamp(_rowsStartIdx + (dy > 0f ? -1 : 1), 0, _rowsMaxStart);
        if (next == _rowsStartIdx) return;
        _scrollOffset = next * RowH;
        RebuildRows();
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
        _meters.Clear();
        _subtitleTmp = null;
        _arrowUp = null;
        _arrowDown = null;
        _lastSubtitleCount = -1;
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

    private static PlayerControl? FindPlayerControl(byte playerId)
    {
        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc != null && pc.PlayerId == playerId) return pc;
        }
        return null;
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    [HarmonyPostfix]
    static void HudUpdate()
    {
        HandleSliderInput();
        if (_window == null || !_window.activeSelf) return;
        HandleWheelScroll();
        UpdateLiveMeters();
        if (_activeSlider == null && Time.frameCount % 30 == 0)
            RefreshRosterIfChanged();
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
        {
            _activeSlider = FindSliderAtMouse();
            if (_activeSlider != null && _activeSlider.Knob != null)
                _activeSlider.Knob.localScale = Vector3.one * KnobScaleActive;
        }

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
        if (slider.Track == null)
        {
            _activeSlider = null;
            return;
        }

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
        if (_activeSlider.Knob != null)
            _activeSlider.Knob.localScale = Vector3.one * KnobScale;
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
        public readonly Transform Knob;
        public readonly Action<float> OnChange;
        public readonly Func<float> GetCurrent;
        public readonly Action OnCommit;
        public bool Changed;

        public PlayerVolumeSlider(Transform track, float trackW, Transform knob, Action<float> onChange, Func<float> getCurrent, Action onCommit)
        {
            Track = track;
            TrackW = trackW;
            Knob = knob;
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

    private static void CreateAccentStrip(string name, Vector3 pos, Vector2 size, Color32 color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_window!.transform, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Create1x1Sprite(Color.white);
        sr.color = color;
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = size;
        sr.sortingOrder = 21;
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

        var iconGO = new GameObject("ResetIcon");
        iconGO.transform.SetParent(go.transform, false);
        iconGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);
        iconGO.transform.localScale = Vector3.one * 3.0f;
        var iconSr = iconGO.AddComponent<SpriteRenderer>();
        iconSr.sprite = ResetIconSprite;
        iconSr.sortingOrder = 24;

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
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
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
        _circleSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _circleSprite;
    }

    private static Sprite? _glowSprite;
    private static Sprite GetGlowSprite()
    {
        if (_glowSprite != null) return _glowSprite;
        const int S = 64;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        float r = S * 0.5f;
        for (int y = 0; y < S; y++)
        for (int x = 0; x < S; x++)
        {
            float dx = x - r + 0.5f, dy = y - r + 0.5f;
            float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
            float a = Mathf.Clamp01(1f - d);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
        }
        tex.Apply();
        _glowSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        _glowSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _glowSprite;
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
