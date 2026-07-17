# API 1.1 Role Recipes

These examples show how an external role mod can reproduce Perfect Comms' 17 TOU-Mira host rows with public API 1.1. Names such as `MyRoles`, `MyVoiceState`, and `MyRadio` are placeholders for your mod's own synchronized API.

Call `Register()` only after the soft-dependency check in [Mod Integration](Mod-Integration). Register once and call `PerfectCommsApi.Unregister(Mod)` before a supported dynamic reload.

Back to **[Mod Integration](Mod-Integration)**

---

## Complete 17-row parity matrix

| # | Built-in host row / default | External API implementation | State the role mod must own |
| :---: | :--- | :--- | :--- |
| 1 | `MuteBlackmailedInMeetings` / On | Bool option + Meeting/Exile speaker `Mute` | Current blackmailed player |
| 2 | `MuteBlackmailedNextRound` / Off | Bool option + Meeting-or-Exile → Tasks observer + Tasks speaker `Mute` | Persisted affected player ids and clear boundary |
| 3 | `MuteParasiteControlled` / On | Bool option + Tasks speaker `Mute` | Active controlled victim |
| 4 | `ParasiteHearFromVictim` / On | Bool option + contextual listener origin, `Additive` | Parasite-to-victim link, victim position/light radius |
| 5 | `MutePuppeteerControlled` / On | Bool option + Tasks speaker `Mute` | Active controlled victim |
| 6 | `PuppeteerHearFromVictim` / On | Bool option + contextual listener origin, `Replace` | Puppeteer-to-victim link, victim position/light radius |
| 7 | `MuteSwooperWhileSwooped` / On | Bool option + Tasks/Meeting/Exile speaker `Mute` while swooped | Active swoop state |
| 8 | `MuffleBlindedOrFlashedHearing` / On | Bool option + contextual listener filter | Per-viewer Eclipsal/Grenadier effect state |
| 9 | `MuffleHypnotizedDuringHysteria` / On | Bool option + contextual listener filter | Hypnotized target and Mass Hysteria state |
| 10 | `CrewpostorUsesImpostorVoice` / On | Bool option + `VoicePlayerTraits.ImpostorVoice` | Crewpostor classification |
| 11 | `MuteGlitchHacked` / On | Bool option + Tasks/Meeting/Exile speaker `Mute` while hacked | Active Glitch Hack state |
| 12 | `MuteJailedInMeetings` / On | Bool option + Meeting/Exile speaker `Mute` | Jailee and owning Jailor |
| 13 | `JailPersistsAfterJailorDeath` / Off | Conditional bool option + jail rule checks Jailor alive state | Jailor alive/dead and persisted jail |
| 14 | `JailorCanUnmuteJailed` / On | Bool option + jail rule reads synchronized temporary allow flag | Jailor UI/input plus allow/revoke RPC |
| 15 | `MediumGhostVoice` / None | Enum option + Tasks-only pair Route/Mute rules | Active Medium, spirit position, and the selected mediated ghost for the reverse direction |
| 16 | `TeamRadioVampires` / On | Bool option + shared channel; receive-only except while transmitting | Vampire membership, phase policy, hold state, UI/input/RPC |
| 17 | `TeamRadioLovers` / On | Bool option + pair-scoped channel; receive-only except while transmitting | Partner pairing, phase policy, hold state, UI/input/RPC |

Every built-in row has a public-API equivalent. The API supplies voice policy and option synchronization; the external mod supplies role truth and role networking.

---

## Register all parity options

The exact keys below are safe because API keys are scoped to your own `modId`; they do not overwrite Perfect Comms' built-in settings.

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
    Toggle("TeamRadioVampires", "Team Radio: Vampires", true);
    Toggle("TeamRadioLovers", "Team Radio: Lovers", true);

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

Add descriptions in production code; they become the in-game help text. The helper is only to keep this page readable.

---

## Speaker rules: Blackmailer, control, Swooper, Glitch, and Jailor

```csharp
private static void RegisterSpeakerRules()
{
    PerfectCommsApi.RegisterVoiceRule(Mod, ctx =>
    {
        bool deliberation = ctx.Phase is VoicePhaseKind.Meeting or VoicePhaseKind.Exile;
        bool liveGame = ctx.Phase is VoicePhaseKind.Tasks or VoicePhaseKind.Meeting or VoicePhaseKind.Exile;

        if (liveGame &&
            ctx.GetOption("MuteGlitchHacked") &&
            MyRoles.IsGlitchHacked(ctx.Player))
        {
            return VoiceRuleResult.Mute("Hacked");
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
                    MyRoles.IsAlive(jailorId);
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
                return VoiceRuleResult.Mute("Parasite controlled");

            if (ctx.GetOption("MutePuppeteerControlled") &&
                MyRoles.IsPuppeteerControlled(ctx.Player))
                return VoiceRuleResult.Mute("Puppeteer controlled");

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
            MyVoiceState.CommitBlackmailForNextRound();
        }
    });
}
```

### Phase-owned bookkeeping

If a role effect should end on death, include that in `MyRoles` or add an explicit `ctx.IsDead` condition. API gates can intentionally act on voice-dead players; they do not silently skip them.

