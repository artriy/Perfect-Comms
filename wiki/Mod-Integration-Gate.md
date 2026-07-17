# Gate, Muffle & Player Traits

Use a speaker rule when a role or effect should change everything one player transmits. Use a global gate when a synchronized condition should silence every speaker in one API phase. Both are enforced in Lobby, Tasks, Meeting, and Exile, including voice-dead players.

Back to **[Mod Integration](Mod-Integration)**

---

## Per-speaker rules

```csharp
PerfectCommsApi.RegisterVoiceRule("com.me.mymod", ctx =>
{
    if (ctx.Phase == VoicePhaseKind.Meeting &&
        ctx.GetOption("MuteGagged") &&
        MyRoles.IsGagged(ctx.Player))
    {
        return VoiceRuleResult.Mute("Gagged");
    }

    if (ctx.Phase == VoicePhaseKind.Tasks &&
        MyRoles.IsVoiceScrambled(ctx.Player))
    {
        return VoiceRuleResult.Muffle("Scrambled");
    }

    return VoiceRuleResult.Pass;
});
```

`VoiceRuleContext` contains the speaker, exact API phase, `IsLocal`, and effective `IsDead`. It also exposes `LocalPlayer`, `LocalIsDead`, and bare-key `GetOption`, `GetEnumOption`, and `GetNumberOption` accessors scoped to the registration's `modId`.

| Result | Effect |
| :--- | :--- |
| `VoiceRuleResult.Pass` | No opinion. Built-in policy and other integrations still apply. |
| `VoiceRuleResult.Muffle(reason)` | Keep the selected route but apply the per-speaker low-pass effect. |
| `VoiceRuleResult.Mute(reason)` | Silence the speaker for the local transmit path and every local receive route. |

Rules run in registration order. Muffle is sticky and a later mute can still win. The first mute fixes the effective mute reason, but remaining callbacks are still invoked so an unchanged legacy integration can refresh phase/option state regardless of another mod's result. Later results cannot relax or rename that mute. Return `Pass` whenever your integration has no opinion.

External speaker restrictions are applied again after route selection. A channel, pair route, built-in team route, or ghost route does not bypass a speaker mute.

---

## Global gates

The original phase gate remains valid:

```csharp
PerfectCommsApi.RegisterGlobalGate(
    "com.me.mymod",
    VoicePhaseKind.Tasks,
    () => MySystems.IsJamActive,
    "Jammed");
```

Use the contextual form when the gate depends on the local listener or a host option:

```csharp
PerfectCommsApi.RegisterContextualGlobalGate(
    "com.me.mymod",
    VoicePhaseKind.Tasks,
    ctx => ctx.GetOption("EnableCommsJam") &&
           MySystems.IsJamActiveFor(ctx.LocalPlayer),
    "Jammed");
```

`VoiceGlobalGateContext` contains `LocalPlayer`, the exact phase, `LocalIsDead`, and all three option accessors. Register each phase separately; Meeting and Exile are distinct.

A true global gate is receiver-enforced for every resolved player state and also blocks the matching local transmit path. This makes a synchronized lobby-wide effect consistent for living, voice-dead, and spectator listeners on cooperative clients.

---

## Player traits

Traits let an external role participate in Perfect Comms' voice classification without pretending to be a built-in role:

```csharp
PerfectCommsApi.RegisterVoicePlayerTraits("com.me.mymod", ctx =>
{
    VoicePlayerTraits traits = VoicePlayerTraits.None;

    if (MyRoles.IsCrewpostor(ctx.Player))
        traits |= VoicePlayerTraits.ImpostorVoice;

    if (MyRoles.IsSpirit(ctx.Player))
        traits |= VoicePlayerTraits.VoiceDead;

    if (MyRoles.IsSpectator(ctx.Player))
        traits |= VoicePlayerTraits.Spectator;

    return traits;
});
```

| Trait | Effect |
| :--- | :--- |
| `ImpostorVoice` | Uses impostor-equivalent vent, ghost-hearing, team-radio, and viewer classification. |
| `VoiceDead` | Treats the player as dead for voice policy even when the game object is alive. |
| `Spectator` | Marks spectator voice and also implies `VoiceDead`. |

Traits compose by bitwise OR across registrations. They can add classification but cannot remove base game classification. Unknown bits and callback exceptions are ignored. The trait callback sees base dead state; later speaker, channel, and pair callbacks see the effective voice-dead state.

---

## Context and phase behavior

- Lobby, Tasks, Meeting, and Exile are all enforceable API phases.
- `ctx.Player` is the speaker being evaluated; `ctx.LocalPlayer` is the local listener when available.
- Audio callbacks run locally at voice-snapshot cadence, roughly 20 times per second per applicable player. Keep them deterministic and cheap.
- A throwing speaker rule is `Pass`; a throwing global predicate is inactive; a throwing trait callback contributes `None`.
- Built-in restrictions still apply. `Pass` never grants an exemption.
- Perfect Comms does not network the role/effect state read by these callbacks.

---

## Common patterns

**Host-configured numeric threshold:**

```csharp
PerfectCommsApi.RegisterVoiceRule("com.me.mymod", ctx =>
    ctx.Phase == VoicePhaseKind.Tasks &&
    ctx.GetOption("EnableWeakSignal") &&
    MyRoles.SignalStrength(ctx.Player) < ctx.GetNumberOption("MinimumSignal")
        ? VoiceRuleResult.Muffle("Weak signal")
        : VoiceRuleResult.Pass);
```

**Persistent next-round state:** record the affected player ids in your mod's synchronized state, update/clear that state from a phase observer, and let a Tasks speaker rule read it. A phase observer is a lifecycle hook, not state synchronization.

**Authoritative private restriction:** use a speaker rule or global gate when a mute must survive every possible route. Pair rules are best for listener-specific privacy and routing.

---

## Next

- **[Channels](Mod-Integration-Channels)** - multiple memberships, receive-only endpoints, and pair routing.
- **[Listener Origin & Filter](Mod-Integration-Listener-Origin)** - remote hearing points and listener-wide muffle.
- **[Examples](Mod-Integration-Examples)** - role recipes and the full TOU-Mira parity matrix.

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- Perfect Comms synchronizes registered host-option values only. Your mod owns gameplay state, UI, custom input, role RPCs, pairings, targets, and lifecycle bookkeeping.
- Host snapshots and callbacks coordinate cooperative clients; they are not hostile-client authentication or enforcement.
