# Host Settings

The host controls the match-wide voice rules. Host changes sync automatically to every player; players separately control their own devices, volumes, keybinds, and HUD.

Open **Host Voice Settings** from the lobby game-settings console or press `F11`. The panel is host-only and closes automatically if host authority moves to another player. Hover the **!** beside an option for its in-game explanation.

## Proximity tab

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Max Distance** | 6.0 | Maximum task-phase proximity distance, adjustable from 1.5 to 20.0. |
| **Voice Falloff** | Smooth | Volume fade: Linear, Smooth, or Voice Focused. |
| **Voice Occlusion** | Vision Only | Wall/vision behavior: Off, Soft Muffle, Soft Fade, Hard Block, or Vision Only. |
| **Walls Block Audio** | On | Lets map walls obstruct voice using the selected occlusion mode. |
| **Hear People in Vision Only** | On | Restricts ordinary task voice to players inside the listener's current vision range. |
| **Hear Through Cameras** | On | Lets security-camera users hear around the active camera position. |

## Lobby tab

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Public Voice Lobby** | Off | Publishes the voice-enabled lobby so other Perfect Comms players can find it. |
| **Public Lobby Directory** | BetterCrewLink Live | Chooses BetterCrewLink Live or the Perfect Comms Registry for the listing. |

The directory only handles public-lobby discovery; changing it does not change the host's voice rules.

## Meeting & Voice tab

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Meeting Floor Grace Period** | Off | Temporarily gives the meeting caller the voice floor when a meeting begins. |
| **Grace Period Seconds** | 5 | Length of the enabled grace period, adjustable from 0 to 15 seconds. |
| **Hear Impostors in Vents** | Off | Allows nearby players to hear an impostor inside a vent. |
| **Private Talk in Vents** | On | Prevents players outside vents from hearing vented speech. |
| **Impostors Hear Dead** | Off | Allows living impostors to hear dead players when the ghost rules permit speech. |
| **Comms Sabotage Disables Voice** | On | Disables ordinary voice while Communications sabotage is active. |
| **Only Ghosts can Talk/Hear** | Off | Restricts task-phase voice to dead players. |
| **Ghosts Hear Each Other Anywhere** | Off | Removes proximity distance between dead players. |
| **Meetings/Lobby Only** | Off | Disables living-player voice during tasks. |
| **Ghosts Also Meeting/Lobby Only** | Off | Applies Meetings/Lobby Only to dead players too. |

Conditional rows appear only when their parent rule is enabled.

## Team Radio tab

| Setting | Default | What it controls |
| :--- | :---: | :--- |
| **Team Radio** | On | Enables private hold-to-talk channels for eligible teams and roles. |
| **Team Radio - Impostors** | On | Enables the impostor team channel. |
| **Team Radio - Usable in Meetings** | Off | Allows eligible channels during meetings. |
| **Team Radio - Usable in Tasks Phase** | On | Allows eligible channels during ordinary task gameplay. |

Players hold `V` to transmit and press `G` to cycle the channels available to their role. Additional radio rows appear as their related options are enabled.

## TOU MIRA tab

This built-in tab is always present. Its options take effect only when compatible TOU-Mira roles and game states are available:

- **Blackmailer:** mute the blackmailed player in meetings; optionally keep the mute for the next task round.
- **Parasite:** mute the controlled victim and optionally hear from the victim's position.
- **Puppeteer:** mute the controlled victim and optionally hear from the victim's position.
- **Swooper:** mute while swooped.
- **Eclipsal / Grenadier:** muffle incoming voice for blinded or flashed players.
- **Hypnotist:** muffle incoming voice for affected players during Mass Hysteria.
- **Crewpostor:** use impostor private-voice and team-radio routing.
- **Glitch:** mute hacked players.
- **Jailor:** mute the jailee in meetings, allow temporary unmute, and optionally keep the jail active after the Jailor dies.
- **Medium Ghost Voice:** choose None, Medium to Ghost, Ghost to Medium, or Both during tasks.
- **Vampire and Lovers Team Radio:** enable their private team channels when Team Radio is on.

Role rules activate automatically when their matching mod and role state are available and otherwise stay dormant.

## Additional mod tabs

Other compatible mods can add their own tab under **Mod Behaviour**, with synced options, through the [mod integration API](Mod-Integration). A client without that mod does not render its tab.

## How synchronization works

Host changes sync automatically. Joining players receive the current rules, and only the host can edit them.

See also: [Installing Perfect Comms](Installing-Perfect-Comms) · [Player Settings & Controls](Controls)
