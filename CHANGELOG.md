# Changelog

## Perfect Comms v2.0.0

This is the backend rewrite release. Perfect Comms no longer depends on Among Us RPCs for voice audio transport. Voice now runs through selectable voice backends, with BetterCrewLink live voice as the default path and Interstellar available as an alternate backend.

### Why You Should Update

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
