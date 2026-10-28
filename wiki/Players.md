# Player Guide

Perfect Comms puts proximity voice, team channels, player volumes, and speaking indicators inside Among Us. Your local settings control what you send, hear, and see; the lobby host controls the match-wide voice rules.

> [!TIP]
> Installing for the first time? Start with **[Installing Perfect Comms](Installing-Perfect-Comms)**. It covers desktop BepInEx setup and the approved Starlight testing path.

## Install and verify

1. Follow [Installing Perfect Comms](Installing-Perfect-Comms) and use the
   package for your platform.
   - Desktop uses `PerfectComms.dll` in `BepInEx/plugins`.
   - Android testing uses `PerfectCommsStarlight.dll` through a Starlight beta
     or staff-approved local-mod testing build. The DLL is self-contained and
     includes its managed dependencies and legal notices. It has no companion
     DLLs or native payload. This testing path does not imply public Starlight
     availability or approval.
2. Launch Among Us. A new installation opens the guided setup automatically.
3. Confirm that **Voice Settings** appears in the Among Us Options menu and that the voice HUD appears in a lobby.
4. The main menu should also show **Voice Lobbies**. Hosts additionally see **Host Voice Settings** at the lobby game-settings console.

On desktop, press `F10` to open Voice Settings. Hosts can press `F11` to open Host Voice Settings.

Managed Starlight players and desktop `PerfectComms.dll` players interoperate in
the same voice lobby. The desktop installation and media stack are unchanged.

When an update notification appears in the main menu, **View release** opens its release page and **Dismiss** closes the card for that menu visit. The card waits while a Perfect Comms panel or first-time setup is open.

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

If the host enables **Ghosts Hear Living During Tasks**, nearby ghosts can hear you during tasks even when living players cannot hear each other. A persistent yellow lobby notice warns you before the match. During tasks, **Ghosts only** identifies that microphone audience; manual mute, deafen, and Push To Talk still control transmission. The option is off by default and does not let ghosts reply to living players.

### Team Radio

Team Radio provides private hold-to-talk channels when the host enables it and your current team or role is eligible. On desktop, hold `V` to transmit and press `G` to cycle available channels. On Android, tap the radio button to cycle or hold it to transmit. The host decides whether channels work during tasks, meetings, or both.

### Player volumes, mute, and deafen

- Press `Shift+B` to open the personal volume mixer with persistent local sliders and live speaking meters for other players. Scroll to reach the full roster. Each slider ranges from 0% to 200% and affects only what you hear; the reset arrow restores that player to 100%.
- Muting stops your microphone transmission without silencing other players.
- Deafening mutes Perfect Comms playback and pauses your microphone transmission until you undeafen.
- The optional alive/dead focus bindings temporarily apply separate group volume levels while held.

### Speaking indicators and privacy

The speaking bar can show only active speakers or reserve a slot for every connected player. The meeting overlay highlights the public meeting card of a speaker. Disguises, concealment, blindness, and compatible mod privacy rules can hide or reattribute speaking indicators so they do not reveal protected identities.

### Voice Lobbies

Open **Voice Lobbies** from the main menu to browse public voice-enabled lobbies. A host chooses whether to publish the lobby and which supported directory to use. Directory selection changes discovery only; it does not change the match's voice rules.

The directory scrolls through all listed lobbies. Each row shows its player count, and its button reads **Join** or the reason it cannot be joined: **Full**, **In game**, **Wrong version**, **No code**, **No region**, or **Unavailable**. **Refresh** requests a fresh list.

**Edit lobby info** opens separate lobby-title and language fields with 40- and 16-character limits. **Save** applies the text; **Cancel** or closing the panel discards unfinished changes. These details appear publicly only when you host and enable Public Voice Lobby. On desktop, `Tab` switches fields, `Enter` saves, and `Esc` cancels.

## Host Voice Settings overview

Only the current lobby host can edit these synced settings.

| Built-in tab | Includes |
| :--- | :--- |
| **Proximity** | Maximum hearing distance, falloff, wall/vision occlusion, and security-camera hearing. |
| **Lobby** | Public voice-lobby listing and directory selection. |
| **Meeting & Voice** | Meeting-floor grace period, vent voice, ghost rules, Communications sabotage, and Meetings/Lobby Only mode. |
| **Team Radio** | Team Radio, the impostor channel, and task/meeting availability. |

Compatible mods can register additional tabs under **Mod behaviour**. Those tabs and role-specific options appear only when the source mod registers them; Perfect Comms does not include a permanent TOU-Mira settings tab.

## OBS Recording Integration

Optional, off by default, and available for native Windows x64 Perfect Comms with 64-bit OBS Studio. Tested with OBS Studio 32.2.2; other OBS versions may work, but compatibility and recording synchronization are unverified. Linux/Proton, macOS/Wine/CrossOver, and Android do not provide this integration. It adds one combined **Perfect Comms** audio source to OBS and saves separate incoming-voice WAV files beside your recording. It does not record your own microphone.

### Install and set up

