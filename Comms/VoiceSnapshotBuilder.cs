using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceSnapshotBuilder
{
    private static DateTime _lastCameraDiagnosticUtc;
    private static string _lastCameraDiagnosticKey = string.Empty;

    public static VoiceGameStateSnapshot Build(bool commsSabotageActive)
    {
        long roleTicks = VoiceFrameProfiler.Begin();
        VoiceRoleMuteState.Update();
        VoiceFrameProfiler.End("snapshot.roles", roleTicks);

        long sceneTicks = VoiceFrameProfiler.Begin();
        var local = PlayerControl.LocalPlayer;
        byte localPlayerId = local != null ? local.PlayerId : byte.MaxValue;
        int localClientId = AmongUsClient.Instance != null ? AmongUsClient.Instance.ClientId : -1;
        bool liveLocalPlayerResolved = IsLiveLocalPlayerResolved(local, localClientId);
        Vector2? localPosition = local != null ? (Vector2)local.transform.position : null;
        int mapId = ResolveMapId();
        var cameraView = ResolveCameraView(local, mapId);
        var phase = VoiceSceneState.ResolvePhase();
        var apiPhase = VoiceModBridge.ToApiPhase(phase);
        VoiceModRegistry.NotifyPhase(apiPhase, local);

        bool localBaseDead = local?.Data != null &&
                             (local.Data.IsDead || local.Data.Role?.IsDead == true);
        VoicePlayerTraits localTraits = local != null
            ? VoiceModRegistry.ResolvePlayerTraits(local, apiPhase, isLocal: true, localBaseDead)
            : VoicePlayerTraits.None;
        bool localVoiceDead = localBaseDead || (localTraits & VoicePlayerTraits.VoiceDead) != 0;

        // Resolve once: is the LOCAL player controlling a victim (Puppeteer/Parasite)? Used to relocate/augment
        // their proximity hearing to the victim's surroundings when the host has the matching toggle on.
        VoiceControlHearingMode localControlMode = VoiceControlHearingMode.None;
        Vector2 localControlledVictimPos = default;
        float localControlledVictimLight = -1f;
        if (local != null && VoiceRoleMuteState.TryGetLocalControlledVictim(local, out var ctrlMode, out var ctrlVictim))
        {
            try
            {
                localControlledVictimPos = (Vector2)ctrlVictim.transform.position;
                localControlledVictimLight = ResolveLocalLightRadius(ctrlVictim);
                localControlMode = ctrlMode;
            }
            catch
            {
                localControlMode = VoiceControlHearingMode.None;
            }
        }

        // Third-party mod listener-origin override (PerfectComms.Api Primitive 3). Reuses the
        // control-hearing fields/branch already consumed by the calculator. A built-in TOU control
        // takes precedence; the override only applies when no built-in control hearing is active.
        ExternalVoiceState localExternal = ExternalVoiceState.None;
        if (local != null)
        {
            localExternal = VoiceModRegistry.ResolvePlayer(local, apiPhase, isLocal: true,
                isDead: localVoiceDead);
            var settings = VoiceRoomSettingsState.Current;
            bool builtInOriginActive =
                localControlMode == VoiceControlHearingMode.PuppeteerSwap && settings.PuppeteerHearFromVictim ||
                localControlMode == VoiceControlHearingMode.ParasiteAdditive && settings.ParasiteHearFromVictim;
            if (!builtInOriginActive && localExternal.ListenerActive)
            {
                localControlledVictimPos = localExternal.ListenerOrigin;
                localControlledVictimLight = localExternal.ListenerLightRadius;
                // Use the External* modes so the calculator applies them unconditionally, without
                // gating on the TOU-specific PuppeteerHearFromVictim/ParasiteHearFromVictim toggles.
                localControlMode = localExternal.ListenerReplace
                    ? VoiceControlHearingMode.ExternalReplace
                    : VoiceControlHearingMode.ExternalAdditive;
            }
        }
        VoiceFrameProfiler.End("snapshot.scene", sceneTicks);

        long playerTicks = VoiceFrameProfiler.Begin();
        var players = new List<VoicePlayerSnapshot>(16);
        bool playerEnumerationCompleted = true;
        try
        {
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
                out bool isCrewpostor,
                out bool isVampire,
                out bool isLover,
                out byte loverPartnerId,
                out bool isBlackmailedNextRound,
                out bool isSwooped,
                out bool isGlitchHacked);
            VoiceRoleMuteState.GetPlayerMediumVoiceState(
                player,
                out bool isMedium,
                out bool hasMediumSpirit,
                out Vector2 mediumSpiritPosition,
                out bool isMediatedGhost,
                out byte mediatingMediumId);

            bool dataDead = data?.IsDead == true;
            bool roleOnlyDead = !dataDead && data?.Role?.IsDead == true;
            bool baseDead = dataDead || roleOnlyDead;
            bool playerIsLocal = player.PlayerId == localPlayerId;
            VoicePlayerTraits traits = playerIsLocal
                ? localTraits
                : VoiceModRegistry.ResolvePlayerTraits(
                    player,
                    apiPhase,
                    isLocal: false,
                    isDead: baseDead,
                    localIsDead: localVoiceDead);
            bool voiceDead = baseDead || (traits & VoicePlayerTraits.VoiceDead) != 0;
            bool voiceSpectator = roleOnlyDead || (traits & VoicePlayerTraits.Spectator) != 0;
            ExternalVoiceState external = playerIsLocal
                ? localExternal
                : VoiceModRegistry.ResolvePlayer(
                    player,
                    apiPhase,
                    isLocal: false,
                    isDead: voiceDead,
                    localIsDead: localVoiceDead);
            external = external with
            {
                Pair = VoiceModRegistry.ResolvePair(
                    local,
                    player,
                    apiPhase,
                    localVoiceDead,
                    voiceDead),
            };
            players.Add(new VoicePlayerSnapshot(
                player.PlayerId,
                clientId,
                name,
                (Vector2)player.transform.position,
                playerIsLocal,
                voiceDead,
                voiceSpectator,
                data?.Role?.IsImpostor == true ||
                (VoiceRoomSettingsState.Current.CrewpostorUsesImpostorVoice && isCrewpostor) ||
                (traits & VoicePlayerTraits.ImpostorVoice) != 0,
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
                mediatingMediumId,
                playerIsLocal ? localControlMode : VoiceControlHearingMode.None,
                playerIsLocal ? localControlledVictimPos : default,
                playerIsLocal ? localControlledVictimLight : -1f,
                external,
                IsGlitchHacked: isGlitchHacked));
        }
        }
        catch
        {
            // Scene transitions can invalidate AllPlayerControls mid-loop; keep what was collected.
            playerEnumerationCompleted = false;
        }

        ApplyLoverPairingFallback(players);
        VoiceFrameProfiler.End("snapshot.players", playerTicks);

        long finalizeTicks = VoiceFrameProfiler.Begin();
        var snapshot = new VoiceGameStateSnapshot(
            phase,
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
            ResolveClosedDoorCount(),
            liveLocalPlayerResolved,
            RoutingRosterRetained: false,
            PlayerEnumerationCompleted: playerEnumerationCompleted);
        VoiceFrameProfiler.End("snapshot.finalize", finalizeTicks);
        return snapshot;
    }

    private static bool IsLiveLocalPlayerResolved(PlayerControl? local, int localClientId)
    {
        if (local == null || localClientId < 0)
            return false;

        try
        {
            var data = local.Data;
            return data != null
                   && !data.Disconnected
                   && local.PlayerId != byte.MaxValue
                   && ResolveClientId(local, data) == localClientId;
        }
        catch
        {
            return false;
        }
    }

    // If partner-id reflection failed (byte.MaxValue) a genuine pair would be false-muted on the lovers
    // radio. Lovers are always a pair of two, so when exactly two exist, pair them explicitly.
    private static void ApplyLoverPairingFallback(List<VoicePlayerSnapshot> players)
    {
        int firstIndex = -1, secondIndex = -1, loverCount = 0;
        for (int i = 0; i < players.Count; i++)
        {
            if (!players[i].IsLover) continue;
            loverCount++;
            if (firstIndex < 0) firstIndex = i;
            else if (secondIndex < 0) secondIndex = i;
        }
        if (loverCount != 2) return;

        var first = players[firstIndex];
        var second = players[secondIndex];
        if (first.LoverPartnerId == byte.MaxValue)
            players[firstIndex] = first with { LoverPartnerId = second.PlayerId };
        if (second.LoverPartnerId == byte.MaxValue)
            players[secondIndex] = second with { LoverPartnerId = first.PlayerId };
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

    // Shared resolver keeps host discovery consistent with VoiceHostAuthority; host id is
    // best-effort, settings sync refuses unauthenticated snapshots when it is unknown.
    private static int ResolveHostClientId()
        => VoiceHostAuthority.ResolveLiveHostClientId();

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
                // Clamp against the live camera count, not a hardcoded 5, so Sentry-added cameras on
                // the single-camera (Polus) surveillance view are addressable.
                int camCount = ResolveCameraCount();
                int index = Mathf.Clamp(planet.currentCamera, 0, Math.Max(0, camCount - 1));
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
        // Diagnostics are off by default. C# evaluates DescribeCameraMembers (a List alloc + 7 reflection
        // field/property hierarchy walks) and the interpolated key BEFORE VoiceDiagnostics.Log's internal
        // IsEnabled check, so without this guard ~12 allocs + 7 reflection lookups ran ~20x/sec the whole
        // time a player had a surveillance camera open, even with logging disabled.
        if (!VoiceDiagnostics.IsEnabled) return;

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
        if (!VoiceDiagnostics.IsEnabled) return;

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
