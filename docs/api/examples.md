---
description: "Copyable API 1.2 role recipes and the TOU-Mira parity matrix."
---

# Examples: API 1.2 role recipes

Recipes that reproduce TOU-Mira's 17 voice settings with public API 1.2. `MyRoles`, `MyVoiceState`, and `MyColors` stand for your mod's own synchronized state. Call `Register()` after the soft-dependency check in [Mod Integration](/api/), once.

## Complete 17-row parity matrix

| # | TOU-Mira setting / default | External API implementation | State the role mod must own |
| :---: | :--- | :--- | :--- |
| 1 | `MuteBlackmailedInMeetings` / On | Bool option + Meeting/Exile speaker `Mute` | Current blackmailed player |
| 2 | `MuteBlackmailedNextRound` / Off | Bool option + Meeting-or-Exile → Tasks observer + Tasks speaker `Mute` (needs row 1 on) | Players blackmailed during the meeting, cleared at the next meeting and in the lobby |
| 3 | `MuteParasiteControlled` / On | Bool option + Tasks speaker `Mute` | Active controlled victim |
| 4 | `ParasiteHearFromVictim` / On | Bool option + contextual listener origin, `Additive` | Parasite-to-victim link, victim position/light radius |
| 5 | `MutePuppeteerControlled` / On | Bool option + Tasks speaker `Mute` | Active controlled victim |
| 6 | `PuppeteerHearFromVictim` / On | Bool option + contextual listener origin, `Replace` | Puppeteer-to-victim link, victim position/light radius |
| 7 | `MuteSwooperWhileSwooped` / On | Bool option + Tasks/Meeting/Exile speaker `Mute` while swooped | Active swoop state |
| 8 | `MuffleBlindedOrFlashedHearing` / On | Bool option + Tasks-only contextual listener filter, plus `SightObscured` | Per-viewer Eclipsal/Grenadier effect state |
| 9 | `MuffleHypnotizedDuringHysteria` / On | Bool option + contextual listener filter | Hypnotized target and Mass Hysteria state |
| 10 | `CrewpostorUsesImpostorVoice` / On | Bool option + `VoicePlayerTraits.ImpostorVoice` (vent and ghost hearing only; see [Crewpostor player trait](/api/examples#crewpostor-player-trait)) | Crewpostor classification |
| 11 | `MuteGlitchHacked` / On | Bool option + Tasks/Meeting/Exile speaker `Mute` while hacked | Active Glitch Hack state |
| 12 | `MuteJailedInMeetings` / On | Bool option + Meeting/Exile speaker `Mute` | Jailee and owning Jailor |
| 13 | `JailPersistsAfterJailorDeath` / Off | Bool option shown only while row 12 is on + jail rule checks that the Jailor is still alive | Jailor alive/dead and persisted jail |
| 14 | `JailorCanUnmuteJailed` / On | Bool option + jail rule reads synchronized temporary allow flag | Jailor UI/input plus allow/revoke RPC |
| 15 | `MediumGhostVoice` / None | Enum option + Tasks-only pair Route/Mute rules + Medium listener origin at the spirit | Active Medium, spirit position, and the selected mediated ghost for the reverse direction |
| 16 | `TeamRadioVampires` / On | Bool option shown only while Team Radio is on + managed Team Radio membership | Current Vampire membership |
| 17 | `TeamRadioLovers` / On | Bool option shown only while Team Radio is on + pair-keyed managed Team Radio membership | Current Lovers pairing |

Every row has an API equivalent except one gap: row 10 cannot put the Crewpostor on built-in Impostors Team Radio. Perfect Comms has no TOU-Mira settings or role detection of its own; TOU-Mira registers these rows through this API.

## Register all parity options

Keys are scoped to your `modId`, so they cannot collide with other mods. `TeamRadioEnabled` mirrors the **Team Radio** host toggle, so the Vampire and Lovers rows hide while it is off.

```csharp
private const string Mod = "com.me.mymod";

private static void RegisterParityOptions()
{
    PerfectCommsApi.RegisterModTab(Mod, "My Mod Voice");

    void Toggle(
        string key,
        string label,
        bool value,
        Func<VoiceHostOptionContext, bool>? visible = null)
        => PerfectCommsApi.RegisterHostOption(
            Mod,
            new VoiceHostOption(key, label, value)
            {
                Visible = visible
            });

    Toggle("MuteBlackmailedInMeetings", "Blackmailer: Mute in Meetings", true);
    Toggle("MuteBlackmailedNextRound", "Blackmailer: Mute Next Round", false);
    Toggle("MuteParasiteControlled", "Parasite: Mute Controlled Victim", true);
    Toggle("ParasiteHearFromVictim", "Parasite: Also Hear Victim", true);
    Toggle("MutePuppeteerControlled", "Puppeteer: Mute Controlled Victim", true);
    Toggle("PuppeteerHearFromVictim", "Puppeteer: Hear From Victim", true);
    Toggle("MuteSwooperWhileSwooped", "Swooper: Mute While Swooped", true);
    Toggle("MuffleBlindedOrFlashedHearing", "Eclipsal/Grenadier: Muffle Hearing", true);
    Toggle("MuffleHypnotizedDuringHysteria", "Hypnotist: Muffle During Hysteria", true);
    Toggle("CrewpostorUsesImpostorVoice", "Crewpostor: Use Impostor Voice", true);
    Toggle("MuteGlitchHacked", "Glitch: Mute Hacked Players", true);
    Toggle("MuteJailedInMeetings", "Jailor: Mute Jailee in Meetings", true);
    Toggle(
        "JailPersistsAfterJailorDeath",
        "Jailor: Jail Persists If Jailor Dies",
        false,
        ctx => ctx.GetOption("MuteJailedInMeetings"));
    Toggle("JailorCanUnmuteJailed", "Jailor: Can Unmute Jailee", true);
    Toggle("TeamRadioVampires", "Team Radio: Vampires", true, ctx => ctx.TeamRadioEnabled);
    Toggle("TeamRadioLovers", "Team Radio: Lovers", true, ctx => ctx.TeamRadioEnabled);

    PerfectCommsApi.RegisterHostEnumOption(
        Mod,
        new VoiceHostEnumOption(
            "MediumGhostVoice",
            "Medium: Ghost Voice",
            Default: 0,
            Choices: new[]
            {
                "None",
                "Medium → Ghost",
                "Ghost → Medium",
                "Both"
            }));
}
```

Add `Description` in production code; it becomes the in-game help text.

## Speaker rules: Blackmailer, control, Swooper, Glitch, and Jailor

```csharp
private static void RegisterSpeakerRules()
{
    PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
    {
        if (ctx.IsDead)
            return VoiceRuleResult.Pass;

        bool deliberation = ctx.Phase is VoicePhaseKind.Meeting or VoicePhaseKind.Exile;
        bool liveGame = ctx.Phase is VoicePhaseKind.Tasks or VoicePhaseKind.Meeting or VoicePhaseKind.Exile;

        if (liveGame &&
            ctx.GetOption("MuteGlitchHacked") &&
            MyRoles.IsGlitchHacked(ctx.Player))
        {
            return VoiceRuleResult.Mute("Glitch Hacked");
        }

        if (liveGame &&
            ctx.GetOption("MuteSwooperWhileSwooped") &&
            MyRoles.IsSwooped(ctx.Player))
        {
            return VoiceRuleResult.Mute("Swooped");
        }

        if (deliberation)
        {
            if (ctx.GetOption("MuteBlackmailedInMeetings") &&
                MyRoles.IsCurrentlyBlackmailed(ctx.Player))
            {
                return VoiceRuleResult.Mute("Blackmailed");
            }

            if (ctx.GetOption("MuteJailedInMeetings") &&
                MyRoles.TryGetJail(ctx.Player, out byte jailorId))
            {
                bool jailorValid =
                    ctx.GetOption("JailPersistsAfterJailorDeath") ||
                    MyRoles.IsLivingJailor(jailorId);
                bool temporarilyAllowed =
                    ctx.GetOption("JailorCanUnmuteJailed") &&
                    MyVoiceState.IsJailVoiceAllowed(ctx.Player.PlayerId);

                if (jailorValid && !temporarilyAllowed)
                    return VoiceRuleResult.Mute("Jailed");
            }
        }

        if (ctx.Phase == VoicePhaseKind.Tasks)
        {
            if (ctx.GetOption("MuteBlackmailedNextRound") &&
                MyVoiceState.IsBlackmailedNextRound(ctx.Player.PlayerId))
                return VoiceRuleResult.Mute("Blackmailed");

            if (ctx.GetOption("MuteParasiteControlled") &&
                MyRoles.IsParasiteControlled(ctx.Player))
                return VoiceRuleResult.Mute("Parasite Controlled");

            if (ctx.GetOption("MutePuppeteerControlled") &&
                MyRoles.IsPuppeteerControlled(ctx.Player))
                return VoiceRuleResult.Mute("Puppeteer Controlled");

        }

        return VoiceRuleResult.Pass;
    });

    PerfectCommsApi.RegisterVoicePhaseObserver(Mod, ctx =>
    {
        if (ctx.Phase == VoicePhaseKind.Lobby)
        {
            MyVoiceState.ResetBlackmailVoiceState();
        }
        else if (ctx.Phase == VoicePhaseKind.Meeting)
        {
            MyVoiceState.BeginMeetingBlackmailTracking();
        }
        else if ((ctx.PreviousPhase is VoicePhaseKind.Meeting or VoicePhaseKind.Exile) &&
                 ctx.Phase == VoicePhaseKind.Tasks)
        {
            if (ctx.GetOption("MuteBlackmailedInMeetings") &&
                ctx.GetOption("MuteBlackmailedNextRound"))
                MyVoiceState.CommitBlackmailForNextRound();
            else
                MyVoiceState.ResetBlackmailVoiceState();
        }
    });
}
```

### Phase-owned bookkeeping

The rule returns `Pass` for voice-dead speakers because every TOU-Mira mute targets living players; API gates otherwise apply to dead speakers too.

The observer only marks boundaries (meeting start, Meeting/Exile → Tasks, lobby); your mod owns and syncs the data. `BeginMeetingBlackmailTracking` clears last round's list and records players blackmailed this meeting, `CommitBlackmailForNextRound` makes that the next-round list, `ResetBlackmailVoiceState` clears both. `IsLivingJailor` must be true only while the jailing player is alive and still the Jailor. The Jailor's unmute button and allow/revoke RPC are yours; Perfect Comms only reads the flag.

## Parasite and Puppeteer listener origins

```csharp
private static void RegisterControlHearing()
{
    PerfectCommsApi.RegisterContextualListenerOrigin(Mod, ctx =>
    {
        if (ctx.Phase != VoicePhaseKind.Tasks)
            return null;

        if (ctx.GetOption("PuppeteerHearFromVictim") &&
            MyRoles.PuppeteerVictim(ctx.Listener) is PlayerControl puppet)
        {
            return new VoiceListenerResult(
                (Vector2)puppet.transform.position,
                LightRadius: MyRoles.LightRadiusAt(puppet),
                VoiceListenerMode.Replace);
        }

        if (ctx.GetOption("ParasiteHearFromVictim") &&
            MyRoles.ParasiteVictim(ctx.Listener) is PlayerControl victim)
        {
            return new VoiceListenerResult(
                (Vector2)victim.transform.position,
                LightRadius: MyRoles.LightRadiusAt(victim),
                VoiceListenerMode.Additive);
        }

        return null;
    });
}
```

`Replace` hears from the Puppeteer victim instead of your body; `Additive` lets the Parasite hear from both. Both use the victim's light radius. `LightRadius: -1` inherits the listener's own radius; `0` disables the vision limit.

## Eclipsal, Grenadier, and Hypnotist listener effects

```csharp
private static void RegisterListenerEffects()
{
    PerfectCommsApi.RegisterContextualListenerFilter(Mod, ctx =>
    {
        bool sightObscured =
            MyRoles.IsEclipsalBlinded(ctx.Listener) ||
            MyRoles.IsGrenadierFlashed(ctx.Listener);

        bool muffleBlinded =
            ctx.Phase == VoicePhaseKind.Tasks &&
            ctx.GetOption("MuffleBlindedOrFlashedHearing") &&
            sightObscured;

        bool hypnotized =
            ctx.GetOption("MuffleHypnotizedDuringHysteria") &&
            MyRoles.IsMassHysteriaActive &&
            MyRoles.IsHypnotized(ctx.Listener);

        return new VoiceListenerFilterResult(muffleBlinded || hypnotized)
        {
            SightObscured = sightObscured,
        };
    });
}
```

Low-pass muffles every incoming route for the local listener: Eclipsal/Grenadier only during Tasks, Hypnotist in every phase while Mass Hysteria is active. `SightObscured` separately limits sight-based hearing during Tasks even when muffling is off. Neither changes what the player transmits.

## Crewpostor player trait

```csharp
private static void RegisterCrewpostor()
{
    PerfectCommsApi.RegisterVoicePlayerTraits(Mod, ctx =>
        ctx.GetOption("CrewpostorUsesImpostorVoice") &&
        MyRoles.IsCrewpostor(ctx.Player)
            ? VoicePlayerTraits.ImpostorVoice
            : VoicePlayerTraits.None);
}
```

`ImpostorVoice` counts the player as an impostor for two Tasks rules only: staying audible in vents (**Hear Impostors in Vents** on, **Private Talk in Vents** off) and hearing the dead (**Impostors Hear Dead**). It does not grant built-in Impostors Team Radio, impostor-only chat, impostor meeting radio, or impostor viewer classification.

## Medium directional ghost voice

Pair rules keep the Medium/spirit link private per direction; a listener origin lets the Medium hear from the spirit:

```csharp
private static void RegisterMedium()
{
    PerfectCommsApi.RegisterVoicePairRule(Mod, ctx =>
    {
        if (ctx.Phase != VoicePhaseKind.Tasks)
            return VoicePairResult.Pass;

        int mode = ctx.GetEnumOption("MediumGhostVoice");
        if (mode == 0)
            return VoicePairResult.Pass;

        bool mediumToGhost = mode is 1 or 3;
        bool ghostToMedium = mode is 2 or 3;

        if (MyRoles.IsActiveMedium(ctx.Speaker))
        {
            // A mediating Medium's voice is private from living non-ghost listeners.
            if (!ctx.ListenerIsDead)
                return VoicePairResult.Mute("Medium private voice");

            return mediumToGhost
                ? VoicePairResult.Route(
                    VoicePairRouteShape.Proximity,
                    speakerOrigin: MyRoles.MediumSpiritPosition(ctx.Speaker),
                    listenerOrigin: MyRoles.VoicePosition(ctx.Listener),
                    reason: "Medium to ghost")
                : VoicePairResult.Mute("Medium direction disabled");
        }

        if (ghostToMedium &&
            MyRoles.IsActiveMedium(ctx.Listener) &&
            ctx.SpeakerIsDead)
        {
            if (!MyRoles.IsSelectedSpirit(ctx.Listener, ctx.Speaker))
                return VoicePairResult.Mute("Non-selected ghost");

            return VoicePairResult.Route(
                VoicePairRouteShape.Ghost,
                speakerOrigin: MyRoles.VoicePosition(ctx.Speaker),
                listenerOrigin: MyRoles.MediumSpiritPosition(ctx.Listener),
                reason: "Ghost to Medium");
        }

        return VoicePairResult.Pass;
    });

    PerfectCommsApi.RegisterContextualListenerOrigin(Mod, ctx =>
        ctx.Phase == VoicePhaseKind.Tasks &&
        ctx.GetEnumOption("MediumGhostVoice") != 0 &&
        MyRoles.IsActiveMedium(ctx.Listener)
            ? new VoiceListenerResult(
                MyRoles.MediumSpiritPosition(ctx.Listener),
                LightRadius: -1f,
                VoiceListenerMode.Replace)
            {
                BypassTaskVoiceGates = true,
            }
            : null);
}
```

The first branch routes the active Medium to every dead listener; the reverse accepts only the ghost that Medium selected. While any mode is on, the Medium hears task voice from the spirit's position with their own light radius, and `BypassTaskVoiceGates` keeps that working through **Only Ghosts can Talk/Hear** and **Comms Sabotage Disables Voice** (speaker mutes, distance, walls, and sight still apply). The pair `Mute` results matter: they stop proximity or another route from revealing the private interaction.

## Vampire and Lovers managed Team Radio

Perfect Comms owns the selector, hold-to-talk capture, selected-channel sync, labels, and private routing:

```csharp
private static void RegisterRoleRadios()
{
    PerfectCommsApi.RegisterManagedRadioChannel(Mod, ctx =>
    {
        if (ctx.IsDead ||
            !ctx.GetOption("TeamRadioVampires") ||
            !MyRoles.IsVampire(ctx.Player))
        {
            return null;
        }

        return new VoiceManagedRadioChannelResult(
            Key: "vampires",
            Label: "Vampires",
            Badge: "V");
    });

    PerfectCommsApi.RegisterManagedRadioChannel(Mod, ctx =>
    {
        if (ctx.IsDead ||
            !ctx.GetOption("TeamRadioLovers") ||
            MyRoles.LoverPairId(ctx.Player) is not byte pairId)
        {
            return null;
        }

        return new VoiceManagedRadioChannelResult(
            Key: $"lovers:{pairId}",
            Label: "Lovers",
            Badge: "L");
    });
}
```

Keys are namespaced by `Mod`. Memberships appear in the Team Radio selector after built-in choices. Holding the radio control captures even in Push To Talk mode and routes only to living players with the same membership; the Team Radio master and Tasks/Meeting settings still apply. Return membership whenever it is current; Perfect Comms applies phase eligibility. Your mod owns only membership and stable pair ids.

## Complete registration

```csharp
internal static void Register()
{
    RegisterParityOptions();
    RegisterSpeakerRules();
    RegisterControlHearing();
    RegisterListenerEffects();
    RegisterCrewpostor();
    RegisterMedium();
    RegisterRoleRadios();
    RegisterOverlayPrivacy();
    PerfectCommsApi.RegisterAnimatedColorRule(Mod, MyColors.IsRainbow);
}

internal static void Unregister()
    => PerfectCommsApi.Unregister(Mod);
```

`RegisterOverlayPrivacy` should add [overlay rules](/api/overlay-privacy) for every disguise, concealment, and alias that affects voice UI. There is no Perfect Comms fallback; your mod registers the whole integration.

## Current status / limitations

**Currently broken:** None of the documented API 1.2 primitives on this page.

- Your mod owns gameplay state, targets/pairs, lifecycle history, and Jailor permissions/UI/RPCs. Managed Team Radio owns its selector, keybind/touch control, PTT capture, key sync, and route.
- Callbacks coordinate cooperative clients; they are not hostile-client enforcement.
