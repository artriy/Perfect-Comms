using HarmonyLib;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatPatches
{
    private static bool _pushToTalkInputHeld;
    private static bool _pushToMuteInputHeld;
    private static bool _radioInputHeld;
    private static bool _transmitInputQuarantinePending;
    private static int _lastMuteToggleFrame = -1;
    private static int _lastSpeakerToggleFrame = -1;
    private static int _lastVolumeToggleFrame = -1;
    private static int _lastLocalRefreshFrame = -1;
    private static int _lastRadioChannelCycleFrame = -1;
    private static int _lastMicModeToggleFrame = -1;
    private static int _lastPushToTalkPollFrame = -1;
    private static int _lastPushToMutePollFrame = -1;
    private static int _lastTeamRadioPollFrame = -1;
    private static System.DateTime _lastKbErrorLogUtc;
    private static VoiceAliveDeadMixFocus _aliveDeadMixFocus;

    internal static VoiceAliveDeadMixFocus AliveDeadMixFocus => _aliveDeadMixFocus;

    internal static void RegisterKeybindHandlers()
    {
        VoiceChatKeybinds.ToggleMute.OnActivate(ToggleMuteFromInput);
        VoiceChatKeybinds.ToggleSpeaker.OnActivate(ToggleSpeakerFromInput);
        VoiceChatKeybinds.VolumeMenu.OnActivate(ToggleVolumeMenuFromInput);
        VoiceChatKeybinds.LocalVoiceRefresh.OnActivate(RequestLocalRefreshFromInput);
        VoiceChatKeybinds.CycleTeamRadioChannel.OnActivate(CycleTeamRadioChannelFromInput);
        VoiceChatKeybinds.ToggleMicMode.OnActivate(ToggleMicModeFromInput);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    static void KeyboardUpdate_Post()
        => ProcessKeybinds("keyboard");

    /// <summary>
    /// Scene construction and EndGame do not guarantee a gameplay joystick, while the voice room
    /// deliberately remains alive. Poll the complete keybind state machine from the persistent
    /// manager in both voice scenes. Per-frame guards make the duplicate joystick path harmless.
    /// </summary>
    internal static void UpdateKeybindsFromFrameDriver()
        => ProcessKeybinds("frame-driver");

    private static void ProcessKeybinds(string source)
    {
        try
        {
            // A prior input-wrapper failure could not safely inspect physical keys. Quarantine
            // them before any normal polling resumes once Unity input becomes readable again.
            if (_transmitInputQuarantinePending)
                ReleaseHeldTransmitInputs();
            if (ShouldHardSuppressVoiceInput(
                    Application.isFocused,
                    VoiceUiKit.RebindRow.ShouldSuppressKeybinds,
                    VoiceUiKit.AnyPanelOpen,
                    Minigame.Instance != null,
                    IsFriendsListOpen()))
            {
                ReleaseHeldTransmitInputs();
                SuppressHardBlockedBindings();
                return;
            }

            bool chatOpen = IsChatOpen();
            bool allowAllWhileChatOpen =
                VoiceSettings.Instance?.AllowKeybindsWhileChatOpen.Value == true;

            FireIfAllowedForChat(
                VoiceChatKeybinds.ToggleMute, chatOpen, allowAllWhileChatOpen);
            FireIfAllowedForChat(
                VoiceChatKeybinds.ToggleSpeaker, chatOpen, allowAllWhileChatOpen);
            FireIfAllowedForChat(
                VoiceChatKeybinds.VolumeMenu, chatOpen, allowAllWhileChatOpen);

            // Player Volumes opens in this pipeline. Its Show() release must remain authoritative;
            // never dispatch another voice action later in the frame after the modal opens.
            if (VoiceUiKit.AnyPanelOpen)
            {
                ReleaseHeldTransmitInputs();
                SuppressHardBlockedBindings();
                return;
            }

            UpdateAliveDeadMixHold(chatOpen, allowAllWhileChatOpen);
            FireIfAllowedForChat(
                VoiceChatKeybinds.LocalVoiceRefresh, chatOpen, allowAllWhileChatOpen);
            FireIfAllowedForChat(
                VoiceChatKeybinds.CycleTeamRadioChannel, chatOpen, allowAllWhileChatOpen);
            FireIfAllowedForChat(
                VoiceChatKeybinds.ToggleMicMode, chatOpen, allowAllWhileChatOpen);
            UpdateTeamRadioHold(chatOpen, allowAllWhileChatOpen);
            UpdatePushToMuteHold(chatOpen, allowAllWhileChatOpen);
            UpdatePushToTalkHold(chatOpen, allowAllWhileChatOpen);
        }
        catch (System.Exception ex)
        {
            HandleTransmitInputFailure(source, ex);
        }
    }

    private static void UpdateTeamRadioHold(bool chatOpen, bool allowAllWhileChatOpen)
    {
        var binding = VoiceChatKeybinds.TeamRadio;
        bool blockedForChat = ShouldBlockBindingForChat(
            chatOpen, allowAllWhileChatOpen, binding.AllowWhileChatOpen);
        if (blockedForChat)
            binding.SuppressUntilReleased();

        int frame = Time.frameCount;
        if (_lastTeamRadioPollFrame == frame &&
            (!blockedForChat || !_radioInputHeld))
            return;
        _lastTeamRadioPollFrame = frame;

        if (blockedForChat)
        {
            var released = ReadHold(false, ref _radioInputHeld);
            VoiceChatHudState.UpdateTeamRadioHold(false, false, released.Up);
            return;
        }

        bool canUseRadio = VoiceChatHudState.CanUseTeamRadioInput();
        bool held = false;
        bool down = false;
        bool up = false;
        if (canUseRadio)
        {
            var radioHold = ReadHold(binding.IsHeld(), ref _radioInputHeld);
            held = radioHold.Held;
            down = radioHold.Down;
            up = radioHold.Up;
        }
        else
        {
            _radioInputHeld = false;
        }

        VoiceChatHudState.UpdateTeamRadioHold(held, down, up);
    }

    private static void UpdatePushToTalkHold(bool chatOpen, bool allowAllWhileChatOpen)
    {
        var binding = VoiceChatKeybinds.PushToTalk;
        bool blockedForChat = ShouldBlockBindingForChat(
            chatOpen, allowAllWhileChatOpen, binding.AllowWhileChatOpen);
        if (blockedForChat)
            binding.SuppressUntilReleased();

        int frame = Time.frameCount;
        if (_lastPushToTalkPollFrame == frame &&
            (!blockedForChat || !_pushToTalkInputHeld))
            return;
        _lastPushToTalkPollFrame = frame;

        if (blockedForChat)
        {
            _pushToTalkInputHeld = false;
            VoiceChatHudState.UpdatePushToTalkHeld(false);
            return;
        }

        if (!VoiceChatHudState.IsPushToTalkMode())
        {
            _pushToTalkInputHeld = false;
            VoiceChatHudState.UpdatePushToTalkHeld(false);
            return;
        }

        bool held = ReadHold(binding.IsHeld(), ref _pushToTalkInputHeld).Held;
        VoiceChatHudState.UpdatePushToTalkHeld(held);
    }

    private static void UpdatePushToMuteHold(bool chatOpen, bool allowAllWhileChatOpen)
    {
        var binding = VoiceChatKeybinds.PushToMute;
        bool primaryHeldRaw = binding.IsPrimaryHeldRaw();
        bool blockedForChat = ShouldBlockBindingForChat(
            chatOpen, allowAllWhileChatOpen, binding.AllowWhileChatOpen);
        if (blockedForChat)
            binding.SuppressUntilReleased();

        int frame = Time.frameCount;
        if (_lastPushToMutePollFrame == frame &&
            (!blockedForChat || !_pushToMuteInputHeld || primaryHeldRaw))
            return;
        _lastPushToMutePollFrame = frame;

        // If Push To Mute was already active when a boundary was crossed, fail closed until
        // its physical primary key is released. A blocked key that was not active cannot arm.
        bool held = (!blockedForChat && binding.IsHeld()) ||
                    (_pushToMuteInputHeld && primaryHeldRaw);
        held = ReadHold(held, ref _pushToMuteInputHeld).Held;
        VoiceChatHudState.UpdatePushToMuteHeld(held);
    }

    private static void HandleTransmitInputFailure(string source, System.Exception ex)
    {
        // Never leave a capture-open hold latched because an IL2CPP object disappeared while
        // reading input. If even the normal release cannot inspect physical keys, fall back to
        // a release that performs no further input reads.
        try { ReleaseHeldTransmitInputs(); }
        catch { EmergencyReleaseHeldTransmitInputs(); }
        var now = System.DateTime.UtcNow;
        if ((now - _lastKbErrorLogUtc).TotalSeconds < 5) return;

        _lastKbErrorLogUtc = now;
        VoiceDiagnostics.DebugError($"[VC] {source} hold-input update failed: {ex.Message}");
    }
    private static void EmergencyReleaseHeldTransmitInputs()
    {
        // Active Push To Mute must fail closed when its raw physical state cannot be trusted.
        // Retry the full binding quarantine before normal polling on the next readable frame.
        bool preservePushToMute = _pushToMuteInputHeld;
        _transmitInputQuarantinePending = true;
        _radioInputHeld = false;
        _pushToTalkInputHeld = false;
        _pushToMuteInputHeld = preservePushToMute;
        try { VoiceChatHudState.ReleaseTransmitHoldsFailClosed(preservePushToMute); }
        catch { }
        try { SetAliveDeadMixFocus(VoiceAliveDeadMixFocus.Neutral, showToast: false); }
        catch { _aliveDeadMixFocus = VoiceAliveDeadMixFocus.Neutral; }
    }


    internal static void ReleaseHeldTransmitInputs()
    {
        // Quarantine every released hold until its physical binding is released so lifecycle
        // callbacks cannot reopen transmission or a mix adjustment on the next unblocked frame.
        // An already-active Push To Mute remains fail-closed while its physical primary is down.
        bool preservePushToMute = _pushToMuteInputHeld &&
                                  VoiceChatKeybinds.PushToMute.IsPrimaryHeldRaw();
        VoiceChatKeybinds.PushToTalk.SuppressUntilReleased();
        VoiceChatKeybinds.TeamRadio.SuppressUntilReleased();
        VoiceChatKeybinds.PushToMute.SuppressUntilReleased();
        VoiceChatKeybinds.AliveLouderDeadQuieter.SuppressUntilReleased();
        VoiceChatKeybinds.AliveQuieterDeadLouder.SuppressUntilReleased();
        _radioInputHeld = false;
        _pushToTalkInputHeld = false;
        _pushToMuteInputHeld = preservePushToMute;
        VoiceChatHudState.ReleaseTransmitHoldsFailClosed(preservePushToMute);
        SetAliveDeadMixFocus(VoiceAliveDeadMixFocus.Neutral, showToast: false);
        _transmitInputQuarantinePending = false;
    }

    internal static bool ShouldHardSuppressVoiceInput(
        bool applicationFocused,
        bool rebindCapturing,
        bool modalOpen,
        bool minigameOpen,
        bool friendsListOpen)
        => !applicationFocused || rebindCapturing || modalOpen ||
           minigameOpen || friendsListOpen;

    internal static bool ShouldBlockBindingForChat(
        bool chatOpen,
        bool allowAllWhileChatOpen,
        bool bindingAllowedWhileChatOpen)
        => chatOpen && !allowAllWhileChatOpen && !bindingAllowedWhileChatOpen;

    internal static bool ShouldSuppressVoiceInput()
    {
        bool allowAllWhileChatOpen =
            VoiceSettings.Instance?.AllowKeybindsWhileChatOpen.Value == true;
        return ShouldHardSuppressVoiceInput(
                   Application.isFocused,
                   VoiceUiKit.RebindRow.ShouldSuppressKeybinds,
                   VoiceUiKit.AnyPanelOpen,
                   Minigame.Instance != null,
                   IsFriendsListOpen()) ||
               ShouldBlockBindingForChat(
                   IsChatOpen(), allowAllWhileChatOpen,
                   bindingAllowedWhileChatOpen: false);
    }


    internal static bool IsFriendsListOpen()
    {
        var friendsList = FriendsListUI.Instance;
        return friendsList != null && friendsList.IsOpen;
    }

    private static bool IsChatOpen()
    {
        if (!HudManager.InstanceExists) return false;
        var chat = HudManager.Instance.Chat;
        return chat != null && chat.IsOpenOrOpening;
    }

    private static void FireIfAllowedForChat(
        VoiceKeybind binding,
        bool chatOpen,
        bool allowAllWhileChatOpen)
    {
        if (ShouldBlockBindingForChat(
                chatOpen, allowAllWhileChatOpen, binding.AllowWhileChatOpen))
        {
            binding.SuppressUntilReleased();
            return;
        }

        binding.FireIfPressed();
    }

    private static void SuppressHardBlockedBindings()
    {
        bool nonModalHardBlock = ShouldHardSuppressVoiceInput(
            Application.isFocused,
            VoiceUiKit.RebindRow.ShouldSuppressKeybinds,
            modalOpen: false,
            Minigame.Instance != null,
            IsFriendsListOpen());

        foreach (var binding in VoiceChatKeybinds.AllBindings)
        {
            // A panel's own hotkey may close it, but no non-modal hard blocker may be bypassed.
            if (!nonModalHardBlock &&
                ((binding == VoiceChatKeybinds.OpenVoiceMenu && VoiceSettingsPanel.IsOpen) ||
                 (binding == VoiceChatKeybinds.OpenHostVoiceSettings && HostSettingsPanel.IsOpen)))
                continue;

            binding.SuppressUntilReleased();
        }
    }

    private static void ToggleMuteFromInput()
    {
        if (!TryConsumeToggleFrame(ref _lastMuteToggleFrame)) return;
        VoiceChatHudState.ToggleMutePublic();
    }

    private static void ToggleSpeakerFromInput()
    {
        if (!TryConsumeToggleFrame(ref _lastSpeakerToggleFrame)) return;
        VoiceChatHudState.ToggleSpeakerPublic();
    }

    private static void ToggleVolumeMenuFromInput()
    {
        if (!TryConsumeToggleFrame(ref _lastVolumeToggleFrame)) return;
        VoiceVolumeMenu.Toggle();
    }

    private static void UpdateAliveDeadMixHold(
        bool chatOpen,
        bool allowAllWhileChatOpen)
    {
        var aliveBinding = VoiceChatKeybinds.AliveLouderDeadQuieter;
        bool aliveBlocked = ShouldBlockBindingForChat(
            chatOpen, allowAllWhileChatOpen, aliveBinding.AllowWhileChatOpen);
        if (aliveBlocked) aliveBinding.SuppressUntilReleased();

        var deadBinding = VoiceChatKeybinds.AliveQuieterDeadLouder;
        bool deadBlocked = ShouldBlockBindingForChat(
            chatOpen, allowAllWhileChatOpen, deadBinding.AllowWhileChatOpen);
        if (deadBlocked) deadBinding.SuppressUntilReleased();

        var focus = VoiceVolumeMath.ResolveAliveDeadMixFocus(
            !aliveBlocked && aliveBinding.IsHeld(),
            !deadBlocked && deadBinding.IsHeld());
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
        if (!TryConsumeToggleFrame(ref _lastLocalRefreshFrame)) return;
        VoiceChatRoom.RequestLocalVoiceRefreshFromKeybind();
    }

    private static void CycleTeamRadioChannelFromInput()
    {
        if (!VoiceChatHudState.CanUseTeamRadioInput()) return;
        if (!TryConsumeToggleFrame(ref _lastRadioChannelCycleFrame)) return;
        VoiceChatHudState.CycleTeamRadioChannel();
    }

    private static void ToggleMicModeFromInput()
    {
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
