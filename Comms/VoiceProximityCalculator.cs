using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceProximityCalculator
{
    private const float GhostVisionRangeMultiplier = 1f;

    public static VoiceProximityResult CalculateLobby(
        VoicePlayerSnapshot? targetPlayer,
        Vector2? listenerPos)
    {
        if (!targetPlayer.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);
        if (!listenerPos.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener);

        var s = VoiceRoomSettingsState.Current;
        var target = targetPlayer.Value;
        float maxDistance = s.MaxChatDistance;
        float dist = Distance(target.Position, listenerPos.Value);
        float volume = VoiceAudioOcclusion.ApplyFalloff(dist, maxDistance, (VoiceFalloffMode)s.FalloffMode);
        if (volume < 0.06f)
            volume = 0f;
        float pan = VoiceChatRoom.GetPan(listenerPos.Value.x, target.Position.x);

        return new(volume, 0f, 0f, pan, VoiceAudioFilterMode.None,
            volume > 0f, VoiceProximityReason.Lobby, 1f);
    }

    public static VoiceProximityResult CalculateMeeting(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot? targetPlayer,
        bool targetRadioActive)
    {
        if (!targetPlayer.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);

        var s = VoiceRoomSettingsState.Current;
        var target = targetPlayer.Value;
        bool localDead = localPlayer?.IsDead == true;
        bool targetDead = target.IsDead;
        bool localImp = localPlayer?.IsImpostor == true;
        bool targetImp = target.IsImpostor;

        if (s.OnlyGhostsCanTalk && !localDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk);

        if (s.ImpostorPrivateRadio && targetRadioActive && targetImp && !targetDead)
        {
            if (localImp)
                return new(0f, 0f, 1f, 0f, VoiceAudioFilterMode.Radio,
                    true, VoiceProximityReason.ImpostorRadio, 1f);

            return VoiceProximityResult.Muted(VoiceProximityReason.ImpostorRadioMuted);
        }

        if (VoiceRoleMuteState.IsMeetingVoiceBlocked(target))
            return VoiceProximityResult.Muted(VoiceRoleMuteState.GetMeetingBlockReason(target));

        if (localDead)
            return CalculateLocalDeadHearing(targetDead, s.OnlyGhostsCanTalk, 1f, 1f, 0f);

        if (targetDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.TargetDeadMuted);

        return new(1f, 0f, 0f, 0f, VoiceAudioFilterMode.None,
            true, VoiceProximityReason.MeetingLiving, 1f);
    }

    public static VoiceProximityResult CalculateTaskPhase(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot? targetPlayer,
        Vector2? listenerPos,
        float localLightRadius,
        int mapId,
        bool cameraViewActive,
        int activeCameraIndex,
        Vector2? activeCameraPosition,
        IEnumerable<VoiceChatRoom.SpeakerCache> speakers,
        IEnumerable<IVoiceComponent> virtualMics,
        bool localInVent,
        bool targetRadioActive,
        bool commsSabActive,
        float previousWallCoefficient)
    {
        if (!targetPlayer.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.Unmapped, previousWallCoefficient);
        if (!listenerPos.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        var s = VoiceRoomSettingsState.Current;
        var target = targetPlayer.Value;
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
        bool localImp = localPlayer?.IsImpostor == true;
        bool targetImp = target.IsImpostor;
        bool targetInVent = target.InVent;

        if (s.OnlyMeetingOrLobby)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyMeetingOrLobby, previousWallCoefficient);
        if (s.OnlyGhostsCanTalk && !localDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk, previousWallCoefficient);
        if (commsSabActive && s.CommsSabDisables && !localDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.CommsSabotage, previousWallCoefficient);

        if (s.ImpostorPrivateRadio && targetRadioActive && targetImp && !targetDead)
        {
            if (localImp)
                return new(0f, 0f, 1f, 0f, VoiceAudioFilterMode.Radio,
                    true, VoiceProximityReason.ImpostorRadio, previousWallCoefficient);

            return VoiceProximityResult.Muted(VoiceProximityReason.ImpostorRadioMuted, previousWallCoefficient);
        }

        if (localDead)
        {
            if (targetDead)
                return CalculateLocalDeadGhostHearing(targetPos, localListenerPos, localLightRadius, s, previousWallCoefficient);
            if (s.OnlyGhostsCanTalk)
                return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk, previousWallCoefficient);
        }

        if (localImp && targetDead && s.ImpostorHearGhosts)
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
        if (s.OnlyHearInSight && localLightRadius > 0f)
            maxDistance = Math.Min(maxDistance, localLightRadius);

        float dist = Distance(targetPos, localListenerPos);
        float volume = VoiceAudioOcclusion.ApplyFalloff(dist, maxDistance, (VoiceFalloffMode)s.FalloffMode);
        float pan = VoiceChatRoom.GetPan(localListenerPos.x, targetPos.x);

        if (s.OnlyHearInSight)
        {
            bool inSight = VoiceAudioOcclusion.Inspect(localListenerPos, targetPos).InSight;
            if (!inSight || dist > maxDistance)
                volume = 0f;
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
                return VoiceProximityResult.Muted(VoiceProximityReason.HardOcclusion, wallCoefficient);
            }

            wallCoefficient += (occlusion.TargetVolumeMultiplier - wallCoefficient) *
                               Math.Clamp(Time.deltaTime * 4f, 0f, 1f);
            filterMode = occlusion.FilterMode;
        }
        else
        {
            wallCoefficient = 1f;
        }

        float finalVolume = volume * wallCoefficient;
        var virtualRoute = CalculateVirtualRoute(target, targetPos, speakers, virtualMics, previousWallCoefficient);
        var proximityRoute = new VoiceProximityResult(finalVolume, 0f, 0f, pan, filterMode,
            finalVolume > 0f,
            localDead ? VoiceProximityReason.LocalDeadHearsLiving : VoiceProximityReason.Proximity,
            wallCoefficient);
        var cameraRoute = hasCameraProxy
            ? CalculateCameraProxy(targetPos, cameraPosition, s, previousWallCoefficient)
            : VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        return SelectBestNormalRoute(proximityRoute, virtualRoute, cameraRoute);
    }

    private static VoiceProximityResult CalculateVirtualRoute(
        VoicePlayerSnapshot target,
        Vector2 targetPos,
        IEnumerable<VoiceChatRoom.SpeakerCache> speakers,
        IEnumerable<IVoiceComponent> virtualMics,
        float previousWallCoefficient)
    {
        float bestVolume = 0f;
        float bestPan = 0f;

        foreach (var speaker in speakers)
        {
            if (speaker.Volume <= 0f || speaker.Speaker.Volume <= 0f) continue;

            foreach (var mic in virtualMics)
            {
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
        float maxDistance = ResolveGhostHearingDistance(localLightRadius, s.MaxChatDistance);
        float dx = targetPos.x - listenerPos.x;
        float dy = targetPos.y - listenerPos.y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float volume = ApplyGhostFalloff(distance, maxDistance, (VoiceFalloffMode)s.FalloffMode);
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

    private static float ApplyGhostFalloff(float distance, float maxDistance, VoiceFalloffMode mode)
    {
        if (maxDistance <= 0f) return 0f;
        float t = Math.Clamp(distance / maxDistance, 0f, 1f);
        return mode switch
        {
            VoiceFalloffMode.Smooth => 1f - t * t * (3f - 2f * t),
            VoiceFalloffMode.VoiceFocused => t < 0.35f ? 1f : MathF.Pow(1f - t, 1.35f),
            _ => 1f - t,
        };
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
        if (cameraVolume <= 0f)
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
