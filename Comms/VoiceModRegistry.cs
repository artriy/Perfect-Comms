using System;
using System.Collections.Generic;
using System.Globalization;
using PerfectComms.Api;
using UnityEngine;

namespace VoiceChatPlugin.VoiceChat;

// Internal registry behind PerfectCommsApi. Holds every third-party registration keyed by
// mod id and exposes guarded query helpers the engine wire-in points call. Every external
// callback is wrapped in try/catch: audio callbacks yield their neutral result (Pass / null /
// option default), while identity-bearing overlay callbacks fail private. No third-party
// exception may break the voice frame.
internal static class VoiceModRegistry
{
    private const int MaxSyncedHostOptions = 256;

    private readonly record struct CallbackGroup<T>(string ModId, T[] Callbacks);

    // Registrations are rare; reads run many times per second. Rebuild an immutable callback
    // snapshot on mutation so callbacks may register/unregister (including themselves) without
    // invalidating a live dictionary/list enumeration or breaking the current voice frame.
    private sealed class CallbackMap<T>
    {
        private readonly Dictionary<string, List<T>> _byMod = new();

        internal int Count => _byMod.Count;
        internal CallbackGroup<T>[] Snapshot { get; private set; } = Array.Empty<CallbackGroup<T>>();

        internal void Add(string modId, T callback)
        {
            if (!_byMod.TryGetValue(modId, out var list))
            {
                list = new List<T>();
                _byMod[modId] = list;
            }
            list.Add(callback);
            RebuildSnapshot();
        }

        internal bool Remove(string modId)
        {
            if (!_byMod.Remove(modId)) return false;
            RebuildSnapshot();
            return true;
        }

        private void RebuildSnapshot()
        {
            var snapshot = new CallbackGroup<T>[_byMod.Count];
            int index = 0;
            foreach (var pair in _byMod)
                snapshot[index++] = new CallbackGroup<T>(pair.Key, pair.Value.ToArray());
            Snapshot = snapshot;
        }
    }

    private sealed record GlobalGate(
        string ModId,
        VoicePhaseKind Phase,
        Func<VoiceGlobalGateContext, bool> IsActive,
        string Reason);

    private static readonly CallbackMap<Func<VoiceRuleContext, VoiceRuleResult>> _rules = new();
    private static readonly CallbackMap<Func<VoiceRuleContext, VoicePlayerTraits>> _playerTraits = new();
    private static readonly CallbackMap<Func<VoicePairContext, VoicePairResult>> _pairRules = new();
    private static readonly CallbackMap<Func<VoiceRuleContext, VoiceChannelResult?>> _channels = new();
    private static readonly CallbackMap<Func<VoiceListenerContext, VoiceListenerResult?>> _origins = new();
    private static readonly CallbackMap<Func<VoiceListenerContext, VoiceListenerFilterResult>> _listenerFilters = new();
    private static readonly CallbackMap<Action<VoicePhaseChangedContext>> _phaseObservers = new();
    private static readonly CallbackMap<Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult>> _overlayViewerRules = new();
    private static readonly CallbackMap<Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult>> _overlaySpeakerRules = new();
    private static readonly List<GlobalGate> _globalGates = new();
    private static GlobalGate[] _globalGateSnapshot = Array.Empty<GlobalGate>();

    // Tabs in registration order; options grouped per mod id.
    private static readonly List<(string ModId, string Label)> _tabs = new();
    private static readonly Dictionary<string, List<VoiceHostOption>> _boolOptions = new();
    private static readonly Dictionary<string, List<VoiceHostEnumOption>> _enumOptions = new();
    private static readonly Dictionary<string, List<VoiceHostNumberOption>> _numberOptions = new();

    // Local host values and remote host-snapshot overrides, keyed "modId.optionKey". Keeping
    // them separate prevents one lobby's host policy from becoming this client's local policy after
    // disconnect/host migration, while still letting a promoted host retain its own configured values.
    private static readonly Dictionary<string, bool> _boolValues = new();
    private static readonly Dictionary<string, int> _enumValues = new();
    private static readonly Dictionary<string, float> _numberValues = new();
    private static VoicePhaseKind? _lastObservedPhase;
    [ThreadStatic] private static bool _resolvingPlayerTraits;

    internal static bool HasAnyRegistrations =>
        _rules.Count > 0 || _playerTraits.Count > 0 || _pairRules.Count > 0 || _channels.Count > 0
        || _origins.Count > 0 || _listenerFilters.Count > 0 || _globalGates.Count > 0
        || _overlayViewerRules.Count > 0 || _overlaySpeakerRules.Count > 0;

