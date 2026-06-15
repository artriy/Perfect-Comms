using System;
using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceHostMenuEntry
{
    private static GameSettingMenu? _hostMenu;
    private static readonly CommsChipButton _chip = new();

    [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.Start))]
    [HarmonyPostfix]
    static void PerfectComms_AddHostButton(GameSettingMenu __instance)
    {
        try
        {
            if (__instance == null) return;
            _hostMenu = __instance;
            EnsureButton();
            if (HostGate()) _chip.ShowWithPop();
        }
        catch (Exception e)
        {
            VoiceDiagnostics.DebugWarning($"[PerfectComms] Failed to add Host button: {e.Message}");
        }
    }

    [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.Close))]
    [HarmonyPostfix]
    static void PerfectComms_HideOnHostClose()
    {
        _hostMenu = null;
        _chip.Hide();
        try { if (HostSettingsPanel.IsOpen) HostSettingsPanel.Hide(); }
        catch { }
    }

    private static bool HostGate() =>
        AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

    private static void EnsureButton()
    {
        if (_chip.Built) return;
        _chip.Build("PERFECT COMMS", "HOST VOICE SETTINGS",
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -48f),
            HostSettingsPanel.Show,
            static () => _hostMenu != null && _hostMenu.gameObject != null && _hostMenu.gameObject.activeInHierarchy,
            HostGate);
    }

    public static bool CursorOverChip => _chip.CursorOver;

    public static void TickHostButton() => _chip.Tick();
}
