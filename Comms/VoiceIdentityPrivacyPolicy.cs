using System.Collections.Generic;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Identity-bearing overlays have two materially different presentation domains. During tasks,
/// live disguises and concealments may hide or alias a transport speaker. Meetings, exile, and
/// results screens already publish stable player identities, so carrying task-world presentation
/// state into those phases creates a negative-evidence role tell instead of protecting identity.
/// </summary>
internal enum VoiceIdentityPrivacyDomain
{
    Neutral,
    TaskWorld,
    PublicIdentity,
}

/// <summary>Pure phase policy shared by the runtime and regression tests.</summary>
internal static class VoiceIdentityPrivacyPhasePolicy
{
    internal static VoiceIdentityPrivacyDomain DomainFor(VoiceGamePhase phase)
        => phase switch
        {
            VoiceGamePhase.Tasks => VoiceIdentityPrivacyDomain.TaskWorld,
            VoiceGamePhase.Meeting or VoiceGamePhase.Exile or VoiceGamePhase.EndGame
                => VoiceIdentityPrivacyDomain.PublicIdentity,
            _ => VoiceIdentityPrivacyDomain.Neutral,
        };

    /// <summary>
    /// Built-in reflection reads describe the live task-world appearance. Public-identity phases
    /// present the real transport source instead; explicit mod API rules still run separately.
    /// </summary>
    internal static bool UsesBuiltInAppearancePrivacy(VoiceGamePhase phase)
        => DomainFor(phase) == VoiceIdentityPrivacyDomain.TaskWorld;

    /// <summary>
    /// Every known phase change is a trusted identity epoch and therefore an implicit quiet edge.
    /// Built-in presentation happens to share a public domain across meeting/exile/results, but
    /// explicit mod API rules receive the exact phase and may legitimately change at those edges.
    /// Unknown is ignored so a transient observation cannot discard a valid transition gate.
    /// </summary>
    internal static bool ShouldResetTransitionState(
        VoiceGamePhase previousPhase,
        VoiceGamePhase currentPhase)
        => previousPhase != VoiceGamePhase.Unknown
           && currentPhase != VoiceGamePhase.Unknown
           && previousPhase != currentPhase;

    /// <summary>
    /// A public phase may use the stable authenticated roster or an already-public UI slot when the
    /// live PlayerControl collection is briefly rebuilding. Task-world identity never gets this
    /// fallback because its current concealment state must be inspected before attribution.
    /// </summary>
    internal static bool CanPresentStablePublicIdentity(
        VoiceGamePhase phase,
        bool authenticatedRosterContainsSource,
        bool publicSurfaceContainsSource)
        => DomainFor(phase) == VoiceIdentityPrivacyDomain.PublicIdentity
           && (authenticatedRosterContainsSource || publicSurfaceContainsSource);
}

/// <summary>Pure cache-key comparison so same-frame phase/game transitions are regression-tested.</summary>
internal static class VoiceIdentityPrivacyFrameCachePolicy
{
    internal static bool CanReuse(
        int cachedFrame,
        VoiceGamePhase cachedPhase,
        int cachedGameId,
        int currentFrame,
        VoiceGamePhase currentPhase,
        int currentGameId)
        => cachedFrame == currentFrame
           && cachedPhase == currentPhase
           && cachedGameId == currentGameId;
}

/// <summary>
/// Tracks the last authoritative phase while ignoring transient Unknown observations. Runtime uses
/// this epoch to decide when every per-source transition gate must be rebased.
/// </summary>
internal sealed class VoiceIdentityPrivacyPhaseEpoch
{
    internal VoiceGamePhase LastKnownPhase { get; private set; } = VoiceGamePhase.Unknown;

    internal bool Advance(VoiceGamePhase currentPhase)
    {
        bool shouldReset = VoiceIdentityPrivacyPhasePolicy.ShouldResetTransitionState(
            LastKnownPhase,
            currentPhase);
        if (currentPhase != VoiceGamePhase.Unknown)
            LastKnownPhase = currentPhase;
        return shouldReset;
    }

