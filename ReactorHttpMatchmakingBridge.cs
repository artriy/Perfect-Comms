using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using static UnityEngine.UI.Button;

namespace VoiceChatPlugin;

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Start))]
internal static class GameStartManagerModdedRegionPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(GameStartManager __instance)
    {
        if (!ReactorHttpMatchmakingBridge.IsKnownModdedRegion()) return;

        ReactorHttpMatchmakingBridge.MarkCurrentRegionModded();
        ReactorHttpMatchmakingBridge.RestorePublicToggle(__instance);
    }
}

internal static class ReactorHttpMatchmakingBridge
{
    private static readonly Type? SendWebRequestPatchType =
        AccessTools.TypeByName("Reactor.Networking.Patches.HttpPatches+SendWebRequestPatch");
    private static readonly FieldInfo? LastConnectionField =
        AccessTools.Field(SendWebRequestPatchType, "<LastConnection>k__BackingField");
    private static readonly FieldInfo? HostPublicButtonField =
        AccessTools.Field(typeof(GameStartManager), "HostPublicButton");
    private static readonly FieldInfo? HostPrivateButtonField =
        AccessTools.Field(typeof(GameStartManager), "HostPrivateButton");
    private static string? _lastWarning;
    private static bool _loggedBridge;

    internal static bool IsKnownModdedRegion()
    {
        try
        {
            var region = DestroyableSingleton<ServerManager>.Instance?.CurrentRegion;
            if (region == null) return false;

            if (IsKnownModdedValue(region.Name)
                || IsKnownModdedValue(region.PingServer)
                || IsKnownModdedValue(region.TargetServer))
                return true;

            var servers = region.Servers;
            if (servers == null) return false;
            foreach (var server in servers)
            {
                if (server == null) continue;
                if (IsKnownModdedValue(server.Name)
                    || IsKnownModdedValue(server.Ip)
                    || IsKnownModdedValue(server.HttpUrl))
                    return true;
            }
        }
        catch (Exception ex)
        {
            WarnOnce($"region check failed: {ex.Message}");
        }

        return false;
    }

    internal static void MarkCurrentRegionModded()
    {
        try
        {
            if (LastConnectionField == null) return;
            var currentRegion = DestroyableSingleton<ServerManager>.Instance?.CurrentRegion;
            var region = currentRegion == null
                ? null
                : ((Il2CppObjectBase)currentRegion).TryCast<StaticHttpRegionInfo>();
            if (region == null) return;

            LastConnectionField.SetValue(null, (region, true));
            if (!_loggedBridge)
            {
                _loggedBridge = true;
                VoiceDiagnostics.DebugInfo("[VC] Marked known modded HTTP region as Reactor-compatible.");
            }
        }
        catch (Exception ex)
        {
            WarnOnce($"Reactor region bridge failed: {ex.Message}");
        }
    }

    internal static void RestorePublicToggle(GameStartManager manager)
    {
        try
        {
            if (HostPublicButtonField?.GetValue(manager) is PassiveButton publicButton)
                publicButton.enabled = true;

            if (HostPrivateButtonField?.GetValue(manager) is not PassiveButton privateButton) return;

            privateButton.enabled = true;
            privateButton.OnClick = new ButtonClickedEvent();
            privateButton.OnClick.AddListener((Action)manager.MakePublic);

            var inactive = privateButton.transform.FindChild("Inactive");
            if (inactive != null && inactive.GetComponent<SpriteRenderer>() is { } sprite)
                sprite.color = Color.white;
        }
        catch (Exception ex)
        {
            WarnOnce($"public toggle restore failed: {ex.Message}");
        }
    }

    private static bool IsKnownModdedValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.IndexOf("duikbo.at", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("aumods.org", StringComparison.OrdinalIgnoreCase) >= 0
               || value.IndexOf("modded", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void WarnOnce(string warning)
    {
        if (string.Equals(_lastWarning, warning, StringComparison.Ordinal)) return;
        _lastWarning = warning;
        VoiceDiagnostics.DebugWarning($"[VC] {warning}");
    }
}
