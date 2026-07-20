#!/usr/bin/env python3
"""Verify that every native graph resolves the reviewed local Cubeb patches."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tomllib
from pathlib import Path


CUBEB_VERSION = "0.36.0"
MANIFESTS = (
    "native/pc-capture/Cargo.toml",
    "native/pc-mobile/Cargo.toml",
    "native/vendor/cubeb/Cargo.toml",
)
LOCKS = (
    "native/pc-capture/Cargo.lock",
    "native/pc-mobile/Cargo.lock",
    "native/vendor/cubeb/Cargo.lock",
)
REQUIRED_VENDOR_LOCKS = (
    "native/vendor/cubeb/Cargo.lock",
    "native/vendor/cubeb-sys/Cargo.lock",
    "native/vendor/cubeb-sys/libcubeb/src/cubeb-coreaudio-rs/Cargo.lock",
    "native/vendor/cubeb-sys/libcubeb/src/cubeb-coreaudio-rs/coreaudio-sys-utils/Cargo.lock",
    "native/vendor/cubeb-sys/libcubeb/src/cubeb-pulse-rs/Cargo.lock",
)


def fail(message: str) -> None:
    raise RuntimeError(message)


def read_toml(path: Path) -> dict:
    try:
        return tomllib.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, tomllib.TOMLDecodeError) as error:
        fail(f"cannot read {path}: {error}")
    return {}


def metadata(root: Path, manifest: Path) -> dict:
    completed = subprocess.run(
        [
            "cargo",
            "metadata",
            "--locked",
            "--format-version",
            "1",
            "--manifest-path",
            str(manifest),
        ],
        cwd=root,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        fail(
            f"cargo metadata failed for {manifest}: "
            f"{completed.stderr.strip()[:1000]}"
        )
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        fail(f"cargo metadata returned invalid JSON for {manifest}: {error}")
    return {}


def assert_local_package(
    packages: list[dict], name: str, expected_manifest: Path, graph: str
) -> None:
    matches = [package for package in packages if package.get("name") == name]
    if len(matches) != 1:
        fail(f"{graph}: expected one {name} package, found {len(matches)}")
    package = matches[0]
    if package.get("version") != CUBEB_VERSION:
        fail(
            f"{graph}: {name} version {package.get('version')!r}, "
            f"expected {CUBEB_VERSION}"
        )
    if package.get("source") is not None:
        fail(f"{graph}: {name} resolved from registry instead of the reviewed local patch")
    actual_manifest = Path(package["manifest_path"]).resolve()
    if actual_manifest != expected_manifest:
        fail(
            f"{graph}: {name} resolved to {actual_manifest}, "
            f"expected {expected_manifest}"
        )


def verify(root: Path) -> None:
    expected = {
        "cubeb": (root / "native/vendor/cubeb/Cargo.toml").resolve(),
        "cubeb-sys": (root / "native/vendor/cubeb-sys/Cargo.toml").resolve(),
    }

    for relative in REQUIRED_VENDOR_LOCKS:
        path = root / relative
        if not path.is_file() or path.stat().st_size == 0:
            fail(f"required vendored Cargo lock is missing or empty: {relative}")

    for relative in MANIFESTS:
        manifest = (root / relative).resolve()
        patch = read_toml(manifest).get("patch", {}).get("crates-io", {})
        if "cubeb-sys" not in patch:
            fail(f"{relative}: missing local cubeb-sys patch")
        if relative != "native/vendor/cubeb/Cargo.toml" and "cubeb" not in patch:
            fail(f"{relative}: missing local cubeb patch")
        graph = metadata(root, manifest)
        for name, expected_manifest in expected.items():
            assert_local_package(graph["packages"], name, expected_manifest, relative)

    for relative in LOCKS:
        lock = read_toml(root / relative)
        packages = lock.get("package", [])
        if any(package.get("name") == "cpal" for package in packages):
            fail(f"{relative}: retired CPAL dependency is still locked")
        for name in expected:
            matches = [package for package in packages if package.get("name") == name]
            if len(matches) != 1:
                fail(f"{relative}: expected one {name} lock entry, found {len(matches)}")
            if matches[0].get("version") != CUBEB_VERSION:
                fail(f"{relative}: unexpected {name} version")
            if "source" in matches[0] or "checksum" in matches[0]:
                fail(f"{relative}: {name} lock entry is not the reviewed path package")

    cmake = (
        root / "native/vendor/cubeb-sys/libcubeb/CMakeLists.txt"
    ).read_text(encoding="utf-8")
    locked_command = "BUILD_COMMAND cargo build --locked --features=gecko-in-tree"
    if cmake.count(locked_command) != 2:
        fail("libcubeb must lock both nested PulseAudio and CoreAudio Cargo builds")
    if "BUILD_COMMAND cargo build --features=gecko-in-tree" in cmake:
        fail("libcubeb still contains an unlocked nested Cargo build")

    bindings = (root / "native/vendor/cubeb-sys/src/context.rs").read_text(
        encoding="utf-8"
    )
    if "pub fn cubeb_get_backend_names() -> cubeb_backend_names;" not in bindings:
        fail("vendored cubeb-sys is missing the compiled-backend inventory binding")

    build_script = (root / "native/vendor/cubeb-sys/build.rs").read_text(
        encoding="utf-8"
    )
    if (
        'env::var("CARGO_CFG_TARGET_FEATURE")' not in build_script
        or '.define("USE_STATIC_MSVC_RUNTIME", "OFF")' not in build_script
        or '"CMAKE_MSVC_RUNTIME_LIBRARY"' not in build_script
    ):
        fail("vendored cubeb-sys no longer matches libcubeb's MSVC CRT to Rust")
    if "cargo:rustc-link-lib=msvcrtd" in build_script:
        fail("vendored cubeb-sys still forces the incompatible debug MSVC CRT")

    windows_toolchain = (root / "native/cmake/windows-static-crt.cmake").read_text(
        encoding="utf-8"
    )
    if '"MultiThreaded"' not in windows_toolchain:
        fail("Windows native dependency toolchain no longer selects the static release MSVC CRT")
    if "MultiThreadedDebug" in windows_toolchain or "<CONFIG:Debug>:Debug" in windows_toolchain:
        fail("Windows native dependency toolchain selects the incompatible debug MSVC CRT")

    print(
        "Cubeb provenance verified: local cubeb + cubeb-sys 0.36.0, "
        "all nested backend locks present, MSVC CRT aligned, no CPAL lock entries."
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    try:
        verify(args.root.resolve())
    except (OSError, RuntimeError, KeyError, TypeError) as error:
        print(f"Cubeb provenance validation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
