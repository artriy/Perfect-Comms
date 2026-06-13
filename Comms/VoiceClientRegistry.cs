using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatPlugin.VoiceChat;

internal enum VoiceClientCompatibility
{
    Unknown,
    Compatible,
    Incompatible,
}

internal sealed class VoiceClientInfo
{
    public int ClientId { get; init; }
    public byte PlayerId { get; set; } = byte.MaxValue;
    public string PlayerName { get; set; } = "Unknown";
    public int ProtocolVersion { get; set; }
    public int MinCompatibleVersion { get; set; }
    public VoiceFeatureFlags Features { get; set; }
    public VoiceClientCompatibility Compatibility { get; set; } = VoiceClientCompatibility.Unknown;
    public string Reason { get; set; } = "no handshake";
    public bool IsRadioActive { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public string StatusKey =>
        $"{Compatibility}|{ProtocolVersion}|{MinCompatibleVersion}|{Features}|{Reason}";
}

internal static class VoiceClientRegistry
{
    // Handshake/profile/audio-bootstrap marks arrive on the data-channel receive thread while the HUD and
    // recovery logic read on the Unity main thread, so the backing dictionary must be serialized. Sync is
    // re-entrant, so composite helpers can call the locked primitives without deadlocking.
    private static readonly object Sync = new();
    private static readonly Dictionary<int, VoiceClientInfo> Clients = new();

    public static IReadOnlyCollection<VoiceClientInfo> Snapshot
    {
        get { lock (Sync) return Clients.Values.ToArray(); }
    }

    public static void Reset()
    {
        lock (Sync) Clients.Clear();
    }

    public static bool MarkHandshake(
        int clientId,
        byte playerId,
        string? playerName,
        Guid expectedGuid,
        Guid receivedGuid,
        int protocolVersion,
        int minCompatibleVersion,
        VoiceFeatureFlags features)
    {
        lock (Sync)
        {
            Clients.TryGetValue(clientId, out var previous);
            string? oldKey = previous?.StatusKey;

            var info = previous ?? new VoiceClientInfo { ClientId = clientId };
            info.PlayerId = playerId;
            info.PlayerName = string.IsNullOrWhiteSpace(playerName) ? info.PlayerName : playerName!;
            info.ProtocolVersion = protocolVersion;
            info.MinCompatibleVersion = minCompatibleVersion;
            info.Features = features;
            info.LastSeenUtc = DateTime.UtcNow;

            if (receivedGuid != expectedGuid)
            {
                info.Compatibility = VoiceClientCompatibility.Incompatible;
                info.Reason = "mod guid mismatch";
            }
            else if (!features.HasFlag(VoiceFeatureFlags.CompatibilityHandshake))
            {
                info.Compatibility = VoiceClientCompatibility.Incompatible;
                info.Reason = "legacy handshake";
            }
            else if (!VoiceProtocol.IsCompatible(protocolVersion, minCompatibleVersion))
            {
                info.Compatibility = VoiceClientCompatibility.Incompatible;
                info.Reason = $"protocol {protocolVersion} outside {VoiceProtocol.MinCompatibleVersion}-{VoiceProtocol.ProtocolVersion}";
            }
            else
            {
                info.Compatibility = VoiceClientCompatibility.Compatible;
                info.Reason = "compatible";
            }

            Clients[clientId] = info;
            return oldKey != info.StatusKey;
        }
    }

    public static void MarkProfile(int clientId, byte playerId, string playerName)
    {
        lock (Sync)
        {
            if (!Clients.TryGetValue(clientId, out var info))
            {
                info = new VoiceClientInfo { ClientId = clientId };
                Clients[clientId] = info;
            }

            info.PlayerId = playerId;
            info.PlayerName = string.IsNullOrWhiteSpace(playerName) ? info.PlayerName : playerName;
            info.LastSeenUtc = DateTime.UtcNow;
        }
    }

    public static bool IsCompatible(int clientId)
    {
        lock (Sync)
            return Clients.TryGetValue(clientId, out var info) &&
                   info.Compatibility == VoiceClientCompatibility.Compatible;
    }

    public static bool IsKnownIncompatible(int clientId)
    {
        lock (Sync)
            return Clients.TryGetValue(clientId, out var info) &&
                   info.Compatibility == VoiceClientCompatibility.Incompatible;
    }

