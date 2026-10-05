using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceRadioStateRpc
{
    private const byte RpcId = 205;


    public static bool TrySend(byte playerId, VoiceRadioState state, int targetClientId, VoiceGamePhase phase)
    {
        try
        {
            var writer = StartWriter(targetClientId);
            if (writer == null)
            {
                VoiceDiagnostics.Log("radio.rpc.send_deferred", $"player={playerId} reason=writer-unavailable");
                return false;
            }

            state = state.Normalize();
            writer.Write(playerId);
            writer.Write(state.IsActive);
            writer.Write((byte)state.Channel);
            if (state.Channel == VoiceTeamRadioChannel.External)
                writer.Write(state.ManagedKey);
            writer.Write((byte)phase);
            FinishWriter(writer);
            return true;
        }
        catch (System.Exception ex)
        {
            VoiceDiagnostics.Log(
                "radio.rpc.send_failed",
                $"player={playerId} channel={state.Channel} errorType={ex.GetType().Name} error=\"{Safe(ex.Message)}\"");
            return false;
        }
    }

    private static string Safe(string? value)
        => (value ?? string.Empty).Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    private static MessageWriter? StartWriter(int targetClientId)
    {
        if (targetClientId < 0 || AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null) return null;
        return AmongUsClient.Instance.StartRpcImmediately(
            PlayerControl.LocalPlayer.NetId,
            RpcId,
            SendOption.Reliable,
            targetClientId);
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
                var state = channel == VoiceTeamRadioChannel.External && reader.BytesRemaining > 0
                    ? VoiceRadioState.Managed(reader.ReadString())
                    : VoiceRadioState.BuiltIn(channel);
                if (reader.BytesRemaining != 1) return;
                var phase = (VoiceGamePhase)reader.ReadByte();

                // Claimed id must match dispatched PlayerControl; PlayerId is netId-derived, not auth, so spoofable on a relay.
                if (__instance == null || __instance.PlayerId != playerId)
                {
                    VoiceDiagnostics.Log("radio.rpc.reject",
                        $"sender={(__instance == null ? "null" : __instance.PlayerId.ToString())} claimed={playerId}");
                    return;
                }

                VoiceChatRoom.ApplyRemoteRadioState(playerId, state, phase);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("radio.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
