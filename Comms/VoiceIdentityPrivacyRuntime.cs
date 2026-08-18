using System;
using System.Collections.Generic;
using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// One identity-safe speaking entry after applying the local viewer's role/appearance policy.
/// SourcePlayerId remains the transport speaker; PresentationPlayerId is the only player identity
/// an overlay is allowed to render.
/// </summary>
internal readonly record struct VoiceIdentityPresentedSpeaker(
    byte SourcePlayerId,
    byte PresentationPlayerId,
    float Level);

/// <summary>
/// Frame-cached, viewer-specific visual projection of VoiceOverlayState. All identity-bearing
/// surfaces consume this same projection so a meeting card, fixed slot, or volume meter cannot
/// reveal an identity suppressed by another surface.
/// </summary>
internal sealed class VoiceIdentityPrivacyFrame
{
    internal static readonly VoiceIdentityPrivacyFrame Empty = new([], false, false);

    internal VoiceIdentityPrivacyFrame(
        IReadOnlyList<VoiceIdentityPresentedSpeaker> speakers,
        bool hideAllForViewer,
        bool dimAll)
    {
        Speakers = speakers;
        HideAllForViewer = hideAllForViewer;
        DimAll = dimAll;
    }

    internal IReadOnlyList<VoiceIdentityPresentedSpeaker> Speakers { get; private set; }
    internal bool HideAllForViewer { get; private set; }
    internal bool DimAll { get; private set; }

    internal void Update(
        IReadOnlyList<VoiceIdentityPresentedSpeaker> speakers,
        bool hideAllForViewer,
        bool dimAll)
    {
        Speakers = speakers;
        HideAllForViewer = hideAllForViewer;
        DimAll = dimAll;
    }
}

/// <summary>
/// Mod-agnostic runtime projection for vanilla appearance privacy and registered overlay rules.
/// Role-mod state is supplied through PerfectCommsApi; this assembly performs no mod discovery.
/// </summary>
internal static class VoiceIdentityPrivacyRuntime
{
    private const float SilentPresentationRetentionSeconds = 1f;

    private static readonly Dictionary<byte, PlayerControl> PlayerLookup = new();
    private static readonly Dictionary<byte, int> PlayerInstanceIds = new();
    private static readonly Dictionary<byte, VoiceIdentityPrivacyTransitionGate> TransitionGates = new();
    private static readonly Dictionary<byte, VoiceIdentityPrivacyResolution> CandidateResolutions = new();
    private static readonly Dictionary<byte, VoiceIdentityPrivacyResolution> FrameResolutions = new();
    private static readonly HashSet<byte> ProvisionalSources = new();
    private static readonly Dictionary<byte, byte> LastPresentedBySource = new();
    private static readonly Dictionary<byte, float> SilentPresentationRetainUntil = new();
    private static readonly HashSet<byte> SnapPresentationIds = new();
    private static readonly HashSet<byte> ActiveSources = new();
    private static readonly HashSet<byte> PreviousActiveSources = new();
    private static readonly List<byte> SilentSourceScratch = new(16);
    private static readonly List<byte> StalePlayerIdScratch = new(16);
    private static readonly List<VoiceIdentityPresentedSpeaker> PresentedSpeakers = new(16);
    private static readonly Dictionary<byte, int> PresentationIndex = new();

    private static int _cachedFrame = -1;
    private static VoiceGamePhase _cachedPhase = VoiceGamePhase.Unknown;
    private static int _cachedGameId = int.MinValue;
    private static readonly VoiceIdentityPrivacyFrame CachedPrivacyFrame =
        new(PresentedSpeakers, false, false);
    private static ViewerEvidence _viewerEvidence = ViewerEvidence.KnownNormal;
    private static int _lifecycleGameId = int.MinValue;
    private static readonly VoiceIdentityPrivacyPhaseEpoch LifecyclePhaseEpoch = new();
    private static bool _sourceRequestedHideAll;

