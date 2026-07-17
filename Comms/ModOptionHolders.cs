namespace VoiceChatPlugin.VoiceChat;

// OptionHolder subclasses backed by VoiceModRegistry's value store (not a ConfigEntry), so a
// third-party mod's host options render in the panel and sync over the host RPC without the mod
// owning a BepInEx config. Composed key = "modId.optionKey".
public sealed class ModToggleHolder : OptionHolder
{
    private readonly string _composedKey;

    public ModToggleHolder(string composedKey, string label)
        : this(composedKey, label, "")
    {
    }

    public ModToggleHolder(string composedKey, string label, string helpText)
    {
        _composedKey = composedKey;
        Label = label;
        HelpText = string.IsNullOrWhiteSpace(helpText)
            ? "Enables or disables this host option provided by the connected mod. The host's choice is synchronized to the lobby."
            : helpText;
    }

    public bool Value
    {
        get => VoiceModRegistry.GetBoolValue(_composedKey);
        set => VoiceModRegistry.SetBoolValue(_composedKey, value);
    }
}

public sealed class ModEnumHolder : OptionHolder
{
    private readonly string _composedKey;

    public string[] Labels { get; }

    public ModEnumHolder(string composedKey, string label, string[] labels)
        : this(composedKey, label, labels, "")
    {
    }

    public ModEnumHolder(string composedKey, string label, string[] labels, string helpText)
    {
        _composedKey = composedKey;
        Label = label;
        Labels = labels;
        HelpText = string.IsNullOrWhiteSpace(helpText)
            ? "Chooses the value for this host option provided by the connected mod. The host's choice is synchronized to the lobby."
            : helpText;
    }

    public int Value
    {
        get => VoiceModRegistry.GetEnumValue(_composedKey);
        set => VoiceModRegistry.SetEnumValue(_composedKey, value);
    }
}

public sealed class ModNumberHolder : OptionHolder
{
    private readonly string _composedKey;

    public float Min { get; }
    public float Max { get; }
    public float Step { get; }
    public string Format { get; }

    public ModNumberHolder(
        string composedKey,
        string label,
        float min,
        float max,
        float step,
        string format,
        string helpText)
    {
        _composedKey = composedKey;
        Label = label;
        Min = min;
        Max = max;
        Step = step;
        Format = string.IsNullOrWhiteSpace(format) ? "0.0" : format;
        HelpText = string.IsNullOrWhiteSpace(helpText)
            ? "Adjusts this host option provided by the connected mod. The host's choice is synchronized to the lobby."
            : helpText;
    }

    public float Value
    {
        get => VoiceModRegistry.GetNumberValue(_composedKey);
        set => VoiceModRegistry.SetNumberValue(_composedKey, value);
    }
}
