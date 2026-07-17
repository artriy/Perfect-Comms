using System;

namespace VoiceChatPlugin.VoiceChat;

internal readonly record struct VoiceHostSenderIdentity(
    int SenderClientId,
    byte SenderPlayerId,
    string SenderPeerId,
    string Transport)
{
    public const int UnknownClientId = -1;
    public const byte UnknownPlayerId = byte.MaxValue;

    public static VoiceHostSenderIdentity Unknown(string transport)
        => new(UnknownClientId, UnknownPlayerId, string.Empty, transport);

    public string StableKey
        => SenderClientId >= 0 ? $"client:{SenderClientId}" :
            SenderPlayerId != UnknownPlayerId ? $"player:{SenderPlayerId}" :
            !string.IsNullOrWhiteSpace(SenderPeerId) ? $"peer:{SenderPeerId}" :
            $"unknown:{Transport}";

    public string ToDiagnosticFields()
        => $"transport={Transport} senderClient={SenderClientId} senderPlayer={SenderPlayerId} senderPeer=\"{Sanitize(SenderPeerId)}\"";

    private static string Sanitize(string value)
        => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Replace("\"", "'");
}

internal static class VoiceHostAuthority
{
    // This matches the routed PlayerControl's resolved owner to the live host id. HandleRpc does not
    // expose authenticated packet-sender provenance, so this is a compatibility/consistency check and
    // must not be treated as a security boundary for remotely destructive operations.
    public static VoiceHostSenderIdentity FromPlayer(PlayerControl? player, string transport)
        => new(ResolveSenderClientId(player), ResolveSenderPlayerId(player), ResolveSenderPeerId(player), transport);

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
        return IsTrustedResolvedHostSender(sender, hostClientId, hostPlayerId, out reason);
    }

    internal static bool IsTrustedResolvedHostSender(
        VoiceHostSenderIdentity sender,
        int hostClientId,
        byte hostPlayerId,
        out string reason)
    {
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

            // Host policy is security-sensitive. PlayerId/NetId identifies the dispatched object but
            // is not authenticated client ownership on every relay. If the sender client cannot be
            // resolved, fail closed and let the bounded settings-request retry converge once the
            // PlayerControl ownership mapping is available.
            reason = "sender-client-unresolved";
            return false;
        }

        // Never infer authority from PlayerId while the live host client is unresolved. During a
        // reconstruction gap many PlayerControls can also have ClientId -1; treating the first one
        // as the host let an arbitrary stale/non-host object authorize policy. The request retry
        // converges once InnerNet's host/client ownership mapping is available.
        reason = "host-client-unresolved";
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

        return VoiceHostSenderIdentity.UnknownClientId;
    }

    // HostId member name varies across Among Us/IL2CPP rebuilds: try candidates, cache the hit, log
    // once on total failure (otherwise every host snapshot is rejected as "unknown-sender").
    // Il2CppInterop surfaces native il2cpp fields (like InnerNetClient.HostId) as MANAGED PROPERTIES on
    // the generated proxy, so the property probe is the one that actually resolves HostId; the field
    // probe is a defensive fallback for any future build that exposes a genuine managed field.
    private static readonly string[] HostIdPropertyNames = { "HostId", "HostClientId", "hostId" };
    private static string? _cachedHostIdPropertyName;
    private static bool _hostIdReflectionFailureLogged;

    internal static int ResolveLiveHostClientId()
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null) return VoiceHostSenderIdentity.UnknownClientId;

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

        return VoiceHostSenderIdentity.UnknownClientId;
    }

    private static bool TryReadHostIdMember(object client, Type type, string name, out int hostId)
    {
        hostId = 0;
        try
        {
            // Property probe: resolves an il2cpp field that Il2CppInterop exposed as a managed property.
            if (type.GetProperty(name)?.GetValue(client) is int propertyHostId)
            {
                hostId = propertyHostId;
                return true;
            }

            // Field probe: defensive fallback for a genuine managed field (inert for il2cpp proxies).
            if (type.GetField(name)?.GetValue(client) is int fieldHostId)
            {
                hostId = fieldHostId;
                return true;
            }
        }
        catch
        {
            // A reflection access on this candidate threw; treat as "not this member" so the caller
            // moves on to the next candidate instead of aborting the whole resolution.
        }

        return false;
    }

    private static byte ResolveHostPlayerId(VoiceGameStateSnapshot? snapshot, int hostClientId)
    {
        if (hostClientId < 0)
            return VoiceHostSenderIdentity.UnknownPlayerId;

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

        return VoiceHostSenderIdentity.UnknownPlayerId;
    }

    private static int ResolveSenderClientId(PlayerControl? player)
    {
        if (player == null) return VoiceHostSenderIdentity.UnknownClientId;

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

        return VoiceHostSenderIdentity.UnknownClientId;
    }

    private static byte ResolveSenderPlayerId(PlayerControl? player)
    {
        try
        {
            return player?.PlayerId ?? VoiceHostSenderIdentity.UnknownPlayerId;
        }
        catch
        {
            return VoiceHostSenderIdentity.UnknownPlayerId;
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
