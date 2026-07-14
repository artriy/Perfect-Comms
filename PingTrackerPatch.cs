using HarmonyLib;
using TMPro;
using System.Collections.Generic;
using VoiceChatPlugin.VoiceChat;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

internal static class VCSorting
{
    public const string Layer = "UI";
    public const int    Backdrop = -32768;
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
    // Vertical-stack pitch (vertical layout only). Kept a touch above icon+name height so the lower slot's ring
    // (outer half-height ~0.236 at RingScale) clears the name of the slot above it (name sits at -LabelOffset).
    private const float SlotHeight  = 0.64f;
    private const float LabelOffset = 0.34f;
    // Distance from the icon CENTRE to the icon-facing edge of a Left/Right name. The label's rect pivot is set to
    // that facing edge, so this value is the exact text-edge position; kept just outside the ring (~0.24 half-width)
    // so the name sits snug against the ring without overlapping it.
    private const float LabelSideOffset = 0.30f;
    // Extra vertical-stack pitch added ONLY when the name sits ABOVE the icon (Top), so an above-name never
    // collides with the slot above it (the default SlotHeight is tuned for names BELOW the icon).
    private const float TopNameExtraPitch = 0.30f;
    private const float RingScale   = 0.48f;
    private const float StaleSlotTimeoutSeconds = 2f;
    // BetterCrewLink-style panel: tight, flat padding rather than the old wide shadow halo.
    private const float BackdropPad = 0.05f;
    private const float NameGap = 0.10f;
    private static readonly Color BackdropColor = new(0f, 0f, 0f, 0.5f);
    // Manual-layout mode: viewport placement + edge clamping, mirroring the voice buttons.
    private const float ManualViewportDepth   = 10f;
    private const float ManualViewportPadding = 0.015f;
    // Footprint margin so the bar can hug the edge like the mic/speaker icons while keeping
    // the icon fully on-screen. Larger = more of the icon stays in view near the edge.
    private const float SlotFootprintHalfWorld = 0.16f;
    private static GameObject?       _barRoot;
    private static SortingGroup?     _barSortingGroup; // cached so KeepSpeakingBarOnTop avoids a per-frame GetComponent
    private static SpriteRenderer?   _backdropSR;
    private static Sprite?           _backdropSprite;
    private static float             _nextColorPrewarmTime;
    private const float              ColorPrewarmInterval = 0.25f; // warm one uncached avatar colour per quarter-second
    private static AspectPosition?   _barAspect;
    private static bool              _layoutVertical;
    private static SpeakingBarPosition _barPosition = SpeakingBarPosition.TopRight;
    private static bool              _manualLayout;
    private static float             _manualX = 0.5f;
    private static float             _manualY = 0.85f;
    private static SpeakingBarNamePosition _configuredNamePosition = SpeakingBarNamePosition.Auto;
    private static SpeakingBarNamePosition _namePosition = SpeakingBarNamePosition.Bottom;
    private static bool              _backdropEnabled;
    private static float             _barScale = 1f;
    private static bool              _fixedAllPlayers;
    private static bool              _showFake15Players;
    private static readonly HashSet<byte> _publiclyDead = new();
    private static VoiceGamePhase _previousPublicDeathPhase = VoiceGamePhase.Menu;
    private static bool _previousPublicDeathMeetingActive;
    private static bool _publicDeathPublicationPending;
    private static readonly List<byte> _fixedRoster = new();
    private static readonly HashSet<byte> _fixedRosterSet = new();
    private static readonly Dictionary<byte, SpeakerSlot> _slots = new();
    private static readonly List<byte> _slotOrder = new();
    private static readonly HashSet<byte> _activeSpeakerIds = new();
    private static readonly Dictionary<byte, float> _activeSpeakerLevels = new();
    // Voice-profile names of the current speakers, used to label the end-game avatars where there is no live
    // PlayerControl to read a name from.
    private static readonly Dictionary<byte, string> _activeSpeakerNames = new();
    private static readonly List<byte> _fadedSlotIds = new();
    // Per-frame O(1) player lookup, rebuilt once per Postfix from a single AllPlayerControls pass.
    private static readonly Dictionary<byte, PlayerControl> _playerLookup = new();
    private static bool _layoutDirty;
    // Set when the slot set or an icon changes; gates the expensive per-slot sorting re-stamp.
    private static bool _sortingDirty;
    private static readonly IComparer<byte> PublicRosterComparer = new PublicRosterIdComparer();
    private const byte PreviewPlayerIdStart = 240;
    private const int PreviewPlayerCount = 15;
    private const int PreviewGhostStartIndex = 10;
    private static readonly string[] PreviewPlayerNames =
    {
        "Player 01", "Player 02", "Player 03", "Player 04", "Player 05",
        "Player 06", "Player 07", "Player 08", "Player 09", "Player 10",
        "Ghost 11", "Ghost 12", "Ghost 13", "Ghost 14", "Ghost 15",
    };

    public static void ApplySpeakingBarPosition(SpeakingBarPosition pos)
    {
        _barPosition = pos;
        // Remember the preset even while manual mode owns placement, so toggling manual
        // off later restores the last-chosen preset. Don't disturb manual layout here.
        if (_manualLayout) return;

        _layoutVertical       = IsVerticalPreset(pos);
        _namePosition         = ResolveNamePosition();
        if (_barAspect == null) return;
        _barAspect.enabled = true;
        ApplyPositionToAspect(_barAspect, pos);
        _barAspect.AdjustPosition();
        _layoutDirty = true;
        LayoutSlotsIfDirty();
    }

    // Re-reads the four manual-layout settings and switches the bar between preset mode
    // (AspectPosition drives placement) and manual mode (X/Y sliders + edge clamping).
    public static void ApplySpeakingBarLayoutSettings()
    {
        var settings = VoiceSettings.Instance;
        if (settings == null) return;

        _manualLayout = settings.SpeakingBarManualLayout.Value;
        _manualX      = settings.SpeakingBarX.Value;
        _manualY      = settings.SpeakingBarY.Value;
        _configuredNamePosition = settings.SpeakingBarNamePosition.Value;
        _backdropEnabled = settings.SpeakingBarBackdrop.Value;
        _barScale     = Mathf.Clamp(settings.SpeakingBarScale.Value,
            SpeakingBarScalePolicy.MinimumUserScale,
            SpeakingBarScalePolicy.MaximumUserScale);
        _fixedAllPlayers = settings.SpeakingBarFixedAllPlayers.Value;
        _showFake15Players = settings.ShowFake15Players.Value;

        if (_manualLayout)
        {
            _layoutVertical       = settings.SpeakingBarLayout.Value == VoiceControlsLayout.Vertical;
            if (_barAspect != null) _barAspect.enabled = false;
        }
        else
        {
            _layoutVertical       = IsVerticalPreset(_barPosition);
            // Drop any auto-fit shrink applied while manual mode was active.
            ApplyRootScale();
            if (_barAspect != null)
            {
                _barAspect.enabled = true;
                ApplyPositionToAspect(_barAspect, _barPosition);
                _barAspect.AdjustPosition();
            }
        }

        _namePosition = ResolveNamePosition();

        _layoutDirty = true;
        LayoutSlotsIfDirty();
    }

    private static void ApplyRootScale()
    {
        if (_barRoot != null)
            _barRoot.transform.localScale = Vector3.one * SpeakingBarScalePolicy.ToRenderedScale(_barScale);
    }

    public static void ClearSpeakingBarSlots()
    {
        if (_slots.Count == 0)
        {
            _slotOrder.Clear();
            if (_backdropSR != null) _backdropSR.enabled = false;
            if (_barRoot != null) _barRoot.SetActive(false);
            return;
        }
        foreach (var kv in _slots)
        {
            var slot = kv.Value;
            if (slot.IconGO   != null) Object.Destroy(slot.IconGO);
            if (slot.RingGO   != null) Object.Destroy(slot.RingGO);
            if (slot.LabelTMP != null) Object.Destroy(slot.LabelTMP.gameObject);
        }
        _slots.Clear();
        _slotOrder.Clear();
        if (_backdropSR != null) _backdropSR.enabled = false;
        if (_barRoot != null) _barRoot.SetActive(false);
        _layoutDirty = true;
        _sortingDirty = true;
    }

