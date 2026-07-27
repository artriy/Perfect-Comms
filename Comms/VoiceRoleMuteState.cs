using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Mod-agnostic voice state. Role mods project their behavior through PerfectCommsApi; this class
/// retains only base Among Us traits, global API gates, built-in impostor radio, and grace period.
/// </summary>
internal static class VoiceRoleMuteState
{
    private static byte _gracePeriodCallerId = byte.MaxValue;
    private static float _gracePeriodDeadline;
    private static float _gracePeriodSeconds;
    private static bool _gracePeriodArmed;

    internal static void Update()
    {
        var phase = VoiceSceneState.ResolvePhase();
        if (VoiceSceneState.IsMeetingVoicePhase(phase))
        {
            if (_gracePeriodCallerId != byte.MaxValue && !_gracePeriodArmed && MeetingHud.Instance != null)
            {
                _gracePeriodDeadline = Time.time + _gracePeriodSeconds;
                _gracePeriodArmed = true;
            }
            return;
        }

        if (_gracePeriodArmed && phase is VoiceGamePhase.Tasks or VoiceGamePhase.Lobby)
            ClearGracePeriod();
    }

    internal static bool IsLocalVoiceBlocked()
        => IsLocalVoiceBlocked(VoiceSceneState.ResolvePhase());

    internal static bool IsLocalVoiceBlocked(VoiceGamePhase phase)
        => TryGetLocalVoiceBlockReason(phase, out _);

    internal static bool IsLocalMeetingVoiceBlocked()
        => TryGetLocalMeetingVoiceBlockReason(out _);


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

        var data = local.Data;
        bool baseDead = data != null && (data.IsDead || data.Role?.IsDead == true);
        VoicePlayerTraits traits = VoiceModRegistry.ResolvePlayerTraits(
            local,
            VoiceModBridge.ToApiPhase(phase),
            isLocal: true,
            baseDead);
        bool voiceDead = baseDead || (traits & VoicePlayerTraits.VoiceDead) != 0;

        if (!VoiceModRegistry.LocalGate(
                local,
                VoiceModBridge.ToApiPhase(phase),
                voiceDead,
                out var modReason))
            return false;

        reason = string.IsNullOrEmpty(modReason) ? "Role Muted" : modReason;
        return true;
    }

    internal static bool TryGetLocalMeetingVoiceBlockReason(out string reason)
    {
        if (!VoiceSceneState.IsMeetingVoicePhase(VoiceSceneState.ResolvePhase()))
        {
            reason = string.Empty;
            return false;
        }

        return TryGetLocalVoiceBlockReason(out reason);
    }

    internal static bool IsMeetingVoiceBlocked(VoicePlayerSnapshot player)
        => IsMeetingVoiceBlocked(player, VoiceSceneState.ResolvePhase());

    internal static bool IsMeetingVoiceBlocked(VoicePlayerSnapshot player, VoiceGamePhase phase)
        => VoiceSceneState.IsMeetingVoicePhase(phase) && !player.IsDead && player.External.Muted;

    internal static VoiceProximityReason GetMeetingBlockReason(VoicePlayerSnapshot player)
        => GetMeetingBlockReason(player, VoiceSceneState.ResolvePhase());

    internal static VoiceProximityReason GetMeetingBlockReason(
        VoicePlayerSnapshot player,
        VoiceGamePhase phase)
        => player.External.Muted
            ? VoiceProximityReason.RoleMuted
            : VoiceProximityReason.MeetingLiving;

    internal static bool IsTaskVoiceBlocked(VoicePlayerSnapshot player)
        => !player.IsDead && player.External.Muted;

    internal static VoiceProximityReason GetTaskBlockReason(VoicePlayerSnapshot player)
        => player.External.Muted
            ? VoiceProximityReason.RoleMuted
            : VoiceProximityReason.Proximity;


    internal static bool IsVoiceImpostor(PlayerControl? player)
    {
        if (player?.Data?.Role?.IsImpostor == true)
            return true;
        if (player == null)
            return false;

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
        => GetFirstTeamRadioChannel(player) != VoiceTeamRadioChannel.None;

    internal static VoiceTeamRadioChannel GetFirstTeamRadioChannel(PlayerControl? player)
    {
        foreach (var channel in VoiceTeamRadioChannels.Order)
            if (CanUseTeamRadioChannel(player, channel))
                return channel;
        return VoiceTeamRadioChannel.None;
    }

    internal static VoiceTeamRadioChannel GetNextTeamRadioChannel(
        PlayerControl? player,
        VoiceTeamRadioChannel current)
    {
        int currentIndex = System.Array.IndexOf(VoiceTeamRadioChannels.Order, current);
        for (int i = 1; i <= VoiceTeamRadioChannels.Order.Length; i++)
        {
            int index = (currentIndex + i + VoiceTeamRadioChannels.Order.Length) %
                        VoiceTeamRadioChannels.Order.Length;
            var candidate = VoiceTeamRadioChannels.Order[index];
            if (CanUseTeamRadioChannel(player, candidate))
                return candidate;
        }

        return VoiceTeamRadioChannel.None;
    }

    internal static bool CanUseTeamRadioChannel(
        PlayerControl? player,
        VoiceTeamRadioChannel channel)
        => player != null &&
           VoiceRoomSettingsState.Current.TeamRadio &&
           channel == VoiceTeamRadioChannel.Impostors &&
           VoiceRoomSettingsState.Current.TeamRadioImpostors &&
           IsVoiceImpostor(player);


    internal static void Reset() => ClearGracePeriod();

    internal static void OnMeetingStarted(byte callerId)
    {
        var settings = VoiceRoomSettingsState.Current;
        if (!settings.GracePeriodEnabled || settings.GracePeriodSeconds <= 0f)
        {
            ClearGracePeriod();
            return;
        }

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
        => _gracePeriodCallerId != byte.MaxValue &&
           _gracePeriodArmed &&
           VoiceRoomSettingsState.Current.GracePeriodEnabled &&
           MeetingHud.Instance != null &&
           Time.time < _gracePeriodDeadline;

    internal static byte GracePeriodCallerId => _gracePeriodCallerId;

    internal static int GracePeriodSecondsRemaining
        => IsGracePeriodActive
            ? Mathf.Max(1, Mathf.CeilToInt(_gracePeriodDeadline - Time.time))
            : 0;
}
