using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRadioStateRpc
{
    private const byte RpcId = 205;

    public static bool TrySend(byte playerId, VoiceTeamRadioChannel channel)
    {
        try
        {
            var writer = StartWriter();
            if (writer == null)
            {
                VoiceDiagnostics.Log("radio.rpc.send_deferred", $"player={playerId} reason=writer-unavailable");
                return false;
            }

            channel = VoiceTeamRadioChannels.Normalize(channel);
            writer.Write(playerId);
            writer.Write(VoiceTeamRadioChannels.IsActive(channel));
            writer.Write((byte)channel);
            FinishWriter(writer);
            return true;
        }
        catch (System.Exception ex)
        {
            VoiceDiagnostics.Log(
                "radio.rpc.send_failed",
                $"player={playerId} channel={channel} errorType={ex.GetType().Name} error=\"{Safe(ex.Message)}\"");
            return false;
        }
    }

    private static string Safe(string? value)
        => (value ?? string.Empty).Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

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

                // Claimed id must match dispatched PlayerControl; PlayerId is netId-derived, not auth, so spoofable on a relay.
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
