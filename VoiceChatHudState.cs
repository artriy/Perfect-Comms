using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MiraAPI.LocalSettings;
using TMPro;
using TownOfUs.Patches;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatHudState
{
    // ── Button references ─────────────────────────────────────────────────────
    // Mic and speaker live in the TOU UiTopRight grid row (no AspectPosition).
    // The jail button stays free-floating with AspectPosition because it is
    // conditional (only visible to the Jailor during meetings).

    private static PassiveButton?  _micButton;
    private static GameObject?     _micButtonObj;
    private static PassiveButton?  _spkButton;
    private static GameObject?     _spkButtonObj;
    private static PassiveButton?  _jailButton;
    private static GameObject?     _jailButtonObj;
    private static AspectPosition? _jailAspect;

    // Tracks whether we've already inserted the buttons into the TOU grid this HUD session.
    private static bool _insertedIntoGrid;

    // ── Jail button position (free-floating, unchanged from original) ──────────
    private static AspectPosition.EdgeAlignments _jailAnchor = AspectPosition.EdgeAlignments.LeftTop;
    private static Vector3 _jailEdge = new(0.10f, 0.72f, -100f);

    private const float ButtonScale    = 0.42f;
    private const int   ButtonSortOrder = 32760;

    // ── State ─────────────────────────────────────────────────────────────────
    private static GameObject?  _micTooltip;
    private static GameObject?  _spkTooltip;
    private static TextMeshPro? _micTooltipTmp;
    private static TextMeshPro? _spkTooltipTmp;
    private static bool  _micMuted;
    private static bool  _impostorHeld;
    private static bool  _pushToTalkHeld;
    private static bool  _speakerMuted;
    private static float _overlayScale = 1f;

    public static bool IsMuted        => _micMuted;
    public static bool IsImpostorRadio => _impostorHeld && CanUseTeamChatRadio();
    public static bool IsSpeakerMuted  => _speakerMuted;

    // ── Init ──────────────────────────────────────────────────────────────────

    internal static void Init()
    {
        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)((_, __) =>
            {
                DestroyButtons();
                DestroyTooltips();
                _insertedIntoGrid = false;
            });

        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings != null)
        {
            ApplyOverlayScale(settings.OverlayScale.Value);
            _micMuted     = settings.StartMuted.Value;
            _speakerMuted = settings.StartDeafened.Value;
        }
    }

    // ── HUD update (called every frame by VCManager) ──────────────────────────

    internal static void UpdateHud()
    {
        var hud = HudManager.Instance;
        if (hud == null) return;

        // Try inserting into the TOU grid if it exists and we haven't yet.
        TryInsertIntoTouGrid(hud);

        // If we still have no buttons (grid not ready yet), fall back to building
        // free-floating buttons so voice is never silently broken.
        if (_micButtonObj == null)
            EnsureHudButtonsFallback(hud);

        EnsureTooltips(hud);
        EnsureHudParent(hud);
        VoiceRoleMuteState.Update();
        ApplyMicState();
        UpdateHudButtonsVisibility();
        RefreshButtonVisuals();
    }

    /// <summary>
    /// Inserts the Mic and Speaker buttons as the two leftmost children of
    /// <c>HudManagerPatches.UiTopRight</c> (TOU's main button row).  Called every
    /// frame until it succeeds; after that <c>_insertedIntoGrid</c> prevents re-entry.
    /// </summary>
    private static void TryInsertIntoTouGrid(HudManager hud)
    {
        if (_insertedIntoGrid) return;

        var gridParent = HudManagerPatches.UiTopRight;
        if (gridParent == null) return; // TOU grid not ready yet this frame

        // Build the buttons if they don't exist yet.
        if (_micButtonObj == null)
            BuildMicButton(hud, gridParent.transform);
        if (_spkButtonObj == null)
            BuildSpkButton(hud, gridParent.transform);

        // Re-parent to the grid if already created elsewhere.
        if (_micButtonObj!.transform.parent != gridParent.transform)
            _micButtonObj.transform.SetParent(gridParent.transform, false);
        if (_spkButtonObj!.transform.parent != gridParent.transform)
            _spkButtonObj.transform.SetParent(gridParent.transform, false);

        // Place mic and speaker at the front (index 0 and 1) so they are the
        // leftmost items in the grid row.
        _micButtonObj.transform.SetSiblingIndex(0);
        _spkButtonObj.transform.SetSiblingIndex(1);

        // Remove AspectPosition if one somehow got attached -- the GridArrange
        // component on UiTopRight owns layout for everything in that parent.
        foreach (var ap in _micButtonObj.GetComponents<AspectPosition>())
            Object.Destroy(ap);
        foreach (var ap in _spkButtonObj.GetComponents<AspectPosition>())
            Object.Destroy(ap);

        // Scale to match the other TOU buttons (they sit in a 0.85×0.85 grid cell).
        _micButtonObj.transform.localScale = Vector3.one;
        _spkButtonObj.transform.localScale = Vector3.one;

        // Force the grid to re-arrange now.
        var grid = HudManagerPatches.UiGrid;
        if (grid != null)
            grid.ArrangeChilds();

        _insertedIntoGrid = true;
    }

    // ── Button construction ────────────────────────────────────────────────────

    private static void BuildMicButton(HudManager hud, Transform parent)
    {
        _micButtonObj      = Object.Instantiate(hud.MapButton.gameObject, parent);
        _micButtonObj.name = "VC_MicButton";
        ClearButtonBG(_micButtonObj);
        CreateIconChild(_micButtonObj, "VoiceChatPlugin.Resources.MicOn.png");
        KeepButtonOnTop(_micButtonObj);

        _micButton = _micButtonObj.GetComponent<PassiveButton>();
        _micButton.OnClick = new ButtonClickedEvent();
        _micButton.OnClick.AddListener((Action)ToggleMutePublic);
        _micButton.OnMouseOver = new UnityEvent();
        _micButton.OnMouseOver.AddListener((Action)ShowMicTooltip);
        _micButton.OnMouseOut = new UnityEvent();
        _micButton.OnMouseOut.AddListener((Action)HideTooltips);
    }

    private static void BuildSpkButton(HudManager hud, Transform parent)
    {
        _spkButtonObj      = Object.Instantiate(hud.MapButton.gameObject, parent);
        _spkButtonObj.name = "VC_SpkButton";
        ClearButtonBG(_spkButtonObj);
        CreateIconChild(_spkButtonObj, "VoiceChatPlugin.Resources.SpeakerOn.png");
        KeepButtonOnTop(_spkButtonObj);

        _spkButton = _spkButtonObj.GetComponent<PassiveButton>();
        _spkButton.OnClick = new ButtonClickedEvent();
        _spkButton.OnClick.AddListener((Action)ToggleSpeakerPublic);
        _spkButton.OnMouseOver = new UnityEvent();
        _spkButton.OnMouseOver.AddListener((Action)ShowSpeakerTooltip);
        _spkButton.OnMouseOut = new UnityEvent();
        _spkButton.OnMouseOut.AddListener((Action)HideTooltips);
    }

    private static void BuildJailButton(HudManager hud, Transform parent)
    {
        _jailButtonObj      = Object.Instantiate(hud.MapButton.gameObject, parent);
        _jailButtonObj.name = "VC_JailUnmuteButton";
        _jailButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
        ClearButtonBG(_jailButtonObj);
        CreateIconChild(_jailButtonObj, "VoiceChatPlugin.Resources.JailUnmute.png");
        KeepButtonOnTop(_jailButtonObj);

        _jailButton = _jailButtonObj.GetComponent<PassiveButton>();
        _jailButton.OnClick = new ButtonClickedEvent();
        _jailButton.OnClick.AddListener((Action)JailUnmutePublic);
        _jailButton.OnMouseOver = new UnityEvent();
        _jailButton.OnMouseOut  = new UnityEvent();

        _jailAspect = _jailButtonObj.GetComponent<AspectPosition>()
                      ?? _jailButtonObj.AddComponent<AspectPosition>();
        _jailAspect.Alignment        = _jailAnchor;
        _jailAspect.DistanceFromEdge = _jailEdge;
    }

    /// <summary>
    /// Fallback: builds free-floating buttons (with AspectPosition) when the TOU
    /// grid isn't available yet.  These will be re-parented into the grid as soon
    /// as it becomes available.
    /// </summary>
    private static void EnsureHudButtonsFallback(HudManager hud)
    {
        if (hud.MapButton == null) return;
        var root = ResolveHudRoot(hud);

        if (_micButtonObj == null)
        {
            BuildMicButton(hud, root);
            var ap = _micButtonObj!.AddComponent<AspectPosition>();
            ap.Alignment        = AspectPosition.EdgeAlignments.RightTop;
            ap.DistanceFromEdge = new Vector3(0.95f, 0.10f, -100f);
            _micButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
        }

        if (_spkButtonObj == null)
        {
            BuildSpkButton(hud, root);
            var ap = _spkButtonObj!.AddComponent<AspectPosition>();
            ap.Alignment        = AspectPosition.EdgeAlignments.RightTop;
            ap.DistanceFromEdge = new Vector3(0.95f, 0.40f, -100f);
            _spkButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
        }

        if (_jailButtonObj == null)
            BuildJailButton(hud, root);
    }

    // ── Indicator position -- jail button only (mic/spk are in the grid) ──────

    /// <summary>
    /// The jail button is the only one that still uses a configurable position
    /// because it is conditionally visible and shouldn't occupy a permanent grid slot.
    /// </summary>
    public static void ApplyIndicatorPosition(IndicatorPosition pos)
    {
        switch (pos)
        {
            case IndicatorPosition.TopRight:
                _jailAnchor = AspectPosition.EdgeAlignments.RightTop;
                _jailEdge   = new Vector3(0.10f, 0.72f, -100f);
                break;
            case IndicatorPosition.BottomLeft:
                _jailAnchor = AspectPosition.EdgeAlignments.LeftBottom;
                _jailEdge   = new Vector3(0.10f, 0.72f, -100f);
                break;
            case IndicatorPosition.BottomRight:
                _jailAnchor = AspectPosition.EdgeAlignments.RightBottom;
                _jailEdge   = new Vector3(0.10f, 0.72f, -100f);
                break;
            default:
                _jailAnchor = AspectPosition.EdgeAlignments.LeftTop;
                _jailEdge   = new Vector3(0.10f, 0.72f, -100f);
                break;
        }

        if (_jailAspect != null)
        {
            _jailAspect.Alignment        = _jailAnchor;
            _jailAspect.DistanceFromEdge = _jailEdge;
            _jailAspect.AdjustPosition();
            KeepButtonOnTop(_jailButtonObj);
        }
    }

    // ── Mic / speaker state ───────────────────────────────────────────────────

    internal static void ApplyMicState()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        bool radioTransmit  = IsInImpostorRadioMode();
        bool pushToTalkMuted = settings?.MicMode.Value == VoiceMicMode.PushToTalk
                               && !_pushToTalkHeld && !radioTransmit;
        bool roleMuted = VoiceRoleMuteState.IsLocalMeetingVoiceBlocked();
        VoiceChatRoom.Current?.SetMute(_micMuted || pushToTalkMuted || roleMuted);
    }

    internal static void ApplySpeakerState()
    {
        if (_speakerMuted)
            VoiceChatRoom.Current?.SetMasterVolume(0f);
        else
        {
            var tab = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
            if (tab != null)
                VoiceChatRoom.Current?.SetMasterVolume(tab.MasterVolume.Value);
        }
    }

    internal static void TrySyncHostRoomSettings() { }

    internal static void ToggleMutePublic()   => SetMuted(!_micMuted);
    internal static void ToggleSpeakerPublic() => SetSpeakerMuted(!_speakerMuted);

    internal static void SetMuted(bool muted)
    {
        _micMuted = muted;
        ApplyMicState();
        if (muted) MeetingSpeakingIndicatorPatch.ClearLocalIndicator();
        RefreshButtonVisuals();
    }

    internal static void SetSpeakerMuted(bool muted)
    {
        _speakerMuted = muted;
        ApplySpeakerState();
        RefreshButtonVisuals();
    }

    internal static void UpdateImpostorRadioHold(bool held, bool justPressed, bool justReleased)
    {
        if (!CanUseTeamChatRadio())
        {
            if (_impostorHeld) { _impostorHeld = false; ApplyMicState(); RefreshButtonVisuals(); }
            return;
        }

        bool prev = _impostorHeld;
        _impostorHeld = held;
        if (prev != _impostorHeld) { ApplyMicState(); RefreshButtonVisuals(); }
    }

    internal static bool IsInImpostorRadioMode()
        => _impostorHeld && CanUseTeamChatRadio() && !_micMuted;

    internal static void UpdatePushToTalkHeld(bool held)
    {
        if (_pushToTalkHeld == held) return;
        _pushToTalkHeld = held;
        ApplyMicState();
        RefreshButtonVisuals();
    }

    internal static void JailUnmutePublic()
    {
        VoiceRoleMuteState.LocalJailorAllowVoice();
        UpdateHudButtonsVisibility();
        RefreshButtonVisuals();
    }

    public static void ApplyOverlayScale(float scale)
    {
        _overlayScale = Mathf.Clamp(scale, 0.75f, 1.5f);
        // Mic and speaker are in the TOU grid so their scale is managed by the grid cell size.
        // Only the jail button (free-floating) gets the explicit scale.
        if (_jailButtonObj != null)
            _jailButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static void DestroyButtons()
    {
        if (_micButtonObj  != null) { Object.Destroy(_micButtonObj);  _micButtonObj  = null; }
        if (_spkButtonObj  != null) { Object.Destroy(_spkButtonObj);  _spkButtonObj  = null; }
        if (_jailButtonObj != null) { Object.Destroy(_jailButtonObj); _jailButtonObj = null; }
        _micButton = null; _spkButton = null; _jailButton = null;
        _jailAspect = null;
    }

    private static void DestroyTooltips()
    {
        if (_micTooltip != null) { Object.Destroy(_micTooltip); _micTooltip = null; }
        if (_spkTooltip != null) { Object.Destroy(_spkTooltip); _spkTooltip = null; }
        _micTooltipTmp = null; _spkTooltipTmp = null;
    }

    private static void EnsureTooltips(HudManager hud)
    {
        var root = ResolveHudRoot(hud);
        if (_micTooltip == null) _micTooltip = CreateTooltipObject(root, out _micTooltipTmp);
        if (_spkTooltip == null) _spkTooltip = CreateTooltipObject(root, out _spkTooltipTmp);
    }

    private static void EnsureHudParent(HudManager hud)
    {
        // Mic and spk belong to the TOU grid -- don't re-parent them to the HUD root.
        // Only the jail button and tooltips need to follow the HUD root.
        var root = ResolveHudRoot(hud);
        ReparentToRoot(_jailButtonObj, root);
        ReparentToRoot(_micTooltip, root);
        ReparentToRoot(_spkTooltip, root);
    }

    private static Transform ResolveHudRoot(HudManager hud)
    {
        var meeting = MeetingHud.Instance;
        if (meeting != null)
        {
            var meetingParent = meeting.transform.parent;
            if (meetingParent != null && meetingParent.gameObject.activeInHierarchy)
                return meetingParent;
            return meeting.transform;
        }
        return hud.transform.parent != null ? hud.transform.parent : hud.transform;
    }

    private static void ReparentToRoot(GameObject? obj, Transform root)
    {
        if (obj == null || obj.transform.parent == root) return;
        obj.transform.SetParent(root, false);
    }

    private static void UpdateHudButtonsVisibility()
    {
        if (_micButtonObj != null) _micButtonObj.SetActive(true);
        if (_spkButtonObj != null) _spkButtonObj.SetActive(true);
        _jailButtonObj?.SetActive(VoiceRoleMuteState.CanLocalJailorUnmute(out _));
        _jailAspect?.AdjustPosition();
        KeepButtonOnTop(_jailButtonObj);
    }

    private static void RefreshButtonVisuals()
    {
        if (_micButtonObj != null)
        {
            var sr = _micButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                if (VoiceRoleMuteState.TryGetLocalMeetingVoiceBlockReason(out _))
                {
                    sr.sprite = Sprites.MicOff;
                    sr.color  = new Color(1f, 0.65f, 0.15f);
                }
                else if (_micMuted)
                {
                    sr.sprite = Sprites.MicOff;
                    sr.color  = new Color(1f, 0.4f, 0.4f);
                }
                else if (IsInImpostorRadioMode())
                {
                    sr.sprite = Sprites.MicOn;
                    sr.color  = new Color(1f, 0.55f, 0.1f);
                }
                else
                {
                    sr.sprite = Sprites.MicOn;
                    sr.color  = Color.white;
                }
            }
        }

        if (_jailButtonObj != null)
        {
            var sr = _jailButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null) { sr.sprite = Sprites.JailUnmute; sr.color = Color.white; }
        }

        if (_spkButtonObj != null)
        {
            var sr = _spkButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                sr.sprite = _speakerMuted ? Sprites.SpkOff : Sprites.SpkOn;
                sr.color  = _speakerMuted ? new Color(1f, 0.4f, 0.4f) : Color.white;
            }
        }
    }

    // ── Tooltips ──────────────────────────────────────────────────────────────

    private static GameObject CreateTooltipObject(Transform root, out TextMeshPro tmp)
    {
        var go = new GameObject("VC_Tooltip");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, 0f, -80f);

        var bg = new GameObject("BG");
        bg.transform.SetParent(go.transform, false);
        var bgSr = bg.AddComponent<SpriteRenderer>();
        bgSr.sprite           = CreateSolidSprite(new Color(0f, 0f, 0f, 0.82f));
        bgSr.sortingLayerName = VCSorting.Layer;
        bgSr.sortingOrder     = ButtonSortOrder + 1;
        bg.transform.localScale = new Vector3(2.6f, 2.0f, 1f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -80f);
        tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize = 1.5f; tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
        tmp.sortingOrder = ButtonSortOrder + 2;
        tmp.rectTransform.sizeDelta = new Vector2(2.4f, 1.8f);
        go.SetActive(false);
        return go;
    }

    private static void ShowMicTooltip()
    {
        if (_micTooltip == null || _micTooltipTmp == null || _micButtonObj == null) return;

        string status = _micMuted ? "Muted"
            : IsInImpostorRadioMode() ? "Team Chat Radio (held)"
            : "Active";

        var tab     = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        string muteKey  = VoiceChatKeybinds.ToggleMute.CurrentKey.ToString();
        string radioKey = VoiceChatKeybinds.ImpostorRadio.CurrentKey.ToString();

        _micTooltipTmp.text =
            "<b>Microphone</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)(tab.MicVolume.Value * 100f)}%\n" +
            $"Mute: {muteKey}  |  Radio: {radioKey} (hold)";

        PositionNear(_micTooltip, _micButtonObj);
        _micTooltip.SetActive(true);
    }

    private static void ShowSpeakerTooltip()
    {
        if (_spkTooltip == null || _spkTooltipTmp == null || _spkButtonObj == null) return;

        string status = _speakerMuted ? "Muted" : "Active";
        var tab    = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        string key = VoiceChatKeybinds.ToggleSpeaker.CurrentKey.ToString();

        _spkTooltipTmp.text =
            "<b>Speaker</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)(tab.MasterVolume.Value * 100f)}%\n" +
            $"Hotkey: {key}";

        PositionNear(_spkTooltip, _spkButtonObj);
        _spkTooltip.SetActive(true);
    }

    private static void HideTooltips()
    {
        _micTooltip?.SetActive(false);
        _spkTooltip?.SetActive(false);
    }

    private static void PositionNear(GameObject tooltip, GameObject btn)
    {
        var p = btn.transform.position;
        tooltip.transform.position = new Vector3(p.x - 0.2f, p.y - 0.9f, p.z - 1f);
    }

    // ── Misc helpers ──────────────────────────────────────────────────────────

    private static bool CanUseTeamChatRadio()
        => VoiceRoleMuteState.CanLocalPlayerUseTeamChatRadio();

    private static void ClearButtonBG(GameObject obj)
    {
        foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>())
            sr.color = Color.clear;
    }

    private static void CreateIconChild(GameObject parent, string resource)
    {
        var go = new GameObject("VCIcon");
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = parent.layer;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite           = LoadSprite(resource);
        sr.sortingLayerName = VCSorting.Layer;
        sr.sortingOrder     = ButtonSortOrder;
    }

    private static void KeepButtonOnTop(GameObject? button)
    {
        if (button == null) return;
        button.transform.SetAsLastSibling();
        var pos = button.transform.localPosition;
        button.transform.localPosition = new Vector3(pos.x, pos.y, -100f);
        foreach (var sr in button.GetComponentsInChildren<SpriteRenderer>(true))
        {
            sr.sortingLayerName = VCSorting.Layer;
            sr.sortingOrder     = ButtonSortOrder;
        }
    }

    private static Sprite CreateSolidSprite(Color c)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, c); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
    }

    private static readonly Dictionary<string, Sprite> _spriteCache = new();

    public static Sprite LoadSprite(string path)
    {
        if (_spriteCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 900f);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[path] = spr;
            return spr;
        }
        catch
        {
            VoiceChatPluginMain.Logger.LogError("[VC] Sprite load failed: " + path);
            return null!;
        }
    }

    private static class Sprites
    {
        public static Sprite MicOn     => LoadSprite("VoiceChatPlugin.Resources.MicOn.png");
        public static Sprite MicOff    => LoadSprite("VoiceChatPlugin.Resources.MicOff.png");
        public static Sprite SpkOn     => LoadSprite("VoiceChatPlugin.Resources.SpeakerOn.png");
        public static Sprite SpkOff    => LoadSprite("VoiceChatPlugin.Resources.SpeakerOff.png");
        public static Sprite JailUnmute => LoadSprite("VoiceChatPlugin.Resources.JailUnmute.png");
    }
}