# Installing Perfect Comms

Perfect Comms is a standalone BepInEx plugin for Among Us. It does **not** require MiraAPI or Reactor.

> Starting with v4.1.7, Perfect Comms is distributed as a plugin DLL only. It does not include BepInEx.

> [!TIP]
> **If your mod provides BepInEx**
>
> 1. Close Among Us.
> 2. Keep the BepInEx installation supplied by the mod or modpack. **Do not install or merge another copy over it.**
> 3. Download `PerfectComms.dll` from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest).
> 4. Place or replace the DLL in `BepInEx/plugins`.
>
> Installing another BepInEx copy over a modpack can replace its loader, runtime, patchers, or configuration and break the pack.

> [!IMPORTANT]
> **If BepInEx is not provided**
>
> Use this path if your mod does not provide BepInEx, or if you are not using another mod.
>
> 1. Download **BepInEx 6 Unity IL2CPP** from the [official BepInEx build page](https://builds.bepinex.dev/projects/bepinex_be).
> 2. Choose the build for your platform:
>    - **Steam or itch.io:** `Unity.IL2CPP-win-x86`
>    - **Epic Games Store or Microsoft Store:** `Unity.IL2CPP-win-x64`
> 3. Extract BepInEx directly into the folder containing `Among Us.exe`.
> 4. Launch the game once to complete BepInEx setup, then close it.
> 5. Download `PerfectComms.dll` from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest) and place it in `BepInEx/plugins`.

For more BepInEx setup details, see the [official IL2CPP installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html).

## Final folder layout

```text
BepInEx/
└─ plugins/
   └─ PerfectComms.dll
```

Launch Among Us. A new Perfect Comms installation opens its guided setup from the main menu.

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
| BepInEx 6 (Unity IL2CPP) | Yes | No; keep the version provided by your modpack or download it from the official build page |
| MiraAPI | No | No |
| Reactor | No | No |
| TOU-Mira | No; optional role integration | No |

Perfect Comms v4.1.7 targets Among Us `2026.3.31` and BepInEx 6 Unity IL2CPP. Perfect Comms detects supported mods such as TOU-Mira at runtime and enables their voice behavior only when they are present. Those mods provide their own MiraAPI or Reactor dependencies; Perfect Comms does not load or require them.

## Updating

The in-game update notice opens the latest-release page; it does not install updates automatically.

- **Desktop:** close the game and replace `BepInEx/plugins/PerfectComms.dll`. Keep the existing BepInEx installation unchanged.
- **Android:** rebuild and reinstall the modded APK with the new `PerfectCommsAndroid.dll`.

Saved settings remain in the BepInEx config folder when the plugin DLL is replaced.

## Uninstalling

Close the game, then remove `BepInEx/plugins/PerfectComms.dll` on Windows or `BepInEx/plugins/PerfectCommsAndroid.dll` from the Android mod build. Optionally delete `BepInEx/config/com.edgetel.perfectcomms.cfg` to remove saved Perfect Comms settings.

Do not remove shared BepInEx files when other installed mods still use BepInEx.

## Troubleshooting

- **No Voice Settings, voice HUD, or Voice Lobbies:** confirm the plugin DLL is directly inside `BepInEx/plugins`, not one folder deeper. Then check `BepInEx/LogOutput.log` for load errors.
- **No console window:** console visibility is controlled by the BepInEx installation supplied by your mod or modpack. Perfect Comms does not require a separate console; use `BepInEx/LogOutput.log`.
- **Can't hear anyone:** confirm you are not deafened, check the selected speaker, Speaker Volume, and per-player volume sliders, and confirm the other players use a compatible Perfect Comms version.
- **Others can't hear you:** confirm you are not muted, hold Push To Talk if selected, check the selected microphone and Mic Volume, and grant operating-system microphone permission.
- **Connection is stuck:** press `F7` to rebuild your local voice session. Refresh has a 10-second cooldown.
- **Windows helper is blocked:** check whether security software quarantined a Perfect Comms audio helper, then restore or allow it only if it came from the official release.
- **Android mic never starts:** confirm the rebuilt APK's manifest contains `android.permission.RECORD_AUDIO`, then grant microphone permission in Android settings.
- **Installing alongside a role mod:** keep the BepInEx, MiraAPI, and Reactor files supplied by that mod. Do not install duplicate copies for Perfect Comms.

See also: [Player Guide](Players) · [Host Settings](Host-Settings) · [Player Settings & Controls](Controls)
