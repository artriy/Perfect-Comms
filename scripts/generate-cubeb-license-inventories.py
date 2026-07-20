#!/usr/bin/env python3
"""Generate notices for Cubeb's vendored nested Rust audio backends.

cubeb-sys builds its macOS AudioUnit implementation with a nested Cargo
invocation. That locked dependency graph is intentionally separate from
pc-mobile/Cargo.lock, so it needs its own cargo-about report. Perfect Comms'
Linux build selects Cubeb's C PulseAudio/ALSA backends instead.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
from pathlib import Path


BACKENDS = {
    "coreaudio": ("cubeb-coreaudio-rs", "cubeb-coreaudio-rust-dependencies.html"),
}


def run(command: list[str], *, cwd: Path | None = None) -> str:
    completed = subprocess.run(
        command,
        cwd=cwd,
        check=True,
        encoding="utf-8",
        stdout=subprocess.PIPE,
    )
    return completed.stdout


def cubeb_packages(root: Path) -> tuple[Path, Path]:
    metadata = json.loads(
        run(
            [
                "cargo",
                "metadata",
                "--locked",
                "--format-version",
                "1",
                "--manifest-path",
                str(root / "native/pc-mobile/Cargo.toml"),
            ]
        )
    )
    sys_matches = [
        Path(package["manifest_path"]).parent
        for package in metadata["packages"]
        if package["name"] == "cubeb-sys"
    ]
    cubeb_matches = [
        Path(package["manifest_path"]).parent
        for package in metadata["packages"]
        if package["name"] == "cubeb"
    ]
    if len(sys_matches) != 1:
        raise RuntimeError(
            f"expected one locked cubeb-sys package, found {len(sys_matches)}"
        )
    if len(cubeb_matches) != 1:
        raise RuntimeError(f"expected one locked cubeb package, found {len(cubeb_matches)}")
    expected_sys = (root / "native/vendor/cubeb-sys").resolve()
    expected_api = (root / "native/vendor/cubeb").resolve()
    if sys_matches[0].resolve() != expected_sys:
        raise RuntimeError(
            f"locked cubeb-sys resolved to {sys_matches[0]}, expected {expected_sys}"
        )
    if cubeb_matches[0].resolve() != expected_api:
        raise RuntimeError(
            f"locked cubeb resolved to {cubeb_matches[0]}, expected {expected_api}"
        )
    source = sys_matches[0] / "libcubeb/src"
    if not source.is_dir():
        raise RuntimeError(f"cubeb-sys vendored source is missing: {source}")
    return sys_matches[0], cubeb_matches[0]


def normalized_notice(path: Path) -> str:
    return path.read_text(encoding="utf-8").replace("\r\n", "\n").strip()


def verify_checked_in_notices(root: Path, cubeb_sys: Path, cubeb_api: Path) -> None:
    wrapper = normalized_notice(root / "Libs/cubeb-rs.LICENSE")
    for upstream in (cubeb_sys / "LICENSE", cubeb_api / "LICENSE"):
        if wrapper != normalized_notice(upstream):
            raise RuntimeError(f"checked-in cubeb-rs notice differs from {upstream}")

    libcubeb = cubeb_sys / "libcubeb/LICENSE"
    if normalized_notice(root / "Libs/libcubeb.LICENSE") != normalized_notice(libcubeb):
        raise RuntimeError(f"checked-in libcubeb notice differs from {libcubeb}")

    resampler = (cubeb_sys / "libcubeb/subprojects/speex/resample.c").read_text(
        encoding="utf-8"
    )
    match = re.match(r"/\*(.*?)\*/", resampler, flags=re.DOTALL)
    if match is None:
        raise RuntimeError("Cubeb's vendored Speex resampler has no leading license block")
    # Ignore comment indentation while comparing every word and punctuation
    # mark in the upstream header to the distributable plain-text notice.
    upstream_words = " ".join(match.group(1).split())
    checked_in_words = " ".join(
        normalized_notice(root / "Libs/cubeb-speex-resampler.LICENSE").split()
    )
    if upstream_words != checked_in_words:
        raise RuntimeError("checked-in Cubeb Speex resampler notice differs from resample.c")


def prepare_backend(source: Path, destination: Path) -> Path:
    shutil.copytree(source, destination)
    for template in destination.rglob("Cargo.toml.in"):
        template.rename(template.with_name("Cargo.toml"))
    manifest = destination / "Cargo.toml"
    lock = destination / "Cargo.lock"
    if not manifest.is_file() or not lock.is_file():
        raise RuntimeError(f"Cubeb backend lacks a packaged manifest or lock: {source}")
    return manifest


def generate(root: Path, output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    cubeb_sys, cubeb_api = cubeb_packages(root)
    verify_checked_in_notices(root, cubeb_sys, cubeb_api)
    source_root = cubeb_sys / "libcubeb/src"
    temporary_root = root / "artifacts"
    temporary_root.mkdir(exist_ok=True)
    work = temporary_root / f".cubeb-license-inventories-{os.getpid()}"
    if work.exists():
        raise RuntimeError(f"refusing to reuse existing inventory work directory: {work}")
    work.mkdir()
    try:
        for backend, (directory_name, output_name) in BACKENDS.items():
            source = source_root / directory_name
            if not source.is_dir():
                raise RuntimeError(f"cubeb-sys {backend} backend is missing: {source}")
            manifest = prepare_backend(source, work / directory_name)
            # No --target intentionally: fetch every target-specific package in
            # the backend's packaged lock before cargo-about enters frozen mode.
            run(["cargo", "fetch", "--locked", "--manifest-path", str(manifest)])
            run(
                [
                    "cargo",
                    "about",
                    "generate",
                    "--manifest-path",
                    str(manifest),
                    "--config",
                    str(root / "native/cargo-about.toml"),
                    str(root / "native/cargo-about.hbs"),
                    "--frozen",
                    "--fail",
                    "--output-file",
                    str(output_dir / output_name),
                ]
            )
    finally:
        shutil.rmtree(work)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    generate(args.root.resolve(), args.output_dir.resolve())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
