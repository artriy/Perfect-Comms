using PerfectComms.Api;

namespace PerfectComms.Api.PackageSmoke;

public static class ApiConsumer
{
    public static VoiceRuleResult Evaluate(VoiceRuleContext context)
        => context.Phase == VoicePhaseKind.Meeting
            ? VoiceRuleResult.Muffle("Package smoke")
            : VoiceRuleResult.Pass;

    public static void Register()
    {
        const string modId = "com.perfectcomms.package-smoke";

        PerfectCommsApi.RegisterVoiceRule(modId, Evaluate);
        PerfectCommsApi.RegisterManagedRadioChannel(modId, context =>
            context.IsDead
                ? null
                : new VoiceManagedRadioChannelResult("crew", "Crew", "C"));
        PerfectCommsApi.RegisterAnimatedColorRule(modId, colorId => colorId == 99);
    }

    public static bool SupportsCompletedIntegration()
        => PerfectCommsApi.Supports(
            VoiceApiCapability.ManagedTeamRadio |
            VoiceApiCapability.PersistentHostOptions |
            VoiceApiCapability.OverlayAppearance);
}
