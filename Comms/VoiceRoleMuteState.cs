using System;
using System.Collections.Generic;
using HarmonyLib;
using MiraAPI.Modifiers;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRoleMuteState
{
    private const string BlackmailedModifierName = "TownOfUs.Modifiers.Impostor.BlackmailedModifier";
    private const string JailedModifierName = "TownOfUs.Modifiers.Crewmate.JailedModifier";
    private const string JailorRoleName = "TownOfUs.Roles.Crewmate.JailorRole";
    private const float RoleStateRefreshInterval = 0.25f;

    private static readonly HashSet<byte> JailVoiceAllowed = new();
    private static readonly Dictionary<byte, CachedRoleState> RoleStateCache = new();
    private static Type? _blackmailedModifierType;
    private static Type? _jailedModifierType;
    private static bool _supportedModTypesResolved;
    private static int _resolvedGameId = int.MinValue;
    private static VoiceGamePhase _resolvedPhase = VoiceGamePhase.Unknown;
    private static float _nextRoleStateRefreshTime;
    private static bool _wasInMeeting;

    private readonly record struct CachedRoleState(bool IsBlackmailed, bool IsJailed, byte JailorId);

    internal static void Update()
    {
        RefreshRoleStateCacheIfNeeded();
        bool inMeeting = MeetingHud.Instance != null;
        if (!inMeeting)
        {
            if (_wasInMeeting)
                JailVoiceAllowed.Clear();
            _wasInMeeting = false;
            return;
        }

        _wasInMeeting = true;
        PruneJailVoiceAllowed();
    }

    internal static bool IsLocalMeetingVoiceBlocked()
        => TryGetLocalMeetingVoiceBlockReason(out _);

    internal static bool TryGetLocalMeetingVoiceBlockReason(out string reason)
    {
        reason = string.Empty;
        Update();

        var local = PlayerControl.LocalPlayer;
        if (local == null || MeetingHud.Instance == null || local.Data?.IsDead == true)
            return false;

        GetPlayerRoleState(local, out bool isBlackmailed, out bool isJailed, out byte jailorId);
        if (isBlackmailed)
        {
            reason = "Blackmailed";
            return true;
        }

        if (isJailed && IsJailorValid(jailorId) && !JailVoiceAllowed.Contains(local.PlayerId))
        {
            reason = "Jailed";
            return true;
        }

        return false;
    }

    internal static bool IsMeetingVoiceBlocked(VoicePlayerSnapshot player)
    {
        if (MeetingHud.Instance == null || player.IsDead)
            return false;
        if (player.IsBlackmailed)
            return true;
        if (player.IsJailed && IsJailorValid(player.JailorId) && !JailVoiceAllowed.Contains(player.PlayerId))
            return true;
        return false;
    }

    internal static VoiceProximityReason GetMeetingBlockReason(VoicePlayerSnapshot player)
        => player.IsBlackmailed ? VoiceProximityReason.Blackmailed : VoiceProximityReason.Jailed;

    internal static bool IsBlackmailed(PlayerControl? player)
    {
        GetPlayerRoleState(player, out bool isBlackmailed, out _, out _);
        return isBlackmailed;
    }

    internal static bool TryGetJailorId(PlayerControl? player, out byte jailorId)
    {
        GetPlayerRoleState(player, out _, out bool isJailed, out jailorId);
        return isJailed;
    }

    internal static void GetPlayerRoleState(PlayerControl? player, out bool isBlackmailed, out bool isJailed, out byte jailorId)
    {
        isBlackmailed = false;
        isJailed = false;
        jailorId = byte.MaxValue;
        if (player == null) return;

        RefreshRoleStateCacheIfNeeded();
        if (!RoleStateCache.TryGetValue(player.PlayerId, out var state)) return;

        isBlackmailed = state.IsBlackmailed;
        isJailed = state.IsJailed;
        jailorId = state.JailorId;
    }

    internal static bool CanLocalJailorUnmute(out byte jailedPlayerId)
    {
        jailedPlayerId = byte.MaxValue;
        Update();

        var local = PlayerControl.LocalPlayer;
        if (local == null || MeetingHud.Instance == null || local.Data?.IsDead == true || !IsJailor(local))
            return false;

        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player == null || player.Data?.IsDead == true) continue;
            if (!TryGetJailorId(player, out byte jailorId) || jailorId != local.PlayerId) continue;
            if (!IsJailorValid(jailorId) || JailVoiceAllowed.Contains(player.PlayerId)) continue;

            jailedPlayerId = player.PlayerId;
            return true;
        }

        return false;
    }

    internal static void LocalJailorAllowVoice()
    {
        RefreshRoleStateCacheIfNeeded(force: true);
        if (!CanLocalJailorUnmute(out byte jailedPlayerId)) return;
        SetJailVoiceAllowed(jailedPlayerId, true);
        SendJailVoiceAllowed(jailedPlayerId, true);
        VoiceChatHudState.ApplyMicState();
    }

    internal static void ApplyRemoteJailVoice(byte jailorId, byte jailedPlayerId, bool allowed)
    {
        RefreshRoleStateCacheIfNeeded(force: true);
        var jailed = FindPlayer(jailedPlayerId);
        if (jailed == null || !TryGetJailorId(jailed, out byte actualJailorId) || actualJailorId != jailorId)
            return;
        if (!IsJailorValid(jailorId))
            return;

        SetJailVoiceAllowed(jailedPlayerId, allowed);
        VoiceChatHudState.ApplyMicState();
    }

    internal static bool IsJailVoiceAllowed(byte playerId)
        => JailVoiceAllowed.Contains(playerId);

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

    private static void PruneJailVoiceAllowed()
    {
        if (JailVoiceAllowed.Count == 0) return;

        foreach (byte playerId in new List<byte>(JailVoiceAllowed))
        {
            var player = FindPlayer(playerId);
            if (player == null || player.Data?.IsDead == true || !TryGetJailorId(player, out byte jailorId) || !IsJailorValid(jailorId))
                JailVoiceAllowed.Remove(playerId);
        }
    }

    private static void RefreshRoleStateCacheIfNeeded(bool force = false)
    {
        RefreshSupportedModTypesIfNeeded();
        if (_blackmailedModifierType == null && _jailedModifierType == null)
        {
            RoleStateCache.Clear();
            return;
        }

        if (!force && Time.time < _nextRoleStateRefreshTime)
            return;

        _nextRoleStateRefreshTime = Time.time + RoleStateRefreshInterval;
        RoleStateCache.Clear();
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player == null) continue;
            RoleStateCache[player.PlayerId] = ReadRoleState(player);
        }
    }

    private static CachedRoleState ReadRoleState(PlayerControl player)
    {
        bool isBlackmailed = GetModifier(player, _blackmailedModifierType) != null;
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

        return new(isBlackmailed, isJailed, jailorId);
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

    private static void RefreshSupportedModTypesIfNeeded()
    {
        VoiceGamePhase phase = VoiceSceneState.ResolvePhase();
        int gameId = AmongUsClient.Instance?.GameId ?? 0;
        bool shouldProbe = phase is VoiceGamePhase.Lobby
            or VoiceGamePhase.Intro
            or VoiceGamePhase.Tasks
            or VoiceGamePhase.Meeting;
        if (!shouldProbe) return;

        bool phaseChanged = phase != _resolvedPhase;
        bool joinedNewLobby = gameId != 0 && gameId != _resolvedGameId;
        if (_supportedModTypesResolved && !phaseChanged && !joinedNewLobby)
            return;

        _resolvedPhase = phase;
        _resolvedGameId = gameId;
        _blackmailedModifierType = ResolveType(BlackmailedModifierName);
        _jailedModifierType = ResolveType(JailedModifierName);
        _supportedModTypesResolved = true;
        _nextRoleStateRefreshTime = 0f;
        RoleStateCache.Clear();
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

    private static PlayerControl? FindPlayer(byte playerId)
    {
        foreach (var player in PlayerControl.AllPlayerControls)
            if (player != null && player.PlayerId == playerId)
                return player;
        return null;
    }
}
