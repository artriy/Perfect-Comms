using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Arms automatic setup from the main-menu lifecycle and waits until the frame after Start
/// before asking the setup UI to show. The setup implementation owns its persisted/process
/// guards; this adapter only guarantees one readiness callback per MainMenuManager instance.
/// </summary>
internal static class VoiceFirstRunSetupMainMenuLifecycle
{
    private static int _menuInstanceId = int.MinValue;
    private static int _startFrame = int.MaxValue;
    private static bool _pending;

    internal static void OnMainMenuStarted(MainMenuManager menu)
    {
        _menuInstanceId = menu.GetInstanceID();
        _startFrame = Time.frameCount;
        _pending = true;
    }

    internal static void LateUpdate(MainMenuManager menu)
    {
        if (!_pending || menu.GetInstanceID() != _menuInstanceId)
            return;

        // Start is too early for a modal overlay: let the vanilla menu and other Start postfixes
        // finish constructing their objects before the setup creates its persistent canvas.
        if (Time.frameCount <= _startFrame)
            return;

        if (VoiceFirstRunSetup.TryShowAutomatic() ||
            !VoiceFirstRunSetup.ShouldDeferMainMenuPopups)
        {
            _pending = false;
            return;
        }

        // A local panel or a transient UI construction failure can defer setup. Keep this menu
        // armed, but back off so a broken scene object cannot produce a log/error every frame.
        _startFrame = Time.frameCount + 30;
    }
}

[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
internal static class VoiceFirstRunSetupMainMenuStartPatch
{
    private static void Postfix(MainMenuManager __instance)
        => VoiceFirstRunSetupMainMenuLifecycle.OnMainMenuStarted(__instance);
}

[HarmonyPatch(typeof(MainMenuManager), "LateUpdate")]
internal static class VoiceFirstRunSetupMainMenuLateUpdatePatch
{
    private static void Postfix(MainMenuManager __instance)
        => VoiceFirstRunSetupMainMenuLifecycle.LateUpdate(__instance);
}
