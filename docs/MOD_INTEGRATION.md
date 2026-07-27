# Mod Integration (API 1.2 quickstart)

Perfect Comms API 1.2 lets a role mod add voice policy without forking Perfect Comms. Compile against the reference-only `PerfectComms.Api` package, treat Perfect Comms as a soft runtime dependency, and register only after plugin id `com.edgetel.perfectcomms` is present.

> Full guide: <https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration>

## Build-time package

Use the package version matching the minimum Perfect Comms release your integration supports. API 1.2 starts with Perfect Comms 4.1.7 and its corrected API package revision 4.1.7.1:

```xml
<ItemGroup>
  <PackageReference Include="PerfectComms.Api"
                    Version="4.1.7.1"
                    PrivateAssets="all" />
</ItemGroup>
```

The package contains only `ref/net6.0/PerfectComms.dll` and its XML documentation. It provides compiler metadata but has no runtime, native, content, or build assets, so `PerfectComms.dll` is not copied to your output. `PrivateAssets="all"` also prevents the build-only reference from becoming a dependency if you package your own mod.

Your project still owns its normal BepInEx and Among Us game-library references. Players must install the matching or newer Perfect Comms mod separately; NuGet does not install a BepInEx plugin.

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

API 1.2 preserves all API 1.0 and 1.1 enum values, positional record constructors, and registration signatures. Existing integrations using gates, channels, listener callbacks, host options, tabs, or overlay privacy can run unchanged.

Compatibility tests pin the original public ABI: type names, constructors, members, enum ordinals, and registration signatures. API 1.2 only adds new listener-filter state and capability flags.

`PerfectCommsApi.ApiVersion` is the compile-time constant `"1.2"`; a consuming compiler embeds it, so it cannot identify the installed runtime. Current builds expose:

```csharp
string runtime = PerfectCommsApi.RuntimeApiVersion;
VoiceApiCapability available = PerfectCommsApi.Capabilities;

bool hasManagedRoleParity = PerfectCommsApi.Supports(
    VoiceApiCapability.PairRouting |
    VoiceApiCapability.ContextualListeners |
    VoiceApiCapability.ManagedTeamRadio |
    VoiceApiCapability.PersistentHostOptions |
    VoiceApiCapability.OverlayAppearance |
    VoiceApiCapability.ListenerSightObscuration);
```

`Supports` requires every requested flag and returns `false` for `None`. If a bridge must also run against API 1.1, probe new capabilities by reflection before entering code that references a new member. Otherwise state Perfect Comms 4.1.7 as the minimum release.

## Implemented primitives

| Primitive | What it does |
| :--- | :--- |
| Gate and global gate | Mute or per-speaker muffle in Lobby, Tasks, Meeting, and Exile, including voice-dead speakers; global gates are enforced on transmit and receive. |
| Player traits | Add impostor-equivalent voice, voice-dead, or spectator classification. |
| Pair rule | Make a listener-specific `Mute`, `Muffle`, or explicit Proximity/Radio/Ghost route. |
| Channel | Retain multiple memberships per player; `TwoWay: false` is a receive-only endpoint. |
| Listener origin/filter | Replace or augment task hearing, muffle all incoming audio, or mark the listener's sight as temporarily obscured; contextual forms receive phase and host options. |
| Phase observer | Observe API phase changes before the new phase's player callbacks run. |
| Host options/tab | Add persistent, lobby-synced bools, enums, and stepped numbers, including conditional row visibility. |
| Managed Team Radio | Add player or pair memberships to Perfect Comms' selector; Perfect Comms owns PTT capture, selected-channel sync, labels, and living non-member privacy. |
| Overlay privacy/appearance | Hide, dim, or safely alias identity-bearing voice UI, and classify custom animated player colors. |

Every callback context that supports settings exposes bare-key accessors scoped to its `modId`: `GetOption`, `GetEnumOption`, and `GetNumberOption`.

### Managed Team Radio

