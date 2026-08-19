using System;
using System.Collections.Generic;

namespace PerfectComms.Starlight.Media;

internal sealed class ManagedVoiceMixer
{
    private const int FrameSamples = 960;
    private const int StereoSamples = FrameSamples * 2;
    private const int SampleRate = 48_000;
    private const int MaximumPeers = 32;
    private const int FilterTransitionSamples = 96;
    private const float GainGlide = 0.002f;
    private const float PanFarSide = 0.25f;
    private const float MuffleCutoffHz = 1_000f;
    private const float RadioDrive = 2f;
    private const float RadioLevel = 0.75f;
    private const float WallDry = 0.85f;
    private const float WallWet = 0.12f;
    private const float GhostDry = 0.6f;
    private const float GhostWet = 0.08f;
    private const float SoftLimitStart = 0.92f;
    private const int GhostTailSamples = SampleRate * 2;
    private const int WallTailSamples = SampleRate;

    private static readonly int[] GhostCombLengths = [1214, 1293, 1390, 1476];
    private static readonly int[] GhostAllPassLengths = [605, 480];
    private static readonly int[] WallCombLengths = [397, 439, 491, 547];
    private static readonly int[] WallAllPassLengths = [185, 141];

    private readonly Dictionary<string, PeerMixState> _peers = new(MaximumPeers, StringComparer.Ordinal);
    private readonly string[] _stalePeers = new string[MaximumPeers];
    private readonly float[] _ghostSend = new float[StereoSamples];
    private readonly float[] _wallSend = new float[StereoSamples];
    private readonly Biquad _lowPass650 = Biquad.LowPass(650f, 0.7f);
    private readonly Biquad _highPass650 = Biquad.HighPass(650f, 0.9f);
    private readonly Biquad _lowPass1900 = Biquad.LowPass(1900f, 0.7f);
    private readonly Biquad _muffleLowPass = Biquad.LowPass(MuffleCutoffHz, 0.7f);
    private readonly Reverb _ghostReverb = new(GhostCombLengths, GhostAllPassLengths, 0.82f, 25);
    private readonly Reverb _wallReverb = new(WallCombLengths, WallAllPassLengths, 0.6f, 11);
    private readonly LookaheadLimiter _outputLimiter = new();

    private bool _deafened;
    private float _master = 1f;
    private float _ghostLowPassZ1;
    private float _ghostLowPassZ2;
    private int _ghostTail;
    private int _wallTail;

    public void Configure(bool deafened, float master, IReadOnlyList<ManagedPeerRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (deafened && !_deafened)
        {
            ResetEffects();
            _peers.Clear();
        }

        _deafened = deafened;
        _master = float.IsFinite(master) ? Math.Clamp(master, 0f, 2f) : 0f;
        foreach (PeerMixState peer in _peers.Values)
            peer.Seen = false;

        for (int i = 0; i < routes.Count; i++)
        {
            ManagedPeerRoute route = routes[i];
            if (!string.IsNullOrEmpty(route.PeerId) &&
                _peers.TryGetValue(route.PeerId, out PeerMixState? peer))
                peer.Seen = true;
        }

        int staleCount = 0;
        foreach (KeyValuePair<string, PeerMixState> pair in _peers)
        {
            if (!pair.Value.Seen)
                _stalePeers[staleCount++] = pair.Key;
        }
        for (int i = 0; i < staleCount; i++)
        {
            _peers.Remove(_stalePeers[i]);
            _stalePeers[i] = null!;
        }
        for (int i = 0; i < routes.Count; i++)
        {
            ManagedPeerRoute route = routes[i];
            if (string.IsNullOrEmpty(route.PeerId))
                continue;

            float gain = float.IsFinite(route.Gain) ? Math.Clamp(route.Gain, 0f, 4f) : 0f;
            float pan = float.IsFinite(route.Pan) ? Math.Clamp(route.Pan, -1f, 1f) : 0f;
            PanGains(pan, out float leftPan, out float rightPan);
            FilterMode mode = FilterModeFromValue(route.Mode);
            if (!_peers.TryGetValue(route.PeerId, out PeerMixState? peer))
            {
                if (_peers.Count >= MaximumPeers)
                    continue;
                peer = new PeerMixState(leftPan * gain, rightPan * gain, mode, route.Muffled);
                _peers.Add(route.PeerId, peer);
            }
            else
            {
                peer.TargetLeft = leftPan * gain;
                peer.TargetRight = rightPan * gain;
                peer.SetMode(mode);
                peer.SetMuffled(route.Muffled);
            }
            peer.Seen = true;
        }

    }

