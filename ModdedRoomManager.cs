using System;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

public static class ModdedRoomManager
{
    public static readonly Guid ModGuid = new("a3f7c821-4b9e-4d62-bc50-1e2f83a97d04");


    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HostGame))]
    public static class HostGamePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(InnerNetClient __instance,
            IGameOptions settings,
            GameFilterOptions filterOpts)
        {
            if (!ReactorHttpMatchmakingBridge.IsKnownModdedRegion())
                return true;

            MessageWriter? msg = null;
            try
            {
                msg = MessageWriter.Get(SendOption.Reliable);
                msg.StartMessage(25);

                msg.WriteBytesAndSize(GameOptionsManager.Instance.gameOptionsFactory.ToBytes(settings, false));
                msg.Write(CrossplayMode.GetCrossplayFlags());
                filterOpts.Serialize(msg);

                msg.Write(ModGuid.ToByteArray());

                msg.EndMessage();
                __instance.SendOrDisconnect(msg);

                VoiceDiagnostics.DebugInfo(
                    $"[VC] HostModdedGame sent with GUID {ModGuid}");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.DebugError(
                    $"[VC] HostGamePatch failed, falling back to vanilla host: {ex.Message}");
                return true;
            }
            finally
            {
                msg?.Recycle();
            }

            return false;
        }
    }

    // ── FindGame / matchmaking filter patch ──────────────────────────────────
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.RequestGameList))]
    public static class FindGamePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(InnerNetClient __instance, IGameOptions settings,
            GameFilterOptions filterOpts)
        {
            // Let vanilla/Reactor HTTP matchmaking run. Reactor adds Client-Mods and records
            // Client-Mods-Processed, which is what unlocks public lobbies on compatible servers.
            VanillaLobbyDiagnostics.Limited("request-game-list", "request", $"InnerNetClient.RequestGameList passthrough state={__instance.GameState} netMode={__instance.NetworkMode} gameId={__instance.GameId}", first: 12, every: 60);
            return true;
        }
    }

}
