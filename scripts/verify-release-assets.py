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


def pe_imports(path: Path) -> set[str]:
    return pe_imports_bytes(path.read_bytes(), str(path))


def pe_imports_bytes(data: bytes, label: str) -> set[str]:
    if len(data) < 0x40 or data[:2] != b"MZ":
        fail(f"{label}: expected a PE file (MZ header)")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if pe_offset + 24 > len(data) or data[pe_offset : pe_offset + 4] != b"PE\0\0":
        fail(f"{label}: invalid PE signature")
    coff = pe_offset + 4
    section_count = struct.unpack_from("<H", data, coff + 2)[0]
    optional_size = struct.unpack_from("<H", data, coff + 16)[0]
    optional = coff + 20
    if optional_size < 2 or optional + optional_size > len(data):
        fail(f"{label}: truncated PE optional header")
    magic = struct.unpack_from("<H", data, optional)[0]
    directory_offset = optional + (112 if magic == 0x20B else 96 if magic == 0x10B else 0)
    if directory_offset == optional:
        fail(f"{label}: unsupported PE optional-header magic 0x{magic:04x}")
    if directory_offset + 16 > optional + optional_size:
        fail(f"{label}: PE optional header has no complete import directory")
    import_rva, _ = struct.unpack_from("<II", data, directory_offset + 8)
    if import_rva == 0:
        return set()

    sections: list[tuple[int, int, int]] = []
    section_table = optional + optional_size
    if section_table + section_count * 40 > len(data):
        fail(f"{label}: truncated PE section table")
    for index in range(section_count):
        entry = section_table + index * 40
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from(
            "<IIII", data, entry + 8
        )
        sections.append((virtual_address, max(virtual_size, raw_size), raw_offset))

    def rva_offset(rva: int) -> int:
        for virtual_address, size, raw_offset in sections:
            if virtual_address <= rva < virtual_address + size:
                offset = raw_offset + rva - virtual_address
                if offset >= len(data):
                    fail(f"{label}: PE RVA 0x{rva:x} resolves past end of file")
                return offset
        fail(f"{label}: PE RVA 0x{rva:x} is outside all sections")
        return 0

    imports: set[str] = set()
    descriptor = rva_offset(import_rva)
    while True:
        if descriptor + 20 > len(data):
            fail(f"{label}: unterminated PE import descriptor table")
        values = struct.unpack_from("<IIIII", data, descriptor)
        if not any(values):
            break
        name_offset = rva_offset(values[3])
        name_end = data.find(b"\0", name_offset)
        if name_end < 0:
            fail(f"{label}: unterminated PE import name")
        try:
            name = data[name_offset:name_end].decode("ascii", "strict").upper()
        except UnicodeDecodeError:
            fail(f"{label}: PE import name is not ASCII")
        imports.add(name)
        descriptor += 20
    return imports


def assert_no_private_windows_runtime_imports(imports: set[str], label: str) -> None:
    forbidden = sorted(
        name
        for name in imports
        if (
            name.startswith(
                (
                    "VCRUNTIME",
                    "MSVCP",
                    "CONCRT",
                    "VCOMP",
                    "MFC",
                    "LIBWINPTHREAD",
                    "LIBGCC",
                    "LIBSTDC++",
                    "LIBSSP",
                    "LIBATOMIC",
                    "LIBGOMP",
                    "LIBQUADMATH",
                    "LIBGFORTRAN",
                )
            )
            or (name.startswith("MSVCR") and name != "MSVCRT.DLL")
        )
    )
    if forbidden:
        fail(
            f"{label}: native binary requires an unbundled compiler runtime: "
            f"{', '.join(forbidden)}; build Windows release assets with static runtimes"
        )


def assert_no_private_windows_runtime(path: Path) -> None:
    assert_no_private_windows_runtime_imports(pe_imports(path), str(path))


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


