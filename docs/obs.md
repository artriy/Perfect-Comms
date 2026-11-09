---
description: "Record Perfect Comms voice chat in OBS, with a separate WAV file for every player."
---

# OBS and streaming

Both features are off by default and work on native Windows only (not Linux/Proton, macOS/Wine/CrossOver, or Android). Use one or the other, not both.

- **[OBS Recording Integration](/obs#obs-recording-integration)**: an OBS plugin that adds a **Perfect Comms** source and saves a separate voice file per player beside every recording.
- **[OBS Compatibility Mode](/obs#obs-compatibility-mode)**: a voice-only window that OBS **Application Audio Capture** can record. No plugin needed.

## OBS Recording Integration

Adds one combined **Perfect Comms** audio source to OBS and saves a separate WAV file for every player beside your recording:

```text
match.mkv
match-voices/
├─ 1-1-Maya.wav
├─ 1-2-Diego.wav
├─ 1-3-Priya.wav
└─ Private voices.wav
```

Each file holds what you heard from that player, after distance, walls and your volume settings. Your own microphone is not in the voice files; record it as its own OBS source.

Requires native Windows x64 Perfect Comms and 64-bit OBS Studio. Tested with OBS Studio 32.2.2; other versions may work, but compatibility and recording sync are unverified.

### Install and set up

1. Install the matching desktop `PerfectComms.dll` normally, and download `PerfectComms-OBS-win-x64.zip` from the same release.
2. Close OBS. Extract the ZIP into your OBS installation folder (the one containing `bin`, `data`, and `obs-plugins`), merging its `obs-plugins` folder. The DLL must end up at `obs-plugins/64bit/perfect-comms-obs.dll`. Restart OBS. The ZIP is not an installer and does not include OBS.
3. In **Voice settings > Audio > Streaming**, enable **OBS Recording Integration** and join voice. Under **Separate Voice Files**, choose **Per player** (default) or **Alive / dead**. Mode changes apply to the next recording.
4. In OBS **Sources**, add **Perfect Comms** once. Use **Add Existing** for other scenes; extra new instances stay silent. Keep its **Sync Offset** at **0 ms** in **Advanced Audio Properties**.
5. Turn **OBS Compatibility Mode** off and remove any Application Audio Capture of **Perfect Comms Audio**. Capture game sound with an **Application Audio Capture** of **Among Us**, not **Desktop Audio**. Typical setup: **Perfect Comms** (voice chat), **Among Us** (game sound), and your microphone.
6. Optional: in **Settings > Output** (Advanced mode), give sources their own recording tracks, for example track 1 your combined mix, 2 game, 3 microphone, 4 Perfect Comms. Assign them in **Advanced Audio Properties** and enable those tracks in Recording. These are ordinary OBS tracks, not the voice files.
7. Record to standard **MKV** or **MP4**, or **Hybrid MP4/MOV**, with automatic file splitting disabled. Custom FFmpeg output is not supported. Use a writable NTFS or ReFS location with enough free space and no junctions or symbolic links anywhere in the folder path.

### Record and edit

Use OBS **Start Recording**, **Pause/Resume**, and **Stop Recording** normally. Paused time is removed and silence is added before late joins and after a player stops speaking. After stopping, wait up to ten seconds before closing OBS or disconnecting voice.

For `match.mkv`, voice files go in `match-voices` beside it (or a numbered variant if that folder exists). Existing files are never overwritten. Files are 48 kHz stereo, 32-bit float WAV, switching to RF64 when large; your editor must support both.

- **Per player:** one file per permitted displayed identity that contributes audio. Safe aliases share their displayed identity's file. Hidden identities share **Private voices.wav**.
- **Alive / dead:** **Alive voices.wav** and **Dead voices.wav**, plus **Private voices.wav** when privacy hides a speaker's group. Absent or silent voices get no file.

To edit: import the video and every WAV at **timeline 0** without trimming leading silence. Mute or remove the combined Perfect Comms track to avoid doubled voices; keep the game and microphone tracks. Separate files avoid OBS's six-track limit.

Voice files contain the incoming audio you are allowed to hear, after player/group volume, spatial audio, voice filters, deafen, and Speaker Volume. They follow the mod's voice routing, not whether your physical speakers work, and exclude your mic and unpermitted audio. OBS faders and filters affect only the combined source.

One recording can span several matches. A player who stays connected under the same name keeps one file; leaving and rejoining or changing name starts a new file. Joining a different lobby starts new files.

### Status and recording problems

**Audio > Streaming** shows **Off**, **Waiting for OBS**, **Connected**, **Recording**, **Paused**, or **Recording error**; **Open Guide** opens this section. A failed recording also shows the HUD warning **OBS recording failed, check Audio settings**. The OBS source's Properties show status and the recording folder, with **Refresh connection and recording status**.

- **Perfect Comms source missing:** check the ZIP folder layout, restart 64-bit OBS, and check the OBS log for a plugin load error. The mod DLL alone does not install the plugin.
- **Waiting for OBS:** enable the integration, join voice, and add the source. Run OBS and Among Us as the same Windows user.
- **Echo or doubled voices:** remove duplicate application/desktop capture, check source monitoring, and do not play the combined track with the voice files in your editor.
- **No named file or unexpected Private voices:** check deafen, Speaker Volume, player volumes, and the host's hearing rules. Privacy-hidden voices intentionally share one anonymous file; their identities cannot be recovered.
- **Recording error:** read the status message. Keep Sync Offset at 0, disable file splitting, use a supported output, and check free space, permissions, sparse-file support, and junctions/symbolic links. Invalid encoder timestamps, output errors, queue overflow, or a disconnect during finalization can leave incomplete files; do not treat an errored voice folder as complete. Fix the cause before recording again. Voice chat continues when recording fails.

## OBS Compatibility Mode

1. Open **Voice settings > Audio > Streaming**, enable **OBS Compatibility Mode**, and join a voice lobby. A minimized **Perfect Comms Audio** window appears. Closing it only minimizes it; turning the option off removes it.
2. In OBS **Sources**, add an **Application Audio Capture** of the **Perfect Comms Audio** window. It carries voice chat only.
3. Capture game sound with a second **Application Audio Capture** of **Among Us**, and keep your microphone as its own input. Do not use **Desktop Audio** for game sound: it also contains voice chat, so every voice plays twice.
