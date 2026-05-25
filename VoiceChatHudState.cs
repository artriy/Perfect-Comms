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
    private const float ButtonScale = 0.42f;
    private const int   ButtonSortOrder = 32760;
    private const int   TooltipSortOrder = 32767;
    private const float TooltipHalfWidth = 1.35f;
    private const float TooltipHalfHeight = 1.05f;
    private const float TooltipButtonGap = 0.35f;
    private const float TooltipViewportPadding = 0.02f;
    private static float _btnX = 0.99f;
    private static float _btnY = 0.10f;
    private static VoiceControlsLayout _controlsLayout = VoiceControlsLayout.Vertical;
    private static GameObject?  _micTooltip;
    private static GameObject?  _spkTooltip;
    private static TextMeshPro? _micTooltipTmp;
    private static TextMeshPro? _spkTooltipTmp;
    private static bool _micMuted;
    private static bool _impostorHeld;
    private static bool _pushToTalkHeld;
    private static bool _speakerMuted;
    private static bool _initialized;
    private static float _overlayScale = 1.30f;

    public static bool IsMuted        => IsManualMuteActive();
    public static bool IsImpostorRadio => IsInImpostorRadioMode();
    public static bool IsSpeakerMuted => _speakerMuted;
    internal static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)((_, __) =>
            {
                DestroyButtons();
                DestroyTooltips();
            });

        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings != null)
        {
            RefreshButtonLayout(settings);
            ApplyOverlayScale(settings.OverlayScale.Value);
            _micMuted     = settings.MicMode.Value == VoiceMicMode.PushToTalk ? false : settings.StartMuted.Value;
            _speakerMuted = settings.StartDeafened.Value;
        }
    }

    internal static void RefreshButtonLayout()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        if (settings == null) return;
        RefreshButtonLayout(settings);
    }

    private static void RefreshButtonLayout(VoiceChatLocalSettings settings)
    {
        _btnX = settings.ButtonPositionX.Value;
        _btnY = settings.ButtonPositionY.Value;
        _controlsLayout = settings.VoiceControlsLayout.Value;
        PositionButtons();
    }
    private static void PositionButtons()
    {
        if (_micButtonObj == null || _spkButtonObj == null) return;

        var cam = Camera.main;
        if (cam == null) return;
        var worldPt = cam.ViewportToWorldPoint(new Vector3(_btnX, _btnY, 10f));

        float scale = _overlayScale * ButtonScale;
        float spacing = scale * 0.8f;

        Vector3 micPos, spkPos, jailPos;
        if (_controlsLayout == VoiceControlsLayout.Vertical)
        {
            micPos  = new Vector3(worldPt.x, worldPt.y,             -100f);
            spkPos  = new Vector3(worldPt.x, worldPt.y - spacing,   -100f);
            jailPos = new Vector3(worldPt.x, worldPt.y - spacing * 2f, -100f);
        }
        else
        {
            micPos  = new Vector3(worldPt.x,             worldPt.y, -100f);
            spkPos  = new Vector3(worldPt.x + spacing,   worldPt.y, -100f);
            jailPos = new Vector3(worldPt.x + spacing * 2f, worldPt.y, -100f);
        }

        var parent = _micButtonObj.transform.parent;
        if (parent != null)
        {
            _micButtonObj.transform.localPosition = parent.InverseTransformPoint(micPos);
            _spkButtonObj.transform.localPosition = parent.InverseTransformPoint(spkPos);
            if (_jailButtonObj != null)
                _jailButtonObj.transform.localPosition = parent.InverseTransformPoint(jailPos);
        }
        else
        {
            _micButtonObj.transform.position = micPos;
            _spkButtonObj.transform.position = spkPos;
            if (_jailButtonObj != null)
                _jailButtonObj.transform.position = jailPos;
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
        }
    }
    private static void EnsureTooltips(HudManager hud)
    {
        var root = ResolveHudRoot(hud);
        if (_micTooltip == null)
            _micTooltip = CreateTooltipObject(root, out _micTooltipTmp);
        if (_spkTooltip == null)
            _spkTooltip = CreateTooltipObject(root, out _spkTooltipTmp);
        KeepTooltipOnTop(_micTooltip);
        KeepTooltipOnTop(_spkTooltip);
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
        PositionButtons();

        KeepButtonOnTop(_micButtonObj);
        KeepButtonOnTop(_spkButtonObj);
        KeepButtonOnTop(_jailButtonObj);
    }

    private static void RefreshButtonVisuals()
    {
        // ── Mic button ────────────────────────────────────────────────────────
        if (_micButtonObj != null)
        {
            var sr = _micButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();

            if (VoiceRoleMuteState.TryGetLocalMeetingVoiceBlockReason(out _))
            {
                if (sr != null) { sr.sprite = Sprites.MicOff; sr.color = new Color(1f, 0.65f, 0.15f); }
            }
            else if (IsManualMuteActive())
            {
                if (sr != null) { sr.sprite = Sprites.MicOff; sr.color = new Color(1f, 0.4f, 0.4f); }
            }
            else if (IsInImpostorRadioMode())
            {
                if (sr != null) { sr.sprite = Sprites.MicOn; sr.color = new Color(1f, 0.55f, 0.1f); }
            }
            else
            {
                if (sr != null) { sr.sprite = Sprites.MicOn; sr.color = Color.white; }
            }
        }
        if (_spkButtonObj != null)
        {
            var sr = _spkButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();

            if (_speakerMuted)
            {
                if (sr != null) { sr.sprite = Sprites.SpkOff; sr.color = new Color(1f, 0.4f, 0.4f); }
            }
            else
            {
                if (sr != null) { sr.sprite = Sprites.SpkOn; sr.color = Color.white; }
            }
        }
    }
    internal static void ApplyMicState()
    {
        var settings = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        bool radioTransmit   = IsInImpostorRadioMode();
        bool pushToTalkMode  = settings?.MicMode.Value == VoiceMicMode.PushToTalk;
        if (pushToTalkMode && _micMuted) _micMuted = false;
        bool pushToTalkMuted = pushToTalkMode && !_pushToTalkHeld && !radioTransmit;
        bool roleMuted       = VoiceRoleMuteState.IsLocalMeetingVoiceBlocked();
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

    internal static bool IsPushToTalkMode()
        => LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance?.MicMode.Value == VoiceMicMode.PushToTalk;

    private static bool IsManualMuteActive()
        => _micMuted && !IsPushToTalkMode();

    internal static void ToggleMutePublic()
    {
        if (IsPushToTalkMode()) { SetMuted(false); return; }
        SetMuted(!_micMuted);
    }

    internal static void SetMuted(bool muted)
    {
        _micMuted = muted && !IsPushToTalkMode();
        ApplyMicState();
        if (_micMuted) MeetingSpeakingIndicatorPatch.ClearLocalIndicator();
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

        bool prev     = _impostorHeld;
        _impostorHeld = held;
        if (prev != _impostorHeld) { ApplyMicState(); RefreshButtonVisuals(); }
    }

    internal static bool IsInImpostorRadioMode()
        => _impostorHeld
        && CanUseImpostorRadio()
        && !IsManualMuteActive()
        && !VoiceRoleMuteState.IsLocalMeetingVoiceBlocked();

    internal static void UpdatePushToTalkHeld(bool held)
    {
        if (_pushToTalkHeld == held) return;
        _pushToTalkHeld = held;
        ApplyMicState();
        RefreshButtonVisuals();
    }

    internal static void ToggleSpeakerPublic() => SetSpeakerMuted(!_speakerMuted);

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
        _overlayScale = Mathf.Clamp(scale, 0.75f, 3.0f);
        if (_micButtonObj != null)
            _micButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
        if (_spkButtonObj != null)
            _spkButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
        if (_jailButtonObj != null)
            _jailButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
        PositionButtons();
    }
    private static void DestroyButtons()
    {
        if (_micButtonObj  != null) { Object.Destroy(_micButtonObj);  _micButtonObj  = null; }
        if (_spkButtonObj  != null) { Object.Destroy(_spkButtonObj);  _spkButtonObj  = null; }
        if (_jailButtonObj != null) { Object.Destroy(_jailButtonObj); _jailButtonObj = null; }
        _micButton   = null; _spkButton   = null; _jailButton  = null;
    }

    private static void DestroyTooltips()
    {
        if (_micTooltip != null) { Object.Destroy(_micTooltip); _micTooltip = null; }
        if (_spkTooltip != null) { Object.Destroy(_spkTooltip); _spkTooltip = null; }
        _micTooltipTmp = null; _spkTooltipTmp = null;
    }
    private static GameObject CreateTooltipObject(Transform root, out TextMeshPro tmp)
    {
        var go = new GameObject("VC_Tooltip");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, 0f, -80f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -80f);
        tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize = 1.5f; tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
        tmp.sortingOrder = TooltipSortOrder;
        tmp.rectTransform.sizeDelta = new Vector2(2.4f, 1.8f);
        KeepTooltipOnTop(go);
        go.SetActive(false);
        return go;
    }

    private static void ShowMicTooltip()
    {
        if (_micTooltip == null || _micTooltipTmp == null || _micButtonObj == null) return;

        var tab = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        bool pushToTalkMode = tab?.MicMode.Value == VoiceMicMode.PushToTalk;
        string status = VoiceRoleMuteState.TryGetLocalMeetingVoiceBlockReason(out string roleMuteReason)
            ? roleMuteReason
            : IsManualMuteActive() ? "Muted"
            : IsInImpostorRadioMode() ? "Impostor Radio (held)"
            : pushToTalkMode ? "Push To Talk"
            : "Active";
        string muteKey  = VoiceChatKeybinds.ToggleMute.CurrentKey.ToString();
        string radioKey = VoiceChatKeybinds.ImpostorRadio.CurrentKey.ToString();

        _micTooltipTmp.text =
            "<b>Microphone</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)((tab?.MicVolume.Value ?? 1f) * 100f)}%\n" +
            (pushToTalkMode
                ? $"Push To Talk active  |  Imp. Radio: {radioKey} (hold)"
                : $"Mute: {muteKey}  |  Imp. Radio: {radioKey} (hold)");

        PositionNear(_micTooltip, _micButtonObj);
        KeepTooltipOnTop(_micTooltip);
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
        KeepTooltipOnTop(_spkTooltip);
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
        var cam = Camera.main;
        if (cam == null)
        {
            tooltip.transform.position = new Vector3(p.x + TooltipHalfWidth, p.y + TooltipHalfHeight, p.z - 1f);
            return;
        }

        var viewport = cam.WorldToViewportPoint(p);
        float side = viewport.x < 0.5f ? 1f : -1f;
        float x = p.x + side * (TooltipHalfWidth + TooltipButtonGap);
        float y = p.y;

        if (viewport.y < 0.35f)
            y = p.y + TooltipHalfHeight + TooltipButtonGap;
        else if (viewport.y > 0.65f)
            y = p.y - TooltipHalfHeight - TooltipButtonGap;

        float z = viewport.z;
        var min = cam.ViewportToWorldPoint(new Vector3(TooltipViewportPadding, TooltipViewportPadding, z));
        var max = cam.ViewportToWorldPoint(new Vector3(1f - TooltipViewportPadding, 1f - TooltipViewportPadding, z));
        float minX = Mathf.Min(min.x, max.x) + TooltipHalfWidth;
        float maxX = Mathf.Max(min.x, max.x) - TooltipHalfWidth;
        float minY = Mathf.Min(min.y, max.y) + TooltipHalfHeight;
        float maxY = Mathf.Max(min.y, max.y) - TooltipHalfHeight;

        tooltip.transform.position = new Vector3(
            ClampTooltipAxis(x, minX, maxX),
            ClampTooltipAxis(y, minY, maxY),
            p.z - 1f);
    }

    private static float ClampTooltipAxis(float value, float min, float max)
        => min <= max ? Mathf.Clamp(value, min, max) : (min + max) * 0.5f;

    private static void KeepTooltipOnTop(GameObject? tooltip)
    {
        if (tooltip == null) return;
        tooltip.transform.SetAsLastSibling();
        VCOverlayCamera.EnsureOnTop(tooltip);
        foreach (var tmp in tooltip.GetComponentsInChildren<TextMeshPro>(true))
        {
            tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
            tmp.sortingOrder = TooltipSortOrder;
        }
        foreach (var renderer in tooltip.GetComponentsInChildren<Renderer>(true))
        {
            renderer.sortingLayerName = VCSorting.Layer;
            renderer.sortingOrder = TooltipSortOrder;
        }
    }

    internal static bool CanUseImpostorRadioInput()
        => CanUseImpostorRadio() && !VoiceRoleMuteState.IsLocalMeetingVoiceBlocked();

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

    private static readonly Dictionary<string, Sprite> _spriteCache = new();

    public static Sprite LoadSprite(string path, bool highQuality = false)
    {
        var cacheKey = highQuality ? path + "#hq" : path;
        if (_spriteCache.TryGetValue(cacheKey, out var cached)) return cached;
        try
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, highQuality)
            {
                wrapMode   = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = highQuality ? 16 : 1,
                mipMapBias = highQuality ? -1.15f : 0f,
            };
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            tex.wrapMode   = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = highQuality ? 16 : 1;
            tex.mipMapBias = highQuality ? -1.15f : 0f;
            if (highQuality) tex.Apply(true, false);
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
