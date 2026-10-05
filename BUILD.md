# Building and release validation

Perfect Comms separates managed compilation from release assembly. A clean
checkout can compile and run tests without prebuilt native binaries. The
packaging scripts are the only supported way to create release artifacts;
desktop packaging validates its native assets, while Starlight packaging
validates a managed-only dependency set.

## Portable local development tools

The local `dev-tools/` bundle contains the downloaded archives, extracted
Windows toolchains, Rust homes, Python build packages, and restored NuGet
package cache. Copy the entire project folder, including `dev-tools/` and
`Libs/`, to transfer it. The bundle is ignored by Git and excluded from default
MSBuild items; pushing or cloning the repository does not transfer it.

On a Windows x64 destination, install the .NET 10 SDK, Visual Studio 2022
with Desktop development with C++ and a Windows SDK (Community or Build
Tools), Git for Windows, and Python 3.14. Then double-click
`dev-tools/Start-Dev.cmd`, or dot-source `dev-tools/Enter-Dev.ps1` in a PowerShell
session that permits local scripts. Activation uses paths relative to the
bundle for `PATH`, `CARGO_HOME`, `RUSTUP_HOME`, `NUGET_PACKAGES`, and `PYTHONPATH`.
The NuGet package path includes a trailing separator so the test projects'
explicit game-library references resolve after moving the bundle.

Run the locked restores below after moving; existing `obj/` files may refer
to the old machine's paths. The bundled GCC 16.2.0 specs directly locate the
shipped default manifest, avoiding its optional-lookup bug with spaces in
installation paths. Rust proxies are regular executables so folder copies do
not require symbolic-link privileges. The bundle also includes GitHub CLI,
NASM 3.02, `cargo-audit` 0.22.2, and `cargo-about` 0.9.1. The x64 MinGW toolchain
includes the architecture-prefixed resource compiler and strip executable names
used by the APM crossfile. Existing x86 tools in a local bundle are not used.

Account authorization does not travel with the bundle. In the activated shell,
run `gh auth login --hostname github.com` for GitHub access. For Cloudflare
deployment, change to `cloudflare/perfect-comms-lobbies` and run
`npx wrangler login`. Install the locked Worker tools with `npm ci`.
Local development and deployment dry runs do not require these account logins.

This setup uses native Windows tools, not WSL. Windows x64 helpers, Pion,
and APM can be built locally; Linux and macOS builds use the existing GitHub
Actions runners. Starlight produces a managed DLL and does not require an
Android SDK or NDK.

Bullseye containers use signed Debian archive repositories to retain the glibc
2.31 baseline. Only archive metadata expiry checks are disabled; package
signatures and checksums are still verified.


## Managed gate

Use the .NET 10 SDK to build and test the net6.0 desktop plugin target against
`AmongUs.GameLibs.Steam` 2026.9.29. The current Steam game and Windows native
payloads are x64 only; Windows x86 is not supported.

```powershell
dotnet restore PerfectComms.Tests/PerfectComms.Tests.csproj --locked-mode
dotnet build PerfectComms.csproj -c Release --no-restore
dotnet test PerfectComms.Tests/PerfectComms.Tests.csproj -c Release --no-restore
```

The Android target is `PerfectComms.Starlight.csproj`, a net10.0 Starlight
plugin compiled with `ANDROID` and `STARLIGHT` against
`AmongUs.GameLibs.Android` `2026.8.18` and the Starlight `1.6.3` host contract.
The Starlight build restores the media,
plugin, and merge-tool dependency graphs with `--locked-mode`; committed lock
files make a dependency change fail the restore instead of silently changing
the build.

The Cloudflare Worker uses an exact Wrangler lockfile. Its hibernating Durable
Object holds live lobby state on host WebSocket connections and pushes
snapshot/upsert/remove events to browser WebSockets without D1 polling. CI runs
the request, ownership, expiry, rate-limit, and TURN tests, then builds the
deployment bundle in dry-run mode.

## Native gate

CI runs formatting, strict Clippy, and every `pc-capture` target test. It also
runs the real Pion WebRTC v4.2.17 offer/answer/trickle-ICE/DTLS/SRTP/Opus
loopback on Windows and Linux, then forces two Linux helpers through the
deployed Cloudflare TURN service and requires relayed Opus audio telemetry in
both directions. The full helper workflow builds and smoke-tests Windows
x64, Linux x64, the final APM-containing/signature-verified universal macOS
app, the matching desktop Pion C-shared transport libraries, and desktop WebRTC
APM libraries.

Desktop helpers require sidecar protocol 17 and Pion ABI 3 for private-only audio
recipient selection. Rebuild every platform's helper and Pion companion together;
older binaries must not be packaged. Public fanout and ICE behavior are unchanged.
Voice clients use game protocol 6 for the impostor settings and phase-scoped radio
state; the mobile interop contract remains ABI 5.

RustSec audits every native lockfile during CI. The desktop codec is the bundled
libopus 1.6.1 source from the pinned `opusic-sys` binding, compiled with DRED on
every native target. The Pion module and its transitive Go dependencies are
locked in `native/pc-pion/go.sum`; CI also verifies the generated license
inventory.

Android is not part of the native gate. Starlight owns Android capture through
its approved interop surface, while the net10.0 managed media project owns
Opus, mixing, and peer transport. The Starlight distribution has no Android
Pion, NDK-built library, or other native payload.

Pion release builds require Go 1.26.2 exactly. Build and optionally stage one
target with:

