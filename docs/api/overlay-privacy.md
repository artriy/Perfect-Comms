---
description: "Keep speaking UI safe for concealed, blinded, transformed or aliased identities."
---

# Overlay privacy

Overlay rules hide, dim, or safely reattribute identity-bearing voice UI (speaking bar, meeting speaking indicators, player-volume activity) for the local viewer. They are **visual only**: they do not mute audio, stop transmission, or change what anyone hears. To stop transmission use a **[Gate](/api/gate)** with `Mute`; for a private audible route use a **[Channel](/api/channels)**; to muffle what the local player hears use a **[Listener Filter](/api/listener-origin#contextual-listener-filter)**.

## Viewer-wide rules

Dim or hide all identity-bearing voice UI for the local viewer:

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

The context has `GetOption`, `GetEnumOption`, and `GetNumberOption` scoped to your `modId`.

### Viewer verdicts

| Result | UI effect |
| :--- | :--- |
| `VoiceOverlayViewerResult.Pass` | No opinion. |
| `VoiceOverlayViewerResult.DimAll` | Keep the overlay but dim identity-bearing presentation. |
| `VoiceOverlayViewerResult.HideAll` | Hide every identity-bearing indicator. |

`HideAll` beats `DimAll` beats `Pass` across registrations. A missing viewer or a throwing callback gives `HideAll`. Viewer rules do not run in the menu, lobby, or end-of-game screens.

## Per-speaker rules

Hide one source or attribute it to a safe alias:

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

The same three option accessors are available.

### Speaker verdicts

| Result | UI effect |
| :--- | :--- |
| `VoiceOverlaySpeakerResult.Pass` | Present the speaker normally unless another rule restricts it. |
| `VoiceOverlaySpeakerResult.Alias(playerId)` | Attribute activity to the target player. |
| `VoiceOverlaySpeakerResult.HideSource` | Hide this source's indicator. |
| `VoiceOverlaySpeakerResult.HideAll` | Hide all indicators for this viewer. |

Order: `HideAll > HideSource > Alias > Pass`. Aliases must agree on one target; conflicting aliases, `byte.MaxValue`, a missing alias id, or a throwing callback hide the source. A missing speaker hides the source; a missing viewer hides everything. An alias target that is disconnected or not found (or, during tasks, concealed, disguised, transparent, or invisible) hides the source. An alias to the speaker's own id presents the speaker normally.

## Rules to know

- Your rules compose with built-in concealment, blindness, and alias protection. `Pass` cannot reveal what built-in policy hides. Exception: during tasks a safe `Alias` replaces the built-in hide for a disguised or concealed source. `DimAll` still applies on top of an alias.
- Meeting, Exile, and end-of-game are public-identity phases: built-in disguise/concealment/blindness no longer hides the real speaker. Your speaker rules still run in Meeting and Exile; viewer rules run in Tasks, Meeting, and Exile.
- Presentation never switches mid-utterance. If a source's result changes while speaking, it is hidden until it goes quiet (a phase change counts as quiet).
- Results are frame-cached: a viewer rule may run once per frame, a speaker rule once per active source per frame. Keep callbacks cheap and throw-free.
- Perfect Comms does not sync disguise, concealment, or alias state. Your mod must provide it on every client.
- Register each rule once; duplicates accumulate. `PerfectCommsApi.Unregister(modId)` removes them.

Exact signatures and fallbacks: **[API Reference](/api/reference)**.

## Current status / limitations

**Currently broken:** None of the documented API 1.2 primitives on this page.

- Your mod owns disguise/concealment state, aliases, gameplay UI, and role RPCs.
- Callbacks coordinate cooperative clients; they are not hostile-client enforcement.
