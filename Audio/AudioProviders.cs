using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace VoiceChatPlugin.Audio;

internal class BufferedSampleProvider : ISampleProvider
{
    private const int RecoveryDecayReads = 25; // ~0.5 s per 40 ms step; fast, reversible decay toward the live setpoint

    private CircularFloatBuffer? _ring;
    private readonly object _stateLock = new();
    private readonly WaveFormat  _format;
    private int _prebufferSamples;
    private bool _isPrebuffering;
    private long _writtenSamples;
    private long _discardedSamples;
    private long _readRequests;
    private long _requestedSamples;
    private long _actualReadSamples;
    private long _underruns;
    private long _prebufferSilenceReads;
    private DateTime _lastBufferEventLogUtc = DateTime.MinValue;
    private DateTime _lastBufferWriteLogUtc = DateTime.MinValue;
    private bool _prebufferLogEmitted;
    private long _prebufferLogSuppressed;
    private DateTime _prebufferStateEnteredUtc = DateTime.MinValue;
    private bool _lastReadEndedSilent = true;
    private bool _hasPlayedAudio;
    private DateTime _prebufferFirstSampleUtc = DateTime.MinValue;
    private int _adaptiveRecoveryPrebufferSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
    private int _stableReadCycles;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    // Adaptive jitter-buffer DEPTH (Fix 2a). The setpoint is driven by a clean per-peer arrival-jitter
    // signal pulled from the upstream BclVoiceJitterBuffer; both the grow and release clamp sites share
    // the SAME reachable ceiling so growth is never trimmed and prebuffer-release can never stall.
    private int _maxAdaptivePrebufferSamples = AudioHelpers.PlaybackMaxRecoveryPrebufferSamples;
    private int _jitterSetpointSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
    // Per-peer link-aware ceiling escalation (P0.2). Only THIS peer's ceiling deepens toward 240 ms, and only
    // when its UNCLAMPED jitter target stays pinned at/above the current ceiling for a sustained streak. The cut
    // sizes (BufferCutToSize/BufferCutSize) are recomputed in lockstep so the ring actually lets the deeper
    // cushion fill instead of discarding everything above the old 180 ms trim. Decay lowers the ceiling too.
    // SEMANTICS: this counts CLAMPED RecomputeSetpointLocked calls accrued WITHIN a talkspurt (each underrun
    // recomputes the setpoint, so in practice it counts clamped underruns), NOT strictly-consecutive recompute
    // calls. A single recompute whose unclamped target falls BELOW the ceiling clears it (the link is comfortably
    // under), as does an idle-reset / Clear / PrebufferSamples set (end-of-talk pressure is dropped). So it
    // reflects clamped-underruns-within-a-talkspurt, cleared on an unclamped recompute or on idle-reset.
    private int _clampStreak;
    private Func<double>? _jitterSamplesProvider;   // per-peer: () => jitterBuffer.CurrentJitterSamples
    // Trailing-stall PLC bridge (Fix 2b-3): on a recent-activity underrun, write up to MaxTrailingPlcFrames
    // of real decoder PLC into the ring to bridge while the deeper cushion refills. Trim-bounded + gated.
    private Func<float[]?>? _plcFrameProvider;       // per-peer: () => one Opus-PLC frame (or null)
    private int _trailingPlcEmitted;
    private const int MaxTrailingPlcFrames = 3;      // ~60 ms bridge, then allow true silence
    private const int DiscontinuityFadeSamples = 240;
    private bool _pendingFadeClear;
    private int _resumeFadeRemaining;
    private long _fadeClears;
    private long _resumeFades;
    // Reused scratch for tapering the bridge frame ACROSS emissions (the provider returns the SAME array up to
    // MaxTrailingPlcFrames times). Scaling in place into this buffer keeps the bridge allocation-free while it
    // fades 1.0 -> 0.66 -> 0.33 toward silence instead of plateauing at the provider's ~0.5 gain (buzz).
    private float[] _plcTaperScratch = System.Array.Empty<float>();
    // After this much wall-clock with no writes the talker has stopped (end-of-utterance / channel-reopen),
    // not merely jittered, so any escalated recovery prebuffer is snapped back to baseline to keep the next
    // talkspurt's onset latency low. It is comfortably longer than the cushion-drain-to-underrun time, so a
    // mid-utterance jitter stall (which keeps writing within the window) still escalates normally.
    private static readonly TimeSpan IdleRecoveryResetWindow = TimeSpan.FromMilliseconds(200);

    public bool ReadFully              { get; set; } = true;
    public bool EnableRecoveryPrebuffer { get; set; } = true;
    public int  BufferLength           { get; set; }
    public int  BufferCutSize          { get; set; } = int.MaxValue;
    public int  BufferCutToSize        { get; set; } = int.MaxValue;
    public bool DiscardOnBufferOverflow{ get; set; }
    public int DebugGroupId            { get; set; } = -1;
    public int PrebufferSamples
    {
        get { lock (_stateLock) return _prebufferSamples; }
        set
        {
            lock (_stateLock)
            {
                _prebufferSamples = Math.Max(0, value);
                _isPrebuffering = EnableRecoveryPrebuffer && _prebufferSamples > 0;
                ResetPrebufferLogStateLocked();
                _adaptiveRecoveryPrebufferSamples = EnableRecoveryPrebuffer ? AudioHelpers.PlaybackRecoveryPrebufferSamples : 0;
                _stableReadCycles = 0;
                _jitterSetpointSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
                _trailingPlcEmitted = 0;
                _clampStreak = 0;
            }
        }
    }

    public WaveFormat WaveFormat  => _format;
    public int  BufferedSamples   { get { lock (_stateLock) return _ring?.Count ?? 0; } }
    internal int CurrentRecoveryPrebufferSamples { get { lock (_stateLock) return _adaptiveRecoveryPrebufferSamples; } }
    // Legacy name kept for compatibility
    public int  BufferedBytes     => BufferedSamples;