    internal static VoiceIdentityPrivacyFrame Current(
        VoiceOverlayState overlay,
        VoiceGamePhase phase)
    {
        int frame = Time.frameCount;
        int gameId = AmongUsClient.Instance?.GameId ?? 0;
        if (VoiceIdentityPrivacyFrameCachePolicy.CanReuse(
                _cachedFrame,
                _cachedPhase,
                _cachedGameId,
                frame,
                phase,
                gameId))
            return CachedPrivacyFrame;

        PresentedSpeakers.Clear();
        PresentationIndex.Clear();
        CandidateResolutions.Clear();
        FrameResolutions.Clear();
        ProvisionalSources.Clear();
        ActiveSources.Clear();
        SnapPresentationIds.Clear();
        _sourceRequestedHideAll = false;

        ResetForLifecycleIfNeeded(phase, gameId);
        RebuildPlayerLookup();
        _viewerEvidence = ReadViewerEvidence(phase);

        var remotes = overlay.RemotePlayers;
        for (int i = 0; i < remotes.Count; i++)
        {
            var remote = remotes[i];
            if (!remote.IsSpeaking || !remote.IsAudible || remote.PlayerId == byte.MaxValue)
                continue;

            AddSpeakingSource(remote.PlayerId, remote.Level, phase);
        }

        var local = PlayerControl.LocalPlayer;
        if (overlay.Local.IsSpeaking)
        {
            byte localPlayerId = local != null
                ? local.PlayerId
                : VoiceChatRoom.Current?.CurrentSnapshot?.LocalPlayerId ?? byte.MaxValue;
            if (localPlayerId != byte.MaxValue)
                AddSpeakingSource(localPlayerId, overlay.Local.Level, phase);
        }

        // Keep the last visible presentation just beyond every UI's release animation. During this
        // bounded quiet tail we continue accepting policy changes and snap the old presentation if
        // the source becomes concealed or changes alias before its ring/card/meter has fully faded.
        float now = Time.unscaledTime;
        SilentSourceScratch.Clear();
        foreach (byte sourceId in PreviousActiveSources)
            if (!ActiveSources.Contains(sourceId))
                SilentSourceScratch.Add(sourceId);

        for (int i = 0; i < SilentSourceScratch.Count; i++)
        {
            byte sourceId = SilentSourceScratch[i];
            if (LastPresentedBySource.ContainsKey(sourceId))
            {
                SilentPresentationRetainUntil[sourceId] =
                    now + SilentPresentationRetentionSeconds;
            }
            else
            {
                // A source that was already hidden has no visible provenance to retain, but its gate
                // still needs the quiet edge so the next utterance can adopt the current policy.
                var candidate = ResolveCandidate(sourceId, phase);
                GetTransitionGate(sourceId).Observe(
                    candidate,
                    isSpeaking: false,
                    isProvisional: ProvisionalSources.Contains(sourceId));
            }
        }

        SilentSourceScratch.Clear();
        foreach (var retained in SilentPresentationRetainUntil)
        {
            byte sourceId = retained.Key;
            if (ActiveSources.Contains(sourceId))
            {
                SilentSourceScratch.Add(sourceId);
                continue;
            }

            if (!LastPresentedBySource.TryGetValue(sourceId, out byte previousPresentationId)
                || now >= retained.Value)
            {
                LastPresentedBySource.Remove(sourceId);
                SilentSourceScratch.Add(sourceId);
                continue;
            }

            var candidate = ResolveCandidate(sourceId, phase);
            GetTransitionGate(sourceId).Observe(
                candidate,
                isSpeaking: false,
                isProvisional: ProvisionalSources.Contains(sourceId));
            if (!candidate.HasConcretePresentation
                || candidate.PresentationPlayerId != previousPresentationId)
            {
                SnapPresentationIds.Add(previousPresentationId);
                LastPresentedBySource.Remove(sourceId);
                SilentSourceScratch.Add(sourceId);
            }
        }

        for (int i = 0; i < SilentSourceScratch.Count; i++)
        {
            SilentPresentationRetainUntil.Remove(SilentSourceScratch[i]);
        }

        PreviousActiveSources.Clear();
        foreach (byte sourceId in ActiveSources)
            PreviousActiveSources.Add(sourceId);

        if (_sourceRequestedHideAll)
        {
            foreach (byte presentationId in PresentationIndex.Keys)
                SnapPresentationIds.Add(presentationId);
            PresentedSpeakers.Clear();
            PresentationIndex.Clear();
        }
        else
        {
            // A colliding source can keep the same presentation legitimately active. Never snap a
            // shared target while another source still resolves to it this frame.
            foreach (byte presentationId in PresentationIndex.Keys)
                SnapPresentationIds.Remove(presentationId);
        }

        bool hideAll = !_viewerEvidence.Known || _viewerEvidence.HideAll || _sourceRequestedHideAll;
        bool dimAll = _viewerEvidence.Known && !hideAll && _viewerEvidence.DimAll;
        CachedPrivacyFrame.Update(PresentedSpeakers, hideAll, dimAll);
        // Commit the cache key only after a complete projection. If a soft-dependency callback throws
        // unexpectedly outside its fail-private wrapper, a later consumer in this frame must retry
        // instead of receiving a partially rebuilt frame.
        _cachedFrame = frame;
        _cachedPhase = phase;
        _cachedGameId = gameId;
        return CachedPrivacyFrame;
    }

