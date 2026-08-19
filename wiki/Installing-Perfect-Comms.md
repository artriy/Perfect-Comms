# Installing Perfect Comms

Perfect Comms is a standalone voice plugin for Among Us. The desktop build runs on BepInEx; the managed Android build runs on Starlight. It does **not** require MiraAPI or Reactor.

> Starting with v4.1.7, the desktop build is distributed as a plugin DLL only. It does not include BepInEx.

> [!TIP]
> **Desktop: if your mod provides BepInEx**
>
> 1. Close Among Us.
> 2. Keep the BepInEx installation supplied by the mod or modpack. **Do not install or merge another copy over it.**
> 3. Download `PerfectComms.dll` from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest).
> 4. Place or replace the DLL in `BepInEx/plugins`.
>
> Installing another BepInEx copy over a modpack can replace its loader, runtime, patchers, or configuration and break the pack.

> [!IMPORTANT]
> **Desktop: if BepInEx is not provided**
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

## Desktop folder layout

```text
BepInEx/
└─ plugins/
   └─ PerfectComms.dll
```

Launch Among Us. A new Perfect Comms installation opens its guided setup from the main menu.

## Android on Starlight

The managed Android tester build is exactly one file:
`PerfectCommsStarlight.dll`. It contains the plugin, managed media stack,
pinned managed dependencies, and required legal notices. Do not add other DLLs
or archives when submitting, sharing, or installing it.

Public Starlight builds do not load unapproved local mods. Use
`PerfectCommsStarlight.dll` only through one of the testing paths documented in
the [Starlight developer testing guide](https://allofus.dev/guides/starlight-dev-guide/#testing):

1. Send that single DLL to All Of Us staff for approval and testing; or
2. upload or copy that single DLL with a Starlight beta/local-mod testing build
   available to approved beta testers.

Follow the placement instructions supplied by All Of Us staff or the testing
build. There is no public manual-copy path for an unapproved Starlight mod.
`PerfectCommsStarlight.dll` is not an APK and contains no native Android
payload. No APK, manifest, or native-library staging is involved. Grant
microphone permission through Android when Starlight asks.

The managed Starlight build interoperates with desktop `PerfectComms.dll`
players in the same voice lobby.

## Guided setup

On a new install, Perfect Comms walks through five steps: Welcome, Audio, Controls, HUD, and Review. You can select and test audio devices, set talk mode and shortcuts, choose whether desktop keybinds remain active while chat is open, choose whether to show the voice controls, lobby connection status, speaking bar, and meeting overlay, preview a visible speaking bar with a live lobby mockup, then save all choices together. Open **Voice Settings > Advanced > First-Time Setup** to run the guide again without changing anything unless you finish and save.

## Verify it loaded

- The guided setup appears on a fresh install, and **Voice Settings** is available from the Among Us Options menu afterward.
- A Perfect Comms voice HUD appears in a lobby or game.
- Hosts see **Host Voice Settings** at the lobby game-settings console (see [Host Settings](Host-Settings)).
- The main menu shows **Voice Lobbies**.

If those are missing on desktop, use `BepInEx/LogOutput.log` rather than waiting
for a console window. On Starlight, follow the beta or approved local-mod
testing build's log instructions.

## Requirements

| Component | Needed by Perfect Comms | Included? |
| :--- | :--- | :--- |
| BepInEx 6 (Unity IL2CPP), desktop | Yes | No; keep the version provided by your modpack or download it from the official build page |
| MiraAPI | No | No |
| Reactor | No | No |
| TOU-Mira | No; optional role integration | No |

Perfect Comms v4.1.10 supports Among Us `2026.3.31`, `2026.6.5`, and `2026.8.18` (v18.0.0). Desktop requires BepInEx 6 Unity IL2CPP and uses `PerfectComms.dll`. Android uses the managed net10.0 `PerfectCommsStarlight.dll`, compiled against the locked `AmongUs.GameLibs.Android` package and distributed as one self-contained managed assembly. Perfect Comms detects supported mods such as TOU-Mira at runtime and enables their voice behavior only when they are present. Those mods provide their own MiraAPI or Reactor dependencies; Perfect Comms does not load or require them.

## Updating

The in-game update notice opens the latest-release page; it does not install updates automatically.

- **Desktop:** close the game and replace `BepInEx/plugins/PerfectComms.dll`. Keep the existing BepInEx installation unchanged.
- **Android on Starlight:** obtain the updated `PerfectCommsStarlight.dll`
  through the approved testing channel, then upload or copy exactly that one
  file using the instructions for the Starlight testing build.

Saved settings remain in the BepInEx config folder when the plugin DLL is replaced.

## Uninstalling

- **Desktop:** close the game and remove `BepInEx/plugins/PerfectComms.dll`.
- **Android on Starlight:** remove `PerfectCommsStarlight.dll` through the
  Starlight beta or approved local-mod testing process.

Optionally delete `BepInEx/config/com.edgetel.perfectcomms.cfg` to remove saved
Perfect Comms settings.

Do not remove shared BepInEx files when other installed mods still use BepInEx.

## Troubleshooting

- **No Voice Settings, voice HUD, or Voice Lobbies on desktop:** confirm `PerfectComms.dll` is directly inside `BepInEx/plugins`, not one folder deeper. Then check `BepInEx/LogOutput.log` for load errors.
- **No console window:** console visibility is controlled by the BepInEx installation supplied by your mod or modpack. Perfect Comms does not require a separate console; use `BepInEx/LogOutput.log`.
- **Can't hear anyone:** confirm you are not deafened, check the selected speaker, Speaker Volume, and per-player volume sliders, and confirm the other players use a compatible Perfect Comms version.
- **Others can't hear you:** confirm you are not muted, hold Push To Talk if selected, check the selected microphone and Mic Volume, and grant operating-system microphone permission.
- **Connection is stuck:** press `F7` to rebuild your local voice session. Refresh has a 10-second cooldown.
- **Windows helper is blocked:** check whether security software quarantined a Perfect Comms audio helper, then restore or allow it only if it came from the official release.
- **No Voice Settings, voice HUD, or Voice Lobbies on Starlight:** confirm the approved test uploaded or copied only `PerfectCommsStarlight.dll`, then follow the testing build's log instructions.
- **Android mic never starts:** grant microphone permission to Starlight in Android settings, then restart the game.
- **Installing alongside a role mod:** keep the BepInEx, MiraAPI, and Reactor files supplied by that mod. Do not install duplicate copies for Perfect Comms.

See also: [Player Guide](Players) · [Host Settings](Host-Settings) · [Player Settings & Controls](Controls)
