#!/usr/bin/env python3
"""Reject native release payloads with the wrong platform, architecture, or bundle layout."""

from __future__ import annotations

import argparse
import json
import struct
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path


PE_I386 = 0x014C
PE_AMD64 = 0x8664
PE_IMAGE_FILE_DLL = 0x2000
ELF_X86_64 = 62
ELF_AARCH64 = 183
ELF_ET_DYN = 3
ELF_PT_DYNAMIC = 2
ELF_PT_INTERP = 3
ELF_SHT_DYNSYM = 11
ELF_SHT_DYNAMIC = 6
ELF_DT_NEEDED = 1
ELF_STB_GLOBAL = 1
ELF_STT_OBJECT = 1
ELF_STT_FUNC = 2
ELF_STV_DEFAULT = 0
ELF_SHN_UNDEF = 0
MACH_X86_64 = 0x01000007
MACH_ARM64 = 0x0100000C
MACH_MH_DYLIB = 6
MACH_LC_SYMTAB = 0x2
MACH_LC_DYLD_INFO = 0x22
MACH_LC_DYLD_INFO_ONLY = 0x80000022
MACH_LC_DYLD_EXPORTS_TRIE = 0x80000033
MACH_DYLIB_LOAD_COMMANDS = frozenset(
    (0xC, 0x20, 0x80000018, 0x8000001F, 0x80000023)
)
MACH_N_STAB = 0xE0
MACH_N_TYPE = 0x0E
MACH_N_EXT = 0x01
MACH_N_UNDF = 0x00
ANDROID_NAME_ATTRIBUTE = "{http://schemas.android.com/apk/res/android}name"
PC_MOBILE_ABI_EXPECTED = 5
PC_MOBILE_ABI_MARKER_PREFIX = b"PERFECTCOMMS_PC_MOBILE_ABI="
PC_MOBILE_ABI_MARKER_SYMBOL = b"PC_MOBILE_ABI_MARKER"
PION_ABI_EXPECTED = 2
PION_VERSION_EXPECTED = "4.2.17"
PION_CONTRACT_MARKER_PREFIX = b"PERFECTCOMMS_PC_PION_ABI="
PION_CONTRACT_MARKER = (
    PION_CONTRACT_MARKER_PREFIX
    + str(PION_ABI_EXPECTED).encode("ascii")
    + b";PION="
    + PION_VERSION_EXPECTED.encode("ascii")
    + bytes([0])
)
PION_CONTRACT_MARKER_SYMBOL = "PC_PION_CONTRACT_MARKER"
PION_REQUIRED_FUNCTION_EXPORTS = frozenset(
    {
        "pc_pion_abi_version",
        "pc_pion_version",
        "pc_pion_engine_new",
        "pc_pion_engine_close",
        "pc_pion_set_ice_servers",
        "pc_pion_add_peer",
        "pc_pion_remove_peer",
        "pc_pion_set_remote_sdp",
        "pc_pion_add_ice_candidate",
        "pc_pion_restart_ice",
        "pc_pion_send_opus",
        "pc_pion_advance_epoch",
        "pc_pion_poll_control",
        "pc_pion_poll_rtp",
        "pc_pion_get_counters",
    }
)
PION_REQUIRED_EXPORTS = PION_REQUIRED_FUNCTION_EXPORTS | {PION_CONTRACT_MARKER_SYMBOL}
AUDIO_ENGINE_EXPECTED = "cubeb"
CUBEB_VERSION_EXPECTED = "0.36.0"
AUDIO_CONTRACT_PREFIX = b"PERFECTCOMMS_AUDIO_CONTRACT="
AUDIO_CONTRACT = b"PERFECTCOMMS_AUDIO_CONTRACT=1;ENGINE=CUBEB;CUBEB=0.36.0;"
AUDIO_BACKENDS_WINDOWS = frozenset(("wasapi", "winmm"))
AUDIO_BACKENDS_LINUX = frozenset(("pulse", "alsa"))
AUDIO_BACKENDS_MACOS = frozenset(("audiounit", "audiounit-rust"))


def fail(message: str) -> None:
    raise ValueError(message)


def require_file(root: Path, relative: str) -> Path:
    path = root / relative
    if not path.is_file() or path.stat().st_size == 0:
        fail(f"missing or empty release asset: {relative}")
    return path


def assert_audio_contract_bytes(data: bytes, label: str) -> None:
    marker_count = data.count(AUDIO_CONTRACT_PREFIX)
    if marker_count != 1:
        fail(
            f"{label}: expected exactly one PerfectComms audio contract marker, "
            f"found {marker_count}"
        )
    marker_offset = data.find(AUDIO_CONTRACT_PREFIX)
    marker = data[marker_offset : marker_offset + len(AUDIO_CONTRACT)]
    if marker != AUDIO_CONTRACT:
        fail(f"{label}: audio contract marker {marker!r}, expected {AUDIO_CONTRACT!r}")


def assert_audio_contract_executable_bytes(data: bytes, label: str) -> None:
    if len(data) >= 4 and data[:4] in (
        bytes.fromhex("cffaedfe"),
        bytes.fromhex("feedfacf"),
        bytes.fromhex("cafebabe"),
        bytes.fromhex("bebafeca"),
        bytes.fromhex("cafebabf"),
        bytes.fromhex("bfbafeca"),
    ):
        for _, slice_data, slice_label in macho_slices(data, label):
            assert_audio_contract_bytes(slice_data, slice_label)
        return
    assert_audio_contract_bytes(data, label)


def expected_audio_backends(data: bytes, label: str) -> frozenset[str]:
    if data.startswith(b"MZ"):
        return AUDIO_BACKENDS_WINDOWS
    if data.startswith(bytes((0x7F,)) + b"ELF"):
        return AUDIO_BACKENDS_LINUX
    if len(data) >= 4 and data[:4] in (
        bytes.fromhex("cffaedfe"),
        bytes.fromhex("feedfacf"),
        bytes.fromhex("cafebabe"),
        bytes.fromhex("bebafeca"),
        bytes.fromhex("cafebabf"),
        bytes.fromhex("bfbafeca"),
    ):
        return AUDIO_BACKENDS_MACOS
    fail(f"{label}: cannot infer helper platform from executable header")
    return frozenset()


def verify_helper_build_info(path: Path, expected_protocol: int) -> None:
    data = path.read_bytes()
    assert_audio_contract_executable_bytes(data, str(path))
    expected_backends = expected_audio_backends(data, str(path))
    try:
        completed = subprocess.run(
            [str(path), "--build-info"],
            check=False,
            capture_output=True,
            text=True,
            timeout=5,
        )
    except (OSError, subprocess.SubprocessError) as error:
        fail(f"{path}: cannot execute --build-info: {error}")
    if completed.returncode != 0:
        fail(
            f"{path}: --build-info exited {completed.returncode}: "
            f"{completed.stderr.strip()[:300]}"
        )
    try:
        value = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        fail(f"{path}: --build-info did not return one JSON object: {error}")
    if not isinstance(value, dict):
        fail(f"{path}: --build-info root is not an object")
    expected_fields = {
        "schema": 1,
        "protocol": expected_protocol,
        "audio_engine": AUDIO_ENGINE_EXPECTED,
        "cubeb_version": CUBEB_VERSION_EXPECTED,
        "contract": AUDIO_CONTRACT.decode("ascii"),
    }
    for field, expected in expected_fields.items():
        if value.get(field) != expected:
            fail(
                f"{path}: --build-info {field}={value.get(field)!r}, expected {expected!r}"
            )
    backends = value.get("compiled_backends")
    if not isinstance(backends, list) or any(not isinstance(item, str) for item in backends):
        fail(f"{path}: --build-info compiled_backends is not a string array")
    if len(backends) != len(set(backends)):
        fail(f"{path}: --build-info contains duplicate compiled backends")
    if frozenset(backends) != expected_backends:
        fail(
            f"{path}: compiled Cubeb backends {sorted(backends)!r}, "
            f"expected {sorted(expected_backends)!r}"
        )


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


