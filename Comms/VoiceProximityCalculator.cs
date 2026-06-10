using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceProximityCalculator
{
    private const float GhostVisionRangeMultiplier = 1f;
    private const float LowVolumeFloor = 0.06f;
    private static float _lastUnimpairedLocalLightRadius;

    internal static Func<bool>? LocalListenerBlindedOrFlashedProvider { get; set; }

    // Clear stale light radius on lifecycle transitions so a prior game can't shrink new-game hearing range.
    internal static void ResetSightState() => _lastUnimpairedLocalLightRadius = 0f;

    public static VoiceProximityResult CalculateLobby(
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
        VoiceTeamRadioChannel targetRadioChannel = VoiceTeamRadioChannel.All)
    {
        if (!targetPlayer.HasValue)
            return VoiceProximityResult.Muted(VoiceProximityReason.Unmapped);

        var s = VoiceRoomSettingsState.Current;
        var target = targetPlayer.Value;
        if (IsUnavailableTarget(target))
            return VoiceProximityResult.Muted(VoiceProximityReason.TargetUnavailable);
        bool localDead = localPlayer?.IsDead == true;
        bool targetDead = target.IsDead;

        if (s.OnlyGhostsCanTalk && !localDead)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk);

        if (VoiceRoleMuteState.IsMeetingVoiceBlocked(target, phase))
            return VoiceProximityResult.Muted(VoiceRoleMuteState.GetMeetingBlockReason(target, phase));

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
    //   PuppeteerSwap    — hear ENTIRELY from the controlled victim's body (origin + sight swapped to the victim).
    //   ParasiteAdditive — hear normally AND from the victim's surroundings; per target keep whichever is louder.
    // Both are gated on the host toggle; everyone else hears from their own body exactly as before.
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
        VoiceTeamRadioChannel targetRadioChannel = VoiceTeamRadioChannel.All)
    {
        var mode = localPlayer.HasValue ? localPlayer.Value.ControlHearingMode : VoiceControlHearingMode.None;
        if (mode != VoiceControlHearingMode.None)
        {
            var cs = VoiceRoomSettingsState.Current;
            if (mode == VoiceControlHearingMode.PuppeteerSwap && cs.PuppeteerHearFromVictim)
                return CalculateTaskPhaseSingle(localPlayer, targetPlayer, localPlayer!.Value.ControlledVictimPosition,
                    localPlayer.Value.ControlledVictimLightRadius, mapId, cameraViewActive, activeCameraIndex,
                    activeCameraPosition, speakers, virtualMics, localInVent, targetRadioActive, commsSabActive,
                    previousWallCoefficient, targetRadioChannel);
            if (mode == VoiceControlHearingMode.ParasiteAdditive && cs.ParasiteHearFromVictim)
            {
                var fromSelf = CalculateTaskPhaseSingle(localPlayer, targetPlayer, listenerPos, localLightRadius, mapId,
                    cameraViewActive, activeCameraIndex, activeCameraPosition, speakers, virtualMics, localInVent,
                    targetRadioActive, commsSabActive, previousWallCoefficient, targetRadioChannel);
                var fromVictim = CalculateTaskPhaseSingle(localPlayer, targetPlayer, localPlayer!.Value.ControlledVictimPosition,
                    localPlayer.Value.ControlledVictimLightRadius, mapId, cameraViewActive, activeCameraIndex,
                    activeCameraPosition, speakers, virtualMics, localInVent, targetRadioActive, commsSabActive,
                    previousWallCoefficient, targetRadioChannel);
                return Louder(fromSelf, fromVictim);
            }
        }
        return CalculateTaskPhaseSingle(localPlayer, targetPlayer, listenerPos, localLightRadius, mapId,
            cameraViewActive, activeCameraIndex, activeCameraPosition, speakers, virtualMics, localInVent,
            targetRadioActive, commsSabActive, previousWallCoefficient, targetRadioChannel);
    }

    // Picks the more-audible of two proximity results (used to merge a Parasite's own-body and victim-surroundings
    // hearing into a union). Ties favour the first (own-body) result.
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
        VoiceTeamRadioChannel targetRadioChannel = VoiceTeamRadioChannel.All)
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
        var localListenerPos = ResolveListenerPosition(localPlayer, listenerPos.Value);
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
        bool localMediatingMedium = IsMediatingMedium(localPlayer) &&
                                     (MediumGhostVoiceMode)s.MediumGhostVoice != MediumGhostVoiceMode.None;

        if (ShouldMeetingLobbyOnlyBlockTaskVoice(s, localDead, targetDead))
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyMeetingOrLobby, previousWallCoefficient);

        var mediumGhostRoute = TryCalculateMediumGhostRoute(localPlayer, target, localListenerPos, s, previousWallCoefficient);
        if (mediumGhostRoute.HasValue)
            return mediumGhostRoute.Value;

        if (s.OnlyGhostsCanTalk && !localDead && !localMediatingMedium)
            return VoiceProximityResult.Muted(VoiceProximityReason.OnlyGhostsCanTalk, previousWallCoefficient);
        if (commsSabActive && s.CommsSabDisables && !localDead && !localMediatingMedium)
            return VoiceProximityResult.Muted(VoiceProximityReason.CommsSabotage, previousWallCoefficient);

        if (VoiceRoleMuteState.IsTaskVoiceBlocked(target))
            return VoiceProximityResult.Muted(VoiceRoleMuteState.GetTaskBlockReason(target), previousWallCoefficient);

        if (s.TeamRadio && targetRadioActive && !targetDead)
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
        bool listenerBlindedOrFlashed = IsLocalListenerBlindedOrFlashed();
        if (s.OnlyHearInSight)
            maxDistance = ResolveSightLimitedMaxDistance(maxDistance, localLightRadius, listenerBlindedOrFlashed);

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
            VoiceTeamRadioChannel.Impostors => settings.TeamRadioImpostors && local.IsImpostor && target.IsImpostor,
            VoiceTeamRadioChannel.Vampires => settings.TeamRadioVampires && local.IsVampire && target.IsVampire,
            VoiceTeamRadioChannel.Lovers => settings.TeamRadioLovers && AreLinkedLovers(local, target),
            VoiceTeamRadioChannel.All =>
                (settings.TeamRadioImpostors && local.IsImpostor && target.IsImpostor) ||
                (settings.TeamRadioVampires && local.IsVampire && target.IsVampire) ||
                (settings.TeamRadioLovers && AreLinkedLovers(local, target)),
            _ => false,
        };
    }

    internal static bool IsUnavailableTarget(VoicePlayerSnapshot target)
        => target.Disconnected || target.IsDummy || !target.IsVisible;

    private static VoiceProximityResult? TryCalculateMediumGhostRoute(
        VoicePlayerSnapshot? localPlayer,
        VoicePlayerSnapshot target,
        Vector2 listenerPos,
        VoiceRoomSettingsSnapshot settings,
        float wallCoefficient)
    {
        if (!localPlayer.HasValue)
            return null;

        var local = localPlayer.Value;
        var mode = (MediumGhostVoiceMode)settings.MediumGhostVoice;
        if (mode == MediumGhostVoiceMode.None)
            return null;

        if (target.IsMedium && target.HasMediumSpirit)
        {
            if (MediumCanTalkToGhosts(mode))
            {
                if (local.IsDead)
                    return CalculateMediumSpatialRoute(
                        target.MediumSpiritPosition,
                        listenerPos,
                        settings,
                        wallCoefficient,
                        VoiceProximityReason.MediumSpeaksToGhost,
                        ghostOutput: false);

                return VoiceProximityResult.Muted(VoiceProximityReason.MediumPrivateFromLiving, wallCoefficient);
            }

            if (local.IsDead)
                return VoiceProximityResult.Muted(VoiceProximityReason.NonSelectedGhostMuted, wallCoefficient);

            return VoiceProximityResult.Muted(VoiceProximityReason.MediumPrivateFromLiving, wallCoefficient);
        }

        if (local.IsMedium && local.HasMediumSpirit && target.IsDead && GhostCanTalkToMedium(mode))
        {
            if (!target.IsMediatedGhost || target.MediatingMediumId != local.PlayerId)
                return VoiceProximityResult.Muted(VoiceProximityReason.NonSelectedGhostMuted, wallCoefficient);

            return CalculateMediumSpatialRoute(
                target.Position,
                local.MediumSpiritPosition,
                settings,
                wallCoefficient,
                VoiceProximityReason.GhostSpeaksToMedium,
                ghostOutput: true);
        }

        return null;
    }

    private static bool IsMediatingMedium(VoicePlayerSnapshot? player)
        => player.HasValue && player.Value.IsMedium && player.Value.HasMediumSpirit;

    private static bool MediumCanTalkToGhosts(MediumGhostVoiceMode mode)
        => mode is MediumGhostVoiceMode.MediumToGhost or MediumGhostVoiceMode.Both;

    private static bool GhostCanTalkToMedium(MediumGhostVoiceMode mode)
        => mode is MediumGhostVoiceMode.GhostToMedium or MediumGhostVoiceMode.Both;

    private static Vector2 ResolveListenerPosition(VoicePlayerSnapshot? localPlayer, Vector2 fallback)
        => IsMediatingMedium(localPlayer) ? localPlayer!.Value.MediumSpiritPosition : fallback;

    private static bool IsLocalListenerBlindedOrFlashed()
        => LocalListenerBlindedOrFlashedProvider?.Invoke() ??
           VoiceRoleMuteState.IsLocalListenerBlindedOrFlashed();

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

    private static VoiceProximityResult CalculateMediumSpatialRoute(
        Vector2 sourcePos,
        Vector2 listenerPos,
        VoiceRoomSettingsSnapshot settings,
        float wallCoefficient,
        VoiceProximityReason reason,
        bool ghostOutput)
    {
        float dist = Distance(sourcePos, listenerPos);
        float volume = VoiceAudioOcclusion.ApplyFalloff(dist, settings.MaxChatDistance, (VoiceFalloffMode)settings.FalloffMode);
        if (volume < LowVolumeFloor)
            volume = 0f;

        float pan = VoiceChatRoom.GetPan(listenerPos.x, sourcePos.x);
        return ghostOutput
            ? new(0f, volume, 0f, pan, VoiceAudioFilterMode.Ghost, volume > 0f, reason, wallCoefficient)
            : new(volume, 0f, 0f, pan, VoiceAudioFilterMode.None, volume > 0f, reason, wallCoefficient);
    }

    private static bool AreLinkedLovers(VoicePlayerSnapshot local, VoicePlayerSnapshot target)
    {
        if (!local.IsLover || !target.IsLover)
            return false;

        // Explicit partner ids only; byte.MaxValue (unresolved sentinel) as wildcard let unrelated pairs match.
        return local.LoverPartnerId == target.PlayerId ||
               target.LoverPartnerId == local.PlayerId;
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
