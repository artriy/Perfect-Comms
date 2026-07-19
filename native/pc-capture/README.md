# pc-capture

PerfectComms native host-side microphone capture helper.

Captures audio via cpal (CoreAudio / WASAPI / ALSA-PipeWire), resamples to
48000 Hz mono f32 in 20ms (960-sample) frames, and streams them plus a JSON
control channel over a loopback TCP connection (127.0.0.1, ephemeral port,
stdin token auth) to the PerfectComms BepInEx mod.

Also runs DSP (WebRTC-APM AEC3/high or very-high noise suppression/HPF), bundled
libopus 1.6.1 encode/decode with classic FEC plus Deep Redundancy (DRED), and the
Pion WebRTC v4.2.17 peer transport with proximity mixing, so mic, peer audio,
and playback all live in this helper. The Rust media core loads Pion through
the companion C-shared library built from `native/pc-pion`; transport startup
fails closed if the matching library is missing. Android uses the same
DRED-capable media core while intentionally leaving the desktop WebRTC-APM DSP
path disabled. Protocol version 13.

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
where its native toolchain and system audio libraries live: Windows targets on a
Windows runner (WASAPI), the Linux target on a Linux runner (ALSA/PipeWire via
`alsa-sys`, which needs ALSA dev headers), and the macOS targets on a macOS
runner (CoreAudio + Apple toolchain).

Cross-compiling from a single host is not assumed. `cargo build --release`,
`cargo check --all-targets`, `cargo clippy --all-targets -- -D warnings`, and
`cargo test` were all verified on the native host. Building a non-host target
from this machine needs that target's system audio libs and linker (for example
the Linux build needs ALSA dev headers and a cross sysroot for pkg-config), so
non-host targets are produced on their own CI runners. Cross-target builds,
`lipo`, codesign, and notarization are owned by the distribution plan, not this
crate.

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