def pe_layout(
    data: bytes, label: str
) -> tuple[int, int, list[tuple[int, int]], list[tuple[int, int, int, int]]]:
    if len(data) < 0x40 or data[:2] != b"MZ":
        fail(f"{label}: expected a PE file (MZ header)")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    if pe_offset + 24 > len(data) or data[pe_offset : pe_offset + 4] != b"PE" + bytes(2):
        fail(f"{label}: invalid PE signature")
    coff = pe_offset + 4
    machine, section_count = struct.unpack_from("<HH", data, coff)
    optional_size, characteristics = struct.unpack_from("<HH", data, coff + 16)
    optional = coff + 20
    if optional_size < 2 or optional + optional_size > len(data):
        fail(f"{label}: truncated PE optional header")
    magic = struct.unpack_from("<H", data, optional)[0]
    if magic == 0x20B:
        directory_count_offset = 108
        directory_offset = 112
    elif magic == 0x10B:
        directory_count_offset = 92
        directory_offset = 96
    else:
        fail(f"{label}: unsupported PE optional-header magic 0x{magic:04x}")
    if directory_count_offset + 4 > optional_size:
        fail(f"{label}: truncated PE data-directory count")
    directory_count = struct.unpack_from("<I", data, optional + directory_count_offset)[0]
    available_directories = max(0, (optional_size - directory_offset) // 8)
    directory_count = min(directory_count, available_directories)
    directories = [
        struct.unpack_from("<II", data, optional + directory_offset + index * 8)
        for index in range(directory_count)
    ]

    section_table = optional + optional_size
    if section_table + section_count * 40 > len(data):
        fail(f"{label}: truncated PE section table")
    sections: list[tuple[int, int, int, int]] = []
    for index in range(section_count):
        entry = section_table + index * 40
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from(
            "<IIII", data, entry + 8
        )
        sections.append((virtual_address, max(virtual_size, raw_size), raw_offset, raw_size))
    return machine, characteristics, directories, sections


def pe_rva_offset(
    data: bytes,
    label: str,
    sections: list[tuple[int, int, int, int]],
    rva: int,
    size: int = 1,
) -> int:
    for virtual_address, virtual_span, raw_offset, raw_size in sections:
        if virtual_address <= rva < virtual_address + virtual_span:
            relative = rva - virtual_address
            if relative + size > raw_size:
                fail(f"{label}: PE RVA 0x{rva:x} points into virtual-only section data")
            offset = raw_offset + relative
            if offset + size > len(data):
                fail(f"{label}: PE RVA 0x{rva:x} resolves past end of file")
            return offset
    fail(f"{label}: PE RVA 0x{rva:x} is outside all sections")
    return 0


def pe_ascii_string(
    data: bytes,
    label: str,
    sections: list[tuple[int, int, int, int]],
    rva: int,
) -> str:
    offset = pe_rva_offset(data, label, sections, rva)
    end = data.find(bytes([0]), offset)
    if end < 0:
        fail(f"{label}: unterminated PE export name")
    try:
        return data[offset:end].decode("ascii", "strict")
    except UnicodeDecodeError:
        fail(f"{label}: PE export name is not ASCII")
    return ""


def assert_exact_pion_marker(data: bytes, label: str) -> int:
    marker_count = data.count(PION_CONTRACT_MARKER_PREFIX)
    if marker_count != 1:
        fail(f"{label}: expected exactly one Pion contract marker, found {marker_count}")
    marker_offset = data.find(PION_CONTRACT_MARKER_PREFIX)
    marker_end = data.find(bytes([0]), marker_offset)
    if marker_end < 0:
        fail(f"{label}: unterminated Pion contract marker")
    marker = data[marker_offset : marker_end + 1]
    if marker != PION_CONTRACT_MARKER:
        fail(f"{label}: Pion contract marker {marker!r}, expected {PION_CONTRACT_MARKER!r}")
    return marker_offset


def pe_exports(
    data: bytes,
    label: str,
    directories: list[tuple[int, int]],
    sections: list[tuple[int, int, int, int]],
) -> tuple[dict[str, list[int]], tuple[int, int]]:
    if not directories or directories[0] == (0, 0):
        fail(f"{label}: PE shared library has no export directory")
    export_rva, export_size = directories[0]
    if export_rva == 0 or export_size < 40:
        fail(f"{label}: PE shared library has an invalid export directory")
    export_offset = pe_rva_offset(data, label, sections, export_rva, 40)
    (
        _,
        _,
        _,
        _,
        _,
        _,
        function_count,
        name_count,
        functions_rva,
        names_rva,
        ordinals_rva,
    ) = struct.unpack_from("<IIHHIIIIIII", data, export_offset)
    if function_count == 0 or name_count == 0:
        fail(f"{label}: PE export directory contains no named exports")
    function_offset = pe_rva_offset(data, label, sections, functions_rva, function_count * 4)
    name_offset = pe_rva_offset(data, label, sections, names_rva, name_count * 4)
    ordinal_offset = pe_rva_offset(data, label, sections, ordinals_rva, name_count * 2)

    exports: dict[str, list[int]] = {}
    for index in range(name_count):
        symbol_name_rva = struct.unpack_from("<I", data, name_offset + index * 4)[0]
        ordinal = struct.unpack_from("<H", data, ordinal_offset + index * 2)[0]
        if ordinal >= function_count:
            fail(f"{label}: PE export ordinal {ordinal} is outside the function table")
        function_rva = struct.unpack_from("<I", data, function_offset + ordinal * 4)[0]
        symbol_name = pe_ascii_string(data, label, sections, symbol_name_rva)
        exports.setdefault(symbol_name, []).append(function_rva)
    return exports, (export_rva, export_size)


def assert_pion_pe_bytes(data: bytes, label: str, expected_machine: int) -> None:
    machine, characteristics, directories, sections = pe_layout(data, label)
    if machine != expected_machine:
        fail(f"{label}: PE machine 0x{machine:04x}, expected 0x{expected_machine:04x}")
    if characteristics & PE_IMAGE_FILE_DLL == 0:
        fail(f"{label}: Pion PE companion is not marked as a DLL")
    marker_offset = assert_exact_pion_marker(data, label)
    exports, (export_rva, export_size) = pe_exports(data, label, directories, sections)
    for symbol_name in sorted(PION_REQUIRED_EXPORTS):
        matches = exports.get(symbol_name, [])
        if len(matches) != 1:
            fail(f"{label}: expected one PE export {symbol_name}, found {len(matches)}")
        symbol_rva = matches[0]
        if symbol_rva == 0:
            fail(f"{label}: PE export {symbol_name} has a null RVA")
        if export_rva <= symbol_rva < export_rva + export_size:
            fail(f"{label}: PE export {symbol_name} is a forwarder")
    marker_rva = exports[PION_CONTRACT_MARKER_SYMBOL][0]
    exported_marker_offset = pe_rva_offset(
        data, label, sections, marker_rva, len(PION_CONTRACT_MARKER)
    )
    if exported_marker_offset != marker_offset:
        fail(f"{label}: exported Pion contract marker does not identify the exact marker")


def assert_pion_pe(path: Path, expected_machine: int) -> None:
    assert_pion_pe_bytes(path.read_bytes(), str(path), expected_machine)


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


def assert_no_external_audio_windows(imports: set[str], label: str) -> None:
    external = sorted(
        name
        for name in imports
        if "CUBEB" in name.upper() or "SPEEXDSP" in name.upper()
    )
    if external:
        fail(
            f"{label}: Windows helper requires an unshipped audio DLL: "
            f"{', '.join(external)}; build vendored Cubeb and its Speex resampler"
        )


def assert_no_private_windows_runtime(path: Path) -> None:
    imports = pe_imports(path)
    assert_no_private_windows_runtime_imports(imports, str(path))
    assert_no_external_audio_windows(imports, str(path))


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


def assert_elf_shared_object_bytes(data: bytes, label: str) -> None:
    if len(data) < 64:
        fail(f"{label}: truncated ELF64 header")
    program_offset = struct.unpack_from("<Q", data, 32)[0]
    program_entry_size = struct.unpack_from("<H", data, 54)[0]
    program_count = struct.unpack_from("<H", data, 56)[0]
    if program_offset == 0 or program_count == 0 or program_entry_size < 56:
        fail(f"{label}: ELF shared library has no usable program-header table")
    if program_offset + program_entry_size * program_count > len(data):
        fail(f"{label}: truncated ELF64 program-header table")
    program_types = {
        struct.unpack_from("<I", data, program_offset + index * program_entry_size)[0]
        for index in range(program_count)
    }
    if ELF_PT_INTERP in program_types:
        fail(f"{label}: ELF Pion companion contains PT_INTERP and is an executable")
    if ELF_PT_DYNAMIC not in program_types:
        fail(f"{label}: ELF Pion companion has no PT_DYNAMIC shared-library segment")


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


def elf64_needed_libraries(data: bytes, label: str) -> set[str]:
    sections = elf64_sections(data, label)
    needed: set[str] = set()
    dynamic_tables = 0
    for section in sections:
        if section[1] != ELF_SHT_DYNAMIC:
            continue
        dynamic_tables += 1
        string_section_index = section[6]
        if string_section_index >= len(sections):
            fail(f"{label}: ELF64 dynamic table has an invalid string-table link")
        strings = section_bytes(data, sections[string_section_index], label)
        entries = section_bytes(data, section, label)
        entry_size = section[9]
        if entry_size < 16 or len(entries) % entry_size != 0:
            fail(f"{label}: malformed ELF64 dynamic table")
        for offset in range(0, len(entries), entry_size):
            tag, value = struct.unpack_from("<QQ", entries, offset)
            if tag != ELF_DT_NEEDED:
                continue
            if value >= len(strings):
                fail(f"{label}: ELF64 DT_NEEDED has an invalid string offset")
            name_end = strings.find(bytes([0]), value)
            if name_end < 0:
                fail(f"{label}: unterminated ELF64 DT_NEEDED name")
            try:
                needed.add(strings[value:name_end].decode("ascii", "strict"))
            except UnicodeDecodeError:
                fail(f"{label}: ELF64 DT_NEEDED name is not ASCII")
    if dynamic_tables == 0:
        fail(f"{label}: ELF executable has no dynamic dependency table")
    return needed


def assert_linux_cubeb_runtime_bytes(data: bytes, label: str) -> None:
    needed = elf64_needed_libraries(data, label)
    forbidden = sorted(
        name
        for name in needed
        if name.lower().startswith("libpulse")
        or name.lower().startswith("libasound.so")
        or name.lower().startswith("libcubeb.so")
        or name.lower().startswith("libspeexdsp.so")
    )
    if forbidden:
        fail(
            f"{label}: Linux helper has a forbidden direct audio dependency: "
            f"{', '.join(forbidden)}; build vendored Cubeb with its C PulseAudio/ALSA "
            "backends, lazy library loading, and the vendored Speex resampler"
        )


def elf64_dynamic_symbols(
    data: bytes, label: str
) -> tuple[list[tuple[int, ...]], dict[str, list[tuple[int, int, int, int, int]]]]:
    sections = elf64_sections(data, label)
    exported: dict[str, list[tuple[int, int, int, int, int]]] = {}
    dynamic_symbol_tables = 0
    for section in sections:
        if section[1] != ELF_SHT_DYNSYM:
            continue
        dynamic_symbol_tables += 1
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
            name_end = strings.find(bytes([0]), name_offset)
            if name_end < 0:
                fail(f"{label}: unterminated ELF64 dynamic symbol name")
            try:
                name = strings[name_offset:name_end].decode("ascii", "strict")
            except UnicodeDecodeError:
                continue
            if name:
                exported.setdefault(name, []).append(
                    (info, other, section_index, value, size)
                )
    if dynamic_symbol_tables == 0:
        fail(f"{label}: ELF shared library has no dynamic symbol table")
    return sections, exported


def assert_pion_elf_bytes(data: bytes, label: str, expected_machine: int) -> None:
    assert_elf_bytes(data, label, expected_machine, ELF_ET_DYN)
    assert_elf_shared_object_bytes(data, label)
    marker_offset = assert_exact_pion_marker(data, label)
    sections, exports = elf64_dynamic_symbols(data, label)
    for symbol_name in sorted(PION_REQUIRED_FUNCTION_EXPORTS):
        matches = exports.get(symbol_name, [])
        if len(matches) != 1:
            fail(f"{label}: expected one ELF export {symbol_name}, found {len(matches)}")
        info, other, section_index, _, _ = matches[0]
        if (
            info >> 4 != ELF_STB_GLOBAL
            or info & 0x0F != ELF_STT_FUNC
            or other & 0x03 != ELF_STV_DEFAULT
            or section_index == ELF_SHN_UNDEF
        ):
            fail(f"{label}: ELF export {symbol_name} must be defined GLOBAL FUNC DEFAULT")

    marker_matches = exports.get(PION_CONTRACT_MARKER_SYMBOL, [])
    if len(marker_matches) != 1:
        fail(
            f"{label}: expected one ELF export {PION_CONTRACT_MARKER_SYMBOL}, "
            f"found {len(marker_matches)}"
        )
    info, other, section_index, value, size = marker_matches[0]
    if (
        info >> 4 != ELF_STB_GLOBAL
        or info & 0x0F != ELF_STT_OBJECT
        or other & 0x03 != ELF_STV_DEFAULT
        or section_index == ELF_SHN_UNDEF
    ):
        fail(f"{label}: Pion contract marker must be defined GLOBAL OBJECT DEFAULT")
    if size != len(PION_CONTRACT_MARKER):
        fail(f"{label}: Pion contract marker size {size}, expected {len(PION_CONTRACT_MARKER)}")
    if section_index >= len(sections):
        fail(f"{label}: Pion contract marker references an invalid ELF64 section")
    marker_section = sections[section_index]
    section_address = marker_section[3]
    section_file_offset = marker_section[4]
    section_size = marker_section[5]
    if value < section_address or value - section_address + size > section_size:
        fail(f"{label}: Pion contract marker lies outside its ELF64 section")
    exported_marker_offset = section_file_offset + value - section_address
    if exported_marker_offset != marker_offset:
        fail(f"{label}: exported Pion contract marker does not identify the exact marker")


def assert_pion_elf(path: Path, expected_machine: int) -> None:
    assert_pion_elf_bytes(path.read_bytes(), str(path), expected_machine)


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


def self_test_elf_needed(libraries: tuple[str, ...]) -> bytes:
    strings = bytearray(1)
    offsets: list[int] = []
    for library in libraries:
        offsets.append(len(strings))
        strings.extend(library.encode("ascii") + bytes([0]))
    dynamic = bytearray()
    for offset in offsets:
        dynamic.extend(struct.pack("<QQ", ELF_DT_NEEDED, offset))
    dynamic.extend(struct.pack("<QQ", 0, 0))

    dynamic_offset = 0x80
    strings_offset = align_up(dynamic_offset + len(dynamic), 8)
    section_offset = align_up(strings_offset + len(strings), 8)
    data = bytearray(section_offset + 3 * 64)
    data[:16] = bytes((0x7F, 0x45, 0x4C, 0x46, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0))
    struct.pack_into(
        "<HHIQQQIHHHHHH",
        data,
        16,
        ELF_ET_DYN,
        ELF_X86_64,
        1,
        0,
        0,
        section_offset,
        0,
        64,
        0,
        0,
        64,
        3,
        0,
    )
    data[dynamic_offset : dynamic_offset + len(dynamic)] = dynamic
    data[strings_offset : strings_offset + len(strings)] = strings
    struct.pack_into(
        "<IIQQQQIIQQ",
        data,
        section_offset + 64,
        0,
        ELF_SHT_DYNAMIC,
        0,
        0,
        dynamic_offset,
        len(dynamic),
        2,
        0,
        8,
        16,
    )
    struct.pack_into(
        "<IIQQQQIIQQ",
        data,
        section_offset + 128,
        0,
        3,
        0,
        0,
        strings_offset,
        len(strings),
        0,
        0,
        1,
        0,
    )
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
    symbol_size = (
        len(marker) if marker is not None else len(expected_pc_mobile_abi_marker(PC_MOBILE_ABI_EXPECTED))
    )

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


def self_test_pion_pe(
    machine: int,
    *,
    exports: frozenset[str] = PION_REQUIRED_EXPORTS,
    marker: bytes = PION_CONTRACT_MARKER,
    dll: bool = True,
) -> bytes:
    pe_offset = 0x80
    optional_size = 0xF0 if machine == PE_AMD64 else 0xE0
    optional_magic = 0x20B if machine == PE_AMD64 else 0x10B
    directory_offset = 112 if machine == PE_AMD64 else 96
    section_virtual_address = 0x1000
    section_raw_offset = 0x200
    section_raw_size = 0x4000
    data = bytearray(section_raw_offset + section_raw_size)

    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, pe_offset)
    data[pe_offset : pe_offset + 4] = b"PE" + bytes(2)
    coff = pe_offset + 4
    characteristics = 0x0022 | (PE_IMAGE_FILE_DLL if dll else 0)
    struct.pack_into(
        "<HHIIIHH", data, coff, machine, 1, 0, 0, 0, optional_size, characteristics
    )
    optional = coff + 20
    struct.pack_into("<H", data, optional, optional_magic)
    struct.pack_into("<I", data, optional + directory_offset - 4, 16)

    section_table = optional + optional_size
    struct.pack_into(
        "<8sIIIIIIHHI",
        data,
        section_table,
        b".rdata",
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

    names = sorted(exports)
    count = len(names)
    export_offset = section_raw_offset
    functions_offset = align_up(export_offset + 40, 4)
    names_offset = functions_offset + count * 4
    ordinals_offset = names_offset + count * 4
    cursor = align_up(ordinals_offset + count * 2, 4)
    dll_name_offset = cursor
    dll_name = b"pc-pion-self-test.dll" + bytes([0])
    data[cursor : cursor + len(dll_name)] = dll_name
    cursor += len(dll_name)
    symbol_name_offsets: list[int] = []
    for name in names:
        encoded = name.encode("ascii") + bytes([0])
        symbol_name_offsets.append(cursor)
        data[cursor : cursor + len(encoded)] = encoded
        cursor += len(encoded)
    export_size = cursor - export_offset
    marker_offset = align_up(cursor, 16)
    data[marker_offset : marker_offset + len(marker)] = marker
    function_cursor = align_up(marker_offset + len(marker), 16)

    function_rvas: list[int] = []
    for name in names:
        if name == PION_CONTRACT_MARKER_SYMBOL:
            target_offset = marker_offset
        else:
            target_offset = function_cursor
            data[function_cursor] = 0xC3
            function_cursor += 1
        function_rvas.append(
            section_virtual_address + target_offset - section_raw_offset
        )
    export_rva = section_virtual_address + export_offset - section_raw_offset
    struct.pack_into("<II", data, optional + directory_offset, export_rva, export_size)
    struct.pack_into(
        "<IIHHIIIIIII",
        data,
        export_offset,
        0,
        0,
        0,
        0,
        section_virtual_address + dll_name_offset - section_raw_offset,
        1,
        count,
        count,
        section_virtual_address + functions_offset - section_raw_offset,
        section_virtual_address + names_offset - section_raw_offset,
        section_virtual_address + ordinals_offset - section_raw_offset,
    )
    for index, (function_rva, symbol_name_offset) in enumerate(
        zip(function_rvas, symbol_name_offsets)
    ):
        struct.pack_into("<I", data, functions_offset + index * 4, function_rva)
        struct.pack_into(
            "<I",
            data,
            names_offset + index * 4,
            section_virtual_address + symbol_name_offset - section_raw_offset,
        )
        struct.pack_into("<H", data, ordinals_offset + index * 2, index)
    return bytes(data)


def self_test_pion_elf(
    machine: int,
    *,
    exports: frozenset[str] = PION_REQUIRED_EXPORTS,
    marker: bytes = PION_CONTRACT_MARKER,
    elf_type: int = ELF_ET_DYN,
    marker_info: int = (ELF_STB_GLOBAL << 4) | ELF_STT_OBJECT,
    program_type: int = ELF_PT_DYNAMIC,
) -> bytes:
    names = sorted(exports)
    strings = bytearray(1)
    string_offsets: dict[str, int] = {}
    for name in names:
        string_offsets[name] = len(strings)
        strings.extend(name.encode("ascii") + bytes([0]))

    symbols = bytearray(24)
    text_value = 0x1000
    for name in names:
        if name == PION_CONTRACT_MARKER_SYMBOL:
            info = marker_info
            section_index = 4
            value = 0x2000
            size = len(marker)
        else:
            info = (ELF_STB_GLOBAL << 4) | ELF_STT_FUNC
            section_index = 3
            value = text_value
            size = 1
            text_value += 1
        symbols.extend(
            struct.pack(
                "<IBBHQQ",
                string_offsets[name],
                info,
                ELF_STV_DEFAULT,
                section_index,
                value,
                size,
            )
        )

    program_offset = 64
    program_entry_size = 56
    string_offset = align_up(program_offset + program_entry_size, 8)
    symbol_offset = align_up(string_offset + len(strings), 8)
    text_offset = align_up(symbol_offset + len(symbols), 8)
    text = bytes(max(1, len(names)))
    marker_offset = align_up(text_offset + len(text), 8)
    section_offset = align_up(marker_offset + len(marker), 8)
    data = bytearray(section_offset + 5 * 64)
    elf_ident = bytes.fromhex("7f454c46020101") + bytes(9)
    struct.pack_into(
        "<16sHHIQQQIHHHHHH",
        data,
        0,
        elf_ident,
        elf_type,
        machine,
        1,
        0,
        program_offset,
        section_offset,
        0,
        64,
        program_entry_size,
        1,
        64,
        5,
        0,
    )
    struct.pack_into(
        "<IIQQQQQQ",
        data,
        program_offset,
        program_type,
        4,
        symbol_offset,
        0x3000,
        0x3000,
        len(symbols),
        len(symbols),
        8,
    )
    data[string_offset : string_offset + len(strings)] = strings
    data[symbol_offset : symbol_offset + len(symbols)] = symbols
    data[text_offset : text_offset + len(text)] = text
    data[marker_offset : marker_offset + len(marker)] = marker
    struct.pack_into(
        "<IIQQQQIIQQ",
        data,
        section_offset + 64,
        0,
        3,
        0,
        0,
        string_offset,
        len(strings),
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
        6,
        0x1000,
        text_offset,
        len(text),
        0,
        0,
        1,
        0,
    )
    struct.pack_into(
        "<IIQQQQIIQQ",
        data,
        section_offset + 256,
        0,
        1,
        2,
        0x2000,
        marker_offset,
        len(marker),
        0,
        0,
        1,
        0,
    )
    return bytes(data)


def encode_uleb128(value: int) -> bytes:
    encoded = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        if value:
            encoded.append(byte | 0x80)
        else:
            encoded.append(byte)
            return bytes(encoded)


def self_test_macho_loads(libraries: tuple[str, ...]) -> bytes:
    commands = bytearray()
    for library in libraries:
        name = library.encode("utf-8") + bytes([0])
        command_size = align_up(24 + len(name), 8)
        command = bytearray(command_size)
        struct.pack_into("<IIIIII", command, 0, 0xC, command_size, 24, 0, 0, 0)
        command[24 : 24 + len(name)] = name
        commands.extend(command)
    data = bytearray(32 + len(commands))
    struct.pack_into(
        "<IIIIIIII",
        data,
        0,
        0xFEEDFACF,
        MACH_X86_64,
        0,
        2,
        len(libraries),
        len(commands),
        0,
        0,
    )
    data[32:] = commands
    return bytes(data)


def self_test_macho_export_trie(exports: frozenset[str]) -> bytes:
    edges = [b"_" + name.encode("ascii") for name in sorted(exports)]
    child = bytes((1, 0, 0))
    root = b""
    for _ in range(8):
        child_offset = len(root)
        candidate = bytearray((0, len(edges)))
        for edge in edges:
            candidate.extend(edge + bytes([0]))
            candidate.extend(encode_uleb128(child_offset))
            child_offset += len(child)
        if len(candidate) == len(root):
            root = bytes(candidate)
            break
        root = bytes(candidate)
    child_offset = len(root)
    root_data = bytearray((0, len(edges)))
    children = bytearray()
    for edge in edges:
        root_data.extend(edge + bytes([0]))
        root_data.extend(encode_uleb128(child_offset + len(children)))
        children.extend(child)
    return bytes(root_data + children)


def self_test_pion_macho_slice(
    architecture: int,
    *,
    exports: frozenset[str] = PION_REQUIRED_EXPORTS,
    marker: bytes = PION_CONTRACT_MARKER,
    file_type: int = MACH_MH_DYLIB,
    use_export_trie: bool = False,
) -> bytes:
    if use_export_trie:
        trie = self_test_macho_export_trie(exports)
        command_size = 16
        marker_offset = align_up(32 + command_size, 8)
        trie_offset = align_up(marker_offset + len(marker), 8)
        data = bytearray(trie_offset + len(trie))
        struct.pack_into(
            "<IIIIIIII",
            data,
            0,
            0xFEEDFACF,
            architecture,
            0,
            file_type,
            1,
            command_size,
            0,
            0,
        )
        struct.pack_into(
            "<IIII",
            data,
            32,
            MACH_LC_DYLD_EXPORTS_TRIE,
            command_size,
            trie_offset,
            len(trie),
        )
        data[marker_offset : marker_offset + len(marker)] = marker
        data[trie_offset : trie_offset + len(trie)] = trie
        return bytes(data)

    names = sorted(exports)
    strings = bytearray(1)
    string_offsets: list[int] = []
    for name in names:
        string_offsets.append(len(strings))
        strings.extend(b"_" + name.encode("ascii") + bytes([0]))
    command_size = 24
    marker_offset = align_up(32 + command_size, 8)
    symbol_offset = align_up(marker_offset + len(marker), 8)
    string_offset = symbol_offset + len(names) * 16
    data = bytearray(string_offset + len(strings))
    struct.pack_into(
        "<IIIIIIII",
        data,
        0,
        0xFEEDFACF,
        architecture,
        0,
        file_type,
        1,
        command_size,
        0,
        0,
    )
    struct.pack_into(
        "<IIIIII",
        data,
        32,
        MACH_LC_SYMTAB,
        command_size,
        symbol_offset,
        len(names),
        string_offset,
        len(strings),
    )
    data[marker_offset : marker_offset + len(marker)] = marker
    data[string_offset : string_offset + len(strings)] = strings
    for index, string_index in enumerate(string_offsets):
        struct.pack_into(
            "<IBBHQ",
            data,
            symbol_offset + index * 16,
            string_index,
            MACH_N_EXT | 0x0E,
            1,
            0,
            marker_offset if names[index] == PION_CONTRACT_MARKER_SYMBOL else 0x1000 + index,
        )
    return bytes(data)


def self_test_pion_fat_macho(x86_64: bytes, arm64: bytes) -> bytes:
    x86_offset = 0x1000
    arm_offset = align_up(x86_offset + len(x86_64), 0x1000)
    data = bytearray(arm_offset + len(arm64))
    data[:4] = bytes.fromhex("cafebabe")
    struct.pack_into(">I", data, 4, 2)
    struct.pack_into(
        ">IIIII", data, 8, MACH_X86_64, 0, x86_offset, len(x86_64), 12
    )
    struct.pack_into(
        ">IIIII", data, 28, MACH_ARM64, 0, arm_offset, len(arm64), 12
    )
    data[x86_offset : x86_offset + len(x86_64)] = x86_64
    data[arm_offset : arm_offset + len(arm64)] = arm64
    return bytes(data)


def expect_self_test_failure(
    check: object, data: bytes, label: str, expected_error: str, *args: object
) -> None:
    try:
        check(data, label, *args)  # type: ignore[operator]
    except ValueError as error:
        if expected_error not in str(error):
            fail(f"{label} returned unexpected error: {error}")
    else:
        fail(f"{label} unexpectedly passed")


def run_self_tests() -> None:
    assert_audio_contract_bytes(b"prefix" + AUDIO_CONTRACT + b"suffix", "audio-contract-valid")
    expect_self_test_failure(
        assert_audio_contract_bytes,
        b"no marker",
        "audio-contract-missing",
        "expected exactly one PerfectComms audio contract marker",
    )
    expect_self_test_failure(
        assert_audio_contract_bytes,
        AUDIO_CONTRACT + b"padding" + AUDIO_CONTRACT,
        "audio-contract-duplicate",
        "expected exactly one PerfectComms audio contract marker",
    )
    expect_self_test_failure(
        assert_audio_contract_bytes,
        b"PERFECTCOMMS_AUDIO_CONTRACT=1;ENGINE=CPAL;CPAL=0.15.3;",
        "audio-contract-retired-cpal",
        "audio contract marker",
    )

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
            self_test_pc_mobile_elf(PC_MOBILE_ABI_MARKER_PREFIX + b"3\0"),
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
            self_test_pc_mobile_elf(PC_MOBILE_ABI_MARKER_PREFIX + b"4"),
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
    assert_no_external_audio_windows(clean_imports, "pe-self-test")
    try:
        assert_no_external_audio_windows(
            {"KERNEL32.DLL", "CUBEB.DLL"}, "pe-cubeb-self-test-invalid"
        )
    except ValueError as error:
        if "unshipped audio DLL" not in str(error):
            fail(f"Windows Cubeb import self-test returned unexpected error: {error}")
    else:
        fail("Windows Cubeb import self-test unexpectedly passed")
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

    cubeb_linux = self_test_elf_needed(("libstdc++.so.6", "libc.so.6", "libdl.so.2"))
    assert_linux_cubeb_runtime_bytes(cubeb_linux, "elf-cubeb-self-test")
    expect_self_test_failure(
        assert_linux_cubeb_runtime_bytes,
        self_test_elf_needed(("libpulse.so.0", "libc.so.6")),
        "elf-cubeb-self-test-pulse-linked",
        "forbidden direct audio dependency: libpulse.so.0",
    )
    expect_self_test_failure(
        assert_linux_cubeb_runtime_bytes,
        self_test_elf_needed(("libasound.so.2", "libc.so.6")),
        "elf-cubeb-self-test-alsa-linked",
        "forbidden direct audio dependency: libasound.so.2",
    )
    expect_self_test_failure(
        assert_linux_cubeb_runtime_bytes,
        self_test_elf_needed(("libcubeb.so.0", "libc.so.6")),
        "elf-cubeb-self-test-shared",
        "forbidden direct audio dependency: libcubeb.so.0",
    )
    expect_self_test_failure(
        assert_linux_cubeb_runtime_bytes,
        self_test_elf_needed(("libspeexdsp.so.1", "libc.so.6")),
        "elf-cubeb-self-test-system-speex",
        "forbidden direct audio dependency: libspeexdsp.so.1",
    )

    mac_system_libraries = (
        "/System/Library/Frameworks/AudioUnit.framework/Versions/A/AudioUnit",
        "/usr/lib/libc++.1.dylib",
    )
    mac_loads = self_test_macho_loads(mac_system_libraries)
    if macho_needed_libraries(mac_loads, "macho-load-self-test") != set(
        mac_system_libraries
    ):
        fail("Mach-O dependency parser self-test returned the wrong libraries")
    assert_no_external_audio_macho_bytes(mac_loads, "macho-load-self-test")
    expect_self_test_failure(
        assert_no_external_audio_macho_bytes,
        self_test_macho_loads(("@rpath/libcubeb.dylib",)),
        "macho-cubeb-self-test-invalid",
        "unshipped audio dylib",
    )
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

    for machine in (PE_AMD64, PE_I386):
        assert_pion_pe_bytes(
            self_test_pion_pe(machine), f"pion-pe-valid-{machine:04x}", machine
        )
    missing_restart = frozenset(PION_REQUIRED_EXPORTS - {"pc_pion_restart_ice"})
    expect_self_test_failure(
        assert_pion_pe_bytes,
        self_test_pion_pe(PE_AMD64, dll=False),
        "pion-pe-not-dll",
        "not marked as a DLL",
        PE_AMD64,
    )
    expect_self_test_failure(
        assert_pion_pe_bytes,
        self_test_pion_pe(PE_AMD64, exports=missing_restart),
        "pion-pe-missing-export",
        "expected one PE export pc_pion_restart_ice",
        PE_AMD64,
    )
    expect_self_test_failure(
        assert_pion_pe_bytes,
        self_test_pion_pe(
            PE_AMD64,
            marker=PION_CONTRACT_MARKER_PREFIX + b"1;PION=4.2.16" + bytes([0]),
        ),
        "pion-pe-wrong-marker",
        "Pion contract marker",
        PE_AMD64,
    )

    for machine in (ELF_X86_64, ELF_AARCH64):
        assert_pion_elf_bytes(
            self_test_pion_elf(machine), f"pion-elf-valid-{machine}", machine
        )
    expect_self_test_failure(
        assert_pion_elf_bytes,
        self_test_pion_elf(ELF_X86_64, elf_type=2),
        "pion-elf-not-shared",
        "ELF type 2",
        ELF_X86_64,
    )
    expect_self_test_failure(
        assert_pion_elf_bytes,
        self_test_pion_elf(ELF_X86_64, program_type=ELF_PT_INTERP),
        "pion-elf-executable-interpreter",
        "contains PT_INTERP",
        ELF_X86_64,
    )
    expect_self_test_failure(
        assert_pion_elf_bytes,
        self_test_pion_elf(ELF_X86_64, exports=missing_restart),
        "pion-elf-missing-export",
        "expected one ELF export pc_pion_restart_ice",
        ELF_X86_64,
    )
    expect_self_test_failure(
        assert_pion_elf_bytes,
        self_test_pion_elf(
            ELF_X86_64,
            marker=PION_CONTRACT_MARKER_PREFIX + b"1;PION=4.2.18" + bytes([0]),
        ),
        "pion-elf-wrong-marker",
        "Pion contract marker",
        ELF_X86_64,
    )
    expect_self_test_failure(
        assert_pion_elf_bytes,
        self_test_pion_elf(
            ELF_X86_64,
            marker_info=(ELF_STB_GLOBAL << 4) | ELF_STT_FUNC,
        ),
        "pion-elf-marker-not-object",
        "must be defined GLOBAL OBJECT DEFAULT",
        ELF_X86_64,
    )

    valid_x86_macho = self_test_pion_macho_slice(MACH_X86_64)
    valid_arm_macho = self_test_pion_macho_slice(MACH_ARM64, use_export_trie=True)
    assert_pion_macho_bytes(
        self_test_pion_fat_macho(valid_x86_macho, valid_arm_macho),
        "pion-macho-valid",
    )
    expect_self_test_failure(
        assert_pion_macho_bytes,
        self_test_pion_fat_macho(
            self_test_pion_macho_slice(MACH_X86_64, file_type=2),
            valid_arm_macho,
        ),
        "pion-macho-not-dylib",
        "not a dylib",
    )
    expect_self_test_failure(
        assert_pion_macho_bytes,
        self_test_pion_fat_macho(
            valid_x86_macho,
            self_test_pion_macho_slice(
                MACH_ARM64, exports=missing_restart, use_export_trie=True
            ),
        ),
        "pion-macho-missing-arm-export",
        "missing Mach-O exports: pc_pion_restart_ice",
    )
    expect_self_test_failure(
        assert_pion_macho_bytes,
        self_test_pion_fat_macho(
            valid_x86_macho,
            self_test_pion_macho_slice(
                MACH_ARM64,
                marker=PION_CONTRACT_MARKER_PREFIX + b"1;PION=4.2.17" + bytes([0]),
                use_export_trie=True,
            ),
        ),
        "pion-macho-wrong-arm-marker",
        "Pion contract marker",
    )
    expect_self_test_failure(
        assert_pion_macho_bytes,
        valid_x86_macho,
        "pion-macho-not-universal",
        "expected exactly x86_64+arm64",
    )
    print("release.asset.selftest.ok check=pc-mobile-abi-marker")
    print("release.asset.selftest.ok check=windows-pe-import-parser-and-self-contained-runtime")
    print("release.asset.selftest.ok check=pion-shared-library-contract")


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


def macho_thin_header(data: bytes, label: str) -> tuple[str, int, int, int, int]:
    if len(data) < 32:
        fail(f"{label}: truncated 64-bit Mach-O header")
    if data[:4] == bytes.fromhex("cffaedfe"):
        endian = "<"
    elif data[:4] == bytes.fromhex("feedfacf"):
        endian = ">"
    else:
        fail(f"{label}: expected a 64-bit Mach-O slice")
    _, architecture, _, file_type, command_count, command_size, _, _ = struct.unpack_from(
        f"{endian}IIIIIIII", data, 0
    )
    if 32 + command_size > len(data):
        fail(f"{label}: Mach-O load commands extend past end of slice")
    return endian, architecture, file_type, command_count, command_size


def macho_slices(data: bytes, label: str) -> list[tuple[int, bytes, str]]:
    if len(data) < 8:
        fail(f"{label}: truncated Mach-O file")
    magic = data[:4]
    if magic not in (bytes.fromhex("cafebabe"), bytes.fromhex("cafebabf")):
        _, architecture, _, _, _ = macho_thin_header(data, label)
        return [(architecture, data, label)]

    architecture_count = struct.unpack_from(">I", data, 4)[0]
    is_64_bit_fat = magic == bytes.fromhex("cafebabf")
    entry_size = 32 if is_64_bit_fat else 20
    if architecture_count == 0 or 8 + architecture_count * entry_size > len(data):
        fail(f"{label}: invalid universal Mach-O header")
    slices: list[tuple[int, bytes, str]] = []
    architectures: set[int] = set()
    ranges: list[tuple[int, int]] = []
    for index in range(architecture_count):
        entry_offset = 8 + index * entry_size
        if is_64_bit_fat:
            architecture, _, slice_offset, slice_size, _, _ = struct.unpack_from(
                ">IIQQII", data, entry_offset
            )
        else:
            architecture, _, slice_offset, slice_size, _ = struct.unpack_from(
                ">IIIII", data, entry_offset
            )
        if architecture in architectures:
            fail(f"{label}: duplicate Mach-O architecture 0x{architecture:08x}")
        if slice_size == 0 or slice_offset + slice_size > len(data):
            fail(f"{label}: Mach-O slice {index} extends past end of file")
        for previous_start, previous_end in ranges:
            if slice_offset < previous_end and previous_start < slice_offset + slice_size:
                fail(f"{label}: overlapping Mach-O slices")
        slice_data = data[slice_offset : slice_offset + slice_size]
        slice_label = f"{label}[arch=0x{architecture:08x}]"
        _, thin_architecture, _, _, _ = macho_thin_header(slice_data, slice_label)
        if thin_architecture != architecture:
            fail(
                f"{slice_label}: fat architecture does not match thin header "
                f"0x{thin_architecture:08x}"
            )
        architectures.add(architecture)
        ranges.append((slice_offset, slice_offset + slice_size))
        slices.append((architecture, slice_data, slice_label))
    return slices


def macho_needed_libraries(data: bytes, label: str) -> set[str]:
    libraries: set[str] = set()
    for _, slice_data, slice_label in macho_slices(data, label):
        endian, _, _, command_count, command_size = macho_thin_header(
            slice_data, slice_label
        )
        command_end = 32 + command_size
        command_offset = 32
        for _ in range(command_count):
            if command_offset + 8 > command_end:
                fail(f"{slice_label}: truncated Mach-O load command")
            command, size = struct.unpack_from(
                f"{endian}II", slice_data, command_offset
            )
            if size < 8 or command_offset + size > command_end:
                fail(f"{slice_label}: invalid Mach-O load command size")
            if command in MACH_DYLIB_LOAD_COMMANDS:
                if size < 24:
                    fail(f"{slice_label}: truncated Mach-O dylib load command")
                name_offset = struct.unpack_from(
                    f"{endian}I", slice_data, command_offset + 8
                )[0]
                if name_offset < 24 or name_offset >= size:
                    fail(f"{slice_label}: invalid Mach-O dylib name offset")
                name_start = command_offset + name_offset
                name_end = slice_data.find(
                    bytes([0]), name_start, command_offset + size
                )
                if name_end < 0:
                    fail(f"{slice_label}: unterminated Mach-O dylib name")
                try:
                    libraries.add(slice_data[name_start:name_end].decode("utf-8", "strict"))
                except UnicodeDecodeError:
                    fail(f"{slice_label}: Mach-O dylib name is not UTF-8")
            command_offset += size
        if command_offset != command_end:
            fail(f"{slice_label}: Mach-O load-command sizes do not match header")
    return libraries


def assert_no_external_audio_macho_bytes(data: bytes, label: str) -> None:
    external = sorted(
        library
        for library in macho_needed_libraries(data, label)
        if "libcubeb" in library.lower() or "libspeexdsp" in library.lower()
    )
    if external:
        fail(
            f"{label}: macOS helper requires an unshipped audio dylib: "
            f"{', '.join(external)}; build vendored Cubeb and its Speex resampler"
        )


def read_uleb128(data: bytes, offset: int, end: int, label: str) -> tuple[int, int]:
    result = 0
    shift = 0
    for _ in range(10):
        if offset >= end:
            fail(f"{label}: truncated ULEB128 in Mach-O export trie")
        byte = data[offset]
        offset += 1
        result |= (byte & 0x7F) << shift
        if byte & 0x80 == 0:
            return result, offset
        shift += 7
    fail(f"{label}: oversized ULEB128 in Mach-O export trie")
    return 0, offset


def macho_export_trie_symbols(data: bytes, offset: int, size: int, label: str) -> set[str]:
    if size == 0 or offset + size > len(data):
        fail(f"{label}: invalid Mach-O export trie range")
    trie_end = offset + size
    exports: set[str] = set()
    active_nodes: set[int] = set()
    visited_nodes = 0

    def visit(node_relative_offset: int, prefix: bytes) -> None:
        nonlocal visited_nodes
        if node_relative_offset >= size:
            fail(f"{label}: Mach-O export trie child offset is out of range")
        if node_relative_offset in active_nodes:
            fail(f"{label}: Mach-O export trie contains a cycle")
        visited_nodes += 1
        if visited_nodes > size:
            fail(f"{label}: Mach-O export trie node count is invalid")
        active_nodes.add(node_relative_offset)
        cursor = offset + node_relative_offset
        terminal_size, cursor = read_uleb128(data, cursor, trie_end, label)
        terminal_end = cursor + terminal_size
        if terminal_end > trie_end:
            fail(f"{label}: Mach-O export trie terminal extends past trie")
        if terminal_size:
            try:
                symbol_name = prefix.decode("ascii", "strict")
            except UnicodeDecodeError:
                symbol_name = ""
            if symbol_name:
                exports.add(symbol_name[1:] if symbol_name.startswith("_") else symbol_name)
        cursor = terminal_end
        if cursor >= trie_end:
            fail(f"{label}: Mach-O export trie node has no child count")
        child_count = data[cursor]
        cursor += 1
        for _ in range(child_count):
            edge_end = data.find(bytes([0]), cursor, trie_end)
            if edge_end < 0:
                fail(f"{label}: unterminated Mach-O export trie edge")
            edge = data[cursor:edge_end]
            cursor = edge_end + 1
            child_offset, cursor = read_uleb128(data, cursor, trie_end, label)
            visit(child_offset, prefix + edge)
        active_nodes.remove(node_relative_offset)

    visit(0, b"")
    return exports


def macho_exported_symbols(data: bytes, label: str) -> tuple[int, int, set[str]]:
    endian, architecture, file_type, command_count, command_size = macho_thin_header(
        data, label
    )
    command_end = 32 + command_size
    command_offset = 32
    symtab: tuple[int, int, int, int] | None = None
    export_tries: list[tuple[int, int]] = []
    for _ in range(command_count):
        if command_offset + 8 > command_end:
            fail(f"{label}: truncated Mach-O load command")
        command, size = struct.unpack_from(f"{endian}II", data, command_offset)
        if size < 8 or command_offset + size > command_end:
            fail(f"{label}: invalid Mach-O load command size")
        if command == MACH_LC_SYMTAB:
            if size < 24:
                fail(f"{label}: truncated Mach-O LC_SYMTAB")
            if symtab is not None:
                fail(f"{label}: duplicate Mach-O LC_SYMTAB")
            symtab = struct.unpack_from(f"{endian}IIII", data, command_offset + 8)
        elif command in (MACH_LC_DYLD_INFO, MACH_LC_DYLD_INFO_ONLY):
            if size < 48:
                fail(f"{label}: truncated Mach-O LC_DYLD_INFO")
            export_offset, export_size = struct.unpack_from(
                f"{endian}II", data, command_offset + 40
            )
            if export_size:
                export_tries.append((export_offset, export_size))
        elif command == MACH_LC_DYLD_EXPORTS_TRIE:
            if size < 16:
                fail(f"{label}: truncated Mach-O LC_DYLD_EXPORTS_TRIE")
            export_offset, export_size = struct.unpack_from(
                f"{endian}II", data, command_offset + 8
            )
            if export_size:
                export_tries.append((export_offset, export_size))
        command_offset += size
    if command_offset != command_end:
        fail(f"{label}: Mach-O load-command sizes do not match header")

    exports: set[str] = set()
    found_symbol_source = False
    if symtab is not None:
        symbol_offset, symbol_count, string_offset, string_size = symtab
        if symbol_offset + symbol_count * 16 > len(data):
            fail(f"{label}: Mach-O symbol table extends past end of slice")
        if string_size == 0 or string_offset + string_size > len(data):
            fail(f"{label}: Mach-O string table extends past end of slice")
        found_symbol_source = True
        string_end = string_offset + string_size
        for index in range(symbol_count):
            name_offset, symbol_type, _, _, _ = struct.unpack_from(
                f"{endian}IBBHQ", data, symbol_offset + index * 16
            )
            if (
                symbol_type & MACH_N_STAB
                or symbol_type & MACH_N_EXT == 0
                or symbol_type & MACH_N_TYPE == MACH_N_UNDF
            ):
                continue
            if name_offset >= string_size:
                fail(f"{label}: Mach-O symbol name offset is outside string table")
            name_start = string_offset + name_offset
            name_end = data.find(bytes([0]), name_start, string_end)
            if name_end < 0:
                fail(f"{label}: unterminated Mach-O symbol name")
            try:
                name = data[name_start:name_end].decode("ascii", "strict")
            except UnicodeDecodeError:
                continue
            if name:
                exports.add(name[1:] if name.startswith("_") else name)
    for export_offset, export_size in set(export_tries):
        found_symbol_source = True
        exports.update(macho_export_trie_symbols(data, export_offset, export_size, label))
    if not found_symbol_source:
        fail(f"{label}: Mach-O dylib has no symbol table or export trie")
    return architecture, file_type, exports


def assert_pion_macho_bytes(data: bytes, label: str) -> None:
    slices = macho_slices(data, label)
    architectures = {architecture for architecture, _, _ in slices}
    required_architectures = {MACH_X86_64, MACH_ARM64}
    if architectures != required_architectures:
        formatted = ",".join(f"0x{value:08x}" for value in sorted(architectures))
        fail(f"{label}: expected exactly x86_64+arm64 Mach-O slices, found [{formatted}]")
    for architecture, slice_data, slice_label in slices:
        assert_exact_pion_marker(slice_data, slice_label)
        parsed_architecture, file_type, exports = macho_exported_symbols(
            slice_data, slice_label
        )
        if parsed_architecture != architecture:
            fail(f"{slice_label}: inconsistent Mach-O architecture")
        if file_type != MACH_MH_DYLIB:
            fail(f"{slice_label}: Pion Mach-O companion is not a dylib")
        missing = sorted(PION_REQUIRED_EXPORTS - exports)
        if missing:
            fail(f"{slice_label}: missing Mach-O exports: {', '.join(missing)}")


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
        "PerfectCommsAudio.app/Contents/MacOS/libpc-pion.dylib",
    )
    try:
        with zipfile.ZipFile(path) as archive:
            entries = {name.lstrip("./").replace("\\", "/"): name for name in archive.namelist()}
            missing = [name for name in required_entries if name not in entries]
            if missing:
                fail(f"{path}: mac bundle is missing: {', '.join(missing)}")
            for relative in required_entries[1:3]:
                data = archive.read(entries[relative])
                assert_universal_macho_bytes(data, f"{path}!{relative}")
                if relative.endswith("/PerfectCommsAudio"):
                    assert_audio_contract_executable_bytes(data, f"{path}!{relative}")
                    assert_no_external_audio_macho_bytes(
                        data, f"{path}!{relative}"
                    )
            pion_relative = required_entries[3]
            assert_pion_macho_bytes(
                archive.read(entries[pion_relative]), f"{path}!{pion_relative}"
            )
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
    pion_path = require_file(root, "Libs/pion/libpc-pion.android-arm64.so")
    assert_pion_elf(pion_path, ELF_AARCH64)
    print(
        "release.asset.ok target=android-arm64 format=elf64-aarch64-shared "
        f"transport=pion abi={PION_ABI_EXPECTED} pion={PION_VERSION_EXPECTED} "
        "path=Libs/pion/libpc-pion.android-arm64.so"
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
        if relative.startswith("Libs/pc-capture/"):
            assert_audio_contract_executable_bytes(path.read_bytes(), str(path))
        print(f"release.asset.ok format=pe machine=0x{machine:04x} path={relative}")
    for relative in elf_assets:
        path = require_file(root, relative)
        assert_elf(path, ELF_X86_64)
        if relative == "Libs/pc-capture/pc-capture-linux-x64":
            assert_linux_cubeb_runtime_bytes(path.read_bytes(), str(path))
            assert_audio_contract_executable_bytes(path.read_bytes(), str(path))
        print(f"release.asset.ok format=elf64 machine=x86_64 path={relative}")
    pion_pe_assets = (
        ("Libs/pion/pc-pion.x64.dll", PE_AMD64),
        ("Libs/pion/pc-pion.x86.dll", PE_I386),
    )
    for relative, machine in pion_pe_assets:
        path = require_file(root, relative)
        assert_pion_pe(path, machine)
        assert_no_private_windows_runtime(path)
        print(
            f"release.asset.ok format=pe-dll machine=0x{machine:04x} "
            f"transport=pion abi={PION_ABI_EXPECTED} pion={PION_VERSION_EXPECTED} "
            f"path={relative}"
        )
    pion_elf_relative = "Libs/pion/libpc-pion.linux-x64.so"
    assert_pion_elf(require_file(root, pion_elf_relative), ELF_X86_64)
    print(
        "release.asset.ok format=elf64-shared machine=x86_64 "
        f"transport=pion abi={PION_ABI_EXPECTED} pion={PION_VERSION_EXPECTED} "
        f"path={pion_elf_relative}"
    )
    mac_zip = require_file(root, "Libs/pc-capture/pc-capture-mac.zip")
    assert_mac_bundle(mac_zip)
    print(
        "release.asset.ok target=macos format=app-zip architectures=x86_64,arm64 "
        f"transport=pion abi={PION_ABI_EXPECTED} pion={PION_VERSION_EXPECTED} "
        "path=Libs/pc-capture/pc-capture-mac.zip"
    )


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
    parser.add_argument(
        "--elf-cubeb-runtime",
        type=Path,
        help=(
            "verify one Linux helper uses lazy system-audio loading and has no "
            "external Cubeb or SpeexDSP dependency"
        ),
    )
    parser.add_argument(
        "--mac-bundle",
        type=Path,
        help="verify one final universal macOS helper bundle and its static audio contract",
    )
    parser.add_argument(
        "--helper-build-info",
        type=Path,
        help="execute one host-native helper and verify its linked Cubeb backend inventory",
    )
    parser.add_argument(
        "--expected-protocol",
        type=int,
        default=14,
        help="protocol expected from --helper-build-info (default: 14)",
    )
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        if args.self_test:
            if (
                args.configuration is not None
                or args.manifest_only
                or args.pe_no_private_runtime
                or args.elf_cubeb_runtime
                or args.mac_bundle
                or args.helper_build_info
            ):
                fail("--self-test cannot be combined with other verification modes")
            run_self_tests()
        elif args.pe_no_private_runtime is not None:
            if (
                args.configuration is not None
                or args.manifest_only
                or args.elf_cubeb_runtime
                or args.mac_bundle
                or args.helper_build_info
            ):
                fail("--pe-no-private-runtime cannot be combined with other verification modes")
            helper = args.pe_no_private_runtime.resolve()
            if not helper.is_file() or helper.stat().st_size == 0:
                fail(f"missing or empty Windows PE asset: {helper}")
            assert_no_private_windows_runtime(helper)
            print(f"release.asset.ok target=windows compiler_runtime=self-contained path={helper}")
        elif args.elf_cubeb_runtime is not None:
            if (
                args.configuration is not None
                or args.manifest_only
                or args.mac_bundle
                or args.helper_build_info
            ):
                fail("--elf-cubeb-runtime cannot be combined with other verification modes")
            helper = args.elf_cubeb_runtime.resolve()
            if not helper.is_file() or helper.stat().st_size == 0:
                fail(f"missing or empty Linux helper asset: {helper}")
            data = helper.read_bytes()
            assert_elf_bytes(data, str(helper), ELF_X86_64)
            assert_linux_cubeb_runtime_bytes(data, str(helper))
            assert_audio_contract_executable_bytes(data, str(helper))
            print(
                "release.asset.ok target=linux cubeb=static "
                "system_audio=runtime-dlopen "
                f"path={helper}"
            )
        elif args.mac_bundle is not None:
            if (
                args.configuration is not None
                or args.manifest_only
                or args.helper_build_info
            ):
                fail("--mac-bundle cannot be combined with other verification modes")
            bundle = args.mac_bundle.resolve()
            if not bundle.is_file() or bundle.stat().st_size == 0:
                fail(f"missing or empty macOS helper bundle: {bundle}")
            assert_mac_bundle(bundle)
            print(
                "release.asset.ok target=macos architectures=x86_64,arm64 "
                f"cubeb=static path={bundle}"
            )
        elif args.helper_build_info is not None:
            if args.configuration is not None or args.manifest_only:
                fail("--helper-build-info cannot be combined with other verification modes")
            helper = args.helper_build_info.resolve()
            if not helper.is_file() or helper.stat().st_size == 0:
                fail(f"missing or empty native helper asset: {helper}")
            verify_helper_build_info(helper, args.expected_protocol)
            print(
                "release.asset.ok audio_engine=cubeb "
                f"cubeb={CUBEB_VERSION_EXPECTED} protocol={args.expected_protocol} path={helper}"
            )
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