def self_test_pe(imports: tuple[str, ...], machine: int) -> bytes:
    pe_offset = 0x80
    optional_size = 0xF0 if machine == PE_AMD64 else 0xE0
    optional_magic = 0x20B if machine == PE_AMD64 else 0x10B
    directory_offset = 112 if machine == PE_AMD64 else 96
    section_virtual_address = 0x1000
    section_raw_offset = 0x200
    section_raw_size = 0x400
    data = bytearray(section_raw_offset + section_raw_size)

    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, pe_offset)
    data[pe_offset : pe_offset + 4] = b"PE\0\0"
    coff = pe_offset + 4
    struct.pack_into("<HHIIIHH", data, coff, machine, 1, 0, 0, 0, optional_size, 0x0022)
    optional = coff + 20
    struct.pack_into("<H", data, optional, optional_magic)
    struct.pack_into("<I", data, optional + directory_offset - 4, 16)
    descriptor_size = (len(imports) + 1) * 20
    struct.pack_into(
        "<II",
        data,
        optional + directory_offset + 8,
        section_virtual_address,
        descriptor_size,
    )

    section_table = optional + optional_size
    struct.pack_into(
        "<8sIIIIIIHHI",
        data,
        section_table,
        b".rdata\0\0",
        section_raw_size,
        section_virtual_address,
        section_raw_size,
        section_raw_offset,
        0,
        0,
        0,
        0,
        0x40000040,
    )

    name_offset = section_raw_offset + align_up(descriptor_size, 16)
    for index, import_name in enumerate(imports):
        encoded = import_name.encode("ascii") + b"\0"
        if name_offset + len(encoded) > len(data):
            fail("PE self-test fixture import names exceed the test section")
        name_rva = section_virtual_address + name_offset - section_raw_offset
        struct.pack_into(
            "<IIIII", data, section_raw_offset + index * 20, 0, 0, 0, name_rva, 0
        )
        data[name_offset : name_offset + len(encoded)] = encoded
        name_offset += len(encoded)
    return bytes(data)


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
    clean_imports = pe_imports_bytes(
        self_test_pe(("KERNEL32.DLL", "WS2_32.DLL", "MSVCRT.DLL"), PE_AMD64),
        "pe-self-test",
    )
    if clean_imports != {"KERNEL32.DLL", "WS2_32.DLL", "MSVCRT.DLL"}:
        fail(f"PE import parser self-test returned {sorted(clean_imports)!r}")
    assert_no_private_windows_runtime_imports(clean_imports, "pe-self-test")
    try:
        assert_no_private_windows_runtime_imports(
            pe_imports_bytes(
                self_test_pe(("KERNEL32.DLL", "VCRUNTIME140.DLL"), PE_AMD64),
                "pe-self-test-invalid",
            ),
            "pe-self-test-invalid",
        )
    except ValueError as error:
        if "unbundled compiler runtime" not in str(error):
            fail(f"PE import policy self-test returned unexpected error: {error}")
    else:
        fail("PE import policy self-test unexpectedly passed")
    try:
        assert_no_private_windows_runtime_imports(
            pe_imports_bytes(
                self_test_pe(("KERNEL32.DLL", "LIBWINPTHREAD-1.DLL"), PE_I386),
                "pe-self-test-mingw-invalid",
            ),
            "pe-self-test-mingw-invalid",
        )
    except ValueError as error:
        if "unbundled compiler runtime" not in str(error):
            fail(f"MinGW import policy self-test returned unexpected error: {error}")
    else:
        fail("MinGW import policy self-test unexpectedly passed")
    for runtime_name in (
        "MSVCR120.DLL",
        "CONCRT140.DLL",
        "VCOMP140.DLL",
        "LIBATOMIC-1.DLL",
        "LIBGOMP-1.DLL",
        "LIBQUADMATH-0.DLL",
        "LIBGFORTRAN-5.DLL",
    ):
        try:
            assert_no_private_windows_runtime_imports(
                {"KERNEL32.DLL", runtime_name},
                "pe-self-test-extra-runtime",
            )
        except ValueError as error:
            if "unbundled compiler runtime" not in str(error):
                fail(f"Additional PE runtime policy self-test returned unexpected error: {error}")
        else:
            fail(f"Additional PE runtime policy self-test accepted {runtime_name}")
    print("release.asset.selftest.ok check=pc-mobile-abi-marker")
    print("release.asset.selftest.ok check=windows-pe-import-parser-and-self-contained-runtime")


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
    for relative, machine in pe_assets:
        path = require_file(root, relative)
        assert_pe(path, machine)
        assert_no_private_windows_runtime(path)
        print(f"release.asset.ok format=pe machine=0x{machine:04x} path={relative}")
    for relative in elf_assets:
        assert_elf(require_file(root, relative), ELF_X86_64)
        print(f"release.asset.ok format=elf64 machine=x86_64 path={relative}")
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
    parser.add_argument(
        "--pe-no-private-runtime",
        type=Path,
        help="verify one Windows PE asset has no unbundled MSVC or MinGW runtime dependency",
    )
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        if args.self_test:
            if args.configuration is not None or args.manifest_only or args.pe_no_private_runtime:
                fail("--self-test cannot be combined with other verification modes")
            run_self_tests()
        elif args.pe_no_private_runtime is not None:
            if args.configuration is not None or args.manifest_only:
                fail("--pe-no-private-runtime cannot be combined with other verification modes")
            helper = args.pe_no_private_runtime.resolve()
            if not helper.is_file() or helper.stat().st_size == 0:
                fail(f"missing or empty Windows PE asset: {helper}")
            assert_no_private_windows_runtime(helper)
            print(f"release.asset.ok target=windows compiler_runtime=self-contained path={helper}")
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
