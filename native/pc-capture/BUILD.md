# pc-capture: build, sign, ship

Capture, playback, DSP (WebRTC-APM AEC/AGC/HPF + DeepFilterNet noise suppression), Opus codec, and WebRTC (webrtc-rs) peer transport with proximity mixing. Loopback 127.0.0.1 single client, token via stdin (native) or token-file (Wine), protocol version 5.

The mod (`PerfectComms.dll`) is platform-agnostic. It embeds one helper binary per target as an embedded resource and extracts the correct one at runtime through the existing `NativeLibraryCache.Extract` path (the same mechanism the embedded `opus.x64.dll` uses). Desktop capture and playback are sidecar-only: the in-proc BASS path was removed in 4.0. If no helper resource is present for the running target, or the helper cannot start, the `CaptureSupervisor` exhausts its restart budget and enters an all-failed state (`_onAllFailed`, logged via `bcl.voice`); there is **no** in-proc desktop fallback, so the user has no voice until the helper can start again. Android is unaffected (it uses the Unity Microphone/AudioSource path, not the sidecar).

## Targets

The five Rust targets, verbatim:

- x86_64-pc-windows-msvc
- i686-pc-windows-msvc
- x86_64-apple-darwin
- aarch64-apple-darwin
- x86_64-unknown-linux-gnu

## Local build

- All non-mac targets: `bash scripts/build-helpers.sh` (use `--dry-run` to preview the target -> output map; pass a single triple, e.g. `bash scripts/build-helpers.sh x86_64-unknown-linux-gnu`, to build just one). Linux needs ALSA dev headers (`libasound2-dev pkg-config`).
- macOS universal + ad-hoc sign: `bash scripts/build-mac.sh` (use `--dry-run` to preview the lipo / codesign plan). Builds both Apple arches, `lipo`s them into a universal binary, wraps it in a `PerfectCommsAudio.app` with an `Info.plist` carrying `NSMicrophoneUsageDescription` and a `PerfectCommsAudio.icns` icon (generated from `Resources/miclogo.png` via `iconutil`), ad-hoc-signs it (`codesign --sign -`), and zips the bundle with `ditto`. The bundle's inner executable is `Contents/MacOS/PerfectCommsAudio`; the embedded/zip artifact name stays `pc-capture-mac.zip`.
- Outputs land in `Libs/pc-capture/` under the frozen names: `pc-capture-win-x64.exe`, `pc-capture-win-x86.exe`, `pc-capture-linux-x64`, `pc-capture-mac.zip`.

These names are frozen: the managed build (`PerfectComms.csproj`), the packaging scripts, the CI workflows, and the mod's extractor all key off them. Do not rename.

## macOS signing: ad-hoc by default (no paid Apple program)

The shipped macOS approach is **ad-hoc codesign only** and intentionally uses **no paid Apple Developer Program, no Developer ID, and no notarization**.

- `scripts/build-mac.sh` runs `codesign --force --deep --sign -`. Ad-hoc signing is free and is the minimum required for an arm64 (Apple Silicon) binary to run at all. There is no hardened runtime, no entitlements file, and no notarization in the default path.
- The mod strips the quarantine xattr at runtime on a best-effort basis (`xattr -dr com.apple.quarantine`, in `SidecarLauncher.StripQuarantine`). In practice the helper is extracted from an embedded resource inside the mod (not downloaded), so the quarantine flag is generally not present to begin with.
- The helper is launched by the mod through the Wine host-exec path, not double-clicked in Finder, so Gatekeeper's "unidentified developer" prompt is not on the hot path. See "How it is launched" below.

This is the real, shipped flow. Developer ID + notarization is an **optional, future** path, not a requirement (see below).

### Optional / future: Developer ID + notarization

`.github/workflows/release.yml` contains an optional, secret-guarded signing branch. It is **inert unless `APPLE_DEVELOPER_ID` is set** in repo secrets. With the secret absent, the release job ships the ad-hoc-signed `pc-capture-mac.zip` unchanged (`if [ -z "${APPLE_DEVELOPER_ID}" ]; then echo "no APPLE_DEVELOPER_ID; shipping unsigned mac app"`). With the secrets present, it codesigns with Developer ID + hardened runtime (`--options runtime`) using `native/pc-capture/macos/pc-capture.entitlements`, notarizes with `notarytool`, and staples. That entitlements file is part of the future path and is only needed if you opt in to paid signing.

