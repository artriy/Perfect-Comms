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
    internal const int PendingCapacity = 4096;
    private const int MaxLinesPerWriterPass = 512;
    private const int ShutdownLockTimeoutMs = 2000;
    private const double FallbackReportIntervalSeconds = 30;

    private static readonly object Lock = new();
    private static readonly AutoResetEvent PendingSignal = new(false);
    private static int _writerThreadStarted;
    private static int _processExiting;
    private static int _shutdownStarted;
    private static StreamWriter? _writer;
    private static string _path = "";
    private static int _mainThreadId = -1;
    private static int _enabled;
    private static DateTime _enabledAtUtc;
    private static DiagnosticSession? _session;
    private static MainThreadContext _mainContext = MainThreadContext.Unknown;
    // A process-local HMAC key keeps low-entropy lobby codes from being recoverable with an
    // offline dictionary while still allowing entries in one diagnostic session to be correlated.
    private static readonly byte[] SensitiveValueHashKey =
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    static VoiceDiagnostics()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownForProcessExit("process-exit");
        AppDomain.CurrentDomain.DomainUnload += (_, _) => ShutdownForProcessExit("domain-unload");
    }

    public static string Path => Volatile.Read(ref _path);
    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public static void Init()
    {
        CaptureMainThreadId();
        if (!IsEnabled) return;

        lock (Lock)
            InitLocked();
        EnsureWriterThread();
    }

    public static void SetEnabled(bool enabled)
    {
        CaptureMainThreadId();
        if (enabled)
        {
            lock (Lock)
            {
                if (!IsEnabled)
                {
                    _enabledAtUtc = DateTime.UtcNow;
                    InitLocked();
                    Volatile.Write(ref _enabled, 1);
                }
                else
                {
                    // Re-attempt opening after an earlier filesystem failure, but do not
                    // replace a live session when the setting dispatches the same value twice.
                    InitLocked();
                }
            }
            EnsureWriterThread();
            PendingSignal.Set();
            return;
        }

        Volatile.Write(ref _enabled, 0);
        lock (Lock)
            CloseLocked("debug-disabled");
    }

    public static void DebugInfo(string message)
    {
        if (!IsEnabled) return;
        MirrorPluginLog("info", message);
        VoiceChatPluginMain.Logger.LogInfo(message);
    }

    public static void DebugWarning(string message)
    {
        if (!IsEnabled) return;
        MirrorPluginLog("warning", message);
        VoiceChatPluginMain.Logger.LogWarning(message);
    }

    public static void DebugError(string message)
    {
        if (!IsEnabled) return;
        MirrorPluginLog("error", message);
        VoiceChatPluginMain.Logger.LogError(message);
    }

    // Explicit lifecycle hooks for a plugin unload path. Flush is synchronous by design and
    // must only be called from lifecycle/control code; normal and audio-path logging uses Log.
    public static void Flush()
    {
        lock (Lock)
        {
            var session = _session;
            if (session == null || _writer == null) return;
            DrainLocked(session, int.MaxValue);
        }
    }

    public static void Shutdown(string reason = "plugin-shutdown")
    {
        Volatile.Write(ref _enabled, 0);
        lock (Lock)
            CloseLocked(reason);
    }

    // Enqueue-only: callers (including the audio pull, capture, and decode threads) never
    // touch the diagnostics file, its StreamWriter, or the writer lock. When the bounded
    // queue is full the event is dropped and the writer emits an explicit aggregate marker.
    public static void Log(string category, string message)
    {
        if (!IsEnabled) return;

        bool isMainThread = IsMainThread();
        if (isMainThread)
            RefreshMainThreadContext();

        var session = Volatile.Read(ref _session);
        if (session == null) return;

        DateTime utc = DateTime.UtcNow;
        long nowTimestamp = Stopwatch.GetTimestamp();
        var context = Volatile.Read(ref _mainContext);
        var entry = new DiagnosticEntry(
            utc,
            DiagnosticLogFormatter.ElapsedSeconds(session.StartTimestamp, nowTimestamp),
            DiagnosticLogFormatter.ElapsedSeconds(session.EnabledTimestamp, nowTimestamp),
            context.Frame,
            context.Identity,
            Environment.CurrentManagedThreadId,
            isMainThread ? "main" : "background",
            DiagnosticLogFormatter.ElapsedMilliseconds(context.UpdatedTimestamp, nowTimestamp),
            category,
            message);

        if (!session.Pending.TryEnqueue(entry)) return;
        EnsureWriterThread();
        PendingSignal.Set();
    }

    // Device labels may contain a user's real name, headset serial, or Bluetooth address. Keep
    // enough stable metadata to correlate selection/recovery without writing that label to a
    // shareable diagnostic file.
    internal static string DescribeDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return "default=true";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(deviceId));
        return $"default=false idHash={Convert.ToHexString(bytes, 0, 6)} idChars={deviceId.Length}";
    }

    internal static string DescribeRoom(string? roomCode)
        => DescribeSensitiveValue("room", roomCode);

    internal static string DescribeRegion(string? region)
        => DescribeSensitiveValue("region", region);

    private static string DescribeSensitiveValue(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"{field}Present=false";
        using var hmac = new System.Security.Cryptography.HMACSHA256(SensitiveValueHashKey);
        var bytes = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return $"{field}Present=true {field}Hash={Convert.ToHexString(bytes, 0, 6)} {field}Chars={value.Length}";
    }

    private static void MirrorPluginLog(string severity, string message)
    {
        // This only enqueues. Writer failures use WriteFallbackError directly, never these
        // Debug* methods, so the BepInEx fallback cannot recurse into the failed writer.
        Log(
            "diagnostics.plugin-log",
            $"severity={severity} message=\"{DiagnosticLogFormatter.SanitizeQuotedValue(message)}\"");
    }

    private static void EnsureWriterThread()
    {
        if (Volatile.Read(ref _processExiting) != 0) return;
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
        while (Volatile.Read(ref _processExiting) == 0)
        {
            PendingSignal.WaitOne(1000);
            if (Volatile.Read(ref _processExiting) != 0) return;

            try
            {
                lock (Lock)
                {
                    var session = _session;
                    if (session == null || !IsEnabled) continue;
                    if (_writer == null && !TryOpenWriterLocked(session)) continue;
                    DrainLocked(session, MaxLinesPerWriterPass);
                    if (session.Pending.PendingCount > 0)
                        PendingSignal.Set();
                }
            }
            catch (Exception ex)
            {
                lock (Lock)
                {
                    var session = _session;
                    if (session != null)
                        HandleWriterFailureLocked(session, "writer-loop", ex, uncertainLines: 0);
                }
            }
        }
    }

    private static void InitLocked()
    {
        if (_session == null)
        {
            DateTime startedUtc = DateTime.UtcNow;
            DateTime enabledUtc = _enabledAtUtc == default ? startedUtc : _enabledAtUtc;
            _session = new DiagnosticSession(enabledUtc, startedUtc, Process.GetCurrentProcess().Id);
        }

        if (_writer == null)
            TryOpenWriterLocked(_session);
    }

    private static bool TryOpenWriterLocked(DiagnosticSession session)
    {
        session.OpenAttempts++;
        string attemptedPath = "";
        try
        {
            string root = System.IO.Path.Combine(Paths.BepInExRootPath, "VoiceChatDiagnostics");
            Directory.CreateDirectory(root);
            string part = session.OpenAttempts == 1 ? "" : $"_part{session.OpenAttempts}";
            attemptedPath = System.IO.Path.Combine(
                root,
                $"voicechat_{session.StartedUtc:yyyyMMdd_HHmmss_fff}_pid{session.ProcessId}_session{session.Id[..8]}{part}.log");

            var writer = new StreamWriter(new FileStream(
                attemptedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite))
            {
                AutoFlush = false,
            };

            _writer = writer;
            Volatile.Write(ref _path, attemptedPath);
            session.SuccessfulOpens++;
            if (IsMainThread())
                RefreshMainThreadContext();

            DateTime openedUtc = DateTime.UtcNow;
            string category = session.SuccessfulOpens == 1 ? "diagnostics.start" : "diagnostics.writer-recovery";
            string message =
                $"path=\"{DiagnosticLogFormatter.SanitizeQuotedValue(attemptedPath)}\" " +
                $"pid={session.ProcessId} enabledAtUtc={session.EnabledUtc:o} sessionStartedUtc={session.StartedUtc:o} " +
                $"openedUtc={openedUtc:o} enableToStartMs={(session.StartedUtc - session.EnabledUtc).TotalMilliseconds:0.0} " +
                $"startToOpenMs={(openedUtc - session.StartedUtc).TotalMilliseconds:0.0} " +
                $"openAttempt={session.OpenAttempts} writerFailures={session.WriterFailures} " +
                $"writerLostOrUncertain={session.WriterLostOrUncertain} " +
                $"previousPath=\"{DiagnosticLogFormatter.SanitizeQuotedValue(session.LastPath)}\" " +
                $"previousError=\"{DiagnosticLogFormatter.SanitizeQuotedValue(session.LastFailure)}\" " +
                $"version=debug-gated-2";
            WriteDirectLocked(session, category, message);
            writer.Flush();
            session.LastPath = attemptedPath;
            return true;
        }
        catch (Exception ex)
        {
            HandleWriterFailureLocked(
                session,
                string.IsNullOrEmpty(attemptedPath) ? "open" : $"open:{attemptedPath}",
                ex,
                uncertainLines: _writer == null ? 0 : 1);
            return false;
        }
    }

    private static bool DrainLocked(DiagnosticSession session, int maxLines)
    {
        if (_writer == null) return false;

        long observedDrops = session.Pending.DroppedSinceReport;
        int dequeued = 0;
        try
        {
            if (observedDrops > 0)
            {
                WriteDirectLocked(
                    session,
                    "diagnostics.queue-drop",
                    $"droppedSinceLast={observedDrops} droppedTotal={session.Pending.DroppedTotal} " +
                    $"capacity={session.Pending.Capacity} pending={session.Pending.PendingCount}");
            }

            while (dequeued < maxLines && session.Pending.TryDequeue(out var entry))
            {
                dequeued++;
                WriteEntryLocked(session, entry);
            }

            _writer.Flush();
            if (observedDrops > 0)
                session.Pending.AcknowledgeDrops(observedDrops);
            return true;
        }
        catch (Exception ex)
        {
            // These entries were removed from the queue and may have reached the OS or may
            // only have reached StreamWriter's buffer. Report them as uncertain, never as
            // definitely written, and continue in a new part file on the next pass.
            HandleWriterFailureLocked(session, "drain", ex, dequeued);
            return false;
        }
    }

    private static void CloseLocked(string reason)
    {
        var session = _session;
        if (session == null)
        {
            DisposeWriterLocked();
            return;
        }

        bool producersQuiesced = session.Pending.StopAcceptingAndWait(1000);
        if (!producersQuiesced)
        {
            int activeProducers = session.Pending.ActiveProducerCount;
            session.WriterLostOrUncertain += Math.Max(1, activeProducers);
            if (_writer != null)
            {
                WriteDirectLocked(
                    session,
                    "diagnostics.shutdown-warning",
                    $"producersQuiesced=false activeProducers={activeProducers} timeoutMs=1000 " +
                    $"writerLostOrUncertain={session.WriterLostOrUncertain}");
            }
        }

        // A failed writer gets one immediate replacement attempt so a disable/process-exit
        // still has a chance to preserve the tail and write a conclusive stop marker.
        for (int attempt = 0; attempt < 2 && session.Pending.PendingCount > 0; attempt++)
        {
            if (_writer == null && !TryOpenWriterLocked(session)) continue;
            DrainLocked(session, int.MaxValue);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (_writer == null && !TryOpenWriterLocked(session)) continue;
            try
            {
                WriteDirectLocked(
                    session,
                    "diagnostics.stop",
                    $"reason={DiagnosticLogFormatter.SanitizeToken(reason)} producersQuiesced={producersQuiesced.ToString().ToLowerInvariant()} pending={session.Pending.PendingCount} " +
                    $"droppedTotal={session.Pending.DroppedTotal} writerFailures={session.WriterFailures} " +
                    $"writerLostOrUncertain={session.WriterLostOrUncertain} sessionSeconds={session.ElapsedSeconds:0.000}");
                _writer!.Flush();
                break;
            }
            catch (Exception ex)
            {
                HandleWriterFailureLocked(session, "stop", ex, uncertainLines: 1);
            }
        }

        DisposeWriterLocked();
        _session = null;
        Volatile.Write(ref _path, "");
    }

    private static void WriteEntryLocked(DiagnosticSession session, DiagnosticEntry entry)
    {
        if (_writer == null) return;
        long sequence = ++session.Sequence;
        _writer.WriteLine(DiagnosticLogFormatter.FormatLine(session.Id, sequence, entry));
    }

    private static void WriteDirectLocked(DiagnosticSession session, string category, string message)
    {
        if (_writer == null) return;

        bool isMainThread = IsMainThread();
        if (isMainThread)
            RefreshMainThreadContext();
        long nowTimestamp = Stopwatch.GetTimestamp();
        var context = Volatile.Read(ref _mainContext);
        var entry = new DiagnosticEntry(
            DateTime.UtcNow,
            DiagnosticLogFormatter.ElapsedSeconds(session.StartTimestamp, nowTimestamp),
            DiagnosticLogFormatter.ElapsedSeconds(session.EnabledTimestamp, nowTimestamp),
            context.Frame,
            context.Identity,
            Environment.CurrentManagedThreadId,
            isMainThread ? "main" : "background",
            DiagnosticLogFormatter.ElapsedMilliseconds(context.UpdatedTimestamp, nowTimestamp),
            category,
            message);
        WriteEntryLocked(session, entry);
    }

    private static void HandleWriterFailureLocked(
        DiagnosticSession session,
        string stage,
        Exception exception,
        int uncertainLines)
    {
        session.WriterFailures++;
        session.WriterLostOrUncertain += Math.Max(0, uncertainLines);
        session.LastFailure = $"{exception.GetType().Name}:{DiagnosticLogFormatter.SanitizeLinePart(exception.Message)}";
        string failedPath = _path;
        if (!string.IsNullOrEmpty(failedPath))
            session.LastPath = failedPath;
        DisposeWriterLocked();

        long now = Stopwatch.GetTimestamp();
        double sinceLastReport = DiagnosticLogFormatter.ElapsedSeconds(session.LastFallbackReportTimestamp, now);
        if (session.LastFallbackReportTimestamp != 0 && sinceLastReport < FallbackReportIntervalSeconds)
        {
            session.SuppressedFallbackReports++;
            return;
        }

        string fallback =
            $"[VC] Diagnostics writer failure (fallback, no recursive diagnostics): " +
            $"session={session.Id} stage={DiagnosticLogFormatter.SanitizeToken(stage)} " +
            $"path=\"{DiagnosticLogFormatter.SanitizeQuotedValue(failedPath)}\" " +
            $"error=\"{DiagnosticLogFormatter.SanitizeQuotedValue(session.LastFailure)}\" " +
            $"failures={session.WriterFailures} lostOrUncertain={session.WriterLostOrUncertain} " +
            $"suppressed={session.SuppressedFallbackReports}";
        session.LastFallbackReportTimestamp = now;
        session.SuppressedFallbackReports = 0;
        WriteFallbackError(fallback);
    }

    private static void DisposeWriterLocked()
    {
        var writer = _writer;
        _writer = null;
        Volatile.Write(ref _path, "");
        if (writer == null) return;
        try { writer.Dispose(); }
        catch { }
    }

    private static void WriteFallbackError(string message)
    {
        string singleLine = DiagnosticLogFormatter.SanitizeLinePart(message);
        try
        {
            VoiceChatPluginMain.Logger?.LogError(singleLine);
            return;
        }
        catch
        {
            // The BepInEx logger may not exist during a very early init or late process exit.
        }

        try { System.Console.Error.WriteLine(singleLine); }
        catch { }
    }

    private static void ShutdownForProcessExit(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        Volatile.Write(ref _processExiting, 1);
        Volatile.Write(ref _enabled, 0);
        PendingSignal.Set();

        bool entered = false;
        try
        {
            entered = Monitor.TryEnter(Lock, ShutdownLockTimeoutMs);
            if (entered)
                CloseLocked(reason);
            else
                WriteFallbackError("[VC] Diagnostics shutdown could not acquire writer lock; stop marker may be missing.");
        }
        catch (Exception ex)
        {
            WriteFallbackError($"[VC] Diagnostics shutdown failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (entered) Monitor.Exit(Lock);
        }
    }

    private static void CaptureMainThreadId()
    {
        Interlocked.CompareExchange(ref _mainThreadId, Environment.CurrentManagedThreadId, -1);
    }

    private static bool IsMainThread()
        => Volatile.Read(ref _mainThreadId) == Environment.CurrentManagedThreadId;

    private static void RefreshMainThreadContext()
    {
        var context = new MainThreadContext(
            ReadMainThreadFrame(),
            ReadMainThreadIdentity(),
            Stopwatch.GetTimestamp());
        Volatile.Write(ref _mainContext, context);
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
            return $"client={clientId} player={playerId} name=\"{DiagnosticLogFormatter.SanitizeQuotedValue(name)}\"";
        }
        catch
        {
            return MainThreadContext.Unknown.Identity;
        }
    }

    private sealed class DiagnosticSession
    {
        public DiagnosticSession(DateTime enabledUtc, DateTime startedUtc, int processId)
        {
            EnabledUtc = enabledUtc;
            StartedUtc = startedUtc;
            ProcessId = processId;
            Id = Guid.NewGuid().ToString("N");
            StartTimestamp = Stopwatch.GetTimestamp();
            double preStartSeconds = Math.Max(0, (startedUtc - enabledUtc).TotalSeconds);
            EnabledTimestamp = StartTimestamp - (long)(preStartSeconds * Stopwatch.Frequency);
            Pending = new DiagnosticBuffer<DiagnosticEntry>(PendingCapacity);
        }

        public string Id { get; }
        public DateTime EnabledUtc { get; }
        public DateTime StartedUtc { get; }
        public int ProcessId { get; }
        public long EnabledTimestamp { get; }
        public long StartTimestamp { get; }
        public DiagnosticBuffer<DiagnosticEntry> Pending { get; }
        public long Sequence;
        public int OpenAttempts;
        public int SuccessfulOpens;
        public int WriterFailures;
        public long WriterLostOrUncertain;
        public long LastFallbackReportTimestamp;
        public int SuppressedFallbackReports;
        public string LastPath = "";
        public string LastFailure = "";
        public double ElapsedSeconds => DiagnosticLogFormatter.ElapsedSeconds(StartTimestamp, Stopwatch.GetTimestamp());
    }

    private sealed record MainThreadContext(int Frame, string Identity, long UpdatedTimestamp)
    {
        public static readonly MainThreadContext Unknown =
            new(-1, "client=-1 player=-1 name=\"unknown\"", 0);
    }
}

