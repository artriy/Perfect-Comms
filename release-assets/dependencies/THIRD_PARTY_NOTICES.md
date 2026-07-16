# Dependency Bundle Third-Party Notices

The `PerfectComms+dependencies-win-x86-steam-itch.zip` and
`PerfectComms+dependencies-win-x64-epic-msstore.zip` release assets redistribute the
matching official BepInEx Unity IL2CPP build 735 archive without modifying its
loader or runtime binaries. Perfect Comms adds its plugin, configuration, Unity
reference libraries, documentation, and checksum manifest around those files.

Pinned upstream archives:

- Windows x86: `BepInEx-Unity.IL2CPP-win-x86-6.0.0-be.735+5fef357.zip`,
  SHA-256 `9cd83eae4d47ab07e4ad7f4d98a0085f60fb4b61957857ff197c8729cf1bc483`.
- Windows x64: `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.735+5fef357.zip`,
  SHA-256 `badef8112853a00939a0df6ca143bc0a4e3dc02bd4d21b873302731bfa0e4df4`.

## BepInEx 6.0.0-be.735

- Redistributed files: `BepInEx/core/BepInEx.Core.dll`,
  `BepInEx/core/BepInEx.Preloader.Core.dll`,
  `BepInEx/core/BepInEx.Unity.Common.dll`, and
  `BepInEx/core/BepInEx.Unity.IL2CPP.dll`.
- Exact source: https://github.com/BepInEx/BepInEx/tree/5fef3570f212b2fb5fbe9c1d20487c13c2fa90cb
- License: GNU LGPL 2.1 or later. Full text:
  `licenses/dependencies/BepInEx-LGPL-2.1.txt`.
- Perfect Comms does not modify these binaries.

## UnityDoorstop 4.3.0

- Redistributed files: `.doorstop_version`, `doorstop_config.ini`, and the
  architecture-specific `winhttp.dll` at the archive root.
- Exact source: https://github.com/NeighTools/UnityDoorstop/tree/v4.3.0
- License: GNU LGPL 2.1 or later. Full text:
  `licenses/dependencies/UnityDoorstop-LGPL-2.1.txt`.
- Perfect Comms does not modify the Doorstop binary. The distributed
  `doorstop_config.ini` is the configuration supplied by the exact BepInEx
  source revision.

## BepInEx mini CoreCLR / .NET runtime 6.0.7

- Redistributed files: the complete architecture-specific `dotnet/` directory,
  including `coreclr.dll`, `clrjit.dll`, `hostpolicy.dll`, managed framework
  assemblies, and runtime support data.
- Exact source/release: https://github.com/BepInEx/dotnet-runtime/tree/6.0.7
- Upstream .NET source: https://github.com/dotnet/runtime/tree/v6.0.7
- License: MIT. Full text: `licenses/dependencies/CoreCLR-MIT.txt`.
- Required upstream notices:
  `licenses/dependencies/CoreCLR-THIRD-PARTY-NOTICES.txt`.
- Perfect Comms does not modify these runtime files.

## Il2CppInterop 1.4.6-ci.426

- Redistributed files: `BepInEx/core/Il2CppInterop.Common.dll`,
  `BepInEx/core/Il2CppInterop.Generator.dll`,
  `BepInEx/core/Il2CppInterop.HarmonySupport.dll`, and
  `BepInEx/core/Il2CppInterop.Runtime.dll`.
- Exact package version selected by BepInEx build 735: `1.4.6-ci.426` from
  https://nuget.bepinex.dev/v3/index.json
- Source: https://github.com/BepInEx/Il2CppInterop
- License: GNU LGPL 3.0 only, as declared by the exact NuGet packages. Full
  text: `licenses/dependencies/Il2CppInterop-LGPL-3.0.txt`.
- Perfect Comms does not modify these binaries.

## HarmonyX 2.10.2 and Harmony

- Redistributed file: `BepInEx/core/0Harmony.dll`.
- Exact HarmonyX source: https://github.com/BepInEx/HarmonyX/tree/v2.10.2
- HarmonyX license: MIT. Full text:
  `licenses/dependencies/HarmonyX-MIT.txt`.
- Harmony upstream license: MIT. Full text:
  `licenses/dependencies/Harmony-upstream-MIT.txt`.
