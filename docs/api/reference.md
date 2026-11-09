---
description: "Every PerfectComms.Api type and method, with the API 1.2 compatibility contract."
---

# API reference

Supported namespace: `PerfectComms.Api` in `PerfectComms.dll`. Current API version: **1.2**. Only `PerfectComms.Api` types are the integration contract; public classes under `VoiceChatPlugin.VoiceChat` are not API.

## Compatibility contract

API 1.2 keeps every API 1.0 and 1.1 enum value, positional record constructor, and registration signature, so existing source and binaries work unchanged. New context/result properties are init-only; new behavior uses separately named methods, not delegate overloads. The original signatures:

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

## Runtime version and capabilities

```csharp
public const string ApiVersion = "1.2";
public const string PluginId = "com.edgetel.perfectcomms";

public static string RuntimeApiVersion { get; }
public static VoiceApiCapability Capabilities { get; }
public static bool Supports(VoiceApiCapability capability);
```

| Member | Meaning |
| :--- | :--- |
| `ApiVersion` | Compiled into the consuming assembly; not a runtime probe. |
| `RuntimeApiVersion` | Installed runtime's `ApiVersion` (currently `"1.2"`). |
| `PluginId` | BepInEx plugin id. Check it first and keep every `PerfectComms.Api` reference inside a bridge that only runs when the plugin is present. |
| `Capabilities` | Every flag below. |
| `Supports` | `true` only if every requested flag is present; `false` for `None`. |

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
    ManagedTeamRadio = 1 << 11,
    PersistentHostOptions = 1 << 12,
    OverlayAppearance = 1 << 14,
    ListenerTaskGateBypass = 1 << 15,
    ListenerSightObscuration = 1 << 16,
}
```

Bit `1 << 13` is unused.

| Perfect Comms | API | Probe members |
| :--- | :--- | :--- |
| 4.0.x and older | constant reads `"1.1"` | None of `RuntimeApiVersion`, `Capabilities`, `Supports`; reflect before calling. |
| 4.1.0 to 4.1.6 | 1.1 | `RuntimeApiVersion` (`"1.1"`), `Capabilities`, `Supports`; flags up to `OverlayPrivacy`. |
| 4.1.7+ (API package `4.1.7.1`) | 1.2 | All. |

API 1.2 only (absent before 4.1.7): the `ManagedTeamRadio`, `PersistentHostOptions`, `OverlayAppearance`, `ListenerTaskGateBypass`, and `ListenerSightObscuration` flags; `RegisterManagedRadioChannel` and `VoiceManagedRadioChannelResult`; `RegisterAnimatedColorRule`; `VoiceListenerResult.BypassTaskVoiceGates`; `VoiceListenerFilterResult.SightObscured`; `VoiceHostOptionContext.TeamRadioEnabled`; `LegacyBinding`/`VoiceHostOptionLegacyBinding`. A 1.2 bridge that must run on older builds must probe by reflection before entering code that references these, or require 4.1.7+.

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

Callbacks receive the exact API phase; Meeting and Exile are distinct. Internal menu/lobby-like states map to `Lobby`. EndGame is a fresh global results-screen call: per-player callbacks cannot be re-resolved after player objects disappear, so retained task/meeting mute, muffle, channel, and pair state is not reapplied.

Where present, these accessors take a bare key scoped to the callback's `modId`. Unregistered or missing keys return `false`, `0`, `0f`.

```csharp
Func<string, bool> GetOption;
Func<string, int> GetEnumOption;
Func<string, float> GetNumberOption;
```

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
    public static readonly VoiceRuleResult Pass;              // (Pass, "")
    public static VoiceRuleResult Mute(string reason);        // null reason -> "Muted"
    public static VoiceRuleResult Muffle(string reason);      // null reason -> "Muffled"
}

public static void RegisterVoiceRule(
    string modId,
    Func<VoiceRuleContext, VoiceRuleResult> rule);
```

