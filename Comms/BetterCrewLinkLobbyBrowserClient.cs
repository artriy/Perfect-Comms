using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct BetterCrewLinkLobbyJoinResult(bool Success, string Code, string Server, string Error)
{
    internal static BetterCrewLinkLobbyJoinResult Fail(string error) => new(false, "", "", error);
}

internal static class BetterCrewLinkLobbyBrowserClient
{
    private static readonly object Gate = new();
    private static SocketIOClient.SocketIO? _socket;
    private static string _serverUrl = "";
    private static readonly Dictionary<int, VoiceLobbyListing> Listings = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool _dirty;
    private static bool _connected;
    private static string _status = "Disconnected";

    internal static void EnsureConnected(string serverUrl)
    {
        serverUrl = VoiceEndpointSettings.NormalizeBetterCrewLinkServerUrl(serverUrl);
        lock (Gate)
        {
            if (_socket != null && string.Equals(_serverUrl, serverUrl, StringComparison.Ordinal))
                return;
        }

        Disconnect();
        var socket = new SocketIOClient.SocketIO(new Uri(serverUrl), BetterCrewLinkSocketOptions.Create());

        socket.OnConnected += async (_, _) =>
        {
            lock (Gate)
            {
                _connected = true;
                _status = "BCL live connected";
                _dirty = true;
            }

            await socket.EmitAsync("lobbybrowser", true).ConfigureAwait(false);
        };

        socket.OnDisconnected += (_, _) =>
        {
            lock (Gate)
            {
                _connected = false;
                _status = "BCL live disconnected";
                Listings.Clear();
                _dirty = true;
            }
        };

        socket.On("new_lobbies", response =>
        {
            try
            {
                var lobbies = ReadLobbyArray(response);
                lock (Gate)
                {
                    Listings.Clear();
                    var accepted = 0;
                    foreach (var lobby in lobbies)
                    {
                        if (AddOrUpdateLocked(lobby))
                            accepted++;
                    }

                    _status = _connected
                        ? $"BCL live connected: {accepted} PerfectComms / {lobbies.Length} public"
                        : _status;
                    _dirty = true;
                }
            }
            catch (Exception ex)
            {
                MarkParseFailed(ex);
            }
        });

        socket.On("update_lobby", response =>
        {
            try
            {
                var lobby = ReadLobby(response);
                lock (Gate)
                {
                    if (lobby != null && BetterCrewLinkLobbyMetadata.IsPerfectComms(lobby))
                        Listings[lobby.id] = BetterCrewLinkLobbyMetadata.ToListing(lobby);
                    else if (lobby != null)
                        Listings.Remove(lobby.id);

                    _dirty = true;
                }
            }
            catch (Exception ex)
            {
                MarkParseFailed(ex);
            }
        });

        socket.On("remove_lobby", response =>
        {
            var id = response.GetValue<int>(0);
            lock (Gate)
            {
                Listings.Remove(id);
                _dirty = true;
            }
        });

        lock (Gate)
        {
            _socket = socket;
            _serverUrl = serverUrl;
            _connected = false;
            _status = "Connecting to BCL live lobbies...";
            Listings.Clear();
            _dirty = true;
        }

        _ = socket.ConnectAsync();
    }

    internal static void RequestSnapshot()
    {
        SocketIOClient.SocketIO? socket;
        lock (Gate) socket = _socket;
        if (socket?.Connected == true)
            _ = socket.EmitAsync("lobbybrowser", true);
    }

    internal static bool TryConsumeSnapshot(out IReadOnlyList<VoiceLobbyListing> listings, out string status)
    {
        lock (Gate)
        {
            listings = Listings.Values.OrderByDescending(lobby => lobby.UpdatedAt).ToArray();
            status = _status;
            var dirty = _dirty;
            _dirty = false;
            return dirty;
        }
    }

