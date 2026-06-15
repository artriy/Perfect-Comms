# Examples

Worked examples that recreate familiar Town of Us style role behaviours with the public API. Each shows the same pattern you would use in your own mod: reference your **own** role/modifier types at compile time (no reflection), and return a verdict only when your role applies.

All snippets assume you registered under one mod id and have already confirmed Perfect Comms is present (see [Mod Integration](Mod-Integration)).

```csharp
using PerfectComms.Api;
const string Mod = "com.me.mymod";
```

← Back to **[Mod Integration](Mod-Integration)**

---

## Blackmailer

**Behaviour:** a blackmailed player cannot talk during meetings. Optionally they also stay muted for the following round.

**Primitive:** [Gate](Mod-Integration-Gate). Two host toggles drive it.

```csharp
PerfectCommsApi.RegisterModTab(Mod, "My Mod");
PerfectCommsApi.RegisterHostOption(Mod,
    new VoiceHostOption("MuteBlackmailed", "<color=#FF1919><b>Blackmailer</b></color>: Mute in Meetings", true));
PerfectCommsApi.RegisterHostOption(Mod,
    new VoiceHostOption("MuteBlackmailedNextRound", "<color=#FF1919><b>Blackmailer</b></color>: Mute Next Round", false));

PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
{
    // Your own modifier, your own check - no Perfect Comms internals.
    bool blackmailed = ctx.Player.GetModifier<BlackmailedModifier>() != null;
    if (!blackmailed) return VoiceRuleResult.Pass;

    // Meetings: muted when the host enabled it.
    if (ctx.Phase == VoicePhaseKind.Meeting && ctx.GetOption("MuteBlackmailed"))
        return VoiceRuleResult.Mute("Blackmailed");

    // Tasks of the following round: muted when the next-round toggle is on.
    if (ctx.Phase == VoicePhaseKind.Tasks
        && ctx.GetOption("MuteBlackmailedNextRound")
        && MyRoles.WasBlackmailedLastMeeting(ctx.Player))
        return VoiceRuleResult.Mute("Blackmailed");

    return VoiceRuleResult.Pass;
});
```

> `WasBlackmailedLastMeeting` is your own bookkeeping - track who was blackmailed when the meeting ended and clear it when the next meeting starts. The API only needs the boolean.

---

## Jailor

**Behaviour:** the jailed player is muted in meetings, but the Jailor can choose to let them speak.

**Primitive:** [Gate](Mod-Integration-Gate), gated on your own "is this player jailed, and has the jailor opened their mic?" state.

```csharp
PerfectCommsApi.RegisterHostOption(Mod,
    new VoiceHostOption("MuteJailed", "<color=#A6A6A6><b>Jailor</b></color>: Mute Jailee in Meetings", true));

PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
{
    if (ctx.Phase != VoicePhaseKind.Meeting) return VoiceRuleResult.Pass;
    if (!ctx.GetOption("MuteJailed")) return VoiceRuleResult.Pass;

    var jail = ctx.Player.GetModifier<JailedModifier>();
    if (jail == null) return VoiceRuleResult.Pass;

    // Jailor unmuted them this meeting -> let them speak.
    if (MyRoles.JailorAllowedVoice(ctx.Player)) return VoiceRuleResult.Pass;

    return VoiceRuleResult.Mute("Jailed");
});
```

> The "jailor opened the mic" decision is your mod's own networked state. Perfect Comms just reads the resulting boolean each frame, so the verdict converges on every client.

---

## Puppeteer

**Behaviour:** while the Puppeteer controls a victim, the victim is muted, and the Puppeteer hears the world **from the victim's body** (their own body is frozen).

**Primitives:** [Gate](Mod-Integration-Gate) for the victim mute + [Listener Origin](Mod-Integration-Listener-Origin) (`Replace`) for the hearing swap.

```csharp
PerfectCommsApi.RegisterHostOption(Mod,
    new VoiceHostOption("MutePuppeteered", "<color=#FF1919><b>Puppeteer</b></color>: Mute Controlled Victim", true));
PerfectCommsApi.RegisterHostOption(Mod,
    new VoiceHostOption("PuppeteerHearsVictim", "<color=#FF1919><b>Puppeteer</b></color>: Hear From Victim", true));

// 1) Mute the controlled victim.
PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
    ctx.GetOption("MutePuppeteered") && ctx.Player.GetModifier<PuppeteerControlModifier>() != null
        ? VoiceRuleResult.Mute("Controlled")
        : VoiceRuleResult.Pass);

// 2) Relocate the Puppeteer's hearing to the victim's position (local player only).
PerfectCommsApi.RegisterListenerOrigin(Mod, local =>
{
    var victim = MyRoles.VictimControlledBy(local);   // the player this local Puppeteer is driving
    return victim != null
        ? new VoiceListenerResult(victim.transform.position, LightRadius: -1f, VoiceListenerMode.Replace)
        : null;
});
```