- `Player`: speaker being resolved. `LocalPlayer`: this client's listener when available. `IsDead`/`LocalIsDead` include contributed `VoiceDead`/`Spectator` traits.
- `Mute`: silences the speaker in Lobby, Tasks, Meeting, and Exile, including voice-dead routes; blocks local transmit when the local player is affected.
- `Muffle`: keeps that speaker audible through the low-pass filter.
- `Mute` beats `Muffle`; otherwise muffle is sticky.

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

Both gate forms are phase-exact and enforced on transmit and receive. The contextual form can read options and local listener state.

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

- Traits OR together across registrations and cannot remove a base classification. Unknown bits are ignored.
- `Spectator` implies `VoiceDead`.
- `ImpostorVoice` counts as impostor for vent hearing (**Hear Impostors in Vents** with **Private Talk in Vents** off) and Tasks ghost hearing (**Impostors Hear Dead**). It does not grant built-in impostor Team Radio, task-only impostor chat, impostor meeting radio, or viewer privacy; those check the real impostor role.
- Trait callbacks get the base dead state; later rules get the effective voice-dead state.

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
    public VoicePairRouteShape Shape { get; init; } = VoicePairRouteShape.Proximity;
    public float Volume { get; init; } = 1f;
    public Vector2? SpeakerOrigin { get; init; }
    public Vector2? ListenerOrigin { get; init; }

    public static readonly VoicePairResult Pass;              // (Pass, "")
    public static VoicePairResult Mute(string reason);        // null reason -> "Muted"
    public static VoicePairResult Muffle(string reason);      // null reason -> "Muffled"
    public static VoicePairResult Route(
        VoicePairRouteShape shape,
        float volume = 1f,
        Vector2? speakerOrigin = null,
        Vector2? listenerOrigin = null,
        string reason = "Mod Route");                         // null reason -> "Mod Route"
}

public static void RegisterVoicePairRule(
    string modId,
    Func<VoicePairContext, VoicePairResult> rule);
```

Pair callbacks see the local listener and a remote speaker.

- `Mute` wins immediately. `Muffle` is retained and applies to whichever route is selected.
- The first valid `Route` wins; later rules are still checked for mute/muffle.
- Shapes: `Radio` is flat; `Proximity` and `Ghost` use host distance/falloff and pan. Omitted origins use resolved speaker/listener positions. Volume clamps to `0..1`. Invalid shape or non-finite volume/origins are neutral.
- A route replaces ordinary routing for that pair. Speaker/global mutes, capture restrictions, and Tasks `OnlyMeetingOrLobby` stay authoritative. In Tasks, the route runs before receive-side `OnlyGhostsCanTalk` and Comms-sabotage blocking (Medium-style exceptions).
- Pair and channel routes cannot bypass Meeting/Exile death separation. Exception: the host's built-in impostor meeting radio, in Meeting only (never Exile), when both across-death options are on. Actual impostors transmit; living actual impostors, all ghosts and spectators listen. Audience authorization runs before pair routing, so crew ghosts and spectators cannot transmit, and living crew or mod-added voice privileges gain no access. Eligible listeners keep pair mutes, route shape and gain.
- With **Ghosts Hear Living During Tasks**, a living speaker's ghost-only Tasks speech goes only to authenticated ghosts and spectators. Pair routes and `BypassTaskVoiceGates` do not extend it to living listeners.

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

- Every non-null result with a non-empty key is retained, so a player can hold several memberships from several callbacks. Keys are scoped internally to `modId` (encoding not part of the contract).
- A listener hears a target when both share the key and the target's membership has `TwoWay: true`. `TwoWay: false` is receive-only.
- The target membership supplies shape, volume, and origin. With several shared memberships, the loudest valid route is used.
- `Radio` and `Muffle` are flat. `Proximity` uses the target's finite `Origin`, else its body position, and spatializes in every phase when a listener position exists.
- Volume clamps. Invalid shape falls back to `Radio`; non-finite origin falls back to the speaker.

### Managed Team Radio

```csharp
public sealed record VoiceManagedRadioChannelResult(
    string Key,
    string Label,
    string Badge);

