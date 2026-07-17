#!/usr/bin/env python3
"""Verify that a release ZIP extracts directly into an Among Us install root."""

from __future__ import annotations

import argparse
import sys
import zipfile
from pathlib import Path, PurePosixPath


LICENSE_FILES = {
    "licenses/libopus-BSD-3-Clause.txt",
    "licenses/opusic-c-BSD-3-Clause.txt",
    "licenses/webrtc-audio-processing-BSD-3-Clause.txt",
    "licenses/WebRTC-BSD-3-Clause.txt",
    "licenses/WebRTC-ooura-BSD.txt",
    "licenses/WebRTC-spl-sqrt-floor-BSD-3-Clause.txt",
    "licenses/WebRTC-fft-BSD-3-Clause.txt",
    "licenses/WebRTC-pffft-BSD-3-Clause.txt",
    "licenses/WebRTC-rnnoise-BSD-3-Clause.txt",
    "licenses/SocketIOClient-MIT.txt",
    "licenses/dotnet-runtime-MIT.txt",
    "licenses/System.Text.Encodings.Web-THIRD-PARTY-NOTICES.txt",
    "licenses/System.Text.Json-THIRD-PARTY-NOTICES.txt",
    "licenses/native-rust-dependencies.html",
}

DEPENDENCY_LICENSE_FILES = {
    "DEPENDENCY_THIRD_PARTY_NOTICES.md",
    "licenses/dependencies/BepInEx-LGPL-2.1.txt",
    "licenses/dependencies/UnityDoorstop-LGPL-2.1.txt",
    "licenses/dependencies/CoreCLR-MIT.txt",
    "licenses/dependencies/CoreCLR-THIRD-PARTY-NOTICES.txt",
    "licenses/dependencies/Il2CppInterop-LGPL-3.0.txt",
    "licenses/dependencies/HarmonyX-MIT.txt",
    "licenses/dependencies/Harmony-upstream-MIT.txt",
    "licenses/dependencies/MonoMod-MIT.txt",
    "licenses/dependencies/SemanticVersioning-MIT.txt",
    "licenses/dependencies/AssetRipper.Primitives-MIT.txt",
    "licenses/dependencies/AsmResolver-MIT.txt",
    "licenses/dependencies/Cpp2IL-MIT.txt",
    "licenses/dependencies/AssetRipper.CIL-MIT.txt",
    "licenses/dependencies/Capstone.NET-MIT.txt",
    "licenses/dependencies/Disarm-MIT.txt",
    "licenses/dependencies/Iced-MIT.txt",
    "licenses/dependencies/Mono.Cecil-MIT.txt",
    "licenses/dependencies/Dobby-Apache-2.0.txt",
    "licenses/dependencies/UnityReferenceLibraries-NOTICE.txt",
}

DEPENDENCY_PE_MACHINES = {
    "dependencies-win-x86": (0x014C, "x86"),
    "dependencies-win-x64": (0x8664, "x64"),
}

DEPENDENCY_PUBLIC_ARCHIVE_NAMES = {
    "dependencies-win-x86": "PerfectComms+dependencies-win-x86-steam-itch.zip",
    "dependencies-win-x64": "PerfectComms+dependencies-win-x64-epic-msstore.zip",
}

DEPENDENCY_CORE_FILES = {
    "0Harmony.dll",
    "AsmResolver.dll",
    "AsmResolver.DotNet.dll",
    "AsmResolver.PE.dll",
    "AsmResolver.PE.File.dll",
    "AssetRipper.CIL.dll",
    "AssetRipper.Primitives.dll",
    "BepInEx.Core.dll",
    "BepInEx.Core.xml",
    "BepInEx.Preloader.Core.dll",
    "BepInEx.Preloader.Core.xml",
    "BepInEx.Unity.Common.dll",
    "BepInEx.Unity.Common.xml",
    "BepInEx.Unity.IL2CPP.dll",
    "BepInEx.Unity.IL2CPP.dll.config",
    "BepInEx.Unity.IL2CPP.xml",
    "Cpp2IL.Core.dll",
    "Disarm.dll",
    "dobby.dll",
    "Gee.External.Capstone.dll",
    "Iced.dll",
    "Il2CppInterop.Common.dll",
    "Il2CppInterop.Generator.dll",
    "Il2CppInterop.HarmonySupport.dll",
    "Il2CppInterop.Runtime.dll",
    "LibCpp2IL.dll",
    "Mono.Cecil.dll",
    "Mono.Cecil.Mdb.dll",
    "Mono.Cecil.Pdb.dll",
    "Mono.Cecil.Rocks.dll",
    "MonoMod.RuntimeDetour.dll",
    "MonoMod.Utils.dll",
    "SemanticVersioning.dll",
    "StableNameDotNet.dll",
    "WasmDisassembler.dll",
}

DEPENDENCY_NATIVE_PE_FILES = {
    "winhttp.dll",
    "dotnet/coreclr.dll",
    "dotnet/clrjit.dll",
    "dotnet/hostpolicy.dll",
    "BepInEx/core/dobby.dll",
}


