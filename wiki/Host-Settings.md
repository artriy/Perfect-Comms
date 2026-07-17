# Host Settings

The host controls the match-wide voice rules. Host changes sync automatically to every player; players separately control their own devices, volumes, keybinds, and HUD.

Open **Host Voice Settings** from the lobby game-settings console. On desktop, the host can also press `F11`. The panel closes automatically if host authority moves to another player. Hover the **!** beside an option for its in-game explanation.

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

On desktop, players hold `V` to transmit and press `G` to cycle the channels available to their role. On Android, they tap the Team Radio button to cycle or hold it to transmit. Additional radio rows appear as their related options are enabled.

## TOU MIRA tab

This built-in tab is always present. Its 17 rows take effect only when compatible TOU-Mira roles and game states are available:

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

Role rules activate automatically when their matching mod and role state are available and otherwise stay dormant.

## Additional mod tabs

Other compatible mods can add their own tab under **Mod Behaviour**, with synced options, through the [mod integration API](Mod-Integration). A client without that mod does not render its tab.

## How synchronization works

Host changes are broadcast automatically, and a joining player requests the current rules. The in-game editor is available only while that player is the lobby host.

See also: [Installing Perfect Comms](Installing-Perfect-Comms) · [Player Settings & Controls](Controls)
