---
description: "Relocate task hearing, muffle incoming audio, or observe API phase changes."
---

# Listener origin, filter and phase observer

Listener origins move where the local player hears task-world audio from, listener filters muffle incoming audio or mark sight as obscured, and phase observers run bookkeeping at phase changes.

## Contextual listener origin

```csharp
PerfectCommsApi.RegisterContextualListenerOrigin("com.me.mymod", ctx =>
{
    if (ctx.Phase != VoicePhaseKind.Tasks ||
        !ctx.GetOption("SpiritHearing") ||
        MyRoles.SpiritPosition(ctx.Listener) is not Vector2 spirit)
    {
        return null;
    }

    return new VoiceListenerResult(
        Origin: spirit,
        LightRadius: -1f,
        Mode: VoiceListenerMode.Additive);
});
```

`VoiceListenerContext` exposes `Listener`, phase, effective `IsDead`, and the option accessors. The original signature still works:

```csharp
PerfectCommsApi.RegisterListenerOrigin("com.me.mymod", local =>
    MyRoles.ControlledTarget(local) is PlayerControl target
        ? new VoiceListenerResult(
            (Vector2)target.transform.position,
            -1f,
            VoiceListenerMode.Replace)
        : null);
```

Both forms share one list and run only for the local player. The first non-null result with a finite origin wins; exceptions and non-finite origins count as no result. Origins apply only during Tasks.

## Replace, Additive, and light radius

| Mode | Task-phase behavior |
| :--- | :--- |
| `Replace` | Hear only from the override origin. |
| `Additive` | Per speaker, keep the more audible of body and override origin. Any non-`Replace` value acts as `Additive`. |

`LightRadius` applies when the host limits hearing to vision:

| Value | Meaning |
| :--- | :--- |
| Negative (including `-1`) or non-finite | Inherit the local player's light radius. |
| `0` | No vision-radius limit at the override. |
| Positive finite | Explicit radius at the override. |

Set `BypassTaskVoiceGates = true` only for controlled-listener roles that must skip the Tasks **Only Ghosts can Talk/Hear** and Comms-sabotage receive gates. Everything else (speaker mutes, phase policy, vent privacy, sight, distance, walls, channels) still applies.

## Contextual listener filter

```csharp
PerfectCommsApi.RegisterContextualListenerFilter("com.me.mymod", ctx =>
{
    bool obscured = MyRoles.IsBlinded(ctx.Listener);
    return new VoiceListenerFilterResult(
        Muffle: obscured && ctx.GetOption("MuffleBlinded"))
    {
        SightObscured = obscured,
    };
});
```

The original boolean form still works:

```csharp
PerfectCommsApi.RegisterListenerFilter(
    "com.me.mymod",
    local => MyRoles.IsBlinded(local));
```

- Both forms share one list and compose restrictively.
- Any `Muffle` applies the low-pass effect to everything the local player hears.
- Any `SightObscured` restricts sight-based hearing without a filter and without revealing the source mod's status.
- Neither changes the player's transmitted voice; use a speaker or pair `Muffle` for that.
- A throwing boolean filter is `false`; a throwing or `null` contextual filter has no effect.

## Phase observers

```csharp
PerfectCommsApi.RegisterVoicePhaseObserver("com.me.mymod", ctx =>
{
    if ((ctx.PreviousPhase is VoicePhaseKind.Meeting or VoicePhaseKind.Exile) &&
        ctx.Phase == VoicePhaseKind.Tasks)
    {
        MyVoiceState.AdvanceNextRoundEffects();
    }
});
```

- `VoicePhaseChangedContext` exposes `PreviousPhase`, `Phase`, `LocalPlayer`, and the option accessors.
- The first observed phase only initializes tracking. Later changes fire once, before the new phase's player callbacks.
- Menu, lobby, end-of-game, and other non-game states all map to `Lobby`, so moving between them fires nothing.
- Exile is its own phase: handle `Exile -> Tasks`, and `Meeting -> Tasks` for flows without Exile.
- Observer exceptions are ignored. Observers do not network or create authoritative state.

## Choosing the primitive

- Move or add the local task-world hearing position: listener origin.
- Muffle everything one listener hears: listener filter.
- Restrict sight-based hearing while blinded: contextual filter with `SightObscured`.
- Muffle one speaker for every listener: [speaker rule](/api/gate#per-speaker-rules).
- Muffle or route one speaker/listener pair: [pair rule](/api/channels#listener-speaker-pair-rules).
- Run bookkeeping at a phase boundary: phase observer.

`Unregister(modId)` removes original and contextual callbacks plus observers for that id. Full signatures: [Listener origin, filter, and phase observer](/api/reference#listener-origin-filter-and-phase-observer).

## Current status / limitations

**Currently broken:** none.

- Your mod owns controller/target state, world positions, phase-persistent role state, UI, and role RPCs.
- Callbacks and observers coordinate cooperative clients; they are not hostile-client enforcement.