public static void RegisterManagedRadioChannel(
    string modId,
    Func<VoiceRuleContext, VoiceManagedRadioChannelResult?> channel);
```

- Return one membership or `null` per resolved player. Players returning the same key (namespaced by `modId`) share a selectable private radio. `Label` shows in tooltips, `Badge` on compact/touch UI.
- Empty/control-character keys and oversized wire keys are ignored; duplicate keys for one player collapse to one choice.
- Uses the existing Team Radio master and Tasks/Meeting policy. Perfect Comms adds memberships after built-in selector choices, opens capture while its radio control is held (also in Push To Talk mode), syncs the selected key, applies the radio filter to matching members, and mutes living non-members before pair/general-channel routing.
- A claimed key missing from the transmitter's resolved memberships is muted. Only living members with the same key hear it; dead players and spectators do not.
- Use `RegisterVoiceChannel` when your mod owns transmit state or wants a nonexclusive route; use `RegisterManagedRadioChannel` when Perfect Comms should own selection, PTT, wire state, and private routing.

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
    VoiceListenerMode Mode)
{
    public bool BypassTaskVoiceGates { get; init; }
}

public sealed record VoiceListenerContext(
    PlayerControl Listener,
    VoicePhaseKind Phase,
    bool IsDead)
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
}

public sealed record VoiceListenerFilterResult(bool Muffle)
{
    public bool SightObscured { get; init; }
}
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

Origins (local player only):

- Original and contextual origins share one registration order; the first non-null result with a finite origin wins.
- `Replace` hears only from the override during Tasks. Any other `Mode` acts as `Additive`: body and override routes are compared and the louder wins per speaker.
- `LightRadius`: negative (including `-1`) or non-finite inherits the local light radius; `0` disables vision-radius limiting at the override; positive is an explicit radius.
- `BypassTaskVoiceGates` bypasses only Tasks-wide `OnlyGhostsCanTalk` and Comms-sabotage receive gates. Speaker mutes, phase policy, vent privacy, sight, distance, walls, channel membership, and ghost-only speech (see pair rules) still apply.

Filters: original and contextual filters share one list and compose restrictively. Any `Muffle` low-passes all audible incoming audio. Any `SightObscured` restricts sight-based hearing as if blinded, without revealing the source mod's state.

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

The first observed phase initializes without firing. Later API phase changes fire once, before the new phase's player callbacks.

## Overlay privacy

Overlay rules affect identity-bearing Perfect Comms UI only, not audio or transmission.

- Viewer results compose `HideAll > DimAll > Pass`. Viewer rules never run in the menu, lobby, or end-of-game results screen; those always show public identity.
- Speaker results compose `HideAll > HideSource > Alias > Pass`. Conflicting, missing, sentinel, or unsafe aliases become `HideSource`. Safe-alias rules: **[Overlay Privacy](/api/overlay-privacy)**.

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

## Overlay appearance

```csharp
public static void RegisterAnimatedColorRule(
    string modId,
    Func<int, bool> isAnimatedColor);
```

Classifiers compose additively: the first `true` gives that color id the animated rainbow speaking-avatar material. Perfect Comms does not reflect mod-specific color state; source mods register their own.

## Host options and tabs

```csharp
public sealed record VoiceHostOptionLegacyBinding(string Section, string Key);

public sealed record VoiceHostOption(string Key, string Label, bool Default)
{
    public string Description { get; init; } = "";
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
    public VoiceHostOptionLegacyBinding? LegacyBinding { get; init; }
}

public sealed record VoiceHostEnumOption(
    string Key,
    string Label,
    int Default,
    string[] Choices)
{
    public string Description { get; init; } = "";
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
    public VoiceHostOptionLegacyBinding? LegacyBinding { get; init; }
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
    public string Description { get; init; } = "";
    public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
    public VoiceHostOptionLegacyBinding? LegacyBinding { get; init; }
}

