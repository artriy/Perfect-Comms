using System;
using System.Collections.Generic;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BclVoiceMixer
{
    private const int MinCushionSamples = AudioHelpers.FrameSize * 5;
    private const int MaxCushionSamples = AudioHelpers.FrameSize * 15;
    private const int CushionMarginSamples = AudioHelpers.FrameSize * 2;
    private const int PauseThresholdSamples = AudioHelpers.FrameSize * 20;
    private const float CushionGapMultiplier = 1.5f;
    private const float CushionGapDecay = 0.997f;
    private const int FadeSamples = AudioHelpers.ClockRate / 100;
    private const int MaxWaitMs = 400;
    private const float GainGlideK = 0.002f;
    private const float RadioDrive = 2.0f;
    private const float RadioLevel = 0.75f;
    private const float GhostDry = 0.6f;
    private const float GhostWet = 0.08f;
    private const float WallDry = 0.85f;
    private const float WallWet = 0.12f;

    private static readonly Biquad Lp650 = Biquad.Lowpass(650f, 0.7f);
    private static readonly Biquad Hp650 = Biquad.Highpass(650f, 0.9f);
    private static readonly Biquad Lp1900 = Biquad.Lowpass(1900f, 0.7f);

    private static readonly int[] GhostCombs = { 1214, 1293, 1390, 1476 };
    private static readonly int[] GhostAllpass = { 605, 480 };
    private static readonly int[] WallCombs = { 397, 439, 491, 547 };
    private static readonly int[] WallAllpass = { 185, 141 };

    private sealed class Peer
    {
        public readonly float[] Ring = new float[AudioHelpers.ClockRate / 2];
        public int Read;
        public int Write;
        public int Count;
        public float Volume;
        public float ClientVolume = 1f;
        public float LeftGain = 1f;
        public float RightGain = 1f;
        public float CurLeft;
        public float CurRight;
        public bool Primed;
        public int FadeRemaining;
        public long LastFeedConsumed;
        public float GapEstimate;
        public int TargetCushion = MinCushionSamples;
        public DateTime PrimeDeadline;
        public VoiceAudioFilterMode Mode;
        public float Bz1;
        public float Bz2;
    }

    private readonly Dictionary<int, Peer> _peers = new();
    private readonly object _sync = new();
    private readonly Reverb _ghostReverb = new(GhostCombs, GhostAllpass, 0.82f, 25);
    private readonly Reverb _wallReverb = new(WallCombs, WallAllpass, 0.6f, 11);
    private float[] _ghostSend = Array.Empty<float>();
    private float[] _wallSend = Array.Empty<float>();
    private float _ghostLpZ1;
    private float _ghostLpZ2;
    private int _ghostTailSamples;
    private int _wallTailSamples;
    private float _limiterGain = 1f;

    private long _diagReadCalls;
    private int _diagChunkMin = int.MaxValue;
    private int _diagChunkMax;
    private long _diagUnderrunReads;
    private long _diagFadeOuts;
    private long _diagPrimes;
    private long _diagUnprimes;
    private long _diagSilentSamples;
    private int _diagMaxRingDepth;
    private long _samplesConsumed;
    private int _diagMaxFeedGapSamples;
    private int _diagMaxTargetCushion;

    public void AddSamples(int group, float[] mono, int count, bool silent)
    {
        if (count <= 0) return;
        lock (_sync)
        {
            if (!_peers.TryGetValue(group, out var p)) { p = new Peer(); _peers[group] = p; }
            if (silent)
            {
                if (!p.Primed && p.Count > 0)
                    PrimeLocked(p);
                return;
            }
            if (!p.Primed && p.PrimeDeadline == DateTime.MinValue)
                p.PrimeDeadline = DateTime.UtcNow.AddMilliseconds(MaxWaitMs);
            if (p.LastFeedConsumed != 0)
            {
                var gap = (int)(_samplesConsumed - p.LastFeedConsumed);
                if (gap > _diagMaxFeedGapSamples) _diagMaxFeedGapSamples = gap;
                p.GapEstimate *= CushionGapDecay;
                if (gap < PauseThresholdSamples && gap > p.GapEstimate)
                    p.GapEstimate = gap;
                p.TargetCushion = Math.Clamp((int)(p.GapEstimate * CushionGapMultiplier) + CushionMarginSamples, MinCushionSamples, MaxCushionSamples);
                if (p.TargetCushion > _diagMaxTargetCushion) _diagMaxTargetCushion = p.TargetCushion;
            }
            p.LastFeedConsumed = _samplesConsumed;
            var len = p.Ring.Length;
            for (var i = 0; i < count; i++)
            {
                if (p.Count >= len)
                {
                    p.Read = (p.Read + 1) % len;
                    p.Count--;
                }
                p.Ring[p.Write] = mono[i];
                p.Write = (p.Write + 1) % len;
                p.Count++;
            }
            if (!p.Primed && p.Count >= p.TargetCushion)
                PrimeLocked(p);
        }
    }

    public void SetPeer(int group, float volume, float pan, VoiceAudioFilterMode mode)
    {
        GetPanGains(pan, out var left, out var right);
        lock (_sync)
        {
            if (!_peers.TryGetValue(group, out var p)) { p = new Peer(); _peers[group] = p; }
            p.Volume = volume;
            p.LeftGain = left;
            p.RightGain = right;
            if (p.Mode != mode)
            {
                p.Mode = mode;
                p.Bz1 = 0f;
                p.Bz2 = 0f;
            }
        }
    }

    public void SetClientVolume(int group, float clientVolume)
    {
        lock (_sync)
        {
            if (!_peers.TryGetValue(group, out var p)) { p = new Peer(); _peers[group] = p; }
            p.ClientVolume = clientVolume;
        }
    }

    public void Remove(int group)
    {
        lock (_sync)
            _peers.Remove(group);
    }

    public string FormatPlayoutDiagnostics()
    {
        lock (_sync)
        {
            var chunkMin = _diagChunkMin == int.MaxValue ? 0 : _diagChunkMin;
            var s = $"minCushion={MinCushionSamples} maxTargetCushion={_diagMaxTargetCushion} reads={_diagReadCalls} chunk={chunkMin}-{_diagChunkMax} maxRingDepth={_diagMaxRingDepth} maxFeedGapMs={_diagMaxFeedGapSamples * 1000 / AudioHelpers.ClockRate} underrunReads={_diagUnderrunReads} fadeOuts={_diagFadeOuts} primes={_diagPrimes} unprimes={_diagUnprimes} silentSamples={_diagSilentSamples}";
            _diagReadCalls = 0;
            _diagChunkMin = int.MaxValue;
            _diagChunkMax = 0;
            _diagUnderrunReads = 0;
            _diagFadeOuts = 0;
            _diagPrimes = 0;
            _diagUnprimes = 0;
            _diagSilentSamples = 0;
            _diagMaxRingDepth = 0;
            _diagMaxFeedGapSamples = 0;
            _diagMaxTargetCushion = 0;
            return s;
        }
    }

    public void Read(float[] interleavedStereo)
    {
        Array.Clear(interleavedStereo, 0, interleavedStereo.Length);
        var frames = interleavedStereo.Length / 2;
        if (_ghostSend.Length != interleavedStereo.Length)
            _ghostSend = new float[interleavedStereo.Length];
        if (_wallSend.Length != interleavedStereo.Length)
            _wallSend = new float[interleavedStereo.Length];
        Array.Clear(_ghostSend, 0, _ghostSend.Length);
        Array.Clear(_wallSend, 0, _wallSend.Length);
        var anyGhost = false;
        var anyWall = false;
        const float wInc = MathF.PI / FadeSamples;
        lock (_sync)
        {
            _diagReadCalls++;
            _samplesConsumed += frames;
            if (frames < _diagChunkMin) _diagChunkMin = frames;
            if (frames > _diagChunkMax) _diagChunkMax = frames;
            foreach (var p in _peers.Values)
            {
                if (!p.Primed)
                {
                    if (p.Count > 0 && p.PrimeDeadline != DateTime.MinValue && DateTime.UtcNow >= p.PrimeDeadline)
                        PrimeLocked(p);
                    else
                        continue;
                }
                if (p.Count > _diagMaxRingDepth) _diagMaxRingDepth = p.Count;
                if (p.Count < frames)
                {
                    _diagUnderrunReads++;
                    _diagSilentSamples += frames - p.Count;
                }
                if (p.Count <= frames)
                    _diagFadeOuts++;
                var ghost = p.Mode == VoiceAudioFilterMode.Ghost;
                var wall = p.Mode == VoiceAudioFilterMode.WallMuffle || p.Mode == VoiceAudioFilterMode.ListenerMuffle;
                anyGhost |= ghost;
                anyWall |= wall;
                var bus = ghost ? _ghostSend : wall ? _wallSend : interleavedStereo;
                var targetL = p.LeftGain * p.Volume * p.ClientVolume;
                var targetR = p.RightGain * p.Volume * p.ClientVolume;
                var n = Math.Min(frames, p.Count);
                var fadeOutStart = p.Count <= frames ? n - Math.Min(FadeSamples, n) : int.MaxValue;
                for (var f = 0; f < n; f++)
                {
                    var s = p.Ring[p.Read];
                    p.Read = (p.Read + 1) % p.Ring.Length;
                    p.Count--;
                    s = ApplyFilter(p, s);
                    if (p.FadeRemaining > 0)
                    {
                        s *= 0.5f * (1f - MathF.Cos((FadeSamples - p.FadeRemaining) * wInc));
                        p.FadeRemaining--;
                    }
                    if (f >= fadeOutStart)
                        s *= 0.5f * (1f - MathF.Cos((n - f) * wInc));
                    p.CurLeft += GainGlideK * (targetL - p.CurLeft);
                    p.CurRight += GainGlideK * (targetR - p.CurRight);
                    bus[f * 2] += s * p.CurLeft;
                    bus[f * 2 + 1] += s * p.CurRight;
                }
                if (p.Count == 0)
                {
                    p.Primed = false;
                    p.PrimeDeadline = DateTime.MinValue;
                    _diagUnprimes++;
                }
            }
        }

        // Ghost reverb send: muffle (1900 Hz) + reverb the dead-heard-by-living voices, blended into the dry mix.
        // Runs only while a ghost is active or its tail is still decaying, then flushes to silence (no idle churn).
        if (anyGhost)
            _ghostTailSamples = AudioHelpers.ClockRate * 2;
        if (anyGhost || _ghostTailSamples > 0)
        {
            for (var f = 0; f < frames; f++)
            {
                var l = _ghostSend[f * 2];
                var r = _ghostSend[f * 2 + 1];
                var mono = Lp1900.Process(ref _ghostLpZ1, ref _ghostLpZ2, (l + r) * 0.5f);
                _ghostReverb.Process(mono, out var wetL, out var wetR);
                interleavedStereo[f * 2] += GhostDry * l + GhostWet * wetL;
                interleavedStereo[f * 2 + 1] += GhostDry * r + GhostWet * wetR;
            }
            if (!anyGhost)
            {
                _ghostTailSamples -= frames;
                if (_ghostTailSamples <= 0)
                {
                    _ghostReverb.Reset();
                    _ghostLpZ1 = 0f;
                    _ghostLpZ2 = 0f;
                    _ghostTailSamples = 0;
                }
            }
        }

        // Wall/occlusion reverb send: short small-room ambience on the already-muffled voice (the dry voice is
        // carried by WallDry, so an occluded talker sounds like they are in the next room, not just dampened).
        if (anyWall)
            _wallTailSamples = AudioHelpers.ClockRate;
        if (anyWall || _wallTailSamples > 0)
        {
            for (var f = 0; f < frames; f++)
            {
                var l = _wallSend[f * 2];
                var r = _wallSend[f * 2 + 1];
                _wallReverb.Process((l + r) * 0.5f, out var wetL, out var wetR);
                interleavedStereo[f * 2] += WallDry * l + WallWet * wetL;
                interleavedStereo[f * 2 + 1] += WallDry * r + WallWet * wetR;
            }
            if (!anyWall)
            {
                _wallTailSamples -= frames;
                if (_wallTailSamples <= 0)
                {
                    _wallReverb.Reset();
                    _wallTailSamples = 0;
                }
            }
        }

        var peak = AudioHelpers.MeasurePeak(interleavedStereo, interleavedStereo.Length);
        var target = AudioHelpers.GetPlaybackMixLimiterGain(peak);
        if (target < _limiterGain)
            _limiterGain = target;
        else
            _limiterGain = Math.Min(1f, _limiterGain + AudioHelpers.PlaybackMixLimiterReleasePerFrame);
        AudioHelpers.ApplyGain(interleavedStereo, interleavedStereo.Length, _limiterGain);
        for (var i = 0; i < interleavedStereo.Length; i++)
            interleavedStereo[i] = Math.Clamp(interleavedStereo[i], -1f, 1f);
    }

    private static float ApplyFilter(Peer p, float s)
    {
        switch (p.Mode)
        {
            case VoiceAudioFilterMode.Radio:
                s = Hp650.Process(ref p.Bz1, ref p.Bz2, s);
                return MathF.Tanh(s * RadioDrive) * RadioLevel;
            case VoiceAudioFilterMode.WallMuffle:
            case VoiceAudioFilterMode.ListenerMuffle:
                return Lp650.Process(ref p.Bz1, ref p.Bz2, s);
            default:
                return s;
        }
    }

    private void PrimeLocked(Peer p)
    {
        p.Primed = true;
        p.PrimeDeadline = DateTime.MinValue;
        p.FadeRemaining = FadeSamples;
        _diagPrimes++;
        p.Bz1 = 0f;
        p.Bz2 = 0f;
        p.CurLeft = p.LeftGain * p.Volume * p.ClientVolume;
        p.CurRight = p.RightGain * p.Volume * p.ClientVolume;
    }

    private static void GetPanGains(float pan, out float left, out float right)
    {
        pan = Math.Clamp(pan, -1f, 1f);
        const float farSide = 0.25f;
        var farGain = farSide + (1f - farSide) * MathF.Cos(Math.Abs(pan) * (MathF.PI / 2f));
        left = pan > 0f ? farGain : 1f;
        right = pan < 0f ? farGain : 1f;
    }

    private readonly struct Biquad
    {
        private readonly float _b0, _b1, _b2, _a1, _a2;

        private Biquad(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
        }

        public float Process(ref float z1, ref float z2, float x)
        {
            var y = _b0 * x + z1;
            z1 = _b1 * x - _a1 * y + z2;
            z2 = _b2 * x - _a2 * y;
            return y;
        }

        public static Biquad Lowpass(float f0, float q)
        {
            Coeffs(f0, q, out var cw, out var alpha);
            float a0 = 1f + alpha;
            return new Biquad((1f - cw) / 2f / a0, (1f - cw) / a0, (1f - cw) / 2f / a0, -2f * cw / a0, (1f - alpha) / a0);
        }

        public static Biquad Highpass(float f0, float q)
        {
            Coeffs(f0, q, out var cw, out var alpha);
            float a0 = 1f + alpha;
            return new Biquad((1f + cw) / 2f / a0, -(1f + cw) / a0, (1f + cw) / 2f / a0, -2f * cw / a0, (1f - alpha) / a0);
        }

        private static void Coeffs(float f0, float q, out float cosW0, out float alpha)
        {
            float w0 = 2f * MathF.PI * f0 / AudioHelpers.ClockRate;
            cosW0 = MathF.Cos(w0);
            alpha = MathF.Sin(w0) / (2f * q);
        }
    }

    private sealed class Reverb
    {
        private const float Damp1 = 0.2f;
        private const float Damp2 = 0.8f;
        private const float ApFeedback = 0.5f;
        private const float InGain = 0.5f;

        private readonly float _feedback;
        private readonly float[][] _combL, _combR, _apL, _apR;
        private readonly int[] _ciL, _ciR, _aiL, _aiR;
        private readonly float[] _filtL, _filtR;

        public Reverb(int[] combLen, int[] apLen, float feedback, int spread)
        {
            _feedback = feedback;
            _combL = new float[combLen.Length][];
            _combR = new float[combLen.Length][];
            _ciL = new int[combLen.Length];
            _ciR = new int[combLen.Length];
            _filtL = new float[combLen.Length];
            _filtR = new float[combLen.Length];
            for (var i = 0; i < combLen.Length; i++)
            {
                _combL[i] = new float[combLen[i]];
                _combR[i] = new float[combLen[i] + spread];
            }
            _apL = new float[apLen.Length][];
            _apR = new float[apLen.Length][];
            _aiL = new int[apLen.Length];
            _aiR = new int[apLen.Length];
            for (var i = 0; i < apLen.Length; i++)
            {
                _apL[i] = new float[apLen[i]];
                _apR[i] = new float[apLen[i] + spread];
            }
        }

        public void Process(float input, out float outL, out float outR)
        {
            var x = input * InGain;
            float l = 0f, r = 0f;
            for (var i = 0; i < _combL.Length; i++)
            {
                l += Comb(_combL[i], ref _ciL[i], ref _filtL[i], x);
                r += Comb(_combR[i], ref _ciR[i], ref _filtR[i], x);
            }
            for (var i = 0; i < _apL.Length; i++)
            {
                l = Allpass(_apL[i], ref _aiL[i], l);
                r = Allpass(_apR[i], ref _aiR[i], r);
            }
            outL = l;
            outR = r;
        }

        public void Reset()
        {
            for (var i = 0; i < _combL.Length; i++)
            {
                Array.Clear(_combL[i], 0, _combL[i].Length);
                Array.Clear(_combR[i], 0, _combR[i].Length);
                _ciL[i] = 0; _ciR[i] = 0; _filtL[i] = 0f; _filtR[i] = 0f;
            }
            for (var i = 0; i < _apL.Length; i++)
            {
                Array.Clear(_apL[i], 0, _apL[i].Length);
                Array.Clear(_apR[i], 0, _apR[i].Length);
                _aiL[i] = 0; _aiR[i] = 0;
            }
        }

        private float Comb(float[] buf, ref int idx, ref float store, float input)
        {
            var y = buf[idx];
            store = y * Damp2 + store * Damp1;
            buf[idx] = input + store * _feedback;
            if (++idx >= buf.Length) idx = 0;
            return y;
        }

        private static float Allpass(float[] buf, ref int idx, float input)
        {
            var y = buf[idx];
            var output = y - input;
            buf[idx] = input + y * ApFeedback;
            if (++idx >= buf.Length) idx = 0;
            return output;
        }
    }
}
