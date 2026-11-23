---
description: "Fix Perfect Comms when it does not load, you cannot hear anyone, others cannot hear you, antivirus blocks voice, or OBS misses voice chat."
---

# Troubleshooting

Find your symptom below. HUD connection messages are explained in [Compatibility](/compatibility#connection-status-messages).

## Perfect Comms does not load

- **No Voice settings button, voice HUD, or voice lobby browser button on desktop:** check that `PerfectComms.dll` is directly inside `BepInEx/plugins`, not a subfolder. Then check `BepInEx/LogOutput.log` for load errors.
- **No console window:** your mod or modpack's BepInEx controls the console. Perfect Comms does not need one; use `BepInEx/LogOutput.log`.
- **Installed alongside a role mod:** keep that mod's BepInEx, MiraAPI, and Reactor files. Do not add duplicate copies.
- **Nothing appears on Starlight:** check that only `PerfectCommsStarlight.dll` was uploaded or copied, then follow the testing build's log instructions.

## You can't hear anyone

1. Check you are not deafened (**Toggle Deafen**, <kbd>Right Ctrl</kbd> by default, or the speaker button on Android).
2. Check **Speaker Volume** and the player's slider in **Player volumes** (<kbd>Shift</kbd>+<kbd>B</kbd>).
3. On desktop, check the selected **Speaker**.
4. Check everyone runs the host's exact version ([Compatibility](/compatibility#everyone-needs-the-hosts-exact-version)).
5. The host's rules (distance, walls, vision, meetings, ghosts) decide who hears whom. See [Host settings](/host).

## Others can't hear you

1. Check you are not muted. In **Push To Talk** mode, hold **Push To Talk (Hold)** (<kbd>C</kbd> by default).
2. Check the selected **Microphone**; test with **Hear Your Microphone**.
3. Check **Mic Volume** and **Mic Sensitivity**.
4. Grant the game microphone permission in your OS. On Android, grant it to Starlight; without it you can hear others but your mic stays off.

## Connection is stuck

Press **Refresh Voice Connection** (<kbd>F7</kbd> by default, 10-second cooldown) to rebuild your local voice session. HUD messages are explained in [Compatibility](/compatibility#connection-status-messages).

## Antivirus blocks voice chat

<HudStatus title="Antivirus blocked voice chat" hint="Press F10 to fix" />

Some antivirus software deletes or blocks `PerfectCommsAudio.exe`, the audio helper Perfect Comms unpacks from `PerfectComms.dll` on native Windows. Voice cannot start until you allow it. The helper ships inside the `PerfectComms.dll` on the [GitHub releases page](https://github.com/artriy/Perfect-Comms/releases); if your copy came from anywhere else, install that release instead.

**Fix it with Windows Security**

1. Open **Windows Security** > **Virus & threat protection** > **Protection history**.
2. Open the entry for `PerfectCommsAudio.exe` and choose **Actions** > **Restore** or **Allow on device**.
3. Back in the game, press **Retry** on the card, or at the top of Voice settings (<kbd>F10</kbd>).

**Other antivirus software:** restore or allow `PerfectCommsAudio.exe` from its quarantine or blocked-items list, then press **Retry**.

::: details Optional: keep updates from being blocked
In **Virus & threat protection** > **Manage settings** > **Exclusions** > **Add or remove exclusions** > **Add an exclusion** > **Folder**, add the helper folder (**Open folder** on the card opens it). It is one of:

- `<Among Us folder>\cache\PerfectComms\native`
- `%LOCALAPPDATA%\PerfectComms\native`, used when the Among Us folder path is too long

Other antivirus software: add the same folder as an exclusion.
:::

::: details Where this message appears
- The voice HUD shows **Antivirus blocked voice chat** in red with **Press F10 to fix** (your **Open Voice Menu** key, or **Open Voice Settings to fix** if unbound). If the helper keeps stopping without a clear antivirus signal, it shows **Antivirus may be blocking voice**.
- In the main menu or a lobby, an **Antivirus blocked voice chat** card shows the fix steps, the helper folder, and **Open folder**, **Retry**, **Open guide**, and **Close**. **Close** hides it until the helper is blocked again.
- Every Voice settings tab shows the message at the top, with **How to fix** and **Retry**.
:::

## OBS does not show voice chat

- **Perfect Comms Audio is missing from OBS:** enable **Audio > Streaming > OBS Compatibility Mode** (native Windows), enter a voice lobby, and wait for the minimized **Perfect Comms Audio** window. Then reopen the **Application Audio Capture** source properties to refresh its list. No other player is needed.
- **Recording plugin problems:** see [Status and recording problems](/obs#status-and-recording-problems).

## Collect logs for a bug report

Enable **Voice settings > Advanced > Diagnostics**, reproduce the problem, and collect `BepInEx/LogOutput.log`. Diagnostics turns off on the next launch.
