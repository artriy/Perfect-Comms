#!/usr/bin/env python3
"""Canonically compare two cargo-about HTML license inventories."""

from __future__ import annotations

import argparse
import hashlib
import sys
import tomllib
from collections import Counter
from html.parser import HTMLParser
from pathlib import Path

Relation = tuple[str, str, str, str]
LockedPackage = tuple[str, str, str, str]


class _InventoryParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self._license_depth = 0
        self._license_id = ""
        self._license_name_parts: list[str] | None = None
        self._license_text_parts: list[str] | None = None
        self._in_used_by = False
        self._anchor_parts: list[str] | None = None
        self._dependencies: list[str] = []
        self.relations: Counter[Relation] = Counter()

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        attributes = dict(attrs)
        classes = (attributes.get("class") or "").split()
        if tag == "li":
            if self._license_depth:
                self._license_depth += 1
            elif "license" in classes:
                self._license_depth = 1
                self._license_id = ""
                self._license_name_parts = None
                self._license_text_parts = None
                self._dependencies = []
        if not self._license_depth:
            return
        if tag == "h3":
            self._license_id = attributes.get("id") or ""
            self._license_name_parts = []
        elif tag == "ul" and "license-used-by" in classes:
            self._in_used_by = True
        elif tag == "a" and self._in_used_by:
            self._anchor_parts = []
        elif tag == "pre" and "license-text" in classes:
            self._license_text_parts = []

    def handle_data(self, data: str) -> None:
        if self._anchor_parts is not None:
            self._anchor_parts.append(data)
        elif self._license_text_parts is not None:
            self._license_text_parts.append(data)
        elif self._license_name_parts is not None:
            self._license_name_parts.append(data)

    def handle_endtag(self, tag: str) -> None:
        if not self._license_depth:
            return
        if tag == "a" and self._anchor_parts is not None:
            dependency = " ".join("".join(self._anchor_parts).split())
            if dependency:
                self._dependencies.append(dependency)
            self._anchor_parts = None
        elif tag == "ul" and self._in_used_by:
            self._in_used_by = False
        elif tag == "h3" and self._license_name_parts is not None:
            self._license_name_parts = [" ".join("".join(self._license_name_parts).split())]
        elif tag == "pre" and self._license_text_parts is not None:
            text = "".join(self._license_text_parts).replace("\r\n", "\n").replace("\r", "\n")
            self._license_text_parts = ["\n".join(line.rstrip() for line in text.split("\n")).strip()]
        elif tag == "li":
            self._license_depth -= 1
            if not self._license_depth:
                self._finish_license()

    def _finish_license(self) -> None:
        name = (self._license_name_parts or [""])[0]
        text = (self._license_text_parts or [""])[0]
        if not self._license_id or not name or not text or not self._dependencies:
            raise ValueError("cargo-about license section is incomplete")
        for dependency in self._dependencies:
            self.relations[(self._license_id, name, text, dependency)] += 1


def _parse_inventory(contents: str, source: str) -> Counter[Relation]:
    parser = _InventoryParser()
    parser.feed(contents)
    parser.close()
    if not parser.relations:
        raise ValueError(f"{source} contains no cargo-about license relationships")
    return parser.relations


def _read_inventory(path: Path) -> Counter[Relation]:
    return _parse_inventory(path.read_text(encoding="utf-8"), str(path))


def _parse_lock(contents: str, source: str) -> Counter[LockedPackage]:
    try:
        document = tomllib.loads(contents)
    except tomllib.TOMLDecodeError as error:
        raise ValueError(f"{source} is not valid TOML: {error}") from error
    packages: Counter[LockedPackage] = Counter()
    for package in document.get("package", []):
        name = package.get("name")
        version = package.get("version")
        if not isinstance(name, str) or not isinstance(version, str):
            raise ValueError(f"{source} contains an invalid package entry")
        packages[(
            name,
            version,
            str(package.get("source", "path")),
            str(package.get("checksum", "")),
        )] += 1
    if not packages:
        raise ValueError(f"{source} contains no locked packages")
    return packages


def _read_lock(path: Path) -> Counter[LockedPackage]:
    return _parse_lock(path.read_text(encoding="utf-8"), str(path))


def _dependencies(relations: Counter[Relation]) -> set[str]:
    return {relation[3] for relation in relations}


def _relation_label(relation: Relation) -> str:
    license_id, name, text, dependency = relation
    text_hash = hashlib.sha256(text.encode("utf-8")).hexdigest()[:12]
    return f"{dependency} -> {name} [{license_id}, text={text_hash}]"


