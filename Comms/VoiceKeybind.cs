using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public enum VoiceModifierMatch
{
    EitherSide = 0,
    Exact = 1,
}

public sealed class VoiceKeybind
{
    private const float StandaloneModifierChordGraceSeconds = 0.15f;

    private readonly ConfigEntry<KeyCode> _entry;
    private readonly ConfigEntry<KeyCode> _modifier;
    private readonly ConfigEntry<VoiceModifierMatch> _modifierMatch;
    private readonly ConfigEntry<bool> _allowWhileChatOpen;
    private readonly List<Action> _callbacks = new();
    private bool _standaloneModifierPending;
    private bool _standaloneModifierChorded;
    private float _standaloneModifierStartedAt;
    private int _standaloneModifierEvaluatedFrame = -1;
    private bool _standaloneModifierHeldThisFrame;
    private bool _standaloneModifierPressedThisFrame;
    private KeyCode _suppressedPrimary = KeyCode.None;
    private KeyCode _suppressedModifierLeft = KeyCode.None;
    private KeyCode _suppressedModifierRight = KeyCode.None;

    public string DisplayName { get; }
    public string HelpText { get; }
    public KeyCode Value => _entry.Value;
    public KeyCode CurrentKey => _entry.Value;
    public KeyCode Modifier => _modifier.Value;
    public VoiceModifierMatch ModifierMatch => _modifierMatch.Value;
    public bool AllowWhileChatOpen => _allowWhileChatOpen.Value;

    public VoiceKeybind(
        ConfigFile config,
        string section,
        string displayName,
        KeyCode defaultKey,
        KeyCode defaultModifier = KeyCode.None)
        : this(config, section, displayName, defaultKey, defaultModifier,
            LegacyModifierMatch(defaultModifier), hasExplicitModifierMatch: false, helpText: "")
    {
    }

    /// <summary>
    /// Creates a binding whose user-facing label differs from its persisted config key. This is
    /// used when copy is clarified without discarding an existing user's binding.
    /// </summary>
    public VoiceKeybind(
        ConfigFile config,
        string section,
        string displayName,
        string configKey,
        KeyCode defaultKey,
        string helpText)
        : this(config, section, displayName, defaultKey, KeyCode.None,
            LegacyModifierMatch(KeyCode.None), hasExplicitModifierMatch: false, helpText: helpText,
            configKey: configKey)
    {
    }

    public VoiceKeybind(
        ConfigFile config,
        string section,
        string displayName,
        KeyCode defaultKey,
        string helpText)
        : this(config, section, displayName, defaultKey, KeyCode.None,
            LegacyModifierMatch(KeyCode.None), hasExplicitModifierMatch: false, helpText: helpText)
    {
    }

    public VoiceKeybind(
        ConfigFile config,
        string section,
        string displayName,
        KeyCode defaultKey,
        KeyCode defaultModifier,
        string helpText)
        : this(config, section, displayName, defaultKey, defaultModifier,
            LegacyModifierMatch(defaultModifier), hasExplicitModifierMatch: false, helpText: helpText)
    {
    }

    public VoiceKeybind(
        ConfigFile config,
        string section,
        string displayName,
        KeyCode defaultKey,
        KeyCode defaultModifier,
        VoiceModifierMatch defaultModifierMatch)
        : this(config, section, displayName, defaultKey, defaultModifier,
            defaultModifierMatch, hasExplicitModifierMatch: true, helpText: "")
    {
    }

    public VoiceKeybind(
        ConfigFile config,
        string section,
        string displayName,
        KeyCode defaultKey,
        KeyCode defaultModifier,
        VoiceModifierMatch defaultModifierMatch,
        string helpText)
        : this(config, section, displayName, defaultKey, defaultModifier,
            defaultModifierMatch, hasExplicitModifierMatch: true, helpText: helpText)
    {
    }

