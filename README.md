<h1 align="center">Perfect Comms</h1>

<p align="center">
  <strong>Proximity chat built directly into Among Us.</strong><br>
  Clean in-game voice, protected lobbies, one-click voice lobby discovery, and mod-specific behaviours.
</p>

<p align="center">
  <img src="assets/brand/divider.svg" alt="divider" width="900">
</p>

Perfect Comms is an in-game proximity voice chat mod for Among Us. It adds native voice, in-game controls, voice lobbies, and meeting/task-phase rules without needing a separate voice app.

## Why Perfect Comms

- **Fully integrated proximity chat** — voice runs directly inside Among Us with in-game controls.
- **Game-aware voice rules** — voice changes between lobby, tasks, meetings, exile, and endgame.
- **No more hackers messing with the voice** — voice stays tied to compatible Perfect Comms clients, so lobby audio is harder to disrupt or fake.
- **Voice Lobbies built in** — find compatible voice lobbies from the main menu and join with one click.
- **Mod-specific behaviours** — extra voice rules can activate when compatible mods are installed.
- **Update-ready** — players can be sent straight to the newest release when a new build drops.

## What makes it must-use

Perfect Comms turns voice chat into part of the match instead of a side tool.

| Old way                        | Perfect Comms                                        |
| ------------------------------ | ---------------------------------------------------- |
| Separate voice app             | Voice inside Among Us                                |
| Manual lobby sharing           | Built-in Voice Lobbies                               |
| Role rules ignored             | Supported mod behaviours                             |
| Hackers messing with the voice | Voice stays tied to compatible Perfect Comms clients |
| Players miss updates           | In-game update prompt                                |

## Supported mod behaviours

Perfect Comms works as its own Among Us proximity voice chat mod.

Some mods get extra voice behaviours when installed:

| Mod      | Supported behaviours                                                    |
| -------- | ----------------------------------------------------------------------- |
| TOU-Mira | Blackmailed stays muted. Jailed muted automatically; Jailor can unmute. |

## Install

1. Download `PerfectComms.dll`, or use `PerfectComms+dependencies.zip` if you need BepInEx, MiraAPI, and Reactor included.
2. Put `PerfectComms.dll` in `BepInEx/plugins`, or extract `PerfectComms+dependencies.zip` into your Among Us install folder.
3. A DLL-only install should look like this:

```text
BepInEx/plugins/PerfectComms.dll
```

DLL-only installs require your mod pack to already include MiraAPI and Reactor. `PerfectComms+dependencies.zip` includes BepInEx, MiraAPI, and Reactor, but does not include TOU-Mira.

## Build

```bash
dotnet build PerfectComms.csproj -c Release --nologo
```

Output:

```text
bin/Release/net6.0/PerfectComms.dll
```

For GitHub release uploads, use `PerfectComms.dll` for Windows and `PerfectCommsAndroid.dll` for Android.

## Notice

Perfect Comms is an unofficial mod. It is not affiliated with Innersloth, Among Us, BepInEx, MiraAPI, Reactor, or any supported mods.

## Credits

- Original repo: [FangkuaiYa/AmongUs-VoiceChat](https://github.com/FangkuaiYa/AmongUs-VoiceChat)
- Special thanks to [idkimneil](https://github.com/idkimneil) — the reason I made this.

<p align="center">
  <img src="assets/brand/divider.svg" alt="divider" width="900">
</p>
