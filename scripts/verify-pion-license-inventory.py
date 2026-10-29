#!/usr/bin/env python3
"""Regenerate and compare the licenses linked into the Pion c-shared library."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path


PINNED_GO_VERSION = "go1.26.2"
PINNED_PION_VERSION = "v4.2.17"
LICENSE_NAMES = ("LICENSE", "LICENSE.txt", "LICENSE.md", "LICENCE", "COPYING")


def normalize_text(text: str) -> str:
    return "\n".join(line.rstrip() for line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n")).strip()


def json_stream(payload: str) -> list[dict[str, object]]:
    decoder = json.JSONDecoder()
    values: list[dict[str, object]] = []
    offset = 0
    while offset < len(payload):
        while offset < len(payload) and payload[offset].isspace():
            offset += 1
        if offset >= len(payload):
            break
        value, offset = decoder.raw_decode(payload, offset)
        values.append(value)
    return values


def run_go(go: str, module: Path, *args: str) -> str:
    environment = os.environ.copy()
    environment["GOTOOLCHAIN"] = "local"
    return subprocess.check_output(
        [go, *args], cwd=module, env=environment, text=True, encoding="utf-8"
    ).strip()


def find_license(directory: Path) -> Path:
    for name in LICENSE_NAMES:
        candidate = directory / name
        if candidate.is_file() and candidate.stat().st_size:
            return candidate
    raise ValueError(f"no supported license file found in {directory}")


def generate(root: Path, go: str) -> str:
    module = root / "native" / "pc-pion"
    go_version = run_go(go, module, "env", "GOVERSION")
    if go_version != PINNED_GO_VERSION:
        raise ValueError(f"expected {PINNED_GO_VERSION}, found {go_version}")
    go_root = Path(run_go(go, module, "env", "GOROOT"))
    packages = json_stream(
        run_go(go, module, "list", "-buildvcs=false", "-mod=readonly", "-deps", "-json", ".")
    )
    modules: dict[str, tuple[str, Path]] = {}
    for package in packages:
        module_value = package.get("Module")
        if not isinstance(module_value, dict):
            continue
        path = module_value.get("Path")
        version = module_value.get("Version")
        directory = module_value.get("Dir")
        if not all(isinstance(value, str) and value for value in (path, version, directory)):
            continue
        modules[path] = (version, Path(directory))

    pion = modules.get("github.com/pion/webrtc/v4")
    if pion is None or pion[0] != PINNED_PION_VERSION:
        actual = pion[0] if pion else "missing"
        raise ValueError(f"expected Pion {PINNED_PION_VERSION}, found {actual}")

    sections = [
        "# Pion/Go Native Dependency License Inventory",
        "",
        "Generated from the packages linked into `native/pc-pion` with the pinned "
        f"{PINNED_GO_VERSION} toolchain. Test-only Go modules are intentionally excluded.",
    ]
    grouped: dict[str, list[str]] = {}

    def add_notice(label: str, license_path: Path) -> None:
        text = normalize_text(license_path.read_text(encoding="utf-8"))
        grouped.setdefault(text, []).append(label)

    add_notice(f"Go toolchain {PINNED_GO_VERSION}", find_license(go_root))
    for path in sorted(modules):
        version, directory = modules[path]
        add_notice(f"{path} {version}", find_license(directory))

    for index, (license_text, labels) in enumerate(
        sorted(grouped.items(), key=lambda item: sorted(item[1])[0]), start=1
    ):
        sections.extend(("", f"## License notice {index}", "", "Used by:"))
        sections.extend(f"- {label}" for label in sorted(labels))
        sections.extend(("", license_text))
    return "\n".join(sections).strip() + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--go", default="go")
    parser.add_argument(
        "--inventory",
        type=Path,
        default=Path("Libs/pion-go-dependencies.txt"),
    )
    args = parser.parse_args()
    root = args.root.resolve()
    inventory = args.inventory
    if not inventory.is_absolute():
        inventory = root / inventory
    try:
        expected = generate(root, args.go)
        actual = inventory.read_text(encoding="utf-8")
        if normalize_text(actual) != normalize_text(expected):
            raise ValueError(
                f"{inventory} does not match the locked Pion/Go dependency graph"
            )
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        print(f"Pion license inventory validation failed: {error}", file=sys.stderr)
        return 1
    print(f"Pion license inventory matches {PINNED_PION_VERSION} and {PINNED_GO_VERSION}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
