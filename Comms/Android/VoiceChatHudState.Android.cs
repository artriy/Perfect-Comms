#if ANDROID
using TMPro;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android touch ownership, radio feedback, and mobile-only HUD behavior. The desktop build
/// preprocesses this file to nothing, so mobile gestures cannot alter Windows input behavior.
/// </summary>
public static partial class VoiceChatHudState
{
    private static PassiveButton? _radioTouchButton;
    private static GameObject? _radioTouchButtonObj;
    private static SpriteRenderer? _radioTouchIconSr;
    private static SpriteRenderer[]? _radioTouchButtonSrs;
    private static TextMeshPro? _radioTouchLabelTmp;
    private static int _micTouchFingerId = -1;
    private static int _radioTouchFingerId = -1;
    private static float _radioTouchStartTime;
    private static bool _radioTouchTransmitStarted;
    private static bool _touchPushToTalkHeld;
    private static bool _touchTeamRadioHeld;
    private static float _suppressMicClickUntilUnscaledTime;

    private const float RadioTouchHoldThresholdSeconds = 0.18f;
    private const float HandledTouchClickSuppressionSeconds = 0.35f;

    private static void EnsureAndroidRadioTouchButton(HudManager hud, Transform root)
    {
        if (_radioTouchButtonObj != null) return;

        var obj = CreateHudButton(
            hud,
            root,
            "radio-touch",
            "VC_RadioTouchButton",
            "VoiceChatPlugin.Resources.MicOn.png",
            AndroidRadioButtonClickNoOp,
            ShowRadioTooltip,
            hideTooltipOnMouseOut: true,
            out var button,
            out var icon,
            out var renderers);
        _radioTouchButtonObj = obj;
        _radioTouchButton = button;
        _radioTouchIconSr = icon;
        _radioTouchButtonSrs = renderers;
        CreateRadioTouchLabel(obj);
    }

    private static void UpdateAndroidRadioTouchButtonVisibility()
    {
        if (_radioTouchButtonObj == null) return;

        bool wasVisible = _radioTouchButtonObj.activeSelf;
        bool shouldShow = AndroidShouldShowTeamRadioButton(
            CanUseTeamRadio(),
            AndroidTeamRadioPhaseSupportsPrivateRouting(),
            TeamRadioBlockedByMeetingPolicy());
        _radioTouchButtonObj.SetActive(shouldShow);

        // Deactivating a touch button does not guarantee a mouse-out callback. Avoid leaving
        // its shared tooltip floating after a phase or eligibility transition hides the button.
        if (wasVisible && !shouldShow && _sharedMicTooltipOwner == SharedMicTooltipOwner.Radio)
            HideTooltips();
    }

    internal static bool AndroidShouldShowTeamRadioButton(
        bool eligible,
        bool routingSupported,
        bool blockedByPhasePolicy)
        => eligible && routingSupported && !blockedByPhasePolicy;

    internal static bool AndroidTeamRadioInputAvailable(
        bool eligible,
        bool routingSupported,
        bool speakerMuted,
        bool microphoneMuted,
        bool transmitBlocked,
        bool blockedByPhasePolicy)
        => eligible
        && routingSupported
        && !speakerMuted
        && !microphoneMuted
        && !transmitBlocked
        && !blockedByPhasePolicy;

    private static void RefreshAndroidRadioTouchButtonVisuals()
    {
        if (_radioTouchButtonObj == null) return;

        var sr = ResolveIconSr(_radioTouchButtonObj, ref _radioTouchIconSr);
        var radioColor = IsInTeamRadioMode()
            ? new Color(1f, 0.55f, 0.1f)
            : CanUseTeamRadioInput()
                ? new Color(1f, 0.78f, 0.22f)
                : new Color(0.55f, 0.45f, 0.3f);
        if (sr != null)
        {
            sr.sprite = Sprites.MicOn;
            sr.color = radioColor;
        }

        if (_radioTouchLabelTmp == null) return;
        string badge = AndroidTeamRadioChannelBadge(GetSelectedTeamRadioChannel());
        if (_radioTouchLabelTmp.text != badge)
            _radioTouchLabelTmp.text = badge;
        _radioTouchLabelTmp.color = radioColor;
    }

