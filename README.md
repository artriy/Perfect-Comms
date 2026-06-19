<h1 align="center">Perfect Comms</h1>

<p align="center">
  <strong>Immersive proximity voice chat, built directly inside Among Us.</strong>
</p>

<p align="center">
  <a href="https://github.com/artriy/Perfect-Comms/releases/latest"><img src="https://img.shields.io/endpoint?style=for-the-badge&url=https%3A%2F%2Fgist.githubusercontent.com%2Fartriy%2Fb09ac2c39551270e9961b92e622b0893%2Fraw%2Flatest.json" alt="Latest release"></a>
  <a href="https://github.com/artriy/Perfect-Comms/releases"><img src="https://img.shields.io/endpoint?style=for-the-badge&url=https%3A%2F%2Fgist.githubusercontent.com%2Fartriy%2Fb09ac2c39551270e9961b92e622b0893%2Fraw%2Fdownloads.json" alt="Total downloads"></a>
</p>

<p align="center">
  <a href="#controls">Controls</a> &nbsp;·&nbsp;
  <a href="#install">Install</a> &nbsp;·&nbsp;
  <a href="#supported-mods">Supported Mods</a> &nbsp;·&nbsp;
  <a href="#for-mod-developers">For Mod Developers</a>
</p>

<p align="center">
  <img src="assets/brand/divider.svg" alt="" width="900">
</p>

Perfect Comms makes voice chat feel like part of the match. Players talk in-game, hear the people around them, find voice-ready lobbies, and play with voice rules that fit the way Among Us is actually played.

<br>

## Why Players Use It

- **Voice built into Among Us**, no Discord or mute bots
- **Extremely immersive proximity audio**
- **Optional Meetings & Lobby Only mode** for a simpler setup
- **Role-specific voice behavior**
- **Built-in voice lobby discovery**
- **Simple in-game controls**, plug and play

<br>

## How It Works

**Proximity by default.** Everyone talks through their own mic and hears each player by how close they are in-game, clear up close and quiet at a distance.

**The host tunes the round.** Hearing range, wall and vision occlusion, ghost and meeting rules, and a meetings-only mode are all host options, so each lobby plays how its host sets it.

<br>

## Supported Mods

Perfect Comms works on its own as a proximity voice mod. Some mods unlock extra voice behavior, integrations activate automatically when the mod is present and stay dormant when it is not.

| Mod | Voice behavior |
| :--- | :--- |
| **TOU-Mira** | Blackmailer, Jailor, Parasite / Puppeteer, Swooper, and Glitch mutes.<br>Crewpostor impostor voice rules.<br>Medium ghost voice modes.<br>Muffled hearing for Eclipsal, Grenadier, and Hypnotist effects.<br>Team Radio for Impostors, Vampires, and Lovers, with keybind cycling. |

<br>

## For Mod Developers

Making a roles mod? You can add your own voice behaviours to Perfect Comms **without forking it**. Reference `PerfectComms.dll` as a soft dependency and register your rules in `Load()`: mutes, private radio channels, relocated hearing, your own host-settings tab, and more.

```csharp
[BepInDependency("com.edgetel.perfectcomms", BepInDependency.DependencyFlags.SoftDependency)]
// in Load():
PerfectCommsApi.RegisterVoiceRule("com.me.mymod", ctx =>
    ctx.Phase == VoicePhaseKind.Meeting && MyRoles.IsGagged(ctx.Player)
        ? VoiceRuleResult.Mute("Gagged")
        : VoiceRuleResult.Pass);
```

Full guide, every primitive, and copy-paste examples are in the **[Mod Integration Wiki](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration)**.

<br>

## Settings

| Hosts set the match rules | Players set their own audio |
| :--- | :--- |
| Talk distance, falloff, and occlusion | Mic and speaker device |
| Vent, ghost, and meeting voice rules | Push to talk or open mic |
| Team Radio channels | Noise suppression, echo cancellation, auto gain |
| Role-based mutes (with supported mods) | Per-player volume and HUD layout |

<br>

## Controls

Defaults below. Every key is rebindable in **Voice Settings**.

| Action | Key | | Action | Key |
| :--- | :---: | :--- | :--- | :---: |
| Open voice menu | `F10` | | Toggle speaker | `N` |
| Open host voice settings | `F11` | | Player volumes | `B` |
| Mute / unmute mic | `M` | | Cycle team radio channel | `G` |
| Push to talk (hold) | `C` | | Refresh voice connection | `F7` |
| Team radio (hold) | `V` | | Refresh voice (host) | `F8` |

<br>

## Install

1. Install **BepInEx 6** (Unity IL2CPP build) into your Among Us folder, or grab **`PerfectComms+dependencies.zip`** from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest) which already bundles it.
2. Drop `PerfectComms.dll` into `BepInEx/plugins`.
3. Launch Among Us. Open Perfect Comms from the Options menu (`F10`). Hosts open Voice Settings from the lobby game-settings console (`F11`).

```text
BepInEx/
└─ plugins/
   └─ PerfectComms.dll
```

Perfect Comms is fully standalone. It installs in the same `BepInEx/plugins` folder as mods that do use Reactor or MiraAPI (such as TOU-Mira) and runs alongside them without conflict.

<br>

## Credits

- Original repo: [FangkuaiYa/AmongUs-VoiceChat](https://github.com/FangkuaiYa/AmongUs-VoiceChat)
- BetterCrewLink: [OhMyGuus/BetterCrewLink](https://github.com/OhMyGuus/BetterCrewLink)
- Interstellar: [Dolly1016/Interstellar](https://github.com/Dolly1016/Interstellar)
- Special thanks to [idkimneil](https://github.com/idkimneil), the reason I made this.

<div align="center">

<img src="assets/brand/divider.svg" alt="" width="900">

</div>

> Perfect Comms is an unofficial mod. It is not affiliated with Innersloth, Among Us, BepInEx, MiraAPI, Reactor, BetterCrewLink, Interstellar, or any supported mods.
