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

    void Update()
    {
        switch (SceneManager.GetActiveScene().name)
        {
            case "OnlineGame":
            case "EndGame":
                VoiceChatHudState.UpdateHud();
                VoiceChatRoomDriver.Update();
                VoiceLobbyRegistryPublisher.Update();
                break;
        }
    }

}
