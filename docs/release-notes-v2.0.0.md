This is the biggest Perfect Comms update so far.

Voice no longer rides on Among Us RPCs. Perfect Comms now uses real voice backends, with BetterCrewLink live voice as the main path and Interstellar as an alternate backend. The result is better voice reliability, better lobby support, cleaner directional audio, and much stronger host-synced room behavior.

### Most Notable Changes

- BetterCrewLink backend + lobby browser is now fully built in.
- Interstellar backend is now fully supported.
- Voice transport no longer depends on Among Us RPCs.
- Public lobby browsing and publishing are much better.
- Directional and proximity audio are more reliable.
- Host settings now sync across the lobby automatically.
- Debug logs are quiet by default unless you turn them on.

### Full Technical Changelog

### Headline Changes

- Fully removed the old voice-over-Among-Us-RPC audio transport.
- Fully implemented the BetterCrewLink backend for voice, signaling, peer mapping, playback, custom data messages, and lobby publishing.
- Fully implemented the Interstellar backend for voice, peer lifecycle, microphone capture, speaker playback, saved volume handling, and proximity routing.
- Fully removed the old per-client `VCPlayer` playback model.
- Fully removed the old `VoiceTransport` RPC bundling and jitter transport path.
- Fully removed the old Perfect Comms handshake RPC dependency.
- Fully implemented a backend abstraction through `IVoiceBackend`.
- Added host-selectable backend support through the in-game `Voice Backend` option.
- Added host-synced room settings so clients follow the host's backend, server URL, proximity settings, occlusion settings, radio settings, ghost settings, vent settings, camera hearing, and meeting/lobby-only mode.
- Added endpoint settings for both BetterCrewLink and Interstellar servers.
- Added automatic backend switching and room rejoin when the host changes backend/server settings.
- Added missing-peer recovery that automatically rejoins the active voice backend when expected remote players are not mapped.

### BetterCrewLink Backend

- Added full BetterCrewLink Socket.IO connection support.
- Added BetterCrewLink room join/rejoin logic using the local player id, client id, host state, room code, and region.
- Added BetterCrewLink peer mapping by socket id, client id, and player profile.
- Added deferred signal handling for peers whose socket mapping arrives after signaling.
- Added pending signal replay once a remote socket becomes mapped.
- Added stale/pending signal dropping for local/self sockets.
- Added WebRTC offer/answer/candidate handling through SIPSorcery.
- Added deterministic offer retry behavior for closed or missing data channels.
- Added BetterCrewLink data-channel voice packet send/receive.
- Added BetterCrewLink custom data messages for Perfect Comms control payloads.
- Added BetterCrewLink radio-state propagation over backend custom messages.
- Added BetterCrewLink jail-voice propagation over backend custom messages plus RPC fallback.
- Added BetterCrewLink host-settings snapshot/request propagation over backend custom messages plus RPC fallback.
- Added BetterCrewLink public lobby publish/remove support through the active voice backend.
- Added standalone BetterCrewLink lobby publishing when the BCL voice backend is not active.
- Added BetterCrewLink lobby metadata conversion to and from BCL public lobby payloads.
- Added filtering so the BCL live browser only shows `PerfectComms` lobbies.
- Added BCL live join flow through `join_lobby`, then normal Among Us game-code join.

### Interstellar Backend

- Added Interstellar voice backend integration using the vendored Interstellar libraries.
- Added Interstellar room creation using Fangkuai/Interstellar room URL formatting.
- Added Interstellar peer connect/disconnect handling.
- Added Interstellar profile mapping from client id to player id/player name.
- Added Interstellar saved per-player volume support.
- Added Interstellar proximity, ghost, radio, meeting, vent, camera, and virtual speaker routing.
- Added Interstellar Windows microphone capture through `WaveInEvent` into `ManualMicrophone`.
- Added Interstellar Android microphone support through the existing Android microphone path.
- Added Interstellar Windows speaker playback through `ManualSpeaker` and WinMM output.
- Added Interstellar Android speaker playback through `ManualSpeaker`.
- Added Interstellar synthetic microphone tone support for calibration/debugging.
- Added Interstellar backend rejoin/reset cleanup paths.

