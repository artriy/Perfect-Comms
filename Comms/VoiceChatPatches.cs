using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatPatches
{
    private static bool _pushToTalkInputHeld;
    private static bool _radioInputHeld;
    private static int _lastMuteToggleFrame = -1;
    private static int _lastSpeakerToggleFrame = -1;
    private static int _lastVolumeToggleFrame = -1;
    private static int _lastLocalRefreshFrame = -1;
    private static int _lastHostRefreshFrame = -1;
    private static int _lastRadioChannelCycleFrame = -1;
    private static int _lastMicModeToggleFrame = -1;
    private static System.DateTime _lastKbErrorLogUtc;

    internal static void RegisterKeybindHandlers()
    {
        VoiceChatKeybinds.ToggleMute.OnActivate(ToggleMuteFromInput);
        VoiceChatKeybinds.ToggleSpeaker.OnActivate(ToggleSpeakerFromInput);
        VoiceChatKeybinds.VolumeMenu.OnActivate(ToggleVolumeMenuFromInput);
        VoiceChatKeybinds.LocalVoiceRefresh.OnActivate(RequestLocalRefreshFromInput);
        VoiceChatKeybinds.HostVoiceRefresh.OnActivate(RequestHostRefreshFromInput);
        VoiceChatKeybinds.CycleTeamRadioChannel.OnActivate(CycleTeamRadioChannelFromInput);
        VoiceChatKeybinds.ToggleMicMode.OnActivate(ToggleMicModeFromInput);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    static void KeyboardUpdate_Post()
    {
        try
        {
            if (VoiceUiKit.RebindRow.ShouldSuppressKeybinds)
            {
                ReleaseHeldInputsForRebind();
                return;
            }

            VoiceChatKeybinds.ToggleMute.FireIfPressed();
            VoiceChatKeybinds.ToggleSpeaker.FireIfPressed();
            VoiceChatKeybinds.VolumeMenu.FireIfPressed();
            VoiceChatKeybinds.LocalVoiceRefresh.FireIfPressed();
            VoiceChatKeybinds.HostVoiceRefresh.FireIfPressed();
            VoiceChatKeybinds.CycleTeamRadioChannel.FireIfPressed();
            VoiceChatKeybinds.ToggleMicMode.FireIfPressed();

            bool canUseRadio = VoiceChatHudState.CanUseTeamRadioInput();
            bool held = false;
            bool down = false;
            bool up = false;
            if (canUseRadio)
            {
                var radioHold = ReadHold(VoiceChatKeybinds.TeamRadio.IsHeld(), ref _radioInputHeld);
                held = radioHold.Held;
                down = radioHold.Down;
                up = radioHold.Up;
            }
            else
            {
                _radioInputHeld = false;
            }

            VoiceChatHudState.UpdateTeamRadioHold(held, down, up);

            if (!VoiceChatHudState.IsPushToTalkMode())
            {
                _pushToTalkInputHeld = false;
                VoiceChatHudState.UpdatePushToTalkHeld(false);
                return;
            }

            bool pttHeld = ReadHold(VoiceChatKeybinds.PushToTalk.IsHeld(), ref _pushToTalkInputHeld).Held;
            VoiceChatHudState.UpdatePushToTalkHeld(pttHeld);
        }
        catch (System.Exception ex)
        {
            var now = System.DateTime.UtcNow;
            if ((now - _lastKbErrorLogUtc).TotalSeconds >= 5)
            {
                _lastKbErrorLogUtc = now;
                VoiceDiagnostics.DebugError($"[VC] keyboard radio/PTT update failed: {ex.Message}");
            }
        }
    }

    private static void ReleaseHeldInputsForRebind()
    {
        bool radioWasHeld = _radioInputHeld;
        _radioInputHeld = false;
        _pushToTalkInputHeld = false;
        VoiceChatHudState.UpdateTeamRadioHold(false, false, radioWasHeld);
        VoiceChatHudState.UpdatePushToTalkHeld(false);
    }

    internal static bool ShouldIgnoreToggleKeybinds()
    {
        if (!HudManager.InstanceExists) return false;

        var chat = HudManager.Instance.Chat;
        return chat != null && chat.IsOpenOrOpening;
    }

    private static void ToggleMuteFromInput()
    {
        if (ShouldIgnoreToggleKeybinds()) return;
        if (!TryConsumeToggleFrame(ref _lastMuteToggleFrame)) return;
        VoiceChatHudState.ToggleMutePublic();
    }

    private static void ToggleSpeakerFromInput()
    {
        if (ShouldIgnoreToggleKeybinds()) return;
        if (!TryConsumeToggleFrame(ref _lastSpeakerToggleFrame)) return;
        VoiceChatHudState.ToggleSpeakerPublic();
    }

    private static void ToggleVolumeMenuFromInput()
    {
        if (ShouldIgnoreToggleKeybinds()) return;
        if (!TryConsumeToggleFrame(ref _lastVolumeToggleFrame)) return;
        VoiceVolumeMenu.Toggle();
    }

    private static void RequestLocalRefreshFromInput()
    {
        if (ShouldIgnoreToggleKeybinds()) return;
        if (!TryConsumeToggleFrame(ref _lastLocalRefreshFrame)) return;
        VoiceChatRoom.RequestLocalVoiceRefreshFromKeybind();
    }

    private static void RequestHostRefreshFromInput()
    {
        if (ShouldIgnoreToggleKeybinds()) return;
        if (!TryConsumeToggleFrame(ref _lastHostRefreshFrame)) return;
        VoiceChatRoom.RequestHostVoiceRefreshFromKeybind();
    }

    private static void CycleTeamRadioChannelFromInput()
    {
        if (ShouldIgnoreToggleKeybinds()) return;
        if (!VoiceChatHudState.CanUseTeamRadioInput()) return;
        if (!TryConsumeToggleFrame(ref _lastRadioChannelCycleFrame)) return;
        VoiceChatHudState.CycleTeamRadioChannel();
    }

    private static void ToggleMicModeFromInput()
    {
        if (ShouldIgnoreToggleKeybinds()) return;
        if (!TryConsumeToggleFrame(ref _lastMicModeToggleFrame)) return;
        VoiceChatHudState.ToggleMicMode();
    }

    private static bool TryConsumeToggleFrame(ref int lastFrame)
    {
        int frame = Time.frameCount;
        if (lastFrame == frame) return false;

        lastFrame = frame;
        return true;
    }

    private static HoldInputState ReadHold(bool held, ref bool previousHeld)
    {
        bool down = held && !previousHeld;
        bool up = !held && previousHeld;
        previousHeld = held;
        return new HoldInputState(held, down, up);
    }

    private readonly struct HoldInputState
    {
        public HoldInputState(bool held, bool down, bool up)
        {
            Held = held;
            Down = down;
            Up = up;
        }

        public bool Held { get; }
        public bool Down { get; }
        public bool Up { get; }
    }
}