def normalize(name: str) -> str:
    value = name.replace("\\", "/")
    while value.startswith("./"):
        value = value[2:]
    return value.rstrip("/")


def required_entries(kind: str) -> set[str]:
    common = {
        "BepInEx/plugins/PerfectComms.dll",
        "README.md",
        "LICENSE",
        "THIRD_PARTY_NOTICES.md",
        "PRIVACY.md",
        "SHA256SUMS.txt",
    } | LICENSE_FILES
    if kind == "Release":
        return common | {
            "VERSION.txt",
            "BepInEx/plugins/pc-capture/pc-capture-win-x64.exe",
            "BepInEx/plugins/pc-capture/pc-capture-win-x86.exe",
            "BepInEx/plugins/pc-capture/pc-capture-linux-x64",
            "BepInEx/plugins/pc-capture/pc-capture-mac.zip",
        }
    if kind == "Android":
        return common | {
            "VERSION.txt",
            "Android/AndroidManifest.xml",
            "Android/README.md",
        }
    if kind in DEPENDENCY_PE_MACHINES:
        core_entries = {f"BepInEx/core/{name}" for name in DEPENDENCY_CORE_FILES}
        return common | DEPENDENCY_LICENSE_FILES | DEPENDENCY_NATIVE_PE_FILES | core_entries | {
            ".doorstop_version",
            "doorstop_config.ini",
            "DEPENDENCIES.txt",
            "BepInEx/config/BepInEx.cfg",
            "BepInEx/unity-libs/2022.3.44.zip",
        }
    raise ValueError(f"unsupported package kind: {kind}")


def validate_archive_filename(archive_path: Path, kind: str) -> None:
    expected_name = DEPENDENCY_PUBLIC_ARCHIVE_NAMES.get(kind)
    if expected_name is not None and archive_path.name != expected_name:
        raise ValueError(
            f"dependency archive name mismatch for {kind}: "
            f"expected {expected_name!r}, got {archive_path.name!r}"
        )


def pe_machine(payload: bytes, name: str) -> int:
    if len(payload) < 0x40 or payload[:2] != b"MZ":
        raise ValueError(f"invalid PE file: {name}")
    header_offset = int.from_bytes(payload[0x3C:0x40], "little")
    if header_offset < 0x40 or header_offset + 6 > len(payload):
        raise ValueError(f"invalid PE header offset: {name}")
    if payload[header_offset : header_offset + 4] != b"PE\0\0":
        raise ValueError(f"invalid PE signature: {name}")
    return int.from_bytes(payload[header_offset + 4 : header_offset + 6], "little")


def validate_archive(archive_path: Path, kind: str) -> int:
    with zipfile.ZipFile(archive_path) as archive:
        files: dict[str, zipfile.ZipInfo] = {}
        for entry in archive.infolist():
            name = normalize(entry.filename)
            if not name or entry.is_dir():
                continue
            pure = PurePosixPath(name)
            if pure.is_absolute() or ".." in pure.parts:
                raise ValueError(f"unsafe archive path: {entry.filename!r}")
            if name in files:
                raise ValueError(f"duplicate archive entry: {name}")
            files[name] = entry

        required = required_entries(kind)
        missing = sorted(required - files.keys())
        empty = sorted(name for name in required if files.get(name, None) is not None and files[name].file_size == 0)
        if missing:
            raise ValueError(f"missing archive-root entries: {', '.join(missing)}")
        if empty:
            raise ValueError(f"empty required entries: {', '.join(empty)}")

        if kind in DEPENDENCY_PE_MACHINES:
            core_prefix = "BepInEx/core/"
            unexpected_core = sorted(
                name
                for name in files
                if name.startswith(core_prefix)
                and name[len(core_prefix) :] not in DEPENDENCY_CORE_FILES
            )
            if unexpected_core:
                raise ValueError(
                    "unexpected BepInEx core entries without audited notices: "
                    + ", ".join(unexpected_core)
                )

            expected_machine, architecture = DEPENDENCY_PE_MACHINES[kind]
            for name in sorted(DEPENDENCY_NATIVE_PE_FILES):
                actual_machine = pe_machine(archive.read(files[name]), name)
                if actual_machine != expected_machine:
                    raise ValueError(
                        f"PE machine mismatch for {name}: package label is {architecture} "
                        f"(expected 0x{expected_machine:04x}, got 0x{actual_machine:04x})"
                    )

    forbidden = sorted(
        name for name in files if name.endswith(("/MiraAPI.dll", "/Reactor.dll"))
    )
    if forbidden:
        raise ValueError(f"forbidden bundled dependencies: {', '.join(forbidden)}")

    return len(files)


def minimal_pe(machine: int) -> bytes:
    payload = bytearray(0x80)
    payload[:2] = b"MZ"
    payload[0x3C:0x40] = (0x40).to_bytes(4, "little")
    payload[0x40:0x44] = b"PE\0\0"
    payload[0x44:0x46] = machine.to_bytes(2, "little")
    return bytes(payload)


