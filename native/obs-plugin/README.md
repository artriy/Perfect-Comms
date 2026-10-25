# Perfect Comms OBS recording

Optional Windows x64 plugin for native Windows Perfect Comms and official 64-bit OBS Studio 32.2.2. The integration is off by default. One combined OBS audio source accompanies separate synchronized incoming-voice files; it does not create an OBS track per player or record your own microphone. Other OBS versions are not covered by this compatibility target. Linux/Proton, macOS/Wine/CrossOver, and Android are not supported by this integration.

## Install and record

1. Install the matching desktop `PerfectComms.dll` in Among Us as usual. Close OBS, then extract `PerfectComms-OBS-win-x64.zip` into your OBS installation folder, alongside its existing `bin`, `data`, and `obs-plugins` folders. Merge the folders and restart OBS. Do not install this ZIP into BepInEx.
2. Check the layout: `obs-plugins/64bit/perfect-comms-obs.dll`. The ZIP also includes this README, `GPL-2.0.txt`, `LGPL-2.1.txt`, and `LICENSE-NOTICE.txt`. It contains no OBS runtime or automatic installer.
3. In Perfect Comms **Voice Settings > Audio > Streaming**, enable **OBS Recording Integration**, then join voice. Set **Separate Voice Files** to **Per player** (default) or **Alive / dead**. Mode changes apply to the next recording.
4. In OBS Sources, add **Perfect Comms** once and reuse it through **Add Existing** across scenes. Duplicate new instances stay silent. Keep **Advanced Audio Properties > Sync Offset** at **0 ms**; offset changes are rejected.
5. Disable duplicate Application Audio Capture of **Perfect Comms Audio** and any desktop/game audio source that also captures Perfect Comms voices. Capture game audio and your own microphone separately. The existing **OBS Compatibility Mode** is an alternative for combined Application Audio Capture, not a requirement for this plugin.
6. Select Advanced output mode in OBS **Settings > Output**, enable the recording audio tracks you need, and assign sources in **Advanced Audio Properties**. For example: track 1 combined mix, track 2 game, track 3 your microphone, track 4 the combined Perfect Comms source. The separate WAVs do not consume OBS's six available tracks.
7. Choose a supported recording format below, disable automatic file splitting, and select a writable recording folder on a sparse-file-capable volume such as NTFS or ReFS. No component of the recording path may be a junction or symbolic link.
8. Use OBS Start/Stop/Pause/Resume to control both video and stems. After stopping, allow up to ten seconds for audio draining and finalization before closing OBS or leaving voice. Check status before relying on the files.

## Find and edit voice files

Recording `match.mkv` creates a sibling `match-voices` folder, with a numbered suffix on collision. Files are 48 kHz stereo 32-bit float WAV, converted to RF64 when needed for size. Use an editor supporting float WAV and RF64.

**Per player** creates files for permitted displayed identities that contribute audio. Safe aliases collapse with their displayed identity rather than revealing the underlying speaker. Hidden identities share `Private voices.wav`. **Alive / dead** creates `Alive voices.wav` and `Dead voices.wav` as needed, plus `Private voices.wav` when the group is protected. A silent or absent identity need not create a file.

Import all WAVs and the video at timeline **0**. Keep leading silence intact. Completed files match the accepted OBS video timeline, including quiet gaps, late joins, and removal of paused time. When using the individual files, mute/remove the combined Perfect Comms track from the video to avoid doubling voice; retain your separately recorded game and microphone tracks.

Stems follow permitted incoming voice, local deafen and Speaker Volume, player/group gains, spatial routing, and mod voice filters. This is logical audible routing, independent of the physical Windows output device. Your local mic is not included. Stems are taken before the combined limiter and before OBS source processing: the OBS fader and OBS filters change the combined source, not the separate files.

## Status and troubleshooting

