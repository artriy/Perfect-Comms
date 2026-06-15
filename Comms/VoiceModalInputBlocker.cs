using HarmonyLib;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceModalInputBlocker
{
    private static bool Blocked =>
        VoiceUiKit.BlockGameInput
        || VoiceOptionsMenuEntry.CursorOverChip
        || VoiceHostMenuEntry.CursorOverChip;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickDown))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockClickDown() => !Blocked;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickUp))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockClickUp() => !Blocked;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveRepeatDown))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockRepeatDown() => !Blocked;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveClickDownGraphic))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockClickDownGraphic() => !Blocked;

    [HarmonyPatch(typeof(PassiveButton), nameof(PassiveButton.ReceiveMouseOver))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockMouseOver() => !Blocked;

    [HarmonyPatch(typeof(SlideBar), nameof(SlideBar.ReceiveClickDrag))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockSlideDrag() => !Blocked;

    [HarmonyPatch(typeof(SlideBar), nameof(SlideBar.ControllerIncrease))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockSlideIncrease() => !Blocked;

    [HarmonyPatch(typeof(SlideBar), nameof(SlideBar.ControllerDecrease))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockSlideDecrease() => !Blocked;

    [HarmonyPatch(typeof(PassiveButtonManager), nameof(PassiveButtonManager.Update))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockPbmUpdate() => !Blocked;

    [HarmonyPatch(typeof(ControllerManager), nameof(ControllerManager.Update))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockControllerUpdate() => !Blocked;

    [HarmonyPatch(typeof(UnityEngine.EventSystems.EventSystem), "Update")]
    [HarmonyPrefix]
    static bool PerfectComms_BlockEventSystem() => !Blocked;
}
