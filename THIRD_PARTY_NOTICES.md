# Third-Party Notices

Perfect Comms embeds native platform engines and DSP libraries as assembly resources and extracts them at
runtime for the applicable platform. Their licenses are reproduced or referenced below.

## libopus (voice codec inside native media engines)

- Files: statically linked inside the platform `pc-capture` and `pc-mobile` binaries; there is no
  separately loaded managed-code Opus DLL.
- Upstream: https://github.com/xiph/opus. Perfect Comms pins libopus 1.6.1 through
  `opusic-c` 1.6.1 / `opusic-sys` 0.7.3, builds it from the binding's bundled source, and enables
  the upstream DRED feature on desktop and Android.
- License: BSD 3-Clause (Xiph.org Foundation). Full text is in source at `Libs/opus.COPYING`
  and in release bundles at `licenses/libopus-BSD-3-Clause.txt`.
- Rust binding license: BSD 3-Clause (Douman). Full text is in source at `Libs/opusic-c.COPYING`
  and in release bundles at `licenses/opusic-c-BSD-3-Clause.txt`.

## webrtc-audio-processing (AEC3 + noise suppression + high-pass filter)

- Release files: `Libs/dsp/webrtc-apm.x64.dll`, `Libs/dsp/webrtc-apm.x86.dll`,
  `Libs/dsp/libwebrtc-apm.so`, `Libs/dsp/libwebrtc-apm.dylib`
- Upstream: WebRTC AudioProcessingModule (Google), via the PulseAudio standalone fork
  https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing (v2.1, WebRTC M131). Prebuilt Windows
  binaries from the `LSXPrime/webrtc-audio-processing` mirror.
- License: BSD 3-Clause. Copyright The WebRTC project authors. Full text is in source at
  `Libs/webrtc-apm.COPYING` and in release bundles at
  `licenses/webrtc-audio-processing-BSD-3-Clause.txt`.
- The APM build also compiles WebRTC's bundled FFT/DSP sources. Their exact upstream texts are
  preserved in source as `Libs/webrtc-upstream.LICENSE`, `Libs/webrtc-ooura.LICENSE`,
  `Libs/webrtc-spl-sqrt-floor.LICENSE`, `Libs/webrtc-fft.LICENSE`, `Libs/webrtc-pffft.LICENSE`,
  and `Libs/webrtc-rnnoise.COPYING`; release bundles copy each one under `licenses/`.

## Bundled managed dependencies

The plugin embeds these managed assemblies as resources and resolves them at runtime.

| Assembly | Upstream and license text |
|----------|---------------------------|
| SocketIOClient, SocketIO.Core, SocketIO.Serializer.* 3.1.2 | [doghappy/socket.io-client-csharp](https://github.com/doghappy/socket.io-client-csharp), MIT. The exact license from the NuGet package's pinned upstream commit is in source at `Libs/socketio-client-csharp.LICENSE` and in release bundles at `licenses/SocketIOClient-MIT.txt`. |
| System.Text.Encodings.Web 8.0.0 | [.NET runtime](https://github.com/dotnet/runtime), MIT. The package license is in source at `Libs/dotnet-runtime.LICENSE.TXT`; its package-specific notices are at `Libs/system-text-encodings-web.THIRD-PARTY-NOTICES.TXT`. Release bundles preserve both under `licenses/`. |
| System.Text.Json 8.0.6 | [.NET runtime](https://github.com/dotnet/runtime), MIT. The package license is in source at `Libs/dotnet-runtime.LICENSE.TXT`; its package-specific notices are at `Libs/system-text-json.THIRD-PARTY-NOTICES.TXT`. Release bundles preserve both under `licenses/`. |

## Native Rust dependencies

The native desktop and Android media engines statically link their locked Rust dependency graphs,
including the WebRTC transport, ICE/TURN, DTLS/SRTP, async runtime, cryptography, serialization,
audio I/O, and platform support crates. A deterministic cargo-about inventory covering every shipped
desktop target and Android ARM64 is generated from `native/pc-mobile/Cargo.lock` at
`Libs/native-rust-dependencies.html` and shipped as `licenses/native-rust-dependencies.html`.
CI regenerates this file from the locked graph and rejects drift.

## Optional Windows dependency bundles

The platform-specific `PerfectComms+dependencies-win-x86-steam-itch.zip` and
`PerfectComms+dependencies-win-x64-epic-msstore.zip` assets additionally redistribute the
matching official BepInEx 6 build 735 loader and runtime. Those assets include
`DEPENDENCY_THIRD_PARTY_NOTICES.md` plus exact license texts under
`licenses/dependencies/` for BepInEx, UnityDoorstop, CoreCLR, Il2CppInterop,
HarmonyX/Harmony, MonoMod, and every additional managed/native file copied from
`BepInEx/core`. The same directory records exact provenance for the pinned
Unity reference libraries. The plugin-only release does not contain those
loader/runtime files.
