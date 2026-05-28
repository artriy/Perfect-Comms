using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceSnapshotBuilder
{
    public static VoiceGameStateSnapshot Build(bool commsSabotageActive)
    {
        VoiceRoleMuteState.Update();

        var local = PlayerControl.LocalPlayer;
        byte localPlayerId = local != null ? local.PlayerId : byte.MaxValue;
        int localClientId = AmongUsClient.Instance != null ? AmongUsClient.Instance.ClientId : -1;
        Vector2? localPosition = local != null ? (Vector2)local.transform.position : null;
        int mapId = ResolveMapId();
        var cameraView = ResolveCameraView(local, mapId);

        var players = new List<VoicePlayerSnapshot>(16);
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player == null) continue;
            var data = player.Data;
            int clientId = ResolveClientId(player, data);
            string name = data?.PlayerName ?? player.name ?? "Unknown";
            VoiceRoleMuteState.GetPlayerRoleState(
                player,
                out bool isBlackmailed,
                out bool isJailed,
                out byte jailorId,
                out bool isParasiteControlled,
                out bool isPuppeteerControlled,
                out _,
                out bool isVampire,
                out bool isLover,
                out byte loverPartnerId,
                out bool isBlackmailedNextRound,
                out bool isSwooped,
                out _);
            VoiceRoleMuteState.GetPlayerMediumVoiceState(
                player,
                out bool isMedium,
                out bool hasMediumSpirit,
                out Vector2 mediumSpiritPosition,
                out bool isMediatedGhost,
                out byte mediatingMediumId);

            players.Add(new VoicePlayerSnapshot(
                player.PlayerId,
                clientId,
                name,
                (Vector2)player.transform.position,
                player.PlayerId == localPlayerId,
                data?.IsDead == true,
                VoiceRoleMuteState.IsVoiceImpostor(player),
                isVampire,
                isLover,
                loverPartnerId,
                player.inVent,
                data?.Disconnected == true,
                player.isDummy || player.notRealPlayer,
                player.gameObject != null && player.gameObject.activeInHierarchy,
                isBlackmailed,
                isJailed,
                jailorId,
                isParasiteControlled,
                isPuppeteerControlled,
                isBlackmailedNextRound,
                isSwooped,
                isMedium,
                hasMediumSpirit,
                mediumSpiritPosition,
                isMediatedGhost,
                mediatingMediumId));
        }

        return new VoiceGameStateSnapshot(
            VoiceSceneState.ResolvePhase(),
            mapId,
            localClientId,
            ResolveHostClientId(),
            localPlayerId,
            localPosition,
            ResolveLocalLightRadius(local),
            cameraView.Active,
            cameraView.Index,
            cameraView.Position,
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

    private static int ResolveHostClientId()
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null) return -1;

            var hostIdProperty = client.GetType().GetProperty("HostId");
            if (hostIdProperty?.GetValue(client) is int hostId)
                return hostId;
        }
        catch
        {
            // Host id is best-effort; settings sync will refuse unauthenticated snapshots when unknown.
        }

        return -1;
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

    private static CameraView ResolveCameraView(PlayerControl? local, int mapId)
    {
        if (local == null)
        {
            VoiceCameraState.Clear();
            return default;
        }

        try
        {
            return VoiceCameraState.TryGetActiveMinigame(out var minigame)
                ? ResolveCameraViewFromMinigame(minigame, mapId)
                : default;
        }
        catch
        {
            VoiceCameraState.Clear();
            return default;
        }
    }

    private static CameraView ResolveCameraViewFromMinigame(Minigame minigame, int mapId)
    {
        switch (minigame)
        {
            case SurveillanceMinigame:
                return new CameraView(true, 6, null);
            case PlanetSurveillanceMinigame planet:
            {
                int index = Mathf.Clamp(planet.currentCamera, 0, 5);
                if (VoiceAudioOcclusion.TryGetFixedCameraPosition(mapId, index, out var position))
                    return new CameraView(true, index, position);

                Vector2 target = planet.TargetPosition;
                return target != default
                    ? new CameraView(true, index, target)
                    : new CameraView(true, index, null);
            }
            case FungleSurveillanceMinigame fungle:
            {
                if (fungle.securityCamera?.cam != null)
                    return new CameraView(true, -1, (Vector2)fungle.securityCamera.cam.transform.position);

                Vector2 target = fungle.TargetPosition;
                return target != default
                    ? new CameraView(true, -1, target)
                    : new CameraView(true, -1, null);
            }
            default:
                return default;
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

    private readonly record struct CameraView(bool Active, int Index, Vector2? Position);
}
