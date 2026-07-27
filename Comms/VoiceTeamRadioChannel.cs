namespace VoiceChatPlugin.VoiceChat;

internal enum VoiceTeamRadioChannel : byte
{
    None = 0,
    Impostors = 1,
    External = 4,
    All = byte.MaxValue,
}

internal static class VoiceTeamRadioChannels
{
    public static readonly VoiceTeamRadioChannel[] Order =
    [
        VoiceTeamRadioChannel.Impostors,
    ];

    public static VoiceTeamRadioChannel FromWire(bool active, byte? channel)
    {
        if (!active)
            return VoiceTeamRadioChannel.None;

        if (!channel.HasValue)
            return VoiceTeamRadioChannel.All;

        return Normalize((VoiceTeamRadioChannel)channel.Value);
    }

    public static VoiceTeamRadioChannel Normalize(VoiceTeamRadioChannel channel)
        => channel is VoiceTeamRadioChannel.Impostors
            or VoiceTeamRadioChannel.External
            or VoiceTeamRadioChannel.All
            ? channel
            : VoiceTeamRadioChannel.None;

    public static bool IsActive(VoiceTeamRadioChannel channel)
        => Normalize(channel) != VoiceTeamRadioChannel.None;

    public static string DisplayName(VoiceTeamRadioChannel channel)
        => Normalize(channel) switch
        {
            VoiceTeamRadioChannel.Impostors => "Impostors",
            VoiceTeamRadioChannel.External => "Managed",
            VoiceTeamRadioChannel.All => "All Teams",
            _ => "Unavailable",
        };
}
