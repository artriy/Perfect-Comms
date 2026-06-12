using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceDiagnostics
{
    private static readonly object Lock = new();
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly AutoResetEvent PendingSignal = new(false);
    private static int _writerThreadStarted;
    private static StreamWriter? _writer;
    private static string _path = "";
    private static int _mainThreadId = -1;
    private static int _lastFrame = -1;
    private static int _enabled;
    private static string _lastIdentity = "client=-1 player=-1 name=\"unknown\"";

    public static string Path => _path;
    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public static void Init()
    {
        if (_mainThreadId == -1)
            _mainThreadId = Environment.CurrentManagedThreadId;
        if (!IsEnabled) return;

        lock (Lock)
            InitLocked();
    }

    public static void SetEnabled(bool enabled)
    {
        if (_mainThreadId == -1)
            _mainThreadId = Environment.CurrentManagedThreadId;
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

    // Enqueue-only: callers (including the NAudio pull and decode threads) never touch the file or its lock;
    // a dedicated background thread drains and flushes.
    public static void Log(string category, string message)
    {
        if (!IsEnabled) return;

        if (IsMainThread())
            RefreshMainThreadContext();
        double elapsed = (DateTime.UtcNow - StartedUtc).TotalSeconds;
        Pending.Enqueue($"{DateTime.UtcNow:o} +{elapsed:0.000}s frame={_lastFrame} {_lastIdentity} {category} {message}");
        EnsureWriterThread();
        PendingSignal.Set();
    }

    private static void EnsureWriterThread()
    {
        if (Interlocked.CompareExchange(ref _writerThreadStarted, 1, 0) != 0) return;
        var thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "VoiceDiagnosticsWriter",
            Priority = System.Threading.ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    private static void WriterLoop()
    {
        while (true)
        {
            try
            {
                PendingSignal.WaitOne(1000);
                if (Pending.IsEmpty) continue;
                lock (Lock)
                {
                    if (_writer == null && IsEnabled)
                        InitLocked();
                    if (_writer == null)
                    {
                        while (Pending.TryDequeue(out _)) { }
                        continue;
                    }
                    bool wrote = false;
                    while (Pending.TryDequeue(out var line))
                    {
                        _writer.WriteLine(line);
                        wrote = true;
                    }
                    if (wrote) _writer.Flush();
                }
            }
            catch
            {
            }
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

            if (_mainThreadId == -1)
                _mainThreadId = Environment.CurrentManagedThreadId;
            if (IsMainThread())
                RefreshMainThreadContext();
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
            while (Pending.TryDequeue(out var line))
                _writer.WriteLine(line);
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
            RefreshMainThreadContext();
        _writer.WriteLine(
            $"{DateTime.UtcNow:o} +{elapsed:0.000}s frame={_lastFrame} {_lastIdentity} {category} {message}");
        _writer.Flush();
    }

    private static bool IsMainThread()
        => Volatile.Read(ref _mainThreadId) == Environment.CurrentManagedThreadId;

    private static void RefreshMainThreadContext()
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
