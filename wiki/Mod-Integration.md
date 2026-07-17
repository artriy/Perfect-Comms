# Mod Integration

Perfect Comms API 1.1 exposes the supported `PerfectComms.Api` surface for role voice, private routes, host settings, and concealment-safe voice UI. Your mod references `PerfectComms.dll` as a soft dependency and owns the gameplay state that its callbacks read; Perfect Comms never references your mod.

---

## Safe setup

Reference `PerfectComms.dll` for compilation, but do not redistribute it with your mod. Keep all API references inside a lazy, non-inlined bridge that is entered only after the literal plugin id is present:

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;

[BepInPlugin("com.me.mymod", "My Mod", "1.0.0")]
[BepInDependency(
    "com.edgetel.perfectcomms",
    BepInDependency.DependencyFlags.SoftDependency)]
public sealed class MyModPlugin : BasePlugin
{
    public override void Load()
    {
        if (!IL2CPPChainloader.Instance.Plugins.ContainsKey(
                "com.edgetel.perfectcomms"))
            return;

        PerfectCommsVoiceIntegration.Register();
    }
}
```

```csharp
using System.Runtime.CompilerServices;
using PerfectComms.Api;

internal static class PerfectCommsVoiceIntegration
{
    private const string Mod = "com.me.mymod";

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Register()
    {
        PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
            ctx.Phase == VoicePhaseKind.Meeting &&
            MyRoles.IsGagged(ctx.Player)
                ? VoiceRuleResult.Mute("Gagged")
                : VoiceRuleResult.Pass);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Unregister()
        => PerfectCommsApi.Unregister(Mod);
}
```

A soft dependency controls load order; it does not make an eagerly resolved API type safe when Perfect Comms is absent. Keep API types out of the plugin class's fields, method signatures, base types, interfaces, and static initializers.

---

## Existing integrations remain compatible

The completed API 1.1 keeps every original enum value, positional record constructor, and reflected registration signature. Existing compiled or source integrations using these methods do not need rewrites:

- `RegisterVoiceRule`, `RegisterGlobalGate`, and `RegisterVoiceChannel`
- `RegisterListenerOrigin` and `RegisterListenerFilter`
- bool/enum host options and `RegisterModTab`
- overlay viewer/speaker rules and `Unregister`

This contract is regression-tested against the unchanged name-only reflection bridge in [TownOfUsMegaChujoweExtension](https://github.com/HekerB/TownOfUsMegaChujoweExtension/blob/55e75669420c9cd093b16e5e0a73c08c85ef924e/TouMegaChujoweExtension/Modules/PerfectCommsIntegration.cs). Its expected types, constructors, members, enum ordinals, and eight method names remain unique and callable.

The same calls now receive the completed behavior: per-speaker muffle is audible, gates include Lobby and voice-dead speakers, global gates are receiver-enforced, all channel memberships are retained, receive-only channels work, Proximity falls back to the speaker position, and `LightRadius: -1` inherits the local light radius.

`PerfectCommsApi.ApiVersion` stays `"1.1"` for compatibility, but it is a compile-time constant. Use the runtime surface for current capability checks:

```csharp
bool ready = PerfectCommsApi.Supports(
    VoiceApiCapability.PairRouting |
    VoiceApiCapability.ContextualListeners |
    VoiceApiCapability.NumericHostOptions);
```

`RuntimeApiVersion`, `Capabilities`, and `Supports(...)` distinguish the completed runtime when code is already running against it. If you support an older assembly that also reported 1.1, reflect for the property before entering a method that references new API types, or declare a minimum Perfect Comms release.

---

## API building blocks

| Area | Use it for | Guide |
| :--- | :--- | :--- |
| Gate, global gate, and speaker muffle | Speaker-wide restrictions in every API phase | [Gate](Mod-Integration-Gate) |
| Player traits and pair rules | Impostor-equivalent voice, voice-dead classification, directional/private Medium-style routing | [Gate](Mod-Integration-Gate#player-traits) and [Channels](Mod-Integration-Channels#listener-speaker-pair-rules) |
| Multiple/directional channels | Team, pair, radio, muffle, and spatial routes; receive-only endpoints | [Channels](Mod-Integration-Channels) |
| Listener origin and filter | Replace/add a task hearing point or muffle everything the local player hears | [Listener Origin & Filter](Mod-Integration-Listener-Origin) |
| Phase observer | Update integration-owned derived state exactly at API phase changes | [Examples](Mod-Integration-Examples#phase-owned-bookkeeping) |
| Host options and tab | Synced toggles, enums, stepped numbers, conditional rows | [Host Options & Tabs](Mod-Integration-Host-Options) |
| Overlay privacy | Hide, dim, or safely alias identity-bearing voice presentation | [Overlay Privacy](Mod-Integration-Overlay-Privacy) |

The [Examples](Mod-Integration-Examples) page includes a 17-row TOU-Mira parity matrix covering every built-in role voice option.

---

## Runtime contract

- Audio callbacks run locally at voice-snapshot cadence, roughly 20 times per second per applicable player. Overlay callbacks run at most once per rendered frame. Phase observers run once per observed API phase transition.
- Keep callbacks cheap, allocation-light, deterministic, and throw-free. Audio failures are neutral; identity-bearing overlay failures are private.
- Return `Pass`, `null`, or `false` whenever a primitive has no opinion.
- Values returned from `GetOption`, `GetEnumOption`, and `GetNumberOption` are scoped automatically to the callback's `modId`.
- Registrations accumulate except the first exact mod-tab id. Register once and call `Unregister(modId)` before a supported reload.
- The callback collection being evaluated is snapshotted. A callback may safely register or unregister without breaking that pass; do normal cross-primitive setup outside callbacks instead of relying on same-frame registration timing.
- Avoid relying on ordering between different mods. Within the route types that need precedence, `Mute` is restrictive and first valid routes/origins win as documented in the API reference.
- EndGame is a fresh global results-screen call; stale per-player API state from Tasks/Meeting is not reapplied.

---

## Role-state ownership

Perfect Comms synchronizes registered host-option values only. Your mod still owns:

- role and modifier discovery;
- current targets, partners, controllers, and spirit positions;
- cross-phase persistence such as “blackmailed next round”;
- temporary permissions such as a Jailor allowing voice;
- custom radio hold state, keybinds, buttons, and RPCs;
- disguise/alias state used by overlay callbacks.

Phase observers can help maintain derived integration state, but they do not create authoritative gameplay state or networking.

---

## Next

- Copy role-oriented implementations from **[Examples](Mod-Integration-Examples)**.
- Check every member and fallback in **[API Reference](Mod-Integration-API-Reference)**.
- Use **[Gate](Mod-Integration-Gate)**, **[Channels](Mod-Integration-Channels)**, **[Listener Origin](Mod-Integration-Listener-Origin)**, **[Host Options](Mod-Integration-Host-Options)**, and **[Overlay Privacy](Mod-Integration-Overlay-Privacy)** for focused details.

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- **Gameplay/UI/netcode remain mod-owned.** The API projects state into voice routing; it does not add role abilities, buttons, keybinds, or role RPCs for you.
- **This is not hostile-client security.** Host-option snapshots and local callbacks coordinate cooperative clients. A modified client can ignore or forge its local behavior.
