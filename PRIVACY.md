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
candidates) inherent to a real-time voice connection. Audio is not stored by the mod during ordinary voice
use. Optional OBS recording can save incoming voice locally as described below. The Perfect Comms registry
is used only for optional public-lobby discovery/publishing and may also provide short-lived managed TURN
credentials.

Opus packets may carry up to 100 ms of encrypted speech redundancy so an authorized receiver can repair a
short network loss. This history is transient, and the encoder is reset at microphone stop/start and before
a newly authorized peer is added so that peer cannot recover speech from before its authorization boundary.

## Optional local OBS recording

**OBS Recording Integration** is off by default and available only on native Windows with the separately
installed OBS plugin. Enabling it exposes a local current-user named pipe,
`\\.\pipe\PerfectComms.OBS.<current-process-user-SID>`, not a new network endpoint. Its access control
admits the current Windows user and SYSTEM only, remote pipe clients are rejected, and OBS verifies that the
pipe server belongs to the same user. The pipe carries incoming voice PCM, timestamps, opaque export IDs,
privacy-projected names/groups, and recording status. It does not upload audio to a recording service.

Only incoming audio the listener is permitted to hear is exported, with hearing rules, player/group volume,
voice filters, spatial routing, deafen, and master volume applied. The bridge follows logical audible routing,
not whether a physical Windows output device is working. The local microphone is not exported; record it
separately in OBS if wanted. OBS source filters and fader changes affect its combined audio source, not the
separate files.

When OBS records, the plugin writes local float-WAV/RF64 files beside the video. On-disk identity names
follow the same permitted presentation projection as speaking indicators: safe aliases share the displayed
identity, and hidden identities share **Private voices** rather than revealing underlying player IDs or
names. Alive/dead mode uses **Alive voices** and **Dead voices**, with **Private voices** when the group
must remain hidden. OBS receives opaque per-connection export IDs, not raw game client/player IDs.
Existing files are not overwritten. Disabling the integration closes the bridge and discards queued audio;
it does not delete recordings already saved. Their storage, retention, and sharing remain under the user's
control.

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
