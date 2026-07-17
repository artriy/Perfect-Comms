using System;
using HarmonyLib;
using Hazel;

namespace VoiceChatPlugin.VoiceChat;

internal static class VoiceHostRefreshRpc
{
    private const byte RpcId = 206;
    internal const string RemoteRefreshRejectionReason =
        "remote-refresh-disabled-untrusted-rpc-source";

    // Handle the legacy packet shape so mixed-version lobbies fail closed instead of
    // treating the id as an unknown RPC. New clients never send this command because
    // PlayerControl.HandleRpc does not expose authenticated packet sender provenance.
    internal static bool RemoteRefreshEnabled => false;

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
    private static class PlayerControlHandleRpcPatch
    {
        public static void Postfix(PlayerControl __instance, byte callId, MessageReader reader)
        {
            if (callId != RpcId) return;

            try
            {
                var nonce = reader.ReadInt32();
                VoiceChatRoom.ApplyHostVoiceRefreshFromRpc(__instance, nonce);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("voice.refresh.rpc.error", $"error=\"{ex.Message}\"");
            }
        }
    }
}
