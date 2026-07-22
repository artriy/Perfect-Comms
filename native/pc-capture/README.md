# pc-capture

PerfectComms native host-side microphone capture helper.

Captures and plays audio through Mozilla Cubeb, using WASAPI (with Cubeb's
WinMM fallback) on Windows,
AudioUnit on macOS, and PulseAudio with ALSA fallback on Linux. Capture is
resampled to 48000 Hz mono f32 in 20ms (960-sample) frames and streamed with a
JSON control channel over a loopback TCP connection (127.0.0.1, ephemeral
port, stdin token auth) to the PerfectComms BepInEx mod.

Also runs DSP (WebRTC-APM AEC3/high or very-high noise suppression/HPF), bundled
libopus 1.6.1 encode/decode with classic FEC plus Deep Redundancy (DRED), and the
Pion WebRTC v4.2.17 peer transport with proximity mixing, so mic, peer audio,
and playback all live in this helper. The Rust media core loads Pion through
the companion C-shared library built from `native/pc-pion`; transport startup
fails closed if the matching library is missing. Android uses the same
DRED-capable media core while intentionally leaving the desktop WebRTC-APM DSP
path disabled. Protocol version 15.

DRED history is bounded to the receiver's 100 ms concealment window. The packet-loss
expectation controls whether libopus can afford to emit DRED (healthy-route settings naturally
emit none), and encoder history is reset before the authorized receiver set expands so a newly
added peer cannot recover speech from before it joined.

## Run

```
pc-capture --handshake <path> [--synthetic-tone]
```

- Native launch writes the session auth token as a single line to the helper's
  STDIN. Wine/CrossOver launch uses a create-new token file inside a per-launch
  mode-`0700` directory and verifies mode `0600` before starting the helper.
- The helper binds 127.0.0.1:0, writes `{"port":<int>,"pid":<int>}` atomically
  to `<path>`, then accepts a single client (a second connection is rejected).
- `--synthetic-tone` replaces real capture with a 220 Hz, 0.012-peak sine (CI / field
  diagnostic; no microphone required).

## Wire protocol

Frame: `[u8 type][u32 len little-endian][payload]`.
- `0x01` CONTROL: payload = UTF-8 JSON.
- `0x02` AUDIO: payload = `[u64 LE captureTsNs][f32 LE PCM * 960]` (helper->mod mic capture).
- `0x03` AUDIO_OUT: selected-output test block (`f32 LE interleaved-stereo * 960`), injected into
  the same bounded playback path used by the live peer mix.

Control ops (mod->helper): `hello`, `select-device`, `select-output-device`, `start`, `stop`,
`ping`, `set-dsp`, `set-diagnostics`, `set-input`, `set-synthetic`, `set-monitor`, `set-ice-servers`, `peer-add`, `peer-remove`, `set-remote-sdp`,
`add-ice-candidate`, `game-state`.
Control ops (helper->mod): `ready`, `devices`, `outputDevices`, `level`, `error`, `pong`,
`local-sdp`, `local-candidate`, `peer-state`, `peer-levels`, `media-state`, `stats`.

`set-input` carries finite/clamped `gain`, `vad_threshold`, and `noise_gate_threshold`. Gain is
soft-limited before Opus; VAD only classifies the local speaking meter. The frame-aware gate has
hysteresis, a 200 ms hangover, and preserves the full opening frame; it keeps encoding continuous
RTP (DTX remains off). `set-diagnostics` controls expensive signal-window sampling without turning
off structural health counters. `level` and batched
decoded pre-route `peer-levels` telemetry are emitted every 100 ms through bounded latest-wins
mailboxes, so a slow UI/IPC consumer cannot stall capture or playout. `set-synthetic` switches
between the selected live microphone and the diagnostic tone without restarting the helper.
`set-monitor` mixes the processed local microphone into the selected output with a bounded gain
and optional delayed playout. Monitor timing is clock-corrected independently from remote voice,
and capture-generation changes discard and re-prime its local-only timeline.

## Test

```
cargo test                         # unit + e2e (synthetic, no mic)
cargo test --test synthetic_e2e    # end-to-end only
cargo fmt -- --check
cargo clippy --all-targets -- -D warnings
```

The transport loopback is required in CI. Set `PC_PION_LIB` to a built Pion
library and `PC_REQUIRE_PION=1` to require it in a local test run instead of
skipping when the companion library is absent.

## Build

```
cargo build --release              # host target
cargo check --all-targets          # fast gate the CI matrix reuses per target
```

`cargo build` produces the Rust helper only. From the repository root, use
`scripts/build-pion.sh` with Go 1.26.2 to produce its required Pion companion,
for example `bash scripts/build-pion.sh linux-x64 --stage`. Windows x64/x86,
Linux x64, macOS x64/arm64/universal, and Android ARM64 targets are supported.

## CI build targets

