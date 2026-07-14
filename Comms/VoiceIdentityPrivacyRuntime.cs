using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
/// Runtime adapter for Town of Us/Mira and vanilla appearance state. TouMira is intentionally a
/// soft dependency, so all role access is reflection-based and cached. The pure policy and
/// transition behavior live in VoiceIdentityPrivacyPolicy.cs and are unit-tested without Unity.
/// </summary>
internal static class VoiceIdentityPrivacyRuntime
{
    private const float SilentPresentationRetentionSeconds = 1f;
    private const string ModifierExtensionsName = "MiraAPI.Modifiers.ModifierExtensions";
    private const string ModifierComponentName = "MiraAPI.Modifiers.ModifierComponent";

    private const string MorphlingMorphName = VoiceConcealedModifierSetPolicy.MorphlingMorphName;
    private const string GlitchMimicName = VoiceConcealedModifierSetPolicy.GlitchMimicName;
    private const string ShapeshifterShiftName = VoiceConcealedModifierSetPolicy.ShapeshifterShiftName;
    private const string ParasiteInfectedName = "TownOfUs.Modifiers.Impostor.ParasiteInfectedModifier";
    private const string ConcealedModifierName = "TownOfUs.Modifiers.ConcealedModifier";

    private const string HerbalistConfusedName = "TownOfUs.Modifiers.Impostor.Herbalist.HerbalistConfusedModifier";
    private const string HypnotisedName = "TownOfUs.Modifiers.Impostor.HypnotisedModifier";
    private const string EclipsalBlindName = "TownOfUs.Modifiers.Impostor.EclipsalBlindModifier";
    private const string GrenadierFlashName = "TownOfUs.Modifiers.Impostor.GrenadierFlashModifier";
    private const string HnsGlobalCamouflageName = "TownOfUs.Modifiers.HnsImpostor.HnsGlobalCamouflageModifier";

    private static readonly string[] HiddenModifierNames =
    [
        "TownOfUs.Modifiers.Impostor.SwoopModifier",
        "TownOfUs.Modifiers.HnsCrewmate.HnsChameleonSwoopModifier",
        "TownOfUs.Modifiers.Impostor.AmbusherConcealedModifier",
        "TownOfUs.Modifiers.Impostor.Venerer.VenererCamouflageModifier",
        "TownOfUs.Modifiers.Crewmate.MediumHiddenModifier",
    ];

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

    private static MethodInfo? _getModifierMethod;
    private static Type? _getModifierMethodOwner;
    private static object?[]? _getModifierArguments;
    private static MethodInfo? _getModifierComponentMethod;
    private static Type? _getModifierComponentMethodOwner;
    private static object?[]? _getModifierComponentArguments;
    private static Type? _activeModifiersOwner;
    private static PropertyInfo? _activeModifiersProperty;
    private static FieldInfo? _activeModifiersField;
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
        if (VoiceIdentityPrivacyPhasePolicy.UsesBuiltInAppearancePrivacy(phase))
        {
            sourceKnown = TryReadSourceEvidence(
                source,
                phase,
                out hideSource,
                out aliasActive,
                out aliasPlayerId);
        }
        bool builtInAliasUnresolved = aliasActive && !aliasPlayerId.HasValue;

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
                if (builtInAliasUnresolved
                    || external.AliasPlayerId is not { } externalAlias
                    || !IsSafeAliasTarget(externalAlias, phase)
                    || (aliasPlayerId.HasValue && aliasPlayerId.Value != externalAlias))
                {
                    aliasPlayerId = null;
                }
                else
                {
                    aliasPlayerId = externalAlias;
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
                VoiceIdentityPrivacyPhasePolicy.UsesBuiltInAppearancePrivacy(phase)
                || VoiceModRegistry.HasOverlayViewerRules
                || VoiceModRegistry.HasOverlaySpeakerRules;
            return mustInspectViewer
                ? new ViewerEvidence(false, false, false)
                : ViewerEvidence.KnownNormal;
        }

