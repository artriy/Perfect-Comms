using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal sealed record VoiceGameStateSnapshot(
    VoiceGamePhase Phase,
    int MapId,
    int LocalClientId,
    int HostClientId,
    byte LocalPlayerId,
    Vector2? LocalPosition,
    float LocalLightRadius,
    bool CameraViewActive,
    int ActiveCameraIndex,
    Vector2? ActiveCameraPosition,
    IReadOnlyList<VoicePlayerSnapshot> Players,
    bool CommsSabotageActive,
    bool MeetingActive,
    int CameraCount,
    int ClosedDoorCount,
    bool LiveLocalPlayerResolved = true,
    bool RoutingRosterRetained = false,
    bool PlayerEnumerationCompleted = true)
{
    public bool TryGetLocalPlayer(out VoicePlayerSnapshot player)
        => TryGetPlayer(LocalPlayerId, out player);

    public bool TryGetPlayer(byte playerId, out VoicePlayerSnapshot player)
    {
        foreach (var candidate in Players)
        {
            if (candidate.PlayerId == playerId)
            {
                player = candidate;
                return true;
            }
        }

        player = default;
        return false;
    }

    public bool TryGetClient(int clientId, out VoicePlayerSnapshot player)
    {
        foreach (var candidate in Players)
        {
            if (candidate.ClientId == clientId)
            {
                player = candidate;
                return true;
            }
        }

        player = default;
        return false;
    }
}
