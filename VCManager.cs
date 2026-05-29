using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoiceChatPlugin.VoiceChat;

namespace VoiceChatPlugin;

internal class VCManager : MonoBehaviour
{
    private static bool _sceneHookRegistered;
    private static GameObject? _managerObject;

    static VCManager()
    {
        ClassInjector.RegisterTypeInIl2Cpp<VCManager>();
    }

    internal static void RegisterSceneHook()
    {
        if (_sceneHookRegistered) return;
        _sceneHookRegistered = true;

        SceneManager.sceneLoaded +=
            (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode _)
    {
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
        switch (SceneManager.GetActiveScene().name)
        {
            case "OnlineGame":
            case "EndGame":
                SafeUpdateHud();
                VoiceChatRoomDriver.Update();
                VoiceLobbyRegistryPublisher.Update();
                break;
        }
    }

    // UpdateHud recomputes the local mute decision every frame and walks AllPlayerControls (the
    // jailor-unmute scan + role-state refresh). An IL2CPP scene transition can invalidate that
    // collection mid-frame; a throw here must not abort Update or the player would be stranded in
    // whatever mute state they were last in (wrongly muted, or wrongly audible).
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
