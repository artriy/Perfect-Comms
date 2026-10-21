#if WINDOWS
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VoiceChatPlugin.VoiceChat;
using Xunit;

public sealed class ObsCompatibilityLifecycleTests
{
    [Fact]
    public async Task WindowFailureKeepsControlHealthyAndAllowsLiveDisable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        serverCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var levelReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disableReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            Assert.Equal("set-obs-compatibility", enable.RootElement.GetProperty("op").GetString());
            Assert.True(enable.RootElement.GetProperty("enabled").GetBoolean());
            await stream.WriteAsync(SidecarProtocol.EncodeControl(
                "{\"op\":\"error\",\"code\":\"obs-window\",\"msg\":\"window creation failed\"}"), serverCancellation.Token);
            await stream.WriteAsync(SidecarProtocol.EncodeControl(
                "{\"op\":\"level\",\"peak\":0.25,\"speaking\":false}"), serverCancellation.Token);
            using var disable = await ReadControl(stream, serverCancellation.Token);
            Assert.Equal("set-obs-compatibility", disable.RootElement.GetProperty("op").GetString());
            Assert.False(disable.RootElement.GetProperty("enabled").GetBoolean());
            disableReceived.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, serverCancellation.Token);
        }, serverCancellation.Token);
        try
        {
            Assert.True(client.Start(null, null));
            client.SetObsCompatibilityMode(true);
            await levelReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Equal(CaptureHealth.Healthy, client.Health);
            Assert.Equal(0, Volatile.Read(ref deaths));
            Assert.Equal(0, Volatile.Read(ref microphoneErrors));
            client.SetObsCompatibilityMode(false);
            await disableReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            Assert.Equal(CaptureHealth.Healthy, client.Health);
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
