using System.Runtime.CompilerServices;
using PerfectComms.Api;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class VoiceModRegistryOverlayPrivacyTests : IDisposable
{
    private readonly List<string> _modIds = new();
    private readonly VoiceOverlayViewerContext _viewerContext = FakeViewerContext();
    private readonly VoiceOverlaySpeakerContext _speakerContext = FakeSpeakerContext();

    [Fact]
    public void OverlayPrivacyShipsAsAdditiveApiVersion11()
    {
        Assert.Equal("1.1", PerfectCommsApi.ApiVersion);
    }

    [Fact]
    public void ViewerRulesComposeToMostRestrictiveVerdict()
    {
        RegisterViewer(_ => VoiceOverlayViewerResult.Pass);
        RegisterViewer(_ => VoiceOverlayViewerResult.DimAll);

        Assert.Equal(
            VoiceOverlayViewerResult.DimAll,
            ResolveViewer());

        RegisterViewer(_ => VoiceOverlayViewerResult.HideAll);

        Assert.Equal(
            VoiceOverlayViewerResult.HideAll,
            ResolveViewer());
    }

    [Fact]
    public void ThrowingOrInvalidViewerRuleFailsToHideAll()
    {
        RegisterViewer(_ => throw new InvalidOperationException("third-party failure"));
        RegisterViewer(_ => new VoiceOverlayViewerResult((VoiceOverlayViewerVerdict)999));

        Assert.Equal(VoiceOverlayViewerResult.HideAll, ResolveViewer());
    }

    [Fact]
    public void MatchingSpeakerAliasesComposeDeterministically()
    {
        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Alias(9));
        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Pass);
        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Alias(9));

        Assert.Equal(
            VoiceOverlaySpeakerResult.Alias(9),
            ResolveSpeaker());
    }

    [Fact]
    public void ConflictingAliasesFailToHideSourceInEitherOrder()
    {
        string first = RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Alias(8));
        string second = RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Alias(9));
        Assert.Equal(VoiceOverlaySpeakerResult.HideSource, ResolveSpeaker());

        PerfectCommsApi.Unregister(first);
        PerfectCommsApi.Unregister(second);
        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Alias(9));
        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Alias(8));

        Assert.Equal(VoiceOverlaySpeakerResult.HideSource, ResolveSpeaker());
    }

    [Fact]
    public void SpeakerRulesComposeHideAllThenHideSourceThenAliasThenPass()
    {
        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Alias(9));
        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.HideSource);
        Assert.Equal(VoiceOverlaySpeakerResult.HideSource, ResolveSpeaker());

        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.HideAll);
        Assert.Equal(VoiceOverlaySpeakerResult.HideAll, ResolveSpeaker());
    }

    [Fact]
    public void ThrowingInvalidOrUnresolvedSpeakerRuleFailsToHideSource()
    {
        RegisterSpeaker(_ => throw new InvalidOperationException("third-party failure"));
        Assert.Equal(VoiceOverlaySpeakerResult.HideSource, ResolveSpeaker());

        RegisterSpeaker(_ => new VoiceOverlaySpeakerResult((VoiceOverlaySpeakerVerdict)999));
        RegisterSpeaker(_ => VoiceOverlaySpeakerResult.Alias(byte.MaxValue));
        Assert.Equal(VoiceOverlaySpeakerResult.HideSource, ResolveSpeaker());
    }

    [Fact]
    public void MissingInputsFailPrivate()
    {
        Assert.Equal(
            VoiceOverlayViewerResult.HideAll,
            VoiceModRegistry.ResolveOverlayViewerPrivacy(
                CreateViewerContext(viewer: null)));
        Assert.Equal(
            VoiceOverlaySpeakerResult.HideAll,
            VoiceModRegistry.ResolveOverlaySpeakerPrivacy(
                CreateSpeakerContext(viewer: null, speaker: FakePlayer())));
        Assert.Equal(
            VoiceOverlaySpeakerResult.HideSource,
            VoiceModRegistry.ResolveOverlaySpeakerPrivacy(
                CreateSpeakerContext(viewer: FakePlayer(), speaker: null)));
    }

    [Fact]
    public void UnregisterRemovesBothOverlayRuleKinds()
    {
        string modId = NewModId();
        int viewerCalls = 0;
        int speakerCalls = 0;
        PerfectCommsApi.RegisterOverlayViewerRule(modId, _ =>
        {
            viewerCalls++;
            return VoiceOverlayViewerResult.HideAll;
        });
        PerfectCommsApi.RegisterOverlaySpeakerRule(modId, _ =>
        {
            speakerCalls++;
            return VoiceOverlaySpeakerResult.HideSource;
        });

        Assert.Equal(VoiceOverlayViewerResult.HideAll, ResolveViewer());
        Assert.Equal(VoiceOverlaySpeakerResult.HideSource, ResolveSpeaker());

        PerfectCommsApi.Unregister(modId);

        Assert.Equal(VoiceOverlayViewerResult.Pass, ResolveViewer());
        Assert.Equal(VoiceOverlaySpeakerResult.Pass, ResolveSpeaker());
        Assert.Equal(1, viewerCalls);
        Assert.Equal(1, speakerCalls);
    }

    [Fact]
    public void CallbackContextReadsOptionsOnlyFromRegisteringMod()
    {
        string first = NewModId();
        string second = NewModId();
        PerfectCommsApi.RegisterHostOption(first, new VoiceHostOption("privacy", "Privacy", true));
        PerfectCommsApi.RegisterHostOption(second, new VoiceHostOption("privacy", "Privacy", false));
        PerfectCommsApi.RegisterOverlayViewerRule(
            first,
            context => context.GetOption("privacy")
                ? VoiceOverlayViewerResult.DimAll
                : VoiceOverlayViewerResult.Pass);
        PerfectCommsApi.RegisterOverlayViewerRule(
            second,
            context => context.GetOption("privacy")
                ? VoiceOverlayViewerResult.HideAll
                : VoiceOverlayViewerResult.Pass);

        Assert.Equal(VoiceOverlayViewerResult.DimAll, ResolveViewer());
    }

    public void Dispose()
    {
        foreach (string modId in _modIds)
            PerfectCommsApi.Unregister(modId);
    }

    private string RegisterViewer(Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult> rule)
    {
        string modId = NewModId();
        PerfectCommsApi.RegisterOverlayViewerRule(modId, rule);
        return modId;
    }

    private string RegisterSpeaker(Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult> rule)
    {
        string modId = NewModId();
        PerfectCommsApi.RegisterOverlaySpeakerRule(modId, rule);
        return modId;
    }

    private string NewModId()
    {
        string modId = "tests.overlay." + Guid.NewGuid().ToString("N");
        _modIds.Add(modId);
        return modId;
    }

    private VoiceOverlayViewerResult ResolveViewer()
        => VoiceModRegistry.ResolveOverlayViewerPrivacy(_viewerContext);

    private VoiceOverlaySpeakerResult ResolveSpeaker()
        => VoiceModRegistry.ResolveOverlaySpeakerPrivacy(_speakerContext);

    private static VoiceOverlayViewerContext FakeViewerContext()
        => CreateViewerContext(FakePlayer());

    private static VoiceOverlaySpeakerContext FakeSpeakerContext()
        => CreateSpeakerContext(FakePlayer(), FakePlayer());

    private static VoiceOverlayViewerContext CreateViewerContext(object? viewer)
        => (VoiceOverlayViewerContext)Activator.CreateInstance(
            typeof(VoiceOverlayViewerContext),
            new[] { viewer, VoicePhaseKind.Tasks, false })!;

    private static VoiceOverlaySpeakerContext CreateSpeakerContext(object? viewer, object? speaker)
        => (VoiceOverlaySpeakerContext)Activator.CreateInstance(
            typeof(VoiceOverlaySpeakerContext),
            new[] { viewer, speaker, VoicePhaseKind.Tasks, false, false })!;

    private static object FakePlayer()
    {
        Type playerType = typeof(VoiceOverlayViewerContext)
            .GetProperty("Viewer")!
            .PropertyType;
        return RuntimeHelpers.GetUninitializedObject(playerType);
    }
}