def write_fixture(
    archive_path: Path,
    kind: str,
    pe_overrides: dict[str, int] | None = None,
) -> None:
    expected_machine = DEPENDENCY_PE_MACHINES.get(kind, (None, ""))[0]
    overrides = pe_overrides or {}
    with zipfile.ZipFile(archive_path, "w") as archive:
        for name in sorted(required_entries(kind)):
            if name in DEPENDENCY_NATIVE_PE_FILES:
                machine = overrides.get(name, expected_machine)
                assert machine is not None
                archive.writestr(name, minimal_pe(machine))
            else:
                archive.writestr(name, b"x")


def self_test() -> int:
    # Keep fixtures directly under artifacts: Windows sandbox tokens can lose
    # access to mode-0700 directories created by tempfile.TemporaryDirectory.
    root = Path.cwd() / "artifacts"
    root.mkdir(exist_ok=True)
    fixtures = [
        root / ".package-layout-self-test-Release.zip",
        root / ".package-layout-self-test-Android.zip",
        root / ".package-layout-self-test-dependencies-win-x86.zip",
        root / ".package-layout-self-test-dependencies-win-x64.zip",
        root / ".package-layout-self-test-cross-label.zip",
        root / ".package-layout-self-test-mixed.zip",
        root / ".package-layout-self-test-core-drift.zip",
        root / ".package-layout-self-test-unsafe.zip",
    ]
    try:
        for kind, expected_name in DEPENDENCY_PUBLIC_ARCHIVE_NAMES.items():
            validate_archive_filename(Path(expected_name), kind)
            try:
                validate_archive_filename(Path(f"wrong-{kind}.zip"), kind)
            except ValueError as error:
                assert "dependency archive name mismatch" in str(error)
            else:
                raise AssertionError("incorrect dependency archive name was accepted")

        for kind in (
            "Release",
            "Android",
            "dependencies-win-x86",
            "dependencies-win-x64",
        ):
            archive_path = root / f".package-layout-self-test-{kind}.zip"
            write_fixture(archive_path, kind)
            assert validate_archive(archive_path, kind) == len(required_entries(kind))

        cross_label_path = root / ".package-layout-self-test-cross-label.zip"
        write_fixture(cross_label_path, "dependencies-win-x86")
        try:
            validate_archive(cross_label_path, "dependencies-win-x64")
        except ValueError as error:
            assert "PE machine mismatch" in str(error)
        else:
            raise AssertionError("cross-labeled dependency archive was accepted")

        mixed_path = root / ".package-layout-self-test-mixed.zip"
        write_fixture(
            mixed_path,
            "dependencies-win-x86",
            {"dotnet/coreclr.dll": DEPENDENCY_PE_MACHINES["dependencies-win-x64"][0]},
        )
        try:
            validate_archive(mixed_path, "dependencies-win-x86")
        except ValueError as error:
            assert "PE machine mismatch for dotnet/coreclr.dll" in str(error)
        else:
            raise AssertionError("mixed-architecture dependency archive was accepted")

        core_drift_path = root / ".package-layout-self-test-core-drift.zip"
        write_fixture(core_drift_path, "dependencies-win-x64")
        with zipfile.ZipFile(core_drift_path, "a") as archive:
            archive.writestr("BepInEx/core/UnknownDependency.dll", b"x")
        try:
            validate_archive(core_drift_path, "dependencies-win-x64")
        except ValueError as error:
            assert "unexpected BepInEx core entries" in str(error)
        else:
            raise AssertionError("unaudited BepInEx core entry was accepted")

        unsafe_path = root / ".package-layout-self-test-unsafe.zip"
        with zipfile.ZipFile(unsafe_path, "w") as archive:
            archive.writestr("../escape.txt", b"x")
        try:
            validate_archive(unsafe_path, "Release")
        except ValueError as error:
            assert "unsafe archive path" in str(error)
        else:
            raise AssertionError("unsafe archive path was accepted")
    finally:
        for fixture in fixtures:
            fixture.unlink(missing_ok=True)

    print("release.package.layout_self_test_ok")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path, nargs="?")
    parser.add_argument(
        "--kind",
        choices=(
            "Release",
            "Android",
            "dependencies-win-x86",
            "dependencies-win-x64",
        ),
    )
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.archive is None or args.kind is None:
        parser.error("archive and --kind are required unless --self-test is used")

    try:
        validate_archive_filename(args.archive, args.kind)
        entry_count = validate_archive(args.archive, args.kind)

        print(
            f"release.package.layout_ok kind={args.kind} "
            f"entries={entry_count} archive={args.archive}"
        )
        return 0
    except (OSError, ValueError, zipfile.BadZipFile) as error:
        print(
            f"release.package.layout_invalid kind={args.kind} "
            f"archive={args.archive} error={error}",
            file=sys.stderr,
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
