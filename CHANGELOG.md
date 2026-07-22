# Changelog

## Perfect Comms v4.1.4

Perfect Comms v4.1.4 makes Push To Talk respond instantly and remain reliable through results-to-lobby transitions, adds Push to Mute, makes chat keybind blocking and connection status optional, expands first-time setup, and prevents stale task-role muffling during meetings.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v4.1.4/assets/brand/divider.svg" alt="divider" width="900">
</p>

### Faster, More Reliable Talk Controls

- **Push To Talk no longer reopens the microphone on every press.**
  > <sub>While connected in Push To Talk mode, Perfect Comms keeps capture ready but discards microphone samples before encoding or transmission until the key is held. Pressing the key opens the native transmit gate immediately; changing devices, permissions, or a failed capture still uses the existing recovery path.</sub>

- **Push To Talk now stays held between the results screen and the lobby.**
  > <sub>Continuous EndGame-to-lobby scene changes no longer masquerade as privacy boundaries and close the transmit gate while its key is still down. Chat, settings panels, rebinding, focus loss, leaving the lobby, and joining a new voice session still require a fresh press before transmission can resume.</sub>

- **Chat keybind blocking is now optional.**
  > <sub>Perfect Comms still blocks all desktop voice shortcuts while chat is open by default. Enable Allow Keybinds In Chat in first-time setup's Choose How You Talk step or Allow Keybinds While Chat Is Open in Voice Settings > Keybinds to keep them active while typing; settings panels, rebinding, focus loss, and voice-session boundaries remain fail-closed.</sub>

- **A new Push To Mute shortcut provides momentary microphone muting.**
  > <sub>Assign the unbound Push To Mute key in first-time setup or Voice Settings. The microphone stays muted while the shortcut is held, then returns to its prior mute state when released.</sub>

### Cleaner Status and First-Time Setup

- **Routine connection progress now stays in the lobby and can be hidden.**
  > <sub>Starting, syncing, and player-count progress appears only while assembling the lobby session. With Voice Connection Status enabled, retrying-audio status remains visible during active games, meetings, and other phases; disabling it hides both routine progress and retry status, while separate unavailable-device warnings remain available.</sub>

- **First-time setup now includes every optional voice HUD visibility choice.**
  > <sub>The guided HUD step can hide the voice controls, lobby connection status, speaking bar, or meeting speaking overlay before the first save. Compact switches keep every label readable, and the live preview stays between those choices and the navigation footer. The miniature preview preserves the live HUD's row and column topology while scaling it to the setup card, so Top Middle's 15-player layout remains two balanced rows. Hiding the speaking bar also hides its layout picker and preview until the bar is shown again.</sub>

### Clear Meeting Audio

- **Eclipsal and Grenadier muffling now ends when a meeting begins.**
  > <sub>The built-in blind/flash hearing effect is limited to the task world, so a Town of Us modifier retained during a meeting, exile, intro, or lobby transition can no longer keep every incoming voice muffled. Explicit phase-aware listener filters registered by other mods remain available in meetings.</sub>

## Perfect Comms v4.1.3

Perfect Comms v4.1.3 makes voice recovery more reliable, prevents stalled connection messages from lingering, adds independent controls for optional voice HUD elements, and keeps settings consistent across multiple Among Us installations.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v4.1.3/assets/brand/divider.svg" alt="divider" width="900">
</p>

### More Reliable Voice Connections

- **Repeated reconnects can no longer bury a player's live connection behind stale work.**
  > <sub>The native voice helper now keeps only the newest generation of peer, SDP, restart, and ICE-candidate work for each player, with bounded candidate storage and explicit applied-operation acknowledgements. If a current operation does not complete promptly, only that client restarts its helper; a confirmed total mesh collapse can no longer defer global recovery forever. Direct peer-to-peer and TURN relay selection are unchanged.</sub>

- **An unreachable player no longer leaves the connecting message onscreen forever.**
  > <sub>The compact voice HUD gives the current lobby up to 30 seconds to connect, then clears a stalled player-count message while voice recovery continues in the background. A changed lobby roster or a later connection regression starts a fresh bounded status window.</sub>

### More Flexible Voice HUD

- **Voice controls and the speaking bar can now be hidden independently.**
  > <sub>The voice settings now include separate switches for hiding the microphone, speaker, deafen, and mobile radio controls or hiding the all-players speaking bar. The role-critical Jailor unmute button remains available; disabling either optional HUD feature hides only its related layout and appearance options.</sub>

### Shared Settings Across Installations

- **Perfect Comms settings now follow you across Among Us installations.**
  > <sub>Perfect Comms stores one user-wide configuration in the game's persistent data folder instead of each installation's BepInEx folder. The first launch migrates the current installation's settings without deleting the old file, and simultaneous clients merge changed settings through locked, atomic writes so unrelated preferences are not overwritten.</sub>

## Perfect Comms v4.1.2

Perfect Comms v4.1.2 rebuilds desktop capture and playback for more dependable device handling and clearer voice. Microphone stalls now preserve the correct timeline, damaged playback fades or recovers at safe boundaries, and one unstable connection can no longer reduce voice quality for an otherwise healthy lobby.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v4.1.2/assets/brand/divider.svg" alt="divider" width="900">
</p>

### Rebuilt Desktop Audio

- **Desktop audio now runs through Mozilla Cubeb.**
  > <sub>Perfect Comms now uses the same mature audio layer used by major desktop applications. Windows uses WASAPI with a WinMM fallback, macOS uses AudioUnit, and Linux uses PulseAudio or PipeWire with an ALSA fallback. Capture and playback remain low-latency while following each platform's native audio system more consistently.</sub>

- **Microphone and speaker selection is more reliable.**
  > <sub>Perfect Comms now tracks stable device identities, correctly distinguishes devices with duplicate names, confirms that playback has actually started before reporting it ready, and can safely fall back to the default speaker when a selected output is no longer available.</sub>

- **Audio tests use the same native path as live voice.**
  > <sub>Microphone monitoring and speaker tests now follow the selected native capture and playback routes. Device changes discard stale test audio, re-prime cleanly, and report clearer reasons when an output is unavailable, busy, denied by the system, or unable to start.</sub>

### Clearer, More Stable Voice

- **Microphone stalls no longer splice unrelated audio together.**
  > <sub>When capture briefly falls behind, Perfect Comms preserves the missing time instead of joining nonadjacent microphone samples. Short gaps become correctly timed concealed silence, while longer interruptions reset safely before live speech resumes. The same timing protection is used on desktop and Android.</sub>

