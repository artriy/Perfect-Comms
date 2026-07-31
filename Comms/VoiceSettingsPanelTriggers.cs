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

        bool allowAllWhileChatOpen =
            VoiceSettings.Instance?.AllowKeybindsWhileChatOpen.Value == true;

        var clientBinding = VoiceChatKeybinds.OpenVoiceMenu;
        if (ShouldBlockPanelBinding(
                clientBinding, VoiceSettingsPanel.IsOpen,
                chatOpen, allowAllWhileChatOpen))
        {
            clientBinding.SuppressUntilReleased();
        }
        else if (clientBinding.WasPressedThisFrame() &&
                 _lastClientFrame != Time.frameCount)
        {
            _lastClientFrame = Time.frameCount;
            bool modalWasOpen = VoiceUiKit.AnyPanelOpen;
            VoiceSettingsPanel.Toggle();
            if (modalWasOpen || VoiceUiKit.AnyPanelOpen)
                SuppressOtherBindingsAcrossPanelBoundary(clientBinding);
        }

        var hostBinding = VoiceChatKeybinds.OpenHostVoiceSettings;
        if (ShouldBlockPanelBinding(
                hostBinding, HostSettingsPanel.IsOpen,
                chatOpen, allowAllWhileChatOpen))
        {
            hostBinding.SuppressUntilReleased();
        }
        else if (hostBinding.WasPressedThisFrame() &&
                 _lastHostFrame != Time.frameCount)
        {
            _lastHostFrame = Time.frameCount;
            bool modalWasOpen = VoiceUiKit.AnyPanelOpen;
            HostSettingsPanel.Toggle();
            if (modalWasOpen || VoiceUiKit.AnyPanelOpen)
                SuppressOtherBindingsAcrossPanelBoundary(hostBinding);
        }
    }

    private static bool ShouldBlockPanelBinding(
        VoiceKeybind binding,
        bool ownPanelOpen,
        bool chatOpen,
        bool allowAllWhileChatOpen)
    {
        bool hardBlocked = VoiceChatPatches.ShouldHardSuppressVoiceInput(
            Application.isFocused,
            VoiceUiKit.RebindRow.ShouldSuppressKeybinds,
            VoiceUiKit.AnyPanelOpen && !ownPanelOpen,
            Minigame.Instance != null,
            VoiceChatPatches.IsFriendsListOpen());
        return hardBlocked || VoiceChatPatches.ShouldBlockBindingForChat(
            chatOpen, allowAllWhileChatOpen, binding.AllowWhileChatOpen);
    }

    private static void SuppressOtherBindingsAcrossPanelBoundary(VoiceKeybind allowedCloser)
    {
        VoiceChatPatches.ReleaseHeldTransmitInputs();
        foreach (var binding in VoiceChatKeybinds.AllBindings)
        {
            if (binding != allowedCloser)
                binding.SuppressUntilReleased();
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
