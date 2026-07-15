using System;
using UnityEngine;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

internal static class VoiceChatRoomDriver
{
    private static bool _wasInGame;
    private static bool _wasInIntro;
    private static bool _wasInEndGame;
    private static float _roomRetryTimer = 0f;
    private static bool _pendingRemap;
    private static int _remapCountdown;
    private static VoiceChatRoom? _failureTrackedRoom;
    private static int _consecutiveUpdateFailures;
    private static int _updateFailureRebuilds;
    private static float _nextUpdateFailureRebuildTime;
    private static float _lastUpdateFailureLogTime = -999f;

    private static bool ShouldCloseRoom()
    {
        bool localOrFreeplay = false;
        try
        {
            localOrFreeplay = VoiceChatRoom.IsLocalVoiceEndpoint(
                AmongUsClient.Instance?.networkAddress);
        }
        catch { }
        return VoiceRoomLifetimeGate.IsTerminalCondition(
            hasAmongUsClient: AmongUsClient.Instance != null,
            isLocalServer: localOrFreeplay,
            explicitDisconnectLatched: VoiceRoomLifetimeGate.IsExplicitDisconnectLatched)
            || (VoiceChatRoom.Current != null && !VoiceChatRoom.IsCurrentSessionEligible(out _));
    }

    private static bool ShouldHaveRoom()
    {
        if (!VoiceChatRoom.IsCurrentSessionEligible(out _)) return false;

        if (ShipStatus.Instance != null) return true;
        if (LobbyBehaviour.Instance != null) return true;
        if (IntroCutscene.Instance != null) return true;
        if (VoiceSceneState.IsEndGameActive) return true;

        return AmongUsClient.Instance.GameState == InnerNet.InnerNetClient.GameStates.Joined;
    }