- **Playback interruptions recover without harsh audio jumps.**
  > <sub>Short dropouts, decoder recovery, voice-effect changes, and stream restarts now fade or crossfade over safe boundaries instead of producing abrupt waveform discontinuities. Filter and reverb transitions also finish smoothly when a voice becomes silent.</sub>

- **A full speaker buffer now fails safely.**
  > <sub>If the output buffer fills, Perfect Comms discards the new overflowing block instead of deleting audio that was already queued. Playback receives a discontinuity marker, clears stale data, and re-primes rather than replaying delayed audio or producing static.</sub>

- **One damaged connection no longer lowers quality for everyone.**
  > <sub>Send-quality adaptation now ignores a single isolated bad route when the rest of the lobby is healthy. Multiple genuinely degraded connections still reduce bitrate and increase packet-loss protection when needed.</sub>

### Diagnostics and Validation

- **Voice diagnostics can identify the failing audio stage.**
  > <sub>Diagnostic sessions now report capture timing, dropped samples, microphone clipping, DSP status, Opus gap recovery, packet loss, jitter-buffer recovery, output overflow, callback health, and the actual native devices and backends in use.</sub>

- **Native helper packaging is more strongly verified.**
  > <sub>Release checks now validate the Cubeb backend, protocol contract, architecture, bundled runtime libraries, and companion Pion transport for Windows 32-bit and 64-bit, Linux, and Intel and Apple Silicon Macs. Stale helpers from the retired desktop audio engine are rejected before launch.</sub>

## Perfect Comms v4.1.1

Perfect Comms v4.1.1 makes voice connections easier to understand and more flexible across different networks. A new live status shows exactly what voice is waiting for, while the rebuilt cross-platform connection engine adds more relay options and stronger handling for busy lobbies.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v4.1.1/assets/brand/divider.svg" alt="divider" width="900">
</p>

### Know When Voice Is Ready

- **See exactly what voice is waiting for.**
  > <sub>The compact voice HUD now shows whether audio is starting, the lobby session is syncing, how many players are connected, or Perfect Comms is retrying after an audio failure. The message disappears once voice is ready and remains stable through brief scene transitions.</sub>

### Stronger Cross-Platform Connections

- **The peer-to-peer connection engine has been rebuilt.**
  > <sub>Windows, Steam Proton and Linux, CrossOver on Intel and Apple Silicon Macs, and Android now use Pion WebRTC for voice connections. Direct peer-to-peer voice remains preferred, while existing proximity effects, controls, and audio processing remain unchanged.</sub>

- **More ways through restrictive networks.**
  > <sub>Automatic and custom relay fallback can now use TURN over UDP or TCP, including secure TLS and DTLS options. This gives voice more connection routes to try when a firewall, VPN, or network blocks ordinary UDP.</sub>

- **Busy lobbies and reconnects are better protected.**
  > <sub>The connection layer has been hardened for several players joining together, connection restarts, network-path changes, and players leaving or rejoining without needlessly disturbing healthy voice links.</sub>

### Maintenance and Validation

- **Broader connection and release checks.**
  > <sub>Automated coverage now exercises direct voice, live relay fallback, repeated connection restarts, mixed 32-bit and 64-bit helpers, platform packaging, and a complete ten-player voice mesh.</sub>

## Perfect Comms v4.1.0

Perfect Comms v4.1.0 makes the new v4 voice engine far more dependable. Steam Proton voice now starts properly, connections repair themselves when networks change, voice remains active between matches, and a new microphone playback test lets you hear how you sound before playing. This release also adds stronger optional noise cleanup, more stable HUD behavior, and completed support for compatible mods.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v4.1.0/assets/brand/divider.svg" alt="divider" width="900">
</p>

### Voice That Starts, Stays Connected, and Recovers

- **Steam Proton voice startup is fixed.**
  > <sub>Fixed the Linux helper failure that left players seeing “Voice unavailable, retrying audio helper” while voice remained stuck on “Connecting.” Perfect Comms now verifies that the helper actually launched and can use an alternate safe location when either the Steam library or temporary folder blocks execution.</sub>

- **Voice follows VPN and network changes automatically.**
  > <sub>When a VPN connects, a network adapter changes, or a route stops working, Perfect Comms refreshes only the affected player connection and gathers a fresh direct-first path. Relay candidates remain available as fallback, so healthy connections stay peer-to-peer without requiring a manual relay mode.</sub>

- **The results-to-lobby audio cut is fixed.**
  > <sub>Voice connections now remain active while Among Us briefly rebuilds the player list after the results screen, preventing the short dropout that could happen when returning to the lobby.</sub>

- **Push To Talk now works on the results screen.**
  > <sub>Holding and releasing your Push To Talk key updates the microphone normally after a match ends instead of leaving it silent or stuck in its previous state.</sub>

### Hear and Improve Your Microphone

- **Hear your microphone before you play.**
  > <sub>The Devices page now includes a Hear Your Microphone test that plays the selected microphone through your current output. It follows microphone, speaker, and volume changes without changing your mute setting. Headphones are recommended to prevent feedback.</sub>

- **Optional one-second delayed playback.**
  > <sub>Turn on Delayed Playback to speak first and hear the result one second later. It is off by default, remains synchronized during longer tests, and clears old audio when the device or delay mode changes. The test stops automatically when you leave Devices or close settings.</sub>

- **Stronger noise suppression for difficult rooms.**
  > <sub>Desktop players can enable a stronger WebRTC noise-suppression level for louder fans, keyboards, and background noise. It is optional, off by default, and may make very quiet speech sound less natural.</sub>

### Smoother Voice and HUD Behavior

- **The speaking bar remains complete and current.**
  > <sub>Cached avatars, names, and silent-player slots remain visible when the results scene removes live player objects, keeping Show All Players stable. Deaths revealed during meetings also update promptly to the correct ghost appearance.</sub>

- **Native audio helpers are more dependable across desktop systems.**
  > <sub>Helper retries, reconnects, cancellation, and shutdown cleanup are more reliable, reducing stuck helper processes after failed launches. CrossOver preserves the signed macOS helper on Intel and Apple Silicon, while Windows selects the correct 32-bit or 64-bit components, including on Windows-on-ARM systems running the game through emulation.</sub>

### Mod Support and Maintenance

- **Completed Mod Integration API 1.1 support.**
  > <sub>Compatible mods can add custom voice channels, listener-specific routing and mutes, alternate listening positions, synchronized host options, and privacy-safe overlays without maintaining their own Perfect Comms fork.</sub>

