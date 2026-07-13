using System;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Validates the optional BetterCrewLink public-lobby directory endpoint. This endpoint is not
/// part of private-room voice media, signaling, peer roster discovery, ICE, or TURN credential
/// delivery; it is used only to browse/publish optional public lobby listings.
/// </summary>
public static class BetterCrewLinkLobbyEndpoint
{
    public const string DefaultServerUrl = "https://bettercrewl.ink";

    public static string NormalizeServerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultServerUrl;

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return DefaultServerUrl;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultServerUrl;
        }

        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? DefaultServerUrl : normalized;
    }
}