public sealed record VoiceHostOptionContext
{
    public Func<string, bool> GetOption { get; init; }
    public Func<string, int> GetEnumOption { get; init; }
    public Func<string, float> GetNumberOption { get; init; }
    public bool TeamRadioEnabled { get; init; }
}
```

| Member | Meaning / fallback |
| :--- | :--- |
| `Visible` | Row visibility; an exception shows the row. |
| `TeamRadioEnabled` | Current host-synced state of the main Team Radio toggle. |
| `LegacyBinding` | One-way migration: when `modId.Key` is first created in Perfect Comms' config, the value under `Section`/`Key` in the same file becomes its default. Empty/whitespace section or key, or a read failure, falls back to `Default`. Afterwards the new setting is authoritative. |
| `Key` | Non-empty, not whitespace, unique across bool, enum, and number options for that exact `modId`. |
| `Choices` | Must be non-empty; null entries become empty strings. Defaults, local changes, and received values clamp to the choices. |
| Number bounds | Finite bounds/default/step, `Max >= Min`, `Step > 0`, else the declaration is ignored. Values clamp to range and round to the nearest step relative to `Min`. |
| `Format` | Empty or invalid falls back to `0.0`. |

```csharp
public static void RegisterHostOption(string modId, VoiceHostOption option);
public static void RegisterHostEnumOption(string modId, VoiceHostEnumOption option);
public static void RegisterHostNumberOption(string modId, VoiceHostNumberOption option);
public static void RegisterModTab(string modId, string tabLabel);
```

- One tab per exact mod id; the first label wins. Rows render toggles, then enums, then numbers, each in registration order.
- Local host values persist in Perfect Comms' global BepInEx config under encoded mod/key definitions. The connected host's values sync as lobby overrides and never overwrite a client's stored local-host choices.
- The snapshot holds at most 256 mod-option values. Each scoped key/type is a 32-bit wire hash (numbers use a separate type salt); unknown hashes are ignored, collisions are not detected.

## Registration, cadence, and failures

- Ignored: empty `modId`, null callback/option, empty `RegisterModTab` label, an option key already registered for that `modId` (any type), and any option registration once 256 option values exist across all mods.
- Registrations accumulate; only the mod tab is deduplicated.
- `Unregister(modId)` removes that id's rules, traits, pair rules, general/managed channels, listener origins and filters, phase observers, overlay rules, animated-color rules, global gates, tab, option declarations, and active option values. Persisted local-host option entries remain for re-registration. There is no per-callback unregister.
- The collection being evaluated is snapshotted: a callback may register or unregister safely; a new callback in that collection starts on its next evaluation. Register across primitives outside callbacks instead of relying on same-frame timing.
- Audio callbacks run at snapshot cadence, about 20 times per second per applicable player. Listener filters and overlay rules may run every rendered frame. Do not rely on exact cadence.

Throwing callbacks fall back as below; a null speaker-rule or pair-rule result is also treated as `Pass`.

| Callback | Failure result |
| :--- | :--- |
| Speaker rule | `Pass` |
| Global gate | inactive |
| Player traits | `None` |
| Pair rule | `Pass` |
| Channel | `null` |
| Managed radio | `null` |
| Listener origin | `null` |
| Listener filter | not muffled or sight-obscured |
| Phase observer | ignored exception |
| Option visibility | visible |
| Overlay viewer | `HideAll` |
| Overlay speaker | `HideSource` |
| Animated color | `false` |

## Current status / limitations

**Currently broken:** none of the API 1.2 primitives on this page.

- Perfect Comms syncs registered host-option values and persists local-host choices. It does not own your role/modifier state, targets, pairings, lifecycle history, or role RPCs. Managed Team Radio owns its selector/input/capture/wire path, not gameplay membership.
- Host-option snapshots and local callback evaluation coordinate cooperative clients. They are not hostile-client authentication or enforcement.