    internal static bool HasOverlayViewerRules => _overlayViewerRules.Count > 0;
    internal static bool HasOverlaySpeakerRules => _overlaySpeakerRules.Count > 0;

    // ---- Registration (called from PerfectCommsApi) ----

    internal static void AddRule(string modId, Func<VoiceRuleContext, VoiceRuleResult> rule)
    {
        if (string.IsNullOrEmpty(modId) || rule == null) return;
        _rules.Add(modId, rule);
    }

    internal static void AddGlobalGate(string modId, VoicePhaseKind phase, Func<bool> isActive, string reason)
    {
        if (string.IsNullOrEmpty(modId) || isActive == null) return;
        _globalGates.Add(new GlobalGate(modId, phase, _ => isActive(), reason ?? "Muted"));
        _globalGateSnapshot = _globalGates.ToArray();
    }

    internal static void AddContextualGlobalGate(
        string modId,
        VoicePhaseKind phase,
        Func<VoiceGlobalGateContext, bool> isActive,
        string reason)
    {
        if (string.IsNullOrEmpty(modId) || isActive == null) return;
        _globalGates.Add(new GlobalGate(modId, phase, isActive, reason ?? "Muted"));
        _globalGateSnapshot = _globalGates.ToArray();
    }

    internal static void AddPlayerTraits(string modId, Func<VoiceRuleContext, VoicePlayerTraits> traits)
    {
        if (string.IsNullOrEmpty(modId) || traits == null) return;
        _playerTraits.Add(modId, traits);
    }

    internal static void AddPairRule(string modId, Func<VoicePairContext, VoicePairResult> rule)
    {
        if (string.IsNullOrEmpty(modId) || rule == null) return;
        _pairRules.Add(modId, rule);
    }

    internal static void AddChannel(string modId, Func<VoiceRuleContext, VoiceChannelResult?> channel)
    {
        if (string.IsNullOrEmpty(modId) || channel == null) return;
        _channels.Add(modId, channel);
    }

    internal static void AddListenerOrigin(string modId, Func<PlayerControl, VoiceListenerResult?> origin)
    {
        if (string.IsNullOrEmpty(modId) || origin == null) return;
        _origins.Add(modId, context => origin(context.Listener));
    }

    internal static void AddContextualListenerOrigin(
        string modId,
        Func<VoiceListenerContext, VoiceListenerResult?> origin)
    {
        if (string.IsNullOrEmpty(modId) || origin == null) return;
        _origins.Add(modId, origin);
    }

    internal static void AddListenerFilter(string modId, Func<PlayerControl, bool> shouldMuffle)
    {
        if (string.IsNullOrEmpty(modId) || shouldMuffle == null) return;
        _listenerFilters.Add(modId,
            context => new VoiceListenerFilterResult(shouldMuffle(context.Listener)));
    }

    internal static void AddContextualListenerFilter(
        string modId,
        Func<VoiceListenerContext, VoiceListenerFilterResult> filter)
    {
        if (string.IsNullOrEmpty(modId) || filter == null) return;
        _listenerFilters.Add(modId, filter);
    }

    internal static void AddPhaseObserver(string modId, Action<VoicePhaseChangedContext> observer)
    {
        if (string.IsNullOrEmpty(modId) || observer == null) return;
        _phaseObservers.Add(modId, observer);
    }

    internal static void AddOverlayViewerRule(
        string modId,
        Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult> rule)
    {
        if (string.IsNullOrEmpty(modId) || rule == null) return;
        _overlayViewerRules.Add(modId, rule);
    }

    internal static void AddOverlaySpeakerRule(
        string modId,
        Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult> rule)
    {
        if (string.IsNullOrEmpty(modId) || rule == null) return;
        _overlaySpeakerRules.Add(modId, rule);
    }

    internal static void AddHostOption(string modId, VoiceHostOption option)
    {
        if (string.IsNullOrEmpty(modId) || option == null) return;
        if (!IsOptionKeyAvailable(modId, option.Key)) return;
        Add(_boolOptions, modId, option);
        var key = Compose(modId, option.Key);
        _boolValues.TryAdd(key, option.Default);
        VoiceModRemoteOptionState.RegisterBool(key, option.Default);
        OptionRevision++;
    }

