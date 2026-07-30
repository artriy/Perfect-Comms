using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceLobbyLiveProtocol
{
    internal const int WireVersion = 1;
    internal const string DefaultRegistryUrl = "https://perfect-comms-lobbies.edgetel.workers.dev";
    internal const int MaximumMessageBytes = 128 * 1024;
    private static readonly byte[] RefreshMessage = Encoding.UTF8.GetBytes("{\"wire\":1,\"type\":\"refresh\"}");
    private static readonly byte[] HeartbeatMessage = Encoding.UTF8.GetBytes("{\"wire\":1,\"type\":\"heartbeat\"}");
    private static readonly byte[] RemoveMessage = Encoding.UTF8.GetBytes("{\"wire\":1,\"type\":\"remove\"}");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static Uri BuildWebSocketUri(string? registryUrl, string role)
    {
        var baseUrl = string.IsNullOrWhiteSpace(registryUrl) ? DefaultRegistryUrl : registryUrl.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            uri = new Uri(DefaultRegistryUrl);

        var builder = new UriBuilder(uri)
        {
            Scheme = "wss",
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Path = uri.AbsolutePath.TrimEnd('/') + "/lobbies/live",
            Query = $"role={Uri.EscapeDataString(role)}",
            Fragment = "",
        };
        return builder.Uri;
    }

    internal static ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("User-Agent", $"PerfectComms/{VoiceChatPluginMain.Version}");
        return socket;
    }

    internal static async Task ConnectAsync(
        ClientWebSocket socket,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Live directory connection timed out");
        }
    }

    internal static byte[] SerializePublish(VoiceLobbyPublishRequest request)
        => JsonSerializer.SerializeToUtf8Bytes(new VoiceLobbyLivePublishEnvelope
        {
            Wire = WireVersion,
            Type = "publish",
            Lobby = request,
        }, JsonOptions);

    internal static Task SendRefreshAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        => SendAsync(socket, RefreshMessage, cancellationToken);

    internal static Task SendHeartbeatAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        => SendAsync(socket, HeartbeatMessage, cancellationToken);

    internal static Task SendRemoveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        => SendAsync(socket, RemoveMessage, cancellationToken);

    internal static async Task SendAsync(ClientWebSocket socket, byte[] message, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;
        await socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream(8192);
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Live directory returned a non-text WebSocket message");
            if (message.Length + result.Count > MaximumMessageBytes)
                throw new InvalidDataException("Live directory message exceeded 128 KiB");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
    }
}

internal sealed class VoiceLobbyLiveEnvelope
{
    [JsonPropertyName("wire")] public int Wire { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("error")] public string Error { get; set; } = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("lobbies")] public List<VoiceLobbyListing>? Lobbies { get; set; }
    [JsonPropertyName("lobby")] public VoiceLobbyListing? Lobby { get; set; }
}

internal sealed class VoiceLobbyLivePublishEnvelope
{
    [JsonPropertyName("wire")] public int Wire { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("lobby")] public VoiceLobbyPublishRequest? Lobby { get; set; }
}