### Voice Audio and Quality

- Raised Opus voice bitrate to 48 kbps.
- Raised Opus complexity to 10.
- Enabled constrained VBR for tighter packet bursts.
- Kept Opus DTX and in-band FEC disabled.
- Added transmit peak measurement.
- Added transmit limiter gain calculation.
- Added smoothed transmit limiter release.
- Added a microphone preprocessor with peak/RMS measurement, minimum transmit gate, and hangover frames.
- Lowered VAD/speaking threshold from `0.012` to `0.004`.
- Changed the default noise gate from `0` to `0.003`.
- Added optional synthetic 48 kHz mono microphone tone.
- Added microphone calibration diagnostics.
- Added a visible `Mic Sensitivity` slider for normal users.
- Added adaptive playback recovery prebuffering.
- Increased recovery prebuffer from 40 ms to 60 ms.
- Increased playback max prebuffer wait from 120 ms to 180 ms.
- Removed edge fade in/out from buffered playback to avoid eating voice edges.
- Added stale buffered-sample clearing on audio routing instances.
- Added audio output peak/non-finite diagnostics.
- Fixed the mixer to clear the output buffer before summing inputs.
- Fixed volume routing so muted routes clear samples and volume only scales the amount actually read.
- Added mono pan routing for backend-specific playback graphs.
- Changed level metering to use absolute sample magnitude.

### BetterCrewLink Playback Graph

- Added split mono playback graphs for BetterCrewLink.
- Added final stereo interleaving from independent left/right mono graphs.
- Added BetterCrewLink stereo pan gains with bounded far-side gain.
- Added per-peer BCL playback routes for normal, ghost, radio, player volume, level meter, and pan.
- Added BCL ghost lowpass and reverb path.
- Added BCL radio highpass and distortion path.
- Added BCL master volume routing.
- Added BCL playback diagnostics for graph format, source format, output device, latency, and split-mono mode.
- Added Android BCL sample-provider speaker support.

### Packets, Jitter, and Timing

- Added BetterCrewLink voice packet wrapper with packet magic, sequence, timestamp, duration, flags, and level.
- Added BCL playout flags and playout-frame model.
- Added BCL jitter buffer with enqueue/drain, quiet-delay draining, legacy packet accounting, drop statistics, and compact diagnostics.
- Increased protocol jitter delay from 1 frame to 2 frames.
- Increased max jitter buffer frames from 16 to 24.
- Increased max jitter frames per update from 6 to 8.
- Increased max decoded frames per update from 48 to 64.
- Increased max jitter delay from 25 ms to 60 ms.
- Added sender guard helper for local sender checks.

### Room Settings Sync and Host Authority

- Added `VoiceRoomSettingsSnapshot`.
- Added `VoiceRoomSettingsState`.
- Added binary `VoiceRoomControlCodec` for host settings snapshots and requests.
- Added trusted-host validation for settings snapshots.
- Added host identity resolution by client id, player id, and backend peer id.
- Added host settings request throttling.
- Added host transfer detection.
- Added automatic remote settings clearing when the local client becomes host.
- Added forced settings resync when host changes.
- Added settings request and snapshot RPC fallback.
- Added backend custom-message path for settings request/snapshot delivery.
- Added diagnostics for sent, requested, applied, rejected, rate-limited, and host-transfer settings events.

### Radio and Jail Voice

- Moved jail voice control out of the old audio RPC stream.
- Added dedicated jail voice RPC id `204`.
- Added backend custom-message jail voice payload support.
- Added dedicated radio state RPC id `205`.
- Added radio-state heartbeat while radio is active.
- Added backend radio-state propagation and RPC fallback.
- Added remote radio-state application through the active backend.

### Lobby Browser and Public Lobby Publishing

