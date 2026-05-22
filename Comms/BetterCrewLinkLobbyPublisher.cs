using System;
using System.Threading.Tasks;
using InnerNet;

namespace VoiceChatPlugin.VoiceChat;

internal static class BetterCrewLinkLobbyPublisher
{
    private const int RepublishIntervalSeconds = 15;
    private static SocketIOClient.SocketIO? _socket;
    private static string _serverUrl = "";
    private static bool _connected;
    private static string? _joinedCode;
    private static int _joinedClientId = -1;
    private static string? _lastStandaloneSignature;
    private static string? _lastBackendSignature;
    private static string? _backendPublishedCode;
    private static DateTime _nextStandalonePublishUtc = DateTime.MinValue;
    private static DateTime _nextBackendPublishUtc = DateTime.MinValue;
    private static Task? _pending;

    internal static void Update(string serverUrl, VoiceLobbyPublishRequest request)
    {
        var signature = BetterCrewLinkLobbyMetadata.BuildSignature(request);
        var room = VoiceChatRoom.Current;
        if (room?.IsBetterCrewLinkBackendActive == true)
        {
            ClearStandalone();
            if (!string.IsNullOrEmpty(_backendPublishedCode)
                && !string.Equals(_backendPublishedCode, request.Code, StringComparison.Ordinal))
                room.TryRemoveBetterCrewLinkLobby(_backendPublishedCode);

            var now = DateTime.UtcNow;
            if (string.Equals(_lastBackendSignature, signature, StringComparison.Ordinal)
                && now < _nextBackendPublishUtc)
                return;

            if (room.TryPublishBetterCrewLinkLobby(request))
            {
                _backendPublishedCode = request.Code;
                _lastBackendSignature = signature;
                _nextBackendPublishUtc = now.AddSeconds(RepublishIntervalSeconds);
            }
            return;
        }

        _backendPublishedCode = null;
        _lastBackendSignature = null;
        if (_pending is { IsCompleted: false }) return;

        EnsureStandaloneSocket(serverUrl);
        if (_socket?.Connected != true || !_connected)
            return;

        if (string.Equals(_lastStandaloneSignature, signature, StringComparison.Ordinal)
            && DateTime.UtcNow < _nextStandalonePublishUtc)
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
            _nextBackendPublishUtc = DateTime.MinValue;
        }

        ClearStandalone();
    }

    private static void EnsureStandaloneSocket(string serverUrl)
    {
        serverUrl = VoiceEndpointSettings.NormalizeBetterCrewLinkServerUrl(serverUrl);
        if (_socket != null && string.Equals(_serverUrl, serverUrl, StringComparison.Ordinal))
            return;

        ClearStandalone();
        var socket = new SocketIOClient.SocketIO(new Uri(serverUrl), new SocketIOClient.SocketIOOptions
        {
            Reconnection = true,
            ReconnectionAttempts = int.MaxValue,
            EIO = SocketIO.Core.EngineIO.V3,
        });

        socket.OnConnected += async (_, _) =>
        {
            _connected = true;
            await Task.CompletedTask;
        };
        socket.OnDisconnected += (_, _) =>
        {
            _connected = false;
            _joinedCode = null;
            _joinedClientId = -1;
            _lastStandaloneSignature = null;
            _nextStandalonePublishUtc = DateTime.MinValue;
        };
        socket.On("clientPeerConfig", _ => { });

        _socket = socket;
        _serverUrl = serverUrl;
        _connected = false;
        _joinedCode = null;
        _joinedClientId = -1;
        _lastStandaloneSignature = null;
        _nextStandalonePublishUtc = DateTime.MinValue;
        _ = socket.ConnectAsync();
    }

    private static async Task PublishStandaloneAsync(VoiceLobbyPublishRequest request, string signature)
    {
        var socket = _socket;
        if (socket == null) return;

        try
        {
            var playerId = ResolveLocalPlayerId();
            var clientId = AmongUsClient.Instance?.ClientId ?? -1;
            if (clientId < 0) return;

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
            _nextStandalonePublishUtc = DateTime.UtcNow.AddSeconds(RepublishIntervalSeconds);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugWarning($"[VC] BCL lobby publish failed: {ex.Message}");
            _lastStandaloneSignature = null;
            _nextStandalonePublishUtc = DateTime.MinValue;
        }
    }

    private static void ClearStandalone()
    {
        var socket = _socket;
        var code = _joinedCode;
        _socket = null;
        _serverUrl = "";
        _connected = false;
        _joinedCode = null;
        _joinedClientId = -1;
        _lastStandaloneSignature = null;
        _nextStandalonePublishUtc = DateTime.MinValue;
        _pending = null;
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
    }

    private static int ResolveLocalPlayerId()
    {
        try { return PlayerControl.LocalPlayer?.PlayerId ?? 0; }
        catch { return 0; }
    }
}
