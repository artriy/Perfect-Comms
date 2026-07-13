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

    private VoiceChatRoom? _pendingRoom;
    private string _pendingDevice = "";
    private bool _requestInFlight;

    internal void RequestMicAndStart(VoiceChatRoom room, string device)
    {
        _pendingRoom = room;
        _pendingDevice = device;

        // If permission already granted, start immediately
        if (Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            CompletePending(granted: true);
            return;
        }

        // One OS request at a time. Repeated device changes or a room replacement update the
        // pending target; the completion applies only to whichever room is still current.
        if (_requestInFlight) return;
        _requestInFlight = true;
        StartCoroutine(RequestAndStart().WrapToIl2Cpp());
    }

    private IEnumerator RequestAndStart()
    {
        VoiceDiagnostics.DebugInfo("[VC] Android: requesting microphone permission...");
        yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);

        var granted = Application.HasUserAuthorization(UserAuthorization.Microphone);
        if (granted)
        {
            VoiceDiagnostics.DebugInfo("[VC] Android: microphone permission granted.");
        }
        else
        {
            VoiceDiagnostics.DebugWarning("[VC] Android: microphone permission denied.");
        }

        CompletePending(granted);
    }

    private void CompletePending(bool granted)
    {
        _requestInFlight = false;
        var room = _pendingRoom;
        var device = _pendingDevice;
        _pendingRoom = null;
        _pendingDevice = "";
        if (granted && room != null && ReferenceEquals(VoiceChatRoom.Current, room))
            room.StartMicAfterPermission(device);
    }
}
#endif
