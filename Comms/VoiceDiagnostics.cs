using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceDiagnostics
{
    private const double FlushIntervalSeconds = 5.0;
    private static readonly object Lock = new();
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    private static StreamWriter? _writer;
    private static string _path = "";
    private static DateTime _lastFlushUtc = StartedUtc;
    private static int _mainThreadId = -1;
    private static int _lastFrame = -1;
    private static int _enabled;
    private static string _lastIdentity = "client=-1 player=-1 name=\"unknown\"";

    public static string Path => _path;
    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public static void Init()
    {
        if (!IsEnabled) return;

        lock (Lock)
            InitLocked();
    }

    public static void SetEnabled(bool enabled)
    {
        Volatile.Write(ref _enabled, enabled ? 1 : 0);
        lock (Lock)
        {
            if (enabled)
                InitLocked();
            else
                CloseLocked();
        }
    }

    public static void DebugInfo(string message)
    {
        if (IsEnabled)
            VoiceChatPluginMain.Logger.LogInfo(message);
    }

    public static void DebugWarning(string message)
    {
        if (IsEnabled)
            VoiceChatPluginMain.Logger.LogWarning(message);
    }

    public static void DebugError(string message)
    {
        if (IsEnabled)
            VoiceChatPluginMain.Logger.LogError(message);
    }

    public static void Log(string category, string message)
    {
        if (!IsEnabled) return;

        lock (Lock)
        {
            if (_writer == null)
                InitLocked();
            WriteLocked(category, message);
        }
    }

    private static void InitLocked()
    {
        if (_writer != null) return;

        try
        {
            string root = System.IO.Path.Combine(Paths.BepInExRootPath, "VoiceChatDiagnostics");
            Directory.CreateDirectory(root);
            int pid = Process.GetCurrentProcess().Id;
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            _path = System.IO.Path.Combine(root, $"voicechat_{stamp}_pid{pid}.log");
            _writer = new StreamWriter(new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = false,
            };

            _mainThreadId = Environment.CurrentManagedThreadId;
            RefreshMainThreadContextLocked();
            WriteLocked("diagnostics.start", $"path={_path} pid={pid} utc={StartedUtc:o} version=debug-gated-1");
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Diagnostics init failed: {ex.Message}");
        }
    }

    private static void CloseLocked()
    {
        if (_writer == null) return;

        try
        {
            WriteLocked("diagnostics.stop", "debug=false");
            _writer.Flush();
            _writer.Dispose();
        }
        catch
        {
        }
        finally
        {
            _writer = null;
            _path = "";
        }
    }

    private static void WriteLocked(string category, string message)
    {
        if (_writer == null) return;

        double elapsed = (DateTime.UtcNow - StartedUtc).TotalSeconds;
        if (IsMainThread())
            RefreshMainThreadContextLocked();
        _writer.WriteLine(
            $"{DateTime.UtcNow:o} +{elapsed:0.000}s frame={_lastFrame} {_lastIdentity} {category} {message}");
        MaybeFlushLocked(category);
    }

    private static void MaybeFlushLocked(string category)
    {
        if (_writer == null) return;

        var now = DateTime.UtcNow;
        if (category is "diagnostics.start" or "room.close" or "transition.perf.slowUpdate" or "transition.audio.clip" ||
            category.StartsWith("audio.buffer.", StringComparison.Ordinal) ||
            (now - _lastFlushUtc).TotalSeconds >= FlushIntervalSeconds)
        {
            _writer.Flush();
            _lastFlushUtc = now;
        }
    }

    private static bool IsMainThread()
        => Volatile.Read(ref _mainThreadId) == Environment.CurrentManagedThreadId;

    private static void RefreshMainThreadContextLocked()
    {
        _lastFrame = ReadMainThreadFrame();
        _lastIdentity = ReadMainThreadIdentity();
    }

    private static int ReadMainThreadFrame()
    {
        try { return Time.frameCount; }
        catch { return -1; }
    }

    private static string ReadMainThreadIdentity()
    {
        try
        {
            int clientId = AmongUsClient.Instance?.ClientId ?? -1;
            var player = PlayerControl.LocalPlayer;
            int playerId = player != null ? player.PlayerId : -1;
            string name = player?.Data?.PlayerName ?? "unknown";
            return $"client={clientId} player={playerId} name=\"{Sanitize(name)}\"";
        }
        catch
        {
            return "client=-1 player=-1 name=\"unknown\"";
        }
    }

    private static string Sanitize(string value)
        => value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
}
