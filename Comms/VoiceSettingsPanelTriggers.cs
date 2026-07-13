using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceSettingsPanelTriggers
{
    private static int _lastClientFrame = -1;
    private static int _lastHostFrame = -1;

    [HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    [HarmonyPostfix]
    static void PerfectComms_PanelHotkeys()
    {
        VoiceUiKit.Tick();

        if (VoiceUiKit.RebindRow.ShouldSuppressKeybinds) return;

        if (!HudManager.InstanceExists) return;

        var chat = HudManager.Instance.Chat;
        if (chat != null && chat.IsOpenOrOpening) return;

        if (VoiceChatKeybinds.OpenVoiceMenu.WasPressedThisFrame() && _lastClientFrame != Time.frameCount)
        {
            _lastClientFrame = Time.frameCount;
            VoiceSettingsPanel.Toggle();
        }

        if (VoiceChatKeybinds.OpenHostVoiceSettings.WasPressedThisFrame() && _lastHostFrame != Time.frameCount)
        {
            _lastHostFrame = Time.frameCount;
            HostSettingsPanel.Toggle();
        }
    }

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Update))]
    [HarmonyPostfix]
    static void PerfectComms_OptionsTick(OptionsMenuBehaviour __instance)
    {
        VoiceOptionsMenuEntry.NotifyOptionsActive(__instance);
        VoiceUiKit.Tick();
    }

    [HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
    [HarmonyPostfix]
    static void PerfectComms_HudTick()
    {
        VoiceUiKit.Tick();
    }
}
