# Host Settings

The host controls the match-wide voice rules. Every client obeys the host's settings, synced automatically. Players separately control their own audio (device, push-to-talk, volumes) in their local Voice Settings.

Open the host panel from the lobby game-settings console (default `F11`, host only).

## Tabs

The host panel is organised into tabs:

| Tab | Controls |
| :--- | :--- |
| **Proximity** | Talk distance, falloff curve, occlusion mode, walls block sound, only-hear-in-sight, camera hearing |
| **Lobby** | Public voice lobby, voice backend, lobby browser backend |
| **Vents & Ghosts** | Hear in vent, vent private chat, impostors hear ghosts, comms-sab disables voice, only-ghosts-can-talk, ghosts hear each other, meetings/lobby-only mode |
| **Team Radio** | Team radio on/off, per-team channels, usable in meetings, usable in tasks |
| **Mod Behaviour** | Role voice rules from supported mods (see below). Each compatible mod gets its own tab here. |

## Role voice rules (Mod Behaviour)

These activate automatically when a supported mod is installed and stay dormant otherwise. With TOU-Mira present, the host can configure:

- Blackmailer: mute blackmailed in meetings / next round
- Jailor: mute jailee in meetings, jailor can unmute, jail persists if jailor dies
- Parasite / Puppeteer: mute controlled victim, hear from controlled victim
- Swooper: mute while swooped
- Glitch: mute hacked players
- Crewpostor: use impostor voice
- Eclipsal / Grenadier: muffle blinded/flashed hearing
- Hypnotist: muffle hypnotized during hysteria
- Medium: ghost voice mode

Other mods can add their own tab and rules through the [mod integration API](Mod-Integration).

## How sync works

When the host changes a setting, the new value is broadcast over an authenticated in-game RPC and applied on every client. Clients cannot forge host settings. Joining mid-match, a client requests the current settings automatically.

See also: [Installing Perfect Comms](Installing-Perfect-Comms) · [Controls](Controls)
