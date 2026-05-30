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
    private const float TooltipHalfHeight = 1.25f;
    private const float TooltipButtonGap = 0.35f;
    private const float TooltipViewportPadding = 0.02f;
    private const float ButtonViewportDepth = 10f;
    private const float ButtonViewportPadding = 0.015f;
    // Right-edge inset for the jail-unmute button on a meeting card, in multiples of the
    // button's world size. Tuned so the icon sits in the empty area past the name without
    // spilling into the gap between cards. Lower = nearer the right edge.
    private const float JailCardRightInset = 0.37f;
    private static float _btnX = 0.99f;
    private static float _btnY = 0.10f;
    private static VoiceControlsLayout _controlsLayout = VoiceControlsLayout.Vertical;
    private static JailUnmuteButtonPlacement _jailPlacement = JailUnmuteButtonPlacement.MeetingCard;
    private static bool _jailOnCard;
    private static GameObject?  _micTooltip;
    private static GameObject?  _spkTooltip;
    private static TextMeshPro? _micTooltipTmp;
    private static TextMeshPro? _spkTooltipTmp;
    private static bool _micMuted;
    private static bool _teamRadioHeld;
    private static VoiceTeamRadioChannel _teamRadioChannel = VoiceTeamRadioChannel.None;
    private static bool _pushToTalkHeld;
    private static bool _speakerMuted;
    private static bool _initialized;
    private static float _overlayScale = 1.30f;

    public static bool IsMuted        => IsManualMuteActive();
    public static bool IsTeamRadio => IsInTeamRadioMode();
    public static bool IsImpostorRadio => IsTeamRadio;
    public static bool IsSpeakerMuted => _speakerMuted;
    internal static bool IsLocalTransmitBlocked => TryGetLocalTransmitBlockReason(out _);
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
        _jailPlacement = settings.JailUnmuteButtonPlacement.Value;
        // Switching away from card mode: drop the card-placement guard now so PositionButtons
        // restores the Voice-HUD spot this frame instead of waiting for the next HUD tick.
        // Reparent the button back to the HUD root FIRST — otherwise PositionButtons would
        // write HUD-root-relative local coordinates onto a button still childed to the
        // jailee's card, flashing it to the wrong spot for one frame.
        if (_jailPlacement != JailUnmuteButtonPlacement.MeetingCard)
        {
            _jailOnCard = false;
            if (_jailButtonObj != null && _micButtonObj != null)
            {
                var hudRoot = _micButtonObj.transform.parent;
                if (hudRoot != null && _jailButtonObj.transform.parent != hudRoot)
                    _jailButtonObj.transform.SetParent(hudRoot, false);
            }
        }
        PositionButtons();
    }
    private static void PositionButtons()
    {
        if (_micButtonObj == null || _spkButtonObj == null) return;

        var cam = Camera.main;
        if (cam == null) return;
        var worldPt = cam.ViewportToWorldPoint(new Vector3(_btnX, _btnY, ButtonViewportDepth));

        float scale = _overlayScale * ButtonScale;
        float spacing = scale * 0.8f;

        Vector3 micPos, spkPos, jailPos;
        if (_controlsLayout == VoiceControlsLayout.Vertical)
        {
            micPos  = new Vector3(worldPt.x, worldPt.y,             -100f);
            spkPos  = new Vector3(worldPt.x, worldPt.y - spacing,   -100f);
            jailPos = new Vector3(worldPt.x, worldPt.y + spacing,   -100f);
        }
        else
        {
            micPos  = new Vector3(worldPt.x,             worldPt.y, -100f);
            spkPos  = new Vector3(worldPt.x + spacing,   worldPt.y, -100f);
            jailPos = new Vector3(worldPt.x + spacing * 2f, worldPt.y, -100f);
        }

        // When the jail button lives on a meeting card, keep it out of the button-group clamp
        // so it can't drag the mic/speaker layout around.
        if (_jailOnCard) jailPos = micPos;
        ClampVoiceButtonViewportPositions(cam, ref micPos, ref spkPos, ref jailPos);

        var parent = _micButtonObj.transform.parent;
        if (parent != null)
        {
            _micButtonObj.transform.localPosition = parent.InverseTransformPoint(micPos);
            _spkButtonObj.transform.localPosition = parent.InverseTransformPoint(spkPos);
            if (_jailButtonObj != null && !_jailOnCard)
                _jailButtonObj.transform.localPosition = parent.InverseTransformPoint(jailPos);
        }
        else
        {
            _micButtonObj.transform.position = micPos;
            _spkButtonObj.transform.position = spkPos;
            if (_jailButtonObj != null && !_jailOnCard)
                _jailButtonObj.transform.position = jailPos;
        }
    }

    private static void ClampVoiceButtonViewportPositions(Camera cam, ref Vector3 micPos, ref Vector3 spkPos, ref Vector3 jailPos)
    {
        var depthWorld = cam.ViewportToWorldPoint(new Vector3(0f, 0f, ButtonViewportDepth));
        var micViewport = cam.WorldToViewportPoint(new Vector3(micPos.x, micPos.y, depthWorld.z));
        var spkViewport = cam.WorldToViewportPoint(new Vector3(spkPos.x, spkPos.y, depthWorld.z));
        var jailViewport = cam.WorldToViewportPoint(new Vector3(jailPos.x, jailPos.y, depthWorld.z));

        float minX = Mathf.Min(Mathf.Min(micViewport.x, spkViewport.x), jailViewport.x);
        float maxX = Mathf.Max(Mathf.Max(micViewport.x, spkViewport.x), jailViewport.x);
        float minY = Mathf.Min(Mathf.Min(micViewport.y, spkViewport.y), jailViewport.y);
        float maxY = Mathf.Max(Mathf.Max(micViewport.y, spkViewport.y), jailViewport.y);

        float shiftX = CalculateViewportShift(minX, maxX);
        float shiftY = CalculateViewportShift(minY, maxY);
        if (Mathf.Approximately(shiftX, 0f) && Mathf.Approximately(shiftY, 0f))
            return;

        var origin = cam.ViewportToWorldPoint(new Vector3(0f, 0f, ButtonViewportDepth));
        var shifted = cam.ViewportToWorldPoint(new Vector3(shiftX, shiftY, ButtonViewportDepth));
        var delta = shifted - origin;
        delta.z = 0f;

        micPos += delta;
        spkPos += delta;
        jailPos += delta;
    }

    private static float CalculateViewportShift(float min, float max)
    {
        float minAllowed = ButtonViewportPadding;
        float maxAllowed = 1f - ButtonViewportPadding;
        float allowedSize = maxAllowed - minAllowed;
        float currentSize = max - min;

        if (currentSize > allowedSize)
            return (minAllowed + maxAllowed) * 0.5f - (min + max) * 0.5f;
        if (min < minAllowed)
            return minAllowed - min;
        if (max > maxAllowed)
            return maxAllowed - max;
        return 0f;
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
        // _jailButtonObj's parent is owned by UpdateHudButtonsVisibility (HUD root vs. the
        // jailee's meeting card), so it is intentionally not reparented here.
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

        bool jailVisible = VoiceRoleMuteState.CanLocalJailorUnmute(out byte jailedId);
        _jailButtonObj?.SetActive(jailVisible);

        _jailOnCard = false;
        if (jailVisible && _jailButtonObj != null &&
            _jailPlacement == JailUnmuteButtonPlacement.MeetingCard &&
            TryResolveJaileeCard(jailedId, out var jaileeCard))
        {
            PositionJailButtonOnCard(jaileeCard);
            _jailOnCard = true;
        }
        else if (_jailButtonObj != null)
        {
            // Voice-HUD placement (and the fallback when no card resolves): ensure the button
            // is back on the HUD root so PositionButtons can lay it out with mic/speaker.
            var hudRoot = _micButtonObj.transform.parent;
            if (hudRoot != null && _jailButtonObj.transform.parent != hudRoot)
                _jailButtonObj.transform.SetParent(hudRoot, false);
        }

        PositionButtons();

        KeepButtonOnTop(_micButtonObj);
        KeepButtonOnTop(_spkButtonObj);
        KeepButtonOnTop(_jailButtonObj);
    }

    // Finds the jailed player's meeting card so the unmute button can be attached to it.
    // Returns false outside meetings or when the card/background isn't ready (→ HUD fallback).
    private static bool TryResolveJaileeCard(byte jailedId, out PlayerVoteArea card)
    {
        card = null!;
        if (jailedId == byte.MaxValue) return false;
        var meeting = MeetingHud.Instance;
        if (meeting == null || meeting.playerStates == null) return false;
        foreach (var pva in meeting.playerStates)
        {
            if (pva == null || pva.TargetPlayerId != jailedId) continue;
            if (pva.Background == null) return false;
            card = pva;
            return true;
        }
        return false;
    }

    // Parents the unmute button to the jailee's card and pins it at the card's RIGHT edge
    // (same side as the jail/execute UI), vertically centered. The left edge sits in the gap
    // between cards and reads as belonging to the neighbouring card, so the right edge is
    // used. Scale is compensated for the card's world scale so the button is the same
    // on-screen size as in the Voice HUD; draw order is handled by sorting.
    private static void PositionJailButtonOnCard(PlayerVoteArea card)
    {
        if (_jailButtonObj == null) return;
        var bg = card.Background;
        if (bg == null) return;
        var parentT = bg.transform;
        if (_jailButtonObj.transform.parent != parentT)
            _jailButtonObj.transform.SetParent(parentT, false);

        float target = _overlayScale * ButtonScale;
        var ls = parentT.lossyScale;
        _jailButtonObj.transform.localScale = new Vector3(
            Mathf.Approximately(ls.x, 0f) ? target : target / ls.x,
            Mathf.Approximately(ls.y, 0f) ? target : target / ls.y,
            1f);

        var bounds = bg.bounds;
        // Inset (world units) from the card's right edge. The Background sprite carries some
        // transparent padding on the right, so the icon must sit a little inside max.x to
        // land ON the card (in the empty area past the name) instead of hanging in the gap.
        // Single tuning knob: larger = further left (toward the name); smaller = nearer the edge.
        float inset = target * JailCardRightInset;
        var worldPos = new Vector3(bounds.max.x - inset, bounds.center.y, bounds.center.z);
        var local = parentT.InverseTransformPoint(worldPos);
        _jailButtonObj.transform.localPosition = new Vector3(local.x, local.y, -1f);
    }

    private static void RefreshButtonVisuals()
    {
        // ── Mic button ────────────────────────────────────────────────────────
        if (_micButtonObj != null)
        {
            var sr = _micButtonObj.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();

            if (TryGetLocalTransmitBlockReason(out _))
            {
                if (sr != null) { sr.sprite = Sprites.MicOff; sr.color = new Color(1f, 0.65f, 0.15f); }
            }
            else if (_speakerMuted)
            {
                if (sr != null) { sr.sprite = Sprites.MicOff; sr.color = new Color(1f, 0.4f, 0.4f); }
            }
            else if (IsManualMuteActive())
            {
                if (sr != null) { sr.sprite = Sprites.MicOff; sr.color = new Color(1f, 0.4f, 0.4f); }
            }
            else if (IsInTeamRadioMode())
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
        bool radioTransmit   = IsInTeamRadioMode();
        bool pushToTalkMode  = settings?.MicMode.Value == VoiceMicMode.PushToTalk;
        if (pushToTalkMode && _micMuted) _micMuted = false;
        bool pushToTalkMuted = pushToTalkMode && !_pushToTalkHeld && !radioTransmit;
        bool roleMuted       = VoiceRoleMuteState.IsLocalVoiceBlocked(VoiceSceneState.ResolvePhase());
        bool policyMuted     = IsLocalRoomPolicyVoiceBlocked(VoiceSceneState.ResolvePhase());
        VoiceChatRoom.Current?.SetMute(_speakerMuted || _micMuted || pushToTalkMuted || roleMuted || policyMuted);
    }

    internal static void ApplySpeakerState()
    {
        var tab = LocalSettingsTabSingleton<VoiceChatLocalSettings>.Instance;
        VoiceChatRoom.Current?.SetMasterVolume(tab?.MasterVolume.Value ?? 1f);
    }

    internal static float GetEffectiveMasterVolume(float masterVolume)
        => _speakerMuted ? 0f : masterVolume;

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

    internal static void UpdateTeamRadioHold(bool held, bool justPressed, bool justReleased)
    {
        var channel = NormalizeTeamRadioChannel();
        if (channel == VoiceTeamRadioChannel.None)
        {
            if (_teamRadioHeld)
            {
                _teamRadioHeld = false;
                ApplyMicState();
                RefreshButtonVisuals();
            }
            return;
        }

        bool prev     = _teamRadioHeld;
        _teamRadioHeld = held;
        if (prev != _teamRadioHeld) { ApplyMicState(); RefreshButtonVisuals(); }
    }

    internal static void UpdateImpostorRadioHold(bool held, bool justPressed, bool justReleased)
        => UpdateTeamRadioHold(held, justPressed, justReleased);

    internal static bool IsInTeamRadioMode()
        => _teamRadioHeld
        && GetSelectedTeamRadioChannel() != VoiceTeamRadioChannel.None
        && !_speakerMuted
        && !IsManualMuteActive()
        && !TryGetLocalTransmitBlockReason(out _)
        && !TeamRadioBlockedByMeetingPolicy();

    // Gating both input and active-mode prevents entering radio mid-meeting when host forbids it,
    // avoiding a silent hard-mute to non-teammates during discussion.
    private static bool TeamRadioBlockedByMeetingPolicy()
        => !VoiceRoomSettingsState.Current.TeamRadioInMeetings
           && VoiceSceneState.IsMeetingVoicePhase(VoiceSceneState.ResolvePhase());

    internal static bool IsInImpostorRadioMode()
        => IsInTeamRadioMode();

    internal static VoiceTeamRadioChannel ActiveTeamRadioChannel()
        => IsInTeamRadioMode() ? GetSelectedTeamRadioChannel() : VoiceTeamRadioChannel.None;

    internal static VoiceTeamRadioChannel GetSelectedTeamRadioChannel()
        => NormalizeTeamRadioChannel();

    internal static void CycleTeamRadioChannel()
    {
        var next = VoiceRoleMuteState.GetNextTeamRadioChannel(PlayerControl.LocalPlayer, _teamRadioChannel);
        if (next == _teamRadioChannel)
            return;

        _teamRadioChannel = next;
        ApplyMicState();
        RefreshButtonVisuals();
        if (_micTooltip?.activeSelf == true)
            ShowMicTooltip();
    }

    private static VoiceTeamRadioChannel NormalizeTeamRadioChannel()
    {
        if (VoiceRoleMuteState.CanUseTeamRadioChannel(PlayerControl.LocalPlayer, _teamRadioChannel))
            return _teamRadioChannel;

        _teamRadioChannel = VoiceRoleMuteState.GetFirstTeamRadioChannel(PlayerControl.LocalPlayer);
        return _teamRadioChannel;
    }

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
        ApplyMicState();
        ApplySpeakerState();
        if (_speakerMuted) MeetingSpeakingIndicatorPatch.ClearLocalIndicator();
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
        string status = TryGetLocalTransmitBlockReason(out string transmitBlockReason)
            ? transmitBlockReason
            : _speakerMuted ? "Deafened"
            : IsManualMuteActive() ? "Muted"
            : IsInTeamRadioMode() ? $"Team Radio: {VoiceTeamRadioChannels.DisplayName(GetSelectedTeamRadioChannel())} (held)"
            : pushToTalkMode ? "Push To Talk"
            : "Active";
        string muteKey  = VoiceChatKeybinds.ToggleMute.CurrentKey.ToString();
        string radioKey = VoiceChatKeybinds.TeamRadio.CurrentKey.ToString();
        string cycleKey = VoiceChatKeybinds.CycleTeamRadioChannel.CurrentKey.ToString();
        string channel = VoiceTeamRadioChannels.DisplayName(GetSelectedTeamRadioChannel());

        _micTooltipTmp.text =
            "<b>Microphone</b>\n" +
            $"Status: {status}\n" +
            $"Team Radio Channel: {channel}\n" +
            $"Volume: {(int)((tab?.MicVolume.Value ?? 1f) * 100f)}%\n" +
            (pushToTalkMode
                ? $"Push To Talk active  |  Team Radio: {radioKey} (hold)  |  Cycle: {cycleKey}"
                : $"Mute: {muteKey}  |  Team Radio: {radioKey} (hold)  |  Cycle: {cycleKey}");

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

    internal static bool CanUseTeamRadioInput()
        => CanUseTeamRadio() && !TryGetLocalTransmitBlockReason(out _) && !TeamRadioBlockedByMeetingPolicy();

    internal static bool CanUseImpostorRadioInput()
        => CanUseTeamRadioInput();

    private static bool CanUseTeamRadio()
        => PlayerControl.LocalPlayer != null
        && PlayerControl.LocalPlayer.Data?.IsDead == false
        && VoiceRoleMuteState.CanUseTeamRadio(PlayerControl.LocalPlayer);

    private static bool CanUseImpostorRadio()
        => CanUseTeamRadio();

    internal static bool TryGetLocalTransmitBlockReason(out string reason)
    {
        var phase = VoiceSceneState.ResolvePhase();
        if (VoiceRoleMuteState.TryGetLocalVoiceBlockReason(phase, out reason))
            return true;

        if (IsLocalRoomPolicyVoiceBlocked(phase, out var policyReason))
        {
            reason = policyReason;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    internal static bool IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase phase)
    {
        var local = PlayerControl.LocalPlayer;
        return IsLocalRoomPolicyVoiceBlocked(phase, local?.Data?.IsDead == true, out _);
    }

    internal static bool IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase phase, bool localDead)
        => IsLocalRoomPolicyVoiceBlocked(phase, localDead, out _);

    private static bool IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase phase, out string reason)
    {
        var local = PlayerControl.LocalPlayer;
        return IsLocalRoomPolicyVoiceBlocked(phase, local?.Data?.IsDead == true, out reason);
    }

    private static bool IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase phase, bool localDead, out string reason)
    {
        reason = string.Empty;
        var settings = VoiceRoomSettingsState.Current;

        if (settings.OnlyMeetingOrLobby &&
            VoiceSceneState.IsTaskVoicePhase(phase) &&
            (settings.OnlyMeetingOrLobbyAffectsGhosts || !localDead))
        {
            reason = "Meetings/Lobby Only";
            return true;
        }

        if (settings.OnlyGhostsCanTalk &&
            !localDead &&
            (VoiceSceneState.IsTaskVoicePhase(phase) || VoiceSceneState.IsMeetingVoicePhase(phase)))
        {
            reason = "Only Ghosts can Talk/Hear";
            return true;
        }

        return false;
    }

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
