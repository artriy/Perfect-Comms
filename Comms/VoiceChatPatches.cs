using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatPatches
{
    private static bool _pushToTalkInputHeld;
    private static bool _radioInputHeld;
    private static bool _transmitReleaseRequired;
    private static int _lastMuteToggleFrame = -1;
    private static int _lastSpeakerToggleFrame = -1;
    private static int _lastVolumeToggleFrame = -1;
    private static int _lastLocalRefreshFrame = -1;
    private static int _lastHostRefreshFrame = -1;
    private static int _lastRadioChannelCycleFrame = -1;
    private static int _lastMicModeToggleFrame = -1;
    private static System.DateTime _lastKbErrorLogUtc;
    private static VoiceAliveDeadMixFocus _aliveDeadMixFocus;

    internal static VoiceAliveDeadMixFocus AliveDeadMixFocus => _aliveDeadMixFocus;

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
            if (ShouldSuppressVoiceInput())
            {
                ReleaseHeldTransmitInputs();
                return;
            }

            // A hold that crossed a privacy boundary (chat/modal/focus/rebind/session) must be
            // physically released before it can arm again. Otherwise closing chat while the key
            // is still down immediately resumes capture without a new user action.
            if (_transmitReleaseRequired)
            {
                if (VoiceChatKeybinds.PushToTalk.IsHeld() || VoiceChatKeybinds.TeamRadio.IsHeld())
                {
                    ReleaseHeldTransmitInputs();
                    return;
                }
                _transmitReleaseRequired = false;
            }

            VoiceChatKeybinds.ToggleMute.FireIfPressed();
            VoiceChatKeybinds.ToggleSpeaker.FireIfPressed();
            VoiceChatKeybinds.VolumeMenu.FireIfPressed();
            UpdateAliveDeadMixHold();
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
            // Never leave a capture-open hold latched because an IL2CPP object disappeared while
            // the keyboard patch was running. The release path is deliberately independent from
            // the failing read and is also used by chat/modal/focus and session boundaries.
            try { ReleaseHeldTransmitInputs(); }
            catch { SetAliveDeadMixFocus(VoiceAliveDeadMixFocus.Neutral, showToast: false); }
            var now = System.DateTime.UtcNow;
            if ((now - _lastKbErrorLogUtc).TotalSeconds >= 5)
            {
                _lastKbErrorLogUtc = now;
                VoiceDiagnostics.DebugError($"[VC] keyboard radio/PTT update failed: {ex.Message}");
            }
        }
    }

    internal static void ReleaseHeldTransmitInputs()
    {
        _transmitReleaseRequired = true;
        _radioInputHeld = false;
        _pushToTalkInputHeld = false;
        VoiceChatHudState.ReleaseTransmitHoldsFailClosed();
        SetAliveDeadMixFocus(VoiceAliveDeadMixFocus.Neutral, showToast: false);
    }

    internal static bool ShouldSuppressVoiceInput()
    {
        bool chatOpen = false;
        if (HudManager.InstanceExists)
        {
            var chat = HudManager.Instance.Chat;
            chatOpen = chat != null && chat.IsOpenOrOpening;
        }
        return ShouldSuppressVoiceInput(
            Application.isFocused,
            VoiceUiKit.RebindRow.ShouldSuppressKeybinds,
            VoiceUiKit.AnyPanelOpen,
            chatOpen);
    }

    internal static bool ShouldSuppressVoiceInput(
        bool applicationFocused,
        bool rebindCapturing,
        bool modalOpen,
        bool chatOpen)
        => !applicationFocused || rebindCapturing || modalOpen || chatOpen;

    internal static bool ShouldIgnoreToggleKeybinds()
        => ShouldSuppressVoiceInput();

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

    private static void UpdateAliveDeadMixHold()
    {
        var focus = ShouldIgnoreToggleKeybinds()
            ? VoiceAliveDeadMixFocus.Neutral
            : VoiceVolumeMath.ResolveAliveDeadMixFocus(
                VoiceChatKeybinds.AliveLouderDeadQuieter.IsHeld(),
                VoiceChatKeybinds.AliveQuieterDeadLouder.IsHeld());
        SetAliveDeadMixFocus(focus, showToast: true);
    }

    private static void SetAliveDeadMixFocus(VoiceAliveDeadMixFocus focus, bool showToast)
    {
        if (_aliveDeadMixFocus == focus) return;
        _aliveDeadMixFocus = focus;
        var profile = GetAliveDeadMixProfile(focus);
        float aliveVolume = VoiceVolumeMath.NormalizeUserVolume(profile.AliveVolume);
        float deadVolume = VoiceVolumeMath.NormalizeUserVolume(profile.DeadVolume);
        if (showToast)
        {
            VoiceChatHudState.ShowCompactStatus(focus == VoiceAliveDeadMixFocus.Neutral
                ? "Voice mix: Normal"
                : $"Voice mix: Alive {Mathf.RoundToInt(aliveVolume * 100f)}% / Dead {Mathf.RoundToInt(deadVolume * 100f)}%");
        }
        VoiceDiagnostics.Log(
            "voice.mix.hold",
            $"focus={focus.ToString().ToLowerInvariant()} alive={aliveVolume:0.00} dead={deadVolume:0.00}");
    }

    private static VoiceAliveDeadMixProfile GetAliveDeadMixProfile(VoiceAliveDeadMixFocus focus)
    {
        var settings = VoiceSettings.Instance;
        return focus switch
        {
            VoiceAliveDeadMixFocus.Alive => settings?.AliveFocusProfile
                ?? VoiceVolumeMath.DefaultAliveFocusProfile,
            VoiceAliveDeadMixFocus.Dead => settings?.DeadFocusProfile
                ?? VoiceVolumeMath.DefaultDeadFocusProfile,
            _ => new VoiceAliveDeadMixProfile(1f, 1f),
        };
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
