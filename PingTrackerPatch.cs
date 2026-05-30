using HarmonyLib;
using TMPro;
using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using MiraAPI.LocalSettings;

namespace VoiceChatPlugin;

internal static class VCSorting
{
    public const string Layer = "UI";
    public const int    Glow  = 32765;
    public const int    Base  = 32766;
    public const int    Ring  = 32767;
    public const int    Text  = 32765;
}

internal static class VCOverlayCamera
{
    internal const int OverlayLayer = 31;
    internal static int OverlayLayerMask => 1 << OverlayLayer;
    private static Camera? _camera;

    public static void Sync()
        => SyncCamera();

    public static void EnsureOnTop(GameObject? go)
    {
        if (go == null) return;
        SyncCamera();
        SetLayerRecursive(go.transform);
    }

    private static void SyncCamera()
    {
        var main = Camera.main;
        if (main == null) return;

        if (_camera == null)
        {
            var go = new GameObject("VC_OverlayCamera");
            Object.DontDestroyOnLoad(go);
            _camera = go.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.Depth;
            _camera.cullingMask = OverlayLayerMask;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;
        }

        _camera.enabled = true;
        _camera.orthographic = main.orthographic;
        _camera.orthographicSize = main.orthographicSize;
        _camera.fieldOfView = main.fieldOfView;
        _camera.nearClipPlane = main.nearClipPlane;
        _camera.farClipPlane = main.farClipPlane;
        _camera.depth = main.depth + 1000f;
        _camera.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
    }

    private static void SetLayerRecursive(Transform root)
    {
        root.gameObject.layer = OverlayLayer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursive(root.GetChild(i));
    }
}

[HarmonyPatch(typeof(PingTracker), nameof(PingTracker.Update))]
public static class PingTrackerPatch
{
    private const float LabelSize   = 0.95f;
    private const float SlotWidth   = 0.52f;
    private const float SlotHeight  = 0.58f;
    private const float LabelOffset = 0.34f;
    private const float BottomNameLift = 0.12f;
    private const float RingScale   = 0.48f;
    private const float LevelSmoothSpeed = 12f;
    private const float FadeInSpeed = 7f;
    private const float FadeOutSpeed = 5f;
    private const float StaleSlotTimeoutSeconds = 2f;
    private static GameObject?       _barRoot;
    private static AspectPosition?   _barAspect;
    private static bool              _layoutVertical;
    private static bool              _layoutAnchoredBottom;
    private static SpeakingBarPosition _barPosition = SpeakingBarPosition.TopRight;
    private static readonly Dictionary<byte, SpeakerSlot> _slots = new();
    private static readonly HashSet<byte> _activeSpeakerIds = new();
    private static readonly Dictionary<byte, float> _activeSpeakerLevels = new();
    private static readonly List<byte> _fadedSlotIds = new();
    // Per-frame O(1) player lookup, rebuilt once per Postfix from a single AllPlayerControls pass.
    private static readonly Dictionary<byte, PlayerControl> _playerLookup = new();
    private static bool _layoutDirty;
    // Set when the slot set or an icon changes; gates the expensive per-slot sorting re-stamp.
    private static bool _sortingDirty;

    public static void ApplySpeakingBarPosition(SpeakingBarPosition pos)
    {
        _barPosition    = pos;
        _layoutVertical = pos is SpeakingBarPosition.TopLeft
            or SpeakingBarPosition.TopRight
            or SpeakingBarPosition.BottomLeft
            or SpeakingBarPosition.BottomRight
            or SpeakingBarPosition.MiddleLeft
            or SpeakingBarPosition.MiddleRight;
        _layoutAnchoredBottom = pos is SpeakingBarPosition.BottomLeft
            or SpeakingBarPosition.BottomMiddle
            or SpeakingBarPosition.BottomRight;
        if (_barAspect == null) return;
        ApplyPositionToAspect(_barAspect, pos);
        _barAspect.AdjustPosition();
        _layoutDirty = true;
        LayoutSlotsIfDirty();
    }


