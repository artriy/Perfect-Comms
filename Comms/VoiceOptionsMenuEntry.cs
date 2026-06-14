using System;
using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceOptionsMenuEntry
{
    private static OptionsMenuBehaviour? _menu;
    private static readonly CommsChipButton _chip = new();

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
    [HarmonyPostfix]
    static void PerfectComms_OnOptionsOpen(OptionsMenuBehaviour __instance)
    {
        _menu = __instance;
        EnsureButton();
        _chip.ShowWithPop();
    }

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close))]
    [HarmonyPrefix]
    static bool PerfectComms_BlockOptionsClose() => !VoiceUiKit.AnyPanelOpen;

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close))]
    [HarmonyPostfix]
    static void PerfectComms_OnOptionsClose()
    {
        _menu = null;
        _chip.Hide();
        try { if (!HudManager.InstanceExists) VoiceSettingsPanel.ForceClose(); }
        catch { }
    }

    public static void NotifyOptionsActive(OptionsMenuBehaviour menu)
    {
        _menu = menu;
        EnsureButton();
        if (!VoiceUiKit.AnyPanelOpen && menu != null && menu.gameObject.activeInHierarchy && !_chip.Visible)
            _chip.ShowWithPop();
    }

    private static void EnsureButton()
    {
        if (_chip.Built) return;
        try
        {
            _chip.Build("PERFECT COMMS", "VOICE SETTINGS",
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(14f, 0f),
                VoiceSettingsPanel.Show,
                static () => _menu != null && _menu.gameObject != null && _menu.gameObject.activeInHierarchy,
                static () => true, 1.0f);
        }
        catch (Exception e)
        {
            VoiceDiagnostics.DebugWarning($"[PerfectComms] Failed to build Options button: {e.Message}");
        }
    }

    public static void TickButton() => _chip.Tick();
}
