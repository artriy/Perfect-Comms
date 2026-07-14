using System.Collections.Generic;

namespace VoiceChatPlugin.VoiceChat;

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