    internal void Reset()
        => LastKnownPhase = VoiceGamePhase.Unknown;
}

/// <summary>
/// Pure allow-list for concealment modifiers that are safe to present as a known alias. Runtime
/// reflection must verify every active ConcealedModifier against this policy; seeing one known
/// alias is not enough when multiple concealments are stacked.
/// </summary>
internal static class VoiceConcealedModifierSetPolicy
{
    internal const string MorphlingMorphName =
        "TownOfUs.Modifiers.Impostor.MorphlingMorphModifier";
    internal const string GlitchMimicName =
        "TownOfUs.Modifiers.Neutral.GlitchMimicModifier";
    internal const string ShapeshifterShiftName =
        "TownOfUs.Modifiers.Impostor.ShapeshifterShiftModifier";

    internal static bool IsRecognizedActiveAlias(
        string? modifierTypeName,
        bool morphActive,
        bool mimicActive,
        bool shiftActive)
        => (morphActive && modifierTypeName == MorphlingMorphName)
           || (mimicActive && modifierTypeName == GlitchMimicName)
           || (shiftActive && modifierTypeName == ShapeshifterShiftName);

    internal static bool AreAllRecognizedActiveAliases(
        IReadOnlyList<string?> modifierTypeNames,
        bool morphActive,
        bool mimicActive,
        bool shiftActive)
    {
        for (int i = 0; i < modifierTypeNames.Count; i++)
        {
            if (!IsRecognizedActiveAlias(
                    modifierTypeNames[i],
                    morphActive,
                    mimicActive,
                    shiftActive))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Describes how a transport speaker may be represented by identity-bearing UI.
/// The numeric values are not a precedence order; <see cref="VoiceIdentityPrivacyPolicy"/>
/// applies the documented privacy ordering explicitly.
/// </summary>
internal enum VoiceIdentityPrivacyDecision
{
    Normal = 0,
    Alias = 1,
    HideSource = 2,
    HideAllForViewer = 3,
    DimAll = 4,
}

/// <summary>
/// Pure evidence supplied by game/mod adapters. A default value is deliberately unknown and
/// therefore fails closed; callers must opt into <see cref="KnownNormal"/> when both scopes were
/// inspected successfully.
/// </summary>
internal readonly record struct VoiceIdentityPrivacyEvidence(
    bool ViewerStateKnown,
    bool SourceStateKnown,
    bool HideAllForViewer = false,
    bool DimAll = false,
    bool HideSource = false,
    bool AliasActive = false,
    byte? AliasPlayerId = null)
{
    internal static VoiceIdentityPrivacyEvidence KnownNormal { get; } = new(
        ViewerStateKnown: true,
        SourceStateKnown: true);
}

/// <summary>
/// A normalized privacy result. Only Normal and Alias carry a concrete presentation player.
/// Hide/Dim decisions intentionally do not expose a player slot to downstream UI.
/// </summary>
internal readonly record struct VoiceIdentityPrivacyResolution(
    byte SourcePlayerId,
    VoiceIdentityPrivacyDecision Decision,
    byte? PresentationPlayerId)
{
    internal bool HasConcretePresentation =>
        Decision is VoiceIdentityPrivacyDecision.Normal or VoiceIdentityPrivacyDecision.Alias
        && PresentationPlayerId is not null
        && PresentationPlayerId != byte.MaxValue;
}

/// <summary>
/// Resolves independently collected viewer-wide and source-specific evidence without touching
/// Unity or game objects.
/// </summary>
internal static class VoiceIdentityPrivacyPolicy
{
    /// <summary>
    /// Unknown evidence is fail-private for the current frame, but it is not a trustworthy baseline
    /// for a continuous-speech transition gate. This commonly occurs while scene-owned controls are
    /// rebuilt between Tasks and Meeting.
    /// </summary>
    internal static bool IsProvisional(VoiceIdentityPrivacyEvidence evidence)
        => !evidence.ViewerStateKnown || !evidence.SourceStateKnown;

    /// <summary>
    /// Privacy precedence is: hard viewer hide, source hide/unresolved alias, viewer dim,
    /// resolved alias, normal. Unknown viewer state hides all; unknown source state hides that
    /// source. An alias-to-self is normalized to Normal.
    /// </summary>
    internal static VoiceIdentityPrivacyResolution Resolve(
        byte sourcePlayerId,
        VoiceIdentityPrivacyEvidence evidence)
    {
        if (!evidence.ViewerStateKnown || evidence.HideAllForViewer)
        {
            return Hidden(sourcePlayerId, VoiceIdentityPrivacyDecision.HideAllForViewer);
        }

        var unresolvedAlias = evidence.AliasActive
                              && (evidence.AliasPlayerId is null
                                  || evidence.AliasPlayerId == byte.MaxValue);
        if (!evidence.SourceStateKnown
            || sourcePlayerId == byte.MaxValue
            || evidence.HideSource
            || unresolvedAlias)
        {
            return Hidden(sourcePlayerId, VoiceIdentityPrivacyDecision.HideSource);
        }

        if (evidence.DimAll)
        {
            return Hidden(sourcePlayerId, VoiceIdentityPrivacyDecision.DimAll);
        }

        if (evidence.AliasActive && evidence.AliasPlayerId is { } aliasPlayerId)
        {
            if (aliasPlayerId == sourcePlayerId)
            {
                return Normal(sourcePlayerId);
            }

            return new VoiceIdentityPrivacyResolution(
                sourcePlayerId,
                VoiceIdentityPrivacyDecision.Alias,
                aliasPlayerId);
        }

        return Normal(sourcePlayerId);
    }

    internal static VoiceIdentityPrivacyResolution Quarantine(
        VoiceIdentityPrivacyResolution accepted,
        VoiceIdentityPrivacyResolution candidate)
    {
        var decision = accepted.Decision == VoiceIdentityPrivacyDecision.HideAllForViewer
                       || candidate.Decision == VoiceIdentityPrivacyDecision.HideAllForViewer
            ? VoiceIdentityPrivacyDecision.HideAllForViewer
            : VoiceIdentityPrivacyDecision.HideSource;
        return Hidden(candidate.SourcePlayerId, decision);
    }

    private static VoiceIdentityPrivacyResolution Normal(byte sourcePlayerId)
        => new(sourcePlayerId, VoiceIdentityPrivacyDecision.Normal, sourcePlayerId);

    private static VoiceIdentityPrivacyResolution Hidden(
        byte sourcePlayerId,
        VoiceIdentityPrivacyDecision decision)
        => new(sourcePlayerId, decision, null);
}

/// <summary>
/// Result of applying a candidate policy to a speaker's quiet-edge transition gate.
/// </summary>
internal readonly record struct VoiceIdentityPrivacyTransition(
    VoiceIdentityPrivacyResolution EffectiveResolution,
    bool IsQuarantined);

/// <summary>
/// Prevents an already-speaking source from changing identity-bearing presentation mid-utterance.
/// Once a candidate changes while speaking, presentation remains quarantined until a quiet frame
/// accepts the latest candidate. Use one gate per transport source.
/// </summary>
internal sealed class VoiceIdentityPrivacyTransitionGate
{
    private bool _hasAcceptedResolution;
    private bool _isQuarantined;
    private bool _quarantineHideAll;
    private VoiceIdentityPrivacyResolution _acceptedResolution;

    internal bool HasAcceptedResolution => _hasAcceptedResolution;
    internal bool IsQuarantined => _isQuarantined;
    internal VoiceIdentityPrivacyResolution AcceptedResolution => _acceptedResolution;

    /// <summary>
    /// Presents provisional fail-private evidence without accepting it as transition history. Once
    /// the scene lookup recovers, the known candidate may be shown immediately even if speech never
    /// went quiet.
    /// </summary>
    internal VoiceIdentityPrivacyTransition Observe(
        VoiceIdentityPrivacyResolution candidate,
        bool isSpeaking,
        bool isProvisional)
    {
        if (!isProvisional)
            return Advance(candidate, isSpeaking);

        if (!_hasAcceptedResolution)
            return new VoiceIdentityPrivacyTransition(candidate, false);

        // Provisional evidence may hide more for this frame, never less. In particular, preserve a
        // viewer-wide HideAll that was already accepted or made sticky by an authoritative change.
        var effective = VoiceIdentityPrivacyPolicy.Quarantine(_acceptedResolution, candidate);
        if (_quarantineHideAll
            && effective.Decision != VoiceIdentityPrivacyDecision.HideAllForViewer)
        {
            effective = new VoiceIdentityPrivacyResolution(
                candidate.SourcePlayerId,
                VoiceIdentityPrivacyDecision.HideAllForViewer,
                null);
        }
        return new VoiceIdentityPrivacyTransition(effective, _isQuarantined);
    }

    internal VoiceIdentityPrivacyTransition Advance(
        VoiceIdentityPrivacyResolution candidate,
        bool isSpeaking)
    {
        if (!_hasAcceptedResolution)
        {
            _hasAcceptedResolution = true;
            _acceptedResolution = candidate;
            return new VoiceIdentityPrivacyTransition(candidate, false);
        }

        if (!isSpeaking)
        {
            _acceptedResolution = candidate;
            _isQuarantined = false;
            _quarantineHideAll = false;
            return new VoiceIdentityPrivacyTransition(candidate, false);
        }

        if (_isQuarantined || candidate != _acceptedResolution)
        {
            _isQuarantined = true;
            var quarantine = VoiceIdentityPrivacyPolicy.Quarantine(_acceptedResolution, candidate);
            _quarantineHideAll |=
                quarantine.Decision == VoiceIdentityPrivacyDecision.HideAllForViewer;
            if (_quarantineHideAll
                && quarantine.Decision != VoiceIdentityPrivacyDecision.HideAllForViewer)
            {
                quarantine = new VoiceIdentityPrivacyResolution(
                    candidate.SourcePlayerId,
                    VoiceIdentityPrivacyDecision.HideAllForViewer,
                    null);
            }

            return new VoiceIdentityPrivacyTransition(quarantine, true);
        }

        return new VoiceIdentityPrivacyTransition(_acceptedResolution, false);
    }

    internal void Reset()
    {
        _hasAcceptedResolution = false;
        _isQuarantined = false;
        _quarantineHideAll = false;
        _acceptedResolution = default;
    }
}

/// <summary>
/// Detects when an alias and another independently routed source resolve to the same visible slot.
/// Callers should coalesce such activity instead of creating a second identity-bearing indicator.
/// </summary>
internal static class VoiceIdentityAliasCollision
{
    internal static bool TryGetPresentationPlayerId(
        VoiceIdentityPrivacyResolution resolution,
        out byte presentationPlayerId)
    {
        if (resolution.HasConcretePresentation
            && resolution.PresentationPlayerId is { } resolvedPlayerId)
        {
            presentationPlayerId = resolvedPlayerId;
            return true;
        }

        presentationPlayerId = byte.MaxValue;
        return false;
    }

    internal static bool HasCollision(
        VoiceIdentityPrivacyResolution first,
        VoiceIdentityPrivacyResolution second)
    {
        if (first.SourcePlayerId == second.SourcePlayerId
            || (first.Decision != VoiceIdentityPrivacyDecision.Alias
                && second.Decision != VoiceIdentityPrivacyDecision.Alias))
        {
            return false;
        }

        return TryGetPresentationPlayerId(first, out var firstPresentationPlayerId)
               && TryGetPresentationPlayerId(second, out var secondPresentationPlayerId)
               && firstPresentationPlayerId == secondPresentationPlayerId;
    }
}
