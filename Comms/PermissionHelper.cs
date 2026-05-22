#if ANDROID
using Il2CppInterop.Runtime.Injection;
using System.Collections;
using UnityEngine;
using BepInEx.Unity.IL2CPP.Utils.Collections;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Android microphone permission helper.
///
/// Nebula wraps microphone start in CheckAndShowConfirmPopup() which on Android
/// shows a dialog (voiceChat.dialog.noMic) and returns without starting.
/// The intent is that the user must confirm before mic is used.
///
/// Since we don't have Nebula's MetaUI, we use Unity's built-in
/// Application.RequestUserAuthorization coroutine, which shows the standard
/// Android system permission dialog for the microphone.
///
/// This is the equivalent of what Nebula intends with CheckAndShowConfirmPopup.
/// </summary>
internal class PermissionHelper : MonoBehaviour
{
    static PermissionHelper()
    {
        ClassInjector.RegisterTypeInIl2Cpp<PermissionHelper>();
    }

    private VoiceChatRoom? _room;
    private string         _device = "";

    internal void RequestMicAndStart(VoiceChatRoom room, string device)
    {
        _room   = room;
        _device = device;

        // If permission already granted, start immediately
        if (Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            _room?.StartMicNow(_device);
            return;
        }

        // Request permission then start when granted
        StartCoroutine(RequestAndStart().WrapToIl2Cpp());
    }

    private IEnumerator RequestAndStart()
    {
        VoiceDiagnostics.DebugInfo("[VC] Android: requesting microphone permission...");
        yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);

        if (Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            VoiceDiagnostics.DebugInfo("[VC] Android: microphone permission granted.");
            _room?.StartMicNow(_device);
        }
        else
        {
            VoiceDiagnostics.DebugWarning("[VC] Android: microphone permission denied.");
        }

        _room   = null;
        _device = "";
    }
}
#endif
