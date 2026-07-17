# Mod Integration (API 1.1 quickstart)

Perfect Comms API 1.1 lets a role mod add voice policy without forking Perfect Comms. Reference `PerfectComms.dll` at build time, treat it as a soft runtime dependency, and register only after plugin id `com.edgetel.perfectcomms` is present.

> Full guide: <https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration>

## Safe soft-dependency setup

Keep every `PerfectComms.Api` reference inside a lazy bridge. The entry point that checks plugin presence must not expose Perfect Comms types in fields, attributes beyond the literal dependency id, signatures, base types, or static initializers.

```csharp
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

        PerfectCommsBridge.Register();
    }
}
```

```csharp
using System.Runtime.CompilerServices;
using PerfectComms.Api;

internal static class PerfectCommsBridge
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

Register once. Call `Unregister(Mod)` before a supported dynamic unload or reload.

## Compatibility and capability checks

All original API 1.0/early-1.1 enum values, positional record constructors, and registration signatures remain available. An existing integration using gates, channels, listener callbacks, host bool/enum options, tabs, or overlay privacy can run unchanged. The completed API 1.1 runtime fixes the old routing gaps behind those same calls.

The compatibility tests mirror the unchanged name-only reflection bridge in [TownOfUsMegaChujoweExtension](https://github.com/HekerB/TownOfUsMegaChujoweExtension/blob/55e75669420c9cd093b16e5e0a73c08c85ef924e/TouMegaChujoweExtension/Modules/PerfectCommsIntegration.cs), including its exact types, constructors, members, enum ordinals, and eight unique registration method names.

`PerfectCommsApi.ApiVersion` remains the compile-time constant `"1.1"`; a consuming compiler embeds it, so it cannot identify the installed runtime. Current builds expose:

```csharp
string runtime = PerfectCommsApi.RuntimeApiVersion;
VoiceApiCapability available = PerfectCommsApi.Capabilities;

bool hasPairRouting = PerfectCommsApi.Supports(
    VoiceApiCapability.PairRouting |
    VoiceApiCapability.ContextualListeners);
```

`Supports` requires every requested flag and returns `false` for `None`. If a bridge must also run against an older assembly that called itself API 1.1, probe the property by reflection before entering code that references a new type or method. Otherwise state a minimum Perfect Comms release.

## Implemented primitives

| Primitive | What it does |
| :--- | :--- |
| Gate and global gate | Mute or per-speaker muffle in Lobby, Tasks, Meeting, and Exile, including voice-dead speakers; global gates are enforced on transmit and receive. |
| Player traits | Add impostor-equivalent voice, voice-dead, or spectator classification. |
| Pair rule | Make a listener-specific `Mute`, `Muffle`, or explicit Proximity/Radio/Ghost route. |
| Channel | Retain multiple memberships per player; `TwoWay: false` is a receive-only endpoint. |
| Listener origin/filter | Replace or augment task hearing, or muffle all incoming audio; contextual forms receive phase and host options. |
| Phase observer | Observe API phase changes before the new phase's player callbacks run. |
| Host options/tab | Add synced bools, enums, and stepped numbers, including conditional row visibility. |
| Overlay privacy | Hide, dim, or safely alias identity-bearing voice UI. |

Every callback context that supports settings exposes bare-key accessors scoped to its `modId`: `GetOption`, `GetEnumOption`, and `GetNumberOption`.

## Important routing details

- Gate `Mute` wins over gate `Muffle`. A working `Muffle` applies only to that speaker while preserving the selected route.
- Pair `Mute` wins; otherwise the first valid pair `Route` wins and any pair `Muffle` is applied afterward. A pair route intentionally replaces ordinary routing for that listener/speaker pair.
- Speaker/global mutes and the Tasks-only `OnlyMeetingOrLobby` policy remain authoritative. During Tasks, an explicit pair route runs before `OnlyGhostsCanTalk` and Comms-sabotage blocking so Medium-style exceptions are possible; during Meeting/Exile those host restrictions run first. Channels never bypass those host restrictions.
- Every non-empty channel result is retained. A target can transmit only through a matching membership with `TwoWay: true`; `false` can receive the same key but cannot transmit it. If several shared memberships route the same speaker, the loudest valid result is used.
- `VoiceAudioShape.Proximity` uses `Origin` when supplied and otherwise falls back to the speaker's resolved body position. It spatializes in Lobby, Tasks, Meeting, and Exile whenever a listener position exists.
- `VoiceListenerResult.LightRadius == -1` inherits the local player's resolved light radius. `0` disables vision-radius limiting at the override. Other negative or non-finite inputs normalize to inheritance.
- Legacy listener delegates still work. Use `RegisterContextualListenerOrigin` and `RegisterContextualListenerFilter` when the effect needs phase or option access.
- Audio callback failures are neutral. Overlay viewer failures become `HideAll`; overlay speaker failures become `HideSource`.
- EndGame is a fresh global results-screen call. Per-player API mute/muffle/channel/pair state from the previous phase is deliberately not reapplied after the game world disappears.

The full TOU-Mira parity matrix and copyable recipes are in [Examples](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-Examples). Exact signatures and fallbacks are in the [API Reference](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-API-Reference).

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- Perfect Comms synchronizes registered host-option values. It does not discover or synchronize your roles, modifiers, channel membership, phase bookkeeping, temporary permissions, aliases, custom buttons, keybinds, or role RPCs. Your mod owns that gameplay state, UI, and netcode; callbacks only project already-available state into voice behavior.
- Host-option snapshots and locally evaluated callbacks are cooperative lobby policy, not hostile-client security. A modified client can ignore or forge local behavior.
