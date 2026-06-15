# Listener Origin

This primitive relocates **where the local player hears from**. Normally you hear proximity audio from your own body; a listener-origin override makes you hear from somewhere else - another player, a camera, a fixed point.

← Back to **[Mod Integration](Mod-Integration)**

---

## How it works

Register a callback for the **local player only**. Return where they should hear from this frame, or `null` for normal hearing:

```csharp
PerfectCommsApi.RegisterListenerOrigin("com.me.mymod", local =>
{
    // `local` is the local PlayerControl.
    if (MyRoles.IsSpiritWalking(local) && MyRoles.SpiritTarget(local) is PlayerControl ghost)
        return new VoiceListenerResult(
            ghost.transform.position,
            LightRadius: -1f,                 // -1 = inherit the local player's light radius
            VoiceListenerMode.Additive);

    return null;                              // hear normally
});
```

---

## `VoiceListenerResult`

```csharp
new VoiceListenerResult(
    Origin,        // Vector2 world position to hear from
    LightRadius,   // sight/vision radius at that origin; -1 inherits local
    Mode);         // Replace or Additive
```

### Modes

| `VoiceListenerMode` | Behaviour |
| :--- | :--- |
| `Replace` | Hear **entirely** from `Origin`. Your own body is silent as a hearing source. Use for "you are fully somewhere else" (possession, remote control). |
| `Additive` | Hear from **both** your own body and `Origin`; per speaker the louder of the two wins. Use for "you also hear over there" (spirit link, eavesdrop). |

---

## Patterns

**Possession - hear entirely as the victim:**

```csharp
PerfectCommsApi.RegisterListenerOrigin("com.me.mymod", local =>
    MyRoles.PossessedVictim(local) is PlayerControl victim
        ? new VoiceListenerResult(victim.transform.position, -1f, VoiceListenerMode.Replace)
        : null);
```

**Spirit link - hear your own surroundings AND a linked ghost's:**

```csharp
PerfectCommsApi.RegisterListenerOrigin("com.me.mymod", local =>
    MyRoles.LinkedSpiritPosition(local) is Vector2 pos
        ? new VoiceListenerResult(pos, -1f, VoiceListenerMode.Additive)
        : null);
```

---

## Notes

- **Local player only** - this controls *your* hearing, not how others hear you. To change how a player is *heard*, use a [Gate](Mod-Integration-Gate) or [Channel](Mod-Integration-Channels).
- **Built-in control hearing wins.** If a supported mod's built-in control ability (e.g. an existing puppeteer/parasite) is already relocating your hearing this frame, your override stands aside. Otherwise it applies.
- Reuses the same engine path as built-in control hearing, so spatial falloff, sight, and occlusion all work from the new origin exactly as they would from your body.
- Fail-closed: a throwing callback yields `null` (normal hearing).

---

## Related: Listener filter (muffle what you hear)

Listener-origin changes *where* you hear from. A listener **filter** changes *how* you hear: while active, it muffles ALL incoming audio for the local player (a low-pass on what you hear, not on what you say). Use it for blinded / flashed / hypnotised hearing effects.

```csharp
PerfectCommsApi.RegisterListenerFilter("com.me.mymod", local => MyRoles.IsBlinded(local));
```

While the predicate returns true, everything the local player hears is muffled. No netcode; fail-closed (a throw = no muffle). This is the listener-side counterpart to a [Gate](Mod-Integration-Gate)'s `Muffle`, which muffles a specific speaker rather than all incoming audio.

---

## Next

- **[Host Options & Tabs](Mod-Integration-Host-Options)** - make the effect host-configurable.
- **[API Reference](Mod-Integration-API-Reference)** - full signatures.
