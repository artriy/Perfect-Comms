using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceJailVoiceRpc
{
    private const byte RpcId = 204;

    public static void Send(byte jailorId, byte jailedPlayerId, bool allowed)
    {
        var writer = StartWriter();
        if (writer == null) return;

        writer.Write(jailorId);
        writer.Write(jailedPlayerId);
        writer.Write(allowed);
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
                var jailorId = reader.ReadByte();
                var jailedPlayerId = reader.ReadByte();
                var allowed = reader.ReadBoolean();
                VoiceRoleMuteState.ApplyRemoteJailVoice(jailorId, jailedPlayerId, allowed);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("jailvoice.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