internal sealed record DiagnosticEntry(
    DateTime Utc,
    double SessionElapsedSeconds,
    double EnabledElapsedSeconds,
    int Frame,
    string Identity,
    int CallerThreadId,
    string ContextThread,
    long MainContextAgeMilliseconds,
    string Category,
    string Message);

internal sealed class DiagnosticBuffer<T>
{
    private readonly ConcurrentQueue<T> _queue = new();
    private int _accepting = 1;
    private int _activeProducers;
    private int _pendingCount;
    private long _droppedSinceReport;
    private long _droppedTotal;

    public DiagnosticBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int PendingCount => Volatile.Read(ref _pendingCount);
    public int ActiveProducerCount => Volatile.Read(ref _activeProducers);
    public long DroppedSinceReport => Interlocked.Read(ref _droppedSinceReport);
    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    public bool TryEnqueue(T value)
    {
        Interlocked.Increment(ref _activeProducers);
        try
        {
            if (Volatile.Read(ref _accepting) == 0) return false;

            int reservedCount = Interlocked.Increment(ref _pendingCount);
            if (reservedCount > Capacity)
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _droppedSinceReport);
                Interlocked.Increment(ref _droppedTotal);
                return false;
            }

            _queue.Enqueue(value);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _activeProducers);
        }
    }

    public bool TryDequeue(out T value)
    {
        if (_queue.TryDequeue(out value!))
        {
            Interlocked.Decrement(ref _pendingCount);
            return true;
        }

        value = default!;
        return false;
    }

    public void AcknowledgeDrops(long count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _droppedSinceReport, -count);
    }

    public bool StopAcceptingAndWait(int timeoutMilliseconds)
    {
        Volatile.Write(ref _accepting, 0);
        return SpinWait.SpinUntil(
            () => Volatile.Read(ref _activeProducers) == 0,
            Math.Max(0, timeoutMilliseconds));
    }
}