- Added `VoiceLobbyBrowserSource` with `BCL Live` and `Cloudflare (Limited)` sources.
- Added local lobby-browser source setting.
- Added in-game lobby publishing source selection through the `Lobby Browser Backend` option.
- Added BCL live lobby browser Socket.IO client.
- Added BCL live lobby list refresh, update, remove, status, and disconnect handling.
- Added BCL live lobby join task handling and timeout handling.
- Added source toggle button in the lobby browser UI.
- Added source-specific empty states and status text.
- Added lobby de-duplication by BCL lobby id or lobby identity.
- Added BCL-aware joinability checks.
- Added improved lobby-browser row rendering, details formatting, status duration, and current-region labeling.
- Added high-quality lobby browser button sprite loading.
- Added improved panel open/close behavior, panel prewarm, button blocking while closing, and button art fitting.
- Added better text/button rendering, disabled button states, close-button styling, and row background sprites.
- Added Cloudflare/duikbo public API metadata fetch for vanilla lobby rows.
- Added vanilla lobby metadata cache with refresh throttling and dirty-state tracking.
- Added vanilla lobby row patches for refresh/update/list handling.
- Added vanilla lobby row UI enrichment for host, status age, player count, map, and mod summary.
- Added vanilla lobby more-info popup enrichment hooks.
- Added vanilla lobby diagnostics and optional patch-state logging.
- Improved registry client errors to include HTTP status and response body.
- Updated registry publishing to choose BCL live publishing or Cloudflare publishing based on selected source.

### Reactor and Vanilla Matchmaking

- Added Reactor HTTP matchmaking bridge for known modded regions.
- Added detection for modded region values such as `duikbo.at`, `aumods.org`, and `modded`.
- Added logic to mark known modded HTTP regions as Reactor-compatible.
- Added public/private host toggle restoration for compatible modded regions.
- Removed custom FindGame mod-filter packet construction.
- Let vanilla/Reactor HTTP matchmaking handle game-list requests and `Client-Mods` processing.
- Added diagnostics for vanilla game-list passthrough.

### Proximity, Occlusion, Cameras, and Routing

- Moved proximity calculations to host-synced `VoiceRoomSettingsState`.
- Added snapshot host client id.
- Added snapshot camera-view state, active camera index, and active camera position.
- Added camera state tracking for Skeld, Polus, Airship, and Fungle surveillance minigames.
- Added Harmony patches for opening, closing, and destroying camera minigames.
- Added fixed camera position lookup for Polus and Airship.
- Added Fungle camera target handling.
- Added Skeld nearest-camera routing.
- Changed camera proxy hearing to use the full configured max chat distance with stronger volume.
- Changed task-phase routing to choose the best route among proximity, virtual speakers, and camera proxy.
- Added local-dead ghost hearing falloff based on vision/chat distance.
- Changed local-dead ghost hearing to use the normal route rather than the old ghost-filter route.
- Added direct distance math to avoid Unity helper dependency in core proximity calculations.
- Added camera state details to local state diagnostics.
- Added backend peer route counts to diagnostics.

### Overlay, HUD, and Player Icons

- Moved remote overlay state to the active backend.
- Updated per-player volume menu to apply saved volumes through backend peer state.
- Updated volume menu population to use backend remote overlay states.
- Removed old `VCPlayer`-based saved volume application.
- Removed meeting border/ring speaking indicators.
- Kept meeting card glow and built-in highlight behavior for speaking players.
- Simplified meeting speaking cleanup to avoid stale controlled-highlight state.
- Added idle-pose caching for crewmate avatars.
- Added outfit matching before using cached avatar poses.
- Added delayed/retry creation for speaking-bar icons when cosmetics are not ready yet.
- Added idle-pose tracking from `PingTrackerPatch`.
- Added high-quality sprite loading mode for HUD/lobby art.

### Local Settings and Game Options

