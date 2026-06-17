# Changelog

## Perfect Comms v3.0.1

Perfect Comms v3.0.1 is a deep audio-quality release. The entire voice engine has been rebuilt around the BASS audio library, a native Opus 1.6.1 codec with neural packet-loss recovery, and DeepFilterNet 3 noise suppression, with a new adaptive playout buffer that makes voice noticeably smoother on any connection. It also updates the WebRTC transport for more reliable peer connections, restores the per-situation radio, wall-occlusion, and ghost voice filters with proper reverb, and centers the speaking bar with automatic row wrapping.

### What's Changed

- **Rebuilt audio engine.**
  > <sub>Microphone capture and playback are now built on the BASS audio library instead of the old NAudio stack, with a single managed mixer driving every voice. This is the foundation for the quality and smoothness improvements below, and it is lower-latency and cleaner under the hood.</sub>

- **Native Opus 1.6.1 with neural packet-loss recovery.**
  > <sub>The managed Opus port has been replaced with native libopus 1.6.1. You get better voice quality at the same bitrate, plus deep PLC (neural concealment of lost audio) and DRED (Deep REDundancy), which reconstructs up to roughly 400 ms of dropped audio from later packets. Brief connection hiccups no longer chop your voice the way they used to.</sub>

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

See `docs/release-notes-v2.1.7.md` for the full release notes.

## Perfect Comms v2.1.6

This Perfect Comms release reworks the per-player volume menu into a live mixer, adds an optional backdrop and steadier animation to the speaking bar, lets you switch between Open Mic and Push-to-Talk with a hotkey, fixes the volume menu hiding in the dark, and routes role-based spectators as voice ghosts.

### What's Changed

- **A live per-player volume mixer.** The volume menu now shows each player's avatar and a live voice meter that moves as they talk, you can scroll the list with the mouse wheel, and the roster refreshes as players join or leave — so it's much easier to find and set the right person's level.
- **The volume menu no longer hides in the dark.** During blackouts and low-vision moments the menu's rows could vanish; they now stay fully visible regardless of in-game vision.
- **Optional speaking-bar backdrop, steadier bar.** You can switch on a subtle backdrop behind the speaking bar for readability over busy scenes, the talking icons now hold a stable order instead of jumping around, and their level animation is smoothed.
- **Switch Open Mic ↔ Push-to-Talk on the fly.** A new keybind flips your microphone between Open Mic and Push-to-Talk and shows a quick confirmation. It's unbound by default — assign a key to it in the keybind settings to use it.
- **Spectators are handled as ghosts for voice.** Players the game marks dead through a role (e.g. Town of Us Spectator) are now routed like ghosts, so they hear and are heard the same as other dead players.
- **No more stray hover highlight on the voice buttons.** The mic/speaker buttons (cloned from the in-game map button) no longer show a leftover hover sprite.

See `docs/release-notes-v2.1.6.md` for the full release notes.

## Perfect Comms v2.1.5

This Perfect Comms release fixes the speaking bar showing the wrong player and voice being heard across the whole map, clears an end-game crash that could disconnect you, tones down the end-game group call so it isn't ear-splitting, and trims a few more audio drop-outs.

### What's Changed

- **The speaking bar shows the right person again.** Fixed a bug where the talking indicator — in meetings and on the in-game bar — could light up the wrong player, or two players' voices could collapse onto a single name, especially after rounds where player slots got reshuffled. Each voice now reliably follows its own player.
- **No more being heard from across the map.** Caused by the same mix-up: a player's voice could ignore distance and come through from anywhere (and, the flip side, a player who was actually nearby could go unheard). Voice now tracks each player's real position again, so proximity works as intended.
- **Fixed a crash/kick around the end-game screen.** Dropping to the end-game results and rejoining the lobby could spam errors and get you disconnected. The voice HUD now steps aside cleanly during that transition, so the freeze-and-kick is gone.
- **The end-game group call is no longer ear-splitting.** On the results screen everyone was played back at full blast; levels are now dialed to a comfortable volume while staying clearly audible.
- **Fewer audio drop-outs and a little less stutter.** Tiny/odd audio frames are now handled cleanly instead of being dropped, and the playback buffer carries a touch more cushion so brief network hiccups don't turn into gaps.

See `docs/release-notes-v2.1.5.md` for the full release notes.

## Perfect Comms v2.1.4

