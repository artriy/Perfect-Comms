# Gate: Mute & Muffle

The Gate primitive silences or muffles a player's voice. It covers the majority of role voice behaviours: "shut this player up while X", "muffle their hearing while Y", "mute everyone while a system is active".

← Back to **[Mod Integration](Mod-Integration)**

---

## Per-player rule

Register a callback that, for each player each frame, returns a verdict:

```csharp
PerfectCommsApi.RegisterVoiceRule("com.me.mymod", ctx =>
{
    // ctx.Player  : PlayerControl being evaluated
    // ctx.Phase   : Lobby / Tasks / Meeting / Exile
    // ctx.IsLocal : is this the local player?
    // ctx.IsDead  : is this player dead?

    if (ctx.Phase == VoicePhaseKind.Meeting && MyRoles.IsGagged(ctx.Player))
        return VoiceRuleResult.Mute("Gagged");      // fully silenced, HUD shows "Gagged"

    if (MyRoles.IsConfused(ctx.Player))
        return VoiceRuleResult.Muffle("Confused");  // heard but low-pass filtered

    return VoiceRuleResult.Pass;                    // no opinion - defer to other rules
});
```

### Verdicts

| Verdict | Effect |
| :--- | :--- |
| `VoiceRuleResult.Mute(reason)` | Player cannot be heard. `reason` shows in the HUD. |
| `VoiceRuleResult.Muffle(reason)` | Player is heard through a low-pass filter. |
| `VoiceRuleResult.Pass` | No opinion. Built-in rules and other mods decide. |

The **first non-`Pass` verdict wins** across all registered mods, so keep rules specific - return `Pass` whenever your role does not apply.

---

## Global gate (whole lobby)

For "everyone is muted while this system/effect is active" (jam fields, blackout events), use a phase-scoped global gate instead of checking every player:

```csharp
PerfectCommsApi.RegisterGlobalGate(
    "com.me.mymod",
    VoicePhaseKind.Meeting,            // phase it applies in
    () => MySystems.IsJamActive,       // cheap predicate, checked once per phase
    "Jammed");                          // HUD reason
```

While the predicate returns `true`, all voice in that phase is muted.

---

## How it behaves

- **Phase-aware.** Gates only apply in the phase you check (`ctx.Phase`) or the phase you registered a global gate for. The four phases are `Lobby`, `Tasks`, `Meeting`, `Exile`.
- **Your own mic too.** If a rule mutes the *local* player, their microphone is gated locally - they cannot transmit. This is automatic.
- **Fail-closed.** If your callback throws, it is treated as `Pass`. The voice frame never breaks.
- **Local-only.** Verdicts are computed on each client from your own role state, so all clients converge - no networking.

---

## Patterns

**Mute while a modifier is present:**

```csharp
PerfectCommsApi.RegisterVoiceRule("com.me.mymod", ctx =>
    ctx.Player.GetModifier<SilencedModifier>() != null
        ? VoiceRuleResult.Mute("Silenced")
        : VoiceRuleResult.Pass);
```

**Mute only the local player (e.g. a self-inflicted effect):**

```csharp
PerfectCommsApi.RegisterVoiceRule("com.me.mymod", ctx =>
    ctx.IsLocal && MyState.SelfMuted
        ? VoiceRuleResult.Mute("Muted")
        : VoiceRuleResult.Pass);
```

**Gate behind a host option** (see [Host Options](Mod-Integration-Host-Options)):

```csharp
PerfectCommsApi.RegisterVoiceRule("com.me.mymod", ctx =>
    ctx.GetOption("MuteSilenced") && MyRoles.IsSilenced(ctx.Player)
        ? VoiceRuleResult.Mute("Silenced")
        : VoiceRuleResult.Pass);
```

---

## Next

- **[Channels](Mod-Integration-Channels)** - let players hear each other, instead of muting.
- **[Host Options & Tabs](Mod-Integration-Host-Options)** - make a rule host-configurable.
