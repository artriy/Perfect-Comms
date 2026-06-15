# Mod Integration (quickstart)

Add your own voice behaviours to Perfect Comms **without forking it**. Reference
`PerfectComms.dll` as a soft dependency, register rules in `Load()`, ship your own DLL.

> **Full guide:** <https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration>

## Setup

1. Reference `PerfectComms.dll` (do **not** redistribute it - players install Perfect Comms separately).
2. Soft-depend so your mod still loads without it.
3. Register in `Load()`.

```csharp
[BepInPlugin("com.me.mymod", "My Mod", "1.0.0")]
[BepInDependency("com.edgetel.perfectcomms", BepInDependency.DependencyFlags.SoftDependency)]
public class MyModPlugin : BasePlugin
{
    public override void Load()
    {
        if (!IL2CPPChainloader.Instance.Plugins.ContainsKey(PerfectComms.Api.PerfectCommsApi.PluginId))
            return;

        const string Mod = "com.me.mymod";
        PerfectComms.Api.PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
            ctx.Phase == PerfectComms.Api.VoicePhaseKind.Meeting && MyRoles.IsGagged(ctx.Player)
                ? PerfectComms.Api.VoiceRuleResult.Mute("Gagged")
                : PerfectComms.Api.VoiceRuleResult.Pass);
    }
}
```

## Primitives

| Primitive | Use it for | Wiki |
| :--- | :--- | :--- |
| **Gate** | Mute / muffle a player or the whole lobby | [Gate](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-Gate) |
| **Channel** | Players hear each other beyond proximity (team / private / directed) | [Channels](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-Channels) |
| **Listener Origin** | Relocate where the local player hears from | [Listener Origin](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-Listener-Origin) |
| **Host Options + Tab** | Host-synced toggles and your own settings tab | [Host Options](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-Host-Options) |

## Contract

- Callbacks run ~20×/second - keep them cheap and allocation-light.
- Callbacks must not throw; if they do, they fail closed (treated as no-opinion).
- Return `Pass` / `null` when your role does not apply.
- `Unregister(modId)` on unload.

Full API reference: <https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration-API-Reference>