```bash
bash scripts/build-pion.sh <win-x64|linux-x64> --stage
```

Build both macOS architecture slices before `mac-universal`; the normal
`scripts/build-mac.sh` flow does this automatically and seals
`libpc-pion.dylib` into the app before signing. For a manual macOS build, run
`mac-x64` and `mac-arm64` without `--stage`, then run `mac-universal --stage`.

## Release assembly

The tag workflow downloads the complete desktop helper matrix and invokes:

```bash
bash scripts/package-release.sh Release
```

For a local desktop package, stage every artifact generated by the helper
workflow under `Libs/pc-capture`, `Libs/dsp`, and `Libs/pion`, then run
`scripts/package-release.ps1`. Desktop packaging requires all three helpers,
the desktop APM libraries, and the Windows x64 and Linux x64 Pion
libraries; the universal macOS Pion dylib is already sealed inside the signed
helper app. Missing or empty files are a hard error.

Build the canonical managed Android tester DLL with either command:

```bash
bash scripts/package-starlight.sh Release
```

```powershell
pwsh scripts/package-starlight.ps1 -Configuration Release
```

Both commands create exactly `artifacts/PerfectCommsStarlight.dll`. The managed
media stack and its pinned managed dependencies are merged into that assembly
at build time. The DLL also embeds the Perfect Comms license, Starlight
third-party notices, and the complete SIPSorcery license. Validation rejects
native payloads, unmanaged imports outside Starlight's exact capture ABI,
executable extraction, unexpected sibling DLLs or archives, and any output
other than the single managed assembly. Copy
or upload only `PerfectCommsStarlight.dll` for Starlight beta or approved
local-mod testing. It is not an APK and contains no native Android payload.

Desktop packaging publishes `PerfectComms.dll`. The desktop DLL embeds its
cross-platform native helpers, managed runtime dependencies, and complete
third-party notices. BepInEx remains an external desktop runtime requirement
and is never downloaded, staged, or published by the release workflow.

On a deeply nested Windows checkout, set `PC_CARGO_TARGET_DIR` to a short Git
Bash path such as `/d/pc-target` before running `scripts/build-helpers.sh` to
avoid the legacy MSVC/CMake `MAX_PATH` limit.

GitHub only publishes release assets after the managed tests, native quality
gate, cross-platform helper builds, DSP smoke tests, and shared-core RTC
loopback all succeed for that exact tagged commit. The workflow also rejects a
tag that is not the current `main` tip, or whose version does not exactly match
the project, plugin, assembly, file, and informational versions.

### Reference-only API package

A desktop managed build produces the reference assembly and XML documentation used by the developer package. Package and smoke-test it without staging native release assets:

```bash
dotnet build PerfectComms.csproj -c Release
bash scripts/package-api.sh Release
```

On Windows, use `pwsh scripts/package-api.ps1 -Configuration Release`. Both commands create `artifacts/PerfectComms.Api.<PerfectCommsApiPackageVersion>.nupkg`, validate that it contains only `ref/net6.0` compiler assets, compile a real consumer through `PackageReference`, and fail if `PerfectComms.dll` is copied to that consumer's output. Desktop release packaging runs the same gate automatically.

Tagged releases publish the package through NuGet.org Trusted Publishing. One-time repository setup is required:

1. Set the GitHub Actions repository variable `NUGET_USER` to the NuGet.org profile name that owns the package.
2. In that NuGet.org account's **Trusted Publishing** settings, add a GitHub Actions policy for owner `artriy`, repository `Perfect-Comms`, and workflow file `release.yml`. Do not set an environment unless the publish job is updated to use the same environment.

The workflow requests only a short-lived OIDC credential; no long-lived NuGet API key is stored.

### GitHub release rehearsal

Before tagging, open **Actions -> Release -> Run workflow** and run it against
`main`. A manual run never creates a GitHub Release. It runs the complete
managed, native, RTC, desktop, Starlight, and packaging gates, then uploads
three Actions artifacts for 14 days:

- `PerfectComms-standalone-dlls-*`, containing the desktop `PerfectComms.dll`;
- `PerfectComms-Starlight-testers-*`, containing only
  `PerfectCommsStarlight.dll` for Starlight beta or approved local-mod testing;
- `PerfectComms-api-nuget-*`, containing the reference-only `PerfectComms.Api.<PerfectCommsApiPackageVersion>.nupkg`.

### Publishing a release

1. Set `<Version>` and `<InformationalVersion>` to `X.Y.Z`, set
   `<AssemblyVersion>` and `<FileVersion>` to `X.Y.Z.0`, set
   `VoiceChatPluginMain.Version` to `X.Y.Z`, and set
   `<PerfectCommsApiPackageVersion>` to the immutable NuGet version published for that API contract.
2. Add the matching `Perfect Comms vX.Y.Z` changelog entry, commit the changes
   on `main`, and wait for every workflow triggered by that push.
3. Run the manual Release rehearsal and inspect its packaged artifact.
4. Create and push tag `vX.Y.Z` on that tested `main` commit.

The tag run repeats every gate, verifies the tag is the current `main` tip,
checks every version field, publishes the desktop `PerfectComms.dll` GitHub
release asset, and publishes the reference-only API package to NuGet.org using
OIDC. The single managed `PerfectCommsStarlight.dll` remains the
`PerfectComms-Starlight-testers-*` workflow artifact for Starlight beta or
approved local-mod testing. GitHub release notes are generated automatically.
A failed gate cannot publish a partial release.
