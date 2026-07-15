# Installing Perfect Comms

Perfect Comms is a standalone BepInEx plugin for Among Us. Its only hard dependency is **BepInEx**. It does **not** require MiraAPI or Reactor.

## Install

1. Install BepInEx 6 for Unity IL2CPP using the [official BepInEx installation instructions](https://docs.bepinex.dev/master/articles/user_guide/installation/index.html).
2. Download `PerfectComms-Release.zip` from the [latest release](https://github.com/artriy/Perfect-Comms/releases/latest)
   and extract it into the Among Us folder. It installs the plugin at:

```text
BepInEx/plugins/PerfectComms.dll
```

3. Launch the game. You should see Perfect Comms load in the BepInEx console.

Perfect Comms does not bundle MiraAPI, Reactor, or TOU-Mira; none of them are required.

Android packagers use `PerfectComms-Android.zip` **and must
merge** [`release-assets/android/AndroidManifest.xml`](../release-assets/android/AndroidManifest.xml)
into the final APK manifest before signing and installing it. That fragment declares
`android.permission.RECORD_AUDIO`; placing the XML beside the DLL does not modify an existing APK.
Android will still ask the player for the runtime microphone permission. A player who denies it can
continue in receive-only mode.

## Verify it loaded

- A Perfect Comms voice HUD appears in-game.
- Hosts see a voice settings entry in the lobby (see [Host Settings](Host-Settings)).
- The main menu shows the voice lobby browser.

## Requirements

| Component | Needed by Perfect Comms | In the zip? |
| :--- | :--- | :--- |
| BepInEx (Unity IL2CPP 6) | Yes | No; install it separately |
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
