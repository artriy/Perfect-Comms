# Third-Party Notices

Perfect Comms embeds the following third-party native libraries as assembly resources and extracts them at
runtime (Windows, x64 and x86). Their licenses are reproduced or referenced below.

## libopus 1.6.1 (voice codec, with DRED + deep PLC + OSCE)

- Files: `Libs/opus.x64.dll`, `Libs/opus.x86.dll`
- Upstream: https://github.com/xiph/opus, tag `v1.6.1` (commit `22244de5a79bd1d6d623c32e72bf1954b56235be`)
- License: BSD 3-Clause (Xiph.org Foundation). Full text in `Libs/opus.COPYING`.
- Build recipe: `Libs/opus-build.md`. The embedded DNN model data is the official Xiph model fetched by the
  pinned `dnn/download_model.sh` hash. Unmodified upstream source.

## DeepFilterNet 3 / libDF (noise suppression)

- Files: `Libs/df.x64.dll`, `Libs/df.x86.dll`
- Upstream: https://github.com/Rikorose/DeepFilterNet (commit `d375b2d8309e0935d165700c91da9de862a99c31`)
- License: dual MIT OR Apache-2.0. Full text in `Libs/df.COPYING`.
- The default DeepFilterNet 3 model (author: Hendrik Schroeter / Rikorose) is compiled into the cdylib via the
  `default-model` feature.
- **Derivative work disclosure:** `libDF/src/capi.rs` was patched so `df_create("")` (empty path) loads the
  embedded default model via `DfParams::default()`. The patch is documented in `Libs/df-build.md`.
- Build recipe: `Libs/df-build.md`.

## webrtc-audio-processing (acoustic echo cancellation AEC3 + automatic gain control AGC2 + high-pass filter)

- Files: `Libs/webrtc-apm.x64.dll`, `Libs/webrtc-apm.x86.dll`
- Upstream: WebRTC AudioProcessingModule (Google), via the PulseAudio standalone fork
  https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing (v2.1, WebRTC M131). Prebuilt Windows
  binaries from the `LSXPrime/webrtc-audio-processing` mirror.
- License: BSD 3-Clause. Copyright The WebRTC project authors. Full text in `Libs/webrtc-apm.COPYING`.

## Bundled managed dependencies

The plugin embeds these managed assemblies as resources and resolves them at runtime. Each is redistributed
under its own open-source license; consult the upstream project for the full text. SPDX is listed where it is
unambiguous, otherwise see upstream.

| Assembly | License |
|----------|---------|
| Concentus (managed Opus codec, used on non-Windows builds) | see upstream (xiph/Opus-derived) |
| SIPSorcery, SIPSorceryMedia.Abstractions | see upstream |
| websocket-sharp | MIT |
| SocketIOClient, SocketIO.Core, SocketIO.Serializer.* | MIT |
| BouncyCastle.Cryptography | MIT |
| DnsClient | Apache-2.0 |
| Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions | MIT (.NET Foundation) |
| System.Diagnostics.DiagnosticSource, System.Text.Encodings.Web, System.Text.Json | MIT (.NET Foundation) |
| Interstellar, Interstellar.Messages | third-party voice backend; see its distribution for license terms |