- **Bundled components and release checks are up to date.**
  > <sub>The libraries behind networking, audio, Voice Lobbies, and the embedded .NET runtime have been refreshed. Expanded checks now guard the correct components and architecture for every supported platform.</sub>

- **The player and modding guides have been refreshed.**
  > <sub>Installation, controls, host settings, player help, and Mod Integration API documentation now match the v4 engine and its current options.</sub>

## Perfect Comms v4.0.0

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/main/assets/brand/major-divider.gif" alt="Perfect Comms" width="900">
</p>

> **Experimental voice engine:** v4.0.0 introduces a new voice engine that is still experimental. If you experience issues, return to v3.2.3 while they are investigated.

Perfect Comms v4.0.0 is the biggest update in the mod’s history. Voice chat has
been rebuilt from the inside out. The difference for players is simple: easier
setup, smoother sound, stronger connections, better platform support, and far
more control over how voice looks and feels.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v4.0.0/assets/brand/divider.svg" alt="divider" width="900">
</p>

### The New Voice Experience

- **Voice chat rebuilt for more systems.**
  > <sub>Perfect Comms now ships with its own voice engine for microphone capture, playback, sound processing, proximity effects, and player connections. It supports 32-bit and 64-bit Windows along with the host environments used by Wine, Proton, and CrossOver. Android receives its own dedicated voice engine. Everything is included with the mod. There is no separate voice application to install or keep open.</sub>

- **BetterCrewLink is no longer the voice backend.**
  > <sub>Perfect Comms v4 no longer sends voice through BetterCrewLink (BCL). Voice is now handled by the new Perfect Comms engine instead.</sub>

- **Connections that repair themselves.**
  > <sub>Voice now recovers automatically from lost connection messages, interrupted handshakes, temporary network failures, player reconnects, voice-engine restarts, and connections that get stuck halfway. Direct connections are still preferred, but Perfect Comms can automatically switch only the affected player to a relay when a direct route cannot be established.</sub>

- **No more manual NAT or Wine switches.**
  > <sub>The old Nat Fix and WineForceRelay options have been removed. Perfect Comms now tries the best direct connection first and falls back automatically when necessary, including on Wine, Proton, and CrossOver. Players no longer need to guess which network setting might make voice work.</sub>

- **Smoother sound on unstable connections.**
  > <sub>Voice buffering now adapts separately for every player, adding protection when someone’s connection becomes unstable and relaxing again when it improves. Playback timing, audio queues, and device clocks were also hardened to prevent choppy speech, growing delay, repeated frames, and audio that slowly falls out of sync.</sub>

- **All the immersive effects are still here.**
  > <sub>Distance, stereo positioning, walls, vision, radio voices, muffling, and ghost reverb have all been carried into the new engine. Echo cancellation and noise suppression now run closer to the actual audio devices where supported, with automatic timing adjustments and graceful fallback if an optional component is unavailable.</sub>

- **A proper Android experience.**
  > <sub>Android voice now uses its own native engine and touch-first HUD. Hold the microphone button to speak in Push To Talk mode. Tap Team Radio to cycle between eligible channels, or hold it to transmit. Desktop-only keybind and speaker-device settings stay hidden where they do not apply.</sub>

### Easier From the First Launch

- **A guided five-step setup.**
  > <sub>New players are welcomed with a guided Welcome, Audio, Controls, HUD, and Review flow. Choose and test your microphone and speaker, watch a live microphone meter, play an output test, select Open Mic or Push To Talk, configure important controls, and preview a complete HUD before saving everything together.</sub>

- **Every setting now explains itself.**
  > <sub>Settings, keybinds, host rules, and options added by compatible mods now have a small help button beside them. Hover it to see a clear explanation of what the option changes and whether it affects only you or the whole lobby. Device and selector rows can also reveal their complete current value, so long names are no longer left unexplained or cut off.</sub>

- **Audio devices that behave predictably.**
  > <sub>Microphones and speakers now use stable device identities instead of relying only on their position in a list. Perfect Comms can distinguish devices with identical names, follow changes to the system default, remember a temporarily disconnected device, and recover after unplugging a microphone, reconnecting Bluetooth audio, or switching devices quickly.</sub>

- **Settings designed for real screens.**
  > <sub>The settings menu has clearer sections, improved scrolling, friendlier slider targets, full-width device selection, and better behavior on smaller resolutions. Temporary previews and troubleshooting options turn themselves off when appropriate instead of becoming permanent accidental settings.</sub>

### Make Voice Look and Sound Like Yours

- **A completely customizable speaking bar.**
  > <sub>Choose from ten ready-made HUD presets or create a manual layout. Position the bar anywhere, arrange players horizontally or vertically, choose avatar direction and name placement, use single or wrapped side lanes, scale everything from 50% to 225%, and add or remove the backdrop. Show All Players can keep a stable slot for everyone instead of displaying only active speakers.</sub>

- **A simulated 15-player live preview.**
  > <sub>The HUD editor can move aside and reveal a simulated 15-player lobby preview while you make changes. It includes sample living players, ghosts, names, colors, and animated speaking activity, and responds immediately to layout changes. The simulation automatically closes when you leave the HUD editor, close settings, or restart the game.</sub>

- **Separate living and dead voice mixes.**
  > <sub>Two optional hold controls let you emphasize living players or dead players whenever you need to. Each control has independent living and dead volume levels from None to 200%. Release the control to return to the normal mix; holding both at once remains neutral.</sub>

- **More flexible controls.**
  > <sub>Every voice action can use keyboard keys, mouse buttons, exact left/right modifiers, or key combinations. The main defaults are `Shift+M` for mute, `Shift+N` for deafen, `Shift+B` for player volumes, `C` for Push To Talk, `V` for Team Radio, and `F7` to rebuild your local voice connection if audio becomes stuck.</sub>

- **A persistent mute and deafen reminder.**
  > <sub>An optional small HUD reminder stays visible whenever your microphone is muted or voice is deafened, making your current voice state obvious at a glance.</sub>

### Safer, Smarter, and Easier to Install

- **Hidden identities stay hidden.**
  > <sub>The speaking bar, meeting speaking glow, player-volume menu, and compatible mod overlays now respect disguises, concealment, blindness, and identity-changing roles. Appearance changes during speech are handled safely, and uncertain identity states hide information instead of briefly exposing the wrong player.</sub>

