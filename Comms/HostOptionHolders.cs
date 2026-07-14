using System;
using BepInEx.Configuration;

namespace VoiceChatPlugin.VoiceChat;

public abstract class OptionHolder
{
    public string Label { get; protected init; } = "";
    public string HelpText { get; protected init; } = "";
    public Func<bool>? Visible { get; init; }
    public bool IsVisible => Visible == null || Visible();
}

public sealed class ToggleHolder : OptionHolder
{
    private readonly ConfigEntry<bool> _entry;

    public ToggleHolder(
        ConfigFile cfg,
        string section,
        string key,
        string label,
        bool def)
        : this(cfg, section, key, label, def, "")
    {
    }

    public ToggleHolder(
        ConfigFile cfg,
        string section,
        string key,
        string label,
        bool def,
        string helpText)
    {
        Label = label;
        HelpText = string.IsNullOrWhiteSpace(helpText)
            ? "Enables or disables this host voice rule for the lobby. The host's choice is synchronized to all players."
            : helpText;
        _entry = cfg.Bind(section, key, def,
            new ConfigDescription(HelpText));
    }

    public bool Value
    {
        get => _entry.Value;
        set => _entry.Value = value;
    }
}

public sealed class EnumHolder : OptionHolder
{
    private readonly ConfigEntry<int> _entry;

    public Type EnumType { get; }
    public string[] Labels { get; }

    public EnumHolder(
        ConfigFile cfg,
        string section,
        string key,
        string label,
        int def,
        Type enumType,
        string[] labels)
        : this(cfg, section, key, label, def, enumType, labels, "")
    {
    }

    public EnumHolder(
        ConfigFile cfg,
        string section,
        string key,
        string label,
        int def,
        Type enumType,
        string[] labels,
        string helpText)
    {
        Label = label;
        HelpText = string.IsNullOrWhiteSpace(helpText)
            ? "Chooses the value for this host voice rule. The host's choice is synchronized to all players."
            : helpText;
        EnumType = enumType;
        Labels = labels;
        _entry = cfg.Bind(section, key, def,
            new ConfigDescription(HelpText));
    }

    public int Value
    {
        get => _entry.Value;
        set => _entry.Value = value;
    }
}

public sealed class NumberHolder : OptionHolder
{
    private readonly ConfigEntry<float> _entry;

    public float Min { get; }
    public float Max { get; }
    public float Step { get; }
    public string Format { get; }

    public NumberHolder(
        ConfigFile cfg,
        string section,
        string key,
        string label,
        float def,
        float min,
        float max,
        float step,
        string format)
        : this(cfg, section, key, label, def, min, max, step, format, "")
    {
    }

    public NumberHolder(
        ConfigFile cfg,
        string section,
        string key,
        string label,
        float def,
        float min,
        float max,
        float step,
        string format,
        string helpText)
    {
        Label = label;
        HelpText = string.IsNullOrWhiteSpace(helpText)
            ? "Adjusts this host voice rule for the lobby. The host's value is synchronized to all players."
            : helpText;
        Min = min;
        Max = max;
        Step = step;
        Format = format;
        _entry = cfg.Bind(section, key, def,
            new ConfigDescription(
                HelpText,
                new AcceptableValueRange<float>(min, max)));
    }

    public float Value
    {
        get => _entry.Value;
        set => _entry.Value = Math.Clamp(value, Min, Max);
    }
}
