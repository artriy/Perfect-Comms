# Installing Perfect Comms

Perfect Comms is a standalone BepInEx plugin for Among Us. Its only hard dependency is **BepInEx**. It does **not** require MiraAPI or Reactor.

## Quick install (with dependencies)

1. Download the bundle from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest) that matches your platform:
   - `PerfectComms+dependencies x86.zip` for Steam and itch.io (`x86` / 32-bit).
   - `PerfectComms+dependencies x64.zip` for Epic Games Store and Microsoft Store (`x64` / 64-bit).
2. Extract it into your Among Us install folder (the folder containing `Among Us.exe`).
3. Launch the game. You should see Perfect Comms load in the BepInEx console.

Do not combine the x86 and x64 bundles. Both include **BepInEx and Perfect
Comms**. Neither includes MiraAPI, Reactor, or TOU-Mira - none of them are
needed for Perfect Comms to run.

## DLL-only install

If you already have BepInEx 6 (Unity IL2CPP) set up, just drop the plugin in:

```text
BepInEx/plugins/PerfectComms.dll
```

Use `PerfectComms.dll` for Windows.

On Android, hold the mic button to transmit in push-to-talk mode. For Team
Radio, tap its button to cycle channels or hold it to transmit. To keep a small
**Muted** / **Deafened** reminder visible, enable **Voice Settings > HUD > Mute /
Deafen Status Reminder**.

## Guided setup

On a new install, Perfect Comms walks through five steps: Welcome, Audio,
Controls, HUD, and Review. You can select and test audio devices, set talk mode
and shortcuts, preview a HUD preset with a live lobby mockup, then save all
choices together. Open **Voice Settings > Advanced > First-Time Setup** to run
the guide again without changing anything unless you finish and save.

## Verify it loaded

- A Perfect Comms voice HUD appears in-game.
- Hosts see a voice settings entry in the lobby (see [Host Settings](Host-Settings)).
- The main menu shows the voice lobby browser.

## Requirements

| Component | Needed by Perfect Comms | In either dependency bundle? |
| :--- | :--- | :--- |
| BepInEx (Unity IL2CPP 6) | Yes | Yes |
| MiraAPI | No | No |
| Reactor | No | No |
| TOU-Mira | No (optional - unlocks extra role voice behaviours when present) | No |

Perfect Comms detects supported mods (like TOU-Mira) at runtime and activates their voice behaviours only when they are installed. Those mods bring their own MiraAPI/Reactor; Perfect Comms never loads or requires them itself.

## Troubleshooting

- **No voice HUD:** confirm `PerfectComms.dll` is in `BepInEx/plugins` and the BepInEx console shows it loading.
- **Can't hear anyone:** check your mic/speaker device in the local settings, and that you are in a Perfect Comms lobby with other compatible clients.
- **Android mic never starts:** confirm the final APK manifest contains
  `android.permission.RECORD_AUDIO`, then grant microphone permission in Android settings. The DLL
  cannot add a permission to an APK after it has been signed.
- **Installing alongside a role mod:** Perfect Comms coexists with mods that use MiraAPI/Reactor (such as TOU-Mira) in the same `BepInEx/plugins` folder. Let that mod provide its own MiraAPI/Reactor; do not add duplicates.

See also: [Player Guide](Players) · [Host Settings](Host-Settings) · [Player Settings & Controls](Controls)