    // The deep adaptive escalation ceiling (Fix 2a). Clamped to never drop below the 60 ms baseline. Setting it
    // also re-derives the per-peer cut sizes in lockstep (P0.2) so the ring trim always tracks the live ceiling.
    public int MaxAdaptivePrebufferSamples
    {
        get { lock (_stateLock) return _maxAdaptivePrebufferSamples; }
        set
        {
            lock (_stateLock)
            {
                _maxAdaptivePrebufferSamples = Math.Max(AudioHelpers.PlaybackRecoveryPrebufferSamples, value);
                RecomputePerPeerCutSizesLocked();
            }
        }
    }

    // Per-peer mutable cut sizes (P0.2): re-derive BufferCutToSize / BufferCutSize from the LIVE per-peer ceiling
    // so the ring lets the deeper cushion fill instead of discarding ring content above a fixed 180 ms trim. The
    // AddSamplesLocked discard and the PLC-bridge headroom gate both read BufferCutToSize, so they move with it
    // automatically. Preserves the ordering invariant target <= reachable < BufferCutToSize < BufferCutSize < ring.
    // Only active once cut sizes were initialised to real values (BufferLength > 0 and a finite cut); a manager
    // built with no ring trim (cut == int.MaxValue) is left untouched.
    private void RecomputePerPeerCutSizesLocked()
    {
        if (BufferCutToSize == int.MaxValue || BufferLength <= 0)
            return; // no ring trim configured (e.g. bufMax <= bufLen) — leave the unbounded cut alone
        int ringMax = BufferLength;
        // reachable = the deepest cushion the ring can physically hold, bounded by the live per-peer ceiling.
        // Leave 2 FULL FRAMES of ring headroom: BufferCutSize is reachable + 2*FrameSize, so capping reachable at
        // ringMax - 2*FrameSize keeps BufferCutSize strictly under the ring (and BufferCutToSize a frame below it)
        // REGARDLESS of the ceiling/ring constants — the target <= reachable < BufferCutToSize < BufferCutSize <
        // ring ordering invariant no longer depends on the shipped 14400/9600 sizing.
        int reachable = Math.Min(ringMax - AudioHelpers.FrameSize * 2, _maxAdaptivePrebufferSamples);
        BufferCutToSize = reachable + AudioHelpers.FrameSize;                                   // 1 frame above reachable
        BufferCutSize   = Math.Min(ringMax - 1, reachable + AudioHelpers.FrameSize * 2);        // 2 frames above reachable, < ring
    }

    // Per-peer ceiling escalation (P0.2). When the unclamped jitter target has stayed pinned at/above the current
    // ceiling for a sustained streak, ratchet THIS peer's ceiling one frame-step toward the 240 ms hard cap and
    // move the cut sizes with it. Returns true when the ceiling actually grew (caller resets the streak).
    private bool TryGrowPerPeerCeilingLocked()
    {
        int next = AudioHelpers.NextPeerCeilingOnGrow(_maxAdaptivePrebufferSamples, _clampStreak);
        if (next <= _maxAdaptivePrebufferSamples)
            return false;
        _maxAdaptivePrebufferSamples = next;
        RecomputePerPeerCutSizesLocked();
        return true;
    }

    // Per-peer ceiling decay (P0.2): lower the ceiling one frame-step toward the 160 ms start (never below it) so
    // a one-time bad spell does not strand the peer at 240 ms forever. Cut sizes follow.
    private void LowerPerPeerCeilingLocked()
    {
        int next = AudioHelpers.NextPeerCeilingOnDecay(_maxAdaptivePrebufferSamples);
        if (next >= _maxAdaptivePrebufferSamples)
            return;
        _maxAdaptivePrebufferSamples = next;
        RecomputePerPeerCutSizesLocked();
    }

    // Per-peer wiring (Fix 2a-3 / 2b-3). The ring pulls the clean jitter signal and a PLC bridge frame
    // through these callbacks under its own _stateLock.
    public void SetJitterSamplesProvider(Func<double> provider) { lock (_stateLock) _jitterSamplesProvider = provider; }
    public void SetPlcFrameProvider(Func<float[]?> provider) { lock (_stateLock) _plcFrameProvider = provider; }

    public BufferedSampleProvider(WaveFormat waveFormat, int? bufferLength = null)
    {
        _format      = waveFormat;
        BufferLength = bufferLength ?? waveFormat.SampleRate * waveFormat.Channels * 8;
    }

    public void AddSamples(float[] buffer, int offset, int count)
    {
        lock (_stateLock)
            AddSamplesLocked(buffer, offset, count);
    }

    private void AddSamplesLocked(float[] buffer, int offset, int count)
    {
        _ring ??= new CircularFloatBuffer(BufferLength);

        if (DiscardOnBufferOverflow && count > _ring.MaxLength)
        {
            System.Threading.Interlocked.Add(ref _discardedSamples, count - _ring.MaxLength);
            LogBufferEvent("audio.buffer.discard", $"reason=oversized incoming={count} max={_ring.MaxLength} buffered={_ring.Count}");
            offset += count - _ring.MaxLength;
            count = _ring.MaxLength;
        }

        if (DiscardOnBufferOverflow && _ring.Count + count > _ring.MaxLength)
        {
            int discard = _ring.Count + count - _ring.MaxLength;
            _ring.Discard(discard);
            _resumeFadeRemaining = DiscontinuityFadeSamples;
            System.Threading.Interlocked.Increment(ref _resumeFades);
            System.Threading.Interlocked.Add(ref _discardedSamples, discard);
            LogBufferEvent("audio.buffer.discard", $"reason=overflow discard={discard} incoming={count} buffered={_ring.Count} max={_ring.MaxLength}");
        }

        int beforeWrite = _ring.Count;
        int written = _ring.Write(buffer, offset, count);
        System.Threading.Interlocked.Add(ref _writtenSamples, written);
        if (written > 0) _lastWriteUtc = DateTime.UtcNow;
        int afterWrite = _ring.Count;
        if (_isPrebuffering && beforeWrite == 0 && afterWrite > 0)
            _prebufferFirstSampleUtc = DateTime.UtcNow;
        LogBufferWrite(beforeWrite, afterWrite, written, count);
        if (written < count && !DiscardOnBufferOverflow)
            throw new InvalidOperationException("Buffer full");
        if (_ring.Count > BufferCutSize && BufferCutSize > BufferCutToSize)
        {
            _ring.Discard(_ring.Count - BufferCutToSize);
            _resumeFadeRemaining = DiscontinuityFadeSamples;
            System.Threading.Interlocked.Increment(ref _resumeFades);
        }
        if (EnableRecoveryPrebuffer && _prebufferSamples > 0 && _isPrebuffering)
        {
            int target = GetPrebufferTargetLocked();
            if (_ring.Count < target) return;

            _isPrebuffering = false;
            _prebufferFirstSampleUtc = DateTime.MinValue;
            LogBufferEvent("audio.buffer.prebufferRelease", $"reason=target-reached buffered={_ring.Count} target={target} startup={!_hasPlayedAudio} suppressed={_prebufferLogSuppressed} waitedMs={PrebufferWaitedMsLocked()}");
        }
    }

