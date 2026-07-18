# pc-capture: build, sign, ship

Capture, playback, DSP (WebRTC-APM AEC3/high or very-high noise suppression/HPF), bundled libopus 1.6.1 codec with classic FEC plus DRED, and WebRTC (webrtc-rs) peer transport with proximity mixing. Loopback 127.0.0.1 single client, token via stdin (native) or token-file (Wine), protocol version 12.

The DRED encoder duration is 100 ms, matching the five-frame concealment cap. Opus' packet-loss
CTL budgets the redundancy dynamically; the healthy 5% and 10% policies do not meet libopus'
minimum two-chunk threshold and therefore spend no DRED bits. Adding an authorized receiver
advances an encoder epoch only after resetting codec/DRED history, preventing pre-join speech
from being carried to that receiver in a later packet.

The mod (`PerfectComms.dll`) is platform-agnostic. It embeds one helper binary per target as an embedded resource and extracts the correct one at runtime through `NativeLibraryCache.Extract`. Desktop capture and playback are sidecar-only: the in-proc BASS path was removed in 4.0. If no helper resource is present for the running target, or the helper cannot start, the `CaptureSupervisor` exhausts its restart budget and enters an all-failed state (`_onAllFailed`, logged under `voice.*` diagnostics); there is **no** in-proc desktop fallback, so the user has no voice until the helper can start again. Android intentionally captures and plays through Unity while `pc-mobile` owns Opus, WebRTC, and mixing. Desktop WebRTC APM DSP is disabled on Android by design.

## Targets

The five Rust targets, verbatim:

- x86_64-pc-windows-msvc
- i686-pc-windows-msvc
- x86_64-apple-darwin
- aarch64-apple-darwin
- x86_64-unknown-linux-gnu

## Local build

- All non-mac targets: `bash scripts/build-helpers.sh` (use `--dry-run` to preview the target -> output map; pass a single triple, e.g. `bash scripts/build-helpers.sh x86_64-unknown-linux-gnu`, to build just one). Linux needs ALSA dev headers (`libasound2-dev pkg-config`).
- macOS universal + ad-hoc sign: `bash scripts/build-mac.sh` (use `--dry-run` to preview the lipo / codesign plan). Builds both Apple arches, `lipo`s them into a universal binary, wraps it in a `PerfectCommsAudio.app` with an `Info.plist` carrying `NSMicrophoneUsageDescription` and a `PerfectCommsAudio.icns` icon (generated from `Resources/miclogo.png` via `iconutil`), ad-hoc-signs it (`codesign --sign -`), and zips the bundle with `ditto`. The bundle's inner executable is `Contents/MacOS/PerfectCommsAudio`; the embedded/zip artifact name stays `pc-capture-mac.zip`.
- Outputs land in `Libs/pc-capture/` under the frozen names: `pc-capture-win-x64.exe`, `pc-capture-win-x86.exe`, `pc-capture-linux-x64`, `pc-capture-mac.zip`.

These names are frozen: the managed build (`PerfectComms.csproj`), the packaging scripts, the CI workflows, and the mod's extractor all key off them. Do not rename.

## macOS signing: ad-hoc by default (no paid Apple program)

The shipped macOS approach is **ad-hoc codesign only** and intentionally uses **no paid Apple Developer Program, no Developer ID, and no notarization**.

- `scripts/build-mac.sh` runs `codesign --force --deep --sign -`. Ad-hoc signing is free and is the minimum required for an arm64 (Apple Silicon) binary to run at all. There is no hardened runtime, no entitlements file, and no notarization in the default path.
- The mod strips the quarantine xattr at runtime on a best-effort basis (`xattr -dr com.apple.quarantine`, in `SidecarLauncher.StripQuarantine`). In practice the helper is extracted from an embedded resource inside the mod (not downloaded), so the quarantine flag is generally not present to begin with.
- The helper is launched by the mod through the Wine host-exec path, not double-clicked in Finder, so Gatekeeper's "unidentified developer" prompt is not on the hot path. See "How it is launched" below.

This is the complete shipped flow. The release workflow does not contain a Developer ID or
notarization branch and does not consume Apple signing secrets.

## Embedding

The managed build embeds each helper as `Lib.pc-capture.<file>` (mirrors `Lib.bass.x64.dll`), one `<EmbeddedResource>` per frozen output name, each guarded by `Condition="Exists(...)"` so a missing target does not break the build. At runtime the mod extracts the right one via `NativeLibraryCache.Extract` into a per-target cache dir, exactly like `bass.dll`. On the mac the embedded resource is the zipped `.app`; the mod unzips it, `chmod +x`es the inner binary, and strips quarantine.

`scripts/package-release.sh` also stages the same four files as **side-files** in the BepInEx plugin folder (`BepInEx/plugins/pc-capture/`) so they ship alongside the DLL in the release zip. Embedding and side-files are not mutually exclusive: the embedded copy is the runtime source of truth; the side-files make the helpers visible in the distributed package.

