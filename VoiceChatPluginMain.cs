using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

[BepInPlugin(Id, "Perfect Comms", Version)]
#if !ANDROID
[BepInProcess("Among Us.exe")]
#endif
public class VoiceChatPluginMain : BasePlugin
{
    public const string Id = "com.edgetel.perfectcomms";
    public const string Version = "4.1.6";
    public static ManualLogSource Logger { get; private set; } = null!;
    internal static ConfigFile PluginConfig { get; private set; } = null!;
    private static PerfectCommsConfigStore? _configStore;
    public Harmony Harmony { get; } = new(Id);
    private const string ResPrefix = "Lib.";
    private static readonly Dictionary<string, Assembly> _asmCache
        = new(StringComparer.OrdinalIgnoreCase);

    public static GameObject? ResidentObject { get; private set; }

    static VoiceChatPluginMain()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownVoiceRuntime("process-exit");
        AppDomain.CurrentDomain.DomainUnload += (_, _) => ShutdownVoiceRuntime("domain-unload");
    }

    internal static void ShutdownVoiceRuntime(string reason)
    {
        try { VoiceLobbyRegistryPublisher.ClearLocalListing(); }
        catch { /* listing deletion is best-effort and cannot block local audio/process teardown */ }
        try { VoiceChatRoom.ShutdownCurrentRoom(reason); }
        catch { /* process/domain shutdown must continue even if the room is already gone */ }
#if WINDOWS
        // Room release normally terminates the helper. This remains the final safety boundary for
        // shutdown while a room is active or if teardown was interrupted by process/domain unload.
        try { SidecarVoiceHost.Shutdown(reason); }
        catch { /* the OS will finish teardown; never block the remaining shutdown hooks */ }
#endif
        PerfectCommsConfigStore.TryFlushPending();
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
        _configStore = PerfectCommsConfigStore.Open(
            Config,
            new BepInPlugin(Id, "Perfect Comms", Version),
            Application.persistentDataPath,
            message => Log.LogInfo(message),
            message => Log.LogWarning(message));
        PluginConfig = _configStore.Config;
        VoiceSettings.Instance = new VoiceChatLocalSettings(PluginConfig);
        VoiceSettings.Instance.WireRuntimeHandlers();
        VoiceChatKeybinds.Initialize(PluginConfig);
        VoiceChatGameOptions.GetInstance();
        VoiceRoleIntegrationOptions.GetInstance();
        _configStore.CompleteInitialization();
        VanillaLobbyDiagnostics.Configure(message => Logger.LogInfo(message), message => Logger.LogWarning(message));
        VoiceDiagnostics.DebugInfo("[VC] Loading Perfect Comms.");
        VoiceDiagnostics.Init();
        if (VoiceDiagnostics.IsEnabled && !string.IsNullOrEmpty(VoiceDiagnostics.Path))
            VoiceDiagnostics.DebugInfo($"[VC] Diagnostics log: {VoiceDiagnostics.Path}");
        ResidentObject = new GameObject("PerfectComms_ResidentObject");
        GameObject.DontDestroyOnLoad(ResidentObject);
        ResidentObject.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        VCManager.RegisterSceneHook();
        VoiceUiDriver.Register();
        VoiceChatHudState.Init();
        VoiceChatPatches.RegisterKeybindHandlers();
        ApplyHarmonyPatchesResiliently();
        // Join and disconnect are one privacy-critical lifecycle contract. A partial resilient
        // patch pass must not leave voice capture running without its authoritative teardown hook.
        VoiceJoinGuard.ValidateCriticalPatchHealth();
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
