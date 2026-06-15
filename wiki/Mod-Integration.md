# Mod Integration

Perfect Comms exposes a small public API (`PerfectComms.Api`) so **any** Among Us role mod can add its own voice behaviours - mutes, private radio channels, relocated hearing, host-settings tabs - **without forking Perfect Comms**.

Your mod references `PerfectComms.dll` as a **soft dependency**, registers its rules in `Load()`, and ships its own DLL. Perfect Comms never references your mod.

---

## Why this exists

Before this API, adding a new role behaviour meant copying the Perfect Comms source, editing the role engine, the snapshot, the settings RPC, and the host panel, then maintaining that fork forever. Now it is a handful of lines in your own plugin.

Everything you register runs **locally on every client** (the same model role mods already use), so most behaviours need no networking at all. The one exception - host-authored toggles - rides the existing authenticated settings RPC for you.

---

## 30-second setup

**1. Reference the DLL.** Add `PerfectComms.dll` as an assembly reference. Do **not** ship it - players install Perfect Comms separately (just like MiraAPI / Reactor).

**2. Soft-depend on it** so your mod still loads when Perfect Comms is absent:

```csharp
[BepInPlugin("com.me.mymod", "My Mod", "1.0.0")]
[BepInDependency("com.edgetel.perfectcomms", BepInDependency.DependencyFlags.SoftDependency)]
public class MyModPlugin : BasePlugin
{
    public override void Load()
    {
        if (IL2CPPChainloader.Instance.Plugins.ContainsKey("com.edgetel.perfectcomms"))
            RegisterVoice();
    }

    private static void RegisterVoice()
    {
        // Your role logic, your own types - no reflection, no Perfect Comms internals.
        PerfectComms.Api.PerfectCommsApi.RegisterVoiceRule("com.me.mymod", ctx =>
            ctx.Phase == PerfectComms.Api.VoicePhaseKind.Meeting && MyRoles.IsGagged(ctx.Player)
                ? PerfectComms.Api.VoiceRuleResult.Mute("Gagged")
                : PerfectComms.Api.VoiceRuleResult.Pass);
    }
}
```

That is a complete, working integration. A gagged player is muted in meetings; everyone else is untouched.

> Wrapping the registration in the `ContainsKey` check keeps it a true soft dependency: referencing `PerfectCommsApi` only runs when the DLL is actually present.

---

## The five primitives

| Primitive | What it does | Networking |
| :--- | :--- | :--- |
| **[Gate](Mod-Integration-Gate)** | Mute or muffle a player (per-player or whole-lobby) in a phase | None |
| **[Channel](Mod-Integration-Channels)** | Two players hear each other beyond proximity (team radio, private pairs), with a chosen audio shape | None |
| **[Listener Origin](Mod-Integration-Listener-Origin)** | Relocate where the local player hears from | None |
| **[Host Options](Mod-Integration-Host-Options)** | Declarative host toggles/enums, synced to all clients | Automatic |
| **[Mod Tab](Mod-Integration-Host-Options#mod-tabs)** | Your own tab in the host settings panel | None |

---

## Ground rules (read once)

- **Callbacks must be cheap and must not throw.** They run ~20×/second per player. Every callback is wrapped in `try/catch` and **fails closed** (a throw becomes "no opinion"), but a slow callback slows the whole voice frame.
- **No allocations in the hot path** where you can avoid them.
- **`Pass` / `null` means "no opinion"** - defer to built-in rules and other mods. Only return a verdict when your role actually applies.
- **Channel keys are namespaced by your mod id** automatically, so two mods can never collide.
- **Unregister on unload:** `PerfectCommsApi.Unregister("com.me.mymod")`.
- **Everything derives from local state.** Since your mod runs on every client, all clients compute the same verdict and converge - no syncing needed (except host options, handled for you).

---

## Next

- Want copy-paste role recipes (Blackmailer, Jailor, Puppeteer, Medium, ...)? **[Examples](Mod-Integration-Examples)**.
- New to it? Read **[Gate](Mod-Integration-Gate)** first - it covers the majority of role behaviours.
- Need team comms or private channels? **[Channels](Mod-Integration-Channels)**.
- Building a "hear from elsewhere" role? **[Listener Origin](Mod-Integration-Listener-Origin)**.
- Want host-configurable rules and your own settings tab? **[Host Options & Tabs](Mod-Integration-Host-Options)**.
- Full signatures: **[API Reference](Mod-Integration-API-Reference)**.
