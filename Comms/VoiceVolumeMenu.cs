using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceVolumeMenu
{
    private const float PanelW = 660f;
    private const float PanelH = 560f;
    private const float PanelScale = 1.15f;
    private const float RowH = 84f;
    private const float TopPad = 12f;
    private const float VMin = 0f;
    private const float VMax = 2f;

    private static VoiceUiKit.PanelShell? _shell;
    private static readonly List<VoiceUiKit.PlayerVolumeRow> _rows = new();
    private static VoiceUiKit.PlayerVolumeRow? _activeRow;
    private static float _scroll;
    private static float _contentHeight;
    private static float _animT;
    private static int _rosterSignature;
    private static bool _shown;

    private static readonly Dictionary<string, float> _savedVolumes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _savedVolumesLoaded;

    private static bool ShellAlive => _shell != null && _shell.Root != null;
    public static bool IsOpen => ShellAlive && _shown;

    public static void Toggle()
    {
        if (_shown) Hide();
        else Show();
    }

    public static void Show()
    {
        var privacy = VoiceIdentityPrivacyRuntime.Current(
            VoiceOverlayState.Current(VoiceChatRoom.Current));
        if (privacy.HideAllForViewer || privacy.DimAll)
            return;

        VoiceUiKit.EnsureCanvas();
        VoiceUiKit.EnsureDriver();

        bool rebuilt = false;
        if (!ShellAlive)
        {
            Destroy();
            Build();
            rebuilt = true;
        }

        _shell!.Root.SetActive(true);
        _shell.Group.alpha = 1f;
        _shell.Group.interactable = true;
        _shell.Group.blocksRaycasts = true;
        _shell.RootRect.localScale = Vector3.one * PanelScale;
        _scroll = 0f;
        _shell.PaneRoot.anchoredPosition = Vector2.zero;
        _animT = 0f;
        _shown = true;

        if (!rebuilt) RebuildRows();

        VoiceUiKit.RaiseAbove(_shell.RootRect);
    }

    public static void Hide()
    {
        _shown = false;
        _animT = 0f;
        _activeRow = null;
        if (_shell != null && _shell.Root != null)
        {
            _shell.Group.alpha = 0f;
            _shell.Group.interactable = false;
            _shell.Group.blocksRaycasts = false;
            _shell.Root.SetActive(false);
        }
    }

    public static void ForceClose() => Hide();

    private static void HeaderClose()
    {
        VoiceUiKit.SwallowClick();
        Hide();
    }

    private static void Build()
    {
        _shell = new VoiceUiKit.PanelShell("VC_VolumeMenu", "PLAYER VOLUMES", PanelW, PanelH, HeaderClose, rail: false, backdrop: false);
        RebuildRows();
    }

    private static void Destroy()
    {
        if (_shell != null)
        {
            if (_shell.Root != null) Object.Destroy(_shell.Root);
            _shell = null;
        }
        _rows.Clear();
        _activeRow = null;
        _scroll = 0f;
        _shown = false;
        _rosterSignature = 0;
    }

    private static void RebuildRows()
    {
        if (_shell == null) return;
        for (int i = _shell.PaneRoot.childCount - 1; i >= 0; i--)
            Object.Destroy(_shell.PaneRoot.GetChild(i).gameObject);
        _rows.Clear();
        _activeRow = null;

        var players = CollectPlayers();
        _rosterSignature = ComputeRosterSignature(players);

        float y = -TopPad;
        for (int i = 0; i < players.Count; i++)
        {
            var entry = players[i];
            float current = GetSavedVolume(entry);
            var pc = FindPlayerControl(entry.PlayerId);

            var row = new VoiceUiKit.PlayerVolumeRow(
                () => current,
                v => current = ApplyVolume(entry, v, persist: false),
                () => SaveVolume(entry, current),
                pc, VMin, VMax)
                .Build(_shell.PaneRoot, entry.Name, _shell.PaneWidth, y, RowH);
            row.PlayerId = entry.PlayerId;
            _rows.Add(row);
            y -= RowH;
        }

        _contentHeight = -y;
        ApplyScroll(true);

        if (players.Count == 0)
        {
            var empty = VoiceUiKit.Text("Empty", _shell.PaneRoot, "No other players in the room yet", 18f,
                VoiceUiKit.TextMuted, TMPro.TextAlignmentOptions.Center);
            empty.rectTransform.Anchor(new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f));
            empty.rectTransform.sizeDelta = new Vector2(0f, 40f);
            empty.rectTransform.anchoredPosition = new Vector2(0f, -40f);
        }
    }

    public static void Tick()
    {
        if (_shell == null) return;
        if (_shell.Root == null) { Destroy(); return; }
        if (!_shown) return;

        var privacy = VoiceIdentityPrivacyRuntime.Current(
            VoiceOverlayState.Current(VoiceChatRoom.Current));
        if (privacy.HideAllForViewer || privacy.DimAll)
        {
            Hide();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            VoiceUiKit.SwallowClick();
            Hide();
            return;
        }

        float dt = Time.deltaTime;
        if (_animT < 1f)
        {
            _animT = Mathf.Min(1f, _animT + dt / 0.22f);
            ApplyOpenAnim();
        }

        _shell.TickHeader();
        if (_shell == null) return;
        HandleScroll();
        HandleInput();

        FeedMeters();
        for (int i = 0; i < _rows.Count; i++) _rows[i].Tick(dt);

        if (_activeRow == null && Time.frameCount % 30 == 0) RefreshRosterIfChanged();
    }

    private static void FeedMeters()
    {
        if (_rows.Count == 0) return;
        var overlay = VoiceOverlayState.Current(VoiceChatRoom.Current);
        var privacy = VoiceIdentityPrivacyRuntime.Current(overlay);
        if (privacy.HideAllForViewer || privacy.DimAll)
        {
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].ClearLevelImmediately();
            return;
        }

        var speakers = privacy.Speakers;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            float level = 0f;
            bool speaking = false;
            for (int j = 0; j < speakers.Count; j++)
            {
                var speaker = speakers[j];
                if (speaker.PresentationPlayerId == row.PlayerId)
                {
                    level = speaker.Level;
                    speaking = true;
                    break;
                }
            }
            if (speaking)
            {
                row.SetLevel(level, true);
                continue;
            }

            // Preserve the ordinary release animation when a speaker merely stops talking. If a
            // visible identity became concealed or was remapped, snap the stale meter off instead.
            // A currently active alias collision is protected by the speaking branch above.
            bool snapForPrivacy = VoiceIdentityPrivacyRuntime.ShouldSnapPresentation(row.PlayerId);
            if (!snapForPrivacy && row.HasVisibleMeter)
            {
                var current = VoiceIdentityPrivacyRuntime.Peek(row.PlayerId);
                snapForPrivacy = !current.HasConcretePresentation
                                 || current.PresentationPlayerId != row.PlayerId;
            }

            if (snapForPrivacy)
                row.ClearLevelImmediately();
            else
                row.SetLevel(0f, false);
        }
    }

    private static void ApplyOpenAnim()
    {
        if (_shell == null) return;
        float t = _animT;
        float scale = VoiceUiKit.AppearScale(t) * PanelScale;
        _shell.RootRect.localScale = new Vector3(scale, scale, 1f);
        _shell.Group.alpha = Mathf.Clamp01(t / 0.6f);
    }

    private static void ApplyScroll(bool reset)
    {
        if (_shell == null) return;
        float viewH = _shell.PaneHeight - 24f;
        float maxScroll = Mathf.Max(0f, _contentHeight - viewH);
        _scroll = reset ? 0f : Mathf.Clamp(_scroll, 0f, maxScroll);
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
    }

    private static void HandleScroll()
    {
        if (_shell == null) return;
        float viewH = _shell.PaneHeight - 24f;
        float maxScroll = Mathf.Max(0f, _contentHeight - viewH);
        if (maxScroll <= 0f) return;
        if (!VoiceUiKit.Contains(_shell.PaneClip)) return;
        float dy = Input.mouseScrollDelta.y;
        if (dy > -0.01f && dy < 0.01f) return;
        _scroll = Mathf.Clamp(_scroll - dy * RowH, 0f, maxScroll);
        _shell.PaneRoot.anchoredPosition = new Vector2(0f, _scroll);
    }

    private static void HandleInput()
    {
        if (!Input.GetMouseButton(0))
        {
            if (_activeRow != null) { _activeRow.OnMouseUp(); _activeRow = null; }
            return;
        }
        if (Input.GetMouseButtonDown(0))
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].OnMouseDown();
            _activeRow = FindDragging();
        }
        else if (_activeRow != null)
        {
            _activeRow.OnMouseDrag();
        }
    }

    private static VoiceUiKit.PlayerVolumeRow? FindDragging()
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].IsDragging) return _rows[i];
        return null;
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

    private static float ApplyVolume(PlayerEntry entry, float v, bool persist)
    {
        v = Mathf.Clamp(v, VMin, VMax);
        if (persist) SaveVolume(entry, v);
        VoiceChatRoom.Current?.TrySetRemoteVolume(entry.PlayerId, entry.Name, v);
        return v;
    }

    private record PlayerEntry(byte PlayerId, string Name);

    private static float GetSavedVolume(PlayerEntry entry)
        => TryGetSavedVolume(entry.Name, out float volume) ? volume : 1f;

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
        var settings = VoiceSettings.Instance;
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
        var settings = VoiceSettings.Instance;
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
        if (VoiceChatRoom.Current != null)
            foreach (var c in VoiceChatRoom.Current.RemoteOverlayStates)
            {
                if (c.PlayerId == byte.MaxValue) continue;
                if (!seen.Add(c.PlayerId)) continue;
                list.Add(new PlayerEntry(c.PlayerId, c.PlayerName));
            }

        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null || pc.Data == null) continue;
            if (pc == PlayerControl.LocalPlayer) continue;
            if (!seen.Add(pc.PlayerId)) continue;
            list.Add(new PlayerEntry(pc.PlayerId, pc.Data.PlayerName));
        }

        var snapshot = VoiceChatRoom.Current?.CurrentSnapshot;
        if (snapshot != null)
            list.RemoveAll(e => !VoiceOverlayState.IsLiveRemoteSpeaker(e.PlayerId, snapshot));

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
}