    private static void UpdatePubliclyDead(VoiceGameStateSnapshot? snapshot)
    {
        if (snapshot == null) return;

        bool reset = SpeakingBarRosterPolicy.IsPublicDeathResetPhase(snapshot.Phase);
        bool publish = SpeakingBarRosterPolicy.IsPublicDeathPublicationBoundary(
            _previousPublicDeathPhase,
            _previousPublicDeathMeetingActive,
            snapshot.Phase,
            snapshot.MeetingActive);

        _previousPublicDeathPhase = snapshot.Phase;
        _previousPublicDeathMeetingActive = snapshot.MeetingActive;
        if (reset)
        {
            _publicDeathPublicationPending = false;
            if (_publiclyDead.Count == 0) return;
            ApplyPublishedDeaths(new HashSet<byte>());
            return;
        }
        if (publish)
            _publicDeathPublicationPending = true;
        if (!_publicDeathPublicationPending) return;
        if (!SpeakingBarRosterPolicy.CanPublishFromSnapshot(
                snapshot.PlayerEnumerationCompleted,
                snapshot.RoutingRosterRetained))
            return;

        _publicDeathPublicationPending = false;

        // Roster work happens only on a public boundary, never on every task frame.
        var next = new HashSet<byte>();
        foreach (var player in snapshot.Players)
            if (player.IsDead)
                next.Add(player.PlayerId);
        ApplyPublishedDeaths(next);
    }

    private static void ApplyPublishedDeaths(HashSet<byte> next)
    {
        if (_publiclyDead.SetEquals(next)) return;

        _publiclyDead.Clear();
        foreach (byte id in next)
            _publiclyDead.Add(id);

        // A public boundary may move several existing slots at once. Keep both modes stable
        // during tasks, then apply the alive-first grouping as one meeting/exile update.
        _fixedRoster.Sort(PublicRosterComparer);
        _slotOrder.Sort(PublicRosterComparer);
        _layoutDirty = true;
    }

    private static void ResetPublicDeathState()
    {
        _publiclyDead.Clear();
        _previousPublicDeathPhase = VoiceGamePhase.Menu;
        _previousPublicDeathMeetingActive = false;
        _publicDeathPublicationPending = false;
    }

    private static SpeakingBarNamePosition ResolveNamePosition()
    {
        if (_configuredNamePosition != SpeakingBarNamePosition.Auto)
            return _configuredNamePosition;

        if (!_manualLayout)
            return SpeakingBarLayoutPolicy.RecommendedNamePositionFor(_barPosition);

        // Manual mode points labels into the screen from the nearest edge. Horizontal-edge
        // ties win at corners so left/right placements stay compact beside vertical columns.
        float leftRightDistance = Mathf.Min(_manualX, 1f - _manualX);
        float topBottomDistance = Mathf.Min(_manualY, 1f - _manualY);
        if (leftRightDistance > 0.33f && topBottomDistance > 0.33f)
            return _layoutVertical ? SpeakingBarNamePosition.Right : SpeakingBarNamePosition.Bottom;
        if (leftRightDistance <= topBottomDistance)
            return _manualX <= 0.5f ? SpeakingBarNamePosition.Right : SpeakingBarNamePosition.Left;

        return _manualY >= 0.5f ? SpeakingBarNamePosition.Bottom : SpeakingBarNamePosition.Top;
    }

    private static bool IsPreviewPlayerId(byte playerId)
        => playerId >= PreviewPlayerIdStart && playerId < PreviewPlayerIdStart + PreviewPlayerCount;

    private static int PreviewIndex(byte playerId)
        => playerId - PreviewPlayerIdStart;

    private static bool IsPreviewGhost(byte playerId)
        => IsPreviewPlayerId(playerId) && PreviewIndex(playerId) >= PreviewGhostStartIndex;

    private static float PreviewVoiceLevel(int previewIndex)
        => previewIndex switch
        {
            0 => 0.86f,
            4 => 0.58f,
            9 => 0.74f,
            12 => 0.92f,
            _ => 0f,
        };

    private static bool IsFixedEligible(VoicePlayerSnapshot p)
        // IsVisible is derived from the transient Unity GameObject state, which commonly flips while
        // MeetingHud/exile UI takes ownership of the scene.  Fixed All Players is a connected-client
        // roster, so world-render visibility must never remove a legitimate slot.
        => !p.Disconnected && !p.IsDummy && p.ClientId >= 0;

    private static void EnsureFixedRosterSlots(VoiceGameStateSnapshot snapshot)
    {
        _fixedRoster.Clear();
        _fixedRosterSet.Clear();
        foreach (var p in snapshot.Players)
        {
            if (!IsFixedEligible(p)) continue;
            if (_fixedRosterSet.Add(p.PlayerId))
                _fixedRoster.Add(p.PlayerId);
        }
        _fixedRoster.Sort(PublicRosterComparer);

        foreach (byte id in _fixedRoster)
        {
            var player = FindPlayer(id);
            if (_slots.TryGetValue(id, out var existing))
            {
                // A retained transition snapshot can briefly outlive the world PlayerControl while
                // MeetingHud/exile objects are being swapped. Preserve the already-correct slot rather
                // than fingerprinting null as default and flashing a rebuilt "?" identity.
                if (player == null)
                    continue;

                if (existing.IconGO == null || existing.RingGO == null || existing.RingRenderer == null || existing.LabelTMP == null)
                {
                    RemoveSlot(id);
                    AddSlot(id, 0f);
                    continue;
                }
                else if (existing.Fingerprint != GetFingerprint(id))
                {
                    // A non-speaking roster slot still has to follow identity/concealment changes (comms
                    // sabotage clearing, a morph the roster should ignore) — the active-speaker loop never
                    // re-fingerprints these, so rebuild here when the live fingerprint drifts.
                    TryCreateSlotIcon(id, existing, replaceExisting: true);
                    UpdateSlotLabel(existing, player);
                    continue;
                }
                else if (!existing.CosmeticsComplete && player != null && CrewmateAvatarRenderer.OutfitCosmeticsResolved(player))
                {
                    CrewmateAvatarRenderer.TryRefreshOutfitCosmetics(existing.IconGO, player, id);
                    existing.CosmeticsComplete = true;
                    existing.GhostAlphaIcon = null;
                    _layoutDirty = true;
                    _sortingDirty = true;
                }
                if (existing.LabelTMP != null)
                {
                    string liveName = GetDisplayName(player);
                    if (existing.LabelTMP.text != liveName)
                        UpdateSlotLabel(existing, player);
                }
                continue;
            }
            if (player == null)
                continue;
            AddSlot(id, 0f);
            if (_slots.TryGetValue(id, out var slot) && !_activeSpeakerIds.Contains(id))
            {
                slot.IsSpeaking = false;
                slot.TargetLevel = 0f;
            }
        }
    }

    private static void ApplyFixedRosterOrder()
    {
        int desiredCount = 0;
        foreach (byte id in _fixedRoster)
            if (_slots.ContainsKey(id))
                desiredCount++;

        bool changed = _slotOrder.Count != desiredCount;
        if (!changed)
        {
            int orderIndex = 0;
            foreach (byte id in _fixedRoster)
            {
                if (!_slots.ContainsKey(id)) continue;
                if (_slotOrder[orderIndex++] == id) continue;
                changed = true;
                break;
            }
        }
        if (!changed) return;

        _slotOrder.Clear();
        foreach (byte id in _fixedRoster)
            if (_slots.ContainsKey(id))
                _slotOrder.Add(id);
        _layoutDirty = true;
    }

    private static void ApplyPublicGhostAlpha()
    {
        foreach (var kv in _slots)
        {
            bool ghost = _showFake15Players ? IsPreviewGhost(kv.Key) : _publiclyDead.Contains(kv.Key);
            float target = ghost ? 0.45f : 1f;
            var slot = kv.Value;
            if (slot.IconGO == null)
            {
                slot.GhostAlphaIcon = null;
                slot.AppliedGhostAlpha = 1f;
                continue;
            }
            if (ReferenceEquals(slot.GhostAlphaIcon, slot.IconGO) && Mathf.Approximately(slot.AppliedGhostAlpha, target))
                continue;
            SetIconAlpha(slot.IconGO, target);
            slot.GhostAlphaIcon = slot.IconGO;
            slot.AppliedGhostAlpha = target;
        }
    }

    private static void SetIconAlpha(GameObject iconGO, float alpha)
    {
        foreach (var sr in iconGO.GetComponentsInChildren<SpriteRenderer>(true))
        {
            var c = sr.color;
            c.a = alpha;
            sr.color = c;
        }
    }

    private static bool IsVerticalPreset(SpeakingBarPosition pos)
        => SpeakingBarLayoutPolicy.OrientationFor(pos) == VoiceControlsLayout.Vertical;

    static void Postfix(PingTracker __instance)
    {
        if (__instance?.text == null) return;
        try { RenderOverlay(__instance); }
        catch (System.Exception ex) { LogOverlayError("PingTracker overlay", ex); }
    }

    // Drives the speaking-bar overlay on the end-game results screen, where no PingTracker ticks (its Update
    // postfix never fires) and the in-game HUD is gone. Called per-frame from VCManager while EndGameManager is
    // active so win-screen reactions show who is talking. Self-gates to the EndGame phase; cheap no-op otherwise.
    internal static void RenderEndGameOverlay()
    {
        if (!VoiceSceneState.IsEndGameActive || Camera.main == null) return;
        try { RenderOverlay(null); }
        catch (System.Exception ex) { LogOverlayError("EndGame overlay", ex); }
    }

