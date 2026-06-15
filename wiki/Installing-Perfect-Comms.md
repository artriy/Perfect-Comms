# Installing Perfect Comms

Perfect Comms is a standalone BepInEx plugin for Among Us. Its only hard dependency is **BepInEx**. It does **not** require MiraAPI or Reactor.

## Quick install (with dependencies)

1. Download `PerfectComms+dependencies.zip` from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest).
2. Extract it into your Among Us install folder (the folder containing `Among Us.exe`).
3. Launch the game. You should see Perfect Comms load in the BepInEx console.

This bundle includes **BepInEx and `PerfectComms.dll` only**. It does not bundle MiraAPI, Reactor, or TOU-Mira - none of them are needed for Perfect Comms to run.

## DLL-only install

If you already have BepInEx 6 (Unity IL2CPP) set up, just drop the plugin in:

```text
BepInEx/plugins/PerfectComms.dll
```

Use `PerfectComms.dll` for Windows and `PerfectCommsAndroid.dll` for Android.

## Verify it loaded

- A Perfect Comms voice HUD appears in-game.
- Hosts see a voice settings entry in the lobby (see [Host Settings](Host-Settings)).
- The main menu shows the voice lobby browser.

## Requirements

| Component | Needed by Perfect Comms | In the zip? |
| :--- | :--- | :--- |
| BepInEx (Unity IL2CPP 6) | Yes | Yes |
| MiraAPI | No | No |
| Reactor | No | No |
| TOU-Mira | No (optional - unlocks extra role voice behaviours when present) | No |

Perfect Comms detects supported mods (like TOU-Mira) at runtime and activates their voice behaviours only when they are installed. Those mods bring their own MiraAPI/Reactor; Perfect Comms never loads or requires them itself.

## Troubleshooting

- **No voice HUD:** confirm `PerfectComms.dll` is in `BepInEx/plugins` and the BepInEx console shows it loading.
- **Can't hear anyone:** check your mic/speaker device in the local settings, and that you are in a Perfect Comms lobby with other compatible clients.
- **Installing alongside a role mod:** Perfect Comms coexists with mods that use MiraAPI/Reactor (such as TOU-Mira) in the same `BepInEx/plugins` folder. Let that mod provide its own MiraAPI/Reactor; do not add duplicates.

See also: [Host Settings](Host-Settings) - [Controls](Controls)
