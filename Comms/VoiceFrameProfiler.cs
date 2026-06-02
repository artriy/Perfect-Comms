using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// Per-frame main-thread performance profiler for hunting stutter / lag spikes / bad frametime.
//
// Gated ENTIRELY by VoiceDiagnostics.IsEnabled (the DebugVoiceStats toggle): when disabled, Begin() does a
// single bool check and returns, and End() returns immediately — effectively zero overhead in normal play.
// When enabled it:
//   * times named segments of the per-frame voice work (Stopwatch),
//   * samples the Unity frame time (Time.unscaledDeltaTime) and the managed heap + GC collection counts,
//   * logs EVERY slow frame (frame.slow) with a full per-segment breakdown + GC/heap signal + context,
//   * logs a rolling-window aggregate (frame.window) every few seconds (fps, frametime p50/p95/p99/max,
//     per-segment avg/max, GC churn, heap-growth rate, player/peer/phase).
//
// Design goal: from TWO clients' diagnostic logs (aligned by the UTC timestamp VoiceDiagnostics prepends to
// every line), a stutter can be attributed to one of three causes:
//   (a) VC CPU      -> a frame.slow where one voice segment dominates "vc"/the breakdown,
//   (b) GC pause    -> a frame.slow with high dt but LOW vc and gc=Y (heap dropped / collection-count bumped),
//   (c) external    -> a frame.slow with high dt, low vc, and no GC (something outside this mod).
// The frame.window allocation/GC stats reveal which segment drives the allocation rate behind (b).
//
// ALL methods run only on the Unity main thread (VCManager.Update, the Harmony Update postfixes, and
// VoiceChatRoom.Update), so the static state needs no locking. VoiceDiagnostics.Log is itself thread-safe.
internal static class VoiceFrameProfiler
{
    // A frame whose wall-clock duration exceeds this is logged in full. ~33ms == below 30fps.
    private const double SlowFrameMs = 33.0;
    // ...also log any frame where our own voice code exceeds this budget, even if the frame wasn't slow.
    private const double VoiceBudgetMs = 8.0;
    private const double WindowSeconds = 5.0;

    private static bool Enabled => VoiceDiagnostics.IsEnabled;

    // ---- current-frame accumulation (main thread only) ----
    private static int _frame = -1;
    private static double _frameDeltaMs;
    private static long _heapAtStart;
    private static int _gc0AtStart, _gc1AtStart, _gc2AtStart;
    private static readonly Dictionary<string, double> _seg = new(24);

    // ---- rolling-window aggregation ----
    private static float _windowStart = float.NaN;
    private static int _windowSlowFrames;
    private static double _windowVcSum, _windowVcMax;
    private static long _windowHeapStart;
    private static int _windowGc0Start, _windowGc1Start, _windowGc2Start;
    private static readonly List<float> _windowFrametimes = new(512);
    private static readonly Dictionary<string, double> _windowSegSum = new(24);
    private static readonly Dictionary<string, double> _windowSegMax = new(24);

    // ---- reusable scratch for logging (main thread only) ----
    private static readonly List<KeyValuePair<string, double>> _sortScratch = new(24);

    // Start timing a segment. Returns 0 when profiling is disabled so End() is a no-op.
    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    public static void End(string segment, long startTicks)
    {
        if (!Enabled || startTicks == 0L) return;
        double ms = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
        EnsureFrame();
        _seg[segment] = (_seg.TryGetValue(segment, out var v) ? v : 0.0) + ms;
    }

    // Called once at the start of the per-frame voice update (VCManager.Update) so every frame gets a flush
    // even if it recorded no segments (e.g. an idle frame between voice ticks).
    public static void Tick()
    {
        if (!Enabled) return;
        EnsureFrame();
    }

