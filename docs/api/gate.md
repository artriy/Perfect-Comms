---
description: "Apply speaker and global voice policy, speaker muffle and player traits in every API phase."
---

# Gate, muffle and player traits

Use a speaker rule to change everything one player transmits, and a global gate to silence every speaker in one API phase. Both apply in Lobby, Tasks, Meeting, and Exile, including to voice-dead players.

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

`VoiceRuleContext` exposes the speaker (`ctx.Player`), exact phase, `IsLocal`, effective `IsDead`, `LocalPlayer`, `LocalIsDead`, and the scoped option accessors.

| Result | Effect |
| :--- | :--- |
| `VoiceRuleResult.Pass` | No opinion. Built-in policy and other integrations still apply. |
| `VoiceRuleResult.Muffle(reason)` | Keep the selected route, add the low-pass effect. |
| `VoiceRuleResult.Mute(reason)` | Silence the speaker on the local transmit path and every local receive route. |

Within one mod, rules run in registration order; do not rely on order between mods. Muffle is sticky and a later mute still wins. The first mute fixes the reason and cannot be relaxed, though remaining callbacks still run. Speaker restrictions are reapplied after route selection, so no channel, pair, team, or ghost route bypasses a speaker mute.

## Global gates

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

`VoiceGlobalGateContext` exposes `LocalPlayer`, phase, `LocalIsDead`, and the option accessors. Register each phase separately; Meeting and Exile are distinct.

An active gate is enforced by every receiver (living, voice-dead, and spectator) and also blocks the local transmit path. The first active gate supplies the mute reason. A player already muted by a speaker rule keeps that reason and skips gate evaluation.

## Player traits

Traits let an external role join Perfect Comms' voice classification:

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
| `ImpostorVoice` | Counts as impostor for vent hearing (**Hear Impostors in Vents** on, **Private Talk in Vents** off) and ghost hearing (**Impostors Hear Dead**). Built-in impostor Team Radio, task-phase impostor chat, and the impostor meeting radio still need the real impostor role. |
| `VoiceDead` | Dead for voice policy even when the game object is alive. |
| `Spectator` | Spectator voice; implies `VoiceDead`. |

Traits OR together across registrations and can add but not remove base classification. The trait callback sees base dead state; speaker, channel, and pair callbacks see effective voice-dead state.

## Rules to know

- A throwing or `null` speaker rule is `Pass`, a throwing gate predicate is inactive, and a throwing trait callback contributes `None`. Unknown trait bits are ignored.
- `Pass` never exempts a player from built-in restrictions.
- Use a speaker rule or global gate when a mute must survive every route; use [pair rules](/api/channels#listener-speaker-pair-rules) for listener-specific privacy.

Full signatures: [Speaker rules and global gates](/api/reference#speaker-rules-and-global-gates), [Player traits](/api/reference#player-traits).

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

**Next-round state:** store affected player ids in your mod's synced state, update it from a [phase observer](/api/listener-origin#phase-observers), and read it in a Tasks speaker rule.

## Current status / limitations

**Currently broken:** none.

- Perfect Comms does not network the role or effect state these callbacks read; your mod owns it.
- Callbacks coordinate cooperative clients; they are not hostile-client enforcement.