internal static class DiagnosticLogFormatter
{
    private const int MaxLinePartChars = 16384;

    public static string FormatLine(string sessionId, long sequence, DiagnosticEntry entry)
    {
        return
            $"{entry.Utc:o} session={SanitizeToken(sessionId)} seq={sequence} " +
            $"+{entry.SessionElapsedSeconds:0.000}s enabled+{entry.EnabledElapsedSeconds:0.000}s " +
            $"frame={entry.Frame} {SanitizeLinePart(entry.Identity)} " +
            $"thread={entry.CallerThreadId} contextThread={SanitizeToken(entry.ContextThread)} " +
            $"mainContextAgeMs={entry.MainContextAgeMilliseconds} " +
            $"{SanitizeToken(entry.Category)} {SanitizeLinePart(entry.Message)}";
    }

    public static string SanitizeLinePart(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        int length = Math.Min(value.Length, MaxLinePartChars);
        char[]? sanitized = null;
        for (int i = 0; i < length; i++)
        {
            char c = value[i];
            if (!char.IsControl(c)) continue;
            sanitized ??= value[..length].ToCharArray();
            sanitized[i] = ' ';
        }

        string result = sanitized == null ? value[..length] : new string(sanitized);
        return value.Length > MaxLinePartChars ? result + "...[truncated]" : result;
    }

    public static string SanitizeQuotedValue(string? value)
        => SanitizeLinePart(value).Replace('"', '\'');

    public static string SanitizeToken(string? value)
    {
        string safe = SanitizeLinePart(value);
        if (safe.Length == 0) return "unknown";
        char[]? sanitized = null;
        for (int i = 0; i < safe.Length; i++)
        {
            if (!char.IsWhiteSpace(safe[i]) && safe[i] != '=') continue;
            sanitized ??= safe.ToCharArray();
            sanitized[i] = '_';
        }
        return sanitized == null ? safe : new string(sanitized);
    }

    public static double ElapsedSeconds(long startTimestamp, long endTimestamp)
    {
        if (startTimestamp <= 0 || endTimestamp <= startTimestamp) return 0;
        return (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
    }

    public static long ElapsedMilliseconds(long startTimestamp, long endTimestamp)
    {
        if (startTimestamp <= 0) return -1;
        if (endTimestamp <= startTimestamp) return 0;
        double milliseconds = (endTimestamp - startTimestamp) * 1000d / Stopwatch.Frequency;
        return milliseconds >= long.MaxValue ? long.MaxValue : (long)milliseconds;
    }
}
