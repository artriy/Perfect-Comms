# Player Settings & Controls

Open **Voice Settings** from the Among Us Options menu. On desktop, you can also press `F10`. These settings are local: they change your own microphone, playback, controls, and HUD without changing the host's lobby rules.

On a new install, Perfect Comms first opens a guided setup for audio devices and tests, talk controls, a live HUD layout preview, and a final review. You can run it again at any time from **Advanced > First-Time Setup**.

The guided HUD choices are Top Middle, Middle Right, Middle Left, Compact, Top Left, Top Right, Left Stack, Right Stack, Bottom Center, and Minimal.

## Audio tab

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Mic Volume** | 100% | How loudly your microphone is sent to other players. |
| **Mic Sensitivity** | 1.00 | How easily quiet speech is detected. Higher values pick up quieter audio. |
| **Speaker Volume** | 100% | Overall Perfect Comms voice volume you hear. |
| **Mic Mode** | Open Mic | Choose voice activation or hold-to-talk. |
| **Noise Suppression** | On | Cleans outgoing microphone noise on Windows desktop builds. |
| **Echo Cancellation** | On | Reduces speaker audio feeding back into your microphone on Windows desktop builds. |
| **Voice Falloff Softness** | 30% | Keeps voices clearer through more of the host's allowed range, then fades near the edge. It never extends that range. |
| **Start Muted** | Off | Starts each voice session with your microphone muted. |
| **Start Deafened** | Off | Starts each voice session with playback muted and microphone transmission paused until you undeafen. |

Mic and speaker volume range from 10% to 200%. Mic Sensitivity ranges from 0.25 to 2.00, and Voice Falloff Softness ranges from 0% to 100%.

## Devices tab

- **Microphone** selects the recording device. **Default** follows the system input device.
- **Speaker** selects the voice playback device on Windows. **Default** follows the system output device.
- Android playback follows the current Android audio route, so it does not show a separate Speaker selector.
- Device rows show the full selected name and keep a saved device visible if it is temporarily unavailable.

The guided setup also provides an optional live microphone meter, mic test, and output test.

## Keybinds tab (desktop)

The Android build uses touch controls and does not show this keyboard tab. On desktop, every binding can use a keyboard key, mouse button, exact modifier, or modifier chord. Select a binding and press the new key or chord; clear it to leave the action unbound.

| Action | Default |
| :--- | :---: |
| Open voice menu | `F10` |
| Open host voice settings | `F11` |
| Mute / unmute mic | `Shift+M` |
| Push to talk (hold) | `C` |
| Team radio (hold) | `V` |
| Cycle team radio channel | `G` |
| Toggle open mic / push to talk | Unbound |
| Toggle deafen | `Shift+N` |
| Player volumes | `Shift+B` |
| Alive louder / dead quieter (hold) | Unbound |
| Alive quieter / dead louder (hold) | Unbound |
| Refresh voice connection | `F7` |

The settings icon beside either alive/dead focus binding expands that binding's independent **Alive Players** and **Dead Players** levels:

- **Alive louder / dead quieter:** 200% alive and 50% dead by default.
- **Alive quieter / dead louder:** 50% alive and 200% dead by default.

These profiles apply only while held. Releasing restores both groups to 100%; holding both focus bindings at once is neutral.
All four expanded sliders range from 0% to 200%; 0% is shown as **None**.

Exact left/right modifiers are supported. Press and release a modifier to bind it alone, or hold it while pressing another key to create a chord. If a plain key overlaps one of its chords, the chord wins.

## HUD tab

### Voice controls

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Controls Layout** | Vertical | Places the microphone, speaker, and role controls vertically or horizontally. |
| **Button Position X / Y** | 99% / 10% | Moves the voice controls around the screen. |
| **Button Scale** | 130% | Changes the size of the voice HUD buttons. |
| **Mute / Deafen Status Reminder** | On | Keeps a small persistent reminder visible while muted or deafened. |

### Speaking bar

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Show All Players** | Off | Keeps a stable slot for every connected player instead of showing only current speakers. |
| **Live Preview** | Off | Moves the settings panel aside and shows an isolated 15-player preview while you edit. It turns itself off when you close settings, leave the HUD tab, or restart the game. |
| **Speaking Bar Position** | Top Middle | Chooses a top, middle-side, or bottom screen preset. |
| **Side Layout** | Single Lane | Uses one lane or wrapped columns for left/right presets. Center presets wrap automatically. |
| **Speaking Bar Name Position** | Auto | Places names inside the screen automatically, or forces Bottom, Top, Left, or Right. |
| **Speaking Bar Scale** | 100% | Changes the size of icons and names from 50% to 225%. |
| **Speaking Bar Backdrop** | On | Shows a translucent backdrop behind the speaking bar. |
| **Speaking Bar Manual Layout** | Off | Replaces the preset with manual layout, facing, and X/Y controls. |

Manual layout adds **Speaking Bar Layout** (Horizontal by default), **Avatar Facing** (Right by default), and **Speaking Bar X / Y** controls ranging from 0% to 100% (50% / 85% by default).

Side Layout starts as Single Lane. The fresh guided setup selects Wrapped; at the default Top Middle preset, both produce the same automatically wrapped center layout.

### Meeting and role UI

- **Meeting Speaking Overlay** normally adds a colored glow to the real speaker's public meeting card. Task-world disguises, concealment, and blindness do not change it once the meeting publicly reveals identities; compatible mod privacy rules can still hide or reattribute the indicator. It is on by default.
- **Jail Unmute Placement** puts the Jailor's temporary unmute control on either the Voice HUD or the jailed player's meeting card. Meeting Card is the default.

## Advanced tab

- **First-Time Setup > Run Setup Again** reopens the guided Welcome, Audio, Controls, HUD, and Review flow. Existing settings are kept unless you finish with changes.
- **Show Fake 15 Players** fills the speaking bar with a test roster for layout troubleshooting. It resets off on every game launch.
- **Diagnostics** writes detailed voice and microphone-calibration logs. It resets off on launch; leave it off unless you are investigating a problem.

During guided setup, **Use existing settings** keeps every current value and marks onboarding complete without applying the draft choices.

## Player volume mixer

Press `Shift+B` to open **Player Volumes**. Each other player has a persistent local slider from 0% to 200%, a reset-to-100% action, and a live speaking meter. These adjustments affect only what you hear.

## In-game controls

### Desktop

- **Push to talk (`C`)** transmits only while held when Mic Mode is Push To Talk.
- **Team radio (`V`)** transmits on the selected eligible team channel; **Cycle (`G`)** changes channel.
- **Refresh voice (`F7`)** rebuilds only your local voice session and has a 10-second cooldown.
- **Deafen (`Shift+N`)** mutes Perfect Comms playback and pauses your microphone transmission until you undeafen.

### Android

The Android Voice Settings panel contains Audio, Devices, HUD, and Advanced tabs. It uses the in-game touch buttons instead of desktop keybinds:

- In **Open Mic** mode, tap the microphone button to mute or unmute.
- In **Push To Talk** mode, hold the microphone button while speaking and release it to stop.
- When Team Radio is available, tap its button to change channel or hold it to transmit.
- Tap the speaker button to deafen or undeafen. Deafening mutes playback and pauses microphone transmission.

See also: [Installing Perfect Comms](Installing-Perfect-Comms) · [Host Settings](Host-Settings)
