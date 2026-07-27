using PerfectComms.Api;

namespace PerfectComms.Api.PackageSmoke;

public static class ApiConsumer
{
    public static VoiceRuleResult Evaluate(VoiceRuleContext context)
        => context.Phase == VoicePhaseKind.Meeting
            ? VoiceRuleResult.Muffle("Package smoke")
            : VoiceRuleResult.Pass;

    public static void Register()
        => PerfectCommsApi.RegisterVoiceRule("com.perfectcomms.package-smoke", Evaluate);
}