    private static void EnsureFrame()
    {
        int f = SafeFrameCount();
        if (f == _frame) return;
        if (_frame >= 0) FlushFrame();   // the frame that just ended
        _frame = f;
        _seg.Clear();
        _frameDeltaMs = SafeUnscaledDeltaMs();
        _heapAtStart = SafeHeap();
        _gc0AtStart = SafeGc(0); _gc1AtStart = SafeGc(1); _gc2AtStart = SafeGc(2);
        if (float.IsNaN(_windowStart)) StartWindow();
    }

    private static void FlushFrame()
    {
        // Non-overlapping top-level main-thread cost: the VCManager tick plus the two game-driven Harmony
        // overlay postfixes. The hud/room.*/proximity entries are CHILDREN of vc.tick (breakdown only) and
        // are intentionally NOT summed here so vc is not double-counted.
        double vcTotal = Seg("vc.tick") + Seg("overlay.pingtracker") + Seg("overlay.meeting");

        long heapNow = SafeHeap();
        long heapDelta = heapNow - _heapAtStart;
        int g0 = SafeGc(0) - _gc0AtStart, g1 = SafeGc(1) - _gc1AtStart, g2 = SafeGc(2) - _gc2AtStart;
        bool gcCollected = g0 > 0 || g1 > 0 || g2 > 0 || heapDelta < 0;

        _windowVcSum += vcTotal;
        if (vcTotal > _windowVcMax) _windowVcMax = vcTotal;
        _windowFrametimes.Add((float)_frameDeltaMs);
        foreach (var kv in _seg)
        {
            _windowSegSum[kv.Key] = (_windowSegSum.TryGetValue(kv.Key, out var s) ? s : 0.0) + kv.Value;
            if (!_windowSegMax.TryGetValue(kv.Key, out var m) || kv.Value > m) _windowSegMax[kv.Key] = kv.Value;
        }

        bool slow = _frameDeltaMs >= SlowFrameMs || vcTotal >= VoiceBudgetMs || g1 > 0 || g2 > 0;
        if (slow)
        {
            _windowSlowFrames++;
            VoiceDiagnostics.Log("frame.slow",
                $"frame={_frame} dt={_frameDeltaMs:0.0}ms fps={(_frameDeltaMs > 0.0 ? 1000.0 / _frameDeltaMs : 0.0):0} " +
                $"vc={vcTotal:0.00}ms vcPct={(_frameDeltaMs > 0.0 ? 100.0 * vcTotal / _frameDeltaMs : 0.0):0} " +
                $"gc={(gcCollected ? "Y" : "n")} gc0={g0} gc1={g1} gc2={g2} " +
                $"heapMB={heapNow / 1048576.0:0.0} heapDeltaKB={heapDelta / 1024.0:0} " +
                $"{Context()} segs=[{DescribeSegments()}]");
        }

        float now = SafeTime();
        if (!float.IsNaN(_windowStart) && now - _windowStart >= WindowSeconds)
            FlushWindow(now, heapNow);
    }

    private static void FlushWindow(float now, long heapNow)
    {
        double secs = now - _windowStart;
        int n = _windowFrametimes.Count;
        if (n > 0) _windowFrametimes.Sort();

        double dtSum = 0.0;
        for (int i = 0; i < n; i++) dtSum += _windowFrametimes[i];
        double avgDt = n > 0 ? dtSum / n : 0.0;
        double p50 = Percentile(0.50, n), p95 = Percentile(0.95, n), p99 = Percentile(0.99, n);
        double dtMax = n > 0 ? _windowFrametimes[n - 1] : 0.0;
        double fps = avgDt > 0.0 ? 1000.0 / avgDt : 0.0;

        int wg0 = SafeGc(0) - _windowGc0Start, wg1 = SafeGc(1) - _windowGc1Start, wg2 = SafeGc(2) - _windowGc2Start;
        double minutes = secs / 60.0;
        double heapGrowthMbMin = minutes > 0.0 ? (heapNow - _windowHeapStart) / 1048576.0 / minutes : 0.0;

        VoiceDiagnostics.Log("frame.window",
            $"secs={secs:0.0} frames={n} fps={fps:0} dtP50={p50:0.0} dtP95={p95:0.0} dtP99={p99:0.0} dtMax={dtMax:0.0} " +
            $"slowFrames={_windowSlowFrames} vcAvg={(n > 0 ? _windowVcSum / n : 0.0):0.00} vcMax={_windowVcMax:0.00} " +
            $"gc0={wg0} gc1={wg1} gc2={wg2} heapMB={heapNow / 1048576.0:0.0} heapGrowthMBmin={heapGrowthMbMin:0.0} " +
            $"{Context()} top=[{DescribeWindowTop(n)}]");

        StartWindow();
    }