    private VoiceKeybind(
        ConfigFile config,
        string section,
        string displayName,
        KeyCode defaultKey,
        KeyCode defaultModifier,
        VoiceModifierMatch defaultModifierMatch,
        bool hasExplicitModifierMatch,
        string helpText,
        string? configKey = null)
    {
        DisplayName = displayName;
        HelpText = helpText;
        var persistedKey = string.IsNullOrWhiteSpace(configKey) ? displayName : configKey;
        _entry = config.Bind(section, persistedKey, defaultKey);
        _modifier = config.Bind(section, persistedKey + " Modifier", defaultModifier);
        // Existing config files have no match-mode entry. Preserve the old behavior for their
        // loaded modifier: Shift/Ctrl/Alt matched either side, while every other key was exact.
        var matchDefault = hasExplicitModifierMatch && _modifier.Value == defaultModifier
            ? defaultModifierMatch
            : LegacyModifierMatch(_modifier.Value);
        _modifierMatch = config.Bind(section, persistedKey + " Modifier Match", matchDefault);
        _allowWhileChatOpen = config.Bind(
            section,
            persistedKey + " Allow While Chat Open",
            IsRecommendedChatAllowedPrimary(_entry.Value));
    }

    public void Set(KeyCode key)
    {
        _entry.Value = key;
        _allowWhileChatOpen.Value = IsRecommendedChatAllowedPrimary(key);
    }
    public void SetModifier(KeyCode mod) => SetModifier(mod, LegacyModifierMatch(mod));

    public void SetModifier(KeyCode mod, VoiceModifierMatch match)
    {
        _modifier.Value = mod;
        _modifierMatch.Value = mod == KeyCode.None ? VoiceModifierMatch.Exact : match;
    }

    public void SetAllowWhileChatOpen(bool allow)
        => _allowWhileChatOpen.Value = allow;

    public void SetBinding(KeyCode key, KeyCode modifier, VoiceModifierMatch match)
    {
        _entry.Value = key;
        _modifier.Value = modifier;
        _modifierMatch.Value = modifier == KeyCode.None ? VoiceModifierMatch.Exact : match;
        _allowWhileChatOpen.Value = IsRecommendedChatAllowedPrimary(key);
    }

    public void Clear()
    {
        _entry.Value = KeyCode.None;
        _modifier.Value = KeyCode.None;
        _modifierMatch.Value = VoiceModifierMatch.Exact;
        _allowWhileChatOpen.Value = false;
    }

    private bool ModifierHeld()
    {
        var m = _modifier.Value;
        if (m == KeyCode.None) return true;
        if (_modifierMatch.Value == VoiceModifierMatch.EitherSide && TryGetModifierPair(m, out var left, out var right))
            return Input.GetKey(left) || Input.GetKey(right);
        return Input.GetKey(m);
    }

    public string Label
    {
        get
        {
            if (Value == KeyCode.None) return "None";
            var m = _modifier.Value;
            if (m == KeyCode.None) return FormatKey(Value);
            return FormatModifier(m, _modifierMatch.Value) + "+" + FormatKey(Value);
        }
    }

    internal static bool IsModifierKey(KeyCode key)
        => key is KeyCode.LeftShift or KeyCode.RightShift
            or KeyCode.LeftControl or KeyCode.RightControl
            or KeyCode.LeftAlt or KeyCode.RightAlt or KeyCode.AltGr
            or KeyCode.LeftCommand or KeyCode.RightCommand
            or KeyCode.LeftWindows or KeyCode.RightWindows;

    internal static bool TryGetModifierPair(KeyCode key, out KeyCode left, out KeyCode right)
    {
        switch (key)
        {
            case KeyCode.LeftShift:
            case KeyCode.RightShift:
                left = KeyCode.LeftShift;
                right = KeyCode.RightShift;
                return true;
            case KeyCode.LeftControl:
            case KeyCode.RightControl:
                left = KeyCode.LeftControl;
                right = KeyCode.RightControl;
                return true;
            case KeyCode.LeftAlt:
            case KeyCode.RightAlt:
                left = KeyCode.LeftAlt;
                right = KeyCode.RightAlt;
                return true;
            case KeyCode.LeftCommand:
            case KeyCode.RightCommand:
                left = KeyCode.LeftCommand;
                right = KeyCode.RightCommand;
                return true;
            case KeyCode.LeftWindows:
            case KeyCode.RightWindows:
                left = KeyCode.LeftWindows;
                right = KeyCode.RightWindows;
                return true;
            default:
                left = KeyCode.None;
                right = KeyCode.None;
                return false;
        }
    }

