#!/usr/bin/env python3
"""Reject native release payloads with the wrong platform, architecture, or bundle layout."""

from __future__ import annotations

import argparse
import struct
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path


PE_I386 = 0x014C
PE_AMD64 = 0x8664
ELF_X86_64 = 62
ELF_AARCH64 = 183
ELF_ET_DYN = 3
ELF_SHT_DYNSYM = 11
ELF_STB_GLOBAL = 1
ELF_STT_OBJECT = 1
ELF_STV_DEFAULT = 0
ELF_SHN_UNDEF = 0
MACH_X86_64 = 0x01000007
MACH_ARM64 = 0x0100000C
ANDROID_NAME_ATTRIBUTE = "{http://schemas.android.com/apk/res/android}name"
PC_MOBILE_ABI_EXPECTED = 3
PC_MOBILE_ABI_MARKER_PREFIX = b"PERFECTCOMMS_PC_MOBILE_ABI="
PC_MOBILE_ABI_MARKER_SYMBOL = b"PC_MOBILE_ABI_MARKER"


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


def elf64_sections(data: bytes, label: str) -> list[tuple[int, ...]]:
    if len(data) < 64:
        fail(f"{label}: truncated ELF64 header")
    section_offset = struct.unpack_from("<Q", data, 40)[0]
    section_entry_size = struct.unpack_from("<H", data, 58)[0]
    section_count = struct.unpack_from("<H", data, 60)[0]
    if section_offset == 0 or section_count == 0 or section_entry_size < 64:
        fail(f"{label}: missing or unsupported ELF64 section table")
    if section_offset + section_entry_size * section_count > len(data):
        fail(f"{label}: truncated ELF64 section table")
    return [
        struct.unpack_from("<IIQQQQIIQQ", data, section_offset + index * section_entry_size)
        for index in range(section_count)
    ]


def section_bytes(data: bytes, section: tuple[int, ...], label: str) -> bytes:
    offset = section[4]
    size = section[5]
    if offset + size > len(data):
        fail(f"{label}: ELF64 section extends past end of file")
    return data[offset : offset + size]


def expected_pc_mobile_abi_marker(expected_abi: int) -> bytes:
    return PC_MOBILE_ABI_MARKER_PREFIX + str(expected_abi).encode("ascii") + b"\0"


def assert_pc_mobile_abi_bytes(data: bytes, label: str, expected_abi: int) -> None:
    expected_marker = expected_pc_mobile_abi_marker(expected_abi)
    marker_count = data.count(PC_MOBILE_ABI_MARKER_PREFIX)
    if marker_count != 1:
        fail(f"{label}: expected exactly one pc-mobile ABI marker, found {marker_count}")

    sections = elf64_sections(data, label)
    matches: list[tuple[int, int, int, int, int, int]] = []
    for section in sections:
        if section[1] != ELF_SHT_DYNSYM:
            continue
        string_section_index = section[6]
        if string_section_index >= len(sections):
            fail(f"{label}: ELF64 dynamic symbol table has an invalid string-table link")
        strings = section_bytes(data, sections[string_section_index], label)
        symbols = section_bytes(data, section, label)
        entry_size = section[9]
        if entry_size < 24 or len(symbols) % entry_size != 0:
            fail(f"{label}: malformed ELF64 dynamic symbol table")
        for offset in range(0, len(symbols), entry_size):
            name_offset, info, other, section_index, value, size = struct.unpack_from(
                "<IBBHQQ", symbols, offset
            )
            if name_offset >= len(strings):
                fail(f"{label}: ELF64 dynamic symbol has an invalid name offset")
            name_end = strings.find(b"\0", name_offset)
            if name_end < 0:
                fail(f"{label}: unterminated ELF64 dynamic symbol name")
            if strings[name_offset:name_end] == PC_MOBILE_ABI_MARKER_SYMBOL:
                matches.append((info, other, section_index, value, size, offset))

    if len(matches) != 1:
        fail(f"{label}: expected one exported pc-mobile ABI marker symbol, found {len(matches)}")
    info, other, section_index, value, size, _ = matches[0]
    if (
        info >> 4 != ELF_STB_GLOBAL
        or info & 0x0F != ELF_STT_OBJECT
        or other & 0x03 != ELF_STV_DEFAULT
        or section_index == ELF_SHN_UNDEF
    ):
        fail(f"{label}: pc-mobile ABI marker symbol must be defined GLOBAL OBJECT DEFAULT")
    if size != len(expected_marker):
        fail(f"{label}: pc-mobile ABI marker size {size}, expected {len(expected_marker)}")
    if section_index >= len(sections):
        fail(f"{label}: pc-mobile ABI marker references an invalid ELF64 section")

    marker_section = sections[section_index]
    section_address = marker_section[3]
    section_offset = marker_section[4]
    section_size = marker_section[5]
    if value < section_address or value - section_address + size > section_size:
        fail(f"{label}: pc-mobile ABI marker lies outside its ELF64 section")
    marker_offset = section_offset + value - section_address
    if marker_offset + size > len(data):
        fail(f"{label}: pc-mobile ABI marker extends past end of file")
    marker = data[marker_offset : marker_offset + size]
    if marker != expected_marker:
        fail(f"{label}: pc-mobile ABI marker {marker!r}, expected {expected_marker!r}")


