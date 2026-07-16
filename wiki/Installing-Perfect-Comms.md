# Installing Perfect Comms

Perfect Comms is a standalone BepInEx plugin for Among Us. Its only hard dependency is **BepInEx**. It does **not** require MiraAPI or Reactor.

## Quick install (with dependencies)

1. Download the bundle from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest) that matches `Among Us.exe`:
   - `PerfectComms+dependencies-win-x86.zip` for a 32-bit game executable. The documented Steam 2026.3.31 target uses this bundle.
   - `PerfectComms+dependencies-win-x64.zip` for a compatible 64-bit game executable.
2. Extract it into your Among Us install folder (the folder containing `Among Us.exe`).
3. Launch the game. You should see Perfect Comms load in the BepInEx console.

Do not combine the x86 and x64 bundles. The architecture label must match the
game executable, and does not override the supported Among Us version. Both
bundles include **BepInEx and `PerfectComms.dll` only**. They do not bundle
MiraAPI, Reactor, or TOU-Mira - none of them are needed for Perfect Comms to
run.

## DLL-only install

If you already have BepInEx 6 (Unity IL2CPP) set up, just drop the plugin in:

```text
BepInEx/plugins/PerfectComms.dll
```

Use `PerfectComms.dll` for Windows. Android packagers use `PerfectCommsAndroid.dll` **and must
merge** [`release-assets/android/AndroidManifest.xml`](../release-assets/android/AndroidManifest.xml)
into the final APK manifest before signing and installing it. That fragment declares
`android.permission.RECORD_AUDIO`; placing the XML beside the DLL does not modify an existing APK.
Android will still ask the player for the runtime microphone permission. A player who denies it can
continue in receive-only mode.

On Android, hold the mic button to transmit in push-to-talk mode. For Team
Radio, tap its button to cycle channels or hold it to transmit. To keep a small
**Muted** / **Deafened** reminder visible, enable **Voice Settings > HUD > Mute /
Deafen Status Reminder**.

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

See also: [Host Settings](Host-Settings) - [Controls](Controls)