    public void Clear()
    {
        lock (_stateLock)
            ClearLocked();
    }

    public void RequestFadeClear()
    {
        lock (_stateLock)
        {
            if (_ring == null || _ring.Count == 0)
            {
                ClearLocked();
                return;
            }
            _pendingFadeClear = true;
        }
    }

    public void DiscardBufferedSamples(int count)
    {
        if (count <= 0) return;
        lock (_stateLock)
        {
            if (_ring == null || _ring.Count == 0) return;
            int discard = Math.Min(count, _ring.Count);
            _ring.Discard(discard);
            _resumeFadeRemaining = DiscontinuityFadeSamples;
            System.Threading.Interlocked.Increment(ref _resumeFades);
            System.Threading.Interlocked.Add(ref _discardedSamples, discard);
        }
    }

    private void ClearLocked()
    {
        _ring?.Reset();
        _isPrebuffering = EnableRecoveryPrebuffer && _prebufferSamples > 0;
        ResetPrebufferLogStateLocked();
        _lastReadEndedSilent = true;
        _hasPlayedAudio = false;
        _prebufferFirstSampleUtc = DateTime.MinValue;
        _adaptiveRecoveryPrebufferSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
        _stableReadCycles = 0;
        _lastWriteUtc = DateTime.MinValue;
        _jitterSetpointSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
        _trailingPlcEmitted = 0;
        _clampStreak = 0;
        _pendingFadeClear = false;
        _resumeFadeRemaining = DiscontinuityFadeSamples;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_stateLock)
            return ReadLocked(buffer, offset, count);
    }

    private int ReadLocked(float[] buffer, int offset, int count)
    {
        if (_pendingFadeClear)
        {
            _pendingFadeClear = false;
            int faded = 0;
            if (_ring != null && _ring.Count > 0)
            {
                faded = _ring.Read(buffer, offset, Math.Min(count, DiscontinuityFadeSamples));
                for (int i = 0; i < faded; i++)
                    buffer[offset + i] *= 1f - (i + 1f) / faded;
            }
            System.Threading.Interlocked.Increment(ref _fadeClears);
            if (VoiceChatPlugin.VoiceChat.VoiceDiagnostics.IsEnabled)
                WriteBufferEvent("audio.buffer.fadeclear", $"faded={faded} requested={count}");
            ClearLocked();
            System.Threading.Interlocked.Increment(ref _readRequests);
            System.Threading.Interlocked.Add(ref _requestedSamples, count);
            System.Threading.Interlocked.Add(ref _actualReadSamples, faded);
            if (!ReadFully) return faded;
            Array.Clear(buffer, offset + faded, count - faded);
            return count;
        }

        // End-of-talk / reopen: once the talker has been silent past the idle window, drop the escalated
        // recovery prebuffer back to baseline so the next talkspurt resumes at minimal onset latency. A
        // mid-utterance jitter stall keeps writing within the window, so its escalation is preserved.
        if (EnableRecoveryPrebuffer
            && _adaptiveRecoveryPrebufferSamples > AudioHelpers.PlaybackRecoveryPrebufferSamples
            && _lastWriteUtc != DateTime.MinValue
            && (DateTime.UtcNow - _lastWriteUtc) >= IdleRecoveryResetWindow)
        {
            _adaptiveRecoveryPrebufferSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
            _stableReadCycles = 0;
            // Decay the trailing-PLC counter + jitter setpoint with the depth so the next talkspurt
            // starts at the 60 ms baseline (Fix 2c-1).
            _trailingPlcEmitted = 0;
            _jitterSetpointSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
            // The per-peer escalation streak is end-of-talk pressure; clear it so the next talkspurt starts fresh.
            // The per-peer CEILING itself is deliberately NOT snapped here — it is a slow link-quality signal that
            // decays one frame-step at a time via DecayRecoveryPrebufferLocked, so a genuinely jittery link keeps
            // its earned depth across short talkspurt gaps and only relaxes over a sustained healthy run (P0.2).
            _clampStreak = 0;
            // Active drain (Fix 2c-2): the talker has provably stopped, so reclaim any queued latency that
            // grew during the prior utterance down to baseline so the next onset is not pushed later.
            if (_ring != null && _ring.Count > AudioHelpers.PlaybackRecoveryPrebufferSamples)
            {
                int drain = _ring.Count - AudioHelpers.PlaybackRecoveryPrebufferSamples;
                _ring.Discard(drain);
                _resumeFadeRemaining = DiscontinuityFadeSamples;
                System.Threading.Interlocked.Increment(ref _resumeFades);
                System.Threading.Interlocked.Add(ref _discardedSamples, drain);
            }
        }

        if (_ring == null)
        {
            System.Threading.Interlocked.Increment(ref _readRequests);
            System.Threading.Interlocked.Add(ref _requestedSamples, count);
            System.Threading.Interlocked.Increment(ref _prebufferSilenceReads);
            if (!_prebufferLogEmitted && ShouldLogBufferEvent())
            {
                WriteBufferEvent("audio.buffer.prebuffer", $"requested={count} buffered={_ring?.Count ?? 0} prebuffer={_prebufferSamples}");
                _prebufferLogEmitted = true;
            }
            else
                _prebufferLogSuppressed++;
            return CompleteRead(buffer, offset, count, 0);
        }

        if (EnableRecoveryPrebuffer && _prebufferSamples > 0 && _isPrebuffering)
        {
            int buffered = _ring.Count;
            int target = GetPrebufferTargetLocked();
            // Scale the wait-cap with the current adaptive target (Fix 2a-5) so a deep cushion can actually
            // fill instead of releasing under-filled at the fixed 180 ms and oscillating on the worst peers.
            int targetMs = (int)(target / (AudioHelpers.ClockRate / 1000.0));
            int waitCapMs = Math.Max(AudioHelpers.PlaybackMaxPrebufferWaitMilliseconds, targetMs + 40);
            bool waitExpired = buffered > 0 &&
                _prebufferFirstSampleUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _prebufferFirstSampleUtc).TotalMilliseconds >= waitCapMs;
            if (buffered < target && !waitExpired)
            {
                System.Threading.Interlocked.Increment(ref _readRequests);
                System.Threading.Interlocked.Add(ref _requestedSamples, count);
                System.Threading.Interlocked.Increment(ref _prebufferSilenceReads);
                if (!_prebufferLogEmitted && ShouldLogBufferEvent())
                {
                    WriteBufferEvent("audio.buffer.prebuffer", $"requested={count} buffered={buffered} prebuffer={target} startup={!_hasPlayedAudio}");
                    _prebufferLogEmitted = true;
                }
                else
                    _prebufferLogSuppressed++;
                return CompleteRead(buffer, offset, count, 0);
            }

            _isPrebuffering = false;
            _prebufferFirstSampleUtc = DateTime.MinValue;
            LogBufferEvent("audio.buffer.prebufferRelease", $"requested={count} buffered={buffered} target={target} waitExpired={waitExpired} startup={!_hasPlayedAudio} suppressed={_prebufferLogSuppressed} waitedMs={PrebufferWaitedMsLocked()}");
        }

        System.Threading.Interlocked.Increment(ref _readRequests);
        System.Threading.Interlocked.Add(ref _requestedSamples, count);
        int bufferedBeforeRead = _ring.Count;
        int num = _ring.Read(buffer, offset, count);
        System.Threading.Interlocked.Add(ref _actualReadSamples, num);
        if (num < count)
        {
            System.Threading.Interlocked.Increment(ref _underruns);
            // Trailing-stall concealment bridge (Fix 2b-3): on a recent-activity underrun (mid-utterance, not
            // end-of-talk), fill the unsatisfied tail of THIS read with up to MaxTrailingPlcFrames of the
            // per-peer bridge frame. Written straight to the output, never into the ring, so the L/R twin rings
            // keep an identical buffered depth (a one-sided ring write skews the stereo image non-directional).
            bool recentlyActive = _lastWriteUtc != DateTime.MinValue &&
                                  (DateTime.UtcNow - _lastWriteUtc) < IdleRecoveryResetWindow;
            if (EnableRecoveryPrebuffer && recentlyActive
                && _trailingPlcEmitted < MaxTrailingPlcFrames
                && _ring != null && _ring.Count + AudioHelpers.FrameSize <= BufferCutToSize
                && _plcFrameProvider != null)
            {
                var plc = _plcFrameProvider();
                if (plc != null && plc.Length >= AudioHelpers.FrameSize)
                {
                    // Taper toward silence ACROSS emissions (the provider hands back the SAME array each call):
                    // emission 0 -> 1.0, 1 -> 0.66, 2 -> 0.33. Scale in place into a reused scratch buffer so a
                    // multi-frame held bridge fades out instead of plateauing at the provider's ~0.5 gain (buzz).
                    if (_plcTaperScratch.Length < AudioHelpers.FrameSize)
                        _plcTaperScratch = new float[AudioHelpers.FrameSize];
                    float gain = 1f - (float)_trailingPlcEmitted / MaxTrailingPlcFrames;
                    for (int s = 0; s < AudioHelpers.FrameSize; s++)
                        _plcTaperScratch[s] = plc[s] * gain;
                    int fill = Math.Min(count - num, AudioHelpers.FrameSize);
                    Array.Copy(_plcTaperScratch, 0, buffer, offset + num, fill);
                    _trailingPlcEmitted++;
                    System.Threading.Interlocked.Add(ref _actualReadSamples, fill);
                    num += fill;
                }
            }
            if (EnableRecoveryPrebuffer && _prebufferSamples > 0)
            {
                // Only a mid-utterance stall deepens the cushion; a benign end-of-talk drain must not escalate.
                if (recentlyActive)
                    IncreaseRecoveryPrebufferLocked();
                _isPrebuffering = true;
                _prebufferFirstSampleUtc = (_ring?.Count ?? 0) > 0 ? DateTime.UtcNow : DateTime.MinValue;
                ResetPrebufferLogStateLocked();
            }
            LogBufferEvent("audio.buffer.underrun",
                $"requested={count} actual={num} bufferedBefore={bufferedBeforeRead} buffered={_ring?.Count ?? 0} prebuffer={_prebufferSamples} recovery={_adaptiveRecoveryPrebufferSamples} jitter={_jitterSetpointSamples} ceiling={ReachableCeilingLocked()} cap={_maxAdaptivePrebufferSamples} cutTo={BufferCutToSize} hasPlayed={_hasPlayedAudio} readEndedSilent={_lastReadEndedSilent}");
        }
        else if (EnableRecoveryPrebuffer && _prebufferSamples > 0 && num == count)
        {
            _trailingPlcEmitted = 0; // a clean full read ends the bridge
            DecayRecoveryPrebufferLocked();
        }
        if (_resumeFadeRemaining > 0 && num > 0)
        {
            for (int i = 0; i < num && _resumeFadeRemaining > 0; i++, _resumeFadeRemaining--)
                buffer[offset + i] *= 1f - (float)_resumeFadeRemaining / DiscontinuityFadeSamples;
        }
        if (num > 0 && num < count)
        {
            int fade = Math.Min(num, DiscontinuityFadeSamples);
            for (int i = 0; i < fade; i++)
                buffer[offset + num - fade + i] *= 1f - (i + 1f) / fade;
            _resumeFadeRemaining = DiscontinuityFadeSamples;
            System.Threading.Interlocked.Increment(ref _resumeFades);
        }
        return CompleteRead(buffer, offset, count, num);
    }

    // The single reachable ceiling shared by BOTH clamp sites (grow + release) and bounded by the ring trim,
    // so depth growth is never trimmed away and prebuffer-release can never stall (Fix 2a-4).
    private int ReachableCeilingLocked()
    {
        int cap = BufferCutToSize == int.MaxValue
            ? _maxAdaptivePrebufferSamples
            : Math.Min(BufferCutToSize, _maxAdaptivePrebufferSamples);
        return Math.Max(AudioHelpers.PlaybackRecoveryPrebufferSamples, cap);
    }

    private int ComputeUnclampedTargetLocked()
    {
        double j = _jitterSamplesProvider?.Invoke() ?? 0.0;
        return AudioHelpers.PlaybackRecoveryPrebufferSamples
               + (int)(AudioHelpers.JitterGain * j)
               + AudioHelpers.JitterDepthMarginSamples;
    }

    private void RefreshJitterSetpointLocked()
    {
        int unclampedTarget = ComputeUnclampedTargetLocked();
        _jitterSetpointSamples = Math.Clamp(unclampedTarget,
            AudioHelpers.PlaybackRecoveryPrebufferSamples, ReachableCeilingLocked());
    }

    // Pull the clean per-peer jitter signal and translate it into a depth setpoint (Fix 2a-3). Also drives the
    // per-peer link-aware ceiling escalation (P0.2): the UNCLAMPED target (before clamping to the ceiling) is what
    // reveals that the link genuinely wants more depth than the ceiling allows. A sustained streak of clamped
    // recomputes (this method runs on every underrun, so the streak counts clamped underruns accrued within a
    // talkspurt) ratchets THIS peer's ceiling one frame-step toward the hard cap; a recompute that is NOT clamped
    // decays the streak by one (floored at 0), so a single transient spike is not enough to deepen a healthy peer. Idle-reset also clears it.
    private void RecomputeSetpointLocked()
    {
        int unclampedTarget = ComputeUnclampedTargetLocked();

        // P0.2: track whether the link wants more than the current per-peer ceiling, then ratchet up on a streak.
        // Decay the streak on a below-ceiling sample instead of hard-resetting it (NextClampStreak), so oscillating
        // jitter still accrues toward the grow threshold instead of being perpetually wiped (the prior dead path).
        // ESCALATION-ONLY: call this from the underrun/decay paths, NEVER from the prebuffer probe path (use
        // RefreshJitterSetpointLocked there) — the clamp streak must count clamped UNDERRUNS, not poll ticks.
        int ceiling = ReachableCeilingLocked();
        bool clamped = AudioHelpers.PeerCeilingIsClamped(unclampedTarget, ceiling);
        _clampStreak = AudioHelpers.NextClampStreak(_clampStreak, clamped);
        if (clamped && TryGrowPerPeerCeilingLocked())
            _clampStreak = 0; // grew one frame-step; re-arm the streak for the next step toward the hard cap

        _jitterSetpointSamples = Math.Clamp(unclampedTarget,
            AudioHelpers.PlaybackRecoveryPrebufferSamples, ReachableCeilingLocked());
    }

    private int GetPrebufferTargetLocked()
    {
        if (!_hasPlayedAudio)
            return _prebufferSamples; // startup onset hold UNCHANGED

        // Proactive prefill: refresh the depth setpoint from live per-peer jitter while prebuffering so a jittery
        // peer fills the cushion to its measured depth, instead of only deepening AFTER an underrun. This is the
        // side-effect-free refresh (no clamp-streak/ceiling mutation) — escalation stays on the underrun path.
        RefreshJitterSetpointLocked();
        int ceiling = ReachableCeilingLocked(); // SAME ceiling as Increase — both move together
        int target = Math.Max(_adaptiveRecoveryPrebufferSamples, _jitterSetpointSamples);
        return Math.Clamp(target,
            AudioHelpers.PlaybackRecoveryPrebufferSamples, ceiling);
    }

    // Test-only seam: exercise the proactive prebuffer target under the state lock without a live audio thread.
    internal int GetPrebufferTargetForTest()
    {
        lock (_stateLock) return GetPrebufferTargetLocked();
    }

    private void IncreaseRecoveryPrebufferLocked()
    {
        _stableReadCycles = 0;
        RecomputeSetpointLocked();              // pull the latest clean per-peer jitter
        // Bursty-link auto-deepen: a mid-utterance underrun ratchets the ceiling toward the hard cap on the real dropout signal, so a clumped WiFi/relay link earns depth even when its low mean jitter never clamps.
        if (_maxAdaptivePrebufferSamples < AudioHelpers.PlaybackMaxRecoveryPrebufferSamplesHard)
        {
            _maxAdaptivePrebufferSamples = Math.Min(
                _maxAdaptivePrebufferSamples + AudioHelpers.FrameSize,
                AudioHelpers.PlaybackMaxRecoveryPrebufferSamplesHard);
            RecomputePerPeerCutSizesLocked();
        }
        int ceiling = ReachableCeilingLocked();
        // On starvation JUMP straight to the measured-jitter setpoint (cover real variance in one step),
        // floored at +2 frames so a not-yet-measured peer still deepens.
        int jump = Math.Max(_jitterSetpointSamples,
                            _adaptiveRecoveryPrebufferSamples + AudioHelpers.RecoveryGrowFloorSamples);
        _adaptiveRecoveryPrebufferSamples = Math.Clamp(jump,
            AudioHelpers.PlaybackRecoveryPrebufferSamples, ceiling);
    }

    private void DecayRecoveryPrebufferLocked()
    {
        // Never decay below what the live jitter setpoint still demands (Fix 2c-3).
        int floor = Math.Max(AudioHelpers.PlaybackRecoveryPrebufferSamples, _jitterSetpointSamples);
        if (_adaptiveRecoveryPrebufferSamples <= floor)
        {
            // Recovery is fully settled at its floor: the link is healthy. If THIS peer's ceiling was ratcheted up
            // toward 240 ms by a prior bad spell, walk it back down one frame-step toward the 160 ms start so a
            // one-time bad spell does not strand it at 240 ms forever (P0.2). Gated on the live jitter no longer
            // wanting the elevated ceiling, and on a sustained run of clean reads, so it never thrashes a peer that
            // still needs the depth. Pull the latest jitter first so the decision tracks current conditions.
            if (_maxAdaptivePrebufferSamples > AudioHelpers.PlaybackMaxRecoveryPrebufferSamples)
            {
                RecomputeSetpointLocked(); // refresh _jitterSetpointSamples + clamp streak from the live signal
                // Lower the ceiling whenever the stream is settled (no clamp pressure — _clampStreak==0 means the
                // latest recompute was NOT clamped at the ceiling) AND the live setpoint is comfortably below the
                // current per-peer cap, i.e. the cap now carries more headroom than the link needs. Earlier this
                // only fired when the setpoint fell below the 160 ms START, so a peer that settled steady at e.g.
                // ~170 ms kept its earned 240 ms cap forever. One frame-step per decay tick, monotone toward the
                // 160 ms floor (NextPeerCeilingOnDecay never goes below it), so it can't oscillate against grow.
                if (_clampStreak == 0
                    && _jitterSetpointSamples <= _maxAdaptivePrebufferSamples - AudioHelpers.FrameSize)
                {
                    _stableReadCycles++;
                    if (_stableReadCycles >= RecoveryDecayReads)
                    {
                        _stableReadCycles = 0;
                        LowerPerPeerCeilingLocked();
                    }
                }
            }
            return;
        }

        _stableReadCycles++;
        if (_stableReadCycles < RecoveryDecayReads)
            return;

        _stableReadCycles = 0;
        _adaptiveRecoveryPrebufferSamples = Math.Max(floor,
            _adaptiveRecoveryPrebufferSamples - (AudioHelpers.FrameSize * 2)); // faster, reversible -40 ms step
    }

    public string ConsumeDebugStats()
        => $"written={System.Threading.Interlocked.Exchange(ref _writtenSamples, 0)} " +
           $"discarded={System.Threading.Interlocked.Exchange(ref _discardedSamples, 0)} " +
           $"readCalls={System.Threading.Interlocked.Exchange(ref _readRequests, 0)} " +
           $"requested={System.Threading.Interlocked.Exchange(ref _requestedSamples, 0)} " +
           $"actual={System.Threading.Interlocked.Exchange(ref _actualReadSamples, 0)} " +
           $"underruns={System.Threading.Interlocked.Exchange(ref _underruns, 0)} " +
           $"prebufferSilence={System.Threading.Interlocked.Exchange(ref _prebufferSilenceReads, 0)} " +
           $"fadeClears={System.Threading.Interlocked.Exchange(ref _fadeClears, 0)} " +
           $"resumeFades={System.Threading.Interlocked.Exchange(ref _resumeFades, 0)}";

    private int CompleteRead(float[] buffer, int offset, int count, int num)
    {
        if (num > 0)
        {
            _lastReadEndedSilent = false;
            _hasPlayedAudio = true;
        }

        if (ReadFully && num < count)
        {
            int missing = count - num;
            Array.Clear(buffer, offset + num, missing);
            num = count;
            _lastReadEndedSilent = true;
        }
        return num;
    }

    private bool ShouldLogBufferEvent()
    {
        if (!VoiceChatPlugin.VoiceChat.VoiceDiagnostics.IsEnabled)
            return false;

        var now = DateTime.UtcNow;
        if ((now - _lastBufferEventLogUtc).TotalSeconds < 0.5)
            return false;

        _lastBufferEventLogUtc = now;
        return true;
    }

    private void WriteBufferEvent(string category, string message)
        => VoiceChatPlugin.VoiceChat.VoiceDiagnostics.Log(category, $"group={DebugGroupId} {message}");

    private bool LogBufferEvent(string category, string message)
    {
        if (!ShouldLogBufferEvent())
            return false;

        WriteBufferEvent(category, message);
        return true;
    }

    private void ResetPrebufferLogStateLocked()
    {
        _prebufferLogEmitted = false;
        _prebufferLogSuppressed = 0;
        _prebufferStateEnteredUtc = _isPrebuffering ? DateTime.UtcNow : DateTime.MinValue;
    }

    private int PrebufferWaitedMsLocked()
        => _prebufferStateEnteredUtc == DateTime.MinValue
            ? 0
            : (int)(DateTime.UtcNow - _prebufferStateEnteredUtc).TotalMilliseconds;

    private void LogBufferWrite(int before, int after, int written, int requested)
    {
        bool important = before == 0 || before < _prebufferSamples || after < _prebufferSamples;
        if (!important) return;

        var now = DateTime.UtcNow;
        if ((now - _lastBufferWriteLogUtc).TotalSeconds < 0.25)
            return;

        _lastBufferWriteLogUtc = now;
        VoiceChatPlugin.VoiceChat.VoiceDiagnostics.Log("audio.buffer.write",
            $"group={DebugGroupId} before={before} after={after} written={written} requested={requested} prebuffer={_prebufferSamples} readEndedSilent={_lastReadEndedSilent}");
    }
}