    internal static void AddHostEnumOption(string modId, VoiceHostEnumOption option)
    {
        if (string.IsNullOrEmpty(modId) || option == null) return;
        if (!IsOptionKeyAvailable(modId, option.Key) || option.Choices == null || option.Choices.Length == 0)
            return;
        var choices = (string[])option.Choices.Clone();
        for (int i = 0; i < choices.Length; i++)
            choices[i] ??= string.Empty;
        option = option with
        {
            Default = Math.Clamp(option.Default, 0, choices.Length - 1),
            Choices = choices,
        };
        Add(_enumOptions, modId, option);
        var key = Compose(modId, option.Key);
        _enumValues.TryAdd(key, option.Default);
        VoiceModRemoteOptionState.RegisterEnum(key, option.Default);
        OptionRevision++;
    }

    internal static void AddHostNumberOption(string modId, VoiceHostNumberOption option)
    {
        if (string.IsNullOrEmpty(modId) || option == null) return;
        if (!IsOptionKeyAvailable(modId, option.Key) || !IsValidNumberDefinition(option)) return;
        option = option with { Format = SafeNumberFormat(option.Format, option.Default) };
        Add(_numberOptions, modId, option);
        var key = Compose(modId, option.Key);
        float value = NormalizeNumber(option, option.Default);
        _numberValues.TryAdd(key, value);
        VoiceModRemoteOptionState.RegisterNumber(key, value);
        OptionRevision++;
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
        var optionKeys = new List<string>();
        if (_boolOptions.TryGetValue(modId, out var boolOptions))
            for (int i = 0; i < boolOptions.Count; i++)
                optionKeys.Add(Compose(modId, boolOptions[i].Key));
        if (_enumOptions.TryGetValue(modId, out var enumOptions))
            for (int i = 0; i < enumOptions.Count; i++)
                optionKeys.Add(Compose(modId, enumOptions[i].Key));
        if (_numberOptions.TryGetValue(modId, out var numberOptions))
            for (int i = 0; i < numberOptions.Count; i++)
                optionKeys.Add(Compose(modId, numberOptions[i].Key));

        _rules.Remove(modId);
        _playerTraits.Remove(modId);
        _pairRules.Remove(modId);
        _channels.Remove(modId);
        _origins.Remove(modId);
        _listenerFilters.Remove(modId);
        _phaseObservers.Remove(modId);
        _overlayViewerRules.Remove(modId);
        _overlaySpeakerRules.Remove(modId);
        bool optionInventoryChanged = _boolOptions.Remove(modId);
        optionInventoryChanged |= _enumOptions.Remove(modId);
        optionInventoryChanged |= _numberOptions.Remove(modId);
        if (_globalGates.RemoveAll(g => g.ModId == modId) > 0)
            _globalGateSnapshot = _globalGates.ToArray();
        _tabs.RemoveAll(t => t.ModId == modId);
        for (int i = 0; i < optionKeys.Count; i++)
        {
            string key = optionKeys[i];
            _boolValues.Remove(key);
            _enumValues.Remove(key);
            _numberValues.Remove(key);
        }
        VoiceModRemoteOptionState.RemoveKeys(optionKeys);
        if (optionInventoryChanged) OptionRevision++;
    }

    internal static void NotifyPhase(VoicePhaseKind phase, PlayerControl? localPlayer)
    {
        if (!_lastObservedPhase.HasValue)
        {
            _lastObservedPhase = phase;
            return;
        }

        VoicePhaseKind previous = _lastObservedPhase.Value;
        if (previous == phase) return;
        _lastObservedPhase = phase;

        CallbackGroup<Action<VoicePhaseChangedContext>>[] observerGroups = _phaseObservers.Snapshot;
        for (int groupIndex = 0; groupIndex < observerGroups.Length; groupIndex++)
        {
            CallbackGroup<Action<VoicePhaseChangedContext>> group = observerGroups[groupIndex];
            var context = MakePhaseChangedContext(group.ModId, previous, phase, localPlayer);
            Action<VoicePhaseChangedContext>[] list = group.Callbacks;
            for (int i = 0; i < list.Length; i++)
            {
                try { list[i](context); }
                catch { }
            }
        }
    }