- Perfect Comms does not modify this binary.

## MonoMod 22.07.31.01

- Redistributed files: `BepInEx/core/MonoMod.Utils.dll` and
  `BepInEx/core/MonoMod.RuntimeDetour.dll`.
- Exact source: https://github.com/MonoMod/MonoMod/tree/v22.07.31.01
- License: MIT. Full text: `licenses/dependencies/MonoMod-MIT.txt`.
- Perfect Comms does not modify these binaries.

## Additional exact `BepInEx/core` inventory

The following files are also copied unchanged from the matching official
BepInEx build-735 archive:

| Redistributed file(s) | Exact version/source | License text |
| --- | --- | --- |
| `SemanticVersioning.dll` | 2.0.2, https://github.com/adamreeve/semver.net | `licenses/dependencies/SemanticVersioning-MIT.txt` |
| `AssetRipper.Primitives.dll` | 3.1.3, https://github.com/AssetRipper/AssetRipper.Primitives/tree/9c4a7d8127cc9af1e5f61edee8eeb90bd90676aa | `licenses/dependencies/AssetRipper.Primitives-MIT.txt` |
| `AsmResolver.dll`, `AsmResolver.DotNet.dll`, `AsmResolver.PE.dll`, `AsmResolver.PE.File.dll` | 6.0.0-beta.1, https://github.com/Washi1337/AsmResolver/tree/f70da7936c203b4ecfb19c9ad71d08c6eb4b5bd3 | `licenses/dependencies/AsmResolver-MIT.txt` |
| `Cpp2IL.Core.dll`, `LibCpp2IL.dll`, `StableNameDotNet.dll`, `WasmDisassembler.dll` | 2022.1.0-pre-release.19, https://github.com/SamboyCoding/Cpp2IL/tree/edbb9949b3f999a44bb42aea14f357d6e0e7820f | `licenses/dependencies/Cpp2IL-MIT.txt` |
| `AssetRipper.CIL.dll` | 1.1.2, https://github.com/AssetRipper/AssetRipper.CIL/tree/a0c48e0216199fccd65736ab74e9a9065cc37fce | `licenses/dependencies/AssetRipper.CIL-MIT.txt` |
| `Gee.External.Capstone.dll` | 2.3.2, https://github.com/ds5678/Capstone.NET/tree/b90e380c14e857865c94a355954236e678bd557c | `licenses/dependencies/Capstone.NET-MIT.txt` |
| `Disarm.dll` | 2022.1.0-master.57+c25313c, https://github.com/SamboyCoding/Disarm/tree/c25313c438ad4f3b3c7284128368504f98b5686d | `licenses/dependencies/Disarm-MIT.txt` |
| `Iced.dll` | 1.21.0, https://github.com/icedland/iced/tree/601c40570d6c986c8e1a6a2e483dd2fdf5ade552 | `licenses/dependencies/Iced-MIT.txt` |
| `Mono.Cecil.dll`, `Mono.Cecil.Mdb.dll`, `Mono.Cecil.Pdb.dll`, `Mono.Cecil.Rocks.dll` | 0.11.4, https://github.com/jbevain/cecil/tree/0.11.4 | `licenses/dependencies/Mono.Cecil-MIT.txt` |
| `dobby.dll` | 1.0.5, https://github.com/BepInEx/Dobby/tree/v1.0.5 | `licenses/dependencies/Dobby-Apache-2.0.txt` |

The BepInEx XML documentation and IL2CPP configuration files shipped beside
these binaries are covered by the BepInEx source/license entry above.

## Unity 2022.3.44 reference libraries

- Redistributed files: `BepInEx/unity-libs/2022.3.44.zip` plus its 66 extracted
  `UnityEngine*.dll` reference assemblies.
- Exact source: https://unity.bepinex.dev/libraries/2022.3.44.zip
- SHA-256: `e9e6c943619867f0aafb6888bd57ec49e46f833b92ec0e43223346370d69e0bd`.
- The upstream archive contains no license or notice document. Exact provenance
  and that limitation are recorded in
  `licenses/dependencies/UnityReferenceLibraries-NOTICE.txt`; that notice is
  not a license grant.

This file supplements, and does not replace, the main
`THIRD_PARTY_NOTICES.md` shipped with Perfect Comms.
