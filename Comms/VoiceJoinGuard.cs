using System;
using System.Reflection;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using Hazel;
using InnerNet;

namespace VoiceChatPlugin.VoiceChat;

// Reactor-faithful version gate, standalone (Reactor-absent) path.
//
// This mirrors Reactor's HAAS flow EXACTLY: the joining client injects its Perfect
// Comms version onto the SceneChange message it sends while loading in
// (CoSendSceneChange). The host reads that injected version inside HandleGameDataInner
// BEFORE the player is spawned into the lobby; on mismatch/absence it kicks and
// SWALLOWS the scene-change (returns false) so the player never finishes joining.
//
// Why this shape: the old approach kicked from OnPlayerJoined AFTER the player had
// already spawned in the lobby, and KickPlayer-on-a-spawned-client desynced modded
// servers and dropped the HOST a few seconds later. Rejecting at scene-change is what
// Reactor does and it never disconnects the host.
//
// Reactor coexistence: when Reactor is loaded it registers Perfect Comms into its own
// handshake (we ship RequireOnAllClients via AssemblyMetadata, see AssemblyInfo.cs)
// and does this gating natively, so ALL patches here stand down (ReactorHandlesIt).
//
// ponytail: plaintext version, no HMAC (a client-only mod can't keep a secret). The
// only fragile piece is the IL2CPP coroutine patch via Il2CppStateMachineWrapper; it
// is try/catch-guarded at every site, so a future game change disables the guard
// rather than crashing.
internal static class VoiceJoinGuard
{
    // Our marker on the injected payload: 4 magic bytes then the version string.
    // Distinct from Reactor's "reactor" magic so the two never cross-read.
    private const byte M0 = 0x50; // P
    private const byte M1 = 0x43; // C
    private const byte M2 = 0x56; // V
    private const byte M3 = 0x31; // 1

    // GameDataTypes.SceneChangeFlag, stable in the Among Us protocol.
    private const byte SceneChangeFlag = 6;

    // Reactor-style kick-reason packet: a tag-255 GameData submessage flagged
    // SetKickReason. Read in HandleGameDataInner (works before the client has fully
    // spawned, unlike a PlayerControl RPC), so the kicked client gets the reason in
    // time to show it on the disconnect screen.
    private const byte SubmessageTag = byte.MaxValue;
    private const byte FlagSetKickReason = 240; // our flag, distinct from Reactor's enum

    private static string? _pendingKickReason;

    private static bool _loggedActive;

    private static bool _reactorProbed;
    private static bool _reactorPresent;
    private static bool _patchHealthEvaluated;
    private static bool _criticalPatchPairHealthy;
    private static string _patchHealthReason = "not-evaluated";
    private static DateTime _nextPatchWarningUtc = DateTime.MinValue;
    private static DateTime _nextGenerationWarningUtc = DateTime.MinValue;

    private static void Dbg(string msg) => VoiceChatPluginMain.Logger.LogMessage("[JoinGuard] " + msg);

    internal static bool ReactorHandlesItPublic => ReactorHandlesIt();

    private static bool ReactorHandlesIt()
    {
        if (_reactorProbed) return _reactorPresent;
        _reactorProbed = true;
        try
        {
            _reactorPresent = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.ContainsKey("gg.reactor.api");
        }
        catch (Exception ex)
        {
            _reactorPresent = false;
            Dbg("reactor probe failed: " + ex.Message);
        }
        if (_reactorPresent) Dbg("Reactor present -> standalone guard standing down (Reactor gates version natively)");
        return _reactorPresent;
    }

    // Kept for VCManager's existing call sites; nothing to reset in the new model.
    public static void Reset()
    {
        _loggedActive = false;
        _pendingKickReason = null;
        _nextPatchWarningUtc = DateTime.MinValue;
        _nextGenerationWarningUtc = DateTime.MinValue;
    }

    // Kept for VCManager's per-frame call; the new model is event-driven via patches.
    public static void Tick()
    {
        if (_patchHealthEvaluated && !_criticalPatchPairHealthy)
        {
            WarnCriticalPatchFailure();
            return;
        }
        if (_loggedActive) return;
        if (AmongUsClient.Instance == null) return;
        try
        {
            var client = AmongUsClient.Instance;
            if (client.GameState == InnerNetClient.GameStates.Joined
                && client.GameId != 0
                && !VoiceRoomLifetimeGate.IsConfirmedJoinedGame(client.GameId))
            {
                WarnUnconfirmedGeneration();
                return;
            }
        }
        catch { }
        _loggedActive = true;
        if (ReactorHandlesIt()) return;
        Dbg($"active ver={VoiceChatPluginMain.Version} (scene-change gate)");
    }

