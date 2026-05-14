using System;
using System.Collections.Generic;
using HarmonyLib;
using MiraAPI.Modifiers;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRoleMuteState
{
    private const string BlackmailedModifierName    = "TownOfUs.Modifiers.Impostor.BlackmailedModifier";
    private const string JailedModifierName         = "TownOfUs.Modifiers.Crewmate.JailedModifier";
    private const string JailorRoleName             = "TownOfUs.Roles.Crewmate.JailorRole";
    private const string MediumRoleName             = "TownOfUs.Roles.Crewmate.MediumRole";
    private const string ParasiteRoleName           = "TownOfUs.Roles.Neutral.ParasiteRole";
    private const string ParasiteOvertakeModName    = "TownOfUs.Modifiers.Neutral.ParasiteOvertakeModifier";
    private const string VampireRoleName            = "TownOfUs.Roles.Impostor.VampireRole";

    // Tracks which players had blackmail applied this round so we can carry it forward.
    private static readonly HashSet<byte> BlackmailedThisRound   = new();
    private static readonly HashSet<byte> BlackmailedLastRound   = new();

    private static readonly Dictionary<string, Type?> TypeCache = new();
    private static readonly HashSet<byte> JailVoiceAllowed = new();
    private static bool _wasInMeeting;

    // ── Per-round blackmail tracking ──────────────────────────────────────────

    /// <summary>
    /// Called when a new meeting starts. Rotates blackmail sets so players who
    /// were blackmailed last round are remembered for the "mute next round" option.
    /// </summary>
    internal static void OnMeetingStart()
    {
        BlackmailedLastRound.Clear();
        foreach (var id in BlackmailedThisRound)
            BlackmailedLastRound.Add(id);
        BlackmailedThisRound.Clear();

        // Populate this round's set from live modifier state.
        foreach (var player in PlayerControl.AllPlayerControls)
        {
            if (player != null && HasModifier(player, BlackmailedModifierName))
                BlackmailedThisRound.Add(player.PlayerId);
        }

        JailVoiceAllowed.Clear();
        _wasInMeeting = true;
    }

    internal static bool IsBlackmailedNextRound(PlayerControl? player)
    {
        if (player == null) return false;
        // A player is "blackmailed next round" if they were in last round's set
        // but are NOT currently blackmailed (the modifier has already been removed).
        return BlackmailedLastRound.Contains(player.PlayerId)
               && !HasModifier(player, BlackmailedModifierName);
    }

    // ── Main update tick ───────────────────────────────────────────────────────

    internal static void Update()
    {
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

    // ── Local player checks ────────────────────────────────────────────────────

    internal static bool IsLocalMeetingVoiceBlocked()
        => TryGetLocalMeetingVoiceBlockReason(out _);

    internal static bool TryGetLocalMeetingVoiceBlockReason(out string reason)
    {
        reason = string.Empty;
        Update();

        var local = PlayerControl.LocalPlayer;
        if (local == null || MeetingHud.Instance == null || local.Data?.IsDead == true)
            return false;

        var opts = VoiceChatGameOptions.Instance;

        if (opts.BlackmailMutesRound.Value && HasModifier(local, BlackmailedModifierName))
        {
            reason = "Blackmailed";
            return true;
        }

        if (opts.BlackmailMutesNextRound.Value && IsBlackmailedNextRound(local))
        {
            reason = "Blackmailed (lingering)";
            return true;
        }

        if (opts.JailorCanControlVoice.Value
            && TryGetJailorId(local, out byte jailorId)
            && IsJailorValid(jailorId)
            && !JailVoiceAllowed.Contains(local.PlayerId))
        {
            reason = "Jailed";
            return true;
        }

        return false;
    }

    // ── Remote player snapshot checks ─────────────────────────────────────────

    internal static bool IsMeetingVoiceBlocked(VoicePlayerSnapshot player)
    {
        if (MeetingHud.Instance == null || player.IsDead)
            return false;

        var opts = VoiceChatGameOptions.Instance;

        if (opts.BlackmailMutesRound.Value && player.IsBlackmailed)
            return true;

        if (opts.BlackmailMutesNextRound.Value && player.IsBlackmailedNextRound)
            return true;

        if (opts.JailorCanControlVoice.Value
            && player.IsJailed
            && IsJailorValid(player.JailorId)
            && !JailVoiceAllowed.Contains(player.PlayerId))
            return true;

        return false;
    }

    internal static VoiceProximityReason GetMeetingBlockReason(VoicePlayerSnapshot player)
        => (player.IsBlackmailed || player.IsBlackmailedNextRound)
            ? VoiceProximityReason.Blackmailed
            : VoiceProximityReason.Jailed;

    // ── Task-phase mute checks ─────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the target player should be muted during the task phase
    /// for a role-specific reason (e.g. Parasite overtake).
    /// </summary>
    internal static bool IsTaskPhaseVoiceBlocked(VoicePlayerSnapshot player)
    {
        if (VoiceChatGameOptions.Instance.ParasiteVictimMuted.Value && player.IsParasiteVictim)
            return true;
        return false;
    }

    // ── Medium spiritual state ─────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the local player is a Medium in spiritual (ghost-talk) state,
    /// meaning they should be treated as dead for voice routing purposes.
    /// </summary>
    internal static bool IsLocalMediumSpiritual()
    {
        if (!VoiceChatGameOptions.Instance.MediumSpiritualVoice.Value)
            return false;
        var local = PlayerControl.LocalPlayer;
        if (local == null || local.Data?.IsDead == true) return false;
        return IsMediumInSpiritualState(local);
    }

    internal static bool IsMediumInSpiritualState(PlayerControl? player)
    {
        if (player == null) return false;
        var roleName = player.Data?.Role?.GetType().FullName;
        if (roleName == null) return false;
        if (!roleName.EndsWith(".MediumRole", StringComparison.Ordinal)
            && roleName != MediumRoleName)
            return false;

        // The Medium's spiritual state is exposed via a property or field on the role.
        // Try "IsSpiritualState", "SpiritualState", "InSpiritualState" -- check whichever exists.
        var role = player.Data!.Role;
        foreach (var name in new[] { "IsSpiritualState", "SpiritualState", "InSpiritualState", "IsSpiritual" })
        {
            var prop = role.GetType().GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop?.GetValue(role) is bool b) return b;

            var field = role.GetType().GetField(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(role) is bool fb) return fb;
        }

        return false;
    }

    // ── Vampire radio check ────────────────────────────────────────────────────

    internal static bool IsVampire(PlayerControl? player)
    {
        if (player == null) return false;
        var roleName = player.Data?.Role?.GetType().FullName;
        return roleName != null
               && (roleName == VampireRoleName
                   || roleName.EndsWith(".VampireRole", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true if the given player is allowed to use the Team Chat Radio channel.
    /// Impostors always qualify (when the option is on). Vampires qualify when the
    /// VampireTeamChatRadio option is also enabled.
    /// </summary>
    internal static bool CanUseTeamChatRadio(VoicePlayerSnapshot player)
    {
        if (!VoiceChatGameOptions.Instance.ImpostorPrivateRadio.Value) return false;
        if (player.IsImpostor) return true;
        if (player.IsVampire && VoiceChatGameOptions.Instance.VampireTeamChatRadio.Value) return true;
        return false;
    }

    internal static bool CanLocalPlayerUseTeamChatRadio()
    {
        var local = PlayerControl.LocalPlayer;
        if (local == null || local.Data?.IsDead == true) return false;
        if (!VoiceChatGameOptions.Instance.ImpostorPrivateRadio.Value) return false;
        if (local.Data!.Role?.IsImpostor == true) return true;
        if (VoiceChatGameOptions.Instance.VampireTeamChatRadio.Value && IsVampire(local)) return true;
        return false;
    }

    // ── Parasite check ────────────────────────────────────────────────────────

    internal static bool IsParasiteVictim(PlayerControl? player)
    {
        if (player == null) return false;
        return HasModifier(player, ParasiteOvertakeModName);
    }

    // ── Jailor helpers ─────────────────────────────────────────────────────────

    internal static bool IsBlackmailed(PlayerControl? player)
        => HasModifier(player, BlackmailedModifierName);

    internal static bool TryGetJailorId(PlayerControl? player, out byte jailorId)
    {
        jailorId = byte.MaxValue;
        var modifier = GetModifier(player, JailedModifierName);
        if (modifier == null) return false;

        try
        {
            object? value = modifier.GetType().GetProperty("JailorId")?.GetValue(modifier);
            if (value is byte id)
            {
                jailorId = id;
                return true;
            }
        }
        catch { }

        return false;
    }

    internal static bool CanLocalJailorUnmute(out byte jailedPlayerId)
    {
        jailedPlayerId = byte.MaxValue;
        Update();

        if (!VoiceChatGameOptions.Instance.JailorCanControlVoice.Value) return false;

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
        if (!CanLocalJailorUnmute(out byte jailedPlayerId)) return;
        SetJailVoiceAllowed(jailedPlayerId, true);
        SendJailVoiceAllowed(jailedPlayerId, true);
        VoiceChatHudState.ApplyMicState();
    }

    internal static void ApplyRemoteJailVoice(byte jailorId, byte jailedPlayerId, bool allowed)
    {
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
            if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return;
            var w = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                VoiceProtocol.AudioRpcId,
                Hazel.SendOption.Reliable,
                -1);
            w.Write((byte)VoicePacketType.JailVoice);
            w.Write(jailedPlayerId);
            w.Write(allowed);
            AmongUsClient.Instance.FinishRpcImmediately(w);
        }
        catch (Exception ex)
        {
            VoiceChatPluginMain.Logger.LogError($"[VC] Jail voice RPC send failed: {ex.Message}");
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
        return roleName == JailorRoleName || roleName?.EndsWith(".JailorRole", StringComparison.Ordinal) == true;
    }

    private static void PruneJailVoiceAllowed()
    {
        if (JailVoiceAllowed.Count == 0) return;

        foreach (byte playerId in new List<byte>(JailVoiceAllowed))
        {
            var player = FindPlayer(playerId);
            if (player == null || player.Data?.IsDead == true
                || !TryGetJailorId(player, out byte jailorId)
                || !IsJailorValid(jailorId))
                JailVoiceAllowed.Remove(playerId);
        }
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    private static bool HasModifier(PlayerControl? player, string typeName)
        => GetModifier(player, typeName) != null;

    private static BaseModifier? GetModifier(PlayerControl? player, string typeName)
    {
        if (player == null) return null;
        var type = ResolveType(typeName);
        if (type == null) return null;

        try { return player.GetModifier(type); }
        catch { return null; }
    }

    private static Type? ResolveType(string fullName)
    {
        if (TypeCache.TryGetValue(fullName, out var cached))
            return cached;

        Type? type = AccessTools.TypeByName(fullName);
        if (type == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(fullName, false);
                if (type != null) break;
            }
        }

        TypeCache[fullName] = type;
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