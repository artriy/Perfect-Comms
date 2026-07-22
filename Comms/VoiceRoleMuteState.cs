using System;
using System.Collections.Generic;
using HarmonyLib;
using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static partial class VoiceRoleMuteState
{
    private const string BlackmailedModifierName = "TownOfUs.Modifiers.Impostor.BlackmailedModifier";
    private const string JailedModifierName = "TownOfUs.Modifiers.Crewmate.JailedModifier";
    private const string ParasiteInfectedModifierName = "TownOfUs.Modifiers.Impostor.ParasiteInfectedModifier";
    private const string PuppeteerControlModifierName = "TownOfUs.Modifiers.Impostor.PuppeteerControlModifier";
    private const string CrewpostorModifierName = "TownOfUs.Modifiers.Game.Alliance.CrewpostorModifier";
    private const string LoverModifierName = "TownOfUs.Modifiers.Game.Alliance.LoverModifier";
    private const string SwoopModifierName = "TownOfUs.Modifiers.Impostor.SwoopModifier";
    private const string GlitchHackedModifierName = "TownOfUs.Modifiers.Neutral.GlitchHackedModifier";
    private const string EclipsalBlindModifierName = "TownOfUs.Modifiers.Impostor.EclipsalBlindModifier";
    private const string GrenadierFlashModifierName = "TownOfUs.Modifiers.Impostor.GrenadierFlashModifier";
    private const string HypnotisedModifierName = "TownOfUs.Modifiers.Impostor.HypnotisedModifier";
    private const string JailorRoleName = "TownOfUs.Roles.Crewmate.JailorRole";
    private const string VampireRoleName = "TownOfUs.Roles.Neutral.VampireRole";
    private const string MediumRoleName = "TownOfUs.Roles.Crewmate.MediumRole";
    private const string MediatedModifierName = "TownOfUs.Modifiers.Crewmate.MediatedModifier";
    private const float RoleStateRefreshInterval = 0.25f;
    private const float JailVoiceGateLogInterval = 2f;
    private const float JailVoiceHeartbeatSeconds = 2f;
    private static float _nextJailVoiceHeartbeatTime;

    private static readonly HashSet<byte> JailVoiceAllowed = new();
    private static byte _gracePeriodCallerId = byte.MaxValue;
    private static float _gracePeriodDeadline;
    private static float _gracePeriodSeconds;
    private static bool _gracePeriodArmed;
    private static readonly HashSet<byte> MeetingBlackmailedPlayers = new();
    private static readonly HashSet<byte> PostMeetingBlackmailedPlayers = new();
    private static readonly Dictionary<byte, CachedRoleState> RoleStateCache = new();

    private static Type? _blackmailedModifierType;
    private static Type? _jailedModifierType;
    private static Type? _parasiteInfectedModifierType;
    private static Type? _puppeteerControlModifierType;
    private static Type? _crewpostorModifierType;
    private static Type? _loverModifierType;
    private static Type? _swoopModifierType;
    private static Type? _glitchHackedModifierType;
    private static Type? _eclipsalBlindModifierType;
    private static Type? _grenadierFlashModifierType;
    private static Type? _hypnotisedModifierType;
    private static Type? _vampireRoleType;
    private static Type? _mediumRoleType;
    private static Type? _mediatedModifierType;
    private static bool _supportedModTypesResolved;
    private static int _resolvedGameId = int.MinValue;
    private static VoiceGamePhase _resolvedPhase = VoiceGamePhase.Unknown;
    private static int _resolvedAssemblyGeneration = -1;
    private static float _nextRoleStateRefreshTime;
    private static bool _wasInMeeting;
    private static DateTime _nextJailVoiceGateLogUtc = DateTime.MinValue;
    private static bool _lastJailVoiceUnmuteAvailable;

    private readonly record struct CachedRoleState(
        bool IsBlackmailed,
        bool IsJailed,
        byte JailorId,
        bool IsParasiteControlled,
        bool IsPuppeteerControlled,
        bool IsCrewpostor,
        bool IsVampire,
        bool IsLover,
        byte LoverPartnerId,
        bool IsBlackmailedNextRound,
        bool IsSwooped,
        bool IsGlitchHacked,
        bool IsMedium,
        bool HasMediumSpirit,
        Vector2 MediumSpiritPosition,
        bool IsMediatedGhost,
        byte MediatingMediumId);

    internal static void Update()
    {
        var settings = VoiceRoomSettingsState.Current;
        var phase = VoiceSceneState.ResolvePhase();
        bool inMeetingVoicePhase = VoiceSceneState.IsMeetingVoicePhase(phase);

        if (inMeetingVoicePhase && !_wasInMeeting)
        {
            PostMeetingBlackmailedPlayers.Clear();
            MeetingBlackmailedPlayers.Clear();
            InvalidateRoleStateCache();
        }

        RefreshRoleStateCacheIfNeeded();

        if (inMeetingVoicePhase)
        {
            _wasInMeeting = true;
            if (_gracePeriodCallerId != byte.MaxValue && !_gracePeriodArmed && MeetingHud.Instance != null)
            {
                _gracePeriodDeadline = Time.time + _gracePeriodSeconds;
                _gracePeriodArmed = true;
            }
            if (settings.MuteBlackmailedInMeetings)
                TrackMeetingBlackmailedPlayers();
            else
                MeetingBlackmailedPlayers.Clear();

            PruneJailVoiceAllowed(settings);
            MaybeResendJailVoiceHeartbeat(settings);
            return;
        }

        if (_wasInMeeting && phase == VoiceGamePhase.Unknown)
            return;

        if (_wasInMeeting)
        {
            _wasInMeeting = false;
            JailVoiceAllowed.Clear();
            ClearGracePeriod();
            PostMeetingBlackmailedPlayers.Clear();

            if (settings.MuteBlackmailedInMeetings && settings.MuteBlackmailedNextRound)
            {
                foreach (byte playerId in MeetingBlackmailedPlayers)
                    PostMeetingBlackmailedPlayers.Add(playerId);
            }

            MeetingBlackmailedPlayers.Clear();
            InvalidateRoleStateCache();
        }
        else if (!settings.MuteBlackmailedNextRound && PostMeetingBlackmailedPlayers.Count > 0)
        {
            PostMeetingBlackmailedPlayers.Clear();
            InvalidateRoleStateCache();
        }

        if (VoiceSceneState.IsLobbyVoicePhase(phase))
        {
            if (PostMeetingBlackmailedPlayers.Count > 0)
            {
                PostMeetingBlackmailedPlayers.Clear();
                InvalidateRoleStateCache();
            }
        }

        PrunePostMeetingBlackmailedPlayers();
    }

    internal static bool IsLocalVoiceBlocked()
        => IsLocalVoiceBlocked(VoiceSceneState.ResolvePhase());

    internal static bool IsLocalVoiceBlocked(VoiceGamePhase phase)
        => TryGetLocalVoiceBlockReason(phase, out _);

    internal static bool IsLocalMeetingVoiceBlocked()
        => TryGetLocalMeetingVoiceBlockReason(out _);

    internal static bool IsLocalListenerBlindedOrFlashed()
    {
        var local = PlayerControl.LocalPlayer;
        if (local == null)
            return false;

        RefreshSupportedModTypesIfNeeded();
        return GetModifier(local, _eclipsalBlindModifierType) != null ||
               GetModifier(local, _grenadierFlashModifierType) != null;
    }

    internal static bool IsLocalListenerHypnotizedDuringHysteria()
    {
        var local = PlayerControl.LocalPlayer;
        if (local == null)
            return false;

        RefreshSupportedModTypesIfNeeded();
        return IsHypnotisedHysteriaActive(GetModifier(local, _hypnotisedModifierType));
    }

    internal static bool IsLocalListenerAudioMuffled(VoiceGamePhase phase)
    {
        var settings = VoiceRoomSettingsState.Current;
        if (settings.MuffleBlindedOrFlashedHearing
            && ShouldApplyBlindedOrFlashedMuffle(phase)
            && IsLocalListenerBlindedOrFlashed())
            return true;
        if (settings.MuffleHypnotizedDuringHysteria
            && IsLocalListenerHypnotizedDuringHysteria())
            return true;

        // Third-party filters receive the authoritative routing phase and remain free to opt into
        // meetings or exile. Only the built-in task-world blindness effect is phase-restricted.
        return VoiceModRegistry.LocalListenerMuffled(PlayerControl.LocalPlayer, phase);
    }

    internal static bool ShouldApplyBlindedOrFlashedMuffle(VoiceGamePhase phase)
        => phase == VoiceGamePhase.Tasks;

    internal static VoiceProximityResult ApplyLocalListenerAudioMuffle(
        VoiceProximityResult result,
        VoiceGamePhase phase)
        => result.Audible && IsLocalListenerAudioMuffled(phase)
            ? result with { FilterMode = VoiceAudioFilterMode.ListenerMuffle }
            : result;

    internal static bool IsVoiceDead(PlayerControl? player)
    {
        if (player == null)
            return false;

        var data = player.Data;
        bool baseDead = data != null && (data.IsDead || data.Role?.IsDead == true);
        VoicePlayerTraits traits = VoiceModRegistry.ResolvePlayerTraits(
            player,
            VoiceModBridge.ToApiPhase(VoiceSceneState.ResolvePhase()),
            player == PlayerControl.LocalPlayer,
            baseDead);
        return baseDead || (traits & VoicePlayerTraits.VoiceDead) != 0;
    }

    internal static bool TryGetLocalVoiceBlockReason(out string reason)
        => TryGetLocalVoiceBlockReason(VoiceSceneState.ResolvePhase(), out reason);

    internal static bool TryGetLocalVoiceBlockReason(VoiceGamePhase phase, out string reason)
    {
        reason = string.Empty;
        Update();

        var local = PlayerControl.LocalPlayer;
        if (local == null)
            return false;

        var localData = local.Data;
        bool baseDead = localData != null &&
                        (localData.IsDead || localData.Role?.IsDead == true);
        VoicePlayerTraits traits = VoiceModRegistry.ResolvePlayerTraits(
            local,
            VoiceModBridge.ToApiPhase(phase),
            isLocal: true,
            baseDead);
        bool voiceDead = baseDead || (traits & VoicePlayerTraits.VoiceDead) != 0;

        // API gates apply to local capture in every voice phase and before built-in living-only rules.
        if (VoiceModRegistry.LocalGate(
                local,
                VoiceModBridge.ToApiPhase(phase),
                voiceDead,
                out var modReason))
        {
            reason = string.IsNullOrEmpty(modReason) ? "Role Muted" : modReason;
            return true;
        }

        if (voiceDead)
            return false;

        GetPlayerRoleState(local, out bool isBlackmailed, out bool isJailed, out byte jailorId,
            out bool isParasiteControlled, out bool isPuppeteerControlled, out bool isCrewpostor,
            out bool isVampire, out bool isLover, out byte loverPartnerId,
            out bool isBlackmailedNextRound, out bool isSwooped, out bool isGlitchHacked);

        var state = new CachedRoleState(
            isBlackmailed,
            isJailed,
            jailorId,
            isParasiteControlled,
            isPuppeteerControlled,
            isCrewpostor,
            isVampire,
            isLover,
            loverPartnerId,
            isBlackmailedNextRound,
            isSwooped,
            isGlitchHacked,
            false,
            false,
            default,
            false,
            byte.MaxValue);

        var settings = VoiceRoomSettingsState.Current;
        bool meetingVoicePhase = VoiceSceneState.IsMeetingVoicePhase(phase);
        bool taskVoicePhase = VoiceSceneState.IsTaskVoicePhase(phase);

        if ((meetingVoicePhase || taskVoicePhase) && settings.MuteGlitchHacked && state.IsGlitchHacked)
        {
            reason = ToDisplayReason(VoiceProximityReason.GlitchHacked);
            return true;
        }

        if (meetingVoicePhase && IsMeetingVoiceBlocked(local.PlayerId, state, settings, out var meetingReason))
        {
            reason = ToDisplayReason(meetingReason);
            return true;
        }

        if (taskVoicePhase && IsTaskVoiceBlocked(local.PlayerId, state, settings, out var taskReason))
        {
            reason = ToDisplayReason(taskReason);
            return true;
        }

        return false;
    }

    internal static bool TryGetLocalMeetingVoiceBlockReason(out string reason)
    {
        reason = string.Empty;
        Update();

        var local = PlayerControl.LocalPlayer;
        var phase = VoiceSceneState.ResolvePhase();
        if (local == null || !VoiceSceneState.IsMeetingVoicePhase(phase) || IsVoiceDead(local))
            return false;

        GetPlayerRoleState(local, out bool isBlackmailed, out bool isJailed, out byte jailorId,
            out _, out _, out _, out _, out _, out _, out _, out bool isSwooped, out bool isGlitchHacked);

        var state = new CachedRoleState(isBlackmailed, isJailed, jailorId, false, false, false, false, false, byte.MaxValue, false, isSwooped, isGlitchHacked, false, false, default, false, byte.MaxValue);
        if (VoiceRoomSettingsState.Current.MuteGlitchHacked && state.IsGlitchHacked)
        {
            reason = ToDisplayReason(VoiceProximityReason.GlitchHacked);
            return true;
        }

        if (!IsMeetingVoiceBlocked(local.PlayerId, state, VoiceRoomSettingsState.Current, out var blockReason))
            return false;

        reason = ToDisplayReason(blockReason);
        return true;
    }

    internal static bool IsMeetingVoiceBlocked(VoicePlayerSnapshot player)
        => IsMeetingVoiceBlocked(player, VoiceSceneState.ResolvePhase());

    internal static bool IsMeetingVoiceBlocked(VoicePlayerSnapshot player, VoiceGamePhase phase)
    {
        if (!VoiceSceneState.IsMeetingVoicePhase(phase) || player.IsDead)
            return false;

        if (player.External.Muted)
            return true;

        var state = new CachedRoleState(
            player.IsBlackmailed,
            player.IsJailed,
            player.JailorId,
            player.IsParasiteControlled,
            player.IsPuppeteerControlled,
            false,
            player.IsVampire,
            player.IsLover,
            player.LoverPartnerId,
            player.IsBlackmailedNextRound,
            player.IsSwooped,
            player.IsGlitchHacked,
            player.IsMedium,
            player.HasMediumSpirit,
            player.MediumSpiritPosition,
            player.IsMediatedGhost,
            player.MediatingMediumId);

        return IsMeetingVoiceBlocked(player.PlayerId, state, VoiceRoomSettingsState.Current, out _);
    }

    internal static VoiceProximityReason GetMeetingBlockReason(VoicePlayerSnapshot player)
        => GetMeetingBlockReason(player, VoiceSceneState.ResolvePhase());

    internal static VoiceProximityReason GetMeetingBlockReason(VoicePlayerSnapshot player, VoiceGamePhase phase)
    {
        if (!VoiceSceneState.IsMeetingVoicePhase(phase))
            return VoiceProximityReason.MeetingLiving;

        if (player.External.Muted)
            return VoiceProximityReason.RoleMuted;

        var state = new CachedRoleState(
            player.IsBlackmailed,
            player.IsJailed,
            player.JailorId,
            player.IsParasiteControlled,
            player.IsPuppeteerControlled,
            false,
            player.IsVampire,
            player.IsLover,
            player.LoverPartnerId,
            player.IsBlackmailedNextRound,
            player.IsSwooped,
            player.IsGlitchHacked,
            player.IsMedium,
            player.HasMediumSpirit,
            player.MediumSpiritPosition,
            player.IsMediatedGhost,
            player.MediatingMediumId);

        return IsMeetingVoiceBlocked(player.PlayerId, state, VoiceRoomSettingsState.Current, out var reason)
            ? reason
            : VoiceProximityReason.MeetingLiving;
    }

    internal static bool IsTaskVoiceBlocked(VoicePlayerSnapshot player)
    {
        if (player.IsDead)
            return false;

        if (player.External.Muted)
            return true;

        var state = new CachedRoleState(
            player.IsBlackmailed,
            player.IsJailed,
            player.JailorId,
            player.IsParasiteControlled,
            player.IsPuppeteerControlled,
            false,
            player.IsVampire,
            player.IsLover,
            player.LoverPartnerId,
            player.IsBlackmailedNextRound,
            player.IsSwooped,
            player.IsGlitchHacked,
            player.IsMedium,
            player.HasMediumSpirit,
            player.MediumSpiritPosition,
            player.IsMediatedGhost,
            player.MediatingMediumId);

        return IsTaskVoiceBlocked(player.PlayerId, state, VoiceRoomSettingsState.Current, out _);
    }

    internal static VoiceProximityReason GetTaskBlockReason(VoicePlayerSnapshot player)
    {
        if (player.External.Muted)
            return VoiceProximityReason.RoleMuted;

        var state = new CachedRoleState(
            player.IsBlackmailed,
            player.IsJailed,
            player.JailorId,
            player.IsParasiteControlled,
            player.IsPuppeteerControlled,
            false,
            player.IsVampire,
            player.IsLover,
            player.LoverPartnerId,
            player.IsBlackmailedNextRound,
            player.IsSwooped,
            player.IsGlitchHacked,
            player.IsMedium,
            player.HasMediumSpirit,
            player.MediumSpiritPosition,
            player.IsMediatedGhost,
            player.MediatingMediumId);

        return IsTaskVoiceBlocked(player.PlayerId, state, VoiceRoomSettingsState.Current, out var reason)
            ? reason
            : VoiceProximityReason.Proximity;
    }

    internal static bool IsBlackmailed(PlayerControl? player)
    {
        GetPlayerRoleState(player, out bool isBlackmailed, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        return isBlackmailed;
    }

    internal static bool TryGetJailorId(PlayerControl? player, out byte jailorId)
    {
        GetPlayerRoleState(player, out _, out bool isJailed, out jailorId, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        return isJailed;
    }

    internal static bool IsVoiceImpostor(PlayerControl? player)
    {
        if (player?.Data?.Role?.IsImpostor == true)
            return true;
        if (VoiceRoomSettingsState.Current.CrewpostorUsesImpostorVoice)
        {
            GetPlayerRoleState(player, out _, out _, out _, out _, out _, out bool isCrewpostor, out _, out _, out _, out _, out _, out _);
            if (isCrewpostor)
                return true;
        }

        if (player == null) return false;
        var data = player.Data;
        bool baseDead = data != null && (data.IsDead || data.Role?.IsDead == true);
        VoicePlayerTraits traits = VoiceModRegistry.ResolvePlayerTraits(
            player,
            VoiceModBridge.ToApiPhase(VoiceSceneState.ResolvePhase()),
            player == PlayerControl.LocalPlayer,
            baseDead);
        return (traits & VoicePlayerTraits.ImpostorVoice) != 0;
    }

    internal static bool CanUseTeamRadio(PlayerControl? player)
    {
        return GetFirstTeamRadioChannel(player) != VoiceTeamRadioChannel.None;
    }

    internal static VoiceTeamRadioChannel GetFirstTeamRadioChannel(PlayerControl? player)
    {
        foreach (var channel in VoiceTeamRadioChannels.Order)
            if (CanUseTeamRadioChannel(player, channel))
                return channel;
        return VoiceTeamRadioChannel.None;
    }

    internal static VoiceTeamRadioChannel GetNextTeamRadioChannel(PlayerControl? player, VoiceTeamRadioChannel current)
    {
        int currentIndex = System.Array.IndexOf(VoiceTeamRadioChannels.Order, current);
        for (int i = 1; i <= VoiceTeamRadioChannels.Order.Length; i++)
        {
            int index = (currentIndex + i + VoiceTeamRadioChannels.Order.Length) % VoiceTeamRadioChannels.Order.Length;
            var candidate = VoiceTeamRadioChannels.Order[index];
            if (CanUseTeamRadioChannel(player, candidate))
                return candidate;
        }

        return VoiceTeamRadioChannel.None;
    }

    internal static bool CanUseTeamRadioChannel(PlayerControl? player, VoiceTeamRadioChannel channel)
    {
        if (player == null) return false;
        var settings = VoiceRoomSettingsState.Current;
        if (!settings.TeamRadio) return false;

        return channel switch
        {
            VoiceTeamRadioChannel.Impostors => settings.TeamRadioImpostors && IsVoiceImpostor(player),
            VoiceTeamRadioChannel.Vampires => settings.TeamRadioVampires && HasRoleRadioState(player, vampire: true),
            VoiceTeamRadioChannel.Lovers => settings.TeamRadioLovers && HasRoleRadioState(player, lover: true),
            _ => false,
        };
    }

    private static bool HasRoleRadioState(PlayerControl player, bool vampire = false, bool lover = false)
    {
        GetPlayerRoleState(player, out _, out _, out _, out _, out _, out _,
            out bool isVampire, out bool isLover, out _, out _, out _, out _);
        return (vampire && isVampire) || (lover && isLover);
    }

    internal static void GetPlayerRoleState(
        PlayerControl? player,
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
        out bool isGlitchHacked)
    {
        isBlackmailed = false;
        isJailed = false;
        jailorId = byte.MaxValue;
        isParasiteControlled = false;
        isPuppeteerControlled = false;
        isCrewpostor = false;
        isVampire = false;
        isLover = false;
        loverPartnerId = byte.MaxValue;
        isBlackmailedNextRound = false;
        isSwooped = false;
        isGlitchHacked = false;
        if (player == null) return;

        RefreshRoleStateCacheIfNeeded();
        if (!RoleStateCache.TryGetValue(player.PlayerId, out var state)) return;

        isBlackmailed = state.IsBlackmailed;
        isJailed = state.IsJailed;
        jailorId = state.JailorId;
        isParasiteControlled = state.IsParasiteControlled;
        isPuppeteerControlled = state.IsPuppeteerControlled;
        isCrewpostor = state.IsCrewpostor;
        isVampire = state.IsVampire;
        isLover = state.IsLover;
        loverPartnerId = state.LoverPartnerId;
        isBlackmailedNextRound = state.IsBlackmailedNextRound;
        isSwooped = state.IsSwooped;
        isGlitchHacked = state.IsGlitchHacked;
    }

    internal static void GetPlayerMediumVoiceState(
        PlayerControl? player,
        out bool isMedium,
        out bool hasMediumSpirit,
        out Vector2 mediumSpiritPosition,
        out bool isMediatedGhost,
        out byte mediatingMediumId)
    {
        isMedium = false;
        hasMediumSpirit = false;
        mediumSpiritPosition = default;
        isMediatedGhost = false;
        mediatingMediumId = byte.MaxValue;
        if (player == null) return;

        RefreshRoleStateCacheIfNeeded();
        if (!RoleStateCache.TryGetValue(player.PlayerId, out var state)) return;

        isMedium = state.IsMedium;
        hasMediumSpirit = state.HasMediumSpirit;
        mediumSpiritPosition = state.MediumSpiritPosition;
        isMediatedGhost = state.IsMediatedGhost;
        mediatingMediumId = state.MediatingMediumId;
    }

    internal static bool CanLocalJailorUnmute(out byte jailedPlayerId)
    {
        bool available = CanLocalJailorUnmuteCore(out jailedPlayerId);
        if (available != _lastJailVoiceUnmuteAvailable)
        {
            _lastJailVoiceUnmuteAvailable = available;
            VoiceDiagnostics.Log("jailvoice.available", $"available={available} jailee={jailedPlayerId}");
        }

        if (!available)
            LogJailVoiceGateThrottled();

        return available;
    }

    private static bool CanLocalJailorUnmuteCore(out byte jailedPlayerId)
    {
        jailedPlayerId = byte.MaxValue;
        Update();

        var settings = VoiceRoomSettingsState.Current;
        if (!settings.MuteJailedInMeetings || !settings.JailorCanUnmuteJailed)
            return false;

        var local = PlayerControl.LocalPlayer;
        if (local == null || !VoiceSceneState.IsMeetingVoicePhase(VoiceSceneState.ResolvePhase()) || local.Data?.IsDead == true || !IsJailor(local))
            return false;

        try
        {
            foreach (var player in PlayerControl.AllPlayerControls)
            {
                if (player == null || player.Data?.IsDead == true) continue;
                if (!TryGetJailorId(player, out byte jailorId) || jailorId != local.PlayerId) continue;
                if (!IsJailorValid(jailorId) || JailVoiceAllowed.Contains(player.PlayerId)) continue;
                jailedPlayerId = player.PlayerId;
                return true;
            }
        }
        catch
        {
            // AllPlayerControls can be invalidated by a mid-enumeration scene transition; fail closed
            // (no jailor unmute available this frame) so the throw doesn't escape the UpdateHud path.
        }

        return false;
    }

    private static void LogJailVoiceGateThrottled()
    {
        if (DateTime.UtcNow < _nextJailVoiceGateLogUtc) return;
        if (!VoiceDiagnostics.IsEnabled) return;

        _nextJailVoiceGateLogUtc = DateTime.UtcNow.AddSeconds(JailVoiceGateLogInterval);

        var phase = VoiceSceneState.ResolvePhase();
        if (!VoiceSceneState.IsMeetingVoicePhase(phase)) return;

        int jailedCount = 0;
        byte jaileeId = byte.MaxValue;
        byte jaileeJailorId = byte.MaxValue;
        foreach (var pair in RoleStateCache)
        {
            if (!pair.Value.IsJailed) continue;
            jailedCount++;
            if (jaileeId == byte.MaxValue)
            {
                jaileeId = pair.Key;
                jaileeJailorId = pair.Value.JailorId;
            }
        }

        if (jailedCount == 0) return;

        var settings = VoiceRoomSettingsState.Current;
        if (!settings.MuteJailedInMeetings || !settings.JailorCanUnmuteJailed)
        {
            VoiceDiagnostics.Log("jailvoice.gate", $"gate=settings muteJailed={settings.MuteJailedInMeetings} canUnmute={settings.JailorCanUnmuteJailed}");
            return;
        }

        var local = PlayerControl.LocalPlayer;
        if (local == null || local.Data?.IsDead == true || !IsJailor(local))
        {
            VoiceDiagnostics.Log("jailvoice.gate", $"gate=localRole jailor={IsJailor(local)} dead={local?.Data?.IsDead == true} phase={phase}");
            return;
        }

        var jailee = FindPlayer(jaileeId);
        VoiceDiagnostics.Log("jailvoice.gate", $"gate=candidate jailedCount={jailedCount} jailorIdMismatch={jaileeJailorId}vs{local.PlayerId} alreadyAllowed={JailVoiceAllowed.Contains(jaileeId)} jaileeDead={jailee == null || jailee.Data?.IsDead == true}");
    }

    internal static void LocalJailorAllowVoice()
    {
        RefreshRoleStateCacheIfNeeded(force: true);
        if (!CanLocalJailorUnmute(out byte jailedPlayerId))
        {
            VoiceDiagnostics.Log("jailvoice.local", "rejected=true");
            return;
        }

        VoiceDiagnostics.Log("jailvoice.local", $"allowed=true jailee={jailedPlayerId}");
        SetJailVoiceAllowed(jailedPlayerId, true);
        SendJailVoiceAllowed(jailedPlayerId, true);
        VoiceChatHudState.ApplyMicState();
    }

    internal static void ApplyRemoteJailVoice(byte jailorId, byte jailedPlayerId, bool allowed)
    {
        // The RPC sender's netId is forgeable on a relay, so we cannot fully authenticate that the
        // sender really is the jailor. We re-derive authority from live state below, but additionally
        // only honour the permissive direction (unmute): a spoofed "re-mute" (allowed=false) is
        // ignored so a forger cannot silence a jailee the jailor chose to keep audible. The jailee is
        // muted by default in meetings and the local jailor never sends a re-mute over the wire, so
        // ignoring allowed=false removes no legitimate behaviour.
        if (!allowed)
            return;

        RefreshRoleStateCacheIfNeeded(force: true);
        var settings = VoiceRoomSettingsState.Current;
        if (!settings.MuteJailedInMeetings || !settings.JailorCanUnmuteJailed)
        {
            VoiceDiagnostics.Log("jailvoice.rpc.apply", $"applied=false reason=settings-off jailor={jailorId} jailee={jailedPlayerId}");
            return;
        }

        var jailed = FindPlayer(jailedPlayerId);
        if (jailed == null || !TryGetJailorId(jailed, out byte actualJailorId))
        {
            VoiceDiagnostics.Log("jailvoice.rpc.apply", $"applied=false reason=no-jailed-player jailor={jailorId} jailee={jailedPlayerId}");
            return;
        }

        if (actualJailorId != jailorId)
        {
            VoiceDiagnostics.Log("jailvoice.rpc.apply", $"applied=false reason=jailor-mismatch jailor={jailorId} actualJailor={actualJailorId} jailee={jailedPlayerId}");
            return;
        }

        if (!IsJailorValid(jailorId))
        {
            VoiceDiagnostics.Log("jailvoice.rpc.apply", $"applied=false reason=jailor-invalid jailor={jailorId} jailee={jailedPlayerId}");
            return;
        }

        bool added = JailVoiceAllowed.Add(jailedPlayerId);
        if (added)
            VoiceDiagnostics.Log("jailvoice.rpc.apply", $"applied=true jailor={jailorId} jailee={jailedPlayerId}");
        VoiceChatHudState.ApplyMicState();
    }

    internal static bool IsJailVoiceAllowed(byte playerId)
        => JailVoiceAllowed.Contains(playerId);

    // Lifecycle reset: clear all per-session role-mute statics so they cannot leak into the next game (LB3).
    internal static void Reset()
    {
        JailVoiceAllowed.Clear();
        MeetingBlackmailedPlayers.Clear();
        PostMeetingBlackmailedPlayers.Clear();
        ClearGracePeriod();
        _wasInMeeting = false;
        InvalidateRoleStateCache();
    }

    internal static void OnMeetingStarted(byte callerId)
    {
        var settings = VoiceRoomSettingsState.Current;
        if (!settings.GracePeriodEnabled || settings.GracePeriodSeconds <= 0f)
        {
            ClearGracePeriod();
            return;
        }

        // Arm the deadline only once MeetingHud is live (in Update), not during the pre-meeting cutscene (LB1).
        _gracePeriodCallerId = callerId;
        _gracePeriodSeconds = settings.GracePeriodSeconds;
        _gracePeriodDeadline = 0f;
        _gracePeriodArmed = false;
        VoiceChatHudState.ApplyMicState();
    }

    private static void ClearGracePeriod()
    {
        _gracePeriodCallerId = byte.MaxValue;
        _gracePeriodDeadline = 0f;
        _gracePeriodSeconds = 0f;
        _gracePeriodArmed = false;
    }

    internal static bool IsGracePeriodActive
        => _gracePeriodCallerId != byte.MaxValue
           && _gracePeriodArmed
           && VoiceRoomSettingsState.Current.GracePeriodEnabled
           && MeetingHud.Instance != null
           && Time.time < _gracePeriodDeadline;

    internal static byte GracePeriodCallerId => _gracePeriodCallerId;

    internal static int GracePeriodSecondsRemaining
        => IsGracePeriodActive ? Mathf.Max(1, Mathf.CeilToInt(_gracePeriodDeadline - Time.time)) : 0;

    private static bool IsMeetingVoiceBlocked(
        byte playerId,
        CachedRoleState state,
        VoiceRoomSettingsSnapshot settings,
        out VoiceProximityReason reason)
    {
        reason = VoiceProximityReason.MeetingLiving;

        if (settings.GracePeriodEnabled && IsGracePeriodActive && playerId != _gracePeriodCallerId)
        {
            reason = VoiceProximityReason.GracePeriod;
            return true;
        }

        if (settings.MuteSwooperWhileSwooped && state.IsSwooped)
        {
            reason = VoiceProximityReason.Swooped;
            return true;
        }

        if (settings.MuteBlackmailedInMeetings && state.IsBlackmailed)
        {
            reason = VoiceProximityReason.Blackmailed;
            return true;
        }

        if (settings.MuteJailedInMeetings &&
            state.IsJailed &&
            (settings.JailPersistsAfterJailorDeath || IsJailorValid(state.JailorId)) &&
            (!settings.JailorCanUnmuteJailed || !JailVoiceAllowed.Contains(playerId)))
        {
            reason = VoiceProximityReason.Jailed;
            return true;
        }

        return false;
    }

    private static bool IsTaskVoiceBlocked(
        byte playerId,
        CachedRoleState state,
        VoiceRoomSettingsSnapshot settings,
        out VoiceProximityReason reason)
    {
        _ = playerId;
        reason = VoiceProximityReason.Proximity;

        if (settings.MuteSwooperWhileSwooped && state.IsSwooped)
        {
            reason = VoiceProximityReason.Swooped;
            return true;
        }

        if (settings.MuteBlackmailedNextRound && state.IsBlackmailedNextRound)
        {
            reason = VoiceProximityReason.BlackmailedNextRound;
            return true;
        }

        if (settings.MuteParasiteControlled && state.IsParasiteControlled)
        {
            reason = VoiceProximityReason.ParasiteControlled;
            return true;
        }

        if (settings.MutePuppeteerControlled && state.IsPuppeteerControlled)
        {
            reason = VoiceProximityReason.PuppeteerControlled;
            return true;
        }

        return false;
    }

    private static string ToDisplayReason(VoiceProximityReason reason)
        => reason switch
        {
            VoiceProximityReason.Blackmailed => "Blackmailed",
            VoiceProximityReason.BlackmailedNextRound => "Blackmailed",
            VoiceProximityReason.Jailed => "Jailed",
            VoiceProximityReason.ParasiteControlled => "Parasite Controlled",
            VoiceProximityReason.PuppeteerControlled => "Puppeteer Controlled",
            VoiceProximityReason.Swooped => "Swooped",
            VoiceProximityReason.GlitchHacked => "Glitch Hacked",
            VoiceProximityReason.GracePeriod => "Caller Has Floor",
            VoiceProximityReason.RoleMuted => "Role Muted",
            _ => "Role Muted",
        };

    private static void TrackMeetingBlackmailedPlayers()
    {
        foreach (var pair in RoleStateCache)
        {
            if (pair.Value.IsBlackmailed)
                MeetingBlackmailedPlayers.Add(pair.Key);
        }
    }

    private static void SetJailVoiceAllowed(byte playerId, bool allowed)
    {
        if (allowed) JailVoiceAllowed.Add(playerId);
        else JailVoiceAllowed.Remove(playerId);
    }

    private static void SendJailVoiceAllowed(byte jailedPlayerId, bool allowed)
    {
        try
        {
            VoiceChatRoom.SendJailVoicePacket(jailedPlayerId, allowed);
            VoiceDiagnostics.Log("jailvoice.rpc.sent", $"jailee={jailedPlayerId} allowed={allowed}");
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.DebugError($"[VC] Jail voice send failed: {ex.Message}");
        }
    }

    internal static bool IsJailorValid(byte jailorId)
    {
        var jailor = FindPlayer(jailorId);
        return jailor != null && jailor.Data?.IsDead != true && IsJailor(jailor);
    }

    private static bool IsJailor(PlayerControl? player)
    {
        string? roleName = player?.Data?.Role?.GetType().FullName;
        return roleName == JailorRoleName;
    }

    // Repairs lost/rejected/pruned applies on any client (one bad frame is otherwise permanent); mirrors radio heartbeat.
    private static void MaybeResendJailVoiceHeartbeat(VoiceRoomSettingsSnapshot settings)
    {
        if (JailVoiceAllowed.Count == 0) return;
        if (!settings.MuteJailedInMeetings || !settings.JailorCanUnmuteJailed) return;
        if (Time.time < _nextJailVoiceHeartbeatTime) return;
        _nextJailVoiceHeartbeatTime = Time.time + JailVoiceHeartbeatSeconds;

        var local = PlayerControl.LocalPlayer;
        if (local == null || local.Data?.IsDead == true || !IsJailor(local)) return;

        foreach (byte playerId in new List<byte>(JailVoiceAllowed))
        {
            var player = FindPlayer(playerId);
            if (player == null || player.Data?.IsDead == true) continue;
            if (!TryGetJailorId(player, out byte jailorId) || jailorId != local.PlayerId) continue;
            try
            {
                VoiceChatRoom.SendJailVoicePacket(playerId, true);
                VoiceDiagnostics.Log("jailvoice.rpc.heartbeat", $"jailee={playerId}");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.DebugError($"[VC] Jail voice heartbeat failed: {ex.Message}");
            }
        }
    }

    private static void PruneJailVoiceAllowed(VoiceRoomSettingsSnapshot settings)
    {
        if (JailVoiceAllowed.Count == 0) return;
        if (!settings.MuteJailedInMeetings || !settings.JailorCanUnmuteJailed)
        {
            JailVoiceAllowed.Clear();
            return;
        }

        foreach (byte playerId in new List<byte>(JailVoiceAllowed))
        {
            var player = FindPlayer(playerId);
            if (player == null || player.Data?.IsDead == true || !TryGetJailorId(player, out byte jailorId) || !IsJailorValid(jailorId))
                JailVoiceAllowed.Remove(playerId);
        }
    }

    private static void PrunePostMeetingBlackmailedPlayers()
    {
        if (PostMeetingBlackmailedPlayers.Count == 0) return;
        bool removed = false;

        foreach (byte playerId in new List<byte>(PostMeetingBlackmailedPlayers))
        {
            var player = FindPlayer(playerId);
            if (player == null || player.Data?.IsDead == true || player.Data?.Disconnected == true)
            {
                PostMeetingBlackmailedPlayers.Remove(playerId);
                removed = true;
            }
        }

        if (removed)
            InvalidateRoleStateCache();
    }

}
