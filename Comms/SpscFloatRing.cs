using System;
using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Preallocated single-producer/single-consumer PCM ring. The producer never advances the
/// consumer cursor: when full, a complete incoming block is rejected so the callback can keep a
/// coherent timeline without locks or partially overwritten samples. The consumer owns bounded
/// latency recovery and can discard stale history before crossfading into fresh audio.
/// </summary>
internal sealed class SpscFloatRing
{
    private readonly float[] _samples;
    private readonly float[] _lastByChannel;
    private readonly float[] _fadeFromByChannel;
    private readonly float[] _resumeFromByChannel;
    private readonly float[] _readScratch;
    private readonly int _channels;
    private readonly int _fadeFrames;
    private readonly int _targetLatencySamples;
    private readonly int _primeLatencySamples;
    private readonly int _maximumLatencySamples;
    private readonly int _driftHysteresisSamples;
    private readonly int _schedulerStallThresholdSamples;
    private readonly bool _enableClockDriftCorrection;
    private long _readPosition;
    private long _writePosition;
    private long _droppedSamples;
    private long _zeroFilledSamples;
    private long _highWaterSamples;
    private long _skippedSamples;
    private long _primingZeroFilledSamples;
    private long _clockCorrectionSamples;
    private long _clockCorrectionCallbacks;
    private long _lastObservedDroppedSamples;
    private int _underrunFadeFrame;
    private int _recoveryFadeFrame;
    private bool _starved;
    private bool _primed;
    private bool _schedulerStallArmed;

    public SpscFloatRing(
        int capacitySamples,
        int channels = 1,
        int fadeFrames = 0,
        int targetLatencySamples = 0,
        int primeLatencySamples = 0,
        int maximumLatencySamples = 0,
        bool enableClockDriftCorrection = false)
    {
        if (capacitySamples <= 0) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (capacitySamples % channels != 0)
            throw new ArgumentException("Capacity must contain complete channel frames.", nameof(capacitySamples));
        if (targetLatencySamples < 0 || targetLatencySamples > capacitySamples || targetLatencySamples % channels != 0)
            throw new ArgumentOutOfRangeException(
                nameof(targetLatencySamples),
                "Target latency must be channel-aligned and within the ring capacity.");
        if (primeLatencySamples < 0 || primeLatencySamples > capacitySamples || primeLatencySamples % channels != 0)
            throw new ArgumentOutOfRangeException(
                nameof(primeLatencySamples),
                "Prime latency must be channel-aligned and within the ring capacity.");
        if (maximumLatencySamples < 0 || maximumLatencySamples > capacitySamples || maximumLatencySamples % channels != 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumLatencySamples),
                "Maximum latency must be channel-aligned and within the ring capacity.");
        if (enableClockDriftCorrection && targetLatencySamples == 0)
            throw new ArgumentException(
                "Clock-drift correction requires a non-zero target latency.",
                nameof(targetLatencySamples));

        var effectiveMaximum = maximumLatencySamples > 0
            ? maximumLatencySamples
            : targetLatencySamples;
        if (effectiveMaximum > 0 && effectiveMaximum < targetLatencySamples)
            throw new ArgumentOutOfRangeException(
                nameof(maximumLatencySamples),
                "Maximum latency cannot be below the target latency.");
        if (effectiveMaximum > 0 && primeLatencySamples > effectiveMaximum)
            throw new ArgumentOutOfRangeException(
                nameof(primeLatencySamples),
                "Prime latency cannot exceed the maximum latency.");

