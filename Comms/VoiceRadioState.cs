namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Selected Team Radio transport state. Built-in channels use an empty managed key; externally
/// registered channels use <see cref="VoiceTeamRadioChannel.External"/> and their namespaced key.
/// </summary>
internal readonly record struct VoiceRadioState(VoiceTeamRadioChannel Channel, string ManagedKey)
{
    internal static readonly VoiceRadioState None = new(VoiceTeamRadioChannel.None, string.Empty);

    internal bool IsActive => VoiceTeamRadioChannels.IsActive(Channel)
                              && (Channel != VoiceTeamRadioChannel.External || !string.IsNullOrEmpty(ManagedKey));

    internal static VoiceRadioState BuiltIn(VoiceTeamRadioChannel channel)
    {
        channel = VoiceTeamRadioChannels.Normalize(channel);
        return channel is VoiceTeamRadioChannel.None or VoiceTeamRadioChannel.External
            ? None
            : new VoiceRadioState(channel, string.Empty);
    }

    internal static VoiceRadioState Managed(string? key)
        => VoiceManagedRadioWireKey.IsValid(key)
            ? new VoiceRadioState(VoiceTeamRadioChannel.External, key!)
            : None;

    internal VoiceRadioState Normalize()
        => Channel == VoiceTeamRadioChannel.External ? Managed(ManagedKey) : BuiltIn(Channel);
}

internal static class VoiceManagedRadioWireKey
{
    internal const int MaxLength = 384;

    internal static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxLength) return false;

        var separatorCount = 0;
        for (var i = 0; i < key.Length; i++)
        {
            var value = key[i];
            if (value == '\0')
            {
                separatorCount++;
                continue;
            }

            if (char.IsControl(value)) return false;
        }

        return separatorCount == 1 && key[0] != '\0' && key[^1] != '\0';
    }
}
