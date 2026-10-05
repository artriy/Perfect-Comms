using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Mod-agnostic voice state. Role mods project their behavior through PerfectCommsApi; this class
/// retains only base Among Us traits, global API gates, built-in impostor radio, and grace period.
/// </summary>
internal static class VoiceRoleMuteState
{
    private static readonly MeetingVoiceFloor Floor = new();

    internal static void Update()
    {
        Floor.Update(
            VoiceSceneState.ResolvePhase(),
            MeetingHud.Instance != null,
            VoiceRoomSettingsState.Current.GracePeriodEnabled,
            Time.unscaledTime);
    }

    internal static bool IsLocalVoiceBlocked()
        => IsLocalVoiceBlocked(VoiceSceneState.ResolvePhase());

    internal static bool IsLocalVoiceBlocked(VoiceGamePhase phase)
        => TryGetLocalVoiceBlockReason(phase, out _);

    internal static bool IsLocalMeetingVoiceBlocked()
        => TryGetLocalMeetingVoiceBlockReason(out _);


    internal static bool IsVoiceDead(PlayerControl? player)
    {
        GetVoiceLifeState(player, VoiceSceneState.ResolvePhase(), out bool voiceDead, out _);
        return voiceDead;
    }

    internal static void GetVoiceLifeState(
        PlayerControl? player, VoiceGamePhase phase, out bool voiceDead, out bool voiceSpectator)
    {
        voiceDead = false;
        voiceSpectator = false;
        if (player == null)
            return;

        var data = player.Data;
        bool dataDead = data?.IsDead == true;
        bool roleDead = data?.Role?.IsDead == true;
        VoicePlayerTraits traits = VoiceModRegistry.ResolvePlayerTraits(
            player,
            VoiceModBridge.ToApiPhase(phase),
            player == PlayerControl.LocalPlayer,
            dataDead || roleDead);
        ResolveVoiceLifeState(dataDead, roleDead, traits, out voiceDead, out voiceSpectator);
    }

    internal static void ResolveVoiceLifeState(
        bool dataDead, bool roleDead, VoicePlayerTraits traits,
        out bool voiceDead, out bool voiceSpectator)
    {
        voiceDead = dataDead || roleDead || (traits & VoicePlayerTraits.VoiceDead) != 0;
        voiceSpectator = (roleDead && !dataDead) || (traits & VoicePlayerTraits.Spectator) != 0;
    }

    internal static bool TryGetLocalVoiceBlockReason(out string reason)
        => TryGetLocalVoiceBlockReason(VoiceSceneState.ResolvePhase(), out reason);

    internal static bool TryGetLocalVoiceBlockReason(VoiceGamePhase phase, out string reason)
        => TryGetLocalVoiceBlockReason(phase, out reason, out _, out _);

    internal static bool TryGetLocalVoiceBlockReason(
        VoiceGamePhase phase, out string reason, out bool voiceDead, out bool voiceSpectator)
    {
        reason = string.Empty;
        voiceDead = false;
        voiceSpectator = false;
        Update();

        var local = PlayerControl.LocalPlayer;
        if (local == null)
            return false;

        GetVoiceLifeState(local, phase, out voiceDead, out voiceSpectator);
        if (Floor.Blocks(local.PlayerId, voiceDead, phase, Time.unscaledTime))
        {
            reason = "Caller has the floor";
            return true;
        }

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
        => VoiceSceneState.IsMeetingVoicePhase(phase) &&
           ((!player.IsDead && player.External.Muted) ||
            (Floor.CallerId != byte.MaxValue && Floor.Blocks(player.PlayerId, player.IsDead, phase, Time.unscaledTime)));

    internal static VoiceProximityReason GetMeetingBlockReason(VoicePlayerSnapshot player)
        => GetMeetingBlockReason(player, VoiceSceneState.ResolvePhase());

    internal static VoiceProximityReason GetMeetingBlockReason(
        VoicePlayerSnapshot player,
        VoiceGamePhase phase)
        => player.External.Muted
            ? VoiceProximityReason.RoleMuted
            : VoiceProximityReason.GracePeriod;

    internal static bool IsTaskVoiceBlocked(VoicePlayerSnapshot player)
        => !player.IsDead && player.External.Muted;

    internal static VoiceProximityReason GetTaskBlockReason(VoicePlayerSnapshot player)
        => player.External.Muted
            ? VoiceProximityReason.RoleMuted
            : VoiceProximityReason.Proximity;



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
    {
        if (channel != VoiceTeamRadioChannel.Impostors || player?.Data?.Role?.IsImpostor != true)
            return false;
        var phase = VoiceSceneState.ResolvePhase();
        GetVoiceLifeState(player, phase, out bool voiceDead, out bool voiceSpectator);
        return CanUseTeamRadioChannel(
            VoiceRoomSettingsState.Current, phase, true,
            voiceDead, voiceSpectator, channel);
    }

    internal static bool CanUseTeamRadioChannel(
        VoiceRoomSettingsSnapshot settings,
        VoiceGamePhase phase,
        bool actualImpostor,
        bool voiceDead,
        bool voiceSpectator,
        VoiceTeamRadioChannel channel)
        => channel == VoiceTeamRadioChannel.Impostors
        && (VoiceImpostorPolicy.MeetingRadioEnabled(settings, phase) ||
            settings.TeamRadio && settings.TeamRadioImpostors)
        && VoiceImpostorPolicy.CanTransmit(settings, actualImpostor, voiceDead, voiceSpectator);


    internal static void Reset() => Floor.Reset();

    internal static void OnMeetingStarted(byte callerId)
    {
        Floor.Begin(callerId, VoiceRoomSettingsState.Current);
        VoiceChatHudState.InvalidateAudioPolicyCache();
        VoiceChatHudState.ApplyMicState();
    }

    internal static bool IsGracePeriodActive
        => Floor.CallerId != byte.MaxValue && Floor.Active(Time.unscaledTime);

    internal static byte GracePeriodCallerId => Floor.CallerId;

    internal static int GracePeriodSecondsRemaining
        => Floor.CallerId != byte.MaxValue ? Floor.SecondsRemaining(Time.unscaledTime) : 0;
}