        _samples = new float[capacitySamples];
        _readScratch = new float[capacitySamples];
        _channels = channels;
        _fadeFrames = Math.Max(0, fadeFrames);
        _targetLatencySamples = targetLatencySamples;
        _primeLatencySamples = primeLatencySamples;
        _maximumLatencySamples = effectiveMaximum;
        _enableClockDriftCorrection = enableClockDriftCorrection;
        _driftHysteresisSamples = targetLatencySamples == 0
            ? 0
            : AlignToChannels(Math.Max(channels, targetLatencySamples / 8), channels);
        // Clock drift is gradual, while a missed Unity callback creates a one-frame depth jump.
        // Arm recovery at a narrow target-plus-25% threshold and require the excess to survive
        // one callback before discarding it. This preserves the inaudible 0.5% drift correction
        // for transient phase jitter without letting a real callback stall linger for seconds.
        _schedulerStallThresholdSamples = targetLatencySamples > 0 && effectiveMaximum > targetLatencySamples
            ? Math.Min(
                effectiveMaximum,
                targetLatencySamples + Math.Max(channels, _driftHysteresisSamples * 2))
            : effectiveMaximum;
        _lastByChannel = new float[channels];
        _fadeFromByChannel = new float[channels];
        _resumeFromByChannel = new float[channels];
        _recoveryFadeFrame = _fadeFrames;
        _primed = primeLatencySamples == 0;
    }

    public int CapacitySamples => _samples.Length;
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);
    public long ZeroFilledSamples => Interlocked.Read(ref _zeroFilledSamples);
    public long HighWaterSamples => Interlocked.Read(ref _highWaterSamples);
    public long SkippedSamples => Interlocked.Read(ref _skippedSamples);
    public long PrimingZeroFilledSamples => Interlocked.Read(ref _primingZeroFilledSamples);
    /// <summary>
    /// Signed extra input samples consumed by bounded clock correction. Positive values mean
    /// catch-up; negative values mean the consumer stretched audio to rebuild its target depth.
    /// </summary>
    public long ClockCorrectionSamples => Interlocked.Read(ref _clockCorrectionSamples);
    public long ClockCorrectionCallbacks => Interlocked.Read(ref _clockCorrectionCallbacks);
    public bool IsPrimed => Volatile.Read(ref _primed);

    public int DepthSamples
    {
        get
        {
            var write = Volatile.Read(ref _writePosition);
            var read = Volatile.Read(ref _readPosition);
            return (int)Math.Clamp(write - read, 0L, _samples.Length);
        }
    }

    public bool TryWrite(ReadOnlySpan<float> source)
    {
        if (source.Length == 0) return true;
        if (source.Length > _samples.Length || source.Length % _channels != 0)
        {
            Interlocked.Add(ref _droppedSamples, source.Length);
            return false;
        }

        // Only the producer writes _writePosition and only the consumer writes _readPosition.
        var write = _writePosition;
        var read = Volatile.Read(ref _readPosition);
        var used = write - read;
        if (used < 0 || used > _samples.Length || source.Length > _samples.Length - used)
        {
            Interlocked.Add(ref _droppedSamples, source.Length);
            return false;
        }

        CopyIntoRing(source, write);
        var nextWrite = write + source.Length;
        Volatile.Write(ref _writePosition, nextWrite);
        UpdateHighWater(nextWrite - read);
        return true;
    }

    /// <summary>Reads available PCM and fills any true underrun with a short channel-safe fade.</summary>
    public int Read(Span<float> destination)
    {
        if (destination.Length == 0) return 0;
        var read = _readPosition;
        var write = Volatile.Read(ref _writePosition);
        var available = (int)Math.Clamp(write - read, 0L, _samples.Length);
        var dropped = Interlocked.Read(ref _droppedSamples);
        if (dropped != _lastObservedDroppedSamples)
        {
            // A full ring rejected newer audio, so every queued sample predates that loss. Drop
            // the stale timeline entirely; the underrun fade below and recovery fade on the next
            // producer block hide the discontinuity without replaying up to 320 ms of old speech.
            _lastObservedDroppedSamples = dropped;
            if (available > 0)
            {
                read += available;
                Volatile.Write(ref _readPosition, read);
                Interlocked.Add(ref _skippedSamples, available);
                available = 0;
            }
            Volatile.Write(ref _primed, _primeLatencySamples == 0);
            _schedulerStallArmed = false;
        }
        else
        {
            var retain = _targetLatencySamples > 0 ? _targetLatencySamples : _maximumLatencySamples;
            var hardMaximumExceeded = _maximumLatencySamples > 0
                                      && available > retain
                                      && available >= _maximumLatencySamples;
            var sustainedSchedulerStall = false;
            if (!hardMaximumExceeded && _schedulerStallArmed)
            {
                sustainedSchedulerStall = available > retain + _driftHysteresisSamples;
                if (!sustainedSchedulerStall) _schedulerStallArmed = false;
            }
            else if (!hardMaximumExceeded
                     && _schedulerStallThresholdSamples > retain
                     && available >= _schedulerStallThresholdSamples)
            {
                _schedulerStallArmed = true;
            }

            if (hardMaximumExceeded || sustainedSchedulerStall)
            {
                // A suspended Unity callback accumulates stale history much faster than oscillator
                // drift. Retain the newest target-depth tail and crossfade into it after either the
                // absolute bound or a two-callback scheduler-stall observation is reached.
                var skip = available - retain;
                skip -= skip % _channels;
                if (skip > 0)
                {
                    read += skip;
                    Volatile.Write(ref _readPosition, read);
                    Interlocked.Add(ref _skippedSamples, skip);
                    available -= skip;
                    _starved = true;
                }
                _schedulerStallArmed = false;
            }
        }

        // Do not begin (or resume after a true underrun) on a nearly empty ring. Holding silence
        // until the configured cushion exists decouples the Stopwatch producer phase from Unity's
        // callback phase and gives the drift controller room to work in both directions.
        if (!Volatile.Read(ref _primed) && available < _primeLatencySamples)
        {
            FillUnderrun(destination, copied: 0, priming: true);
            return 0;
        }
        if (!Volatile.Read(ref _primed)) Volatile.Write(ref _primed, true);

        if (available >= destination.Length)
        {
            var correction = DetermineClockCorrection(available, destination.Length);
            var inputSamples = destination.Length + correction;
            if (correction == 0)
            {
                CopyFromRing(destination, read);
            }
            else
            {
                CopyFromRing(_readScratch.AsSpan(0, inputSamples), read);
                ResampleInterleaved(
                    _readScratch.AsSpan(0, inputSamples),
                    destination,
                    _channels);
                Interlocked.Add(ref _clockCorrectionSamples, correction);
                Interlocked.Increment(ref _clockCorrectionCallbacks);
            }
            Volatile.Write(ref _readPosition, read + inputSamples);
            ApplyRecoveryFade(destination);
            return destination.Length;
        }

        var copied = available;
        if (copied > 0)
        {
            CopyFromRing(destination[..copied], read);
            Volatile.Write(ref _readPosition, read + copied);
            ApplyRecoveryFade(destination[..copied]);
        }
        FillUnderrun(destination, copied, priming: false);
        if (_primeLatencySamples > 0) Volatile.Write(ref _primed, false);
        _schedulerStallArmed = false;

        return copied;
    }

    /// <summary>Call only after producer and consumer workers have stopped.</summary>
    public void Clear()
    {
        Array.Clear(_samples, 0, _samples.Length);
        Array.Clear(_lastByChannel, 0, _lastByChannel.Length);
        Array.Clear(_fadeFromByChannel, 0, _fadeFromByChannel.Length);
        Array.Clear(_resumeFromByChannel, 0, _resumeFromByChannel.Length);
        Volatile.Write(ref _readPosition, 0);
        Volatile.Write(ref _writePosition, 0);
        _underrunFadeFrame = 0;
        _recoveryFadeFrame = _fadeFrames;
        _starved = false;
        _schedulerStallArmed = false;
        _lastObservedDroppedSamples = Interlocked.Read(ref _droppedSamples);
        Volatile.Write(ref _primed, _primeLatencySamples == 0);
    }

    private int DetermineClockCorrection(int available, int outputSamples)
    {
        if (!_enableClockDriftCorrection || outputSamples % _channels != 0) return 0;
        var outputFrames = outputSamples / _channels;
        // A whole channel frame is already more than 0.5% for smaller callbacks, so leave those
        // untouched instead of violating the audible-rate bound.
        if (outputFrames < 200) return 0;

        // Half a percent is inaudible as a short linear rate adjustment but can continuously
        // cancel hundreds of ppm of device/Stopwatch drift. The depth hysteresis prevents the
        // correction direction from toggling on ordinary callback scheduling jitter.
        var correctionFrames = Math.Max(1, outputFrames / 200);
        var correctionSamples = correctionFrames * _channels;
        if (available > _targetLatencySamples + _driftHysteresisSamples
            && available >= outputSamples + correctionSamples)
            return correctionSamples;
        if (available < _targetLatencySamples - _driftHysteresisSamples
            && outputSamples > correctionSamples)
            return -correctionSamples;
        return 0;
    }

    private void FillUnderrun(Span<float> destination, int copied, bool priming)
    {
        var missing = destination.Length - copied;
        if (missing <= 0) return;
        Interlocked.Add(ref _zeroFilledSamples, missing);
        if (priming) Interlocked.Add(ref _primingZeroFilledSamples, missing);
        if (!_starved)
        {
            _starved = true;
            _underrunFadeFrame = 0;
            _recoveryFadeFrame = _fadeFrames;
            Array.Copy(_lastByChannel, _fadeFromByChannel, _channels);
        }
        for (var i = 0; i < missing; i++)
        {
            var frame = _underrunFadeFrame + i / _channels;
            var channel = (copied + i) % _channels;
            var scale = frame < _fadeFrames
                ? 1f - (frame + 1f) / (_fadeFrames + 1f)
                : 0f;
            var output = _fadeFromByChannel[channel] * scale;
            destination[copied + i] = output;
            _lastByChannel[channel] = output;
        }
        _underrunFadeFrame = Math.Min(
            _fadeFrames,
            _underrunFadeFrame + (missing + _channels - 1) / _channels);
    }

    private static void ResampleInterleaved(
        ReadOnlySpan<float> source,
        Span<float> destination,
        int channels)
    {
        var inputFrames = source.Length / channels;
        var outputFrames = destination.Length / channels;
        if (inputFrames <= 0 || outputFrames <= 0)
        {
            destination.Clear();
            return;
        }
        if (inputFrames == 1 || outputFrames == 1)
        {
            for (var frame = 0; frame < outputFrames; frame++)
                source[..channels].CopyTo(destination.Slice(frame * channels, channels));
            return;
        }

        var denominator = outputFrames - 1;
        for (var outputFrame = 0; outputFrame < outputFrames; outputFrame++)
        {
            var position = (long)outputFrame * (inputFrames - 1);
            var leftFrame = (int)(position / denominator);
            var remainder = (int)(position % denominator);
            var rightFrame = Math.Min(leftFrame + 1, inputFrames - 1);
            var weight = remainder / (float)denominator;
            for (var channel = 0; channel < channels; channel++)
            {
                var left = source[leftFrame * channels + channel];
                var right = source[rightFrame * channels + channel];
                destination[outputFrame * channels + channel] = left + (right - left) * weight;
            }
        }
    }

    private static int AlignToChannels(int value, int channels)
        => value - value % channels;

    private void ApplyRecoveryFade(Span<float> samples)
    {
        if (_starved)
        {
            _starved = false;
            _recoveryFadeFrame = 0;
            Array.Copy(_lastByChannel, _resumeFromByChannel, _channels);
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var channel = i % _channels;
            var frame = _recoveryFadeFrame + i / _channels;
            var output = samples[i];
            if (frame < _fadeFrames)
            {
                var progress = frame / (float)_fadeFrames;
                output = _resumeFromByChannel[channel]
                         + (output - _resumeFromByChannel[channel]) * progress;
                samples[i] = output;
            }
            _lastByChannel[channel] = output;
        }

        _recoveryFadeFrame = Math.Min(
            _fadeFrames,
            _recoveryFadeFrame + (samples.Length + _channels - 1) / _channels);
    }

    private void CopyIntoRing(ReadOnlySpan<float> source, long position)
    {
        var index = (int)(position % _samples.Length);
        var first = Math.Min(source.Length, _samples.Length - index);
        source[..first].CopyTo(_samples.AsSpan(index, first));
        if (first < source.Length)
            source[first..].CopyTo(_samples.AsSpan(0, source.Length - first));
    }

    private void CopyFromRing(Span<float> destination, long position)
    {
        var index = (int)(position % _samples.Length);
        var first = Math.Min(destination.Length, _samples.Length - index);
        _samples.AsSpan(index, first).CopyTo(destination[..first]);
        if (first < destination.Length)
            _samples.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
    }

    private void UpdateHighWater(long depth)
    {
        var bounded = Math.Clamp(depth, 0L, _samples.Length);
        while (true)
        {
            var current = Interlocked.Read(ref _highWaterSamples);
            if (bounded <= current) return;
            if (Interlocked.CompareExchange(ref _highWaterSamples, bounded, current) == current) return;
        }
    }
}
