# Host Options & Tabs

Give the host configurable voice settings for your mod, and your own tab in the Perfect Comms host settings panel. Option values are **synced from the host to every client** automatically over the authenticated settings RPC - you never touch networking.

← Back to **[Mod Integration](Mod-Integration)**

---

## Declare options

Register your options once, in `Load()` (after checking Perfect Comms is present):

```csharp
const string Mod = "com.me.mymod";

// A toggle:
PerfectCommsApi.RegisterHostOption(Mod,
    new VoiceHostOption("MuteSilenced", "<color=#FF0000><b>Silencer</b></color>: Mute Silenced", true));

// A stepper / enum:
PerfectCommsApi.RegisterHostEnumOption(Mod,
    new VoiceHostEnumOption("GhostVoice", "<color=#A680FF><b>Spirit</b></color>: Ghost Voice",
        Default: 0, Choices: new[] { "Off", "Spirit -> Ghost", "Ghost -> Spirit", "Both" }));
```

- `Key` is unique within your mod (stored and synced as `modId.Key`).
- `Label` supports the same rich-text markup the built-in options use.
- `Default` / `Default + Choices` set the starting value.

---

## Read option values

Inside any [Gate](Mod-Integration-Gate) or [Channel](Mod-Integration-Channels) callback, read the **host-synced** value with the bare key (auto-qualified to your mod):

```csharp
PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
{
    if (!ctx.GetOption("MuteSilenced")) return VoiceRuleResult.Pass;   // host turned it off
    return MyRoles.IsSilenced(ctx.Player)
        ? VoiceRuleResult.Mute("Silenced")
        : VoiceRuleResult.Pass;
});

// enum:
int ghostMode = ctx.GetEnumOption("GhostVoice");
```

The value every client reads is the **host's** value - clients cannot forge it. When the host flips a toggle, the change is broadcast and every client's callbacks immediately see the new value.

---

## Mod tabs

Register a tab to group your options under their own page in the host panel, sitting alongside the built-in "TOU MIRA" tab under the **MOD BEHAVIOUR** section:

```csharp
PerfectCommsApi.RegisterModTab(Mod, "My Mod");
// every RegisterHostOption / RegisterHostEnumOption under `Mod` renders inside this tab
```

- Tab **labels are local** (no sync) - every client with your mod registers the same label.
- A client **without** your mod simply does not render the tab - fail-soft, no errors.
- Options appear in registration order (toggles, then enums).

A complete host-configurable mod:

```csharp
private static void RegisterVoice()
{
    const string Mod = "com.me.mymod";
    PerfectCommsApi.RegisterModTab(Mod, "My Mod");
    PerfectCommsApi.RegisterHostOption(Mod,
        new VoiceHostOption("MuteSilenced", "<color=#FF0000><b>Silencer</b></color>: Mute Silenced", true));

    PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
        ctx.GetOption("MuteSilenced") && MyRoles.IsSilenced(ctx.Player)
            ? VoiceRuleResult.Mute("Silenced")
            : VoiceRuleResult.Pass);
}
```

---

## How the sync works (for the curious)

Option values ride a trailing, **hash-keyed** block appended to the host's settings RPC. Because entries are keyed by a hash of `modId.Key` rather than a fixed byte offset, adding or removing options never shifts the rest of the packet, and older clients that stop reading before the block simply keep their defaults. The wire format is forward- and backward-compatible by construction.

You do not need to do anything for this - registering an option is enough.

---

## Next

- **[API Reference](Mod-Integration-API-Reference)** - every type and method.
- **[Gate](Mod-Integration-Gate)** · **[Channels](Mod-Integration-Channels)** · **[Listener Origin](Mod-Integration-Listener-Origin)**
