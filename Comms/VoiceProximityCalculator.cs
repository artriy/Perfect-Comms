using System;
using System.Collections.Generic;
using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceProximityCalculator
{
    private const float GhostVisionRangeMultiplier = 1f;
    private const float LowVolumeFloor = 0.06f;
    private static float _lastUnimpairedLocalLightRadius;

    // Clear stale light radius on lifecycle transitions so a prior game can't shrink new-game hearing range.
    internal static void ResetSightState()
        => _lastUnimpairedLocalLightRadius = 0f;

    public static VoiceProximityResult CalculateLobby(
        VoicePlayerSnapshot? targetPlayer,
        Vector2? listenerPos)
        => CalculateLobby(null, targetPlayer, listenerPos);

    public static VoiceProximityResult CalculateLobby(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot? targetPlayer,
        Vector2? listenerPos)
    {
        if (!targetPlayer.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);
        var target = targetPlayer.Value;
        if (IsUnavailableTarget(target))
            return VoiceProximityResult.Muted(VoiceProximityReason.TargetUnavailable);
        if (!listenerPos.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener);

        if (TryGetModPairRoute(localPlayer, target, 1f, out var pairRoute, listenerPos))
            return pairRoute;
        if (TryGetModChannelRoute(localPlayer, target, 1f, out var channelRoute, listenerPos))
            return channelRoute;

        var s = VoiceRoomSettingsState.Current;
        float maxDistance = s.MaxChatDistance;
        float dist = Distance(target.Position, listenerPos.Value);
        float volume = VoiceAudioOcclusion.ApplyFalloff(dist, maxDistance, (VoiceFalloffMode)s.FalloffMode);
        if (volume < LowVolumeFloor)
            volume = 0f;
        float pan = VoiceChatRoom.GetPan(listenerPos.Value.x, target.Position.x);

        return new(volume, 0f, 0f, pan, VoiceAudioFilterMode.None,
            volume > 0f, VoiceProximityReason.Lobby, 1f);
    }

    // End-game summary screen: the Among Us world (player controls, positions) is gone, so the snapshot has no
    // players and proximity cannot be computed -- CalculateLobby would mute every peer (Unmapped / NoListener).
    // EndGame is already a global voice phase, so treat it like a post-game group call: every connected peer is
    // heard at full volume, centered, letting players react to the result (e.g. a jester win) together.
    public static VoiceProximityResult CalculateEndGame()
        => new(0.7f, 0f, 0f, 0f, VoiceAudioFilterMode.None, true, VoiceProximityReason.Lobby, 1f);

    public static VoiceProximityResult CalculateMeeting(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot? targetPlayer,
        bool targetRadioActive,
        VoiceTeamRadioChannel targetRadioChannel = VoiceTeamRadioChannel.All)
        => CalculateMeeting(localPlayer, targetPlayer, targetRadioActive, VoiceGamePhase.Meeting, targetRadioChannel);

    public static VoiceProximityResult CalculateMeeting(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot? targetPlayer,
        bool targetRadioActive,
        VoiceGamePhase phase,
        VoiceTeamRadioChannel targetRadioChannel = VoiceTeamRadioChannel.All,
        string targetManagedRadioKey = "")
    {
        if (!targetPlayer.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);

        var s = VoiceRoomSettingsState.Current;
        var target = targetPlayer.Value;
        if (IsUnavailableTarget(target))
            return VoiceProximityResult.Muted(VoiceProximityReason.TargetUnavailable);
        bool localDead = localPlayer?.IsDead == true;
        bool targetDead = target.IsDead;
        var targetRadioState = targetRadioChannel == VoiceTeamRadioChannel.External
            ? VoiceRadioState.Managed(targetManagedRadioKey)
            : targetRadioActive
                ? VoiceRadioState.BuiltIn(targetRadioChannel)
                : VoiceRadioState.None;

        if (s.OnlyGhostsCanTalk && !localDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk);

        if (VoiceRoleMuteState.IsMeetingVoiceBlocked(target, phase))
            return VoiceProximityResult.Muted(VoiceRoleMuteState.GetMeetingBlockReason(target, phase));

        if (s.TeamRadio
            && s.TeamRadioInMeetings
            && TryGetManagedRadioRoute(localPlayer, target, targetRadioState, 1f, out var managedRadio))
            return managedRadio;

        Vector2? meetingListenerPosition = localPlayer?.Position;
        if (TryGetModPairRoute(localPlayer, target, 1f, out var pairRoute, meetingListenerPosition))
            return pairRoute;

        // Third-party mod channel (PerfectComms.Api Primitive 2): local & target sharing a channel
        // hear each other with the channel's audio shape, before built-in team radio. The channel
        // owns its membership (it may deliberately include dead players, e.g. a Medium seance), so
        // this is NOT gated on target/local life state.
        if (TryGetModChannelRoute(localPlayer, target, 1f, out var meetingModChannel, meetingListenerPosition))
            return meetingModChannel;

        if (s.TeamRadio && s.TeamRadioInMeetings && targetRadioActive && !targetDead)
        {
            if (CanHearTeamRadio(localPlayer, target, s, targetRadioChannel))
                return new(0f, 0f, 1f, 0f, VoiceAudioFilterMode.Radio,
                    true, VoiceProximityReason.TeamRadio, 1f);

            // Living non-teammates hard-muted; dead listeners fall through so ghosts still hear them.
            if (!localDead)
                return VoiceProximityResult.Muted(VoiceProximityReason.TeamRadioMuted);
        }

        if (localDead)
        {
            return CalculateLocalDeadHearing(targetDead, s.OnlyGhostsCanTalk, 1f, 1f, 0f);
        }

        if (targetDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.TargetDeadMuted);

        return new(1f, 0f, 0f, 0f, VoiceAudioFilterMode.None,
            true, VoiceProximityReason.MeetingLiving, 1f);
    }

    // Meeting radio routing requires the host opt-in; when off, radio is ignored and teammates
    // are heard via normal meeting audibility (no private meeting channel).

    // Public entry. Applies Town of Us control-hearing for the LOCAL player, then defers to CalculateTaskPhaseSingle:
    // Registered listener origins can replace the local origin or add a second hearing point.
    public static VoiceProximityResult CalculateTaskPhase(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot? targetPlayer,
        Vector2? listenerPos,
        float localLightRadius,
        int mapId,
        bool cameraViewActive,
        int activeCameraIndex,
        Vector2? activeCameraPosition,
        IReadOnlyList<VoiceChatRoom.SpeakerCache> speakers,
        IReadOnlyList<IVoiceComponent> virtualMics,
        bool localInVent,
        bool targetRadioActive,
        bool commsSabActive,
        float previousWallCoefficient,
        VoiceTeamRadioChannel targetRadioChannel = VoiceTeamRadioChannel.All,
        string targetManagedRadioKey = "")
    {
        var mode = localPlayer.HasValue ? localPlayer.Value.ControlHearingMode : VoiceControlHearingMode.None;
        if (mode is VoiceControlHearingMode.ExternalReplace or VoiceControlHearingMode.ExternalAdditive)
        {
            float overrideLightRadius = localPlayer!.Value.ControlledVictimLightRadius < 0f
                ? localLightRadius
                : localPlayer.Value.ControlledVictimLightRadius;
            if (mode == VoiceControlHearingMode.ExternalReplace)
                return CalculateTaskPhaseSingle(localPlayer, targetPlayer, localPlayer.Value.ControlledVictimPosition,
                    overrideLightRadius, mapId, cameraViewActive, activeCameraIndex,
                    activeCameraPosition, speakers, virtualMics, localInVent, targetRadioActive, commsSabActive,
                    previousWallCoefficient, targetRadioChannel, targetManagedRadioKey);

            var fromSelf = CalculateTaskPhaseSingle(localPlayer, targetPlayer, listenerPos, localLightRadius, mapId,
                cameraViewActive, activeCameraIndex, activeCameraPosition, speakers, virtualMics, localInVent,
                targetRadioActive, commsSabActive, previousWallCoefficient, targetRadioChannel, targetManagedRadioKey);
            var fromOverride = CalculateTaskPhaseSingle(
                localPlayer,
                targetPlayer,
                localPlayer.Value.ControlledVictimPosition,
                overrideLightRadius,
                mapId,
                cameraViewActive,
                activeCameraIndex,
                activeCameraPosition,
                speakers,
                virtualMics,
                localInVent,
                targetRadioActive,
                commsSabActive,
                previousWallCoefficient,
                targetRadioChannel,
                targetManagedRadioKey);
            return Louder(fromSelf, fromOverride);
        }
        return CalculateTaskPhaseSingle(localPlayer, targetPlayer, listenerPos, localLightRadius, mapId,
            cameraViewActive, activeCameraIndex, activeCameraPosition, speakers, virtualMics, localInVent,
            targetRadioActive, commsSabActive, previousWallCoefficient, targetRadioChannel, targetManagedRadioKey);
    }

    // Picks the more-audible of the player's normal origin and a registered additive listener origin.
    // Ties favour the first (own-body) result.
    private static VoiceProximityResult Louder(VoiceProximityResult a, VoiceProximityResult b)
    {
        float la = a.NormalVolume + a.GhostVolume + a.RadioVolume;
        float lb = b.NormalVolume + b.GhostVolume + b.RadioVolume;
        return lb > la ? b : a;
    }

    private static VoiceProximityResult CalculateTaskPhaseSingle(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot? targetPlayer,
        Vector2? listenerPos,
        float localLightRadius,
        int mapId,
        bool cameraViewActive,
        int activeCameraIndex,
        Vector2? activeCameraPosition,
        IReadOnlyList<VoiceChatRoom.SpeakerCache> speakers,
        IReadOnlyList<IVoiceComponent> virtualMics,
        bool localInVent,
        bool targetRadioActive,
        bool commsSabActive,
        float previousWallCoefficient,
        VoiceTeamRadioChannel targetRadioChannel = VoiceTeamRadioChannel.All,
        string targetManagedRadioKey = "")
    {
        if (!targetPlayer.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.Unmapped, previousWallCoefficient);
        var target = targetPlayer.Value;
        if (IsUnavailableTarget(target))
            return VoiceProximityResult.Muted(VoiceProximityReason.TargetUnavailable, previousWallCoefficient);
        if (!listenerPos.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        var s = VoiceRoomSettingsState.Current;
        var targetPos = target.Position;
        var localListenerPos = listenerPos.Value;
        Vector2 cameraPosition = default;
        bool hasCameraProxy = s.CameraCanHear && VoiceAudioOcclusion.TryGetCameraListenerPosition(
            mapId,
            cameraViewActive,
            activeCameraIndex,
            activeCameraPosition,
            targetPos,
            out cameraPosition);
        bool localDead = localPlayer?.IsDead == true;
        bool targetDead = target.IsDead;
        var targetRadioState = targetRadioChannel == VoiceTeamRadioChannel.External
            ? VoiceRadioState.Managed(targetManagedRadioKey)
            : targetRadioActive
                ? VoiceRadioState.BuiltIn(targetRadioChannel)
                : VoiceRadioState.None;
        bool localImp = localPlayer?.IsImpostor == true;
        bool targetImp = target.IsImpostor;
        bool targetInVent = target.InVent;
        bool localBypassesTaskVoiceGates =
            localPlayer?.External.ListenerBypassTaskVoiceGates == true;

        if (ShouldMeetingLobbyOnlyBlockTaskVoice(s, localDead, targetDead))
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyMeetingOrLobby, previousWallCoefficient);

        // Host-enforced speaker mutes remain authoritative over every private API route.
        if (VoiceRoleMuteState.IsTaskVoiceBlocked(target))
            return VoiceProximityResult.Muted(VoiceRoleMuteState.GetTaskBlockReason(target), previousWallCoefficient);

        bool taskRadioAllowed = !s.TeamRadioInMeetings || s.TeamRadioInTasks;
        if (s.TeamRadio
            && taskRadioAllowed
            && TryGetManagedRadioRoute(
                localPlayer,
                target,
                targetRadioState,
                previousWallCoefficient,
                out var managedRadio))
            return managedRadio;

        if (TryGetModPairRoute(
                localPlayer,
                target,
                previousWallCoefficient,
                out var pairRoute,
                localListenerPos))
            return pairRoute;


        if (s.OnlyGhostsCanTalk && !localDead && !localBypassesTaskVoiceGates)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk, previousWallCoefficient);
        if (commsSabActive && s.CommsSabDisables && !localDead && !localBypassesTaskVoiceGates)
            return VoiceProximityResult.Muted(VoiceProximityReason.CommsSabotage, previousWallCoefficient);

        // Third-party mod channel (PerfectComms.Api Primitive 2), before built-in team radio. The
        // channel owns its membership (may deliberately include dead players, e.g. a Medium seance),
        // so this is NOT gated on target/local life state. Passes the listener position so a spatial
        // (Origin-carrying) channel is heard from its point with falloff.
        if (TryGetModChannelRoute(localPlayer, target, previousWallCoefficient, out var taskModChannel, localListenerPos))
            return taskModChannel;

        // Task-phase team radio is gated by the "Usable in Tasks" sub-toggle ONLY when the meeting/lobby radio
        // option is on; when that parent is off the sub-toggle does nothing and radio stays task-usable.
        taskRadioAllowed = !s.TeamRadioInMeetings || s.TeamRadioInTasks;
        if (s.TeamRadio && taskRadioAllowed && targetRadioActive && !targetDead)
        {
            if (CanHearTeamRadio(localPlayer, target, s, targetRadioChannel))
                return new(0f, 0f, 1f, 0f, VoiceAudioFilterMode.Radio,
                    true, VoiceProximityReason.TeamRadio, previousWallCoefficient);

            // Living non-teammates hard-muted; dead listeners fall through to proximity below.
            if (!localDead)
                return VoiceProximityResult.Muted(VoiceProximityReason.TeamRadioMuted, previousWallCoefficient);
        }

        if (localDead)
        {
            if (targetDead)
                return CalculateLocalDeadGhostHearing(targetPos, localListenerPos, localLightRadius, s, previousWallCoefficient);
            if (s.OnlyGhostsCanTalk)
                return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk, previousWallCoefficient);
        }

        if (localImp && targetDead && !target.IsSpectator && s.ImpostorHearGhosts)
        {
            float ghostDist = Distance(targetPos, localListenerPos);
            float ghostVolume = VoiceAudioOcclusion.ApplyFalloff(
                ghostDist,
                s.MaxChatDistance,
                (VoiceFalloffMode)s.FalloffMode);
            float ghostPan = VoiceChatRoom.GetPan(localListenerPos.x, targetPos.x);
            return new(0f, ghostVolume, 0f, ghostPan, VoiceAudioFilterMode.Ghost,
                ghostVolume > 0f,
                VoiceProximityReason.ImpostorHearsGhost,
                previousWallCoefficient);
        }

        if (targetDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.TargetDeadMuted, previousWallCoefficient);

        if (s.VentPrivateChat && (localInVent || targetInVent))
        {
            if (targetInVent && !localInVent)
                return VoiceProximityResult.Muted(VoiceProximityReason.VentPrivateMuted, previousWallCoefficient);
        }

        if (targetInVent && !s.VentPrivateChat)
        {
            if (!targetImp || !s.HearInVent)
                return VoiceProximityResult.Muted(VoiceProximityReason.VentMuted, previousWallCoefficient);
        }

        float maxDistance = s.MaxChatDistance;
        bool listenerSightObscured = localPlayer?.External.ListenerSightObscured == true;
        if (s.OnlyHearInSight)
            maxDistance = ResolveSightLimitedMaxDistance(maxDistance, localLightRadius, listenerSightObscured);

        float dist = Distance(targetPos, localListenerPos);
        float volume = VoiceAudioOcclusion.ApplyFalloff(dist, maxDistance, (VoiceFalloffMode)s.FalloffMode);
        float pan = VoiceChatRoom.GetPan(localListenerPos.x, targetPos.x);

        bool sightBlocked = false;
        if (s.OnlyHearInSight)
        {
            bool inSight = VoiceAudioOcclusion.Inspect(localListenerPos, targetPos).InSight;
            if (!inSight || dist > maxDistance)
            {
                volume = 0f;
                sightBlocked = true;
            }
        }

        float wallCoefficient = previousWallCoefficient;
        VoiceAudioFilterMode filterMode = VoiceAudioFilterMode.None;
        if (volume > 0f && s.WallsBlockSound)
        {
            var occlusion = VoiceAudioOcclusion.Evaluate(
                localListenerPos,
                targetPos,
                (VoiceOcclusionMode)s.OcclusionMode);

            if (occlusion.TargetVolumeMultiplier <= 0f && occlusion.IsOccluded)
            {
                var hardOcclusionVirtualRoute = CalculateVirtualRoute(target, targetPos, speakers, virtualMics, previousWallCoefficient);
                if (hardOcclusionVirtualRoute.Audible)
                    return hardOcclusionVirtualRoute;
                if (hasCameraProxy)
                    return CalculateCameraProxy(targetPos, cameraPosition, s, previousWallCoefficient);
                // Smooth hard occlusion toward silence instead of an instant cut at every wall edge.
                wallCoefficient += (0f - wallCoefficient) * Math.Clamp(Time.deltaTime * 8f, 0f, 1f);
                if (wallCoefficient < 0.02f)
                    return VoiceProximityResult.Muted(VoiceProximityReason.HardOcclusion, 0f);
                filterMode = VoiceAudioFilterMode.WallMuffle;
            }
            else
            {
                wallCoefficient += (occlusion.TargetVolumeMultiplier - wallCoefficient) *
                                   Math.Clamp(Time.deltaTime * 4f, 0f, 1f);
                filterMode = occlusion.FilterMode;
            }
        }
        else
        {
            wallCoefficient = 1f;
        }

        float finalVolume = volume * wallCoefficient;
        if (finalVolume < LowVolumeFloor)
            finalVolume = 0f;
        var virtualRoute = CalculateVirtualRoute(target, targetPos, speakers, virtualMics, previousWallCoefficient);
        VoiceProximityReason proximityReason = sightBlocked
            ? VoiceProximityReason.SightBlocked
            : (localDead ? VoiceProximityReason.LocalDeadHearsLiving : VoiceProximityReason.Proximity);
        var proximityRoute = new VoiceProximityResult(finalVolume, 0f, 0f, pan, filterMode,
            finalVolume > 0f,
            proximityReason,
            wallCoefficient);
        var cameraRoute = hasCameraProxy
            ? CalculateCameraProxy(targetPos, cameraPosition, s, previousWallCoefficient)
            : VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        return SelectBestNormalRoute(proximityRoute, virtualRoute, cameraRoute);
    }

    private static bool ShouldMeetingLobbyOnlyBlockTaskVoice(
        VoiceRoomSettingsSnapshot settings,
        bool localDead,
        bool targetDead)
        => settings.OnlyMeetingOrLobby &&
           (settings.OnlyMeetingOrLobbyAffectsGhosts || !localDead || !targetDead);

    private static bool TryGetModPairRoute(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot target,
        float wallCoefficient,
        out VoiceProximityResult result,
        Vector2? listenerPos = null)
    {
        result = default;
        if (!localPlayer.HasValue) return false;

        ExternalVoicePairState pair = target.External.Pair;
        if (pair.Verdict == VoicePairVerdict.Mute)
        {
            result = VoiceProximityResult.Muted(VoiceProximityReason.RoleMuted, wallCoefficient);
            return true;
        }
        if (pair.Verdict != VoicePairVerdict.Route)
            return false;

        float volume = Math.Clamp(pair.Volume, 0f, 1f);
        if ((VoicePairRouteShape)pair.Shape == VoicePairRouteShape.Radio)
        {
            result = new(0f, 0f, volume, 0f, VoiceAudioFilterMode.Radio,
                volume > 0f, VoiceProximityReason.ModPairRoute, wallCoefficient);
            return true;
        }

        Vector2 source = pair.HasSpeakerOrigin ? pair.SpeakerOrigin : target.Position;
        Vector2 listener = pair.HasListenerOrigin
            ? pair.ListenerOrigin
            : listenerPos ?? localPlayer.Value.Position;
        var settings = VoiceRoomSettingsState.Current;
        float distance = Distance(source, listener);
        float spatial = VoiceAudioOcclusion.ApplyFalloff(
            distance,
            settings.MaxChatDistance,
            (VoiceFalloffMode)settings.FalloffMode) * volume;
        if (spatial < LowVolumeFloor) spatial = 0f;
        float pan = VoiceChatRoom.GetPan(listener.x, source.x);
        bool ghost = (VoicePairRouteShape)pair.Shape == VoicePairRouteShape.Ghost;
        result = ghost
            ? new(0f, spatial, 0f, pan, VoiceAudioFilterMode.Ghost,
                spatial > 0f, VoiceProximityReason.ModPairRoute, wallCoefficient)
            : new(spatial, 0f, 0f, pan, VoiceAudioFilterMode.None,
                spatial > 0f, VoiceProximityReason.ModPairRoute, wallCoefficient);
        return true;
    }

    // Every matching membership is considered. TwoWay=false is a receive-only membership, so a
    // target with that membership cannot transmit through it but can hear another transmitting member.
    private static bool TryGetModChannelRoute(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot target,
        float wallCoefficient,
        out VoiceProximityResult result,
        Vector2? listenerPos = null)
    {
        result = default;
        if (!localPlayer.HasValue) return false;
        var local = localPlayer.Value;
        ExternalVoiceChannelState[]? localChannels = local.External.Channels;
        ExternalVoiceChannelState[]? targetChannels = target.External.Channels;
        if (localChannels == null || targetChannels == null) return false;

        bool found = false;
        VoiceProximityResult best = default;
        for (int targetIndex = 0; targetIndex < targetChannels.Length; targetIndex++)
        {
            ExternalVoiceChannelState targetChannel = targetChannels[targetIndex];
            if (!targetChannel.CanTransmit || string.IsNullOrEmpty(targetChannel.Key)) continue;

            bool shared = false;
            for (int localIndex = 0; localIndex < localChannels.Length; localIndex++)
            {
                if (!string.Equals(
                        localChannels[localIndex].Key,
                        targetChannel.Key,
                        StringComparison.Ordinal))
                    continue;
                shared = true;
                break;
            }
            if (!shared) continue;

            float volume = Math.Clamp(targetChannel.Volume, 0f, 1f);
            var filter = VoiceModBridge.ToFilterMode(targetChannel.Shape);
            if (filter == VoiceAudioFilterMode.None)
            {
                Vector2 source = targetChannel.HasOrigin ? targetChannel.Origin : target.Position;
                Vector2 listener = listenerPos ?? local.Position;
                var settings = VoiceRoomSettingsState.Current;
                float distance = Distance(source, listener);
                float spatial = VoiceAudioOcclusion.ApplyFalloff(
                    distance,
                    settings.MaxChatDistance,
                    (VoiceFalloffMode)settings.FalloffMode) * volume;
                if (spatial < LowVolumeFloor) spatial = 0f;
                float spatialPan = VoiceChatRoom.GetPan(listener.x, source.x);
                var candidate = new VoiceProximityResult(
                    spatial, 0f, 0f, spatialPan, VoiceAudioFilterMode.None,
                    spatial > 0f, VoiceProximityReason.ModChannel, wallCoefficient);
                if (!found || IsLouder(candidate, best)) best = candidate;
                found = true;
                continue;
            }

            VoiceProximityResult flatCandidate = filter switch
            {
                VoiceAudioFilterMode.Radio => new(0f, 0f, volume, 0f,
                    VoiceAudioFilterMode.Radio, volume > 0f, VoiceProximityReason.ModChannel, wallCoefficient),
                VoiceAudioFilterMode.ListenerMuffle => new(volume, 0f, 0f, 0f,
                    VoiceAudioFilterMode.ListenerMuffle, volume > 0f, VoiceProximityReason.ModChannel, wallCoefficient),
                _ => new(volume, 0f, 0f, 0f,
                    VoiceAudioFilterMode.None, volume > 0f, VoiceProximityReason.ModChannel, wallCoefficient),
            };
            if (!found || IsLouder(flatCandidate, best)) best = flatCandidate;
            found = true;
        }

        result = best;
        return found;
    }

    private static bool IsLouder(VoiceProximityResult candidate, VoiceProximityResult current)
        => candidate.NormalVolume + candidate.GhostVolume + candidate.RadioVolume
           > current.NormalVolume + current.GhostVolume + current.RadioVolume;

    private static bool TryGetManagedRadioRoute(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot target,
        VoiceRadioState targetRadioState,
        float wallCoefficient,
        out VoiceProximityResult result)
    {
        result = default;
        if (targetRadioState.Channel != VoiceTeamRadioChannel.External || !targetRadioState.IsActive)
            return false;
        if (target.IsDead)
            return false;

        // A claimed external transmit key is valid only while the speaker's current resolved
        // memberships contain it. This prevents a stale or forged radio RPC from opening a route.
        if (!HasManagedRadioMembership(target, targetRadioState.ManagedKey))
        {
            result = VoiceProximityResult.Muted(VoiceProximityReason.TeamRadioMuted, wallCoefficient);
            return true;
        }

        // Preserve the existing Team Radio ghost policy: dead listeners fall through to their
        // normal all-hearing route instead of being constrained to living private channels.
        if (localPlayer?.IsDead == true)
            return false;

        if (localPlayer.HasValue
            && HasManagedRadioMembership(localPlayer.Value, targetRadioState.ManagedKey))
        {
            result = new VoiceProximityResult(
                0f,
                0f,
                1f,
                0f,
                VoiceAudioFilterMode.Radio,
                true,
                VoiceProximityReason.TeamRadio,
                wallCoefficient);
            return true;
        }

        result = VoiceProximityResult.Muted(VoiceProximityReason.TeamRadioMuted, wallCoefficient);
        return true;
    }

    private static bool HasManagedRadioMembership(VoicePlayerSnapshot player, string key)
    {
        ExternalVoiceManagedRadioState[]? channels = player.External.ManagedRadioChannels;
        if (channels == null) return false;
        for (var i = 0; i < channels.Length; i++)
            if (string.Equals(channels[i].Key, key, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool CanHearTeamRadio(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot target,
        VoiceRoomSettingsSnapshot settings,
        VoiceTeamRadioChannel targetRadioChannel)
    {
        if (!localPlayer.HasValue)
            return false;

        var local = localPlayer.Value;
        return VoiceTeamRadioChannels.Normalize(targetRadioChannel) switch
        {
            VoiceTeamRadioChannel.Impostors or VoiceTeamRadioChannel.All =>
                settings.TeamRadioImpostors && local.IsImpostor && target.IsImpostor,
            _ => false,
        };
    }

    internal static bool IsUnavailableTarget(VoicePlayerSnapshot target)
        => target.Disconnected || target.IsDummy || !target.IsVisible;


    internal static VoiceProximityResult ApplyExternalAudioEffects(
        VoiceProximityResult result,
        VoicePlayerSnapshot? targetPlayer,
        VoiceGamePhase? phase = null)
    {
        // The EndGame roster is transition-retained and its per-player callbacks cannot be
        // re-resolved after PlayerControls disappear. Keep the fresh results-screen group call
        // free of stale task/meeting mute, muffle, and pair state.
        if (phase == VoiceGamePhase.EndGame)
            return result;
        if (!targetPlayer.HasValue)
            return result;

        ExternalVoiceState external = targetPlayer.Value.External;
        if (external.Muted || external.Pair.Verdict == VoicePairVerdict.Mute)
            return VoiceProximityResult.Muted(VoiceProximityReason.RoleMuted, result.WallCoefficient);

        if (result.Audible && (external.Muffled || external.Pair.Muffled))
            return result with { FilterMode = VoiceAudioFilterMode.ListenerMuffle };

        return result;
    }

    private static float ResolveSightLimitedMaxDistance(
        float maxDistance,
        float localLightRadius,
        bool listenerBlindedOrFlashed)
    {
        if (!listenerBlindedOrFlashed)
        {
            if (localLightRadius > 0f)
            {
                _lastUnimpairedLocalLightRadius = localLightRadius;
                return Math.Min(maxDistance, localLightRadius);
            }

            return maxDistance;
        }

        float referenceRadius = _lastUnimpairedLocalLightRadius > 0f
            ? _lastUnimpairedLocalLightRadius
            : VoiceRoomSettingsSnapshot.Defaults.MaxChatDistance;
        if (localLightRadius > 0f)
            referenceRadius = Math.Min(referenceRadius, localLightRadius);

        referenceRadius = Math.Clamp(
            referenceRadius,
            VoiceRoomSettingsSnapshot.MinChatDistance,
            VoiceRoomSettingsSnapshot.MaxChatDistanceLimit);
        return Math.Min(maxDistance, referenceRadius);
    }


    private static VoiceProximityResult CalculateVirtualRoute(
        VoicePlayerSnapshot target,
        Vector2 targetPos,
        IReadOnlyList<VoiceChatRoom.SpeakerCache> speakers,
        IReadOnlyList<IVoiceComponent> virtualMics,
        float previousWallCoefficient)
    {
        // Index with for-loops over IReadOnlyList rather than foreach over IEnumerable: the latter
        // boxes a heap enumerator per call (twice, nested) even when the lists are empty, and this
        // runs per peer per frame. Short-circuit the common empty case up front.
        int speakerCount = speakers.Count;
        int micCount = virtualMics.Count;
        if (speakerCount == 0 || micCount == 0)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        float bestVolume = 0f;
        float bestPan = 0f;

        for (int si = 0; si < speakerCount; si++)
        {
            var speaker = speakers[si];
            if (speaker.Volume <= 0f || speaker.Speaker.Volume <= 0f) continue;

            for (int mi = 0; mi < micCount; mi++)
            {
                var mic = virtualMics[mi];
                if (mic.Volume <= 0f || mic.Radious <= 0f) continue;
                if (!speaker.Speaker.CanPlaySoundFrom(mic)) continue;

                float micCatch = Math.Clamp(mic.CanCatch(target, targetPos), 0f, 1f);
                if (micCatch <= 0f) continue;

                float volume = micCatch * mic.Volume * speaker.Volume * speaker.Speaker.Volume;
                if (volume <= bestVolume) continue;

                bestVolume = volume;
                bestPan = speaker.Pan;
            }
        }

        if (bestVolume <= 0f)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        return new(Math.Clamp(bestVolume, 0f, 1f), 0f, 0f, bestPan, VoiceAudioFilterMode.None,
            true, VoiceProximityReason.Proximity, previousWallCoefficient);
    }

    private static VoiceProximityResult CalculateLocalDeadHearing(
        bool targetDead,
        bool onlyGhostsCanTalk,
        float wallCoefficient,
        float volume,
        float pan)
    {
        if (onlyGhostsCanTalk && !targetDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk, wallCoefficient);

        return new(volume, 0f, 0f, pan, VoiceAudioFilterMode.None,
            true,
            targetDead ? VoiceProximityReason.LocalDeadHearsGhost : VoiceProximityReason.LocalDeadHearsLiving,
            wallCoefficient);
    }

    private static VoiceProximityResult CalculateLocalDeadGhostHearing(
        Vector2 targetPos,
        Vector2 listenerPos,
        float localLightRadius,
        VoiceRoomSettingsSnapshot s,
        float wallCoefficient)
    {
        // Ghosts hear each other at any distance when enabled: full volume, no falloff (walls still apply).
        if (s.GhostsHearEachOtherUnlimited)
        {
            float fullPan = VoiceChatRoom.GetPan(listenerPos.x, targetPos.x);
            return CalculateLocalDeadHearing(true, s.OnlyGhostsCanTalk, wallCoefficient, 1f, fullPan);
        }

        float maxDistance = ResolveGhostHearingDistance(localLightRadius, s.MaxChatDistance);
        float dx = targetPos.x - listenerPos.x;
        float dy = targetPos.y - listenerPos.y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float volume = VoiceAudioOcclusion.ApplyFalloff(distance, maxDistance, (VoiceFalloffMode)s.FalloffMode);
        if (volume <= 0f)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener, wallCoefficient);

        float pan = VoiceChatRoom.GetPan(listenerPos.x, targetPos.x);
        return CalculateLocalDeadHearing(true, s.OnlyGhostsCanTalk, wallCoefficient, volume, pan);
    }

    private static float ResolveGhostHearingDistance(float localLightRadius, float fallbackDistance)
    {
        if (localLightRadius > 0f)
            return Math.Clamp(
                localLightRadius * GhostVisionRangeMultiplier,
                VoiceRoomSettingsSnapshot.MinChatDistance,
                VoiceRoomSettingsSnapshot.MaxChatDistanceLimit);

        return fallbackDistance;
    }

    private static VoiceProximityResult CalculateCameraProxy(
        Vector2 targetPos,
        Vector2 cameraPosition,
        VoiceRoomSettingsSnapshot s,
        float previousWallCoefficient)
    {
        float cameraRange = s.MaxChatDistance;
        float cameraDist = Distance(targetPos, cameraPosition);
        float cameraVolume = VoiceAudioOcclusion.ApplyFalloff(cameraDist, cameraRange, (VoiceFalloffMode)s.FalloffMode) * 0.8f;
        if (cameraVolume < LowVolumeFloor)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        float pan = VoiceChatRoom.GetPan(cameraPosition.x, targetPos.x);
        return new(cameraVolume, 0f, 0f, pan, VoiceAudioFilterMode.WallMuffle,
            true, VoiceProximityReason.CameraProxy, previousWallCoefficient);
    }

    private static VoiceProximityResult SelectBestNormalRoute(
        VoiceProximityResult proximityRoute,
        VoiceProximityResult virtualRoute,
        VoiceProximityResult cameraRoute)
    {
        var best = proximityRoute;
        if (virtualRoute.Audible && virtualRoute.NormalVolume > best.NormalVolume)
            best = virtualRoute;
        if (cameraRoute.Audible && cameraRoute.NormalVolume > best.NormalVolume)
            best = cameraRoute;
        return best;
    }

    private static float Distance(Vector2 a, Vector2 b)
    {
        float dx = a.x - b.x;
        float dy = a.y - b.y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