Perfect Comms **Audio > Streaming** shows Off, Waiting for OBS, Connected, Recording, Paused, or Recording error. **Open Guide** opens the [player setup guide](https://github.com/artriy/Perfect-Comms/wiki/Players#obs-recording-integration). OBS source Properties shows status and output folder; click **Refresh connection and recording status** to update it.

- Missing source: check the installed DLL/data paths, restart 64-bit OBS 32.2.2, and inspect the OBS log for load errors.
- Waiting for OBS: join voice with the integration enabled, add the source, and run OBS and Among Us as the same Windows user.
- Echo/doubling: disable duplicate application/desktop voice capture and check monitoring. In your editor, do not mix the combined voice track with the same separate voices.
- Missing named files: check hearing rules, deafen, Speaker Volume, and player volumes. Hidden identities intentionally merge into Private voices.
- Recording error: read the displayed message. Check supported output, Sync Offset 0, disabled file splitting, disk space/permissions, sparse-file support, and junctions/symbolic links in the whole recording path. Invalid timestamps, output errors, exhausted bounded queues, and disconnects during finalization fail visibly. Voice chat continues; an errored folder may be incomplete. Correct the cause before starting a new recording.

## Output and synchronization contract

Supported outputs: official OBS 32.2.2 `ffmpeg_muxer` (standard MKV/MP4), `mp4_output` (hybrid MP4), and `mov_output` (hybrid MOV). Turn off automatic recording file splitting. Split output buffers defer multiple packet submissions, so their acceptance cannot be confirmed individually through the public API. Other output implementations are rejected visibly.

Synchronization uses OBS composition timestamps and PTS denominator ticks, including fractional FPS and encoder frame-rate divisors. Missing composition timestamps stop stems instead of guessing from DTS. The first `ffmpeg_muxer` callback accounts for exact encoder-header bytes; packet acceptance then requires its complete payload-byte increment. Hybrid MP4/MOV normalize relative PTS against the first video packet and delay clock commitment by one accepted video packet, matching the muxer's retained final sample. Audio packets never release that held video sample. Finalization on `deactivate` drops the sample the hybrid muxer does not write. Rejected stopping packets, counter mismatches, and output errors cannot silently extend stems.
Hybrid sample durations use adjacent accepted raw DTS differences, not nominal FPS, including stretched samples after dropped frames.

Confirmed neighboring video clocks also map continuous audio through dropped-video gaps when their deltas agree at 48 kHz sample precision. Explicit resume boundaries prohibit bridging paused time. Reordered B-frames can fill those gaps later without writing source samples twice.
Continuous intervals share one integer source-to-destination sample translation, retained with the clock history. Discontinuities establish a new translation; fractional anchor rounding cannot merge neighboring PCM samples.

Pause retention preserves unresolved pre-pause encoder audio across arbitrarily long pauses, while discarding later paused input. The conservative retention horizon is the pause notification plus two global video frame intervals, matching OBS's maximum future pause boundary. Resume retains that unresolved window for the existing ten-second encoder timing allowance. Queue exhaustion remains a visible error, never an unbounded allocation.

After the final accepted video interval, the writer waits up to ten seconds for a combined-audio timestamp reaching its absolute end. The helper sends combined first within each batch, after all prior-batch stems. That next-batch watermark therefore confirms receipt of earlier stems before finalization. A missing watermark or disconnect during draining is reported as incomplete recording.

WAV files use sparse silence and convert their reserved RIFF header to RF64 when needed. The recording volume must support sparse files (for example NTFS or ReFS); unsupported volumes fail visibly. The voices directory is created atomically with Windows SDK `NtCreateFile`, exclusive `FILE_CREATE`, and `OBJ_DONT_REPARSE`, rejecting reparse points throughout the path rather than checking only the final directory after creation. The directory stays open without write or delete sharing. Child files are created relative to that directory handle with `OBJ_DONT_REPARSE`, so ancestor path replacement cannot redirect them. Files use exclusive creation and sanitized names. `ntdll` is a Windows system import, not a shipped dependency.

File reads and writes use cancellation-aware overlapped I/O. Module shutdown cancels outstanding work and abandons further finalization rather than padding or flushing new data. Headers are updated after each mixed block, leaving the last committed length recoverable; shutdown is not reported as successful completion.

Regression tests in `tests/recording-tests.cpp` cover packet acceptance/rejection, fractional FPS, encoder divisors, absent composition timestamps, bounded drain failure, long-pause delayed frames, directory write exclusion, cancellation, actual sparse RF64 conversion and size fields, in addition to parsing, privacy buses, overlaps, and reconnect namespaces. Build and run these through the existing CMake/CTest target.

A real official OBS 32.2.2 Windows x64 frontend capture using x264 and standard MKV, with native Pion peers sending synthetic tones, produced 3,570 video frames at 30 fps (119.1 seconds). All four resulting Alice, Bob, Replacement, and Private voice WAVs measured 119.1 seconds; the 48-second OBS pause was excluded. This establishes that exercised output path, not every encoder, format, OBS version, or physical audio device.

## Build and licensing

From the repository root, with PowerShell, CMake 3.24+, Visual Studio x64 C++ tools, and a Windows SDK:

```powershell
pwsh -File scripts/build-obs-plugin.ps1 -Configuration Release
```

The script downloads SHA-256-pinned official OBS 32.2.2 source/runtime archives, generates import libraries from official DLL exports, builds the plugin, runs CTest unless `-SkipTests` is supplied, and packages `artifacts/PerfectComms-OBS-win-x64.zip`. It does not install into OBS. `-ObsRoot` and `-ObsSource` can select existing official runtime/source roots; those overrides are caller-supplied inputs rather than hash-verified downloads.

The OBS-linked plugin is GPL-2.0-or-later under the scoped LGPL-2.1 section 3 conversion in `LICENSE-NOTICE.txt`. The rest of the Perfect Comms repository keeps its existing license.
