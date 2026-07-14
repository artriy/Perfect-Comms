using System;
using System.Collections.Generic;
using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// Internal registry behind PerfectCommsApi. Holds every third-party registration keyed by
// mod id and exposes fail-closed query helpers the engine wire-in points call. Every external
// callback is wrapped in try/catch: audio callbacks yield their neutral result (Pass / null /
// option default), while identity-bearing overlay callbacks fail private. No third-party
// exception may break the voice frame.
internal static class VoiceModRegistry
{
    private sealed record GlobalGate(string ModId, VoicePhaseKind Phase, Func<bool> IsActive, string Reason);

    private static readonly Dictionary<string, List<Func<VoiceRuleContext, VoiceRuleResult>>> _rules = new();
    private static readonly Dictionary<string, List<Func<VoiceRuleContext, VoiceChannelResult?>>> _channels = new();
    private static readonly Dictionary<string, List<Func<PlayerControl, VoiceListenerResult?>>> _origins = new();
    private static readonly Dictionary<string, List<Func<PlayerControl, bool>>> _listenerFilters = new();
    private static readonly Dictionary<string, List<Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult>>> _overlayViewerRules = new();
    private static readonly Dictionary<string, List<Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult>>> _overlaySpeakerRules = new();
    private static readonly List<GlobalGate> _globalGates = new();

    // Tabs in registration order; options grouped per mod id.
    private static readonly List<(string ModId, string Label)> _tabs = new();
    private static readonly Dictionary<string, List<VoiceHostOption>> _boolOptions = new();
    private static readonly Dictionary<string, List<VoiceHostEnumOption>> _enumOptions = new();

    // Local host values and authenticated remote-host overrides, keyed "modId.optionKey". Keeping
    // them separate prevents one lobby's host policy from becoming this client's local policy after
    // disconnect/host migration, while still letting a promoted host retain its own configured values.
    private static readonly Dictionary<string, bool> _boolValues = new();
    private static readonly Dictionary<string, int> _enumValues = new();

    internal static bool HasAnyRegistrations =>
        _rules.Count > 0 || _channels.Count > 0 || _origins.Count > 0 || _globalGates.Count > 0
        || _overlayViewerRules.Count > 0 || _overlaySpeakerRules.Count > 0;

    // ---- Registration (called from PerfectCommsApi) ----

    internal static void AddRule(string modId, Func<VoiceRuleContext, VoiceRuleResult> rule)
    {
        if (string.IsNullOrEmpty(modId) || rule == null) return;
        Add(_rules, modId, rule);
    }

    internal static void AddGlobalGate(string modId, VoicePhaseKind phase, Func<bool> isActive, string reason)
    {
        if (string.IsNullOrEmpty(modId) || isActive == null) return;
        _globalGates.Add(new GlobalGate(modId, phase, isActive, reason ?? "Muted"));
    }

    internal static void AddChannel(string modId, Func<VoiceRuleContext, VoiceChannelResult?> channel)
    {
        if (string.IsNullOrEmpty(modId) || channel == null) return;
        Add(_channels, modId, channel);
    }

    internal static void AddListenerOrigin(string modId, Func<PlayerControl, VoiceListenerResult?> origin)
    {
        if (string.IsNullOrEmpty(modId) || origin == null) return;
        Add(_origins, modId, origin);
    }

    internal static void AddListenerFilter(string modId, Func<PlayerControl, bool> shouldMuffle)
    {
        if (string.IsNullOrEmpty(modId) || shouldMuffle == null) return;
        Add(_listenerFilters, modId, shouldMuffle);
    }

    internal static void AddOverlayViewerRule(
        string modId,
        Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult> rule)
    {
        if (string.IsNullOrEmpty(modId) || rule == null) return;
        Add(_overlayViewerRules, modId, rule);
    }

    internal static void AddOverlaySpeakerRule(
        string modId,
        Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult> rule)
    {
        if (string.IsNullOrEmpty(modId) || rule == null) return;
        Add(_overlaySpeakerRules, modId, rule);
    }

    internal static void AddHostOption(string modId, VoiceHostOption option)
    {
        if (string.IsNullOrEmpty(modId) || option == null) return;
        Add(_boolOptions, modId, option);
        var key = Compose(modId, option.Key);
        _boolValues.TryAdd(key, option.Default);
        VoiceModRemoteOptionState.RegisterBool(key, option.Default);
    }

