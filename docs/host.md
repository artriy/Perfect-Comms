---
description: "Match-wide voice rules for lobby hosts, a live hearing strip for distance, falloff and walls, and invite links for your guests."
---

# Host voice settings

The host sets match-wide voice rules; changes apply immediately, sync to every player, and are saved for later lobbies (settings file section `Host.VoiceChat`, location in [Uninstalling](/install#uninstalling)). Players keep their own devices, volumes, keybinds, and HUD.

Open **Host voice settings** from the **Lobby-wide · Perfect Comms** chip on the lobby game-settings console, or press <kbd>F11</kbd> on desktop (host only). If the host role moves to another player, the panel closes and shows **Host voice settings closed: host changed**. Hover **?** for an option's in-game explanation. Indented rows depend on the row above.

Tabs: **Proximity**, **Lobby**, **Voice rules**, **Team radio**, then one per installed mod with voice options under **Mod behaviour**.

## Proximity tab

The controls below are the tab's first five settings: **Max Distance**, **Voice Falloff**, **Voice Occlusion**, **Walls Block Audio** and **Hear People in Vision Only**. Change a rule and the strip shows who you would hear, using the mod's own distance, falloff and wall rules.

<HostProximity />

The tab's last setting:

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Hear Through Cameras** | On | Camera users hear around the active camera. |

## Lobby tab

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Public Voice Lobby** | Off | Lists the lobby in the Perfect Comms live directory; carries no audio and changes no rules. |

## Voice rules tab

Rows shown in panel order. Hiding a dependent row does not reset its saved value.

| Setting | Default | Shown when | What it controls |
| :--- | :---: | :--- | :--- |
| **Meeting Floor Grace Period** | Off | Always | Meeting caller gets an exclusive voice floor at meeting start. |
| **Grace Period Seconds** | 5 | **Meeting Floor Grace Period** is on | 0 to 15 seconds, steps of 1. |
| **Meetings/Lobby Only** | Off | Always | No living-player voice during tasks. |
| **Meeting Only - Impostor Chat** | Off | **Meetings/Lobby Only** is on | Living actual impostors speak globally during tasks; ghosts and spectators listen; dead impostors speak only with **Impostors Talk Across Death**; living crew and mod impostor privileges excluded. |
| **Ghosts Also Meeting/Lobby Only** | Off | **Meetings/Lobby Only** is on | Applies **Meetings/Lobby Only** to dead players. |
| **Only Ghosts can Talk/Hear** | Off | Always | Task voice for dead players only, except the rule below. |
| **Ghosts Hear Living During Tasks** | Off | **Meetings/Lobby Only** or **Only Ghosts can Talk/Hear** is on, and not **Ghosts Also Meeting/Lobby Only** with **Meetings/Lobby Only** | Ghosts and spectators hear nearby living players during tasks, with normal ghost distance, falloff, vent privacy, mute, Push To Talk, deafen, and host role mutes; no living-to-living speech. |
| **Ghosts Hear Each Other Anywhere** | Off | Always | No distance limit between dead players. |
| **Impostors Hear Dead** | Off | Always | Living impostors hear dead players when ghost rules permit speech. |
| **Impostors Talk Across Death** | Off | Always | Dead actual impostors reply in task-phase impostor chat and impostor Team Radio; crew ghosts and spectators stay listen-only. |
| **Impostors Talk Across Death in Meetings** | On | **Impostors Talk Across Death** is on | Living and dead actual impostors hold radio in Meeting to speak privately; living impostors, ghosts, and spectators listen; living crew get neither audio nor radio state. |
| **Hear Impostors in Vents** | Off | Always | Nearby players hear a vented impostor. |
| **Private Talk in Vents** | On | Always | Players outside vents cannot hear vented speech. |
| **Comms Sabotage Disables Voice** | On | Always | No ordinary voice during Communications sabotage. |

The meeting exception works independently of Team Radio and **Meeting Only - Impostor Chat**, not in Exile or tasks. Releasing radio restores normal speech and death separation; mute and meeting-floor rules still win.

Ghost listening grants no replies to the living and no Team Radio access, and leaves impostor chat and radio audiences unchanged. While active, a yellow bottom-right lobby notice warns everyone and the task mic status reads **Ghosts only** (mute, deafen, and Push To Talk take priority). After host migration, clients wait for a fresh host grant; ghost-only delivery stops when authenticated roster information is unavailable.

## Team radio tab

| Setting | Default | Shown when | What it controls |
| :--- | :---: | :--- | :--- |
| **Team Radio** | On | Always | Private hold-to-talk channels for eligible teams and roles. |
| **Team Radio - Impostors** | On | **Team Radio** is on | Impostor channel. |
| **Team Radio - Usable in Meetings** | Off | **Team Radio** is on | Channels usable in meetings. |
| **Team Radio - Usable in Tasks Phase** | On | **Team Radio** and **Team Radio - Usable in Meetings** are on | Channels usable during tasks; saved value applies while hidden. |

Controls: [Keybinds](/settings#keybinds-tab-desktop) on desktop, the Team Radio button on Android. Mod channels (for example TOU-Mira Vampires and Lovers) are switched in that mod's tab.

## TOU MIRA tab

Registered by TOU-Mira through the [mod integration API](/api/); shown under **Mod behaviour** only when TOU-Mira with Perfect Comms support is installed. Each rule is dormant until its role state exists.

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Blackmailer: Mute Blackmailed in Meetings** | On | Mutes the currently blackmailed player during Meeting and Exile voice. |
| **Blackmailer: Mute Blackmailed Next Round** | Off | Carries the meeting mute into the following task round. |
| **Parasite: Mute Controlled Victim** | On | Mutes the active controlled victim during tasks. |
| **Parasite: Also Hear Controlled Victim** | On | Adds the victim's position and light radius as another hearing point for the Parasite. |
| **Puppeteer: Mute Controlled Victim** | On | Mutes the active controlled victim during tasks. |
| **Puppeteer: Hear From Controlled Victim** | On | Replaces the Puppeteer's hearing point with the victim's position and light radius. |
| **Swooper: Mute While Swooped** | On | Mutes a Swooper while the swoop is active. |
| **Eclipsal/Grenadier: Muffle Blinded/Flashed Hearing** | On | Muffles incoming voice for the affected local listener. |
| **Hypnotist: Muffle Hypnotized During Hysteria** | On | Muffles incoming voice for affected players during Mass Hysteria. |
| **Crewpostor: Use Impostor Voice** | On | Uses impostor-equivalent private voice and Team Radio classification. |
| **Glitch: Mute Hacked Players** | On | Mutes a player while the Glitch's Hack effect is active. |
| **Jailor: Mute Jailee in Meetings** | On | Mutes the jailee during Meeting and Exile voice unless temporarily allowed. |
| **Jailor: Jail Persists If Jailor Dies** | Off | Keeps meeting voice jail active after the Jailor dies; shown when jailee muting is enabled. |
| **Jailor: Can Unmute Jailee** | On | Lets the Jailor temporarily permit the jailee to speak. |
| **Medium: Ghost Voice** | None | Chooses None, Medium to Ghost, Ghost to Medium, or Both during tasks. |
| **Team Radio - Vampires** | On | Enables the vampire channel while Team Radio and the current phase permit it. |
| **Team Radio - Lovers** | On | Enables the lovers channel while Team Radio and the current phase permit it. |

## Additional mod tabs

Other mods can add tabs with synced options through the [mod integration API](/api/). Clients without the mod do not show its tab. Options are saved in your settings file; host values apply only for that lobby.

## How synchronization works

- Only the current host can edit (others see **Host only**). Host and players should run the same Perfect Comms version; incompatible snapshots are rejected and re-requested.
- The host sends settings on every change and roster change; joining players request them. Clients accept settings only from the current host.
- Voice connects before settings arrive, but rules wait. After 10 seconds without host settings (host lacks Perfect Comms or is incompatible), players use their own saved host settings.
- On host migration, clients drop the old host's ghost-listening grant, keep the old rules up to 10 seconds, then the new host's saved settings replace them for everyone.

## Invite your guests

Send guests a link that opens [Tonight](/) with your lobby title, code, region, start time and your exact Perfect Comms version, plus that version's download and install steps. Nothing is uploaded: the details live in the link itself.

<InviteComposer />
