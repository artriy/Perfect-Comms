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


## Settings

| Hosts set the match rules | Players set their own audio |
| :--- | :--- |
| Talk distance, falloff, and occlusion | Mic and speaker device |
| Vent, ghost, and meeting voice rules | Open mic, push to talk, and an optional noise gate |
| Team Radio channels | Meeting spatial audio, plus noise suppression and echo cancellation on desktop |
| Role-based mutes (with supported mods) | Per-player volume and HUD layout |

<br>

## Controls

### Desktop shortcuts

| Key | Action |
| :---: | :--- |
| `Right Alt` | Mute / unmute microphone |
| `Right Ctrl` | Deafen / undeafen |
| Hold `C` | Push To Talk |
| Hold `V` | Team Radio |
| `G` | Cycle Team Radio channel |
| `Shift+B` | Open Player Volumes |
| `F7` | Refresh voice connection |
| `F10` | Open Voice Settings |
| `F11` | Open Host Voice Settings |

> [!NOTE]
> **Optional shortcuts are off by default.**
>
> Assign any of these in **Voice Settings > Keybinds**:
> - **Push To Mute** (hold)
> - **Toggle Open Mic / Push To Talk**
> - **Alive Louder / Dead Quieter** (hold)
> - **Alive Quieter / Dead Louder** (hold)

> [!TIP]
> **First time using Perfect Comms?**
> - **Open Mic** is the default: your voice sends automatically when you speak.
>   Tap `Right Alt` to mute or unmute your microphone.
> - **Push To Talk:** select it in **Voice Settings > Audio**, then hold `C`
>   whenever you want to speak.
> - **Deafen:** tap `Right Ctrl` to stop hearing voice and pause your microphone
>   until you undeafen.
> - **Team Radio:** when your role and the host settings allow a private channel,
>   hold `V` to talk and press `G` to cycle available channels.

**Platform notes**

- **Desktop:** Press and release `Right Alt` to mute or unmute, and `Right Ctrl`
  to deafen or undeafen. If you press either one together with another key,
  Perfect Comms does not mute or deafen. Both controls work while chat is open
  by default; choose which other shortcuts work there from **Voice Settings >
  Keybinds**.
- **HUD reminder:** Enable **Voice Settings > HUD > Mute / Deafen Status
  Reminder** to keep your muted or deafened state visible.

## Install

> Starting with v4.1.7, the desktop build is distributed as a plugin DLL only. It does not include BepInEx.

> [!TIP]
> **Desktop: if your mod provides BepInEx**
>
> 1. Close Among Us.
> 2. Keep the BepInEx installation supplied by the mod or modpack. **Do not install or merge another copy over it.**
> 3. Download `PerfectComms.dll` from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest).
> 4. Place or replace the DLL in `BepInEx/plugins`.

> [!IMPORTANT]
> **Desktop: if BepInEx is not provided**
>
> Use this path if your mod does not provide BepInEx, or if you are not using another mod.
>
> 1. Download **BepInEx 6 Unity IL2CPP** from the [official BepInEx build page](https://builds.bepinex.dev/projects/bepinex_be).
> 2. Choose the build for your platform:
>    - **Steam or itch.io:** `Unity.IL2CPP-win-x86`
>    - **Epic Games Store or Microsoft Store:** `Unity.IL2CPP-win-x64`
> 3. Extract BepInEx into the folder containing `Among Us.exe`.
> 4. Launch the game once to complete BepInEx setup, then close it.
> 5. Download `PerfectComms.dll` from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest) and place it in `BepInEx/plugins`.

### Desktop folder layout

```text
BepInEx/
└─ plugins/
   └─ PerfectComms.dll
```

### Android on Starlight

The managed Android tester artifact is `PerfectCommsStarlight.dll`. It is a
self-contained managed assembly: the required managed media stack is merged
into the DLL, and the managed dependency notices and complete SIPSorcery terms
are embedded in it. It has no companion DLLs.

Use `PerfectCommsStarlight.dll` only through an All Of Us staff-approved test
or a Starlight beta/local-mod testing build, following the
[Starlight developer testing guide](https://allofus.dev/guides/starlight-dev-guide/#testing).
This testing path does not imply public Starlight availability or approval.
There is no Perfect Comms APK or native Android payload. No APK, manifest, or
native-library staging is involved.

The Starlight build interoperates with desktop `PerfectComms.dll` players in
the same voice lobby. The desktop build and its native media stack are
unchanged.

Launch Among Us and open Perfect Comms from the Options menu. On desktop, `F10`
opens Voice Settings and `F11` opens Host Voice Settings.

Perfect Comms installs beside mods that use Reactor or MiraAPI (such as TOU-Mira) without replacing their loader or dependencies. For BepInEx setup help, see the [official IL2CPP installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html).

<br>

## For Mod Developers

Making a roles mod? You can add your own voice behaviours to Perfect Comms **without forking it**: mutes, private routes, persistent host options, concealment-safe overlays, animated colors, and managed Team Radio that reuses Perfect Comms' selector/PTT/network path. Compile against the small reference-only API package; it never installs or copies the Perfect Comms runtime into your mod:

```xml
<PackageReference Include="PerfectComms.Api"
                  Version="4.1.7.1"
                  PrivateAssets="all" />
```

`4.1.7.1` is the corrected API-package revision for the Perfect Comms 4.1.7 runtime; the player-facing mod version remains 4.1.7. Players still install Perfect Comms separately. Declare it as a soft dependency and register your rules only when it is present:

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

## Credits

- Original repo: [FangkuaiYa/AmongUs-VoiceChat](https://github.com/FangkuaiYa/AmongUs-VoiceChat)
- BetterCrewLink: [OhMyGuus/BetterCrewLink](https://github.com/OhMyGuus/BetterCrewLink)
- Peer transport: [Pion WebRTC](https://github.com/pion/webrtc)
- Special thanks to [idkimneil](https://github.com/idkimneil), the reason I made this.

<div align="center">

<img src="assets/brand/divider.svg" alt="" width="900">

</div>

> Perfect Comms is an unofficial mod. It is not affiliated with Innersloth, Among Us, BepInEx, MiraAPI, Reactor, BetterCrewLink, or any supported mods.
