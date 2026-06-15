# API Reference

Namespace: `PerfectComms.Api` (in `PerfectComms.dll`). API version `1.0`.

← Back to **[Mod Integration](Mod-Integration)**

---

## `PerfectCommsApi` (static)

```csharp
public const string ApiVersion = "1.0";
public const string PluginId   = "com.edgetel.perfectcomms";

// Gate
void RegisterVoiceRule(string modId, Func<VoiceRuleContext, VoiceRuleResult> rule);
void RegisterGlobalGate(string modId, VoicePhaseKind phase, Func<bool> isActive, string reason);

// Channel
void RegisterVoiceChannel(string modId, Func<VoiceRuleContext, VoiceChannelResult?> channel);

// Listener origin + filter
void RegisterListenerOrigin(string modId, Func<PlayerControl, VoiceListenerResult?> origin);
void RegisterListenerFilter(string modId, Func<PlayerControl, bool> shouldMuffle);

// Host options + tab
void RegisterHostOption(string modId, VoiceHostOption option);
void RegisterHostEnumOption(string modId, VoiceHostEnumOption option);
void RegisterModTab(string modId, string tabLabel);

// Cleanup
void Unregister(string modId);
```

All registrations are keyed by `modId`. Call `Unregister(modId)` on unload to remove every registration for your mod.

---

## Enums

```csharp
enum VoicePhaseKind  { Lobby, Tasks, Meeting, Exile }

enum VoiceAudioShape { Proximity, Radio, Muffle }

enum VoiceVerdict    { Pass, Mute, Muffle }

enum VoiceListenerMode { Replace, Additive }
```

---

## `VoiceRuleContext`

Passed to gate and channel callbacks.

```csharp
sealed record VoiceRuleContext(
    PlayerControl Player,
    VoicePhaseKind Phase,
    bool IsLocal,
    bool IsDead)
{
    Func<string, bool> GetOption;      // host-synced bool, bare key auto-qualified to your mod
    Func<string, int>  GetEnumOption;  // host-synced int/enum
}
```

`GetOption("Key")` / `GetEnumOption("Key")` read the host's synced value for `modId.Key`.

---

## `VoiceRuleResult`

```csharp
sealed record VoiceRuleResult(VoiceVerdict Verdict, string Reason)
{
    static readonly VoiceRuleResult Pass;
    static VoiceRuleResult Mute(string reason);
    static VoiceRuleResult Muffle(string reason);
}
```

Return `Pass` for "no opinion". First non-`Pass` across all mods wins.

---

## `VoiceChannelResult`

```csharp
sealed record VoiceChannelResult(
    string Key,
    bool TwoWay = true,
    VoiceAudioShape Shape = VoiceAudioShape.Radio,
    float Volume = 1f,
    Vector2? Origin = null);
```

Two players resolving the same `Key` hear each other. Keys are namespaced by `modId` internally. Return `null` for "not on a channel". Set `Origin` (with `Shape: Proximity`) to make the channel spatial - heard from that point with falloff (e.g. a Medium seance heard from the spirit position).

---

## `VoiceListenerResult`

```csharp
sealed record VoiceListenerResult(
    Vector2 Origin,
    float LightRadius,        // -1 inherits the local player's radius
    VoiceListenerMode Mode);
```

Local player only. Return `null` for normal hearing.

---

## `VoiceHostOption` / `VoiceHostEnumOption`

```csharp
sealed record VoiceHostOption(string Key, string Label, bool Default);

sealed record VoiceHostEnumOption(string Key, string Label, int Default, string[] Choices);
```

Stored and synced as `modId.Key`. `Label` supports rich-text markup.

---

## Behavioural contract

- **Callbacks run ~20×/second.** Keep them cheap and allocation-light.
- **Fail-closed.** A throwing callback is treated as `Pass` / `null` / option default. The voice frame never breaks.
- **Local & convergent.** All callbacks evaluate on each client from local game state, so verdicts converge with no networking - except host option *values*, which are synced from the host automatically.
- **Namespaced.** Channel keys and option keys are scoped by `modId`; mods cannot collide.

---

## Minimal complete example

```csharp
[BepInPlugin("com.me.mymod", "My Mod", "1.0.0")]
[BepInDependency("com.edgetel.perfectcomms", BepInDependency.DependencyFlags.SoftDependency)]
public class MyModPlugin : BasePlugin
{
    public override void Load()
    {
        if (!IL2CPPChainloader.Instance.Plugins.ContainsKey(PerfectCommsApi.PluginId)) return;

        const string Mod = "com.me.mymod";
        PerfectCommsApi.RegisterModTab(Mod, "My Mod");
        PerfectCommsApi.RegisterHostOption(Mod,
            new VoiceHostOption("MuteGagged", "<b>Gag</b>: Mute Gagged", true));

        PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
            ctx.GetOption("MuteGagged") && MyRoles.IsGagged(ctx.Player)
                ? VoiceRuleResult.Mute("Gagged")
                : VoiceRuleResult.Pass);
    }
}
```
