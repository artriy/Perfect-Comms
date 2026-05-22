using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRoomSettingsRpc
{
    private const byte RpcId = 203;
    private const byte SnapshotKind = 1;
    private const byte RequestKind = 2;

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
        writer.Write(settings.ImpostorPrivateRadio);
        writer.Write(settings.OnlyGhostsCanTalk);
        writer.Write(settings.OnlyMeetingOrLobby);
    }

    private static VoiceRoomSettingsSnapshot ReadSettings(MessageReader reader)
    {
        return new VoiceRoomSettingsSnapshot(
            reader.ReadInt32(),
            reader.ReadString(),
            reader.ReadSingle(),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean()).Clamp();
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
