# pc-capture

PerfectComms native host-side microphone capture helper.

Captures audio via cpal (CoreAudio / WASAPI / ALSA-PipeWire), resamples to
48000 Hz mono f32 in 20ms (960-sample) frames, and streams them plus a JSON
control channel over a loopback TCP connection (127.0.0.1, ephemeral port,
stdin token auth) to the PerfectComms BepInEx mod.

Also runs DSP (WebRTC-APM AEC/AGC/HPF + DeepFilterNet noise suppression), Opus
encode/decode, and the WebRTC (webrtc-rs) peer transport with proximity mixing,
so mic, peer audio, and playback all live in this helper. Protocol version 7.

## Run

```
pc-capture --handshake <path> [--synthetic-tone]
```

- The launcher writes the session auth token as a single line to the helper's
  STDIN.
- The helper binds 127.0.0.1:0, writes `{"port":<int>,"pid":<int>}` atomically
  to `<path>`, then accepts a single client (a second connection is rejected).
- `--synthetic-tone` replaces real capture with a 220 Hz, 0.012-peak sine (CI / field
  diagnostic; no microphone required).

## Wire protocol

Frame: `[u8 type][u32 len little-endian][payload]`.
- `0x01` CONTROL: payload = UTF-8 JSON.
- `0x02` AUDIO: payload = `[u64 LE captureTsNs][f32 LE PCM * 960]` (helper->mod mic capture).
- `0x03` AUDIO_OUT: legacy speaker block (`f32 LE interleaved-stereo * 960`). The sidecar now
  owns the speaker mix end to end and parses-but-discards this; kept only for the wire contract.

Control ops (mod->helper): `hello`, `select-device`, `select-output-device`, `start`, `stop`,
`ping`, `set-dsp`, `set-input`, `set-synthetic`, `set-ice-servers`, `peer-add`, `peer-remove`, `set-remote-sdp`,
`add-ice-candidate`, `game-state`.
Control ops (helper->mod): `ready`, `devices`, `outputDevices`, `level`, `error`, `pong`,
`local-sdp`, `local-candidate`, `peer-state`, `peer-levels`.

`set-input` carries finite/clamped `gain` and `vad_threshold`. Gain is applied before Opus;
VAD only classifies the local speaking meter and never gates encoded audio. `level` and batched
decoded pre-route `peer-levels` telemetry are emitted every 100 ms through bounded latest-wins
mailboxes, so a slow UI/IPC consumer cannot stall capture or playout. `set-synthetic` switches
between the selected live microphone and the diagnostic tone without restarting the helper.

## Test

```
cargo test                         # unit + e2e (synthetic, no mic)
cargo test --test synthetic_e2e    # end-to-end only
cargo fmt -- --check
cargo clippy --all-targets -- -D warnings
```

## Build

```
cargo build --release              # host target
cargo check --all-targets          # fast gate the CI matrix reuses per target
```

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
--sign -`, free, no Apple Developer account or notarization). The mod strips the
Gatekeeper quarantine attribute and sets the exec bit on the extracted bundle,
so the ad-hoc-signed helper launches and prompts for microphone access via TCC.

Per-target binaries ship as side-files in the BepInEx plugin folder; the mod
extracts them via the `NativeLibraryCache` pattern.
