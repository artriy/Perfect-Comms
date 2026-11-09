---
description: "Install Perfect Comms on Windows, Linux (Proton), macOS (Wine) or Android (Starlight), then update or uninstall it."
---

# Installing Perfect Comms

Perfect Comms is a standalone voice plugin for Among Us: BepInEx on desktop, Starlight on Android. The desktop download is the plugin DLL only; it does not include BepInEx.

## Desktop

Open the folder that contains `Among Us.exe` (on Steam: right-click Among Us, then **Manage** > **Browse local files**) and pick the card that matches it.

<div class="install-paths">
<section class="install-path">

### There is a `BepInEx` folder

```text
Among Us/
├─ Among Us.exe
└─ BepInEx/
```

Your mod or modpack already provides BepInEx.

1. Close Among Us.
2. Keep that BepInEx. **Do not install or merge another copy over it**; that can replace the pack's loader, runtime, patchers, or configuration and break it.
3. Download [`PerfectComms.dll`](https://github.com/artriy/Perfect-Comms/releases/latest/download/PerfectComms.dll) (latest release).
4. Place or replace the DLL in `BepInEx/plugins`.

</section>
<section class="install-path">

### There is no `BepInEx` folder

```text
Among Us/
└─ Among Us.exe
```

1. Download **BepInEx 6 Unity IL2CPP** from the [official BepInEx build page](https://builds.bepinex.dev/projects/bepinex_be).
2. Choose `Unity.IL2CPP-win-x64` (Windows, Linux/Proton, or macOS/Wine). Windows x86 is not supported.
3. Extract BepInEx into the folder containing `Among Us.exe`.
4. Launch the game once to finish BepInEx setup, then close it.
5. Download [`PerfectComms.dll`](https://github.com/artriy/Perfect-Comms/releases/latest/download/PerfectComms.dll) (latest release) and place it in `BepInEx/plugins`.

</section>
</div>

Either way, you end up with:

```text
BepInEx/
└─ plugins/
   └─ PerfectComms.dll
```

More BepInEx detail: [official IL2CPP installation guide](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html).

## Android on Starlight

The Android build is one self-contained file, `PerfectCommsStarlight.dll` (not an APK, no native payload). Do not add other DLLs or archives.

Public Starlight builds do not load unapproved local mods, so use one of the paths in the [Starlight developer testing guide](https://allofus.dev/guides/starlight-dev-guide/#testing):

1. Send the DLL to All Of Us staff for approval and testing; or
2. upload or copy it with a Starlight beta/local-mod testing build available to approved beta testers.

Follow the placement instructions from All Of Us staff or the testing build. Grant microphone permission when Starlight asks. Starlight and desktop players can share a voice lobby.

## Verify it loaded

- A new install opens the four-step [first-time setup](/guide#first-time-setup), and a **Voice settings** button is in the Among Us Options menu.
- A voice HUD appears in a lobby or game.
- Hosts see **Host voice settings** at the lobby game-settings console.
- The main menu shows the voice lobby browser button.

If these are missing on desktop, check `BepInEx/LogOutput.log`. On Starlight, follow the testing build's log instructions. Common fixes are in [Troubleshooting](/troubleshooting).

## Requirements

Desktop needs **BepInEx 6 (Unity IL2CPP)**, which is not included: keep your modpack's copy or install it as above. MiraAPI, Reactor, and TOU-Mira are not required; supported mods such as TOU-Mira are detected at runtime and bring their own dependencies.

Desktop runs on Windows, Linux/Proton, and macOS/Wine with the x64 Windows game build (latest release targets Steam Among Us `2026.9.29`). Windows x86 is not supported. Exact versions are in [Compatibility](/compatibility).

On Linux/Proton and macOS/Wine, Perfect Comms runs a native audio helper outside Wine, unpacked into `<Among Us folder>\cache\PerfectComms\native`. In sandboxed Wine prefixes (such as GameHub on macOS) it goes into the prefix's `%LOCALAPPDATA%\PerfectComms\native`, with its temporary files beside it.

## Updating

When a newer release exists, a main-menu card appears: **View release** opens the release page and **Dismiss** hides the card for that menu visit. The card waits while a Perfect Comms panel or first-time setup is open. Updates are never installed automatically.

- **Desktop:** close the game and replace `BepInEx/plugins/PerfectComms.dll`. Leave BepInEx unchanged.
- **Android on Starlight:** get the updated `PerfectCommsStarlight.dll` through the approved testing channel and upload or copy that one file per the testing build's instructions.

Settings survive the update. They live in one user-wide file in the game's persistent data folder, shared by every Among Us installation on the same account.

## Uninstalling

- **Desktop:** close the game and remove `BepInEx/plugins/PerfectComms.dll`.
- **Android on Starlight:** remove `PerfectCommsStarlight.dll` through the Starlight beta or approved testing process.

Optionally delete the settings file, `PerfectComms/com.edgetel.perfectcomms.cfg` in the game's persistent data folder (on Windows, `%USERPROFILE%\AppData\LocalLow\Innersloth\Among Us`). If that folder was unavailable, Perfect Comms used `BepInEx/config/com.edgetel.perfectcomms.cfg`; `BepInEx/LogOutput.log` names the file in use.

Do not remove shared BepInEx files that other mods still use.
