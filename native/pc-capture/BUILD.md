# pc-capture: build, sign, ship

Cubeb capture/playback, DSP (WebRTC-APM AEC3/high or very-high noise suppression/HPF), bundled libopus 1.6.1 codec with classic FEC plus DRED, and Pion WebRTC v4.2.17 peer transport with proximity mixing. The Rust media core loads Pion through a required companion C-shared library. Loopback 127.0.0.1 single client, token via stdin (native) or token-file (Wine), protocol version 14.

The DRED encoder duration is 100 ms, matching the five-frame concealment cap. Opus' packet-loss
CTL budgets the redundancy dynamically; the healthy 5% and 10% policies do not meet libopus'
minimum two-chunk threshold and therefore spend no DRED bits. Adding an authorized receiver
advances an encoder epoch only after resetting codec/DRED history, preventing pre-join speech
from being carried to that receiver in a later packet.

The mod (`PerfectComms.dll`) is platform-agnostic. It embeds one helper binary and its matching Pion transport per target, then extracts the correct pair at runtime through the native-library cache. Desktop capture and playback are sidecar-only: the in-proc BASS path was removed in 4.0. If either resource is absent, the Pion library cannot load, or the helper cannot start, the `CaptureSupervisor` exhausts its restart budget and enters an all-failed state (`_onAllFailed`, logged under `voice.*` diagnostics); there is **no** in-proc desktop fallback, so the user has no voice until the helper can start again. Android intentionally captures and plays through Unity while `pc-mobile` owns Opus and mixing and loads the Android Pion companion for peer transport. Desktop WebRTC APM DSP is disabled on Android by design.

## Targets

The five Rust targets, verbatim:

- x86_64-pc-windows-msvc
- i686-pc-windows-msvc
- x86_64-apple-darwin
- aarch64-apple-darwin
- x86_64-unknown-linux-gnu

The Pion build additionally names its C-shared targets `win-x64`, `win-x86`,
`linux-x64`, `mac-x64`, `mac-arm64`, `mac-universal`, and `android-arm64`.

## Local build

- All non-mac Rust helpers: `bash scripts/build-helpers.sh` (use `--dry-run` to preview the target -> output map; pass a single triple, e.g. `bash scripts/build-helpers.sh x86_64-unknown-linux-gnu`, to build just one). Cubeb requires CMake 3.19+ and a C/C++ compiler. Linux additionally needs `pkg-config`, `libpulse-dev`, and `libasound2-dev` so the shipped helper contains its PulseAudio primary backend and ALSA fallback.
- Pion companions: install Go 1.26.2 exactly, then run `bash scripts/build-pion.sh <win-x64|win-x86|linux-x64|android-arm64> --stage`. The script verifies the locked Pion v4.2.17 module before building. Windows targets need the matching MinGW C compiler, Linux needs GCC, and Android ARM64 needs the pinned NDK through `ANDROID_NDK_HOME` or `ANDROID_NDK_ROOT`. For a manual macOS build, build `mac-x64` and `mac-arm64` without `--stage`, then build `mac-universal --stage`.
- macOS universal + ad-hoc sign: `bash scripts/build-mac.sh` (use `--dry-run` to preview the lipo / codesign plan). Builds both Rust and Pion Apple slices, `lipo`s each into a universal binary, seals `libpc-pion.dylib` into a `PerfectCommsAudio.app` with an `Info.plist` carrying `NSMicrophoneUsageDescription` and a `PerfectCommsAudio.icns` icon (generated from `Resources/miclogo.png` via `iconutil`), ad-hoc-signs it (`codesign --sign -`), and zips the bundle with `ditto`. The bundle's inner executable is `Contents/MacOS/PerfectCommsAudio`; the embedded/zip artifact name stays `pc-capture-mac.zip`.
- Rust helper outputs land in `Libs/pc-capture/` under the frozen names: `pc-capture-win-x64.exe`, `pc-capture-win-x86.exe`, `pc-capture-linux-x64`, `pc-capture-mac.zip`. Staged standalone Pion outputs land in `Libs/pion/` as `pc-pion.x64.dll`, `pc-pion.x86.dll`, `libpc-pion.linux-x64.so`, and `libpc-pion.android-arm64.so`; the macOS dylib stays inside the signed app.

Cubeb is built from the pinned `cubeb-sys` vendored source as a static library
inside every desktop helper. The build scripts clear
`LIBCUBEB_SYS_USE_PKG_CONFIG`, preventing a developer or CI machine from
silently substituting an external `libcubeb` and creating an undeclared runtime
dependency. Windows uses Cubeb/WASAPI with WinMM fallback, macOS uses
Cubeb/AudioUnit, and Linux uses Cubeb/PulseAudio with ALSA fallback. PipeWire desktops are supported
through their standard PulseAudio compatibility service.

The scripts isolate pkg-config discovery so Cubeb's vendored Speex resampler is
part of the same static helper (the Windows CMake toolchain additionally forces
`BUNDLE_SPEEX=ON`). Release validation parses PE imports, ELF
`DT_NEEDED`, and Mach-O load commands and rejects external Cubeb or speexdsp
libraries. This output-level gate also catches a stale Cargo/CMake cache that
was previously built with system-library opt-ins.

