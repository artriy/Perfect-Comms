using System;

namespace VoiceChatPlugin.VoiceChat;

internal sealed class BetterCrewLinkPublicLobby
{
    public int id { get; set; } = -1;
    public string code { get; set; } = "";
    public string gameCode { get; set; } = "";
    public string roomCode { get; set; } = "";
    public string title { get; set; } = "";
    public string host { get; set; } = "";
    public int current_players { get; set; }
    public int max_players { get; set; }
    public string language { get; set; } = "";
    public string mods { get; set; } = "";
    public bool isPublic { get; set; }
    public string server { get; set; } = "";
    public int gameState { get; set; }
    public long stateTime { get; set; }
}

internal static class BetterCrewLinkLobbyMetadata
{
    internal const string PerfectCommsModTag = "PerfectComms";
    private const int LobbyState = 0;
    private const int TasksState = 1;
    private const int UnknownState = 4;

    internal static bool IsPerfectComms(BetterCrewLinkPublicLobby? lobby)
        => NormalizeModTag(lobby?.mods).Contains(NormalizeModTag(PerfectCommsModTag), StringComparison.Ordinal);

    internal static bool TryGetLobbyId(VoiceLobbyListing listing, out int lobbyId)
        => int.TryParse(listing.Id, out lobbyId);

    internal static BetterCrewLinkPublicLobby ToBclLobby(VoiceLobbyPublishRequest request)
        => new()
        {
            id = -1,
            title = Clamp(request.Title, 20, "Perfect Comms"),
            host = Clamp(request.Host, 10, "Unknown"),
            current_players = Math.Max(0, request.Players),
            max_players = Math.Max(1, request.MaxPlayers),
            code = ClampCode(request.Code),
            gameCode = ClampCode(request.Code),
            roomCode = ClampCode(request.Code),
            language = Clamp(request.Language, 16, "English"),
            mods = PerfectCommsModTag,
            isPublic = true,
            server = "",
            gameState = ToBclGameState(request.State),
            stateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    internal static VoiceLobbyListing ToListing(BetterCrewLinkPublicLobby lobby)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stateChangedAt = lobby.stateTime > 0 ? lobby.stateTime / 1000 : now;
        return new VoiceLobbyListing
        {
            Id = lobby.id.ToString(),
            Code = ResolveCode(lobby),
            Region = string.IsNullOrWhiteSpace(lobby.server) ? "BCL" : lobby.server.Trim(),
            Language = lobby.language ?? "",
            Title = string.IsNullOrWhiteSpace(lobby.title) ? "Perfect Comms" : lobby.title,
            Host = string.IsNullOrWhiteSpace(lobby.host) ? "Unknown" : lobby.host,
            Players = Math.Max(0, lobby.current_players),
            MaxPlayers = Math.Max(1, lobby.max_players),
            State = FromBclGameState(lobby.gameState),
            StateChangedAt = stateChangedAt,
            ModVersion = PerfectCommsModTag,
            ProtocolVersion = VoiceProtocol.ProtocolVersion,
            UpdatedAt = now,
            ExpiresAt = now + 30,
        };
    }

    internal static string BuildSignature(VoiceLobbyPublishRequest request)
        => string.Join("|",
            request.Code,
            request.State,
            request.Players,
            request.MaxPlayers,
            request.Host,
            request.Title,
            request.Language);

    private static int ToBclGameState(string? state)
        => string.Equals(state, "Lobby", StringComparison.OrdinalIgnoreCase)
            ? LobbyState
            : string.Equals(state, "InGame", StringComparison.OrdinalIgnoreCase)
                ? TasksState
                : UnknownState;

    private static string FromBclGameState(int state)
        => state == LobbyState ? "Lobby" : state is TasksState or 2 ? "InGame" : "Unknown";

    private static string Clamp(string? value, int max, string fallback)
    {
        var text = (value ?? "").Trim();
        if (string.IsNullOrEmpty(text)) text = fallback;
        return text.Length <= max ? text : text[..max];
    }

    private static string ResolveCode(BetterCrewLinkPublicLobby lobby)
        => ClampCode(FirstNonEmpty(lobby.code, lobby.gameCode, lobby.roomCode));

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string ClampCode(string? value)
    {
        var text = (value ?? "").Trim().ToUpperInvariant();
        Span<char> buffer = stackalloc char[Math.Min(text.Length, 8)];
        int count = 0;
        foreach (var c in text)
        {
            if (count >= buffer.Length) break;
            if (c is >= 'A' and <= 'Z')
                buffer[count++] = c;
        }

        return count >= 4 ? new string(buffer[..count]) : "";
    }

    private static string NormalizeModTag(string? value)
    {
        var text = value ?? "";
        Span<char> buffer = stackalloc char[text.Length];
        int count = 0;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                buffer[count++] = char.ToUpperInvariant(c);
        }

        return new string(buffer[..count]);
    }
}