        bool known = true;
        bool hideAll = false;
        bool dimAll = false;

        if (VoiceIdentityPrivacyPhasePolicy.UsesBuiltInAppearancePrivacy(phase))
        {
            known &= TryHasModifier(local, HerbalistConfusedName, out bool herbalistConfused);
            hideAll |= herbalistConfused;

            known &= TryGetNamedModifier(local, HypnotisedName, out var hypnotised);
            if (hypnotised != null)
            {
                if (TryReadBool(hypnotised, "HysteriaActive", out bool hysteriaActive))
                    hideAll |= hysteriaActive;
                else
                    known = false;
            }

            known &= TryHasModifier(local, EclipsalBlindName, out bool eclipsalBlind);
            hideAll |= eclipsalBlind;

            known &= TryHasModifier(local, GrenadierFlashName, out bool grenadierFlash);
            if (grenadierFlash)
            {
                // TouMira fully blinds living non-impostors but only dims impostor-aligned/dead viewers.
                // Dim is still treated as non-attributing in speaker-only mode; fixed/static UI may dim.
                if (!VoiceRoleMuteState.IsVoiceDead(local) && !VoiceRoleMuteState.IsVoiceImpostor(local))
                    hideAll = true;
                else
                    dimAll = true;
            }

            known &= TryHasModifier(local, HnsGlobalCamouflageName, out bool hnsGlobalCamouflage);
            hideAll |= hnsGlobalCamouflage;

            if (TryReadOutfitType(local, out int localOutfitType))
            {
                // Mushroom Mix-Up is a viewer-wide random identity scramble.
                hideAll |= localOutfitType == 3;
            }
            else
            {
                known = false;
            }

            if (TryReadStaticBool("TownOfUs.Patches.HudManagerPatches", "CamouflageCommsEnabled", out bool commsCamo))
                hideAll |= commsCamo;
            else
                known = false;
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

    private static bool TryReadSourceEvidence(
        PlayerControl source,
        VoiceGamePhase phase,
        out bool hideSource,
        out bool aliasActive,
        out byte? aliasPlayerId)
    {
        hideSource = false;
        aliasActive = false;
        aliasPlayerId = null;

        // Only these explicit presentation modifiers are aliases. In particular, Ambusher.Target is
        // an attack victim and Parasite.Controller is a control/listening relationship, not a voice identity.
        if (!TryReadAlias(source, MorphlingMorphName, out bool morphActive, out byte? morphTarget)
            || !TryReadAlias(source, GlitchMimicName, out bool mimicActive, out byte? mimicTarget)
            || !TryReadAlias(source, ShapeshifterShiftName, out bool shiftActive, out byte? shiftTarget))
        {
            return false;
        }

        aliasActive = morphActive || mimicActive || shiftActive;
        aliasPlayerId = SelectAliasTarget(
            morphActive, morphTarget,
            mimicActive, mimicTarget,
            shiftActive, shiftTarget);

        for (int i = 0; i < HiddenModifierNames.Length; i++)
        {
            if (!TryHasModifier(source, HiddenModifierNames[i], out bool hidden))
                return false;
            if (hidden)
            {
                hideSource = true;
                return true;
            }
        }

        if (!TryHasModifier(source, ParasiteInfectedName, out bool parasiteControlled))
            return false;

        if (!TryReadOutfitType(source, out int outfitType))
            return false;

        // Parasite itself never aliases to the victim and the victim never aliases merely because it
        // is controlled. If the optional victim-looking-like-Parasite presentation is active (Morph),
        // suppress attribution instead of falsely claiming the controller is speaking.
        if (parasiteControlled && outfitType == 7)
        {
            hideSource = true;
            return true;
        }

        // TouMira deliberately gives every concealment/disguise a common base modifier. Inspect the
        // complete active set: a known Morph/Mimic plus one unknown stacked concealment is still
        // private. Looking up only the first assignable modifier would allow that stack to leak.
        if (!TryInspectConcealedModifierSet(
                source,
                morphActive,
                mimicActive,
                shiftActive,
                out bool hasConcealedModifier,
                out bool allConcealedModifiersAreRecognizedAliases))
        {
            return false;
        }
        if (hasConcealedModifier && !allConcealedModifiersAreRecognizedAliases)
        {
            hideSource = true;
            return true;
        }

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

        // Known identity-changing/anonymous outfit values that did not have an authoritative alias
        // above are unsafe. HorseWrangler (2) only changes body shape and is not an identity disguise.
        if (!aliasActive && outfitType != 0 && outfitType != 2)
        {
            hideSource = true;
            return true;
        }

        // Covers Shy and future transparency-based concealments that do not change outfit type.
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
                {
                    hideSource = true;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        if (aliasActive)
        {
            if (aliasPlayerId is not { } targetId || !IsSafeAliasTarget(targetId, phase))
                aliasPlayerId = null;
        }

        return true;
    }

    private static byte? SelectAliasTarget(
        bool firstActive,
        byte? firstTarget,
        bool secondActive,
        byte? secondTarget,
        bool thirdActive,
        byte? thirdTarget)
    {
        byte? selected = null;
        bool hasSelection = false;
        if (!MergeAlias(firstActive, firstTarget, ref selected, ref hasSelection)
            || !MergeAlias(secondActive, secondTarget, ref selected, ref hasSelection)
            || !MergeAlias(thirdActive, thirdTarget, ref selected, ref hasSelection))
        {
            return null;
        }
        return selected;
    }

    private static bool MergeAlias(bool active, byte? target, ref byte? selected, ref bool hasSelection)
    {
        if (!active) return true;
        if (!hasSelection)
        {
            selected = target;
            hasSelection = true;
            return true;
        }
        return selected == target;
    }

    private static bool TryInspectConcealedModifierSet(
        PlayerControl player,
        bool morphActive,
        bool mimicActive,
        bool shiftActive,
        out bool hasConcealedModifier,
        out bool allConcealedModifiersAreRecognizedAliases)
    {
        hasConcealedModifier = false;
        allConcealedModifiersAreRecognizedAliases = true;

        var concealedType = ResolveType(ConcealedModifierName);
        if (concealedType == null)
            return true; // This TouMira version has no common concealment base.

        var extensionsType = ResolveType(ModifierExtensionsName);
        var componentType = ResolveType(ModifierComponentName);
        if (extensionsType == null || componentType == null)
            return false;

        var method = ResolveGetModifierComponentMethod(extensionsType, componentType);
        var args = _getModifierComponentArguments;
        if (method == null || args == null || args.Length == 0)
            return false;

        object? component;
        try
        {
            Array.Clear(args, 0, args.Length);
            args[0] = player;
            component = method.Invoke(null, args);
        }
        catch
        {
            return false;
        }

        // A component cannot have an active modifier before it exists. This is the expected lobby
        // state on some Mira builds and is therefore a known-empty set, not an inspection failure.
        if (component == null)
            return true;

        object? activeModifiers;
        try
        {
            ResolveActiveModifiersMember(component.GetType());
            activeModifiers = _activeModifiersProperty?.GetValue(component)
                              ?? _activeModifiersField?.GetValue(component);
        }
        catch
        {
            return false;
        }

        if (activeModifiers == null)
            return false;

        return TryInspectConcealedModifierCollection(
            activeModifiers,
            concealedType,
            morphActive,
            mimicActive,
            shiftActive,
            out hasConcealedModifier,
            out allConcealedModifiersAreRecognizedAliases);
    }

    private static bool TryInspectConcealedModifierCollection(
        object activeModifiers,
        Type concealedType,
        bool morphActive,
        bool mimicActive,
        bool shiftActive,
        out bool hasConcealedModifier,
        out bool allConcealedModifiersAreRecognizedAliases)
    {
        hasConcealedModifier = false;
        allConcealedModifiersAreRecognizedAliases = true;

        try
        {
            if (activeModifiers is IEnumerable enumerable)
            {
                foreach (object? modifier in enumerable)
                {
                    InspectConcealedModifier(
                        modifier,
                        concealedType,
                        morphActive,
                        mimicActive,
                        shiftActive,
                        ref hasConcealedModifier,
                        ref allConcealedModifiersAreRecognizedAliases);
                }
                return true;
            }

            // Some IL2CPP collection wrappers do not expose System.Collections.IEnumerable even
            // though they retain Count/get_Item. Support that shape without accepting an unreadable set.
            var collectionType = activeModifiers.GetType();
            var countProperty = collectionType.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var itemProperty = collectionType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (countProperty?.GetValue(activeModifiers) is not int count || itemProperty == null)
                return false;

            for (int i = 0; i < count; i++)
            {
                object? modifier = itemProperty.GetValue(activeModifiers, [i]);
                InspectConcealedModifier(
                    modifier,
                    concealedType,
                    morphActive,
                    mimicActive,
                    shiftActive,
                    ref hasConcealedModifier,
                    ref allConcealedModifiersAreRecognizedAliases);
            }
            return true;
        }
        catch
        {
            hasConcealedModifier = false;
            allConcealedModifiersAreRecognizedAliases = false;
            return false;
        }
    }

    private static void InspectConcealedModifier(
        object? modifier,
        Type concealedType,
        bool morphActive,
        bool mimicActive,
        bool shiftActive,
        ref bool hasConcealedModifier,
        ref bool allConcealedModifiersAreRecognizedAliases)
    {
        if (modifier == null || !concealedType.IsInstanceOfType(modifier))
            return;

        hasConcealedModifier = true;
        string? fullName = modifier.GetType().FullName;
        if (!VoiceConcealedModifierSetPolicy.IsRecognizedActiveAlias(
                fullName,
                morphActive,
                mimicActive,
                shiftActive))
        {
            allConcealedModifiersAreRecognizedAliases = false;
        }
    }

    private static bool IsSafeAliasTarget(byte targetId, VoiceGamePhase phase)
    {
        if (targetId == byte.MaxValue)
            return false;

        if (!PlayerLookup.TryGetValue(targetId, out var target)
            || target == null
            || target.Data == null)
        {
            // Public aliases point at an already-published identity slot. During Meeting/Exile scene
            // rebuilds the target control may be absent for a frame even though the authenticated
            // roster/card remains authoritative. Tasks never get this fallback.
            return HasStablePublicIdentity(targetId, phase);
        }

        if (target.Data.Disconnected)
            return false;

        // A meeting/public-results slot already exposes this player's stable identity. An explicit
        // third-party rule may still alias to that slot, but task-world camouflage on the target must
        // not make an otherwise valid public slot fail closed.
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

        if (!TryReadOutfitType(target, out int targetOutfitType))
            return false;
        if (targetOutfitType is not (0 or 2))
            return false;

        // An alias must not route attribution onto a target with any active concealment, including
        // a stacked/unknown modifier whose current outfit happens to look normal.
        if (!TryInspectConcealedModifierSet(
                target,
                morphActive: false,
                mimicActive: false,
                shiftActive: false,
                out bool targetHasConcealedModifier,
                out _)
            || targetHasConcealedModifier)
        {
            return false;
        }

        if (!TryReadBodyAlpha(target, out float bodyAlpha) || bodyAlpha < 0.95f)
            return false;

        try
        {
            if (!target.Visible || target.shouldAppearInvisible)
                return false;
        }
        catch
        {
            return false;
        }

        return true;
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
                if (state != null && state.TargetPlayerId == sourcePlayerId)
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

    private static bool TryReadAlias(PlayerControl source, string modifierName, out bool active, out byte? targetId)
    {
        active = false;
        targetId = null;
        if (!TryGetNamedModifier(source, modifierName, out var modifier))
            return false;
        if (modifier == null)
            return true;

        active = true;
        try
        {
            object? value = modifier.GetType().GetProperty("Target")?.GetValue(modifier);
            if (value is PlayerControl target && target != null)
                targetId = target.PlayerId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHasModifier(PlayerControl player, string modifierName, out bool present)
    {
        present = false;
        if (!TryGetNamedModifier(player, modifierName, out var modifier))
            return false;
        present = modifier != null;
        return true;
    }

    private static bool TryGetNamedModifier(PlayerControl player, string modifierName, out object? modifier)
    {
        modifier = null;
        var modifierType = ResolveType(modifierName);
        if (modifierType == null)
            return true; // This TouMira version simply does not contain that effect.

        var extensionsType = ResolveType(ModifierExtensionsName);
        if (extensionsType == null)
            return false;

        var method = ResolveGetModifierMethod(extensionsType);
        var args = _getModifierArguments;
        if (method == null || args == null || args.Length < 2)
            return false;

        try
        {
            Array.Clear(args, 0, args.Length);
            args[0] = player;
            args[1] = modifierType;
            modifier = method.Invoke(null, args);
            return true;
        }
        catch
        {
            modifier = null;
            return false;
        }
    }

    private static MethodInfo? ResolveGetModifierMethod(Type owner)
    {
        if (_getModifierMethod != null && _getModifierMethodOwner == owner)
            return _getModifierMethod;

        try
        {
            foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "GetModifier" || method.IsGenericMethodDefinition)
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length >= 2
                    && parameters[0].ParameterType == typeof(PlayerControl)
                    && parameters[1].ParameterType == typeof(Type))
                {
                    _getModifierMethod = method;
                    _getModifierMethodOwner = owner;
                    _getModifierArguments = new object?[parameters.Length];
                    return method;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static MethodInfo? ResolveGetModifierComponentMethod(Type owner, Type componentType)
    {
        if (_getModifierComponentMethod != null
            && _getModifierComponentMethodOwner == owner
            && componentType.IsAssignableFrom(_getModifierComponentMethod.ReturnType))
        {
            return _getModifierComponentMethod;
        }

        _getModifierComponentMethod = null;
        _getModifierComponentMethodOwner = owner;
        _getModifierComponentArguments = null;
        try
        {
            foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "GetModifierComponent" || method.IsGenericMethodDefinition)
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 1
                    && parameters[0].ParameterType == typeof(PlayerControl)
                    && componentType.IsAssignableFrom(method.ReturnType))
                {
                    _getModifierComponentMethod = method;
                    _getModifierComponentArguments = new object?[1];
                    return method;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void ResolveActiveModifiersMember(Type owner)
    {
        if (_activeModifiersOwner == owner)
            return;

        _activeModifiersOwner = owner;
        _activeModifiersProperty = null;
        _activeModifiersField = null;
        try
        {
            _activeModifiersProperty = owner.GetProperty(
                "ActiveModifiers",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_activeModifiersProperty == null)
            {
                _activeModifiersField = owner.GetField(
                    "ActiveModifiers",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }
        catch
        {
            _activeModifiersProperty = null;
            _activeModifiersField = null;
        }
    }

    private static Type? ResolveType(string fullName)
        => SoftDependencyTypeResolver.ResolveExact(fullName);

    private static bool TryReadBool(object instance, string propertyName, out bool value)
    {
        value = false;
        try
        {
            object? raw = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
            if (raw is not bool boolValue) return false;
            value = boolValue;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadStaticBool(string typeName, string memberName, out bool value)
    {
        value = false;
        var type = ResolveType(typeName);
        if (type == null) return true;
        try
        {
            object? raw = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                          ?? type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (raw is not bool boolValue) return false;
            value = boolValue;
            return true;
        }
        catch
        {
            return false;
        }
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
