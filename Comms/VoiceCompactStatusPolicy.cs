namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Pure presentation policy for the small, non-blocking voice HUD status. Keeping composition out
/// of Unity makes the priority rules and the optional mute/deafen warning independently testable.
/// </summary>
internal static class VoiceCompactStatusPolicy
{
    private const string PrimaryColor = "#FFCC66";
    private const string StateColor = "#FF7373";

    internal static string Compose(
        string? transient,
        string? operationalWarning,
        bool microphoneMuted,
        bool speakerDeafened,
        bool showMuteDeafenWarnings)
    {
        string primary = JoinCurrentStatus(transient, operationalWarning);
        string state = showMuteDeafenWarnings
            ? StateText(microphoneMuted, speakerDeafened)
            : string.Empty;

        if (primary.Length == 0)
            return state.Length == 0 ? string.Empty : Color(StateColor, state);
        if (state.Length == 0)
            return Color(PrimaryColor, primary);
        return $"{Color(PrimaryColor, primary)}\n{Color(StateColor, state)}";
    }

    internal static string StateText(bool microphoneMuted, bool speakerDeafened)
    {
        if (microphoneMuted && speakerDeafened) return "Muted / Deafened";
        if (speakerDeafened) return "Deafened";
        return microphoneMuted ? "Muted" : string.Empty;
    }

    private static string JoinCurrentStatus(string? transient, string? operationalWarning)
    {
        string first = string.IsNullOrWhiteSpace(transient) ? string.Empty : transient!.Trim();
        string second = string.IsNullOrWhiteSpace(operationalWarning) ? string.Empty : operationalWarning!.Trim();
        if (first.Length == 0) return second;
        if (second.Length == 0 || string.Equals(first, second, System.StringComparison.Ordinal)) return first;
        return $"{first}\n{second}";
    }

    private static string Color(string hex, string text) => $"<color={hex}>{text}</color>";
}
