using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRadioStateRpc
{
    private const byte RpcId = 205;

    public static void Send(byte playerId, VoiceTeamRadioChannel channel)
    {
        var writer = StartWriter();
        if (writer == null) return;

        channel = VoiceTeamRadioChannels.Normalize(channel);
        writer.Write(playerId);
        writer.Write(VoiceTeamRadioChannels.IsActive(channel));
        writer.Write((byte)channel);
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

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    private static class PlayerControlHandleRpcPatch
    {
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != RpcId) return;

            try
            {
                var playerId = reader.ReadByte();
                var active = reader.ReadBoolean();
                var channel = VoiceTeamRadioChannels.FromWire(
                    active,
                    reader.BytesRemaining > 0 ? reader.ReadByte() : null);

                // Bind to the network sender: a client may only set its OWN radio state.
                if (__instance == null || __instance.PlayerId != playerId)
                {
                    VoiceDiagnostics.Log("radio.rpc.reject",
                        $"sender={(__instance == null ? "null" : __instance.PlayerId.ToString())} claimed={playerId}");
                    return;
                }

                VoiceChatRoom.ApplyRemoteRadioState(playerId, channel);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("radio.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
