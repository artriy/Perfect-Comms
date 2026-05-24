using HarmonyLib;
using MiraAPI.Keybinds;
using Rewired;
using System.Reflection;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

[HarmonyPatch]
public static class VoiceChatPatches
{
    private const float ChatHoldActivationDelaySeconds = 0.30f;
    private static readonly FieldInfo? TextBoxCaretPosField = AccessTools.Field(typeof(TextBoxTMP), "caretPos");
    private static readonly MethodInfo? TextBoxSetCaretPositionMethod = AccessTools.Method(typeof(TextBoxTMP), "SetCaretPosition");
    private static ChatHoldGate _pushToTalkChatGate;
    private static ChatHoldGate _radioChatGate;

    internal static void RegisterKeybindHandlers()
    {
        VoiceChatKeybinds.ToggleMute.OnActivate(() =>
        {
            if (ShouldIgnoreToggleKeybinds()) return;
            VoiceChatHudState.ToggleMutePublic();
        });
        VoiceChatKeybinds.ToggleSpeaker.OnActivate(() =>
        {
            if (ShouldIgnoreToggleKeybinds()) return;
            VoiceChatHudState.ToggleSpeakerPublic();
        });
        VoiceChatKeybinds.VolumeMenu.OnActivate(() =>
        {
            if (ShouldIgnoreToggleKeybinds()) return;
            VoiceVolumeMenu.Toggle();
        });
    }

