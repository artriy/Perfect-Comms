# Channels & Pair Routing

Channels add named routes beyond ordinary proximity. API 1.1 retains every valid membership, supports receive-only endpoints, and can spatialize a Proximity channel from either an explicit origin or the speaker's resolved body position. Pair rules handle listener-specific privacy and explicit Medium-style routes.

Back to **[Mod Integration](Mod-Integration)**

---

## Register channel memberships

```csharp
PerfectCommsApi.RegisterVoiceChannel("com.me.mymod", ctx =>
    MyRoles.FactionId(ctx.Player) is byte faction
        ? new VoiceChannelResult($"faction:{faction}")
        : null);
```

`VoiceChannelResult` keeps its original positional contract:

```csharp
new VoiceChannelResult(
    Key,                                  // required, non-empty
    TwoWay: true,                         // false = receive-only
    Shape: VoiceAudioShape.Radio,
    Volume: 1f,                           // clamped to 0..1
    Origin: null);
```

Keys are namespaced by the registration's `modId`. A key matches only another membership with the same exact mod id and key.

Every non-null result with a non-empty key is retained across all registered callbacks. One player can therefore belong to several team, pair, or role channels at once. If several shared transmitting memberships produce routes for the same speaker, Perfect Comms keeps the loudest valid result:

```csharp
PerfectCommsApi.RegisterVoiceChannel(Mod, ctx =>
    MyRoles.FactionId(ctx.Player) is byte faction
        ? new VoiceChannelResult($"faction:{faction}")
        : null);

PerfectCommsApi.RegisterVoiceChannel(Mod, ctx =>
    MyRoles.LoverPairId(ctx.Player) is byte pair
        ? new VoiceChannelResult($"lovers:{pair}")
        : null);
```

Return `null` for no membership. Invalid/empty memberships are ignored rather than preventing another callback from contributing one.

---

## Transmit and receive direction

For the local listener to hear a target through a channel:

1. both players must hold the same namespaced key; and
2. the target's matching membership must have `TwoWay: true`.

`TwoWay: false` is a receive-only endpoint. It can hear a matching transmitting member but cannot itself be heard through that membership.

| Speaker membership | Listener membership | Result |
| :---: | :---: | :--- |
| `true` | `true` | Listener hears speaker; the reverse direction also works. |
| `true` | `false` | Receive-only listener hears speaker. |
| `false` | `true` | Listener does not hear this speaker; the reverse direction can work. |
| `false` | `false` | No route in either direction. |

This supports a mod-owned push-to-radio state without changing the legacy result type:

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

Perfect Comms does not create the keybind, button, hold-state RPC, or authoritative role membership. Each client must already have that synchronized mod state.

---

## Shapes, volume, and origin

The target's transmitting membership supplies the route's shape, volume, and origin.

| Shape | Effect |
| :--- | :--- |
| `Radio` | Flat audio without distance falloff. |
| `Muffle` | Flat channel audio through the low-pass effect. |
| `Proximity` | Normal host distance/falloff and pan from `Origin`, or from the speaker's resolved body position when `Origin` is absent or non-finite. |

`Proximity` spatializes in Lobby, Tasks, Meeting, and Exile whenever a listener position is available. It no longer needs an explicit origin:

```csharp
PerfectCommsApi.RegisterVoiceChannel(Mod, ctx =>
    MyRoles.IsSpirit(ctx.Player)
        ? new VoiceChannelResult(
            "seance",
            Shape: VoiceAudioShape.Proximity)
        : null);
```

Use `Origin` when the voice belongs at a spirit, camera, puppet, or other mod-owned world position:

```csharp
return new VoiceChannelResult(
    "seance",
    Shape: VoiceAudioShape.Proximity,
    Volume: 0.8f,
    Origin: MyRoles.SpiritPosition(ctx.Player));
```

Volume is clamped to `0..1`. A non-finite volume becomes silent, an invalid shape falls back to Radio, and a non-finite origin falls back to the speaker.

---

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

`VoicePairContext` contains the local `Listener`, target `Speaker`, exact phase, effective dead flags for both, and the three scoped host-option accessors.

| Pair result | Effect |
| :--- | :--- |
| `Pass` | No pair-specific opinion. |
| `Mute(reason)` | Hide this speaker from this listener. Mute wins immediately. |
| `Muffle(reason)` | Muffle whichever route is ultimately selected for this pair. |
| `Route(...)` | Replace ordinary routing for this pair with Proximity, Radio, or Ghost output. |

The first valid Route is retained, but later rules are still checked for a restrictive Mute or Muffle. `Radio` is flat. `Proximity` and `Ghost` use normal host falloff/pan. Omitted origins fall back to resolved player positions; volume is clamped. Invalid/non-finite results and exceptions are neutral.

Pair routes are considered early enough in Lobby, Tasks, Meeting, and Exile routing to implement private role paths instead of ordinary routing. Speaker/global mutes and the Tasks-only `OnlyMeetingOrLobby` policy remain authoritative. During Tasks, an explicit pair route runs before `OnlyGhostsCanTalk` and Comms-sabotage blocking so Medium-style role exceptions are possible. During Meeting/Exile, those host restrictions run before the pair route. Channels run below those host restrictions in every phase.

EndGame is a fresh global results-screen call after player objects disappear. Transition-retained per-player channel/pair/mute/muffle state is not reapplied there.

---

## Routing precedence and failures

- Built-in room, only-ghosts, and phase policy follow the explicit order above; a channel does not grant a universal bypass.
- External/global speaker mute is applied after channel or pair selection and always wins.
- Multiple memberships are considered without depending on registration order between different mods.
- A throwing channel callback contributes no membership for that evaluation.
- A throwing pair callback is `Pass`.
- Register once. `Unregister(modId)` removes all memberships/rules registered by that id.

---

## Next

- **[Gate](Mod-Integration-Gate)** - authoritative speaker/global restrictions and player traits.
- **[Listener Origin & Filter](Mod-Integration-Listener-Origin)** - move the local hearing point.
- **[Examples](Mod-Integration-Examples)** - Medium, Vampire, and Lovers recipes.

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- Perfect Comms synchronizes registered host-option values only. Your mod owns channel membership, pairings, spirit positions, transmit-hold state, UI, input, and role RPCs.
- Channels and pair callbacks coordinate cooperative clients; they are not hostile-client authentication or enforcement.