Use `RegisterManagedRadioChannel` instead of a general `RegisterVoiceChannel` when Perfect Comms should own the complete radio control plane:

```csharp
PerfectCommsApi.RegisterManagedRadioChannel(Mod, ctx =>
    !ctx.IsDead && MyRoles.LoverPairId(ctx.Player) is { } pairId
        ? new VoiceManagedRadioChannelResult(
            $"lovers:{pairId}",
            "Lovers",
            "L")
        : null);
```

Eligible channels appear after built-in choices. Holding Perfect Comms' Team Radio control opens capture even in Push To Talk mode, synchronizes the selected namespaced key, applies the radio filter to matching members, and hard-mutes living non-members before permissive pair/general-channel routes. The normal Team Radio master and phase settings remain authoritative. Your mod still owns role membership and pair ids; return current state from the callback.

### Source-mod integrations

The source mod owns its role state, lifecycle, UI, and RPCs. Register Perfect Comms callbacks directly from that source mod's soft-dependency bridge. Registration composes by `modId`, so unrelated mods can register concurrently without claiming a global integration slot.

Perfect Comms no longer contains a TOU-Mira reflection adapter or duplicate TOU-Mira host settings. TOU-Mira registers its own complete integration when Perfect Comms is present. If Perfect Comms is absent, only that optional voice registration is skipped; TOU-Mira's gameplay and Jailor controls continue to operate normally.

## Important routing details

- Gate `Mute` wins over gate `Muffle`. A working `Muffle` applies only to that speaker while preserving the selected route.
- Pair `Mute` wins; otherwise the first valid pair `Route` wins and any pair `Muffle` is applied afterward. A pair route intentionally replaces ordinary routing for that listener/speaker pair.
- Speaker/global mutes and the Tasks-only `OnlyMeetingOrLobby` policy remain authoritative. During Tasks, an explicit pair route runs before `OnlyGhostsCanTalk` and Comms-sabotage blocking so Medium-style exceptions are possible; during Meeting/Exile those host restrictions run first. Channels never bypass those host restrictions.
- Every non-empty channel result is retained. A target can transmit only through a matching membership with `TwoWay: true`; `false` can receive the same key but cannot transmit it. If several shared memberships route the same speaker, the loudest valid result is used.
- `VoiceAudioShape.Proximity` uses `Origin` when supplied and otherwise falls back to the speaker's resolved body position. It spatializes in Lobby, Tasks, Meeting, and Exile whenever a listener position exists.
- `VoiceListenerResult.LightRadius == -1` inherits the local player's resolved light radius. `0` disables vision-radius limiting at the override. Other negative or non-finite inputs normalize to inheritance.
- Original listener delegates still work. Use `RegisterContextualListenerOrigin` and `RegisterContextualListenerFilter` when the effect needs phase or option access. A filter result can independently set `Muffle` and `SightObscured`; the latter restricts sight-based hearing without applying the low-pass filter.
- Audio callback failures are neutral. Overlay viewer failures become `HideAll`; overlay speaker failures become `HideSource`.
- EndGame is a fresh global results-screen call. Per-player API mute/muffle/channel/pair state from the previous phase is deliberately not reapplied after the game world disappears.

The full TOU-Mira parity matrix and copyable recipes are in [Examples](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-Examples). Exact signatures and fallbacks are in the [API Reference](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-API-Reference).

## Current status / limitations

**Currently broken:** None of the documented API 1.2 primitives on this page.

- Perfect Comms synchronizes registered host-option values and persists the local host's values in its global BepInEx config. It does not discover your roles, modifiers, channel membership, phase bookkeeping, temporary permissions, aliases, or role RPCs. Managed Team Radio owns only Perfect Comms' existing selector/input/capture/wire path; your mod still supplies current membership keys and gameplay policy.
- Host-option snapshots and locally evaluated callbacks are cooperative lobby policy, not hostile-client security. A modified client can ignore or forge local behavior.