def _fixture(dependencies: list[str], *, name: str = "MIT License", text: str = "MIT text") -> str:
    anchors = "".join(
        f'<li><a href="ignored-{index}">{dependency}</a></li>'
        for index, dependency in enumerate(dependencies)
    )
    return (
        '<li class="license"><h3 id="MIT">'
        f'{name}</h3><ul class="license-used-by">{anchors}</ul>'
        f'<pre class="license-text">{text}</pre></li>'
    )


def _run_self_test() -> None:
    first = _fixture(["alpha 1.0.0", "beta 2.0.0"])
    reordered = _fixture(["beta 2.0.0", "alpha 1.0.0"])
    changed_version = _fixture(["alpha 1.0.0", "beta 2.1.0"])
    changed_name = _fixture(["alpha 1.0.0", "beta 2.0.0"], name="Different license")
    changed_text = _fixture(["alpha 1.0.0", "beta 2.0.0"], text="Different text")
    removed_relation = _fixture(["alpha 1.0.0"])

    expected = _parse_inventory(first, "first fixture")
    assert expected == _parse_inventory(reordered, "reordered fixture")
    assert expected != _parse_inventory(changed_version, "version fixture")
    assert expected != _parse_inventory(changed_name, "name fixture")
    assert expected != _parse_inventory(changed_text, "text fixture")
    assert expected != _parse_inventory(removed_relation, "relationship fixture")

    subset = _parse_lock(
        'version = 4\n[[package]]\nname = "alpha"\nversion = "1.0.0"\n',
        "subset lock fixture",
    )
    superset = _parse_lock(
        'version = 4\n[[package]]\nname = "alpha"\nversion = "1.0.0"\n'
        '[[package]]\nname = "beta"\nversion = "2.0.0"\n',
        "superset lock fixture",
    )
    changed = _parse_lock(
        'version = 4\n[[package]]\nname = "alpha"\nversion = "1.0.1"\n',
        "changed lock fixture",
    )
    assert not subset - superset
    assert subset - changed

    print("Native license inventory verifier self-test passed.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Verify dependency versions, accepted-license assignments, and license text in a "
            "checked-in cargo-about report while ignoring HTML ordering and repository links."
        )
    )
    parser.add_argument("committed", nargs="?", type=Path)
    parser.add_argument("generated", nargs="?", type=Path)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--subset-lock", type=Path)
    parser.add_argument("--superset-lock", type=Path)
    args = parser.parse_args()

    if args.self_test:
        _run_self_test()
        return 0
    if args.subset_lock is not None or args.superset_lock is not None:
        if args.subset_lock is None or args.superset_lock is None:
            parser.error("--subset-lock and --superset-lock are required together")
        if args.committed is not None or args.generated is not None:
            parser.error("lock comparison cannot be combined with HTML inventory comparison")
        try:
            subset = _read_lock(args.subset_lock)
            superset = _read_lock(args.superset_lock)
        except (OSError, UnicodeError, ValueError) as error:
            print(f"Native dependency lock validation failed: {error}", file=sys.stderr)
            return 1
        missing = subset - superset
        if missing:
            print(
                f"{args.superset_lock} does not cover every package in {args.subset_lock}:",
                file=sys.stderr,
            )
            for name, version, source, checksum in sorted(missing.elements())[:25]:
                identity = source if source != "path" else "path dependency"
                if checksum:
                    identity += f", checksum={checksum[:12]}"
                print(f"  {name} {version} ({identity})", file=sys.stderr)
            return 1
        print(
            f"Native release lock covers all {sum(subset.values())} desktop locked packages."
        )
        return 0
    if args.committed is None or args.generated is None:
        parser.error("committed and generated HTML paths are required")

    try:
        committed = _read_inventory(args.committed)
        generated = _read_inventory(args.generated)
    except (OSError, UnicodeError, ValueError) as error:
        print(f"Native license inventory validation failed: {error}", file=sys.stderr)
        return 1

    missing = generated - committed
    stale = committed - generated
    if missing or stale:
        for heading, relations in (("Missing or changed relationships", missing), ("Stale relationships", stale)):
            if not relations:
                continue
            print(f"{heading}:", file=sys.stderr)
            expanded = list(relations.elements())
            for relation in sorted(expanded, key=_relation_label)[:25]:
                print(f"  {_relation_label(relation)}", file=sys.stderr)
            if len(expanded) > 25:
                print(f"  ... and {len(expanded) - 25} more", file=sys.stderr)
        return 1

    print(
        "Native license inventory matches "
        f"{len(_dependencies(generated))} locked dependencies and "
        f"{sum(generated.values())} license relationships."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