    static void Postfix(PingTracker __instance)
    {
        if (__instance?.text == null) return;

        EnsureBar(__instance);
        if (_barRoot == null) return;

        try
        {
            RebuildPlayerLookup();
            TrackAvatarIdlePoses();

            var room = VoiceChatRoom.Current;
            var overlay = VoiceOverlayState.Current(room);
            _activeSpeakerIds.Clear();
            _activeSpeakerLevels.Clear();

            foreach (var remote in overlay.RemotePlayers)
            {
                if (remote.IsSpeaking && remote.IsAudible)
                {
                    _activeSpeakerIds.Add(remote.PlayerId);
                    _activeSpeakerLevels[remote.PlayerId] = remote.Level;
                }
            }

            if (PlayerControl.LocalPlayer && overlay.Local.IsSpeaking)
            {
                byte lid = PlayerControl.LocalPlayer.PlayerId;
                if (lid != byte.MaxValue)
                {
                    _activeSpeakerIds.Add(lid);
                    _activeSpeakerLevels[lid] = overlay.Local.Level;
                }
            }

            if (MeetingHud.Instance == null)
                MeetingSpeakingIndicatorPatch.UpdateIndicators(overlay);

            foreach (var kv in _slots)
            {
                kv.Value.IsSpeaking = false;
                kv.Value.TargetLevel = 0f;
            }

            // Add / rebuild slots for speaking players
            foreach (byte id in _activeSpeakerIds)
            {
                var player = FindPlayer(id);
                var liveFp = GetFingerprint(id);
                float level = _activeSpeakerLevels.TryGetValue(id, out var currentLevel) ? currentLevel : 0f;
                if (_slots.TryGetValue(id, out var slot))
                {
                    slot.LastActiveTime = Time.time;
                    if (player != null) slot.LastPlayerSeenTime = Time.time;
                    if (slot.Fingerprint != liveFp)
                    {
                        slot.TargetLevel = level;
                        slot.IsSpeaking = true;
                        slot.PlayerColor = GetPaletteColor(player);
                        if (slot.PendingFingerprint != liveFp)
                        {
                            slot.PendingFingerprint = liveFp;
                            UpdateSlotLabel(slot, player);
                        }
                        TryCreateSlotIcon(id, slot, replaceExisting: true);
                        continue;
                    }
                    else
                    {
                        slot.TargetLevel = level;
                        slot.IsSpeaking = true;
                        if (slot.IconGO == null)
                        {
                            TryCreateSlotIcon(id, slot);
                        }
                        else if (!slot.HasCachedPoseIcon && player != null && CrewmateAvatarRenderer.HasCachedCosmeticPose(id, player))
                        {
                            // Upgrade body-only icon to cosmetics in place — no destroy/recreate pop.
                            if (CrewmateAvatarRenderer.TryUpgradeWithCachedPose(slot.IconGO, id, player))
                            {
                                slot.HasCachedPoseIcon = true;
                                _layoutDirty = true;
                                _sortingDirty = true;
                            }
                        }
                        continue;
                    }
                }
                AddSlot(id, level);
            }

            UpdateSlotRings();

            _fadedSlotIds.Clear();
            foreach (var kv in _slots)
                if ((!kv.Value.IsSpeaking && kv.Value.Visibility <= 0.01f) || ShouldForceRemoveSlot(kv.Key, kv.Value))
                    _fadedSlotIds.Add(kv.Key);
            foreach (var id in _fadedSlotIds) RemoveSlot(id);

            LayoutSlotsIfDirty();

            _barRoot.SetActive(_slots.Count > 0);
            KeepSpeakingBarOnTop(__instance);
        }
        catch (System.Exception ex)
        {
            LogOverlayError("PingTracker overlay update", ex);
        }
    }

    private static float _lastOverlayErrorLog = -999f;

    private static void LogOverlayError(string where, System.Exception ex)
    {
        if (Time.time - _lastOverlayErrorLog < 5f) return;
        _lastOverlayErrorLog = Time.time;
        VoiceDiagnostics.DebugError($"[VC] {where} failed: {ex.Message}");
    }