internal class MonoToStereoSampleProvider : ISampleProvider
{
    private static readonly WaveFormat _fmt =
        WaveFormat.CreateIeeeFloatWaveFormat(AudioHelpers.ClockRate, 2);

    private readonly ISampleProvider _src;
    private float[] _mono = Array.Empty<float>();

    public WaveFormat WaveFormat => _fmt;
    public MonoToStereoSampleProvider(ISampleProvider mono) => _src = mono;

    public int Read(float[] buffer, int offset, int count)
    {
        int monoCount = count / 2;
        if (_mono.Length < monoCount)
            _mono = new float[monoCount];

        int read = _src.Read(_mono, 0, monoCount);

        for (int i = 0; i < read; i++)
        {
            buffer[offset + i * 2]     = _mono[i];
            buffer[offset + i * 2 + 1] = _mono[i];
        }

        if (read < monoCount)
            Array.Clear(buffer, offset + read * 2, (monoCount - read) * 2);

        // Odd count: zero the unpaired tail; report full count on a filled source so NAudio sees no short read.
        int produced = read * 2;
        if ((count & 1) == 1)
        {
            buffer[offset + count - 1] = 0f;
            if (read == monoCount)
                produced = count;
        }

        return produced;
    }
}

internal class StereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    private float[] _temp = Array.Empty<float>();
    private readonly object _lock = new();
    private float _pan;
    private float _volume = 1f;

    public WaveFormat WaveFormat { get; }
    public float Volume
    {
        get { lock (_lock) return _volume; }
        set { lock (_lock) _volume = Math.Clamp(value, 0f, 4f); }
    }

    public float Pan
    {
        get { lock (_lock) return _pan; }
        set { lock (_lock) _pan = Math.Clamp(value, -1f, 1f); }
    }

    public StereoSampleProvider(ISampleProvider src)
    {
        _src       = src;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int monoCount = count / 2;
        if (_temp.Length < monoCount) _temp = new float[monoCount];

        int read = _src.Read(_temp, 0, monoCount);

        float pan;
        float volume;
        lock (_lock)
        {
            pan = _pan;
            volume = _volume;
        }

        double angle = (pan + 1.0) * 0.25 * Math.PI;
        float leftGain = (float)(Math.Cos(angle) * volume);
        float rightGain = (float)(Math.Sin(angle) * volume);

        for (int i = 0; i < read; i++)
        {
            float sample = _temp[i];
            buffer[offset + i * 2] = sample * leftGain;
            buffer[offset + i * 2 + 1] = sample * rightGain;
        }

        if (read < monoCount)
            Array.Clear(buffer, offset + read * 2, (monoCount - read) * 2);

        return read * 2;
    }
}

