# Third-Party Notices

Perfect Comms embeds the following third-party native libraries as assembly resources and extracts them at
runtime for the applicable desktop platform. Their licenses are reproduced or referenced below.

## libopus 1.6.1 (voice codec, with DRED + deep PLC + OSCE)

- Files: `Libs/opus.x64.dll`, `Libs/opus.x86.dll`
- Upstream: https://github.com/xiph/opus, tag `v1.6.1` (commit `22244de5a79bd1d6d623c32e72bf1954b56235be`)
- License: BSD 3-Clause (Xiph.org Foundation). Full text in `Libs/opus.COPYING`.
- Build recipe: `Libs/opus-build.md`. The embedded DNN model data is the official Xiph model fetched by the
  pinned `dnn/download_model.sh` hash. Unmodified upstream source.

## webrtc-audio-processing (AEC3 + noise suppression + high-pass filter)

- Release files: `Libs/dsp/webrtc-apm.x64.dll`, `Libs/dsp/webrtc-apm.x86.dll`,
  `Libs/dsp/libwebrtc-apm.so`, `Libs/dsp/libwebrtc-apm.dylib`
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
| SocketIOClient, SocketIO.Core, SocketIO.Serializer.* | MIT |
| System.Text.Encodings.Web, System.Text.Json | MIT (.NET Foundation) |