    internal static VoiceModifierMatch LegacyModifierMatch(KeyCode key)
        => key is KeyCode.LeftShift or KeyCode.RightShift
            or KeyCode.LeftControl or KeyCode.RightControl
            or KeyCode.LeftAlt or KeyCode.RightAlt
                ? VoiceModifierMatch.EitherSide
                : VoiceModifierMatch.Exact;

    private static bool IsRecommendedChatAllowedPrimary(KeyCode key)
        => key is KeyCode.RightAlt or KeyCode.RightControl
            or KeyCode.Mouse3 or KeyCode.Mouse4 or KeyCode.Mouse5 or KeyCode.Mouse6;

    internal static bool ModifierMatchesKey(
        KeyCode configuredModifier,
        VoiceModifierMatch match,
        KeyCode actualKey)
    {
        if (configuredModifier == KeyCode.None || actualKey == KeyCode.None)
            return false;
        if (match == VoiceModifierMatch.EitherSide
            && TryGetModifierPair(configuredModifier, out var left, out var right))
            return actualKey == left || actualKey == right;
        return actualKey == configuredModifier;
    }

    internal static string FormatModifier(KeyCode key, VoiceModifierMatch match)
    {
        if (match == VoiceModifierMatch.EitherSide && TryGetModifierPair(key, out _, out _))
        {
            return key switch
            {
                KeyCode.LeftShift or KeyCode.RightShift => "Shift",
                KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
                KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
                KeyCode.LeftCommand or KeyCode.RightCommand => "Command",
                KeyCode.LeftWindows or KeyCode.RightWindows => "Windows",
                _ => FormatKey(key),
            };
        }

        return FormatKey(key);
    }

    internal static string FormatKey(KeyCode key)
        => key switch
        {
            KeyCode.None => "None",
            KeyCode.LeftShift => "Left Shift",
            KeyCode.RightShift => "Right Shift",
            KeyCode.LeftControl => "Left Ctrl",
            KeyCode.RightControl => "Right Ctrl",
            KeyCode.LeftAlt => "Left Alt",
            KeyCode.RightAlt => "Right Alt",
            KeyCode.AltGr => "AltGr",
            KeyCode.LeftCommand => "Left Command",
            KeyCode.RightCommand => "Right Command",
            KeyCode.LeftWindows => "Left Windows",
            KeyCode.RightWindows => "Right Windows",
            KeyCode.Mouse0 => "MB1",
            KeyCode.Mouse1 => "MB2",
            KeyCode.Mouse2 => "MB3",
            KeyCode.Mouse3 => "MB4",
            KeyCode.Mouse4 => "MB5",
            KeyCode.Mouse5 => "MB6",
            KeyCode.Mouse6 => "MB7",
            _ => key.ToString(),
        };

    internal bool MatchesModifierKey(KeyCode actualKey)
        => ModifierMatchesKey(_modifier.Value, _modifierMatch.Value, actualKey);

    internal bool IsModifierSatisfied() => ModifierHeld();

    internal bool IsPrimaryHeldRaw()
        => Value != KeyCode.None && Input.GetKey(Value);

    private void EvaluateStandaloneModifier()
    {
        if (_standaloneModifierEvaluatedFrame == Time.frameCount) return;
        _standaloneModifierEvaluatedFrame = Time.frameCount;
        _standaloneModifierHeldThisFrame = false;
        _standaloneModifierPressedThisFrame = false;

        bool down = Input.GetKeyDown(Value);
        bool held = Input.GetKey(Value);
        bool up = Input.GetKeyUp(Value);
        if (down || (!_standaloneModifierPending && held))
        {
            _standaloneModifierPending = true;
            _standaloneModifierChorded = false;
            _standaloneModifierStartedAt = Time.unscaledTime;
        }

        if (_standaloneModifierPending
            && (WasOtherKeyboardOrMousePressed()
                || VoiceChatKeybinds.HasActiveChordUsingModifier(Value, this)))
            _standaloneModifierChorded = true;

        _standaloneModifierHeldThisFrame = _standaloneModifierPending
            && held
            && !_standaloneModifierChorded
            && Time.unscaledTime - _standaloneModifierStartedAt
                >= StandaloneModifierChordGraceSeconds;

        if (up)
        {
            _standaloneModifierPressedThisFrame = _standaloneModifierPending
                && !_standaloneModifierChorded;
            _standaloneModifierPending = false;
            _standaloneModifierChorded = false;
        }
        else if (!held && _standaloneModifierPending)
        {
            // Focus loss can consume a key-up event. Reset instead of leaving the binding latched.
            _standaloneModifierPending = false;
            _standaloneModifierChorded = false;
        }
    }

