using System.Collections.Concurrent;
using System.Diagnostics;
using PerfectComms.Starlight.Media;
using VoiceChatPlugin.VoiceChat;
using Xunit;

namespace PerfectComms.Starlight.Tests;

public sealed class PrivateRadioTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateScopeFiltersExistingAndLatePeersWithoutChangingConnections(bool mobile)
    {
        using var sender = new RadioSender(mobile);
        await sender.ConnectAsync("1");
        await sender.ConnectAsync("2");
        float[] capture = Signal(181f, 0.4f);
        await sender.SendAsync(capture, "1", "2");

        Assert.True(sender.Configure(true, ["1", "3"]));
        await sender.SendAsync(capture, "1");
        await SettleAsync();
        Assert.Equal(1, sender.PacketCount("2"));

        await sender.ConnectAsync("3");
        await sender.ConnectAsync("4");
        await sender.SendAsync(capture, "1", "3");
        await SettleAsync();
        Assert.Equal(1, sender.PacketCount("2"));
        Assert.Equal(0, sender.PacketCount("4"));

        Assert.True(sender.Configure(true, []));
        sender.Push(capture, capture.Length);
        await SettleAsync();
        Assert.Equal(3, sender.PacketCount("1"));
        Assert.Equal(1, sender.PacketCount("2"));
        Assert.Equal(1, sender.PacketCount("3"));
        Assert.Equal(0, sender.PacketCount("4"));

        Assert.True(sender.Configure(false, ["1"]));
        await sender.SendAsync(Signal(997f, 0.18f), "1", "2", "3", "4");
        sender.AssertConnectionsUnchanged();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopeCutoversDiscardPartialCaptureAndCodecHistory(bool mobile)
    {
        using var sender = new RadioSender(mobile);
        await sender.ConnectAsync("1");
        await sender.ConnectAsync("2");
        Assert.True(sender.Configure(true, ["1"]));
        float[] secret = Signal(181f, 0.7f);
        float[] target = Signal(997f, 0.18f);
        using var expectedEncoder = new ManagedOpusEncoder();
        expectedEncoder.Configure(1f, 0f, 0f);
        expectedEncoder.Reset();
        var expectedPacket = new byte[ManagedOpusEncoder.MaxPacketBytes];
        for (int i = 0; i < 5; i++)
        {
            expectedEncoder.Encode(secret, expectedPacket, out _, out _);
            await sender.SendAsync(secret, "1");
        }

        int half = target.Length / 2;
        sender.Push(target, half);
        Assert.True(sender.Configure(true, ["1", "1"]));
        sender.Push(target[half..], half);
        await sender.WaitForPacketsAsync("1", 6);
        int length = expectedEncoder.Encode(target, expectedPacket, out _, out _);
        Assert.True(sender.Packet("1", 5).AsSpan().SequenceEqual(expectedPacket.AsSpan(0, length)));

        sender.Push(secret, half);
        Assert.True(sender.Configure(true, ["2"]));
        sender.Push(target, half);
        await SettleAsync();
        Assert.Equal(0, sender.PacketCount("2"));
        sender.Push(target[half..], half);
        await sender.WaitForPacketsAsync("2", 1);
        expectedEncoder.Reset();
        length = expectedEncoder.Encode(target, expectedPacket, out _, out _);
        Assert.True(sender.Packet("2", 0).AsSpan().SequenceEqual(expectedPacket.AsSpan(0, length)));
        Assert.Equal(6, sender.PacketCount("1"));

        await sender.SendAsync(secret, "2");
        sender.Push(secret, half);
        Assert.True(sender.Configure(false, []));
        sender.Push(target, half);
        await SettleAsync();
        Assert.Equal(6, sender.PacketCount("1"));
        Assert.Equal(2, sender.PacketCount("2"));
        sender.Push(target[half..], half);
        await sender.WaitForPacketsAsync("1", 7);
        await sender.WaitForPacketsAsync("2", 3);
        expectedEncoder.Reset();
        length = expectedEncoder.Encode(target, expectedPacket, out _, out _);
        Assert.True(sender.Packet("1", 6).AsSpan().SequenceEqual(expectedPacket.AsSpan(0, length)));
        Assert.True(sender.Packet("2", 2).AsSpan().SequenceEqual(expectedPacket.AsSpan(0, length)));
        sender.AssertConnectionsUnchanged();
    }

    private static Task SettleAsync() => Task.Delay(200, TestContext.Current.CancellationToken);

    private static float[] Signal(float frequency, float amplitude)
    {
        var samples = new float[ManagedOpusEncoder.FrameSamples];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = amplitude * (float)Math.Sin(Math.Tau * frequency * i / ManagedOpusEncoder.SampleRate);
        return samples;
    }

    private sealed class RadioSender : IDisposable
    {
        private readonly ManagedVoiceEngine? _engine;
        private readonly MobileVoiceClient? _mobile;
        private readonly ConcurrentDictionary<string, Receiver> _receivers = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _failures = new();
        private readonly ConcurrentQueue<(string Peer, string State)> _states = new();

        public RadioSender(bool mobile)
        {
            if (mobile)
            {
                _mobile = new MobileVoiceClient();
                _mobile.OnLocalSdp += ForwardSdp;
                _mobile.OnLocalCandidate += ForwardCandidate;
                _mobile.OnPeerState += ObserveState;
                Assert.True(_mobile.Start());
                _mobile.SetInput(1f, 0f, 0f);
                _mobile.SetMicActive(true);
            }
            else
            {
                _engine = new ManagedVoiceEngine();
                _engine.LocalSdp += ForwardSdp;
                _engine.LocalCandidate += ForwardCandidate;
                _engine.PeerState += ObserveState;
                Assert.True(_engine.Start());
                _engine.SetInput(1f, 0f, 0f);
                _engine.SetMicActive(true);
            }
        }

        public bool Configure(bool active, IReadOnlyList<string> receivers)
            => _mobile is not null
                ? _mobile.ConfigurePrivateRadio(active, receivers)
                : _engine!.ConfigurePrivateRadio(active, receivers);

        public void Push(float[] samples, int count)
        {
            if (_mobile is not null) _mobile.PushMic(samples, count);
            else _engine!.PushMic(samples, count, 0);
        }

        public async Task ConnectAsync(string id)
        {
            var receiver = new Receiver();
            Assert.True(_receivers.TryAdd(id, receiver));
            receiver.Peer.LocalSdp += (type, sdp) =>
            {
                bool accepted = _mobile is not null
                    ? _mobile.SetRemoteSdp(id, 1, type, sdp)
                    : _engine!.SetRemoteSdp(id, 1, type, sdp);
                if (!accepted) _failures.Enqueue($"Sender rejected {id} {type} SDP.");
            };
            receiver.Peer.LocalCandidate += candidate =>
            {
                bool accepted = _mobile is not null
                    ? _mobile.AddIceCandidate(id, 1, candidate)
                    : _engine!.AddIceCandidate(id, 1, candidate);
                if (!accepted) _failures.Enqueue($"Sender rejected {id} candidate.");
            };
            Assert.True(receiver.Peer.Start(createOffer: false));
            Assert.True(_mobile is not null
                ? _mobile.AddPeer(id, true, 1)
                : _engine!.AddPeer(id, true, 1));
            await Task.WhenAll(receiver.Connected.Task, receiver.SenderConnected.Task).WaitAsync(
                TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken);
            Assert.Empty(_failures);
        }

        public int PacketCount(string id) => _receivers[id].Packets.Count;
        public byte[] Packet(string id, int index) => _receivers[id].Packets.ToArray()[index];

        public async Task SendAsync(float[] samples, params string[] recipients)
        {
            int[] expected = recipients.Select(id => PacketCount(id) + 1).ToArray();
            Push(samples, samples.Length);
            for (int i = 0; i < recipients.Length; i++)
                await WaitForPacketsAsync(recipients[i], expected[i]);
        }

        public async Task WaitForPacketsAsync(string id, int expected)
        {
            var deadline = Stopwatch.StartNew();
            while (PacketCount(id) < expected && deadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.Equal(expected, PacketCount(id));
            Assert.Empty(_failures);
        }

        public void AssertConnectionsUnchanged()
        {
            Assert.Empty(_failures);
            foreach ((string id, Receiver receiver) in _receivers)
            {
                Assert.True(receiver.Peer.IsConnected);
                Assert.Equal(1, _states.Count(state => state.Peer == id && state.State == "connected"));
                Assert.DoesNotContain(_states, state => state.Peer == id &&
                    state.State is "failed" or "disconnected" or "closed");
                Assert.DoesNotContain(receiver.States, state => state is "failed" or "disconnected" or "closed");
            }
        }

        private void ForwardSdp(string id, int generation, string type, string sdp)
        {
            if (!_receivers[id].Peer.SetRemoteSdp(type, sdp))
                _failures.Enqueue($"Receiver {id} rejected {type} SDP.");
        }

        private void ForwardCandidate(string id, int generation, string candidate)
        {
            if (!_receivers[id].Peer.AddIceCandidate(candidate))
                _failures.Enqueue($"Receiver {id} rejected candidate.");
        }

        private void ObserveState(string id, int generation, string state)
        {
            _states.Enqueue((id, state));
            if (state == "connected") _receivers[id].SenderConnected.TrySetResult();
        }

        public void Dispose()
        {
            _mobile?.Dispose();
            _engine?.Dispose();
            foreach (Receiver receiver in _receivers.Values) receiver.Peer.Dispose();
        }
    }

    private sealed class Receiver
    {
        public ManagedWebRtcPeer Peer { get; } = new("sender", 1, []);
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SenderConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<byte[]> Packets { get; } = new();
        public ConcurrentQueue<string> States { get; } = new();

        public Receiver()
        {
            Peer.OpusPacketReceived += (_, _, _, payload) => Packets.Enqueue(payload.AsSpan().ToArray());
            Peer.StateChanged += state =>
            {
                States.Enqueue(state);
                if (state == "connected") Connected.TrySetResult();
            };
        }
    }
}