These three bookkeeping methods must operate on state your mod already owns and synchronizes. The observer supplies deterministic meeting-start, post-Exile/post-Meeting, and lobby-reset boundaries but does not send the data.

The Jailor's temporary unmute button and allow/revoke RPC also belong to the role mod. Perfect Comms only reads the resulting flag.

---

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

`Replace` hears from the Puppeteer victim instead of the local body. `Additive` lets the Parasite hear from both positions. Both parity routes use the controlled victim's resolved light radius. Use `LightRadius: -1` only when you intentionally want to inherit the listener's own radius; `0` disables the vision-radius limit.

---

## Eclipsal, Grenadier, and Hypnotist listener muffle

```csharp
private static void RegisterListenerEffects()
{
    PerfectCommsApi.RegisterContextualListenerFilter(Mod, ctx =>
    {
        bool blinded =
            ctx.GetOption("MuffleBlindedOrFlashedHearing") &&
            (MyRoles.IsEclipsalBlinded(ctx.Listener) ||
             MyRoles.IsGrenadierFlashed(ctx.Listener));

        bool hypnotized =
            ctx.GetOption("MuffleHypnotizedDuringHysteria") &&
            MyRoles.IsMassHysteriaActive &&
            MyRoles.IsHypnotized(ctx.Listener);

        return new VoiceListenerFilterResult(blinded || hypnotized);
    });
}
```

This muffles every audible incoming route for that local listener. It does not alter what the affected player transmits.

---

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

`ImpostorVoice` participates in the same vent, ghost-hearing, team-radio, and viewer classification as built-in impostor voice.

---

## Medium directional ghost voice

Use pair rules so the selected Medium/spirit relationship stays private and each direction can be controlled independently:

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
}
```

For built-in parity, the first branch routes the active Medium to every dead listener when that direction is enabled. The reverse branch accepts only the ghost selected by that Medium. A different role design can add a selected-pair check to the first branch too.

Pair `Mute` results are important: they prevent ordinary proximity or a different permissive route from revealing a private Medium interaction.

---

## Vampire and Lovers push-to-radio

All eligible members keep a channel membership. A player becomes a transmitter only while the role mod's synchronized radio hold state is true:

```csharp
private static void RegisterRoleRadios()
{
    PerfectCommsApi.RegisterVoiceChannel(Mod, ctx =>
    {
        if (!ctx.IsDead &&
            MyRadio.IsVoicePhaseEnabled(ctx.Phase) &&
            ctx.GetOption("TeamRadioVampires") &&
            MyRoles.IsVampire(ctx.Player))
        {
            return new VoiceChannelResult(
                "radio:vampires",
                TwoWay: MyRadio.IsTransmitting(ctx.Player.PlayerId),
                Shape: VoiceAudioShape.Radio);
        }

        return null;
    });

    PerfectCommsApi.RegisterVoiceChannel(Mod, ctx =>
    {
        if (ctx.IsDead ||
            !MyRadio.IsVoicePhaseEnabled(ctx.Phase) ||
            !ctx.GetOption("TeamRadioLovers") ||
            MyRoles.LoverPairId(ctx.Player) is not byte pairId)
        {
            return null;
        }

        return new VoiceChannelResult(
            $"radio:lovers:{pairId}",
            TwoWay: MyRadio.IsTransmitting(ctx.Player.PlayerId),
            Shape: VoiceAudioShape.Radio);
    });
}
```

`TwoWay: false` is receive-only: the player can hear a matching transmitter but cannot be heard back. When their synchronized hold state becomes true, the same membership transmits.

`MyRadio.IsVoicePhaseEnabled` is the role mod's synchronized phase policy; use it to mirror whichever Tasks/Meeting/Exile availability your radio UI exposes. Perfect Comms does not add the radio keybind/button, eligibility UI, selected-channel state, phase policy, or hold-state RPC. The mod must publish those state changes to every client that evaluates the channel.

---

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
}

internal static void Unregister()
    => PerfectCommsApi.Unregister(Mod);
```

Overlay privacy is separate from audio policy. Register viewer/speaker overlay rules when a role's disguise or concealment should hide or alias Perfect Comms' identity-bearing UI.

---

## Next

- **[API Reference](Mod-Integration-API-Reference)** - exact records, signatures, precedence, and fallbacks.
- **[Gate](Mod-Integration-Gate)** - speaker/global policy and traits.
- **[Channels](Mod-Integration-Channels)** - receive-only membership and pair routing.
- **[Listener Origin](Mod-Integration-Listener-Origin)** - contextual listener behavior and phase observers.
- **[Host Options](Mod-Integration-Host-Options)** - numeric and conditional rows.
- **[Overlay Privacy](Mod-Integration-Overlay-Privacy)** - hide, dim, or alias voice UI.

## Current status / limitations

**Currently broken:** None of the documented API 1.1 primitives on this page.

- Perfect Comms synchronizes registered host-option values only. The role mod owns all gameplay state, targets/pairs, lifecycle history, buttons/keybinds, UI, and role RPCs used by these recipes.
- These callbacks coordinate cooperative clients; they are not hostile-client authentication or enforcement.
