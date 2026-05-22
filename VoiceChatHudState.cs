using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MiraAPI.LocalSettings;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static class VoiceChatHudState
{
    private static PassiveButton?  _micButton;
    private static GameObject?     _micButtonObj;
    private static PassiveButton?  _spkButton;
    private static GameObject?     _spkButtonObj;
    private static PassiveButton?  _jailButton;
    private static GameObject?     _jailButtonObj;
    private static AspectPosition? _micAspect;
    private static AspectPosition? _spkAspect;
    private static AspectPosition? _jailAspect;

    // ── Indicator position ────────────────────────────────────────────────────
    // Each IndicatorPosition value maps to an EdgeAlignment + two edge vectors
    // (mic offset, spk offset).  The two buttons are spaced 0.65 units apart
    // along the dominant axis of each anchor.

    private const float ButtonScale = 0.42f;
    private const int ButtonSortOrder = 32760;
    private static AspectPosition.EdgeAlignments _buttonAnchor = AspectPosition.EdgeAlignments.LeftTop;
    private static Vector3 _micEdge = new(0.10f, 0.10f, -100f);
    private static Vector3 _spkEdge = new(0.10f, 0.40f, -100f);
    private static Vector3 _jailEdge = new(0.10f, 0.72f, -100f);

    /// <summary>
    /// Applies a new indicator position immediately.  Called from
    /// VoiceChatLocalSettings.OnOptionChanged and on HUD construction.
    /// </summary>
    public static void ApplyIndicatorPosition(IndicatorPosition pos)
    {
        switch (pos)
        {
            case IndicatorPosition.TopRight:
                _buttonAnchor = AspectPosition.EdgeAlignments.RightTop;
                _micEdge = new Vector3(0.10f, 0.10f, -100f);
                _spkEdge = new Vector3(0.10f, 0.40f, -100f);
                _jailEdge = new Vector3(0.10f, 0.72f, -100f);
                break;
            case IndicatorPosition.BottomLeft:
                _buttonAnchor = AspectPosition.EdgeAlignments.LeftBottom;
                _micEdge = new Vector3(0.10f, 0.40f, -100f);
                _spkEdge = new Vector3(0.10f, 0.10f, -100f);
                _jailEdge = new Vector3(0.10f, 0.72f, -100f);
                break;
            case IndicatorPosition.BottomRight:
                _buttonAnchor = AspectPosition.EdgeAlignments.RightBottom;
                _micEdge = new Vector3(0.10f, 0.40f, -100f);
                _spkEdge = new Vector3(0.10f, 0.10f, -100f);
                _jailEdge = new Vector3(0.10f, 0.72f, -100f);
                break;
            default:
                _buttonAnchor = AspectPosition.EdgeAlignments.LeftTop;
                _micEdge = new Vector3(0.10f, 0.10f, -100f);
                _spkEdge = new Vector3(0.10f, 0.40f, -100f);
                _jailEdge = new Vector3(0.10f, 0.72f, -100f);
                break;
        }

        // If buttons already exist, update them live without recreation.
        if (_micAspect != null)
        {
            _micAspect.Alignment        = _buttonAnchor;
            _micAspect.DistanceFromEdge = _micEdge;
            _micAspect.AdjustPosition();
            KeepButtonOnTop(_micButtonObj);
        }
        if (_spkAspect != null)
        {
            _spkAspect.Alignment        = _buttonAnchor;
            _spkAspect.DistanceFromEdge = _spkEdge;
            _spkAspect.AdjustPosition();
            KeepButtonOnTop(_spkButtonObj);
        }
        if (_jailAspect != null)
        {
            _jailAspect.Alignment        = _buttonAnchor;
            _jailAspect.DistanceFromEdge = _jailEdge;
            _jailAspect.AdjustPosition();
            KeepButtonOnTop(_jailButtonObj);
        }
    }

    private static GameObject?  _micTooltip;
    private static GameObject?  _spkTooltip;
    private static TextMeshPro? _micTooltipTmp;
    private static TextMeshPro? _spkTooltipTmp;
    private static bool _micMuted;
    private static bool _impostorHeld;
    private static bool _pushToTalkHeld;
    private static bool _speakerMuted;
    private static float _overlayScale = 1f;
    public static bool IsMuted           => _micMuted;
    public static bool IsImpostorRadio   => _impostorHeld && CanUseImpostorRadio();
    public static bool IsSpeakerMuted    => _speakerMuted;

    internal static void Init()
    {
        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)((_, __) =>
            {
                DestroyButtons();
                DestroyTooltips();
            });

        // Apply saved position immediately.
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings != null)
        {
            ApplyIndicatorPosition(settings.VoiceIndicatorPosition.Value);
            ApplyOverlayScale(settings.OverlayScale.Value);
            _micMuted = settings.StartMuted.Value;
            _speakerMuted = settings.StartDeafened.Value;
        }
    }

    internal static void UpdateHud()
    {
        var hud = HudManager.Instance;
        if (hud == null) return;

        EnsureHudButtons(hud);
        EnsureTooltips(hud);
        EnsureHudParent(hud);
        VoiceRoleMuteState.Update();
        ApplyMicState();
        UpdateHudButtonsVisibility();
        RefreshButtonVisuals();
    }

    internal static void ApplyMicState()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        bool radioTransmit = IsInImpostorRadioMode();
        bool pushToTalkMuted = settings?.MicMode.Value == VoiceMicMode.PushToTalk &&
                               !_pushToTalkHeld &&
                               !radioTransmit;
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

    internal static void ToggleMutePublic()
    {
        SetMuted(!_micMuted);
    }

    internal static void SetMuted(bool muted)
    {
        _micMuted = muted;
        ApplyMicState();
        if (muted)
            MeetingSpeakingIndicatorPatch.ClearLocalIndicator();
        RefreshButtonVisuals();
    }

    internal static void UpdateImpostorRadioHold(bool held, bool justPressed, bool justReleased)
    {
        if (!CanUseImpostorRadio())
        {
            if (_impostorHeld)
            {
                _impostorHeld = false;
                ApplyMicState();
                RefreshButtonVisuals();
            }
            return;
        }

        bool prev = _impostorHeld;
        _impostorHeld = held;

        if (prev != _impostorHeld)
        {
            ApplyMicState();
            RefreshButtonVisuals();
        }
    }

    internal static bool IsInImpostorRadioMode()
        => _impostorHeld && CanUseImpostorRadio() && !_micMuted;

    internal static void UpdatePushToTalkHeld(bool held)
    {
        if (_pushToTalkHeld == held) return;
        _pushToTalkHeld = held;
        ApplyMicState();
        RefreshButtonVisuals();
    }

    internal static void ToggleSpeakerPublic()
    {
        SetSpeakerMuted(!_speakerMuted);
    }

    internal static void JailUnmutePublic()
    {
        VoiceRoleMuteState.LocalJailorAllowVoice();
        UpdateHudButtonsVisibility();
        RefreshButtonVisuals();
    }

    internal static void SetSpeakerMuted(bool muted)
    {
        _speakerMuted = muted;
        ApplySpeakerState();
        RefreshButtonVisuals();
    }

    public static void ApplyOverlayScale(float scale)
    {
        _overlayScale = Mathf.Clamp(scale, 0.75f, 1.5f);
        if (_micButtonObj != null)
            _micButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
        if (_spkButtonObj != null)
            _spkButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
        if (_jailButtonObj != null)
            _jailButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
    }

    private static void DestroyButtons()
    {
        if (_micButtonObj != null) { Object.Destroy(_micButtonObj); _micButtonObj = null; }
        if (_spkButtonObj != null) { Object.Destroy(_spkButtonObj); _spkButtonObj = null; }
        if (_jailButtonObj != null) { Object.Destroy(_jailButtonObj); _jailButtonObj = null; }
        _micButton = null; _spkButton = null; _jailButton = null;
        _micAspect = null; _spkAspect = null; _jailAspect = null;
    }

    private static void DestroyTooltips()
    {
        if (_micTooltip != null) { Object.Destroy(_micTooltip); _micTooltip = null; }
        if (_spkTooltip != null) { Object.Destroy(_spkTooltip); _spkTooltip = null; }
        _micTooltipTmp = null; _spkTooltipTmp = null;
    }

    private static void EnsureHudButtons(HudManager hud)
    {
        if (hud.MapButton == null) return;
        var root = ResolveHudRoot(hud);

        if (_micButtonObj == null)
        {
            _micButtonObj      = Object.Instantiate(hud.MapButton.gameObject, root);
            _micButtonObj.name = "VC_MicButton";
            _micButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
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

            _micAspect = _micButtonObj.GetComponent<AspectPosition>()
                ?? _micButtonObj.AddComponent<AspectPosition>();
            _micAspect.Alignment        = _buttonAnchor;
            _micAspect.DistanceFromEdge = _micEdge;
        }

        if (_spkButtonObj == null)
        {
            _spkButtonObj      = Object.Instantiate(hud.MapButton.gameObject, root);
            _spkButtonObj.name = "VC_SpkButton";
            _spkButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
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

            _spkAspect = _spkButtonObj.GetComponent<AspectPosition>()
                ?? _spkButtonObj.AddComponent<AspectPosition>();
            _spkAspect.Alignment        = _buttonAnchor;
            _spkAspect.DistanceFromEdge = _spkEdge;
        }

        if (_jailButtonObj == null)
        {
            _jailButtonObj      = Object.Instantiate(hud.MapButton.gameObject, root);
            _jailButtonObj.name = "VC_JailUnmuteButton";
            _jailButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
            ClearButtonBG(_jailButtonObj);
            CreateIconChild(_jailButtonObj, "VoiceChatPlugin.Resources.JailUnmute.png");
            KeepButtonOnTop(_jailButtonObj);

            _jailButton = _jailButtonObj.GetComponent<PassiveButton>();
            _jailButton.OnClick = new ButtonClickedEvent();
            _jailButton.OnClick.AddListener((Action)JailUnmutePublic);
            _jailButton.OnMouseOver = new UnityEvent();
            _jailButton.OnMouseOut = new UnityEvent();

            _jailAspect = _jailButtonObj.GetComponent<AspectPosition>()
                ?? _jailButtonObj.AddComponent<AspectPosition>();
            _jailAspect.Alignment        = _buttonAnchor;
            _jailAspect.DistanceFromEdge = _jailEdge;
        }
    }

    private static void EnsureTooltips(HudManager hud)
    {
        var root = ResolveHudRoot(hud);
        if (_micTooltip == null)
            _micTooltip = CreateTooltipObject(root, out _micTooltipTmp);
        if (_spkTooltip == null)
            _spkTooltip = CreateTooltipObject(root, out _spkTooltipTmp);
    }

    private static void EnsureHudParent(HudManager hud)
    {
        var root = ResolveHudRoot(hud);
        ReparentToRoot(_micButtonObj, root);
        ReparentToRoot(_spkButtonObj, root);
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
        if (_micButtonObj == null || _spkButtonObj == null) return;
        _micButtonObj.SetActive(true);
        _spkButtonObj.SetActive(true);
        _jailButtonObj?.SetActive(VoiceRoleMuteState.CanLocalJailorUnmute(out _));
        _micAspect?.AdjustPosition();
        _spkAspect?.AdjustPosition();
        _jailAspect?.AdjustPosition();
        KeepButtonOnTop(_micButtonObj);
        KeepButtonOnTop(_spkButtonObj);
        KeepButtonOnTop(_jailButtonObj);
    }

    // ── Visual refresh ────────────────────────────────────────────────────────
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
            if (sr != null)
            {
                sr.sprite = Sprites.JailUnmute;
                sr.color = Color.white;
            }
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
        bgSr.sprite = CreateSolidSprite(new Color(0f, 0f, 0f, 0.82f));
        bgSr.sortingLayerName = VCSorting.Layer;
        bgSr.sortingOrder = ButtonSortOrder + 1;
        bg.transform.localScale = new Vector3(2.6f, 2.0f, 1f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -80f);
        tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize = 1.5f; tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false; tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer); tmp.sortingOrder = ButtonSortOrder + 2;
        tmp.rectTransform.sizeDelta = new Vector2(2.4f, 1.8f);
        go.SetActive(false);
        return go;
    }

    private static void ShowMicTooltip()
    {
        if (_micTooltip == null || _micTooltipTmp == null || _micButtonObj == null) return;

        string status = _micMuted ? "Muted"
            : IsInImpostorRadioMode() ? "Impostor Radio (held)"
            : "Active";

        var tab = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        string muteKey  = VoiceChatKeybinds.ToggleMute.CurrentKey.ToString();
        string radioKey = VoiceChatKeybinds.ImpostorRadio.CurrentKey.ToString();

        _micTooltipTmp.text =
            "<b>Microphone</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)(tab.MicVolume.Value * 100f)}%\n" +
            $"Mute: {muteKey}  |  Imp. Radio: {radioKey} (hold)";

        PositionNear(_micTooltip, _micButtonObj);
        _micTooltip.SetActive(true);
    }

    private static void ShowSpeakerTooltip()
    {
        if (_spkTooltip == null || _spkTooltipTmp == null || _spkButtonObj == null) return;

        string status = _speakerMuted ? "Muted" : "Active";
        var tab = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        string hotkey = VoiceChatKeybinds.ToggleSpeaker.CurrentKey.ToString();

        _spkTooltipTmp.text =
            "<b>Speaker</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)(tab.MasterVolume.Value * 100f)}%\n" +
            $"Hotkey: {hotkey}";

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

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static bool CanUseImpostorRadio()
        => PlayerControl.LocalPlayer != null
        && PlayerControl.LocalPlayer.Data?.Role?.IsImpostor == true
        && PlayerControl.LocalPlayer.Data?.IsDead == false
        && VoiceChatGameOptions.GetInstance().ImpostorPrivateRadio.Value;

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
        sr.sprite = LoadSprite(resource);
        sr.sortingLayerName = VCSorting.Layer;
        sr.sortingOrder = ButtonSortOrder;
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
            sr.sortingOrder = ButtonSortOrder;
        }
    }

    private static Sprite CreateSolidSprite(Color c)
    {
        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, c); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
    }

    private static readonly Dictionary<string, Sprite> _spriteCache = new();

    public static Sprite LoadSprite(string path, bool highQuality = false)
    {
        var cacheKey = highQuality ? path + "#hq" : path;
        if (_spriteCache.TryGetValue(cacheKey, out var cached)) return cached;
        try
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, highQuality)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = highQuality ? 16 : 1,
                mipMapBias = highQuality ? -1.15f : 0f,
            };
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = highQuality ? 16 : 1;
            tex.mipMapBias = highQuality ? -1.15f : 0f;
            if (highQuality)
                tex.Apply(true, false);
            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 900f);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[cacheKey] = spr;
            return spr;
        }
        catch
        {
            VoiceDiagnostics.DebugError("[VC] Sprite load failed: " + path);
            return null!;
        }
    }

    private static class Sprites
    {
        public static Sprite MicOn  => LoadSprite("VoiceChatPlugin.Resources.MicOn.png");
        public static Sprite MicOff => LoadSprite("VoiceChatPlugin.Resources.MicOff.png");
        public static Sprite SpkOn  => LoadSprite("VoiceChatPlugin.Resources.SpeakerOn.png");
        public static Sprite SpkOff => LoadSprite("VoiceChatPlugin.Resources.SpeakerOff.png");
        public static Sprite JailUnmute => LoadSprite("VoiceChatPlugin.Resources.JailUnmute.png");
    }
}