    // ---- Engine queries (neutral audio fallback; private overlay fallback) ----

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
        CallbackGroup<Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult>>[] viewerGroups =
            _overlayViewerRules.Snapshot;
        for (int groupIndex = 0; groupIndex < viewerGroups.Length; groupIndex++)
        {
            CallbackGroup<Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult>> group =
                viewerGroups[groupIndex];
            var scopedContext = MakeOverlayViewerContext(group.ModId, context);
            Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult>[] list = group.Callbacks;
            for (int i = 0; i < list.Length; i++)
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

        CallbackGroup<Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult>>[] speakerGroups =
            _overlaySpeakerRules.Snapshot;
        for (int groupIndex = 0; groupIndex < speakerGroups.Length; groupIndex++)
        {
            CallbackGroup<Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult>> group =
                speakerGroups[groupIndex];
            var scopedContext = MakeOverlaySpeakerContext(group.ModId, context);
            Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult>[] list = group.Callbacks;
            for (int i = 0; i < list.Length; i++)
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
        reason = string.Empty;
        return false;
    }

    internal static bool TryGetGlobalGate(VoicePhaseKind phase, out string reason)
    {
        PlayerControl? localPlayer = SafeLocalPlayer();
        bool localIsDead = SafeIsDead(localPlayer);
        localIsDead |= (ResolvePlayerTraits(
            localPlayer,
            phase,
            isLocal: true,
            isDead: localIsDead) & VoicePlayerTraits.VoiceDead) != 0;
        return TryGetGlobalGate(phase, localPlayer, localIsDead, out reason);
    }

    private static bool TryGetGlobalGate(
        VoicePhaseKind phase,
        PlayerControl? localPlayer,
        bool localIsDead,
        out string reason)
    {
        reason = string.Empty;
        GlobalGate[] gates = _globalGateSnapshot;
        for (int i = 0; i < gates.Length; i++)
        {
            var g = gates[i];
            if (g.Phase != phase) continue;
            bool active;
            try { active = g.IsActive(MakeGlobalGateContext(g.ModId, localPlayer, phase, localIsDead)); }
            catch { active = false; }
            if (active)
            {
                reason = g.Reason;
                return true;
            }
        }
        return false;
    }

    internal static VoicePlayerTraits ResolvePlayerTraits(
        PlayerControl? player,
        VoicePhaseKind phase,
        bool isLocal,
        bool isDead,
        bool? localIsDead = null)
    {
        if (player == null || _playerTraits.Count == 0)
            return VoicePlayerTraits.None;
        if (_resolvingPlayerTraits)
            return VoicePlayerTraits.None;

        VoicePlayerTraits effective = VoicePlayerTraits.None;
        const VoicePlayerTraits allowed =
            VoicePlayerTraits.ImpostorVoice |
            VoicePlayerTraits.VoiceDead |
            VoicePlayerTraits.Spectator;

        _resolvingPlayerTraits = true;
        try
        {
            CallbackGroup<Func<VoiceRuleContext, VoicePlayerTraits>>[] traitGroups = _playerTraits.Snapshot;
            for (int groupIndex = 0; groupIndex < traitGroups.Length; groupIndex++)
            {
                CallbackGroup<Func<VoiceRuleContext, VoicePlayerTraits>> group = traitGroups[groupIndex];
                var context = MakeContext(group.ModId, player, phase, isLocal, isDead, localIsDead);
                Func<VoiceRuleContext, VoicePlayerTraits>[] list = group.Callbacks;
                for (int i = 0; i < list.Length; i++)
                {
                    VoicePlayerTraits result;
                    try { result = list[i](context); }
                    catch { result = VoicePlayerTraits.None; }
                    effective |= result & allowed;
                }
            }
        }
        finally { _resolvingPlayerTraits = false; }

        if ((effective & VoicePlayerTraits.Spectator) != 0)
            effective |= VoicePlayerTraits.VoiceDead;
        return effective;
    }

    internal static ExternalVoicePairState ResolvePair(
        PlayerControl? listener,
        PlayerControl? speaker,
        VoicePhaseKind phase,
        bool listenerIsDead,
        bool speakerIsDead)
    {
        if (listener == null || speaker == null || _pairRules.Count == 0)
            return ExternalVoicePairState.None;

        bool muffled = false;
        VoicePairResult? route = null;

        CallbackGroup<Func<VoicePairContext, VoicePairResult>>[] pairGroups = _pairRules.Snapshot;
        for (int groupIndex = 0; groupIndex < pairGroups.Length; groupIndex++)
        {
            CallbackGroup<Func<VoicePairContext, VoicePairResult>> group = pairGroups[groupIndex];
            var context = MakePairContext(
                group.ModId,
                listener,
                speaker,
                phase,
                listenerIsDead,
                speakerIsDead);
            Func<VoicePairContext, VoicePairResult>[] list = group.Callbacks;
            for (int i = 0; i < list.Length; i++)
            {
                VoicePairResult result;
                try { result = list[i](context) ?? VoicePairResult.Pass; }
                catch { result = VoicePairResult.Pass; }

                switch (result.Verdict)
                {
                    case VoicePairVerdict.Pass:
                        break;
                    case VoicePairVerdict.Muffle:
                        muffled = true;
                        break;
                    case VoicePairVerdict.Mute:
                        return new ExternalVoicePairState(
                            VoicePairVerdict.Mute,
                            SafeReason(result.Reason, "Muted"),
                            muffled,
                            (int)VoicePairRouteShape.Proximity,
                            0f,
                            false,
                            default,
                            false,
                            default);
                    case VoicePairVerdict.Route:
                        if (route == null && IsValidPairRoute(result))
                            route = result;
                        break;
                    default:
                        break;
                }
            }
        }

        if (route == null)
            return ExternalVoicePairState.None with { Muffled = muffled };

        return new ExternalVoicePairState(
            VoicePairVerdict.Route,
            SafeReason(route.Reason, "Mod Route"),
            muffled,
            (int)route.Shape,
            ClampVolume(route.Volume),
            route.SpeakerOrigin.HasValue && IsFinite(route.SpeakerOrigin.Value),
            route.SpeakerOrigin.GetValueOrDefault(),
            route.ListenerOrigin.HasValue && IsFinite(route.ListenerOrigin.Value),
            route.ListenerOrigin.GetValueOrDefault());
    }

    // SINGLE per-player resolve, called once per player by the snapshot builder. Bundles the
    // gate verdict + channel membership + (local-only) listener origin into one ExternalVoiceState.
    // Every mod callback runs here with the fallback described above; the engine reads plain values.
    internal static ExternalVoiceState ResolvePlayer(
        PlayerControl? player,
        VoicePhaseKind phase,
        bool isLocal,
        bool isDead,
        bool? localIsDead = null)
    {
        if (player == null || !HasAnyRegistrations)
            return ExternalVoiceState.None;

        bool muted = false, muffled = false;
        string reason = string.Empty;
        List<ExternalVoiceChannelState>? channels = null;
        bool listenerActive = false;
        Vector2 listenerOrigin = default;
        float listenerLight = -1f;
        bool listenerReplace = true;

        // Restrictive composition: Mute wins; otherwise every Muffle is retained as a low-pass.
        // Every callback still runs even after a Mute so legacy integrations that refresh shared
        // phase/option state inside their rule remain correct regardless of mod registration order.
        CallbackGroup<Func<VoiceRuleContext, VoiceRuleResult>>[] ruleGroups = _rules.Snapshot;
        for (int groupIndex = 0; groupIndex < ruleGroups.Length; groupIndex++)
        {
            CallbackGroup<Func<VoiceRuleContext, VoiceRuleResult>> group = ruleGroups[groupIndex];
            var ctx = MakeContext(group.ModId, player, phase, isLocal, isDead, localIsDead);
            Func<VoiceRuleContext, VoiceRuleResult>[] list = group.Callbacks;
            for (int i = 0; i < list.Length; i++)
            {
                VoiceRuleResult result;
                try { result = list[i](ctx) ?? VoiceRuleResult.Pass; }
                catch { result = VoiceRuleResult.Pass; }
                if (result.Verdict == VoiceVerdict.Pass) continue;
                if (result.Verdict == VoiceVerdict.Mute && !muted)
                {
                    muted = true;
                    reason = SafeReason(result.Reason, "Muted");
                }
                else if (result.Verdict == VoiceVerdict.Muffle && !muted && !muffled)
                {
                    muffled = true;
                    reason = SafeReason(result.Reason, "Muffled");
                }
            }
        }

        // A phase-wide gate is also receiver-enforced. Legacy gates are wrapped into this same
        // contextual path, after the voice rule has had a chance to refresh compatibility caches.
        PlayerControl? localPlayer = isLocal ? player : SafeLocalPlayer();
        bool effectiveLocalIsDead = isLocal ? isDead : localIsDead ?? SafeIsDead(localPlayer);
        if (!muted && TryGetGlobalGate(phase, localPlayer, effectiveLocalIsDead, out string globalReason))
        {
            muted = true;
            reason = SafeReason(globalReason, "Muted");
        }

        // Retain every accepted membership. Keys are namespaced so separate mods cannot collide.
        CallbackGroup<Func<VoiceRuleContext, VoiceChannelResult?>>[] channelGroups = _channels.Snapshot;
        for (int groupIndex = 0; groupIndex < channelGroups.Length; groupIndex++)
        {
            CallbackGroup<Func<VoiceRuleContext, VoiceChannelResult?>> group = channelGroups[groupIndex];
            var context = MakeContext(group.ModId, player, phase, isLocal, isDead, localIsDead);
            Func<VoiceRuleContext, VoiceChannelResult?>[] list = group.Callbacks;
            for (int i = 0; i < list.Length; i++)
            {
                VoiceChannelResult? channel;
                try { channel = list[i](context); }
                catch { channel = null; }
                if (channel == null || string.IsNullOrEmpty(channel.Key)) continue;

                bool hasOrigin = channel.Origin.HasValue && IsFinite(channel.Origin.Value);
                int shape = Enum.IsDefined(typeof(VoiceAudioShape), channel.Shape)
                    ? (int)channel.Shape
                    : (int)VoiceAudioShape.Radio;
                channels ??= new List<ExternalVoiceChannelState>();
                channels.Add(new ExternalVoiceChannelState(
                    group.ModId + "\u0000" + channel.Key,
                    channel.TwoWay,
                    shape,
                    ClampVolume(channel.Volume),
                    hasOrigin,
                    hasOrigin ? channel.Origin!.Value : default));
            }
        }

        // Listener origin: local player only, first non-null wins.
        if (isLocal && _origins.Count > 0)
        {
            CallbackGroup<Func<VoiceListenerContext, VoiceListenerResult?>>[] originGroups = _origins.Snapshot;
            for (int groupIndex = 0; groupIndex < originGroups.Length; groupIndex++)
            {
                CallbackGroup<Func<VoiceListenerContext, VoiceListenerResult?>> group = originGroups[groupIndex];
                var context = MakeListenerContext(group.ModId, player, phase, isDead);
                Func<VoiceListenerContext, VoiceListenerResult?>[] list = group.Callbacks;
                for (int i = 0; i < list.Length; i++)
                {
                    VoiceListenerResult? result;
                    try { result = list[i](context); }
                    catch { result = null; }
                    if (result == null || !IsFinite(result.Origin)) continue;
                    listenerActive = true;
                    listenerOrigin = result.Origin;
                    listenerLight = float.IsFinite(result.LightRadius)
                        ? result.LightRadius < 0f ? -1f : result.LightRadius
                        : -1f;
                    listenerReplace = result.Mode == VoiceListenerMode.Replace;
                    break;
                }
                if (listenerActive) break;
            }
        }

        return new ExternalVoiceState(
            muted,
            muffled,
            reason,
            channels?.ToArray(),
            listenerActive,
            listenerOrigin,
            listenerLight,
            listenerReplace,
            ExternalVoicePairState.None);
    }

    // Any registered listener filter returning true muffles all incoming audio for the local player.
    internal static bool LocalListenerMuffled(
        PlayerControl? local,
        VoiceGamePhase routingPhase)
    {
        if (local == null || _listenerFilters.Count == 0) return false;
        VoicePhaseKind phase = VoiceModBridge.ToApiPhase(routingPhase);
        bool isDead = SafeIsDead(local);
        isDead |= (ResolvePlayerTraits(
            local,
            phase,
            isLocal: true,
            isDead: isDead) & VoicePlayerTraits.VoiceDead) != 0;
        return ResolveListenerMuffled(local, phase, isDead);
    }

    internal static bool ResolveListenerMuffled(
        PlayerControl local,
        VoicePhaseKind phase,
        bool isDead)
    {
        if (local == null || _listenerFilters.Count == 0) return false;
        CallbackGroup<Func<VoiceListenerContext, VoiceListenerFilterResult>>[] filterGroups =
            _listenerFilters.Snapshot;
        for (int groupIndex = 0; groupIndex < filterGroups.Length; groupIndex++)
        {
            CallbackGroup<Func<VoiceListenerContext, VoiceListenerFilterResult>> group =
                filterGroups[groupIndex];
            var context = MakeListenerContext(group.ModId, local, phase, isDead);
            Func<VoiceListenerContext, VoiceListenerFilterResult>[] list = group.Callbacks;
            for (int i = 0; i < list.Length; i++)
            {
                VoiceListenerFilterResult result;
                try { result = list[i](context) ?? new VoiceListenerFilterResult(false); }
                catch { result = new VoiceListenerFilterResult(false); }
                if (result.Muffle) return true;
            }
        }
        return false;
    }

    // ---- Host-panel tab + option access (Stage C) ----

    internal static IReadOnlyList<(string ModId, string Label)> Tabs => _tabs;

    internal static IReadOnlyList<VoiceHostOption> BoolOptionsFor(string modId)
        => _boolOptions.TryGetValue(modId, out var l) ? l : Array.Empty<VoiceHostOption>();

    internal static IReadOnlyList<VoiceHostEnumOption> EnumOptionsFor(string modId)
        => _enumOptions.TryGetValue(modId, out var l) ? l : Array.Empty<VoiceHostEnumOption>();

    internal static IReadOnlyList<VoiceHostNumberOption> NumberOptionsFor(string modId)
        => _numberOptions.TryGetValue(modId, out var l) ? l : Array.Empty<VoiceHostNumberOption>();

    // Registry-backed OptionHolders for one mod tab, in registration order (bools then enums),
    // for the host settings panel to render.
    internal static List<OptionHolder> HoldersForTab(int tabIndex)
    {
        var holders = new List<OptionHolder>();
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return holders;
        string modId = _tabs[tabIndex].ModId;
        foreach (var opt in BoolOptionsFor(modId))
            holders.Add(new ModToggleHolder(
                Compose(modId, opt.Key), opt.Label, opt.Description)
            {
                Visible = Visibility(modId, opt.Visible),
            });
        foreach (var opt in EnumOptionsFor(modId))
            holders.Add(new ModEnumHolder(
                Compose(modId, opt.Key), opt.Label, opt.Choices, opt.Description)
            {
                Visible = Visibility(modId, opt.Visible),
            });
        foreach (var opt in NumberOptionsFor(modId))
            holders.Add(new ModNumberHolder(
                Compose(modId, opt.Key),
                opt.Label,
                opt.Min,
                opt.Max,
                opt.Step,
                opt.Format,
                opt.Description)
            {
                Visible = Visibility(modId, opt.Visible),
            });
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
        VoiceHostEnumOption? definition = FindEnumOption(composedKey);
        if (definition == null || definition.Choices.Length == 0) return;
        value = Math.Clamp(value, 0, definition.Choices.Length - 1);
        if (cur == value) return;
        _enumValues[composedKey] = value;
        OptionRevision++;
    }

    internal static float GetNumberValue(string composedKey)
        => VoiceModRemoteOptionState.IsActive
            ? VoiceModRemoteOptionState.GetNumber(composedKey)
            : _numberValues.TryGetValue(composedKey, out var value) ? value : 0f;

    internal static void SetNumberValue(string composedKey, float value)
    {
        if (!_numberValues.TryGetValue(composedKey, out float current)) return;
        VoiceHostNumberOption? definition = FindNumberOption(composedKey);
        if (definition == null) return;
        value = NormalizeNumber(definition, value);
        if (Math.Abs(current - value) < 0.0001f) return;
        _numberValues[composedKey] = value;
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
        foreach (var pair in _numberValues)
            yield return (NumberKeyHash(pair.Key), true, BitConverter.SingleToInt32Bits(pair.Value));
    }

    // Apply a synced value received from the host (hash-matched; unknown values are ignored).
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
            {
                if (KeyHash(key) != hash) continue;
                VoiceHostEnumOption? definition = FindEnumOption(key);
                if (definition != null && definition.Choices.Length > 0)
                    VoiceModRemoteOptionState.SetEnum(
                        key,
                        Math.Clamp(value, 0, definition.Choices.Length - 1));
                return;
            }
            foreach (var key in _numberValues.Keys)
            {
                if (NumberKeyHash(key) != hash) continue;
                float number = BitConverter.Int32BitsToSingle(value);
                VoiceHostNumberOption? definition = FindNumberOption(key);
                if (definition != null)
                    VoiceModRemoteOptionState.SetNumber(key, NormalizeNumber(definition, number));
                return;
            }
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

    private static PlayerControl? SafeLocalPlayer()
    {
        try
        {
            var local = PlayerControl.LocalPlayer;
            return IsMissingPlayer(local) ? null : local;
        }
        catch { return null; }
    }

    private static bool SafeIsDead(PlayerControl? player)
    {
        if (IsMissingPlayer(player)) return false;
        try
        {
            var data = player!.Data;
            return data != null && (data.IsDead || data.Role?.IsDead == true);
        }
        catch { return false; }
    }

    // ctx.GetOption(key) takes the BARE option key; it is auto-qualified with the asking
    // mod's id, so a mod reads its own options without repeating its id.
    private static VoiceRuleContext MakeContext(
        string modId,
        PlayerControl player,
        VoicePhaseKind phase,
        bool isLocal,
        bool isDead,
        bool? localIsDead = null)
    {
        PlayerControl? local = isLocal ? player : SafeLocalPlayer();
        return new VoiceRuleContext(player, phase, isLocal, isDead)
        {
            LocalPlayer = local,
            LocalIsDead = isLocal ? isDead : localIsDead ?? SafeIsDead(local),
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
            GetNumberOption = key => GetNumberValue(Compose(modId, key)),
        };
    }

    private static VoiceGlobalGateContext MakeGlobalGateContext(
        string modId,
        PlayerControl? localPlayer,
        VoicePhaseKind phase,
        bool localIsDead)
        => new(localPlayer, phase, localIsDead)
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
            GetNumberOption = key => GetNumberValue(Compose(modId, key)),
        };

