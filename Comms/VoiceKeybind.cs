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
    private readonly List<Action> _callbacks = new();
    private bool _standaloneModifierPending;
    private bool _standaloneModifierChorded;
    private float _standaloneModifierStartedAt;
    private int _standaloneModifierEvaluatedFrame = -1;
    private bool _standaloneModifierHeldThisFrame;
    private bool _standaloneModifierPressedThisFrame;

    public string DisplayName { get; }
    public string HelpText { get; }
    public KeyCode Value => _entry.Value;
    public KeyCode CurrentKey => _entry.Value;
    public KeyCode Modifier => _modifier.Value;
    public VoiceModifierMatch ModifierMatch => _modifierMatch.Value;

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
        string helpText)
    {
        DisplayName = displayName;
        HelpText = helpText;
        _entry = config.Bind(section, displayName, defaultKey);
        _modifier = config.Bind(section, displayName + " Modifier", defaultModifier);
        // Existing config files have no match-mode entry. Preserve the old behavior for their
        // loaded modifier: Shift/Ctrl/Alt matched either side, while every other key was exact.
        var matchDefault = hasExplicitModifierMatch && _modifier.Value == defaultModifier
            ? defaultModifierMatch
            : LegacyModifierMatch(_modifier.Value);
        _modifierMatch = config.Bind(section, displayName + " Modifier Match", matchDefault);
    }

    public void Set(KeyCode key) => _entry.Value = key;
    public void SetModifier(KeyCode mod) => SetModifier(mod, LegacyModifierMatch(mod));

    public void SetModifier(KeyCode mod, VoiceModifierMatch match)
    {
        _modifier.Value = mod;
        _modifierMatch.Value = mod == KeyCode.None ? VoiceModifierMatch.Exact : match;
    }

    public void SetBinding(KeyCode key, KeyCode modifier, VoiceModifierMatch match)
    {
        _entry.Value = key;
        _modifier.Value = modifier;
        _modifierMatch.Value = modifier == KeyCode.None ? VoiceModifierMatch.Exact : match;
    }

    public void Clear()
    {
        _entry.Value = KeyCode.None;
        _modifier.Value = KeyCode.None;
        _modifierMatch.Value = VoiceModifierMatch.Exact;
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

    private void EvaluateConflictingStandaloneModifier()
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
            && VoiceChatKeybinds.HasActiveChordUsingModifier(Value, this))
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

    public bool IsHeld()
    {
        if (Value == KeyCode.None) return false;
        if (_modifier.Value == KeyCode.None && IsModifierKey(Value)
            && VoiceChatKeybinds.HasConfiguredChordUsingModifier(Value, this))
        {
            EvaluateConflictingStandaloneModifier();
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
        if (Value == KeyCode.None) return false;
        if (_modifier.Value == KeyCode.None && IsModifierKey(Value)
            && VoiceChatKeybinds.HasConfiguredChordUsingModifier(Value, this))
        {
            EvaluateConflictingStandaloneModifier();
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