    internal static void AddHostEnumOption(string modId, VoiceHostEnumOption option)
    {
        if (string.IsNullOrEmpty(modId) || option == null) return;
        Add(_enumOptions, modId, option);
        var key = Compose(modId, option.Key);
        _enumValues.TryAdd(key, option.Default);
        VoiceModRemoteOptionState.RegisterEnum(key, option.Default);
    }

    internal static void AddTab(string modId, string label)
    {
        if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(label)) return;
        for (int i = 0; i < _tabs.Count; i++)
            if (_tabs[i].ModId == modId) return; // dedupe
        _tabs.Add((modId, label));
    }

    internal static void RemoveAll(string modId)
    {
        if (string.IsNullOrEmpty(modId)) return;
        _rules.Remove(modId);
        _channels.Remove(modId);
        _origins.Remove(modId);
        _listenerFilters.Remove(modId);
        _overlayViewerRules.Remove(modId);
        _overlaySpeakerRules.Remove(modId);
        _boolOptions.Remove(modId);
        _enumOptions.Remove(modId);
        _globalGates.RemoveAll(g => g.ModId == modId);
        _tabs.RemoveAll(t => t.ModId == modId);
        var prefix = modId + ".";
        var deadBools = new List<string>();
        foreach (var k in _boolValues.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) deadBools.Add(k);
        foreach (var k in deadBools)
            _boolValues.Remove(k);
        var deadEnums = new List<string>();
        foreach (var k in _enumValues.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) deadEnums.Add(k);
        foreach (var k in deadEnums)
            _enumValues.Remove(k);
        VoiceModRemoteOptionState.RemovePrefix(prefix);
    }

    // ---- Engine queries (fail-closed) ----

    /// <summary>
    /// Restrictively composes all viewer-wide overlay rules. Missing viewer state and callback
    /// failures hide every identity-bearing indicator.
    /// </summary>
    internal static VoiceOverlayViewerResult ResolveOverlayViewerPrivacy(
        PlayerControl? viewer,
        VoicePhaseKind phase,
        bool isDead)
    {
        if (IsMissingPlayer(viewer)) return VoiceOverlayViewerResult.HideAll;
        // This is the normal built-in-only path. Do not allocate a callback context every rendered
        // frame when no third-party mod registered an overlay rule.
        if (_overlayViewerRules.Count == 0) return VoiceOverlayViewerResult.Pass;
        return ResolveOverlayViewerPrivacy(new VoiceOverlayViewerContext(viewer!, phase, isDead));
    }

    internal static VoiceOverlayViewerResult ResolveOverlayViewerPrivacy(
        VoiceOverlayViewerContext context)
    {
        if (context == null || IsMissingPlayer(context.Viewer))
            return VoiceOverlayViewerResult.HideAll;
        if (_overlayViewerRules.Count == 0)
            return VoiceOverlayViewerResult.Pass;

        var effective = VoiceOverlayViewerResult.Pass;
        foreach (var pair in _overlayViewerRules)
        {
            var scopedContext = MakeOverlayViewerContext(pair.Key, context);
            var list = pair.Value;
            for (int i = 0; i < list.Count; i++)
            {
                VoiceOverlayViewerResult result;
                try { result = list[i](scopedContext); }
                catch { result = VoiceOverlayViewerResult.HideAll; }

                switch (result.Verdict)
                {
                    case VoiceOverlayViewerVerdict.Pass:
                        break;
                    case VoiceOverlayViewerVerdict.DimAll:
                        effective = VoiceOverlayViewerResult.DimAll;
                        break;
                    case VoiceOverlayViewerVerdict.HideAll:
                    default:
                        return VoiceOverlayViewerResult.HideAll;
                }
            }
        }
        return effective;
    }

    /// <summary>
    /// Restrictively composes all source-specific overlay rules. Missing viewer state hides all;
    /// missing speaker state, callback failures, invalid aliases, and alias conflicts hide source.
    /// </summary>
    internal static VoiceOverlaySpeakerResult ResolveOverlaySpeakerPrivacy(
        PlayerControl? viewer,
        PlayerControl? speaker,
        VoicePhaseKind phase,
        bool viewerIsDead,
        bool speakerIsDead)
    {
        if (IsMissingPlayer(viewer)) return VoiceOverlaySpeakerResult.HideAll;
        if (IsMissingPlayer(speaker)) return VoiceOverlaySpeakerResult.HideSource;
        // Avoid one context allocation per active speaker per frame in the common no-extension case.
        if (_overlaySpeakerRules.Count == 0) return VoiceOverlaySpeakerResult.Pass;
        return ResolveOverlaySpeakerPrivacy(new VoiceOverlaySpeakerContext(
            viewer!,
            speaker!,
            phase,
            viewerIsDead,
            speakerIsDead));
    }

    internal static VoiceOverlaySpeakerResult ResolveOverlaySpeakerPrivacy(
        VoiceOverlaySpeakerContext context)
    {
        if (context == null || IsMissingPlayer(context.Viewer))
            return VoiceOverlaySpeakerResult.HideAll;
        if (IsMissingPlayer(context.Speaker))
            return VoiceOverlaySpeakerResult.HideSource;
        if (_overlaySpeakerRules.Count == 0)
            return VoiceOverlaySpeakerResult.Pass;

        var effectiveVerdict = VoiceOverlaySpeakerVerdict.Pass;
        byte? aliasPlayerId = null;

        foreach (var pair in _overlaySpeakerRules)
        {
            var scopedContext = MakeOverlaySpeakerContext(pair.Key, context);
            var list = pair.Value;
            for (int i = 0; i < list.Count; i++)
            {
                VoiceOverlaySpeakerResult result;
                try { result = list[i](scopedContext); }
                catch { result = VoiceOverlaySpeakerResult.HideSource; }

                switch (result.Verdict)
                {
                    case VoiceOverlaySpeakerVerdict.Pass:
                        break;
                    case VoiceOverlaySpeakerVerdict.HideAll:
                        return VoiceOverlaySpeakerResult.HideAll;
                    case VoiceOverlaySpeakerVerdict.HideSource:
                        effectiveVerdict = VoiceOverlaySpeakerVerdict.HideSource;
                        aliasPlayerId = null;
                        break;
                    case VoiceOverlaySpeakerVerdict.Alias:
                        if (effectiveVerdict == VoiceOverlaySpeakerVerdict.HideSource)
                            break;
                        if (result.AliasPlayerId is not { } candidate
                            || candidate == byte.MaxValue
                            || (aliasPlayerId.HasValue && aliasPlayerId.Value != candidate))
                        {
                            effectiveVerdict = VoiceOverlaySpeakerVerdict.HideSource;
                            aliasPlayerId = null;
                        }
                        else
                        {
                            effectiveVerdict = VoiceOverlaySpeakerVerdict.Alias;
                            aliasPlayerId = candidate;
                        }
                        break;
                    default:
                        effectiveVerdict = VoiceOverlaySpeakerVerdict.HideSource;
                        aliasPlayerId = null;
                        break;
                }
            }
        }

        return effectiveVerdict switch
        {
            VoiceOverlaySpeakerVerdict.Alias when aliasPlayerId.HasValue
                => VoiceOverlaySpeakerResult.Alias(aliasPlayerId.Value),
            VoiceOverlaySpeakerVerdict.HideSource => VoiceOverlaySpeakerResult.HideSource,
            _ => VoiceOverlaySpeakerResult.Pass,
        };
    }

    // Phase-scoped global gate (e.g. system-wide jam). Returns true + reason when active.
    // Also used for the LOCAL transmit gate ("am I muted right now?").
    internal static bool LocalGate(PlayerControl? local, VoicePhaseKind phase, bool isDead, out string reason)
    {
        var state = ResolvePlayer(local, phase, isLocal: true, isDead);
        if (state.Muted) { reason = state.HasReason ? state.Reason : "Muted"; return true; }
        return TryGetGlobalGate(phase, out reason);
    }

    internal static bool TryGetGlobalGate(VoicePhaseKind phase, out string reason)
    {
        reason = string.Empty;
        for (int i = 0; i < _globalGates.Count; i++)
        {
            var g = _globalGates[i];
            if (g.Phase != phase) continue;
            bool active;
            try { active = g.IsActive(); }
            catch { active = false; }
            if (active)
            {
                reason = g.Reason;
                return true;
            }
        }
        return false;
    }

    // SINGLE per-player resolve, called once per player by the snapshot builder. Bundles the
    // gate verdict + channel membership + (local-only) listener origin into one ExternalVoiceState.
    // Every mod callback runs here, wrapped fail-closed; the rest of the engine reads plain values.
    internal static ExternalVoiceState ResolvePlayer(PlayerControl? player, VoicePhaseKind phase, bool isLocal, bool isDead)
    {
        if (player == null || !HasAnyRegistrations)
            return ExternalVoiceState.None;

        bool muted = false, muffled = false;
        string reason = string.Empty;
        string channelKey = string.Empty;
        bool channelTwoWay = true;
        int channelShape = (int)VoiceAudioShape.Radio;
        float channelVolume = 1f;
        bool channelHasOrigin = false;
        Vector2 channelOrigin = default;
        bool listenerActive = false;
        Vector2 listenerOrigin = default;
        float listenerLight = -1f;
        bool listenerReplace = true;

        // Gate: first non-Pass across all mods wins.
        foreach (var pair in _rules)
        {
            var ctx = MakeContext(pair.Key, player, phase, isLocal, isDead);
            var list = pair.Value;
            for (int i = 0; i < list.Count && !muted; i++)
            {
                VoiceRuleResult result;
                try { result = list[i](ctx) ?? VoiceRuleResult.Pass; }
                catch { result = VoiceRuleResult.Pass; }
                if (result.Verdict == VoiceVerdict.Pass) continue;
                if (result.Verdict == VoiceVerdict.Mute) { muted = true; reason = result.Reason; }
                else if (!muffled) { muffled = true; reason = result.Reason; }
            }
            if (muted) break;
        }

        // Channel: first non-null membership wins (key already namespaced by the mod).
        foreach (var pair in _channels)
        {
            VoiceChannelResult? ch = Eval(pair.Value, pair.Key, player, phase, isLocal, isDead);
            if (ch == null || string.IsNullOrEmpty(ch.Key)) continue;
            channelKey = pair.Key + "\u0000" + ch.Key; // namespace so two mods can't collide
            channelTwoWay = ch.TwoWay;
            channelShape = (int)ch.Shape;
            channelVolume = Mathf.Clamp01(ch.Volume);
            if (ch.Origin.HasValue)
            {
                channelHasOrigin = true;
                channelOrigin = ch.Origin.Value;
            }
            break;
        }

        // Listener origin: local player only, first non-null wins.
        if (isLocal && _origins.Count > 0)
        {
            foreach (var pair in _origins)
            {
                var list = pair.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    VoiceListenerResult? result;
                    try { result = list[i](player); }
                    catch { result = null; }
                    if (result == null) continue;
                    listenerActive = true;
                    listenerOrigin = result.Origin;
                    listenerLight = result.LightRadius;
                    listenerReplace = result.Mode == VoiceListenerMode.Replace;
                    break;
                }
                if (listenerActive) break;
            }
        }

        return new ExternalVoiceState(
            muted, muffled, reason,
            channelKey, channelTwoWay, channelShape, channelVolume,
            channelHasOrigin, channelOrigin,
            listenerActive, listenerOrigin, listenerLight, listenerReplace);
    }

    // Local-player listener filter (Gap 1): any registered filter returning true muffles all
    // incoming audio for the local player this frame. Fail-closed (a throw = no muffle).
    internal static bool LocalListenerMuffled(PlayerControl? local)
    {
        if (local == null || _listenerFilters.Count == 0) return false;
        foreach (var pair in _listenerFilters)
        {
            var list = pair.Value;
            for (int i = 0; i < list.Count; i++)
            {
                bool muffle;
                try { muffle = list[i](local); }
                catch { muffle = false; }
                if (muffle) return true;
            }
        }
        return false;
    }

    private static VoiceChannelResult? Eval(List<Func<VoiceRuleContext, VoiceChannelResult?>> list,
        string modId, PlayerControl player, VoicePhaseKind phase, bool isLocal, bool isDead)
    {
        if (list.Count == 0) return null;
        var ctx = MakeContext(modId, player, phase, isLocal, isDead);
        for (int i = 0; i < list.Count; i++)
        {
            VoiceChannelResult? r;
            try { r = list[i](ctx); }
            catch { r = null; }
            if (r != null) return r;
        }
        return null;
    }

    // ---- Host-panel tab + option access (Stage C) ----

    internal static IReadOnlyList<(string ModId, string Label)> Tabs => _tabs;

    internal static IReadOnlyList<VoiceHostOption> BoolOptionsFor(string modId)
        => _boolOptions.TryGetValue(modId, out var l) ? l : Array.Empty<VoiceHostOption>();

    internal static IReadOnlyList<VoiceHostEnumOption> EnumOptionsFor(string modId)
        => _enumOptions.TryGetValue(modId, out var l) ? l : Array.Empty<VoiceHostEnumOption>();

    // Registry-backed OptionHolders for one mod tab, in registration order (bools then enums),
    // for the host settings panel to render.
    internal static List<OptionHolder> HoldersForTab(int tabIndex)
    {
        var holders = new List<OptionHolder>();
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return holders;
        string modId = _tabs[tabIndex].ModId;
        foreach (var opt in BoolOptionsFor(modId))
            holders.Add(new ModToggleHolder(Compose(modId, opt.Key), opt.Label));
        foreach (var opt in EnumOptionsFor(modId))
            holders.Add(new ModEnumHolder(Compose(modId, opt.Key), opt.Label, opt.Choices));
        return holders;
    }

    // Bumped whenever any host-option value changes, so the host snapshot dedupe re-broadcasts
    // on a mod toggle even though VoiceRoomSettingsSnapshot itself is unchanged.
    internal static int OptionRevision { get; private set; }

    internal static bool GetBoolValue(string composedKey)
        => VoiceModRemoteOptionState.IsActive
            ? VoiceModRemoteOptionState.GetBool(composedKey)
            : _boolValues.TryGetValue(composedKey, out var v) && v;

    internal static void SetBoolValue(string composedKey, bool value)
    {
        if (!_boolValues.TryGetValue(composedKey, out var cur)) return;
        if (cur == value) return;
        _boolValues[composedKey] = value;
        OptionRevision++;
    }

    internal static int GetEnumValue(string composedKey)
        => VoiceModRemoteOptionState.IsActive
            ? VoiceModRemoteOptionState.GetEnum(composedKey)
            : _enumValues.TryGetValue(composedKey, out var v) ? v : 0;

    internal static void SetEnumValue(string composedKey, int value)
    {
        if (!_enumValues.TryGetValue(composedKey, out var cur)) return;
        if (cur == value) return;
        _enumValues[composedKey] = value;
        OptionRevision++;
    }

    // Stable hash of a composed key for the wire (offset-independent sync).
    internal static int KeyHash(string composedKey)
    {
        unchecked
        {
            int hash = 23;
            foreach (char c in composedKey) hash = hash * 31 + c;
            return hash;
        }
    }

    // All synced option values as (hash, isEnum, value) for the host RPC trailing block.
    internal static IEnumerable<(int Hash, bool IsEnum, int Value)> SyncedValues()
    {
        foreach (var pair in _boolValues)
            yield return (KeyHash(pair.Key), false, pair.Value ? 1 : 0);
        foreach (var pair in _enumValues)
            yield return (KeyHash(pair.Key), true, pair.Value);
    }

    // Apply a synced value received from the host (hash-matched, fail-closed on unknown).
    internal static void BeginRemoteSync()
        => VoiceModRemoteOptionState.BeginSync();

    internal static void ClearRemoteSyncedValues()
        => VoiceModRemoteOptionState.Clear();

    internal static void ApplySyncedValue(int hash, bool isEnum, int value)
    {
        if (!VoiceModRemoteOptionState.IsActive) BeginRemoteSync();
        if (isEnum)
        {
            foreach (var key in _enumValues.Keys)
                if (KeyHash(key) == hash) { VoiceModRemoteOptionState.SetEnum(key, value); return; }
        }
        else
        {
            foreach (var key in _boolValues.Keys)
                if (KeyHash(key) == hash) { VoiceModRemoteOptionState.SetBool(key, value != 0); return; }
        }
    }

    internal static string Compose(string modId, string key) => modId + "." + key;

    // ---- internals ----

    private static bool IsMissingPlayer(PlayerControl? player)
        => player is null || player == null;

    private static VoiceChannelResult? Eval(Func<VoiceRuleContext, VoiceChannelResult?> func,
        string modId, PlayerControl player, VoicePhaseKind phase, bool isLocal, bool isDead)
    {
        try { return func(MakeContext(modId, player, phase, isLocal, isDead)); }
        catch { return null; }
    }

    // ctx.GetOption(key) takes the BARE option key; it is auto-qualified with the asking
    // mod's id, so a mod reads its own options without repeating its id.
    private static VoiceRuleContext MakeContext(string modId, PlayerControl player, VoicePhaseKind phase, bool isLocal, bool isDead)
        => new(player, phase, isLocal, isDead)
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
        };

    private static VoiceOverlayViewerContext MakeOverlayViewerContext(
        string modId,
        VoiceOverlayViewerContext context)
        => context with
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
        };

    private static VoiceOverlaySpeakerContext MakeOverlaySpeakerContext(
        string modId,
        VoiceOverlaySpeakerContext context)
        => context with
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
        };

    private static void Add<T>(Dictionary<string, List<T>> map, string modId, T item)
    {
        if (!map.TryGetValue(modId, out var list))
        {
            list = new List<T>();
            map[modId] = list;
        }
        list.Add(item);
    }
}
