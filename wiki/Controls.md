# Player Settings & Controls

Open **Voice Settings** from the Among Us Options menu. On desktop, you can also press `F10`. These settings are local: they change your own microphone, playback, controls, and HUD without changing the host's lobby rules.

On a new install, Perfect Comms first opens a guided setup for audio devices and tests, talk controls (including the global choice to allow every desktop keybind while chat is open), a live HUD layout preview, and a final review. You can run it again at any time from **Advanced > First-Time Setup**.

The guided HUD choices are Top Middle, Middle Right, Middle Left, Compact, Top Left, Top Right, Left Stack, Right Stack, Bottom Center, and Minimal.

## Audio tab

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Mic Volume** | 100% | How loudly your microphone is sent to other players. It does not change when speech is detected. |
| **Mic Sensitivity** | 1.00 | How easily quiet speech is detected. Higher values pick up quieter audio. |
| **Speaker Volume** | 100% | Overall Perfect Comms voice volume you hear. |
| **Meeting Spatial Audio** | Low | Spreads natural Meeting and End Game voices across the stereo field. Choose Off, Low, or Full; Team Radio stays centered. |
| **Mic Mode** | Open Mic | Choose voice activation or hold-to-talk. |
| **Noise Gate** | Off | Gently removes residual room noise between phrases. Leave it off to preserve the quietest speech, breaths, and word endings. |
| **Noise Suppression** | On | Cleans outgoing microphone noise on Windows desktop builds. |
| **Stronger Noise Suppression** | Off | Uses the strongest WebRTC noise suppression level on Windows desktop builds. It only applies while Noise Suppression is on and may make quiet speech sound less natural. |
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
- **Hear Your Microphone** plays the selected microphone through your selected speaker or current Android output so you can check how you actually sound. Headphones are recommended to prevent feedback.
- **Delayed Playback** adds a one-second delay to that microphone test, letting you speak first and listen back afterward. The test stops automatically when you leave the Devices tab or close Voice Settings.

The guided setup also provides an optional live microphone meter, mic test, and output test.

## Keybinds tab (desktop)

The Android build uses touch controls and does not show this keyboard tab. On desktop, every binding can use a keyboard key, mouse button, standalone left/right modifier, or modifier chord. Select a binding and press the new key or chord; clear it to leave the action unbound.

| Action | Default | What it does |
| :--- | :---: | :--- |
| Open voice menu | `F10` | Opens or closes local Voice Settings. |
| Open host voice settings | `F11` | Opens the lobby's host-only voice rules when you are the host. |
| Mute / unmute mic | `Right Alt` | Toggles manual microphone mute in Open Mic mode. |
| Push To Mute (hold) | Unbound | Mutes while held, then restores the previous mute state. |
| Push To Talk (hold) | `C` | Transmits while held when Mic Mode is Push To Talk. |
| Team Radio (hold) | `V` | Transmits over the selected eligible private team channel while held. |
| Cycle Team Radio channel | `G` | Cycles through the built-in and mod-registered channels currently available to you. |
| Toggle Open Mic / Push To Talk | Unbound | Switches between the two microphone modes. |
| Toggle deafen | `Right Ctrl` | Mutes Perfect Comms playback and pauses microphone transmission until you undeafen. |
| Player Volumes | `Shift+B` | Opens the local per-player volume mixer. |
| Alive louder / dead quieter (hold) | Unbound | Applies its configured living/dead volume profile while held. |
| Alive quieter / dead louder (hold) | Unbound | Applies its separately configured living/dead volume profile while held. |
| Refresh voice connection | `F7` | Rejoins only your local voice session and has a 10-second cooldown. |

Tap and release `Right Alt` or `Right Ctrl` by itself. A standalone modifier binding does not fire when that key is used as part of another shortcut. Exact left/right modifiers are supported in chords. If a plain key overlaps one of its chords, the chord wins.

Perfect Comms v4.1.7 performs a one-time migration that changes Mute and Deafen to `Right Alt` and `Right Ctrl`, including previously customized or unbound values. Bindings changed after that migration persist normally.

**Allow Keybinds While Chat Is Open** defaults to Off. With the default per-binding choices, Mute and Deafen remain available while Among Us chat is open and every other shortcut is blocked. You have two ways to change this:

- Enable **Allow Keybinds While Chat Is Open** to allow every Perfect Comms shortcut while typing.
- Leave it off, then open **Chat Keybinds > Choose Chat Keybinds** and select exactly which bindings may work while chat is open.

