# Host Options & Tabs

Host options add session-local controls to the Perfect Comms host panel. Their values travel in the host-settings snapshot so compatible clients can evaluate the same voice policy. API 1.1 supports toggles, enum steppers, numeric rows, descriptions, and conditional visibility.

Back to **[Mod Integration](Mod-Integration)**

---

## Register a tab and rows

```csharp
const string Mod = "com.me.mymod";

PerfectCommsApi.RegisterModTab(Mod, "My Mod");

PerfectCommsApi.RegisterHostOption(
    Mod,
    new VoiceHostOption(
        Key: "EnableSpiritVoice",
        Label: "<b>Medium</b>: Spirit Voice",
        Default: true)
    {
        Description = "Lets the configured Medium route hear spirits."
    });

PerfectCommsApi.RegisterHostEnumOption(
    Mod,
    new VoiceHostEnumOption(
        Key: "SpiritDirection",
        Label: "Spirit Direction",
        Default: 3,
        Choices: new[] { "None", "Medium → Ghost", "Ghost → Medium", "Both" })
    {
        Description = "Chooses which side can transmit."
    });

PerfectCommsApi.RegisterHostNumberOption(
    Mod,
    new VoiceHostNumberOption(
        Key: "SpiritVolume",
        Label: "Spirit Volume",
        Default: 0.8f,
        Min: 0f,
        Max: 1f,
        Step: 0.05f,
        Format: "0%")
    {
        Description = "Sets the Medium route volume.",
        Visible = ctx => ctx.GetOption("EnableSpiritVoice")
    });
```

`Description` becomes the row's help text. `Label` supports the same rich text used by built-in host settings.

One exact `modId` gets one tab; the first label wins. Options render only when their mod id has a tab. Rows are grouped as toggles, then enums, then numbers, preserving registration order within each group.

---

## Conditional visibility

Every option type has:

```csharp
public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
```

`VoiceHostOptionContext` exposes bare-key `GetOption`, `GetEnumOption`, and `GetNumberOption`, automatically scoped to the option's `modId`.

```csharp
Visible = ctx =>
    ctx.GetOption("EnableSpiritVoice") &&
    ctx.GetEnumOption("SpiritDirection") != 0
```

Visibility changes presentation only; hiding a row does not reset its value. A throwing visibility callback shows the row so a callback failure does not make configuration unreachable.

Avoid visibility cycles where row A depends on B while B depends on A. Use a stable parent toggle/enum for dependent rows.

---

## Read values in callbacks

Pass the bare key. Perfect Comms composes `modId.Key` internally:

```csharp
PerfectCommsApi.RegisterVoicePairRule(Mod, ctx =>
{
    if (!ctx.GetOption("EnableSpiritVoice"))
        return VoicePairResult.Pass;

    return MyMedium.IsAllowedPair(ctx)
        ? VoicePairResult.Route(
            VoicePairRouteShape.Ghost,
            ctx.GetNumberOption("SpiritVolume"))
        : VoicePairResult.Pass;
});
```

All new contextual callbacks expose the same three accessors:

- speaker, channel, and player-trait contexts;
- contextual global gate;
- listener origin and listener filter contexts;
- listener/speaker pair context;
- phase observer context;
- overlay viewer and speaker contexts;
- option visibility context.

The original listener delegates take only `PlayerControl`; use the contextual listener registrations when they need option access.

---

## Numeric normalization

A numeric declaration is ignored unless:

- `Key` is non-empty;
- `Default`, `Min`, `Max`, and `Step` are finite;
- `Max >= Min`; and
- `Step > 0`.

The default, host changes, and received values are clamped to `Min..Max` and rounded to the nearest step relative to `Min`.

`Format` controls display formatting only. Choose a format that matches the stored value, such as `"0.0"`, `"0%"`, or `"0.00 s"`.

Bool and enum declarations retain the original compatibility behavior and are caller-validated. Use a stable, non-empty, case-sensitive key; provide a non-empty enum choice array and a valid default index; and handle an unexpected enum integer defensively when mod versions differ.

---

## Lifetime, sync, and compatibility

- Values are session-local memory. They are not written to either mod's BepInEx config and restart at registered defaults.
- Only values are synchronized. Tabs, labels, choices, descriptions, visibility callbacks, and gameplay state are local declarations.
- Compatible clients must register the same `modId), keys, types, and meanings.
- Unknown option hashes are ignored by a client.
- The host snapshot supports at most 256 synchronized mod-option values across all installed mods.
- The wire identifies a value with a 32-bit hash of `modId.Key`. Collisions are not detected.
- Duplicate registrations accumulate as rows while the first stored value for a composed key remains authoritative. Register once.
- `Unregister(modId)` removes that id's tab, option declarations, values, and every other API registration.

All original bool/enum records and registration signatures remain unchanged. Existing integrations continue to compile and run; numbers and visibility are additive API 1.1 features.

---

## Registration checklist

- Use a stable reverse-DNS mod id.
- Keep keys stable, simple, unique, and case-sensitive.
- Register after Perfect Comms is confirmed present.
- Register the tab once and every option once.
- Keep `Visible` fast, deterministic, and local.
- Treat host values as cooperative policy, not authentication.

---

## Next

- **[Gate](Mod-Integration-Gate)** - consume options in speaker/global policy.
- **[Channels](Mod-Integration-Channels)** - use enum/numeric settings for directions and volume.
- **[Examples](Mod-Integration-Examples)** - complete TOU-style declarations and callbacks.

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- Perfect Comms synchronizes these option values only. Your mod owns gameplay state, role UI, buttons/keybinds, and role RPCs.
- Option snapshots and local callbacks coordinate cooperative clients; they are not hostile-client authentication or enforcement.