Every desktop helper also implements `--build-info`. The response binds protocol
13 to Cubeb 0.36.0, an immutable audio-contract marker, and the exact compiled
backend set for that target. Release packaging executes this probe, and the
managed launcher repeats it before using a native helper (including the
host-native helper launched by Wine/CrossOver/Proton). A same-protocol helper
from the retired audio engine is therefore rejected instead of being silently
embedded or launched.

On Linux only, the target dependency also unifies cubeb-sys's upstream
`unittest-build` feature. That unfortunately named switch disables Cubeb's
nested Rust Pulse backend and retains the vendored C PulseAudio plus ALSA
backends with `LAZY_LOAD_LIBS=ON`. This is a deliberate runtime-coverage choice:
the C Pulse implementation is legacy, but the helper can launch and fall back
to ALSA on a host where `libpulse.so.0` is absent. The release ELF verifier
rejects `DT_NEEDED` entries for PulseAudio, ALSA, or an external Cubeb library.
Cubeb's ALSA backend enumerates only one `default` endpoint upstream. Perfect
Comms supplements that list with `snd_device_name_hint` while still opening the
selected PCM through Cubeb, so wired, USB, `plug`, and `plughw` endpoints remain
selectable on ALSA-only hosts. PulseAudio and PipeWire's Pulse service use
Cubeb's native enumeration. Do not enable this feature for macOS; its current
Rust AudioUnit backend remains part of both universal slices.

`PC_CUBEB_BACKEND=pulse|alsa` is an allowlisted Linux-only diagnostic/CI
override. Production launchers leave it unset and use Cubeb's standard backend
selection; unsupported values and non-Linux overrides are ignored.

Headless CI proves backend feature selection, compilation, static packaging,
and the absence of forbidden runtime links; it does not claim that a physical
headset, hot-plug transition, or OS route works. Release qualification still
needs real-device checks on Windows/WASAPI, macOS/AudioUnit, Linux
PulseAudio/PipeWire-Pulse, and Linux ALSA explicit/default routing.

These names are frozen: the managed build (`PerfectComms.csproj`), the packaging scripts, the CI workflows, and the mod's extractor all key off them. Do not rename them.

## macOS signing: ad-hoc by default (no paid Apple program)

The shipped macOS approach is **ad-hoc codesign only** and intentionally uses **no paid Apple Developer Program, no Developer ID, and no notarization**.

- `scripts/build-mac.sh` runs `codesign --force --deep --sign -`. Ad-hoc signing is free and is the minimum required for an arm64 (Apple Silicon) binary to run at all. There is no hardened runtime, no entitlements file, and no notarization in the default path.
- The mod strips the quarantine xattr at runtime on a best-effort basis (`xattr -dr com.apple.quarantine`, in `SidecarLauncher.StripQuarantine`). In practice the helper is extracted from an embedded resource inside the mod (not downloaded), so the quarantine flag is generally not present to begin with.
- The helper is launched by the mod through the Wine host-exec path, not double-clicked in Finder, so Gatekeeper's "unidentified developer" prompt is not on the hot path. See "How it is launched" below.

This is the complete shipped flow. The release workflow does not contain a Developer ID or
notarization branch and does not consume Apple signing secrets.

## Embedding

The managed build embeds each helper as `Lib.pc-capture.<file>` and each standalone Pion companion as `Lib.pc-pion.<file>`, with one `<EmbeddedResource>` per frozen output name. A normal developer build may omit native resources, but release validation requires the complete platform set. At runtime the mod extracts the content-matched helper and Pion library into the per-target cache. On macOS the embedded resource is the zipped `.app`; its Pion dylib is already inside the signed bundle, so the mod preserves it while unzipping the app, setting executable permissions, and stripping quarantine. Android extracts `libpc-pion.android-arm64.so` to a content-addressed path and configures that exact path before `pc-mobile` creates its engine.

`scripts/package-release.sh` also stages the same four helper files as **side-files** in the BepInEx plugin folder (`BepInEx/plugins/pc-capture/`) so they ship alongside the DLL in the release zip. The Pion libraries are embedded in `PerfectComms.dll` (or sealed inside the macOS app) and extracted next to the selected helper at runtime. The embedded, content-matched copies are the runtime source of truth.

## How it is launched

`SidecarLauncher` generates a random token and a host-visible handshake-file path, then:

- **Native Windows:** `Process.Start(helper.exe, --handshake <path>)`, token written to the helper's stdin.
- **Wine (CrossOver / Proton):** host-exec via `start.exe /unix "<hostpath>" --handshake <path> --token-file <path>`. The Windows-side paths are translated to host paths with `winepath -u` (`WineEnvironment.ResolveHostPath`). Each launch first creates a private mode-`0700` host-visible directory, then create-news and verifies a mode-`0600` token file inside it. Permission setup fails closed; the token is deleted after launch and the directory is removed when the helper is released. The token is never placed in the process command line or diagnostics. This is what runs the native mac/linux helper outside the Wine boundary.
- **Host-exec blocked:** the handshake times out; the `CaptureSupervisor` retries within its restart budget and, if the helper never starts, enters the all-failed "voice unavailable" state (logged). There is no in-proc desktop fallback since BASS was removed in 4.0.

