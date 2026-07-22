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
        => UpdatePanelHotkeys(allowWithoutHudManager: false);

    internal static void UpdatePanelHotkeysFromFrameDriver()
        => UpdatePanelHotkeys(allowWithoutHudManager: true);

    private static void UpdatePanelHotkeys(bool allowWithoutHudManager)
    {
        VoiceUiKit.Tick();

        if (!Application.isFocused || VoiceUiKit.RebindRow.ShouldSuppressKeybinds) return;

        bool chatOpen = false;
        if (HudManager.InstanceExists)
        {
            var chat = HudManager.Instance.Chat;
            chatOpen = chat != null && chat.IsOpenOrOpening;
        }
        else if (!allowWithoutHudManager)
        {
            return;
        }

        if (VoiceChatPatches.ShouldBlockKeybindsForChat(chatOpen)) return;

        if (VoiceChatKeybinds.OpenVoiceMenu.WasPressedThisFrame() &&
            _lastClientFrame != Time.frameCount)
        {
            _lastClientFrame = Time.frameCount;
            VoiceSettingsPanel.Toggle();
        }

        if (VoiceChatKeybinds.OpenHostVoiceSettings.WasPressedThisFrame() &&
            _lastHostFrame != Time.frameCount)
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
