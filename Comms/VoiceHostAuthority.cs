using System;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct VoiceHostSenderIdentity(
    int SenderClientId,
    byte SenderPlayerId,
    string SenderPeerId,
    string Transport)
{
    public static VoiceHostSenderIdentity Unknown(string transport)
        => new(VoiceBackendCustomMessage.UnknownClientId, VoiceBackendCustomMessage.UnknownPlayerId, string.Empty, transport);

    public string StableKey
        => SenderClientId >= 0 ? $"client:{SenderClientId}" :
            SenderPlayerId != VoiceBackendCustomMessage.UnknownPlayerId ? $"player:{SenderPlayerId}" :
            !string.IsNullOrWhiteSpace(SenderPeerId) ? $"peer:{SenderPeerId}" :
            $"unknown:{Transport}";

    public string ToDiagnosticFields()
        => $"transport={Transport} senderClient={SenderClientId} senderPlayer={SenderPlayerId} senderPeer=\"{Sanitize(SenderPeerId)}\"";

    private static string Sanitize(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace("\"", "'");
}

internal static class VoiceHostAuthority
{
    public static VoiceHostSenderIdentity FromBackendMessage(VoiceBackendCustomMessage message, string transport)
        => new(message.SenderClientId, message.SenderPlayerId, message.SenderPeerId, transport);

    public static VoiceHostSenderIdentity FromPlayer(PlayerControl? player, string transport)
        => new(ResolveSenderClientId(player), ResolveSenderPlayerId(player), ResolveSenderPeerId(player), transport);

    public static bool IsTrustedHostSender(
        VoiceBackendCustomMessage message,
        VoiceGameStateSnapshot? snapshot,
        string transport,
        out string reason,
        out int hostClientId,
        out byte hostPlayerId)
        => IsTrustedHostSender(FromBackendMessage(message, transport), snapshot, out reason, out hostClientId, out hostPlayerId);

    public static bool IsTrustedHostSender(
        PlayerControl? player,
        VoiceGameStateSnapshot? snapshot,
        string transport,
        out VoiceHostSenderIdentity sender,
        out string reason,
        out int hostClientId,
        out byte hostPlayerId)
    {
        sender = FromPlayer(player, transport);
        return IsTrustedHostSender(sender, snapshot, out reason, out hostClientId, out hostPlayerId);
    }

    public static bool IsTrustedHostSender(
        VoiceHostSenderIdentity sender,
        VoiceGameStateSnapshot? snapshot,
        out string reason,
        out int hostClientId,
        out byte hostPlayerId)
    {
        hostClientId = ResolveHostClientId(snapshot);
        hostPlayerId = ResolveHostPlayerId(snapshot, hostClientId);

        if (hostClientId >= 0)
        {
            if (sender.SenderClientId == hostClientId)
            {
                reason = "host-client";
                return true;
            }

            if (sender.SenderClientId >= 0)
            {
                reason = "non-host";
                return false;
            }
        }

        if (hostPlayerId != VoiceBackendCustomMessage.UnknownPlayerId)
        {
            if (sender.SenderPlayerId == hostPlayerId)
            {
                reason = "host-player";
                return true;
            }

            if (sender.SenderPlayerId != VoiceBackendCustomMessage.UnknownPlayerId)
            {
                reason = "non-host";
                return false;
            }
        }

        reason = "unknown-sender";
        return false;
    }

    public static int ResolveHostClientId(VoiceGameStateSnapshot? snapshot)
    {
        // Prefer the live host id; the cached snapshot lags host migration by a refresh cycle,
        // briefly rejecting the new host as "non-host". Fall back to snapshot if live unavailable.
        var live = ResolveLiveHostClientId();
        if (live >= 0)
            return live;

        if (snapshot?.HostClientId >= 0)
            return snapshot.HostClientId;

        return VoiceBackendCustomMessage.UnknownClientId;
    }

    // HostId member name varies across Among Us/IL2CPP rebuilds: try candidates, cache the hit, log
    // once on total failure (otherwise every host snapshot is rejected as "unknown-sender"). Each
    // candidate is probed as a FIELD as well as a property: HostId is a public field on the GameLibs
    // InnerNetClient, so the previous property-only lookup never matched and always returned -1.
    private static readonly string[] HostIdPropertyNames = { "HostId", "HostClientId", "hostId" };
    private static string? _cachedHostIdPropertyName;
    private static bool _hostIdReflectionFailureLogged;

    internal static int ResolveLiveHostClientId()
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null) return VoiceBackendCustomMessage.UnknownClientId;

            var type = client.GetType();
            if (_cachedHostIdPropertyName != null
                && TryReadHostIdMember(client, type, _cachedHostIdPropertyName, out int cachedHostId))
                return cachedHostId;

            foreach (var name in HostIdPropertyNames)
            {
                if (TryReadHostIdMember(client, type, name, out int hostId))
                {
                    _cachedHostIdPropertyName = name;
                    return hostId;
                }
            }

            if (!_hostIdReflectionFailureLogged)
            {
                _hostIdReflectionFailureLogged = true;
                VoiceDiagnostics.Log("host.resolve.failed",
                    $"reason=no-hostid-member type=\"{type.FullName}\"");
            }
        }
        catch
        {
        }

        return VoiceBackendCustomMessage.UnknownClientId;
    }

    private static bool TryReadHostIdMember(object client, Type type, string name, out int hostId)
    {
        hostId = 0;
        if (type.GetProperty(name)?.GetValue(client) is int propertyHostId)
        {
            hostId = propertyHostId;
            return true;
        }

        if (type.GetField(name)?.GetValue(client) is int fieldHostId)
        {
            hostId = fieldHostId;
            return true;
        }

        return false;
    }

    private static byte ResolveHostPlayerId(VoiceGameStateSnapshot? snapshot, int hostClientId)
    {
        if (snapshot != null && hostClientId >= 0 && snapshot.TryGetClient(hostClientId, out var host))
            return host.PlayerId;

        try
        {
            foreach (var player in PlayerControl.AllPlayerControls)
            {
                if (player == null) continue;
                if (ResolveSenderClientId(player) == hostClientId)
                    return player.PlayerId;
            }
        }
        catch
        {
        }

        return VoiceBackendCustomMessage.UnknownPlayerId;
    }

    private static int ResolveSenderClientId(PlayerControl? player)
    {
        if (player == null) return VoiceBackendCustomMessage.UnknownClientId;

        try
        {
            var data = player.Data;
            if (data != null) return data.ClientId;
        }
        catch
        {
        }

        try
        {
            var client = AmongUsClient.Instance?.GetClientFromCharacter(player);
            if (client != null) return client.Id;
        }
        catch
        {
        }

        return VoiceBackendCustomMessage.UnknownClientId;
    }

    private static byte ResolveSenderPlayerId(PlayerControl? player)
    {
        try
        {
            return player?.PlayerId ?? VoiceBackendCustomMessage.UnknownPlayerId;
        }
        catch
        {
            return VoiceBackendCustomMessage.UnknownPlayerId;
        }
    }

    private static string ResolveSenderPeerId(PlayerControl? player)
    {
        try
        {
            return player?.NetId.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
