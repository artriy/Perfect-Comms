using System.Threading;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Makes an explicit InnerNet disconnect authoritative over stale scene objects. EndGame and
/// LobbyBehaviour can survive for a frame while DisconnectInternal is unwinding; without this
/// latch the room driver could recreate capture immediately after the user chose Exit.
/// Only AmongUsClient.OnGameJoined confirms that a later network session may own voice again.
/// </summary>
internal static class VoiceRoomLifetimeGate
{
    private static int _explicitDisconnectLatched;

    internal static bool IsExplicitDisconnectLatched
        => Volatile.Read(ref _explicitDisconnectLatched) != 0;

    internal static void MarkExplicitDisconnect(string reason)
    {
        Interlocked.Exchange(ref _explicitDisconnectLatched, 1);
        try { VoiceDiagnostics.Log("voice.room.lifetime", $"event=disconnect-latched reason={Safe(reason)}"); }
        catch { /* a diagnostic sink can never interfere with the vanilla disconnect */ }
    }

    internal static void ConfirmJoinedSession(string source)
    {
        if (Interlocked.Exchange(ref _explicitDisconnectLatched, 0) == 0)
            return;
        try { VoiceDiagnostics.Log("voice.room.lifetime", $"event=disconnect-cleared source={Safe(source)}"); }
        catch { /* joining the game must not depend on diagnostics */ }
    }

    private static string Safe(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", System.StringComparison.Ordinal)
            .Replace("\r", "\\r", System.StringComparison.Ordinal)
            .Replace("\n", "\\n", System.StringComparison.Ordinal)
            .Replace("\"", "\\\"", System.StringComparison.Ordinal);
}