internal class ReverbSampleProvider : ISampleProvider
{
    private const float FeedbackDamping = 0.35f;
    private const float DenormalGuard = 1e-20f;

    private readonly ISampleProvider _src;
    private readonly float[] _delay;
    private readonly float[] _feedbackLp;
    private int   _pos;
    private float _decay, _wet, _dry;

    public float Decay      { get => _decay; set => _decay = Math.Clamp(value, 0f, 1f); }
    public float WetDryMix  { get => _wet;   set { _wet = Math.Clamp(value, 0f, 1f); _dry = 1f - _wet; } }
    public WaveFormat WaveFormat => _src.WaveFormat;

    public ReverbSampleProvider(ISampleProvider src, int delayMs, float decay, float wetDry)
    {
        _src   = src;
        int n  = (int)(src.WaveFormat.SampleRate * (delayMs / 1000f)) * src.WaveFormat.Channels;
        _delay = new float[Math.Max(1, n)]; // avoid zero-length line (modulo-by-zero on _delay[_pos])
        _feedbackLp = new float[Math.Max(1, src.WaveFormat.Channels)];
        Decay     = decay;
        WetDryMix = wetDry;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _src.Read(buffer, offset, count);
        for (int i = 0; i < read; i++)
        {
            float cur     = buffer[offset + i];
            float delayed = _delay[_pos];
            int channel = i % _feedbackLp.Length;
            _feedbackLp[channel] += FeedbackDamping * (delayed - _feedbackLp[channel]);
            _delay[_pos]       = cur + _feedbackLp[channel] * _decay + DenormalGuard;
            buffer[offset + i] = cur * _dry + delayed * _wet;
            _pos = (_pos + 1) % _delay.Length;
        }
        return read;
    }
}

