using System.Collections.Concurrent;
using System.Diagnostics;
using PerfectComms.Starlight.Media;
using Xunit;

namespace PerfectComms.Starlight.Tests;

public sealed class ManagedVoiceEngineTests
{
    [Fact]
    public void MicDeactivationDiscardsAPartialCaptureFrame()
    {
        using var engine = new ManagedVoiceEngine();
        var capture = new float[ManagedOpusEncoder.FrameSamples];
        FillSignal(capture, 0, 440f, 0.2f);

        Assert.True(engine.Start());
        engine.SetInput(1f, 0f, 0f);
        engine.SetMicActive(true);
        engine.PushMic(capture, ManagedOpusEncoder.FrameSamples / 2, 0);
        Assert.Equal(0L, engine.EncodedFrames);

        engine.SetMicActive(false);
        engine.SetMicActive(true);
        engine.PushMic(capture, ManagedOpusEncoder.FrameSamples / 2, 0);
        Assert.Equal(0L, engine.EncodedFrames);

        engine.PushMic(capture, ManagedOpusEncoder.FrameSamples / 2, 0);
        Assert.Equal(1L, engine.EncodedFrames);
    }

    [Fact]
    public async Task ConcurrentStartAndDisposeCompletesWithTheEngineDisposed()
    {
        var engine = new ManagedVoiceEngine();
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        Task<bool> start = Task.Run(() =>
        {
            ready.Signal();
            release.Wait(TestContext.Current.CancellationToken);
            return engine.Start();
        }, TestContext.Current.CancellationToken);
        Task dispose = Task.Run(() =>
        {
            ready.Signal();
            release.Wait(TestContext.Current.CancellationToken);
            engine.Dispose();
        }, TestContext.Current.CancellationToken);

        bool tasksReady = ready.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.StartNew();
        release.Set();
        try
        {
            Assert.True(tasksReady);
            await Task.WhenAll(start, dispose).WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
            Assert.False(engine.IsRunning);
            Assert.False(engine.Start());
        }
        finally
        {
            release.Set();
            engine.Dispose();
        }
    }

