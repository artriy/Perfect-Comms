---
description: "First-time setup, proximity voice, Team Radio, player volumes, speaking indicators and voice lobbies."
---

# Player guide

Perfect Comms adds proximity voice, team channels, player volumes, and speaking indicators to Among Us. Your local settings control what you send, hear, and see; the lobby host controls the match-wide voice rules.

> [!TIP]
> Not installed yet? Start with **[Installing Perfect Comms](/install)**.

## First-time setup

A new installation opens the guided setup automatically. Nothing is saved until you finish the final Review step; leaving early keeps your existing settings.

| Step | What you choose |
| :--- | :--- |
| **Audio** | Microphone and output, levels, Open Mic or Push To Talk. |
| **Controls** | Voice controls; on desktop, whether keybinds stay active while chat is open. |
| **HUD** | Voice controls, connection status, speaking bar, meeting overlay, speaking-bar layout with live preview. |
| **Review** | Check and save everything together. |

Run it again from **Voice settings > Advanced > First-Time Setup > Run setup again**.

## Everyday controls

Open **Voice settings** from the Options menu or with <kbd>F10</kbd> on desktop; hosts open **Host voice settings** with <kbd>F11</kbd> or the **Lobby-wide · Perfect Comms** chip at the lobby game-settings console. Every local setting, desktop keybind, chat behavior, and Android touch control is listed in **[Voice settings and controls](/settings)**.

## During a match

### Proximity and host rules

Task-phase voice is proximity-based by default. Distance, falloff, walls, vision, cameras, vent voice, meetings, ghost rules, Communications sabotage, and Meetings/Lobby Only mode follow the host's synced settings, which you cannot override. See **[Host settings](/host)**.

If the host enables **Ghosts Hear Living During Tasks**, nearby ghosts can hear you during tasks even when living players cannot hear each other. A yellow lobby notice warns you before the match, and **Ghosts only** marks that audience during tasks. Mute, deafen, and Push To Talk still apply. Ghosts cannot reply to living players.

### Team Radio

Private hold-to-talk channels, available when the host enables them and your team or role is eligible. On desktop, hold <kbd>V</kbd> to transmit and press <kbd>G</kbd> to cycle channels. On Android, tap the radio button to cycle or hold it to transmit. The host decides whether channels work during tasks, meetings, or both.

### Player volumes, mute, and deafen

- <kbd>Shift</kbd>+<kbd>B</kbd> opens the volume mixer: per-player sliders from 0% to 200% with live speaking meters, saved locally. The reset arrow restores 100%.
- Mute stops your microphone transmission only.
- Deafen silences Perfect Comms playback and pauses your microphone until you undeafen.
- Optional alive/dead focus bindings apply separate group volumes while held.

### Speaking indicators and privacy

The speaking bar shows active speakers only or reserves a slot for every connected player. The meeting overlay highlights the speaker's meeting card. Disguises, concealment, blindness, and compatible mod privacy rules can hide or reattribute indicators so they do not reveal protected identities.

### Voice lobbies

The voice lobby browser button in the main menu opens **Voice lobbies**, which lists up to 100 public voice-enabled lobbies. Hosts list their lobby with **Public Voice Lobby** in the Lobby tab of Host voice settings; this affects discovery only, not the match rules.

Each row shows title, host, region, language, code, lobby or in-game status and duration, and player count. The button reads **Join** or the reason you cannot join: **Full**, **In game**, **Wrong version**, **No code**, **No region**, or **Unavailable**. **Refresh** reloads the list. **Wrong version** compares only the voice protocol; see [Compatibility](/compatibility#voice-lobbies-and-wrong-version).

**Edit lobby info** sets the lobby title (40 characters) and language (16 characters). **Save** applies; **Cancel** or closing discards. They appear publicly only when you host with **Public Voice Lobby** on. On desktop, <kbd>Tab</kbd> switches fields, <kbd>Enter</kbd> saves, <kbd>Esc</kbd> cancels.
