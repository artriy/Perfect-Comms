# Installing Perfect Comms

Perfect Comms is a standalone BepInEx plugin for Among Us. Its only hard dependency is **BepInEx**. It does **not** require MiraAPI or Reactor.

## Choose the correct download

Download from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest).

| Your setup | Download |
| :--- | :--- |
| Desktop Among Us with BepInEx 6 Unity IL2CPP | `PerfectComms.dll` |
| Fresh desktop Among Us installation | Install BepInEx first, then download `PerfectComms.dll` |
| BepInEx-enabled ARM64 Android mod build | `PerfectCommsAndroid.dll` |

Starting with v4.1.7, desktop releases do not bundle BepInEx. The release does not contain a ready-to-install Android APK or an Android dependency bundle.

## Windows install

1. Close Among Us.
2. If another mod or modpack already provides or requires BepInEx, keep that exact installation. **Do not install or merge a second copy of BepInEx over it.**
3. If this is a fresh, unmodded installation, install the tested **BepInEx 6.0.0-be.735 Unity IL2CPP** build matching your Among Us executable:
   - [Windows x86](https://builds.bepinex.dev/projects/bepinex_be/735/BepInEx-Unity.IL2CPP-win-x86-6.0.0-be.735%2B5fef357.zip) for Steam and itch.io.
   - [Windows x64](https://builds.bepinex.dev/projects/bepinex_be/735/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.735%2B5fef357.zip) for Epic Games Store and Microsoft Store.

   Extract BepInEx directly into the folder containing `Among Us.exe`, launch once to finish its first-run setup, and close the game. IL2CPP builds are distributed through the official [BepInEx Bleeding Edge page](https://builds.bepinex.dev/projects/bepinex_be); use the links above for the version tested with Perfect Comms. The [official IL2CPP installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html) has additional details.
4. Place or replace the downloaded DLL at:

```text
BepInEx/plugins/PerfectComms.dll
```

5. Launch Among Us. A new Perfect Comms installation opens its guided setup from the main menu.

Installing another BepInEx copy over a modpack can replace its loader, runtime, patchers, or configuration and break the pack. Perfect Comms therefore distributes only its plugin DLL and uses the BepInEx installation selected by the player or modpack.

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
| BepInEx 6 (Unity IL2CPP) | Yes | No; use the version supplied by your modpack or install the tested upstream build |
| MiraAPI | No | No |
| Reactor | No | No |
| TOU-Mira | No; optional role integration | No |

Perfect Comms v4.1.7 is built against Among Us `2026.3.31` and BepInEx Unity IL2CPP `6.0.0-be.735`. Perfect Comms detects supported mods such as TOU-Mira at runtime and enables their voice behavior only when they are present. Those mods provide their own MiraAPI or Reactor dependencies; Perfect Comms does not load or require them.

## Updating

The in-game update notice opens the latest-release page; it does not install updates automatically.

- **Desktop:** close the game and replace `BepInEx/plugins/PerfectComms.dll`. Keep the existing BepInEx installation unchanged.
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
