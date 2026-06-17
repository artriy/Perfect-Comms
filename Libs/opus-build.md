# Rebuilding opus.x64.dll (libopus 1.6.1 with DRED + deep PLC + OSCE)

This embedded native library is the Windows x64 Opus codec used on the Windows build (`NativeOpusCodec.cs`).
It is built from upstream libopus with the ML features enabled (Deep REDundancy, deep packet-loss concealment,
and OSCE), and stripped so the shipped DLL depends only on `KERNEL32.dll` + the UCRT `api-ms-win-crt-*` (present
on Windows 10+); no VC++ redistributable or mingw runtime DLL is required.

## Source

- Upstream: https://github.com/xiph/opus
- Pinned tag: `v1.6.1` (commit `22244de5a79bd1d6d623c32e72bf1954b56235be`)
- License: BSD 3-Clause (Xiph.org Foundation). See `opus.COPYING`.
- Unmodified upstream (no source patches).

## DNN model data

DRED / deep PLC / OSCE need the neural weight headers, which are NOT in the git checkout; they are downloaded
from the official Xiph model mirror by the bundled script:

```
dnn/download_model.sh a5177ec6fb7d15058e99e57029746100121f68e4890b1467d4094aa336b6013e
```

(The hash is the one pinned in the v1.6.1 `autogen.sh`; the script fetches `opus_data-<hash>.tar.gz` from
`https://media.xiph.org/opus/models/` and verifies its SHA-256.)

## Compile (CMake + MinGW-w64 gcc, from the opus checkout root)

```
cmake -B build -G "MinGW Makefiles" -DCMAKE_BUILD_TYPE=Release \
      -DOPUS_BUILD_SHARED_LIBRARY=ON -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_BUILD_TESTING=OFF \
      -DOPUS_DRED=ON -DOPUS_DEEP_PLC=ON -DOPUS_OSCE=ON
cmake --build build -j
strip -s build/libopus.dll
```

Copy `build/libopus.dll` to `PerfectComms/Libs/opus.x64.dll`. It is embedded via `<EmbeddedResource>` in
`PerfectComms.csproj` with logical name `Lib.opus.x64.dll`, matching `OpusNative.ResourceName`.

Verify the DRED API is exported: `objdump -p opus.x64.dll | grep -i dred` should list `opus_dred_decoder_create`,
`opus_dred_parse`, `opus_decoder_dred_decode`.
