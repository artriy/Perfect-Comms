using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

public sealed class VoiceKeybind
{
    private readonly ConfigEntry<KeyCode> _entry;
    private readonly List<Action> _callbacks = new();

    public string DisplayName { get; }
    public KeyCode Value => _entry.Value;
    public KeyCode CurrentKey => _entry.Value;

    public VoiceKeybind(ConfigFile config, string section, string displayName, KeyCode defaultKey)
    {
        DisplayName = displayName;
        _entry = config.Bind(section, displayName, defaultKey);
    }

    public void Set(KeyCode key) => _entry.Value = key;
    public void Clear() => _entry.Value = KeyCode.None;

    public bool IsHeld() => Value != KeyCode.None && Input.GetKey(Value);

    public bool WasPressedThisFrame() => Value != KeyCode.None && Input.GetKeyDown(Value);

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
