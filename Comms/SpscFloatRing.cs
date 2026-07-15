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
    private readonly int _channels;
    private readonly int _fadeFrames;
    private readonly int _targetLatencySamples;
    private long _readPosition;
    private long _writePosition;
    private long _droppedSamples;
    private long _zeroFilledSamples;
    private long _highWaterSamples;
    private long _skippedSamples;
    private long _lastObservedDroppedSamples;
    private int _underrunFadeFrame;
    private int _recoveryFadeFrame;
    private bool _starved;

    public SpscFloatRing(
        int capacitySamples,
        int channels = 1,
        int fadeFrames = 0,
        int targetLatencySamples = 0)
    {
        if (capacitySamples <= 0) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (capacitySamples % channels != 0)
            throw new ArgumentException("Capacity must contain complete channel frames.", nameof(capacitySamples));
        if (targetLatencySamples < 0 || targetLatencySamples > capacitySamples || targetLatencySamples % channels != 0)
            throw new ArgumentOutOfRangeException(
                nameof(targetLatencySamples),
                "Target latency must be channel-aligned and within the ring capacity.");

        _samples = new float[capacitySamples];
        _channels = channels;
        _fadeFrames = Math.Max(0, fadeFrames);
        _targetLatencySamples = targetLatencySamples;
        _lastByChannel = new float[channels];
        _fadeFromByChannel = new float[channels];
        _resumeFromByChannel = new float[channels];
        _recoveryFadeFrame = _fadeFrames;
    }

    public int CapacitySamples => _samples.Length;
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);
    public long ZeroFilledSamples => Interlocked.Read(ref _zeroFilledSamples);
    public long HighWaterSamples => Interlocked.Read(ref _highWaterSamples);
    public long SkippedSamples => Interlocked.Read(ref _skippedSamples);

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
        }
        else if (_targetLatencySamples > 0 && available > _targetLatencySamples)
        {
            // No data was lost, but a stalled Unity callback accumulated excess latency. Retain
            // only the newest bounded, channel-aligned tail and crossfade into it immediately.
            var skip = available - _targetLatencySamples;
            skip -= skip % _channels;
            if (skip > 0)
            {
                read += skip;
                Volatile.Write(ref _readPosition, read);
                Interlocked.Add(ref _skippedSamples, skip);
                available -= skip;
                _starved = true;
            }
        }
        var copied = Math.Min(destination.Length, available);

        CopyFromRing(destination[..copied], read);
        Volatile.Write(ref _readPosition, read + copied);

        if (copied > 0) ApplyRecoveryFade(destination[..copied]);

        var missing = destination.Length - copied;
        if (missing > 0)
        {
            Interlocked.Add(ref _zeroFilledSamples, missing);
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
        _lastObservedDroppedSamples = Interlocked.Read(ref _droppedSamples);
    }

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
