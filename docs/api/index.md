---
description: "Add voice rules to your Among Us mod with the PerfectComms.Api package: setup, capability checks and the runtime contract."
---

# Mod integration

The `PerfectComms.Api` surface (API 1.2) lets your mod drive role voice, managed private radio, persistent host settings, concealment-safe voice UI, and sight-obscuration effects. Your mod owns the gameplay state its callbacks read; Perfect Comms never references your mod.

## Safe setup

Reference package `4.1.7.1` targets API 1.2 and works with Perfect Comms 4.1.7 and later:

```xml
<ItemGroup>
  <PackageReference Include="PerfectComms.Api"
                    Version="4.1.7.1"
                    PrivateAssets="all" />
</ItemGroup>
```

The package is reference-only: it does not install Perfect Comms or copy `PerfectComms.dll` to your output. Keep your normal BepInEx and Among Us references, and require players to install Perfect Comms separately; do not redistribute it.

Declare a soft dependency and keep every API reference inside a lazy, non-inlined bridge entered only after the plugin id is present:

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

A soft dependency only controls load order. Keep API types out of the plugin class's fields, signatures, base types, interfaces, and static initializers, and check the literal id rather than `PerfectCommsApi.PluginId`.

## Compatibility and capability checks

API 1.2 keeps every API 1.0 and 1.1 enum value, positional record constructor, and registration signature, so existing integrations need no rewrites. API 1.2 only added members; see [Compatibility contract](/api/reference#compatibility-contract) for the list.

`PerfectCommsApi.ApiVersion` is a compile-time constant. Probe the installed runtime instead:

```csharp
bool ready = PerfectCommsApi.Supports(
    VoiceApiCapability.PairRouting |
    VoiceApiCapability.ManagedTeamRadio |
    VoiceApiCapability.PersistentHostOptions |
    VoiceApiCapability.OverlayAppearance |
    VoiceApiCapability.ListenerSightObscuration);
```

`Supports` requires every requested flag and returns `false` for `None`. Perfect Comms 4.1.0 to 4.1.6 return `false` for API 1.2 flags; builds before 4.1.0 have no probe members. To support those builds, check `Supports` (or reflect on pre-4.1.0 builds) before entering a separate non-inlined method that touches API 1.2 members. Otherwise state Perfect Comms 4.1.7 as your minimum. Details: [Runtime version and capabilities](/api/reference#runtime-version-and-capabilities).

## API building blocks

| Area | Use it for | Guide |
| :--- | :--- | :--- |
| Gate, global gate, and speaker muffle | Speaker-wide restrictions in every API phase | [Gate](/api/gate) |
| Player traits and pair rules | Impostor-equivalent voice, voice-dead classification, directional/private Medium-style routing | [Gate](/api/gate#player-traits) and [Channels](/api/channels#listener-speaker-pair-rules) |
| Multiple/directional channels | Team, pair, radio, muffle, and spatial routes; receive-only endpoints | [Channels](/api/channels) |
| Managed Team Radio | Perfect Comms-owned selector, PTT/capture, wire state, labels, and exclusive living-member routing | [Examples](/api/examples#vampire-and-lovers-managed-team-radio) |
| Listener origin and filter | Replace/add a task hearing point, muffle incoming audio, or restrict sight-based hearing while vision is obscured | [Listener Origin & Filter](/api/listener-origin) |
| Phase observer | Update integration-owned derived state exactly at API phase changes | [Examples](/api/examples#phase-owned-bookkeeping) |
| Host options and tab | Persistent local-host toggles/enums/numbers with lobby sync and conditional rows | [Host Options & Tabs](/api/host-options) |
| Overlay privacy and appearance | Hide, dim, or safely alias voice presentation; classify animated custom colors | [Overlay Privacy](/api/overlay-privacy) |

[Examples](/api/examples) includes a TOU-Mira parity matrix covering every built-in role voice option.

### Source-owned TOU-Mira integration

Current Town of Us Mira builds register their full Perfect Comms integration from their own soft-dependency bridge. No claim or cutover call is needed, and Perfect Comms has no TOU-Mira adapter or duplicate settings. Without Perfect Comms, TOU-Mira skips only that registration.

## Runtime contract

- Audio callbacks run locally about 20 times per second per applicable player. Overlay callbacks run at most once per frame. Phase observers run once per API phase transition.
- Keep callbacks cheap, allocation-light, deterministic, and throw-free. Audio failures are neutral; identity-bearing overlay failures are private.
- Return `Pass`, `null`, or `false` when a primitive has no opinion.
- `GetOption`, `GetEnumOption`, and `GetNumberOption` are scoped to the callback's `modId`.
- Registrations accumulate, except that only the first `RegisterModTab` per mod id creates a tab and a repeated host-option key for the same mod id is ignored. Register once; call `Unregister(modId)` before a supported reload.
- Callbacks may register or unregister mid-pass without breaking it, but do cross-primitive setup outside callbacks.
- Do not rely on ordering between mods. `Mute` is restrictive; first valid routes/origins win as described in the [API Reference](/api/reference).
- EndGame is a fresh results-screen call; per-player state from Tasks/Meeting is not reapplied.

## Role-state ownership

Perfect Comms persists and syncs registered host-option values. Your mod owns:

- role and modifier discovery;
- targets, partners, controllers, and spirit positions;
- cross-phase state such as "blackmailed next round";
- temporary permissions such as a Jailor allowing voice;
- radio hold state, keybinds, buttons, and RPCs when using general channels instead of managed Team Radio;
- disguise/alias state read by overlay callbacks.

Phase observers can maintain derived state but do not create authoritative gameplay state or networking.

## Current status / limitations

**Currently broken:** none.

- Managed Team Radio supplies selector, input, capture, and wire path only; it adds no role abilities or role-state RPCs.
- Not hostile-client security: host snapshots and local callbacks coordinate cooperative clients, and a modified client can ignore them.
