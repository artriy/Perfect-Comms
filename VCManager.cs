using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

internal class VCManager : MonoBehaviour
{
    private static bool _sceneHookRegistered;
    private static GameObject? _managerObject;

    // Cached active scene name. Reading SceneManager.GetActiveScene().name marshals a fresh managed
    // string across the IL2CPP boundary on every access; doing that per-frame in Update() was a steady
    // GC contributor. We update this only on scene load / active-scene change instead.
    private static string _activeSceneName = "";

    static VCManager()
    {
        ClassInjector.RegisterTypeInIl2Cpp<VCManager>();
    }

    internal static void RegisterSceneHook()
    {
        if (_sceneHookRegistered) return;
        _sceneHookRegistered = true;

        _activeSceneName = SceneManager.GetActiveScene().name;
        VoiceSceneState.SetEndGameSceneHint(_activeSceneName == "EndGame");
        VoiceChatRoom.NotifyScenePhaseBoundary();
        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
        SceneManager.activeSceneChanged +=
            (UnityEngine.Events.UnityAction<Scene, Scene>)OnActiveSceneChanged;
    }

    private static void OnActiveSceneChanged(Scene previous, Scene next)
    {
        _activeSceneName = next.name;
        VoiceSceneState.SetEndGameSceneHint(_activeSceneName == "EndGame");
        VoiceChatRoom.NotifyScenePhaseBoundary();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        // Among Us transitions are single-mode loads, so the loaded scene becomes the active one.
        // Refresh the cache here too in case activeSceneChanged ordering differs across the boundary.
        _activeSceneName = scene.name;
        VoiceSceneState.SetEndGameSceneHint(_activeSceneName == "EndGame");
        VoiceChatRoom.NotifyScenePhaseBoundary();
        EnsureManagerObject();
        switch (scene.name)
        {
            case "MainMenu":
            case "MatchMaking":
                VoiceJoinGuard.Reset();
                VoiceLobbyRegistryPublisher.ClearLocalListing();
                VoiceChatRoom.CloseCurrentRoom($"scene-loaded:{scene.name}");
                VoiceLobbyBrowserUi.Clear();
                break;
        }
    }

    private static void EnsureManagerObject()
    {
        if (_managerObject != null) return;

        _managerObject = new GameObject("VC_Manager");
        GameObject.DontDestroyOnLoad(_managerObject);
        _managerObject.hideFlags |= HideFlags.DontUnloadUnusedAsset | HideFlags.HideAndDontSave;
        _managerObject.AddComponent<VCManager>();
    }

    private static float _lastUpdateErrorLogTime = -999f;
    private static string _lastUpdateErrorStep = "";

    // Unity calls this on its main thread before native/managed teardown. Release the active
    // session and synchronously terminate the process-lifetime helper here; AppDomain hooks are
    // the last-resort fallback.
    void OnApplicationQuit()
    {
        VoiceChatPluginMain.ShutdownVoiceRuntime("application-quit");
    }

    void Update()
    {
        switch (_activeSceneName)
        {
            case "OnlineGame":
            case "EndGame":
                VoiceJoinGuard.Tick();
                VoiceFrameProfiler.Tick();
                long vcTicks = VoiceFrameProfiler.Begin();
                long hudTicks = VoiceFrameProfiler.Begin();
                SafeUpdateHud();
                VoiceFrameProfiler.End("hud", hudTicks);
                VoiceChatRoomDriver.Update();
                PingTrackerPatch.RenderEndGameOverlay();
                long pubTicks = VoiceFrameProfiler.Begin();
                VoiceLobbyRegistryPublisher.Update();
                VoiceFrameProfiler.End("publisher", pubTicks);
                VoiceFrameProfiler.End("vc.tick", vcTicks);
                break;
            default:
                // Left the profiled scenes: flush the final frame + open window so they aren't stranded.
                // No-ops when profiling is disabled or nothing is pending.
                VoiceFrameProfiler.Flush();
                break;
        }
    }

    // IL2CPP scene transitions can invalidate AllPlayerControls mid-walk; swallow the throw so
    // Update isn't aborted, which would strand the player in their last (possibly wrong) mute state.
    private static void SafeUpdateHud()
    {
        try
        {
            VoiceChatHudState.UpdateHud();
        }
        catch (System.Exception ex)
        {
            string step = VoiceChatHudState.LastUpdateStep;
            string context = VoiceChatHudState.DescribeUpdateContext();
            VoiceChatHudState.RecoverAfterUpdateFailure();
            // Keep repeated failures quiet, but do not hide a second failure in a different
            // initialization step (for example mic clone then speaker clone) behind the timer.
            if (step == _lastUpdateErrorStep && Time.time - _lastUpdateErrorLogTime < 5f) return;
            _lastUpdateErrorLogTime = Time.time;
            _lastUpdateErrorStep = step;
            VoiceDiagnostics.DebugError(
                $"[VC] HUD update failed scene={_activeSceneName} frame={Time.frameCount} " +
                $"{context}: {ex.GetType().Name}: {ex.Message}");
        }
    }

}
