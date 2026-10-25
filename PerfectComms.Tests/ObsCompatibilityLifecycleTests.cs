#if WINDOWS
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class ObsCompatibilityLifecycleTests
{
    [Theory]
    [InlineData("obs-window", false)]
    [InlineData("obs-recording", true)]
    public async Task ObsFeatureFailureKeepsControlHealthyAndAllowsLiveDisable(string errorCode, bool recording)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        serverCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var levelReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disableReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recordingError = new TaskCompletionSource<VoiceObsRecordingStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recordingWaiting = new TaskCompletionSource<VoiceObsRecordingStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deaths = 0;
        var microphoneErrors = 0;
        var client = new SidecarVoiceClient((_, _) => new SidecarLaunchResult
        {
            Success = true,
            Port = port,
        })
        {
            PingIntervalMs = 10_000,
        };
        client.OnObsRecordingStatus += status =>
        {
            if (status.State == "error") recordingError.TrySetResult(status);
            if (status.State == "waiting") recordingWaiting.TrySetResult(status);
        };
        client.OnDead += _ => Interlocked.Increment(ref deaths);
        client.OnRecoverableError += (_, _) => Interlocked.Increment(ref microphoneErrors);
        client.OnLevel += (_, _) => levelReceived.TrySetResult(true);
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(serverCancellation.Token);
            var stream = connection.GetStream();
            using var hello = await ReadControl(stream, serverCancellation.Token);
            Assert.Equal("hello", hello.RootElement.GetProperty("op").GetString());
            await stream.WriteAsync(SidecarProtocol.EncodeControl(
                $"{{\"op\":\"ready\",\"proto\":{SidecarVoiceClient.Proto},\"format\":{{\"rate\":48000,\"channels\":2,\"sample\":\"f32\"}}}}"), serverCancellation.Token);
            using var enable = await ReadControl(stream, serverCancellation.Token);
            Assert.Equal(recording ? "set-obs-recording" : "set-obs-compatibility",
                enable.RootElement.GetProperty("op").GetString());
            Assert.True(enable.RootElement.GetProperty("enabled").GetBoolean());
            await stream.WriteAsync(SidecarProtocol.EncodeControl(
                $"{{\"op\":\"error\",\"code\":\"{errorCode}\",\"msg\":\"OBS setup failed\"}}"), serverCancellation.Token);
            await stream.WriteAsync(SidecarProtocol.EncodeControl(
                "{\"op\":\"level\",\"peak\":0.25,\"speaking\":false}"), serverCancellation.Token);
            using var disable = await ReadControl(stream, serverCancellation.Token);
            Assert.Equal(recording ? "set-obs-recording" : "set-obs-compatibility",
                disable.RootElement.GetProperty("op").GetString());
            Assert.False(disable.RootElement.GetProperty("enabled").GetBoolean());
            if (recording) Assert.Equal(0, disable.RootElement.GetProperty("mode").GetInt32());
            disableReceived.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, serverCancellation.Token);
        }, serverCancellation.Token);
        try
        {
            Assert.True(client.Start(null, null));
            if (recording) client.SetObsRecording(true, VoiceObsRecordingMode.PerPlayer);
            else client.SetObsCompatibilityMode(true);
            await levelReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Equal(CaptureHealth.Healthy, client.Health);
            if (recording)
                Assert.Equal("OBS setup failed", (await recordingError.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)).Message);
            Assert.Equal(0, Volatile.Read(ref deaths));
            Assert.Equal(0, Volatile.Read(ref microphoneErrors));
            if (recording) client.SetObsRecording(false, (VoiceObsRecordingMode)99);
            else client.SetObsCompatibilityMode(false);
            await disableReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Equal(CaptureHealth.Healthy, client.Health);
            client.Stop();
            Assert.Equal("waiting", (await recordingWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)).State);
        }
        finally
        {
            client.Dispose();
            serverCancellation.Cancel();
            listener.Stop();
            try
            {
                await server;
            }
            catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task RecordingStatusIsRelayedWithoutGlobalMutationAndResetsOnHelperEof()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        serverCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var recording = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeHelper = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var globalStatus = VoiceObsRecording.Status;
        using var client = new SidecarVoiceClient((_, _) => new SidecarLaunchResult { Success = true, Port = port })
        {
            PingIntervalMs = 10_000,
        };
        client.OnObsRecordingStatus += status =>
        {
            if (status.State == "recording") recording.TrySetResult(true);
            if (status.State == "waiting" && recording.Task.IsCompleted) disconnected.TrySetResult(true);
        };
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(serverCancellation.Token);
            var stream = connection.GetStream();
            using var hello = await ReadControl(stream, serverCancellation.Token);
            await stream.WriteAsync(SidecarProtocol.EncodeControl(
                $"{{\"op\":\"ready\",\"proto\":{SidecarVoiceClient.Proto},\"format\":{{\"rate\":48000,\"channels\":2,\"sample\":\"f32\"}}}}"), serverCancellation.Token);
            await streamingStarted.Task.WaitAsync(serverCancellation.Token);
            await stream.WriteAsync(SidecarProtocol.EncodeControl(
                "{\"op\":\"obs-recording-status\",\"state\":\"recording\",\"message\":\"Recording voices\"}"), serverCancellation.Token);
            await closeHelper.Task.WaitAsync(serverCancellation.Token);
        }, serverCancellation.Token);
        try
        {
            Assert.True(client.Start(null, null));
            streamingStarted.TrySetResult(true);
            await recording.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Same(globalStatus, VoiceObsRecording.Status);
            closeHelper.TrySetResult(true);
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Equal(CaptureHealth.Dead, client.Health);
            Assert.Same(globalStatus, VoiceObsRecording.Status);
            await server;
        }
        finally
        {
            closeHelper.TrySetResult(true);
            serverCancellation.Cancel();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested) { }
        }
    }

    private static async Task<JsonDocument> ReadControl(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[SidecarProtocol.HeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken);
        Assert.Equal(SidecarProtocol.TypeControl, header[0]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
        Assert.InRange(length, 1u, (uint)SidecarProtocol.MaxPayloadBytes);
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonDocument.Parse(payload);
    }
}
#endif
