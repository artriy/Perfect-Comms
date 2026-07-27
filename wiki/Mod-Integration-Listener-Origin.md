# Listener Origin, Filter & Phase Observer

Listener origin changes where the local player hears task-world spatial audio from. Listener filters can muffle all incoming Perfect Comms audio or mark the local listener's sight as temporarily obscured. API 1.2 keeps the original delegates and adds contextual phase, host-option, and sight-obscuration state.

Back to **[Mod Integration](Mod-Integration)**

---

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

`VoiceListenerContext` contains the local `Listener`, exact API phase, effective `IsDead`, and `GetOption`, `GetEnumOption`, and `GetNumberOption`.

The original signature remains supported unchanged:

```csharp
PerfectCommsApi.RegisterListenerOrigin(Mod, local =>
    MyRoles.ControlledTarget(local) is PlayerControl target
        ? new VoiceListenerResult(
            (Vector2)target.transform.position,
            -1f,
            VoiceListenerMode.Replace)
        : null);
```

Original and contextual origin callbacks share one registration list. The first finite, non-null origin wins; exceptions and invalid origins are neutral.

---

## Replace, Additive, and light radius

| Mode | Task-phase behavior |
| :--- | :--- |
| `Replace` | Calculate spatial hearing entirely from the override origin. |
| `Additive` | Compare body-origin and override-origin audibility per speaker; keep the more audible result. |

`LightRadius` matters when the host limits hearing to vision:

| Value | Meaning |
| :--- | :--- |
| Any negative value, including `-1` | Inherit the local player's resolved light radius. |
| `0` | Disable vision-radius limiting at the override origin. |
| Positive finite value | Use this explicit radius at the override origin. |
| Non-finite value | Normalize to inheritance. |

Listener-origin relocation is task-phase only. Meeting/Lobby voice does not use the task-world override.

Set `BypassTaskVoiceGates = true` on a `VoiceListenerResult` only for controlled-listener behavior that must bypass the Tasks-wide `OnlyGhostsCanTalk` and Comms-sabotage receive gates. Speaker mutes, phase policy, vent privacy, sight, distance, walls, and channel membership still apply.

---

## Contextual listener filter

Use a contextual filter when blindness, hypnosis, or another listener-owned state should affect incoming hearing:

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

The original boolean delegate also remains supported:

```csharp
PerfectCommsApi.RegisterListenerFilter(
    "com.me.mymod",
    local => MyRoles.IsBlinded(local));
```

Original and contextual filters share one list and compose restrictively. Any `Muffle` result applies the low-pass effect to all audible incoming routes for the local listener. Any `SightObscured` result restricts hearing when the host uses sight-based proximity, without applying a filter or exposing the source mod's private status. Neither field changes the player's transmitted voice. Use `VoiceRuleResult.Muffle` or pair `Muffle` for a speaker-specific effect.

Filter results are cached for the current evaluation pass. Exceptions are neutral: a throwing original predicate behaves as `false`, and a throwing contextual callback contributes neither effect.

---

## Phase observers

A phase observer runs once when the API phase changes:

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

`VoicePhaseChangedContext` contains `PreviousPhase`, `Phase`, `LocalPlayer`, and all three scoped option accessors. The first observed phase initializes the tracker without firing. Later changes fire once before the new phase's player callbacks.

Several internal menu/lobby states map to `VoicePhaseKind.Lobby`; moving between them does not create an API phase transition. Exile is a distinct API phase, so normal post-meeting bookkeeping must accept `Exile -> Tasks` as shown above (and `Meeting -> Tasks` for flows without Exile). Observer exceptions are ignored.

Observers are useful for integration-owned derived bookkeeping, such as activating a synchronized “muted next round” flag. They do not network or authoritatively create that state.

---

## Choosing the primitive

- Move or add the local task-world hearing position: listener origin.
- Muffle everything one listener hears: listener filter.
- Restrict sight-based hearing while the listener is blinded or obscured: contextual listener filter with `SightObscured`.
- Muffle one speaker for every listener: speaker rule.
- Muffle or route one speaker/listener pair: pair rule.
- Run lifecycle bookkeeping at a phase boundary: phase observer.

Register callbacks once, keep them cheap, and return neutral values when inactive. `PerfectCommsApi.Unregister(modId)` removes original and contextual callbacks plus observers for that exact id.

---

## Next

- **[Channels](Mod-Integration-Channels)** - speaker/listener pair routing and spatial channels.
- **[Host Options & Tabs](Mod-Integration-Host-Options)** - options available in contextual callbacks.
- **[Examples](Mod-Integration-Examples)** - Parasite, Puppeteer, blindness, hypnosis, and next-round recipes.

## Current status / limitations

**Currently broken:** None of the documented API 1.2 primitives on this page.

- Perfect Comms persists and synchronizes registered host-option values. Your mod owns controller/target state, world positions, phase-persistent role state, UI, and role RPCs.
- Listener callbacks and observers coordinate cooperative clients; they are not hostile-client authentication or enforcement.