    /// <summary>
    /// Resolves current policy without changing the quiet-edge gate. Used to remove a stale dynamic
    /// slot as soon as its player becomes concealed, even if that player is currently silent.
    /// </summary>
    internal static VoiceIdentityPrivacyResolution Peek(
        byte sourcePlayerId,
        VoiceGamePhase phase)
    {
        int gameId = AmongUsClient.Instance?.GameId ?? 0;
        if (!VoiceIdentityPrivacyFrameCachePolicy.CanReuse(
                _cachedFrame,
                _cachedPhase,
                _cachedGameId,
                Time.frameCount,
                phase,
                gameId))
        {
            ResetForLifecycleIfNeeded(phase, gameId);
            RebuildPlayerLookup();
            _viewerEvidence = ReadViewerEvidence(phase);
            // Peek may run before the frame's full projection. Never reuse a source result collected
            // under an earlier frame or exact phase, but leave _cachedFrame untouched so Current
            // still performs the complete rebuild when its overlay is available.
            CandidateResolutions.Clear();
            FrameResolutions.Clear();
            ProvisionalSources.Clear();
        }
        if (FrameResolutions.TryGetValue(sourcePlayerId, out var effective))
            return effective;
        return ResolveCandidate(sourcePlayerId, phase);
    }

    /// <summary>
    /// True when an active source or a retained quiet-tail source lost/changed this presentation
    /// because of a privacy transition. UI should snap it off; an ordinary quiet edge may still fade.
    /// </summary>
    internal static bool ShouldSnapPresentation(byte presentationPlayerId)
        => SnapPresentationIds.Contains(presentationPlayerId);

    internal static bool TryFindPlayer(byte playerId, out PlayerControl player)
    {
        if (_cachedFrame != Time.frameCount)
            RebuildPlayerLookup();
        return PlayerLookup.TryGetValue(playerId, out player!) && player != null;
    }

    internal static void Reset()
    {
        _cachedFrame = -1;
        _cachedPhase = VoiceGamePhase.Unknown;
        _cachedGameId = int.MinValue;
        CachedPrivacyFrame.Update(PresentedSpeakers, false, false);
        _viewerEvidence = ViewerEvidence.KnownNormal;
        _lifecycleGameId = int.MinValue;
        LifecyclePhaseEpoch.Reset();
        _sourceRequestedHideAll = false;
        PresentedSpeakers.Clear();
        PresentationIndex.Clear();
        CandidateResolutions.Clear();
        FrameResolutions.Clear();
        ProvisionalSources.Clear();
        LastPresentedBySource.Clear();
        SilentPresentationRetainUntil.Clear();
        SnapPresentationIds.Clear();
        ActiveSources.Clear();
        PreviousActiveSources.Clear();
        SilentSourceScratch.Clear();
        TransitionGates.Clear();
        PlayerLookup.Clear();
        PlayerInstanceIds.Clear();
        StalePlayerIdScratch.Clear();
    }