    private static void RenderOverlay(PingTracker? __instance)
    {
        long overlayTicks = VoiceFrameProfiler.Begin();
        long ensureBarTicks = VoiceFrameProfiler.Begin();
        EnsureBar(__instance);
        VoiceFrameProfiler.End("overlay.ensurebar", ensureBarTicks);
        if (_barRoot == null) { VoiceFrameProfiler.End("overlay.pingtracker", overlayTicks); return; }

        try
        {
            var room = VoiceChatRoom.Current;
            // Warm one not-yet-cached avatar colour per quarter-second so the ~60-75ms base-sprite build
            // happens during idle time, not the instant a new-coloured player first speaks.
            if (room != null && Time.time >= _nextColorPrewarmTime)
            {
                _nextColorPrewarmTime = Time.time + ColorPrewarmInterval;
                long pwTicks = VoiceFrameProfiler.Begin();
                CrewmateAvatarRenderer.PrewarmNextColor();
                VoiceFrameProfiler.End("overlay.prewarm", pwTicks);
            }
            long privacyTicks = VoiceFrameProfiler.Begin();
            var overlay = VoiceOverlayState.Current(room);
            var privacyPhase = VoiceSceneState.ResolvePhase();
            var privacy = VoiceIdentityPrivacyRuntime.Current(overlay, privacyPhase);
            VoiceFrameProfiler.End("overlay.privacy", privacyTicks);
            _activeSpeakerIds.Clear();
            _activeSpeakerLevels.Clear();
            _activeSpeakerNames.Clear();
            var snapshot = room?.CurrentSnapshot;

            var presentedSpeakers = privacy.Speakers;
            for (int i = 0; i < presentedSpeakers.Count; i++)
            {
                var speaker = presentedSpeakers[i];
                byte presentationId = speaker.PresentationPlayerId;
                _activeSpeakerIds.Add(presentationId);
                if (!_activeSpeakerLevels.TryGetValue(presentationId, out float existingLevel)
                    || speaker.Level > existingLevel)
                {
                    _activeSpeakerLevels[presentationId] = speaker.Level;
                }

                if (VoiceIdentityPrivacyRuntime.TryFindPlayer(presentationId, out var presentationPlayer)
                    && presentationPlayer.Data != null)
                {
                    _activeSpeakerNames[presentationId] = presentationPlayer.Data.PlayerName;
                }
                else if (snapshot != null
                         && snapshot.TryGetPlayer(presentationId, out var presentationSnapshot)
                         && !string.IsNullOrWhiteSpace(presentationSnapshot.PlayerName))
                {
                    _activeSpeakerNames[presentationId] = presentationSnapshot.PlayerName;
                }
                else if (presentationId == speaker.SourcePlayerId)
                {
                    // A normal source may use its authenticated voice-profile name while its live
                    // control is rebuilding. Never copy the source name onto a distinct alias slot.
                    var remotes = overlay.RemotePlayers;
                    for (int j = 0; j < remotes.Count; j++)
                    {
                        if (remotes[j].PlayerId != speaker.SourcePlayerId) continue;
                        _activeSpeakerNames[presentationId] = remotes[j].PlayerName;
                        break;
                    }
                }
            }

            UpdatePubliclyDead(snapshot);
            if (_showFake15Players)
            {
                // Keep the real meeting indicators live, but replace only the local speaking bar.
                // Preview slots never enter the privacy, transport, roster, or audio paths.
                if (MeetingHud.Instance == null)
                    MeetingSpeakingIndicatorPatch.UpdateIndicators(overlay);
                _playerLookup.Clear();
                RenderFakePreview(__instance);
                VoiceFrameProfiler.End("overlay.pingtracker", overlayTicks);
                return;
            }

            // Rebuild the FindPlayer lookup only when it will actually be read this frame — i.e. when
            // someone is speaking (the active-speaker loop calls FindPlayer/GetFingerprint) or slots are
            // still fading out. When the overlay is idle (nobody speaking, no slots), skip the per-frame
            // AllPlayerControls IL2CPP walk entirely.
            if (_activeSpeakerIds.Count > 0 || _slots.Count > 0 || _fixedAllPlayers)
                RebuildPlayerLookup();
            else
                _playerLookup.Clear();

            if (MeetingHud.Instance == null)
                MeetingSpeakingIndicatorPatch.UpdateIndicators(overlay);

            // Fixed all-players is a roster: show real identities, never live disguises (a meeting forces this too).
            CrewmateAvatarRenderer.PreferRealIdentity = _fixedAllPlayers;
            bool fixedActive = _fixedAllPlayers && snapshot != null;

            foreach (var kv in _slots)
            {
                kv.Value.IsSpeaking = false;
                kv.Value.TargetLevel = 0f;
            }

            if (!fixedActive && _slots.Count > 0)
            {
                // A dynamic slot from the previous utterance must disappear immediately when its
                // source becomes hidden or is now presented as an alias. Letting it fade would leave
                // a short-lived real-identity breadcrumb during the concealment transition.
                _fadedSlotIds.Clear();
                foreach (var kv in _slots)
                {
                    var resolution = VoiceIdentityPrivacyRuntime.Peek(kv.Key, privacyPhase);
                    if (VoiceIdentityPrivacyRuntime.ShouldSnapPresentation(kv.Key)
                        || !resolution.HasConcretePresentation
                        || resolution.PresentationPlayerId != kv.Key)
                        _fadedSlotIds.Add(kv.Key);
                }
                foreach (byte id in _fadedSlotIds) RemoveSlot(id);
            }

            if (fixedActive)
                EnsureFixedRosterSlots(snapshot!);

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

                        // Defer a *color-only* fingerprint change by one frame: the live body color can
                        // read a transient wrong value for a single frame during cosmetics init / role
                        // swaps, and we don't want to destroy+recreate the icon GameObject for a flicker.
                        // Structural changes (outfit type, hat/skin/visor/name) always rebuild immediately
                        // so the icon never goes stale.
                        bool structuralChange = !StructureMatches(slot.Fingerprint, liveFp);
                        if (!structuralChange && slot.PendingFingerprint != liveFp)
                        {
                            slot.PendingFingerprint = liveFp; // stage; rebuild next frame only if it persists
                            continue;
                        }

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
                        else if (!slot.CosmeticsComplete && player != null && CrewmateAvatarRenderer.OutfitCosmeticsResolved(player))
                        {
                            // The player's cosmetics finished loading — attach them in place (no destroy/recreate pop).
                            CrewmateAvatarRenderer.TryRefreshOutfitCosmetics(slot.IconGO, player, id);
                            slot.CosmeticsComplete = true;
                            slot.GhostAlphaIcon = null;
                            _layoutDirty = true;
                            _sortingDirty = true;
                        }
                        continue;
                    }
                }
                AddSlot(id, level);
            }

            if (fixedActive)
            {
                foreach (var kv in _slots)
                {
                    var currentResolution = VoiceIdentityPrivacyRuntime.Peek(kv.Key, privacyPhase);
                    bool slotIdentityBecamePrivate = !_activeSpeakerIds.Contains(kv.Key)
                                                     && (!currentResolution.HasConcretePresentation
                                                         || currentResolution.PresentationPlayerId != kv.Key);
                    if (privacy.HideAllForViewer
                        || privacy.DimAll
                        || VoiceIdentityPrivacyRuntime.ShouldSnapPresentation(kv.Key)
                        || slotIdentityBecamePrivate)
                    {
                        SnapSlotRing(kv.Value);
                    }
                }
            }

            if (_slots.Count > 0 && !_barRoot.activeSelf) _barRoot.SetActive(true);

            UpdateSlotRings();

            if (fixedActive)
            {
                _fadedSlotIds.Clear();
                foreach (var kv in _slots)
                    if (!_fixedRosterSet.Contains(kv.Key))
                        _fadedSlotIds.Add(kv.Key);
                foreach (var id in _fadedSlotIds) RemoveSlot(id);
                ApplyFixedRosterOrder();
            }
            else
            {
                _fadedSlotIds.Clear();
                foreach (var kv in _slots)
                    if ((!kv.Value.IsSpeaking && kv.Value.Visibility <= 0.01f) || ShouldForceRemoveSlot(kv.Key, kv.Value))
                        _fadedSlotIds.Add(kv.Key);
                foreach (var id in _fadedSlotIds) RemoveSlot(id);
                ApplyDynamicRosterOrder();
            }

            ApplyPublicGhostAlpha();

            LayoutSlotsIfDirty();

            _barRoot.SetActive(_slots.Count > 0);
            KeepSpeakingBarOnTop(__instance);
        }
        catch (System.Exception ex)
        {
            LogOverlayError("PingTracker overlay update", ex);
        }
        VoiceFrameProfiler.End("overlay.pingtracker", overlayTicks);
    }

    private static void RenderFakePreview(PingTracker? template)
    {
        if (_barRoot == null) return;

        _activeSpeakerIds.Clear();
        _activeSpeakerLevels.Clear();
        _activeSpeakerNames.Clear();
        EnsureFakePreviewSlots();
        _barRoot.SetActive(true);

        foreach (var kv in _slots)
        {
            int previewIndex = PreviewIndex(kv.Key);
            float level = PreviewVoiceLevel(previewIndex);
            kv.Value.IsSpeaking = level > 0f;
            kv.Value.TargetLevel = level;
            if (level > 0f)
                kv.Value.LastActiveTime = Time.time;
        }

        UpdateSlotRings();
        ApplyPublicGhostAlpha();
        LayoutSlotsIfDirty();
        KeepSpeakingBarOnTop(template);
    }

    private static void EnsureFakePreviewSlots()
    {
        _fadedSlotIds.Clear();
        foreach (byte id in _slots.Keys)
            if (!IsPreviewPlayerId(id))
                _fadedSlotIds.Add(id);
        foreach (byte id in _fadedSlotIds)
            RemoveSlot(id);

        for (int i = 0; i < PreviewPlayerCount; i++)
        {
            byte id = (byte)(PreviewPlayerIdStart + i);
            if (!_slots.TryGetValue(id, out var slot)
                || slot.RingGO == null
                || slot.RingRenderer == null
                || slot.LabelTMP == null)
            {
                if (slot != null)
                    RemoveSlot(id);
                AddPreviewSlot(id, i);
                continue;
            }

            // Palette assets can be transiently unavailable while scenes change. Keep the
            // ring/name slot and retry just the optional body instead of rebuilding everything.
            if (slot.IconGO == null
                && CrewmateAvatarRenderer.TryCreatePreview(i, _barRoot!.transform, out var iconGO))
            {
                slot.IconGO = iconGO;
                _layoutDirty = true;
                _sortingDirty = true;
            }
        }

        bool orderChanged = _slotOrder.Count != PreviewPlayerCount;
        if (!orderChanged)
        {
            for (int i = 0; i < PreviewPlayerCount; i++)
            {
                if (_slotOrder[i] == PreviewPlayerIdStart + i) continue;
                orderChanged = true;
                break;
            }
        }

        if (!orderChanged) return;
        _slotOrder.Clear();
        for (int i = 0; i < PreviewPlayerCount; i++)
        {
            byte id = (byte)(PreviewPlayerIdStart + i);
            if (_slots.ContainsKey(id))
                _slotOrder.Add(id);
        }
        _layoutDirty = true;
    }

    private static void ApplyDynamicRosterOrder()
    {
        // Slot/public-state changes already mark layout dirty. Avoid a per-frame copy/sort
        // while the dynamic bar is merely animating voice visibility.
        if (!_layoutDirty || _slotOrder.Count <= 1) return;
        _slotOrder.Sort(PublicRosterComparer);
    }

    private static float _lastOverlayErrorLog = -999f;

    private static void LogOverlayError(string where, System.Exception ex)
    {
        if (Time.time - _lastOverlayErrorLog < 5f) return;
        _lastOverlayErrorLog = Time.time;
        VoiceDiagnostics.DebugError($"[VC] {where} failed: {ex.Message}");
    }

    private static void EnsureBar(PingTracker? template)
    {
        if (_barRoot != null) return;

        // If a meeting-owned parent was destroyed, Unity destroys the visual children while the
        // managed slot dictionary survives. Drop those dead references before constructing a new root.
        if (_slots.Count > 0)
            DestroySpeakingBarSlots();

        _barRoot   = new GameObject("VC_SpeakingBar");
        _barRoot.transform.SetParent(ResolveOverlayRoot(template), false);
        _barAspect = _barRoot.AddComponent<AspectPosition>();
        ApplySortingGroup(_barRoot, VCSorting.Ring);

        var backdropGO = new GameObject("VC_BarBackdrop");
        backdropGO.transform.SetParent(_barRoot.transform, false);
        _backdropSR = backdropGO.AddComponent<SpriteRenderer>();
        _backdropSR.sprite = GetBackdropSprite();
        _backdropSR.drawMode = SpriteDrawMode.Sliced;
        _backdropSR.color = BackdropColor;
        _backdropSR.sortingLayerName = VCSorting.Layer;
        _backdropSR.sortingOrder = VCSorting.Backdrop;
        _backdropSR.maskInteraction = SpriteMaskInteraction.None;
        VCOverlayCamera.EnsureOnTop(backdropGO);

        var settings = VoiceSettings.Instance;
        if (settings != null)
        {
            _barPosition  = settings.SpeakingBarPosition.Value;
            _manualLayout = settings.SpeakingBarManualLayout.Value;
            _manualX      = settings.SpeakingBarX.Value;
            _manualY      = settings.SpeakingBarY.Value;
            _configuredNamePosition = settings.SpeakingBarNamePosition.Value;
            _backdropEnabled = settings.SpeakingBarBackdrop.Value;
            _barScale     = Mathf.Clamp(settings.SpeakingBarScale.Value,
                SpeakingBarScalePolicy.MinimumUserScale,
                SpeakingBarScalePolicy.MaximumUserScale);
            _fixedAllPlayers = settings.SpeakingBarFixedAllPlayers.Value;
            _showFake15Players = settings.ShowFake15Players.Value;
            if (_manualLayout)
            {
                _layoutVertical       = settings.SpeakingBarLayout.Value == VoiceControlsLayout.Vertical;
            }
            else
            {
                _layoutVertical       = IsVerticalPreset(_barPosition);
            }
            _namePosition = ResolveNamePosition();
        }

        ApplyRootScale();

        if (_manualLayout)
        {
            _barAspect.enabled = false;
        }
        else
        {
            ApplyPositionToAspect(_barAspect, _barPosition);
            _barAspect.AdjustPosition();
        }
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

        // End-game results screen: no HudManager/MeetingHud/PingTracker. Anchor to the end-game UI (or the main
        // camera) so the speaking bar still has a parent to render under.
        if (VoiceSceneState.IsEndGameActive)
        {
            var endGameManager = Object.FindObjectOfType<EndGameManager>();
            if (endGameManager != null) return endGameManager.transform;
            var endGameCamera = Camera.main;
            if (endGameCamera != null) return endGameCamera.transform;
        }

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
        if (_manualLayout)
        {
            PositionSpeakingBarManual();
        }
        else
        {
            PositionSpeakingBarPreset();
        }
        // Cache the bar's SortingGroup instead of GetComponent/AddComponent every frame. The Unity
        // null-check re-acquires it if the bar GameObject was destroyed and recreated.
        if (_barSortingGroup == null)
            _barSortingGroup = _barRoot.GetComponent<SortingGroup>() ?? _barRoot.AddComponent<SortingGroup>();
        _barSortingGroup.sortingLayerName = VCSorting.Layer;
        _barSortingGroup.sortingOrder = VCSorting.Ring;

        VCOverlayCamera.Sync(); // must follow the main camera every frame
        // Re-stamp allocates via GetComponentsInChildren; only run on actual change.
        if (_sortingDirty)
        {
            ApplySpeakingBarSorting();
            _sortingDirty = false;
        }
    }

    // Manual mode: reset to baseline scale, place the bar root at the viewport-relative
    // slider position (mirrors VoiceChatHudState.PositionButtons), shrink to fit when there
    // are too many speakers to fit on screen, then clamp so no slot leaves the screen.
    private static void PositionSpeakingBarManual()
    {
        var cam = Camera.main;
        if (cam == null) return;
        PositionBarAtViewport(cam, _manualX, _manualY);
    }

    private static void PositionSpeakingBarPreset()
    {
        var cam = Camera.main;
        if (cam == null) return;
        if (_barAspect != null) _barAspect.enabled = false;
        var anchor = PresetViewportAnchor(_barPosition);
        PositionBarAtViewport(cam, anchor.x, anchor.y);
    }

    private static Vector2 PresetViewportAnchor(SpeakingBarPosition pos) => pos switch
    {
        SpeakingBarPosition.TopLeft      => new Vector2(0f, 1f),
        SpeakingBarPosition.TopMiddle    => new Vector2(0.5f, 1f),
        SpeakingBarPosition.TopRight     => new Vector2(1f, 1f),
        SpeakingBarPosition.MiddleLeft   => new Vector2(0f, 0.5f),
        SpeakingBarPosition.MiddleRight  => new Vector2(1f, 0.5f),
        SpeakingBarPosition.BottomLeft   => new Vector2(0f, 0f),
        SpeakingBarPosition.BottomMiddle => new Vector2(0.5f, 0f),
        SpeakingBarPosition.BottomRight  => new Vector2(1f, 0f),
        _ => new Vector2(0.5f, 1f),
    };

    private static void PositionBarAtViewport(Camera cam, float vx, float vy)
    {
        if (_barRoot == null) return;
        ApplyRootScale();
        var worldPt = cam.ViewportToWorldPoint(new Vector3(vx, vy, ManualViewportDepth));
        var parent  = _barRoot.transform.parent;
        Vector3 local = parent != null
            ? parent.InverseTransformPoint(new Vector3(worldPt.x, worldPt.y, worldPt.z))
            : new Vector3(worldPt.x, worldPt.y, worldPt.z);
        _barRoot.transform.localPosition = new Vector3(local.x, local.y, -100f);
        AutoFitSpeakingBar(cam);
        ClampSpeakingBarToViewport(cam);
    }

    // When the bar's on-screen extent exceeds the viewport (e.g. a tall vertical stack of
    // many simultaneous speakers), shrink the whole root uniformly so every icon stays
    // fully visible. Content size scales linearly with root scale, so the needed factor is
    // allowed / measured. Never enlarges; only shrinks.
    private static void AutoFitSpeakingBar(Camera cam)
    {
        if (_barRoot == null) return;
        if (!TryComputeSlotViewportBounds(cam, out float minX, out float maxX, out float minY, out float maxY))
            return;

        float allowed = 1f - 2f * ManualViewportPadding;
        float sizeX = maxX - minX;
        float sizeY = maxY - minY;

        float scale = 1f;
        if (sizeX > allowed) scale = Mathf.Min(scale, allowed / sizeX);
        if (sizeY > allowed) scale = Mathf.Min(scale, allowed / sizeY);

        if (scale < 0.999f)
            _barRoot.transform.localScale = Vector3.one * scale * SpeakingBarScalePolicy.ToRenderedScale(_barScale);
    }

    // Generalizes VoiceChatHudState.ClampVoiceButtonViewportPositions from the 3 fixed
    // buttons to N speaker slots: shift the whole bar root so every icon/ring/label stays
    // inside the viewport padding. Only the root moves; child layout is preserved.
    private static void ClampSpeakingBarToViewport(Camera cam)
    {
        if (_barRoot == null) return;
        if (!TryComputeSlotViewportBounds(cam, out float minX, out float maxX, out float minY, out float maxY))
            return;

        float shiftX = CalculateManualViewportShift(minX, maxX);
        float shiftY = CalculateManualViewportShift(minY, maxY);
        if (Mathf.Approximately(shiftX, 0f) && Mathf.Approximately(shiftY, 0f)) return;

        var origin  = cam.ViewportToWorldPoint(new Vector3(0f, 0f, ManualViewportDepth));
        var shifted = cam.ViewportToWorldPoint(new Vector3(shiftX, shiftY, ManualViewportDepth));
        var delta = shifted - origin;
        delta.z = 0f;
        _barRoot.transform.position += delta;
    }

    private static bool TryComputeSlotViewportBounds(Camera cam,
        out float minX, out float maxX, out float minY, out float maxY)
    {
        minX = float.MaxValue; maxX = float.MinValue;
        minY = float.MaxValue; maxY = float.MinValue;
        if (_barRoot == null || _slots.Count == 0) return false;

        var depthWorld = cam.ViewportToWorldPoint(new Vector3(0f, 0f, ManualViewportDepth));
        float depthZ = depthWorld.z;
        float rootScale = Mathf.Abs(_barRoot.transform.lossyScale.x);
        float coreHalf = RingScale * 0.5f * rootScale;

        bool any = false;
        foreach (var kv in _slots)
        {
            var slot = kv.Value;
            Transform? core = slot.IconGO != null ? slot.IconGO.transform
                : slot.RingGO != null ? slot.RingGO.transform : null;
            if (core != null)
            {
                var p = core.position;
                AccumulateBox(cam, p.x, p.y, coreHalf, coreHalf, depthZ, ref minX, ref maxX, ref minY, ref maxY, ref any);
            }
            if (slot.LabelTMP != null && slot.LabelWidth > 0.0001f && slot.LabelHeight > 0.0001f)
            {
                var lp = slot.LabelTMP.transform.position;
                float lhw = slot.LabelWidth * 0.5f * rootScale;
                float lhh = slot.LabelHeight * 0.5f * rootScale;
                float labelCenterX = _namePosition switch
                {
                    SpeakingBarNamePosition.Left => lp.x - lhw,
                    SpeakingBarNamePosition.Right => lp.x + lhw,
                    _ => lp.x,
                };
                AccumulateBox(cam, labelCenterX, lp.y, lhw, lhh, depthZ,
                    ref minX, ref maxX, ref minY, ref maxY, ref any);
            }
        }
        if (_backdropEnabled && _backdropSR != null && _backdropSR.enabled)
        {
            var bounds = _backdropSR.bounds;
            AccumulateBox(cam, bounds.center.x, bounds.center.y, bounds.extents.x, bounds.extents.y,
                bounds.center.z, ref minX, ref maxX, ref minY, ref maxY, ref any);
        }
        return any;
    }

    private static void AccumulateBox(Camera cam, float cx, float cy, float hx, float hy, float depthZ,
        ref float minX, ref float maxX, ref float minY, ref float maxY, ref bool any)
    {
        AccumulateViewportPoint(cam, cx - hx, cy - hy, depthZ, ref minX, ref maxX, ref minY, ref maxY, ref any);
        AccumulateViewportPoint(cam, cx + hx, cy - hy, depthZ, ref minX, ref maxX, ref minY, ref maxY, ref any);
        AccumulateViewportPoint(cam, cx - hx, cy + hy, depthZ, ref minX, ref maxX, ref minY, ref maxY, ref any);
        AccumulateViewportPoint(cam, cx + hx, cy + hy, depthZ, ref minX, ref maxX, ref minY, ref maxY, ref any);
    }

    private static void AccumulateViewportPoint(Camera cam, float wx, float wy, float depthZ,
        ref float minX, ref float maxX, ref float minY, ref float maxY, ref bool any)
    {
        var vp = cam.WorldToViewportPoint(new Vector3(wx, wy, depthZ));
        minX = Mathf.Min(minX, vp.x);
        maxX = Mathf.Max(maxX, vp.x);
        minY = Mathf.Min(minY, vp.y);
        maxY = Mathf.Max(maxY, vp.y);
        any = true;
    }

    // Same logic as VoiceChatHudState.CalculateViewportShift, including the
    // "content larger than the allowed area → center it" fallback.
    private static float CalculateManualViewportShift(float min, float max)
    {
        float minAllowed = ManualViewportPadding;
        float maxAllowed = 1f - ManualViewportPadding;
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
            Level = voiceLevel,
            TargetLevel = voiceLevel,
            SmoothedLevel = VoiceLevelVisual.NormalizeVoiceLevel(voiceLevel),
            IsSpeaking = true,
            LastActiveTime = Time.time,
            LastPlayerSeenTime = player != null ? Time.time : 0f
        };

        TryCreateSlotIcon(playerId, slot);

        long ringTicks = VoiceFrameProfiler.Begin();
        CreateRing(playerId, slot);
        VoiceFrameProfiler.End("overlay.ringcreate", ringTicks);

        long labelTicks = VoiceFrameProfiler.Begin();
        var labelGO = new GameObject("VC_Label");
        labelGO.transform.SetParent(_barRoot.transform, false);
        var tmp = labelGO.AddComponent<TextMeshPro>();
        tmp.text               = string.Empty;
        tmp.fontSize           = LabelSize;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.overflowMode       = TextOverflowModes.Ellipsis;
        tmp.sortingLayerID     = SortingLayer.NameToID(VCSorting.Layer);
        tmp.sortingOrder       = VCSorting.Text;
        tmp.color              = Color.white;
        tmp.alpha              = 0f;
        tmp.rectTransform.sizeDelta = new Vector2(1.4f, 0.45f);
        slot.LabelTMP = tmp;
        UpdateSlotLabel(slot, player);
        // End-game (no live PlayerControl): label from the cached voice profile name so it isn't blank.
        if (player == null && _activeSpeakerNames.TryGetValue(playerId, out var fallbackName) && !string.IsNullOrWhiteSpace(fallbackName))
            slot.LabelTMP.text = fallbackName;
        VCOverlayCamera.EnsureOnTop(labelGO);
        VoiceFrameProfiler.End("overlay.labelcreate", labelTicks);
        _slots[playerId] = slot;
        _slotOrder.Add(playerId);
        _layoutDirty = true;
        _sortingDirty = true;
    }

    private static void AddPreviewSlot(byte playerId, int previewIndex)
    {
        if (_barRoot == null) return;

        float voiceLevel = PreviewVoiceLevel(previewIndex);
        var slot = new SpeakerSlot
        {
            Level = voiceLevel,
            TargetLevel = voiceLevel,
            SmoothedLevel = VoiceLevelVisual.NormalizeVoiceLevel(voiceLevel),
            Visibility = voiceLevel > 0f ? 1f : 0f,
            IsSpeaking = voiceLevel > 0f,
            LastActiveTime = Time.time,
            LastPlayerSeenTime = Time.time,
            CosmeticsComplete = true,
        };

        if (CrewmateAvatarRenderer.TryCreatePreview(previewIndex, _barRoot.transform, out var iconGO))
            slot.IconGO = iconGO;

        CreateRing(playerId, slot);

        var labelGO = new GameObject("VC_PreviewLabel");
        labelGO.transform.SetParent(_barRoot.transform, false);
        var tmp = labelGO.AddComponent<TextMeshPro>();
        tmp.text = PreviewPlayerNames[previewIndex];
        tmp.fontSize = LabelSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.sortingLayerID = SortingLayer.NameToID(VCSorting.Layer);
        tmp.sortingOrder = VCSorting.Text;
        tmp.color = Color.white;
        tmp.alpha = 1f;
        tmp.rectTransform.sizeDelta = new Vector2(1.4f, 0.45f);
        slot.LabelTMP = tmp;
        slot.LabelMeasurePending = true;
        VCOverlayCamera.EnsureOnTop(labelGO);

        _slots[playerId] = slot;
        _slotOrder.Add(playerId);
        _layoutDirty = true;
        _sortingDirty = true;
    }

    private static bool TryCreateSlotIcon(byte playerId, SpeakerSlot slot, bool replaceExisting = false)
    {
        if (_barRoot == null) return false;
        if (slot.IconGO != null && !replaceExisting) return true;

        // overlay.iconcreate = the speaker avatar build (CrewmateAvatarRenderer.TryCreate -> base sprite +
        // ring sprite + cosmetic layers). This is the first-speaker hitch; timing it confirms the ring-sprite
        // bulk-upload fix and surfaces any remaining first-build cost.
        long iconTicks = VoiceFrameProfiler.Begin();
        var player = FindPlayer(playerId);
        bool created = false;
        if (player != null && CrewmateAvatarRenderer.TryCreate(playerId, player, _barRoot.transform, out var iconGO))
        {
            if (slot.IconGO != null) Object.Destroy(slot.IconGO);
            slot.IconGO = iconGO;
            slot.Fingerprint = GetFingerprint(playerId);
            slot.CosmeticsComplete = CrewmateAvatarRenderer.OutfitCosmeticsResolved(player);
            slot.PendingFingerprint = default;
            _layoutDirty = true;
            _sortingDirty = true;
            created = true;
        }
        // End-game results screen: the player has no live PlayerControl, so rebuild the avatar from the outfit
        // cached while in-game (body + real cosmetics). Falls through to a ring + name slot if never cached.
        else if (player == null && CrewmateAvatarRenderer.TryCreateFromCache(playerId, _barRoot.transform, out var cachedIcon))
        {
            if (slot.IconGO != null) Object.Destroy(slot.IconGO);
            slot.IconGO = cachedIcon;
            slot.Fingerprint = GetFingerprint(playerId);
            slot.CosmeticsComplete = true;
            slot.PendingFingerprint = default;
            _layoutDirty = true;
            _sortingDirty = true;
            created = true;
        }
        VoiceFrameProfiler.End("overlay.iconcreate", iconTicks);
        return created;
    }

    private static void UpdateSlotLabel(SpeakerSlot slot, PlayerControl? player)
    {
        if (slot.LabelTMP == null) return;
        if (slot.VanillaStyleVersion != _vanillaNameStyleVersion && ApplyVanillaNameStyle(slot.LabelTMP, player))
            slot.VanillaStyleVersion = _vanillaNameStyleVersion;
        slot.LabelTMP.text = GetDisplayName(player);
        slot.LabelMeasurePending = true;
        if (slot.VanillaStyleVersion == _vanillaNameStyleVersion)
            slot.LabelTMP.alpha = 1f;
    }

    private static TMPro.TMP_FontAsset? _vanillaNameFont;
    private static Material? _vanillaNameMaterial;
    private static TMPro.FontStyles _vanillaFontStyle;
    private static float _vanillaOutlineWidth;
    private static Color32 _vanillaOutlineColor;
    private static int _vanillaNameStyleVersion = 1;
    private static int _vanillaStyleFailures;
    private static bool _vanillaCachePopulated;
    private static bool _vanillaStyleBroken;
    private const int VanillaStyleFailureLimit = 5;

    // set_font is only safe once the label is active in hierarchy; earlier it NREs inside LoadFontAsset under IL2CPP.
    private static bool ApplyVanillaNameStyle(TextMeshPro tmp, PlayerControl? player)
    {
        if (_vanillaStyleBroken) return true;
        try
        {
            if (!tmp.gameObject.activeInHierarchy) return false;
            if (_vanillaNameFont == null || _vanillaNameMaterial == null)
            {
                var source = player != null ? player.cosmetics?.nameText : null;
                if (source == null || source.font == null || source.fontSharedMaterial == null)
                    source = HudManager.Instance?.TaskPanel?.taskText;
                if (source == null || source.font == null || source.fontSharedMaterial == null) return false;
                _vanillaNameFont = source.font;
                _vanillaNameMaterial = source.fontSharedMaterial;
                _vanillaFontStyle = source.fontStyle;
                _vanillaOutlineWidth = source.outlineWidth;
                _vanillaOutlineColor = source.outlineColor;
                _vanillaCachePopulated = true;
                _vanillaNameStyleVersion++;
                VoiceDiagnostics.Log("overlay.namestyle",
                    $"srcFont=\"{_vanillaNameFont.name}\" srcMat=\"{_vanillaNameMaterial.name}\" srcSize={source.fontSize:0.00} srcStyle={_vanillaFontStyle} srcOutlineW={_vanillaOutlineWidth:0.000} srcOutlineC={_vanillaOutlineColor} labelSize={LabelSize:0.00} version={_vanillaNameStyleVersion}");
            }
            tmp.font = _vanillaNameFont;
            tmp.fontSharedMaterial = _vanillaNameMaterial;
            tmp.fontStyle = _vanillaFontStyle;
            if (Mathf.Abs(tmp.outlineWidth - _vanillaOutlineWidth) > 0.001f)
            {
                tmp.outlineWidth = _vanillaOutlineWidth;
                tmp.outlineColor = _vanillaOutlineColor;
            }
            _vanillaStyleFailures = 0;
            return true;
        }
        catch (System.Exception ex)
        {
            if (++_vanillaStyleFailures >= VanillaStyleFailureLimit)
            {
                _vanillaStyleBroken = true;
                VoiceDiagnostics.DebugWarning($"[VC] Vanilla name style unavailable, keeping default label style: {ex.Message}");
                return true;
            }
            return false;
        }
    }

    private static void RemoveSlot(byte id)
    {
        if (!_slots.TryGetValue(id, out var slot)) return;
        if (slot.IconGO   != null) Object.Destroy(slot.IconGO);
        if (slot.RingGO   != null) Object.Destroy(slot.RingGO);
        if (slot.LabelTMP != null) Object.Destroy(slot.LabelTMP.gameObject);
        _slots.Remove(id);
        _slotOrder.Remove(id);
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
        if (_vanillaCachePopulated && (_vanillaNameFont == null || _vanillaNameMaterial == null))
        {
            _vanillaCachePopulated = false;
            _vanillaNameFont = null;
            _vanillaNameMaterial = null;
            _vanillaNameStyleVersion++;
        }

        foreach (var kv in _slots)
        {
            var slot = kv.Value;
            if (slot.LabelTMP != null)
            {
                if (slot.VanillaStyleVersion != _vanillaNameStyleVersion && slot.LabelTMP.gameObject.activeInHierarchy
                    && ApplyVanillaNameStyle(slot.LabelTMP, FindPlayer(kv.Key)))
                    slot.VanillaStyleVersion = _vanillaNameStyleVersion;
                if (slot.VanillaStyleVersion == _vanillaNameStyleVersion && slot.LabelTMP.alpha < 1f)
                    slot.LabelTMP.alpha = 1f;
            }
            if (slot.LabelMeasurePending && slot.LabelTMP != null && slot.LabelTMP.gameObject.activeInHierarchy)
            {
                if (slot.VanillaStyleVersion != _vanillaNameStyleVersion && ApplyVanillaNameStyle(slot.LabelTMP, FindPlayer(kv.Key)))
                    slot.VanillaStyleVersion = _vanillaNameStyleVersion;
                float w = slot.LabelTMP.preferredWidth;
                float h = slot.LabelTMP.preferredHeight;
                if (!float.IsNaN(w) && !float.IsInfinity(w) && w >= 0f &&
                    !float.IsNaN(h) && !float.IsInfinity(h) && h >= 0f)
                {
                    slot.LabelMeasurePending = false;
                    // The visible TMP rect ellipsizes long names; layout must use the same cap so
                    // one pathological name cannot stretch every row/column off-screen.
                    w = Mathf.Min(w, slot.LabelTMP.rectTransform.sizeDelta.x);
                    h = Mathf.Min(h, slot.LabelTMP.rectTransform.sizeDelta.y);
                    if (Mathf.Abs(w - slot.LabelWidth) > 0.01f || Mathf.Abs(h - slot.LabelHeight) > 0.01f)
                    {
                        slot.LabelWidth = w;
                        slot.LabelHeight = h;
                        _layoutDirty = true;
                    }
                }
            }
            if (slot.RingRenderer == null || slot.RingGO == null) continue;

            slot.SmoothedLevel = VoiceLevelVisual.SmoothLevel(slot.SmoothedLevel, slot.TargetLevel, Time.deltaTime);
            slot.Visibility = VoiceLevelVisual.StepVisibility(slot.Visibility, slot.IsSpeaking, Time.deltaTime);
            slot.Level = slot.TargetLevel;

            float brightness = Mathf.SmoothStep(0f, 1f, slot.SmoothedLevel);
            // Fixed BetterCrewLink "talking" green (#2ecc71) for every speaker — the ring never carries the player's
            // color, so rainbow is treated exactly like any other color; only its opacity tracks the voice level.
            Color color = (Color)new Color32(46, 204, 113, 255);
            color.a = Mathf.Lerp(0.22f, 0.92f, brightness) * slot.Visibility;
            slot.RingRenderer.color = color;
            slot.RingGO.transform.localScale = Vector3.one * RingScale;
            slot.RingRenderer.enabled = slot.Visibility > 0.01f;
        }

        if (_backdropSR != null)
        {
            _backdropSR.enabled = _backdropEnabled && _slots.Count > 0;
            if (_backdropSR.enabled)
                _backdropSR.color = BackdropColor;
        }
    }

    private static void SnapSlotRing(SpeakerSlot slot)
    {
        slot.IsSpeaking = false;
        slot.Level = 0f;
        slot.TargetLevel = 0f;
        slot.SmoothedLevel = 0f;
        slot.Visibility = 0f;
        if (slot.RingRenderer != null)
            slot.RingRenderer.enabled = false;
    }

    private static void LayoutSlots()
    {
        _layoutDirty = false;
        int count = 0;
        float crossPitch = SlotWidth;
        foreach (byte id in _slotOrder)
        {
            if (!_slots.TryGetValue(id, out var slot)) continue;
            crossPitch = Mathf.Max(crossPitch, RequiredSlotPitch(slot));
            count++;
        }
        if (count == 0) return;

        float primaryPitch = _namePosition == SpeakingBarNamePosition.Top
            ? SlotHeight + TopNameExtraPitch
            : SlotHeight;
        int safeCapacity = _layoutVertical
            ? MaxSlotsPerColumn(primaryPitch, count)
            : MaxSlotsPerRow(crossPitch, count);
        int lineCount = SpeakingBarLayoutPolicy.GetLineCount(count, safeCapacity);
        var primaryDirection = ActivePrimaryDirection();
        var overflowDirection = ActiveOverflowDirection();
        float cMinX = 0f, cMaxX = 0f, cMinY = 0f, cMaxY = 0f;
        bool any = false;
        int layoutIndex = 0;

        foreach (byte id in _slotOrder)
        {
            if (!_slots.TryGetValue(id, out var slot)) continue;
            var placement = SpeakingBarLayoutPolicy.GetPlacement(layoutIndex, count, lineCount);
            float x;
            float y;
            if (_layoutVertical)
            {
                float overflowSign = overflowDirection == SpeakingBarLayoutDirection.Left ? -1f : 1f;
                float primarySign = primaryDirection == SpeakingBarLayoutDirection.Up ? 1f : -1f;
                x = ShouldCenterOverflowLines()
                    ? (placement.LineIndex - (lineCount - 1) * 0.5f) * crossPitch
                    : placement.LineIndex * crossPitch * overflowSign;
                float indexOffset = placement.IndexInLine;
                if (ShouldCenterVerticalLines())
                    indexOffset -= (placement.LineSize - 1) * 0.5f;
                y = indexOffset * primaryPitch * primarySign;
            }
            else
            {
                float overflowSign = overflowDirection == SpeakingBarLayoutDirection.Up ? 1f : -1f;
                x = (placement.IndexInLine - (placement.LineSize - 1) * 0.5f) * crossPitch;
                y = ShouldCenterOverflowLines()
                    ? ((lineCount - 1) * 0.5f - placement.LineIndex) * primaryPitch
                    : placement.LineIndex * primaryPitch * overflowSign;
            }

            PositionSlot(slot, x, y, ref cMinX, ref cMaxX, ref cMinY, ref cMaxY, ref any);
            layoutIndex++;
        }

        if (any)
            UpdateBackdrop(cMinX, cMaxX, cMinY, cMaxY);
    }

    private static SpeakingBarLayoutDirection ActivePrimaryDirection()
    {
        if (!_manualLayout)
            return SpeakingBarLayoutPolicy.PrimaryDirectionFor(_barPosition);
        if (!_layoutVertical)
            return SpeakingBarLayoutDirection.Right;
        return _manualY < 0.5f ? SpeakingBarLayoutDirection.Up : SpeakingBarLayoutDirection.Down;
    }

    private static SpeakingBarLayoutDirection ActiveOverflowDirection()
    {
        if (!_manualLayout)
            return SpeakingBarLayoutPolicy.OverflowDirectionFor(_barPosition);
        if (_layoutVertical)
            return _manualX <= 0.5f ? SpeakingBarLayoutDirection.Right : SpeakingBarLayoutDirection.Left;
        return _manualY < 0.5f ? SpeakingBarLayoutDirection.Up : SpeakingBarLayoutDirection.Down;
    }

    private static bool ShouldCenterVerticalLines()
    {
        if (!_manualLayout)
            return _barPosition is SpeakingBarPosition.MiddleLeft or SpeakingBarPosition.MiddleRight;
        return _manualY > 0.33f && _manualY < 0.67f;
    }

    private static bool ShouldCenterOverflowLines()
    {
        if (!_manualLayout) return false;
        return _layoutVertical
            ? _manualX > 0.33f && _manualX < 0.67f
            : _manualY > 0.33f && _manualY < 0.67f;
    }

    private static void PositionSlot(
        SpeakerSlot slot,
        float x,
        float y,
        ref float minX,
        ref float maxX,
        ref float minY,
        ref float maxY,
        ref bool any)
    {
        if (slot.IconGO != null)
            slot.IconGO.transform.localPosition = new Vector3(x, y, -100f);
        if (slot.RingGO != null)
            slot.RingGO.transform.localPosition = new Vector3(x, y, -101f);
        if (slot.LabelTMP != null)
            ApplyLabelPlacement(slot.LabelTMP, x, y, y - LabelOffset);

        GetSlotXExtents(slot, x, out float sxMin, out float sxMax);
        GetSlotYExtents(slot, y, out float syMin, out float syMax);
        if (!any)
        {
            minX = sxMin; maxX = sxMax; minY = syMin; maxY = syMax;
            any = true;
            return;
        }
        minX = Mathf.Min(minX, sxMin); maxX = Mathf.Max(maxX, sxMax);
        minY = Mathf.Min(minY, syMin); maxY = Mathf.Max(maxY, syMax);
    }

    private static int MaxSlotsPerRow(float slotPitch, int count)
    {
        if (count <= 1) return 1;
        var cam = Camera.main;
        if (cam == null || !cam.orthographic || slotPitch <= 0.0001f) return count;
        float worldWidth = 2f * cam.orthographicSize * cam.aspect * (1f - 2f * ManualViewportPadding);
        float localWidth = worldWidth / Mathf.Max(SpeakingBarScalePolicy.ToRenderedScale(_barScale), 0.0001f);
        int fit = Mathf.FloorToInt(Mathf.Max(0f, localWidth - BackdropPad * 2f) / slotPitch);
        return Mathf.Clamp(fit, 1, count);
    }

    private static int MaxSlotsPerColumn(float slotPitch, int count)
    {
        if (count <= 1) return 1;
        var cam = Camera.main;
        if (cam == null || !cam.orthographic || slotPitch <= 0.0001f) return count;
        float worldHeight = 2f * cam.orthographicSize * (1f - 2f * ManualViewportPadding);
        float localHeight = worldHeight / Mathf.Max(SpeakingBarScalePolicy.ToRenderedScale(_barScale), 0.0001f);
        int fit = Mathf.FloorToInt(Mathf.Max(0f, localHeight - BackdropPad * 2f) / slotPitch);
        return Mathf.Clamp(fit, 1, count);
    }

    private static float RequiredSlotPitch(SpeakerSlot slot)
    {
        const float ringHalf = RingScale * 0.5f;
        return _namePosition switch
        {
            SpeakingBarNamePosition.Left or SpeakingBarNamePosition.Right
                => LabelSideOffset + slot.LabelWidth + ringHalf + NameGap,
            _ => slot.LabelWidth + NameGap,
        };
    }

    private static void GetSlotXExtents(SpeakerSlot slot, float iconX, out float min, out float max)
    {
        const float ringHalf = RingScale * 0.5f;
        switch (_namePosition)
        {
            case SpeakingBarNamePosition.Left:
                min = iconX - LabelSideOffset - slot.LabelWidth;
                max = iconX + ringHalf;
                break;
            case SpeakingBarNamePosition.Right:
                min = iconX - ringHalf;
                max = iconX + LabelSideOffset + slot.LabelWidth;
                break;
            default:
                float halfW = Mathf.Max(ringHalf, slot.LabelWidth * 0.5f);
                min = iconX - halfW;
                max = iconX + halfW;
                break;
        }
    }

    private static void GetSlotYExtents(SpeakerSlot slot, float iconY, out float min, out float max)
    {
        const float ringHalf = RingScale * 0.5f;
        float labelHalf = slot.LabelHeight * 0.5f;
        switch (_namePosition)
        {
            case SpeakingBarNamePosition.Top:
                min = iconY - ringHalf;
                max = iconY + LabelOffset + labelHalf;
                break;
            case SpeakingBarNamePosition.Bottom:
                min = iconY - LabelOffset - labelHalf;
                max = iconY + ringHalf;
                break;
            default:
                float halfH = Mathf.Max(ringHalf, labelHalf);
                min = iconY - halfH;
                max = iconY + halfH;
                break;
        }
    }

    private static void UpdateBackdrop(float minX, float maxX, float minY, float maxY)
    {
        if (_backdropSR == null) return;

        _backdropSR.size = new Vector2(maxX - minX + BackdropPad * 2f, maxY - minY + BackdropPad * 2f);
        _backdropSR.transform.localPosition = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, -99f);
    }

    private static Sprite GetBackdropSprite()
    {
        if (_backdropSprite != null) return _backdropSprite;

        const int size = 64;
        const float radius = 4f;
        const float feather = 1f;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        float insetMin = radius;
        float insetMax = size - 1f - radius;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = Mathf.Max(Mathf.Max(insetMin - x, x - insetMax), 0f);
            float dy = Mathf.Max(Mathf.Max(insetMin - y, y - insetMax), 0f);
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            byte a = (byte)(Mathf.Clamp01((radius - d) / feather) * 255f);
            pixels[y * size + x] = new Color32(255, 255, 255, a);
        }

        tex.SetPixels32(pixels);
        tex.Apply();
        _backdropSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 64f, 0,
            SpriteMeshType.FullRect, new Vector4(8f, 8f, 8f, 8f));
        _backdropSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _backdropSprite;
    }

    // Places a slot's name label relative to its icon per the local SpeakingBarNamePosition setting. Bottom (default)
    // keeps the historic below-icon spot; Top sits above; Left/Right sit beside the icon with the label's rect pivot
    // anchored to the icon-facing edge (so LabelSideOffset is the exact gap and long names grow away from the icon).
    private static void ApplyLabelPlacement(TextMeshPro tmp, float iconX, float iconCentreY, float bottomLabelY)
    {
        Vector3 pos;
        TextAlignmentOptions align;
        Vector2 pivot;
        switch (_namePosition)
        {
            case SpeakingBarNamePosition.Top:
                pos = new Vector3(iconX, iconCentreY + LabelOffset, -102f);
                align = TextAlignmentOptions.Center; pivot = new Vector2(0.5f, 0.5f);
                break;
            case SpeakingBarNamePosition.Left:
                pos = new Vector3(iconX - LabelSideOffset, iconCentreY, -102f);
                align = TextAlignmentOptions.Right; pivot = new Vector2(1f, 0.5f);
                break;
            case SpeakingBarNamePosition.Right:
                pos = new Vector3(iconX + LabelSideOffset, iconCentreY, -102f);
                align = TextAlignmentOptions.Left; pivot = new Vector2(0f, 0.5f);
                break;
            default: // Bottom
                pos = new Vector3(iconX, bottomLabelY, -102f);
                align = TextAlignmentOptions.Center; pivot = new Vector2(0.5f, 0.5f);
                break;
        }
        tmp.transform.localPosition = pos;
        tmp.alignment = align;
        tmp.rectTransform.pivot = pivot;
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
        ResetPublicDeathState();
        VoiceOverlayState.InvalidateCache();
        VoiceIdentityPrivacyRuntime.Reset();
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
        _slotOrder.Clear();
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
            ResetPublicDeathState();
            VoiceOverlayState.InvalidateCache();
            VoiceIdentityPrivacyRuntime.Reset();
            VoiceVolumeMenu.ForceClose();
            CrewmateAvatarRenderer.ClearCache();
            _layoutDirty = false;
            _sortingDirty = false;
            if (_barRoot != null) { Object.Destroy(_barRoot); _barRoot = null; _barSortingGroup = null; _backdropSR = null; }
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
        bool stableIdentity = _fixedAllPlayers || MeetingHud.Instance != null;
        return new OutfitFingerprint(
            stableIdentity ? 0 : GetDisplayOutfitId(pc),
            GetPlayerColorId(pc),
            outfit.HatId,
            outfit.SkinId,
            outfit.VisorId,
            outfit.PlayerName,
            !stableIdentity && CrewmateAvatarRenderer.IsConcealed(pc));
    }

    // Color-blind fingerprint compare: true when at most the body ColorId differs. Lets the consumer
    // loop debounce a single-frame live-color blip without ever deferring a real (structural) change.
    private static bool StructureMatches(in OutfitFingerprint a, in OutfitFingerprint b)
        => a.OutfitTypeId == b.OutfitTypeId
        && a.HatId == b.HatId
        && a.SkinId == b.SkinId
        && a.VisorId == b.VisorId
        && a.PlayerName == b.PlayerName
        && a.Concealed == b.Concealed;

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

    private static int GetPlayerColorId(PlayerControl pc)
    {
        if (MeetingHud.Instance != null || _fixedAllPlayers)
        {
            try { return GetDisplayOutfit(pc).ColorId; } catch { }
        }
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
            // Match the avatar: meeting or fixed-roster bar shows the real outfit, never the live disguise.
            if (MeetingHud.Instance != null || _fixedAllPlayers) return pc.Data.DefaultOutfit;
            return pc.CurrentOutfit ?? pc.Data.DefaultOutfit;
        }
        catch
        {
            return pc.Data.DefaultOutfit;
        }
    }

    private static int GetDisplayOutfitId(PlayerControl pc)
    {
        if (_fixedAllPlayers || MeetingHud.Instance != null) return 0;
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
        if (!_fixedAllPlayers && MeetingHud.Instance == null && CrewmateAvatarRenderer.IsConcealed(player))
            return string.Empty;
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

        // Fill a Color32[] and upload in ONE SetPixels32 call instead of 16384 individual SetPixel interop
        // calls (each crossing the IL2CPP boundary and allocating a Color) — this one-time build dropped from
        // a multi-tens-of-ms main-thread hitch on first speaker to a tight managed loop + a single upload.
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - center + 0.5f;
            float dy = y - center + 0.5f;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float outerA = Mathf.Clamp01((outerR - d) / feather);
            float innerA = Mathf.Clamp01((d - innerR) / feather);
            byte a = (byte)(Mathf.Clamp01(outerA * innerA) * 255f);
            pixels[y * size + x] = new Color32(255, 255, 255, a);
        }

        tex.SetPixels32(pixels);
        tex.Apply();
        _ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        _ringSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _ringSprite;
    }

    // ── Data types ─────────────────────────────────────────────────────────────
    private readonly record struct OutfitFingerprint(
        int OutfitTypeId, int ColorId, string HatId, string SkinId, string VisorId, string PlayerName, bool Concealed);

    private sealed class PublicRosterIdComparer : IComparer<byte>
    {
        public int Compare(byte left, byte right)
            => SpeakingBarRosterPolicy.ComparePlayerIds(left, right, _publiclyDead);
    }

    private class SpeakerSlot
    {
        public GameObject?       IconGO;
        public GameObject?       RingGO;
        public SpriteRenderer?   RingRenderer;
        public TextMeshPro?      LabelTMP;
        public OutfitFingerprint Fingerprint;
        public OutfitFingerprint PendingFingerprint;
        public bool              CosmeticsComplete;
        public float             Level;
        public float             TargetLevel;
        public float             SmoothedLevel;
        public float             Visibility;
        public float             LastActiveTime;
        public float             LastPlayerSeenTime;
        public bool              IsSpeaking;
        public float             LabelWidth;
        public float             LabelHeight;
        public bool              LabelMeasurePending;
        public int               VanillaStyleVersion;
        public float             AppliedGhostAlpha;
        public GameObject?       GhostAlphaIcon;
    }
}
