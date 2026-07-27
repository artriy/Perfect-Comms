#!/usr/bin/env python3
"""Validate that PerfectComms.Api is a small, reference-only NuGet package."""

from __future__ import annotations

import argparse
from pathlib import Path, PurePosixPath
import sys
import xml.etree.ElementTree as ET
import zipfile


PACKAGE_ID = "PerfectComms.Api"
REFERENCE_PATH = "ref/net6.0/PerfectComms.dll"
DOCUMENTATION_PATH = "ref/net6.0/PerfectComms.xml"
README_PATH = "README.md"
MAX_PACKAGE_BYTES = 2 * 1024 * 1024
MAX_REFERENCE_BYTES = 1024 * 1024
FORBIDDEN_PREFIXES = (
    "lib/",
    "runtimes/",
    "native/",
    "tools/",
    "build/",
    "buildtransitive/",
    "content/",
    "contentfiles/",
)


class PackageValidationError(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise PackageValidationError(message)


def child_text(parent: ET.Element, namespace: str, name: str) -> str:
    child = parent.find(f"{namespace}{name}")
    return "" if child is None or child.text is None else child.text.strip()


def validate(package_path: Path, expected_version: str) -> None:
    require(package_path.is_file(), f"package does not exist: {package_path}")
    require(package_path.stat().st_size <= MAX_PACKAGE_BYTES,
            f"package is too large for a reference-only API: {package_path.stat().st_size} bytes")

    with zipfile.ZipFile(package_path) as package:
        names = package.namelist()
        normalized = [PurePosixPath(name).as_posix() for name in names]
        lowered = [name.lower() for name in normalized]

        for required in (REFERENCE_PATH, DOCUMENTATION_PATH, README_PATH):
            require(required.lower() in lowered, f"missing required package entry: {required}")

        for name in lowered:
            require(not name.startswith(FORBIDDEN_PREFIXES),
                    f"runtime/build asset is forbidden in the reference-only package: {name}")

        dll_entries = [name for name in lowered if name.endswith(".dll")]
        require(dll_entries == [REFERENCE_PATH.lower()],
                f"the package must contain only {REFERENCE_PATH}; found {dll_entries}")

        reference_entry = normalized[lowered.index(REFERENCE_PATH.lower())]
        reference = package.read(reference_entry)
        require(len(reference) <= MAX_REFERENCE_BYTES,
                f"reference assembly is unexpectedly large: {len(reference)} bytes")
        require(b"ReferenceAssemblyAttribute" in reference,
                "PerfectComms.dll is not marked as a .NET reference assembly")
        require(b"PerfectComms.Api" in reference,
                "PerfectComms.Api metadata is missing from the reference assembly")

        nuspec_entries = [name for name in normalized if name.lower().endswith(".nuspec")]
        require(len(nuspec_entries) == 1,
                f"expected one nuspec, found {nuspec_entries}")
        nuspec_root = ET.fromstring(package.read(nuspec_entries[0]))
        namespace_uri = ""
        if nuspec_root.tag.startswith("{"):
            namespace_uri = nuspec_root.tag[1:nuspec_root.tag.index("}")]
        namespace = f"{{{namespace_uri}}}" if namespace_uri else ""
        metadata = nuspec_root.find(f"{namespace}metadata")
        require(metadata is not None, "nuspec metadata is missing")
        assert metadata is not None

        require(child_text(metadata, namespace, "id") == PACKAGE_ID,
                f"nuspec id must be {PACKAGE_ID}")
        require(child_text(metadata, namespace, "version") == expected_version,
                f"nuspec version must be {expected_version}")
        require(child_text(metadata, namespace, "readme") == README_PATH,
                f"nuspec readme must be {README_PATH}")

        dependencies = metadata.find(f"{namespace}dependencies")
        dependency_packages = [] if dependencies is None else dependencies.findall(f".//{namespace}dependency")
        require(not dependency_packages,
                "reference-only package must not impose transitive package dependencies")

    print(
        "nuget.package.valid "
        f"path={package_path} version={expected_version} bytes={package_path.stat().st_size}"
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--expected-version", required=True)
    args = parser.parse_args()

    try:
        validate(args.package, args.expected_version)
    except (PackageValidationError, ET.ParseError, zipfile.BadZipFile) as exc:
        print(f"nuget.package.invalid: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
