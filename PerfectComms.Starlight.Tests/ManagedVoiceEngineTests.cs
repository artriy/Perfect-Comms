using System.Collections.Concurrent;
using System.Diagnostics;
using PerfectComms.Starlight.Media;
using VoiceChatPlugin.VoiceChat;
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
    public async Task MobileClientReapsATimedOutEngineExactlyOnceAfterItsActiveCallReturns()
    {
        var client = new MobileVoiceClient();
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? activeCall = null;
        try
        {
            Assert.True(client.Start());
            activeCall = Task.Run(
                () => Assert.True(client.HoldEngineCallForTest(acquired, release)),
                TestContext.Current.CancellationToken);
            Assert.True(acquired.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));

            await Task.Run(client.Dispose, TestContext.Current.CancellationToken).WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.False(activeCall.IsCompleted);
            Assert.Equal(0, client.EngineDisposalCountForTest);

            release.Set();
            await activeCall.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(
                () => client.EngineDisposalCountForTest == 1,
                TimeSpan.FromSeconds(5)));

            client.Dispose();
            Assert.Equal(1, client.EngineDisposalCountForTest);
        }
        finally
        {
            release.Set();
            if (activeCall != null)
            {
                try
                {
                    await activeCall.WaitAsync(
                        TimeSpan.FromSeconds(2),
                        TestContext.Current.CancellationToken);
                }
                catch { }
            }
            client.Dispose();
        }
    }

    [Theory]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, true, true, true)]
    public void AndroidMicrophoneDoesNotResumeWhenAnyLifecycleGateRejects(
        bool disposed,
        bool paused,
        bool permitted,
        bool requested,
        bool muted)
    {
        Assert.False(PerfectCommsVoiceBackend.ShouldResumeAndroidMicrophone(
            disposed,
            paused,
            permitted,
            requested,
            muted));
    }

    [Fact]
    public void AndroidMicrophoneResumesOnlyForALivePermittedRequestedUnmutedRoom()
    {
        Assert.True(PerfectCommsVoiceBackend.ShouldResumeAndroidMicrophone(
            disposed: false,
            paused: false,
            permitted: true,
            requested: true,
            muted: false));
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
    public void RtpIngressOverflowRetainsNewestPacketsAndCountsDrops()
    {
        var ingress = new ManagedVoiceEngine.RtpIngressRing(4, 8);

        for (ushort sequence = 1; sequence <= 7; sequence++)
        {
            byte[] payload = [(byte)sequence];
            Assert.True(ingress.TryWrite(
                sequence,
                (uint)sequence * (uint)ManagedOpusEncoder.FrameSamples,
                new ArraySegment<byte>(payload),
                out bool dropped));
            Assert.Equal(sequence > 4, dropped);
        }

        Assert.Equal(3L, ingress.DroppedPackets);
        var output = new byte[8];
        for (ushort sequence = 4; sequence <= 7; sequence++)
        {
            Assert.True(ingress.TryRead(output, out ushort actual, out uint timestamp, out int length));
            Assert.Equal(sequence, actual);
            Assert.Equal((uint)sequence * (uint)ManagedOpusEncoder.FrameSamples, timestamp);
            Assert.Equal(1, length);
            Assert.Equal((byte)sequence, output[0]);
        }
        Assert.False(ingress.TryRead(output, out _, out _, out _));
    }

    [Fact]
    public void PlaybackOverflowRetainsStereoAlignedNewestTailAndCountsDrops()
    {
        using var engine = new ManagedVoiceEngine();
        int stereoFrameSamples = ManagedOpusEncoder.FrameSamples * 2;
        var queued = new float[stereoFrameSamples * 17];
        for (int frame = 0; frame < 17; frame++)
        {
            for (int sample = 0; sample < ManagedOpusEncoder.FrameSamples; sample++)
            {
                queued[frame * stereoFrameSamples + sample * 2] = frame + 1;
                queued[frame * stereoFrameSamples + sample * 2 + 1] = -(frame + 1);
            }
        }

        engine.QueuePlaybackForTest(queued);

        Assert.Equal(stereoFrameSamples * 16, engine.PlaybackDepthSamples);
        Assert.Equal((long)stereoFrameSamples, engine.PlaybackDroppedSamples);
        var playback = new float[stereoFrameSamples];
        for (int frame = 14; frame <= 17; frame++)
        {
            engine.ReadPlayback(playback, playback.Length);
            for (int sample = 0; sample < ManagedOpusEncoder.FrameSamples; sample++)
            {
                Assert.Equal((float)frame, playback[sample * 2]);
                Assert.Equal((float)-frame, playback[sample * 2 + 1]);
            }
        }
        Assert.Equal((long)stereoFrameSamples * 12, engine.PlaybackSkippedSamples);
    }

    [Fact]
    public async Task PlaybackReadCompletesWhileMixerWorkIsPaused()
    {
        using var engine = new ManagedVoiceEngine();
        using var mixerEntered = new ManualResetEventSlim();
        using var releaseMixer = new ManualResetEventSlim();
        engine.BeforeMixerWorkForTest = () =>
        {
            mixerEntered.Set();
            releaseMixer.Wait(TestContext.Current.CancellationToken);
        };
        Task pump = Task.Run(
            engine.PumpPlaybackFrameForTest,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(mixerEntered.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            var playback = new float[ManagedOpusEncoder.FrameSamples * 2];
            await Task.Run(
                () => engine.ReadPlayback(playback, playback.Length),
                TestContext.Current.CancellationToken).WaitAsync(
                    TimeSpan.FromSeconds(2),
                    TestContext.Current.CancellationToken);
            Assert.All(playback, sample => Assert.Equal(0f, sample));
        }
        finally
        {
            releaseMixer.Set();
            await pump.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DeafeningDuringPublicationPauseRejectsThePreDeafenMix()
    {
        using var engine = new ManagedVoiceEngine();
        using var mixCompleted = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        engine.ConfigureGameState(false, 1f, []);
        engine.BeforePlaybackWriteForTest = () =>
        {
            mixCompleted.Set();
            releasePublication.Wait(TestContext.Current.CancellationToken);
        };
        Task pump = Task.Run(
            engine.PumpPlaybackFrameForTest,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(mixCompleted.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            engine.ConfigureGameState(true, 1f, []);
            Assert.Equal(0, engine.PlaybackDepthSamples);
        }
        finally
        {
            releasePublication.Set();
            await pump.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, engine.PlaybackDepthSamples);
    }

    [Fact]
    public void DiscontinuityPacketRendersInTheSamePlaybackPump()
    {
        using var engine = new ManagedVoiceEngine();
        using var encoder = new ManagedOpusEncoder();
        engine.AddPeerForTest("peer");
        engine.ConfigureGameState(
            false,
            1f,
            [new ManagedPeerRoute("peer", 1f, 0f, 0, false)]);
        var samples = new float[ManagedOpusEncoder.FrameSamples];
        FillSignal(samples, 0, 440f, 0.2f);
        var encoded = new byte[ManagedOpusEncoder.MaxPacketBytes];
        int encodedLength = encoder.Encode(samples, encoded, out _, out _);
        byte[] packet = encoded.AsSpan(0, encodedLength).ToArray();

        for (ushort sequence = 1; sequence <= 3; sequence++)
        {
            Assert.True(engine.IngestOpusForTest(
                "peer",
                1,
                1,
                sequence,
                (uint)(sequence - 1) * (uint)ManagedOpusEncoder.FrameSamples,
                packet));
            if (sequence == 3)
            {
                engine.PumpPlaybackFrameForTest();
                engine.PumpPlaybackFrameForTest();
                engine.PumpPlaybackFrameForTest();
            }
        }

        engine.ConfigureGameState(true, 1f, []);
        engine.ConfigureGameState(
            false,
            1f,
            [new ManagedPeerRoute("peer", 1f, 0f, 0, false)]);
        uint discontinuousTimestamp = 9u * (uint)ManagedOpusEncoder.FrameSamples;
        Assert.True(engine.IngestOpusForTest("peer", 1, 1, 4, discontinuousTimestamp, packet));

        engine.PumpPlaybackFrameForTest();

        int stereoFrameSamples = ManagedOpusEncoder.FrameSamples * 2;
        Assert.Equal(stereoFrameSamples, engine.PlaybackDepthSamples);
        engine.QueuePlaybackForTest(new float[stereoFrameSamples * 3]);
        var playback = new float[stereoFrameSamples];
        engine.ReadPlayback(playback, playback.Length);
        Assert.True(Energy(playback) > 0.001d);
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

    [Fact]
    public void SsrcReplacementAcceptsTheNewSourceAndRejectsTheRetiredSource()
    {
        using var engine = new ManagedVoiceEngine();
        byte[] opus = [1, 2, 3];

        Assert.True(engine.Start());
        Assert.True(engine.AddPeer("source-peer", false, 1));
        Assert.True(engine.IngestOpusForTest("source-peer", 1, 0x10203040, 30_000, 960, opus));
        Assert.True(engine.IngestOpusForTest("source-peer", 1, 0x50607080, 1_000, 960, opus));
        Assert.False(engine.IngestOpusForTest("source-peer", 1, 0x10203040, 30_001, 1_920, opus));
    }

    [Fact]
    public async Task SsrcReplacementDefersDecoderResetUntilTheNextPlaybackPump()
    {
        using var engine = new ManagedVoiceEngine();
        using var encoder = new ManagedOpusEncoder();
        using var mixerEntered = new ManualResetEventSlim();
        using var releaseMixer = new ManualResetEventSlim();
        engine.AddPeerForTest("epoch-peer");
        engine.ConfigureGameState(
            false,
            1f,
            [new ManagedPeerRoute("epoch-peer", 1f, 0f, 0, false)]);
        var samples = new float[ManagedOpusEncoder.FrameSamples];
        FillSignal(samples, 0, 440f, 0.2f);
        var encoded = new byte[ManagedOpusEncoder.MaxPacketBytes];
        int encodedLength = encoder.Encode(samples, encoded, out _, out _);
        byte[] packet = encoded.AsSpan(0, encodedLength).ToArray();
        for (ushort sequence = 30_000; sequence < 30_003; sequence++)
        {
            Assert.True(engine.IngestOpusForTest(
                "epoch-peer",
                1,
                0x10203040,
                sequence,
                (uint)(sequence - 30_000) * (uint)ManagedOpusEncoder.FrameSamples,
                packet));
        }
        engine.BeforeMixerWorkForTest = () =>
        {
            mixerEntered.Set();
            releaseMixer.Wait(TestContext.Current.CancellationToken);
        };
        Task oldPump = Task.Run(
            engine.PumpPlaybackFrameForTest,
            TestContext.Current.CancellationToken);

        try
        {
            Assert.True(mixerEntered.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            float oldPeak = engine.PeerDecodedPeakForTest("epoch-peer");
            Assert.True(oldPeak > 0f);
            for (ushort sequence = 1_000; sequence < 1_003; sequence++)
            {
                Assert.True(engine.IngestOpusForTest(
                    "epoch-peer",
                    1,
                    0x50607080,
                    sequence,
                    (uint)(sequence - 1_000) * (uint)ManagedOpusEncoder.FrameSamples,
                    packet));
            }
            Assert.Equal(oldPeak, engine.PeerDecodedPeakForTest("epoch-peer"));
            Assert.Equal(0, engine.PeerSourceEpochForTest("epoch-peer"));
        }
        finally
        {
            engine.BeforeMixerWorkForTest = null;
            releaseMixer.Set();
            await oldPump.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        }

        engine.PumpPlaybackFrameForTest();
        Assert.Equal(1, engine.PeerSourceEpochForTest("epoch-peer"));
        Assert.True(engine.PeerDecodedPeakForTest("epoch-peer") > 0f);
    }

    [Fact]
    public async Task RestartAndRemoteOfferPreserveFifoCandidateOrdering()
    {
        using var offerer = new ManagedWebRtcPeer("offerer", 1, []);
        var offerReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidateReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.LocalSdp += (type, sdp) =>
        {
            if (type == "offer")
                offerReady.TrySetResult(sdp);
        };
        offerer.LocalCandidate += candidate => candidateReady.TrySetResult(candidate);
        Assert.True(offerer.Start(createOffer: true));
        string offer = await offerReady.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        string candidate = await candidateReady.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        using var peer = new ManagedWebRtcPeer("restart-peer", 1, []);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            peer.SetRemoteDescriptionReadyForTest();
            peer.SetLocalSignalsPublishedForTest();
            await peer.HoldNegotiationForTest(release.Task).WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

            Assert.True(peer.RestartIce(createOffer: false));
            Assert.Equal(2, peer.LocalSignalStateForTest);
            Assert.True(peer.SetRemoteSdp("offer", offer));
            Assert.True(peer.RemoteDescriptionPendingForTest);
            Assert.True(peer.AddIceCandidate(candidate));
            Assert.Equal(1, peer.PendingRemoteCandidateCountForTest);

            release.TrySetResult();
            await peer.WaitForNegotiationForTest().WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.False(peer.RemoteDescriptionPendingForTest);
            Assert.True(peer.RemoteDescriptionReadyForTest);
            Assert.Equal(0, peer.PendingRemoteCandidateCountForTest);
            Assert.Equal(1, peer.AppliedRemoteCandidateCountForTest);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task SendFailureRecoversOnlyAfterRestartAndReconnect()
    {
        using var peer = new ManagedWebRtcPeer("send-peer", 1, []);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await peer.HoldNegotiationForTest(release.Task).WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            int attempts = 0;
            int failures = 0;
            peer.StateChanged += state =>
            {
                if (state == "failed")
                    Interlocked.Increment(ref failures);
            };
            peer.SendAudioForTest = (_, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("send failed");
            };
            peer.SetConnectedForTest();
            byte[] packet = [1, 2, 3];

            Assert.False(peer.SendEncodedOpus(packet, packet.Length));
            Assert.False(peer.IsConnected);
            Assert.False(peer.SendEncodedOpus(packet, packet.Length));
            Assert.Equal(1, Volatile.Read(ref attempts));
            Assert.Equal(1, Volatile.Read(ref failures));

            Assert.True(peer.RestartIce(createOffer: false));
            peer.SetConnectedForTest();
            Assert.True(peer.SendEncodedOpus(packet, packet.Length));
            Assert.Equal(2, Volatile.Read(ref attempts));
            Assert.Equal(1, Volatile.Read(ref failures));
        }
        finally
        {
            peer.Dispose();
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task DisposeDiscardsTelemetryQueuedBehindAnActiveCallback()
    {
        var engine = new ManagedVoiceEngine();
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        int callbacks = 0;
        engine.LocalLevel += (_, _) =>
        {
            Interlocked.Increment(ref callbacks);
            callbackEntered.Set();
            releaseCallback.Wait(TestContext.Current.CancellationToken);
        };

        try
        {
            Assert.True(engine.Start());
            Assert.True(engine.EnqueueLocalLevelForTest());
            Assert.True(callbackEntered.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            Assert.True(engine.EnqueueLocalLevelForTest());

            await Task.Run(engine.Dispose, TestContext.Current.CancellationToken).WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(1, Volatile.Read(ref callbacks));
            releaseCallback.Set();
            Assert.True(SpinWait.SpinUntil(
                () => engine.PendingEventCount == 0,
                TimeSpan.FromSeconds(2)));
            Assert.Equal(1, Volatile.Read(ref callbacks));
        }
        finally
        {
            releaseCallback.Set();
            engine.Dispose();
        }
    }

    [Fact]
    public void DisposeFromCallbackPreventsTheNextQueuedCallback()
    {
        var engine = new ManagedVoiceEngine();
        using var callbackEntered = new ManualResetEventSlim();
        using var allowDispose = new ManualResetEventSlim();
        using var disposeReturned = new ManualResetEventSlim();
        int callbacks = 0;
        engine.LocalLevel += (_, _) =>
        {
            if (Interlocked.Increment(ref callbacks) != 1)
                return;
            callbackEntered.Set();
            allowDispose.Wait(TestContext.Current.CancellationToken);
            engine.Dispose();
            disposeReturned.Set();
        };

        try
        {
            Assert.True(engine.Start());
            Assert.True(engine.EnqueueLocalLevelForTest());
            Assert.True(callbackEntered.Wait(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken));
            Assert.True(engine.EnqueueLocalLevelForTest());
            allowDispose.Set();
            Assert.True(disposeReturned.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            Assert.True(SpinWait.SpinUntil(
                () => engine.PendingEventCount == 0,
                TimeSpan.FromSeconds(2)));
            Assert.Equal(1, Volatile.Read(ref callbacks));
        }
        finally
        {
            allowDispose.Set();
            engine.Dispose();
        }
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
