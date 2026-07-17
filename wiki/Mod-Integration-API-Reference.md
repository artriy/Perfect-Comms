# API Reference

Supported namespace: `PerfectComms.Api` in `PerfectComms.dll`. Current API version: **1.1**.

Back to **[Mod Integration](Mod-Integration)**

Only the types in `PerfectComms.Api` are the supported integration contract. Public implementation classes under `VoiceChatPlugin.VoiceChat` are not API.

---

## Compatibility contract

The completed API 1.1 preserves all original public enum values, positional record constructors, and registration signatures. Existing integrations can keep their source and binaries unchanged. New context properties are init-only additions; new behavior uses separately named methods instead of ambiguous delegate overloads.

The legacy signatures remain:

```csharp
RegisterVoiceRule(string, Func<VoiceRuleContext, VoiceRuleResult>);
RegisterGlobalGate(string, VoicePhaseKind, Func<bool>, string);
RegisterVoiceChannel(string, Func<VoiceRuleContext, VoiceChannelResult?>);
RegisterListenerOrigin(string, Func<PlayerControl, VoiceListenerResult?>);
RegisterListenerFilter(string, Func<PlayerControl, bool>);
RegisterHostOption(string, VoiceHostOption);
RegisterHostEnumOption(string, VoiceHostEnumOption);
RegisterModTab(string, string);
RegisterOverlayViewerRule(string, Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult>);
RegisterOverlaySpeakerRule(string, Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult>);
Unregister(string);
```

The completed runtime fixes behavior behind those calls: per-speaker muffle, Lobby/dead/global gate enforcement, multiple and receive-only channels, speaker-position Proximity fallback, and `LightRadius: -1` inheritance.

---

## Runtime version and capabilities

```csharp
public const string ApiVersion = "1.1";
public const string PluginId = "com.edgetel.perfectcomms";

public static string RuntimeApiVersion { get; }
public static VoiceApiCapability Capabilities { get; }
public static bool Supports(VoiceApiCapability capability);
```

`ApiVersion` is compiled into a consuming assembly and is not a runtime probe. `RuntimeApiVersion` is a property. `Supports` requires every requested flag and returns `false` for `None`.

```csharp
[Flags]
public enum VoiceApiCapability
{
    None = 0,
    PerSpeakerMuffle = 1 << 0,
    GlobalReceiveGate = 1 << 1,
    DirectionalChannels = 1 << 2,
    MultipleChannels = 1 << 3,
    ContextualListeners = 1 << 4,
    PairRouting = 1 << 5,
    PlayerTraits = 1 << 6,
    PhaseObservers = 1 << 7,
    ConditionalHostOptions = 1 << 8,
    NumericHostOptions = 1 << 9,
    OverlayPrivacy = 1 << 10,
}
```

An older assembly may also contain the literal `"1.1"` without these members. A bridge supporting such builds must reflect for the property before entering code that references a new API type, or require a current Perfect Comms release.

---

## Phases and common option access

```csharp
public enum VoicePhaseKind
{
    Lobby,
    Tasks,
    Meeting,
    Exile,
}
```

Callbacks receive the exact API phase. Meeting and Exile are distinct. Internal menu/lobby-like states map to `Lobby`; a phase observer reports API-level changes, not every internal scene transition. EndGame becomes a fresh global results-screen call: no per-player callback can be re-resolved after player objects disappear, so retained task/meeting mute, muffle, channel, and pair state is not reapplied.

Where present, these delegates take a bare key and scope it to the callback's registered `modId`:

```csharp
Func<string, bool> GetOption;
Func<string, int> GetEnumOption;
Func<string, float> GetNumberOption;
```

Missing values return `false`, `0`, and `0f`.

---

## Speaker rules and global gates

```csharp
public enum VoiceVerdict
{
    Pass,
    Mute,
    Muffle,
}

public sealed record VoiceRuleContext(
    PlayerControl Player,
    VoicePhaseKind Phase,
    bool IsLocal,
    bool IsDead)
{
    public PlayerControl? LocalPlayer { get; init; }
    public bool LocalIsDead { get; init; }
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}

public sealed record VoiceRuleResult(VoiceVerdict Verdict, string Reason)
{
    public static readonly VoiceRuleResult Pass;
    public static VoiceRuleResult Mute(string reason);
    public static VoiceRuleResult Muffle(string reason);
}
```