    internal static async Task<BetterCrewLinkLobbyJoinResult> JoinLobbyAsync(string serverUrl, int lobbyId)
    {
        EnsureConnected(serverUrl);
        SocketIOClient.SocketIO? socket;
        lock (Gate) socket = _socket;
        if (socket == null)
            return BetterCrewLinkLobbyJoinResult.Fail("BCL live socket is not ready");

        var completion = new TaskCompletionSource<BetterCrewLinkLobbyJoinResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await socket.EmitAsync("join_lobby", response =>
            {
                try
                {
                    var status = response.GetValue<int>(0);
                    if (status == 0)
                    {
                        completion.TrySetResult(new BetterCrewLinkLobbyJoinResult(
                            true,
                            response.GetValue<string>(1) ?? "",
                            response.GetValue<string>(2) ?? "",
                            ""));
                    }
                    else
                    {
                        completion.TrySetResult(BetterCrewLinkLobbyJoinResult.Fail(response.GetValue<string>(1) ?? "Lobby is not joinable"));
                    }
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(BetterCrewLinkLobbyJoinResult.Fail(ex.Message));
                }
            }, lobbyId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BetterCrewLinkLobbyJoinResult.Fail(ex.Message);
        }

        var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(6))).ConfigureAwait(false);
        return completed == completion.Task
            ? await completion.Task.ConfigureAwait(false)
            : BetterCrewLinkLobbyJoinResult.Fail("Timed out joining BCL lobby");
    }

    internal static void Disconnect()
    {
        SocketIOClient.SocketIO? socket;
        lock (Gate)
        {
            socket = _socket;
            _socket = null;
            _serverUrl = "";
            _connected = false;
            Listings.Clear();
            _status = "Disconnected";
            _dirty = true;
        }

        if (socket == null) return;
        _ = DisconnectAsync(socket);
    }

    private static async Task DisconnectAsync(SocketIOClient.SocketIO socket)
    {
        try { await socket.EmitAsync("lobbybrowser", false).ConfigureAwait(false); } catch { }
        try { await socket.DisconnectAsync().ConfigureAwait(false); } catch { }
    }

    private static bool AddOrUpdateLocked(BetterCrewLinkPublicLobby lobby)
    {
        if (!BetterCrewLinkLobbyMetadata.IsPerfectComms(lobby)) return false;
        Listings[lobby.id] = BetterCrewLinkLobbyMetadata.ToListing(lobby);
        return true;
    }

    private static BetterCrewLinkPublicLobby[] ReadLobbyArray(SocketIOClient.SocketIOResponse response)
    {
        try
        {
            return response.GetValue<BetterCrewLinkPublicLobby[]>(0) ?? Array.Empty<BetterCrewLinkPublicLobby>();
        }
        catch
        {
            var element = response.GetValue<JsonElement>(0);
            return DeserializeLobbyArray(element);
        }
    }

    private static BetterCrewLinkPublicLobby? ReadLobby(SocketIOClient.SocketIOResponse response)
    {
        try
        {
            return response.GetValue<BetterCrewLinkPublicLobby>(0);
        }
        catch
        {
            var element = response.GetValue<JsonElement>(0);
            return DeserializeLobby(element);
        }
    }

    private static BetterCrewLinkPublicLobby[] DeserializeLobbyArray(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<BetterCrewLinkPublicLobby[]>(element.GetRawText(), JsonOptions) ?? Array.Empty<BetterCrewLinkPublicLobby>(),
            JsonValueKind.String => JsonSerializer.Deserialize<BetterCrewLinkPublicLobby[]>(element.GetString() ?? "[]", JsonOptions) ?? Array.Empty<BetterCrewLinkPublicLobby>(),
            _ => Array.Empty<BetterCrewLinkPublicLobby>(),
        };
    }

    private static BetterCrewLinkPublicLobby? DeserializeLobby(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => JsonSerializer.Deserialize<BetterCrewLinkPublicLobby>(element.GetRawText(), JsonOptions),
            JsonValueKind.String => JsonSerializer.Deserialize<BetterCrewLinkPublicLobby>(element.GetString() ?? "{}", JsonOptions),
            _ => null,
        };
    }

    private static void MarkParseFailed(Exception ex)
    {
        lock (Gate)
        {
            _status = "BCL live parse failed: " + ex.Message;
            _dirty = true;
        }

        VoiceDiagnostics.DebugWarning($"[VC] BCL lobby browser parse failed: {ex.Message}");
    }
}
