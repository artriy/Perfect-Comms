using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;

namespace VoiceChatPlugin.VoiceChat;

internal static class VanillaLobbyDiagnostics
{
    internal static bool Verbose => VoiceDiagnostics.IsEnabled;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> NextLogUtc = new(StringComparer.Ordinal);
    private static Action<string>? _infoSink;
    private static Action<string>? _warningSink;

    internal static void Configure(Action<string> infoSink, Action<string> warningSink)
    {
        _infoSink = infoSink;
        _warningSink = warningSink;
    }

    internal static void Info(string area, string message)
    {
        if (Verbose) _infoSink?.Invoke($"[VC][VanillaLobby][{area}] {message}");
    }

    internal static void Warning(string area, string message)
    {
        if (Verbose)
            (_warningSink ?? _infoSink)?.Invoke($"[VC][VanillaLobby][{area}] {message}");
    }

    internal static void Notice(string area, string message)
    {
        if (Verbose)
            _infoSink?.Invoke($"[VC][VanillaLobby][{area}] {message}");
    }

    internal static void NoticeLimited(string key, string area, string message, int first = 8, int every = 120)
    {
        if (!Verbose) return;

        int count;
        lock (Gate)
        {
            Counts.TryGetValue(key, out count);
            count++;
            Counts[key] = count;
        }

        if (count <= first || every > 0 && count % every == 0)
            Notice(area, $"#{count} {message}");
    }

    internal static void Limited(string key, string area, string message, int first = 12, int every = 60)
    {
        if (!Verbose) return;

        int count;
        lock (Gate)
        {
            Counts.TryGetValue(key, out count);
            count++;
            Counts[key] = count;
        }

        if (count <= first || every > 0 && count % every == 0)
            Info(area, $"#{count} {message}");
    }

    internal static void Throttled(string key, string area, string message, double seconds = 2.0)
    {
        if (!Verbose) return;

        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (NextLogUtc.TryGetValue(key, out var next) && now < next) return;
            NextLogUtc[key] = now.AddSeconds(seconds);
        }

        Info(area, message);
    }

    internal static string DescribeTmp(TextMeshPro? tmp)
    {
        if (tmp == null) return "<null>";
        var text = tmp.text ?? "";
        text = text.Replace("\r", "\\r").Replace("\n", "\\n");
        if (text.Length > 80) text = text[..80] + "…";
        return $"name='{tmp.name}' active={tmp.gameObject.activeInHierarchy} text='{text}' size={tmp.fontSize:0.##}";
    }
}

internal static class VanillaLobbyPatchDiagnostics
{
    internal static void LogPatchState(Harmony harmony)
    {
        if (!VanillaLobbyDiagnostics.Verbose) return;

        VanillaLobbyDiagnostics.Info("patch-state",
            $"assembly={typeof(VanillaLobbyPatchDiagnostics).Assembly.GetName().Name} version={typeof(VanillaLobbyPatchDiagnostics).Assembly.GetName().Version} harmonyId={harmony.Id}");

        LogMethod(typeof(FindAGameManager), nameof(FindAGameManager.RefreshList));
        LogMethod(typeof(FindAGameManager), nameof(FindAGameManager.Update));
        LogMethod(typeof(FindAGameManager), nameof(FindAGameManager.HandleList));
        LogMethod(typeof(GameContainer), nameof(GameContainer.SetGameListing));
        LogMethod(typeof(GameContainer), nameof(GameContainer.SetupGameInfo));
        LogMethod(typeof(FindGameMoreInfoPopup), nameof(FindGameMoreInfoPopup.SetupInfo));
        LogMethod(typeof(GameStartManager), nameof(GameStartManager.Start));
        LogMethod(typeof(InnerNet.InnerNetClient), nameof(InnerNet.InnerNetClient.RequestGameList));

        LogFields(typeof(GameContainer));
        LogFields(typeof(FindAGameManager));
        LogFields(typeof(FindGameMoreInfoPopup));
    }

    private static void LogMethod(Type type, string methodName)
    {
        var method = AccessTools.Method(type, methodName);
        if (method == null)
        {
            VanillaLobbyDiagnostics.Warning("patch-state", $"method-missing type={type.FullName} method={methodName}");
            return;
        }

        var info = Harmony.GetPatchInfo(method);
        var owners = info == null
            ? "<no patch info>"
            : string.Join(",", info.Owners.OrderBy(x => x));
        var prefixes = info?.Prefixes?.Count ?? 0;
        var postfixes = info?.Postfixes?.Count ?? 0;
        VanillaLobbyDiagnostics.Info("patch-state", $"method={type.Name}.{methodName} owners={owners} prefixes={prefixes} postfixes={postfixes}");
    }

    private static void LogFields(Type type)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var fields = type.GetFields(flags)
            .Select(f => $"{f.Name}:{f.FieldType.Name}")
            .OrderBy(x => x)
            .ToArray();
        VanillaLobbyDiagnostics.Info("patch-state", $"fields {type.Name} count={fields.Length} [{string.Join("; ", fields)}]");
    }
}