If you ever opt in, these repository secrets are read by the release workflow:

- APPLE_DEVELOPER_ID
- APPLE_NOTARY_PROFILE
- APPLE_ID
- APPLE_TEAM_ID
- APPLE_APP_PASSWORD
- APPLE_CERT_P12_BASE64
- APPLE_CERT_PASSWORD

None of these are required for the default ad-hoc release.

## Embedding

The managed build embeds each helper as `Lib.pc-capture.<file>` (mirrors `Lib.bass.x64.dll`), one `<EmbeddedResource>` per frozen output name, each guarded by `Condition="Exists(...)"` so a missing target does not break the build. At runtime the mod extracts the right one via `NativeLibraryCache.Extract` into a per-target cache dir, exactly like `bass.dll`. On the mac the embedded resource is the zipped `.app`; the mod unzips it, `chmod +x`es the inner binary, and strips quarantine.

`scripts/package-release.sh` also stages the same four files as **side-files** in the BepInEx plugin folder (`BepInEx/plugins/pc-capture/`) so they ship alongside the DLL in the release zip. Embedding and side-files are not mutually exclusive: the embedded copy is the runtime source of truth; the side-files make the helpers visible in the distributed package.

## How it is launched

`SidecarLauncher` generates a random token and a host-visible handshake-file path, then:

- **Native Windows:** `Process.Start(helper.exe, --handshake <path>)`, token written to the helper's stdin.
- **Wine (CrossOver / Proton):** host-exec via `start.exe /unix "<hostpath>" --handshake <path>`. The Windows-side path is translated to a host path with `winepath -u` (`WineEnvironment.ResolveHostPath`). Token is written to stdin. This is what runs the native mac/linux helper outside the Wine boundary.
- **Host-exec blocked:** the handshake times out; the `CaptureSupervisor` retries within its restart budget and, if the helper never starts, enters the all-failed "voice unavailable" state (logged). There is no in-proc desktop fallback since BASS was removed in 4.0.

The helper binds `127.0.0.1:0`, writes `{port, pid}` to the handshake file, and the mod connects over loopback (Wine Winsock bridges to host loopback).

## Mic permission (TCC) and fallback

On macOS, mic permission (TCC) attributes to the **CrossOver / host process that launches the helper**, not to a separate signed app identity. If the user can already talk in-game, that grant is in place and the helper inherits it. If mic access is blocked, the helper cannot capture and the `CaptureSupervisor` surfaces the all-failed "voice unavailable" state; there is no in-proc desktop fallback (BASS was removed in 4.0). Mic permission is an OS-level grant and is not something the mod can fix in code.

## CI

- `.github/workflows/native-helpers.yml`: builds the five targets on GitHub-hosted runners (`windows-latest` x64/x86, `ubuntu-latest` gnu, `macos-latest` universal ad-hoc), uploads `helper-*` artifacts, then runs a `--synthetic-tone` smoke job (`scripts/ci-smoke-helper.sh`) that asserts the handshake `ready` format `{rate:48000,channels:1,sample:"f32"}` and that AUDIO frames are exactly `8 + 960*4` bytes.
- `.github/workflows/release.yml`: on `v*` tags, calls the helpers workflow, runs the optional Developer-ID sign/notarize/staple branch (inert without secrets, see above), then packages with `scripts/package-release.sh` and attaches `PerfectComms-Release.zip` to the GitHub Release.

## Compatibility

The helper announces `proto` in its `ready` payload. The mod rejects any helper whose `proto != 5` (the `Proto` constant in `SidecarVoiceClient`). The bundled binary is content-hashed (`NativeLibraryCache`) and re-extracted automatically whenever it changes, so a stale or mismatched side-file cannot be used; the embedded, version-matched helper wins.
