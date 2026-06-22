using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public sealed class VoiceKeybind
{
    private readonly ConfigEntry<KeyCode> _entry;
    private readonly ConfigEntry<KeyCode> _modifier;
    private readonly List<Action> _callbacks = new();

    public string DisplayName { get; }
    public KeyCode Value => _entry.Value;
    public KeyCode CurrentKey => _entry.Value;
    public KeyCode Modifier => _modifier.Value;

    public VoiceKeybind(ConfigFile config, string section, string displayName, KeyCode defaultKey, KeyCode defaultModifier = KeyCode.None)
    {
        DisplayName = displayName;
        _entry = config.Bind(section, displayName, defaultKey);
        _modifier = config.Bind(section, displayName + " Modifier", defaultModifier);
    }

    public void Set(KeyCode key) => _entry.Value = key;
    public void SetModifier(KeyCode mod) => _modifier.Value = mod;
    public void Clear() { _entry.Value = KeyCode.None; _modifier.Value = KeyCode.None; }

    private bool ModifierHeld()
    {
        var m = _modifier.Value;
        if (m == KeyCode.None) return true;
        if (m == KeyCode.LeftShift || m == KeyCode.RightShift) return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (m == KeyCode.LeftControl || m == KeyCode.RightControl) return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (m == KeyCode.LeftAlt || m == KeyCode.RightAlt) return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        return Input.GetKey(m);
    }

    public string Label
    {
        get
        {
            if (Value == KeyCode.None) return "None";
            var m = _modifier.Value;
            if (m == KeyCode.None) return Value.ToString();
            var mod = (m == KeyCode.LeftShift || m == KeyCode.RightShift) ? "Shift"
                : (m == KeyCode.LeftControl || m == KeyCode.RightControl) ? "Ctrl"
                : (m == KeyCode.LeftAlt || m == KeyCode.RightAlt) ? "Alt"
                : m.ToString();
            return mod + "+" + Value;
        }
    }

    public bool IsHeld() => Value != KeyCode.None && ModifierHeld() && Input.GetKey(Value);

    public bool WasPressedThisFrame() => Value != KeyCode.None && ModifierHeld() && Input.GetKeyDown(Value);

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
