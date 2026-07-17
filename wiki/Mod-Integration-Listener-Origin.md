# Listener Origin, Filter & Phase Observer

Listener origin changes where the local player hears task-world spatial audio from. Listener filters muffle all audible incoming Perfect Comms audio for the local player. Contextual API 1.1 forms add phase and host-option access without removing the original delegates.

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

Legacy and contextual origin callbacks share one registration list. The first finite, non-null origin wins; exceptions and invalid origins are neutral.

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

An enabled active built-in Parasite or Puppeteer control-hearing origin takes precedence. If its corresponding built-in host behavior is disabled, an external origin can apply normally.

---

## Contextual listener filter

Use a contextual filter when blindness, hypnosis, or another listener-owned state should muffle all incoming audio:

```csharp
PerfectCommsApi.RegisterContextualListenerFilter("com.me.mymod", ctx =>
    new VoiceListenerFilterResult(
        Muffle: ctx.GetOption("MuffleBlinded") &&
                 MyRoles.IsBlinded(ctx.Listener)));
```

The original boolean delegate also remains supported:

```csharp
PerfectCommsApi.RegisterListenerFilter(
    "com.me.mymod",
    local => MyRoles.IsBlinded(local));
```

Legacy and contextual filters share one list and compose as “any muffle.” One active result applies the low-pass effect to all audible incoming routes for the local listener. It does not change that player's transmitted voice and cannot select one speaker; use `VoiceRuleResult.Muffle` or a pair `Muffle` for that.

Filter results are frame-cached. Exceptions are neutral: a throwing legacy predicate behaves as `false`, and a throwing contextual callback returns no muffle.

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
- Muffle one speaker for every listener: speaker rule.
- Muffle or route one speaker/listener pair: pair rule.
- Run lifecycle bookkeeping at a phase boundary: phase observer.

Register callbacks once, keep them cheap, and return neutral values when inactive. `PerfectCommsApi.Unregister(modId)` removes legacy and contextual callbacks plus observers for that exact id.

---

## Next

- **[Channels](Mod-Integration-Channels)** - speaker/listener pair routing and spatial channels.
- **[Host Options & Tabs](Mod-Integration-Host-Options)** - options available in contextual callbacks.
- **[Examples](Mod-Integration-Examples)** - Parasite, Puppeteer, blindness, hypnosis, and next-round recipes.

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- Perfect Comms synchronizes registered host-option values only. Your mod owns controller/target state, world positions, phase-persistent role state, UI, and role RPCs.
- Listener callbacks and observers coordinate cooperative clients; they are not hostile-client authentication or enforcement.
