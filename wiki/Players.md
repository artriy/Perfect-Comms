# Player Guide

Perfect Comms puts proximity voice, team channels, player volumes, and speaking indicators inside Among Us. Your local settings control what you send, hear, and see; the lobby host controls the match-wide voice rules.

> [!TIP]
> Installing for the first time? Start with **[Installing Perfect Comms](Installing-Perfect-Comms)**. It has separate instructions for modpacks that provide BepInEx and installations where BepInEx is not provided.

## Install and verify

1. Follow [Installing Perfect Comms](Installing-Perfect-Comms) and place the correct plugin DLL in `BepInEx/plugins`.
   - Desktop uses `PerfectComms.dll`.
   - A BepInEx-enabled ARM64 Android mod build uses `PerfectCommsAndroid.dll`; the release is not an APK.
2. Launch Among Us. A new installation opens the guided setup automatically.
3. Confirm that **Voice Settings** appears in the Among Us Options menu and that the voice HUD appears in a lobby.
4. The main menu should also show **Voice Lobbies**. Hosts additionally see **Host Voice Settings** at the lobby game-settings console.

On desktop, press `F10` to open Voice Settings. Hosts can press `F11` to open Host Voice Settings.

## First-time setup

The setup saves everything only after you finish the final Review step. Existing settings remain unchanged if you leave early.

| Step | What you choose |
| :--- | :--- |
| **Welcome** | Begin a new setup or keep the settings you already have. |
| **Audio** | Select and test your microphone and output, set levels, and choose Open Mic or Push To Talk. |
| **Controls** | Review voice controls and, on desktop, choose whether keybinds stay active while chat is open. |
| **HUD** | Show or hide voice controls, connection status, the speaking bar, and the meeting overlay; choose a speaking-bar layout with a live preview. |
| **Review** | Check the complete setup and save it together. |

Run the setup again at any time from **Voice Settings > Advanced > First-Time Setup**.

## Voice Settings

These settings are local. They never change the host's lobby rules.

| Tab | What it controls |
| :--- | :--- |
| **Audio** | Microphone and speaker volume, mic sensitivity, meeting spatial audio, Open Mic or Push To Talk, optional noise gate, voice falloff softness, startup mute/deafen, noise suppression, and echo cancellation. |
| **Devices** | Microphone selection, Windows speaker selection, live microphone monitoring, and optional delayed playback for testing how you sound. |
| **Keybinds** (desktop) | Every keyboard or mouse binding, standalone left/right modifiers, exact modifier chords, per-binding chat behavior, temporary alive/dead volume profiles, and voice refresh. |
| **HUD** | Voice-control layout, mute/deafen reminder, connection status, speaking-bar presets or manual placement, live preview, and meeting speaking overlay. |
| **Advanced** | Run setup again, show a fake 15-player roster for layout testing, and enable temporary diagnostics. |

Android shows Audio, Devices, HUD, and Advanced tabs. Android playback follows the current system audio route, so it does not show the Windows Speaker selector or desktop Keybinds tab.

For every setting, range, and default, see **[Player Settings & Controls](Controls)**.

## Desktop controls

Every binding can be changed or cleared in **Voice Settings > Keybinds**.

| Action | Default |
| :--- | :---: |
| Open Voice Settings | `F10` |
| Open Host Voice Settings | `F11` |
| Mute or unmute microphone | `Right Alt` |
| Push To Talk | Hold `C` |
| Push To Mute | Unbound |
| Team Radio | Hold `V` |
| Cycle Team Radio channel | `G` |
| Toggle Open Mic / Push To Talk | Unbound |
| Deafen or undeafen | `Right Ctrl` |
| Open Player Volumes | `Shift+B` |
| Alive louder / dead quieter | Unbound |
| Alive quieter / dead louder | Unbound |
| Refresh local voice connection | `F7` |

Tap and release `Right Alt` or `Right Ctrl` by itself. These standalone modifier bindings do not fire when used as part of another shortcut.

With the default per-binding choices, Mute and Deafen remain available while chat is open and every other Perfect Comms shortcut is blocked. Enable **Allow Keybinds While Chat Is Open** to allow them all, or leave it off and use **Chat Keybinds > Choose Chat Keybinds** to select individual shortcuts. Perfect Comms panels, active rebinding, open task/minigame screens, the Friends List, the Voice Lobby editor, and application focus loss always block desktop keybinds. Release a blocked hold or chord before it can activate again.

