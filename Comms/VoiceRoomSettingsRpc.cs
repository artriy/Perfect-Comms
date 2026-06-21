using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRoomSettingsRpc
{
    private const byte RpcId = 203;
    private const byte SnapshotKind = 1;
    private const byte RequestKind = 2;

    // Cap untrusted host backend URL (mirrors VoiceRoomControlCodec.MaxServerUrlBytes).
    private const int MaxBackendServerUrlChars = 512;

    public static void SendSnapshot(VoiceRoomSettingsSnapshot settings)
    {
        var writer = StartWriter();
        if (writer == null) return;

        writer.Write(SnapshotKind);
        WriteSettings(writer, settings.Clamp());
        FinishWriter(writer);
    }

    public static void SendRequest()
    {
        var writer = StartWriter();
        if (writer == null) return;

        writer.Write(RequestKind);
        FinishWriter(writer);
    }

    private static MessageWriter? StartWriter()
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return null;
        return AmongUsClient.Instance.StartRpcImmediately(
            PlayerControl.LocalPlayer.NetId,
            RpcId,
            SendOption.Reliable,
            -1);
    }

    private static void FinishWriter(MessageWriter writer)
    {
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    private static void WriteSettings(MessageWriter writer, VoiceRoomSettingsSnapshot settings)
    {
        writer.Write(settings.Backend);
        writer.Write(settings.BackendServerUrl ?? string.Empty);
        writer.Write(settings.MaxChatDistance);
        writer.Write(settings.FalloffMode);
        writer.Write(settings.OcclusionMode);
        writer.Write(settings.WallsBlockSound);
        writer.Write(settings.OnlyHearInSight);
        writer.Write(settings.ImpostorHearGhosts);
        writer.Write(settings.HearInVent);
        writer.Write(settings.VentPrivateChat);
        writer.Write(settings.CommsSabDisables);
        writer.Write(settings.CameraCanHear);
        writer.Write(settings.TeamRadio);
        writer.Write(settings.TeamRadioImpostors);
        writer.Write(settings.TeamRadioVampires);
        writer.Write(settings.TeamRadioLovers);
        writer.Write(settings.OnlyGhostsCanTalk);
        writer.Write(settings.OnlyMeetingOrLobby);
        writer.Write(settings.MuteBlackmailedInMeetings);
        writer.Write(settings.MuteBlackmailedNextRound);
        writer.Write(settings.MuteJailedInMeetings);
        writer.Write(settings.JailorCanUnmuteJailed);
        writer.Write(settings.MuteParasiteControlled);
        writer.Write(settings.MutePuppeteerControlled);
        writer.Write(settings.CrewpostorUsesImpostorVoice);
        writer.Write(settings.MuteSwooperWhileSwooped);
        writer.Write(settings.MediumGhostVoice);
        writer.Write(settings.MuteGlitchHacked);
        writer.Write(settings.MuffleBlindedOrFlashedHearing);
        writer.Write(settings.MuffleHypnotizedDuringHysteria);
        writer.Write(settings.OnlyMeetingOrLobbyAffectsGhosts);
        writer.Write(settings.TeamRadioInMeetings);
        writer.Write(settings.PuppeteerHearFromVictim);
        writer.Write(settings.ParasiteHearFromVictim);
        writer.Write(settings.TeamRadioInTasks);
        writer.Write(settings.GhostsHearEachOtherUnlimited);
        writer.Write(settings.JailPersistsAfterJailorDeath);
        writer.Write(settings.GracePeriodEnabled);
        writer.Write(settings.GracePeriodSeconds);
        WriteModOptions(writer);
    }

    // Trailing variable-length block of third-party mod host-option values (PerfectComms.Api
    // Primitive 4). Hash-keyed so adding mod options never shifts the fixed offsets above; legacy
    // readers that stop before this block simply keep their defaults. count, then per entry:
    // (keyHash:int, isEnum:bool, value:int).
    private static void WriteModOptions(MessageWriter writer)
    {
        var entries = new System.Collections.Generic.List<(int Hash, bool IsEnum, int Value)>(
            VoiceModRegistry.SyncedValues());
        writer.Write(entries.Count);
        foreach (var e in entries)
        {
            writer.Write(e.Hash);
            writer.Write(e.IsEnum);
            writer.Write(e.Value);
        }
    }

    private static void ReadModOptions(MessageReader reader)
    {
        if (reader.BytesRemaining < 4) return; // legacy snapshot ended before this block
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            if (reader.BytesRemaining < 9) break; // int + bool + int
            int hash = reader.ReadInt32();
            bool isEnum = reader.ReadBoolean();
            int value = reader.ReadInt32();
            VoiceModRegistry.ApplySyncedValue(hash, isEnum, value);
        }
    }

    private static VoiceRoomSettingsSnapshot ReadSettings(MessageReader reader)
    {
        int backend = reader.ReadInt32();
        string backendServerUrl = reader.ReadString();
        if (backendServerUrl.Length > MaxBackendServerUrlChars)
            backendServerUrl = backendServerUrl.Substring(0, MaxBackendServerUrlChars);
        float maxChatDistance = reader.ReadSingle();
        int falloffMode = reader.ReadInt32();
        int occlusionMode = reader.ReadInt32();
        bool wallsBlockSound = reader.ReadBoolean();
        bool onlyHearInSight = reader.ReadBoolean();
        bool impostorHearGhosts = reader.ReadBoolean();
        bool hearInVent = reader.ReadBoolean();
        bool ventPrivateChat = reader.ReadBoolean();
        bool commsSabDisables = reader.ReadBoolean();
        bool cameraCanHear = reader.ReadBoolean();
        bool teamRadio = reader.ReadBoolean();
        bool hasTeamRadioSubSettings = reader.BytesRemaining >= 13;
        var defaults = VoiceRoomSettingsSnapshot.Defaults;
        bool teamRadioImpostors = defaults.TeamRadioImpostors;
        bool teamRadioVampires = defaults.TeamRadioVampires;
        bool teamRadioLovers = defaults.TeamRadioLovers;
        if (hasTeamRadioSubSettings)
        {
            teamRadioImpostors = reader.ReadBoolean();
            teamRadioVampires = reader.ReadBoolean();
            teamRadioLovers = reader.ReadBoolean();
        }

        bool onlyGhostsCanTalk = reader.ReadBoolean();
        bool onlyMeetingOrLobby = reader.ReadBoolean();
        bool muteBlackmailedInMeetings = reader.ReadBoolean();
        bool muteBlackmailedNextRound = reader.ReadBoolean();
        bool muteJailedInMeetings = reader.ReadBoolean();
        bool jailorCanUnmuteJailed = reader.ReadBoolean();
        bool muteParasiteControlled = reader.ReadBoolean();
        bool mutePuppeteerControlled = reader.ReadBoolean();
        bool crewpostorUsesImpostorVoice = reader.ReadBoolean();
        bool muteSwooperWhileSwooped = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.MuteSwooperWhileSwooped;
        VoiceRoomSettingsSnapshot clamped;
        int mediumGhostVoice = reader.BytesRemaining >= 4 ? reader.ReadInt32() : defaults.MediumGhostVoice;
        bool muteGlitchHacked = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.MuteGlitchHacked;
        bool muffleBlindedOrFlashedHearing = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.MuffleBlindedOrFlashedHearing;
        bool muffleHypnotizedDuringHysteria = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.MuffleHypnotizedDuringHysteria;
        bool onlyMeetingOrLobbyAffectsGhosts = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.OnlyMeetingOrLobbyAffectsGhosts;
        bool teamRadioInMeetings = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.TeamRadioInMeetings;
        bool puppeteerHearFromVictim = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.PuppeteerHearFromVictim;
        bool parasiteHearFromVictim = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.ParasiteHearFromVictim;
        bool teamRadioInTasks = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.TeamRadioInTasks;
        bool ghostsHearEachOtherUnlimited = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.GhostsHearEachOtherUnlimited;
        bool jailPersistsAfterJailorDeath = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.JailPersistsAfterJailorDeath;
        bool gracePeriodEnabled = reader.BytesRemaining > 0 ? reader.ReadBoolean() : defaults.GracePeriodEnabled;
        float gracePeriodSeconds = reader.BytesRemaining >= 4 ? reader.ReadSingle() : defaults.GracePeriodSeconds;

        clamped = new VoiceRoomSettingsSnapshot(
            backend,
            backendServerUrl,
            maxChatDistance,
            falloffMode,
            occlusionMode,
            wallsBlockSound,
            onlyHearInSight,
            impostorHearGhosts,
            hearInVent,
            ventPrivateChat,
            commsSabDisables,
            cameraCanHear,
            teamRadio,
            teamRadioImpostors,
            teamRadioVampires,
            teamRadioLovers,
            onlyGhostsCanTalk,
            onlyMeetingOrLobby,
            onlyMeetingOrLobbyAffectsGhosts,
            muteBlackmailedInMeetings,
            muteBlackmailedNextRound,
            muteJailedInMeetings,
            jailorCanUnmuteJailed,
            muteParasiteControlled,
            mutePuppeteerControlled,
            crewpostorUsesImpostorVoice,
            muteSwooperWhileSwooped,
            mediumGhostVoice,
            muteGlitchHacked,
            muffleBlindedOrFlashedHearing,
            muffleHypnotizedDuringHysteria,
            teamRadioInMeetings,
            puppeteerHearFromVictim,
            parasiteHearFromVictim,
            teamRadioInTasks,
            ghostsHearEachOtherUnlimited,
            jailPersistsAfterJailorDeath,
            gracePeriodEnabled,
            gracePeriodSeconds).Clamp();
        ReadModOptions(reader);
        return clamped;
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    private static class PlayerControlHandleRpcPatch
    {
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != RpcId) return;

            try
            {
                var kind = reader.ReadByte();
                if (kind == SnapshotKind)
                {
                    var settings = ReadSettings(reader);
                    if (AmongUsClient.Instance?.AmHost == true) return;
                    if (!VoiceHostAuthority.IsTrustedHostSender(__instance,
                            VoiceChatRoom.Current?.CurrentSnapshot,
                            "rpc",
                            out var sender,
                            out var reason,
                            out var hostClientId,
                            out var hostPlayerId))
                    {
                        VoiceDiagnostics.Log("settings.snapshot.rejected",
                            $"{sender.ToDiagnosticFields()} reason={reason} hostClient={hostClientId} hostPlayer={hostPlayerId}");
                        // Stale host id (e.g. post-migration): re-request a fresh snapshot.
                        VoiceChatRoom.NoteHostSettingsSnapshotRejected();
                        return;
                    }

                    VoiceRoomSettingsState.ApplyRemote(settings);
                    VoiceChatRoom.NoteHostSettingsSnapshotApplied("rpc", hostClientId, hostPlayerId);
                    VoiceDiagnostics.Log("settings.snapshot.applied",
                        $"{sender.ToDiagnosticFields()} kind=host-snapshot hostClient={hostClientId} hostPlayer={hostPlayerId}");
                    return;
                }

                if (kind == RequestKind && AmongUsClient.Instance?.AmHost == true)
                    VoiceChatRoom.RespondToHostSettingsRequest(VoiceHostAuthority.FromPlayer(__instance, "rpc"));
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("settings.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
