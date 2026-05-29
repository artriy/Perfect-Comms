using System;
using System.Threading.Tasks;
using InnerNet;

namespace VoiceChatPlugin.VoiceChat;

internal static class BetterCrewLinkLobbyPublisher
{
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(5);
    private static SocketIOClient.SocketIO? _socket;
    private static string _serverUrl = "";
    private static bool _connected;
    private static string? _joinedCode;
    private static int _joinedClientId = -1;
    private static string? _lastStandaloneSignature;
    private static string? _lastBackendSignature;
    private static string? _backendPublishedCode;
    private static int _lastBackendJoinEpoch = -1;
    private static DateTime _nextStandaloneRetryUtc = DateTime.MinValue;
    private static bool _publishDirty = true;
    private static Task? _pending;

    internal static void Update(string serverUrl, VoiceLobbyPublishRequest request)
    {
        var signature = BetterCrewLinkLobbyMetadata.BuildSignature(request);
        var room = VoiceChatRoom.Current;
        if (room?.IsBetterCrewLinkBackendActive == true)
        {
            ClearStandalone();
            PublishThroughBackendSocket(room, request, signature);
            return;
        }

        _backendPublishedCode = null;
        _lastBackendSignature = null;
        _lastBackendJoinEpoch = -1;
        if (_pending is { IsCompleted: false }) return;

        EnsureStandaloneSocket(serverUrl);
        if (_socket?.Connected != true || !_connected)
            return;

        var signatureChanged = !string.Equals(_lastStandaloneSignature, signature, StringComparison.Ordinal);
        var codeChanged = !string.Equals(_joinedCode, request.Code, StringComparison.Ordinal);
        if (signatureChanged || codeChanged)
            _publishDirty = true;

        if (!_publishDirty)
            return;

        if (DateTime.UtcNow < _nextStandaloneRetryUtc)
            return;

        _pending = PublishStandaloneAsync(request, signature);
    }

    internal static void Clear()
    {
        if (!string.IsNullOrEmpty(_backendPublishedCode))
        {
            VoiceChatRoom.Current?.TryRemoveBetterCrewLinkLobby(_backendPublishedCode);
            _backendPublishedCode = null;
            _lastBackendSignature = null;
            _lastBackendJoinEpoch = -1;
        }

        ClearStandalone();
    }

    private static void PublishThroughBackendSocket(VoiceChatRoom room, VoiceLobbyPublishRequest request, string signature)
    {
        var joinEpoch = room.BetterCrewLinkPublicLobbyJoinEpoch;
        var signatureChanged = !string.Equals(_lastBackendSignature, signature, StringComparison.Ordinal);
        var codeChanged = !string.Equals(_backendPublishedCode, request.Code, StringComparison.Ordinal);
        var rejoined = _lastBackendJoinEpoch != joinEpoch;

        if (!string.IsNullOrEmpty(_backendPublishedCode) && codeChanged)
        {
            room.TryRemoveBetterCrewLinkLobby(_backendPublishedCode);
            _backendPublishedCode = null;
        }

        if (!signatureChanged && !codeChanged && !rejoined)
            return;

        if (room.TryPublishBetterCrewLinkLobby(request))
        {
            _backendPublishedCode = request.Code;
            _lastBackendSignature = signature;
            _lastBackendJoinEpoch = joinEpoch;
        }
    }

    private static void EnsureStandaloneSocket(string serverUrl)
    {
        serverUrl = VoiceEndpointSettings.NormalizeBetterCrewLinkServerUrl(serverUrl);
        if (_socket != null && string.Equals(_serverUrl, serverUrl, StringComparison.Ordinal))
            return;

        ClearStandalone();
        var socket = new SocketIOClient.SocketIO(new Uri(serverUrl), BetterCrewLinkSocketOptions.Create());

        socket.OnConnected += async (_, _) =>
        {
            _connected = true;
            _publishDirty = true;
            _nextStandaloneRetryUtc = DateTime.MinValue;
            await Task.CompletedTask;
        };
        socket.OnDisconnected += (_, _) =>
        {
            _connected = false;
            _joinedCode = null;
            _joinedClientId = -1;
            _lastStandaloneSignature = null;
            _publishDirty = true;
            _nextStandaloneRetryUtc = DateTime.MinValue;
        };
        socket.On("clientPeerConfig", _ => { });

        _socket = socket;
        _serverUrl = serverUrl;
        _connected = false;
        _joinedCode = null;
        _joinedClientId = -1;
        _lastStandaloneSignature = null;
        _nextStandaloneRetryUtc = DateTime.MinValue;
        _publishDirty = true;
        _ = socket.ConnectAsync();
    }

    private static async Task PublishStandaloneAsync(VoiceLobbyPublishRequest request, string signature)
    {
        var socket = _socket;
        if (socket == null)
        {
            _publishDirty = true;
            _nextStandaloneRetryUtc = DateTime.UtcNow.Add(FailureRetryDelay);
            return;
        }

        try
        {
            var playerId = ResolveLocalPlayerId();
            var clientId = AmongUsClient.Instance?.ClientId ?? -1;
            if (clientId < 0)
            {
                _publishDirty = true;
                _nextStandaloneRetryUtc = DateTime.UtcNow.Add(FailureRetryDelay);
                return;
            }

            if (!string.IsNullOrEmpty(_joinedCode)
                && !string.Equals(_joinedCode, request.Code, StringComparison.Ordinal))
            {
                try { await socket.EmitAsync("remove_lobby", _joinedCode).ConfigureAwait(false); } catch { }
                _joinedCode = null;
                _joinedClientId = -1;
            }

            if (!string.Equals(_joinedCode, request.Code, StringComparison.Ordinal) || _joinedClientId != clientId)
            {
                await socket.EmitAsync("id", new object[] { playerId, clientId }).ConfigureAwait(false);
                await socket.EmitAsync("join", new object[] { request.Code, playerId, clientId, true }).ConfigureAwait(false);
                _joinedCode = request.Code;
                _joinedClientId = clientId;
            }

            await socket.EmitAsync("lobby", new object[] { request.Code, BetterCrewLinkLobbyMetadata.ToBclLobby(request) }).ConfigureAwait(false);
            _lastStandaloneSignature = signature;
            _publishDirty = false;
            _nextStandaloneRetryUtc = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning($"[VC] BCL lobby publish failed: {ex.Message}");
            _publishDirty = true;
            _nextStandaloneRetryUtc = DateTime.UtcNow.Add(FailureRetryDelay);
        }
    }

    private static void ClearStandalone()
    {
        var socket = _socket;
        var code = _joinedCode;
        _socket = null;
        _connected = false;
        _joinedCode = null;
        _joinedClientId = -1;
        _lastStandaloneSignature = null;
        _nextStandaloneRetryUtc = DateTime.MinValue;
        _publishDirty = true;

        if (socket == null) return;
        _ = ClearStandaloneAsync(socket, code);
    }

    private static async Task ClearStandaloneAsync(SocketIOClient.SocketIO socket, string? code)
    {
        try
        {
            if (!string.IsNullOrEmpty(code))
                await socket.EmitAsync("remove_lobby", code).ConfigureAwait(false);
        }
        catch { }

        try { await socket.EmitAsync("leave").ConfigureAwait(false); } catch { }
        try { await socket.DisconnectAsync().ConfigureAwait(false); } catch { }
        // Dispose releases the websocket, timers, and the unbounded reconnect loop's CTS.
        try { socket.Dispose(); } catch { }
    }

    private static int ResolveLocalPlayerId()
    {
        try { return PlayerControl.LocalPlayer?.PlayerId ?? 0; }
        catch { return 0; }
    }
}
