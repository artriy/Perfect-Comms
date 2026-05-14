using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceProximityCalculator
{
    public static VoiceProximityResult CalculateLobby(
        VoicePlayerSnapshot? targetPlayer,
        Vector2? listenerPos)
    {
        if (!targetPlayer.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);
        if (!listenerPos.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener);

        var s = VoiceChatGameOptions.Instance;
        var target = targetPlayer.Value;
        float maxDistance = s.MaxChatDistance.Value;
        float dist = Vector2.Distance(target.Position, listenerPos.Value);
        float volume = VoiceAudioOcclusion.ApplyFalloff(dist, maxDistance, (VoiceFalloffMode)s.FalloffMode.Value);
        if (volume < 0.06f) volume = 0f;
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

        var s = VoiceChatGameOptions.Instance;
        var target = targetPlayer.Value;
        bool localDead  = localPlayer?.IsDead == true;
        bool targetDead = target.IsDead;

        // Medium spiritual state: local Medium acts as ghost — hears ghosts, not the living.
        bool localMediumSpiritual = !localDead && VoiceRoleMuteState.IsLocalMediumSpiritual();

        if (s.OnlyGhostsCanTalk.Value && !localDead && !localMediumSpiritual)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk);

        // Team Chat Radio (Impostors + optionally Vampires).
        if (targetRadioActive && VoiceRoleMuteState.CanUseTeamChatRadio(target) && !targetDead)
        {
            bool localCanHearRadio = localPlayer.HasValue
                && VoiceRoleMuteState.CanUseTeamChatRadio(localPlayer.Value)
                && !localDead;

            if (localCanHearRadio)
                return new(0f, 0f, 1f, 0f, VoiceAudioFilterMode.Radio,
                    true, VoiceProximityReason.ImpostorRadio, 1f);

            return VoiceProximityResult.Muted(VoiceProximityReason.ImpostorRadioMuted);
        }

        if (VoiceRoleMuteState.IsMeetingVoiceBlocked(target))
            return VoiceProximityResult.Muted(VoiceRoleMuteState.GetMeetingBlockReason(target));

        // Medium spiritual state: route through ghost channel.
        if (localMediumSpiritual)
            return CalculateLocalDeadHearing(targetDead, s.OnlyGhostsCanTalk.Value, 1f, 1f, 0f);

        if (localDead)
            return CalculateLocalDeadHearing(targetDead, s.OnlyGhostsCanTalk.Value, 1f, 1f, 0f);

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

        var s = VoiceChatGameOptions.Instance;
        var target = targetPlayer.Value;
        var targetPos = target.Position;
        var localListenerPos = listenerPos.Value;
        Vector2 cameraPosition = default;
        bool hasCameraProxy = s.CameraCanHear.Value
                              && VoiceAudioOcclusion.TryGetCameraListenerPosition(targetPos, out cameraPosition);
        bool localDead    = localPlayer?.IsDead == true;
        bool targetDead   = target.IsDead;
        bool targetInVent = target.InVent;

        if (s.OnlyMeetingOrLobby.Value)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyMeetingOrLobby, previousWallCoefficient);
        if (s.OnlyGhostsCanTalk.Value && !localDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk, previousWallCoefficient);
        if (commsSabActive && s.CommsSabDisables.Value && !localDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.CommsSabotage, previousWallCoefficient);

        // Team Chat Radio (task phase).
        if (targetRadioActive && VoiceRoleMuteState.CanUseTeamChatRadio(target) && !targetDead)
        {
            bool localCanHearRadio = localPlayer.HasValue
                && VoiceRoleMuteState.CanUseTeamChatRadio(localPlayer.Value)
                && !localDead;

            if (localCanHearRadio)
                return new(0f, 0f, 1f, 0f, VoiceAudioFilterMode.Radio,
                    true, VoiceProximityReason.ImpostorRadio, previousWallCoefficient);

            return VoiceProximityResult.Muted(VoiceProximityReason.ImpostorRadioMuted, previousWallCoefficient);
        }

        // Parasite victim muted in task phase.
        if (VoiceRoleMuteState.IsTaskPhaseVoiceBlocked(target))
            return VoiceProximityResult.Muted(VoiceProximityReason.Blackmailed, previousWallCoefficient);

        if (localDead)
            return CalculateLocalDeadHearing(targetDead, s.OnlyGhostsCanTalk.Value, previousWallCoefficient, 1f, 0f);

        bool localImp = localPlayer?.IsImpostor == true;
        if (localImp && targetDead && s.ImpostorHearGhosts.Value)
        {
            float ghostDist   = Vector2.Distance(targetPos, localListenerPos);
            float ghostVolume = VoiceAudioOcclusion.ApplyFalloff(ghostDist, s.MaxChatDistance.Value,
                (VoiceFalloffMode)s.FalloffMode.Value);
            float ghostPan    = VoiceChatRoom.GetPan(localListenerPos.x, targetPos.x);
            return new(0f, ghostVolume, 0f, ghostPan, VoiceAudioFilterMode.Ghost,
                ghostVolume > 0f, VoiceProximityReason.ImpostorHearsGhost, previousWallCoefficient);
        }

        if (targetDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.TargetDeadMuted, previousWallCoefficient);

        if (s.VentPrivateChat.Value && (localInVent || targetInVent))
        {
            if (targetInVent && !localInVent)
                return VoiceProximityResult.Muted(VoiceProximityReason.VentPrivateMuted, previousWallCoefficient);
        }

        if (targetInVent && !s.VentPrivateChat.Value)
        {
            if (!target.IsImpostor || !s.HearInVent.Value)
                return VoiceProximityResult.Muted(VoiceProximityReason.VentMuted, previousWallCoefficient);
        }

        float maxDistance = s.MaxChatDistance.Value;
        if (s.OnlyHearInSight.Value && localLightRadius > 0f)
            maxDistance = Math.Min(maxDistance, localLightRadius);

        float dist   = Vector2.Distance(targetPos, localListenerPos);
        float volume = VoiceAudioOcclusion.ApplyFalloff(dist, maxDistance, (VoiceFalloffMode)s.FalloffMode.Value);
        float pan    = VoiceChatRoom.GetPan(localListenerPos.x, targetPos.x);

        if (s.OnlyHearInSight.Value)
        {
            bool inSight = VoiceAudioOcclusion.Inspect(localListenerPos, targetPos).InSight;
            if (!inSight || dist > maxDistance) volume = 0f;
        }

        float wallCoefficient = previousWallCoefficient;
        VoiceAudioFilterMode filterMode = VoiceAudioFilterMode.None;
        if (volume > 0f && s.WallsBlockSound.Value)
        {
            var occlusion = VoiceAudioOcclusion.Evaluate(
                localListenerPos, targetPos, (VoiceOcclusionMode)s.OcclusionMode.Value);

            if (occlusion.TargetVolumeMultiplier <= 0f && occlusion.IsOccluded)
            {
                var hardRoute = CalculateVirtualRoute(target, targetPos, speakers, virtualMics, previousWallCoefficient);
                if (hardRoute.Audible) return hardRoute;
                if (hasCameraProxy) return CalculateCameraProxy(targetPos, cameraPosition, s, previousWallCoefficient);
                return VoiceProximityResult.Muted(VoiceProximityReason.HardOcclusion, wallCoefficient);
            }

            wallCoefficient += (occlusion.TargetVolumeMultiplier - wallCoefficient)
                               * Math.Clamp(Time.deltaTime * 4f, 0f, 1f);
            filterMode = occlusion.FilterMode;
        }
        else
        {
            wallCoefficient = 1f;
        }

        float finalVolume = volume * wallCoefficient;
        var virtualRoute  = CalculateVirtualRoute(target, targetPos, speakers, virtualMics, previousWallCoefficient);
        if (virtualRoute.Audible && virtualRoute.NormalVolume > finalVolume)
            return virtualRoute;

        if (finalVolume <= 0f && hasCameraProxy)
            return CalculateCameraProxy(targetPos, cameraPosition, s, previousWallCoefficient);

        return new(finalVolume, 0f, 0f, pan, filterMode,
            finalVolume > 0f, VoiceProximityReason.Proximity, wallCoefficient);
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    private static VoiceProximityResult CalculateLocalDeadHearing(
        bool targetDead,
        bool onlyGhostsCanTalk,
        float wallCoefficient,
        float volume,
        float pan)
    {
        if (onlyGhostsCanTalk && !targetDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk, wallCoefficient);

        return new(0f, volume, 0f, pan, VoiceAudioFilterMode.Ghost,
            true,
            targetDead ? VoiceProximityReason.LocalDeadHearsGhost : VoiceProximityReason.LocalDeadHearsLiving,
            wallCoefficient);
    }

    private static VoiceProximityResult CalculateVirtualRoute(
        VoicePlayerSnapshot target,
        Vector2 targetPos,
        IEnumerable<VoiceChatRoom.SpeakerCache> speakers,
        IEnumerable<IVoiceComponent> virtualMics,
        float previousWallCoefficient)
    {
        float bestVolume = 0f;
        float bestPan    = 0f;

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
                bestPan    = speaker.Pan;
            }
        }

        if (bestVolume <= 0f)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        return new(Math.Clamp(bestVolume, 0f, 1f), 0f, 0f, bestPan, VoiceAudioFilterMode.None,
            true, VoiceProximityReason.Proximity, previousWallCoefficient);
    }

    private static VoiceProximityResult CalculateCameraProxy(
        Vector2 targetPos,
        Vector2 cameraPosition,
        VoiceChatGameOptions s,
        float previousWallCoefficient)
    {
        float cameraRange  = s.MaxChatDistance.Value * 0.65f;
        float cameraDist   = Vector2.Distance(targetPos, cameraPosition);
        float cameraVolume = VoiceAudioOcclusion.ApplyFalloff(cameraDist, cameraRange,
            (VoiceFalloffMode)s.FalloffMode.Value) * 0.65f;
        if (cameraVolume <= 0f)
            return VoiceProximityResult.Muted(VoiceProximityReason.NoListener, previousWallCoefficient);

        float pan = VoiceChatRoom.GetPan(cameraPosition.x, targetPos.x);
        return new(cameraVolume, 0f, 0f, pan, VoiceAudioFilterMode.WallMuffle,
            true, VoiceProximityReason.CameraProxy, previousWallCoefficient);
    }
}