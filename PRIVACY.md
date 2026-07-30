# Privacy

Perfect Comms is a proximity voice mod. This describes the network connections it makes and the data they
carry. It is informational, not legal advice.

## Voice and signaling

When you are in a voice room, connection setup (signaling) travels through Among Us custom RPCs. The
receive callback identifies the routed `PlayerControl`, but does not provide cryptographically authenticated
packet-sender provenance. Perfect Comms therefore treats a modded lobby as a cooperative trust boundary: a
hostile modified client may spoof or disrupt signaling and control messages. Especially disruptive remote
commands, such as forcing every client to rebuild its voice session, are disabled; the remaining object and
roster checks are compatibility safeguards, not a security boundary against a hostile lobby participant.
Microphone audio flows peer-to-peer over WebRTC to the other players in the room, or through a TURN relay
when a direct connection cannot be established. This carries the network metadata (IP addresses and ICE
candidates) inherent to a real-time voice connection. Audio is not stored by the mod. The Perfect Comms
registry is used only for optional public-lobby discovery/publishing and may also provide short-lived managed
TURN credentials.

Opus packets may carry up to 100 ms of encrypted speech redundancy so an authorized receiver can repair a
short network loss. This history is transient, and the encoder is reset at microphone stop/start and before
a newly authorized peer is added so that peer cannot recover speech from before its authorization boundary.

## Public lobby discovery and publishing

Opening the in-game Voice Lobby browser opens a WebSocket connection to the configured Perfect Comms live
directory (by default `wss://perfect-comms-lobbies.edgetel.workers.dev/lobbies/live?role=browser`). The
connection receives a current snapshot and subsequent lobby add, update, and removal events. It sends only a
refresh request when the player clicks Refresh; the service still receives the player's IP as part of the
network connection.

If a host enables **Public Voice Lobby**, Perfect Comms opens a host WebSocket and publishes the room code,
Among Us region, language, title, host display name, player counts, current game state, mod version, and
protocol version. It also sends a random listing id and random ownership token; the token is used only to
protect listing replacement and is never sent to lobby browsers. No Among Us client/player id is published.
Closing the room, disabling publication, disconnecting, or missing the heartbeat timeout removes the listing.
The directory connection does not carry private-room voice or voice signaling.

## Update check (outbound HTTP)

To tell you when a newer build exists, the mod requests the latest release metadata from (default, or the
configured update URL):

- `https://api.github.com/repos/artriy/Perfect-Comms/releases/latest`
- fallback: `https://perfect-comms-lobbies.edgetel.workers.dev/updates/latest`

The request includes a User-Agent containing the mod version and receives release metadata (version, notes,
download link). No account or personal data is sent.

## Third parties

These endpoints are operated by GitHub and Cloudflare under their respective privacy policies. Perfect Comms
is not affiliated with Innersloth, Among Us, BepInEx, MiraAPI, Reactor, or any supported mods.
