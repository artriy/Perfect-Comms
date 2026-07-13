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

public static class VoiceChatHudState
{
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
    private static bool _micMuted;
    private static bool _teamRadioHeld;
    private static VoiceTeamRadioChannel _teamRadioChannel = VoiceTeamRadioChannel.None;
    private static bool _pushToTalkHeld;
    private static bool _speakerMuted;
    private static bool _initialized;
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
            });

        var settings = VoiceSettings.Instance;
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
        var settings = VoiceSettings.Instance;
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
        if (_micButtonObj == null || _spkButtonObj == null) return;

        var cam = MainCamera();
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
        // This is deliberately before every HUD/PlayerControl readiness return. The audio engine is
        // room-owned, not HUD-owned: a missing HUD, MapButton or rebuilding LocalPlayer must never
        // leave the microphone in the policy state from the previous phase.
        ApplyAudioPolicy(VoiceChatRoom.Current?.CurrentSnapshot);

        _lastUpdateStep = "readiness.hud";
        var hud = HudManager.Instance;
        if (hud == null) { _lastUpdateStep = "waiting.hud"; return; }

        _lastUpdateStep = "readiness.local-player";
        var localPlayer = PlayerControl.LocalPlayer;
        if (localPlayer == null) { _lastUpdateStep = "waiting.local-player"; return; }

        // LocalPlayer becomes non-null a few frames before its GameData and the lobby HUD template
        // are guaranteed to be ready. Do not run role queries or clone MapButton in that window.
        // This was the timing in the observed one-shot early-lobby NullReferenceException.
        _lastUpdateStep = "readiness.local-data";
        if (localPlayer.Data == null) { _lastUpdateStep = "waiting.local-data"; return; }

        _lastUpdateStep = "readiness.map-button";
        if (hud.MapButton == null) { _lastUpdateStep = "waiting.map-button"; return; }

        _lastUpdateStep = "pending-toast";
        var pendingToast = _pendingToast;
        if (pendingToast != null) { _pendingToast = null; ShowToast(pendingToast); }

        _lastUpdateStep = "ensure-buttons";
        long bTicks = VoiceFrameProfiler.Begin();
        EnsureHudButtons(hud);
        VoiceFrameProfiler.End("hud.buttons", bTicks);
        _lastUpdateStep = "ensure-tooltips";
        long tTicks = VoiceFrameProfiler.Begin();
        EnsureTooltips(hud);
        VoiceFrameProfiler.End("hud.tooltips", tTicks);
        _lastUpdateStep = "ensure-parent";
        EnsureHudParent(hud);
        _lastUpdateStep = "role-state";
        VoiceRoleMuteState.Update();
        _lastUpdateStep = "apply-mic-state";
        ApplyMicState();
        _lastUpdateStep = "button-visibility";
        UpdateHudButtonsVisibility();
        _lastUpdateStep = "button-visuals";
        long vTicks = VoiceFrameProfiler.Begin();
        RefreshButtonVisuals();
        VoiceFrameProfiler.End("hud.visuals", vTicks);
        _lastUpdateStep = "toast";
        UpdateToast(hud);
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

        if (_micButtonObj == null)
        {
            var obj = CreateHudButton(
                hud,
                root,
                "mic",
                "VC_MicButton",
                "VoiceChatPlugin.Resources.MicOn.png",
                ToggleMutePublic,
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

        if (_jailButtonObj == null)
        {
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
        }
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

        KeepButtonOnTop(_micButtonObj, ref _micButtonSrs);
        KeepButtonOnTop(_spkButtonObj, ref _spkButtonSrs);
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
        try
        {
            ApplyAudioPolicyCore(snapshot);
        }
        catch (Exception ex)
        {
            // A mod callback or a Unity object invalidated mid-read must not abort the room update.
            // Fail closed only for gameplay role/policy phases; speaker playback remains untouched.
            var phase = snapshot?.Phase ?? VoiceGamePhase.Unknown;
            var pushToTalkMuted = false;
            try
            {
                pushToTalkMuted = VoiceSettings.Instance?.MicMode.Value == VoiceMicMode.PushToTalk
                                  && !_pushToTalkHeld;
            }
            catch { }
            VoiceChatRoom.Current?.SetMute(CombineTransmitMute(
                _speakerMuted,
                _micMuted,
                pushToTalkMuted,
                ShouldFailClosedWithoutLocalIdentity(phase),
                policyMuted: false));
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
        VoiceChatRoom.Current?.SetMute(CombineTransmitMute(
            _speakerMuted,
            _micMuted,
            pushToTalkMuted,
            roleMuted,
            policyMuted));
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
        bool pushToTalkMuted,
        bool roleMuted,
        bool policyMuted)
        => speakerMuted || manualMuted || pushToTalkMuted || roleMuted || policyMuted;

    internal static void ResetAudioPolicyCache()
        => _lastTrustedLocalAudioState = null;

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
        ApplyMicState();
        RefreshButtonVisuals();
        ShowToast(next == VoiceMicMode.PushToTalk ? "Push To Talk" : "Open Mic");
    }

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
        BestEffortDestroy(ref _micButtonObj);
        BestEffortDestroy(ref _spkButtonObj);
        BestEffortDestroy(ref _jailButtonObj);
        _micButton   = null; _spkButton   = null; _jailButton  = null;
    }

    private static void DestroyTooltips()
    {
        BestEffortDestroy(ref _micTooltip);
        BestEffortDestroy(ref _spkTooltip);
        _micTooltipTmp = null; _spkTooltipTmp = null;
        // Drop the cached child-component arrays so a re-created tooltip re-caches fresh references.
        _micTooltipRenderers = null; _spkTooltipRenderers = null;
        _micTooltipTmps = null; _spkTooltipTmps = null;
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

        var tab = VoiceSettings.Instance;
        bool pushToTalkMode = tab?.MicMode.Value == VoiceMicMode.PushToTalk;
        string status = TryGetLocalTransmitBlockReason(out string transmitBlockReason)
            ? transmitBlockReason
            : _speakerMuted ? "Deafened"
            : IsManualMuteActive() ? "Muted"
            : IsInTeamRadioMode() ? $"Team Radio: {VoiceTeamRadioChannels.DisplayName(GetSelectedTeamRadioChannel())} (held)"
            : pushToTalkMode ? "Push To Talk"
            : "Active";
        string muteKey  = VoiceChatKeybinds.ToggleMute.Label;
        string radioKey = VoiceChatKeybinds.TeamRadio.Label;
        string cycleKey = VoiceChatKeybinds.CycleTeamRadioChannel.Label;
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
        KeepTooltipOnTop(_micTooltip, ref _micTooltipRenderers, ref _micTooltipTmps);
        _micTooltip.SetActive(true);
    }

    private static void ShowSpeakerTooltip()
    {
        if (_spkTooltip == null || _spkTooltipTmp == null || _spkButtonObj == null) return;

        string status = _speakerMuted ? "Muted" : "Active";
        var tab = VoiceSettings.Instance;
        string hotkey = VoiceChatKeybinds.ToggleSpeaker.Label;

        _spkTooltipTmp.text =
            "<b>Speaker</b>\n" +
            $"Status: {status}\n" +
            $"Volume: {(int)((tab?.MasterVolume.Value ?? 0f) * 100f)}%\n" +
            $"Hotkey: {hotkey}";

        PositionNear(_spkTooltip, _spkButtonObj);
        KeepTooltipOnTop(_spkTooltip, ref _spkTooltipRenderers, ref _spkTooltipTmps);
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
        => CanUseTeamRadio() && !TryGetLocalTransmitBlockReason(out _) && !TeamRadioBlockedByMeetingPolicy();

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
        sr.sprite = LoadSprite(resource);
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
