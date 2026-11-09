---
description: "Create multiple, receive-only, spatial and listener-specific voice routes."
---

# Channels and pair routing

Channels add named routes beyond proximity, managed Team Radio puts them on Perfect Comms' own radio selector, and pair rules route or hide one speaker for one listener.

## Register channel memberships

```csharp
PerfectCommsApi.RegisterVoiceChannel("com.me.mymod", ctx =>
    MyRoles.FactionId(ctx.Player) is byte faction
        ? new VoiceChannelResult($"faction:{faction}")
        : null);
```

```csharp
new VoiceChannelResult(
    Key,                                  // required, non-empty
    TwoWay: true,                         // false = receive-only
    Shape: VoiceAudioShape.Radio,
    Volume: 1f,                           // clamped to 0..1
    Origin: null);
```

- Keys are namespaced by `modId`; a key matches only the same key from the same mod id.
- Every non-null result with a non-empty key is kept, so one player can hold several memberships. When several produce routes for one speaker, the loudest wins.
- Return `null` for no membership. Invalid or empty results are ignored without blocking other callbacks.

## Transmit and receive direction

The listener hears a speaker through a channel when both hold the same key and the speaker's membership has `TwoWay: true`. `TwoWay: false` is receive-only.

| Speaker membership | Listener membership | Result |
| :---: | :---: | :--- |
| `true` | `true` | Both directions work. |
| `true` | `false` | Receive-only listener hears speaker. |
| `false` | `true` | Listener does not hear this speaker. |
| `false` | `false` | No route. |

This supports mod-owned push-to-radio:

```csharp
PerfectCommsApi.RegisterVoiceChannel(Mod, ctx =>
{
    if (!MyRoles.IsVampire(ctx.Player))
        return null;

    return new VoiceChannelResult(
        "vampires",
        TwoWay: MyRadio.IsHoldingTransmit(ctx.Player),
        Shape: VoiceAudioShape.Radio);
});
```

Your mod supplies the keybind, button, hold-state RPC, and membership, already synced on every client.

## Managed Team Radio

For role radio without your own input or networking:

```csharp
PerfectCommsApi.RegisterManagedRadioChannel(Mod, ctx =>
    MyRoles.LoverPairId(ctx.Player) is { } pair
        ? new VoiceManagedRadioChannelResult(
            $"lovers:{pair}",
            "Lovers",
            "L")
        : null);
```

- Each local membership becomes a selector choice after the built-in channels while **Team Radio** is on and the local player is not voice-dead.
- Perfect Comms owns the keyboard/touch input, opens capture while held (even in Push To Talk), and sends the selected key.
- While held, only living players holding the same key hear the speaker; dead players and spectators hear nothing. This runs before pair rules and general channels, so they cannot reopen it. A key counts only while the speaker still holds it.
- Meeting and Exile need **Team Radio - Usable in Meetings**; Tasks follows **Team Radio - Usable in Tasks Phase**. Not available in Lobby. Speaker mutes, global gates, and pair mutes still apply.
- Keys: not empty or whitespace, at most 160 characters, no control characters. Invalid keys and repeated keys from one mod are ignored.
- Labels and badges: line breaks and tabs become spaces, cut to 48 and 16 characters. Empty label falls back to the key, empty badge to the label.

Use `RegisterVoiceChannel` for simultaneous routes, custom shape/volume/origin, or mod-owned transmit state. Use `RegisterManagedRadioChannel` for one selectable, exclusive radio channel.

## Shapes, volume, and origin

The speaker's transmitting membership supplies shape, volume, and origin.

| Shape | Effect |
| :--- | :--- |
| `Radio` | Flat, no distance falloff. |
| `Muffle` | Flat, through the low-pass effect. |
| `Proximity` | Host falloff and pan from `Origin`, or from the speaker's body when `Origin` is absent or non-finite. No walls or sight limits. Works in every phase when a listener position exists. |

```csharp
return new VoiceChannelResult(
    "seance",
    Shape: VoiceAudioShape.Proximity,
    Volume: 0.8f,
    Origin: MyRoles.SpiritPosition(ctx.Player));
```

Non-finite volume is silent, an invalid shape falls back to Radio, and a non-finite origin falls back to the speaker.

## Listener-speaker pair rules

Use a pair rule when policy depends on both the local listener and one speaker:

```csharp
PerfectCommsApi.RegisterVoicePairRule(Mod, ctx =>
{
    if (!MyMedium.CanHearSpirit(ctx.Listener, ctx.Speaker))
        return VoicePairResult.Pass;

    return VoicePairResult.Route(
        VoicePairRouteShape.Ghost,
        volume: ctx.GetNumberOption("MediumVolume"),
        speakerOrigin: MyMedium.SpiritPosition(ctx.Speaker),
        listenerOrigin: MyMedium.HearingPosition(ctx.Listener),
        reason: "Medium spirit voice");
});
```

`VoicePairContext` exposes `Listener`, `Speaker`, phase, effective dead flags for both, and the option accessors.

| Pair result | Effect |
| :--- | :--- |
| `Pass` | No opinion. |
| `Mute(reason)` | Hide this speaker from this listener. Wins immediately. |
| `Muffle(reason)` | Muffle whichever route is selected for this pair. |
| `Route(...)` | Replace routing for this pair with Proximity, Radio, or Ghost output. |

- The first valid Route wins; later rules are still checked for Mute or Muffle.
- `Radio` is flat. `Proximity` and `Ghost` use host falloff and pan (`Ghost` adds the ghost filter), without walls or sight limits.
- Omitted `speakerOrigin` uses the speaker's position; omitted `listenerOrigin` uses the local hearing point (a registered listener origin during Tasks).
- A Route with an undefined shape, non-finite volume, or non-finite origin is ignored, as are `null` and exceptions.
- During Tasks a pair route runs before **Only Ghosts can Talk/Hear** and **Comms Sabotage Disables Voice**, so Medium-style exceptions work, and can route a dead speaker to a living listener. In Meeting and Exile it cannot bypass **Only Ghosts can Talk/Hear** or death separation.

Exact authority rules (impostor meeting radio, private audiences): [Listener-speaker pair rules](/api/reference#listener-speaker-pair-rules).

## Routing precedence

- **Tasks**, per speaker: ghost-only and impostor-chat audiences, **Meetings/Lobby Only**, speaker mute, built-in Team Radio, managed Team Radio, pair route, **Only Ghosts can Talk/Hear** and Comms sabotage (skipped by a listener origin with `BypassTaskVoiceGates`), general channel, then normal ghost, vent, and proximity hearing.
- **Meeting and Exile**: **Only Ghosts can Talk/Hear** for a living listener, speaker mute or meeting floor, death separation, built-in Team Radio or impostor meeting radio, managed Team Radio, pair route, general channel, then normal meeting hearing.
- **Lobby**: pair route, general channel, then normal proximity.
- External/global speaker mute is applied after selection and always wins.
- A throwing channel callback adds no membership; a throwing pair callback is `Pass`.
- EndGame does not reapply per-player channel, pair, mute, or muffle state.

## Current status / limitations

**Currently broken:** none.

- Your mod owns channel membership, pairings, spirit positions, and role RPCs. General channels also need your own transmit state, UI, and input.
- Callbacks coordinate cooperative clients; they are not hostile-client enforcement.
