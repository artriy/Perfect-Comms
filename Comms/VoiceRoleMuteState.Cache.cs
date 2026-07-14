using System;
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
            // AllPlayerControls can be invalidated mid-enumeration during scene transitions. Keep the
            // partial/empty cache and retry next refresh instead of throwing into the UpdateHud path.
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

    private static System.Reflection.MethodInfo? _getModifierMethod;
    private static Type? _getModifierMethodOwner;
    private static object?[]? _getModifierArgs;

    private static object? GetModifier(PlayerControl player, Type? type)
    {
        if (type == null) return null;
        var extType = ResolveType("MiraAPI.Modifiers.ModifierExtensions");
        if (extType == null) return null;
        var method = ResolveGetModifierMethod(extType);
        if (method == null) return null;
        var args = _getModifierArgs;
        if (args == null || args.Length < 2) return null;
        try
        {
            args[0] = player;
            args[1] = type;
            return method.Invoke(null, args);
        }
        catch
        {
            return null;
        }
    }

    private static System.Reflection.MethodInfo? ResolveGetModifierMethod(Type extType)
    {
        if (_getModifierMethod != null && _getModifierMethodOwner == extType)
            return _getModifierMethod;
        try
        {
            foreach (var m in extType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != "GetModifier" || m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length >= 2 && ps[0].ParameterType == typeof(PlayerControl) && ps[1].ParameterType == typeof(Type))
                {
                    _getModifierMethod = m;
                    _getModifierMethodOwner = extType;
                    _getModifierArgs = new object?[ps.Length];
                    return m;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsGlitchHackActive(object? modifier)
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

    private static bool IsHypnotisedHysteriaActive(object? modifier)
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
        int assemblyGeneration = SoftDependencyTypeResolver.AssemblyGeneration;
        if (!ShouldRefreshSupportedModTypes(
                _supportedModTypesResolved,
                phaseChanged,
                joinedNewLobby,
                _resolvedAssemblyGeneration,
                assemblyGeneration))
            return;

        _resolvedPhase = phase;
        _resolvedGameId = gameId;
        _resolvedAssemblyGeneration = assemblyGeneration;
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

    internal static bool ShouldRefreshSupportedModTypes(
        bool alreadyResolved,
        bool phaseChanged,
        bool joinedNewLobby,
        int resolvedAssemblyGeneration,
        int currentAssemblyGeneration)
        => !alreadyResolved
           || phaseChanged
           || joinedNewLobby
           || resolvedAssemblyGeneration != currentAssemblyGeneration;

    private static Type? ResolveType(string fullName)
        => SoftDependencyTypeResolver.ResolveExact(fullName);

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

    private static byte GetLoverPartnerId(object? modifier)
    {
        if (modifier == null) return byte.MaxValue;

        if (TryGetLoverPartner(modifier, "OtherLover", useProperty: true, out var partner) ||
            TryGetLoverPartner(modifier, "GetOtherLover", useProperty: false, out partner))
        {
            return partner.PlayerId;
        }

        return byte.MaxValue;
    }

    private static bool TryGetLoverPartner(object modifier, string memberName, bool useProperty, out PlayerControl partner)
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

    private static byte GetMediatingMediumId(object? modifier)
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

    // For the LOCAL player only: if they are actively controlling another player as a Puppeteer or Parasite, returns
    // the control-hearing mode and the controlled victim. The controlling player is read by reflection from the
    // victim's <Control>Modifier.Controller property (PerfectComms has no compile-time TOU reference). Fails closed
    // to None so a missing/garbage controller never relocates the listener.
    internal static bool TryGetLocalControlledVictim(PlayerControl? local, out VoiceControlHearingMode mode, out PlayerControl victim)
    {
        mode = VoiceControlHearingMode.None;
        victim = null!;
        if (local == null) return false;
        try
        {
            RefreshSupportedModTypesIfNeeded();
            if (_puppeteerControlModifierType == null && _parasiteInfectedModifierType == null)
                return false;
            byte localId = local.PlayerId;
            foreach (var player in PlayerControl.AllPlayerControls)
            {
                if (player == null) continue;
                // Puppeteer (swap) takes precedence; one player can't hold both control modifiers at once.
                if (ControllerMatches(player, _puppeteerControlModifierType, localId))
                {
                    mode = VoiceControlHearingMode.PuppeteerSwap;
                    victim = player;
                    return true;
                }
                if (ControllerMatches(player, _parasiteInfectedModifierType, localId))
                {
                    mode = VoiceControlHearingMode.ParasiteAdditive;
                    victim = player;
                    return true;
                }
            }
        }
        catch
        {
            mode = VoiceControlHearingMode.None;
            victim = null!;
        }
        return false;
    }

    private static bool ControllerMatches(PlayerControl victim, Type? modifierType, byte controllerId)
    {
        var modifier = GetModifier(victim, modifierType);
        if (modifier == null) return false;
        try
        {
            object? value = modifier.GetType().GetProperty("Controller")?.GetValue(modifier);
            return value is PlayerControl controller && controller != null && controller.PlayerId == controllerId;
        }
        catch
        {
            return false;
        }
    }
}