    private bool WasOtherKeyboardOrMousePressed()
    {
        foreach (var key in KeyboardAndMouseCandidates)
        {
            if (key != Value && Input.GetKeyDown(key)) return true;
        }

        return false;
    }

    public void SuppressUntilReleased()
    {
        ResetStandaloneModifierState();
        CaptureHeldKey(Value, ref _suppressedPrimary);

        var modifier = _modifier.Value;
        if (_modifierMatch.Value == VoiceModifierMatch.EitherSide
            && TryGetModifierPair(modifier, out var left, out var right))
        {
            CaptureHeldKey(left, ref _suppressedModifierLeft);
            CaptureHeldKey(right, ref _suppressedModifierRight);
        }
        else
        {
            CaptureHeldKey(modifier, ref _suppressedModifierLeft);
        }
    }

    private static void CaptureHeldKey(KeyCode key, ref KeyCode suppressedKey)
    {
        if (key != KeyCode.None && Input.GetKey(key))
            suppressedKey = key;
    }

    private bool IsSuppressedUntilReleased()
    {
        if (IsHeldRaw(_suppressedPrimary)
            || IsHeldRaw(_suppressedModifierLeft)
            || IsHeldRaw(_suppressedModifierRight))
            return true;

        _suppressedPrimary = KeyCode.None;
        _suppressedModifierLeft = KeyCode.None;
        _suppressedModifierRight = KeyCode.None;
        return false;
    }

    private static bool IsHeldRaw(KeyCode key)
        => key != KeyCode.None && Input.GetKey(key);

    private void ResetStandaloneModifierState()
    {
        _standaloneModifierPending = false;
        _standaloneModifierChorded = false;
        _standaloneModifierHeldThisFrame = false;
        _standaloneModifierPressedThisFrame = false;
        _standaloneModifierEvaluatedFrame = -1;
    }

    private static readonly KeyCode[] KeyboardAndMouseCandidates = BuildKeyboardAndMouseCandidates();

    private static KeyCode[] BuildKeyboardAndMouseCandidates()
    {
        var candidates = new List<KeyCode>();
        foreach (var value in Enum.GetValues(typeof(KeyCode)))
        {
            var key = (KeyCode)value;
            var keyValue = (int)key;
            if (keyValue > (int)KeyCode.None && keyValue <= (int)KeyCode.Mouse6)
                candidates.Add(key);
        }

        return candidates.ToArray();
    }

    public bool IsHeld()
    {
        if (Value == KeyCode.None || IsSuppressedUntilReleased()) return false;
        if (_modifier.Value == KeyCode.None && IsModifierKey(Value))
        {
            EvaluateStandaloneModifier();
            return _standaloneModifierHeldThisFrame;
        }

        if (!ModifierHeld()) return false;
        if (_modifier.Value == KeyCode.None
            && VoiceChatKeybinds.HasActiveChordForPrimary(Value, this))
            return false;
        return Input.GetKey(Value);
    }

    public bool WasPressedThisFrame()
    {
        if (Value == KeyCode.None || IsSuppressedUntilReleased()) return false;
        if (_modifier.Value == KeyCode.None && IsModifierKey(Value))
        {
            EvaluateStandaloneModifier();
            return _standaloneModifierPressedThisFrame;
        }

        if (!ModifierHeld()) return false;
        if (_modifier.Value == KeyCode.None
            && VoiceChatKeybinds.HasActiveChordForPrimary(Value, this))
            return false;
        return Input.GetKeyDown(Value);
    }

    public void OnActivate(Action callback)
    {
        if (callback != null) _callbacks.Add(callback);
    }

    public void FireIfPressed()
    {
        if (!WasPressedThisFrame()) return;
        foreach (var cb in _callbacks)
        {
            try { cb(); }
            catch (Exception ex) { VoiceDiagnostics.Log("keybind.error", $"bind={DisplayName} error=\"{ex.Message}\""); }
        }
    }
}