Push To Talk keeps the selected microphone ready while connected but discards audio before encoding and transmission until the binding is held. The operating system may therefore show the microphone as active between presses.

## Android controls

- In **Open Mic** mode, tap the microphone button to mute or unmute.
- In **Push To Talk** mode, hold the microphone button while speaking and release it to stop.
- When Team Radio is available, tap its button to cycle channels or hold it to transmit.
- Tap the speaker button to deafen or undeafen.

## During a match

### Proximity and host rules

Task-phase voice is proximity-based by default. Distance, falloff, walls, vision, cameras, vent voice, meeting behavior, ghost rules, Communications sabotage, and Meetings/Lobby Only mode all follow the host's synced settings.

You cannot override these match rules from Voice Settings. See **[Host Settings](Host-Settings)** for their exact behavior.

### Team Radio

Team Radio provides private hold-to-talk channels when the host enables it and your current team or role is eligible. On desktop, hold `V` to transmit and press `G` to cycle available channels. On Android, tap the radio button to cycle or hold it to transmit. The host decides whether channels work during tasks, meetings, or both.

### Player volumes, mute, and deafen

- Press `Shift+B` to open persistent local volume sliders and live speaking meters for other players. Each slider ranges from 0% to 200% and affects only what you hear.
- Muting stops your microphone transmission without silencing other players.
- Deafening mutes Perfect Comms playback and pauses your microphone transmission until you undeafen.
- The optional alive/dead focus bindings temporarily apply separate group volume levels while held.

### Speaking indicators and privacy

The speaking bar can show only active speakers or reserve a slot for every connected player. The meeting overlay highlights the public meeting card of a speaker. Disguises, concealment, blindness, and compatible mod privacy rules can hide or reattribute speaking indicators so they do not reveal protected identities.

### Voice Lobbies

Open **Voice Lobbies** from the main menu to browse public voice-enabled lobbies. A host chooses whether to publish the lobby and which supported directory to use. Directory selection changes discovery only; it does not change the match's voice rules.

## Host Voice Settings overview

Only the current lobby host can edit these synced settings.

| Built-in tab | Includes |
| :--- | :--- |
| **Proximity** | Maximum hearing distance, falloff, wall/vision occlusion, and security-camera hearing. |
| **Lobby** | Public voice-lobby listing and directory selection. |
| **Meeting & Voice** | Meeting-floor grace period, vent voice, ghost rules, Communications sabotage, and Meetings/Lobby Only mode. |
| **Team Radio** | Team Radio, the impostor channel, and task/meeting availability. |

Compatible mods can register additional tabs under **Mod Behaviour**. Those tabs and role-specific options appear only when the source mod registers them; Perfect Comms does not include a permanent TOU-Mira settings tab.

## Troubleshooting

- **No Voice Settings, HUD, or Voice Lobbies:** confirm the plugin DLL is directly inside `BepInEx/plugins`, not inside another folder. Then check `BepInEx/LogOutput.log` for load errors.
- **Connection is stuck or needs rebuilding:** press `F7` to refresh only your local voice session. Refresh has a 10-second cooldown.
- **You cannot hear another player:** confirm you are not deafened, check Speaker Volume and that player's slider in **Player Volumes**, then confirm the selected output device.
- **Others cannot hear you:** confirm you are not muted, hold Push To Talk if selected, check Mic Volume and Mic Sensitivity, and test the selected microphone in the Devices tab.
- **Microphone or speaker is unavailable:** check operating-system permissions and device routing, then reopen Voice Settings. On Android, grant microphone permission to the rebuilt app.
- **A Windows audio helper is blocked:** check whether security software quarantined it. Restore or allow it only when Perfect Comms came from the official release.
- **A problem needs detailed logs:** enable **Voice Settings > Advanced > Diagnostics**, reproduce the problem, and collect the BepInEx log. Diagnostics turns itself off on the next launch.

## More help

- [Installing Perfect Comms](Installing-Perfect-Comms)
- [Player Settings & Controls](Controls)
- [Host Settings](Host-Settings)