def align_up(value: int, alignment: int) -> int:
    return (value + alignment - 1) // alignment * alignment


def self_test_pc_mobile_elf(
    marker: bytes | None,
    *,
    symbol_count: int = 1,
    symbol_info: int = (ELF_STB_GLOBAL << 4) | ELF_STT_OBJECT,
    symbol_other: int = ELF_STV_DEFAULT,
    symbol_section: int = 3,
    extra_marker_data: bytes = b"",
) -> bytes:
    symbol_strings = b"\0" + PC_MOBILE_ABI_MARKER_SYMBOL + b"\0"
    marker_data = (marker if marker is not None else b"not-an-abi-marker\0") + extra_marker_data
    symbol_size = len(marker) if marker is not None else len(expected_pc_mobile_abi_marker(3))

    string_offset = 64
    symbol_offset = align_up(string_offset + len(symbol_strings), 8)
    symbols = bytearray(24)
    for _ in range(symbol_count):
        symbols.extend(
            struct.pack(
                "<IBBHQQ",
                1,
                symbol_info,
                symbol_other,
                symbol_section,
                0x1000,
                symbol_size,
            )
        )
    marker_offset = align_up(symbol_offset + len(symbols), 8)
    section_offset = align_up(marker_offset + len(marker_data), 8)
    data = bytearray(section_offset + 4 * 64)
    elf_ident = b"\x7fELF\x02\x01\x01" + b"\0" * 9
    struct.pack_into(
        "<16sHHIQQQIHHHHHH",
        data,
        0,
        elf_ident,
        ELF_ET_DYN,
        ELF_AARCH64,
        1,
        0,
        0,
        section_offset,
        0,
        64,
        0,
        0,
        64,
        4,
        0,
    )
    data[string_offset : string_offset + len(symbol_strings)] = symbol_strings
    data[symbol_offset : symbol_offset + len(symbols)] = symbols
    data[marker_offset : marker_offset + len(marker_data)] = marker_data
    struct.pack_into(
        "<IIQQQQIIQQ",
        data,
        section_offset + 64,
        0,
        3,
        0,
        0,
        string_offset,
        len(symbol_strings),
        0,
        0,
        1,
        0,
    )
    struct.pack_into(
        "<IIQQQQIIQQ",
        data,
        section_offset + 128,
        0,
        ELF_SHT_DYNSYM,
        0,
        0,
        symbol_offset,
        len(symbols),
        1,
        1,
        8,
        24,
    )
    struct.pack_into(
        "<IIQQQQIIQQ",
        data,
        section_offset + 192,
        0,
        1,
        2,
        0x1000,
        marker_offset,
        len(marker_data),
        0,
        0,
        1,
        0,
    )
    return bytes(data)