> Listener-origin callbacks receive only the local `PlayerControl`, not a `VoiceRuleContext`, so they cannot call `ctx.GetOption`. If you want the swap to honour a host toggle, only set the controlling state in `MyRoles.VictimControlledBy` when your synced toggle is on, or simply always relocate and let the victim-mute toggle govern the audible result. (For a Parasite-style "hear your own body **and** the victim", use `VoiceListenerMode.Additive` instead of `Replace`.)

---

## Medium

**Behaviour:** a living Medium and the dead players can talk to each other privately during tasks, as a two-way séance channel, while the living crew cannot hear it.

**Primitive:** [Channel](Mod-Integration-Channels). Everyone on the séance shares one key; non-members never resolve it, so they are excluded automatically.

```csharp
PerfectCommsApi.RegisterHostEnumOption(Mod,
    new VoiceHostEnumOption("MediumVoice", "<color=#A680FF><b>Medium</b></color>: Ghost Voice",
        Default: 3, Choices: new[] { "Off", "Medium -> Ghosts", "Ghosts -> Medium", "Both" }));

PerfectCommsApi.RegisterVoiceChannel(Mod, ctx =>
{
    // Channel membership: the living Medium and any dead player join the same key.
    bool isMedium = ctx.Player.GetRole<MediumRole>() != null && !ctx.IsDead;
    bool isGhost  = ctx.IsDead;
    if (!isMedium && !isGhost) return null;

    // One shared key for the séance. (Use a per-medium key if you support multiple mediums.)
    return new VoiceChannelResult("medium-seance", TwoWay: true, Shape: VoiceAudioShape.Radio);
});
```

### Directional variants

The host enum above offers one-way modes. Channels express direction with `TwoWay`:

- **Medium -> Ghosts only:** ghosts hear the medium, not vice versa. Give the *medium* a one-way link and ghosts a listen-only side:

```csharp
PerfectCommsApi.RegisterVoiceChannel(Mod, ctx =>
{
    int mode = ctx.GetEnumOption("MediumVoice");      // 0 Off, 1 M->G, 2 G->M, 3 Both
    if (mode == 0) return null;

    bool isMedium = ctx.Player.GetRole<MediumRole>() != null && !ctx.IsDead;
    bool isGhost  = ctx.IsDead;
    if (!isMedium && !isGhost) return null;

    // Both -> two-way. Otherwise the speaker side that is allowed gets a one-way link.
    bool twoWay = mode == 3;
    return new VoiceChannelResult("medium-seance", TwoWay: twoWay, Shape: VoiceAudioShape.Radio);
});
```

> Channels are full-volume radio by default. For a spatial seance (the ghost is loud near the spirit point and fades with distance), set `Shape: VoiceAudioShape.Proximity` and pass `Origin: spiritPosition` on the channel result - the listener then hears from that point with falloff, no separate listener-origin call needed. See [Channels: Spatial channels](Mod-Integration-Channels#spatial-channels-origin).

---

## Swooper / invisibility

**Behaviour:** while a role is invisible (swooped, vanished, phased), they cannot be heard.

**Primitive:** [Gate](Mod-Integration-Gate). Fold every "invisible" modifier into one check.

```csharp
PerfectCommsApi.RegisterHostOption(Mod,
    new VoiceHostOption("MuteHidden", "<color=#8E7CC3><b>Hidden Roles</b></color>: Mute While Hidden", true));

PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
{
    if (!ctx.GetOption("MuteHidden")) return VoiceRuleResult.Pass;
    bool hidden =
        ctx.Player.GetModifier<SwoopModifier>() != null ||
        ctx.Player.GetModifier<VanishModifier>() != null;
    return hidden ? VoiceRuleResult.Mute("Hidden") : VoiceRuleResult.Pass;
});
```

---

## System-wide effect (Hacker jam)

**Behaviour:** while a system effect is active, everyone is muted for its duration.

**Primitive:** global gate - cheaper than a per-player rule.

```csharp
PerfectCommsApi.RegisterGlobalGate(Mod, VoicePhaseKind.Meeting, () => MySystems.JamActive, "Jammed");
PerfectCommsApi.RegisterGlobalGate(Mod, VoicePhaseKind.Tasks,   () => MySystems.JamActive, "Jammed");
```

---

## Notes carried by every example

- The role/modifier checks (`GetModifier<T>`, `GetRole<T>`, `MyRoles.*`) are **your** mod's API - you reference your own types directly. Perfect Comms never sees them.
- Keep callbacks cheap; they run ~20x/second per player.
- Return `Pass` / `null` whenever your role does not apply.
- Everything derives from local state, so all clients converge with no networking (host option *values* are synced for you).

## Next

- **[API Reference](Mod-Integration-API-Reference)** - every type and method.
- **[Gate](Mod-Integration-Gate)** · **[Channels](Mod-Integration-Channels)** · **[Listener Origin](Mod-Integration-Listener-Origin)** · **[Host Options & Tabs](Mod-Integration-Host-Options)**