internal class AudioBuffer : ISampleProvider
{
    private float[]?          _buf;
    private float[]?          _tmp;
    private int               _len;
    private readonly ISampleProvider _src;

    public int        GroupId    { get; }
    public WaveFormat WaveFormat => _src.WaveFormat;

    public AudioBuffer(ISampleProvider src, int groupId) { _src = src; GroupId = groupId; }

    public void Clear() => _buf = null;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_buf == null)
        {
            if (_tmp != null && _tmp.Length >= count) _buf = _tmp;
            else _tmp = _buf = new float[count];
            int n = _src.Read(_buf, 0, count);
            if (n < count) Array.Clear(_buf, n, count - n);
            _len = count;
        }
        if (count != _len) throw new InvalidOperationException("Count must be consistent.");
        Buffer.BlockCopy(_buf, 0, buffer, offset * 4, count * 4);
        return count;
    }
}

internal class AudioMixer : ISampleProvider
{
    private record struct Input(ISampleProvider Provider, int GroupId);
    private readonly List<Input> _inputs = new();
    private readonly object      _inputsLock = new();
    private readonly WaveFormat  _fmt;
    private Input[] _inputSnapshot = Array.Empty<Input>();
    private float[] _tmp = null!;
    private DateTime _lastOutputPeakLogUtc = DateTime.MinValue;
    private DateTime _lastInputErrorLogUtc = DateTime.MinValue;
    private float _mixLimiterGain = 1f;