    [Fact]
    public void DeafeningDiscardsAlreadyQueuedPlayback()
    {
        using var engine = new ManagedVoiceEngine();
        var queued = new float[ManagedOpusEncoder.FrameSamples * 2 * 4];
        Array.Fill(queued, 0.5f);
        engine.ConfigureGameState(false, 1f, []);
        engine.QueuePlaybackForTest(queued);
        Assert.Equal(queued.Length, engine.PlaybackDepthSamples);

        engine.ConfigureGameState(true, 1f, []);

        Assert.Equal(0, engine.PlaybackDepthSamples);
        var playback = new float[ManagedOpusEncoder.FrameSamples * 2];
        engine.ReadPlayback(playback, playback.Length);
        Assert.All(playback, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void EventQueueDropsTelemetryAndFailsOnCriticalOverflowAtItsExactCapacity()
    {
        var engine = new ManagedVoiceEngine();
        using var handlerEntered = new ManualResetEventSlim();
        using var releaseHandler = new ManualResetEventSlim();
        engine.LocalLevel += (_, _) =>
        {
            handlerEntered.Set();
            releaseHandler.Wait(TestContext.Current.CancellationToken);
        };

        try
        {
            Assert.True(engine.Start());
            Assert.True(engine.EnqueueLocalLevelForTest());
            Assert.True(handlerEntered.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));

            for (int i = 0; i < ManagedVoiceEngine.MaximumPendingEvents; i++)
                Assert.True(engine.EnqueueLocalLevelForTest());

            Assert.Equal(ManagedVoiceEngine.MaximumPendingEvents, engine.PendingEventCount);
            Assert.False(engine.EnqueueLocalLevelForTest());
            Assert.Equal(ManagedVoiceEngine.MaximumPendingEvents, engine.PendingEventCount);
            Assert.False(engine.EnqueuePeerStateForTest());
            Assert.False(engine.IsRunning);
            Assert.False(engine.Start());

            releaseHandler.Set();
            Assert.True(SpinWait.SpinUntil(
                () => engine.PendingEventCount == 0,
                TimeSpan.FromSeconds(2)));
        }
        finally
        {
            releaseHandler.Set();
            engine.Dispose();
        }
    }

    [Fact]
    public async Task TwoManagedEnginesNegotiateAndPlayOpusBidirectionally()
    {
        using var engineA = new ManagedVoiceEngine();
        using var engineB = new ManagedVoiceEngine();
        var connectedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signalingFailures = new ConcurrentQueue<string>();
        var states = new ConcurrentQueue<string>();
        var descriptions = new ConcurrentBag<string>();
        int candidatesFromA = 0;
        int candidatesFromB = 0;
        int explicitEndCandidates = 0;

        engineA.LocalSdp += (_, generation, type, sdp) =>
        {
            descriptions.Add(sdp);
            if (!engineB.SetRemoteSdp("a", generation, type, sdp))
                signalingFailures.Enqueue($"B rejected A {type} SDP.");
        };
        engineB.LocalSdp += (_, generation, type, sdp) =>
        {
            descriptions.Add(sdp);
            if (!engineA.SetRemoteSdp("b", generation, type, sdp))
                signalingFailures.Enqueue($"A rejected B {type} SDP.");
        };
        engineA.LocalCandidate += (_, generation, candidate) =>
        {
            Interlocked.Increment(ref candidatesFromA);
            if (candidate.Length == 0)
                Interlocked.Increment(ref explicitEndCandidates);
            if (!engineB.AddIceCandidate("a", generation, candidate))
                signalingFailures.Enqueue("B rejected an A ICE candidate.");
        };
        engineB.LocalCandidate += (_, generation, candidate) =>
        {
            Interlocked.Increment(ref candidatesFromB);
            if (candidate.Length == 0)
                Interlocked.Increment(ref explicitEndCandidates);
            if (!engineA.AddIceCandidate("b", generation, candidate))
                signalingFailures.Enqueue("A rejected a B ICE candidate.");
        };
        engineA.PeerState += (_, _, state) =>
        {
            states.Enqueue($"A:{state}");
            if (state == "connected")
                connectedA.TrySetResult();
        };
        engineB.PeerState += (_, _, state) =>
        {
            states.Enqueue($"B:{state}");
            if (state == "connected")
                connectedB.TrySetResult();
        };

        Assert.True(engineA.Start());
        Assert.True(engineB.Start());
        engineA.ConfigureGameState(false, 1f, [new ManagedPeerRoute("b", 1f, 0f, 0, false)]);
        engineB.ConfigureGameState(false, 1f, [new ManagedPeerRoute("a", 1f, 0f, 0, false)]);
        Assert.True(engineB.AddPeer("a", false, 1));
        Assert.True(engineA.AddPeer("b", true, 1));

        Task connection = Task.WhenAll(connectedA.Task, connectedB.Task);
        Task connectionDeadline = Task.Delay(
            TimeSpan.FromSeconds(12),
            TestContext.Current.CancellationToken);
        Task connectionOutcome = await Task.WhenAny(connection, connectionDeadline);
        Assert.True(
            ReferenceEquals(connectionOutcome, connection),
            $"Connection timed out. States: {string.Join(", ", states)}");
        await connection;

        Assert.Empty(signalingFailures);
        Assert.True(Volatile.Read(ref candidatesFromA) > 0);
        Assert.True(Volatile.Read(ref candidatesFromB) > 0);
        Assert.Equal(0, Volatile.Read(ref explicitEndCandidates));
        Assert.Contains(descriptions, sdp =>
            sdp.Contains("a=rtpmap:111 opus/48000/2", StringComparison.OrdinalIgnoreCase));

        engineA.SetInput(1f, 0f, 0f);
        engineB.SetInput(1f, 0f, 0f);
        engineA.SetMicActive(true);
        engineB.SetMicActive(true);
        var captureA = new float[ManagedOpusEncoder.FrameSamples];
        var captureB = new float[ManagedOpusEncoder.FrameSamples];
        var playbackA = new float[ManagedOpusEncoder.FrameSamples * 2];
        var playbackB = new float[ManagedOpusEncoder.FrameSamples * 2];
        double energyAtA = 0d;
        double energyAtB = 0d;
        int frameIndex = 0;
        var mediaDeadline = Stopwatch.StartNew();

        while (mediaDeadline.Elapsed < TimeSpan.FromSeconds(6) &&
               (energyAtA < 0.1d || energyAtB < 0.1d ||
                engineA.ReceivedPackets < 8 || engineB.ReceivedPackets < 8))
        {
            int offset = frameIndex * ManagedOpusEncoder.FrameSamples;
            FillSignal(captureA, offset, 440f, 0.18f);
            FillSignal(captureB, offset, 659f, 0.16f);
            ulong captureGap = frameIndex switch { 2 => 2ul, 5 => 6ul, _ => 0ul };
            engineA.PushMic(captureA, captureA.Length, captureGap);
            engineB.PushMic(captureB, captureB.Length, 0);
            await Task.Delay(20, TestContext.Current.CancellationToken);
            engineA.ReadPlayback(playbackA, playbackA.Length);
            engineB.ReadPlayback(playbackB, playbackB.Length);
            energyAtA += Energy(playbackA);
            energyAtB += Energy(playbackB);
            frameIndex++;
        }

        engineA.SetMicActive(false);
        engineB.SetMicActive(false);

        Assert.Empty(signalingFailures);
        Assert.True(engineA.SentPackets >= 8, $"A sent {engineA.SentPackets} packets.");
        Assert.True(engineB.SentPackets >= 8, $"B sent {engineB.SentPackets} packets.");
        Assert.True(engineA.SentPackets <= frameIndex, $"A sent {engineA.SentPackets} packets for {frameIndex} captured frames.");
        Assert.True(engineA.ReceivedPackets >= 8, $"A received {engineA.ReceivedPackets} packets.");
        Assert.True(engineB.ReceivedPackets >= 8, $"B received {engineB.ReceivedPackets} packets.");
        Assert.True(energyAtA > 0.1d, $"A playback energy was {energyAtA:F6}.");
        Assert.True(energyAtB > 0.1d, $"B playback energy was {energyAtB:F6}.");
    }

    [Fact]
    public async Task IceServerCredentialRefreshFailsExistingPeersForRecreation()
    {
        using var engine = new ManagedVoiceEngine();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int failures = 0;
        engine.PeerState += (peerId, generation, state) =>
        {
            if (peerId == "refresh-peer" && generation == 1 && state == "failed")
            {
                Interlocked.Increment(ref failures);
                failed.TrySetResult();
            }
        };

        Assert.True(engine.Start());
        ManagedIceServer initial = new("turn:relay.example.test", "user", "credential-one");
        engine.SetIceServers([initial]);
        Assert.True(engine.AddPeer("refresh-peer", false, 1));

        engine.SetIceServers([initial]);
        Assert.False(failed.Task.IsCompleted);

        engine.SetIceServers([
            new ManagedIceServer("turn:relay.example.test", "user", "credential-two")
        ]);
        await failed.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref failures));
    }

    private static void FillSignal(float[] samples, int sampleOffset, float frequency, float amplitude)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            double phase = Math.Tau * frequency * (sampleOffset + i) / ManagedOpusEncoder.SampleRate;
            samples[i] = amplitude * (float)Math.Sin(phase);
        }
    }

    private static double Energy(float[] samples)
    {
        double energy = 0d;
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.True(float.IsFinite(samples[i]));
            energy += samples[i] * samples[i];
        }
        return energy;
    }
}