New bindings to `Right Alt`, `Right Ctrl`, or mouse buttons MB4 through MB7 start allowed in chat; other primary keys start blocked. Rebinding a primary key recalculates that recommended choice, and you can override it afterward. A permitted printable key can both trigger its voice action and type into the message.

Perfect Comms panels, active key rebinding, open task/minigame screens, the Friends List, the Voice Lobby editor, and application focus loss always block desktop keybinds. A blocked hold or chord must be physically released before it can activate again.

Desktop Push To Talk keeps the selected capture stream ready while a voice session is connected, but discards samples before encoding or transmission until the binding is held. This removes hardware-start delay; the operating system can show the microphone as in use between presses.

The settings icon beside either alive/dead focus binding expands that binding's independent **Alive Players** and **Dead Players** levels:

- **Alive louder / dead quieter:** 200% alive and 50% dead by default.
- **Alive quieter / dead louder:** 50% alive and 200% dead by default.

These profiles apply only while held. Releasing restores both groups to 100%; holding both focus bindings at once is neutral.
All four expanded sliders range from 0% to 200%; 0% is shown as **None**.

## HUD tab

### Voice controls

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Disable Voice Controls HUD** | Off | Hides the microphone, deafen, and Android Team Radio buttons. Desktop keyboard shortcuts remain active. |
| **Controls Layout** | Vertical | Places the microphone, deafen, and Android Team Radio controls vertically or horizontally. |
| **Button Position X / Y** | 99% / 10% | Moves the voice controls around the screen. |
| **Button Scale** | 130% | Changes the size of the voice HUD buttons. |
| **Mute / Deafen Status Reminder** | On | Keeps a small persistent reminder visible while muted or deafened. |
| **Voice Connection Status** | On | Shows routine starting, syncing, and player-count progress in the lobby and retry status in any phase. Turn it off to hide all connection-progress messages; separate device warnings remain available. |

### Speaking bar

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Disable Speaking Bar** | Off | Hides the speaking bar and its dependent layout, appearance, and preview settings. |
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

### Meeting overlay

- **Meeting Speaking Overlay** normally adds a colored glow to the real speaker's public meeting card. Task-world disguises, concealment, and blindness do not change it once the meeting publicly reveals identities; compatible mod privacy rules can still hide or reattribute the indicator. It is on by default.

## Advanced tab

- **First-Time Setup > Run Setup Again** reopens the guided Welcome, Audio, Controls, HUD, and Review flow. Existing settings are kept unless you finish with changes. On desktop, the Controls step can globally allow every keybind while chat is open; choose individual chat keybinds later from the full Keybinds tab. The HUD step exposes Hide controls, Hide connection status, Hide speaking bar, and Hide meeting overlay; hiding the speaking bar hides its preset picker and live preview.
- **Show Fake 15 Players** fills the speaking bar with a test roster for layout troubleshooting. It resets off on every game launch.
- **Diagnostics** writes detailed voice and microphone-calibration logs. It resets off on launch; leave it off unless you are investigating a problem.

During guided setup, **Use existing settings** keeps every current value and marks onboarding complete without applying the draft choices.

## Player volume mixer

Press `Shift+B` to open **Player Volumes**. Each other player has a persistent local slider from 0% to 200%, a reset-to-100% action, and a live speaking meter. These adjustments affect only what you hear.

## In-game controls

### Desktop

- **Mute (`Right Alt`)** toggles manual microphone mute in Open Mic mode.
- **Push To Talk (`C`)** transmits only while held when Mic Mode is Push To Talk.
- **Push To Mute (unbound)** temporarily mutes the microphone while held and restores the prior mute state when released.
- **Team Radio (`V`)** transmits on the selected eligible team channel while held; **Cycle (`G`)** changes channel.
- **Refresh voice (`F7`)** rebuilds only your local voice session and has a 10-second cooldown.
- **Deafen (`Right Ctrl`)** mutes Perfect Comms playback and pauses your microphone transmission until you undeafen.

### Android

The Android Voice Settings panel contains Audio, Devices, HUD, and Advanced tabs. It uses the in-game touch buttons instead of desktop keybinds:

- In **Open Mic** mode, tap the microphone button to mute or unmute.
- In **Push To Talk** mode, hold the microphone button while speaking and release it to stop.
- When Team Radio is available, tap its button to change channel or hold it to transmit.
- Tap the speaker button to deafen or undeafen. Deafening mutes playback and pauses microphone transmission.

See also: [Installing Perfect Comms](Installing-Perfect-Comms) · [Host Settings](Host-Settings)
