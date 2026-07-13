using System;
using System.Collections.Generic;

namespace VoiceChatPlugin.VoiceChat;

/// <summary>
/// Authenticated remote-host option overlay, isolated from PlayerControl-dependent registry code so
/// session cleanup is safe during teardown and test hosts. Local values remain owned by the registry;
/// this overlay is seeded from registered defaults for each complete host snapshot.
/// </summary>
internal static class VoiceModRemoteOptionState
{
    private static readonly Dictionary<string, bool> BoolDefaults = new();
    private static readonly Dictionary<string, int> EnumDefaults = new();
    private static readonly Dictionary<string, bool> RemoteBools = new();
    private static readonly Dictionary<string, int> RemoteEnums = new();

    internal static bool IsActive { get; private set; }

    internal static void RegisterBool(string key, bool defaultValue)
    {
        BoolDefaults.TryAdd(key, defaultValue);
        if (IsActive) RemoteBools.TryAdd(key, defaultValue);
    }

    internal static void RegisterEnum(string key, int defaultValue)
    {
        EnumDefaults.TryAdd(key, defaultValue);
        if (IsActive) RemoteEnums.TryAdd(key, defaultValue);
    }

    internal static void RemovePrefix(string prefix)
    {
        RemoveMatching(BoolDefaults, prefix);
        RemoveMatching(EnumDefaults, prefix);
        RemoveMatching(RemoteBools, prefix);
        RemoveMatching(RemoteEnums, prefix);
    }

    internal static void BeginSync()
    {
        RemoteBools.Clear();
        RemoteEnums.Clear();
        foreach (var pair in BoolDefaults) RemoteBools[pair.Key] = pair.Value;
        foreach (var pair in EnumDefaults) RemoteEnums[pair.Key] = pair.Value;
        IsActive = true;
    }

    internal static void Clear()
    {
        RemoteBools.Clear();
        RemoteEnums.Clear();
        IsActive = false;
    }

    internal static bool GetBool(string key)
        => RemoteBools.TryGetValue(key, out var value) && value;

    internal static int GetEnum(string key)
        => RemoteEnums.TryGetValue(key, out var value) ? value : 0;

    internal static void SetBool(string key, bool value) => RemoteBools[key] = value;
    internal static void SetEnum(string key, int value) => RemoteEnums[key] = value;

    private static void RemoveMatching<T>(Dictionary<string, T> values, string prefix)
    {
        var remove = new List<string>();
        foreach (var key in values.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) remove.Add(key);
        foreach (var key in remove) values.Remove(key);
    }
}
