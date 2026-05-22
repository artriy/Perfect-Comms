using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRadioStateRpc
{
    private const byte RpcId = 205;

    public static void Send(byte playerId, bool active)
    {
        var writer = StartWriter();
        if (writer == null) return;

        writer.Write(playerId);
        writer.Write(active);
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
        public static void Postfix(byte callId, MessageReader reader)
        {
            if (callId != RpcId) return;

            try
            {
                var playerId = reader.ReadByte();
                var active = reader.ReadBoolean();
                VoiceChatRoom.ApplyRemoteRadioState(playerId, active);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("radio.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
