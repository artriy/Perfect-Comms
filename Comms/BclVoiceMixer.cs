using System;
using System.Collections.Generic;
using VoiceChatPlugin.Audio;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BclVoiceMixer
{
    private const int PrimeSamples = AudioHelpers.PlaybackRecoveryPrebufferSamples;
    private const int FadeSamples = AudioHelpers.ClockRate / 100;
    private const int MaxWaitMs = AudioHelpers.PlaybackMaxPrebufferWaitMilliseconds;
    private const float GainGlideK = 0.002f;
    private const float RadioDrive = 2.0f;
    private const float RadioLevel = 0.75f;
    private const float GhostDry = 0.6f;
    private const float GhostWet = 0.08f;

    private static readonly Biquad Lp650 = Biquad.Lowpass(650f, 0.7f);
    private static readonly Biquad Hp650 = Biquad.Highpass(650f, 0.9f);
    private static readonly Biquad Lp1900 = Biquad.Lowpass(1900f, 0.7f);

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
        public DateTime PrimeDeadline;
        public VoiceAudioFilterMode Mode;
        public float Bz1;
        public float Bz2;
    }

    private readonly Dictionary<int, Peer> _peers = new();
    private readonly object _sync = new();
    private readonly GhostReverb _ghostReverb = new();
    private float[] _ghostSend = Array.Empty<float>();
    private float _ghostLpZ1;
    private float _ghostLpZ2;
    private int _ghostTailSamples;
    private float _limiterGain = 1f;

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
            if (!p.Primed && p.Count >= PrimeSamples)
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

    public void Read(float[] interleavedStereo)
    {
        Array.Clear(interleavedStereo, 0, interleavedStereo.Length);
        var frames = interleavedStereo.Length / 2;
        if (_ghostSend.Length != interleavedStereo.Length)
            _ghostSend = new float[interleavedStereo.Length];
        Array.Clear(_ghostSend, 0, _ghostSend.Length);
        var anyGhost = false;
        const float wInc = MathF.PI / FadeSamples;
        lock (_sync)
        {
            foreach (var p in _peers.Values)
            {
                if (!p.Primed)
                {
                    if (p.Count > 0 && p.PrimeDeadline != DateTime.MinValue && DateTime.UtcNow >= p.PrimeDeadline)
                        PrimeLocked(p);
                    else
                        continue;
                }
                var ghost = p.Mode == VoiceAudioFilterMode.Ghost;
                anyGhost |= ghost;
                var bus = ghost ? _ghostSend : interleavedStereo;
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

    private sealed class GhostReverb
    {
        private const float Feedback = 0.82f;
        private const float Damp1 = 0.2f;
        private const float Damp2 = 0.8f;
        private const float ApFeedback = 0.5f;
        private const float InGain = 0.5f;
        private const int Spread = 25;
        private static readonly int[] CombLen = { 1214, 1293, 1390, 1476 };
        private static readonly int[] ApLen = { 605, 480 };

        private readonly float[][] _combL, _combR, _apL, _apR;
        private readonly int[] _ciL, _ciR, _aiL, _aiR;
        private readonly float[] _filtL, _filtR;

        public GhostReverb()
        {
            _combL = new float[CombLen.Length][];
            _combR = new float[CombLen.Length][];
            _ciL = new int[CombLen.Length];
            _ciR = new int[CombLen.Length];
            _filtL = new float[CombLen.Length];
            _filtR = new float[CombLen.Length];
            for (var i = 0; i < CombLen.Length; i++)
            {
                _combL[i] = new float[CombLen[i]];
                _combR[i] = new float[CombLen[i] + Spread];
            }
            _apL = new float[ApLen.Length][];
            _apR = new float[ApLen.Length][];
            _aiL = new int[ApLen.Length];
            _aiR = new int[ApLen.Length];
            for (var i = 0; i < ApLen.Length; i++)
            {
                _apL[i] = new float[ApLen[i]];
                _apR[i] = new float[ApLen[i] + Spread];
            }
        }

        public void Process(float input, out float outL, out float outR)
        {
            var x = input * InGain;
            float l = 0f, r = 0f;
            for (var i = 0; i < CombLen.Length; i++)
            {
                l += Comb(_combL[i], ref _ciL[i], ref _filtL[i], x);
                r += Comb(_combR[i], ref _ciR[i], ref _filtR[i], x);
            }
            for (var i = 0; i < ApLen.Length; i++)
            {
                l = Allpass(_apL[i], ref _aiL[i], l);
                r = Allpass(_apR[i], ref _aiR[i], r);
            }
            outL = l;
            outR = r;
        }

        public void Reset()
        {
            for (var i = 0; i < CombLen.Length; i++)
            {
                Array.Clear(_combL[i], 0, _combL[i].Length);
                Array.Clear(_combR[i], 0, _combR[i].Length);
                _ciL[i] = 0; _ciR[i] = 0; _filtL[i] = 0f; _filtR[i] = 0f;
            }
            for (var i = 0; i < ApLen.Length; i++)
            {
                Array.Clear(_apL[i], 0, _apL[i].Length);
                Array.Clear(_apR[i], 0, _apR[i].Length);
                _aiL[i] = 0; _aiR[i] = 0;
            }
        }

        private static float Comb(float[] buf, ref int idx, ref float store, float input)
        {
            var y = buf[idx];
            store = y * Damp2 + store * Damp1;
            buf[idx] = input + store * Feedback;
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