`Player` is the speaker being resolved. `LocalPlayer` is this client's listener when available. `IsDead` and `LocalIsDead` include contributed `VoiceDead`/`Spectator` traits for normal rule evaluation.

`Mute` silences the speaker in Lobby, Tasks, Meeting, and Exile, including voice-dead routes, and blocks the local transmit path when the local player is affected. `Muffle` keeps only that speaker audible through the low-pass filter. `Mute` wins over `Muffle`; otherwise muffle is sticky. Null or throwing rules become `Pass`.

```csharp
public sealed record VoiceGlobalGateContext(
    PlayerControl? LocalPlayer,
    VoicePhaseKind Phase,
    bool LocalIsDead)
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}

public static void RegisterGlobalGate(
    string modId,
    VoicePhaseKind phase,
    Func<bool> isActive,
    string reason);

public static void RegisterContextualGlobalGate(
    string modId,
    VoicePhaseKind phase,
    Func<VoiceGlobalGateContext, bool> isActive,
    string reason);
```

Both forms are phase-exact and receiver-enforced as well as transmit-enforced. The contextual form can read options and local listener state. A throwing predicate is inactive.

See **[Gate](Mod-Integration-Gate)**.

---

## Player traits

```csharp
[Flags]
public enum VoicePlayerTraits
{
    None = 0,
    ImpostorVoice = 1 << 0,
    VoiceDead = 1 << 1,
    Spectator = 1 << 2,
}

public static void RegisterVoicePlayerTraits(
    string modId,
    Func<VoiceRuleContext, VoicePlayerTraits> traits);
```

Traits compose by bitwise OR across registrations and cannot remove a base classification. `Spectator` implies `VoiceDead`. Unknown bits and throwing callbacks are ignored. `ImpostorVoice` contributes the same vent, ghost-hearing, Team Radio, and viewer classification used by built-in impostor voice. Trait callbacks receive the player's base dead state; subsequent rules receive the effective voice-dead state.

---

## Listener-speaker pair rules

```csharp
public enum VoicePairVerdict
{
    Pass,
    Mute,
    Muffle,
    Route,
}

public enum VoicePairRouteShape
{
    Proximity,
    Radio,
    Ghost,
}

public sealed record VoicePairContext(
    PlayerControl Listener,
    PlayerControl Speaker,
    VoicePhaseKind Phase,
    bool ListenerIsDead,
    bool SpeakerIsDead)
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}

public sealed record VoicePairResult(VoicePairVerdict Verdict, string Reason)
{
    public VoicePairRouteShape Shape { get; init; }
    public float Volume { get; init; }
    public Vector2? SpeakerOrigin { get; init; }
    public Vector2? ListenerOrigin { get; init; }

    public static readonly VoicePairResult Pass;
    public static VoicePairResult Mute(string reason);
    public static VoicePairResult Muffle(string reason);
    public static VoicePairResult Route(
        VoicePairRouteShape shape,
        float volume = 1f,
        Vector2? speakerOrigin = null,
        Vector2? listenerOrigin = null,
        string reason = "Mod Route");
}

public static void RegisterVoicePairRule(
    string modId,
    Func<VoicePairContext, VoicePairResult> rule);
```

The listener is always local. Pair `Mute` wins immediately. Pair `Muffle` is retained and applies to whichever route is otherwise selected. The first valid `Route` wins, but later rules are still inspected for restrictive mute/muffle results.

`Radio` is flat. `Proximity` and `Ghost` use normal host distance/falloff and pan. Omitted `SpeakerOrigin` and `ListenerOrigin` use the resolved speaker and listener positions. Volume is clamped to `0..1`. Invalid shape, non-finite volume/origins, null, and exceptions are neutral.

An explicit pair route replaces ordinary routing for that pair. Speaker/global mutes and Tasks `OnlyMeetingOrLobby` remain authoritative. In Tasks, the pair route runs before `OnlyGhostsCanTalk` and Comms-sabotage blocking to support Medium-style exceptions; in Meeting/Exile those host restrictions run first. Channels stay below those restrictions.