- **Better meeting and ghost visuals.**
  > <sub>Meeting speaking indicators follow the same privacy rules as the rest of the voice HUD. Fixed player slots remain stable through meeting transitions, and dead players now use the game’s proper ghost artwork instead of appearing as living crewmates.</sub>

- **The correct installer for every Windows store.**
  > <sub>Steam and itch.io players use `PerfectComms+dependencies-win-x86-steam-itch.zip`. Epic Games Store and Microsoft Store players use `PerfectComms+dependencies-win-x64-epic-msstore.zip`. Both include the matching BepInEx installation and Perfect Comms. `PerfectComms.dll` and `PerfectCommsAndroid.dll` remain available for existing installations.</sub>

## Perfect Comms v3.2.3

Perfect Comms v3.2.3 fixes a crash where players on CrossOver or Wine could take the whole lobby down when they joined, stops hitchy or weak-machine players from sounding choppy to everyone, and gives push-to-talk its own mic color.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v3.2.3/assets/brand/divider.svg" alt="divider" width="900">
</p>

### What's Changed

- **CrossOver / Wine players no longer crash the lobby on join.**
  > <sub>Players running Among Us through CrossOver or Wine/Proton could freeze on their voice connection the moment they joined and take everyone else down with them. Voice now waits to sync the host's settings before it connects, instead of jumping onto a local default that could hang. As part of this fix the Interstellar voice backend is temporarily disabled, so everyone uses BetterCrewLink for now.</sub>

- **Choppy, cutting voice from stuttering players is smoothed out.**
  > <sub>When a player's machine hitches (a weak PC, or running under CrossOver/Wine), their voice arrived in bursts and sounded choppy or cut out for everyone listening. The receive buffer now deepens automatically just for that player while they're stuttering, so they come through clean again. It costs nothing for players on steady connections, and the extra buffering drains back on its own as soon as they recover.</sub>

- **Push-to-talk has its own mic color.**
  > <sub>In push-to-talk mode the mic button now shows cyan when it's armed and white while you're actually transmitting, so you can tell at a glance whether you're being heard. Mute still overrides with its usual color.</sub>

- **Mute and deafen now use Shift by default, and you can bind key combos.**
  > <sub>The default keys for muting your mic and toggling your speaker are now Shift+M and Shift+N (they were M and N), so you're less likely to hit them by accident. If you were still on the old defaults you'll be moved over once; custom keybinds are left alone. You can now also rebind any voice key to a combo, like Shift, Ctrl, or Alt plus a key, or back to a single key, in the voice settings.</sub>

## Perfect Comms v3.2.2

Perfect Comms v3.2.2 makes voice smoother and more responsive on shaky connections, fixes the framerate drop in the new Unity Audio mode, and adds a meeting grace period that gives the caller the floor.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v3.2.2/assets/brand/divider.svg" alt="divider" width="900">
</p>

### What's Changed

- **Meeting grace period (new).**
  > <sub>A new host option in the renamed "Meeting & Voice" settings tab. When a meeting or report starts, everyone except the player who called it is briefly muted for a configurable 0 to 15 seconds, so the caller is heard first instead of everyone talking over each other. Set it to 0 to keep the old behavior.</sub>

- **Unity Audio mode no longer tanks your framerate.**
  > <sub>The Unity Audio compatibility mode added in 3.2.1 could drop the game to a near slideshow while you talked. Microphone encoding now runs off the render thread with its per-frame work capped, so framerate stays smooth in Unity Audio mode.</sub>

- **Smoother voice on poor connections.**
  > <sub>Voice now adapts to your link with up to 300ms of jitter buffering on both the sending and receiving sides, so audio holds together through packet loss, lag spikes, and bursty connections instead of breaking up.</sub>

- **More responsive voice.**
  > <sub>The send timing was tightened so voice goes out with less delay (around 20ms down to 5ms in steady state), making conversations feel snappier.</sub>

- **Voice reliability and audio fixes.**
  > <sub>A round of voice-engine fixes: a send-timing race, thread-safe muting, more accurate packet-loss recovery, audio memory-leak cleanup, and corrected meeting-mute timing. On Android, the Interstellar backend no longer produces occasional clicks or doubled audio. Plus assorted memory, HUD, and diagnostics fixes.</sub>

## Perfect Comms v3.2.1

Perfect Comms v3.2.1 adds a Unity Audio compatibility mode for players whose normal audio path does not work, and fixes deafening.

<p align="center">
  <img src="https://raw.githubusercontent.com/artriy/Perfect-Comms/v3.2.1/assets/brand/divider.svg" alt="divider" width="900">
</p>

### What's Changed

- **Unity Audio compatibility mode (new).**
  > <sub>A new "Use Unity Audio" toggle in Audio settings routes microphone capture and playback through Unity's own audio engine instead of the BASS path, for players whose voice does not work otherwise (some Wine/CrossOver setups and unusual audio devices). It is a fallback: it has a little more delay and runs without noise suppression, echo cancellation, or auto mic gain, since those need the BASS path. Enabling it switches those three off automatically and turns them back on when you disable it.</sub>

- **Deafening is fixed.**
  > <sub>Deafening had no effect: you would still hear everyone after deafening. It now properly silences all incoming voice.</sub>

## Perfect Comms v3.2.0

Perfect Comms v3.2.0 rebuilds the voice processing chain around the same battle-tested WebRTC audio engine that powers Chrome and Google Meet: real AEC3 echo cancellation and AGC2 automatic gain, with DeepFilterNet still handling noise suppression. Text-to-speech and other line-in audio now come through continuously instead of cutting out, and the whole mod is lighter on memory and more crash-resistant.

### What's Changed

- **Real echo cancellation (WebRTC AEC3).**
  > <sub>The Echo Cancellation toggle now runs WebRTC's AEC3, the same canceller used by Chrome and Google Meet, replacing the older Speex one. It properly removes speaker bleed for players who don't use headphones, including the harsh echo from cheap laptop speakers that the old canceller couldn't touch.</sub>

- **Smarter automatic gain (WebRTC AGC2).**
  > <sub>Auto Mic Gain now uses WebRTC's AGC2, which raises your level only while you're actually talking, so quiet mics are boosted cleanly without pumping up background noise during pauses.</sub>

- **Text-to-speech and line-in no longer cut out.**
  > <sub>Synthetic or already-clean audio (TTS, virtual cables, line-in) used to get chopped at the silent gaps between words. The mic now transmits continuously, so that audio comes through clean and uninterrupted, with no setting to flip.</sub>

