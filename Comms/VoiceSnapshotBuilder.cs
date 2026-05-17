using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceSnapshotBuilder
{
    public static VoiceGameStateSnapshot Build(bool commsSabotageActive)
    {
        var local = PlayerControl.LocalPlayer;
        byte localPlayerId = local != null ? local.PlayerId : byte.MaxValue;
        int localClientId = AmongUsClient.Instance != null ? AmongUsClient.Instance.ClientId : -1;
        Vector2? localPosition = local != null ? (Vector2)local.transform.position : null;

        var players = new List<VoicePlayerSnapshot>(16);
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player == null) continue;
            var data = player.Data;
            int clientId = ResolveClientId(player, data);
            string name = data?.PlayerName ?? player.name ?? "Unknown";
            VoiceRoleMuteState.GetPlayerRoleState(player, out bool isBlackmailed, out bool isJailed, out byte jailorId);

            players.Add(new VoicePlayerSnapshot(
                player.PlayerId,
                clientId,
                name,
                (Vector2)player.transform.position,
                player.PlayerId == localPlayerId,
                data?.IsDead == true,
                data?.Role?.IsImpostor == true,
                player.inVent,
                data?.Disconnected == true,
                player.isDummy || player.notRealPlayer,
                player.gameObject != null && player.gameObject.activeInHierarchy,
                isBlackmailed,
                isJailed,
                jailorId));
        }

        return new VoiceGameStateSnapshot(
            VoiceSceneState.ResolvePhase(),
            ResolveMapId(),
            localClientId,
            localPlayerId,
            localPosition,
            ResolveLocalLightRadius(local),
            players,
            commsSabotageActive,
            MeetingHud.Instance != null,
            ResolveCameraCount(),
            ResolveClosedDoorCount());
    }

    private static int ResolveMapId()
    {
        try
        {
            return ShipStatus.Instance != null ? (int)ShipStatus.Instance.Type : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int ResolveClientId(PlayerControl player, NetworkedPlayerInfo? data)
    {
        if (data != null) return data.ClientId;
        if (AmongUsClient.Instance != null)
        {
            var client = AmongUsClient.Instance.GetClientFromCharacter(player);
            if (client != null) return client.Id;
        }

        return -1;
    }

    private static int ResolveCameraCount()
    {
        try
        {
            return ShipStatus.Instance?.AllCameras?.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static float ResolveLocalLightRadius(PlayerControl? local)
    {
        try
        {
            if (local?.Data != null && ShipStatus.Instance != null)
                return ShipStatus.Instance.CalculateLightRadius(local.Data);
        }
        catch
        {
            // ignored; diagnostics will report -1 when unavailable
        }

        return -1f;
    }

    private static int ResolveClosedDoorCount()
    {
        try
        {
            var doors = ShipStatus.Instance?.AllDoors;
            if (doors == null) return 0;

            int closed = 0;
            foreach (var door in doors)
                if (door != null && !door.IsOpen)
                    closed++;
            return closed;
        }
        catch
        {
            return 0;
        }
    }
}
