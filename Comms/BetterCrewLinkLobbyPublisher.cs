using System;
using System.Threading.Tasks;
using InnerNet;

namespace VoiceChatPlugin.VoiceChat;

internal static class BetterCrewLinkLobbyPublisher
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();
    private static SocketIOClient.SocketIO? _socket;
    private static int _socketGeneration;
    private static string _serverUrl = "";
    private static bool _connected;
    private static string? _joinedCode;
    private static int _joinedClientId = -1;
    private static string? _lastStandaloneSignature;
    private static string? _pendingCode;
    private static DateTime _nextStandaloneRetryUtc = DateTime.MinValue;
    private static bool _publishDirty = true;
    private static Task? _pending;

    internal static void Update(string serverUrl, VoiceLobbyPublishRequest request)
    {
        var signature = BetterCrewLinkLobbyMetadata.BuildSignature(request);
        EnsureStandaloneSocket(serverUrl);
        lock (Gate)
        {
            if (_pending is { IsCompleted: false }) return;
            _pending = null;

            var socket = _socket;
            if (socket?.Connected != true || !_connected)
                return;

            var signatureChanged = !string.Equals(_lastStandaloneSignature, signature, StringComparison.Ordinal);
            var codeChanged = !string.Equals(_joinedCode, request.Code, StringComparison.Ordinal);
            if (signatureChanged || codeChanged)
                _publishDirty = true;

            if (!_publishDirty || DateTime.UtcNow < _nextStandaloneRetryUtc)
                return;

            var generation = _socketGeneration;
            _pendingCode = request.Code;
            _pending = PublishStandaloneAsync(socket, generation, request, signature);
        }
    }

    internal static void Clear()
    {
        ClearStandalone();
    }

    private static void EnsureStandaloneSocket(string serverUrl)
    {
        serverUrl = BetterCrewLinkLobbyEndpoint.NormalizeServerUrl(serverUrl);
        lock (Gate)
            if (_socket != null && string.Equals(_serverUrl, serverUrl, StringComparison.Ordinal))
                return;

        ClearStandalone();
        var socket = new SocketIOClient.SocketIO(new Uri(serverUrl), BetterCrewLinkSocketOptions.Create());
        int generation;

        lock (Gate)
        {
            generation = ++_socketGeneration;
            socket.OnConnected += async (_, _) =>
            {
                lock (Gate)
                {
                    if (!MatchesSocketGeneration(_socket, _socketGeneration, socket, generation)) return;
                    _connected = true;
                    _publishDirty = true;
                    _nextStandaloneRetryUtc = DateTime.MinValue;
                }
                await Task.CompletedTask;
            };
            socket.OnDisconnected += (_, _) =>
            {
                lock (Gate)
                {
                    if (!MatchesSocketGeneration(_socket, _socketGeneration, socket, generation)) return;
                    _connected = false;
                    _joinedCode = null;
                    _joinedClientId = -1;
                    _lastStandaloneSignature = null;
                    _publishDirty = true;
                    _nextStandaloneRetryUtc = DateTime.MinValue;
                }
            };
            socket.On("clientPeerConfig", _ => Task.CompletedTask);

            _socket = socket;
            _serverUrl = serverUrl;
            _connected = false;
            _joinedCode = null;
            _joinedClientId = -1;
            _lastStandaloneSignature = null;
            _pendingCode = null;
            _publishDirty = true;
            _nextStandaloneRetryUtc = DateTime.MinValue;
        }
        _ = socket.ConnectAsync();
    }

    private static async Task PublishStandaloneAsync(
        SocketIOClient.SocketIO socket,
        int generation,
        VoiceLobbyPublishRequest request,
        string signature)
    {
        try
        {
            if (!IsCurrentSocket(socket, generation)) return;
            var playerId = ResolveLocalPlayerId();
            var clientId = AmongUsClient.Instance?.ClientId ?? -1;
            if (clientId < 0)
            {
                MarkPublishFailed(socket, generation);
                return;
            }

            string? joinedCode;
            int joinedClientId;
            if (!TryReadJoinState(socket, generation, out joinedCode, out joinedClientId)) return;
            if (!string.IsNullOrEmpty(joinedCode)
                && !string.Equals(joinedCode, request.Code, StringComparison.Ordinal))
            {
                try { await socket.EmitAsync("remove_lobby", new object[] { joinedCode }).ConfigureAwait(false); } catch { }
                if (!TryClearJoinedCode(socket, generation, joinedCode)) return;
                joinedCode = null;
                joinedClientId = -1;
            }

            if (!string.Equals(joinedCode, request.Code, StringComparison.Ordinal) || joinedClientId != clientId)
            {
                if (!IsCurrentSocket(socket, generation)) return;
                await socket.EmitAsync("id", new object[] { playerId, clientId }).ConfigureAwait(false);
                if (!IsCurrentSocket(socket, generation)) return;
                await socket.EmitAsync("join", new object[] { request.Code, playerId, clientId, true }).ConfigureAwait(false);
                if (!TryMarkJoined(socket, generation, request.Code, clientId)) return;
            }

            if (!IsCurrentSocket(socket, generation)) return;
            await socket.EmitAsync("lobby", new object[] { request.Code, BetterCrewLinkLobbyMetadata.ToBclLobby(request) }).ConfigureAwait(false);
            TryMarkPublishComplete(socket, generation, signature);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning($"[VC] BCL lobby publish failed: {ex.Message}");
            MarkPublishFailed(socket, generation);
        }
    }

    private static void ClearStandalone()
    {
        SocketIOClient.SocketIO? socket;
        string? joinedCode;
        string? pendingCode;
        Task? pending;
        lock (Gate)
        {
            ++_socketGeneration;
            socket = _socket;
            joinedCode = _joinedCode;
            pendingCode = _pendingCode;
            pending = _pending;
            _socket = null;
            _serverUrl = "";
            _connected = false;
            _joinedCode = null;
            _joinedClientId = -1;
            _lastStandaloneSignature = null;
            _pendingCode = null;
            _pending = null;
            _nextStandaloneRetryUtc = DateTime.MinValue;
            _publishDirty = true;
        }

        if (socket == null) return;
        _ = ClearStandaloneAsync(socket, joinedCode, pendingCode, pending);
    }

    private static Task ClearStandaloneAsync(
        SocketIOClient.SocketIO socket,
        string? joinedCode,
        string? pendingCode,
        Task? pending)
        => RunAfterPendingAsync(pending, async () =>
        {
            try
            {
                if (!string.IsNullOrEmpty(joinedCode))
                    await socket.EmitAsync("remove_lobby", new object[] { joinedCode }).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(pendingCode)
                    && !string.Equals(pendingCode, joinedCode, StringComparison.Ordinal))
                    await socket.EmitAsync("remove_lobby", new object[] { pendingCode }).ConfigureAwait(false);
            }
            catch { }

            try { await socket.EmitAsync("leave").ConfigureAwait(false); } catch { }
            try { await socket.DisconnectAsync().ConfigureAwait(false); } catch { }
            // Dispose tears down the websocket and the unbounded reconnect loop's CTS.
            try { socket.Dispose(); } catch { }
        });

    internal static async Task RunAfterPendingAsync(Task? pending, Func<Task> completion)
    {
        if (pending != null)
        {
            try { await pending.ConfigureAwait(false); }
            catch { /* cleanup must still run after a failed publish */ }
        }
        await completion().ConfigureAwait(false);
    }

    internal static bool MatchesSocketGeneration(
        object? currentSocket,
        int currentGeneration,
        object candidateSocket,
        int candidateGeneration)
        => ReferenceEquals(currentSocket, candidateSocket) && currentGeneration == candidateGeneration;

    private static bool IsCurrentSocket(SocketIOClient.SocketIO socket, int generation)
    {
        lock (Gate)
            return MatchesSocketGeneration(_socket, _socketGeneration, socket, generation);
    }

    private static bool TryReadJoinState(
        SocketIOClient.SocketIO socket,
        int generation,
        out string? joinedCode,
        out int joinedClientId)
    {
        lock (Gate)
        {
            if (!MatchesSocketGeneration(_socket, _socketGeneration, socket, generation))
            {
                joinedCode = null;
                joinedClientId = -1;
                return false;
            }
            joinedCode = _joinedCode;
            joinedClientId = _joinedClientId;
            return true;
        }
    }

    private static bool TryClearJoinedCode(
        SocketIOClient.SocketIO socket,
        int generation,
        string joinedCode)
    {
        lock (Gate)
        {
            if (!MatchesSocketGeneration(_socket, _socketGeneration, socket, generation)) return false;
            if (string.Equals(_joinedCode, joinedCode, StringComparison.Ordinal))
            {
                _joinedCode = null;
                _joinedClientId = -1;
            }
            return true;
        }
    }

    private static bool TryMarkJoined(
        SocketIOClient.SocketIO socket,
        int generation,
        string code,
        int clientId)
    {
        lock (Gate)
        {
            if (!MatchesSocketGeneration(_socket, _socketGeneration, socket, generation)) return false;
            _joinedCode = code;
            _joinedClientId = clientId;
            return true;
        }
    }

    private static void TryMarkPublishComplete(
        SocketIOClient.SocketIO socket,
        int generation,
        string signature)
    {
        lock (Gate)
        {
            if (!MatchesSocketGeneration(_socket, _socketGeneration, socket, generation)) return;
            _lastStandaloneSignature = signature;
            _pendingCode = null;
            _publishDirty = false;
            _nextStandaloneRetryUtc = DateTime.MinValue;
        }
    }

    private static void MarkPublishFailed(SocketIOClient.SocketIO socket, int generation)
    {
        lock (Gate)
        {
            if (!MatchesSocketGeneration(_socket, _socketGeneration, socket, generation)) return;
            _pendingCode = null;
            _publishDirty = true;
            _nextStandaloneRetryUtc = DateTime.UtcNow.Add(FailureRetryDelay);
        }
    }

    private static int ResolveLocalPlayerId()
    {
        try { return PlayerControl.LocalPlayer?.PlayerId ?? 0; }
        catch { return 0; }
    }
}
