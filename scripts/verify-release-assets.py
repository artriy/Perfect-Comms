#!/usr/bin/env python3
"""Reject native release payloads with the wrong platform, architecture, or bundle layout."""

from __future__ import annotations

import argparse
import struct
import sys
import zipfile
from pathlib import Path


PE_I386 = 0x014C
PE_AMD64 = 0x8664
ELF_X86_64 = 62
ELF_AARCH64 = 183
ELF_ET_DYN = 3
MACH_X86_64 = 0x01000007
MACH_ARM64 = 0x0100000C


def fail(message: str) -> None:
    raise ValueError(message)


def require_file(root: Path, relative: str) -> Path:
    path = root / relative
    if not path.is_file() or path.stat().st_size == 0:
        fail(f"missing or empty release asset: {relative}")
    return path


def assert_pe(path: Path, expected_machine: int) -> None:
    data = path.read_bytes()
    if len(data) < 0x40 or data[:2] != b"MZ":
        fail(f"{path}: expected a PE file (MZ header)")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if pe_offset + 6 > len(data) or data[pe_offset : pe_offset + 4] != b"PE\0\0":
        fail(f"{path}: invalid PE signature")
    machine = struct.unpack_from("<H", data, pe_offset + 4)[0]
    if machine != expected_machine:
        fail(f"{path}: PE machine 0x{machine:04x}, expected 0x{expected_machine:04x}")


def assert_elf(path: Path, expected_machine: int, expected_type: int | None = None) -> None:
    assert_elf_bytes(path.read_bytes(), str(path), expected_machine, expected_type)


def assert_elf_bytes(
    data: bytes, label: str, expected_machine: int, expected_type: int | None = None
) -> None:
    if len(data) < 20 or data[:4] != b"\x7fELF":
        fail(f"{label}: expected an ELF file")
    if data[4] != 2 or data[5] != 1:
        fail(f"{label}: expected ELF64 little-endian, got class={data[4]} data={data[5]}")
    machine = struct.unpack_from("<H", data, 18)[0]
    if machine != expected_machine:
        fail(f"{label}: ELF machine {machine}, expected {expected_machine}")
    elf_type = struct.unpack_from("<H", data, 16)[0]
    if expected_type is not None and elf_type != expected_type:
        fail(f"{label}: ELF type {elf_type}, expected {expected_type}")


def macho_architectures(data: bytes, label: str) -> set[int]:
    if len(data) < 8:
        fail(f"{label}: truncated Mach-O file")
    magic = data[:4]
    if magic in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
        count = struct.unpack_from(">I", data, 4)[0]
        entry_size = 20 if magic == b"\xca\xfe\xba\xbe" else 32
        if count == 0 or 8 + count * entry_size > len(data):
            fail(f"{label}: invalid universal Mach-O header")
        return {struct.unpack_from(">I", data, 8 + i * entry_size)[0] for i in range(count)}
    if magic == b"\xcf\xfa\xed\xfe":
        return {struct.unpack_from("<I", data, 4)[0]}
    if magic == b"\xfe\xed\xfa\xcf":
        return {struct.unpack_from(">I", data, 4)[0]}
    fail(f"{label}: expected a 64-bit Mach-O or universal Mach-O file")
    return set()


def assert_universal_macho_bytes(data: bytes, label: str) -> None:
    architectures = macho_architectures(data, label)
    required = {MACH_X86_64, MACH_ARM64}
    if not required.issubset(architectures):
        formatted = ",".join(f"0x{value:08x}" for value in sorted(architectures))
        fail(f"{label}: expected x86_64+arm64 universal Mach-O, found [{formatted}]")


def assert_universal_macho(path: Path) -> None:
    assert_universal_macho_bytes(path.read_bytes(), str(path))


def assert_mac_bundle(path: Path) -> None:
    required_entries = (
        "PerfectCommsAudio.app/Contents/Info.plist",
        "PerfectCommsAudio.app/Contents/MacOS/PerfectCommsAudio",
        "PerfectCommsAudio.app/Contents/MacOS/libwebrtc-apm.dylib",
        "PerfectCommsAudio.app/Contents/MacOS/libdf.dylib",
    )
    try:
        with zipfile.ZipFile(path) as archive:
            entries = {name.lstrip("./").replace("\\", "/"): name for name in archive.namelist()}
            missing = [name for name in required_entries if name not in entries]
            if missing:
                fail(f"{path}: mac bundle is missing: {', '.join(missing)}")
            for relative in required_entries[1:]:
                data = archive.read(entries[relative])
                assert_universal_macho_bytes(data, f"{path}!{relative}")
    except zipfile.BadZipFile as error:
        fail(f"{path}: invalid zip archive: {error}")


def verify_android(root: Path) -> None:
    path = require_file(root, "Libs/pc-mobile/libpc_mobile.so")
    assert_elf(path, ELF_AARCH64, ELF_ET_DYN)
    print("release.asset.ok target=android-arm64 format=elf64-aarch64-shared path=Libs/pc-mobile/libpc_mobile.so")


def verify_desktop(root: Path) -> None:
    pe_assets = (
        ("Libs/pc-capture/pc-capture-win-x64.exe", PE_AMD64),
        ("Libs/pc-capture/pc-capture-win-x86.exe", PE_I386),
        ("Libs/dsp/webrtc-apm.x64.dll", PE_AMD64),
        ("Libs/dsp/webrtc-apm.x86.dll", PE_I386),
        ("Libs/dsp/df.x64.dll", PE_AMD64),
        ("Libs/dsp/df.x86.dll", PE_I386),
    )
    elf_assets = (
        "Libs/pc-capture/pc-capture-linux-x64",
        "Libs/dsp/libwebrtc-apm.so",
        "Libs/dsp/libdf.so",
    )
    macho_assets = (
        "Libs/dsp/libwebrtc-apm.dylib",
        "Libs/dsp/libdf.dylib",
    )
    for relative, machine in pe_assets:
        assert_pe(require_file(root, relative), machine)
        print(f"release.asset.ok format=pe machine=0x{machine:04x} path={relative}")
    for relative in elf_assets:
        assert_elf(require_file(root, relative), ELF_X86_64)
        print(f"release.asset.ok format=elf64 machine=x86_64 path={relative}")
    for relative in macho_assets:
        assert_universal_macho(require_file(root, relative))
        print(f"release.asset.ok format=macho-universal architectures=x86_64,arm64 path={relative}")
    mac_zip = require_file(root, "Libs/pc-capture/pc-capture-mac.zip")
    assert_mac_bundle(mac_zip)
    print("release.asset.ok target=macos format=app-zip architectures=x86_64,arm64 path=Libs/pc-capture/pc-capture-mac.zip")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--configuration", choices=("Release", "Android"), required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        if args.configuration == "Android":
            verify_android(root)
        else:
            verify_desktop(root)
    except (OSError, ValueError) as error:
        print(f"release.asset.invalid configuration={args.configuration} error={error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
