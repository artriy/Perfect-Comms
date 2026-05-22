using System;

namespace VoiceChatPlugin.VoiceChat;

public readonly record struct VoiceEndpoint(VoiceTransportBackend Backend, string ServerUrl)
{
    public bool IsInterstellar => Backend == VoiceTransportBackend.Interstellar;
    public bool IsBetterCrewLink => Backend == VoiceTransportBackend.BetterCrewLink;
}

public static class VoiceEndpointSettings
{
    public const string DefaultBetterCrewLinkServerUrl = "https://bettercrewl.ink";
    public const string DefaultInterstellarServerUrl = "ws://interstellar.amongusclub.cn:19836";

    public static VoiceEndpoint Resolve(string? interstellarServerUrl)
        => new(VoiceTransportBackend.Interstellar, NormalizeInterstellarServerUrl(interstellarServerUrl));

    public static VoiceEndpoint Resolve(VoiceTransportBackend backend, string? betterCrewLinkServerUrl, string? interstellarServerUrl)
        => backend == VoiceTransportBackend.Interstellar
            ? new(VoiceTransportBackend.Interstellar, NormalizeInterstellarServerUrl(interstellarServerUrl))
            : new(VoiceTransportBackend.BetterCrewLink, NormalizeBetterCrewLinkServerUrl(betterCrewLinkServerUrl));

    public static VoiceEndpoint ResolveHostSelected(VoiceRoomSettingsSnapshot hostSettings, string? betterCrewLinkServerUrl, string? interstellarServerUrl)
    {
        var backend = (VoiceTransportBackend)hostSettings.Backend;
        if (!Enum.IsDefined(typeof(VoiceTransportBackend), backend))
            backend = VoiceTransportBackend.BetterCrewLink;

        var selectedUrl = hostSettings.BackendServerUrl;
        if (!string.IsNullOrWhiteSpace(selectedUrl))
        {
            return backend == VoiceTransportBackend.Interstellar
                ? new VoiceEndpoint(VoiceTransportBackend.Interstellar, NormalizeInterstellarServerUrl(selectedUrl))
                : new VoiceEndpoint(VoiceTransportBackend.BetterCrewLink, NormalizeBetterCrewLinkServerUrl(selectedUrl));
        }

        return Resolve(backend, betterCrewLinkServerUrl, interstellarServerUrl);
    }

    public static string NormalizeInterstellarServerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultInterstellarServerUrl;

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return DefaultInterstellarServerUrl;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultInterstellarServerUrl;
        }

        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (normalized.EndsWith("/vc", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^3].TrimEnd('/');

        return string.IsNullOrWhiteSpace(normalized) ? DefaultInterstellarServerUrl : normalized;
    }

    public static string NormalizeBetterCrewLinkServerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultBetterCrewLinkServerUrl;

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return DefaultBetterCrewLinkServerUrl;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultBetterCrewLinkServerUrl;
        }

        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? DefaultBetterCrewLinkServerUrl : normalized;
    }

    public static string BuildInterstellarRoomUrl(string serverUrl)
    {
        return NormalizeInterstellarServerUrl(serverUrl) + "/vc";
    }
}
