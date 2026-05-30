using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace VoiceChatPlugin.Audio;

internal class BufferedSampleProvider : ISampleProvider
{
    private const int RecoveryDecayReads = 240;

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
    private bool _lastReadEndedSilent = true;
    private bool _hasPlayedAudio;
    private DateTime _prebufferFirstSampleUtc = DateTime.MinValue;
    private int _adaptiveRecoveryPrebufferSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
    private int _stableReadCycles;
    private DateTime _lastWriteUtc = DateTime.MinValue;
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
                _adaptiveRecoveryPrebufferSamples = EnableRecoveryPrebuffer ? AudioHelpers.PlaybackRecoveryPrebufferSamples : 0;
                _stableReadCycles = 0;
            }
        }
    }

    public WaveFormat WaveFormat  => _format;
    public int  BufferedSamples   { get { lock (_stateLock) return _ring?.Count ?? 0; } }
    internal int CurrentRecoveryPrebufferSamples { get { lock (_stateLock) return _adaptiveRecoveryPrebufferSamples; } }
    // Legacy name kept for compatibility
    public int  BufferedBytes     => BufferedSamples;

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
        }
        if (EnableRecoveryPrebuffer && _prebufferSamples > 0 && _isPrebuffering)
        {
            int target = GetPrebufferTargetLocked();
            if (_ring.Count < target) return;

            _isPrebuffering = false;
            _prebufferFirstSampleUtc = DateTime.MinValue;
            LogBufferEvent("audio.buffer.prebufferRelease", $"reason=target-reached buffered={_ring.Count} target={target} startup={!_hasPlayedAudio}");
        }
    }

    public void Clear()
    {
        lock (_stateLock)
        {
            _ring?.Reset();
            _isPrebuffering = EnableRecoveryPrebuffer && _prebufferSamples > 0;
            _lastReadEndedSilent = true;
            _hasPlayedAudio = false;
            _prebufferFirstSampleUtc = DateTime.MinValue;
            _adaptiveRecoveryPrebufferSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
            _stableReadCycles = 0;
            _lastWriteUtc = DateTime.MinValue;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_stateLock)
            return ReadLocked(buffer, offset, count);
    }

    private int ReadLocked(float[] buffer, int offset, int count)
    {
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
        }

        if (_ring == null)
        {
            System.Threading.Interlocked.Increment(ref _readRequests);
            System.Threading.Interlocked.Add(ref _requestedSamples, count);
            System.Threading.Interlocked.Increment(ref _prebufferSilenceReads);
            LogBufferEvent("audio.buffer.prebuffer", $"requested={count} buffered={_ring?.Count ?? 0} prebuffer={_prebufferSamples}");
            return CompleteRead(buffer, offset, count, 0);
        }

        if (EnableRecoveryPrebuffer && _prebufferSamples > 0 && _isPrebuffering)
        {
            int buffered = _ring.Count;
            int target = GetPrebufferTargetLocked();
            bool waitExpired = buffered > 0 &&
                _prebufferFirstSampleUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _prebufferFirstSampleUtc).TotalMilliseconds >= AudioHelpers.PlaybackMaxPrebufferWaitMilliseconds;
            if (buffered < target && !waitExpired)
            {
                System.Threading.Interlocked.Increment(ref _readRequests);
                System.Threading.Interlocked.Add(ref _requestedSamples, count);
                System.Threading.Interlocked.Increment(ref _prebufferSilenceReads);
                LogBufferEvent("audio.buffer.prebuffer", $"requested={count} buffered={buffered} prebuffer={target} startup={!_hasPlayedAudio}");
                return CompleteRead(buffer, offset, count, 0);
            }

            _isPrebuffering = false;
            _prebufferFirstSampleUtc = DateTime.MinValue;
            LogBufferEvent("audio.buffer.prebufferRelease", $"requested={count} buffered={buffered} target={target} waitExpired={waitExpired} startup={!_hasPlayedAudio}");
        }

        System.Threading.Interlocked.Increment(ref _readRequests);
        System.Threading.Interlocked.Add(ref _requestedSamples, count);
        int bufferedBeforeRead = _ring.Count;
        int num = _ring.Read(buffer, offset, count);
        System.Threading.Interlocked.Add(ref _actualReadSamples, num);
        if (num < count)
        {
            System.Threading.Interlocked.Increment(ref _underruns);
            if (EnableRecoveryPrebuffer && _prebufferSamples > 0)
            {
                // Grow the cushion on any starvation; a genuine end-of-talk idle is undone separately by the
                // idle-reset at the top of ReadLocked once the talker has been silent past the idle window.
                IncreaseRecoveryPrebufferLocked();
                _isPrebuffering = true;
                _prebufferFirstSampleUtc = (_ring?.Count ?? 0) > 0 ? DateTime.UtcNow : DateTime.MinValue;
            }
            LogBufferEvent("audio.buffer.underrun",
                $"requested={count} actual={num} bufferedBefore={bufferedBeforeRead} buffered={_ring?.Count ?? 0} prebuffer={_prebufferSamples} recovery={_adaptiveRecoveryPrebufferSamples} hasPlayed={_hasPlayedAudio} readEndedSilent={_lastReadEndedSilent}");
        }
        else if (EnableRecoveryPrebuffer && _prebufferSamples > 0 && num == count)
        {
            DecayRecoveryPrebufferLocked();
        }
        return CompleteRead(buffer, offset, count, num);
    }

    private int GetPrebufferTargetLocked()
    {
        if (!_hasPlayedAudio)
            return _prebufferSamples;

        int boundedRecovery = Math.Clamp(_adaptiveRecoveryPrebufferSamples,
            AudioHelpers.PlaybackRecoveryPrebufferSamples,
            Math.Max(AudioHelpers.PlaybackRecoveryPrebufferSamples, _prebufferSamples));
        return boundedRecovery;
    }

    private void IncreaseRecoveryPrebufferLocked()
    {
        _stableReadCycles = 0;
        int maxRecovery = Math.Max(AudioHelpers.PlaybackRecoveryPrebufferSamples, _prebufferSamples);
        _adaptiveRecoveryPrebufferSamples = Math.Min(maxRecovery,
            _adaptiveRecoveryPrebufferSamples + (AudioHelpers.FrameSize * 2));
    }

    private void DecayRecoveryPrebufferLocked()
    {
        if (_adaptiveRecoveryPrebufferSamples <= AudioHelpers.PlaybackRecoveryPrebufferSamples)
            return;

        _stableReadCycles++;
        if (_stableReadCycles < RecoveryDecayReads)
            return;

        _stableReadCycles = 0;
        _adaptiveRecoveryPrebufferSamples = Math.Max(AudioHelpers.PlaybackRecoveryPrebufferSamples,
            _adaptiveRecoveryPrebufferSamples - AudioHelpers.FrameSize);
    }

    public string ConsumeDebugStats()
        => $"written={System.Threading.Interlocked.Exchange(ref _writtenSamples, 0)} " +
           $"discarded={System.Threading.Interlocked.Exchange(ref _discardedSamples, 0)} " +
           $"readCalls={System.Threading.Interlocked.Exchange(ref _readRequests, 0)} " +
           $"requested={System.Threading.Interlocked.Exchange(ref _requestedSamples, 0)} " +
           $"actual={System.Threading.Interlocked.Exchange(ref _actualReadSamples, 0)} " +
           $"underruns={System.Threading.Interlocked.Exchange(ref _underruns, 0)} " +
           $"prebufferSilence={System.Threading.Interlocked.Exchange(ref _prebufferSilenceReads, 0)}";

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

    private void LogBufferEvent(string category, string message)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastBufferEventLogUtc).TotalSeconds < 0.5)
            return;

        _lastBufferEventLogUtc = now;
        VoiceChatPlugin.VoiceChat.VoiceDiagnostics.Log(category, $"group={DebugGroupId} {message}");
    }

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
    private readonly ISampleProvider _src;
    private readonly float[] _delay;
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
            _delay[_pos]       = cur + delayed * _decay;
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
            int r = inp.Provider.Read(_tmp, 0, count);
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
