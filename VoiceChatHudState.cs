using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using static UnityEngine.UI.Button;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin.VoiceChat;

public static partial class VoiceChatHudState
{
    internal enum SharedMicTooltipOwner
    {
        None,
        Microphone,
        Radio,
    }

    private static PassiveButton?  _micButton;
    private static GameObject?     _micButtonObj;
    private static PassiveButton?  _spkButton;
    private static GameObject?     _spkButtonObj;
    private static PassiveButton?  _jailButton;
    private static GameObject?     _jailButtonObj;
    // Cached child SpriteRenderers so the per-frame refresh/sort paths avoid Transform.Find + GetComponent
    // and GetComponentsInChildren (managed array alloc + IL2CPP interop) every frame. Captured when the
    // (one-time) buttons are created; re-acquired automatically if a cache entry is null.
    private static SpriteRenderer? _micIconSr;
    private static SpriteRenderer? _spkIconSr;
    private static SpriteRenderer[]? _micButtonSrs;
    private static SpriteRenderer[]? _spkButtonSrs;
    private static SpriteRenderer[]? _jailButtonSrs;
    private const float ButtonScale = 0.42f;
    private const int   ButtonSortOrder = 32760;
    private const int   TooltipSortOrder = 32767;
    private const float TooltipHalfWidth = 1.35f;
    private const float TooltipHalfHeight = 1.25f;
    private const float TooltipButtonGap = 0.35f;
    private const float TooltipViewportPadding = 0.02f;
    private const float ButtonViewportDepth = 10f;
    // Right-edge inset for the jail-unmute button on a meeting card, in multiples of the
    // button's world size. Tuned so the icon sits in the empty area past the name without
    // spilling into the gap between cards. Lower = nearer the right edge.
    private const float JailCardRightInset = 0.37f;
    private static float _btnX = 0.99f;
    private static float _btnY = 0.10f;
    private static VoiceControlsLayout _controlsLayout = VoiceControlsLayout.Vertical;
    private static bool _voiceControlsHudEnabled = true;
    private static JailUnmuteButtonPlacement _jailPlacement = JailUnmuteButtonPlacement.MeetingCard;
    private static bool _jailOnCard;
    private static GameObject?  _micTooltip;
    private static GameObject?  _spkTooltip;
    private static TextMeshPro? _micTooltipTmp;
    private static TextMeshPro? _spkTooltipTmp;
    private static SharedMicTooltipOwner _sharedMicTooltipOwner;
    // Fix 4-HUD-b: KeepTooltipOnTop ran GetComponentsInChildren<Renderer>(true) AND
    // GetComponentsInChildren<TextMeshPro>(true) for BOTH tooltips EVERY frame (the largest avoidable
    // per-frame managed-array alloc on the HUD path). The tooltip hierarchy is static, so cache each
    // child set once (mirrors the _micButtonSrs pattern) and only re-stamp sorting per frame. Both the
    // Renderer set and the TextMeshPro set must be cached/stamped — the TMP's sortingLayerID is stamped
    // on a separate path from the Renderer's sortingLayerName, so dropping either regresses sorting.
    private static Renderer[]?    _micTooltipRenderers;
    private static Renderer[]?    _spkTooltipRenderers;
    private static TextMeshPro[]? _micTooltipTmps;
    private static TextMeshPro[]? _spkTooltipTmps;
    private static GameObject?    _toastObj;
    private static TextMeshPro?   _toastTmp;
    private static Renderer[]?    _toastRenderers;
    private static TextMeshPro[]? _toastTmps;
    private static string _toastMessage = "";
    private static float _toastExpiry;
    private const float ToastDurationSeconds = 4f;
    private const float ToastViewportY = 0.84f;
    private static GameObject?    _compactStatusObj;
    private static TextMeshPro?   _compactStatusTmp;
    private static Renderer[]?    _compactStatusRenderers;
    private static TextMeshPro[]? _compactStatusTmps;
    private static string _compactStatusMessage = "";
    private static float _compactStatusExpiry;
    private static string _lastCompactTransient = "";
    private static string _lastCompactWarning = "";
    private static string _lastCompactRendered = "";
    private static bool _lastCompactMicMuted;
    private static bool _lastCompactDeafened;
    private static bool _lastCompactStateEnabled = true;
    private const float CompactStatusDurationSeconds = 2.5f;
    private const float CompactStatusViewportGap = 0.055f;
    private const float CompactStatusViewportPadding = 0.01f;
    private const float CompactStatusFallbackHalfWidthViewport = 0.145f;
    private const float CompactStatusFallbackHalfHeightViewport = 0.065f;
    private static bool _micMuted;
    private static bool _pushToMuteHeld;
    private static bool _teamRadioHeld;
    private static bool _keyboardTeamRadioHeld;
    private static VoiceTeamRadioChannel _teamRadioChannel = VoiceTeamRadioChannel.None;
    private static bool _pushToTalkHeld;
    private static bool _keyboardPushToTalkHeld;
    private static bool _speakerMuted;
    private static bool _initialized;
    private static int _audioPolicyRevision;
    private static int _lastAudioPolicyRevision = -1;
    private static int _lastAudioPolicyFrame = -1;
    private static VoiceGameStateSnapshot? _lastAudioPolicySnapshot;
    // Audio policy must survive the short windows where Unity has destroyed/recreated the HUD or
    // LocalPlayer but the authenticated voice room is intentionally kept alive. Keep the last
    // trustworthy local snapshot so role/death policy can be evaluated for the *current* phase
    // without depending on a MapButton (or briefly treating an unresolved player as unrestricted).
    private static VoicePlayerSnapshot? _lastTrustedLocalAudioState;
    private static DateTime _nextAudioPolicyErrorLogUtc = DateTime.MinValue;
    private static float _overlayScale = 1.30f;
    // SafeUpdateHud includes this marker in its throttled failure log. IL2CPP exceptions often lose
    // their managed stack, so recording the operation about to run is the only reliable way to
    // distinguish a not-yet-ready HUD template from role-state or scene-teardown failures.
    private static string _lastUpdateStep = "not-started";

    internal static string LastUpdateStep => _lastUpdateStep;

    internal static void RecoverAfterUpdateFailure()
    {
        // Unity's managed wrapper can remain non-null for one frame after its native GameObject was
        // destroyed. Drop every cached wrapper so the next HUD update clones fresh scene objects.
        _cachedMainCamera = null;
        DestroyButtons();
        DestroyTooltips();
        DestroyCompactStatus();
        // Preserve the HUD-independent privacy contract even when setup/role/UI work threw before
        // the normal policy point in UpdateHud.
        ApplyAudioPolicy(VoiceChatRoom.Current?.CurrentSnapshot);
    }

    public static bool IsMuted        => IsManualMuteActive();
    public static bool IsTeamRadio => IsInTeamRadioMode();
    public static bool IsImpostorRadio => IsTeamRadio;
    public static bool IsSpeakerMuted => _speakerMuted;
    internal static bool IsLocalTransmitBlocked => TryGetLocalTransmitBlockReason(out _);

    internal static void ShowToast(string message)
    {
        _toastMessage = message ?? "";
        _toastExpiry = Time.time + ToastDurationSeconds;
    }

    // Time.time is main-thread-only, so off-thread callers (sidecar supervisor / voice worker
    // threads) stash the message here; UpdateHud drains it into ShowToast on the main thread.
    private static volatile string? _pendingToast;

    internal static void ShowToastThreadSafe(string message)
        => _pendingToast = message ?? "";

    /// <summary>
    /// Shows an existing routine voice notification on the small status surface. Deliberately
    /// separate from ShowToast: helper failures and compatibility notices retain the prominent
    /// banner, while refresh/mode/mix confirmations no longer occupy the center of the screen.
    /// </summary>
    internal static void ShowCompactStatus(string message)
    {
        _compactStatusMessage = message ?? "";
        _compactStatusExpiry = Time.unscaledTime + CompactStatusDurationSeconds;
    }

    internal static void Init()
    {
        if (_initialized) return;
        _initialized = true;

        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)((_, __) =>
            {
                _lastUpdateStep = "scene-reset";
                _cachedMainCamera = null;
                DestroyButtons();
                DestroyTooltips();
                DestroyToast();
                DestroyCompactStatus();
            });