    internal static void ValidateCriticalPatchHealth()
    {
        bool reactor = ReactorHandlesIt();
        bool inject = HasOurPrefix(CoSendSceneChangePatch.TargetMethod());
        bool validate = HasOurPrefix(HandleGameDataInnerPatch.TargetMethod());
        bool disconnect = HasOurPrefix(AccessTools.Method(
            typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal)));
        bool joined = HasOurPostfix(AccessTools.Method(
            typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined)));

        _criticalPatchPairHealthy = IsCriticalPatchPairHealthy(
            joined, disconnect, inject, validate, reactor);
        _patchHealthEvaluated = true;
        _patchHealthReason = _criticalPatchPairHealthy
            ? "healthy"
            : $"join={joined} disconnect={disconnect} inject={inject} validate={validate} reactor={reactor}";

        VoiceDiagnostics.Log(
            "voice.patch-health",
            $"healthy={_criticalPatchPairHealthy} {_patchHealthReason}");
        if (!_criticalPatchPairHealthy)
            WarnCriticalPatchFailure(force: true);
    }

    internal static bool IsCriticalPatchPairHealthy(
        bool joinedPatch,
        bool disconnectPatch,
        bool versionInjectPatch,
        bool versionValidatePatch,
        bool reactorHandlesVersionGate)
        => joinedPatch
           && disconnectPatch
           && (reactorHandlesVersionGate || (versionInjectPatch && versionValidatePatch));

    internal static bool CanStartVoiceForCurrentSession(int gameId, out string reason)
    {
        if (!_patchHealthEvaluated)
        {
            reason = "patch-health-not-evaluated";
            return false;
        }
        if (!_criticalPatchPairHealthy)
        {
            reason = "critical-patch-pair-unhealthy";
            WarnCriticalPatchFailure();
            return false;
        }
        if (!VoiceRoomLifetimeGate.IsConfirmedJoinedGame(gameId))
        {
            reason = "join-generation-unconfirmed";
            WarnUnconfirmedGeneration();
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool HasOurPrefix(MethodBase? target)
    {
        if (target == null) return false;
        var patches = Harmony.GetPatchInfo(target)?.Prefixes;
        if (patches == null) return false;
        foreach (var patch in patches)
            if (string.Equals(patch.owner, VoiceChatPluginMain.Id, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool HasOurPostfix(MethodBase? target)
    {
        if (target == null) return false;
        var patches = Harmony.GetPatchInfo(target)?.Postfixes;
        if (patches == null) return false;
        foreach (var patch in patches)
            if (string.Equals(patch.owner, VoiceChatPluginMain.Id, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void WarnCriticalPatchFailure(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < _nextPatchWarningUtc) return;
        _nextPatchWarningUtc = now.AddSeconds(30);
        const string warning = "Perfect Comms voice disabled: critical game hooks are unavailable. Update the mod or game.";
        VoiceChatHudState.ShowToastThreadSafe(warning);
        VoiceDiagnostics.DebugError($"[VC] {warning} ({_patchHealthReason})");
    }

    private static void WarnUnconfirmedGeneration()
    {
        var now = DateTime.UtcNow;
        if (now < _nextGenerationWarningUtc) return;
        _nextGenerationWarningUtc = now.AddSeconds(30);
        const string warning = "Perfect Comms voice is waiting for a confirmed lobby join. Leave and rejoin this lobby.";
        VoiceChatHudState.ShowToastThreadSafe(warning);
        VoiceDiagnostics.DebugWarning($"[VC] {warning}");
    }

    private static string MismatchMessage(string clientVersion) =>
        "Perfect Comms version mismatch.\n\n" +
        $"This lobby is running Perfect Comms {VoiceChatPluginMain.Version}.\n" +
        $"You have {clientVersion}.\n\n" +
        "Update Perfect Comms to join this lobby.";

    private static string MissingMessage() =>
        $"This lobby requires Perfect Comms {VoiceChatPluginMain.Version}.\n\n" +
        "Install or enable Perfect Comms to join this lobby.";

    // Host -> target client: tag-255 GameDataTo carrying the kick reason. Same shape
    // Reactor uses in KickWithReason; the host emitting this does not disconnect it.
    private static void SendKickReason(InnerNetClient inc, int targetClientId, string reason)
    {
        try
        {
            var writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage((byte) Tags.GameDataTo);
            writer.Write(inc.GameId);
            writer.WritePacked(targetClientId);
            writer.StartMessage(SubmessageTag);
            writer.Write(FlagSetKickReason);
            writer.Write(reason);
            writer.EndMessage();
            writer.EndMessage();
            inc.SendOrDisconnect(writer);
            writer.Recycle();
        }
        catch (Exception ex) { Dbg("send-reason error: " + ex.Message); }
    }

    // ---- Client side: inject our version onto the SceneChange we send while joining ----
    [HarmonyPatch]
    private static class CoSendSceneChangePatch
    {
        public static MethodBase? TargetMethod()
            => Il2CppStateMachineWrapper<InnerNetClient>.GetStateMachineMoveNext(nameof(InnerNetClient.CoSendSceneChange));

        public static bool Prepare() => Il2CppStateMachineWrapper<InnerNetClient>.GetStateMachineMoveNext(nameof(InnerNetClient.CoSendSceneChange)) != null;

        public static bool Prefix(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __instance, ref bool __result)
        {
            if (ReactorHandlesIt()) return true;
            try
            {
                var wrapper = new Il2CppStateMachineWrapper<InnerNetClient>(__instance);
                var inc = wrapper.Instance;
                var sceneName = wrapper.GetParameter<string>("sceneName");

                if (inc.AmHost || inc.connection?.State != ConnectionState.Connected || inc.ClientId < 0)
                    return true;

                var clientData = inc.FindClientById(inc.ClientId);
                if (clientData == null) return true;

                var writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage((byte) Tags.GameData);
                writer.Write(inc.GameId);
                writer.StartMessage(SceneChangeFlag);
                writer.WritePacked(inc.ClientId);
                writer.Write(sceneName);
                // PATCH: inject Perfect Comms version after the scene name.
                writer.Write(M0); writer.Write(M1); writer.Write(M2); writer.Write(M3);
                writer.Write(VoiceChatPluginMain.Version);
                writer.EndMessage();
                writer.EndMessage();
                inc.SendOrDisconnect(writer);
                writer.Recycle();

                inc.StartCoroutine(inc.CoOnPlayerChangedScene(clientData, sceneName));
                wrapper.State = -1;
                __result = false;
                Dbg($"injected version onto scene-change ver={VoiceChatPluginMain.Version}");
                return false;
            }
            catch (Exception ex)
            {
                Dbg("CoSendSceneChange inject failed (guard disabled this join): " + ex.Message);
                return true;
            }
        }
    }

    // ---- Host side: validate the injected version at scene-change, before spawn ----
    [HarmonyPatch]
    private static class HandleGameDataInnerPatch
    {
        public static MethodBase? TargetMethod()
            => Il2CppStateMachineWrapper<InnerNetClient>.GetStateMachineMoveNext(nameof(InnerNetClient.HandleGameDataInner));

        public static bool Prepare() => Il2CppStateMachineWrapper<InnerNetClient>.GetStateMachineMoveNext(nameof(InnerNetClient.HandleGameDataInner)) != null;

        public static bool Prefix(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __instance, ref bool __result)
        {
            if (ReactorHandlesIt()) return true;
            MessageReader? reader = null;
            try
            {
                var wrapper = new Il2CppStateMachineWrapper<InnerNetClient>(__instance);
                if (wrapper.State != 0) return true;

                var inc = wrapper.Instance;
                reader = wrapper.GetParameter<MessageReader>("reader");
                if (reader == null) return true;

                // Client side: our kick-reason packet. Stash it for the disconnect screen.
                if (reader.Tag == SubmessageTag && !inc.AmHost)
                {
                    byte flag = reader.ReadByte();
                    if (flag == FlagSetKickReason)
                    {
                        _pendingKickReason = reader.ReadString();
                        Dbg("received kick reason");
                        reader.Recycle();
                        __result = false;
                        return false;
                    }
                    return true;
                }

                if (reader.Tag != SceneChangeFlag || !inc.AmHost) return true;

                int clientId = reader.ReadPackedInt32();
                var clientData = inc.FindClientById(clientId);
                string sceneName = reader.ReadString();
                if (clientData == null || string.IsNullOrWhiteSpace(sceneName))
                {
                    // Match vanilla's own not-found path: recycle and swallow.
                    reader.Recycle();
                    __result = false;
                    return false;
                }

                // Read our injected version (4 magic bytes + string), if present.
                string? clientVersion = null;
                if (reader.BytesRemaining >= 5
                    && reader.ReadByte() == M0 && reader.ReadByte() == M1
                    && reader.ReadByte() == M2 && reader.ReadByte() == M3)
                {
                    clientVersion = reader.ReadString();
                }

                bool ok = clientVersion != null
                    && string.Equals(clientVersion, VoiceChatPluginMain.Version, StringComparison.Ordinal);

                if (!ok)
                {
                    string reason = clientVersion == null ? MissingMessage() : MismatchMessage(clientVersion);
                    Dbg($"reject id={clientId} theirs={clientVersion ?? "(none)"} ours={VoiceChatPluginMain.Version} -> kick at scene-change");
                    // Reactor-exact: send a tag-255 SetKickReason GameDataTo, THEN kick.
                    SendKickReason(inc, clientId, reason);
                    inc.KickPlayer(clientId, false);
                    reader.Recycle();
                    __result = false;
                    return false; // swallow: player never finishes joining
                }

                // Valid: run the normal scene change ourselves and swallow the original.
                // Do NOT recycle here — like Reactor, the reader is owned by the caller in
                // this path; recycling would double-free.
                Dbg($"cleared id={clientId} ver={clientVersion}");
                inc.StartCoroutine(inc.CoOnPlayerChangedScene(clientData, sceneName));
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Dbg("HandleGameDataInner validate failed (passing through): " + ex.Message);
                return true; // let vanilla handle it; never break the host
            }
        }
    }

    // ---- Client side: show the stored reason on the disconnect screen ----
    [HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
    private static class DisconnectInternalPatch
    {
        public static void Prefix(InnerNetClient __instance, ref DisconnectReasons reason)
        {
            // DisconnectInternal is the earliest deterministic local-lobby exit boundary. Release
            // the voice session here (capture, peers, routing, and the helper process). EndGame ->
            // lobby transitions do not disconnect and therefore retain the same uninterrupted lease.
            var lifetimeReason = $"innernet-disconnect:{reason}";
            VoiceRoomLifetimeGate.MarkExplicitDisconnect(lifetimeReason);
            try { VoiceLobbyRegistryPublisher.ClearLocalListing(); }
            catch { /* registry cleanup must never interfere with vanilla disconnect */ }
            try { VoiceChatRoom.CloseCurrentRoom(lifetimeReason); }
            catch (Exception ex)
            {
                // Never let plugin cleanup prevent the game's own disconnect. The latch stays set,
                // so the driver still cannot recreate voice from lingering EndGame scene objects.
                try { VoiceDiagnostics.Log("voice.room.lifetime", $"event=disconnect-cleanup-failed errorType={ex.GetType().Name}"); }
                catch { }
            }

            // Reactor also swaps this screen; when it's present we never set our reason
            // (all our patches stand down), but bail explicitly so we can never clobber
            // Reactor's own kick-reason screen.
            if (ReactorHandlesIt()) return;
            if (reason != DisconnectReasons.Kicked || _pendingKickReason == null) return;
            reason = DisconnectReasons.Custom;
            __instance.LastCustomDisconnect = _pendingKickReason;
            _pendingKickReason = null;
            Dbg("disconnect swap -> perfect comms reason");
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameJoined))]
    private static class OnGameJoinedPatch
    {
        public static void Postfix()
        {
            // This is a confirmed new network session, unlike stale Joined/scene state that can
            // remain visible while DisconnectInternal is still transitioning away from EndGame.
            VoiceRoomLifetimeGate.ConfirmJoinedSession("among-us-on-game-joined");
            if (!VoiceChatRoom.IsCurrentSessionEligible(out var ineligibleReason))
            {
                VoiceDiagnostics.Log(
                    "voice.room.start-skipped",
                    $"source=among-us-on-game-joined reason={ineligibleReason} generation={VoiceRoomLifetimeGate.CurrentSessionGeneration}");
                return;
            }
            try
            {
                // Begin helper launch and device setup from the authoritative join callback instead
                // of waiting for the next scene/driver poll and a fully-built PlayerControl roster.
                VoiceChatRoom.EnsureStartedForJoinedSession();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.DebugError($"[VC] OnGameJoined voice bootstrap failed: {ex.Message}");
            }
        }
    }
}