This Perfect Comms release gives the voice settings menu a cleaner, easier-to-read makeover, keeps voice and avatars from dropping out on the end-game screen, and rounds out another batch of fixes for one-way and patchy voice.

### What's Changed

- **A cleaner voice settings menu.** The settings page has a tidier two-column layout, easier-on-the-eyes colors, and shorter labels that no longer wrap onto two lines. The microphone and speaker pickers now clearly show which is which, instead of both just reading "Default".
- **Voice now stays put through the end-game screen.** Voice keeps working through the end screen and between rounds, and player voice and speaker icons no longer vanish the moment a game ends.
- **A heads-up when you refresh voice.** Pressing a voice-refresh key now shows a quick on-screen message so you know it worked. Refreshing as the host lets everyone know; refreshing just for yourself shows only to you.
- **No more freeze when switching microphone or speaker mid-game.** Changing your mic or speaker quickly no longer risks a crash, and leaving the speaker on "Default" now follows whatever your computer's current default speaker is.
- **Fixed some players' audio going flat (pure mono, non-directional).** In some cases a player's voice lost its proximity and direction and came through evenly no matter where they were on the map. Their audio now plays back with the correct distance and direction again.
- **More "I can hear you but you can't hear me" fixes.** Several cases where a voice link got stuck on one side — so one player could be heard but couldn't hear back, or a connected player made no sound at all — now sort themselves out automatically instead of needing a rejoin.
- **Voice is a touch smoother still.** Continued behind-the-scenes tuning keeps speech steady through uneven connections, with a little less stutter.

See `docs/release-notes-v2.1.4.md` for the full release notes.

## Perfect Comms v2.1.3