    private static void StartWindow()
    {
        _windowStart = SafeTime();
        _windowSlowFrames = 0;
        _windowVcSum = 0.0; _windowVcMax = 0.0;
        _windowFrametimes.Clear();
        _windowSegSum.Clear(); _windowSegMax.Clear();
        _windowHeapStart = SafeHeap();
        _windowGc0Start = SafeGc(0); _windowGc1Start = SafeGc(1); _windowGc2Start = SafeGc(2);
    }

    private static double Percentile(double q, int n)
    {
        if (n <= 0) return 0.0;
        int idx = (int)(q * n);
        if (idx >= n) idx = n - 1;
        return _windowFrametimes[idx];
    }

    private static double Seg(string key) => _seg.TryGetValue(key, out var v) ? v : 0.0;

    // Segments sorted by cost descending: "name=ms name=ms ...".
    private static string DescribeSegments()
    {
        _sortScratch.Clear();
        foreach (var kv in _seg) _sortScratch.Add(kv);
        _sortScratch.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = new System.Text.StringBuilder(_sortScratch.Count * 16);
        for (int i = 0; i < _sortScratch.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(_sortScratch[i].Key).Append('=').Append(_sortScratch[i].Value.ToString("0.00"));
        }
        return sb.ToString();
    }

    // Top window segments by average cost: "name(avg=..,max=..) ...".
    private static string DescribeWindowTop(int frames)
    {
        _sortScratch.Clear();
        foreach (var kv in _windowSegSum) _sortScratch.Add(kv);
        _sortScratch.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = new System.Text.StringBuilder(128);
        int shown = 0;
        for (int i = 0; i < _sortScratch.Count && shown < 5; i++, shown++)
        {
            var key = _sortScratch[i].Key;
            double avg = frames > 0 ? _sortScratch[i].Value / frames : 0.0;
            double max = _windowSegMax.TryGetValue(key, out var m) ? m : 0.0;
            if (shown > 0) sb.Append(' ');
            sb.Append(key).Append("(avg=").Append(avg.ToString("0.00")).Append(",max=").Append(max.ToString("0.00")).Append(')');
        }
        return sb.ToString();
    }

    private static string Context()
    {
        try
        {
            var room = VoiceChatRoom.Current;
            var snap = room?.CurrentSnapshot;
            string phase = snap != null ? snap.Phase.ToString() : "none";
            int players = snap?.Players?.Count ?? -1;
            int peers = room?.BackendPeerCount ?? -1;
            return $"phase={phase} players={players} peers={peers}";
        }
        catch
        {
            return "phase=? players=? peers=?";
        }
    }

    private static int SafeFrameCount() { try { return Time.frameCount; } catch { return _frame < 0 ? 0 : _frame; } }
    private static double SafeUnscaledDeltaMs() { try { return Time.unscaledDeltaTime * 1000.0; } catch { return 0.0; } }
    private static float SafeTime() { try { return Time.realtimeSinceStartup; } catch { return 0f; } }
    private static long SafeHeap() { try { return GC.GetTotalMemory(false); } catch { return 0L; } }
    private static int SafeGc(int gen) { try { return GC.CollectionCount(gen); } catch { return 0; } }
}
