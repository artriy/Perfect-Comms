---
description: "What Perfect Comms connects to, what it sends, and the config-file network settings."
---

# Privacy and networking

The project's [privacy statement](https://github.com/artriy/Perfect-Comms/blob/main/PRIVACY.md) is informational, not legal advice.

## Connections at a glance

| Connection | When | Destination | What it carries |
| :--- | :--- | :--- | :--- |
| Voice signaling | While you are in a voice lobby or match | Among Us game server, as custom RPCs | Connection setup messages between Perfect Comms players |
| Voice audio | While you are in a voice lobby or match | Other players directly (peer-to-peer WebRTC), or a TURN relay | Your microphone audio, plus the IP addresses and connection candidates any real-time connection needs |
| STUN | While voice connects | Public STUN servers run by Google, Cloudflare, and Twilio | Your public address lookup for peer-to-peer connections |
| Relay credentials | Only when a direct connection to a player fails and no custom TURN server is set | `https://perfect-comms-lobbies.edgetel.workers.dev/turn-credentials` | A request for short-lived relay credentials |
| Voice lobby browser | While the **Voice lobbies** browser is open | `wss://perfect-comms-lobbies.edgetel.workers.dev/lobbies/live?role=browser` | The current public lobby list and its updates |
| Public lobby listing | While you host with **Public Voice Lobby** on | Same directory service | Your lobby's public listing (see below) |
| Among Us game list details | While the Among Us online game list is open | `https://au-eu.duikbo.at/public_api/games` | A download of public lobby details |
| Update check | Each time the main menu loads | `https://api.github.com/repos/artriy/Perfect-Comms/releases/latest` | A request for the latest release version and link |

The directory service carries no voice audio or signaling. Every service also sees your IP address as part of the connection.

## Voice

Signaling travels through the Among Us game server as custom RPCs. Modded lobbies are treated as cooperative: a hostile modified client can spoof or disrupt signaling, so do not rely on Perfect Comms for security against other players.

Microphone audio goes directly to other players over WebRTC. If a direct connection fails, audio to that player goes through a TURN relay:

- **Default:** Perfect Comms requests Cloudflare TURN credentials, valid for one hour, from the Perfect Comms service. The request body is empty.
- **Custom:** set your own TURN server in the [config file](/privacy#config-file-settings). Managed credentials are then never requested.

Audio is not stored during ordinary voice use. Packets can carry up to 100 ms of earlier speech to repair short packet loss; that history resets when your mic stops or starts and before a newly allowed player is added.

## Voice lobby directory

The **Voice lobbies** browser receives the lobby list and updates, and sends only a refresh request when you click **Refresh**.

When you host with **Public Voice Lobby** on (off by default, [Lobby tab](/host#lobby-tab)), Perfect Comms publishes:

- room code, Among Us region, and current state (in the lobby or in a game)
- lobby title and language from **Edit lobby info**
- your in-game name as the host name
- player count and maximum players
- Perfect Comms version and voice protocol version
- a random listing ID and a random ownership token; the token protects your listing from replacement and is never shown to browsers

No Among Us player or client ID is published. The listing is removed when you close the room, turn the option off, or disconnect; the directory drops it after 90 seconds without a heartbeat.

## Among Us game list details

While the Among Us online game list is open, Perfect Comms downloads public lobby details from `https://au-eu.duikbo.at/public_api/games` on each list refresh, at most every 8 seconds, to fill each game's tags with **Host:** and the host's name, the lobby status with its duration, and the player count. Nothing about you is sent beyond the request itself.

## Update check

Each main-menu load asks GitHub for the latest release, sending a `User-Agent` of `PerfectComms/<your version>` and receiving the version and page link. No account or personal data is sent. A newer version shows the update card ([Updating](/install#updating)).

## Optional OBS recording

[OBS Recording Integration](/obs#obs-recording-integration) opens no network port and never uploads audio. It uses a local Windows named pipe that only your Windows user and SYSTEM can open. Voice files stay on your computer.

## Config-file settings

Not shown in Voice settings. Edit `com.edgetel.perfectcomms.cfg` ([location](/install#uninstalling)) while the game is closed.

| Section | Key | Default | What it controls |
| :--- | :--- | :--- | :--- |
| `Updates` | `NotificationsEnabled` | `true` | Set to `false` to stop the main-menu update check and card. |
| `Updates` | `NotificationUrl` | GitHub latest-release API | Update source. `https` only, else falls back to GitHub. A non-GitHub address also receives your version as a `current` query value. |
| `Lobby Browser` | `RegistryUrl` | `https://perfect-comms-lobbies.edgetel.workers.dev` | Directory for the voice lobby browser, public listings, and managed relay credentials. `https` only, else falls back to the default. |
| `Voice Server` | `TurnServerUrl` | Empty | Your own TURN relay, as a `turn:` or `turns:` address. Empty uses managed relay credentials. |
| `Voice Server` | `TurnUsername` | Empty | Username for your TURN relay. |
| `Voice Server` | `TurnCredential` | Empty | Password for your TURN relay. |

A custom TURN server needs all three `Voice Server` values. If any is invalid, Perfect Comms logs a warning and automatic relay fallback is unavailable until fixed; it does not switch back to managed credentials.

## Third parties

These services are operated by GitHub, Cloudflare, Google, Twilio, and the operator of `au-eu.duikbo.at` under their own policies. Perfect Comms is not affiliated with Innersloth, Among Us, BepInEx, MiraAPI, Reactor, or any supported mods.