1. Install the matching desktop `PerfectComms.dll` normally. Download the separate `PerfectComms-OBS-win-x64.zip` from the same release.
2. Close OBS. Extract the ZIP into your OBS installation folder, the folder containing `bin`, `data`, and `obs-plugins`. Merge its `obs-plugins` folder, rather than placing the ZIP inside another plugin folder. The DLL belongs at `obs-plugins/64bit/perfect-comms-obs.dll`. Restart OBS. This ZIP is not an installer and does not include OBS itself.
3. In Perfect Comms, open **Voice Settings > Audio > Streaming**, enable **OBS Recording Integration**, and join voice. Under **Separate Voice Files**, choose **Per player** (default) or **Alive / dead**. Changes to this mode apply to the next recording.
4. In OBS **Sources**, add **Perfect Comms** once. Use **Add Existing** to reuse that source in other scenes; additional new instances stay silent. Keep its **Sync Offset** at **0 ms** in **Advanced Audio Properties**.
5. Disable any Application Audio Capture of **Perfect Comms Audio** and any desktop/game capture route that also includes incoming Perfect Comms voices. Keep your microphone as a separate OBS input and capture game audio separately. **OBS Compatibility Mode** remains available for combined Application Audio Capture without this plugin; it is not needed for the recording integration.
6. In **Settings > Output**, use Advanced output mode to select recording audio tracks. For example, track 1 can hold your combined listening mix, track 2 game audio, track 3 your microphone, and track 4 the Perfect Comms combined source. In **Advanced Audio Properties**, assign each source accordingly and enable those tracks in Recording. These are ordinary OBS tracks, not the individual voice files.
7. Choose standard **MKV** or **MP4**, or **Hybrid MP4/MOV**, with automatic recording file splitting disabled. Custom FFmpeg output is not supported. Record to a writable NTFS or ReFS location with enough free space, without junctions or symbolic links anywhere in the recording folder path.

### Record and edit

Use OBS **Start Recording**, **Pause/Resume**, and **Stop Recording** normally. OBS controls the voice files automatically, including removing paused time and adding silence before late joins or after a player stops speaking. After stopping, allow up to ten seconds for voice-file finalization before closing OBS or disconnecting voice.

For `match.mkv`, look beside the video for `match-voices`, or a numbered variant if that folder already exists. Existing files are never overwritten. Import the video and every voice WAV into your editor at **timeline 0**, without trimming each WAV's leading silence or aligning it to the first spoken word. The files are 48 kHz stereo, 32-bit float WAV, switching to RF64 for large files; your editor must support those formats.

- **Per player:** a file for each permitted displayed identity that contributes audio. Safe aliases share their displayed identity's file. Hidden identities share **Private voices.wav**, not individually named files.
- **Alive / dead:** **Alive voices.wav** and **Dead voices.wav** as needed, plus **Private voices.wav** when privacy hides a speaker's group. An absent or silent voice does not require a file.

Separate files avoid OBS's six-track limit. When editing with these files, mute or remove the combined Perfect Comms audio from the video to avoid doubling voices. Keep the separately recorded game and microphone tracks.

Voice files follow the incoming audio you are allowed to hear, including player/group volume, spatial audio, voice filters, deafen, and Speaker Volume. They follow the mod's logical voice routing, not whether your physical Windows speakers are working. They exclude your local mic and any unpermitted audio. OBS source fader changes and OBS filters affect the combined OBS source only, not the separate files.

### Status and recording problems

**Audio > Streaming** shows **Off**, **Waiting for OBS**, **Connected**, **Recording**, **Paused**, or **Recording error**. **Open Guide** opens this section. The OBS source's Properties also shows status and the recording folder; use **Refresh connection and recording status** there.

- **Perfect Comms source missing:** check the ZIP's folder layout, restart 64-bit OBS, and check its log for a plugin load error. Installing only the mod DLL does not install the OBS plugin.
- **Waiting for OBS:** enable the integration, join voice, and add the source. Run OBS and Among Us as the same Windows user.
- **Echo or doubled voices:** remove duplicate application/desktop capture, check source monitoring, and do not play the combined voice track alongside the separate voice files in your editor.
- **No named file or unexpected Private voices:** check local deafen, Speaker Volume, player volumes, and the host's hearing rules. Privacy-hidden voices intentionally share one anonymous file; recording cannot recover their real identities.
- **Recording error:** read the status message. Keep Sync Offset at 0, disable automatic file splitting, use a supported output, and check free space, folder permissions, sparse-file support, and junctions/symbolic links. Invalid encoder timestamps, output errors, queue overflow, or a disconnect during finalization can leave incomplete files. Correct the cause before starting a new recording; do not treat an errored voice folder as complete. Voice chat continues when recording fails.

## Troubleshooting

- **No Voice Settings, HUD, or Voice Lobbies on desktop:** confirm `PerfectComms.dll` is directly inside `BepInEx/plugins`, not inside another folder. Then check `BepInEx/LogOutput.log` for load errors.
- **Connection is stuck or needs rebuilding:** press `F7` to refresh only your local voice session. Refresh has a 10-second cooldown.
- **You cannot hear another player:** confirm you are not deafened, check Speaker Volume and that player's slider in **Player Volumes**, then confirm the selected output device.
- **Others cannot hear you:** confirm you are not muted, hold Push To Talk if selected, check Mic Volume and Mic Sensitivity, and test the selected microphone in the Devices tab.
- **Microphone or speaker is unavailable:** check operating-system permissions and device routing, then reopen Voice Settings. On Android, grant microphone permission to Starlight.
- **The Starlight test does not load:** confirm the approved test received `PerfectCommsStarlight.dll`, then follow the beta or staff-approved local-mod testing build's log instructions.
- **A Windows audio helper is blocked:** check whether security software quarantined it. Restore or allow it only when Perfect Comms came from the official release.
- **Perfect Comms Audio is missing from OBS:** on native Windows, enable **Audio > Streaming > OBS Compatibility Mode**, enter a voice lobby, and wait for the minimized **Perfect Comms Audio** helper window. Then reopen the OBS **Application Audio Capture** source properties to refresh its application list. Another player is not required for the window to appear.
- **A problem needs detailed logs:** enable **Voice Settings > Advanced > Diagnostics**, reproduce the problem, and collect the BepInEx log. Diagnostics turns itself off on the next launch.

## More help

- [Installing Perfect Comms](Installing-Perfect-Comms)
- [Player Settings & Controls](Controls)
- [Host Settings](Host-Settings)