    [HarmonyPostfix, HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    static void KeyboardUpdate_Post()
    {
        bool chatOpen = IsChatOpenOrOpening();
        var player = ReInput.players.GetPlayer(0);

        bool canUseRadio = VoiceChatHudState.CanUseImpostorRadioInput();
        var action = VoiceChatKeybinds.ImpostorRadio.RewiredInputAction;
        bool held = false;
        bool down = false;
        bool up = false;
        if (action != null && canUseRadio)
        {
            held = ApplyChatHoldGate(
                player.GetButton(action.id),
                player.GetButtonDown(action.id),
                player.GetButtonUp(action.id),
                chatOpen,
                ref _radioChatGate,
                out down,
                out up);
        }
        else
        {
            _radioChatGate.Reset();
        }

        VoiceChatHudState.UpdateImpostorRadioHold(held, down, up);

        var pttAction = VoiceChatKeybinds.PushToTalk.RewiredInputAction;
        if (pttAction == null || !VoiceChatHudState.IsPushToTalkMode())
        {
            _pushToTalkChatGate.Reset();
            VoiceChatHudState.UpdatePushToTalkHeld(false);
            return;
        }

        bool pttHeld = ApplyChatHoldGate(
            player.GetButton(pttAction.id),
            player.GetButtonDown(pttAction.id),
            player.GetButtonUp(pttAction.id),
            chatOpen,
            ref _pushToTalkChatGate,
            out _,
            out _);
        VoiceChatHudState.UpdatePushToTalkHeld(pttHeld);
    }

    internal static bool ShouldIgnoreToggleKeybinds()
    {
        if (!HudManager.InstanceExists) return false;

        var chat = HudManager.Instance.Chat;
        return chat != null && chat.IsOpenOrOpening;
    }

    private static bool ApplyChatHoldGate(
        bool rawHeld,
        bool rawDown,
        bool rawUp,
        bool chatOpen,
        ref ChatHoldGate gate,
        out bool justPressed,
        out bool justReleased)
    {
        if (!chatOpen)
        {
            gate.Reset();
            justPressed = rawDown;
            justReleased = rawUp;
            return rawHeld;
        }

        if (!rawHeld)
        {
            justPressed = false;
            justReleased = gate.Active;
            if (gate.RawHeld && !gate.Active)
                InsertPendingTapCharacter(ref gate);
            gate.Reset();
            return false;
        }

        if (!gate.RawHeld)
        {
            gate.RawHeld = true;
            gate.StartTime = Time.realtimeSinceStartup;
        }

        if (!gate.Active && Time.realtimeSinceStartup - gate.StartTime >= ChatHoldActivationDelaySeconds)
        {
            gate.Active = true;
            justPressed = true;
            justReleased = false;
            return true;
        }

        justPressed = false;
        justReleased = false;
        return gate.Active;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.Update))]
    static bool TextBox_Update_Prefix(TextBoxTMP __instance)
        => !ShouldHoldVoiceKeyOwnChatInput(__instance);

    private static bool ShouldHoldVoiceKeyOwnChatInput(TextBoxTMP textBox)
    {
        if (!HudManager.InstanceExists) return false;

        var chat = HudManager.Instance.Chat;
        if (chat == null || !chat.IsOpenOrOpening || chat.freeChatField == null)
            return false;
        if (chat.freeChatField.textArea != textBox)
            return false;

        if (VoiceChatHudState.IsPushToTalkMode() &&
            TryCaptureHeldChatKey(VoiceChatKeybinds.PushToTalk, textBox, ref _pushToTalkChatGate))
            return true;
        return VoiceChatHudState.CanUseImpostorRadioInput() &&
               TryCaptureHeldChatKey(VoiceChatKeybinds.ImpostorRadio, textBox, ref _radioChatGate);
    }

    private static bool TryCaptureHeldChatKey(MiraKeybind keybind, TextBoxTMP textBox, ref ChatHoldGate gate)
    {
        var action = keybind.RewiredInputAction;
        if (action == null || !TryGetChatCharacter(keybind.CurrentKey, out char character))
            return false;

        var player = ReInput.players.GetPlayer(0);
        if (!player.GetButton(action.id))
            return false;

        if (!gate.RawHeld)
        {
            gate.RawHeld = true;
            gate.StartTime = Time.realtimeSinceStartup;
        }

        gate.PendingTextBox = textBox;
        gate.PendingChar = character;
        return true;
    }

    private static void InsertPendingTapCharacter(ref ChatHoldGate gate)
    {
        var textBox = gate.PendingTextBox;
        if (textBox == null || gate.PendingChar == '\0')
            return;

        string current = textBox.text ?? string.Empty;
        int caret = GetCaretPosition(textBox, current.Length);
        string next = current.Insert(caret, gate.PendingChar.ToString());
        textBox.SetText(next);
        SetCaretPosition(textBox, caret + 1);
    }

    private static int GetCaretPosition(TextBoxTMP textBox, int fallback)
    {
        if (TextBoxCaretPosField?.GetValue(textBox) is int caret)
            return Mathf.Clamp(caret, 0, fallback);
        return fallback;
    }

    private static void SetCaretPosition(TextBoxTMP textBox, int position)
    {
        if (TextBoxSetCaretPositionMethod != null)
            TextBoxSetCaretPositionMethod.Invoke(textBox, new object[] { position });
    }

    private static bool TryGetChatCharacter(KeyboardKeyCode key, out char character)
    {
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (key >= KeyboardKeyCode.A && key <= KeyboardKeyCode.Z)
        {
            character = (char)((shift ? 'A' : 'a') + ((int)key - (int)KeyboardKeyCode.A));
            return true;
        }

        if (key >= KeyboardKeyCode.Alpha0 && key <= KeyboardKeyCode.Alpha9)
        {
            character = shift ? ShiftedNumberCharacter(key) : (char)(int)key;
            return true;
        }

        if (key >= KeyboardKeyCode.Keypad0 && key <= KeyboardKeyCode.Keypad9)
        {
            character = (char)('0' + ((int)key - (int)KeyboardKeyCode.Keypad0));
            return true;
        }

        character = key switch
        {
            KeyboardKeyCode.Space => ' ',
            KeyboardKeyCode.KeypadPeriod => '.',
            KeyboardKeyCode.KeypadDivide => '/',
            KeyboardKeyCode.KeypadMultiply => '*',
            KeyboardKeyCode.KeypadMinus => '-',
            KeyboardKeyCode.KeypadPlus => '+',
            KeyboardKeyCode.KeypadEquals => '=',
            _ => (int)key >= 33 && (int)key <= 126 ? (char)(int)key : '\0',
        };
        return character != '\0';
    }

    private static char ShiftedNumberCharacter(KeyboardKeyCode key)
        => key switch
        {
            KeyboardKeyCode.Alpha1 => '!',
            KeyboardKeyCode.Alpha2 => '@',
            KeyboardKeyCode.Alpha3 => '#',
            KeyboardKeyCode.Alpha4 => '$',
            KeyboardKeyCode.Alpha5 => '%',
            KeyboardKeyCode.Alpha6 => '^',
            KeyboardKeyCode.Alpha7 => '&',
            KeyboardKeyCode.Alpha8 => '*',
            KeyboardKeyCode.Alpha9 => '(',
            KeyboardKeyCode.Alpha0 => ')',
            _ => '\0',
        };

    private static bool IsChatOpenOrOpening()
    {
        if (!HudManager.InstanceExists) return false;

        var chat = HudManager.Instance.Chat;
        return chat != null && chat.IsOpenOrOpening;
    }

    private struct ChatHoldGate
    {
        public bool RawHeld;
        public bool Active;
        public float StartTime;
        public TextBoxTMP? PendingTextBox;
        public char PendingChar;

        public void Reset()
        {
            RawHeld = false;
            Active = false;
            StartTime = 0f;
            PendingTextBox = null;
            PendingChar = '\0';
        }
    }
}