internal static class VoiceLobbyLiveBrowserClient
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim SendGate = new(1, 1);
    private static readonly Dictionary<string, VoiceLobbyListing> Listings = new(StringComparer.Ordinal);
    private static ClientWebSocket? _socket;
    private static CancellationTokenSource? _lifetime;
    private static string _endpoint = "";
    private static string _status = "Disconnected";
    private static bool _dirty = true;
    private static int _generation;

    internal static void EnsureConnected(string registryUrl)
    {
        var endpoint = VoiceLobbyLiveProtocol.BuildWebSocketUri(registryUrl, "browser").AbsoluteUri;
        int generation;
        CancellationToken token;
        lock (Gate)
        {
            if (_lifetime != null && string.Equals(_endpoint, endpoint, StringComparison.Ordinal)) return;
            StopLocked();
            _endpoint = endpoint;
            _lifetime = new CancellationTokenSource();
            generation = ++_generation;
            token = _lifetime.Token;
            _status = "Connecting to Perfect Comms live lobbies...";
            _dirty = true;
        }

        _ = RunAsync(new Uri(endpoint), generation, token);
    }

    internal static void RequestSnapshot()
    {
        ClientWebSocket? socket;
        int generation;
        lock (Gate)
        {
            socket = _socket;
            generation = _generation;
        }
        if (socket?.State == WebSocketState.Open)
            _ = SendRefreshAsync(socket, generation);
    }

    internal static bool TryConsumeSnapshot(out IReadOnlyList<VoiceLobbyListing> listings, out string status)
    {
        lock (Gate)
        {
            listings = Listings.Values
                .OrderByDescending(listing => string.Equals(listing.State, "Lobby", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(listing => listing.UpdatedAt)
                .ToArray();
            status = _status;
            var dirty = _dirty;
            _dirty = false;
            return dirty;
        }
    }

    internal static void Disconnect()
    {
        ClientWebSocket? socket;
        CancellationTokenSource? lifetime;
        lock (Gate)
        {
            socket = _socket;
            lifetime = _lifetime;
            StopLocked();
            _endpoint = "";
            Listings.Clear();
            _status = "Disconnected";
            _dirty = true;
        }

        try { lifetime?.Cancel(); } catch { }
        AbortAndDispose(socket);
        try { lifetime?.Dispose(); } catch { }
    }

    private static void StopLocked()
    {
        try { _lifetime?.Cancel(); } catch { }
        AbortAndDispose(_socket);
        try { _lifetime?.Dispose(); } catch { }
        _socket = null;
        _lifetime = null;
        Listings.Clear();
    }

    private static async Task RunAsync(Uri endpoint, int generation, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                SetConnectingStatus(generation, attempt);
                socket = VoiceLobbyLiveProtocol.CreateSocket();
                await VoiceLobbyLiveProtocol.ConnectAsync(socket, endpoint, cancellationToken).ConfigureAwait(false);
                if (!TrySetSocket(generation, socket)) return;

                attempt = 0;
                SetStatus(generation, "Perfect Comms live directory connected");
                while (!cancellationToken.IsCancellationRequested)
                {
                    var text = await VoiceLobbyLiveProtocol.ReceiveTextAsync(socket, cancellationToken)
                        .ConfigureAwait(false);
                    if (text == null) break;
                    ApplyMessage(generation, text);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetFailure(generation, ex.Message);
            }
            finally
            {
                ClearSocket(generation, socket);
                AbortAndDispose(socket);
            }

            if (cancellationToken.IsCancellationRequested) break;
            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt - 1, 3)));
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task SendRefreshAsync(ClientWebSocket socket, int generation)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try { await VoiceLobbyLiveProtocol.SendRefreshAsync(socket, timeout.Token).ConfigureAwait(false); }
            finally { SendGate.Release(); }
        }
        catch (Exception ex)
        {
            SetFailure(generation, ex.Message);
        }
    }

    private static void ApplyMessage(int generation, string text)
    {
        VoiceLobbyLiveEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<VoiceLobbyLiveEnvelope>(text, VoiceLobbyLiveProtocol.JsonOptions);
        }
        catch (Exception ex)
        {
            SetFailure(generation, "Invalid live-directory message: " + ex.Message);
            return;
        }
        if (envelope == null || envelope.Wire != VoiceLobbyLiveProtocol.WireVersion)
        {
            SetFailure(generation, "Live-directory protocol mismatch");
            return;
        }

        lock (Gate)
        {
            if (generation != _generation || _lifetime == null) return;
            switch (envelope.Type)
            {
                case "snapshot":
                    Listings.Clear();
                    foreach (var listing in envelope.Lobbies ?? new List<VoiceLobbyListing>())
                        if (!string.IsNullOrWhiteSpace(listing.Id)) Listings[listing.Id] = listing;
                    _status = $"Perfect Comms live connected: {Listings.Count} lobby/lobbies";
                    _dirty = true;
                    break;
                case "upsert" when envelope.Lobby != null && !string.IsNullOrWhiteSpace(envelope.Lobby.Id):
                    Listings[envelope.Lobby.Id] = envelope.Lobby;
                    _status = $"Perfect Comms live connected: {Listings.Count} lobby/lobbies";
                    _dirty = true;
                    break;
                case "remove" when !string.IsNullOrWhiteSpace(envelope.Id):
                    Listings.Remove(envelope.Id);
                    _status = $"Perfect Comms live connected: {Listings.Count} lobby/lobbies";
                    _dirty = true;
                    break;
                case "error":
                    _status = "Live directory error: " + FriendlyError(envelope.Error);
                    _dirty = true;
                    break;
            }
        }
    }

    private static bool TrySetSocket(int generation, ClientWebSocket socket)
    {
        lock (Gate)
        {
            if (generation != _generation || _lifetime == null) return false;
            _socket = socket;
            return true;
        }
    }

    private static void ClearSocket(int generation, ClientWebSocket? socket)
    {
        lock (Gate)
        {
            if (generation != _generation || !ReferenceEquals(_socket, socket)) return;
            _socket = null;
        }
    }

    private static void SetConnectingStatus(int generation, int attempt)
        => SetStatus(generation, attempt == 0
            ? "Connecting to Perfect Comms live lobbies..."
            : $"Reconnecting to Perfect Comms live lobbies (attempt {attempt + 1})...");

    private static void SetStatus(int generation, string status)
    {
        lock (Gate)
        {
            if (generation != _generation || _lifetime == null) return;
            _status = status;
            _dirty = true;
        }
    }

    private static void SetFailure(int generation, string error)
    {
        lock (Gate)
        {
            if (generation != _generation || _lifetime == null) return;
            Listings.Clear();
            _status = "Live directory unavailable: " + error;
            _dirty = true;
        }
        VoiceDiagnostics.DebugWarning("[VC] Live lobby browser failed: " + error);
    }

    private static string FriendlyError(string error)
        => error switch
        {
            "directory_capacity" => "directory is at capacity",
            "rate_limited" => "too many requests",
            "wire_version_mismatch" => "client/server protocol mismatch",
            _ => string.IsNullOrWhiteSpace(error) ? "unknown server error" : error.Replace('_', ' '),
        };

    private static void AbortAndDispose(ClientWebSocket? socket)
    {
        if (socket == null) return;
        try { socket.Abort(); } catch { }
        try { socket.Dispose(); } catch { }
    }
}

