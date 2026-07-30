using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Validates the Perfect Comms public-lobby directory endpoint. This endpoint is
/// discovery-only; private-room voice media and signaling never use it.
/// </summary>
public static class VoiceLobbyRegistryEndpoint
{
    public const string DefaultRegistryUrl = VoiceLobbyLiveProtocol.DefaultRegistryUrl;

    public static string NormalizeRegistryUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return DefaultRegistryUrl;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return DefaultRegistryUrl;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return DefaultRegistryUrl;
        return trimmed.TrimEnd('/');
    }
}