- Added `Voice Backend` game option.
- Added `Lobby Browser Backend` game option.
- Added `BetterCrewLinkServerUrl` local setting.
- Added `InterstellarServerUrl` local setting.
- Added `Lobby Browser` source local setting.
- Moved `Synthetic 48k Mic Tone` to config-only advanced debug settings.
- Moved raw noise gate and VAD threshold tuning to config-only advanced audio settings.
- Added `Mic Calibration Logs` debug setting.
- Changed output-device enumeration from WASAPI/MMDevice to WinMM device names.
- Skips `Microsoft Sound Mapper` in speaker device choices.
- Rejoins the active voice backend when BCL or Interstellar server URLs change.
- Refreshes capture runtime options when noise gate, VAD, synthetic tone, or calibration log settings change.

### Diagnostics and Reliability

- Made voice diagnostics keep main-thread frame and identity context safely for background-thread logs.
- Gated Perfect Comms diagnostic files and debug logger output behind the `Debug Voice Stats` setting.
- Kept `Debug Voice Stats` disabled by default so normal installs do not emit voice diagnostics.
- Added transport switch and selected-backend diagnostics.
- Added missing-peer recovery diagnostics.
- Added backend stats summaries for BetterCrewLink and Interstellar.
- Added BCL mic/speaker/capture/device diagnostics.
- Added Interstellar mic/speaker/manual-speaker diagnostics.
- Added settings sync diagnostics.
- Added lobby browser diagnostics.
- Added audio peak diagnostics.
- Added transition/bootstrap refresh diagnostics.
- Added room close state cleanup diagnostics.
- Added backend custom-message queueing onto the main update loop.
- Added safer backend cleanup on close, rejoin, and backend switch.
- Added local room identity resolution for both BCL and Interstellar.
- Added Fangkuai/Interstellar-specific room identity handling.

### Dependencies and Packaging

- Removed embedded `Libs/NAudio.dll`.
- Removed embedded `Libs/NAudio.Wasapi.dll`.
- Added `NAudio.WinMM`.
- Added `SIPSorcery`.
- Added `SocketIOClient`.
- Added Interstellar references.
- Added embedded `Interstellar.dll`.
- Added embedded `Interstellar.Messages.dll`.
- Added embedded SIPSorcery, Socket.IO, BouncyCastle, DnsClient, Microsoft.Extensions, and System.Text.Json dependencies.
- Suppressed target-framework support warnings for runtime-compatible transitive libraries.

### Added Source Files

- Added `Audio/MicPreprocessor.cs`.
- Added `Audio/WinMmOutputDevices.cs`.
- Added `Comms/BclVoicePacket.cs`.
- Added `Comms/BetterCrewLinkLobbyBrowserClient.cs`.
- Added `Comms/BetterCrewLinkLobbyMetadata.cs`.
- Added `Comms/BetterCrewLinkLobbyPublisher.cs`.
- Added `Comms/BetterCrewLinkPlaybackGraph.cs`.
- Added `Comms/BetterCrewLinkVoiceBackend.cs`.
- Added `Comms/InterstellarVoiceBackend.cs`.
- Added `Comms/IVoiceBackend.cs`.
- Added `Comms/VoiceCameraState.cs`.
- Added `Comms/VoiceCameraStatePatches.cs`.
- Added `Comms/VoiceEndpointSettings.cs`.
- Added `Comms/VoiceHostAuthority.cs`.
- Added `Comms/VoiceJailVoiceRpc.cs`.
- Added `Comms/VoiceLobbyBrowserSource.cs`.
- Added `Comms/VoiceRadioStateRpc.cs`.
- Added `Comms/VoiceRoomControlCodec.cs`.
- Added `Comms/VoiceRoomSettingsRpc.cs`.
- Added `Comms/VoiceRoomSettingsSnapshot.cs`.
- Added `Comms/VoiceTransportBackend.cs`.
- Added `ReactorHttpMatchmakingBridge.cs`.

### Removed Source Files

- Removed `Audio/SmoothedAudioValue.cs`.
- Removed `Comms/VCPlayer.cs`.
- Removed `Comms/VoiceTransport.cs`.

### Verification

- `dotnet build PerfectComms.csproj -c Release --no-restore -m:1` succeeds.
- `dotnet test --no-restore --verbosity minimal` exits successfully.
