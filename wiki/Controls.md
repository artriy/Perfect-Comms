# Controls

Defaults below. Every key is rebindable in **Voice Settings** (open with `F10`).

| Action | Key | | Action | Key |
| :--- | :---: | :--- | :--- | :---: |
| Open voice menu | `F10` | | Toggle speaker | `Shift+N` |
| Open host voice settings | `F11` | | Player volumes | `Shift+B` |
| Mute / unmute mic | `Shift+M` | | Cycle team radio channel | `G` |
| Push to talk (hold) | `C` | | Refresh voice connection | `F7` |
| Team radio (hold) | `V` | | Refresh voice (host) | `F8` |
| Alive louder / dead quieter (hold) | Unbound | | Alive quieter / dead louder (hold) | Unbound |

## Notes

- **Open voice menu (`F10`)** is also reachable from the Options menu. This is where you set your mic/speaker device, push-to-talk vs open mic, noise suppression, and HUD layout.
- **Setting help** is available by hovering the **!** beside any option in the local Voice Settings or host Voice Settings panels. Device selectors use a full-width wrapped row, and hovering the selected device shows its exact full name.
- **Exact modifier bindings** are supported. Press and release `Left Shift`, `Right Ctrl`, or a similar modifier to bind it by itself, or hold that exact key and press another key to create a chord such as `Left Shift+F`. If a plain key or modifier overlaps one of its chords, the chord wins.
- **Alive/dead voice focus** is local-only and applies whenever those voices are otherwise audible. Each action is unbound by default and works only while held; releasing it immediately restores both groups to 100%, while holding both actions is neutral. Use the settings icon immediately left of either binding to expand that action's independent **Alive Players** and **Dead Players** sliders (defaults: 200%/50% and 50%/200%).
- **Host voice settings (`F11`)** opens only for the lobby host, from the game-settings console. See [Host Settings](Host-Settings).
- **Push to talk (`C`)** works when open-mic is off. Hold to transmit.
- **Team radio (`V`)** is held to talk on your team channel (when team radio is enabled by the host and your role qualifies). **Cycle (`G`)** switches between channels you can use.
- **Android controls:** hold the mic button to transmit in push-to-talk mode. Tap the Team Radio button to cycle channels, or hold it to transmit.
- **Mute / deafen reminder:** enable **Voice Settings > HUD > Mute / Deafen Status Reminder** for a small persistent status indicator.
- **Refresh voice (`F7`)** re-establishes your own voice connection if audio drops. **Host refresh (`F8`)** asks all clients to refresh.

See also: [Installing Perfect Comms](Installing-Perfect-Comms) · [Host Settings](Host-Settings)
