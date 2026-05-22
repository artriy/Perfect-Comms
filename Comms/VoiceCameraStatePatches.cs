using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch(typeof(SurveillanceMinigame))]
internal static class SurveillanceCameraStatePatches
{
    [HarmonyPostfix, HarmonyPatch(nameof(SurveillanceMinigame.Begin))]
    private static void Begin_Postfix(SurveillanceMinigame __instance)
        => VoiceCameraState.Open(__instance);

    [HarmonyPrefix, HarmonyPatch(nameof(SurveillanceMinigame.Close))]
    private static void Close_Prefix(SurveillanceMinigame __instance)
        => VoiceCameraState.Close(__instance);

    [HarmonyPrefix, HarmonyPatch(nameof(SurveillanceMinigame.OnDestroy))]
    private static void OnDestroy_Prefix(SurveillanceMinigame __instance)
        => VoiceCameraState.Close(__instance);
}

[HarmonyPatch(typeof(PlanetSurveillanceMinigame))]
internal static class PlanetCameraStatePatches
{
    [HarmonyPostfix, HarmonyPatch(nameof(PlanetSurveillanceMinigame.Begin))]
    private static void Begin_Postfix(PlanetSurveillanceMinigame __instance)
        => VoiceCameraState.Open(__instance);

    [HarmonyPrefix, HarmonyPatch(nameof(PlanetSurveillanceMinigame.Close))]
    private static void Close_Prefix(PlanetSurveillanceMinigame __instance)
        => VoiceCameraState.Close(__instance);

    [HarmonyPrefix, HarmonyPatch(nameof(PlanetSurveillanceMinigame.OnDestroy))]
    private static void OnDestroy_Prefix(PlanetSurveillanceMinigame __instance)
        => VoiceCameraState.Close(__instance);
}

[HarmonyPatch(typeof(FungleSurveillanceMinigame))]
internal static class FungleCameraStatePatches
{
    [HarmonyPostfix, HarmonyPatch(nameof(FungleSurveillanceMinigame.Begin))]
    private static void Begin_Postfix(FungleSurveillanceMinigame __instance)
        => VoiceCameraState.Open(__instance);

    [HarmonyPrefix, HarmonyPatch(nameof(FungleSurveillanceMinigame.Close))]
    private static void Close_Prefix(FungleSurveillanceMinigame __instance)
        => VoiceCameraState.Close(__instance);
}