    internal static void Update()
    {
        if (ShouldCloseRoom())
        {
            if (VoiceChatRoom.Current != null)
                VoiceChatRoom.CloseCurrentRoom("driver-no-active-session");
            _wasInGame = _wasInIntro = _wasInEndGame = false;
            _roomRetryTimer = 0f;
            _pendingRemap = false;
            _remapCountdown = 0;
            ResetUpdateFailureState();
            VoiceSceneState.Reset();
            return;
        }

        if (VoiceChatRoom.Current == null && ShouldHaveRoom())
        {
            _roomRetryTimer -= Time.deltaTime;

            if (_roomRetryTimer <= 0f)
            {
                _roomRetryTimer = 1f;
                VoiceDiagnostics.DebugInfo(
                    "[VC] VoiceChatRoom is missing while joined; creating now.");

                try
                {
                    VoiceChatRoom.Start();

                    if (VoiceChatRoom.Current != null)
                    {
                        VoiceDiagnostics.DebugInfo("[VC] Room created successfully");
                        VoiceChatHudState.ApplyMicState();
                        VoiceChatHudState.ApplySpeakerState();
                    }
                    else
                    {
                        VoiceDiagnostics.DebugError("[VC] Room creation failed.");
                    }
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.DebugError($"[VC] Room creation exception: {ex.Message}\n{ex.StackTrace}");
                }
            }
            if (VoiceChatRoom.Current == null)
                return;
        }

        if (VoiceChatRoom.Current == null)
            return;

        if (!ReferenceEquals(_failureTrackedRoom, VoiceChatRoom.Current))
        {
            ResetUpdateFailureState();
            _failureTrackedRoom = VoiceChatRoom.Current;
        }

        _roomRetryTimer = 0f;

        bool inIntro = IntroCutscene.Instance != null;
        if (_wasInIntro && !inIntro)
        {
            _pendingRemap = true;
            _remapCountdown = 2;
            VoiceDiagnostics.DebugInfo("[VC] IntroCutscene ended: deferred mappings reset.");
        }
        _wasInIntro = inIntro;

        bool inEndGame = VoiceSceneState.IsEndGameActive;
        if (inEndGame && !_wasInEndGame)
        {
            // Keep the voice mesh alive through EndGame so win-screen reactions stay audible and the next
            // round needs no reconnect. Do NOT Rejoin here: that tears down every peer connection. Stale
            // per-round peer mappings are reset non-destructively at the next IntroCutscene end (deferred
            // remap) and by missing-peer recovery, so no teardown is needed.
            VoiceChatRoom.Current.ForceUpdateLocalProfile();
            _pendingRemap = false;
            _remapCountdown = 0;
            VoiceDiagnostics.DebugInfo("[VC] EndGame: voice mesh kept alive.");
        }
        _wasInEndGame = inEndGame;

        bool inGame = ShipStatus.Instance != null;
        if (inGame && !_wasInGame)
        {
            VoiceDiagnostics.DebugInfo("[VC] Entered OnlineGame scene");
            
            // Ensure audio is working
            var room = VoiceChatRoom.Current;
            if (room != null)
            {
                VoiceDiagnostics.DebugInfo($"[VC] Microphone active: {room.UsingMicrophone}");
                room.ForceCompatibilityRefresh("entered game");
            }
        }
        _wasInGame = inGame;

        if (_pendingRemap && inGame)
        {
            if (_remapCountdown > 0)
            {
                _remapCountdown--;
            }
            else
            {
                _pendingRemap = false;
                ForceRemapRemotePeers();
            }
        }

        VoiceChatHudState.TrySyncHostRoomSettings();

        try
        {
            VoiceChatRoom.Current.Update();
            _consecutiveUpdateFailures = 0;
        }
        catch (Exception ex)
        {
            var room = VoiceChatRoom.Current;
            _consecutiveUpdateFailures++;
            float now = Time.realtimeSinceStartup;
            var decision = DecideUpdateFailureRecovery(
                _consecutiveUpdateFailures,
                _updateFailureRebuilds,
                now,
                _nextUpdateFailureRebuildTime,
                _lastUpdateFailureLogTime);

            try { room?.FailClosedAfterUpdateFailure(); }
            catch { }

            if (decision.ShouldLog)
            {
                _lastUpdateFailureLogTime = now;
                VoiceDiagnostics.DebugError(
                    $"[VC] Room update failed closed count={_consecutiveUpdateFailures} " +
                    $"rebuilds={_updateFailureRebuilds}: {ex.GetType().Name}: {ex.Message}");
            }

            if (decision.ShouldRebuild && room != null)
            {
                int attempt = _updateFailureRebuilds + 1;
                _updateFailureRebuilds = attempt;
                _nextUpdateFailureRebuildTime = now + UpdateFailureRebuildDelaySeconds(attempt);
                room.RequestBoundedUpdateFailureRecovery(attempt);
            }
        }
    }

    internal readonly record struct UpdateFailureRecoveryDecision(bool ShouldLog, bool ShouldRebuild);

    internal static UpdateFailureRecoveryDecision DecideUpdateFailureRecovery(
        int consecutiveFailures,
        int rebuildAttempts,
        float now,
        float nextRebuildTime,
        float lastLogTime)
    {
        bool powerOfTwo = consecutiveFailures > 0
                          && (consecutiveFailures & (consecutiveFailures - 1)) == 0;
        bool shouldLog = consecutiveFailures == 1 || powerOfTwo || now - lastLogTime >= 5f;
        bool shouldRebuild = consecutiveFailures >= 3
                             && rebuildAttempts < 3
                             && now >= nextRebuildTime;
        return new UpdateFailureRecoveryDecision(shouldLog, shouldRebuild);
    }

    internal static float UpdateFailureRebuildDelaySeconds(int completedAttempts)
        => 5f * (1 << Math.Min(Math.Max(completedAttempts - 1, 0), 2));

    private static void ResetUpdateFailureState()
    {
        _failureTrackedRoom = null;
        _consecutiveUpdateFailures = 0;
        _updateFailureRebuilds = 0;
        _nextUpdateFailureRebuildTime = 0f;
        _lastUpdateFailureLogTime = -999f;
    }

    private static void ForceRemapRemotePeers()
    {
        var room = VoiceChatRoom.Current;
        if (room == null) return;

        int count = room.ResetRemotePeerMappingsNoMute();

        room.ForceCompatibilityRefresh("deferred remap");

        VoiceDiagnostics.DebugInfo($"[VC] Deferred mappings reset for {count} client(s).");
    }
}
