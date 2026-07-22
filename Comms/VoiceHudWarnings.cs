using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Provides the operational status text that used to be appended to Among Us's PingTracker label.
/// Rendering is owned by the dedicated compact voice HUD so the text can be clamped to the safe
/// area instead of inheriting the vanilla ping label's scene-dependent pivot and clipping bounds.
/// </summary>
internal static class VoiceHudWarnings
{
    private const float RefreshIntervalSeconds = 0.25f;
    private static float _nextRefreshTime;
    private static string _cached = string.Empty;

    internal static string BuildWarning()
    {
        if (Time.unscaledTime < _nextRefreshTime) return _cached;
        _nextRefreshTime = Time.unscaledTime + RefreshIntervalSeconds;
        _cached = ComputeWarning();
        return _cached;
    }

    internal static void Invalidate()
    {
        _nextRefreshTime = 0f;
        _cached = string.Empty;
    }

    private static string ComputeWarning()
    {
        var room = VoiceChatRoom.Current;
        if (room == null) return string.Empty;

        if (VoiceRoleMuteState.IsGracePeriodActive)
        {
            int secs = VoiceRoleMuteState.GracePeriodSecondsRemaining;
            var local = PlayerControl.LocalPlayer;
            if (local != null && VoiceRoleMuteState.GracePeriodCallerId == local.PlayerId)
                return $"you have the floor ({secs}s)";
            return $"caller has the floor ({secs}s)";
        }

        var connection = room.ConnectionProgress;
        bool showConnectionStatus =
            VoiceSettings.Instance?.ShowVoiceConnectionStatus.Value ?? true;
        if (room.ShouldShowConnectionProgress &&
            VoiceConnectionStatusPolicy.ShouldPresent(
                connection,
                VoiceSceneState.ResolvePhase(),
                showConnectionStatus))
        {
            // Two animation frames per second keeps the status visibly alive without making the
            // compact text jitter rapidly beside the voice buttons.
            int animationFrame = (int)(Time.unscaledTime * 2f);
            return VoiceConnectionStatusPolicy.BuildText(connection, animationFrame);
        }

        if (!room.UsingMicrophone && !room.Mute)
            return "mic unavailable";

        if (!room.UsingSpeaker && !VoiceChatHudState.IsSpeakerMuted)
            return "speaker unavailable";

        return string.Empty;
    }
}
