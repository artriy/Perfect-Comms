#if WINDOWS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class SidecarVoiceHostTests
{
    [Fact]
    public void ReleaseQuiescesSessionStopsHelperAndKeepsHostReusable()
    {
        var firstClient = new FakeSidecarVoiceClient();
        var secondClient = new FakeSidecarVoiceClient();
        var createCount = 0;
        var host = new SidecarVoiceHostCore(() =>
        {
            createCount++;
            return createCount == 1 ? firstClient : secondClient;
        });

        var first = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out var failure));
        Assert.Equal(string.Empty, failure);
        Assert.True(first.EnsureStarted("mic-a", "spk-a"));
        first.SetMicActive(true);
        first.SetSynthetic(true);
        Assert.True(first.AddPeer("42", isOfferer: true, generation: 1));
        first.SendGameState(false, 1f, new[]
        {
            new SidecarProtocol.GameStatePeerInput("42", 1f, 0f, 0, false)
        });

        first.Dispose();
        WaitUntil(() => firstClient.DisposeCount == 1);

        Assert.Equal(0, firstClient.HandlerCount);
        Assert.False(firstClient.MicActiveCalls[^1]);
        Assert.False(firstClient.SyntheticCalls[^1]);
        Assert.Contains("42", firstClient.RemovedPeers);
        Assert.Equal((true, 0f, 0), firstClient.GameStates[^1]);

        SidecarVoiceLease? second = null;
        WaitUntil(() => (second = host.TryAcquire(Callbacks(), out failure)) != null);
        Assert.Equal(string.Empty, failure);
        Assert.Equal(0, secondClient.HandlerCount);
        Assert.True(second!.EnsureStarted("mic-b", "spk-b"));
        Assert.Equal(10, secondClient.HandlerCount);
        Assert.Equal(1, firstClient.StartCount);
        Assert.Equal(1, secondClient.StartCount);
        Assert.Equal(2, createCount);

        second.Dispose();
        WaitUntil(() => secondClient.DisposeCount == 1);
    }

    [Fact]
    public void FailedExplicitRemoveStaysTrackedAndReleaseRetriesIt()
    {
        var fake = new FakeSidecarVoiceClient();
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));
        Assert.True(lease.AddPeer("42", isOfferer: true, generation: 1));
        fake.RemoveFailuresRemaining = 1;

        Assert.True(lease.RemovePeer("42", generation: 1));
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);

        Assert.Equal(new[] { "42", "42" }, fake.RemovedPeers);
    }

    [Fact]
    public void HostAllowsOnlyOneActiveLease()
    {
        var host = new SidecarVoiceHostCore(() => new FakeSidecarVoiceClient());
        var first = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));

        Assert.Null(host.TryAcquire(Callbacks(), out var failure));
        Assert.StartsWith("lease-active:", failure, StringComparison.Ordinal);

        first.Dispose();
        Assert.NotNull(host.TryAcquire(Callbacks(), out failure));
        Assert.Equal(string.Empty, failure);
    }

    [Fact]
    public void ConditionalOutputSelectionIsRecheckedWhenItsTurnArrives()
    {
        using var commandEntered = new ManualResetEventSlim();
        using var allowCommand = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true,
            CommandEntered = commandEntered,
            AllowCommand = allowCommand,
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));
        var blockedAdmission = Task.Run(() => lease.SetMicActive(true));
        Assert.True(commandEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(blockedAdmission.Wait(TimeSpan.FromSeconds(2)));
        var current = true;

        Assert.True(lease.TrySelectOutputDeviceIf("superseded", () => current));
        current = false;
        allowCommand.Set();
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);

        Assert.Empty(fake.OutputSelectionCalls);
    }

    [Fact]
    public void ConditionalAudioRouteIsRecheckedWhenItsTurnArrives()
    {
        using var commandEntered = new ManualResetEventSlim();
        using var allowCommand = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true,
            CommandEntered = commandEntered,
            AllowCommand = allowCommand,
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));
        var blockedAdmission = Task.Run(() => lease.SetMicActive(true));
        Assert.True(commandEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(blockedAdmission.Wait(TimeSpan.FromSeconds(2)));
        var current = true;

        Assert.True(lease.TryConfigureAudioRouteIf(
            "mic-stale", "spk-stale", SidecarCaptureMode.Warm, synthetic: false, () => current));
        current = false;
        allowCommand.Set();
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);

        Assert.Empty(fake.AudioRouteCalls);
    }

    [Fact]
    public async Task BlockedClientActionDoesNotBlockQueriesOrCommandAdmission()
    {
        using var commandEntered = new ManualResetEventSlim();
        using var allowCommand = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true,
            CommandEntered = commandEntered,
            AllowCommand = allowCommand,
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        var blockedAdmission = Task.Run(() => lease.SetMicActive(true));
        Assert.True(commandEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(blockedAdmission.Wait(TimeSpan.FromSeconds(2)));
        var probe = Task.Run(() =>
        {
            Assert.Equal(CaptureHealth.Healthy, lease.Health);
            Assert.Single(lease.OutputDevices);
            Assert.True(lease.ConfigureAudioRoute(
                "mic", "spk", SidecarCaptureMode.Transmit, synthetic: false));
        });

        Assert.Same(probe, await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(2))));
        allowCommand.Set();
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);
    }

    [Fact]
    public async Task ReleaseIsNonblockingRejectsLaterCommandsAndRetiresInOrder()
    {
        using var commandEntered = new ManualResetEventSlim();
        using var allowCommand = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true,
            CommandEntered = commandEntered,
            AllowCommand = allowCommand,
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        var blockedAdmission = Task.Run(() => lease.SetMicActive(true));
        Assert.True(commandEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(blockedAdmission.Wait(TimeSpan.FromSeconds(2)));
        var release = Task.Run(lease.Dispose);

        Assert.Same(release, await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(2))));
        Assert.False(lease.ConfigureAudioRoute(
            "late-mic", "late-spk", SidecarCaptureMode.Transmit, synthetic: false));
        Assert.Null(host.TryAcquire(Callbacks(), out var failure));
        Assert.Equal("helper-retiring", failure);
        allowCommand.Set();
        WaitUntil(() => fake.DisposeCount == 1);

        Assert.True(fake.Sequence.IndexOf("mic:true") < fake.Sequence.IndexOf("mic:false"));
        Assert.True(fake.Sequence.IndexOf("mic:false") < fake.Sequence.IndexOf("dispose"));
        Assert.DoesNotContain("route:late-mic:late-spk", fake.Sequence);
    }

    [Fact]
    public void BoundedCommandQueueRejectsOverflowImmediately()
    {
        using var commandEntered = new ManualResetEventSlim();
        using var allowCommand = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true,
            CommandEntered = commandEntered,
            AllowCommand = allowCommand,
        };
        var host = new SidecarVoiceHostCore(() => fake, commandCapacity: 1);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        var blockedAdmission = Task.Run(() => lease.SetMicActive(true));
        Assert.True(commandEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(blockedAdmission.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(lease.ConfigureAudioRoute(
            "queued-mic", "queued-spk", SidecarCaptureMode.Warm, synthetic: false));
        Assert.False(lease.SelectOutputDevice("overflow"));

        allowCommand.Set();
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);
        Assert.DoesNotContain("overflow", fake.OutputSelectionCalls);
    }

    [Fact]
    public void InitialConfigurationIsOneOrderedOperationWithItsActualResult()
    {
        var fake = new FakeSidecarVoiceClient
        {
            InitialConfigurationResult = false,
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));
        lease.SetSynthetic(true);

        var configured = lease.TryConfigureInitialCapture(
            "mic", "spk",
            aec: true, agc: true, ns: true, nsVeryHigh: false, hpf: true,
            gain: 1f, vadThreshold: 0.01f, noiseGateThreshold: 0.02f,
            synthetic: false, micActive: true, micWarm: false,
            monitorEnabled: false, monitorDelayed: false, monitorGain: 1f,
            iceServers: null);

        Assert.False(configured);
        Assert.Equal(new[] { "synthetic:true", "initial-config" }, fake.Sequence);
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);
    }

    [Fact]
    public void BackgroundAudioRouteCompletionReturnsTheClientResult()
    {
        var fake = new FakeSidecarVoiceClient
        {
            AudioRouteResult = false,
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        Assert.False(lease.ConfigureAudioRouteAndWait(
            "mic", "spk", SidecarCaptureMode.Transmit, synthetic: false));
        Assert.Single(fake.AudioRouteCalls);

        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);
    }

    [Fact]
    public async Task ConcurrentEnsureStartedIsSingleFlight()
    {
        using var startEntered = new ManualResetEventSlim();
        using var allowStart = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            StartEntered = startEntered,
            AllowStart = allowStart
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));

        var first = Task.Run(() => lease.EnsureStarted("mic", "spk"));
        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Null(host.TryAcquire(Callbacks(), out var failure));
        Assert.StartsWith("lease-active:", failure, StringComparison.Ordinal);
        var second = Task.Run(() => lease.EnsureStarted("mic", "spk"));

        await Task.Delay(50);
        Assert.Equal(1, fake.StartCount);
        allowStart.Set();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, fake.StartCount);
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);
    }

    [Fact]
    public async Task StartupDoesNotBlockHealthQueriesOrCommands()
    {
        using var startEntered = new ManualResetEventSlim();
        using var allowStart = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            StartEntered = startEntered,
            AllowStart = allowStart
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));

        var start = Task.Run(() => lease.EnsureStarted("mic", "spk"));
        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(2)));
        var probe = Task.Run(() =>
        {
            Assert.Equal(CaptureHealth.Dead, lease.Health);
            Assert.Empty(lease.OutputDevices);
            lease.SetMicActive(true);
            lease.SetInput(1f, 0.01f, 0.02f);
            Assert.False(lease.ConfigureAudioRoute(
                "mic", "spk", SidecarCaptureMode.Transmit, synthetic: false));
        });

        var completed = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(2)));
        allowStart.Set();

        Assert.Same(probe, completed);
        await probe;
        Assert.Empty(fake.MicActiveCalls);
        Assert.True(await start);

        lease.SetMicActive(true);
        WaitUntil(() => fake.MicActiveCalls.Count == 1);
        Assert.Equal(new[] { true }, fake.MicActiveCalls);
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);
    }

    [Fact]
    public async Task ReleaseDuringStartupDoesNotWaitForStartOrPublishStaleSuccess()
    {
        using var startEntered = new ManualResetEventSlim();
        using var allowStart = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            StartEntered = startEntered,
            AllowStart = allowStart
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));

        var start = Task.Run(() => lease.EnsureStarted("mic", "spk"));
        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(2)));
        var release = Task.Run(lease.Dispose);

        var completed = await Task.WhenAny(release, Task.Delay(TimeSpan.FromSeconds(2)));
        var releasedBeforeStartCompleted = ReferenceEquals(release, completed);
        if (!releasedBeforeStartCompleted)
            allowStart.Set();

        Assert.True(releasedBeforeStartCompleted);
        await release;
        Assert.Equal(0, fake.DisposeCount);
        allowStart.Set();

        Assert.False(await start);
        Assert.False(lease.IsActive);
        Assert.Equal(CaptureHealth.Dead, lease.Health);
        WaitUntil(() => fake.DisposeCount == 1);
    }

    [Fact]
    public async Task ShutdownDuringStartupDoesNotWaitForStartOrPublishStaleSuccess()
    {
        using var startEntered = new ManualResetEventSlim();
        using var allowStart = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            StartEntered = startEntered,
            AllowStart = allowStart
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));

        var start = Task.Run(() => lease.EnsureStarted("mic", "spk"));
        Assert.True(startEntered.Wait(TimeSpan.FromSeconds(2)));
        var shutdown = Task.Run(() => host.Shutdown("test-exit"));

        var completed = await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(2)));
        var shutdownBeforeStartCompleted = ReferenceEquals(shutdown, completed);
        if (!shutdownBeforeStartCompleted)
            allowStart.Set();

        Assert.True(shutdownBeforeStartCompleted);
        await shutdown;
        Assert.Equal(0, fake.DisposeCount);
        allowStart.Set();

        Assert.False(await start);
        Assert.False(lease.IsActive);
        Assert.Equal(CaptureHealth.Dead, lease.Health);
        WaitUntil(() => fake.DisposeCount == 1);
        Assert.Null(host.TryAcquire(Callbacks(), out var failure));
        Assert.Equal("host-shutdown", failure);
    }

    [Fact]
    public void DeadHelperIsDisposedAndCleanlyReplaced()
    {
        var firstClient = new FakeSidecarVoiceClient();
        var secondClient = new FakeSidecarVoiceClient();
        var created = 0;
        var deadEvents = 0;
        var host = new SidecarVoiceHostCore(() => ++created == 1 ? firstClient : secondClient);
        var first = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(_ => deadEvents++), out _));
        Assert.True(first.EnsureStarted("mic", "spk"));

        firstClient.RaiseDead("heartbeat timeout");
        Assert.Equal(1, deadEvents);
        first.Dispose();
        WaitUntil(() => firstClient.DisposeCount == 1);
        Assert.Equal(0, firstClient.HandlerCount);

        SidecarVoiceLease? second = null;
        WaitUntil(() => (second = host.TryAcquire(Callbacks(), out _)) != null);
        Assert.True(second!.EnsureStarted("mic", "spk"));
        Assert.Equal(2, created);
        Assert.Equal(10, secondClient.HandlerCount);
        second.Dispose();
        WaitUntil(() => secondClient.DisposeCount == 1);
    }

    [Fact]
    public void RetiringHelperBlocksReplacementUntilCleanupCompletes()
    {
        using var allowDisposeCompletion = new ManualResetEventSlim();
        try
        {
            var firstClient = new FakeSidecarVoiceClient
            {
                AllowDisposeCompletion = allowDisposeCompletion,
            };
            var secondClient = new FakeSidecarVoiceClient();
            var created = 0;
            var host = new SidecarVoiceHostCore(() => ++created == 1 ? firstClient : secondClient);
            var first = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
            Assert.True(first.EnsureStarted("mic", "spk"));

            first.Dispose();
            WaitUntil(() => firstClient.DisposeCount == 1);

            Assert.False(firstClient.CleanupComplete);
            Assert.Null(host.TryAcquire(Callbacks(), out var failure));
            Assert.Equal("helper-retiring", failure);
            Assert.Equal(1, created);

            allowDisposeCompletion.Set();
            Assert.True(SpinWait.SpinUntil(
                () => firstClient.CleanupComplete,
                TimeSpan.FromSeconds(2)));
            SidecarVoiceLease? second = null;
            WaitUntil(() => (second = host.TryAcquire(Callbacks(), out failure)) != null);
            Assert.Equal(string.Empty, failure);
            Assert.True(second!.EnsureStarted("mic", "spk"));
            Assert.Equal(2, created);
            second.Dispose();
            WaitUntil(() => secondClient.DisposeCount == 1);
        }
        finally
        {
            allowDisposeCompletion.Set();
        }
    }

    [Fact]
    public void ActiveLeaseDoesNotReplaceDeadHelperBeforeCleanupCompletes()
    {
        using var allowDisposeCompletion = new ManualResetEventSlim();
        try
        {
            var firstClient = new FakeSidecarVoiceClient
            {
                AllowDisposeCompletion = allowDisposeCompletion,
            };
            var secondClient = new FakeSidecarVoiceClient();
            var created = 0;
            var host = new SidecarVoiceHostCore(() => ++created == 1 ? firstClient : secondClient);
            var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
            Assert.True(lease.EnsureStarted("mic", "spk"));
            firstClient.RaiseDead("heartbeat timeout");

            Assert.False(lease.EnsureStarted("mic", "spk"));
            WaitUntil(() => firstClient.DisposeCount == 1);
            Assert.False(firstClient.CleanupComplete);
            Assert.Equal(1, created);

            allowDisposeCompletion.Set();
            Assert.True(SpinWait.SpinUntil(
                () => firstClient.CleanupComplete,
                TimeSpan.FromSeconds(2)));
            Assert.True(SpinWait.SpinUntil(
                () => lease.EnsureStarted("mic", "spk"),
                TimeSpan.FromSeconds(2)));
            Assert.Equal(2, created);
            lease.Dispose();
            WaitUntil(() => secondClient.DisposeCount == 1);
        }
        finally
        {
            allowDisposeCompletion.Set();
        }
    }

    [Fact]
    public void RecoverableDeviceErrorIsForwardedWithoutKillingLease()
    {
        var fake = new FakeSidecarVoiceClient();
        var seen = 0;
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(
            Callbacks(onRecoverableError: (code, _) =>
            {
                Assert.Equal("mic-error", code);
                seen++;
            }), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        fake.RaiseRecoverableError("mic-error", "permission temporarily unavailable");

        Assert.Equal(1, seen);
        Assert.Equal(CaptureHealth.Healthy, lease.Health);
        lease.Dispose();
        WaitUntil(() => fake.DisposeCount == 1);
    }

    [Fact]
    public void ProcessShutdownKillsHelperEvenWithActiveLease()
    {
        var fake = new FakeSidecarVoiceClient();
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        host.Shutdown("test-exit");
        WaitUntil(() => fake.DisposeCount == 1);

        Assert.Equal(0, fake.HandlerCount);
        Assert.False(lease.IsActive);
        Assert.Equal(CaptureHealth.Dead, lease.Health);
        Assert.Null(host.TryAcquire(Callbacks(), out var failure));
        Assert.Equal("host-shutdown", failure);
    }

    [Fact]
    public async Task SaturatedCriticalRoutesRetireTheHelperFailClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var commandEntered = new ManualResetEventSlim();
        using var allowCommand = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true,
            CommandEntered = commandEntered,
            AllowCommand = allowCommand,
        };
        var host = new SidecarVoiceHostCore(() => fake, commandCapacity: 1);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        lease.SetMicActive(true);
        Assert.True(await Task.Run(
            () => commandEntered.Wait(TimeSpan.FromSeconds(2), cancellationToken),
            cancellationToken));
        Assert.True(lease.ConfigureAudioRoute(
            "transmit-mic", "transmit-spk", SidecarCaptureMode.Transmit, synthetic: false));
        Assert.True(lease.ConfigureAudioRoute(
            "warm-mic", "warm-spk", SidecarCaptureMode.Warm, synthetic: false));
        Assert.False(lease.ConfigureAudioRoute(
            "stopped-mic", "stopped-spk", SidecarCaptureMode.Stopped, synthetic: false));

        var release = lease.ReleaseAsync();
        Assert.False(release.IsCompleted);
        allowCommand.Set();
        await AwaitCompletion(release);

        Assert.Equal(2, fake.AudioRouteCalls.Count);
        Assert.Equal(SidecarCaptureMode.Warm, fake.AudioRouteCalls[^1].Mode);
        Assert.False(fake.MicActiveCalls[^1]);
        Assert.Equal(0, fake.HandlerCount);
    }

    [Fact]
    public async Task CommandWorkerStartFailureFencesFallbackCleanup()
    {
        using var allowFirstCleanup = new ManualResetEventSlim();
        try
        {
            var firstClient = new FakeSidecarVoiceClient
            {
                AllowDisposeCompletion = allowFirstCleanup,
            };
            var secondClient = new FakeSidecarVoiceClient();
            var createCount = 0;
            var workerStartCount = 0;
            var host = new SidecarVoiceHostCore(
                () => Interlocked.Increment(ref createCount) == 1 ? firstClient : secondClient,
                commandCapacity: 4,
                startCommandWorker: (run, name) =>
                {
                    if (Interlocked.Increment(ref workerStartCount) == 1)
                        throw new InvalidOperationException("worker-start-failed");
                    new Thread(run)
                    {
                        IsBackground = true,
                        Name = name,
                    }.Start();
                });
            var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));

            Assert.False(lease.EnsureStarted("mic-a", "spk-a"));
            await AwaitCompletion(firstClient.DisposeObserved);
            Assert.False(firstClient.CleanupComplete);
            Assert.False(lease.EnsureStarted("mic-b", "spk-b"));
            Assert.Equal(1, createCount);

            allowFirstCleanup.Set();
            Assert.True(await TryUntilAsync(() => lease.EnsureStarted("mic-b", "spk-b")));
            Assert.Equal(2, createCount);
            Assert.False(firstClient.MicActiveCalls[^1]);

            await AwaitCompletion(lease.ReleaseAsync());
        }
        finally
        {
            allowFirstCleanup.Set();
        }
    }

    [Fact]
    public async Task ReleaseAsyncWaitsForFallbackRetirement()
    {
        using var allowCleanup = new ManualResetEventSlim();
        try
        {
            var fake = new FakeSidecarVoiceClient
            {
                AllowDisposeCompletion = allowCleanup,
            };
            var host = new SidecarVoiceHostCore(
                () => fake,
                commandCapacity: 4,
                startCommandWorker: (_, _) => throw new InvalidOperationException("worker-start-failed"));
            var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));

            Assert.False(lease.EnsureStarted("mic", "spk"));
            await AwaitCompletion(fake.DisposeObserved);
            var release = lease.ReleaseAsync();

            Assert.False(release.IsCompleted);
            Assert.Null(host.TryAcquire(Callbacks(), out var failure));
            Assert.Equal("helper-retiring", failure);
            allowCleanup.Set();
            await AwaitCompletion(release);
            var replacement = Assert.IsType<SidecarVoiceLease>(
                host.TryAcquire(Callbacks(), out failure));
            Assert.Equal(string.Empty, failure);
            await AwaitCompletion(replacement.ReleaseAsync());
        }
        finally
        {
            allowCleanup.Set();
        }
    }

    [Fact]
    public async Task ReleaseAsyncCompletesAfterCleanupAndRetirementFenceClears()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var commandEntered = new ManualResetEventSlim();
        using var allowCommand = new ManualResetEventSlim();
        using var allowCleanup = new ManualResetEventSlim();
        try
        {
            var fake = new FakeSidecarVoiceClient
            {
                BlockNextMicActive = true,
                CommandEntered = commandEntered,
                AllowCommand = allowCommand,
                AllowDisposeCompletion = allowCleanup,
            };
            var host = new SidecarVoiceHostCore(() => fake);
            var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
            Assert.True(lease.EnsureStarted("mic", "spk"));

            lease.SetMicActive(true);
            Assert.True(await Task.Run(
                () => commandEntered.Wait(TimeSpan.FromSeconds(2), cancellationToken),
                cancellationToken));
            var release = lease.ReleaseAsync();
            Assert.False(release.IsCompleted);

            allowCommand.Set();
            await AwaitCompletion(fake.DisposeObserved);
            Assert.Equal(0, fake.HandlerCount);
            Assert.False(fake.CleanupComplete);
            Assert.False(release.IsCompleted);

            allowCleanup.Set();
            await AwaitCompletion(release);
            var replacement = Assert.IsType<SidecarVoiceLease>(
                host.TryAcquire(Callbacks(), out var failure));
            Assert.Equal(string.Empty, failure);
            await AwaitCompletion(replacement.ReleaseAsync());
        }
        finally
        {
            allowCleanup.Set();
            allowCommand.Set();
        }
    }

    [Fact]
    public async Task OutputFrameCompletionReturnsActualWriteResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var frameEntered = new ManualResetEventSlim();
        using var allowFrame = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            OutputFrameEntered = frameEntered,
            AllowOutputFrame = allowFrame,
            OutputFrameResult = false,
        };
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));
        var frame = new[] { 0.25f, -0.5f };

        var submission = Task.Run(
            () => lease.SendOutputTestFrameAndWait(frame),
            cancellationToken);
        Assert.True(await Task.Run(
            () => frameEntered.Wait(TimeSpan.FromSeconds(2), cancellationToken),
            cancellationToken));
        Assert.False(submission.IsCompleted);
        allowFrame.Set();

        Assert.Same(submission, await Task.WhenAny(
            submission,
            Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)));
        Assert.False(await submission);
        Assert.Equal(new[] { 0.25f, -0.5f }, Assert.Single(fake.OutputFrames));
        await AwaitCompletion(lease.ReleaseAsync());
    }

    [Fact]
    public async Task OutputFrameCompletionRejectsBoundedQueueOverflow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var commandEntered = new ManualResetEventSlim();
        using var allowCommand = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true,
            CommandEntered = commandEntered,
            AllowCommand = allowCommand,
        };
        var host = new SidecarVoiceHostCore(() => fake, commandCapacity: 1);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        lease.SetMicActive(true);
        Assert.True(await Task.Run(
            () => commandEntered.Wait(TimeSpan.FromSeconds(2), cancellationToken),
            cancellationToken));
        Assert.True(lease.SelectOutputDevice("queued-output"));
        var submission = Task.Run(
            () => lease.SendOutputTestFrameAndWait(new[] { 0.5f, -0.5f }),
            cancellationToken);

        Assert.Same(submission, await Task.WhenAny(
            submission,
            Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)));
        Assert.False(await submission);
        Assert.Empty(fake.OutputFrames);

        var release = lease.ReleaseAsync();
        allowCommand.Set();
        await AwaitCompletion(release);
    }

    private static async Task AwaitCompletion(Task completion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Same(completion, await Task.WhenAny(
            completion,
            Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)));
        await completion;
    }

    private static async Task<bool> TryUntilAsync(Func<bool> predicate)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (predicate()) return true;
            await Task.Delay(10, cancellationToken);
        }
        return predicate();
    }

    private static void WaitUntil(Func<bool> predicate)
        => Assert.True(SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(2)));

    private static SidecarVoiceCallbacks Callbacks(
        Action<string>? onDead = null,
        Action<string, string>? onRecoverableError = null)
        => new(
            (_, _) => { },
            onDead ?? (_ => { }),
            onRecoverableError ?? ((_, _) => { }),
            (_, _, _, _) => { },
            (_, _, _) => { },
            (_, _, _) => { },
            (_, _) => { },
            _ => { },
            _ => { },
            _ => { });

    private sealed class FakeSidecarVoiceClient : ISidecarVoiceClient
    {
        private Action<float[], int>? _onFrame;
        private Action<string>? _onDead;
        private Action<string, string>? _onRecoverableError;
        private Action<string, int, string, string>? _onLocalSdp;
        private Action<string, int, string>? _onLocalCandidate;
        private Action<string, int, string>? _onPeerState;
        private Action<float, bool>? _onLevel;
        private Action<IReadOnlyList<SidecarProtocol.PeerLevel>>? _onPeerLevels;
        private Action<SidecarPlaybackState>? _onPlaybackState;
        private Action<SidecarCaptureState>? _onCaptureState;

        public event Action<float[], int>? OnFrame { add => _onFrame += value; remove => _onFrame -= value; }
        public event Action<string>? OnDead { add => _onDead += value; remove => _onDead -= value; }
        public event Action<string, string>? OnRecoverableError { add => _onRecoverableError += value; remove => _onRecoverableError -= value; }
        public event Action<string, int, string, string>? OnLocalSdp { add => _onLocalSdp += value; remove => _onLocalSdp -= value; }
        public event Action<string, int, string>? OnLocalCandidate { add => _onLocalCandidate += value; remove => _onLocalCandidate -= value; }
        public event Action<string, int, string>? OnPeerState { add => _onPeerState += value; remove => _onPeerState -= value; }
        public event Action<float, bool>? OnLevel { add => _onLevel += value; remove => _onLevel -= value; }
        public event Action<IReadOnlyList<SidecarProtocol.PeerLevel>>? OnPeerLevels { add => _onPeerLevels += value; remove => _onPeerLevels -= value; }
        public event Action<SidecarCaptureState>? OnCaptureState { add => _onCaptureState += value; remove => _onCaptureState -= value; }
        public event Action<SidecarPlaybackState>? OnPlaybackState { add => _onPlaybackState += value; remove => _onPlaybackState -= value; }

        public CaptureHealth Health { get; private set; } = CaptureHealth.Dead;
        public IReadOnlyList<VoiceDeviceInfo> OutputDevices { get; } =
            new[] { new VoiceDeviceInfo("speaker-id", "speaker", true) };
        public int StartCount => Volatile.Read(ref StartCountBacking);
        public int DisposeCount => Volatile.Read(ref DisposeCountBacking);
        public bool CleanupComplete => Volatile.Read(ref CleanupCompleteBacking) != 0;
        public ManualResetEventSlim? StartEntered { get; init; }
        public ManualResetEventSlim? AllowStart { get; init; }
        public ManualResetEventSlim? AllowDisposeCompletion { get; init; }
        public bool BlockNextMicActive { get; init; }
        public ManualResetEventSlim? CommandEntered { get; init; }
        public ManualResetEventSlim? AllowCommand { get; init; }
        public ManualResetEventSlim? OutputFrameEntered { get; init; }
        public ManualResetEventSlim? AllowOutputFrame { get; init; }
        public bool InitialConfigurationResult { get; init; } = true;
        public bool AudioRouteResult { get; init; } = true;
        public bool OutputFrameResult { get; init; } = true;
        public List<bool> MicActiveCalls { get; } = new();
        public List<bool> SyntheticCalls { get; } = new();
        public List<string> RemovedPeers { get; } = new();
        public List<string> OutputSelectionCalls { get; } = new();
        public List<(string Input, string Output, SidecarCaptureMode Mode, bool Synthetic)> AudioRouteCalls { get; } = new();
        public List<float[]> OutputFrames { get; } = new();
        public int RemoveFailuresRemaining { get; set; }
        public List<(bool Deaf, float Master, int Peers)> GameStates { get; } = new();
        public List<string> Sequence { get; } = new();
        public Task DisposeObserved => DisposeObservedBacking.Task;

        public int HandlerCount =>
            Count(_onFrame) + Count(_onDead) + Count(_onRecoverableError) + Count(_onLocalSdp) + Count(_onLocalCandidate) +
            Count(_onPeerState) + Count(_onLevel) + Count(_onPeerLevels) + Count(_onCaptureState) + Count(_onPlaybackState);

        public bool Start(string? micDevice, string? spkDevice)
        {
            Interlocked.Increment(ref StartCountBacking);
            StartEntered?.Set();
            AllowStart?.Wait(TimeSpan.FromSeconds(5));
            Health = CaptureHealth.Healthy;
            return true;
        }

        private int StartCountBacking;
        private int DisposeCountBacking;
        private int CleanupCompleteBacking = 1;
        private int BlockedMicActiveConsumed;
        private readonly TaskCompletionSource<bool> DisposeObservedBacking =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryConfigureInitialCapture(string micDevice, string outputDevice, bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf, float gain, float vadThreshold, float noiseGateThreshold, bool synthetic, bool micActive, bool micWarm, bool monitorEnabled, bool monitorDelayed, float monitorGain, IEnumerable<IceServer>? iceServers)
        {
            Sequence.Add("initial-config");
            return InitialConfigurationResult;
        }
        public void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf) { }
        public void SetSynthetic(bool enabled)
        {
            SyntheticCalls.Add(enabled);
            Sequence.Add($"synthetic:{enabled.ToString().ToLowerInvariant()}");
        }
        public void SetMonitor(bool enabled, bool delayed, float gain)
            => Sequence.Add($"monitor:{enabled.ToString().ToLowerInvariant()}");
        public void SetInput(float gain, float vadThreshold, float noiseGateThreshold) { }
        public void SetMicActive(bool active)
        {
            if (active
                && BlockNextMicActive
                && Interlocked.Exchange(ref BlockedMicActiveConsumed, 1) == 0)
            {
                CommandEntered?.Set();
                AllowCommand?.Wait(TimeSpan.FromSeconds(5));
            }
            MicActiveCalls.Add(active);
            Sequence.Add($"mic:{active.ToString().ToLowerInvariant()}");
        }
        public void SetMicWarm() { }
        public bool ConfigurePrivateRadio(bool active, IReadOnlyList<string> receivers) => true;
        public void SelectMicDevice(string deviceId) { }
        public bool SelectOutputDevice(string deviceId)
        {
            OutputSelectionCalls.Add(deviceId);
            return true;
        }
        public bool ConfigureAudioRoute(
            string inputDevice,
            string outputDevice,
            SidecarCaptureMode captureMode,
            bool synthetic)
        {
            AudioRouteCalls.Add((inputDevice, outputDevice, captureMode, synthetic));
            Sequence.Add($"route:{inputDevice}:{outputDevice}");
            return AudioRouteResult;
        }
        public void SendOutputTestFrame(float[] interleavedStereo)
            => SendOutputTestFrameAndWait(interleavedStereo);
        public bool SendOutputTestFrameAndWait(float[] interleavedStereo)
        {
            OutputFrameEntered?.Set();
            AllowOutputFrame?.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            OutputFrames.Add((float[])interleavedStereo.Clone());
            return OutputFrameResult;
        }
        public bool AddPeer(string peerId, bool isOfferer, int generation) => true;
        public bool RemovePeer(string peerId, int generation)
        {
            RemovedPeers.Add(peerId);
            if (RemoveFailuresRemaining <= 0) return true;
            RemoveFailuresRemaining--;
            return false;
        }
        public bool RestartIce(string peerId, int generation, bool createOffer) => true;
        public bool SetRemoteSdp(string peerId, int generation, string sdpType, string sdp) => true;
        public bool AddIceCandidate(string peerId, int generation, string candidate) => true;
        public void SetIceServers(IEnumerable<IceServer> servers) { }
        public void SendGameState(bool deaf, float master, IReadOnlyList<SidecarProtocol.GameStatePeerInput> peers)
            => GameStates.Add((deaf, master, peers.Count));

        public void RaiseDead(string reason)
        {
            Health = CaptureHealth.Dead;
            _onDead?.Invoke(reason);
        }

        public void RaiseRecoverableError(string code, string message)
            => _onRecoverableError?.Invoke(code, message);

        public void Dispose()
        {
            Health = CaptureHealth.Dead;
            if (AllowDisposeCompletion != null)
            {
                var cleanupGate = AllowDisposeCompletion;
                var cancellationToken = TestContext.Current.CancellationToken;
                Volatile.Write(ref CleanupCompleteBacking, 0);
                Task.Run(() =>
                {
                    try
                    {
                        cleanupGate.Wait(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    finally
                    {
                        Volatile.Write(ref CleanupCompleteBacking, 1);
                    }
                });
            }
            Sequence.Add("dispose");
            Interlocked.Increment(ref DisposeCountBacking);
            DisposeObservedBacking.TrySetResult(true);
        }

        private static int Count(Delegate? value) => value?.GetInvocationList().Length ?? 0;
    }
}
#endif