- x86_64-pc-windows-msvc
- i686-pc-windows-msvc
- x86_64-apple-darwin
- aarch64-apple-darwin
- x86_64-unknown-linux-gnu

The crate is target-agnostic Rust with no host-only constructs, so each target
above is built by its own CI runner (native compile per OS). Each target builds
where its native toolchain and system audio libraries live: Windows targets on
a Windows runner (Cubeb/WASAPI with WinMM fallback), the Linux target on a Linux runner
(Cubeb/PulseAudio with ALSA fallback), and the macOS targets on a macOS runner
(Cubeb/AudioUnit + Apple toolchain). Cubeb is C/C++ and Rust internally, so
CMake 3.19 or newer and a C/C++ compiler are required. Linux builds also need
`pkg-config`, `libpulse-dev`, and `libasound2-dev`; those headers compile both
host backends while Cubeb loads the available system audio service at runtime.
The release verifier rejects hard `DT_NEEDED` links to PulseAudio, ALSA, or an
external Cubeb library, so the helper can still start on Pulse/PipeWire and
ALSA-only hosts even when the other audio library is not installed.

Cross-compiling from a single host is not assumed. `cargo build --release`,
`cargo check --all-targets`, `cargo clippy --all-targets -- -D warnings`, and
`cargo test` are run on native hosts. Building a non-host target needs that
target's C/C++ toolchain, system audio headers/frameworks, linker, and a
correctly configured Cargo/CMake cross environment, so non-host targets are
produced on their own CI runners. Cross-target builds, `lipo`, codesign, and
notarization are owned by the distribution plan, not this crate.

macOS ships universal (`lipo x86_64 + aarch64`) inside a minimal `.app` bundle
with `Info.plist` `NSMicrophoneUsageDescription`, ad-hoc codesigned (`codesign
--sign -`, free, no Apple Developer account or notarization). Its universal
`libpc-pion.dylib` is sealed into the app before signing. The mod strips the
Gatekeeper quarantine attribute and sets the exec bit on the extracted bundle,
so the ad-hoc-signed helper launches and prompts for microphone access via TCC.

Per-target helper binaries ship as side-files in the BepInEx plugin folder and
as embedded resources. On Windows and Linux, the mod extracts the embedded,
content-matched Pion library beside the helper; macOS uses the copy already
inside the signed app. Android extracts its Pion resource and passes the exact
absolute path to `pc-mobile` before creating the transport.

Cubeb itself is compiled from the version pinned by `Cargo.lock` and statically
linked into each desktop helper. There is no `cubeb.dll`, `libcubeb.so`, or
`libcubeb.dylib` to install or ship. `LIBCUBEB_SYS_USE_PKG_CONFIG` is
intentionally cleared by the release build scripts so an ambient system Cubeb
cannot change that packaging contract. The scripts isolate pkg-config discovery
so Cubeb deterministically compiles its vendored Speex resampler. Output-level
PE, ELF, and Mach-O checks reject external Cubeb
or speexdsp libraries, covering stale local Cargo/CMake caches as well as clean
CI builds.

`pc-capture --build-info` reports protocol 15, Cubeb 0.36.0, the immutable
Perfect Comms audio-contract marker, and the exact backends compiled into that
binary. Packaging executes this probe, and the managed launcher requires the
same contract before every uncached native launch, including host-native
Wine/CrossOver/Proton launches. A retired same-protocol helper cannot pass this
check.

Linux deliberately enables cubeb-sys's upstream `unittest-build` feature for
production. Despite that feature's name, it is the upstream switch that omits
the nested Rust backends; this keeps Cubeb's C PulseAudio and ALSA backends in
lazy-load mode. The tradeoff is intentional: the C Pulse path is legacy and
ALSA has a lower upstream support tier, but a host missing `libpulse.so.0` can
still start and use ALSA. Cubeb's ALSA enumerator exposes only its single
`default` endpoint upstream, so Perfect Comms supplements it with ALSA PCM
hints and passes an explicitly selected raw PCM name back through Cubeb. This
preserves wired, USB, `plug`, and `plughw` choices on ALSA-only machines.
PulseAudio and PipeWire's Pulse service retain Cubeb's own per-device list.
Windows keeps WASAPI, and macOS keeps Cubeb's current Rust AudioUnit backend.

For deterministic Linux diagnostics and CI, `PC_CUBEB_BACKEND=pulse` or
`PC_CUBEB_BACKEND=alsa` forces one of those two compiled backends. Normal
launches leave it unset so Cubeb performs its standard ordered selection;
unrecognized values and all non-Linux overrides are ignored.

The headless build gates validate backend selection policy and binary linkage,
not physical endpoint routing. Headsets, wired-jack route changes, hot-plugging,
and OS permissions still require real-device validation on each host audio
stack.

Android audio is deliberately outside this dependency: `pc-mobile` continues
to receive Unity `Microphone` PCM and returns mixed PCM to a Unity
`AudioSource`. The Android build does not compile or link Cubeb.