    public void Mix(IReadOnlyList<DecodedPeerFrame> frames, Span<float> outputStereo, List<ManagedPeerLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(levels);
        if (outputStereo.Length != StereoSamples)
            throw new ArgumentException($"Playback frames must contain exactly {StereoSamples} samples.", nameof(outputStereo));

        outputStereo.Clear();
        levels.Clear();
        int frameCount = Math.Min(frames.Count, MaximumPeers);
        MeasurePeerLevels(frames, frameCount, levels);
        if (_deafened)
            return;

        Array.Clear(_ghostSend);
        Array.Clear(_wallSend);
        bool anyGhost = false;
        bool anyWall = false;
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            DecodedPeerFrame frame = frames[frameIndex];
            float[]? samples = frame.Samples;
            int sampleCount = samples is null ? 0 : Math.Clamp(frame.SampleCount, 0, Math.Min(FrameSamples, samples.Length));

            if (samples is null || sampleCount == 0 || !_peers.TryGetValue(frame.PeerId, out PeerMixState? peer))
                continue;
            if (peer.Mode == FilterMode.Ghost || (peer.TransitionRemaining > 0 && peer.PreviousMode == FilterMode.Ghost))
                anyGhost = true;
            if (peer.Mode == FilterMode.WallMuffle || (peer.TransitionRemaining > 0 && peer.PreviousMode == FilterMode.WallMuffle))
                anyWall = true;

            GainRamp loudnessGain = peer.Loudness.ProcessFrame(samples, sampleCount, frame.MeasurementEligible);

            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                float input = samples[sampleIndex];
                input = float.IsFinite(input) ? Math.Clamp(input, -1f, 1f) : 0f;
                input *= loudnessGain.At(sampleIndex, sampleCount);
                float currentPrimary = ApplyFilter(peer.Mode, ref peer.FilterZ1, ref peer.FilterZ2, input);
                float current = peer.MufflePath.Process(currentPrimary, peer.Muffled, peer.PreviousMuffled,
                    peer.MuffleTransitionRemaining, _muffleLowPass);
                peer.Left += GainGlide * (peer.TargetLeft - peer.Left);
                peer.Right += GainGlide * (peer.TargetRight - peer.Right);

                if (peer.TransitionRemaining == 0)
                {
                    RouteSample(peer.Mode, sampleIndex, current * peer.Left, current * peer.Right, outputStereo);
                    if (peer.MuffleTransitionRemaining > 0)
                        peer.MuffleTransitionRemaining--;
                    continue;
                }

                float previousPrimary = ApplyFilter(peer.PreviousMode, ref peer.PreviousFilterZ1,
                    ref peer.PreviousFilterZ2, input);
                float previous = peer.PreviousPrimaryMufflePath.Process(previousPrimary, peer.Muffled,
                    peer.PreviousMuffled, peer.MuffleTransitionRemaining, _muffleLowPass);
                int completed = FilterTransitionSamples - peer.TransitionRemaining;
                float progress = completed / (FilterTransitionSamples - 1f);
                RouteSample(peer.PreviousMode, sampleIndex, previous * peer.Left * (1f - progress),
                    previous * peer.Right * (1f - progress), outputStereo);
                RouteSample(peer.Mode, sampleIndex, current * peer.Left * progress,
                    current * peer.Right * progress, outputStereo);
                peer.TransitionRemaining--;
                if (peer.MuffleTransitionRemaining > 0)
                    peer.MuffleTransitionRemaining--;
            }
        }

        RenderReverb(outputStereo, anyGhost, anyWall);
        for (int i = 0; i < StereoSamples; i++)
            outputStereo[i] *= _master;
        _outputLimiter.Process(outputStereo);
        for (int i = 0; i < StereoSamples; i += 2)
        {
            SoftLimitStereoPair(outputStereo[i], outputStereo[i + 1], out float limitedLeft, out float limitedRight);
            outputStereo[i] = limitedLeft;
            outputStereo[i + 1] = limitedRight;
        }
    }

    public void RemovePeer(string peerId)
    {
        if (!string.IsNullOrEmpty(peerId))
            _peers.Remove(peerId);
    }

    public void Reset()
    {
        _peers.Clear();
        _deafened = false;
        _master = 1f;
        ResetEffects();
    }

    private static void MeasurePeerLevels(
        IReadOnlyList<DecodedPeerFrame> frames,
        int frameCount,
        List<ManagedPeerLevel> levels)
    {
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            DecodedPeerFrame frame = frames[frameIndex];
            float[]? samples = frame.Samples;
            int sampleCount = samples is null
                ? 0
                : Math.Clamp(frame.SampleCount, 0, Math.Min(FrameSamples, samples.Length));
            float peak = 0f;
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                float sample = samples![sampleIndex];
                if (float.IsFinite(sample))
                    peak = Math.Max(peak, Math.Abs(sample));
            }
            levels.Add(new ManagedPeerLevel(frame.PeerId, Math.Min(peak, 1f)));
        }
    }

    private void RenderReverb(Span<float> outputStereo, bool anyGhost, bool anyWall)
    {
        if (anyGhost)
            _ghostTail = GhostTailSamples;
        if (anyGhost || _ghostTail > 0)
        {
            for (int frame = 0; frame < FrameSamples; frame++)
            {
                int sample = frame * 2;
                float left = _ghostSend[sample];
                float right = _ghostSend[sample + 1];
                float mono = _lowPass1900.Process(ref _ghostLowPassZ1, ref _ghostLowPassZ2, (left + right) * 0.5f);
                _ghostReverb.Process(mono, out float wetLeft, out float wetRight);
                outputStereo[sample] += GhostDry * left + GhostWet * wetLeft;
                outputStereo[sample + 1] += GhostDry * right + GhostWet * wetRight;
            }
            if (!anyGhost)
            {
                _ghostTail = Math.Max(0, _ghostTail - FrameSamples);
                if (_ghostTail == 0)
                {
                    _ghostReverb.Reset();
                    _ghostLowPassZ1 = 0f;
                    _ghostLowPassZ2 = 0f;
                }
            }
        }

        if (anyWall)
            _wallTail = WallTailSamples;
        if (anyWall || _wallTail > 0)
        {
            for (int frame = 0; frame < FrameSamples; frame++)
            {
                int sample = frame * 2;
                float left = _wallSend[sample];
                float right = _wallSend[sample + 1];
                _wallReverb.Process((left + right) * 0.5f, out float wetLeft, out float wetRight);
                outputStereo[sample] += WallDry * left + WallWet * wetLeft;
                outputStereo[sample + 1] += WallDry * right + WallWet * wetRight;
            }
            if (!anyWall)
            {
                _wallTail = Math.Max(0, _wallTail - FrameSamples);
                if (_wallTail == 0)
                    _wallReverb.Reset();
            }
        }
    }

    private void RouteSample(FilterMode mode, int frame, float left, float right, Span<float> outputStereo)
    {
        int sample = frame * 2;
        switch (mode)
        {
            case FilterMode.Ghost:
                _ghostSend[sample] += left;
                _ghostSend[sample + 1] += right;
                break;
            case FilterMode.WallMuffle:
                _wallSend[sample] += left;
                _wallSend[sample + 1] += right;
                break;
            default:
                outputStereo[sample] += left;
                outputStereo[sample + 1] += right;
                break;
        }
    }

    private float ApplyFilter(FilterMode mode, ref float z1, ref float z2, float sample)
    {
        return mode switch
        {
            FilterMode.Radio => MathF.Tanh(_highPass650.Process(ref z1, ref z2, sample) * RadioDrive) * RadioLevel,
            FilterMode.WallMuffle => _lowPass650.Process(ref z1, ref z2, sample),
            _ => sample
        };
    }

    private void ResetEffects()
    {
        _ghostReverb.Reset();
        _wallReverb.Reset();
        Array.Clear(_ghostSend);
        Array.Clear(_wallSend);
        _ghostLowPassZ1 = 0f;
        _ghostLowPassZ2 = 0f;
        _ghostTail = 0;
        _wallTail = 0;
        _outputLimiter.Reset();
        foreach (PeerMixState peer in _peers.Values)
            peer.ResetSignalState();
    }

    private static FilterMode FilterModeFromValue(int mode) => mode switch
    {
        1 => FilterMode.Ghost,
        2 => FilterMode.Radio,
        3 => FilterMode.WallMuffle,
        _ => FilterMode.None
    };

    private static void PanGains(float pan, out float left, out float right)
    {
        float farGain = PanFarSide + (1f - PanFarSide) * MathF.Cos(Math.Abs(pan) * MathF.PI * 0.5f);
        left = pan > 0f ? farGain : 1f;
        right = pan < 0f ? farGain : 1f;
        float normalization = MathF.Sqrt(left * left + right * right);
        left /= normalization;
        right /= normalization;
    }

    private static float SoftLimit(float sample)
    {
        if (!float.IsFinite(sample))
            return 0f;
        float magnitude = Math.Abs(sample);
        if (magnitude <= SoftLimitStart)
            return sample;
        float headroom = 1f - SoftLimitStart;
        float limited = SoftLimitStart + headroom * MathF.Tanh((magnitude - SoftLimitStart) / headroom);
        return MathF.CopySign(Math.Min(limited, 1f), sample);
    }

    private static void SoftLimitStereoPair(float left, float right, out float limitedLeft, out float limitedRight)
    {
        if (!float.IsFinite(left)) left = 0f;
        if (!float.IsFinite(right)) right = 0f;
        float peak = Math.Max(Math.Abs(left), Math.Abs(right));
        if (peak <= SoftLimitStart)
        {
            limitedLeft = left;
            limitedRight = right;
            return;
        }
        float gain = SoftLimit(peak) / peak;
        limitedLeft = Math.Clamp(left * gain, -1f, 1f);
        limitedRight = Math.Clamp(right * gain, -1f, 1f);
    }

    private enum FilterMode { None, Ghost, Radio, WallMuffle }

    private sealed class PeerMixState
    {
        public PeerMixState(float left, float right, FilterMode mode, bool muffled)
        {
            Left = left;
            Right = right;
            TargetLeft = left;
            TargetRight = right;
            Mode = mode;
            PreviousMode = mode;
            Muffled = muffled;
            PreviousMuffled = muffled;
        }

        public float Left;
        public float Right;
        public float TargetLeft;
        public float TargetRight;
        public FilterMode Mode;
        public FilterMode PreviousMode;
        public float FilterZ1;
        public float FilterZ2;
        public float PreviousFilterZ1;
        public float PreviousFilterZ2;
        public int TransitionRemaining;
        public bool Muffled;
        public bool PreviousMuffled;
        public MufflePath MufflePath;
        public MufflePath PreviousPrimaryMufflePath;
        public int MuffleTransitionRemaining;
        public readonly PeerLoudnessNormalizer Loudness = new();
        public bool Seen;

        public void SetMode(FilterMode mode)
        {
            if (Mode == mode) return;
            PreviousMode = Mode;
            PreviousFilterZ1 = FilterZ1;
            PreviousFilterZ2 = FilterZ2;
            PreviousPrimaryMufflePath = MufflePath;
            TransitionRemaining = FilterTransitionSamples;
            Mode = mode;
            FilterZ1 = 0f;
            FilterZ2 = 0f;
        }

        public void SetMuffled(bool muffled)
        {
            if (Muffled == muffled) return;
            PreviousMuffled = Muffled;
            MufflePath.BeginTransition();
            PreviousPrimaryMufflePath.BeginTransition();
            MuffleTransitionRemaining = FilterTransitionSamples;
            Muffled = muffled;
        }

        public void ResetSignalState()
        {
            FilterZ1 = FilterZ2 = PreviousFilterZ1 = PreviousFilterZ2 = 0f;
            TransitionRemaining = 0;
            MufflePath = default;
            PreviousPrimaryMufflePath = default;
            MuffleTransitionRemaining = 0;
            PreviousMode = Mode;
            PreviousMuffled = Muffled;
            Left = TargetLeft;
            Right = TargetRight;
        }
    }

    private struct MufflePath
    {
        private float _currentZ1;
        private float _currentZ2;
        private float _previousZ1;
        private float _previousZ2;

        public void BeginTransition()
        {
            _previousZ1 = _currentZ1;
            _previousZ2 = _currentZ2;
            _currentZ1 = _currentZ2 = 0f;
        }

        public float Process(float input, bool muffled, bool previousMuffled, int transitionRemaining, in Biquad lowPass)
        {
            float current = muffled ? lowPass.Process(ref _currentZ1, ref _currentZ2, input) : input;
            if (transitionRemaining == 0) return current;
            float previous = previousMuffled ? lowPass.Process(ref _previousZ1, ref _previousZ2, input) : input;
            int completed = FilterTransitionSamples - transitionRemaining;
            float progress = completed / (FilterTransitionSamples - 1f);
            return previous * (1f - progress) + current * progress;
        }
    }

    private readonly struct Biquad
    {
        private readonly float _b0;
        private readonly float _b1;
        private readonly float _b2;
        private readonly float _a1;
        private readonly float _a2;

        private Biquad(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
        }

        public static Biquad LowPass(float frequency, float q)
        {
            Coefficients(frequency, q, out float cosine, out float alpha);
            float a0 = 1f + alpha;
            return new Biquad((1f - cosine) * 0.5f / a0, (1f - cosine) / a0,
                (1f - cosine) * 0.5f / a0, -2f * cosine / a0, (1f - alpha) / a0);
        }

        public static Biquad HighPass(float frequency, float q)
        {
            Coefficients(frequency, q, out float cosine, out float alpha);
            float a0 = 1f + alpha;
            return new Biquad((1f + cosine) * 0.5f / a0, -(1f + cosine) / a0,
                (1f + cosine) * 0.5f / a0, -2f * cosine / a0, (1f - alpha) / a0);
        }

        public float Process(ref float z1, ref float z2, float sample)
        {
            float output = _b0 * sample + z1;
            z1 = _b1 * sample - _a1 * output + z2;
            z2 = _b2 * sample - _a2 * output;
            return output;
        }

        private static void Coefficients(float frequency, float q, out float cosine, out float alpha)
        {
            float angularFrequency = 2f * MathF.PI * frequency / SampleRate;
            cosine = MathF.Cos(angularFrequency);
            alpha = MathF.Sin(angularFrequency) / (2f * q);
        }
    }

    private readonly struct GainRamp
    {
        private readonly float _start;
        private readonly float _end;

        public GainRamp(float start, float end)
        {
            _start = start;
            _end = end;
        }

        public float At(int sample, int samples)
        {
            if (samples <= 1) return _end;
            return _start + (_end - _start) * sample / (samples - 1f);
        }
    }

    private sealed class PeerLoudnessNormalizer
    {
        private const int BlockSamples = SampleRate / 10;
        private const int HistoryBlocks = 50;
        private const int MinimumActiveBlocks = 4;
        private const int StableHistoryBlocks = 30;
        private const float TargetSpeechDb = -23f;
        private const float AbsoluteSpeechGateDb = -55f;
        private const float RelativeSpeechGateDb = 10f;
        private const float NormalizerDeadbandDb = 2f;
        private const float MinimumNormalizerGainDb = -18f;
        private const float OrdinaryMakeupLimitDb = 6f;
        private const float ConfidentMakeupLimitDb = 12f;
        private const float MakeupMinimumSnrDb = 15f;
        private const float ConfidentMakeupMinimumSnrDb = 20f;
        private const float MaximumOutputNoiseDb = -45f;
        private const float LevelAttackSeconds = 0.15f;
        private const float LevelReleaseSeconds = 4f;
        private const float OverloadThresholdDb = TargetSpeechDb + 10f;
        private const float OverloadAttackSeconds = 0.03f;
        private const float OverloadReleaseSeconds = 1.5f;
        private const float PeakCeiling = 0.70794576f;
        private const float PeakAttackSeconds = 0.005f;
        private const float PeakReleaseSeconds = 1f;
        private const float DbFloor = -120f;

        private KWeighting _weighting = new();
        private readonly float[] _historyLevels = new float[HistoryBlocks];
        private readonly bool[] _historySpeech = new bool[HistoryBlocks];
        private readonly float[] _percentileScratch = new float[HistoryBlocks];
        private int _historyCount;
        private int _historyNext;
        private double _blockWeightedSquareSum;
        private double _blockRawSquareSum;
        private double _blockCorrelationSum;
        private float _blockPeak;
        private int _blockNearRail;
        private int _blockFlatRail;
        private int _blockZeroCrossings;
        private int _blockSamples;
        private float _previousRaw;
        private float _normalizerGainDb;
        private float _desiredNormalizerGainDb;
        private float _overloadGainDb = ConfidentMakeupLimitDb;
        private float _desiredOverloadGainDb = ConfidentMakeupLimitDb;
        private float _peerPeakGain = 1f;
        private float _noiseLevelDb = DbFloor;
        private float _clippingConfidence;

        public PeerLoudnessNormalizer()
        {
            Array.Fill(_historyLevels, DbFloor);
        }

        public GainRamp ProcessFrame(float[] samples, int count, bool measurementEligible)
        {
            float startGain = CombinedGain();
            float framePeak = 0f;
            for (int i = 0; i < count; i++)
            {
                float sample = samples[i];
                sample = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
                framePeak = Math.Max(framePeak, Math.Abs(sample));
                if (measurementEligible)
                    ObserveSample(sample);
                else
                {
                    _weighting.Process(sample);
                    _previousRaw = sample;
                }
            }

            float duration = count / (float)SampleRate;
            if (measurementEligible)
            {
                _normalizerGainDb = Smooth(
                    _normalizerGainDb,
                    _desiredNormalizerGainDb,
                    _desiredNormalizerGainDb < _normalizerGainDb ? LevelAttackSeconds : LevelReleaseSeconds,
                    duration);
                _overloadGainDb = Smooth(
                    _overloadGainDb,
                    _desiredOverloadGainDb,
                    _desiredOverloadGainDb < _overloadGainDb ? OverloadAttackSeconds : OverloadReleaseSeconds,
                    duration);
            }

            float peakAfterLeveling = framePeak * DbToGain(LevelGainDb());
            float desiredPeakGain = peakAfterLeveling > PeakCeiling ? PeakCeiling / peakAfterLeveling : 1f;
            _peerPeakGain = Math.Clamp(
                Smooth(
                    _peerPeakGain,
                    desiredPeakGain,
                    desiredPeakGain < _peerPeakGain ? PeakAttackSeconds : PeakReleaseSeconds,
                    duration),
                0f,
                1f);
            return new GainRamp(startGain, CombinedGain());
        }

        private void ObserveSample(float sample)
        {
            float weighted = _weighting.Process(sample);
            if (!float.IsFinite(weighted)) weighted = 0f;
            _blockWeightedSquareSum += (double)weighted * weighted;
            _blockRawSquareSum += (double)sample * sample;
            _blockCorrelationSum += (double)_previousRaw * sample;
            _blockPeak = Math.Max(_blockPeak, Math.Abs(sample));
            if (Math.Abs(sample) >= 0.98f)
            {
                _blockNearRail++;
                if (Math.Abs(_previousRaw) >= 0.98f &&
                    MathF.CopySign(1f, sample) == MathF.CopySign(1f, _previousRaw) &&
                    Math.Abs(sample - _previousRaw) <= 0.0001f)
                    _blockFlatRail++;
            }
            if (sample != 0f && _previousRaw != 0f &&
                MathF.CopySign(1f, sample) != MathF.CopySign(1f, _previousRaw))
                _blockZeroCrossings++;
            _previousRaw = sample;
            if (++_blockSamples >= BlockSamples)
                FinishBlock();
        }

        private void FinishBlock()
        {
            double samples = Math.Max(_blockSamples, 1);
            double weightedMeanSquare = _blockWeightedSquareSum / samples;
            double rawMeanSquare = _blockRawSquareSum / samples;
            float levelDb = MeanSquareToDb(weightedMeanSquare);
            float rawRms = (float)Math.Sqrt(rawMeanSquare);
            float crestDb = rawRms > 0f ? 20f * MathF.Log10(Math.Max(_blockPeak / rawRms, 1f)) : 0f;
            float correlation = _blockRawSquareSum > 1e-12
                ? (float)(_blockCorrelationSum / _blockRawSquareSum)
                : 0f;
            float zeroCrossRate = _blockZeroCrossings / (float)Math.Max(_blockSamples, 2);
            bool speechLike = levelDb >= AbsoluteSpeechGateDb &&
                correlation > 0.05f &&
                zeroCrossRate < 0.35f &&
                crestDb >= 2f &&
                crestDb <= 30f;
            float nearRailRatio = _blockNearRail / (float)Math.Max(_blockSamples, 1);
            float flatRailRatio = _blockFlatRail / (float)Math.Max(_blockSamples, 1);
            if (nearRailRatio >= 0.01f && flatRailRatio >= 0.0005f)
                _clippingConfidence += (1f - _clippingConfidence) * 0.25f;
            else
                _clippingConfidence *= 0.98f;

            PushHistory(levelDb, speechLike);
            UpdateTargets(levelDb);
            _blockWeightedSquareSum = 0;
            _blockRawSquareSum = 0;
            _blockCorrelationSum = 0;
            _blockPeak = 0f;
            _blockNearRail = 0;
            _blockFlatRail = 0;
            _blockZeroCrossings = 0;
            _blockSamples = 0;
        }

        private void PushHistory(float levelDb, bool speechLike)
        {
            _historyLevels[_historyNext] = Math.Clamp(levelDb, DbFloor, 6f);
            _historySpeech[_historyNext] = speechLike;
            _historyNext = (_historyNext + 1) % HistoryBlocks;
            _historyCount = Math.Min(_historyCount + 1, HistoryBlocks);
        }

        private void UpdateTargets(float latestLevelDb)
        {
            _noiseLevelDb = PercentileAll(0.2f);
            float activeGate = Math.Max(AbsoluteSpeechGateDb, _noiseLevelDb + RelativeSpeechGateDb);
            float activeLevelDb = PercentileSpeech(activeGate, 0.65f, out int activeBlocks);
            if (activeBlocks >= MinimumActiveBlocks)
            {
                float desired = TargetSpeechDb - activeLevelDb;
                if (Math.Abs(desired) <= NormalizerDeadbandDb) desired = 0f;
                if (desired > 0f)
                {
                    float snrDb = activeLevelDb - _noiseLevelDb;
                    bool confident = _historyCount >= StableHistoryBlocks &&
                        activeBlocks >= StableHistoryBlocks / 2 &&
                        snrDb >= ConfidentMakeupMinimumSnrDb;
                    float makeupLimit = confident ? ConfidentMakeupLimitDb : OrdinaryMakeupLimitDb;
                    float noiseLimitedGain = Math.Max(MaximumOutputNoiseDb - _noiseLevelDb, 0f);
                    desired = snrDb < MakeupMinimumSnrDb || _clippingConfidence >= 0.5f
                        ? 0f
                        : Math.Min(Math.Min(desired, makeupLimit), noiseLimitedGain);
                }
                _desiredNormalizerGainDb = Math.Clamp(desired, MinimumNormalizerGainDb, ConfidentMakeupLimitDb);
            }
            _desiredOverloadGainDb = latestLevelDb >= OverloadThresholdDb
                ? Math.Clamp(TargetSpeechDb - latestLevelDb, MinimumNormalizerGainDb, 0f)
                : ConfidentMakeupLimitDb;
        }

        private float PercentileAll(float percentile)
        {
            if (_historyCount == 0) return DbFloor;
            Array.Copy(_historyLevels, _percentileScratch, _historyCount);
            SortScratch(_historyCount);
            int position = (int)MathF.Floor((_historyCount - 1) * Math.Clamp(percentile, 0f, 1f) + 0.5f);
            return _percentileScratch[Math.Min(position, _historyCount - 1)];
        }

        private float PercentileSpeech(float thresholdDb, float percentile, out int count)
        {
            count = 0;
            for (int i = 0; i < _historyCount; i++)
            {
                if (_historySpeech[i] && _historyLevels[i] >= thresholdDb)
                    _percentileScratch[count++] = _historyLevels[i];
            }
            if (count == 0) return DbFloor;
            SortScratch(count);
            int position = (int)MathF.Floor((count - 1) * Math.Clamp(percentile, 0f, 1f) + 0.5f);
            return _percentileScratch[Math.Min(position, count - 1)];
        }

        private void SortScratch(int count)
        {
            for (int i = 1; i < count; i++)
            {
                float value = _percentileScratch[i];
                int position = i;
                while (position > 0 && _percentileScratch[position - 1] > value)
                {
                    _percentileScratch[position] = _percentileScratch[position - 1];
                    position--;
                }
                _percentileScratch[position] = value;
            }
        }

        private float LevelGainDb() => Math.Clamp(
            Math.Min(_normalizerGainDb, _overloadGainDb),
            MinimumNormalizerGainDb,
            ConfidentMakeupLimitDb);

        private float CombinedGain() => Math.Clamp(DbToGain(LevelGainDb()) * _peerPeakGain, 0f, 4f);

        private static float MeanSquareToDb(double meanSquare) =>
            !double.IsFinite(meanSquare) || meanSquare <= 1e-12
                ? DbFloor
                : (float)(10 * Math.Log10(meanSquare));

        private static float DbToGain(float db) => MathF.Pow(10f, db / 20f);
    }

    private struct FixedBiquad
    {
        private readonly float _b0;
        private readonly float _b1;
        private readonly float _b2;
        private readonly float _a1;
        private readonly float _a2;
        private float _z1;
        private float _z2;

        public FixedBiquad(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; _z1 = 0f; _z2 = 0f;
        }

        public float Process(float input)
        {
            float output = _b0 * input + _z1;
            _z1 = _b1 * input - _a1 * output + _z2;
            _z2 = _b2 * input - _a2 * output;
            return output;
        }
    }

    private struct KWeighting
    {
        private FixedBiquad _shelf = new(
            1.5351249f, -2.6916962f, 1.1983929f, -1.6906593f, 0.73248076f);
        private FixedBiquad _highPass = new(1f, -2f, 1f, -1.9900475f, 0.99007225f);

        public KWeighting()
        {
        }

        public float Process(float sample) => _highPass.Process(_shelf.Process(sample));
    }

    private sealed class LookaheadLimiter
    {
        private const int LookaheadFrames = SampleRate / 200;
        private const float Ceiling = 0.8912509f;
        private const float AttackSeconds = 0.0005f;
        private const float ReleaseSeconds = 0.1f;
        private readonly float[] _delayLeft = new float[LookaheadFrames];
        private readonly float[] _delayRight = new float[LookaheadFrames];
        private readonly float[] _maxPeaks = new float[LookaheadFrames + 1];
        private readonly ulong[] _maxIndices = new ulong[LookaheadFrames + 1];
        private int _maxHead;
        private int _maxLength;
        private ulong _sampleIndex;
        private int _write;
        private float _gain = 1f;
        private float _previousLeft0;
        private float _previousLeft1;
        private float _previousLeft2;
        private float _previousRight0;
        private float _previousRight1;
        private float _previousRight2;

        public void Process(Span<float> stereo)
        {
            for (int frame = 0; frame < stereo.Length / 2; frame++)
            {
                int sample = frame * 2;
                float inputLeft = float.IsFinite(stereo[sample]) ? stereo[sample] : 0f;
                float inputRight = float.IsFinite(stereo[sample + 1]) ? stereo[sample + 1] : 0f;
                float delayedLeft = _delayLeft[_write];
                float delayedRight = _delayRight[_write];
                _delayLeft[_write] = inputLeft;
                _delayRight[_write] = inputRight;
                float insertedPeak = EstimateInsertedPeak(inputLeft, inputRight);
                if (++_write == LookaheadFrames) _write = 0;
                float detectedPeak = ObservePeak(insertedPeak);
                float desiredGain = detectedPeak > Ceiling ? Ceiling / detectedPeak : 1f;
                _gain = Math.Clamp(
                    Smooth(
                        _gain,
                        desiredGain,
                        desiredGain < _gain ? AttackSeconds : ReleaseSeconds,
                        1f / SampleRate),
                    0f,
                    1f);
                stereo[sample] = Math.Clamp(delayedLeft * _gain, -1f, 1f);
                stereo[sample + 1] = Math.Clamp(delayedRight * _gain, -1f, 1f);
            }
        }

        public void Reset()
        {
            Array.Clear(_delayLeft);
            Array.Clear(_delayRight);
            Array.Clear(_maxPeaks);
            Array.Clear(_maxIndices);
            _maxHead = 0;
            _maxLength = 0;
            _sampleIndex = 0;
            _write = 0;
            _gain = 1f;
            _previousLeft0 = _previousLeft1 = _previousLeft2 = 0f;
            _previousRight0 = _previousRight1 = _previousRight2 = 0f;
        }

        private float ObservePeak(float peak)
        {
            int capacity = _maxPeaks.Length;
            ulong oldest = _sampleIndex > LookaheadFrames ? _sampleIndex - LookaheadFrames : 0;
            while (_maxLength > 0 && _maxIndices[_maxHead] < oldest)
            {
                _maxHead = (_maxHead + 1) % capacity;
                _maxLength--;
            }
            while (_maxLength > 0)
            {
                int back = (_maxHead + _maxLength - 1) % capacity;
                if (_maxPeaks[back] > peak) break;
                _maxLength--;
            }
            int tail = (_maxHead + _maxLength) % capacity;
            _maxPeaks[tail] = peak;
            _maxIndices[tail] = _sampleIndex;
            _maxLength++;
            if (_sampleIndex != ulong.MaxValue) _sampleIndex++;
            return _maxPeaks[_maxHead];
        }

        private float EstimateInsertedPeak(float left, float right)
        {
            float leftPeak = CubicSegmentPeak(_previousLeft0, _previousLeft1, _previousLeft2, left);
            float rightPeak = CubicSegmentPeak(_previousRight0, _previousRight1, _previousRight2, right);
            _previousLeft0 = _previousLeft1;
            _previousLeft1 = _previousLeft2;
            _previousLeft2 = left;
            _previousRight0 = _previousRight1;
            _previousRight1 = _previousRight2;
            _previousRight2 = right;
            return Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), Math.Max(leftPeak, rightPeak));
        }

        private static float CubicSegmentPeak(float y0, float y1, float y2, float y3)
        {
            float peak = Math.Max(Math.Abs(y1), Math.Abs(y2));
            for (int quarter = 1; quarter <= 3; quarter++)
            {
                float t = quarter * 0.25f;
                float t2 = t * t;
                float t3 = t2 * t;
                float sample = 0.5f * (2f * y1 + (-y0 + y2) * t +
                    (2f * y0 - 5f * y1 + 4f * y2 - y3) * t2 +
                    (-y0 + 3f * y1 - 3f * y2 + y3) * t3);
                peak = Math.Max(peak, Math.Abs(sample));
            }
            return float.IsFinite(peak) ? peak : 0f;
        }
    }

    private static float Smooth(float current, float target, float seconds, float duration)
    {
        if (!float.IsFinite(current) || !float.IsFinite(target)) return target;
        if (seconds <= 0f || duration <= 0f) return target;
        float alpha = 1f - MathF.Exp(-duration / seconds);
        return current + (target - current) * Math.Clamp(alpha, 0f, 1f);
    }

    private sealed class Reverb
    {
        private const float Damp1 = 0.2f;
        private const float Damp2 = 0.8f;
        private const float AllPassFeedback = 0.5f;
        private const float InputGain = 0.5f;
        private readonly float _feedback;
        private readonly float[][] _combLeft;
        private readonly float[][] _combRight;
        private readonly int[] _combIndexLeft;
        private readonly int[] _combIndexRight;
        private readonly float[] _filterLeft;
        private readonly float[] _filterRight;
        private readonly float[][] _allPassLeft;
        private readonly float[][] _allPassRight;
        private readonly int[] _allPassIndexLeft;
        private readonly int[] _allPassIndexRight;

        public Reverb(int[] combLengths, int[] allPassLengths, float feedback, int spread)
        {
            _feedback = feedback;
            _combLeft = AllocateLines(combLengths, 0);
            _combRight = AllocateLines(combLengths, spread);
            _combIndexLeft = new int[combLengths.Length];
            _combIndexRight = new int[combLengths.Length];
            _filterLeft = new float[combLengths.Length];
            _filterRight = new float[combLengths.Length];
            _allPassLeft = AllocateLines(allPassLengths, 0);
            _allPassRight = AllocateLines(allPassLengths, spread);
            _allPassIndexLeft = new int[allPassLengths.Length];
            _allPassIndexRight = new int[allPassLengths.Length];
        }

        public void Process(float input, out float left, out float right)
        {
            float scaled = input * InputGain;
            left = right = 0f;
            for (int i = 0; i < _combLeft.Length; i++)
            {
                left += ProcessComb(_combLeft[i], ref _combIndexLeft[i], ref _filterLeft[i], scaled, _feedback);
                right += ProcessComb(_combRight[i], ref _combIndexRight[i], ref _filterRight[i], scaled, _feedback);
            }
            for (int i = 0; i < _allPassLeft.Length; i++)
            {
                left = ProcessAllPass(_allPassLeft[i], ref _allPassIndexLeft[i], left);
                right = ProcessAllPass(_allPassRight[i], ref _allPassIndexRight[i], right);
            }
        }

        public void Reset()
        {
            ClearLines(_combLeft); ClearLines(_combRight); ClearLines(_allPassLeft); ClearLines(_allPassRight);
            Array.Clear(_combIndexLeft); Array.Clear(_combIndexRight);
            Array.Clear(_allPassIndexLeft); Array.Clear(_allPassIndexRight);
            Array.Clear(_filterLeft); Array.Clear(_filterRight);
        }

        private static float[][] AllocateLines(int[] lengths, int spread)
        {
            var lines = new float[lengths.Length][];
            for (int i = 0; i < lengths.Length; i++) lines[i] = new float[lengths[i] + spread];
            return lines;
        }

        private static float ProcessComb(float[] buffer, ref int index, ref float store, float input, float feedback)
        {
            float output = buffer[index];
            store = output * Damp2 + store * Damp1;
            buffer[index] = input + store * feedback;
            if (++index == buffer.Length) index = 0;
            return output;
        }

        private static float ProcessAllPass(float[] buffer, ref int index, float input)
        {
            float delayed = buffer[index];
            float output = delayed - input;
            buffer[index] = input + delayed * AllPassFeedback;
            if (++index == buffer.Length) index = 0;
            return output;
        }

        private static void ClearLines(float[][] lines)
        {
            for (int i = 0; i < lines.Length; i++) Array.Clear(lines[i]);
        }
    }
}
