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

    [Fact]
    public async Task ObsCompatibilityUsesCurrentSettingAfterColdStartAndHelperReplacement()
    {
        var enabled = false;
        var firstClient = new FakeSidecarVoiceClient();
        var secondClient = new FakeSidecarVoiceClient();
        var createCount = 0;
        var host = new SidecarVoiceHostCore(
            () => Interlocked.Increment(ref createCount) == 1 ? firstClient : secondClient,
            () => enabled);
        enabled = true;
        host.ApplyObsCompatibilityMode();
        var first = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(first.EnsureStarted("mic", "speaker"));
        Assert.True(first.SendOutputTestFrameAndWait(new[] { 0f, 0f }));
        Assert.Equal(new[] { true }, firstClient.ObsCompatibilityCalls);

        enabled = false;
        host.ApplyObsCompatibilityMode();
        Assert.True(first.SendOutputTestFrameAndWait(new[] { 0f, 0f }));
        Assert.Equal(new[] { true, false }, firstClient.ObsCompatibilityCalls);
        Assert.Equal(1, firstClient.StartCount);
        Assert.Empty(firstClient.MicActiveCalls);
        Assert.Empty(firstClient.SyntheticCalls);
        Assert.Empty(firstClient.AudioRouteCalls);
        Assert.Empty(firstClient.OutputSelectionCalls);
        await AwaitCompletion(first.ReleaseAsync());

        enabled = true;
        host.ApplyObsCompatibilityMode();
        var second = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(second.EnsureStarted("mic", "speaker"));
        Assert.True(second.SendOutputTestFrameAndWait(new[] { 0f, 0f }));
        Assert.Equal(new[] { true }, secondClient.ObsCompatibilityCalls);
        Assert.Equal(new[] { true, false }, firstClient.ObsCompatibilityCalls);
        await AwaitCompletion(second.ReleaseAsync());
    }

    [Fact]
    public async Task ObsCompatibilityChangeDuringReadinessUsesLatestSetting()
    {
        using var startEntered = new ManualResetEventSlim();
        using var allowStart = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            StartEntered = startEntered,
            AllowStart = allowStart,
        };
        var enabled = false;
        var host = new SidecarVoiceHostCore(() => fake, () => enabled);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        var startup = Task.Run(() => lease.EnsureStarted("mic", "speaker"));
        try
        {
            Assert.True(startEntered.Wait(TimeSpan.FromSeconds(2)));
            enabled = true;
            host.ApplyObsCompatibilityMode();
        }
        finally
        {
            allowStart.Set();
        }
        Assert.True(await startup);
        Assert.True(lease.SendOutputTestFrameAndWait(new[] { 0f, 0f }));
        Assert.Equal(new[] { true }, fake.ObsCompatibilityCalls);
        await AwaitCompletion(lease.ReleaseAsync());
    }

    [Fact]
    public async Task UnsupportedHelperReceivesNoObsCompatibilityCommand()
    {
        var fake = new FakeSidecarVoiceClient();
        var host = new SidecarVoiceHostCore(() => fake, () => null);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "speaker"));
        host.ApplyObsCompatibilityMode();
        Assert.True(lease.SendOutputTestFrameAndWait(new[] { 0f, 0f }));
        Assert.Empty(fake.ObsCompatibilityCalls);
        Assert.Equal(CaptureHealth.Healthy, lease.Health);
        await AwaitCompletion(lease.ReleaseAsync());
    }

    [Fact]
    public async Task ObsCompatibilityReappliesAfterDeadHelperRestartOnSameLease()
    {
        var firstClient = new FakeSidecarVoiceClient();
        var secondClient = new FakeSidecarVoiceClient();
        var createCount = 0;
        var host = new SidecarVoiceHostCore(
            () => Interlocked.Increment(ref createCount) == 1 ? firstClient : secondClient,
            () => true);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "speaker"));
        Assert.True(lease.SendOutputTestFrameAndWait(new[] { 0f, 0f }));
        firstClient.RaiseDead("helper exited");
        Assert.False(lease.EnsureStarted("mic", "speaker"));

        Assert.True(await TryUntilAsync(() => lease.EnsureStarted("mic", "speaker")));
        Assert.True(lease.SendOutputTestFrameAndWait(new[] { 0f, 0f }));
        Assert.Equal(new[] { true }, firstClient.ObsCompatibilityCalls);
        Assert.Equal(new[] { true }, secondClient.ObsCompatibilityCalls);
        Assert.Equal(1, secondClient.StartCount);
        await AwaitCompletion(lease.ReleaseAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ObsCompatibilityChangeSurvivesFullCommandQueue(bool requested, bool reserveCritical)
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
        var enabled = !requested;
        var host = new SidecarVoiceHostCore(() => fake, commandCapacity: 1,
            obsCompatibilityMode: () => Volatile.Read(ref enabled));
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        try
        {
            Assert.True(lease.EnsureStarted("mic", "speaker"));
            Assert.True(await TryUntilAsync(() => lease.SendOutputTestFrameAndWait(new[] { 0f, 0f })));
            int sequenceStart = fake.Sequence.Count;
            lease.SetMicActive(true);
            Assert.True(await Task.Run(
                () => commandEntered.Wait(TimeSpan.FromSeconds(2), cancellationToken), cancellationToken));
            Assert.True(lease.ConfigureAudioRoute("mic", "speaker", SidecarCaptureMode.Warm, false));
            if (reserveCritical)
                Assert.True(lease.ConfigureAudioRoute("reserved", "speaker", SidecarCaptureMode.Stopped, false));

            Volatile.Write(ref enabled, requested);
            host.ApplyObsCompatibilityMode();
            Volatile.Write(ref enabled, !requested);
            host.ApplyObsCompatibilityMode();
            Volatile.Write(ref enabled, requested);
            host.ApplyObsCompatibilityMode();
            allowCommand.Set();

            Assert.True(await TryUntilAsync(() => lease.SendOutputTestFrameAndWait(new[] { 0f, 0f })));
            Assert.Equal(new[] { !requested, requested }, fake.ObsCompatibilityCalls);
            Assert.Equal(1, fake.StartCount);
            Assert.Equal(CaptureHealth.Healthy, lease.Health);
            if (reserveCritical)
                Assert.Equal(new[]
                {
                    "mic:true", "route:mic:speaker", "route:reserved:speaker",
                    $"obs:{requested.ToString().ToLowerInvariant()}",
                }, fake.Sequence.Skip(sequenceStart));
        }
        finally
        {
            allowCommand.Set();
            await AwaitCompletion(lease.ReleaseAsync());
        }
    }

    [Fact]
    public async Task ObsCompatibilityPendingChangeCannotOutliveItsLease()
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
        var enabled = false;
        var host = new SidecarVoiceHostCore(() => fake, commandCapacity: 1,
            obsCompatibilityMode: () => Volatile.Read(ref enabled));
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        try
        {
            Assert.True(lease.EnsureStarted("mic", "speaker"));
            Assert.True(await TryUntilAsync(() => lease.SendOutputTestFrameAndWait(new[] { 0f, 0f })));
            lease.SetMicActive(true);
            Assert.True(await Task.Run(
                () => commandEntered.Wait(TimeSpan.FromSeconds(2), cancellationToken), cancellationToken));
            Assert.True(lease.ConfigureAudioRoute("mic", "speaker", SidecarCaptureMode.Warm, false));
            Volatile.Write(ref enabled, true);
            host.ApplyObsCompatibilityMode();

            var released = lease.ReleaseAsync();
            allowCommand.Set();
            await AwaitCompletion(released);

            Assert.Equal(new[] { false }, fake.ObsCompatibilityCalls);
            Assert.Equal(1, fake.DisposeCount);
        }
        finally
        {
            allowCommand.Set();
            await AwaitCompletion(lease.ReleaseAsync());
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task RecordingChangesCoalesceBehindCriticalRoutesAndDoNotLeakIntoReplacement(bool enabled, int mode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        var first = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true, CommandEntered = entered, AllowCommand = unblock,
        };
        var second = new FakeSidecarVoiceClient();
        var created = 0;
        int setting = 0;
        var host = new SidecarVoiceHostCore(
            () => Interlocked.Increment(ref created) == 1 ? first : second,
            commandCapacity: 1,
            obsRecordingSettings: () =>
            {
                int current = Volatile.Read(ref setting);
                return new SidecarObsRecordingSettings((current & 1) != 0, (VoiceObsRecordingMode)(current >> 1));
            });
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        try
        {
            Assert.True(lease.EnsureStarted("mic", "speaker"));
            Assert.True(await TryUntilAsync(() => lease.SendOutputTestFrameAndWait(new[] { 0f, 0f })));
            lease.SetMicActive(true);
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2), cancellationToken), cancellationToken));
            Assert.True(lease.ConfigureAudioRoute("queued", "speaker", SidecarCaptureMode.Warm, false));
            Assert.True(lease.ConfigureAudioRoute("reserved", "speaker", SidecarCaptureMode.Stopped, false));
            Volatile.Write(ref setting, 3);
            host.ApplyObsRecording();
            Volatile.Write(ref setting, enabled ? 1 | (mode << 1) : 0);
            host.ApplyObsRecording();
            unblock.Set();
            Assert.True(await TryUntilAsync(() => lease.SendOutputTestFrameAndWait(new[] { 0f, 0f })));
            Assert.Equal(new[]
            {
                new SidecarObsRecordingSettings(false, VoiceObsRecordingMode.PerPlayer),
                new SidecarObsRecordingSettings(enabled, (VoiceObsRecordingMode)mode),
            }, first.ObsRecordingCalls);
            Assert.Equal(CaptureHealth.Healthy, lease.Health);
            await AwaitCompletion(lease.ReleaseAsync());

            Volatile.Write(ref setting, 0);
            var replacement = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
            try
            {
                Assert.True(replacement.EnsureStarted("mic", "speaker"));
                Assert.True(await TryUntilAsync(() => replacement.SendOutputTestFrameAndWait(new[] { 0f, 0f })));
                Assert.Equal(new[] { new SidecarObsRecordingSettings(false, VoiceObsRecordingMode.PerPlayer) },
                    second.ObsRecordingCalls);
            }
            finally { await AwaitCompletion(replacement.ReleaseAsync()); }
        }
        finally
        {
            unblock.Set();
            await AwaitCompletion(lease.ReleaseAsync());
        }
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("alias")]
    [InlineData("deaf")]
    [InlineData("removed")]
    public async Task LatestGameStateBypassesFullQueueAndNeverReplaysReplacedIdentity(string transition)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        var fake = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true, CommandEntered = entered, AllowCommand = unblock,
        };
        var host = new SidecarVoiceHostCore(() => fake, commandCapacity: 1);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        try
        {
            Assert.True(lease.EnsureStarted("mic", "speaker"));
            lease.SetMicActive(true);
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2), cancellationToken), cancellationToken));
            Assert.True(lease.ConfigureAudioRoute("queued", "speaker", SidecarCaptureMode.Warm, false));
            Assert.True(lease.ConfigureAudioRoute("reserved", "speaker", SidecarCaptureMode.Stopped, false));
            var named = new[] { new SidecarProtocol.GameStatePeerInput("42", 1f, 0f, 0, false, 8, "Original", 1) };
            for (var update = 0; update < 1000; update++)
                Assert.True(lease.SendGameState(false, 1f, named));
            var latest = transition switch
            {
                "anonymous" => new[] { new SidecarProtocol.GameStatePeerInput("42", 1f, 0f, 0, false) },
                "alias" => new[] { new SidecarProtocol.GameStatePeerInput("42", 1f, 0f, 0, false, 10, "Alias", 2) },
                "deaf" => named,
                _ => Array.Empty<SidecarProtocol.GameStatePeerInput>(),
            };
            bool deaf = transition == "deaf";
            Assert.True(lease.SendGameState(deaf, 1f, latest));
            unblock.Set();
            Assert.True(await TryUntilAsync(() => lease.SendOutputTestFrameAndWait(new[] { 0f, 0f })));

            var sent = Assert.Single(fake.GameStateRoutes);
            Assert.Equal(deaf, sent.Deaf);
            Assert.Equal(latest, sent.Peers);
            Assert.Equal(new[] { "mic:true", "game-state", "route:queued:speaker", "route:reserved:speaker" },
                fake.Sequence);
        }
        finally
        {
            unblock.Set();
            await AwaitCompletion(lease.ReleaseAsync());
        }
    }

    [Fact]
    public async Task RetiredLeaseDropsPendingGameStateAndRecordingSettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        var first = new FakeSidecarVoiceClient
        {
            BlockNextMicActive = true, CommandEntered = entered, AllowCommand = unblock,
        };
        var second = new FakeSidecarVoiceClient();
        int created = 0;
        int enabled = 0;
        var host = new SidecarVoiceHostCore(
            () => Interlocked.Increment(ref created) == 1 ? first : second,
            commandCapacity: 1,
            obsRecordingSettings: () => new SidecarObsRecordingSettings(Volatile.Read(ref enabled) != 0,
                VoiceObsRecordingMode.PerPlayer));
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        try
        {
            Assert.True(lease.EnsureStarted("mic", "speaker"));
            Assert.True(await TryUntilAsync(() => lease.SendOutputTestFrameAndWait(new[] { 0f, 0f })));
            lease.SetMicActive(true);
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2), cancellationToken), cancellationToken));
            Assert.True(lease.ConfigureAudioRoute("queued", "speaker", SidecarCaptureMode.Warm, false));
            Assert.True(lease.ConfigureAudioRoute("reserved", "speaker", SidecarCaptureMode.Stopped, false));
            var named = new[] { new SidecarProtocol.GameStatePeerInput("42", 1f, 0f, 0, false, 8, "Original", 1) };
            Assert.True(lease.SendGameState(false, 1f, named));
            Volatile.Write(ref enabled, 1);
            host.ApplyObsRecording();
            var retirement = lease.ReleaseAsync();
            Assert.False(lease.SendGameState(false, 1f, named));
            unblock.Set();
            await AwaitCompletion(retirement);

            var quiesced = Assert.Single(first.GameStateRoutes);
            Assert.True(quiesced.Deaf);
            Assert.Empty(quiesced.Peers);
            Assert.Equal(new[] { new SidecarObsRecordingSettings(false, VoiceObsRecordingMode.PerPlayer) },
                first.ObsRecordingCalls);
            Volatile.Write(ref enabled, 0);
            var replacement = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
            try
            {
                Assert.True(replacement.EnsureStarted("mic", "speaker"));
                Assert.True(await TryUntilAsync(() => replacement.SendOutputTestFrameAndWait(new[] { 0f, 0f })));
                Assert.Empty(second.GameStateRoutes);
                Assert.Equal(new[] { new SidecarObsRecordingSettings(false, VoiceObsRecordingMode.PerPlayer) },
                    second.ObsRecordingCalls);
            }
            finally { await AwaitCompletion(replacement.ReleaseAsync()); }
        }
        finally
        {
            unblock.Set();
            await AwaitCompletion(lease.ReleaseAsync());
        }
    }

    [Fact]
    public async Task RecordingStatusRejectsRetiredClientEvenWhenItsLeaseRestarts()
    {
        var first = new FakeSidecarVoiceClient();
        var second = new FakeSidecarVoiceClient();
        int created = 0;
        var host = new SidecarVoiceHostCore(
            () => Interlocked.Increment(ref created) == 1 ? first : second,
            obsRecordingSettings: () => new SidecarObsRecordingSettings(true, VoiceObsRecordingMode.PerPlayer));
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        try
        {
            Assert.True(lease.EnsureStarted("mic", "speaker"));
            first.RaiseObsRecordingStatus("recording");
            Assert.Equal("recording", VoiceObsRecording.Status.State);
            var lateFirstStatus = first.CaptureObsRecordingStatusCallback();
            first.RaiseDead("disconnected");
            Assert.Equal("waiting", VoiceObsRecording.Status.State);
            lateFirstStatus(new VoiceObsRecordingStatus("recording", "stale"));
            Assert.Equal("waiting", VoiceObsRecording.Status.State);
            Assert.False(lease.EnsureStarted("mic", "speaker"));
            Assert.True(await TryUntilAsync(() => lease.EnsureStarted("mic", "speaker")));
            second.RaiseObsRecordingStatus("ready");
            lateFirstStatus(new VoiceObsRecordingStatus("error", "retired helper"));
            Assert.Equal("ready", VoiceObsRecording.Status.State);
            var lateSecondStatus = second.CaptureObsRecordingStatusCallback();
            var retirement = lease.ReleaseAsync();
            Assert.Equal("waiting", VoiceObsRecording.Status.State);
            lateSecondStatus(new VoiceObsRecordingStatus("recording", "retired lease"));
            Assert.Equal("waiting", VoiceObsRecording.Status.State);
            await AwaitCompletion(retirement);
            Assert.Equal(0, first.HandlerCount);
            Assert.Equal(0, second.HandlerCount);
        }
        finally { await AwaitCompletion(lease.ReleaseAsync()); }
    }

    [Fact]
    public async Task DisabledRecordingIgnoresInFlightHelperStatus()
    {
        var fake = new FakeSidecarVoiceClient();
        bool enabled = true;
        var host = new SidecarVoiceHostCore(() => fake,
            obsRecordingSettings: () => new SidecarObsRecordingSettings(enabled, VoiceObsRecordingMode.PerPlayer));
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        try
        {
            Assert.True(lease.EnsureStarted("mic", "speaker"));
            fake.RaiseObsRecordingStatus("recording");
            Assert.Equal("recording", VoiceObsRecording.Status.State);
            enabled = false;
            VoiceObsRecording.SettingChanged(false);
            host.ApplyObsRecording();
            fake.RaiseObsRecordingStatus("error");
            Assert.Equal("disabled", VoiceObsRecording.Status.State);
        }
        finally { await AwaitCompletion(lease.ReleaseAsync()); }
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
        private Action<VoiceObsRecordingStatus>? _onObsRecordingStatus;

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
        public event Action<VoiceObsRecordingStatus>? OnObsRecordingStatus { add => _onObsRecordingStatus += value; remove => _onObsRecordingStatus -= value; }

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
        public List<bool> ObsCompatibilityCalls { get; } = new();
        public List<SidecarObsRecordingSettings> ObsRecordingCalls { get; } = new();
        public List<string> RemovedPeers { get; } = new();
        public List<string> OutputSelectionCalls { get; } = new();
        public List<(string Input, string Output, SidecarCaptureMode Mode, bool Synthetic)> AudioRouteCalls { get; } = new();
        public List<float[]> OutputFrames { get; } = new();
        public int RemoveFailuresRemaining { get; set; }
        public List<(bool Deaf, float Master, int Peers)> GameStates { get; } = new();
        public List<(bool Deaf, float Master, SidecarProtocol.GameStatePeerInput[] Peers)> GameStateRoutes { get; } = new();
        public List<string> Sequence { get; } = new();
        public Task DisposeObserved => DisposeObservedBacking.Task;

        public int HandlerCount =>
            Count(_onFrame) + Count(_onDead) + Count(_onRecoverableError) + Count(_onLocalSdp) + Count(_onLocalCandidate) +
            Count(_onPeerState) + Count(_onLevel) + Count(_onPeerLevels) + Count(_onCaptureState) + Count(_onPlaybackState) +
            Count(_onObsRecordingStatus);

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
        public void SetObsCompatibilityMode(bool enabled)
        {
            ObsCompatibilityCalls.Add(enabled);
            Sequence.Add($"obs:{enabled.ToString().ToLowerInvariant()}");
        }
        public void SetObsRecording(bool enabled, VoiceObsRecordingMode mode)
            => ObsRecordingCalls.Add(new SidecarObsRecordingSettings(enabled, mode));
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
        {
            GameStates.Add((deaf, master, peers.Count));
            GameStateRoutes.Add((deaf, master, peers.ToArray()));
            Sequence.Add("game-state");
        }

        public void RaiseObsRecordingStatus(string state)
            => _onObsRecordingStatus?.Invoke(new VoiceObsRecordingStatus(state, string.Empty));

        public Action<VoiceObsRecordingStatus> CaptureObsRecordingStatusCallback()
            => _onObsRecordingStatus ?? (_ => { });

        public void RaiseDead(string reason)
        {
            Health = CaptureHealth.Dead;
            RaiseObsRecordingStatus("waiting");
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