    private static void AddSpeakingSource(
        byte sourcePlayerId,
        float level,
        VoiceGamePhase phase)
    {
        if (!ActiveSources.Add(sourcePlayerId))
            return;

        SilentPresentationRetainUntil.Remove(sourcePlayerId);

        var candidate = ResolveCandidate(sourcePlayerId, phase);
        var gate = GetTransitionGate(sourcePlayerId);
        bool isProvisional = ProvisionalSources.Contains(sourcePlayerId);
        // A source that was quiet last frame may safely adopt its latest disguise/concealment before
        // this utterance begins. Only changes while continuously speaking require quarantine.
        if (!PreviousActiveSources.Contains(sourcePlayerId))
            gate.Observe(candidate, isSpeaking: false, isProvisional);
        var transition = gate.Observe(candidate, isSpeaking: true, isProvisional);
        var resolution = transition.EffectiveResolution;
        if (resolution.Decision == VoiceIdentityPrivacyDecision.HideAllForViewer)
            _sourceRequestedHideAll = true;
        FrameResolutions[sourcePlayerId] = resolution;

        bool hasPresentation = VoiceIdentityAliasCollision.TryGetPresentationPlayerId(
            resolution,
            out byte presentationPlayerId);
        if (LastPresentedBySource.TryGetValue(sourcePlayerId, out byte previousPresentationId)
            && (!hasPresentation || previousPresentationId != presentationPlayerId))
        {
            SnapPresentationIds.Add(previousPresentationId);
        }

        if (!hasPresentation)
        {
            LastPresentedBySource.Remove(sourcePlayerId);
            return;
        }

        LastPresentedBySource[sourcePlayerId] = presentationPlayerId;

        level = Mathf.Clamp01(level);
        if (PresentationIndex.TryGetValue(presentationPlayerId, out int existingIndex))
        {
            var existing = PresentedSpeakers[existingIndex];
            if (level > existing.Level)
                PresentedSpeakers[existingIndex] = existing with { Level = level };
            return;
        }

        PresentationIndex[presentationPlayerId] = PresentedSpeakers.Count;
        PresentedSpeakers.Add(new VoiceIdentityPresentedSpeaker(sourcePlayerId, presentationPlayerId, level));
    }

    private static VoiceIdentityPrivacyTransitionGate GetTransitionGate(byte sourcePlayerId)
    {
        if (!TransitionGates.TryGetValue(sourcePlayerId, out var gate))
        {
            gate = new VoiceIdentityPrivacyTransitionGate();
            TransitionGates[sourcePlayerId] = gate;
        }
        return gate;
    }

    private static bool UsesBuiltInAppearancePrivacy(VoiceGamePhase phase)
        => VoiceIdentityPrivacyPhasePolicy.UsesBuiltInAppearancePrivacy(phase);