This Perfect Comms release targets choppy / robotic voice (the kind where a player's speech breaks up or cuts in and out, especially at random and in fuller lobbies) and makes voice connections noticeably more reliable.

### What's Changed

- **Much less choppy/robotic voice.** The playback buffer now adapts to each player's connection, deepening when their audio is arriving unevenly and easing back down between sentences, so brief network hiccups get smoothed over instead of turning into stutter.
- **Fewer voice drop-outs from bad packets.** A player who sends the occasional undecodable audio frame is no longer muted for several seconds; only a genuinely broken stream is parked, and short gaps are concealed instead of cutting out.
- **Players not hearing each other right after joining a fresh lobby is fixed.** When two players joined a brand-new lobby together, their voice link could get stuck so neither could hear the other until someone left and rejoined. The connection now repairs itself automatically.
- **Steadier voice connections.** Fixed a recovery loop that could repeatedly reset all of your voice connections when one player couldn't be reached, and tightened per-peer reconnection so a stuck connection re-establishes on its own. This reduces cases where you suddenly stop hearing someone mid-game while still being heard.
- **Smoother framerate in big lobbies.** Voice-related per-frame work that grew with the player count was trimmed, reducing hitches at 12+ players.

See `docs/release-notes-v2.1.3.md` for the full release notes.

## Perfect Comms v2.1.2

This Perfect Comms release is a big stutter-and-lag-spike fix — if voice was making your game hitch, freeze, or drop, it should feel much smoother now.

### What's Changed

- **Much smoother voice** — the stutters and lag spikes around joining, talking, and hearing players should be greatly reduced.
- **Nat Fix (on by default)** — helps players behind strict NATs or firewalls connect when they couldn't before.
- **Steadier connections** that recover more gracefully instead of constantly dropping and retrying.

See `docs/release-notes-v2.1.2.md` for the full release notes.

## Perfect Comms v2.1.1

This Perfect Comms release fixes an echo / doubled-voice bug that built up during a match whenever players' connections dropped and reconnected.

### What's Changed

- Fixed an "echoey" / doubled-voice bug: when a player's connection dropped and reconnected, their old voice channel was left running instead of being cleaned up, so the same player could be heard more than once and unused "zombie" channels piled up over the match. Each player now keeps exactly one live voice channel, and the stale one is torn down on reconnect.
- Reduced the matching slow build-up of wasted voice processing those leftover channels caused, so longer games stay lighter on CPU and audio.

See `docs/release-notes-v2.1.1.md` for the full release notes.

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

See `docs/release-notes-v2.1.0.md` for the full release notes.

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

See `docs/release-notes-v2.0.9.md` for the full release notes.

## Perfect Comms v2.0.8

This Perfect Comms release improves camera hearing, room-policy mutes, and boosted voice playback.

### What's Changed

- Fixed Airship camera hearing with real Airship camera positions.
- Added **Ghosts Also Meeting/Lobby Only** under **Meetings/Lobby Only**.
- Blocked disconnected, dummy, and invisible players from being routed through voice.
- Fixed speaker mute so volume refreshes and backend reconnects do not restore playback while deafened.
- Let player volume reach 200% while keeping boosted playback limited to avoid fuzzy output.

See `docs/release-notes-v2.0.8.md` for the full release notes.

## Perfect Comms v2.0.7

This Perfect Comms release fixes transitional voice routing around exile and other non-gameplay phases.

### What's Changed

- Changed Exile to use meeting-style voice routing instead of falling back to lobby proximity.
- Preserved meeting mutes during Exile, so blackmailed and jailed players stay muted through the exile sequence.
- Kept task-only role mutes out of lobby-like phases such as Intro, EndGame, Menu, and Unknown.
- Shared the same phase policy across BetterCrewLink and Interstellar so both backends route transitions consistently.

See `docs/release-notes-v2.0.7.md` for the full release notes.

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

See `docs/release-notes-v2.0.6.md` for the full release notes.

## Perfect Comms v2.0.5

This Perfect Comms release adds role-based voice controls and improves voice button placement.

### What's Changed

- Added a new *Perfect Comms: Role Voice Rules* settings section for role-specific voice behavior.
- Added *Mute Blackmailed Next Round* so blackmail can optionally continue after the meeting.
- Added options to mute parasite-controlled and puppeteer-controlled players.
- Improved voice button placement so mic, speaker, and jail controls stay visible and easier to position near the screen edge.

See `docs/release-notes-v2.0.5.md` for the full release notes.

## Perfect Comms v2.0.4

This Perfect Comms release focuses on BetterCrewLink audio stability and chat input safety.

### Fixed

- Fixed intermittent fuzzy/static audio in BetterCrewLink lobbies when multiple voices overlap.
- Fixed hot BetterCrewLink mic frames after RNNoise so clipped capture peaks are limited before Opus encoding.
- Fixed chat input handling so Perfect Comms no longer intercepts textbox typing, preventing crashes while typing in chat.

See `docs/release-notes-v2.0.4.md` for the full release notes.

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

See `docs/release-notes-v2.0.3.md` for the full release notes.

## Perfect Comms v2.0.2

### What's Changed

- Fixed voice keybind behavior while chat is open so toggle shortcuts stay blocked during typing, while push-to-talk and impostor radio can still activate after a short hold.
- Fixed printable push-to-talk and radio keys in chat so quick taps type normally, but held voice keys do not spam characters into the message field.
- Fixed push-to-talk chat handling so it only applies when Mic Mode is set to Push To Talk.
- Fixed impostor radio chat handling so it only applies when the local player can actually use the radio, including blackmailer and jailor voice-block rules.
- Changed Push To Talk mode so the mute toggle no longer creates a redundant manual mute state; released PTT remains muted and holding PTT still transmits.
- Fixed the `VC: mic unavailable` warning appearing during normal Push To Talk idle mute.

See `docs/release-notes-v2.0.2.md` for the full release notes.

## Perfect Comms v2.0.1

### What's Changed

- Changed the assets to a cleaner version by @AtonyGit in #1.
- Fixed the Voice Lobbies close X so it sits in a clearer top-right position and is easier to see.
- Fixed bottom-positioned speaking indicators so player names no longer clip off the bottom of the screen while staying close to the edge.

See `docs/release-notes-v2.0.1.md` for the full release notes.

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

See `docs/release-notes-v2.0.0.md` for the full technical changelog.

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

**PUBLIC BETA — expect bugs.**  
This is the first public Perfect Comms release. It is ready for real lobbies, but still needs wider testing.

### What makes this release special

- **Fully integrated proximity chat** — voice runs inside Among Us, no separate voice app needed.
- **Supported mod behaviours** — TOU-Mira blackmailed players stay muted, and Jailor can unmute jailed players.
- **No more hackers messing with the voice** — voice stays tied to compatible Perfect Comms clients.
- **Built-in Voice Lobbies** — find compatible voice lobbies from the main menu and join with one click.
- **In-game update prompt** — players can be sent straight to the newest release when an update drops.

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