    public WaveFormat WaveFormat => _fmt;

    public AudioMixer(int channels)
        => _fmt = WaveFormat.CreateIeeeFloatWaveFormat(AudioHelpers.ClockRate, channels);

    public int Read(float[] buffer, int offset, int count)
    {
        var inputs = _inputSnapshot;

        Array.Clear(buffer, offset, count);
        if (_tmp == null || _tmp.Length < count) _tmp = new float[count];
        if (inputs.Length == 0) return count;
        foreach (var inp in inputs)
        {
            int r;
            try
            {
                r = inp.Provider.Read(_tmp, 0, count);
            }
            catch (Exception ex)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastInputErrorLogUtc).TotalSeconds >= 1.0)
                {
                    _lastInputErrorLogUtc = now;
                    VoiceChatPlugin.VoiceChat.VoiceDiagnostics.Log("audio.mixer.input_error", $"group={inp.GroupId} error=\"{ex.Message}\"");
                }
                continue;
            }
            for (int i = 0; i < r; i++) buffer[offset + i] += _tmp[i];
        }

        LimitOutputPeakIfNeeded(buffer, offset, count, inputs.Length);
        return count;
    }

    private void LimitOutputPeakIfNeeded(float[] buffer, int offset, int count, int inputCount)
    {
        float preLimitPeak = 0f;
        int nonFinite = 0;
        for (int i = 0; i < count; i++)
        {
            var index = offset + i;
            float sample = buffer[index];
            if (!float.IsFinite(sample))
            {
                buffer[index] = 0f;
                nonFinite++;
                continue;
            }

            float abs = sample < 0f ? -sample : sample;
            if (abs > preLimitPeak)
                preLimitPeak = abs;
        }

        var targetGain = AudioHelpers.GetPlaybackMixLimiterGain(preLimitPeak);
        if (targetGain < _mixLimiterGain)
            _mixLimiterGain = targetGain;
        else
            _mixLimiterGain = Math.Min(targetGain, _mixLimiterGain + AudioHelpers.PlaybackMixLimiterReleasePerFrame);

        if (_mixLimiterGain < 0.999f)
        {
            for (int i = 0; i < count; i++)
                buffer[offset + i] *= _mixLimiterGain;
        }

        LogOutputPeakIfNeeded(buffer, offset, count, inputCount, preLimitPeak, nonFinite, _mixLimiterGain);
    }

    private void LogOutputPeakIfNeeded(
        float[] buffer,
        int offset,
        int count,
        int inputCount,
        float preLimitPeak,
        int nonFinite,
        float limiterGain)
    {
        float postLimitPeak = 0f;
        for (int i = 0; i < count; i++)
        {
            float sample = buffer[offset + i];
            if (!float.IsFinite(sample))
                continue;

            float abs = sample < 0f ? -sample : sample;
            if (abs > postLimitPeak)
                postLimitPeak = abs;
        }

        bool limited = limiterGain < 0.999f || nonFinite > 0;
        if (!limited && postLimitPeak < 0.98f)
            return;

        var now = DateTime.UtcNow;
        if ((now - _lastOutputPeakLogUtc).TotalSeconds < 0.5)
            return;

        _lastOutputPeakLogUtc = now;
        VoiceChatPlugin.VoiceChat.VoiceDiagnostics.Log("audio.output.peak",
            $"peak={postLimitPeak:0.00000} preLimitPeak={preLimitPeak:0.00000} limiterGain={limiterGain:0.000} limited={limited} nonFinite={nonFinite} inputs={inputCount} samples={count} channels={_fmt.Channels}");
    }

    public void AddInput(ISampleProvider src, int groupId)
    {
        Input input;
        if (src.WaveFormat.Channels == 1 && _fmt.Channels == 2)
            input = new(new MonoToStereoSampleProvider(src), groupId);
        else
            input = new(src, groupId);

        lock (_inputsLock)
        {
            _inputs.Add(input);
            _inputSnapshot = _inputs.ToArray();
        }
    }

    public void RemoveInput(int groupId)
    {
        lock (_inputsLock)
        {
            _inputs.RemoveAll(i => i.GroupId == groupId);
            _inputSnapshot = _inputs.ToArray();
        }
    }
}

