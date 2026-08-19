using PerfectComms.Starlight.Media;
using Xunit;

namespace PerfectComms.Starlight.Tests;

public sealed class ManagedVoiceMixerTests
{
    [Fact]
    public void RouteGainMasterAndPanControlTheExpectedChannels()
    {
        float[] signal = CreateSignal(0.08f, 523f);
        var fullMixer = new ManagedVoiceMixer();
        var attenuatedMixer = new ManagedVoiceMixer();
        ManagedPeerRoute[] fullRoute = [new("peer", 1f, 1f, 0, false)];
        ManagedPeerRoute[] attenuatedRoute = [new("peer", 0.5f, 1f, 0, false)];
        fullMixer.Configure(false, 1f, fullRoute);
        attenuatedMixer.Configure(false, 0.5f, attenuatedRoute);

        float[] full = RenderSteady(fullMixer, signal);
        float[] attenuated = RenderSteady(attenuatedMixer, signal);
        double fullLeft = MeanChannelMagnitude(full, 0);
        double fullRight = MeanChannelMagnitude(full, 1);
        double attenuatedRight = MeanChannelMagnitude(attenuated, 1);

        Assert.True(fullRight > fullLeft * 3.5d, $"Left={fullLeft:F6}, right={fullRight:F6}");
        Assert.InRange(attenuatedRight / fullRight, 0.245d, 0.255d);
    }

    [Fact]
    public void DeafeningClearsRoutesLimiterDelayAndEffectTails()
    {
        var mixer = new ManagedVoiceMixer();
        var levels = new List<ManagedPeerLevel>(1);
        var output = new float[ManagedOpusEncoder.FrameSamples * 2];
        float[] signal = CreateSignal(0.25f, 317f);
        DecodedPeerFrame[] frames = [new("peer", signal, signal.Length, true)];
        ManagedPeerRoute[] ghostRoute = [new("peer", 1f, 0f, 1, false)];
        mixer.Configure(false, 1f, ghostRoute);

        for (int i = 0; i < 4; i++)
            mixer.Mix(frames, output, levels);
        Assert.Contains(output, sample => Math.Abs(sample) > 0.0001f);

        mixer.Configure(true, 1f, Array.Empty<ManagedPeerRoute>());
        mixer.Mix(Array.Empty<DecodedPeerFrame>(), output, levels);
        Assert.All(output, sample => Assert.Equal(0f, sample));

        mixer.Configure(false, 1f, Array.Empty<ManagedPeerRoute>());
        mixer.Mix(Array.Empty<DecodedPeerFrame>(), output, levels);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void FilterChangesCrossfadeFromThePreviousRoute()
    {
        var mixer = new ManagedVoiceMixer();
        var levels = new List<ManagedPeerLevel>(1);
        var output = new float[ManagedOpusEncoder.FrameSamples * 2];
        var constant = new float[ManagedOpusEncoder.FrameSamples];
        Array.Fill(constant, 0.1f);
        DecodedPeerFrame[] frames = [new("peer", constant, constant.Length, true)];
        mixer.Configure(false, 1f, [new ManagedPeerRoute("peer", 1f, 0f, 0, false)]);

        for (int i = 0; i < 3; i++)
            mixer.Mix(frames, output, levels);
        float baseline = output[400 * 2];

        mixer.Configure(false, 1f, [new ManagedPeerRoute("peer", 1f, 0f, 1, false)]);
        mixer.Mix(frames, output, levels);
        float transitionStart = output[240 * 2];
        float transitionEnd = output[(240 + 95) * 2];

        Assert.True(baseline > 0.05f);
        Assert.InRange(transitionStart / baseline, 0.9f, 1.1f);
        Assert.InRange(transitionEnd / baseline, 0.5f, 0.7f);
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void InvalidAndOverdrivenPeerSamplesRemainFiniteAndBounded()
    {
        var mixer = new ManagedVoiceMixer();
        var levels = new List<ManagedPeerLevel>(32);
        var output = new float[ManagedOpusEncoder.FrameSamples * 2];
        var hostile = new float[ManagedOpusEncoder.FrameSamples];
        for (int i = 0; i < hostile.Length; i++)
        {
            hostile[i] = i % 4 switch
            {
                0 => float.NaN,
                1 => float.PositiveInfinity,
                2 => float.NegativeInfinity,
                _ => 10f
            };
        }

        var frames = new DecodedPeerFrame[32];
        for (int i = 0; i < frames.Length; i++)
            frames[i] = new DecodedPeerFrame("peer", hostile, hostile.Length, true);
        mixer.Configure(false, 2f, [new ManagedPeerRoute("peer", 4f, 0f, 0, false)]);
        mixer.Mix(frames, output, levels);
        mixer.Mix(frames, output, levels);

        Assert.Equal(32, levels.Count);
        Assert.All(levels, level => Assert.Equal(1f, level.Peak));
        Assert.Contains(output, sample => sample != 0f);
        Assert.All(output, sample =>
        {
            Assert.True(float.IsFinite(sample));
            Assert.InRange(sample, -1f, 1f);
        });
    }

    [Fact]
    public void WarmedMixDoesNotAllocate()
    {
        var mixer = new ManagedVoiceMixer();
        var levels = new List<ManagedPeerLevel>(1);
        var output = new float[ManagedOpusEncoder.FrameSamples * 2];
        float[] signal = CreateSignal(0.08f, 419f);
        DecodedPeerFrame[] frames = [new("peer", signal, signal.Length, true)];
        mixer.Configure(false, 1f, [new ManagedPeerRoute("peer", 1f, -0.35f, 2, true)]);

        for (int i = 0; i < 16; i++)
            mixer.Mix(frames, output, levels);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++)
            mixer.Mix(frames, output, levels);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Single(levels);
    }

    private static float[] RenderSteady(ManagedVoiceMixer mixer, float[] signal)
    {
        var output = new float[ManagedOpusEncoder.FrameSamples * 2];
        var levels = new List<ManagedPeerLevel>(1);
        DecodedPeerFrame[] frames = [new("peer", signal, signal.Length, true)];
        for (int i = 0; i < 3; i++)
            mixer.Mix(frames, output, levels);
        return output;
    }

    private static double MeanChannelMagnitude(float[] stereo, int channel)
    {
        double sum = 0d;
        for (int frame = 240; frame < ManagedOpusEncoder.FrameSamples; frame++)
            sum += Math.Abs(stereo[frame * 2 + channel]);
        return sum / (ManagedOpusEncoder.FrameSamples - 240);
    }

    private static float[] CreateSignal(float amplitude, float frequency)
    {
        var signal = new float[ManagedOpusEncoder.FrameSamples];
        for (int i = 0; i < signal.Length; i++)
            signal[i] = amplitude * MathF.Sin(MathF.Tau * frequency * i / ManagedOpusEncoder.SampleRate);
        return signal;
    }
}
