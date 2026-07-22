# Installing Perfect Comms

Perfect Comms is a standalone BepInEx plugin for Among Us. Its only hard dependency is **BepInEx**. It does **not** require MiraAPI or Reactor.

## Choose the correct download

Download from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest).

| Your setup | Download |
| :--- | :--- |
| Fresh Steam or itch.io install on Windows | `PerfectComms+dependencies-win-x86-steam-itch.zip` |
| Fresh Epic Games Store or Microsoft Store install on Windows | `PerfectComms+dependencies-win-x64-epic-msstore.zip` |
| Windows with BepInEx 6 Unity IL2CPP already installed | `PerfectComms.dll` |
| BepInEx-enabled ARM64 Android mod build | `PerfectCommsAndroid.dll` |

The release does not contain a ready-to-install Android APK or an Android dependency bundle.

## Windows quick install (with dependencies)

1. Close Among Us.
2. Download the dependency bundle for your store.
3. Extract the archive directly into the Among Us folder that contains `Among Us.exe`.
4. Confirm that `winhttp.dll` is beside `Among Us.exe` and that `BepInEx/plugins/PerfectComms.dll` exists.
5. Launch the game. A new installation opens the guided Perfect Comms setup from the main menu.

Do not combine the x86 and x64 bundles, and do not extract the archive into an extra nested folder. Both bundles include the correct BepInEx build and Perfect Comms for their target store. Neither includes MiraAPI, Reactor, or TOU-Mira.

The bundled BepInEx configuration intentionally keeps its separate console window off. Perfect Comms can be running normally without one.

## Windows DLL-only install

Use this route only when BepInEx 6 Unity IL2CPP is already installed for that copy of Among Us:

```text
BepInEx/plugins/PerfectComms.dll
```

Close the game, place or replace `PerfectComms.dll` at that path, then launch again. Do not use `PerfectCommsAndroid.dll` or either Windows dependency bundle on Android.

## Android DLL install (advanced)

`PerfectCommsAndroid.dll` is for an existing **BepInEx-enabled ARM64 Android** mod build. It is not an APK and cannot be installed by tapping the DLL.

1. Start with an Android Among Us mod pack or APK-building workflow that already provides BepInEx.
2. Add `PerfectCommsAndroid.dll` through that workflow's plugin step so the built app contains `BepInEx/plugins/PerfectCommsAndroid.dll`.
3. Before the APK is signed, make sure its real manifest declares `android.permission.RECORD_AUDIO`.
4. Install the rebuilt APK and grant microphone permission when Android asks.

Do not use the desktop `PerfectComms.dll` on Android. Copying the DLL or a manifest fragment beside an already signed or already installed APK does not add the plugin or microphone permission; the APK must be rebuilt and signed through the Android mod workflow.

## Guided setup

On a new install, Perfect Comms walks through five steps: Welcome, Audio, Controls, HUD, and Review. You can select and test audio devices, set talk mode and shortcuts, choose whether desktop keybinds remain active while chat is open, choose whether to show the voice controls, lobby connection status, speaking bar, and meeting overlay, preview a visible speaking bar with a live lobby mockup, then save all choices together. Open **Voice Settings > Advanced > First-Time Setup** to run the guide again without changing anything unless you finish and save.

## Verify it loaded

- The guided setup appears on a fresh install, and **Voice Settings** is available from the Among Us Options menu afterward.
- A Perfect Comms voice HUD appears in a lobby or game.
- Hosts see **Host Voice Settings** at the lobby game-settings console (see [Host Settings](Host-Settings)).
- The main menu shows **Voice Lobbies**.

If those are missing, use `BepInEx/LogOutput.log` rather than waiting for a console window.

## Requirements

| Component | Needed by Perfect Comms | Included? |
| :--- | :--- | :--- |
| BepInEx 6 (Unity IL2CPP) | Yes | Included in both Windows dependency bundles; Android must provide it separately |
| MiraAPI | No | No |
| Reactor | No | No |
| TOU-Mira | No; optional role integration | No |

Perfect Comms v4.1.1 is built against Among Us `2026.3.31` and BepInEx Unity IL2CPP `6.0.0-be.735`. Perfect Comms detects supported mods such as TOU-Mira at runtime and enables their voice behavior only when they are present. Those mods provide their own MiraAPI or Reactor dependencies; Perfect Comms does not load or require them.

## Updating

The in-game update notice opens the latest-release page; it does not install updates automatically.

- **Windows DLL-only:** close the game and replace `BepInEx/plugins/PerfectComms.dll`.
- **Windows dependency bundle:** download the correct store/architecture bundle and extract it to the same game folder. Do not switch architectures or combine bundles.
- **Android:** rebuild and reinstall the modded APK with the new `PerfectCommsAndroid.dll`.

Saved settings remain in the BepInEx config folder when the plugin DLL is replaced.

## Uninstalling

Close the game, then remove `BepInEx/plugins/PerfectComms.dll` on Windows or `BepInEx/plugins/PerfectCommsAndroid.dll` from the Android mod build. Optionally delete `BepInEx/config/com.edgetel.perfectcomms.cfg` to remove saved Perfect Comms settings.

Do not remove shared BepInEx files when other installed mods still use BepInEx.

## Troubleshooting

- **No Voice Settings or voice HUD:** confirm the plugin DLL is directly inside `BepInEx/plugins`, not one folder deeper. On Windows, also confirm that the dependency bundle matches the store and architecture.
- **No console window:** this is expected with the dependency bundles. Check `BepInEx/LogOutput.log` for Perfect Comms load errors.
- **Can't hear anyone:** select the intended microphone and speaker in Voice Settings, check operating-system microphone permission, and confirm the other players use a compatible Perfect Comms version.
- **Windows helper is blocked:** check whether security software quarantined a Perfect Comms audio helper, then restore or allow it only if it came from the official release.
- **Android mic never starts:** confirm the rebuilt APK's manifest contains `android.permission.RECORD_AUDIO`, then grant microphone permission in Android settings.
- **Installing alongside a role mod:** let that mod provide its own MiraAPI or Reactor files. Do not add duplicate copies for Perfect Comms.

See also: [Player Guide](Players) · [Host Settings](Host-Settings) · [Player Settings & Controls](Controls)
