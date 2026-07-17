# Overlay Privacy

Overlay privacy lets a mod restrict or safely reattribute identity-bearing Perfect Comms UI for the local viewer. It covers the speaking bar, meeting speaking indicators, player-volume activity, and other voice presentation that could reveal who is speaking.

Back to **[Mod Integration](Mod-Integration)**

Overlay rules are **visual only**. They do not mute audio, stop transmission, change a voice route, or alter what another client hears.

---

## Viewer-wide rules

Use a viewer rule when the local viewer's entire identity-bearing voice UI must be dimmed or hidden:

```csharp
PerfectCommsApi.RegisterOverlayViewerRule("com.me.mymod", ctx =>
{
    if (!ctx.GetOption("HideVoiceUiWhileBlind"))
        return VoiceOverlayViewerResult.Pass;

    return MyRoles.IsCompletelyBlinded(ctx.Viewer)
        ? VoiceOverlayViewerResult.HideAll
        : VoiceOverlayViewerResult.Pass;
});
```

```csharp
public sealed record VoiceOverlayViewerContext(
    PlayerControl Viewer,
    VoicePhaseKind Phase,
    bool IsDead);
```

The runtime injects `GetOption`, `GetEnumOption`, and `GetNumberOption` accessors scoped to the callback's `modId`.

### Viewer verdicts

| Result | UI effect |
| :--- | :--- |
| `VoiceOverlayViewerResult.Pass` | No opinion; built-in privacy and other registrations still apply. |
| `VoiceOverlayViewerResult.DimAll` | Keep the overlay present but remove/dim identity-bearing presentation. |
| `VoiceOverlayViewerResult.HideAll` | Hide every identity-bearing voice indicator for this viewer. |

Results compose restrictively across all registrations: `HideAll` wins over `DimAll`, which wins over `Pass`. A missing viewer or throwing viewer callback fails private to `HideAll`.

---

## Per-speaker rules

Use a speaker rule when the viewer may see most speakers normally but a particular transport source must be hidden or attributed to a safe alias:

```csharp
PerfectCommsApi.RegisterOverlaySpeakerRule("com.me.mymod", ctx =>
{
    if (MyRoles.IsConcealed(ctx.Speaker))
        return VoiceOverlaySpeakerResult.HideSource;

    byte? disguiseTarget = MyRoles.DisguiseTargetId(ctx.Speaker);
    return disguiseTarget.HasValue
        ? VoiceOverlaySpeakerResult.Alias(disguiseTarget.Value)
        : VoiceOverlaySpeakerResult.Pass;
});
```

```csharp
public sealed record VoiceOverlaySpeakerContext(
    PlayerControl Viewer,
    PlayerControl Speaker,
    VoicePhaseKind Phase,
    bool ViewerIsDead,
    bool SpeakerIsDead);
```

The runtime injects all three host-option accessors scoped to the callback's `modId` here as well.

### Speaker verdicts

| Result | UI effect |
| :--- | :--- |
| `VoiceOverlaySpeakerResult.Pass` | Present the transport speaker normally unless another rule restricts it. |
| `VoiceOverlaySpeakerResult.Alias(playerId)` | Attribute activity to the safe target player instead of the transport source. |
| `VoiceOverlaySpeakerResult.HideSource` | Hide this source's identity-bearing indicator. |
| `VoiceOverlaySpeakerResult.HideAll` | Hide all identity-bearing voice indicators for this viewer. |

Composition is restrictive: `HideAll > HideSource > Alias > Pass`. Repeated aliases are accepted only when they agree on the same target. Conflicting aliases, `byte.MaxValue`, a missing alias id, an unsafe/unavailable target, or a throwing speaker callback fail private by hiding the source.

---

## Built-in privacy and public meetings

External overlay rules compose with Perfect Comms' built-in concealment, blindness, and alias protections. An integration can make presentation more private or provide a safe alias; it cannot use `Pass` to reveal an identity that built-in policy already hides.

During task-world phases, built-in disguise, concealment, blindness, and alias state can hide or reattribute identity-bearing voice UI. Meeting and Exile are public-identity phases: once the game publicly reveals identities, task-world disguise/concealment/blindness does not suppress or alias the real speaker's meeting indicator. Explicit mod API privacy rules still run in those phases and can return `HideAll`, `HideSource`, `DimAll`, or a safe `Alias`.

Overlay callbacks are evaluated from local state and their results are frame-cached. A viewer rule may run once per rendered frame; a speaker rule may run once per active source in that frame. Keep callbacks cheap, allocation-light, and throw-free.

Perfect Comms does not synchronize disguise, concealment, or alias state. Host-option values are the only integration values it networks. Your mod must already provide the state used by these callbacks on each viewer's client.

---

## Choose the correct primitive

- To stop or allow transmission, use a **[Gate](Mod-Integration-Gate)** with `Mute`.
- To create an audible private route, use a **[Channel](Mod-Integration-Channels)**.
- To muffle everything the local player hears, use a **[Listener Filter](Mod-Integration-Listener-Origin#contextual-listener-filter)**.
- Use overlay privacy only for identity-bearing visual presentation.

---

## Lifecycle

Register each rule once. Duplicate registrations accumulate. Call `PerfectCommsApi.Unregister(modId)` from the mod's unload path to remove both viewer and speaker rules along with every other registration for that id.

See **[API Reference](Mod-Integration-API-Reference)** for exact signatures and fallback behavior.

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- Perfect Comms synchronizes registered host-option values only. Your mod owns disguise/concealment state, aliases, gameplay UI, and role RPCs.
- Overlay callbacks coordinate cooperative clients; they are not hostile-client authentication or enforcement.