See **[Channels](Mod-Integration-Channels#listener-speaker-pair-rules)**.

---

## Channels

```csharp
public enum VoiceAudioShape
{
    Proximity,
    Radio,
    Muffle,
}

public sealed record VoiceChannelResult(
    string Key,
    bool TwoWay = true,
    VoiceAudioShape Shape = VoiceAudioShape.Radio,
    float Volume = 1f,
    Vector2? Origin = null);

public static void RegisterVoiceChannel(
    string modId,
    Func<VoiceRuleContext, VoiceChannelResult?> channel);
```

Every non-null result with a non-empty key is retained, so one player can hold several memberships from several callbacks. Keys are scoped internally to the registering `modId`; the encoding is not part of the public contract. When several shared memberships produce routes, the loudest valid route is used.

For a local listener to hear a target, both need the same namespaced key and the target membership must have `TwoWay: true`. A `false` membership is receive-only: it can hear a transmitting member but cannot be heard back through that membership. The target membership supplies shape, volume, and origin.

`Radio` and `Muffle` are flat. `Proximity` uses the target's `Origin` when finite and otherwise the target's resolved body position. It spatializes in Lobby, Tasks, Meeting, and Exile whenever a listener position is available. Volume is clamped; an invalid shape falls back to Radio, and a non-finite origin falls back to the speaker.

See **[Channels](Mod-Integration-Channels)**.

---

## Listener origin, filter, and phase observer

```csharp
public enum VoiceListenerMode
{
    Replace,
    Additive,
}

public sealed record VoiceListenerResult(
    Vector2 Origin,
    float LightRadius,
    VoiceListenerMode Mode);

public sealed record VoiceListenerContext(
    PlayerControl Listener,
    VoicePhaseKind Phase,
    bool IsDead)
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}

public sealed record VoiceListenerFilterResult(bool Muffle);
```

```csharp
public static void RegisterListenerOrigin(
    string modId,
    Func<PlayerControl, VoiceListenerResult?> origin);

public static void RegisterContextualListenerOrigin(
    string modId,
    Func<VoiceListenerContext, VoiceListenerResult?> origin);

public static void RegisterListenerFilter(
    string modId,
    Func<PlayerControl, bool> shouldMuffle);

public static void RegisterContextualListenerFilter(
    string modId,
    Func<VoiceListenerContext, VoiceListenerFilterResult> filter);
```

Legacy and contextual origins share one registration order; the first finite, non-null origin wins. An enabled built-in Parasite/Puppeteer control-hearing origin takes precedence. `Replace` hears only from the override during Tasks; `Additive` compares body and override routes and keeps the louder result per speaker.

Any negative `LightRadius`, including `-1`, inherits the local resolved light radius. `0` disables vision-radius limiting at the override. A positive value supplies an explicit radius; non-finite values normalize to inheritance.

Legacy and contextual filters also share one list. Any `true`/`Muffle` result muffles all audible incoming audio for the local listener. Failures are neutral.

```csharp
public sealed record VoicePhaseChangedContext(
    VoicePhaseKind PreviousPhase,
    VoicePhaseKind Phase,
    PlayerControl? LocalPlayer)
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}

public static void RegisterVoicePhaseObserver(
    string modId,
    Action<VoicePhaseChangedContext> observer);
```

The first observed phase initializes the tracker without firing. Later API phase changes fire once before the new phase's player callbacks. Observer exceptions are ignored.

See **[Listener Origin & Filter](Mod-Integration-Listener-Origin)**.

---

## Overlay privacy

Viewer and speaker contexts expose all three option accessors. Viewer results compose `HideAll > DimAll > Pass`. Speaker results compose `HideAll > HideSource > Alias > Pass`; conflicting, missing, sentinel, or unsafe aliases become `HideSource`.

Viewer exceptions fail to `HideAll`; speaker exceptions fail to `HideSource`. Overlay rules affect identity-bearing Perfect Comms UI only, not audio or transmission.

```csharp
public enum VoiceOverlayViewerVerdict
{
    Pass,
    DimAll,
    HideAll,
}

public readonly record struct VoiceOverlayViewerResult(
    VoiceOverlayViewerVerdict Verdict)
{
    public static readonly VoiceOverlayViewerResult Pass;
    public static readonly VoiceOverlayViewerResult DimAll;
    public static readonly VoiceOverlayViewerResult HideAll;
}

public enum VoiceOverlaySpeakerVerdict
{
    Pass,
    Alias,
    HideSource,
    HideAll,
}

public readonly record struct VoiceOverlaySpeakerResult(
    VoiceOverlaySpeakerVerdict Verdict,
    byte? AliasPlayerId = null)
{
    public static readonly VoiceOverlaySpeakerResult Pass;
    public static readonly VoiceOverlaySpeakerResult HideSource;
    public static readonly VoiceOverlaySpeakerResult HideAll;
    public static VoiceOverlaySpeakerResult Alias(byte targetPlayerId);
}

public sealed record VoiceOverlayViewerContext(
    PlayerControl Viewer,
    VoicePhaseKind Phase,
    bool IsDead)
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}

public sealed record VoiceOverlaySpeakerContext(
    PlayerControl Viewer,
    PlayerControl Speaker,
    VoicePhaseKind Phase,
    bool ViewerIsDead,
    bool SpeakerIsDead)
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}

public static void RegisterOverlayViewerRule(
    string modId,
    Func<VoiceOverlayViewerContext, VoiceOverlayViewerResult> rule);

public static void RegisterOverlaySpeakerRule(
    string modId,
    Func<VoiceOverlaySpeakerContext, VoiceOverlaySpeakerResult> rule);
```

See **[Overlay Privacy](Mod-Integration-Overlay-Privacy)** for the exact result records and safe-alias rules.

---

## Host options and tabs

```csharp
public sealed record VoiceHostOption(string Key, string Label, bool Default)
{
    public string Description { get; init; }
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
}

public sealed record VoiceHostEnumOption(
    string Key,
    string Label,
    int Default,
    string[] Choices)
{
    public string Description { get; init; }
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
}

public sealed record VoiceHostNumberOption(
    string Key,
    string Label,
    float Default,
    float Min,
    float Max,
    float Step,
    string Format = "0.0")
{
    public string Description { get; init; }
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
}

public sealed record VoiceHostOptionContext
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}
```

`VoiceHostOptionContext` exposes the three scoped accessors. A visibility callback exception shows the row rather than hiding it.

Every option key must be non-empty and unique across bool, enum, and number options for that exact `modId`. Enum choices must be non-empty; enum defaults, local changes, and received values clamp to the declared choices. Numeric declarations require finite bounds/default/step, `Max >= Min`, and `Step > 0`; invalid declarations are ignored. Numeric values clamp to the range and round to the nearest step relative to `Min`, and an invalid display format falls back to `0.0`.

```csharp
public static void RegisterHostOption(string modId, VoiceHostOption option);
public static void RegisterHostEnumOption(string modId, VoiceHostEnumOption option);
public static void RegisterHostNumberOption(string modId, VoiceHostNumberOption option);
public static void RegisterModTab(string modId, string tabLabel);
```

One exact mod id gets one tab; its first label wins. Rows render toggles, then enums, then numbers, preserving registration order inside each group. Values are session-local defaults/host snapshots, not BepInEx config.

The snapshot holds at most 256 mod-option values and identifies each scoped key/type with a 32-bit wire hash (numbers use a separate type salt); unknown hashes are ignored and collisions are not detected.

See **[Host Options & Tabs](Mod-Integration-Host-Options)**.

---

## Registration, cadence, and failures

An empty `modId` or null registration callback/option is ignored. Host options receive the validation above and the inventory is capped at 256 synced values. Registrations accumulate; only the exact mod tab is deduplicated.

`Unregister(modId)` removes that id's rules, traits, pair rules, channels, listener callbacks, observers, overlay rules, gates, tab, option declarations, and option values. There is no individual unregister method.

The callback collection currently being evaluated is snapshotted. A callback may register or unregister safely without invalidating that pass; a new callback in the same collection begins on its next evaluation. Do normal cross-primitive registration outside callbacks rather than depending on same-frame timing between rules, gates, channels, and listeners.

| Callback | Failure result |
| :--- | :--- |
| Speaker rule | `Pass` |
| Global gate | inactive |
| Player traits | `None` |
| Pair rule | `Pass` |
| Channel | `null` |
| Listener origin | `null` |
| Listener filter | not muffled |
| Phase observer | ignored exception |
| Option visibility | visible |
| Overlay viewer | `HideAll` |
| Overlay speaker | `HideSource` |

Audio callbacks run at snapshot cadence, roughly 20 times per second per applicable player. Listener filters and overlay rules may run once per rendered frame. Do not rely on an exact cadence.

---

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- Perfect Comms synchronizes registered host-option values, not your role/modifier state, targets, pairings, lifecycle history, custom radio hold state, temporary permissions, buttons, keybinds, or role RPCs. The integrating mod owns gameplay state, UI, and netcode.
- Host-option snapshots and local callback evaluation coordinate cooperative clients. They are not hostile-client authentication or enforcement.
