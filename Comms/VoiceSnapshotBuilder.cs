using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceSnapshotBuilder
{
    private static DateTime _lastCameraDiagnosticUtc;
    private static string _lastCameraDiagnosticKey = string.Empty;

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
            if (TryResolveGameOptionsMapId(out int optionsMapId))
                return NormalizeMapId(optionsMapId);

            var ship = ShipStatus.Instance;
            if (ship == null) return -1;
            if (ship is AirshipStatus) return 4;

            return NormalizeMapId((int)ship.Type, ship.GetType().Name);
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryResolveGameOptionsMapId(out int mapId)
    {
        mapId = -1;
        try
        {
            var options = GameOptionsManager.Instance?.CurrentGameOptions;
            if (options != null)
            {
                mapId = options.MapId;
                return true;
            }

            var manager = GameOptionsManager.Instance;
            if (manager != null && TryReadMemberMapId(manager, "currentGameOptions", out mapId))
                return true;
        }
        catch
        {
            mapId = -1;
        }

        return false;
    }

    private static bool TryReadMemberMapId(object instance, string memberName, out int mapId)
    {
        mapId = -1;
        try
        {
            var type = instance.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
                return TryReadMapId(property.GetValue(instance), out mapId);

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                return TryReadMapId(field.GetValue(instance), out mapId);
        }
        catch
        {
            mapId = -1;
        }

        return false;
    }

    private static bool TryReadMapId(object? options, out int mapId)
    {
        mapId = -1;
        if (options == null) return false;

        try
        {
            var type = options.GetType();
            var property = type.GetProperty("MapId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var field = property == null
                ? type.GetField("MapId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                : null;
            object? value = property != null ? property.GetValue(options) : field?.GetValue(options);
            if (value == null) return false;

            mapId = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            mapId = -1;
            return false;
        }
    }

    private static int NormalizeMapId(int mapId, string? shipTypeName = null)
    {
        if (mapId == 3 && shipTypeName?.Contains("Fungle", StringComparison.OrdinalIgnoreCase) == true)
            return 5;

        return mapId;
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
            if (!VoiceCameraState.TryGetActiveMinigame(out var minigame))
                return default;

            var cameraView = ResolveCameraViewFromMinigame(minigame, mapId);
            MaybeLogCameraDiagnostic(minigame, mapId, cameraView);
            return cameraView;
        }
        catch (Exception ex)
        {
            VoiceCameraState.Clear();
            MaybeLogCameraError(mapId, ex);
            return default;
        }
    }

    private static CameraView ResolveCameraViewFromMinigame(Minigame minigame, int mapId)
    {
        switch (minigame)
        {
            case SurveillanceMinigame:
                return IsMultiCameraSurveillanceMap(mapId)
                    ? new CameraView(true, 6, null)
                    : default;
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

    private static bool IsMultiCameraSurveillanceMap(int mapId)
        => mapId is 0 or 3 or 4;

    private static void MaybeLogCameraDiagnostic(Minigame minigame, int mapId, CameraView cameraView)
    {
        var now = DateTime.UtcNow;
        string members = DescribeCameraMembers(minigame);
        string key =
            $"{mapId}|{minigame.GetType().Name}|{SafeInstanceId(minigame)}|{cameraView.Active}|{cameraView.Index}|{FormatVector(cameraView.Position)}|{members}";
        if (string.Equals(key, _lastCameraDiagnosticKey, StringComparison.Ordinal) &&
            (now - _lastCameraDiagnosticUtc).TotalSeconds < 1.0)
            return;

        _lastCameraDiagnosticKey = key;
        _lastCameraDiagnosticUtc = now;
        VoiceDiagnostics.Log("camera.snapshot",
            $"map={mapId} {DescribeShip()} minigame={minigame.GetType().Name} instance={SafeInstanceId(minigame)} " +
            $"active={cameraView.Active} index={cameraView.Index} pos={FormatVector(cameraView.Position)} members=\"{members}\"");
    }

    private static void MaybeLogCameraError(int mapId, Exception ex)
    {
        var now = DateTime.UtcNow;
        string key = $"error|{mapId}|{ex.GetType().Name}|{ex.Message}";
        if (string.Equals(key, _lastCameraDiagnosticKey, StringComparison.Ordinal) &&
            (now - _lastCameraDiagnosticUtc).TotalSeconds < 1.0)
            return;

        _lastCameraDiagnosticKey = key;
        _lastCameraDiagnosticUtc = now;
        VoiceDiagnostics.Log("camera.snapshot.error", $"map={mapId} error={ex.GetType().Name}:{LogSafe(ex.Message)}");
    }

    private static string DescribeShip()
    {
        try
        {
            string optionsMap = TryResolveGameOptionsMapId(out int optionsMapId)
                ? optionsMapId.ToString(CultureInfo.InvariantCulture)
                : "none";
            var ship = ShipStatus.Instance;
            if (ship == null) return $"ship=none shipType=none gameMap={optionsMap} allCameras=0";
            int allCameras = ship.AllCameras?.Length ?? 0;
            return $"ship={ship.GetType().Name} shipType={ship.Type} gameMap={optionsMap} allCameras={allCameras}";
        }
        catch (Exception ex)
        {
            return $"ship=error:{ex.GetType().Name} shipType=error gameMap=error allCameras=-1";
        }
    }

    private static string DescribeCameraMembers(Minigame minigame)
    {
        var parts = new List<string>();
        AddMemberValue(parts, minigame, "currentCamera");
        AddMemberValue(parts, minigame, "currentCam");
        AddMemberValue(parts, minigame, "camNumber");
        AddMemberValue(parts, minigame, "TargetPosition");
        AddMemberValue(parts, minigame, "targetPosition");
        AddMemberValue(parts, minigame, "FilteredRooms");
        AddMemberValue(parts, minigame, "survCameras");
        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }

    private static void AddMemberValue(List<string> parts, object instance, string name)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var type = instance.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    parts.Add($"{name}={FormatObject(field.GetValue(instance))}");
                    return;
                }

                var prop = type.GetProperty(name, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                {
                    parts.Add($"{name}={FormatObject(prop.GetValue(instance))}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            parts.Add($"{name}=error:{ex.GetType().Name}");
        }
    }

    private static string FormatObject(object? value)
    {
        if (value == null) return "null";
        if (value is Vector2 vector) return FormatVector(vector);
        if (value is Array array) return $"{value.GetType().Name}[{array.Length}]";
        return LogSafe(value.ToString() ?? string.Empty);
    }

    private static int SafeInstanceId(Minigame minigame)
    {
        try { return minigame.GetInstanceID(); }
        catch { return 0; }
    }

    private static string FormatVector(Vector2? value)
        => value.HasValue ? FormatVector(value.Value) : "none";

    private static string FormatVector(Vector2 value)
        => $"({value.x:0.000},{value.y:0.000})";

    private static string LogSafe(string value)
        => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");

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