    private static VoiceIdentityPrivacyResolution ResolveCandidate(
        byte sourcePlayerId,
        VoiceGamePhase phase)
    {
        if (CandidateResolutions.TryGetValue(sourcePlayerId, out var cached))
            return cached;

        var evidence = new VoiceIdentityPrivacyEvidence(
            ViewerStateKnown: _viewerEvidence.Known,
            SourceStateKnown: false,
            HideAllForViewer: _viewerEvidence.HideAll,
            DimAll: _viewerEvidence.DimAll);

        if (!PlayerLookup.TryGetValue(sourcePlayerId, out var source) || source == null || source.Data == null)
        {
            // Meeting/exile scene transitions can temporarily rebuild AllPlayerControls while the
            // authenticated routing snapshot and public card still identify this audible source. Do
            // not seed HideSource into the freshly reset gate in that gap: it would suppress the ring
            // until the speaker pauses. Third-party source rules still fail private because they need
            // a live PlayerControl callback context.
            if (HasStablePublicIdentity(sourcePlayerId, phase)
                && !VoiceModRegistry.HasOverlaySpeakerRules)
            {
                evidence = evidence with { SourceStateKnown = true };
                return ResolveAndCacheCandidate(
                    sourcePlayerId,
                    evidence);
            }
            return ResolveAndCacheCandidate(
                sourcePlayerId,
                evidence);
        }

        bool sourceKnown = true;
        bool hideSource = false;
        bool aliasActive = false;
        byte? aliasPlayerId = null;
        if (UsesBuiltInAppearancePrivacy(phase))
            sourceKnown = TryReadBuiltInSourceEvidence(source, phase, out hideSource);

        var local = PlayerControl.LocalPlayer;
        var external = VoiceModRegistry.HasOverlaySpeakerRules
            ? VoiceModRegistry.ResolveOverlaySpeakerPrivacy(
                local,
                source,
                VoiceModBridge.ToApiPhase(phase),
                local != null && VoiceRoleMuteState.IsVoiceDead(local),
                VoiceRoleMuteState.IsVoiceDead(source))
            : VoiceOverlaySpeakerResult.Pass;
        switch (external.Verdict)
        {
            case VoiceOverlaySpeakerVerdict.HideAll:
                evidence = evidence with { HideAllForViewer = true };
                break;
            case VoiceOverlaySpeakerVerdict.HideSource:
                hideSource = true;
                break;
            case VoiceOverlaySpeakerVerdict.Alias:
                aliasActive = true;
                if (external.AliasPlayerId is not { } externalAlias ||
                    !IsSafeAliasTarget(externalAlias, phase))
                {
                    aliasPlayerId = null;
                }
                else
                {
                    aliasPlayerId = externalAlias;
                    hideSource = false;
                }
                break;
        }

        evidence = evidence with
        {
            SourceStateKnown = sourceKnown,
            HideSource = hideSource,
            AliasActive = aliasActive,
            AliasPlayerId = aliasPlayerId,
        };
        return ResolveAndCacheCandidate(
            sourcePlayerId,
            evidence);
    }

    private static ViewerEvidence ReadViewerEvidence(VoiceGamePhase phase)
    {
        var local = PlayerControl.LocalPlayer;
        if (phase is VoiceGamePhase.Menu or VoiceGamePhase.Lobby or VoiceGamePhase.EndGame)
            return ViewerEvidence.KnownNormal;
        if (local == null)
        {
            bool mustInspectViewer =
                UsesBuiltInAppearancePrivacy(phase) ||
                VoiceModRegistry.HasOverlayViewerRules ||
                VoiceModRegistry.HasOverlaySpeakerRules;
            return mustInspectViewer
                ? new ViewerEvidence(false, false, false)
                : ViewerEvidence.KnownNormal;
        }

        bool known = true;
        bool hideAll = false;
        bool dimAll = false;

        if (UsesBuiltInAppearancePrivacy(phase))
        {
            if (TryReadOutfitType(local, out int localOutfitType))
            {
                // Mushroom Mix-Up randomizes every visible identity for this viewer.
                hideAll |= localOutfitType == 3;
            }
            else
            {
                known = false;
            }
        }

        var external = VoiceModRegistry.ResolveOverlayViewerPrivacy(
            local,
            VoiceModBridge.ToApiPhase(phase),
            VoiceRoleMuteState.IsVoiceDead(local));
        switch (external.Verdict)
        {
            case VoiceOverlayViewerVerdict.HideAll:
                hideAll = true;
                break;
            case VoiceOverlayViewerVerdict.DimAll:
                dimAll = true;
                break;
        }

        return new ViewerEvidence(known, hideAll, dimAll && !hideAll);
    }

