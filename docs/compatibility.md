---
description: "Version matching, supported game builds, platform differences, other mods and connection messages."
---

# Compatibility and versions

## Everyone needs the host's exact version

A Perfect Comms host admits only players running the **exact same Perfect Comms version**. Each joining player is checked while loading in, before appearing in the lobby:

- Different version: removed with **Perfect Comms version mismatch.** (lobby's version and theirs), then **Update Perfect Comms to join this lobby.**
- No Perfect Comms: removed with **This lobby requires Perfect Comms** (lobby's version), then **Install or enable Perfect Comms to join this lobby.**

With Reactor installed, Perfect Comms registers as required on all clients and Reactor performs the check with its own message. Keep Reactor on both sides or neither: a host without Reactor refuses a player whose game has Reactor, with the "requires Perfect Comms" message.

The desktop `PerfectComms.dll` and Starlight `PerfectCommsStarlight.dll` of one release share a version and play together.

Releases that change voice compatibility say so under **Compatibility** in the changelog. v4.1.12 introduced voice protocol 8 and host-settings format 5.

### Checking your version

- With Reactor, its main-menu and in-game credits list **Perfect Comms** with your version.
- The version-mismatch message names both versions.
- The [latest release](https://github.com/artriy/Perfect-Comms/releases/latest) page shows the newest version; the main-menu update card appears when one exists ([Updating](/install#updating)).

## Voice lobbies and "Wrong version"

In the **Voice lobbies** browser, **Wrong version** means the host's voice protocol differs from yours. A host on a different release with the same protocol is not marked, but still refuses you with the version-mismatch message. Update to the host's version to join.

## Lobbies whose host does not run Perfect Comms

There is no version check. Players with Perfect Comms still connect voice, but with no host settings, each waits about 10 seconds and then uses their own saved [Host voice settings](/host), so rules can differ per player. For consistent rules, have the host install Perfect Comms.

Voice does not start in Freeplay or in games hosted on this computer (`localhost` or a loopback address).

## Game versions

| Build | Game |
| :--- | :--- |
| Desktop `PerfectComms.dll` | Among Us v19 or newer, x64 Windows build. Current release targets Steam Among Us `2026.9.29`. Windows x86 not supported. |
| Android `PerfectCommsStarlight.dll` | Starlight on Android, built against `AmongUs.GameLibs.Android` `2026.8.18` and the Starlight `1.6.3` host contract. |

If a game update breaks hooks Perfect Comms needs, voice stays off and the HUD repeats **Perfect Comms voice disabled: critical game hooks are unavailable. Update the mod or game.** Install the release that supports your game version.

## Platform differences

| | Windows | Linux (Proton) | macOS (Wine/CrossOver) | Android (Starlight) |
| :--- | :---: | :---: | :---: | :---: |
| Package | `PerfectComms.dll` | `PerfectComms.dll` | `PerfectComms.dll` | `PerfectCommsStarlight.dll` |
| Audio engine | `PerfectCommsAudio.exe` helper | Native Linux helper | Native macOS helper app | Built into the DLL |
| Keybinds tab | Yes | Yes | Yes | No (touch buttons) |
| Speaker selector | Yes | Yes | Yes | No (follows the system audio route) |
| Noise Suppression and Echo Cancellation | Yes | Yes | Yes | No |
| OBS Compatibility Mode and OBS Recording Integration | Yes | No | No | No |

Under Proton or Wine, the helper runs as a native Linux or macOS process, not inside Wine. On native Windows, antivirus can block it; see [Antivirus blocks voice chat](/troubleshooting#antivirus-blocks-voice-chat).

## Other mods

Perfect Comms runs without MiraAPI, Reactor, or any role mod. Role mods add voice behavior through the [Perfect Comms API](/api/); their options appear under **Mod behaviour** in [Host voice settings](/host).

- **TOU-Mira:** v1.7.0 and later require Perfect Comms v4.1.7 or newer. Perfect Comms v4.1.6 and earlier support TOU-Mira only through v1.6.3.
- **Reactor:** optional. When present, it does the version check and lists Perfect Comms in its credits.
- Keep the BepInEx, MiraAPI, and Reactor files your modpack supplies. Do not install extra copies.

The API package is versioned separately; see [Mod Integration](/api/) for which mod versions each API version supports.

## Connection status messages

Shown above the voice controls when **HUD > Voice Connection Status** is on ([HUD tab](/settings#hud-tab)). The trailing dots animate.

| Message | Meaning |
| :--- | :--- |
| **Connecting voice... starting audio** | Your local audio engine is starting. |
| **Connecting voice... syncing session** | Waiting for the lobby's player list or the host's voice settings. |
| **Connecting voice... 3/5 players connected** | Some player connections are not up yet. Players without Perfect Comms count as not connected; incompatible protocols are left out. Hides after 30 seconds while the player list stays the same. |
| **Voice unavailable - retrying audio...** | The audio engine failed and is retrying. Also appears during a match. |
| **Antivirus blocked voice chat** / **Antivirus may be blocking voice** | See [Antivirus blocks voice chat](/troubleshooting#antivirus-blocks-voice-chat). |

The starting, syncing, and player-count messages appear only in the lobby.

**Perfect Comms voice is waiting for a confirmed lobby join. Leave and rejoin this lobby.** means exactly that. For other stuck connections, press <kbd>F7</kbd> (10-second cooldown).