    private static VoiceListenerContext MakeListenerContext(
        string modId,
        PlayerControl listener,
        VoicePhaseKind phase,
        bool isDead)
        => new(listener, phase, isDead)
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
            GetNumberOption = key => GetNumberValue(Compose(modId, key)),
        };

    private static VoicePairContext MakePairContext(
        string modId,
        PlayerControl listener,
        PlayerControl speaker,
        VoicePhaseKind phase,
        bool listenerIsDead,
        bool speakerIsDead)
        => new(listener, speaker, phase, listenerIsDead, speakerIsDead)
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
            GetNumberOption = key => GetNumberValue(Compose(modId, key)),
        };

    private static VoicePhaseChangedContext MakePhaseChangedContext(
        string modId,
        VoicePhaseKind previous,
        VoicePhaseKind phase,
        PlayerControl? localPlayer)
        => new(previous, phase, localPlayer)
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
            GetNumberOption = key => GetNumberValue(Compose(modId, key)),
        };

    private static VoiceOverlayViewerContext MakeOverlayViewerContext(
        string modId,
        VoiceOverlayViewerContext context)
        => context with
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
            GetNumberOption = key => GetNumberValue(Compose(modId, key)),
        };

    private static VoiceOverlaySpeakerContext MakeOverlaySpeakerContext(
        string modId,
        VoiceOverlaySpeakerContext context)
        => context with
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
            GetNumberOption = key => GetNumberValue(Compose(modId, key)),
        };

    private static Func<bool>? Visibility(
        string modId,
        Func<VoiceHostOptionContext, bool>? callback)
    {
        if (callback == null) return null;
        var context = new VoiceHostOptionContext
        {
            GetOption = key => GetBoolValue(Compose(modId, key)),
            GetEnumOption = key => GetEnumValue(Compose(modId, key)),
            GetNumberOption = key => GetNumberValue(Compose(modId, key)),
        };
        return () =>
        {
            try { return callback(context); }
            catch { return true; }
        };
    }

    private static VoiceHostNumberOption? FindNumberOption(string composedKey)
    {
        foreach (var pair in _numberOptions)
        {
            var list = pair.Value;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(Compose(pair.Key, list[i].Key), composedKey, StringComparison.Ordinal))
                    return list[i];
        }
        return null;
    }

    private static VoiceHostEnumOption? FindEnumOption(string composedKey)
    {
        foreach (var pair in _enumOptions)
        {
            var list = pair.Value;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(Compose(pair.Key, list[i].Key), composedKey, StringComparison.Ordinal))
                    return list[i];
        }
        return null;
    }

    private static bool IsOptionKeyAvailable(string modId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (_boolValues.Count + _enumValues.Count + _numberValues.Count >= MaxSyncedHostOptions)
            return false;
        string composedKey = Compose(modId, key);
        return !_boolValues.ContainsKey(composedKey)
               && !_enumValues.ContainsKey(composedKey)
               && !_numberValues.ContainsKey(composedKey);
    }

    private static bool IsValidNumberDefinition(VoiceHostNumberOption option)
        => !string.IsNullOrWhiteSpace(option.Key)
           && float.IsFinite(option.Default)
           && float.IsFinite(option.Min)
           && float.IsFinite(option.Max)
           && float.IsFinite(option.Step)
           && option.Max >= option.Min
           && option.Step > 0f;

    private static float NormalizeNumber(VoiceHostNumberOption option, float value)
    {
        if (!float.IsFinite(value)) value = option.Default;
        value = Math.Clamp(value, option.Min, option.Max);
        float steps = MathF.Round((value - option.Min) / option.Step);
        return Math.Clamp(option.Min + steps * option.Step, option.Min, option.Max);
    }

    private static string SafeNumberFormat(string? format, float sample)
    {
        if (string.IsNullOrWhiteSpace(format)) return "0.0";
        try
        {
            _ = sample.ToString(format, CultureInfo.InvariantCulture);
            return format;
        }
        catch (FormatException)
        {
            return "0.0";
        }
    }

    private static int NumberKeyHash(string composedKey)
        => KeyHash(composedKey + "\u0000number");

    private static bool IsValidPairRoute(VoicePairResult result)
        => Enum.IsDefined(typeof(VoicePairRouteShape), result.Shape)
           && float.IsFinite(result.Volume)
           && (!result.SpeakerOrigin.HasValue || IsFinite(result.SpeakerOrigin.Value))
           && (!result.ListenerOrigin.HasValue || IsFinite(result.ListenerOrigin.Value));

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.x) && float.IsFinite(value.y);

    private static float ClampVolume(float value)
        => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static string SafeReason(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

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
