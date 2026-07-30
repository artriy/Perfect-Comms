# Third-Party Notices

Perfect Comms embeds native platform engines and DSP libraries as assembly resources and extracts them at
runtime for the applicable platform. Their licenses are reproduced or referenced below.

## Mozilla Cubeb (desktop audio input/output)

- Files: compiled from the pinned `cubeb` / `cubeb-sys` Rust crates and their vendored Cubeb source,
  then statically linked into each Windows, Linux, and macOS `pc-capture` helper. No standalone
  Cubeb shared library is distributed. Android uses Unity audio and does not link Cubeb.
- Upstream: https://github.com/mozilla/cubeb and https://github.com/mozilla/cubeb-rs.
- License: ISC. The exact C library and Rust binding texts are embedded in `PerfectComms.dll` as
  `Licenses.libcubeb-ISC.txt` and `Licenses.cubeb-rs-ISC.txt`. Cubeb's macOS build invokes a
  separately locked Rust AudioUnit dependency graph, embedded as
  `Licenses.cubeb-coreaudio-rust-dependencies.html`. Linux intentionally uses Cubeb's vendored C
  PulseAudio/ALSA backends and therefore does not compile the nested Rust PulseAudio project.
- Cubeb also compiles its vendored Speex resampler because release builders do not supply a system
  `speexdsp`. Its BSD 3-Clause notice is embedded as
  `Licenses.cubeb-speex-resampler-BSD-3-Clause.txt`.

## libopus (voice codec inside native media engines)

- Files: statically linked inside the platform `pc-capture` and `pc-mobile` binaries; there is no
  separately loaded managed-code Opus DLL.
- Upstream: https://github.com/xiph/opus. Perfect Comms pins libopus 1.6.1 through
  `opusic-c` 1.6.1 / `opusic-sys` 0.7.3, builds it from the binding's bundled source, and enables
  the upstream DRED feature on desktop and Android.
- License: BSD 3-Clause (Xiph.org Foundation), embedded as
  `Licenses.libopus-BSD-3-Clause.txt`.
- Rust binding license: BSD 3-Clause (Douman), embedded as
  `Licenses.opusic-c-BSD-3-Clause.txt`.

## webrtc-audio-processing (AEC3 + noise suppression + high-pass filter)

- Release files: `Libs/dsp/webrtc-apm.x64.dll`, `Libs/dsp/webrtc-apm.x86.dll`,
  `Libs/dsp/libwebrtc-apm.so`, and the signed
  `PerfectCommsAudio.app/Contents/MacOS/libwebrtc-apm.dylib` inside `pc-capture-mac.zip`
- Upstream: WebRTC AudioProcessingModule (Google), via the PulseAudio standalone fork
  https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing (v2.1, WebRTC M131). Windows
  binaries are built by release CI from the vendored `LSXPrime/webrtc-audio-processing` source.
- License: BSD 3-Clause. Copyright The WebRTC project authors. The main text is embedded as
  `Licenses.webrtc-audio-processing-BSD-3-Clause.txt`.
- The APM build also compiles WebRTC's bundled FFT/DSP sources. Their exact upstream texts are
  embedded as `Licenses.WebRTC-BSD-3-Clause.txt`, `Licenses.WebRTC-ooura-BSD.txt`,
  `Licenses.WebRTC-spl-sqrt-floor-BSD-3-Clause.txt`, `Licenses.WebRTC-fft-BSD-3-Clause.txt`,
  `Licenses.WebRTC-pffft-BSD-3-Clause.txt`, and `Licenses.WebRTC-rnnoise-BSD-3-Clause.txt`.

## Pion WebRTC v4.2.17 (peer-to-peer transport)

- Release files: `Libs/pion/pc-pion.x64.dll`, `Libs/pion/pc-pion.x86.dll`,
  `Libs/pion/libpc-pion.linux-x64.so`, the signed
  `PerfectCommsAudio.app/Contents/MacOS/libpc-pion.dylib` inside `pc-capture-mac.zip`, and
  `Libs/pion/libpc-pion.android-arm64.so` in the Android build.
- Upstream: https://github.com/pion/webrtc, pinned to v4.2.17 together with the exact module graph
  in `native/pc-pion/go.mod` and `native/pc-pion/go.sum`.
- License: MIT for Pion; the c-shared binary also incorporates the BSD-licensed Go runtime and
  its locked Go module dependencies. Exact module versions and reproduced license texts are
  embedded as `Licenses.pion-go-dependencies.txt`.

## Bundled managed dependencies

The plugin embeds these managed assemblies as resources and resolves them at runtime.

| Assembly | Upstream and license text |
|----------|---------------------------|
| System.Text.Encodings.Web and System.Text.Json 10.0.10 | [.NET runtime](https://github.com/dotnet/runtime), MIT. The package license is embedded as `Licenses.dotnet-runtime-MIT.txt`; the exact 10.x package notices are embedded as `Licenses.System.Text.Encodings.Web-THIRD-PARTY-NOTICES.txt` and `Licenses.System.Text.Json-THIRD-PARTY-NOTICES.txt`. |
| Microsoft.Bcl.AsyncInterfaces and System.IO.Pipelines 10.0.10 | [.NET runtime](https://github.com/dotnet/runtime), MIT. These System.Text.Json runtime dependencies share the embedded .NET runtime license and 10.x NuGet third-party notices above. |

## Native Rust dependencies

The native desktop and Android media engines statically link locked Rust dependency graphs,
including the Pion dynamic-loading facade, serialization, audio I/O, codec, DSP integration, and
platform support crates. A deterministic cargo-about inventory covering every shipped desktop
target and Android ARM64 is generated from `native/pc-mobile/Cargo.lock` and embedded as
`Licenses.native-rust-dependencies.html`. Cubeb's separately locked macOS Rust AudioUnit graph is
embedded as `Licenses.cubeb-coreaudio-rust-dependencies.html`. CI regenerates both inventories
from their locks and rejects drift.
