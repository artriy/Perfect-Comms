using System;
using HarmonyLib;
using MiraAPI.Modifiers;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static partial class VoiceRoleMuteState
{
    private static void RefreshRoleStateCacheIfNeeded(bool force = false)
    {
        RefreshSupportedModTypesIfNeeded();
        if (_blackmailedModifierType == null &&
            _jailedModifierType == null &&
            _parasiteInfectedModifierType == null &&
            _puppeteerControlModifierType == null &&
            _crewpostorModifierType == null &&
            _loverModifierType == null &&
            _swoopModifierType == null &&
            _glitchHackedModifierType == null &&
            _eclipsalBlindModifierType == null &&
            _grenadierFlashModifierType == null &&
            _hypnotisedModifierType == null &&
            _vampireRoleType == null &&
            _mediumRoleType == null &&
            _mediatedModifierType == null &&
            PostMeetingBlackmailedPlayers.Count == 0)
        {
            RoleStateCache.Clear();
            return;
        }

        if (!force && Time.time < _nextRoleStateRefreshTime)
            return;

        _nextRoleStateRefreshTime = Time.time + RoleStateRefreshInterval;
        RoleStateCache.Clear();
        try
        {
            foreach (var player in PlayerControl.AllPlayerControls)
            {
                if (player == null) continue;
                RoleStateCache[player.PlayerId] = ReadRoleState(player);
            }
        }
        catch
        {
            // AllPlayerControls can be invalidated by a scene transition mid-enumeration. Keep the
            // partial/empty cache and retry on the next refresh rather than throwing into the
            // unguarded per-frame UpdateHud path.
        }
    }

    private static CachedRoleState ReadRoleState(PlayerControl player)
    {
        bool isBlackmailed = GetModifier(player, _blackmailedModifierType) != null ||
                              MeetingBlackmailedPlayers.Contains(player.PlayerId);
        bool isParasiteControlled = GetModifier(player, _parasiteInfectedModifierType) != null;
        bool isPuppeteerControlled = GetModifier(player, _puppeteerControlModifierType) != null;
        bool isCrewpostor = GetModifier(player, _crewpostorModifierType) != null;
        bool isVampire = IsRole(player, _vampireRoleType, VampireRoleName);
        bool isMedium = IsRole(player, _mediumRoleType, MediumRoleName);
        Vector2 mediumSpiritPosition = default;
        bool hasMediumSpirit = isMedium && player.Data?.IsDead != true && TryGetMediumSpiritPosition(player, out mediumSpiritPosition);
        var loverModifier = GetModifier(player, _loverModifierType);
        bool isLover = loverModifier != null;
        byte loverPartnerId = GetLoverPartnerId(loverModifier);
        bool isSwooped = GetModifier(player, _swoopModifierType) != null;
        bool isGlitchHacked = IsGlitchHackActive(GetModifier(player, _glitchHackedModifierType));
        var mediatedModifier = GetModifier(player, _mediatedModifierType);
        bool isMediatedGhost = mediatedModifier != null && player.Data?.IsDead == true;
        byte mediatingMediumId = isMediatedGhost ? GetMediatingMediumId(mediatedModifier) : byte.MaxValue;
        bool isBlackmailedNextRound = PostMeetingBlackmailedPlayers.Contains(player.PlayerId);
        byte jailorId = byte.MaxValue;
        bool isJailed = false;

        var modifier = GetModifier(player, _jailedModifierType);
        if (modifier != null)
        {
            try
            {
                object? value = modifier.GetType().GetProperty("JailorId")?.GetValue(modifier);
                if (value is byte id)
                {
                    jailorId = id;
                    isJailed = true;
                }
            }
            catch
            {
                // ignored; role integration should fail closed per player, not per frame
            }
        }

        return new(
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
            isMedium,
            hasMediumSpirit,
            mediumSpiritPosition,
            isMediatedGhost,
            mediatingMediumId);
    }

    private static BaseModifier? GetModifier(PlayerControl player, Type? type)
    {
        if (type == null) return null;
        try
        {
            return player.GetModifier(type);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGlitchHackActive(BaseModifier? modifier)
    {
        if (modifier == null) return false;
        try
        {
            object? value = modifier.GetType().GetProperty("ShouldHideHacked")?.GetValue(modifier);
            return value is bool shouldHideHacked ? !shouldHideHacked : true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHypnotisedHysteriaActive(BaseModifier? modifier)
    {
        if (modifier == null) return false;
        try
        {
            object? value = modifier.GetType().GetProperty("HysteriaActive")?.GetValue(modifier);
            return value is bool hysteriaActive && hysteriaActive;
        }
        catch
        {
            return false;
        }
    }

    private static void RefreshSupportedModTypesIfNeeded()
    {
        VoiceGamePhase phase = VoiceSceneState.ResolvePhase();
        int gameId = AmongUsClient.Instance?.GameId ?? 0;
        bool shouldProbe = phase is VoiceGamePhase.Lobby
            or VoiceGamePhase.Intro
            or VoiceGamePhase.Tasks
            or VoiceGamePhase.Meeting
            or VoiceGamePhase.Exile;
        if (!shouldProbe) return;

        bool phaseChanged = phase != _resolvedPhase;
        bool joinedNewLobby = gameId != 0 && gameId != _resolvedGameId;
        if (_supportedModTypesResolved && !phaseChanged && !joinedNewLobby)
            return;

        _resolvedPhase = phase;
        _resolvedGameId = gameId;
        _blackmailedModifierType = ResolveType(BlackmailedModifierName);
        _jailedModifierType = ResolveType(JailedModifierName);
        _parasiteInfectedModifierType = ResolveType(ParasiteInfectedModifierName);
        _puppeteerControlModifierType = ResolveType(PuppeteerControlModifierName);
        _crewpostorModifierType = ResolveType(CrewpostorModifierName);
        _loverModifierType = ResolveType(LoverModifierName);
        _swoopModifierType = ResolveType(SwoopModifierName);
        _glitchHackedModifierType = ResolveType(GlitchHackedModifierName);
        _eclipsalBlindModifierType = ResolveType(EclipsalBlindModifierName);
        _grenadierFlashModifierType = ResolveType(GrenadierFlashModifierName);
        _hypnotisedModifierType = ResolveType(HypnotisedModifierName);
        _vampireRoleType = ResolveType(VampireRoleName);
        _mediumRoleType = ResolveType(MediumRoleName);
        _mediatedModifierType = ResolveType(MediatedModifierName);
        _supportedModTypesResolved = true;
        InvalidateRoleStateCache();
    }

    private static Type? ResolveType(string fullName)
    {
        Type? type = AccessTools.TypeByName(fullName);
        if (type != null) return type;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = asm.GetType(fullName, false);
            if (type != null) break;
        }

        return type;
    }

    private static bool IsRole(PlayerControl player, Type? type, string fullName)
    {
        var role = player.Data?.Role;
        if (role == null) return false;

        try
        {
            var roleType = role.GetType();
            if (roleType.FullName == fullName)
                return true;
            return type != null && (roleType == type || type.IsAssignableFrom(roleType));
        }
        catch
        {
            return false;
        }
    }

    private static byte GetLoverPartnerId(BaseModifier? modifier)
    {
        if (modifier == null) return byte.MaxValue;

        if (TryGetLoverPartner(modifier, "OtherLover", useProperty: true, out var partner) ||
            TryGetLoverPartner(modifier, "GetOtherLover", useProperty: false, out partner))
        {
            return partner.PlayerId;
        }

        return byte.MaxValue;
    }

    private static bool TryGetLoverPartner(BaseModifier modifier, string memberName, bool useProperty, out PlayerControl partner)
    {
        partner = null!;
        try
        {
            object? value = useProperty
                ? modifier.GetType().GetProperty(memberName)?.GetValue(modifier)
                : modifier.GetType().GetMethod(memberName, Type.EmptyTypes)?.Invoke(modifier, null);
            if (value is not PlayerControl player)
                return false;

            partner = player;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetMediumSpiritPosition(PlayerControl player, out Vector2 position)
    {
        position = default;
        var role = player.Data?.Role;
        if (role == null) return false;

        try
        {
            object? spirit = role.GetType().GetProperty("Spirit")?.GetValue(role);
            switch (spirit)
            {
                case Component component:
                    position = component.transform.position;
                    return true;
                case GameObject gameObject:
                    position = gameObject.transform.position;
                    return true;
            }

            if (spirit == null) return false;
            if (spirit.GetType().GetProperty("transform")?.GetValue(spirit) is Transform transform)
            {
                position = transform.position;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static byte GetMediatingMediumId(BaseModifier? modifier)
    {
        if (modifier == null) return byte.MaxValue;

        try
        {
            object? value = modifier.GetType().GetProperty("MediumId")?.GetValue(modifier);
            return value is byte id ? id : byte.MaxValue;
        }
        catch
        {
            return byte.MaxValue;
        }
    }

    private static void InvalidateRoleStateCache()
    {
        _nextRoleStateRefreshTime = 0f;
        RoleStateCache.Clear();
    }

    private static PlayerControl? FindPlayer(byte playerId)
    {
        try
        {
            foreach (var player in PlayerControl.AllPlayerControls)
                if (player != null && player.PlayerId == playerId)
                    return player;
        }
        catch
        {
            // AllPlayerControls can be invalidated mid-enumeration during scene transitions.
        }
        return null;
    }
}
