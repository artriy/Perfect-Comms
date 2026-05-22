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
        if (snapshot?.HostClientId >= 0)
            return snapshot.HostClientId;

        try
        {
            var client = AmongUsClient.Instance;
            if (client == null) return VoiceBackendCustomMessage.UnknownClientId;

            var hostIdProperty = client.GetType().GetProperty("HostId");
            if (hostIdProperty?.GetValue(client) is int hostId)
                return hostId;
        }
        catch
        {
        }

        return VoiceBackendCustomMessage.UnknownClientId;
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
