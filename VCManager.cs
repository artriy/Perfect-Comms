using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

internal static class VoiceSceneInputPolicy
{
    private static bool IsContinuousVoiceScene(string sceneName)
        => sceneName is "OnlineGame" or "EndGame";

    internal static bool ShouldPreserveHeldTransmitInputs(
        string previousSceneName,
        string nextSceneName)
        => IsContinuousVoiceScene(previousSceneName) && IsContinuousVoiceScene(nextSceneName);

    internal static string ResolvePreviousSceneName(
        string eventPreviousSceneName,
        string nextSceneName,
        string cachedActiveSceneName,
        string sceneNameBeforeLoad)
    {
        if (!string.IsNullOrEmpty(eventPreviousSceneName))
            return eventPreviousSceneName;
        if (!string.IsNullOrEmpty(cachedActiveSceneName) &&
            cachedActiveSceneName != nextSceneName)
            return cachedActiveSceneName;
        if (!string.IsNullOrEmpty(sceneNameBeforeLoad) &&
            sceneNameBeforeLoad != nextSceneName)
            return sceneNameBeforeLoad;
        return "";
    }

    internal static bool ShouldReleaseHeldTransmitInputs(
        bool preserveHeldTransmitInputs,
        bool panelWasOpen)
        => !preserveHeldTransmitInputs || panelWasOpen;
}

internal class VCManager : MonoBehaviour
{
    private static bool _sceneHookRegistered;
    private static GameObject? _managerObject;

    // Cached active scene name. Reading SceneManager.GetActiveScene().name marshals a fresh managed
    // string across the IL2CPP boundary on every access; doing that per-frame in Update() was a steady
    // GC contributor. We update this only on scene load / active-scene change instead.
    private static string _activeSceneName = "";
    // sceneLoaded can precede activeSceneChanged. Preserve the old cache until the latter decides
    // whether the transition is a privacy boundary.
    private static string _sceneNameBeforeLoad = "";


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
        string previousSceneName = VoiceSceneInputPolicy.ResolvePreviousSceneName(
            previous.name,
            next.name,
            _activeSceneName,
            _sceneNameBeforeLoad);
        _sceneNameBeforeLoad = "";
        _activeSceneName = next.name;
        VoiceSceneState.SetEndGameSceneHint(_activeSceneName == "EndGame");
        VoiceChatRoom.NotifyScenePhaseBoundary();
        VoiceUiKit.ClosePersistentPanels(
            $"active-scene:{previousSceneName}->{next.name}",
            preserveHeldTransmitInputs:
                VoiceSceneInputPolicy.ShouldPreserveHeldTransmitInputs(
                    previousSceneName,
                    next.name));
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        // Among Us transitions are single-mode loads, so the loaded scene becomes the active one.
        // Refresh the cache here too in case activeSceneChanged ordering differs across the boundary.
        if (_activeSceneName != scene.name)
            _sceneNameBeforeLoad = _activeSceneName;
        _activeSceneName = scene.name;
        VoiceSceneState.SetEndGameSceneHint(_activeSceneName == "EndGame");
        VoiceChatRoom.NotifyScenePhaseBoundary();
        // activeSceneChanged owns the privacy-boundary decision. This callback may run before or
        // after it, so it closes persistent UI without turning the second notification into a
        // spurious held-key release. Session creation/closure independently releases every hold.
        VoiceUiKit.ClosePersistentPanels(
            $"scene-loaded:{scene.name}",
            preserveHeldTransmitInputs: true);
        VoiceFirstRunSetup.CloseForSceneChange();
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

    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus) return;
        // KeyboardJoystick.Update is not guaranteed to run after Windows/Android focus is lost.
        // Release transmit holds from Unity's authoritative focus callback as well.
        try { VoiceChatPatches.ReleaseHeldTransmitInputs(); } catch { }
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
                VoiceChatPatches.UpdateKeybindsFromFrameDriver();
                VoiceSettingsPanelTriggers.UpdatePanelHotkeysFromFrameDriver();
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