## How it is launched

`SidecarLauncher` generates a random token and a host-visible handshake-file path, then:

- **Native Windows:** `Process.Start(helper.exe, --handshake <path>)`, token written to the helper's stdin.
- **Wine (CrossOver / Proton):** host-exec via `start.exe /unix "<hostpath>" --handshake <path> --token-file <path>`. The Windows-side paths are translated to host paths with `winepath -u` (`WineEnvironment.ResolveHostPath`). Each launch first creates a private mode-`0700` host-visible directory, then create-news and verifies a mode-`0600` token file inside it. Permission setup fails closed; the token is deleted after launch and the directory is removed when the helper is released. The token is never placed in the process command line or diagnostics. This is what runs the native mac/linux helper outside the Wine boundary.
- **Host-exec blocked:** the handshake times out; the `CaptureSupervisor` retries within its restart budget and, if the helper never starts, enters the all-failed "voice unavailable" state (logged). There is no in-proc desktop fallback since BASS was removed in 4.0.

The helper binds `127.0.0.1:0`, writes `{port, pid}` to the handshake file, and the mod connects over loopback (Wine Winsock bridges to host loopback).

## Mic permission (TCC) and fallback

On macOS, mic permission (TCC) attributes to the **CrossOver / host process that launches the helper**, not to a separate signed app identity. If capture permission is absent or no input device exists, the helper reports a recoverable `mic-error` and retries the input device with capped backoff. Signaling, peer connections, mixing, and speaker playback remain active, so receive-only use is supported. A capture stream that had previously worked and later wedges is still supervised and may trigger a bounded helper/media recovery. Mic permission is an OS-level grant and is not something the mod can grant in code.

## CI

- `.github/workflows/native-helpers.yml`: builds the five desktop targets on GitHub-hosted runners (`windows-latest` x64/x86, `ubuntu-latest` x64, `macos-latest` universal x64+arm64), uploads `helper-*` artifacts, and runs `scripts/ci-smoke-helper.sh`. The smoke verifies the managed/native protocol version, control-only ready handshake, synthetic level cadence, reusable `stop`, prompt exit on control EOF, and final macOS DSP loading.
- `.github/workflows/release.yml`: on `v*` tags, waits for the managed, helper, DSP, RTC/TURN, and packaging gates, then publishes `PerfectComms+dependencies-win-x86-steam-itch.zip`, `PerfectComms+dependencies-win-x64-epic-msstore.zip`, `PerfectComms.dll`, and `PerfectCommsAndroid.dll`. Each dependency ZIP is built from the matching SHA-pinned BepInEx build and its native PE machine types are verified before upload. The final macOS app embedded in the desktop DLL is ad-hoc signed after both DSP dylibs are staged.

## Compatibility

The helper announces `proto` in its `ready` payload. The mod rejects any helper whose `proto != 12` (the `Proto` constant in `SidecarVoiceClient`). Protocol 12 adds coordinated automatic mixed-ICE restart after network changes; protocol 11 adds selectable high/very-high WebRTC noise suppression. Protocol 10 sends stable audio device IDs separately from presentation names. Protocol 9 added the speech-safe noise-gate threshold, diagnostics sampling control, encoded-RTP receive metrics, selected ICE/RTCP path metrics, and encoder-policy telemetry. Protocol 8 added managed `AUDIO_OUT` injection and playback lifecycle acknowledgement for the selected-output setup test; protocol 7 added restartable live device/synthetic switching, native input gain/VAD controls, and bounded local/peer level telemetry on top of the older protocol 6 ICE policy. The bundled binary is content-hashed (`NativeLibraryCache`) and re-extracted automatically whenever it changes, so a stale or mismatched side-file cannot be used; the embedded, version-matched helper wins.

## Media diagnostics

Protocol 9 stats include encoded-RTP reorder/deadline/FEC/PLC/underrun metrics, adaptive target/depth/jitter gauges, selected ICE candidate types and real RTT/RTCP loss, and the active shared encoder policy. They may also include the additive `diagnostics` object with `schema: 1`, which reports capture/playback lifecycle generations and open attempts, resolved formats, callback cadence, ring age/high-water/drop state, timestamp validity, and raw/pre-DSP/post-DSP/post-gain signal windows. Sparse `media-state` messages carry the same schema for command, open, first-callback, retry, stop, and playback lifecycle transitions. Signal-window scans are disabled when managed diagnostics are off; structural counters and any queue or realtime-window loss remain explicit.

AEC delay is derived from the processed frame's process-local monotonic first-sample time, not wall clock or the newest capture callback: output hardware latency + render queue + ADC-to-DSP capture path + a bounded acoustic allowance. Non-48 kHz resampling re-anchors against each valid hardware callback so device-clock drift cannot accumulate over a game. Raw endpoint names exist only in the authenticated loopback control payload; managed diagnostic files log bounded fingerprints instead.