internal static class VoiceLobbyLivePublisher
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim WakeSignal = new(0, 1);
    private static readonly SemaphoreSlim SendGate = new(1, 1);
    private static ClientWebSocket? _socket;
    private static CancellationTokenSource? _lifetime;
    private static string _endpoint = "";
    private static byte[]? _latestPublish;
    private static string? _latestSignature;
    private static bool _publishDirty;
    private static int _generation;

    internal static void Update(string registryUrl, VoiceLobbyPublishRequest request, string signature)
    {
        var endpoint = VoiceLobbyLiveProtocol.BuildWebSocketUri(registryUrl, "host").AbsoluteUri;
        ClientWebSocket? oldSocket = null;
        CancellationTokenSource? oldLifetime = null;
        int generation = 0;
        CancellationToken token = default;
        var start = false;

        lock (Gate)
        {
            if (_lifetime == null || !string.Equals(_endpoint, endpoint, StringComparison.Ordinal))
            {
                oldSocket = _socket;
                oldLifetime = _lifetime;
                _socket = null;
                _lifetime = new CancellationTokenSource();
                _endpoint = endpoint;
                _latestSignature = null;
                _publishDirty = true;
                generation = ++_generation;
                token = _lifetime.Token;
                start = true;
            }

            if (!string.Equals(_latestSignature, signature, StringComparison.Ordinal))
            {
                _latestPublish = VoiceLobbyLiveProtocol.SerializePublish(request);
                _latestSignature = signature;
                _publishDirty = true;
            }
        }

        if (oldLifetime != null || oldSocket != null)
            StopConnection(oldSocket, oldLifetime);
        Wake();
        if (start)
            _ = RunAsync(new Uri(endpoint), generation, token);
    }

    internal static void Clear()
    {
        ClientWebSocket? socket;
        CancellationTokenSource? lifetime;
        lock (Gate)
        {
            socket = _socket;
            lifetime = _lifetime;
            _socket = null;
            _lifetime = null;
            _endpoint = "";
            _latestPublish = null;
            _latestSignature = null;
            _publishDirty = false;
            ++_generation;
        }
        StopConnection(socket, lifetime);
    }

    private static async Task RunAsync(Uri endpoint, int generation, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                socket = VoiceLobbyLiveProtocol.CreateSocket();
                await VoiceLobbyLiveProtocol.ConnectAsync(socket, endpoint, cancellationToken).ConfigureAwait(false);
                if (!TrySetSocket(generation, socket)) return;
                attempt = 0;
                MarkDirty(generation);

                var receiveTask = ReceiveControlMessagesAsync(socket, generation, cancellationToken);
                while (!cancellationToken.IsCancellationRequested && !receiveTask.IsCompleted)
                {
                    using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var wakeTask = WakeSignal.WaitAsync(cycleCancellation.Token);
                    var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(30), cycleCancellation.Token);
                    var completed = await Task.WhenAny(receiveTask, wakeTask, heartbeatTask).ConfigureAwait(false);
                    cycleCancellation.Cancel();
                    if (completed == receiveTask) break;

                    if (completed == wakeTask)
                    {
                        await wakeTask.ConfigureAwait(false);
                        var publish = TakeDirtyPublish(generation);
                        if (publish != null)
                            await SendAsync(socket, publish, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendHeartbeatAsync(socket, cancellationToken).ConfigureAwait(false);
                    }
                }
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.DebugWarning("[VC] Live lobby publisher failed: " + ex.Message);
            }
            finally
            {
                ClearSocket(generation, socket);
                AbortAndDispose(socket);
            }

            if (cancellationToken.IsCancellationRequested) break;
            attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt - 1, 3)));
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task ReceiveControlMessagesAsync(
        ClientWebSocket socket,
        int generation,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var text = await VoiceLobbyLiveProtocol.ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            if (text == null) return;
            VoiceLobbyLiveEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<VoiceLobbyLiveEnvelope>(text, VoiceLobbyLiveProtocol.JsonOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Invalid live-directory control message", ex);
            }
            if (envelope == null || envelope.Wire != VoiceLobbyLiveProtocol.WireVersion)
                throw new InvalidDataException("Live-directory protocol mismatch");
            if (string.Equals(envelope.Type, "ready", StringComparison.Ordinal))
            {
                MarkDirty(generation);
                Wake();
            }
            else if (string.Equals(envelope.Type, "error", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Live directory rejected listing: " + envelope.Error);
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, byte[] message, CancellationToken cancellationToken)
    {
        await SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await VoiceLobbyLiveProtocol.SendAsync(socket, message, cancellationToken).ConfigureAwait(false); }
        finally { SendGate.Release(); }
    }

    private static async Task SendHeartbeatAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        await SendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await VoiceLobbyLiveProtocol.SendHeartbeatAsync(socket, cancellationToken).ConfigureAwait(false); }
        finally { SendGate.Release(); }
    }

    private static byte[]? TakeDirtyPublish(int generation)
    {
        lock (Gate)
        {
            if (generation != _generation || _lifetime == null || !_publishDirty) return null;
            _publishDirty = false;
            return _latestPublish;
        }
    }

    private static void MarkDirty(int generation)
    {
        lock (Gate)
        {
            if (generation != _generation || _lifetime == null) return;
            _publishDirty = true;
        }
    }

    private static bool TrySetSocket(int generation, ClientWebSocket socket)
    {
        lock (Gate)
        {
            if (generation != _generation || _lifetime == null) return false;
            _socket = socket;
            return true;
        }
    }

    private static void ClearSocket(int generation, ClientWebSocket? socket)
    {
        lock (Gate)
        {
            if (generation != _generation || !ReferenceEquals(_socket, socket)) return;
            _socket = null;
        }
    }

    private static void StopConnection(ClientWebSocket? socket, CancellationTokenSource? lifetime)
    {
        try { lifetime?.Cancel(); } catch { }
        if (socket != null) _ = RemoveAndCloseAsync(socket);
        try { lifetime?.Dispose(); } catch { }
    }

    private static async Task RemoveAndCloseAsync(ClientWebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await VoiceLobbyLiveProtocol.SendRemoveAsync(socket, timeout.Token).ConfigureAwait(false);
                if (socket.State == WebSocketState.Open)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "listing closed", timeout.Token)
                        .ConfigureAwait(false);
            }
            finally { SendGate.Release(); }
        }
        catch { }
        finally { AbortAndDispose(socket); }
    }

    private static void Wake()
    {
        try { WakeSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private static void AbortAndDispose(ClientWebSocket? socket)
    {
        if (socket == null) return;
        try { socket.Abort(); } catch { }
        try { socket.Dispose(); } catch { }
    }
}
