# Changelog

## Perfect Comms v2.0.3

This Perfect Comms release focuses on voice stability, speaking ring accuracy, noise suppression, and cleaner in-game controls.

### What's Changed

- Added volume controls sliders by @idkimneil in [#4](https://github.com/artriy/Perfect-Comms/pull/4).
- Reduced mute/unmute spam crashes with serialized mic capture transitions.
- Added RNNoise noise suppression, enabled by default.
- Added host and local voice refresh keybinds.
- Added MB4/MB5 mouse button bind support (in local settings).
- Fixed comms sabotage voice blocking detection.
- Fixed rainbow, idle-pose, and morph/mimic speaking ring portraits.
- Added Middle Left and Middle Right speaking bar positions.
- Improved voice overlay positioning, layout, scale, and defaults.
- Improved the player volume menu and slider behavior.
- Deafened players now stop transmitting voice too.
- Removed `.DS_Store`.

See `docs/release-notes-v2.0.3.md` for the full release notes.

## Perfect Comms v2.0.2

### What's Changed

- Fixed voice keybind behavior while chat is open so toggle shortcuts stay blocked during typing, while push-to-talk and impostor radio can still activate after a short hold.
- Fixed printable push-to-talk and radio keys in chat so quick taps type normally, but held voice keys do not spam characters into the message field.
- Fixed push-to-talk chat handling so it only applies when Mic Mode is set to Push To Talk.
- Fixed impostor radio chat handling so it only applies when the local player can actually use the radio, including blackmailer and jailor voice-block rules.
- Changed Push To Talk mode so the mute toggle no longer creates a redundant manual mute state; released PTT remains muted and holding PTT still transmits.
- Fixed the `VC: mic unavailable` warning appearing during normal Push To Talk idle mute.

See `docs/release-notes-v2.0.2.md` for the full release notes.

## Perfect Comms v2.0.1

### What's Changed

- Changed the assets to a cleaner version by @AtonyGit in #1.
- Fixed the Voice Lobbies close X so it sits in a clearer top-right position and is easier to see.
- Fixed bottom-positioned speaking indicators so player names no longer clip off the bottom of the screen while staying close to the edge.

See `docs/release-notes-v2.0.1.md` for the full release notes.

## Perfect Comms v2.0.0

This is the backend rewrite release. Perfect Comms no longer depends on Among Us RPCs for voice audio transport. Voice now runs through selectable voice backends, with BetterCrewLink live voice as the default path and Interstellar available as an alternate backend.

### Most Notable Changes

- BetterCrewLink backend + lobby browser is now fully built in.
- Interstellar backend is now fully supported.
- Voice transport no longer depends on Among Us RPCs.
- Public lobby browsing and publishing are much better.
- Directional and proximity audio are more reliable.
- Host settings now sync across the lobby automatically.
- Debug logs are quiet by default unless you turn them on.

See `docs/release-notes-v2.0.0.md` for the full technical changelog.

## Perfect Comms v1.0.2

### Changed

- Reposition Perfect Comms as its own Among Us proximity chat mod.
- Document TOU-Mira as a supported mod behaviour instead of a requirement.
- Add `PerfectComms+dependencies.zip` with BepInEx, MiraAPI, Reactor, and Perfect Comms.
- Cache supported-mod role state for 0.25 seconds instead of checking every voice snapshot.

### Fixed

- Missing TOU-Mira role/modifier types now no-op and retry instead of being cached as unavailable.
- Default voice lobby title now uses `Perfect Comms` instead of TOU-Mira-specific wording.

## Perfect Comms v1.0.1

### Changed

- Update notifications now check GitHub releases directly for future releases.

### Fixed

- Speaking indicators now render above the Among Us game UI instead of being hidden behind menus or scene elements.
- Speaking-ring player icons now show a stable recolored crewmate body with loaded cosmetics, without cloning live player objects.

## Perfect Comms v1.0.0

**PUBLIC BETA — expect bugs.**  
This is the first public Perfect Comms release. It is ready for real lobbies, but still needs wider testing.

### What makes this release special

- **Fully integrated proximity chat** — voice runs inside Among Us, no separate voice app needed.
- **Supported mod behaviours** — TOU-Mira blackmailed players stay muted, and Jailor can unmute jailed players.
- **No more hackers messing with the voice** — voice stays tied to compatible Perfect Comms clients.
- **Built-in Voice Lobbies** — find compatible voice lobbies from the main menu and join with one click.
- **In-game update prompt** — players can be sent straight to the newest release when an update drops.

### Added

- Native Perfect Comms BepInEx plugin.
- Windows release build: `PerfectComms.dll`.
- Android release build: `PerfectCommsAndroid.dll`.
- Dependency bundle: `PerfectComms+dependencies.zip` with BepInEx, MiraAPI, Reactor, and Perfect Comms.
- Proximity voice during gameplay.
- Meeting voice behavior for modded role states.
- Jailor-controlled jailed voice support.
- Blackmailer voice blocking support.
- Voice Lobby button and lobby browser.
- Compatible-client voice checks.
- Reactor mod list entry for Perfect Comms.
- Clickable main-menu update notification.

### Beta notes

- **Expect bugs in some lobbies.** Report anything weird to me.
- 15-player support is designed and hardened, but still needs more real public-lobby testing.
- Everyone in the lobby should use the same Perfect Comms release.

### Install

Download `PerfectComms.dll` and place it here:

```text
BepInEx/plugins/PerfectComms.dll
```

Requires MiraAPI and Reactor from your mod pack. Supported mod behaviours activate only when matching mods are installed.
