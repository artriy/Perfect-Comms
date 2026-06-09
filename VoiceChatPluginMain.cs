using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MiraAPI;
using MiraAPI.PluginLoading;
using Reactor;
using Reactor.Networking;
using Reactor.Networking.Attributes;
using Reactor.Utilities;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

[BepInPlugin(Id, "Perfect Comms", Version)]
[BepInProcess("Among Us.exe")]
[BepInDependency(ReactorPlugin.Id)]
[BepInDependency(MiraApiPlugin.Id)]
[ReactorModFlags(ModFlags.RequireOnAllClients)]
public class VoiceChatPluginMain : BasePlugin, IMiraPlugin
{
    public const string Id = "com.edgetel.perfectcomms";
    public const string Version = "2.1.5";
    public static ManualLogSource Logger { get; private set; } = null!;
    public Harmony Harmony { get; } = new(Id);
    public string OptionsTitleText => "Perfect Comms";
    public ConfigFile GetConfigFile() => Config;
    private const string ResPrefix = "Lib.";
    private static readonly Dictionary<string, Assembly> _asmCache
        = new(StringComparer.OrdinalIgnoreCase);

    public static GameObject? ResidentObject { get; private set; }

    static VoiceChatPluginMain()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
    }

    private static Assembly? ResolveEmbeddedAssembly(object? sender, ResolveEventArgs args)
    {
        var shortName = new AssemblyName(args.Name).Name;
        if (shortName == null) return null;
        if (shortName.Equals("MiraAPI", StringComparison.OrdinalIgnoreCase) ||
            shortName.Equals("Reactor", StringComparison.OrdinalIgnoreCase))
            return null;
        if (_asmCache.TryGetValue(shortName, out var cached)) return cached;
        foreach (var resourceName in new[]
        {
            ResPrefix + shortName + ".dll",
            typeof(VoiceChatPluginMain).Namespace + ".Libs." + shortName + ".dll"
        })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) continue;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var loaded = Assembly.Load(ms.ToArray());
            _asmCache[shortName] = loaded;
            return loaded;
        }

        return null;
    }

    public override void Load()
    {
        Logger = Log;
        VanillaLobbyDiagnostics.Configure(message => Logger.LogInfo(message), message => Logger.LogWarning(message));
        VoiceDiagnostics.DebugInfo("[VC] Loading Perfect Comms.");
        ReactorCredits.Register("Perfect Comms", Version, false, ReactorCredits.AlwaysShow);
        VoiceDiagnostics.Init();
        if (VoiceDiagnostics.IsEnabled && !string.IsNullOrEmpty(VoiceDiagnostics.Path))
            VoiceDiagnostics.DebugInfo($"[VC] Diagnostics log: {VoiceDiagnostics.Path}");
        ResidentObject = new GameObject("PerfectComms_ResidentObject");
        GameObject.DontDestroyOnLoad(ResidentObject);
        ResidentObject.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        VCManager.RegisterSceneHook();
        VoiceChatHudState.Init();
        VoiceChatPatches.RegisterKeybindHandlers();
        ApplyHarmonyPatchesResiliently();
        DeviceLabelPatch.TryApply(Harmony); // reflection-resolved target; applied conditionally, never aborts
        VanillaLobbyPatchDiagnostics.LogPatchState(Harmony);
        VoiceDiagnostics.DebugInfo("[VC] Perfect Comms loaded.");
    }

    // Patch classes one-by-one: PatchAll aborts the whole pass on a single incompatible
    // patch (game-version skew), silently disabling every later patch. Skip and log instead.
    private void ApplyHarmonyPatchesResiliently()
    {
        int skipped = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
        {
            try
            {
                Harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                skipped++;
                VoiceDiagnostics.DebugWarning($"[VC] Skipped Harmony patch class {type.FullName}: {ex.Message}");
            }
        }

        if (skipped > 0)
            VoiceDiagnostics.DebugWarning($"[VC] {skipped} Harmony patch class(es) were skipped (incompatible with this game version); the rest applied.");
    }
}
