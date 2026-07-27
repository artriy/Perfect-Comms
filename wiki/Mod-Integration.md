# Mod Integration

Perfect Comms API 1.2 exposes the supported `PerfectComms.Api` surface for role voice, managed private radio, persistent host settings, concealment-safe voice UI, and temporary sight-obscuration effects. Your mod compiles against the reference-only API package and owns the gameplay state that its callbacks read; Perfect Comms never references your mod.

---

## Safe setup

Add the build-only package version matching the minimum Perfect Comms release your integration supports. API 1.2 starts with Perfect Comms 4.1.7 and its corrected API package revision 4.1.7.1:

```xml
<ItemGroup>
  <PackageReference Include="PerfectComms.Api"
                    Version="4.1.7.1"
                    PrivateAssets="all" />
</ItemGroup>
```

The package contains only the `net6.0` reference assembly and XML documentation. It does not install Perfect Comms, add native payloads, or copy `PerfectComms.dll` to your build output. `PrivateAssets="all"` also keeps it out of your own package dependencies. Keep your mod's normal BepInEx and Among Us game-library references, and require players to install Perfect Comms separately.

Declare Perfect Comms as a soft dependency, but do not redistribute it with your mod. Keep all API references inside a lazy, non-inlined bridge that is entered only after the literal plugin id is present:

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

API 1.2 preserves every API 1.0 and 1.1 enum value, positional record constructor, and registration signature. Existing compiled or source integrations using these methods do not need rewrites:

- `RegisterVoiceRule`, `RegisterGlobalGate`, and `RegisterVoiceChannel`
- `RegisterListenerOrigin` and `RegisterListenerFilter`
- bool/enum host options and `RegisterModTab`
- overlay viewer/speaker rules and `Unregister`

ABI regression tests pin the original public types, constructors, members, enum ordinals, and registration signatures.

The same calls retain the completed routing behavior: per-speaker muffle is audible, gates include Lobby and voice-dead speakers, global gates are receiver-enforced, all channel memberships are retained, receive-only channels work, Proximity falls back to the speaker position, and `LightRadius: -1` inherits the local light radius. API 1.2 adds independent listener sight-obscuration state.

`PerfectCommsApi.ApiVersion` is `"1.2"`, but it is a compile-time constant. Use the runtime surface for current capability checks:

```csharp
bool ready = PerfectCommsApi.Supports(
    VoiceApiCapability.PairRouting |
    VoiceApiCapability.ManagedTeamRadio |
    VoiceApiCapability.PersistentHostOptions |
    VoiceApiCapability.OverlayAppearance |
    VoiceApiCapability.ListenerSightObscuration);
```

`RuntimeApiVersion`, `Capabilities`, and `Supports(...)` identify the installed runtime when code is already running against it. If you support API 1.1, reflect for new capabilities before entering a method that references new API members, or require Perfect Comms 4.1.7 or newer.

---

## API building blocks

| Area | Use it for | Guide |
| :--- | :--- | :--- |
| Gate, global gate, and speaker muffle | Speaker-wide restrictions in every API phase | [Gate](Mod-Integration-Gate) |
| Player traits and pair rules | Impostor-equivalent voice, voice-dead classification, directional/private Medium-style routing | [Gate](Mod-Integration-Gate#player-traits) and [Channels](Mod-Integration-Channels#listener-speaker-pair-rules) |
| Multiple/directional channels | Team, pair, radio, muffle, and spatial routes; receive-only endpoints | [Channels](Mod-Integration-Channels) |
| Managed Team Radio | Perfect Comms-owned selector, PTT/capture, wire state, labels, and exclusive living-member routing | [Examples](Mod-Integration-Examples#vampire-and-lovers-managed-team-radio) |
| Listener origin and filter | Replace/add a task hearing point, muffle incoming audio, or restrict sight-based hearing while vision is obscured | [Listener Origin & Filter](Mod-Integration-Listener-Origin) |
| Phase observer | Update integration-owned derived state exactly at API phase changes | [Examples](Mod-Integration-Examples#phase-owned-bookkeeping) |
| Host options and tab | Persistent local-host toggles/enums/numbers with lobby sync and conditional rows | [Host Options & Tabs](Mod-Integration-Host-Options) |
| Overlay privacy and appearance | Hide, dim, or safely alias voice presentation; classify animated custom colors | [Overlay Privacy](Mod-Integration-Overlay-Privacy) |

The [Examples](Mod-Integration-Examples) page includes a 17-row TOU-Mira parity matrix covering every built-in role voice option.

### Source-owned TOU-Mira integration

Current Town of Us Mira builds register their complete role, listener, managed-radio, host-option, overlay-privacy, and animated-color integration directly from TOU-Mira's soft-dependency bridge.

No claim or cutover call is required. Perfect Comms contains no TOU-Mira reflection adapter and no duplicate TOU-Mira settings. If Perfect Comms is absent, TOU-Mira skips only the optional registration; its gameplay, Jailor UI, and role RPCs remain source-owned and continue to work.

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

Perfect Comms persists and synchronizes registered host-option values. Your mod still owns:

- role and modifier discovery;
- current targets, partners, controllers, and spirit positions;
- cross-phase persistence such as “blackmailed next round”;
- temporary permissions such as a Jailor allowing voice;
- custom radio hold state, keybinds, buttons, and RPCs when using general channels instead of managed Team Radio;
- disguise/alias state used by overlay callbacks.

Phase observers can help maintain derived integration state, but they do not create authoritative gameplay state or networking.

---

## Next

- Copy role-oriented implementations from **[Examples](Mod-Integration-Examples)**.
- Check every member and fallback in **[API Reference](Mod-Integration-API-Reference)**.
- Use **[Gate](Mod-Integration-Gate)**, **[Channels](Mod-Integration-Channels)**, **[Listener Origin](Mod-Integration-Listener-Origin)**, **[Host Options](Mod-Integration-Host-Options)**, and **[Overlay Privacy](Mod-Integration-Overlay-Privacy)** for focused details.

## Current status / limitations

**Currently broken:** None of the documented API 1.2 primitives on this page.

- **Gameplay state remains mod-owned.** The API projects role state into voice behavior. Managed Team Radio supplies its selector/input/capture/wire path; it does not add role abilities or role-state RPCs. Other UI, buttons, keybinds, and netcode remain your responsibility.
- **This is not hostile-client security.** Host-option snapshots and local callbacks coordinate cooperative clients. A modified client can ignore or forge its local behavior.
