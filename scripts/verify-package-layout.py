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
    raise ValueError(f"unsupported package kind: {kind}")


def validate_archive(archive_path: Path, kind: str) -> int:
    with zipfile.ZipFile(archive_path) as archive:
        files: dict[str, int] = {}
        for entry in archive.infolist():
            name = normalize(entry.filename)
            if not name or entry.is_dir():
                continue
            pure = PurePosixPath(name)
            if pure.is_absolute() or ".." in pure.parts:
                raise ValueError(f"unsafe archive path: {entry.filename!r}")
            if name in files:
                raise ValueError(f"duplicate archive entry: {name}")
            files[name] = entry.file_size

    required = required_entries(kind)
    missing = sorted(required - files.keys())
    empty = sorted(name for name in required if files.get(name, 0) == 0)
    if missing:
        raise ValueError(f"missing archive-root entries: {', '.join(missing)}")
    if empty:
        raise ValueError(f"empty required entries: {', '.join(empty)}")

    forbidden = sorted(
        name for name in files if name.endswith(("/MiraAPI.dll", "/Reactor.dll"))
    )
    if forbidden:
        raise ValueError(f"forbidden bundled dependencies: {', '.join(forbidden)}")

    return len(files)


def self_test() -> int:
    # Keep fixtures directly under artifacts: Windows sandbox tokens can lose
    # access to mode-0700 directories created by tempfile.TemporaryDirectory.
    root = Path.cwd() / "artifacts"
    root.mkdir(exist_ok=True)
    fixtures = [
        root / ".package-layout-self-test-Release.zip",
        root / ".package-layout-self-test-Android.zip",
        root / ".package-layout-self-test-unsafe.zip",
    ]
    try:
        for kind in ("Release", "Android"):
            archive_path = root / f".package-layout-self-test-{kind}.zip"
            with zipfile.ZipFile(archive_path, "w") as archive:
                for name in sorted(required_entries(kind)):
                    archive.writestr(name, b"x")
            assert validate_archive(archive_path, kind) == len(required_entries(kind))

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
    parser.add_argument("--kind", choices=("Release", "Android"))
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if args.archive is None or args.kind is None:
        parser.error("archive and --kind are required unless --self-test is used")

    try:
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