        var settings = VoiceSettings.Instance;
        if (settings != null)
        {
            RefreshButtonLayout(settings);
            ApplyOverlayScale(settings.OverlayScale.Value);
            ApplySessionPreferences(settings);
        }
    }

    /// <summary>
    /// Resets process-static HUD/input state for a confirmed new voice session. StartMuted and
    /// StartDeafened are session defaults, not one-time plugin-load defaults; PTT/radio holds must
    /// never carry across lobbies.
    /// </summary>
    internal static void BeginVoiceSession()
    {
        // The setup canvas and its microphone preview are persistent. A newly authenticated room
        // must take capture ownership before any HUD/input state is applied.
        try { VoiceFirstRunSetup.ForceClose(); } catch { }
        _teamRadioHeld = false;
        _keyboardTeamRadioHeld = false;
        _teamRadioChannel = VoiceTeamRadioChannel.None;
        _pushToTalkHeld = false;
        _keyboardPushToTalkHeld = false;
        _pushToMuteHeld = false;
#if ANDROID
        ResetAndroidTouchState();
#endif
        _lastTrustedLocalAudioState = null;

        var settings = VoiceSettings.Instance;
        if (settings != null)
            ApplySessionPreferences(settings);
        else
        {
            // Missing settings during bootstrap is a fail-closed capture state.
            _micMuted = true;
            _speakerMuted = false;
        }
        InvalidateAudioPolicyCache();
    }

    internal static void EndVoiceSession()
    {
        _teamRadioHeld = false;
        _keyboardTeamRadioHeld = false;
        _teamRadioChannel = VoiceTeamRadioChannel.None;
        _pushToTalkHeld = false;
        _keyboardPushToTalkHeld = false;
        _pushToMuteHeld = false;
#if ANDROID
        ResetAndroidTouchState();
#endif
        _lastTrustedLocalAudioState = null;
        _compactStatusMessage = "";
        _compactStatusExpiry = 0f;
        InvalidateAudioPolicyCache();
    }

    internal static void ReleaseTransmitHoldsFailClosed(bool pushToMuteHeld)
    {
        bool changed = _teamRadioHeld || _pushToTalkHeld || _pushToMuteHeld != pushToMuteHeld;
        _teamRadioHeld = false;
        _keyboardTeamRadioHeld = false;
        _pushToTalkHeld = false;
        _keyboardPushToTalkHeld = false;
        _pushToMuteHeld = pushToMuteHeld;
#if ANDROID
        ResetAndroidTouchState();
#endif
        if (!changed) return;

        InvalidateAudioPolicyCache();
        // State is already fail-closed even if a Unity wrapper disappears during the best-effort
        // backend/UI refresh below.
        try { ApplyMicState(); } catch { }
        try { RefreshButtonVisuals(); } catch { }
    }

    private static void ApplySessionPreferences(VoiceChatLocalSettings settings)
    {
        _micMuted = settings.MicMode.Value == VoiceMicMode.PushToTalk
            ? false
            : settings.StartMuted.Value;
        _speakerMuted = settings.StartDeafened.Value;
    }

    internal static void RefreshButtonLayout()
    {
        var settings = VoiceSettings.Instance;
        if (settings == null) return;
        RefreshButtonLayout(settings);
    }

    private static void RefreshButtonLayout(VoiceChatLocalSettings settings)
    {
        _btnX = settings.ButtonPositionX.Value;
        _btnY = settings.ButtonPositionY.Value;
        _controlsLayout = settings.VoiceControlsLayout.Value;
        _voiceControlsHudEnabled = VoiceHudFeatureVisibility.Resolve(
            settings.DisableVoiceControlsHud.Value,
            settings.DisableSpeakingBar.Value).VoiceControlsHudVisible;
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
        if (!_voiceControlsHudEnabled)
        {
            HideVoiceControlsHud();
            return;
        }
        PositionButtons();
    }
    // Camera.main is a FindGameObjectWithTag scan in IL2CPP; cache it and refetch only when null/destroyed (Unity != null detects destroyed).
    private static Camera? _cachedMainCamera;
    private static Camera? MainCamera()
    {
        if (_cachedMainCamera != null) return _cachedMainCamera;
        _cachedMainCamera = Camera.main;
        return _cachedMainCamera;
    }

    private static void PositionButtons()
    {
        if (!_voiceControlsHudEnabled)
        {
            PositionStandaloneJailButton();
            return;
        }

        if (_micButtonObj == null || _spkButtonObj == null) return;

        var cam = MainCamera();
        if (cam == null) return;
        var worldPt = cam.ViewportToWorldPoint(new Vector3(_btnX, _btnY, ButtonViewportDepth));

        float scale = _overlayScale * ButtonScale;
        float spacing = scale * 0.8f;

        Vector3 micPos, spkPos, jailPos;
#if ANDROID
        Vector3 radioPos;
        bool radioInLayout = _radioTouchButtonObj != null && _radioTouchButtonObj.activeSelf;
#endif
        if (_controlsLayout == VoiceControlsLayout.Vertical)
        {
            micPos  = new Vector3(worldPt.x, worldPt.y,             -100f);
            spkPos  = new Vector3(worldPt.x, worldPt.y - spacing,   -100f);
#if ANDROID
            radioPos = radioInLayout
                ? new Vector3(worldPt.x, worldPt.y + spacing, -100f)
                : micPos;
            jailPos = new Vector3(
                worldPt.x,
                worldPt.y + spacing * (radioInLayout ? 2f : 1f),
                -100f);
#else
            jailPos = new Vector3(worldPt.x, worldPt.y + spacing,   -100f);
#endif
        }
        else
        {
            micPos  = new Vector3(worldPt.x,             worldPt.y, -100f);
            spkPos  = new Vector3(worldPt.x + spacing,   worldPt.y, -100f);
#if ANDROID
            radioPos = radioInLayout
                ? new Vector3(worldPt.x + spacing * 2f, worldPt.y, -100f)
                : micPos;
            jailPos = new Vector3(
                worldPt.x + spacing * (radioInLayout ? 3f : 2f),
                worldPt.y,
                -100f);
#else
            jailPos = new Vector3(worldPt.x + spacing * 2f, worldPt.y, -100f);
#endif
        }

        // When the jail button lives on a meeting card, keep it out of the button-group clamp
        // so it can't drag the mic/speaker layout around.
        if (_jailOnCard) jailPos = micPos;
#if ANDROID
        ClampVoiceButtonViewportPositions(cam, ref micPos, ref spkPos, ref radioPos, ref jailPos);
#else
        ClampVoiceButtonViewportPositions(cam, ref micPos, ref spkPos, ref jailPos);
#endif

        var parent = _micButtonObj.transform.parent;
        if (parent != null)
        {
            _micButtonObj.transform.localPosition = parent.InverseTransformPoint(micPos);
            _spkButtonObj.transform.localPosition = parent.InverseTransformPoint(spkPos);
#if ANDROID
            if (_radioTouchButtonObj != null)
                _radioTouchButtonObj.transform.localPosition = parent.InverseTransformPoint(radioPos);
#endif
            if (_jailButtonObj != null && !_jailOnCard)
                _jailButtonObj.transform.localPosition = parent.InverseTransformPoint(jailPos);
        }
        else
        {
            _micButtonObj.transform.position = micPos;
            _spkButtonObj.transform.position = spkPos;
#if ANDROID
            if (_radioTouchButtonObj != null)
                _radioTouchButtonObj.transform.position = radioPos;
#endif
            if (_jailButtonObj != null && !_jailOnCard)
                _jailButtonObj.transform.position = jailPos;
        }
    }
    private static void PositionStandaloneJailButton()
    {
        if (_jailButtonObj == null || _jailOnCard) return;

        var cam = MainCamera();
        if (cam == null) return;

        var jailPos = cam.ViewportToWorldPoint(new Vector3(_btnX, _btnY, ButtonViewportDepth));
        jailPos = new Vector3(jailPos.x, jailPos.y, -100f);

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        IncludeProposedButtonViewportBounds(
            cam,
            _jailButtonObj,
            _jailButtonSrs,
            jailPos,
            ref minX,
            ref maxX,
            ref minY,
            ref maxY);
        jailPos += ButtonGroupClampDelta(cam, minX, maxX, minY, maxY);

        var parent = _jailButtonObj.transform.parent;
        if (parent != null)
            _jailButtonObj.transform.localPosition = parent.InverseTransformPoint(jailPos);
        else
            _jailButtonObj.transform.position = jailPos;
    }


    private static void ClampVoiceButtonViewportPositions(Camera cam, ref Vector3 micPos, ref Vector3 spkPos, ref Vector3 jailPos)
    {
        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        IncludeProposedButtonViewportBounds(
            cam, _micButtonObj, _micButtonSrs, micPos, ref minX, ref maxX, ref minY, ref maxY);
        IncludeProposedButtonViewportBounds(
            cam, _spkButtonObj, _spkButtonSrs, spkPos, ref minX, ref maxX, ref minY, ref maxY);
        if (!_jailOnCard && _jailButtonObj != null && _jailButtonObj.activeSelf)
            IncludeProposedButtonViewportBounds(
                cam, _jailButtonObj, _jailButtonSrs, jailPos,
                ref minX, ref maxX, ref minY, ref maxY);

        var delta = ButtonGroupClampDelta(cam, minX, maxX, minY, maxY);
        micPos += delta;
        spkPos += delta;
        jailPos += delta;
    }

    private static void IncludeProposedButtonViewportBounds(
        Camera cam,
        GameObject? button,
        SpriteRenderer[]? renderers,
        Vector3 proposedPosition,
        ref float minX,
        ref float maxX,
        ref float minY,
        ref float maxY)
    {
        bool includedRenderer = false;
        if (button != null && renderers != null)
        {
            var currentPosition = button.transform.position;
            foreach (var renderer in renderers)
            {
                if (renderer == null || renderer.sprite == null) continue;
                var bounds = renderer.bounds;
                if (bounds.size.x <= 0f || bounds.size.y <= 0f) continue;

                float worldMinX = proposedPosition.x + bounds.min.x - currentPosition.x;
                float worldMaxX = proposedPosition.x + bounds.max.x - currentPosition.x;
                float worldMinY = proposedPosition.y + bounds.min.y - currentPosition.y;
                float worldMaxY = proposedPosition.y + bounds.max.y - currentPosition.y;
                IncludeWorldRectViewportBounds(
                    cam,
                    worldMinX,
                    worldMaxX,
                    worldMinY,
                    worldMaxY,
                    proposedPosition.z,
                    ref minX,
                    ref maxX,
                    ref minY,
                    ref maxY);
                includedRenderer = true;
            }
        }

        if (!includedRenderer)
        {
            var viewport = cam.WorldToViewportPoint(proposedPosition);
            IncludeViewportPoint(viewport, ref minX, ref maxX, ref minY, ref maxY);
        }
    }

    private static void IncludeWorldRectViewportBounds(
        Camera cam,
        float worldMinX,
        float worldMaxX,
        float worldMinY,
        float worldMaxY,
        float worldZ,
        ref float minX,
        ref float maxX,
        ref float minY,
        ref float maxY)
    {
        IncludeViewportPoint(
            cam.WorldToViewportPoint(new Vector3(worldMinX, worldMinY, worldZ)),
            ref minX, ref maxX, ref minY, ref maxY);
        IncludeViewportPoint(
            cam.WorldToViewportPoint(new Vector3(worldMinX, worldMaxY, worldZ)),
            ref minX, ref maxX, ref minY, ref maxY);
        IncludeViewportPoint(
            cam.WorldToViewportPoint(new Vector3(worldMaxX, worldMinY, worldZ)),
            ref minX, ref maxX, ref minY, ref maxY);
        IncludeViewportPoint(
            cam.WorldToViewportPoint(new Vector3(worldMaxX, worldMaxY, worldZ)),
            ref minX, ref maxX, ref minY, ref maxY);
    }

    private static void IncludeViewportPoint(
        Vector3 point,
        ref float minX,
        ref float maxX,
        ref float minY,
        ref float maxY)
    {
        minX = Mathf.Min(minX, point.x);
        maxX = Mathf.Max(maxX, point.x);
        minY = Mathf.Min(minY, point.y);
        maxY = Mathf.Max(maxY, point.y);
    }

    private static Vector3 ButtonGroupClampDelta(
        Camera cam,
        float minX,
        float maxX,
        float minY,
        float maxY)
    {
        if (float.IsInfinity(minX) || float.IsInfinity(maxX)
            || float.IsInfinity(minY) || float.IsInfinity(maxY))
            return Vector3.zero;

        Rect safe = NormalizedSafeViewportRect();
        float shiftX = CalculateViewportShift(minX, maxX, safe.xMin, safe.xMax);
        float shiftY = CalculateViewportShift(minY, maxY, safe.yMin, safe.yMax);
        if (Mathf.Approximately(shiftX, 0f) && Mathf.Approximately(shiftY, 0f))
            return Vector3.zero;

        var origin = cam.ViewportToWorldPoint(new Vector3(0f, 0f, ButtonViewportDepth));
        var shifted = cam.ViewportToWorldPoint(new Vector3(shiftX, shiftY, ButtonViewportDepth));
        var delta = shifted - origin;
        delta.z = 0f;
        return delta;
    }

    internal static float CalculateViewportShift(float min, float max, float minAllowed, float maxAllowed)
    {
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

    internal static Rect NormalizedSafeViewportRect()
    {
        float screenWidth = Mathf.Max(1f, Screen.width);
        float screenHeight = Mathf.Max(1f, Screen.height);
        Rect safe = Screen.safeArea;
        if (safe.width < 1f || safe.height < 1f)
            safe = new Rect(0f, 0f, screenWidth, screenHeight);
        return new Rect(
            safe.xMin / screenWidth,
            safe.yMin / screenHeight,
            safe.width / screenWidth,
            safe.height / screenHeight);
    }
    // P1.2: pre-warm the one-time HUD init off the game-entry frame. The first UpdateHud() otherwise lands the
    // ~24 MB / ~177 ms first-init (embedded-PNG sprite decode + HUD button Instantiate + tooltip GameObject
    // creation) on the worst transition frame, next to the engine scene-load freeze and the peer-join wave. This
    // is called from room construction (the same lifecycle slot as WarmOpusCodec) so the work is already done by
    // the time the HUD first renders. It is fully idempotent: it only does work EnsureHudButtons/EnsureTooltips/
    // LoadSprite would have done on first use, so those per-frame paths remain the fallback (they find the work
    // already done) and HUD appearance/behaviour is unchanged. MUST be called on the Unity main thread (Unity
    // object creation), which the room-construction slot already is.
    public static void Prewarm()
    {
        try
        {
            var settings = VoiceSettings.Instance;
            if (settings != null &&
                !VoiceHudFeatureVisibility.Resolve(
                    settings.DisableVoiceControlsHud.Value,
                    settings.DisableSpeakingBar.Value).VoiceControlsHudVisible)
                return;
            // 1) Decode every embedded-PNG sprite now (the dominant first-init cost). HudManager-independent and
            //    cached in _spriteCache, so the later CreateIconChild/LoadSprite calls are free.
            _ = Sprites.MicOn;
            _ = Sprites.MicOff;
            _ = Sprites.SpkOn;
            _ = Sprites.SpkOff;
            _ = Sprites.JailUnmute;

            // 2) If the HUD already exists, pre-instantiate the buttons (one-per-call by design, so loop to build
            //    all three) and the tooltip GameObjects (INACTIVE) under the real HUD root. If the HUD is not up
            //    yet (common at room construction), skip — the per-frame EnsureHudButtons/EnsureTooltips fallback
            //    builds them, still spread one-button-per-frame, just without the pre-warm head start.
            var hud = HudManager.Instance;
            if (hud != null && hud.MapButton != null)
            {
                // EnsureHudButtons builds at most one button per call; three calls cover mic/spk/jail.
                EnsureHudButtons(hud);
                EnsureHudButtons(hud);
                EnsureHudButtons(hud);
                EnsureTooltips(hud); // CreateTooltipObject sets each tooltip inactive
            }
        }
        catch (Exception ex)
        {
            // Best-effort: any failure just falls back to the per-frame first-use path; never break room setup.
            VoiceDiagnostics.DebugError("[VC] HUD pre-warm failed: " + ex.Message);
        }
    }

    internal static void UpdateHud()
    {
        _lastUpdateStep = "readiness.hud";
        var hud = HudManager.Instance;
        if (hud == null)
        {
#if ANDROID
            ReleaseAndroidTouchInput();
#endif
            ApplyAudioPolicy(VoiceChatRoom.Current?.CurrentSnapshot);
            _lastUpdateStep = "waiting.hud";
            return;
        }

        _lastUpdateStep = "readiness.local-player";
        var localPlayer = PlayerControl.LocalPlayer;
        if (localPlayer == null)
        {
#if ANDROID
            ReleaseAndroidTouchInput();
#endif
            ApplyAudioPolicy(VoiceChatRoom.Current?.CurrentSnapshot);
            _lastUpdateStep = "waiting.local-player";
            return;
        }

        // LocalPlayer becomes non-null a few frames before its GameData and the lobby HUD template
        // are guaranteed to be ready. Do not run role queries or clone MapButton in that window.
        // This was the timing in the observed one-shot early-lobby NullReferenceException.
        _lastUpdateStep = "readiness.local-data";
        if (localPlayer.Data == null)
        {
#if ANDROID
            ReleaseAndroidTouchInput();
#endif
            ApplyAudioPolicy(VoiceChatRoom.Current?.CurrentSnapshot);
            _lastUpdateStep = "waiting.local-data";
            return;
        }

        _lastUpdateStep = "readiness.map-button";
        if (hud.MapButton == null)
        {
#if ANDROID
            ReleaseAndroidTouchInput();
#endif
            ApplyAudioPolicy(VoiceChatRoom.Current?.CurrentSnapshot);
            _lastUpdateStep = "waiting.map-button";
            return;
        }

        _lastUpdateStep = "pending-toast";
        var pendingToast = _pendingToast;
        if (pendingToast != null) { _pendingToast = null; ShowToast(pendingToast); }

        _lastUpdateStep = "ensure-buttons";
        long bTicks = VoiceFrameProfiler.Begin();
        EnsureHudButtons(hud);
        VoiceFrameProfiler.End("hud.buttons", bTicks);
        if (_voiceControlsHudEnabled)
        {
            _lastUpdateStep = "ensure-tooltips";
            long tTicks = VoiceFrameProfiler.Begin();
            EnsureTooltips(hud);
            VoiceFrameProfiler.End("hud.tooltips", tTicks);
            _lastUpdateStep = "ensure-parent";
            EnsureHudParent(hud);
        }
        else
        {
            _lastUpdateStep = "controls-hidden";
            HideVoiceControlsHud();
        }
        _lastUpdateStep = "role-state";
        VoiceRoleMuteState.Update();
        // Apply exactly once after role/cache refresh on the ready-HUD path. The room tick later in
        // this frame sees the same snapshot/revision and reuses this result.
        _lastUpdateStep = "apply-mic-state";
        ApplyMicState();
        _lastUpdateStep = "button-visibility";
        UpdateHudButtonsVisibility();
#if ANDROID
        _lastUpdateStep = "touch-input";
        if (_voiceControlsHudEnabled)
            UpdateAndroidTouchInput();
        else
            ReleaseAndroidTouchInput();
#endif
        if (_voiceControlsHudEnabled)
        {
            _lastUpdateStep = "button-visuals";
            long vTicks = VoiceFrameProfiler.Begin();
            RefreshButtonVisuals();
            VoiceFrameProfiler.End("hud.visuals", vTicks);
        }
        _lastUpdateStep = "toast";
        UpdateToast(hud);
        _lastUpdateStep = "compact-status";
        UpdateCompactStatus(hud);
        _lastUpdateStep = "complete";
    }

    internal static bool ShouldApplyMicStateWhileHudUnavailable(VoiceGamePhase phase) => true;

    internal static string DescribeUpdateContext()
    {
        try
        {
            var hud = HudManager.Instance;
            var local = PlayerControl.LocalPlayer;
            return $"step={_lastUpdateStep} hud={hud != null} mapButton={hud != null && hud.MapButton != null} " +
                   $"local={local != null} localData={local != null && local.Data != null} " +
                   $"micObj={_micButtonObj != null} speakerObj={_spkButtonObj != null} jailObj={_jailButtonObj != null}";
        }
        catch (Exception ex)
        {
            return $"step={_lastUpdateStep} contextProbeFailed={ex.GetType().Name}";
        }
    }

    private static void EnsureHudButtons(HudManager hud)
    {
        if (hud.MapButton == null) return;
        _lastUpdateStep = "buttons.resolve-root";
        var root = ResolveHudRoot(hud);
        if (!_voiceControlsHudEnabled)
        {
            EnsureJailButton(hud, root);
            return;
        }


        if (_micButtonObj == null)
        {
            var obj = CreateHudButton(
                hud,
                root,
                "mic",
                "VC_MicButton",
                "VoiceChatPlugin.Resources.MicOn.png",
#if ANDROID
                AndroidMicButtonClick,
#else
                ToggleMutePublic,
#endif
                ShowMicTooltip,
                hideTooltipOnMouseOut: true,
                out var button,
                out var icon,
                out var renderers);
            _micButtonObj = obj;
            _micButton = button;
            _micIconSr = icon;
            _micButtonSrs = renderers;
            return; // build at most one button per frame: spreads the Instantiate + icon PNG-decode cost
        }

        if (_spkButtonObj == null)
        {
            var obj = CreateHudButton(
                hud,
                root,
                "speaker",
                "VC_SpkButton",
                "VoiceChatPlugin.Resources.SpeakerOn.png",
                ToggleSpeakerPublic,
                ShowSpeakerTooltip,
                hideTooltipOnMouseOut: true,
                out var button,
                out var icon,
                out var renderers);
            _spkButtonObj = obj;
            _spkButton = button;
            _spkIconSr = icon;
            _spkButtonSrs = renderers;
            return; // build at most one button per frame
        }

#if ANDROID
        if (EnsureJailButton(hud, root))
            return; // keep the existing one-button-per-frame initialization budget
        EnsureAndroidRadioTouchButton(hud, root);
#else
        EnsureJailButton(hud, root);
#endif
    }

    private static bool EnsureJailButton(HudManager hud, Transform root)
    {
        if (_jailButtonObj != null) return false;

        var obj = CreateHudButton(
            hud,
            root,
            "jail",
            "VC_JailUnmuteButton",
            "VoiceChatPlugin.Resources.JailUnmute.png",
            JailUnmutePublic,
            onMouseOver: null,
            hideTooltipOnMouseOut: false,
            out var button,
            out _,
            out var renderers);
        _jailButtonObj = obj;
        _jailButton = button;
        _jailButtonSrs = renderers;
        return true;
    }

    private static GameObject CreateHudButton(
        HudManager hud,
        Transform root,
        string diagnosticName,
        string objectName,
        string iconResource,
        Action onClick,
        Action? onMouseOver,
        bool hideTooltipOnMouseOut,
        out PassiveButton button,
        out SpriteRenderer icon,
        out SpriteRenderer[]? renderers)
    {
        GameObject? created = null;
        try
        {
            _lastUpdateStep = $"buttons.{diagnosticName}.template";
            var template = hud.MapButton;
            if (template == null || template.gameObject == null)
                throw new InvalidOperationException($"HUD MapButton template unavailable while creating {diagnosticName}");

            _lastUpdateStep = $"buttons.{diagnosticName}.clone";
            created = Object.Instantiate(template.gameObject, root);
            if (created == null)
                throw new InvalidOperationException($"HUD MapButton clone failed while creating {diagnosticName}");

            created.name = objectName;
            created.transform.localScale = Vector3.one * (_overlayScale * ButtonScale);

            _lastUpdateStep = $"buttons.{diagnosticName}.clear-background";
            ClearButtonBG(created);
            _lastUpdateStep = $"buttons.{diagnosticName}.icon";
            icon = CreateIconChild(created, iconResource);

            _lastUpdateStep = $"buttons.{diagnosticName}.component";
            button = created.GetComponent<PassiveButton>();
            if (button == null)
                throw new InvalidOperationException($"HUD MapButton clone has no PassiveButton while creating {diagnosticName}");

            _lastUpdateStep = $"buttons.{diagnosticName}.events";
            button.OnClick = new ButtonClickedEvent();
            button.OnClick.AddListener(onClick);
            button.OnMouseOver = new UnityEvent();
            if (onMouseOver != null)
                button.OnMouseOver.AddListener(onMouseOver);
            button.OnMouseOut = new UnityEvent();
            if (hideTooltipOnMouseOut)
                button.OnMouseOut.AddListener((Action)HideTooltips);

            _lastUpdateStep = $"buttons.{diagnosticName}.sorting";
            renderers = null;
            KeepButtonOnTop(created, ref renderers);
            return created;
        }
        catch
        {
            // Publish the static object/button references only after this helper returns. If the
            // source hierarchy is transiently invalid, remove the incomplete clone and retry next
            // frame instead of permanently keeping a button without icons or click handlers.
            if (created != null)
            {
                try { Object.Destroy(created); }
                catch { }
            }
            throw;
        }
    }
    private static void EnsureTooltips(HudManager hud)
    {
        var root = ResolveHudRoot(hud);
        if (_micTooltip == null)
            _micTooltip = CreateTooltipObject(root, out _micTooltipTmp);
        if (_spkTooltip == null)
            _spkTooltip = CreateTooltipObject(root, out _spkTooltipTmp);
        KeepTooltipOnTop(_micTooltip, ref _micTooltipRenderers, ref _micTooltipTmps);
        KeepTooltipOnTop(_spkTooltip, ref _spkTooltipRenderers, ref _spkTooltipTmps);
    }

    private static void EnsureHudParent(HudManager hud)
    {
        var root = ResolveHudRoot(hud);
        ReparentToRoot(_micButtonObj, root);
        ReparentToRoot(_spkButtonObj, root);
#if ANDROID
        ReparentToRoot(_radioTouchButtonObj, root);
#endif
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

    private static void HideVoiceControlsHud()
    {
        _micButtonObj?.SetActive(false);
        _spkButtonObj?.SetActive(false);
#if ANDROID
        _radioTouchButtonObj?.SetActive(false);
        ReleaseAndroidTouchInput();
#endif
        HideTooltips();
        if (_compactStatusObj != null && _compactStatusObj.activeSelf)
            _compactStatusObj.SetActive(false);
    }

    private static void UpdateHudButtonsVisibility()
    {
        bool canLocalJailorUnmute = VoiceRoleMuteState.CanLocalJailorUnmute(out byte jailedId);
        VoiceHudControlVisibility visibility = VoiceHudControlVisibilityPolicy.Resolve(
            _voiceControlsHudEnabled,
            canLocalJailorUnmute);

        if (!visibility.PrimaryControlsVisible)
        {
            HideVoiceControlsHud();
        }
        else
        {
            _micButtonObj?.SetActive(true);
            _spkButtonObj?.SetActive(true);
#if ANDROID
            UpdateAndroidRadioTouchButtonVisibility();
#endif
        }

        _jailButtonObj?.SetActive(visibility.JailUnmuteVisible);
        _jailOnCard = false;
        if (visibility.JailUnmuteVisible && _jailButtonObj != null &&
            _jailPlacement == JailUnmuteButtonPlacement.MeetingCard &&
            TryResolveJaileeCard(jailedId, out var jaileeCard))
        {
            PositionJailButtonOnCard(jaileeCard);
            _jailOnCard = true;
        }
        else if (_jailButtonObj != null)
        {
            // Voice-HUD placement (and the fallback when no card resolves) remains independent
            // from the optional microphone/speaker controls.
            var hud = HudManager.Instance;
            if (hud != null)
                ReparentToRoot(_jailButtonObj, ResolveHudRoot(hud));
        }

        PositionButtons();

        if (visibility.PrimaryControlsVisible)
        {
            KeepButtonOnTop(_micButtonObj, ref _micButtonSrs);
            KeepButtonOnTop(_spkButtonObj, ref _spkButtonSrs);
#if ANDROID
            KeepButtonOnTop(_radioTouchButtonObj, ref _radioTouchButtonSrs);
#endif
        }
        KeepButtonOnTop(_jailButtonObj, ref _jailButtonSrs);
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
            var sr = ResolveIconSr(_micButtonObj, ref _micIconSr);

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
            else if (IsPushToTalkMode())
            {
                if (sr != null) { sr.sprite = _pushToTalkHeld ? Sprites.MicOn : Sprites.MicOff; sr.color = _pushToTalkHeld ? Color.white : new Color(0.13f, 0.83f, 0.93f); }
            }
            else
            {
                if (sr != null) { sr.sprite = Sprites.MicOn; sr.color = Color.white; }
            }
        }
        if (_spkButtonObj != null)
        {
            var sr = ResolveIconSr(_spkButtonObj, ref _spkIconSr);

            if (_speakerMuted)
            {
                if (sr != null) { sr.sprite = Sprites.SpkOff; sr.color = new Color(1f, 0.4f, 0.4f); }
            }
            else
            {
                if (sr != null) { sr.sprite = Sprites.SpkOn; sr.color = Color.white; }
            }
        }
#if ANDROID
        RefreshAndroidRadioTouchButtonVisuals();
#endif
    }
    internal static void ApplyMicState()
    {
        ApplyAudioPolicy(VoiceChatRoom.Current?.CurrentSnapshot);
    }

    /// <summary>
    /// Applies transmit policy without requiring any HUD object. A valid live PlayerControl remains
    /// the most authoritative source. During scene reconstruction, the room snapshot (or its last
    /// trusted local entry) preserves role/death state while the freshly-resolved phase is applied.
    /// If neither exists in Tasks/Meeting/Exile, fail closed until identity is trustworthy again.
    /// This only mutes capture; speaker playback and peer signaling remain available for listen-only
    /// users, including clients with no microphone or denied microphone permission.
    /// </summary>
    internal static void ApplyAudioPolicy(VoiceGameStateSnapshot? snapshot)
    {
        int frame = Time.frameCount;
        int revision = _audioPolicyRevision;
        if (_lastAudioPolicyFrame == frame
            && _lastAudioPolicyRevision == revision
            && ReferenceEquals(_lastAudioPolicySnapshot, snapshot))
            return;

        _lastAudioPolicyFrame = frame;
        _lastAudioPolicyRevision = revision;
        _lastAudioPolicySnapshot = snapshot;
        try
        {
            ApplyAudioPolicyCore(snapshot);
        }
        catch (Exception ex)
        {
            // A mod callback or a Unity object invalidated mid-read must not abort the room update.
            // Fail closed only for gameplay role/policy phases; speaker playback remains untouched.
            var phase = snapshot?.Phase ?? VoiceGamePhase.Unknown;
            var pushToTalkMode = false;
            var pushToTalkMuted = false;
            try
            {
                pushToTalkMode = VoiceSettings.Instance?.MicMode.Value == VoiceMicMode.PushToTalk;
                pushToTalkMuted = pushToTalkMode && !_pushToTalkHeld;
            }
            catch { }
            VoiceChatRoom.Current?.SetMicrophonePolicy(
                CombineTransmitMute(
                    _speakerMuted,
                    _micMuted,
                    _pushToMuteHeld,
                    pushToTalkMuted,
                    ShouldFailClosedWithoutLocalIdentity(phase),
                    policyMuted: false),
                keepCaptureWarm: pushToTalkMode);
            if (DateTime.UtcNow >= _nextAudioPolicyErrorLogUtc)
            {
                _nextAudioPolicyErrorLogUtc = DateTime.UtcNow.AddSeconds(2);
                try
                {
                    VoiceDiagnostics.Log(
                        "voice.audio-policy.error",
                        $"phase={phase} errorType={ex.GetType().Name} action={(ShouldFailClosedWithoutLocalIdentity(phase) ? "mute-capture" : "preserve-manual-policy")}");
                }
                catch { }
            }
        }
    }

    private static void ApplyAudioPolicyCore(VoiceGameStateSnapshot? snapshot)
    {
        var settings = VoiceSettings.Instance;
        bool radioTransmit   = IsInTeamRadioMode();
        bool pushToTalkMode  = settings?.MicMode.Value == VoiceMicMode.PushToTalk;
        if (pushToTalkMode && _micMuted) _micMuted = false;
        bool pushToTalkMuted = pushToTalkMode && !_pushToTalkHeld && !radioTransmit;
        var resolvedPhase = VoiceSceneState.ResolvePhase();
        var phase = resolvedPhase == VoiceGamePhase.Unknown && snapshot != null
            ? snapshot.Phase
            : resolvedPhase;

        bool liveLocalReady = false;
        bool roleMuted;
        bool localDead;
        PlayerControl? liveLocal = null;
        try
        {
            liveLocal = PlayerControl.LocalPlayer;
            liveLocalReady = liveLocal != null && liveLocal.Data != null;
        }
        catch { liveLocalReady = false; }

        if (liveLocalReady)
        {
            roleMuted = VoiceRoleMuteState.IsLocalVoiceBlocked(phase);
            localDead = VoiceRoleMuteState.IsVoiceDead(liveLocal);
            if (snapshot != null
                && snapshot.LiveLocalPlayerResolved
                && snapshot.TryGetLocalPlayer(out var currentLocal)
                && currentLocal.IsLocal
                && currentLocal.PlayerId == liveLocal!.PlayerId)
                _lastTrustedLocalAudioState = currentLocal;
        }
        else
        {
            VoicePlayerSnapshot? trusted = null;
            if (snapshot != null
                && (snapshot.LiveLocalPlayerResolved || snapshot.RoutingRosterRetained)
                && snapshot.TryGetLocalPlayer(out var snapshotLocal)
                && snapshotLocal.IsLocal
                && !snapshotLocal.Disconnected)
            {
                trusted = snapshotLocal;
                _lastTrustedLocalAudioState = snapshotLocal;
            }
            else if (_lastTrustedLocalAudioState.HasValue)
            {
                trusted = _lastTrustedLocalAudioState.Value;
            }

            if (trusted.HasValue)
            {
                var local = trusted.Value;
                roleMuted = IsSnapshotRoleVoiceBlocked(local, phase);
                localDead = local.IsDead;
            }
            else
            {
                // An unresolved gameplay identity must not accidentally clear a role or host-policy
                // mute. Lobby/Intro/EndGame are global voice phases and have no such restriction.
                roleMuted = ShouldFailClosedWithoutLocalIdentity(phase);
                localDead = false;
            }
        }

        bool policyMuted = IsLocalRoomPolicyVoiceBlocked(phase, localDead);
        VoiceChatRoom.Current?.SetMicrophonePolicy(
            CombineTransmitMute(
                _speakerMuted,
                _micMuted,
                _pushToMuteHeld,
                pushToTalkMuted,
                roleMuted,
                policyMuted),
            keepCaptureWarm: pushToTalkMode);
    }

    private static bool IsSnapshotRoleVoiceBlocked(VoicePlayerSnapshot local, VoiceGamePhase phase)
    {
        if (VoiceModRegistry.TryGetGlobalGate(VoiceModBridge.ToApiPhase(phase), out _))
            return true;
        if (VoiceSceneState.IsMeetingVoicePhase(phase))
            return VoiceRoleMuteState.IsMeetingVoiceBlocked(local, phase);
        if (VoiceSceneState.IsTaskVoicePhase(phase))
            return VoiceRoleMuteState.IsTaskVoiceBlocked(local);
        return false;
    }

    internal static bool ShouldFailClosedWithoutLocalIdentity(VoiceGamePhase phase)
        => VoiceSceneState.IsMeetingVoicePhase(phase) || VoiceSceneState.IsTaskVoicePhase(phase);

    internal static bool CombineTransmitMute(
        bool speakerMuted,
        bool manualMuted,
        bool pushToMuteMuted,
        bool pushToTalkMuted,
        bool roleMuted,
        bool policyMuted)
        => speakerMuted || manualMuted || pushToMuteMuted || pushToTalkMuted || roleMuted || policyMuted;

    internal static void ResetAudioPolicyCache()
    {
        _lastTrustedLocalAudioState = null;
        InvalidateAudioPolicyCache();
    }

    internal static void InvalidateAudioPolicyCache()
    {
        unchecked { _audioPolicyRevision++; }
        _lastAudioPolicyFrame = -1;
        _lastAudioPolicySnapshot = null;
    }

    internal static void ApplySpeakerState()
    {
        var tab = VoiceSettings.Instance;
        VoiceChatRoom.Current?.SetMasterVolume(tab?.MasterVolume.Value ?? 1f);
    }

    internal static float GetEffectiveMasterVolume(float masterVolume)
        => _speakerMuted ? 0f : masterVolume;

    internal static void TrySyncHostRoomSettings() { }

    internal static bool IsPushToTalkMode()
        => VoiceSettings.Instance?.MicMode.Value == VoiceMicMode.PushToTalk;

    internal static void ToggleMicMode()
    {
        var settings = VoiceSettings.Instance;
        if (settings == null) return;
        var next = settings.MicMode.Value == VoiceMicMode.PushToTalk ? VoiceMicMode.OpenMic : VoiceMicMode.PushToTalk;
        settings.MicMode.Value = next;
        InvalidateAudioPolicyCache();
        ApplyMicState();
        RefreshButtonVisuals();
        ShowCompactStatus(next == VoiceMicMode.PushToTalk ? "Push To Talk" : "Open Mic");
    }

    private static bool IsManualMuteActive()
        => _pushToMuteHeld || (_micMuted && !IsPushToTalkMode());

    internal static void ToggleMutePublic()
    {
        if (IsPushToTalkMode()) { SetMuted(false); return; }
        SetMuted(!_micMuted);
    }

    internal static void SetMuted(bool muted)
    {
        _micMuted = muted && !IsPushToTalkMode();
        InvalidateAudioPolicyCache();
        ApplyMicState();
        if (_micMuted) MeetingSpeakingIndicatorPatch.ClearLocalIndicator();
        RefreshButtonVisuals();
    }

    internal static void UpdatePushToMuteHeld(bool held)
    {
        if (_pushToMuteHeld == held) return;
        _pushToMuteHeld = held;
        InvalidateAudioPolicyCache();
        ApplyMicState();
        if (held) MeetingSpeakingIndicatorPatch.ClearLocalIndicator();
        RefreshButtonVisuals();
    }

    internal static void UpdateTeamRadioHold(bool held, bool justPressed, bool justReleased)
    {
        _keyboardTeamRadioHeld = held;
#if ANDROID
        held = _keyboardTeamRadioHeld || _touchTeamRadioHeld;
#endif
        ApplyTeamRadioHold(held);
    }

    private static void ApplyTeamRadioHold(bool held)
    {
        var channel = NormalizeTeamRadioChannel();
        if (channel == VoiceTeamRadioChannel.None)
        {
            if (_teamRadioHeld)
            {
                _teamRadioHeld = false;
                InvalidateAudioPolicyCache();
                ApplyMicState();
                RefreshButtonVisuals();
            }
            return;
        }

        bool prev     = _teamRadioHeld;
        _teamRadioHeld = held;
        if (prev != _teamRadioHeld)
        {
            InvalidateAudioPolicyCache();
            ApplyMicState();
            RefreshButtonVisuals();
        }
    }

    internal static void UpdateImpostorRadioHold(bool held, bool justPressed, bool justReleased)
        => UpdateTeamRadioHold(held, justPressed, justReleased);

    internal static bool IsInTeamRadioMode()
        => _teamRadioHeld
        && GetSelectedTeamRadioChannel() != VoiceTeamRadioChannel.None
        && !_speakerMuted
        && !IsManualMuteActive()
        && !TryGetLocalTransmitBlockReason(out _)
#if ANDROID
        && AndroidTeamRadioPhaseSupportsPrivateRouting()
#endif
        && !TeamRadioBlockedByMeetingPolicy();

    // Gating both input and active-mode prevents entering radio mid-meeting when host forbids it,
    // avoiding a silent hard-mute to non-teammates during discussion.
    private static bool TeamRadioBlockedByMeetingPolicy()
    {
        var s = VoiceRoomSettingsState.Current;
        var phase = VoiceSceneState.ResolvePhase();
        // Meetings: blocked unless radio is allowed in meetings.
        if (!s.TeamRadioInMeetings && VoiceSceneState.IsMeetingVoicePhase(phase))
            return true;
        // Tasks: blocked when the meeting/lobby radio option is on but its "Usable in Tasks" sub-toggle is off.
        if (s.TeamRadioInMeetings && !s.TeamRadioInTasks && VoiceSceneState.IsTaskVoicePhase(phase))
            return true;
        return false;
    }

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
        {
#if ANDROID
            ShowCompactStatus(AndroidTeamRadioChannelStatus(next));
#endif
            return;
        }

        _teamRadioChannel = next;
        InvalidateAudioPolicyCache();
        ApplyMicState();
        RefreshButtonVisuals();
#if ANDROID
        ShowCompactStatus(AndroidTeamRadioChannelStatus(next));
#endif
        var tooltipOwner = ResolveSharedMicTooltipRefreshOwner(
            _micTooltip?.activeSelf == true,
            _sharedMicTooltipOwner);
#if ANDROID
        if (tooltipOwner == SharedMicTooltipOwner.Radio)
            ShowRadioTooltip();
        else
#endif
        if (tooltipOwner == SharedMicTooltipOwner.Microphone)
            ShowMicTooltip();
    }

    internal static SharedMicTooltipOwner ResolveSharedMicTooltipRefreshOwner(
        bool tooltipActive,
        SharedMicTooltipOwner owner)
        => tooltipActive ? owner : SharedMicTooltipOwner.None;

    private static VoiceTeamRadioChannel NormalizeTeamRadioChannel()
    {
        if (VoiceRoleMuteState.CanUseTeamRadioChannel(PlayerControl.LocalPlayer, _teamRadioChannel))
            return _teamRadioChannel;

        _teamRadioChannel = VoiceRoleMuteState.GetFirstTeamRadioChannel(PlayerControl.LocalPlayer);
        return _teamRadioChannel;
    }

    internal static void UpdatePushToTalkHeld(bool held)
    {
        _keyboardPushToTalkHeld = held;
#if ANDROID
        held = _keyboardPushToTalkHeld || _touchPushToTalkHeld;
#endif
        ApplyPushToTalkHeld(held);
    }

    private static void ApplyPushToTalkHeld(bool held)
    {
        if (_pushToTalkHeld == held) return;
        _pushToTalkHeld = held;
        InvalidateAudioPolicyCache();
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
        InvalidateAudioPolicyCache();
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
#if ANDROID
        ApplyAndroidRadioOverlayScale();
#endif
        PositionButtons();
    }
    private static void DestroyButtons()
    {
#if ANDROID
        DestroyAndroidTouchControls();
#endif
        BestEffortDestroy(ref _micButtonObj);
        BestEffortDestroy(ref _spkButtonObj);
        BestEffortDestroy(ref _jailButtonObj);
        _micButton   = null; _spkButton   = null; _jailButton  = null;
    }

    private static void DestroyTooltips()
    {
        BestEffortDestroy(ref _micTooltip);
        BestEffortDestroy(ref _spkTooltip);
        _sharedMicTooltipOwner = SharedMicTooltipOwner.None;
        _micTooltipTmp = null; _spkTooltipTmp = null;
        // Drop the cached child-component arrays so a re-created tooltip re-caches fresh references.
        _micTooltipRenderers = null; _spkTooltipRenderers = null;
        _micTooltipTmps = null; _spkTooltipTmps = null;
    }

    private static void DestroyCompactStatus()
    {
        BestEffortDestroy(ref _compactStatusObj);
        _compactStatusTmp = null;
        _compactStatusRenderers = null;
        _compactStatusTmps = null;
    }

    private static void BestEffortDestroy(ref GameObject? obj)
    {
        var current = obj;
        obj = null;
        if (current == null) return;
        try { Object.Destroy(current); }
        catch { /* stale IL2CPP wrapper; clearing our reference is the recovery */ }
    }

    // Transient on-screen banner shown on the VC overlay layer (same surface as the mic/speaker
    // icons), so it stays visible above the meeting HUD. Lazily created in the per-frame UpdateHud
    // path, mirroring EnsureHudButtons/EnsureTooltips.
    private static void UpdateToast(HudManager hud)
    {
        bool active = !string.IsNullOrEmpty(_toastMessage) && Time.time < _toastExpiry;
        if (!active)
        {
            if (_toastObj != null && _toastObj.activeSelf) _toastObj.SetActive(false);
            return;
        }

        var root = ResolveHudRoot(hud);
        if (_toastObj == null)
            _toastObj = CreateToastObject(root, out _toastTmp);
        ReparentToRoot(_toastObj, root);

        if (_toastTmp != null && _toastTmp.text != _toastMessage)
            _toastTmp.text = _toastMessage;

        PositionToast();
        KeepTooltipOnTop(_toastObj, ref _toastRenderers, ref _toastTmps);
        if (!_toastObj.activeSelf) _toastObj.SetActive(true);
    }

    private static void PositionToast()
    {
        if (_toastObj == null) return;
        var cam = MainCamera();
        if (cam == null) return;
        var world = cam.ViewportToWorldPoint(new Vector3(0.5f, ToastViewportY, ButtonViewportDepth));
        _toastObj.transform.position = new Vector3(world.x, world.y, world.z - 1f);
    }

    private static GameObject CreateToastObject(Transform root, out TextMeshPro tmp)
    {
        var go = new GameObject("VC_Toast");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, 0f, -80f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localPosition = Vector3.zero;
        tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize = 2.2f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = new Color(1f, 0.85f, 0.4f);
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
        tmp.sortingOrder = TooltipSortOrder;
        tmp.rectTransform.sizeDelta = new Vector2(10f, 1.6f);
        go.SetActive(false);
        return go;
    }

    private static void DestroyToast()
    {
        if (_toastObj != null) { Object.Destroy(_toastObj); _toastObj = null; }
        _toastTmp = null;
        _toastRenderers = null;
        _toastTmps = null;
    }

    private static void UpdateCompactStatus(HudManager hud)
    {
        if (!_voiceControlsHudEnabled)
        {
            if (_compactStatusObj != null && _compactStatusObj.activeSelf)
                _compactStatusObj.SetActive(false);
            return;
        }

        string transient = Time.unscaledTime < _compactStatusExpiry ? _compactStatusMessage : string.Empty;
        if (transient.Length == 0 && _compactStatusMessage.Length != 0)
            _compactStatusMessage = "";

        string warning = VoiceHudWarnings.BuildWarning();
        bool showManualState = VoiceSettings.Instance?.ShowMuteDeafenStatusAlerts.Value ?? true;
        bool microphoneMuted = IsManualMuteActive();
        if (!string.Equals(transient, _lastCompactTransient, StringComparison.Ordinal)
            || !string.Equals(warning, _lastCompactWarning, StringComparison.Ordinal)
            || microphoneMuted != _lastCompactMicMuted
            || _speakerMuted != _lastCompactDeafened
            || showManualState != _lastCompactStateEnabled)
        {
            _lastCompactTransient = transient;
            _lastCompactWarning = warning;
            _lastCompactMicMuted = microphoneMuted;
            _lastCompactDeafened = _speakerMuted;
            _lastCompactStateEnabled = showManualState;
            _lastCompactRendered = VoiceCompactStatusPolicy.Compose(
                transient,
                warning,
                microphoneMuted,
                _speakerMuted,
                showManualState);
        }
        string rendered = _lastCompactRendered;

        if (rendered.Length == 0)
        {
            if (_compactStatusObj != null && _compactStatusObj.activeSelf)
                _compactStatusObj.SetActive(false);
            return;
        }

        var root = ResolveHudRoot(hud);
        if (_compactStatusObj == null)
            _compactStatusObj = CreateCompactStatusObject(root, out _compactStatusTmp);
        ReparentToRoot(_compactStatusObj, root);

        if (_compactStatusTmp != null && _compactStatusTmp.text != rendered)
            _compactStatusTmp.text = rendered;

        // TMP does not publish reliable rendered bounds for an inactive object. Activate before
        // forcing the mesh so PositionCompactStatus can clamp the actual glyph block, including a
        // multi-line grace-period warning, rather than a guessed fixed rectangle.
        if (!_compactStatusObj.activeSelf) _compactStatusObj.SetActive(true);
        PositionCompactStatus();
        KeepTooltipOnTop(_compactStatusObj, ref _compactStatusRenderers, ref _compactStatusTmps);
    }

    private static GameObject CreateCompactStatusObject(Transform root, out TextMeshPro tmp)
    {
        var go = new GameObject("VC_CompactStatus");
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(0f, 0f, -80f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        textGo.transform.localPosition = Vector3.zero;
        tmp = textGo.AddComponent<TextMeshPro>();
        tmp.fontSize = 1.15f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.richText = true;
        tmp.outlineWidth = 0.22f;
        tmp.outlineColor = new Color32(0, 0, 0, 225);
        tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
        tmp.sortingOrder = TooltipSortOrder;
        tmp.rectTransform.sizeDelta = new Vector2(7.5f, 2.2f);
        go.SetActive(false);
        return go;
    }

    private static void PositionCompactStatus()
    {
        if (_compactStatusObj == null) return;
        var cam = MainCamera();
        if (cam == null) return;

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        var fallback = cam.ViewportToWorldPoint(new Vector3(_btnX, _btnY, ButtonViewportDepth));
        IncludeProposedButtonViewportBounds(
            cam,
            _micButtonObj,
            _micButtonSrs,
            _micButtonObj != null ? _micButtonObj.transform.position : fallback,
            ref minX,
            ref maxX,
            ref minY,
            ref maxY);
        IncludeProposedButtonViewportBounds(
            cam,
            _spkButtonObj,
            _spkButtonSrs,
            _spkButtonObj != null ? _spkButtonObj.transform.position : fallback,
            ref minX,
            ref maxX,
            ref minY,
            ref maxY);
        if (_jailButtonObj != null && _jailButtonObj.activeInHierarchy && !_jailOnCard)
            IncludeProposedButtonViewportBounds(
                cam,
                _jailButtonObj,
                _jailButtonSrs,
                _jailButtonObj.transform.position,
                ref minX,
                ref maxX,
                ref minY,
                ref maxY);
#if ANDROID
        if (_radioTouchButtonObj != null && _radioTouchButtonObj.activeInHierarchy)
            IncludeProposedButtonViewportBounds(
                cam,
                _radioTouchButtonObj,
                _radioTouchButtonSrs,
                _radioTouchButtonObj.transform.position,
                ref minX,
                ref maxX,
                ref minY,
                ref maxY);
#endif

        float statusMinX;
        float statusMaxX;
        float statusMinY;
        float statusMaxY;
        if (!TryGetCompactStatusViewportBounds(
                cam, out statusMinX, out statusMaxX, out statusMinY, out statusMaxY))
        {
            var center = cam.WorldToViewportPoint(_compactStatusObj.transform.position);
            statusMinX = center.x - CompactStatusFallbackHalfWidthViewport;
            statusMaxX = center.x + CompactStatusFallbackHalfWidthViewport;
            statusMinY = center.y - CompactStatusFallbackHalfHeightViewport;
            statusMaxY = center.y + CompactStatusFallbackHalfHeightViewport;
        }

        Rect safe = NormalizedSafeViewportRect();
        float alignX = (minX + maxX - statusMinX - statusMaxX) * 0.5f;
        bool placeAbove = (minY + maxY) * 0.5f <= safe.center.y;
        float alignY = placeAbove
            ? maxY + CompactStatusViewportGap - statusMinY
            : minY - CompactStatusViewportGap - statusMaxY;

        float shiftedMinX = statusMinX + alignX;
        float shiftedMaxX = statusMaxX + alignX;
        float shiftedMinY = statusMinY + alignY;
        float shiftedMaxY = statusMaxY + alignY;
        float paddingX = Mathf.Min(CompactStatusViewportPadding, safe.width * 0.25f);
        float paddingY = Mathf.Min(CompactStatusViewportPadding, safe.height * 0.25f);
        float clampX = CalculateViewportShift(
            shiftedMinX, shiftedMaxX, safe.xMin + paddingX, safe.xMax - paddingX);
        float clampY = CalculateViewportShift(
            shiftedMinY, shiftedMaxY, safe.yMin + paddingY, safe.yMax - paddingY);

        var origin = cam.ViewportToWorldPoint(new Vector3(0f, 0f, ButtonViewportDepth));
        var shifted = cam.ViewportToWorldPoint(
            new Vector3(alignX + clampX, alignY + clampY, ButtonViewportDepth));
        var delta = shifted - origin;
        delta.z = 0f;
        _compactStatusObj.transform.position += delta;
    }

    private static bool TryGetCompactStatusViewportBounds(
        Camera cam,
        out float minX,
        out float maxX,
        out float minY,
        out float maxY)
    {
        minX = float.PositiveInfinity;
        maxX = float.NegativeInfinity;
        minY = float.PositiveInfinity;
        maxY = float.NegativeInfinity;
        if (_compactStatusTmp == null) return false;

        _compactStatusTmp.ForceMeshUpdate(ignoreActiveState: true, forceTextReparsing: true);
        var bounds = _compactStatusTmp.textBounds;
        if (bounds.size.x <= 0f || bounds.size.y <= 0f) return false;

        var transform = _compactStatusTmp.transform;
        IncludeViewportPoint(
            cam.WorldToViewportPoint(transform.TransformPoint(
                new Vector3(bounds.min.x, bounds.min.y, bounds.center.z))),
            ref minX, ref maxX, ref minY, ref maxY);
        IncludeViewportPoint(
            cam.WorldToViewportPoint(transform.TransformPoint(
                new Vector3(bounds.min.x, bounds.max.y, bounds.center.z))),
            ref minX, ref maxX, ref minY, ref maxY);
        IncludeViewportPoint(
            cam.WorldToViewportPoint(transform.TransformPoint(
                new Vector3(bounds.max.x, bounds.min.y, bounds.center.z))),
            ref minX, ref maxX, ref minY, ref maxY);
        IncludeViewportPoint(
            cam.WorldToViewportPoint(transform.TransformPoint(
                new Vector3(bounds.max.x, bounds.max.y, bounds.center.z))),
            ref minX, ref maxX, ref minY, ref maxY);
        return !float.IsInfinity(minX) && !float.IsInfinity(maxX)
            && !float.IsInfinity(minY) && !float.IsInfinity(maxY);
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
        // Sorting is stamped by the EnsureTooltips KeepTooltipOnTop calls immediately after creation,
        // which also populate the per-tooltip component caches; no need to stamp the unparented local here.
        go.SetActive(false);
        return go;
    }

    private static void ShowMicTooltip()
    {
        if (_micTooltip == null || _micTooltipTmp == null || _micButtonObj == null) return;

        _sharedMicTooltipOwner = SharedMicTooltipOwner.Microphone;

        var tab = VoiceSettings.Instance;
        bool pushToTalkMode = tab?.MicMode.Value == VoiceMicMode.PushToTalk;
        string status = TryGetLocalTransmitBlockReason(out string transmitBlockReason)
            ? transmitBlockReason
            : _speakerMuted ? "Deafened"
            : IsManualMuteActive() ? "Muted"
            : IsInTeamRadioMode() ? $"Team Radio: {VoiceTeamRadioChannels.DisplayName(GetSelectedTeamRadioChannel())} (held)"
            : pushToTalkMode ? "Push To Talk"
            : "Active";
        string channel = VoiceTeamRadioChannels.DisplayName(GetSelectedTeamRadioChannel());

#if ANDROID
        bool radioVisible = _radioTouchButtonObj?.activeInHierarchy == true;
        _micTooltipTmp.text =
            "<b>Microphone</b>\n" +
            $"Status: {status}\n" +
            (radioVisible ? $"Team Radio Channel: {channel}\n" : string.Empty) +
            $"Volume: {(int)((tab?.MicVolume.Value ?? 1f) * 100f)}%\n" +
            AndroidVoiceUiPolicy.MicrophoneTooltipAction(
                pushToTalkMode,
                radioVisible);
#else
        string muteKey  = VoiceChatKeybinds.ToggleMute.Label;
        string radioKey = VoiceChatKeybinds.TeamRadio.Label;
        string cycleKey = VoiceChatKeybinds.CycleTeamRadioChannel.Label;
        _micTooltipTmp.text =
            "<b>Microphone</b>\n" +
            $"Status: {status}\n" +
            $"Team Radio Channel: {channel}\n" +
            $"Volume: {(int)((tab?.MicVolume.Value ?? 1f) * 100f)}%\n" +
            (pushToTalkMode
                ? $"Push To Talk active  |  Team Radio: {radioKey} (hold)  |  Cycle: {cycleKey}"
                : $"Mute: {muteKey}  |  Team Radio: {radioKey} (hold)  |  Cycle: {cycleKey}");
#endif

        PositionNear(_micTooltip, _micButtonObj);
        KeepTooltipOnTop(_micTooltip, ref _micTooltipRenderers, ref _micTooltipTmps);
        _micTooltip.SetActive(true);
    }

    private static void ShowSpeakerTooltip()
    {
        if (_spkTooltip == null || _spkTooltipTmp == null || _spkButtonObj == null) return;

        string status = _speakerMuted ? "Deafened" : "Active";
        var tab = VoiceSettings.Instance;
#if ANDROID
        _spkTooltipTmp.text =
            "<b>Deafen</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)((tab?.MasterVolume.Value ?? 0f) * 100f)}%\n" +
            AndroidVoiceUiPolicy.SpeakerTooltipAction;
#else
        string hotkey = VoiceChatKeybinds.ToggleSpeaker.Label;
        _spkTooltipTmp.text =
            "<b>Deafen</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)((tab?.MasterVolume.Value ?? 0f) * 100f)}%\n" +
            $"Hotkey: {hotkey}\n" +
            "Mutes playback and pauses microphone transmission.";
#endif

        PositionNear(_spkTooltip, _spkButtonObj);
        KeepTooltipOnTop(_spkTooltip, ref _spkTooltipRenderers, ref _spkTooltipTmps);
        _spkTooltip.SetActive(true);
    }

    private static void HideTooltips()
    {
        _micTooltip?.SetActive(false);
        _spkTooltip?.SetActive(false);
        _sharedMicTooltipOwner = SharedMicTooltipOwner.None;
    }

    private static void PositionNear(GameObject tooltip, GameObject btn)
    {
        var p = btn.transform.position;
        var cam = MainCamera();
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

    private static void KeepTooltipOnTop(
        GameObject? tooltip,
        ref Renderer[]? cachedRenderers,
        ref TextMeshPro[]? cachedTmps)
    {
        if (tooltip == null) return;
        tooltip.transform.SetAsLastSibling();
        VCOverlayCamera.EnsureOnTop(tooltip);
        // The tooltip hierarchy is static, so cache the child component arrays once instead of
        // re-allocating them every frame. (Nulled in DestroyTooltips so a re-created tooltip re-caches.)
        cachedTmps      ??= tooltip.GetComponentsInChildren<TextMeshPro>(true);
        cachedRenderers ??= tooltip.GetComponentsInChildren<Renderer>(true);
        int layerId = SortingLayer.NameToID(VCSorting.Layer);
        foreach (var tmp in cachedTmps)
        {
            if (tmp == null) continue;
            tmp.sortingLayerID = layerId;
            tmp.sortingOrder = TooltipSortOrder;
        }
        foreach (var renderer in cachedRenderers)
        {
            if (renderer == null) continue;
            renderer.sortingLayerName = VCSorting.Layer;
            renderer.sortingOrder = TooltipSortOrder;
        }
    }

    internal static bool CanUseTeamRadioInput()
#if ANDROID
        => AndroidTeamRadioInputAvailable(
            CanUseTeamRadio(),
            AndroidTeamRadioPhaseSupportsPrivateRouting(),
            _speakerMuted,
            IsManualMuteActive(),
            TryGetLocalTransmitBlockReason(out _),
            TeamRadioBlockedByMeetingPolicy());
#else
        => CanUseTeamRadio()
        && !TryGetLocalTransmitBlockReason(out _)
        && !TeamRadioBlockedByMeetingPolicy();
#endif

    internal static bool CanUseImpostorRadioInput()
        => CanUseTeamRadioInput();

    private static bool CanUseTeamRadio()
        => PlayerControl.LocalPlayer != null
        && PlayerControl.LocalPlayer.Data != null
        && !VoiceRoleMuteState.IsVoiceDead(PlayerControl.LocalPlayer)
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
        return IsLocalRoomPolicyVoiceBlocked(phase, VoiceRoleMuteState.IsVoiceDead(local), out _);
    }

    internal static bool IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase phase, bool localDead)
        => IsLocalRoomPolicyVoiceBlocked(phase, localDead, out _);

    private static bool IsLocalRoomPolicyVoiceBlocked(VoiceGamePhase phase, out string reason)
    {
        var local = PlayerControl.LocalPlayer;
        return IsLocalRoomPolicyVoiceBlocked(phase, VoiceRoleMuteState.IsVoiceDead(local), out reason);
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
        foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>(true))
        {
            // A source HUD hierarchy can contain a destroyed component during the first lobby
            // frame or scene teardown. Unity preserves a null slot in this component array.
            if (sr == null) continue;
            sr.sprite = null;
            sr.color = Color.clear;
        }
    }

    private static SpriteRenderer CreateIconChild(GameObject parent, string resource)
    {
        var go = new GameObject("VCIcon");
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = Vector3.zero;
        go.layer = parent.layer;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = LoadControlSprite(resource);
        sr.sortingLayerName = VCSorting.Layer;
        sr.sortingOrder = ButtonSortOrder;
        return sr;
    }

    // Returns the cached "VCIcon" SpriteRenderer for a button, re-acquiring (Transform.Find + GetComponent)
    // only when the cache is empty, so RefreshButtonVisuals does no per-frame interop in the common case.
    private static SpriteRenderer? ResolveIconSr(GameObject button, ref SpriteRenderer? cached)
    {
        if (cached == null)
            cached = button.transform.Find("VCIcon")?.GetComponent<SpriteRenderer>();
        return cached;
    }

    private static void KeepButtonOnTop(GameObject? button, ref SpriteRenderer[]? cachedSrs)
    {
        if (button == null) return;
        button.transform.SetAsLastSibling();
        var pos = button.transform.localPosition;
        button.transform.localPosition = new Vector3(pos.x, pos.y, -100f);
        // GetComponentsInChildren allocates a fresh managed array every call; the button hierarchy is
        // static, so cache it once. (Reset to null at button (re)creation so a new button re-caches.)
        cachedSrs ??= button.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var sr in cachedSrs)
        {
            if (sr == null) continue;
            sr.sortingLayerName = VCSorting.Layer;
            sr.sortingOrder = ButtonSortOrder;
        }
    }

    private static readonly Dictionary<string, Sprite> _spriteCache = new();
    private static readonly Dictionary<string, Sprite> _controlSpriteCache = new();

    private static Sprite LoadControlSprite(string path)
    {
        if (_controlSpriteCache.TryGetValue(path, out var cached)) return cached;

        var source = LoadSprite(path);
        if (source == null) return null!;
        try
        {
            var texture = source.texture;
            var pixels = texture.GetPixels32();
            int minX = texture.width;
            int minY = texture.height;
            int maxX = -1;
            int maxY = -1;
            for (int y = 0; y < texture.height; y++)
            {
                int row = y * texture.width;
                for (int x = 0; x < texture.width; x++)
                {
                    if (pixels[row + x].a == 0) continue;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            if (maxX < minX || maxY < minY)
            {
                _controlSpriteCache[path] = source;
                return source;
            }

            // Keep one transparent texel for bilinear filtering, while excluding the large
            // decorative canvas margins from the edge-clamping footprint.
            minX = Math.Max(0, minX - 1);
            minY = Math.Max(0, minY - 1);
            maxX = Math.Min(texture.width - 1, maxX + 1);
            maxY = Math.Min(texture.height - 1, maxY + 1);
            float width = maxX - minX + 1;
            float height = maxY - minY + 1;
            var rect = new Rect(minX, minY, width, height);
            var originalPivot = source.rect.position + source.pivot;
            var pivot = new Vector2(
                (originalPivot.x - minX) / width,
                (originalPivot.y - minY) / height);
            var tight = Sprite.Create(texture, rect, pivot, source.pixelsPerUnit);
            tight.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _controlSpriteCache[path] = tight;
            return tight;
        }
        catch
        {
            // A non-readable replacement texture is still usable; it simply retains its full
            // sprite rectangle for clamping.
            _controlSpriteCache[path] = source;
            return source;
        }
    }

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
        public static Sprite MicOn  => LoadControlSprite("VoiceChatPlugin.Resources.MicOn.png");
        public static Sprite MicOff => LoadControlSprite("VoiceChatPlugin.Resources.MicOff.png");
        public static Sprite SpkOn  => LoadControlSprite("VoiceChatPlugin.Resources.SpeakerOn.png");
        public static Sprite SpkOff => LoadControlSprite("VoiceChatPlugin.Resources.SpeakerOff.png");
        public static Sprite JailUnmute => LoadControlSprite("VoiceChatPlugin.Resources.JailUnmute.png");
    }
}
