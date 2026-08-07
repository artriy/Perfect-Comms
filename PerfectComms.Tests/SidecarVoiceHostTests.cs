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
        first.AddPeer("42", isOfferer: true, generation: 1);
        first.SendGameState(false, 1f, new[]
        {
            new SidecarProtocol.GameStatePeerInput("42", 1f, 0f, 0, false)
        });

        first.Dispose();

        Assert.Equal(0, firstClient.HandlerCount);
        Assert.False(firstClient.MicActiveCalls[^1]);
        Assert.False(firstClient.SyntheticCalls[^1]);
        Assert.Contains("42", firstClient.RemovedPeers);
        Assert.Equal((true, 0f, 0), firstClient.GameStates[^1]);
        Assert.Equal(1, firstClient.DisposeCount);

        var second = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out failure));
        Assert.Equal(string.Empty, failure);
        Assert.Equal(0, secondClient.HandlerCount);
        Assert.True(second.EnsureStarted("mic-b", "spk-b"));
        Assert.Equal(10, secondClient.HandlerCount);
        Assert.Equal(1, firstClient.StartCount);
        Assert.Equal(1, secondClient.StartCount);
        Assert.Equal(2, createCount);

        second.Dispose();
        Assert.Equal(1, secondClient.DisposeCount);
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

        Assert.False(lease.RemovePeer("42", generation: 1));
        lease.Dispose();

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
    public void ConditionalOutputSelectionDoesNotSendWhenSuperseded()
    {
        var fake = new FakeSidecarVoiceClient();
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        Assert.False(lease.TrySelectOutputDeviceIf("stale", () => false));
        Assert.Empty(fake.OutputSelectionCalls);

        Assert.True(lease.TrySelectOutputDeviceIf("current", () => true));
        Assert.Equal(new[] { "current" }, fake.OutputSelectionCalls);
        lease.Dispose();
    }

    [Fact]
    public void ConditionalAudioRouteDoesNotSendWhenSuperseded()
    {
        var fake = new FakeSidecarVoiceClient();
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        Assert.False(lease.TryConfigureAudioRouteIf(
            "mic-stale", "spk-stale", SidecarCaptureMode.Warm, synthetic: false, () => false));
        Assert.Empty(fake.AudioRouteCalls);

        Assert.True(lease.TryConfigureAudioRouteIf(
            "mic-current", "spk-current", SidecarCaptureMode.Transmit, synthetic: true, () => true));
        Assert.Equal(
            ("mic-current", "spk-current", SidecarCaptureMode.Transmit, true),
            Assert.Single(fake.AudioRouteCalls));
        lease.Dispose();
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
        Assert.Equal(new[] { true }, fake.MicActiveCalls);
        lease.Dispose();
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
        Assert.Equal(1, fake.DisposeCount);
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
        Assert.Equal(1, fake.DisposeCount);
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
        Assert.Equal(1, firstClient.DisposeCount);
        Assert.Equal(0, firstClient.HandlerCount);

        var second = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(second.EnsureStarted("mic", "spk"));
        Assert.Equal(2, created);
        Assert.Equal(10, secondClient.HandlerCount);
        second.Dispose();
    }

    [Fact]
    public void RetiringHelperBlocksReplacementUntilCleanupCompletes()
    {
        using var allowDisposeCompletion = new ManualResetEventSlim();
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

        Assert.False(firstClient.CleanupComplete);
        Assert.Null(host.TryAcquire(Callbacks(), out var failure));
        Assert.Equal("helper-retiring", failure);
        Assert.Equal(1, created);

        allowDisposeCompletion.Set();
        Assert.True(SpinWait.SpinUntil(
            () => firstClient.CleanupComplete,
            TimeSpan.FromSeconds(2)));
        var second = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out failure));
        Assert.Equal(string.Empty, failure);
        Assert.True(second.EnsureStarted("mic", "spk"));
        Assert.Equal(2, created);
        second.Dispose();
    }

    [Fact]
    public void ActiveLeaseDoesNotReplaceDeadHelperBeforeCleanupCompletes()
    {
        using var allowDisposeCompletion = new ManualResetEventSlim();
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
        Assert.False(firstClient.CleanupComplete);
        Assert.Equal(1, created);

        allowDisposeCompletion.Set();
        Assert.True(SpinWait.SpinUntil(
            () => firstClient.CleanupComplete,
            TimeSpan.FromSeconds(2)));
        Assert.True(lease.EnsureStarted("mic", "spk"));
        Assert.Equal(2, created);
        lease.Dispose();
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
    }

    [Fact]
    public void ProcessShutdownKillsHelperEvenWithActiveLease()
    {
        var fake = new FakeSidecarVoiceClient();
        var host = new SidecarVoiceHostCore(() => fake);
        var lease = Assert.IsType<SidecarVoiceLease>(host.TryAcquire(Callbacks(), out _));
        Assert.True(lease.EnsureStarted("mic", "spk"));

        host.Shutdown("test-exit");

        Assert.Equal(1, fake.DisposeCount);
        Assert.Equal(0, fake.HandlerCount);
        Assert.False(lease.IsActive);
        Assert.Equal(CaptureHealth.Dead, lease.Health);
        Assert.Null(host.TryAcquire(Callbacks(), out var failure));
        Assert.Equal("host-shutdown", failure);
    }

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
        public int DisposeCount { get; private set; }
        public bool CleanupComplete => Volatile.Read(ref CleanupCompleteBacking) != 0;
        public ManualResetEventSlim? StartEntered { get; init; }
        public ManualResetEventSlim? AllowStart { get; init; }
        public ManualResetEventSlim? AllowDisposeCompletion { get; init; }
        public List<bool> MicActiveCalls { get; } = new();
        public List<bool> SyntheticCalls { get; } = new();
        public List<string> RemovedPeers { get; } = new();
        public List<string> OutputSelectionCalls { get; } = new();
        public List<(string Input, string Output, SidecarCaptureMode Mode, bool Synthetic)> AudioRouteCalls { get; } = new();
        public int RemoveFailuresRemaining { get; set; }
        public List<(bool Deaf, float Master, int Peers)> GameStates { get; } = new();

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
        private int CleanupCompleteBacking = 1;

        public bool TryConfigureInitialCapture(string micDevice, string outputDevice, bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf, float gain, float vadThreshold, float noiseGateThreshold, bool synthetic, bool micActive, bool micWarm, bool monitorEnabled, bool monitorDelayed, float monitorGain, IEnumerable<IceServer>? iceServers) => true;
        public void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf) { }
        public void SetSynthetic(bool enabled) => SyntheticCalls.Add(enabled);
        public void SetMonitor(bool enabled, bool delayed, float gain) { }
        public void SetInput(float gain, float vadThreshold, float noiseGateThreshold) { }
        public void SetMicActive(bool active) => MicActiveCalls.Add(active);
        public void SetMicWarm() { }
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
            return true;
        }
        public void SendOutputTestFrame(float[] interleavedStereo) { }
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
            DisposeCount++;
            Health = CaptureHealth.Dead;
            if (AllowDisposeCompletion == null) return;
            Volatile.Write(ref CleanupCompleteBacking, 0);
            Task.Run(() =>
            {
                AllowDisposeCompletion.Wait();
                Volatile.Write(ref CleanupCompleteBacking, 1);
            });
        }

        private static int Count(Delegate? value) => value?.GetInvocationList().Length ?? 0;
    }
}
#endif
