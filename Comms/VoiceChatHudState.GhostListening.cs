using TMPro;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public static partial class VoiceChatHudState
{
    internal enum GhostMicrophoneAudience
    {
        None,
        Muted,
        Deafened,
        PushToTalk,
        GhostsOnly,
    }

    private static GameObject? _ghostListeningNotice;
    private static TextMeshPro? _ghostListeningNoticeText;
    private static Renderer[]? _ghostListeningNoticeRenderers;
    private static TextMeshPro[]? _ghostListeningNoticeTmps;

    internal static GhostMicrophoneAudience ResolveGhostMicrophoneAudience(
        bool ghostOnlySpeaker, bool transmitBlocked, bool microphoneMuted,
        bool speakerDeafened, bool pushToTalkBlocked, bool radioTransmit)
    {
        if (!ghostOnlySpeaker || transmitBlocked || radioTransmit) return GhostMicrophoneAudience.None;
        if (speakerDeafened) return GhostMicrophoneAudience.Deafened;
        if (microphoneMuted) return GhostMicrophoneAudience.Muted;
        return pushToTalkBlocked ? GhostMicrophoneAudience.PushToTalk : GhostMicrophoneAudience.GhostsOnly;
    }

    private static string GhostListeningMicrophoneStatus()
    {
        var phase = VoiceSceneState.ResolvePhase();
        var local = PlayerControl.LocalPlayer;
        if (local == null || local.Data == null) return string.Empty;
        VoiceRoleMuteState.GetVoiceLifeState(local, phase, out bool dead, out bool spectator);
        bool ghostOnly = VoiceGhostListeningPolicy.IsGhostOnlySpeaker(
            VoiceRoomSettingsState.Current, phase, dead, spectator, local.Data.Role?.IsImpostor == true);
        if (!ghostOnly) return string.Empty;
        return ResolveGhostMicrophoneAudience(
            ghostOnly,
            TryGetLocalTransmitBlockReason(out _),
            IsManualMuteActive(),
            _speakerMuted,
            VoiceSettings.Instance?.MicMode.Value == VoiceMicMode.PushToTalk && !_pushToTalkHeld,
            IsInTeamRadioMode()) switch
        {
            GhostMicrophoneAudience.PushToTalk => "Push To Talk",
            GhostMicrophoneAudience.GhostsOnly => "Ghosts only",
            _ => string.Empty,
        };
    }

    private static void UpdateGhostListeningNotice(HudManager? hud)
    {
        bool synced = AmongUsClient.Instance != null &&
            (AmongUsClient.Instance.AmHost || VoiceRoomSettingsState.RemoteSnapshot.HasValue);
        if (hud == null || !VoiceHudWarnings.ShouldShowGhostListeningNotice(
                VoiceRoomSettingsState.Current, VoiceSceneState.ResolvePhase(), synced))
        {
            if (_ghostListeningNotice != null) _ghostListeningNotice.SetActive(false);
            return;
        }
        RenderGhostListeningNotice(hud);
    }

    private static void RenderGhostListeningNotice(HudManager hud)
    {

        var cam = MainCamera();
        if (cam == null) return;
        var root = ResolveHudRoot(hud);
        if (_ghostListeningNotice == null)
        {
            _ghostListeningNotice = CreateCompactStatusObject(root, out var text);
            _ghostListeningNotice.name = "VC_GhostListeningNotice";
            _ghostListeningNoticeText = text;
            text.text = "Nearby ghosts can hear you during tasks, even when living players can’t.";
            text.color = new Color(1f, 0.85f, 0.4f);
            text.fontSize = 1.8f;
            text.alignment = TextAlignmentOptions.BottomRight;
            text.enableWordWrapping = true;
        }
        ReparentToRoot(_ghostListeningNotice, root);
        var safe = NormalizedSafeViewportRect(cam);
        var bottomLeft = cam.ViewportToWorldPoint(new Vector3(safe.XMin, safe.YMin, ButtonViewportDepth));
        var topRight = cam.ViewportToWorldPoint(new Vector3(safe.XMax, safe.YMax, ButtonViewportDepth));
        _ghostListeningNoticeText!.rectTransform.sizeDelta = new Vector2(
            (topRight.x - bottomLeft.x) * 0.38f, (topRight.y - bottomLeft.y) * 0.16f);
        _ghostListeningNotice.SetActive(true);
        var anchor = cam.ViewportToWorldPoint(new Vector3(safe.CenterX, safe.CenterY, ButtonViewportDepth));
        _ghostListeningNotice.transform.position = new Vector3(anchor.x, anchor.y, anchor.z - 1f);
        if (TryGetTextViewportBounds(cam, _ghostListeningNoticeText,
                out float minX, out float maxX, out float minY, out float maxY))
        {
            float controlMinX = float.PositiveInfinity, controlMaxX = float.NegativeInfinity;
            float controlMinY = float.PositiveInfinity, controlMaxY = float.NegativeInfinity;
            IncludeGhostListeningControlBounds(cam, _micButtonObj, _micButtonSrs,
                ref controlMinX, ref controlMaxX, ref controlMinY, ref controlMaxY);
            IncludeGhostListeningControlBounds(cam, _spkButtonObj, _spkButtonSrs,
                ref controlMinX, ref controlMaxX, ref controlMinY, ref controlMaxY);
#if ANDROID
            IncludeGhostListeningControlBounds(cam, _radioTouchButtonObj, _radioTouchButtonSrs,
                ref controlMinX, ref controlMaxX, ref controlMinY, ref controlMaxY);
#endif
            VoiceHudRect controls = float.IsInfinity(controlMinX) ? default :
                new VoiceHudRect(controlMinX, controlMinY, controlMaxX - controlMinX, controlMaxY - controlMinY);
            VoiceHudRect status = default;
            if (_compactStatusObj != null && _compactStatusObj.activeInHierarchy &&
                TryGetTextViewportBounds(cam, _compactStatusTmp,
                    out float statusX, out float statusRight, out float statusY, out float statusTop))
                status = new VoiceHudRect(statusX, statusY, statusRight - statusX, statusTop - statusY);
            if (!TryPlaceGhostListeningNotice(safe, maxX - minX, maxY - minY, controls, status, out var position))
            {
                _ghostListeningNotice.SetActive(false);
                return;
            }
            ShiftGhostListeningNotice(cam, position.X - minX, position.Y - minY);
        }
        KeepTooltipOnTop(_ghostListeningNotice, ref _ghostListeningNoticeRenderers, ref _ghostListeningNoticeTmps);
    }

    internal static bool TryPlaceGhostListeningNotice(
        VoiceHudRect safe, float width, float height, VoiceHudRect controls,
        VoiceHudRect status, out VoiceHudPoint position)
    {
        const float gap = 0.015f;
        float right = safe.XMax - gap - width;
        float bottom = safe.YMin + gap;
        for (int column = 0; column < 5; column++)
        {
            float x = column switch
            {
                0 => right,
                1 => controls.XMin - gap - width,
                2 => status.XMin - gap - width,
                3 => controls.XMax + gap,
                _ => status.XMax + gap,
            };
            if (x < safe.XMin + gap || x > right) continue;
            for (int row = 0; row < 5; row++)
            {
                float y = row switch
                {
                    0 => bottom,
                    1 => controls.YMax + gap,
                    2 => status.YMax + gap,
                    3 => controls.YMin - gap - height,
                    _ => status.YMin - gap - height,
                };
                if (y < bottom || y > safe.YMax - gap - height) continue;
                var notice = new VoiceHudRect(x, y, width, height);
                if (GhostListeningNoticeOverlaps(notice, controls, gap) ||
                    GhostListeningNoticeOverlaps(notice, status, gap)) continue;
                position = new VoiceHudPoint(x, y);
                return true;
            }
        }
        position = default;
        return false;
    }

    private static bool GhostListeningNoticeOverlaps(VoiceHudRect notice, VoiceHudRect obstruction, float gap)
        => obstruction.Width > 0f && obstruction.Height > 0f &&
           notice.XMin < obstruction.XMax + gap && notice.XMax > obstruction.XMin - gap &&
           notice.YMin < obstruction.YMax + gap && notice.YMax > obstruction.YMin - gap;

    private static void IncludeGhostListeningControlBounds(
        Camera cam, GameObject? control, SpriteRenderer[]? renderers,
        ref float minX, ref float maxX, ref float minY, ref float maxY)
    {
        if (control == null || !control.activeInHierarchy) return;
        IncludeProposedButtonViewportBounds(cam, control, renderers, control.transform.position,
            ref minX, ref maxX, ref minY, ref maxY);
    }

    private static void ShiftGhostListeningNotice(Camera cam, float x, float y)
    {
        var origin = cam.ViewportToWorldPoint(new Vector3(0f, 0f, ButtonViewportDepth));
        var shifted = cam.ViewportToWorldPoint(new Vector3(x, y, ButtonViewportDepth));
        var delta = shifted - origin;
        delta.z = 0f;
        _ghostListeningNotice!.transform.position += delta;
    }

    private static void DestroyGhostListeningNotice()
    {
        BestEffortDestroy(ref _ghostListeningNotice);
        _ghostListeningNoticeText = null;
        _ghostListeningNoticeRenderers = null;
        _ghostListeningNoticeTmps = null;
    }
}