- **Cleaner noise suppression.**
  > <sub>DeepFilterNet noise suppression is now capped so it removes noise without ever fully erasing quiet or steady speech, fixing voices that could sound thin or garbled in some conditions.</sub>

- **Lighter and smoother.**
  > <sub>Cut the mod's per-frame memory allocations dramatically, reducing the tiny stutters garbage collection can cause, and fixed a memory leak that slowly grew across a play session.</sub>

- **More stable.**
  > <sub>Closed several rare crash paths (malformed network messages, audio-thread and timer errors, and exceptions inside game patches) so they now log and recover instead of taking down the game, plus a few thread-safety fixes that matter most on 32-bit clients.</sub>

- **Fails gracefully.**
  > <sub>If a native audio component can't load, voice now degrades cleanly instead of breaking, and a couple of game-version-sensitive lookups warn once and keep going instead of silently breaking on an Among Us update.</sub>

## Perfect Comms v3.1.0

Perfect Comms v3.1.0 is a deep audio-quality release. The entire voice engine has been rebuilt around the BASS audio library, a native Opus 1.6.1 codec with neural packet-loss recovery, and DeepFilterNet 3 noise suppression, with a new adaptive playout buffer that makes voice noticeably smoother on any connection. It also updates the WebRTC transport for more reliable peer connections, restores the per-situation radio, wall-occlusion, and ghost voice filters with proper reverb, and centers the speaking bar with automatic row wrapping.

### What's Changed

- **Rebuilt audio engine.**
  > <sub>Microphone capture and playback are now built on the BASS audio library instead of the old NAudio stack, with a single managed mixer driving every voice. This is the foundation for the quality and smoothness improvements below, and it is lower-latency and cleaner under the hood.</sub>

- **Native Opus 1.6.1 with neural packet-loss recovery.**
  > <sub>The managed Opus port has been replaced with native libopus 1.6.1. You get better voice quality at the same bitrate, plus deep PLC (neural concealment of lost audio) and DRED (Deep REDundancy), which reconstructs multiple dropped frames from later packets. Brief connection hiccups no longer chop your voice the way they used to.</sub>

- **DeepFilterNet 3 noise suppression.**
  > <sub>RNNoise has been replaced with DeepFilterNet 3, a much stronger neural denoiser. Keyboard, fans, and background chatter are removed far more cleanly while your actual voice stays natural. It is still toggleable under Noise Suppression.</sub>

- **Much smoother voice.**
  > <sub>Fixed audio that could sound choppy or cut in and out. A new low-latency playback path plus an adaptive playout buffer that sizes itself to your connection: snappy and low-delay on a clean link, automatically adding cushion when the network gets bursty, then relaxing again when it clears.</sub>

- **Smarter jitter buffer.**
  > <sub>Playout depth is driven by a real per-player jitter estimate and is aware of FEC/DRED recovery frames, so unstable connections stay smooth while stable ones keep low delay.</sub>

- **More reliable peer connections.**
  > <sub>Updated the WebRTC voice transport (SIPSorcery 10.0.6 to 10.0.10, the latest release). Voice peer connections establish more reliably and stay stable, so players connect to each other faster and drop less often.</sub>

- **Per-situation voice filters restored.**
  > <sub>Radio voices get a crisp radio-band filter, voices heard through walls or while occluded are muffled with a subtle next-room reverb, and ghost voices heard by the living (Medium and similar roles) get a ghostly reverb. Ghosts talking to each other still sound completely normal.</sub>

- **Android parity.**
  > <sub>The Android build now runs the same voice mixer as desktop: per-situation filters, reverb, click-free fades, and gliding volume and stereo position.</sub>

- **Centered speaking bar with automatic rows.**
  > <sub>The speaking bar is now centered on screen and automatically wraps the talking players into multiple rows when there are too many to fit one line, so names always stay on-screen and readable instead of running off the edge.</sub>

- **Speaking bar shows the correct identity.**
  > <sub>During meetings the bar no longer briefly flashes a Glitch/Morphling's disguise for the first couple of frames before settling on their real appearance, while comms sabotage still greys everyone out (including when a meeting is called while comms is down). Also fixed players occasionally rendering as a grey blank in the bar.</sub>

- **Under the hood.**
  > <sub>Hardened the native-library loaders (hash-validated extraction, proper cleanup), removed unused audio components, and documented all bundled third-party library licenses.</sub>

## Perfect Comms v3.0.0

Perfect Comms is now fully standalone: no Reactor or MiraAPI required. This release also brings a rebuilt in-game settings menu, fully rebindable keyboard and mouse controls, hardware echo cancellation, a reworked speaking bar, another round of audio-reliability fixes, and a public mod-integration API so other mods can register custom voice behaviours without forking Perfect Comms.

### What's Changed

- **No more Reactor or MiraAPI dependency.**
  > <sub>Perfect Comms now runs on vanilla Among Us plus BepInEx alone. It still shares a `BepInEx/plugins` folder with mods that use Reactor or MiraAPI (such as TOU-Mira) without conflict, and TOU-Mira voice integrations still activate automatically when those mods are present.</sub>

- **Rebuilt in-game settings menu.**
  > <sub>Client settings open from the Among Us Options menu (or press F10); host voice settings open from the lobby game-settings console (or press F11). Settings are grouped into clear sections with the more advanced options tucked away. This replaces the previous MiraAPI settings tab, all the same voice and role rules are now here.</sub>

- **Fully rebindable keyboard and mouse controls.**
  > <sub>Every voice keybind can be rebound to any keyboard key or mouse button, including MB4 and MB5. Existing keybinds reset to their defaults **once** because of the new binding system.</sub>

- **Hardware echo cancellation.**
  > <sub>A new native acoustic echo canceller in the mic pipeline, with an Echo Cancellation toggle under Noise Suppression, stops the game and other players echoing back through your mic.</sub>

- **New host options.**
  > <sub>Added mid-game Team Radio, unlimited ghost hearing range, and a "jail persists if the Jailor dies" rule.</sub>

- **Reworked speaking bar.**
  > <sub>New always-on all-players mode, adjustable scale and position, and an optional backdrop for readability. Names always match your in-game nameplates, stay clamped on-screen, and the bar no longer bobs or flashes the wrong font.</sub>

- **More audio reliability fixes.**
  > <sub>Fixed left/right stereo drifting apart, smoother voice on bursty connections, faster recovery for stuck players, and a push-to-talk indicator that always releases when you stop talking.</sub>

