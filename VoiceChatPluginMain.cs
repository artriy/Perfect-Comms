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
    public const string Version = "1.0.2";
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
        var resourceName = ResPrefix + shortName + ".dll";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var loaded = Assembly.Load(ms.ToArray());
        _asmCache[shortName] = loaded;
        return loaded;
    }

    public override void Load()
    {
        Logger = Log;
        Logger.LogInfo("[VC] Loading Perfect Comms.");
        ReactorCredits.Register("Perfect Comms", Version, false, ReactorCredits.AlwaysShow);
        VoiceDiagnostics.Init();
        Logger.LogInfo($"[VC] Diagnostics log: {VoiceDiagnostics.Path}");
        ResidentObject = new GameObject("PerfectComms_ResidentObject");
        GameObject.DontDestroyOnLoad(ResidentObject);
        ResidentObject.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        VCManager.RegisterSceneHook();
        VoiceChatHudState.Init();
        VoiceChatPatches.RegisterKeybindHandlers();
        Harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("[VC] Perfect Comms loaded.");
    }
}