    private static bool TryReadBuiltInSourceEvidence(
        PlayerControl source,
        VoiceGamePhase phase,
        out bool hideSource)
    {
        hideSource = false;

        if (!TryReadOutfitType(source, out int outfitType))
            return false;

        try
        {
            if (global::VoiceChatPlugin.CrewmateAvatarRenderer.IsConcealed(source))
            {
                hideSource = true;
                return true;
            }
        }
        catch
        {
            return false;
        }

        // 0 is the normal outfit and 2 is HorseWrangler's body-shape-only presentation. Every other
        // outfit changes or obscures identity. A registered source rule may replace this with a safe
        // explicit alias; without one the privacy-preserving result is to hide the source.
        if (outfitType is not (0 or 2))
        {
            hideSource = true;
            return true;
        }

        if (!TryReadBodyAlpha(source, out float bodyAlpha))
            return false;
        if (bodyAlpha < 0.95f)
        {
            hideSource = true;
            return true;
        }

        if (VoiceSceneState.IsTaskVoicePhase(phase))
        {
            try
            {
                if (!source.Visible || source.shouldAppearInvisible)
                    hideSource = true;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeAliasTarget(byte targetId, VoiceGamePhase phase)
    {
        if (targetId == byte.MaxValue)
            return false;

        if (!PlayerLookup.TryGetValue(targetId, out var target) ||
            target == null ||
            target.Data == null)
        {
            // Meeting/exile can rebuild controls while the public roster slot remains authoritative.
            return HasStablePublicIdentity(targetId, phase);
        }

        if (target.Data.Disconnected)
            return false;
        if (!VoiceIdentityPrivacyPhasePolicy.UsesBuiltInAppearancePrivacy(phase))
            return true;

        try
        {
            if (global::VoiceChatPlugin.CrewmateAvatarRenderer.IsConcealed(target))
                return false;
        }
        catch
        {
            return false;
        }

        if (!TryReadOutfitType(target, out int targetOutfitType) ||
            targetOutfitType is not (0 or 2))
            return false;
        if (!TryReadBodyAlpha(target, out float bodyAlpha) || bodyAlpha < 0.95f)
            return false;

        try
        {
            return target.Visible && !target.shouldAppearInvisible;
        }
        catch
        {
            return false;
        }
    }

    private static VoiceIdentityPrivacyResolution ResolveAndCacheCandidate(
        byte sourcePlayerId,
        VoiceIdentityPrivacyEvidence evidence)
    {
        if (VoiceIdentityPrivacyPolicy.IsProvisional(evidence))
            ProvisionalSources.Add(sourcePlayerId);
        else
            ProvisionalSources.Remove(sourcePlayerId);

        var resolution = VoiceIdentityPrivacyPolicy.Resolve(sourcePlayerId, evidence);
        CandidateResolutions[sourcePlayerId] = resolution;
        return resolution;
    }

    private static bool HasStablePublicIdentity(byte sourcePlayerId, VoiceGamePhase phase)
    {
        bool authenticatedRosterContainsSource = false;
        var snapshot = VoiceChatRoom.Current?.CurrentSnapshot;
        if (snapshot != null
            && snapshot.TryGetPlayer(sourcePlayerId, out var player)
            && !player.Disconnected
            && !player.IsDummy
            && player.ClientId >= 0)
        {
            authenticatedRosterContainsSource = true;
        }

        bool publicSurfaceContainsSource = MeetingHasPublicSlot(sourcePlayerId)
                                           || (phase == VoiceGamePhase.EndGame
                                               && global::VoiceChatPlugin.CrewmateAvatarRenderer.HasCachedIdentity(
                                                   sourcePlayerId));
        return VoiceIdentityPrivacyPhasePolicy.CanPresentStablePublicIdentity(
            phase,
            authenticatedRosterContainsSource,
            publicSurfaceContainsSource);
    }

    private static bool MeetingHasPublicSlot(byte sourcePlayerId)
    {
        try
        {
            var states = MeetingHud.Instance?.playerStates;
            if (states == null) return false;
            foreach (var state in states)
            {
                if (state != null
                    && PlayerVoteAreaPlayerId.TryRead(state, out var playerId)
                    && playerId == sourcePlayerId)
                    return true;
            }
        }
        catch
        {
            // A meeting card collection may be rebuilding at the same transition edge. The retained
            // authenticated snapshot above remains the preferred source; otherwise fail private.
        }

        return false;
    }


    private static bool TryReadOutfitType(PlayerControl player, out int outfitType)
    {
        try
        {
            outfitType = (int)player.CurrentOutfitType;
            return true;
        }
        catch
        {
            outfitType = 0;
            return false;
        }
    }

    private static bool TryReadBodyAlpha(PlayerControl player, out float alpha)
    {
        try
        {
            alpha = player.cosmetics.currentBodySprite.BodySprite.color.a;
            return true;
        }
        catch
        {
            alpha = 1f;
            return false;
        }
    }

    private static void RebuildPlayerLookup()
    {
        PlayerLookup.Clear();
        try
        {
            var players = PlayerControl.AllPlayerControls;
            if (players == null) return;
            foreach (var player in players)
            {
                if (player == null || player.Data == null || PlayerLookup.ContainsKey(player.PlayerId))
                    continue;
                PlayerLookup[player.PlayerId] = player;

                try
                {
                    int instanceId = player.GetInstanceID();
                    if (PlayerInstanceIds.TryGetValue(player.PlayerId, out int previousInstanceId)
                        && previousInstanceId != instanceId)
                    {
                        ResetSourceState(player.PlayerId);
                    }
                    PlayerInstanceIds[player.PlayerId] = instanceId;
                }
                catch
                {
                    // A transient Unity-object read does not make the player lookup unusable. The
                    // source evidence path still fails private if the rest of the object is incomplete.
                }
            }

            StalePlayerIdScratch.Clear();
            foreach (byte playerId in PlayerInstanceIds.Keys)
                if (!PlayerLookup.ContainsKey(playerId))
                    StalePlayerIdScratch.Add(playerId);
            for (int i = 0; i < StalePlayerIdScratch.Count; i++)
            {
                byte playerId = StalePlayerIdScratch[i];
                PlayerInstanceIds.Remove(playerId);
                ResetSourceState(playerId);
            }
        }
        catch
        {
            // A partial lookup fails closed per requested source through SourceStateKnown=false.
        }
    }

    private static void ResetSourceState(byte playerId)
    {
        TransitionGates.Remove(playerId);
        CandidateResolutions.Remove(playerId);
        FrameResolutions.Remove(playerId);
        ProvisionalSources.Remove(playerId);
        LastPresentedBySource.Remove(playerId);
        SilentPresentationRetainUntil.Remove(playerId);
        ActiveSources.Remove(playerId);
        PreviousActiveSources.Remove(playerId);
    }

    private static void ResetForLifecycleIfNeeded(VoiceGamePhase phase, int gameId)
    {
        bool newGame = gameId != 0 && gameId != _lifecycleGameId;
        if (newGame)
            LifecyclePhaseEpoch.Reset();
        var previousPhase = LifecyclePhaseEpoch.LastKnownPhase;
        bool returnedToLobby = phase == VoiceGamePhase.Lobby && previousPhase != VoiceGamePhase.Lobby;
        bool returnedToMenu = phase == VoiceGamePhase.Menu && previousPhase != VoiceGamePhase.Menu;
        bool privacyPhaseChanged = LifecyclePhaseEpoch.Advance(phase);
        if (newGame || returnedToLobby || returnedToMenu || privacyPhaseChanged)
        {
            ResetPresentationTransitionState();
            if (newGame || returnedToLobby || returnedToMenu)
                PlayerInstanceIds.Clear();
        }

        _lifecycleGameId = gameId;
    }

    private static void ResetPresentationTransitionState()
    {
        // Remove any prior-phase alias/identity immediately instead of letting its release animation
        // survive into a phase with different built-in or external presentation rules.
        foreach (byte presentationId in LastPresentedBySource.Values)
            SnapPresentationIds.Add(presentationId);

        TransitionGates.Clear();
        CandidateResolutions.Clear();
        FrameResolutions.Clear();
        ProvisionalSources.Clear();
        LastPresentedBySource.Clear();
        SilentPresentationRetainUntil.Clear();
        ActiveSources.Clear();
        PreviousActiveSources.Clear();
        SilentSourceScratch.Clear();
        _sourceRequestedHideAll = false;
    }

    private readonly record struct ViewerEvidence(bool Known, bool HideAll, bool DimAll)
    {
        internal static ViewerEvidence KnownNormal { get; } = new(true, false, false);
    }
}
