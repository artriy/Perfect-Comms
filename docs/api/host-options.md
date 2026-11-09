---
description: "Add your own synced host settings and a Host voice settings tab."
---

# Host options and tabs

Host options add persistent, lobby-synced rows to the Perfect Comms host panel: toggles, enum steppers, numbers, descriptions, conditional visibility, and migration from an older config entry. The local host's choices are saved in Perfect Comms' global BepInEx config (per-instance config if the global file is unusable); the connected host's values sync to clients so everyone evaluates the same voice policy.

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

`Description` is the row's help text. `Label` supports rich text. One tab per `modId` (first label wins); options show only if their mod id has a tab. Rows are grouped toggles, enums, numbers, each in registration order.

## Conditional visibility

Every option type has:

```csharp
public Func<VoiceHostOptionContext, bool>? Visible { get; init; }
```

The context has `GetOption`, `GetEnumOption`, `GetNumberOption` (bare keys, scoped to your `modId`) and `TeamRadioEnabled` (host-synced main Team Radio toggle).

```csharp
Visible = ctx =>
    ctx.GetOption("EnableSpiritVoice") &&
    ctx.GetEnumOption("SpiritDirection") != 0
```

Hiding a row does not reset its value. A throwing callback shows the row. Avoid cycles (A depends on B, B on A); hang dependent rows off a stable parent.

## Migrating an older setting

Set `LegacyBinding` to a `VoiceHostOptionLegacyBinding(Section, Key)` naming an entry in Perfect Comms' config. The first time the option is created, that entry's value replaces `Default`. Afterwards the old entry is ignored.

```csharp
new VoiceHostOption("EnableSpiritVoice", "Spirit Voice", Default: true)
{
    LegacyBinding = new VoiceHostOptionLegacyBinding("My Mod", "EnableSpiritVoice")
}
```

## Read values in callbacks

Pass the bare key; Perfect Comms composes `modId.Key`:

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

The same accessors exist on every contextual callback: speaker, channel, player-trait, contextual global gate, listener origin/filter, pair, phase observer, overlay viewer/speaker, and option visibility. The original listener delegates take only `PlayerControl`; use the contextual registrations for option access. An unregistered key reads `false`, `0`, or `0f`.

## Validation and clamping

- Numbers are ignored unless `Key` is non-empty, `Default`/`Min`/`Max`/`Step` are finite, `Max >= Min`, and `Step > 0`. Values are clamped to `Min..Max` and rounded to the nearest step from `Min`; a non-finite saved value falls back to `Default`.
- `Format` affects display only (`"0.0"`, `"0%"`, `"0.00 s"`); empty or invalid falls back to `"0.0"`.
- Bool and enum options are ignored when `Key` is empty or whitespace; enums also when `Choices` is `null` or empty (a `null` choice shows as empty). Enum values are clamped to a valid index. Handle unexpected enum integers defensively: a different choice count on another client changes what an index means.

## Sync and compatibility

- A joined lobby's host values override evaluation but never overwrite your saved local-host choices. An option the host does not send keeps this client's registered value.
- Only values sync. Tabs, labels, choices, descriptions, and visibility callbacks are local, so all clients must register the same `modId`, keys, types, and meanings. Unknown option hashes are ignored.
- Values are part of host-settings snapshot schema 5; a client rejects a snapshot with a different schema, so host and clients need Perfect Comms versions sharing it.
- Max 256 host options across all mods; extras are ignored.
- Values are keyed by a 32-bit hash of `modId.Key` (numbers use a separate salt). Collisions are not detected.
- Each `modId.Key` registers once across all three types; duplicates are ignored.
- `Unregister(modId)` removes the tab, options, values, and every other registration for that id; saved config entries remain for re-registration.

Use a stable reverse-DNS mod id, register after confirming Perfect Comms is present, and keep `Visible` fast, deterministic, and local. Exact signatures: **[API Reference](/api/reference)**.

## Current status / limitations

**Currently broken:** None of the documented API 1.2 primitives on this page.

- Your mod owns gameplay state, role UI, and role RPCs; managed Team Radio is a separate primitive.
- Host values coordinate cooperative clients; they are not hostile-client enforcement.