def run_self_tests() -> None:
    expected_marker = expected_pc_mobile_abi_marker(PC_MOBILE_ABI_EXPECTED)
    valid = self_test_pc_mobile_elf(expected_marker)
    assert_pc_mobile_abi_bytes(valid, "self-test-valid", PC_MOBILE_ABI_EXPECTED)

    invalid_cases = (
        (self_test_pc_mobile_elf(None), "expected exactly one pc-mobile ABI marker"),
        (
            self_test_pc_mobile_elf(expected_marker, extra_marker_data=expected_marker),
            "expected exactly one pc-mobile ABI marker",
        ),
        (
            self_test_pc_mobile_elf(PC_MOBILE_ABI_MARKER_PREFIX + b"4\0"),
            "pc-mobile ABI marker",
        ),
        (
            self_test_pc_mobile_elf(PC_MOBILE_ABI_MARKER_PREFIX + b"03\0"),
            "pc-mobile ABI marker size",
        ),
        (
            self_test_pc_mobile_elf(PC_MOBILE_ABI_MARKER_PREFIX + b"\0"),
            "pc-mobile ABI marker size",
        ),
        (
            self_test_pc_mobile_elf(PC_MOBILE_ABI_MARKER_PREFIX + b"12345678901\0"),
            "pc-mobile ABI marker size",
        ),
        (
            self_test_pc_mobile_elf(PC_MOBILE_ABI_MARKER_PREFIX + b"3"),
            "pc-mobile ABI marker size",
        ),
        (
            self_test_pc_mobile_elf(expected_marker, symbol_count=0),
            "expected one exported pc-mobile ABI marker symbol",
        ),
        (
            self_test_pc_mobile_elf(expected_marker, symbol_count=2),
            "expected one exported pc-mobile ABI marker symbol",
        ),
        (
            self_test_pc_mobile_elf(expected_marker, symbol_info=(ELF_STB_GLOBAL << 4) | 2),
            "must be defined GLOBAL OBJECT DEFAULT",
        ),
        (
            self_test_pc_mobile_elf(expected_marker, symbol_other=1),
            "must be defined GLOBAL OBJECT DEFAULT",
        ),
        (
            self_test_pc_mobile_elf(expected_marker, symbol_section=ELF_SHN_UNDEF),
            "must be defined GLOBAL OBJECT DEFAULT",
        ),
    )
    for index, (data, expected_error) in enumerate(invalid_cases, start=1):
        try:
            assert_pc_mobile_abi_bytes(data, f"self-test-invalid-{index}", PC_MOBILE_ABI_EXPECTED)
        except ValueError as error:
            if expected_error not in str(error):
                fail(
                    f"ABI verifier self-test {index} returned unexpected error: {error}"
                )
        else:
            fail(f"ABI verifier self-test {index} unexpectedly passed")
    print("release.asset.selftest.ok check=pc-mobile-abi-marker")


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


def verify_android_manifest(root: Path) -> None:
    relative = "release-assets/android/AndroidManifest.xml"
    path = require_file(root, relative)
    try:
        manifest = ET.parse(path).getroot()
    except ET.ParseError as error:
        fail(f"{path}: invalid Android manifest XML: {error}")
    permissions = {
        element.attrib.get(ANDROID_NAME_ATTRIBUTE, "")
        for element in manifest.iter()
        if element.tag.rsplit("}", 1)[-1] == "uses-permission"
    }
    if "android.permission.RECORD_AUDIO" not in permissions:
        fail(f"{path}: missing android.permission.RECORD_AUDIO uses-permission")
    print(f"release.asset.ok target=android permission=android.permission.RECORD_AUDIO path={relative}")


def verify_android(root: Path) -> None:
    verify_android_manifest(root)
    path = require_file(root, "Libs/pc-mobile/libpc_mobile.so")
    data = path.read_bytes()
    assert_elf_bytes(data, str(path), ELF_AARCH64, ELF_ET_DYN)
    assert_pc_mobile_abi_bytes(data, str(path), PC_MOBILE_ABI_EXPECTED)
    print(
        "release.asset.ok target=android-arm64 format=elf64-aarch64-shared "
        f"abi={PC_MOBILE_ABI_EXPECTED} path=Libs/pc-mobile/libpc_mobile.so"
    )


def verify_desktop(root: Path) -> None:
    pe_assets = (
        ("Libs/pc-capture/pc-capture-win-x64.exe", PE_AMD64),
        ("Libs/pc-capture/pc-capture-win-x86.exe", PE_I386),
        ("Libs/dsp/webrtc-apm.x64.dll", PE_AMD64),
        ("Libs/dsp/webrtc-apm.x86.dll", PE_I386),
    )
    elf_assets = (
        "Libs/pc-capture/pc-capture-linux-x64",
        "Libs/dsp/libwebrtc-apm.so",
    )
    macho_assets = (
        "Libs/dsp/libwebrtc-apm.dylib",
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
    parser.add_argument("--configuration", choices=("Release", "Android"))
    parser.add_argument(
        "--manifest-only",
        action="store_true",
        help="validate only the Android RECORD_AUDIO manifest fragment",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="run deterministic verifier checks without reading release assets",
    )
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        if args.self_test:
            if args.configuration is not None or args.manifest_only:
                fail("--self-test cannot be combined with --configuration or --manifest-only")
            run_self_tests()
        elif args.configuration is None:
            fail("--configuration is required unless --self-test is used")
        elif args.manifest_only:
            if args.configuration != "Android":
                fail("--manifest-only is valid only with --configuration Android")
            verify_android_manifest(root)
        elif args.configuration == "Android":
            verify_android(root)
        else:
            verify_desktop(root)
    except (OSError, ValueError) as error:
        print(f"release.asset.invalid configuration={args.configuration} error={error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
