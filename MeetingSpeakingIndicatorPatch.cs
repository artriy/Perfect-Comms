using HarmonyLib;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using UnityEngine.Rendering;
using System;
using System.Collections.Generic;
using MiraAPI.LocalSettings;

namespace VoiceChatPlugin;

[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
public static class MeetingSpeakingIndicatorPatch
{
    private const float BorderScale   = 0.72f;
    private const float LevelSmoothSpeed = 12f;
    private const float FadeInSpeed = 7f;
    private const float FadeOutSpeed = 5f;
    private static readonly Vector3 CardGlowScale = new(0.92f, 0.66f, 1f);
    private static readonly Dictionary<byte, SpriteRenderer> _borders = new();
    private static readonly Dictionary<byte, SpriteRenderer> _cardGlows = new();
    private static readonly Dictionary<byte, float> _speakingLevels = new();
    private static readonly Dictionary<byte, SmoothVisualState> _visualStates = new();
    private static readonly Dictionary<byte, Color> _playerColors = new();
    private static readonly HashSet<byte> _controlledHighlights = new();
    private static Sprite? _ringSprite;
    private static Sprite? _cardGlowSprite;
    private static DateTime _lastUpdateLogUtc;
    private static int _updateCalls;

    public static void Postfix(MeetingHud __instance)
        => UpdateIndicators(VoiceOverlayState.Current(VoiceChatRoom.Current), __instance);

    internal static void UpdateIndicators(VoiceOverlayState overlay)
    {
        var meetingHud = MeetingHud.Instance;
        if (meetingHud == null)
        {
            DisableAll();
            return;
        }

        UpdateIndicators(overlay, meetingHud);
    }

    internal static void ClearLocalIndicator()
    {
        var local = PlayerControl.LocalPlayer;
        if (local != null)
            ClearPlayerIndicator(local.PlayerId);
    }

    private static void ClearPlayerIndicator(byte playerId)
    {
        _speakingLevels.Remove(playerId);
        _visualStates.Remove(playerId);
        if (_borders.TryGetValue(playerId, out var border) && border != null)
            border.enabled = false;
        if (_cardGlows.TryGetValue(playerId, out var glow) && glow != null)
            glow.enabled = false;

        var meetingHud = MeetingHud.Instance;
        if (meetingHud?.playerStates == null) return;
        foreach (var state in meetingHud.playerStates)
        {
            if (state == null || state.TargetPlayerId != playerId) continue;
            ClearBuiltInHighlight(state, playerId, true);
        }
    }

    private static void UpdateIndicators(VoiceOverlayState overlay, MeetingHud meetingHud)
    {
        _updateCalls++;
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        bool debugHud = settings?.DebugVoiceStats.Value == true;
        bool logNow = debugHud && ShouldLog(ref _lastUpdateLogUtc);
        if (settings != null && !settings.MeetingSpeakingOverlay.Value)
        {
            DisableAll();
            if (logNow)
                LogHud("hud.meeting.update", $"disabled calls={Take(ref _updateCalls)} {DescribeHudRoot(meetingHud)} {DescribeOverlay(overlay)}");
            return;
        }

        if (meetingHud.playerStates == null)
        {
            DisableAll();
            if (logNow)
                LogHud("hud.meeting.update", $"no-playerStates calls={Take(ref _updateCalls)} {DescribeHudRoot(meetingHud)} {DescribeOverlay(overlay)}");
            return;
        }

        _speakingLevels.Clear();
        foreach (var remote in overlay.RemotePlayers)
        {
            if (remote.IsSpeaking && remote.IsAudible)
                _speakingLevels[remote.PlayerId] = remote.Level;
        }

        if (PlayerControl.LocalPlayer != null && overlay.Local.IsSpeaking)
        {
            byte lid = PlayerControl.LocalPlayer.PlayerId;
            if (lid != byte.MaxValue) _speakingLevels[lid] = overlay.Local.Level;
        }

        foreach (var state in meetingHud.playerStates)
        {
            if (state == null) continue;

            byte pid       = state.TargetPlayerId;
            bool isTalking = _speakingLevels.TryGetValue(pid, out float level);
            var visual = GetVisualState(pid);
            visual.TargetLevel = isTalking ? level : 0f;
            visual.SmoothedLevel = Mathf.Lerp(
                visual.SmoothedLevel,
                NormalizeVoiceLevel(visual.TargetLevel),
                Mathf.Clamp01(Time.deltaTime * LevelSmoothSpeed));
            visual.Visibility = Mathf.MoveTowards(
                visual.Visibility,
                isTalking ? 1f : 0f,
                Time.deltaTime * (isTalking ? FadeInSpeed : FadeOutSpeed));

            if (!_borders.TryGetValue(pid, out var borderSr) || borderSr == null)
                borderSr = CreateBorder(meetingHud, state, pid);
            if (!_cardGlows.TryGetValue(pid, out var cardGlowSr) || cardGlowSr == null)
                cardGlowSr = CreateCardGlow(meetingHud, state, pid);

            if (borderSr == null && cardGlowSr == null) continue;

            SyncOverlayTransforms(meetingHud, state, borderSr, cardGlowSr);

            if (visual.Visibility > 0.01f)
            {
                Color playerColor = GetPlayerColor(pid);
                float brightness = Mathf.SmoothStep(0f, 1f, visual.SmoothedLevel);
                playerColor.a = Mathf.Lerp(0.28f, 1f, brightness) * visual.Visibility;
                if (borderSr != null)
                {
                    borderSr.color    = playerColor;
                    borderSr.enabled  = true;
                }

                if (cardGlowSr != null)
                {
                    var glowColor = playerColor;
                    glowColor.a = Mathf.Lerp(0.20f, 0.88f, brightness) * visual.Visibility;
                    cardGlowSr.color = glowColor;
                    cardGlowSr.enabled = true;
                }

                ApplyBuiltInHighlight(state, pid, playerColor, brightness, visual.Visibility);

            }
            else
            {
                if (borderSr != null) borderSr.enabled = false;
                if (cardGlowSr != null) cardGlowSr.enabled = false;
                ClearBuiltInHighlight(state, pid, true);
            }
        }

        if (logNow)
        {
            int states = CountPlayerStates(meetingHud);
            LogHud("hud.meeting.update",
                $"calls={Take(ref _updateCalls)} {DescribeHudRoot(meetingHud)} states={states} " +
                $"speakingLevels={DescribeSpeakingLevels()} borders={_borders.Count} cardGlows={_cardGlows.Count} {DescribeOverlay(overlay)}");

            int index = 0;
            foreach (var state in meetingHud.playerStates)
            {
                LogHud("hud.meeting.state", DescribeVoteArea(index, state));
                index++;
            }
        }
    }

    private static void DisableAll()
    {
        foreach (var sr in _borders.Values)
            if (sr != null) sr.enabled = false;
        foreach (var sr in _cardGlows.Values)
            if (sr != null) sr.enabled = false;
        ClearAllBuiltInHighlights();
        _controlledHighlights.Clear();
        _visualStates.Clear();
    }

    private static void ClearAllBuiltInHighlights()
    {
        var meetingHud = MeetingHud.Instance;
        if (meetingHud?.playerStates == null) return;
        foreach (var state in meetingHud.playerStates)
        {
            if (state != null)
                ClearBuiltInHighlight(state, state.TargetPlayerId, true);
        }
    }

    private static void SyncOverlayTransforms(
        MeetingHud meetingHud,
        PlayerVoteArea state,
        SpriteRenderer? border,
        SpriteRenderer? glow)
    {
        if (border != null)
        {
            var root = ResolveMeetingOverlayRoot(meetingHud);
            if (border.transform.parent != root)
                border.transform.SetParent(root, false);
            border.transform.localPosition = ToOverlayLocal(root, GetIconWorldPosition(state), -100f);
            border.transform.localScale = Vector3.one * BorderScale;
            ApplySortingGroup(border.gameObject, VCSorting.Ring);
            VCOverlayCamera.EnsureOnTop(border.gameObject);
            border.transform.SetAsLastSibling();
        }

        if (glow != null)
        {
            if (state.Background != null)
            {
                if (glow.transform.parent != state.Background.transform)
                    glow.transform.SetParent(state.Background.transform, false);
                glow.transform.localPosition = new Vector3(0f, 0f, -0.05f);
                glow.transform.localScale = new Vector3(1.10f, 1.28f, 1f);
            }
            else
            {
                var root = ResolveMeetingOverlayRoot(meetingHud);
                if (glow.transform.parent != root)
                    glow.transform.SetParent(root, false);
                glow.transform.localPosition = ToOverlayLocal(root, GetCardWorldPosition(state), -101f);
                glow.transform.localScale = CardGlowScale;
                ApplySortingGroup(glow.gameObject, VCSorting.Glow);
                VCOverlayCamera.EnsureOnTop(glow.gameObject);
                glow.transform.SetAsLastSibling();
            }
        }
    }

    private static void ApplySortingGroup(GameObject go, int order)
    {
        var group = go.GetComponent<SortingGroup>() ?? go.AddComponent<SortingGroup>();
        group.sortingLayerName = VCSorting.Layer;
        group.sortingOrder = order;
    }

    private static Transform ResolveMeetingOverlayRoot(MeetingHud meetingHud)
    {
        var meetingParent = meetingHud.transform.parent;
        if (meetingParent != null && meetingParent.gameObject.activeInHierarchy)
            return meetingParent;
        return meetingHud.transform;
    }

    private static Vector3 ToOverlayLocal(Transform root, Vector3 worldPosition, float z)
    {
        var local = root.InverseTransformPoint(worldPosition);
        return new Vector3(local.x, local.y, z);
    }

    private static Vector3 GetIconWorldPosition(PlayerVoteArea state)
    {
        var icon = state.PlayerIcon;
        return icon != null ? icon.transform.position : state.transform.position;
    }

    private static Vector3 GetCardWorldPosition(PlayerVoteArea state)
        => state.Background != null ? state.Background.transform.position : state.transform.position;

    private static SmoothVisualState GetVisualState(byte playerId)
    {
        if (!_visualStates.TryGetValue(playerId, out var visual))
        {
            visual = new SmoothVisualState();
            _visualStates[playerId] = visual;
        }

        return visual;
    }

    private static void ApplyBuiltInHighlight(PlayerVoteArea state, byte playerId, Color color, float brightness, float visibility)
    {
        var highlight = state.HighlightedFX;
        if (highlight == null) return;

        var highlightColor = color;
        highlightColor.a = Mathf.Lerp(0.20f, 0.95f, brightness) * visibility;
        highlight.color = highlightColor;
        highlight.sortingLayerName = VCSorting.Layer;
        highlight.sortingOrder = VCSorting.Ring;
        highlight.maskInteraction = SpriteMaskInteraction.None;
        highlight.enabled = visibility > 0.01f;
        _controlledHighlights.Add(playerId);
    }

    private static void ClearBuiltInHighlight(PlayerVoteArea state, byte playerId, bool force = false)
    {
        if (!force && !_controlledHighlights.Remove(playerId)) return;
        _controlledHighlights.Remove(playerId);
        if (state.HighlightedFX != null)
            state.HighlightedFX.enabled = false;
    }

    private static Vector3 GetCardLocalPosition(PlayerVoteArea state)
    {
        Vector3 local = state.transform.localPosition;
        return new Vector3(local.x, local.y, local.z - 0.04f);
    }

    private static Vector3 GetIconLocalPosition(PlayerVoteArea state)
    {
        Vector3 local = state.transform.localPosition;
        var icon = state.PlayerIcon;
        if (icon != null)
            local += icon.transform.localPosition;
        return new Vector3(local.x, local.y, local.z - 0.08f);
    }

    private static bool TryGetScreenRect(PlayerVoteArea state, out Rect rect)
    {
        var camera = Camera.main;
        if (camera == null)
        {
            rect = default;
            return false;
        }

        if (!TryGetWorldBounds(state, out var bounds))
        {
            Vector3 screen = camera.WorldToScreenPoint(state.transform.position);
            rect = new Rect(screen.x - 190f, Screen.height - screen.y - 34f, 380f, 68f);
            return true;
        }

        Vector3 min = camera.WorldToScreenPoint(bounds.min);
        Vector3 max = camera.WorldToScreenPoint(bounds.max);

        float xMin = Mathf.Min(min.x, max.x);
        float xMax = Mathf.Max(min.x, max.x);
        float yMin = Screen.height - Mathf.Max(min.y, max.y);
        float yMax = Screen.height - Mathf.Min(min.y, max.y);

        rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        if (rect.width < 20f || rect.height < 20f)
            rect = new Rect(rect.x - 150f, rect.y - 28f, 300f, 56f);

        rect.xMin -= 10f;
        rect.xMax += 10f;
        rect.yMin -= 6f;
        rect.yMax += 6f;
        return rect.width > 0f && rect.height > 0f;
    }

    private static bool TryGetWorldBounds(PlayerVoteArea state, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        foreach (var renderer in state.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null) continue;
            if (!hasBounds)
            {
                bounds = renderer.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return hasBounds;
    }

    private static bool ShouldLog(ref DateTime lastUtc)
    {
        var now = DateTime.UtcNow;
        if ((now - lastUtc).TotalSeconds < 1.0)
            return false;

        lastUtc = now;
        return true;
    }

    private static int Take(ref int value)
    {
        int result = value;
        value = 0;
        return result;
    }

    private static void LogHud(string category, string message)
    {
        string full = $"{message}";
        VoiceDiagnostics.Log(category, full);
        VoiceChatPluginMain.Logger.LogInfo($"[VC HUD] {category} {full}");
    }

    private static bool ShouldDebugLogHud()
        => LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance?.DebugVoiceStats.Value == true;

    private static string DescribeHudRoot(MeetingHud meetingHud)
        => $"scene={CurrentSceneName()} hudActive={meetingHud.gameObject.activeInHierarchy} " +
           $"hudPos={DescribeVector(meetingHud.transform.position)} hudLocal={DescribeVector(meetingHud.transform.localPosition)} " +
           $"stateCount={CountPlayerStates(meetingHud)} camera={DescribeCamera()} screen={Screen.width}x{Screen.height}";

    private static string DescribeOverlay(VoiceOverlayState overlay)
    {
        int remoteCount = 0;
        int remoteSpeaking = 0;
        int remoteAudible = 0;
        string remote = "";
        foreach (var r in overlay.RemotePlayers)
        {
            remoteCount++;
            if (r.IsSpeaking) remoteSpeaking++;
            if (r.IsAudible) remoteAudible++;
            if (remote.Length < 700)
                remote += $" pid={r.PlayerId}:s={r.IsSpeaking}:a={r.IsAudible}:lvl={r.Level:0.000}:reason={r.Reason}";
        }

        return $"localSpeak={overlay.Local.IsSpeaking} localLevel={overlay.Local.Level:0.000} localMuted={overlay.Local.IsMuted} " +
               $"remoteCount={remoteCount} remoteSpeaking={remoteSpeaking} remoteAudible={remoteAudible} remotes=[{remote.Trim()}]";
    }

    private static int CountPlayerStates(MeetingHud meetingHud)
    {
        if (meetingHud.playerStates == null) return -1;
        int count = 0;
        foreach (var _ in meetingHud.playerStates)
            count++;
        return count;
    }

    private static string DescribeSpeakingLevels()
    {
        if (_speakingLevels.Count == 0) return "none";
        string result = "";
        foreach (var kv in _speakingLevels)
            result += $"{kv.Key}:{kv.Value:0.000};";
        return result;
    }

    private static string DescribeVoteArea(int index, PlayerVoteArea? state)
    {
        if (state == null) return $"index={index} state=null";

        byte pid = state.TargetPlayerId;
        bool talking = _speakingLevels.TryGetValue(pid, out float level);
        _borders.TryGetValue(pid, out var border);
        _cardGlows.TryGetValue(pid, out var glow);
        bool hasBounds = TryGetWorldBounds(state, out var bounds);
        bool hasRect = TryGetScreenRect(state, out var rect);
        int rendererCount = CountRenderers(state);

        return $"index={index} player={pid} talking={talking} level={level:0.000} active={state.gameObject.activeInHierarchy} " +
               $"pos={DescribeVector(state.transform.position)} local={DescribeVector(state.transform.localPosition)} renderers={rendererCount} " +
               $"bounds={(hasBounds ? DescribeBounds(bounds) : "none")} screenRect={(hasRect ? DescribeRect(rect) : "none")} " +
               $"background={DescribeSpriteRenderer(state.Background)} playerIcon={DescribePlayerIcon(state.PlayerIcon)} " +
               $"border={DescribeSpriteRenderer(border)} glow={DescribeSpriteRenderer(glow)}";
    }

    private static int CountRenderers(PlayerVoteArea state)
    {
        int count = 0;
        foreach (var renderer in state.GetComponentsInChildren<Renderer>(true))
            if (renderer != null) count++;
        return count;
    }

    private static string DescribePlayerIcon(PoolablePlayer? icon)
    {
        if (icon == null) return "null";
        var renderer = icon.GetComponentInChildren<SpriteRenderer>(true);
        return $"active={icon.gameObject.activeInHierarchy} pos={DescribeVector(icon.transform.position)} local={DescribeVector(icon.transform.localPosition)} renderer={DescribeSpriteRenderer(renderer)}";
    }

    private static string DescribeSpriteRenderer(SpriteRenderer? renderer)
    {
        if (renderer == null) return "null";
        return $"name={renderer.gameObject.name} active={renderer.gameObject.activeInHierarchy} enabled={renderer.enabled} " +
               $"layer={renderer.sortingLayerName} order={renderer.sortingOrder} color={DescribeColor(renderer.color)} " +
               $"pos={DescribeVector(renderer.transform.position)} local={DescribeVector(renderer.transform.localPosition)} " +
               $"bounds={DescribeBounds(renderer.bounds)}";
    }

    private static string DescribeCamera()
    {
        var camera = Camera.main;
        return camera == null
            ? "camera=null"
            : $"camera={camera.name} pos={DescribeVector(camera.transform.position)} ortho={camera.orthographic} size={camera.orthographicSize:0.000} depth={camera.depth:0.000}";
    }

    private static string CurrentSceneName()
    {
        try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
        catch { return "scene-error"; }
    }

    private static string ParentName(Transform transform)
        => transform.parent != null ? transform.parent.name : "none";

    private static string DescribeVector(Vector3 value)
        => $"({value.x:0.000},{value.y:0.000},{value.z:0.000})";

    private static string DescribeRect(Rect rect)
        => $"({rect.x:0.0},{rect.y:0.0},{rect.width:0.0},{rect.height:0.0})";

    private static string DescribeBounds(Bounds bounds)
        => $"center={DescribeVector(bounds.center)} size={DescribeVector(bounds.size)} min={DescribeVector(bounds.min)} max={DescribeVector(bounds.max)}";

    private static string DescribeColor(Color color)
        => $"({color.r:0.00},{color.g:0.00},{color.b:0.00},{color.a:0.00})";

    private static float NormalizeVoiceLevel(float level)
    {
        if (level <= 0.003f) return 0f;
        float normalized = Mathf.InverseLerp(0.003f, 0.55f, level);
        return Mathf.Pow(Mathf.Clamp01(normalized), 0.65f);
    }

    private static SpriteRenderer? CreateBorder(MeetingHud meetingHud, PlayerVoteArea state, byte playerId)
    {
        try
        {
            var go = new GameObject("VC_GlowBorder");
            var root = ResolveMeetingOverlayRoot(meetingHud);
            go.transform.SetParent(root, false);
            go.transform.localPosition = ToOverlayLocal(root, GetIconWorldPosition(state), -100f);
            go.transform.localScale    = Vector3.one * BorderScale;
            ApplySortingGroup(go, VCSorting.Ring);
            VCOverlayCamera.EnsureOnTop(go);
            go.transform.SetAsLastSibling();

            var sr         = go.AddComponent<SpriteRenderer>();
            sr.sprite           = GetRingSprite();
            sr.sortingLayerName = VCSorting.Layer;
            sr.sortingOrder     = VCSorting.Ring;
            sr.maskInteraction  = SpriteMaskInteraction.None;
            sr.enabled          = false;

            _borders[playerId] = sr;
            if (ShouldDebugLogHud())
                LogHud("hud.meeting.create", $"type=border player={playerId} parent={ParentName(go.transform)} localPos={DescribeVector(go.transform.localPosition)} scale={DescribeVector(go.transform.localScale)} renderer={DescribeSpriteRenderer(sr)}");
            return sr;
        }
        catch (Exception ex)
        {
            if (ShouldDebugLogHud())
                LogHud("hud.meeting.create.error", $"type=border player={playerId} error={ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }

    private static SpriteRenderer? CreateCardGlow(MeetingHud meetingHud, PlayerVoteArea state, byte playerId)
    {
        try
        {
            var go = new GameObject("VC_CardSpeakingGlow");
            var background = state.Background;
            bool hasBackground = background != null && background.sprite != null;
            var root = ResolveMeetingOverlayRoot(meetingHud);
            go.transform.SetParent(hasBackground ? background!.transform : root, false);
            go.transform.localPosition = hasBackground ? new Vector3(0f, 0f, -0.05f) : ToOverlayLocal(root, GetCardWorldPosition(state), -101f);
            go.transform.localScale = hasBackground ? new Vector3(1.10f, 1.28f, 1f) : CardGlowScale;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = hasBackground ? background!.sprite : GetCardGlowSprite();
            sr.sortingLayerID = hasBackground ? background!.sortingLayerID : SortingLayer.NameToID(VCSorting.Layer);
            sr.sortingOrder = VCSorting.Glow;
            sr.maskInteraction = SpriteMaskInteraction.None;
            sr.enabled = false;

            _cardGlows[playerId] = sr;
            if (ShouldDebugLogHud())
                LogHud("hud.meeting.create", $"type=cardGlow player={playerId} hasBackground={hasBackground} parent={ParentName(go.transform)} localPos={DescribeVector(go.transform.localPosition)} scale={DescribeVector(go.transform.localScale)} renderer={DescribeSpriteRenderer(sr)}");
            return sr;
        }
        catch (Exception ex)
        {
            if (ShouldDebugLogHud())
                LogHud("hud.meeting.create.error", $"type=cardGlow player={playerId} error={ex.GetType().Name}:{ex.Message}");
            return null;
        }
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
    private static class DestroyPatch
    {
        private static void Postfix()
        {
            _borders.Clear();
            _cardGlows.Clear();
            _speakingLevels.Clear();
            _visualStates.Clear();
            _playerColors.Clear();
            _controlledHighlights.Clear();
        }
    }

    private static Sprite GetRingSprite()
    {
        if (_ringSprite != null) return _ringSprite;

        const int   Size      = 128;
        const float Inner     = 0.80f; 
        const float Feather   = 1.8f; 

        var   tex     = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        float centre  = Size * 0.5f;
        float outerR  = centre - 1f;
        float innerR  = outerR * Inner;

        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float dx = x - centre + 0.5f;
            float dy = y - centre + 0.5f;
            float d  = Mathf.Sqrt(dx * dx + dy * dy);
            float outerA = Mathf.Clamp01((outerR - d) / Feather);
            float innerA = Mathf.Clamp01((d - innerR) / Feather);
            float a      = outerA * innerA;

            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();

        _ringSprite = Sprite.Create(
            tex,
            new Rect(0, 0, Size, Size),
            new Vector2(0.5f, 0.5f),
            Size);
        _ringSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _ringSprite;
    }

    private static Sprite GetCardGlowSprite()
    {
        if (_cardGlowSprite != null) return _cardGlowSprite;

        const int Width = 192;
        const int Height = 64;
        const float Feather = 14f;

        var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            float edge = Mathf.Min(Mathf.Min(x, Width - 1 - x), Mathf.Min(y, Height - 1 - y));
            float edgeGlow = Mathf.Pow(1f - Mathf.Clamp01(edge / Feather), 1.15f);
            float fill = 0.24f;
            float a = Mathf.Clamp01(fill + edgeGlow * 0.62f);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }

        tex.Apply();
        _cardGlowSprite = Sprite.Create(
            tex,
            new Rect(0, 0, Width, Height),
            new Vector2(0.5f, 0.5f),
            Height);
        _cardGlowSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _cardGlowSprite;
    }

    private static Color GetPlayerColor(byte playerId)
    {
        if (_playerColors.TryGetValue(playerId, out var cached))
            return cached;

        foreach (var pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null || pc.Data == null) continue;
            if (pc.PlayerId != playerId) continue;

            int colorId = pc.Data.DefaultOutfit.ColorId;
            if (colorId >= 0 && colorId < Palette.PlayerColors.Length)
            {
                var color = (Color)Palette.PlayerColors[colorId];
                _playerColors[playerId] = color;
                return color;
            }
        }

        var fallback = new Color(0.18f, 0.80f, 0.44f, 1f); // BCL fallback green
        _playerColors[playerId] = fallback;
        return fallback;
    }

    private sealed class SmoothVisualState
    {
        public float TargetLevel;
        public float SmoothedLevel;
        public float Visibility;
    }
}