The helper binds `127.0.0.1:0`, writes `{port, pid}` to the handshake file, and the mod connects over loopback (Wine Winsock bridges to host loopback).

Because Wine, Proton, and CrossOver launch the host-native helper, Cubeb talks
to the host audio stack directly: PulseAudio/ALSA on Linux and AudioUnit on
macOS. It does not route the helper's audio through Wine's emulated WASAPI
layer. Native Windows launches use Cubeb's WASAPI backend.

Android remains a separate media surface. Unity owns microphone capture and
`AudioSource` playback while `pc-mobile` owns codec/mixing/transport; Cubeb is
desktop-only and is not present in the Android library or APK payload.

## Mic permission (TCC) and fallback

On macOS, mic permission (TCC) attributes to the **CrossOver / host process that launches the helper**, not to a separate signed app identity. If capture permission is absent or no input device exists, the helper reports a recoverable `mic-error` and retries the input device with capped backoff. Signaling, peer connections, mixing, and speaker playback remain active, so receive-only use is supported. A capture stream that had previously worked and later wedges is still supervised and may trigger a bounded helper/media recovery. Mic permission is an OS-level grant and is not something the mod can grant in code.

## CI

- `.github/workflows/native-helpers.yml`: builds the five desktop Rust targets and their Pion companions on GitHub-hosted runners (`windows-latest` x64/x86, glibc-2.31 Linux x64, `macos-latest` universal x64+arm64), plus Android ARM64 with the pinned NDK. It uploads the native artifacts and runs `scripts/ci-smoke-helper.sh`. The smoke verifies the Cubeb build contract/backend inventory, managed/native protocol version, Pion startup, control-only ready handshake, synthetic level cadence, reusable `stop`, prompt exit on control EOF, and final macOS DSP/Pion loading.
- `.github/workflows/release.yml`: on `v*` tags, waits for the managed, helper, DSP, RTC/TURN, and packaging gates, then publishes `PerfectComms+dependencies-win-x86-steam-itch.zip`, `PerfectComms+dependencies-win-x64-epic-msstore.zip`, `PerfectComms.dll`, and `PerfectCommsAndroid.dll`. Each dependency ZIP is built from the matching SHA-pinned BepInEx build and its native PE machine types are verified before upload. The final macOS app embedded in the desktop DLL is ad-hoc signed after both DSP dylibs are staged.

## Compatibility

The helper announces `proto` in its `ready` payload. Before launch, the mod also requires the helper's `--build-info` response to prove protocol 14, Cubeb 0.36.0, and the platform's exact backend inventory. Protocol 14 generation-scopes every native peer mutation, coalesces obsolete RTC control work, acknowledges applied peer and SDP operations, and reports bounded scheduler health. Protocol 13 adds local microphone monitoring with optional delayed playback; protocol 12 adds coordinated automatic mixed-ICE restart after network changes; protocol 11 adds selectable high/very-high WebRTC noise suppression. Protocol 10 sends stable audio device IDs separately from presentation names. Protocol 9 added the speech-safe noise-gate threshold, diagnostics sampling control, encoded-RTP receive metrics, selected ICE/RTCP path metrics, and encoder-policy telemetry. Protocol 8 added managed `AUDIO_OUT` injection and playback lifecycle acknowledgements, protocol 7 added native microphone capture, and protocol 6 added the control-only `listen_only` handshake used by receive-only sessions. Managed/native protocol bumps are deliberate compatibility boundaries; rebuild and ship the helper with the managed DLL whenever this value changes.

## Media diagnostics

Protocol 9 stats include encoded-RTP reorder/deadline/FEC/PLC/underrun metrics, adaptive target/depth/jitter gauges, selected ICE candidate types and real RTT/RTCP loss, and the active shared encoder policy. They may also include the additive `diagnostics` object with `schema: 1`, which reports capture/playback lifecycle generations and open attempts, resolved formats, callback cadence, ring age/high-water/drop state, timestamp validity, and raw/pre-DSP/post-DSP/post-gain signal windows. Sparse `media-state` messages carry the same schema for command, open, first-callback, retry, stop, and playback lifecycle transitions. Signal-window scans are disabled when managed diagnostics are off; structural counters and any queue or realtime-window loss remain explicit.

AEC delay is derived from the processed frame's process-local monotonic first-sample time, not wall clock or the newest capture callback: output hardware latency + render queue + ADC-to-DSP capture path + a bounded acoustic allowance. Non-48 kHz resampling re-anchors against each valid hardware callback so device-clock drift cannot accumulate over a game. Raw endpoint names exist only in the authenticated loopback control payload; managed diagnostic files log bounded fingerprints instead.