internal class AudioRoutingInstanceNode
{
    private readonly AudioMixer?  _mixer;
    private readonly AudioBuffer? _buf;
    private readonly ISampleProvider _proc;

    public ISampleProvider Output    => _buf ?? _proc;
    public ISampleProvider Processor => _proc;

    public AudioRoutingInstanceNode(
        Action<AudioBuffer>                addBuffer,
        ISampleProvider                    source,
        Func<ISampleProvider, ISampleProvider> ctor,
        bool hasMultipleInput,
        bool hasMultipleOutput,
        int  channels,
        int  groupId)
    {
        if (hasMultipleInput)
        {
            _mixer = new AudioMixer(channels);
            if (source != null) _mixer.AddInput(source, -1);
        }
        else
        {
            _mixer = null;
            if (source.WaveFormat.Channels == 1 && channels == 2)
                source = new MonoToStereoSampleProvider(source);
        }
        _proc = ctor((_mixer ?? source)!);
        if (hasMultipleOutput)
        {
            _buf = new AudioBuffer(_proc, groupId);
            addBuffer(_buf);
        }
    }

    public void AddInput(ISampleProvider src, int groupId) => _mixer?.AddInput(src, groupId);
    public void RemoveInput(int groupId)                   => _mixer?.RemoveInput(groupId);
}


public class AudioRoutingInstance : IHasAudioPropertyNode
{
    private readonly AudioRoutingInstanceNode[] _nodes;
    private readonly BufferedSampleProvider     _source;

    internal AudioRoutingInstance(
        AudioRoutingInstanceNode[] nodes,
        BufferedSampleProvider     source)
    {
        _nodes  = nodes;
        _source = source;
    }

    public DateTime LastReceiptUtc { get; private set; } = DateTime.MinValue;

    public void AddSamples(float[] samples, int offset, int count)
    {
        LastReceiptUtc = DateTime.UtcNow;
        _source.AddSamples(samples, offset, count);
    }

    public void ClearBufferedSamples() => _source.Clear();
    public void FadeClearBufferedSamples() => _source.RequestFadeClear();
    public void DiscardBufferedSamples(int count) => _source.DiscardBufferedSamples(count);

    // Per-peer adaptive jitter-buffer wiring (Fix 2a-3 / 2b-3): the owning PeerConnection supplies the clean
    // arrival-jitter signal and a PLC bridge frame so the ring deepens/conceals per peer.
    public void SetJitterSamplesProvider(Func<double> provider) => _source.SetJitterSamplesProvider(provider);
    public void SetPlcFrameProvider(Func<float[]?> provider) => _source.SetPlcFrameProvider(provider);

    public bool ClearBufferedSamplesIfStale(DateTime nowUtc, TimeSpan maxAge)
    {
        if (LastReceiptUtc == DateTime.MinValue || BufferedSamples <= 0)
            return false;
        if (nowUtc - LastReceiptUtc <= maxAge)
            return false;

        ClearBufferedSamples();
        return true;
    }

    public int BufferedSamples => _source.BufferedSamples;

    public string ConsumeDebugStats() => _source.ConsumeDebugStats();

    AudioRoutingInstanceNode IHasAudioPropertyNode.GetProperty(int id) => _nodes[id];
}
