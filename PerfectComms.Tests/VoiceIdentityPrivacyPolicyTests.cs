using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceIdentityPrivacyPolicyTests
{
    [Fact]
    public void DefaultEvidenceFailsClosedForUnknownViewer()
    {
        var result = VoiceIdentityPrivacyPolicy.Resolve(3, default);

        Assert.Equal(VoiceIdentityPrivacyDecision.HideAllForViewer, result.Decision);
        Assert.False(result.HasConcretePresentation);
        Assert.Null(result.PresentationPlayerId);
    }

    [Fact]
    public void KnownNormalUsesTransportSourceAsPresentationIdentity()
    {
        var result = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            VoiceIdentityPrivacyEvidence.KnownNormal);

        Assert.Equal(VoiceIdentityPrivacyDecision.Normal, result.Decision);
        Assert.Equal((byte)3, result.PresentationPlayerId);
        Assert.True(result.HasConcretePresentation);
    }

    [Fact]
    public void HardViewerHideHasHighestPrecedence()
    {
        var evidence = new VoiceIdentityPrivacyEvidence(
            ViewerStateKnown: true,
            SourceStateKnown: false,
            HideAllForViewer: true,
            DimAll: true,
            HideSource: true,
            AliasActive: true,
            AliasPlayerId: 9);

        var result = VoiceIdentityPrivacyPolicy.Resolve(3, evidence);

        Assert.Equal(VoiceIdentityPrivacyDecision.HideAllForViewer, result.Decision);
        Assert.False(result.HasConcretePresentation);
    }

    [Fact]
    public void UnknownSourceHidesOnlySourceWhenViewerIsKnown()
    {
        var result = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: false));

        Assert.Equal(VoiceIdentityPrivacyDecision.HideSource, result.Decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(255)]
    public void ActiveUnresolvedAliasFailsClosed(int? aliasPlayerId)
    {
        var result = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                AliasActive: true,
                AliasPlayerId: aliasPlayerId is null ? null : (byte)aliasPlayerId.Value));

        Assert.Equal(VoiceIdentityPrivacyDecision.HideSource, result.Decision);
        Assert.False(result.HasConcretePresentation);
    }

    [Fact]
    public void SourceHidePrecedesDimAndAlias()
    {
        var result = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                DimAll: true,
                HideSource: true,
                AliasActive: true,
                AliasPlayerId: 9));

        Assert.Equal(VoiceIdentityPrivacyDecision.HideSource, result.Decision);
    }

    [Fact]
    public void DimAllPrecedesResolvedAlias()
    {
        var result = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                DimAll: true,
                AliasActive: true,
                AliasPlayerId: 9));

        Assert.Equal(VoiceIdentityPrivacyDecision.DimAll, result.Decision);
        Assert.False(result.HasConcretePresentation);
    }

    [Fact]
    public void ResolvedAliasUsesTargetPresentationIdentity()
    {
        var result = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                AliasActive: true,
                AliasPlayerId: 9));

        Assert.Equal(VoiceIdentityPrivacyDecision.Alias, result.Decision);
        Assert.Equal((byte)3, result.SourcePlayerId);
        Assert.Equal((byte)9, result.PresentationPlayerId);
        Assert.True(result.HasConcretePresentation);
    }

    [Fact]
    public void AliasToSelfNormalizesToNormal()
    {
        var result = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                AliasActive: true,
                AliasPlayerId: 3));

        Assert.Equal(VoiceIdentityPrivacyDecision.Normal, result.Decision);
        Assert.Equal((byte)3, result.PresentationPlayerId);
    }

    [Fact]
    public void InitialSpeakingObservationDoesNotInventATransition()
    {
        var gate = new VoiceIdentityPrivacyTransitionGate();
        var normal = ResolveNormal(3);

        var result = gate.Advance(normal, isSpeaking: true);

        Assert.Equal(normal, result.EffectiveResolution);
        Assert.False(result.IsQuarantined);
        Assert.True(gate.HasAcceptedResolution);
    }

    [Fact]
    public void MidSpeechAliasTransitionIsHiddenUntilQuietEdge()
    {
        var gate = new VoiceIdentityPrivacyTransitionGate();
        var normal = ResolveNormal(3);
        var alias = ResolveAlias(3, 9);

        gate.Advance(normal, isSpeaking: true);
        var changed = gate.Advance(alias, isSpeaking: true);
        var stillSpeaking = gate.Advance(alias, isSpeaking: true);

        Assert.Equal(VoiceIdentityPrivacyDecision.HideSource, changed.EffectiveResolution.Decision);
        Assert.True(changed.IsQuarantined);
        Assert.Equal(VoiceIdentityPrivacyDecision.HideSource, stillSpeaking.EffectiveResolution.Decision);

        var quiet = gate.Advance(alias, isSpeaking: false);
        var nextUtterance = gate.Advance(alias, isSpeaking: true);

        Assert.False(quiet.IsQuarantined);
        Assert.Equal(alias, quiet.EffectiveResolution);
        Assert.Equal(alias, nextUtterance.EffectiveResolution);
        Assert.False(gate.IsQuarantined);
    }

    [Fact]
    public void RevertingCandidateWhileSpeakingDoesNotEscapeQuarantine()
    {
        var gate = new VoiceIdentityPrivacyTransitionGate();
        var normal = ResolveNormal(3);
        var alias = ResolveAlias(3, 9);

        gate.Advance(normal, isSpeaking: true);
        gate.Advance(alias, isSpeaking: true);
        var reverted = gate.Advance(normal, isSpeaking: true);

        Assert.True(reverted.IsQuarantined);
        Assert.Equal(VoiceIdentityPrivacyDecision.HideSource, reverted.EffectiveResolution.Decision);
    }

    [Fact]
    public void HardViewerHideIsPreservedDuringQuarantine()
    {
        var gate = new VoiceIdentityPrivacyTransitionGate();
        var normal = ResolveNormal(3);
        var hideAll = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                HideAllForViewer: true));

        gate.Advance(normal, isSpeaking: true);
        var hidden = gate.Advance(hideAll, isSpeaking: true);

        Assert.True(hidden.IsQuarantined);
        Assert.Equal(
            VoiceIdentityPrivacyDecision.HideAllForViewer,
            hidden.EffectiveResolution.Decision);

        var revertedWhileSpeaking = gate.Advance(normal, isSpeaking: true);
        Assert.True(revertedWhileSpeaking.IsQuarantined);
        Assert.Equal(
            VoiceIdentityPrivacyDecision.HideAllForViewer,
            revertedWhileSpeaking.EffectiveResolution.Decision);
    }

    [Fact]
    public void QuietPolicyChangeIsAcceptedImmediately()
    {
        var gate = new VoiceIdentityPrivacyTransitionGate();
        var normal = ResolveNormal(3);
        var alias = ResolveAlias(3, 9);

        gate.Advance(normal, isSpeaking: false);
        var changed = gate.Advance(alias, isSpeaking: false);

        Assert.False(changed.IsQuarantined);
        Assert.Equal(alias, changed.EffectiveResolution);
        Assert.Equal(alias, gate.AcceptedResolution);
    }

    [Fact]
    public void ResetDropsAcceptedPolicyAndQuarantine()
    {
        var gate = new VoiceIdentityPrivacyTransitionGate();
        gate.Advance(ResolveNormal(3), isSpeaking: true);
        gate.Advance(ResolveAlias(3, 9), isSpeaking: true);

        gate.Reset();

        Assert.False(gate.HasAcceptedResolution);
        Assert.False(gate.IsQuarantined);
        var alias = ResolveAlias(3, 9);
        Assert.Equal(alias, gate.Advance(alias, isSpeaking: true).EffectiveResolution);
    }

    [Fact]
    public void AliasAndTargetNormalPresentationCollide()
    {
        var alias = ResolveAlias(3, 9);
        var target = ResolveNormal(9);

        Assert.True(VoiceIdentityAliasCollision.HasCollision(alias, target));
        Assert.True(VoiceIdentityAliasCollision.HasCollision(target, alias));
    }

    [Fact]
    public void TwoAliasesToSameTargetCollide()
    {
        Assert.True(VoiceIdentityAliasCollision.HasCollision(
            ResolveAlias(3, 9),
            ResolveAlias(4, 9)));
    }

    [Fact]
    public void HiddenOrDifferentPresentationsDoNotCollide()
    {
        var hidden = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                HideSource: true));

        Assert.False(VoiceIdentityAliasCollision.HasCollision(hidden, ResolveNormal(9)));
        Assert.False(VoiceIdentityAliasCollision.HasCollision(
            ResolveAlias(3, 8),
            ResolveNormal(9)));
        Assert.False(VoiceIdentityAliasCollision.HasCollision(
            ResolveNormal(3),
            ResolveNormal(9)));
    }

    [Fact]
    public void ConcretePresentationHelperRejectsPrivacyOnlyDecisions()
    {
        var normal = ResolveNormal(3);
        var hidden = VoiceIdentityPrivacyPolicy.Resolve(
            3,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                HideSource: true));

        Assert.True(VoiceIdentityAliasCollision.TryGetPresentationPlayerId(normal, out var playerId));
        Assert.Equal((byte)3, playerId);
        Assert.False(VoiceIdentityAliasCollision.TryGetPresentationPlayerId(hidden, out playerId));
        Assert.Equal(byte.MaxValue, playerId);
    }

    [Fact]
    public void ConcealedModifierSetAllowsOnlyEveryActiveRecognizedAlias()
    {
        string[] recognized =
        [
            VoiceConcealedModifierSetPolicy.MorphlingMorphName,
            VoiceConcealedModifierSetPolicy.GlitchMimicName,
        ];

        Assert.True(VoiceConcealedModifierSetPolicy.AreAllRecognizedActiveAliases(
            recognized,
            morphActive: true,
            mimicActive: true,
            shiftActive: false));
    }

    [Fact]
    public void StackedUnknownConcealmentFailsPrivateEvenBesideRecognizedAlias()
    {
        string[] stacked =
        [
            VoiceConcealedModifierSetPolicy.MorphlingMorphName,
            "FutureMod.Modifiers.UnknownConcealment",
        ];

        Assert.False(VoiceConcealedModifierSetPolicy.AreAllRecognizedActiveAliases(
            stacked,
            morphActive: true,
            mimicActive: false,
            shiftActive: false));
    }

    [Fact]
    public void InactiveAliasModifierIsNotAcceptedByNameAlone()
    {
        Assert.False(VoiceConcealedModifierSetPolicy.IsRecognizedActiveAlias(
            VoiceConcealedModifierSetPolicy.MorphlingMorphName,
            morphActive: false,
            mimicActive: false,
            shiftActive: false));
    }

    [Fact]
    public void PrivacyFrameUpdatesInPlaceForHotPathReuse()
    {
        var speakers = new List<VoiceIdentityPresentedSpeaker>
        {
            new(3, 9, 0.5f),
        };
        var frame = new VoiceIdentityPrivacyFrame([], false, false);

        frame.Update(speakers, hideAllForViewer: true, dimAll: false);

        Assert.Same(speakers, frame.Speakers);
        Assert.True(frame.HideAllForViewer);
        Assert.False(frame.DimAll);
    }

    private static VoiceIdentityPrivacyResolution ResolveNormal(byte sourcePlayerId)
        => VoiceIdentityPrivacyPolicy.Resolve(
            sourcePlayerId,
            VoiceIdentityPrivacyEvidence.KnownNormal);

    private static VoiceIdentityPrivacyResolution ResolveAlias(byte sourcePlayerId, byte aliasPlayerId)
        => VoiceIdentityPrivacyPolicy.Resolve(
            sourcePlayerId,
            new VoiceIdentityPrivacyEvidence(
                ViewerStateKnown: true,
                SourceStateKnown: true,
                AliasActive: true,
                AliasPlayerId: aliasPlayerId));
}