- **Public mod-integration API (`PerfectComms.Api`).**
  > <sub>Other mods can register custom voice behaviours as a soft dependency, no forking required: gates (mute/muffle), channels (private/team audio), listener origin, host options, and a host-panel tab. Callbacks are fail-closed, so a broken or missing mod has no effect on voice. Full guide and reference on the [wiki](https://github.com/artriy/Perfect-Comms/wiki/Mod-Integration).</sub>

## Perfect Comms v2.1.7

This Perfect Comms release is a big audio-quality pass: smoother voice with no clicks or pops, lower delay that adapts to each player's connection, fixed Bluetooth headsets going silent, better quiet-mic handling, and a Jailor unmute that always sticks.

### What's Changed

- **Voice is much smoother all around.**
  > <sub>Moving around, crossing walls, muting, and range edges no longer click or cut hard. Volume and stereo position glide instead of stepping.</sub>

- **Fixed short stutter/echo bursts during packet loss.**
  > <sub>A bug repeated the same split-second of audio when several packets were lost in a row.</sub>

- **Lower voice delay that adapts per player.**
  > <sub>Stable connections get snappier voice, laggy players automatically get more cushion, and their late-arriving audio is played instead of dropped.</sub>

- **Voice quality adapts to real packet loss.**
  > <sub>Loss protection is dialed up only for players who need it, instead of a fixed setting for everyone.</sub>

- **Bluetooth headsets no longer go deaf.**
  > <sub>Using one headset (e.g. AirPods) for mic and speaker could leave you unable to hear; the mod now recovers automatically, and switches back to full listening quality while you're muted.</sub>

- **Quiet microphones sound better.**
  > <sub>Mic boost survives mute cycles, rumble no longer trips the voice gate, and unmuting is instant.</sub>

- **The Jailor's unmute always sticks now.**
  > <sub>It re-confirms itself automatically so a missed unmute can't leave the jailee silent.</sub>

- **Speaking-bar names match in-game names.**
  > <sub>Same font and outline as player nameplates, readable over any background.</sub>

- **More self-healing playback.**
  > <sub>A stalled or erroring output device restarts itself instead of staying silent until you switch devices.</sub>
## Perfect Comms v2.1.6

This Perfect Comms release reworks the per-player volume menu into a live mixer, adds an optional backdrop and steadier animation to the speaking bar, lets you switch between Open Mic and Push-to-Talk with a hotkey, fixes the volume menu hiding in the dark, and routes role-based spectators as voice ghosts.

### What's Changed

- **A live per-player volume mixer.** The volume menu now shows each player's avatar and a live voice meter that moves as they talk, you can scroll the list with the mouse wheel, and the roster refreshes as players join or leave. This makes it much easier to find and set the right person's level.
- **The volume menu no longer hides in the dark.** During blackouts and low-vision moments the menu's rows could vanish; they now stay fully visible regardless of in-game vision.
- **Optional speaking-bar backdrop, steadier bar.** You can switch on a subtle backdrop behind the speaking bar for readability over busy scenes, the talking icons now hold a stable order instead of jumping around, and their level animation is smoothed.
- **Switch Open Mic ↔ Push-to-Talk on the fly.** A new keybind flips your microphone between Open Mic and Push-to-Talk and shows a quick confirmation. It's unbound by default. Assign a key to it in the keybind settings to use it.
- **Spectators are handled as ghosts for voice.** Players the game marks dead through a role (e.g. Town of Us Spectator) are now routed like ghosts, so they hear and are heard the same as other dead players.
- **No more stray hover highlight on the voice buttons.** The mic/speaker buttons (cloned from the in-game map button) no longer show a leftover hover sprite.
## Perfect Comms v2.1.5

This Perfect Comms release fixes the speaking bar showing the wrong player and voice being heard across the whole map, clears an end-game crash that could disconnect you, tones down the end-game group call so it isn't ear-splitting, and trims a few more audio drop-outs.

### What's Changed

- **The speaking bar shows the right person again.** Fixed a bug where the talking indicator in meetings and on the in-game bar could light up the wrong player, or two players' voices could collapse onto a single name, especially after rounds where player slots got reshuffled. Each voice now reliably follows its own player.
- **No more being heard from across the map.** Caused by the same mix-up: a player's voice could ignore distance and come through from anywhere (and, the flip side, a player who was actually nearby could go unheard). Voice now tracks each player's real position again, so proximity works as intended.
- **Fixed a crash/kick around the end-game screen.** Dropping to the end-game results and rejoining the lobby could spam errors and get you disconnected. The voice HUD now steps aside cleanly during that transition, so the freeze-and-kick is gone.
- **The end-game group call is no longer ear-splitting.** On the results screen everyone was played back at full blast; levels are now dialed to a comfortable volume while staying clearly audible.
- **Fewer audio drop-outs and a little less stutter.** Tiny/odd audio frames are now handled cleanly instead of being dropped, and the playback buffer carries a touch more cushion so brief network hiccups don't turn into gaps.
## Perfect Comms v2.1.4

This Perfect Comms release gives the voice settings menu a cleaner, easier-to-read makeover, keeps voice and avatars from dropping out on the end-game screen, and rounds out another batch of fixes for one-way and patchy voice.

### What's Changed

- **A cleaner voice settings menu.** The settings page has a tidier two-column layout, easier-on-the-eyes colors, and shorter labels that no longer wrap onto two lines. The microphone and speaker pickers now clearly show which is which, instead of both just reading "Default".
- **Voice now stays put through the end-game screen.** Voice keeps working through the end screen and between rounds, and player voice and speaker icons no longer vanish the moment a game ends.
- **A heads-up when you refresh voice.** Pressing a voice-refresh key now shows a quick on-screen message so you know it worked. Refreshing as the host lets everyone know; refreshing just for yourself shows only to you.
- **No more freeze when switching microphone or speaker mid-game.** Changing your mic or speaker quickly no longer risks a crash, and leaving the speaker on "Default" now follows whatever your computer's current default speaker is.
- **Fixed some players' audio going flat (pure mono, non-directional).** In some cases a player's voice lost its proximity and direction and came through evenly no matter where they were on the map. Their audio now plays back with the correct distance and direction again.
- **More "I can hear you but you can't hear me" fixes.** Several cases where a voice link got stuck on one side could leave one player audible but unable to hear back, or leave a connected player completely silent. These cases now sort themselves out automatically instead of needing a rejoin.
- **Voice is a touch smoother still.** Continued behind-the-scenes tuning keeps speech steady through uneven connections, with a little less stutter.
## Perfect Comms v2.1.3

This Perfect Comms release targets choppy / robotic voice (the kind where a player's speech breaks up or cuts in and out, especially at random and in fuller lobbies) and makes voice connections noticeably more reliable.

### What's Changed

- **Much less choppy/robotic voice.** The playback buffer now adapts to each player's connection, deepening when their audio is arriving unevenly and easing back down between sentences, so brief network hiccups get smoothed over instead of turning into stutter.
- **Fewer voice drop-outs from bad packets.** A player who sends the occasional undecodable audio frame is no longer muted for several seconds; only a genuinely broken stream is parked, and short gaps are concealed instead of cutting out.
- **Players not hearing each other right after joining a fresh lobby is fixed.** When two players joined a brand-new lobby together, their voice link could get stuck so neither could hear the other until someone left and rejoined. The connection now repairs itself automatically.
- **Steadier voice connections.** Fixed a recovery loop that could repeatedly reset all of your voice connections when one player couldn't be reached, and tightened per-peer reconnection so a stuck connection re-establishes on its own. This reduces cases where you suddenly stop hearing someone mid-game while still being heard.
- **Smoother framerate in big lobbies.** Voice-related per-frame work that grew with the player count was trimmed, reducing hitches at 12+ players.
## Perfect Comms v2.1.2

This Perfect Comms release is a big stutter-and-lag-spike fix. If voice was making your game hitch, freeze, or drop, it should feel much smoother now.

### What's Changed

- **Much smoother voice.** The stutters and lag spikes around joining, talking, and hearing players should be greatly reduced.
- **Nat Fix (on by default).** Helps players behind strict NATs or firewalls connect when they couldn't before.
- **Steadier connections** that recover more gracefully instead of constantly dropping and retrying.
## Perfect Comms v2.1.1

This Perfect Comms release fixes an echo / doubled-voice bug that built up during a match whenever players' connections dropped and reconnected.

### What's Changed

- Fixed an "echoey" / doubled-voice bug: when a player's connection dropped and reconnected, their old voice channel was left running instead of being cleaned up, so the same player could be heard more than once and unused "zombie" channels piled up over the match. Each player now keeps exactly one live voice channel, and the stale one is torn down on reconnect.
- Reduced the matching slow build-up of wasted voice processing those leftover channels caused, so longer games stay lighter on CPU and audio.
## Perfect Comms v2.1.0

This Perfect Comms release adds new ways to arrange the speaking bar, gives the Parasite and Puppeteer their own special hearing, and polishes the speaking-bar player icons.

### What's Changed

- Added a **Speaking Bar Name Position** setting so you can place each player's name below, above, to the left, or to the right of their icon.
- Added **Parasite: Also Hear Controlled Victim**, letting the Parasite also hear everyone around the player they're controlling, on top of their own surroundings.
- Added **Puppeteer: Hear From Controlled Victim**, so the Puppeteer hears from their victim's location while controlling them, matching where they're actually looking.
- Fixed players' cosmetics (hats, skins, and visors) not showing on the speaking bar - they now load instantly and correctly.
- The speaking-bar icon no longer twists or leans as you move; it stays a clean, upright crewmate.
- Further tightened disguise and camouflage hiding, so even more concealed players and faded ghosts can't be identified from the speaking bar.
- Rainbow-colored players now get a matching rainbow glow on their meeting highlight.
- Refined the speaking ring's look and stopped names from clipping the ring above them in vertical layouts.
## Perfect Comms v2.0.9

This Perfect Comms release lets you customize the speaking bar, cleans up the in-game UI and player icons, and makes voice more reliable and secure.

### What's Changed

- Added a Voice Falloff Softness slider so you can adjust how clearly you hear players while they're within your vision.
- Added sliders to move the speaking bar wherever you like.
- Added a setting to switch the speaking bar between a horizontal or vertical layout, and player icons now stay neatly inside the screen.
- Added a setting to let team radio be used during meetings.
- Added a setting to move the Jailor's unmute button, so now you can unmute the jailee from the meeting card and switch the button's position in the settings.
- Improved the in-game UI and player icons, so speaking indicators always show up correctly and an icon is never left invisible.
- Disguised players (camouflage, morph, and similar) now stay properly hidden on the speaking bar instead of giving away who they are.
- Voice now repairs itself when someone's connection drops, instead of everyone needing to refresh.
- Strengthened security so hackers can't tamper with host settings or pretend to be someone else.
- Improved audio so it sounds smoother, with fewer crackles and dropouts when a connection hiccups.
- Fixed voice in more situations like joining a lobby, exile, meetings, security cameras, and when players disconnect, so the right people can always hear each other.
## Perfect Comms v2.0.8

This Perfect Comms release improves camera hearing, room-policy mutes, and boosted voice playback.

### What's Changed

- Fixed Airship camera hearing with real Airship camera positions.
- Added **Ghosts Also Meeting/Lobby Only** under **Meetings/Lobby Only**.
- Blocked disconnected, dummy, and invisible players from being routed through voice.
- Fixed speaker mute so volume refreshes and backend reconnects do not restore playback while deafened.
- Let player volume reach 200% while keeping boosted playback limited to avoid fuzzy output.
## Perfect Comms v2.0.7

This Perfect Comms release fixes transitional voice routing around exile and other non-gameplay phases.

### What's Changed

- Changed Exile to use meeting-style voice routing instead of falling back to lobby proximity.
- Preserved meeting mutes during Exile, so blackmailed and jailed players stay muted through the exile sequence.
- Kept task-only role mutes out of lobby-like phases such as Intro, EndGame, Menu, and Unknown.
- Shared the same phase policy across BetterCrewLink and Interstellar so both backends route transitions consistently.
## Perfect Comms v2.0.6

This Perfect Comms release expands role-aware voice behavior and adds team radio channels.

### What's Changed

- Added **Crewpostor: Use Impostor Voice**.
  - Crewpostor can use impostor radio, talk with impostors in vents, and inherit the other impostor-only voice behavior when this option is enabled.
- Added **Swooper: Mute While Swooped**.
- Added **Glitch: Mute Hacked Players**.
- Added **Medium: Ghost Voice**.
  - Choose **Medium -> Ghost**, **Ghost -> Medium**, or **Both** for private Medium spirit communication.
- Added **Eclipsal/Grenadier: Muffle Blinded/Flashed Hearing**.
- Added **Hypnotist: Muffle Hypnotized During Hysteria**.
- Changed **Impostor Radio** to **Team Radio** with:
  - **Team Radio - Impostors**
  - **Team Radio - Vampires**
  - **Team Radio - Lovers**
  - Players with more than one radio can cycle between channels with a keybind.
- Improved Role Voice Rules labels with role-first wording, role-matched colors thanks to @idkimneil in [#6](https://github.com/artriy/Perfect-Comms/pull/6), and cleaner ordering.
## Perfect Comms v2.0.5

This Perfect Comms release adds role-based voice controls and improves voice button placement.

### What's Changed

- Added a new *Perfect Comms: Role Voice Rules* settings section for role-specific voice behavior.
- Added *Mute Blackmailed Next Round* so blackmail can optionally continue after the meeting.
- Added options to mute parasite-controlled and puppeteer-controlled players.
- Improved voice button placement so mic, speaker, and jail controls stay visible and easier to position near the screen edge.
## Perfect Comms v2.0.4

This Perfect Comms release focuses on BetterCrewLink audio stability and chat input safety.

### Fixed

- Fixed intermittent fuzzy/static audio in BetterCrewLink lobbies when multiple voices overlap.
- Fixed hot BetterCrewLink mic frames after RNNoise so clipped capture peaks are limited before Opus encoding.
- Fixed chat input handling so Perfect Comms no longer intercepts textbox typing, preventing crashes while typing in chat.
## Perfect Comms v2.0.3

This Perfect Comms release focuses on voice stability, speaking ring accuracy, noise suppression, and cleaner in-game controls.

### What's Changed

- Added volume controls sliders by @idkimneil in [#4](https://github.com/artriy/Perfect-Comms/pull/4).
- Reduced mute/unmute spam crashes with serialized mic capture transitions.
- Added RNNoise noise suppression, enabled by default.
- Added host and local voice refresh keybinds.
- Added MB4/MB5 mouse button bind support (in local settings).
- Fixed comms sabotage voice blocking detection.
- Fixed rainbow, idle-pose, and morph/mimic speaking ring portraits.
- Added Middle Left and Middle Right speaking bar positions.
- Improved voice overlay positioning, layout, scale, and defaults.
- Improved the player volume menu and slider behavior.
- Deafened players now stop transmitting voice too.
- Removed `.DS_Store`.
## Perfect Comms v2.0.2

### What's Changed

- Fixed voice keybind behavior while chat is open so toggle shortcuts stay blocked during typing, while push-to-talk and impostor radio can still activate after a short hold.
- Fixed printable push-to-talk and radio keys in chat so quick taps type normally, but held voice keys do not spam characters into the message field.
- Fixed push-to-talk chat handling so it only applies when Mic Mode is set to Push To Talk.
- Fixed impostor radio chat handling so it only applies when the local player can actually use the radio, including blackmailer and jailor voice-block rules.
- Changed Push To Talk mode so the mute toggle no longer creates a redundant manual mute state; released PTT remains muted and holding PTT still transmits.
- Fixed the `VC: mic unavailable` warning appearing during normal Push To Talk idle mute.
## Perfect Comms v2.0.1

### What's Changed

- Changed the assets to a cleaner version by @AtonyGit in #1.
- Fixed the Voice Lobbies close X so it sits in a clearer top-right position and is easier to see.
- Fixed bottom-positioned speaking indicators so player names no longer clip off the bottom of the screen while staying close to the edge.
## Perfect Comms v2.0.0

This is the backend rewrite release. Perfect Comms no longer depends on Among Us RPCs for voice audio transport. Voice now runs through selectable voice backends, with BetterCrewLink live voice as the default path and Interstellar available as an alternate backend.

### Most Notable Changes

- BetterCrewLink backend + lobby browser is now fully built in.
- Interstellar backend is now fully supported.
- Voice transport no longer depends on Among Us RPCs.
- Public lobby browsing and publishing are much better.
- Directional and proximity audio are more reliable.
- Host settings now sync across the lobby automatically.
- Debug logs are quiet by default unless you turn them on.
## Perfect Comms v1.0.2

### Changed

- Reposition Perfect Comms as its own Among Us proximity chat mod.
- Document TOU-Mira as a supported mod behaviour instead of a requirement.
- Add `PerfectComms+dependencies.zip` with BepInEx, MiraAPI, Reactor, and Perfect Comms.
- Cache supported-mod role state for 0.25 seconds instead of checking every voice snapshot.

### Fixed

- Missing TOU-Mira role/modifier types now no-op and retry instead of being cached as unavailable.
- Default voice lobby title now uses `Perfect Comms` instead of TOU-Mira-specific wording.

## Perfect Comms v1.0.1

### Changed

- Update notifications now check GitHub releases directly for future releases.

### Fixed

- Speaking indicators now render above the Among Us game UI instead of being hidden behind menus or scene elements.
- Speaking-ring player icons now show a stable recolored crewmate body with loaded cosmetics, without cloning live player objects.

## Perfect Comms v1.0.0

**PUBLIC BETA: expect bugs.**
This is the first public Perfect Comms release. It is ready for real lobbies, but still needs wider testing.

### What makes this release special

- **Fully integrated proximity chat.** Voice runs inside Among Us, no separate voice app needed.
- **Supported mod behaviours.** TOU-Mira blackmailed players stay muted, and Jailor can unmute jailed players.
- **No more hackers messing with the voice.** Voice stays tied to compatible Perfect Comms clients.
- **Built-in Voice Lobbies.** Find compatible voice lobbies from the main menu and join with one click.
- **In-game update prompt.** Players can be sent straight to the newest release when an update drops.

### Added

- Native Perfect Comms BepInEx plugin.
- Windows release build: `PerfectComms.dll`.
- Android release build: `PerfectCommsAndroid.dll`.
- Dependency bundle: `PerfectComms+dependencies.zip` with BepInEx, MiraAPI, Reactor, and Perfect Comms.
- Proximity voice during gameplay.
- Meeting voice behavior for modded role states.
- Jailor-controlled jailed voice support.
- Blackmailer voice blocking support.
- Voice Lobby button and lobby browser.
- Compatible-client voice checks.
- Reactor mod list entry for Perfect Comms.
- Clickable main-menu update notification.

### Beta notes

- **Expect bugs in some lobbies.** Report anything weird to me.
- 15-player support is designed and hardened, but still needs more real public-lobby testing.
- Everyone in the lobby should use the same Perfect Comms release.

### Install

Download `PerfectComms.dll` and place it here:

```text
BepInEx/plugins/PerfectComms.dll
```

Requires MiraAPI and Reactor from your mod pack. Supported mod behaviours activate only when matching mods are installed.
