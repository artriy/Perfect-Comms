# Channels: Private & Team Radio

A channel lets two (or more) players hear each other **beyond proximity** - team comms, faction radio, or a private one-to-one link - with a chosen audio shape (full-volume radio, muffled, or proximity-shaped).

← Back to **[Mod Integration](Mod-Integration)**

---

## How it works

You return the channel a player belongs to this frame. **Two players that resolve the same key hear each other.** Keys are namespaced by your mod id internally, so they never collide with other mods or the built-in channels.

```csharp
PerfectCommsApi.RegisterVoiceChannel("com.me.mymod", ctx =>
{
    // Everyone on the same team shares one key -> they hear each other.
    byte team = MyRoles.TeamOf(ctx.Player);
    return team != byte.MaxValue
        ? new VoiceChannelResult($"team:{team}")
        : null;   // not on a channel this frame
});
```

Return `null` when the player is not on any channel.

---

## `VoiceChannelResult`

```csharp
new VoiceChannelResult(
    Key,                         // string: same key on two players = they hear each other
    TwoWay = true,               // false = directional (see below)
    Shape  = VoiceAudioShape.Radio,
    Volume = 1f);
```

### Audio shape

| `VoiceAudioShape` | Sound |
| :--- | :--- |
| `Radio` *(default)* | Full volume, no falloff - classic team comms. |
| `Muffle` | Heard, but low-pass filtered. Good for "muffled link" effects. |
| `Proximity` | Proximity-shaped - falls off with distance. By default from the speaker's body; set `Origin` to hear from a fixed point. |

`Volume` (0-1) scales the channel audio.

### Spatial channels (`Origin`)

A `Proximity` channel can carry an `Origin` point. When set, the listener hears the speaker **as if the audio came from `Origin`**, with normal distance falloff - not from the speaker's body. This is how a Medium seance is heard from the spirit's location: stand near the spirit point and the ghost is loud; walk away and it fades.

```csharp
PerfectCommsApi.RegisterVoiceChannel("com.me.mymod", ctx =>
    MyRoles.SeancePoint(ctx.Player) is Vector2 spirit
        ? new VoiceChannelResult("seance", Shape: VoiceAudioShape.Proximity, Origin: spirit)
        : null);
```

`Origin` is only used when `Shape` is `Proximity`. Spatial routing needs a listener position, so it applies during the task phase (where players have positions); in meetings the channel falls back to flat audio.

### Directional channels

`TwoWay: true` (default) - both players hear each other.

`TwoWay: false` - a one-directional link. The reverse direction is suppressed unless the listener also shares a two-way link. Use this for "this role hears its target, but the target does not hear back".

---

## Patterns

**Faction radio** (all members hear each other, full volume):

```csharp
PerfectCommsApi.RegisterVoiceChannel("com.me.mymod", ctx =>
    MyRoles.IsCultist(ctx.Player)
        ? new VoiceChannelResult("cult")
        : null);
```

**Private pair** (owner ↔ target, keyed by the owner so only that pair matches):

```csharp
PerfectCommsApi.RegisterVoiceChannel("com.me.mymod", ctx =>
{
    byte ownerId = MyRoles.LinkOwnerOf(ctx.Player); // owner for both members of the pair
    return ownerId != byte.MaxValue
        ? new VoiceChannelResult($"link:{ownerId}")
        : null;
});
```

**Muffled "swallowed" link at reduced volume:**

```csharp
PerfectCommsApi.RegisterVoiceChannel("com.me.mymod", ctx =>
    MyRoles.SwallowedBy(ctx.Player) is byte predator
        ? new VoiceChannelResult($"belly:{predator}", Shape: VoiceAudioShape.Muffle, Volume: 0.7f)
        : null);
```

**One-way overhear** (a spy hears a room's occupants, not vice versa):

```csharp
PerfectCommsApi.RegisterVoiceChannel("com.me.mymod", ctx =>
    MyRoles.SpyChannelFor(ctx.Player) is string room
        ? new VoiceChannelResult(room, TwoWay: false)
        : null);
```

---

## Notes

- The channel route is evaluated **after** mutes - a player muted by a [Gate](Mod-Integration-Gate) stays muted even if they share a channel.
- Channels apply in both **meeting** and **task** phases (mirroring the built-in team radio), before built-in radio resolves.
- Like every primitive, channel resolution is **local and fail-closed** - a throwing callback yields `null` (no channel).

---

## Next

- **[Listener Origin](Mod-Integration-Listener-Origin)** - relocate where a player hears from.
- **[Host Options & Tabs](Mod-Integration-Host-Options)** - gate a channel behind a host toggle.