    private static void ApplyAndroidRadioOverlayScale()
    {
        if (_radioTouchButtonObj != null)
            _radioTouchButtonObj.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);
    }

    private static void DestroyAndroidTouchControls()
    {
        // A scene teardown can destroy a touch target without producing TouchPhase.Ended.
        // Close every transmit source before discarding finger ownership so capture cannot latch.
        if (_touchPushToTalkHeld || _touchTeamRadioHeld
            || _micTouchFingerId >= 0 || _radioTouchFingerId >= 0)
            ReleaseTransmitHoldsFailClosed();

        BestEffortDestroy(ref _radioTouchButtonObj);
        _radioTouchButton = null;
        _radioTouchIconSr = null;
        _radioTouchButtonSrs = null;
        _radioTouchLabelTmp = null;
        ResetAndroidTouchState();
    }

    private static void ClampVoiceButtonViewportPositions(
        Camera cam,
        ref Vector3 micPos,
        ref Vector3 spkPos,
        ref Vector3 radioPos,
        ref Vector3 jailPos)
    {
        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        IncludeProposedButtonViewportBounds(
            cam, _micButtonObj, _micButtonSrs, micPos, ref minX, ref maxX, ref minY, ref maxY);
        IncludeProposedButtonViewportBounds(
            cam, _spkButtonObj, _spkButtonSrs, spkPos, ref minX, ref maxX, ref minY, ref maxY);
        if (_radioTouchButtonObj != null && _radioTouchButtonObj.activeSelf)
            IncludeProposedButtonViewportBounds(
                cam, _radioTouchButtonObj, _radioTouchButtonSrs, radioPos,
                ref minX, ref maxX, ref minY, ref maxY);
        if (!_jailOnCard && _jailButtonObj != null && _jailButtonObj.activeSelf)
            IncludeProposedButtonViewportBounds(
                cam, _jailButtonObj, _jailButtonSrs, jailPos,
                ref minX, ref maxX, ref minY, ref maxY);

        var delta = ButtonGroupClampDelta(cam, minX, maxX, minY, maxY);
        micPos += delta;
        spkPos += delta;
        radioPos += delta;
        jailPos += delta;
    }

    private static void AndroidMicButtonClick()
    {
        if (ShouldSuppressHandledTouchClick(
                _micTouchFingerId >= 0,
                Time.unscaledTime,
                _suppressMicClickUntilUnscaledTime))
        {
            _suppressMicClickUntilUnscaledTime = 0f;
            return;
        }

        ToggleMutePublic();
    }

    internal static bool ShouldSuppressHandledTouchClick(
        bool touchStillTracked,
        float unscaledTime,
        float suppressUntilUnscaledTime)
        => touchStillTracked
        || suppressUntilUnscaledTime > 0f && unscaledTime <= suppressUntilUnscaledTime;

    private static void AndroidRadioButtonClickNoOp()
    {
        // Touch down/up is handled explicitly below so a short tap can cycle channels while a
        // sustained press transmits. PassiveButton still owns the visual hit surface.
    }

    private static void CreateRadioTouchLabel(GameObject button)
    {
        var labelObject = new GameObject("RadioLabel");
        labelObject.transform.SetParent(button.transform, false);
        labelObject.transform.localPosition = new Vector3(0.28f, -0.28f, -1f);
        labelObject.layer = button.layer;
        var label = labelObject.AddComponent<TextMeshPro>();
        label.text = "R";
        label.fontSize = 2f;
        label.fontStyle = FontStyles.Bold;
        label.color = new Color(1f, 0.88f, 0.28f);
        label.alignment = TextAlignmentOptions.Center;
        label.enableWordWrapping = false;
        label.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
        label.sortingOrder = ButtonSortOrder + 1;
        label.rectTransform.sizeDelta = new Vector2(0.65f, 0.65f);
        _radioTouchLabelTmp = label;
    }

    internal static string AndroidTeamRadioChannelBadge(VoiceTeamRadioChannel channel)
        => VoiceTeamRadioChannels.Normalize(channel) switch
        {
            VoiceTeamRadioChannel.Impostors => "I",
            VoiceTeamRadioChannel.Vampires => "V",
            VoiceTeamRadioChannel.Lovers => "L",
            VoiceTeamRadioChannel.All => "A",
            _ => "R",
        };

    internal static string AndroidTeamRadioChannelStatus(VoiceTeamRadioChannel channel)
    {
        channel = VoiceTeamRadioChannels.Normalize(channel);
        return VoiceTeamRadioChannels.IsActive(channel)
            ? $"Team Radio: {VoiceTeamRadioChannels.DisplayName(channel)}"
            : "Team Radio unavailable";
    }

    internal static bool AndroidTeamRadioPhaseSupportsPrivateRouting(VoiceGamePhase phase)
        => VoiceSceneState.IsTaskVoicePhase(phase) || VoiceSceneState.IsMeetingVoicePhase(phase);

    private static bool AndroidTeamRadioPhaseSupportsPrivateRouting()
        => AndroidTeamRadioPhaseSupportsPrivateRouting(VoiceSceneState.ResolvePhase());

    private static void UpdateAndroidTouchInput()
    {
        if (VoiceChatPatches.ShouldSuppressVoiceInput())
        {
            ReleaseAndroidTouchInput();
            return;
        }

        UpdateAndroidPushToTalkTouch();
        UpdateAndroidRadioTouch();
    }

    private static void UpdateAndroidPushToTalkTouch()
    {
        if (!IsPushToTalkMode() || _micButtonObj == null || !_micButtonObj.activeInHierarchy)
        {
            ReleaseAndroidPushToTalkTouch();
            return;
        }

        if (_micTouchFingerId >= 0)
        {
            if (!TryFindTouch(_micTouchFingerId, out var tracked)
                || tracked.phase is TouchPhase.Ended or TouchPhase.Canceled)
                ReleaseAndroidPushToTalkTouch();
            return;
        }

        for (int i = 0; i < Input.touchCount; i++)
        {
            var touch = Input.GetTouch(i);
            if (touch.phase != TouchPhase.Began || touch.fingerId == _radioTouchFingerId) continue;
            if (!TouchHitsButton(touch.position, _micButtonObj)) continue;

            _micTouchFingerId = touch.fingerId;
            _touchPushToTalkHeld = true;
            ApplyPushToTalkHeld(_keyboardPushToTalkHeld || _touchPushToTalkHeld);
            break;
        }
    }

    private static void UpdateAndroidRadioTouch()
    {
        if (_radioTouchButtonObj == null
            || !_radioTouchButtonObj.activeInHierarchy
            || !CanUseTeamRadioInput())
        {
            ReleaseAndroidRadioTouch();
            return;
        }

        if (_radioTouchFingerId >= 0)
        {
            if (!TryFindTouch(_radioTouchFingerId, out var tracked))
            {
                ReleaseAndroidRadioTouch();
                return;
            }

            if (tracked.phase is TouchPhase.Ended or TouchPhase.Canceled)
            {
                bool cycle = !_radioTouchTransmitStarted && tracked.phase == TouchPhase.Ended;
                ReleaseAndroidRadioTouch();
                if (cycle && CanUseTeamRadioInput()) CycleTeamRadioChannel();
                return;
            }

            if (!_radioTouchTransmitStarted
                && Time.unscaledTime - _radioTouchStartTime >= RadioTouchHoldThresholdSeconds)
            {
                _radioTouchTransmitStarted = true;
                _touchTeamRadioHeld = true;
                ApplyTeamRadioHold(_keyboardTeamRadioHeld || _touchTeamRadioHeld);
            }
            return;
        }

        for (int i = 0; i < Input.touchCount; i++)
        {
            var touch = Input.GetTouch(i);
            if (touch.phase != TouchPhase.Began || touch.fingerId == _micTouchFingerId) continue;
            if (!TouchHitsButton(touch.position, _radioTouchButtonObj)) continue;

            _radioTouchFingerId = touch.fingerId;
            _radioTouchStartTime = Time.unscaledTime;
            _radioTouchTransmitStarted = false;
            break;
        }
    }

    private static bool TryFindTouch(int fingerId, out Touch touch)
    {
        for (int i = 0; i < Input.touchCount; i++)
        {
            var candidate = Input.GetTouch(i);
            if (candidate.fingerId != fingerId) continue;
            touch = candidate;
            return true;
        }
        touch = default;
        return false;
    }

    private static bool TouchHitsButton(Vector2 screenPosition, GameObject button)
    {
        var cam = MainCamera();
        if (cam == null) return false;
        var world = cam.ScreenToWorldPoint(new Vector3(screenPosition.x, screenPosition.y, ButtonViewportDepth));
        var renderers = button.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var renderer in renderers)
        {
            if (renderer == null || renderer.sprite == null) continue;
            var bounds = renderer.bounds;
            float padding = Mathf.Max(bounds.size.x, bounds.size.y) * 0.30f;
            if (world.x >= bounds.min.x - padding && world.x <= bounds.max.x + padding
                && world.y >= bounds.min.y - padding && world.y <= bounds.max.y + padding)
                return true;
        }
        return false;
    }

    private static void ReleaseAndroidPushToTalkTouch()
    {
        bool handledTouch = _micTouchFingerId >= 0;
        _micTouchFingerId = -1;
        if (handledTouch)
            _suppressMicClickUntilUnscaledTime =
                Time.unscaledTime + HandledTouchClickSuppressionSeconds;
        if (!_touchPushToTalkHeld) return;
        _touchPushToTalkHeld = false;
        ApplyPushToTalkHeld(_keyboardPushToTalkHeld);
    }

    private static void ReleaseAndroidRadioTouch()
    {
        _radioTouchFingerId = -1;
        _radioTouchStartTime = 0f;
        _radioTouchTransmitStarted = false;
        if (!_touchTeamRadioHeld) return;
        _touchTeamRadioHeld = false;
        ApplyTeamRadioHold(_keyboardTeamRadioHeld);
    }

    private static void ReleaseAndroidTouchInput()
    {
        ReleaseAndroidPushToTalkTouch();
        ReleaseAndroidRadioTouch();
    }

    private static void ResetAndroidTouchState()
    {
        _micTouchFingerId = -1;
        _radioTouchFingerId = -1;
        _radioTouchStartTime = 0f;
        _radioTouchTransmitStarted = false;
        _touchPushToTalkHeld = false;
        _touchTeamRadioHeld = false;
        _suppressMicClickUntilUnscaledTime = 0f;
    }

    private static void ShowRadioTooltip()
    {
        if (_micTooltip == null || _micTooltipTmp == null || _radioTouchButtonObj == null) return;

        _sharedMicTooltipOwner = SharedMicTooltipOwner.Radio;

        string channel = VoiceTeamRadioChannels.DisplayName(GetSelectedTeamRadioChannel());
        string status = TryGetLocalTransmitBlockReason(out string transmitBlockReason)
            ? transmitBlockReason
            : _speakerMuted ? "Deafened"
            : IsManualMuteActive() ? "Microphone muted"
            : TeamRadioBlockedByMeetingPolicy() ? "Unavailable in this phase"
            : IsInTeamRadioMode() ? "Transmitting"
            : CanUseTeamRadioInput() ? "Ready"
            : "Unavailable";
        _micTooltipTmp.text =
            "<b>Team Radio</b>\n" +
            $"Status: {status}\n" +
            $"Channel: {channel}\n" +
            "Tap: change channel  |  Hold: transmit";

        PositionNear(_micTooltip, _radioTouchButtonObj);
        KeepTooltipOnTop(_micTooltip, ref _micTooltipRenderers, ref _micTooltipTmps);
        _micTooltip.SetActive(true);
    }
}
#endif
