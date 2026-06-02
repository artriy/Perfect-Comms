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
        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
        SceneManager.activeSceneChanged +=
            (UnityEngine.Events.UnityAction<Scene, Scene>)OnActiveSceneChanged;
    }

    private static void OnActiveSceneChanged(Scene previous, Scene next)
    {
        _activeSceneName = next.name;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
        // Among Us transitions are single-mode loads, so the loaded scene becomes the active one.
        // Refresh the cache here too in case activeSceneChanged ordering differs across the boundary.
        _activeSceneName = scene.name;
        EnsureManagerObject();
        switch (scene.name)
        {
            case "MainMenu":
            case "MatchMaking":
                VoiceLobbyRegistryPublisher.ClearLocalListing();
                VoiceChatRoom.CloseCurrentRoom();
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

    void Update()
    {
        switch (_activeSceneName)
        {
            case "OnlineGame":
            case "EndGame":
                VoiceFrameProfiler.Tick();
                long vcTicks = VoiceFrameProfiler.Begin();
                long hudTicks = VoiceFrameProfiler.Begin();
                SafeUpdateHud();
                VoiceFrameProfiler.End("hud", hudTicks);
                VoiceChatRoomDriver.Update();
                long pubTicks = VoiceFrameProfiler.Begin();
                VoiceLobbyRegistryPublisher.Update();
                VoiceFrameProfiler.End("publisher", pubTicks);
                VoiceFrameProfiler.End("vc.tick", vcTicks);
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
            if (Time.time - _lastUpdateErrorLogTime < 5f) return;
            _lastUpdateErrorLogTime = Time.time;
            VoiceDiagnostics.DebugError("[VC] HUD update failed: " + ex.Message);
        }
    }

}