    public static void MarkAudioBootstrap(int clientId)
    {
        lock (Sync)
        {
            if (Clients.TryGetValue(clientId, out var existing))
            {
                if (existing.Compatibility != VoiceClientCompatibility.Unknown)
                    return;
            }
            else
            {
                existing = new VoiceClientInfo { ClientId = clientId };
                Clients[clientId] = existing;
            }

            existing.ProtocolVersion = VoiceProtocol.ProtocolVersion;
            existing.MinCompatibleVersion = VoiceProtocol.MinCompatibleVersion;
            existing.Features = VoiceProtocol.CurrentFeatures;
            existing.Compatibility = VoiceClientCompatibility.Compatible;
            existing.Reason = "audio bootstrap";
            existing.LastSeenUtc = DateTime.UtcNow;
        }
    }

    public static bool AreAllLiveRemoteClientsCompatible()
    {
        if (AmongUsClient.Instance == null) return false;

        bool hasRemoteClient = false;
        foreach (var client in AmongUsClient.Instance.allClients)
        {
            if (client.Id == AmongUsClient.Instance.ClientId) continue;
            hasRemoteClient = true;
            if (!IsCompatible(client.Id)) return false;
        }

        return hasRemoteClient;
    }

    public static bool HasLiveRemoteClients()
    {
        if (AmongUsClient.Instance == null) return false;

        foreach (var client in AmongUsClient.Instance.allClients)
            if (client.Id != AmongUsClient.Instance.ClientId)
                return true;
        return false;
    }

    public static bool HasKnownIncompatibleLiveRemoteClients()
    {
        if (AmongUsClient.Instance == null) return false;

        foreach (var client in AmongUsClient.Instance.allClients)
            if (client.Id != AmongUsClient.Instance.ClientId && IsKnownIncompatible(client.Id))
                return true;
        return false;
    }

    public static void MarkRadioActive(int clientId, bool active)
    {
        lock (Sync)
        {
            if (!Clients.TryGetValue(clientId, out var info))
            {
                info = new VoiceClientInfo { ClientId = clientId };
                Clients[clientId] = info;
            }

            info.IsRadioActive = active;
            info.LastSeenUtc = DateTime.UtcNow;
        }
    }

    public static bool IsRadioActive(int clientId)
    {
        lock (Sync)
            return Clients.TryGetValue(clientId, out var info) && info.IsRadioActive;
    }

    public static int[] GetCompatibleClientIds()
    {
        if (AmongUsClient.Instance == null) return Array.Empty<int>();

        var liveClientIds = new HashSet<int>();
        foreach (var client in AmongUsClient.Instance.allClients)
        {
            if (client.Id != AmongUsClient.Instance.ClientId)
                liveClientIds.Add(client.Id);
        }

        return liveClientIds
            .Where(IsCompatible)
            .ToArray();
    }

    public static void GetCompatibilitySummary(out int count, out bool hasCompatible, out bool hasIncompatible)
    {
        count = 0;
        hasCompatible = false;
        hasIncompatible = false;

        lock (Sync)
        {
            foreach (var client in Clients.Values)
            {
                count++;
                if (client.Compatibility == VoiceClientCompatibility.Compatible)
                    hasCompatible = true;
                else if (client.Compatibility == VoiceClientCompatibility.Incompatible)
                    hasIncompatible = true;
            }
        }
    }

    public static void PruneDisconnectedClients()
    {
        if (AmongUsClient.Instance == null)
        {
            lock (Sync) Clients.Clear();
            return;
        }

        var liveClientIds = new HashSet<int>();
        foreach (var client in AmongUsClient.Instance.allClients)
            liveClientIds.Add(client.Id);

        lock (Sync)
        {
            foreach (var id in Clients.Keys.ToArray())
                if (!liveClientIds.Contains(id))
                    Clients.Remove(id);
        }
    }

    public static string Describe(int clientId)
    {
        lock (Sync)
        {
            if (!Clients.TryGetValue(clientId, out var info))
                return $"client={clientId} compatibility=unknown reason=no handshake";

            return $"client={clientId} player={info.PlayerId}:{info.PlayerName} " +
                   $"compatibility={info.Compatibility} protocol={info.ProtocolVersion} " +
                   $"min={info.MinCompatibleVersion} features={info.Features} reason={info.Reason}";
        }
    }
}