    private static void EnsureBar(PingTracker template)
    {
        if (_barRoot != null) return;

        _barRoot   = new GameObject("VC_SpeakingBar");
        _barRoot.transform.SetParent(ResolveOverlayRoot(template), false);
        _barAspect = _barRoot.AddComponent<AspectPosition>();
        ApplySortingGroup(_barRoot, VCSorting.Ring);

        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings != null)
        {
            _barPosition    = settings.SpeakingBarPosition.Value;
            _layoutVertical = _barPosition is SpeakingBarPosition.TopLeft
                or SpeakingBarPosition.TopRight
                or SpeakingBarPosition.BottomLeft
                or SpeakingBarPosition.BottomRight
                or SpeakingBarPosition.MiddleLeft
                or SpeakingBarPosition.MiddleRight;
            _layoutAnchoredBottom = _barPosition is SpeakingBarPosition.BottomLeft
                or SpeakingBarPosition.BottomMiddle
                or SpeakingBarPosition.BottomRight;
        }

        ApplyPositionToAspect(_barAspect, _barPosition);
        _barAspect.AdjustPosition();
        KeepSpeakingBarOnTop(template);
        _barRoot.SetActive(false);
    }

    private static Transform ResolveOverlayRoot(PingTracker? template = null)
    {
        var meeting = MeetingHud.Instance;
        if (meeting != null)
        {
            var meetingParent = meeting.transform.parent;
            if (meetingParent != null && meetingParent.gameObject.activeInHierarchy)
                return meetingParent;
            return meeting.transform;
        }

        var hud = HudManager.Instance;
        if (hud != null)
            return hud.transform.parent != null ? hud.transform.parent : hud.transform;

        if (template?.transform.parent != null) return template.transform.parent;
        if (template != null) return template.transform;
        return _barRoot != null && _barRoot.transform.parent != null ? _barRoot.transform.parent : _barRoot!.transform;
    }

    private static void KeepSpeakingBarOnTop(PingTracker? template = null)
    {
        if (_barRoot == null) return;

        var root = ResolveOverlayRoot(template);
        if (_barRoot.transform.parent != root)
        {
            _barRoot.transform.SetParent(root, false);
            _barAspect?.AdjustPosition();
        }

        _barRoot.transform.SetAsLastSibling();
        var pos = _barRoot.transform.localPosition;
        _barRoot.transform.localPosition = new Vector3(pos.x, pos.y, -100f);
        ApplySortingGroup(_barRoot, VCSorting.Ring);
        VCOverlayCamera.Sync(); // must follow the main camera every frame
        // Re-stamp allocates via GetComponentsInChildren; only run on actual change.
        if (_sortingDirty)
        {
            ApplySpeakingBarSorting();
            _sortingDirty = false;
        }
    }

    private static void TrackAvatarIdlePoses()
    {
        try
        {
            var players = PlayerControl.AllPlayerControls;
            if (players == null) return;
            foreach (var pc in players)
                CrewmateAvatarRenderer.TrackIdlePose(pc);
        }
        catch
        {
            // Scene transitions can temporarily invalidate the player collection.
        }
    }

    private static void ApplySortingGroup(GameObject go, int order)
    {
        var group = go.GetComponent<SortingGroup>() ?? go.AddComponent<SortingGroup>();
        group.sortingLayerName = VCSorting.Layer;
        group.sortingOrder = order;
    }

    private static void ApplySpeakingBarSorting()
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.IconGO != null)
                ApplyTopSorting(slot.IconGO);
            if (slot.RingRenderer != null)
            {
                slot.RingRenderer.sortingLayerName = VCSorting.Layer;
                slot.RingRenderer.sortingOrder = VCSorting.Ring;
                slot.RingRenderer.maskInteraction = SpriteMaskInteraction.None;
            }
            if (slot.LabelTMP != null)
            {
                slot.LabelTMP.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
                slot.LabelTMP.sortingOrder = VCSorting.Text;
            }
        }
    }

    private static void ApplyPositionToAspect(AspectPosition asp, SpeakingBarPosition pos)
    {
        switch (pos)
        {
            case SpeakingBarPosition.TopMiddle:
                asp.Alignment        = AspectPosition.EdgeAlignments.Top;
                asp.DistanceFromEdge = new Vector3(0f, 0.25f, 0f);
                break;
            case SpeakingBarPosition.TopRight:
                asp.Alignment        = AspectPosition.EdgeAlignments.RightTop;
                asp.DistanceFromEdge = new Vector3(1.2f, 0.25f, 0f);
                break;
            case SpeakingBarPosition.BottomLeft:
                asp.Alignment        = AspectPosition.EdgeAlignments.LeftBottom;
                asp.DistanceFromEdge = new Vector3(0.60f, 0.35f, 0f);
                break;
            case SpeakingBarPosition.BottomMiddle:
                asp.Alignment        = AspectPosition.EdgeAlignments.Bottom;
                asp.DistanceFromEdge = new Vector3(0f, 0.35f, 0f);
                break;
            case SpeakingBarPosition.BottomRight:
                asp.Alignment        = AspectPosition.EdgeAlignments.RightBottom;
                asp.DistanceFromEdge = new Vector3(1.2f, 0.35f, 0f);
                break;
            case SpeakingBarPosition.MiddleLeft:
                asp.Alignment        = AspectPosition.EdgeAlignments.Left;
                asp.DistanceFromEdge = new Vector3(0.60f, 0f, 0f);
                break;
            case SpeakingBarPosition.MiddleRight:
                asp.Alignment        = AspectPosition.EdgeAlignments.Right;
                asp.DistanceFromEdge = new Vector3(1.2f, 0f, 0f);
                break;
            default: // TopLeft
                asp.Alignment        = AspectPosition.EdgeAlignments.LeftTop;
                asp.DistanceFromEdge = new Vector3(0.60f, 0.25f, 0f);
                break;
        }
    }

    private static void AddSlot(byte playerId, float voiceLevel)
    {
        if (_barRoot == null) return;

        var player = FindPlayer(playerId);
        var fp = GetFingerprint(playerId);
        var slot = new SpeakerSlot
        {
            Fingerprint = fp,
            PlayerColor = GetPaletteColor(player),
            Level = voiceLevel,
            TargetLevel = voiceLevel,
            SmoothedLevel = NormalizeVoiceLevel(voiceLevel),
            IsSpeaking = true,
            LastActiveTime = Time.time,
            LastPlayerSeenTime = player != null ? Time.time : 0f
        };

        TryCreateSlotIcon(playerId, slot);

        CreateRing(playerId, slot);

        var labelGO = new GameObject("VC_Label");
        labelGO.transform.SetParent(_barRoot.transform, false);
        var tmp = labelGO.AddComponent<TextMeshPro>();
        tmp.text               = string.Empty;
        tmp.fontSize           = LabelSize;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingLayerID     = SortingLayer.NameToID(VCSorting.Layer);
        tmp.sortingOrder       = VCSorting.Text;
        tmp.color              = Color.white;
        tmp.rectTransform.sizeDelta = new Vector2(1.4f, 0.45f);
        slot.LabelTMP = tmp;
        UpdateSlotLabel(slot, player);
        VCOverlayCamera.EnsureOnTop(labelGO);
        _slots[playerId] = slot;
        _layoutDirty = true;
        _sortingDirty = true;
    }

    private static bool TryCreateSlotIcon(byte playerId, SpeakerSlot slot, bool replaceExisting = false, bool requireCachedCosmetics = false)
    {
        if (_barRoot == null) return false;
        if (slot.IconGO != null && !replaceExisting) return true;

        var player = FindPlayer(playerId);
        if (player != null && CrewmateAvatarRenderer.TryCreate(playerId, player, _barRoot.transform, out var iconGO))
        {
            bool hasCachedCosmeticPose = CrewmateAvatarRenderer.HasCachedCosmeticPose(playerId, player);
            if (requireCachedCosmetics && !hasCachedCosmeticPose)
            {
                if (iconGO != null) Object.Destroy(iconGO);
                return false;
            }

            if (slot.IconGO != null) Object.Destroy(slot.IconGO);
            slot.IconGO = iconGO;
            slot.Fingerprint = GetFingerprint(playerId);
            slot.HasCachedPoseIcon = hasCachedCosmeticPose;
            slot.PendingFingerprint = default;
            _layoutDirty = true;
            _sortingDirty = true;
            return true;
        }
        return false;
    }

    private static void UpdateSlotLabel(SpeakerSlot slot, PlayerControl? player)
    {
        if (slot.LabelTMP == null) return;
        slot.LabelTMP.text = GetDisplayName(player);
    }

    private static void RemoveSlot(byte id)
    {
        if (!_slots.TryGetValue(id, out var slot)) return;
        if (slot.IconGO   != null) Object.Destroy(slot.IconGO);
        if (slot.RingGO   != null) Object.Destroy(slot.RingGO);
        if (slot.LabelTMP != null) Object.Destroy(slot.LabelTMP.gameObject);
        _slots.Remove(id);
        _layoutDirty = true;
        _sortingDirty = true;
    }

    private static bool ShouldForceRemoveSlot(byte id, SpeakerSlot slot)
    {
        if (slot.IsSpeaking) return false;
        if (Time.time - slot.LastActiveTime > StaleSlotTimeoutSeconds) return true;

        var player = FindPlayer(id);
        if (player != null)
        {
            slot.LastPlayerSeenTime = Time.time;
            return false;
        }

        return slot.LastPlayerSeenTime > 0f && Time.time - slot.LastPlayerSeenTime > StaleSlotTimeoutSeconds;
    }

    private static void CreateRing(byte playerId, SpeakerSlot slot)
    {
        if (_barRoot == null) return;

        var ringGO = new GameObject($"VC_SpeakingRing_{playerId}");
        ringGO.transform.SetParent(_barRoot.transform, false);
        ringGO.transform.localScale = Vector3.one * RingScale;

        var sr = ringGO.AddComponent<SpriteRenderer>();
        sr.sprite = CreateRingSprite();
        sr.sortingLayerName = VCSorting.Layer;
        sr.sortingOrder = VCSorting.Ring;
        sr.maskInteraction = SpriteMaskInteraction.None;
        VCOverlayCamera.EnsureOnTop(ringGO);

        slot.RingGO = ringGO;
        slot.RingRenderer = sr;
    }

    private static void UpdateSlotRings()
    {
        foreach (var kv in _slots)
        {
            var slot = kv.Value;
            if (slot.RingRenderer == null || slot.RingGO == null) continue;

            slot.SmoothedLevel = Mathf.Lerp(
                slot.SmoothedLevel,
                NormalizeVoiceLevel(slot.TargetLevel),
                Mathf.Clamp01(Time.deltaTime * LevelSmoothSpeed));
            slot.Visibility = Mathf.MoveTowards(
                slot.Visibility,
                slot.IsSpeaking ? 1f : 0f,
                Time.deltaTime * (slot.IsSpeaking ? FadeInSpeed : FadeOutSpeed));
            slot.Level = slot.TargetLevel;

            float brightness = Mathf.SmoothStep(0f, 1f, slot.SmoothedLevel);
            Color color = Color.Lerp(slot.PlayerColor, new Color(0.55f, 1f, 0f, 1f), 0.72f);
            color.a = Mathf.Lerp(0.22f, 0.92f, brightness) * slot.Visibility;
            slot.RingRenderer.color = color;
            slot.RingGO.transform.localScale = Vector3.one * RingScale;
            slot.RingRenderer.enabled = slot.Visibility > 0.01f;
        }
    }

    private static float NormalizeVoiceLevel(float level)
    {
        if (level <= 0.003f) return 0f;
        float normalized = Mathf.InverseLerp(0.003f, 0.55f, level);
        return Mathf.Pow(Mathf.Clamp01(normalized), 0.65f);
    }

    private static void LayoutSlots()
    {
        _layoutDirty = false;
        if (_layoutVertical)
        {
            float totalH = _slots.Count * SlotHeight;
            float startY = totalH * 0.5f - SlotHeight * 0.5f;
            int i = 0;
            foreach (var kv in _slots)
            {
                float y = _layoutAnchoredBottom ? i * SlotHeight + BottomNameLift : startY - i * SlotHeight;
                float labelY = y - LabelOffset;
                if (kv.Value.IconGO   != null)
                    kv.Value.IconGO.transform.localPosition  = new Vector3(0f, y, -100f);
                if (kv.Value.RingGO   != null)
                    kv.Value.RingGO.transform.localPosition  = new Vector3(0f, y, -101f);
                if (kv.Value.LabelTMP != null)
                    kv.Value.LabelTMP.transform.localPosition = new Vector3(0f, labelY, -102f);
                i++;
            }
        }
        else
        {
            float totalW = _slots.Count * SlotWidth;
            float startX = -totalW * 0.5f + SlotWidth * 0.5f;
            float iconY = _layoutAnchoredBottom ? BottomNameLift : 0f;
            float labelY = _layoutAnchoredBottom ? BottomNameLift - LabelOffset : -LabelOffset;
            int i = 0;
            foreach (var kv in _slots)
            {
                float x = startX + i * SlotWidth;
                if (kv.Value.IconGO   != null)
                    kv.Value.IconGO.transform.localPosition  = new Vector3(x, iconY, -100f);
                if (kv.Value.RingGO   != null)
                    kv.Value.RingGO.transform.localPosition  = new Vector3(x, iconY, -101f);
                if (kv.Value.LabelTMP != null)
                    kv.Value.LabelTMP.transform.localPosition = new Vector3(x, labelY, -102f);
                i++;
            }
        }
    }

    private static void LayoutSlotsIfDirty()
    {
        if (_layoutDirty)
            LayoutSlots();
    }

    internal static void ClearSpeakingBar()
    {
        _activeSpeakerIds.Clear();
        _activeSpeakerLevels.Clear();
        _fadedSlotIds.Clear();
        VoiceOverlayState.InvalidateCache();
        DestroySpeakingBarSlots();
        _layoutDirty = false;
        if (_barRoot != null)
            _barRoot.SetActive(false);
    }

    private static void DestroySpeakingBarSlots()
    {
        foreach (var kv in _slots)
        {
            if (kv.Value.IconGO   != null) Object.Destroy(kv.Value.IconGO);
            if (kv.Value.RingGO   != null) Object.Destroy(kv.Value.RingGO);
            if (kv.Value.LabelTMP != null) Object.Destroy(kv.Value.LabelTMP.gameObject);
        }
        _slots.Clear();
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Start))]
    private static class HudStartPatch
    {
        private static void Postfix()
        {
            DestroySpeakingBarSlots();
            _activeSpeakerIds.Clear();
            _activeSpeakerLevels.Clear();
            _fadedSlotIds.Clear();
            _playerLookup.Clear();
            VoiceOverlayState.InvalidateCache();
            CrewmateAvatarRenderer.ClearCache();
            _layoutDirty = false;
            _sortingDirty = false;
            if (_barRoot != null) { Object.Destroy(_barRoot); _barRoot = null; }
            _barAspect = null;
        }
    }

    private static void ApplyTopSorting(GameObject go)
    {
        if (CrewmateAvatarRenderer.IsCustomIcon(go))
        {
            CrewmateAvatarRenderer.ApplySorting(go);
            return;
        }

        foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true))
        {
            sr.sortingLayerName = VCSorting.Layer;
            sr.sortingOrder     = VCSorting.Base;
            sr.maskInteraction  = SpriteMaskInteraction.None;
        }

        foreach (var tmp in go.GetComponentsInChildren<TextMeshPro>(true))
        {
            tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
            tmp.sortingOrder = VCSorting.Text;
        }
    }

    private static OutfitFingerprint GetFingerprint(byte playerId)
    {
        var pc = FindPlayer(playerId);
        if (pc?.Data == null) return default;
        var outfit = GetDisplayOutfit(pc);
        return new OutfitFingerprint(
            GetDisplayOutfitId(pc),
            GetPlayerColorId(pc),
            outfit.HatId,
            outfit.SkinId,
            outfit.VisorId,
            outfit.PlayerName);
    }

    // Built once per frame so FindPlayer is O(1) instead of re-scanning the IL2CPP list per speaker.
    private static void RebuildPlayerLookup()
    {
        _playerLookup.Clear();
        try
        {
            var players = PlayerControl.AllPlayerControls;
            if (players == null) return;
            foreach (var pc in players)
                if (pc != null) _playerLookup[pc.PlayerId] = pc;
        }
        catch
        {
            // Scene transitions can temporarily invalidate the player collection.
        }
    }

    private static PlayerControl? FindPlayer(byte id)
        => _playerLookup.TryGetValue(id, out var pc) && pc != null ? pc : null;

    // Shared with the meeting overlay to keep ring/glow/body color in parity (concealed-grey and fallback paths).
    private static Color GetPaletteColor(PlayerControl? pc)
        => CrewmateAvatarRenderer.GetPaletteColor(pc);

    private static int GetPlayerColorId(PlayerControl pc)
    {
        int bodyColor;
        try { bodyColor = pc.cosmetics.bodyMatProperties.ColorId; }
        catch { try { return GetDisplayOutfit(pc).ColorId; } catch { return 0; } }

        // bodyMatProperties reads 0 (red) before cosmetics init; prefer the networked
        // outfit color when valid so the fallback body isn't transiently red.
        if (bodyColor == 0)
        {
            try
            {
                int outfitColor = GetDisplayOutfit(pc).ColorId;
                if (outfitColor > 0) return outfitColor;
            }
            catch { /* keep bodyColor */ }
        }
        return bodyColor;
    }

    private static NetworkedPlayerInfo.PlayerOutfit GetDisplayOutfit(PlayerControl pc)
    {
        try
        {
            return pc.CurrentOutfit ?? pc.Data.DefaultOutfit;
        }
        catch
        {
            return pc.Data.DefaultOutfit;
        }
    }

    private static int GetDisplayOutfitId(PlayerControl pc)
    {
        try
        {
            return (int)pc.CurrentOutfitType;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetDisplayName(PlayerControl? player)
    {
        if (player?.Data == null) return "?";
        // Hide name of a concealed (camo/mixed-up/swooped) speaker so the overlay can't identify them.
        if (CrewmateAvatarRenderer.IsConcealed(player)) return string.Empty;
        try
        {
            var name = GetDisplayOutfit(player).PlayerName;
            if (!string.IsNullOrWhiteSpace(name)) return name;
            // The game blanks the outfit name when concealing identity (Morph/Mimic disguises used by
            // Hysteria, Ambusher, Chameleon, etc. set PlayerName empty and toggle the nameplate off).
            // Only fall back to the real name for the default outfit, so a concealed speaker is never
            // de-anonymized in the bar; a Morphling disguise still shows its (non-empty) target name.
            return GetDisplayOutfitId(player) == 0 ? (player.Data.PlayerName ?? "?") : string.Empty;
        }
        catch
        {
            return player.Data.PlayerName ?? "?";
        }
    }

    private static Sprite? _ringSprite;

    private static Sprite CreateRingSprite()
    {
        if (_ringSprite != null) return _ringSprite;

        const int size = 128;
        const float inner = 0.88f;
        const float feather = 1.6f;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float center = size * 0.5f;
        float outerR = center - 1f;
        float innerR = outerR * inner;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - center + 0.5f;
            float dy = y - center + 0.5f;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float outerA = Mathf.Clamp01((outerR - d) / feather);
            float innerA = Mathf.Clamp01((d - innerR) / feather);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, outerA * innerA));
        }

        tex.Apply();
        _ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        _ringSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _ringSprite;
    }

    // ── Data types ─────────────────────────────────────────────────────────────
    private readonly record struct OutfitFingerprint(
        int OutfitTypeId, int ColorId, string HatId, string SkinId, string VisorId, string PlayerName);

    private class SpeakerSlot
    {
        public GameObject?       IconGO;
        public GameObject?       RingGO;
        public SpriteRenderer?   RingRenderer;
        public TextMeshPro?      LabelTMP;
        public OutfitFingerprint Fingerprint;
        public OutfitFingerprint PendingFingerprint;
        public bool              HasCachedPoseIcon;
        public Color             PlayerColor;
        public float             Level;
        public float             TargetLevel;
        public float             SmoothedLevel;
        public float             Visibility;
        public float             LastActiveTime;
        public float             LastPlayerSeenTime;
        public bool              IsSpeaking;
    }
}
