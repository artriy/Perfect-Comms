# Third-Party Notices

Perfect Comms embeds the following third-party native libraries as assembly resources and extracts them at
runtime (Windows x64 only). Their licenses are reproduced or referenced below.

## BASS / BASS_FX (audio I/O) — PROPRIETARY, see license terms

- Files: `Libs/bass.x64.dll`, `Libs/bass.x86.dll`
- Upstream: un4seen Developments, https://www.un4seen.com/bass.html (managed wrapper: ManagedBass)
- License: **proprietary**. BASS is **free only for non-commercial use**; commercial/monetized use (including
  donation-supported distribution) requires a paid redistribution license from un4seen. Perfect Comms is a free,
  non-monetized mod and is distributed under the un4seen free/non-commercial terms. If this changes, a
  commercial BASS license must be obtained (or BASS replaced with a permissively-licensed audio path).
  See https://www.un4seen.com/ for the current license text.

## libopus 1.6.1 (voice codec, with DRED + deep PLC + OSCE)

- Files: `Libs/opus.x64.dll`
- Upstream: https://github.com/xiph/opus, tag `v1.6.1` (commit `22244de5a79bd1d6d623c32e72bf1954b56235be`)
- License: BSD 3-Clause (Xiph.org Foundation). Full text in `Libs/opus.COPYING`.
- Build recipe: `Libs/opus-build.md`. The embedded DNN model data is the official Xiph model fetched by the
  pinned `dnn/download_model.sh` hash. Unmodified upstream source.

## DeepFilterNet 3 / libDF (noise suppression)

- Files: `Libs/df.x64.dll`
- Upstream: https://github.com/Rikorose/DeepFilterNet (commit `d375b2d8309e0935d165700c91da9de862a99c31`)
- License: dual MIT OR Apache-2.0. Full text in `Libs/df.COPYING`.
- The default DeepFilterNet 3 model (author: Hendrik Schroeter / Rikorose) is compiled into the cdylib via the
  `default-model` feature.
- **Derivative work disclosure:** `libDF/src/capi.rs` was patched so `df_create("")` (empty path) loads the
  embedded default model via `DfParams::default()`. The patch is documented in `Libs/df-build.md`.
- Build recipe: `Libs/df-build.md`.

## speexdsp (acoustic echo cancellation + preprocessor)

- Files: `Libs/speexdsp.x64.dll`, `Libs/speexdsp.x86.dll`
- Upstream: https://gitlab.xiph.org/xiph/speexdsp (commit `ed26e53`)
- License: BSD 3-Clause. Copyright 2002-2008 Xiph.org Foundation and Jean-Marc Valin. Full text in
  `Libs/speexdsp.COPYING`.
- Build recipe: `Libs/speexdsp-build.md`.

## Bundled managed dependencies

The plugin embeds these managed assemblies as resources and resolves them at runtime. Each is redistributed
under its own open-source license; consult the upstream project for the full text. SPDX is listed where it is
unambiguous, otherwise see upstream.

| Assembly | License |
|----------|---------|
| ManagedBass, ManagedBass.Fx (BASS .NET wrapper) | MPL-2.0 (wraps the proprietary BASS native, see above) |
| Concentus (managed Opus codec, used on non-Windows builds) | see upstream (xiph/Opus-derived) |
| SIPSorcery, SIPSorceryMedia.Abstractions | see upstream |
| websocket-sharp | MIT |
| SocketIOClient, SocketIO.Core, SocketIO.Serializer.* | MIT |
| BouncyCastle.Cryptography | MIT |
| DnsClient | Apache-2.0 |
| Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions | MIT (.NET Foundation) |
| System.Diagnostics.DiagnosticSource, System.Text.Encodings.Web, System.Text.Json | MIT (.NET Foundation) |
| Interstellar, Interstellar.Messages | third-party voice backend; see its distribution for license terms |
