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
            new SidecarProtocol.GameStatePeerInput("42", 1f, 0f, 0)
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
        Assert.Equal(9, secondClient.HandlerCount);
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

        Assert.False(lease.RemovePeer("42"));
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
        Assert.Equal(9, secondClient.HandlerCount);
        second.Dispose();
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

        public event Action<float[], int>? OnFrame { add => _onFrame += value; remove => _onFrame -= value; }
        public event Action<string>? OnDead { add => _onDead += value; remove => _onDead -= value; }
        public event Action<string, string>? OnRecoverableError { add => _onRecoverableError += value; remove => _onRecoverableError -= value; }
        public event Action<string, int, string, string>? OnLocalSdp { add => _onLocalSdp += value; remove => _onLocalSdp -= value; }
        public event Action<string, int, string>? OnLocalCandidate { add => _onLocalCandidate += value; remove => _onLocalCandidate -= value; }
        public event Action<string, int, string>? OnPeerState { add => _onPeerState += value; remove => _onPeerState -= value; }
        public event Action<float, bool>? OnLevel { add => _onLevel += value; remove => _onLevel -= value; }
        public event Action<IReadOnlyList<SidecarProtocol.PeerLevel>>? OnPeerLevels { add => _onPeerLevels += value; remove => _onPeerLevels -= value; }
        public event Action<SidecarPlaybackState>? OnPlaybackState { add => _onPlaybackState += value; remove => _onPlaybackState -= value; }

        public CaptureHealth Health { get; private set; } = CaptureHealth.Dead;
        public IReadOnlyList<VoiceDeviceInfo> OutputDevices { get; } =
            new[] { new VoiceDeviceInfo("speaker-id", "speaker", true) };
        public int StartCount => Volatile.Read(ref StartCountBacking);
        public int DisposeCount { get; private set; }
        public ManualResetEventSlim? StartEntered { get; init; }
        public ManualResetEventSlim? AllowStart { get; init; }
        public List<bool> MicActiveCalls { get; } = new();
        public List<bool> SyntheticCalls { get; } = new();
        public List<string> RemovedPeers { get; } = new();
        public int RemoveFailuresRemaining { get; set; }
        public List<(bool Deaf, float Master, int Peers)> GameStates { get; } = new();

        public int HandlerCount =>
            Count(_onFrame) + Count(_onDead) + Count(_onRecoverableError) + Count(_onLocalSdp) + Count(_onLocalCandidate) +
            Count(_onPeerState) + Count(_onLevel) + Count(_onPeerLevels) + Count(_onPlaybackState);

        public bool Start(string? micDevice, string? spkDevice)
        {
            Interlocked.Increment(ref StartCountBacking);
            StartEntered?.Set();
            AllowStart?.Wait(TimeSpan.FromSeconds(5));
            Health = CaptureHealth.Healthy;
            return true;
        }

        private int StartCountBacking;

        public bool TryConfigureInitialCapture(string micDevice, string outputDevice, bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf, float gain, float vadThreshold, float noiseGateThreshold, bool synthetic, bool micActive, IEnumerable<IceServer>? iceServers) => true;
        public void SetDsp(bool aec, bool agc, bool ns, bool nsVeryHigh, bool hpf) { }
        public void SetSynthetic(bool enabled) => SyntheticCalls.Add(enabled);
        public void SetMonitor(bool enabled, bool delayed, float gain) { }
        public void SetInput(float gain, float vadThreshold, float noiseGateThreshold) { }
        public void SetMicActive(bool active) => MicActiveCalls.Add(active);
        public void SelectMicDevice(string deviceId) { }
        public void SelectOutputDevice(string deviceId) { }
        public void SendOutputTestFrame(float[] interleavedStereo) { }
        public bool AddPeer(string peerId, bool isOfferer, int generation) => true;
        public bool RemovePeer(string peerId)
        {
            RemovedPeers.Add(peerId);
            if (RemoveFailuresRemaining <= 0) return true;
            RemoveFailuresRemaining--;
            return false;
        }
        public bool RestartIce(string peerId, bool createOffer) => true;
        public bool SetRemoteSdp(string peerId, string sdpType, string sdp) => true;
        public bool AddIceCandidate(string peerId, string candidate) => true;
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
        }

        private static int Count(Delegate? value) => value?.GetInvocationList().Length ?? 0;
    }
}
#endif
