#!/usr/bin/env python3
"""Ten real-helper direct-P2P mesh smoke for the production Pion transport.

This manual release diagnostic stages the shipped Windows x64/x86 helpers and their
matching native libraries, creates the complete 10-client mesh (45 connections / 90
peer endpoints), verifies decoded pre-route audio and non-relay selected paths, restarts
ICE on every connection, and churns client 9 at a new generation.

Raw SDP, ICE candidates, candidate-pair identifiers, and network addresses are routed
only in memory. Helper stderr stays in the private staging directory, which is verified
removed after the run. The stable summary contains only aggregate counts and timing.
"""

from __future__ import annotations

import argparse
import collections
import concurrent.futures
import contextlib
import hashlib
import json
import os
import queue
import re
import secrets
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Iterable


TYPE_CONTROL = 0x01
MAX_FRAME_BYTES = 1024 * 1024
CLIENT_COUNT = 10
LINK_COUNT = CLIENT_COUNT * (CLIENT_COUNT - 1) // 2
ENDPOINT_COUNT = LINK_COUNT * 2
LEVEL_THRESHOLD = 0.001
PION_VERSION = "v4.2.17"

# Keep this list byte-for-byte aligned with PerfectCommsVoiceBackend.DefaultIceServers.
MANAGED_STUN_URLS = (
    "stun:stun.l.google.com:19302",
    "stun:stun1.l.google.com:19302",
    "stun:stun.cloudflare.com:3478",
    "stun:global.stun.twilio.com:3478",
)
MANAGED_ICE_SERVERS = [{"urls": [url]} for url in MANAGED_STUN_URLS]


def source_protocol_version(root: Path) -> int:
    managed = (root / "Comms" / "SidecarVoiceClient.cs").read_text(encoding="utf-8")
    native = (root / "native" / "pc-capture" / "src" / "proto.rs").read_text(
        encoding="utf-8"
    )
    managed_match = re.search(r"public const int Proto\s*=\s*(\d+)", managed)
    native_match = re.search(r"PROTO_VERSION:\s*u32\s*=\s*(\d+)", native)
    if managed_match is None or native_match is None:
        raise RuntimeError("protocol-contract-unreadable")
    managed_version = int(managed_match.group(1))
    native_version = int(native_match.group(1))
    if managed_version != native_version:
        raise RuntimeError("protocol-contract-mismatch")
    return managed_version


def recv_exact(sock: socket.socket, length: int) -> bytes:
    chunks: list[bytes] = []
    remaining = length
    while remaining:
        chunk = sock.recv(remaining)
        if not chunk:
            raise ConnectionError("control-connection-closed")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def recv_frame(sock: socket.socket) -> tuple[int, bytes]:
    header = recv_exact(sock, 5)
    frame_type = header[0]
    length = struct.unpack("<I", header[1:])[0]
    if length > MAX_FRAME_BYTES:
        raise ValueError("inbound-frame-too-large")
    return frame_type, recv_exact(sock, length)


class MeshFailure(RuntimeError):
    """A deliberately address-free and signaling-free smoke failure."""

    def __init__(self, reason: str, **progress: object) -> None:
        super().__init__(reason)
        self.reason = reason
        self.progress = progress

    def summary(self) -> str:
        fields = [f"reason={self.reason}"]
        for key in sorted(self.progress):
            value = self.progress[key]
            if isinstance(value, (bool, int, float)) or (
                isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_.:,=-]+", value)
            ):
                fields.append(f"{key}={value}")
        return " ".join(fields)


@dataclass(frozen=True)
class ArchitectureBundle:
    name: str
    helper: Path
    pion: Path
    apm: Path | None
    pion_name: str
    apm_name: str


@dataclass(frozen=True)
class StagedClient:
    index: int
    architecture: str
    executable: Path
    apm_expected: bool


class Helper:
    def __init__(
        self,
        name: str,
        executable: Path,
        work: Path,
        protocol: int,
        timeout: float,
    ) -> None:
        self.name = name
        self._events: queue.Queue[dict[str, Any]] = queue.Queue()
        self._write_lock = threading.Lock()
        self._reader_error: str | None = None
        self._stderr = (work / f"{name}-stderr.log").open("w+b")
        handshake = work / f"{name}-handshake.json"
        token = secrets.token_urlsafe(24)
        try:
            self.process = subprocess.Popen(
                [
                    str(executable),
                    "--synthetic-tone",
                    "--handshake",
                    str(handshake),
                    "--owner-pid",
                    str(os.getpid()),
                ],
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=self._stderr,
                cwd=executable.parent,
            )
        except Exception as exc:
            self._stderr.close()
            raise MeshFailure("helper-spawn", client=name, error=type(exc).__name__) from None

        try:
            if self.process.stdin is None:
                raise MeshFailure("helper-stdin", client=name)
            self.process.stdin.write((token + "\n").encode("utf-8"))
            self.process.stdin.flush()
            self.process.stdin.close()

            deadline = time.monotonic() + timeout
            port = 0
            while time.monotonic() < deadline:
                if self.process.poll() is not None:
                    raise MeshFailure("helper-handshake-exit", client=name)
                try:
                    port = int(json.loads(handshake.read_text(encoding="utf-8"))["port"])
                    break
                except (FileNotFoundError, KeyError, ValueError, json.JSONDecodeError):
                    time.sleep(0.02)
            if not port:
                raise MeshFailure("helper-handshake-timeout", client=name)

            try:
                self.socket = socket.create_connection(("127.0.0.1", port), timeout=timeout)
            except Exception as exc:
                raise MeshFailure(
                    "helper-control-connect", client=name, error=type(exc).__name__
                ) from None
            self.socket.settimeout(timeout)
            self.send({"op": "hello", "proto": protocol, "token": token})
            frame_type, body = recv_frame(self.socket)
            ready = json.loads(body)
            if (
                frame_type != TYPE_CONTROL
                or ready.get("op") != "ready"
                or ready.get("proto") != protocol
            ):
                raise MeshFailure("helper-ready-incompatible", client=name)

            self.socket.settimeout(None)
            self._reader = threading.Thread(
                target=self._read_loop,
                name=f"p2p-mesh-{name}",
                daemon=True,
            )
            self._reader.start()
        except Exception:
            self.close(partial=True)
            raise

    def _read_loop(self) -> None:
        try:
            while True:
                frame_type, body = recv_frame(self.socket)
                if frame_type != TYPE_CONTROL:
                    continue
                message = json.loads(body)
                if isinstance(message, dict):
                    self._events.put(message)
        except Exception as exc:
            self._reader_error = type(exc).__name__

    def send(self, message: dict[str, Any]) -> None:
        body = json.dumps(message, separators=(",", ":")).encode("utf-8")
        if len(body) > MAX_FRAME_BYTES:
            raise MeshFailure("outbound-frame-too-large", client=self.name)
        frame = bytes([TYPE_CONTROL]) + struct.pack("<I", len(body)) + body
        try:
            with self._write_lock:
                self.socket.sendall(frame)
        except Exception as exc:
            raise MeshFailure(
                "helper-control-write", client=self.name, error=type(exc).__name__
            ) from None

    def drain(self) -> list[dict[str, Any]]:
        result: list[dict[str, Any]] = []
        while True:
            try:
                result.append(self._events.get_nowait())
            except queue.Empty:
                return result

    def runtime_health(self) -> dict[str, bool]:
        try:
            self._stderr.flush()
            self._stderr.seek(0)
            text = self._stderr.read().decode("utf-8", errors="replace")
        except Exception:
            return {
                "pion_loaded": False,
                "apm_loaded": False,
                "critical": True,
                "owner_exit": False,
            }
        return {
            "pion_loaded": f"Pion WebRTC {PION_VERSION} transport loaded" in text,
            "apm_loaded": "dsp apm=true" in text or "dsp set apm=true" in text,
            "critical": "critical media failure" in text,
            "owner_exit": "owner exited" in text or "owner guard failed" in text,
        }

    def close(self, partial: bool = False) -> None:
        process = getattr(self, "process", None)
        if not partial:
            try:
                self.send({"op": "stop"})
            except Exception:
                pass
            if process is not None and process.poll() is None:
                try:
                    process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    pass
        try:
            if hasattr(self, "socket"):
                self.socket.close()
        except Exception:
            pass
        if process is not None and process.poll() is None:
            process.kill()
        if process is not None:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    pass
        try:
            self._stderr.close()
        except Exception:
            pass


Endpoint = tuple[int, int]


class Mesh:
    def __init__(
        self, helpers: list[Helper], phase_timeout: float, architectures: list[str]
    ) -> None:
        self.helpers = helpers
        self.phase_timeout = phase_timeout
        self.architectures = architectures
        self.names = [helper.name for helper in helpers]
        self.name_to_index = {name: index for index, name in enumerate(self.names)}
        self.generation: dict[Endpoint, int] = {
            (source, target): 1
            for source in range(CLIENT_COUNT)
            for target in range(CLIENT_COUNT)
            if source != target
        }
        self.connected: set[tuple[int, int, int]] = set()
        self.heard: set[tuple[int, int, int]] = set()
        self.levels_seen: set[tuple[int, int, int]] = set()
        self.eoc_epochs: collections.Counter[tuple[int, int, int]] = collections.Counter()
        self.eoc_messages: collections.Counter[tuple[int, int, int]] = collections.Counter()
        self.sdp_counts: collections.Counter[tuple[int, int, int, str]] = collections.Counter()
        self.sdp_hashes: dict[tuple[int, int, int, str], bytes] = {}
        self.sdp_ufrag_hashes: dict[tuple[int, int, int, str], bytes] = {}
        self.paths_by_source: dict[int, dict[tuple[int, int], dict[str, Any]]] = {}
        self.latest_stats: dict[int, dict[str, Any]] = {}
        self.state_histories: dict[tuple[int, int, int], list[str]] = {}
        self.pongs: collections.Counter[int] = collections.Counter()
        self.last_ping = 0.0
        self.errors: list[tuple[int, str]] = []
        self.stale_signals = 0
        self.late_candidates_after_eoc = 0
        self.monitor_unaffected = False
        self.unaffected_pairs: set[tuple[int, int]] = set()
        self.unaffected_signal_events = 0
        self.unaffected_bad_states = 0
        self.coordinate_restart_answerers = False
        self.coordinated_answerers: set[tuple[int, int, int]] = set()
        self.restart_feedback_count = 0
        self.restart_level_count = 0
        self.churn_feedback_count = 0
        self.churn_level_count = 0

    @staticmethod
    def endpoints() -> list[Endpoint]:
        return [
            (source, target)
            for source in range(CLIENT_COUNT)
            for target in range(CLIENT_COUNT)
            if source != target
        ]

    @staticmethod
    def pairs() -> list[tuple[int, int]]:
        return [
            (lower, higher)
            for lower in range(CLIENT_COUNT)
            for higher in range(lower + 1, CLIENT_COUNT)
        ]

    def endpoint_key(self, source: int, target: int) -> tuple[int, int, int]:
        return source, target, self.generation[(source, target)]

    def configure(self) -> None:
        for source, helper in enumerate(self.helpers):
            peers = [
                {"id": self.names[target], "gain": 1.0, "pan": 0.0, "mode": 0}
                for target in range(CLIENT_COUNT)
                if target != source
            ]
            helper.send({"op": "set-ice-servers", "servers": MANAGED_ICE_SERVERS})
            helper.send(
                {
                    "op": "set-dsp",
                    "aec": True,
                    "agc": True,
                    "ns": True,
                    "ns_very_high": True,
                    "hpf": True,
                }
            )
            helper.send({"op": "set-diagnostics", "enabled": True})
            helper.send(
                {
                    "op": "set-input",
                    "gain": 1.0,
                    "vad_threshold": 0.005,
                    "noise_gate_threshold": 0.003,
                }
            )
            helper.send({"op": "set-synthetic", "enabled": True})
            # Peer levels are measured before route gain/master. A zero master guarantees that
            # the stress test cannot produce audible playback while still exercising decode.
            helper.send(
                {
                    "op": "game-state",
                    "deaf": False,
                    "master": 0.0,
                    "peers": peers,
                }
            )
        for helper in self.helpers:
            helper.send({"op": "start"})

    def add_initial_peers(self) -> None:
        # Install every answerer before any offerer so signaling is deterministic and no early
        # offer can race a missing remote endpoint.
        for lower, higher in self.pairs():
            self.helpers[higher].send(
                {
                    "op": "peer-add",
                    "peer_id": self.names[lower],
                    "offerer": False,
                    "relay_only": False,
                    "generation": 1,
                }
            )
        for lower, higher in self.pairs():
            self.helpers[lower].send(
                {
                    "op": "peer-add",
                    "peer_id": self.names[higher],
                    "offerer": True,
                    "relay_only": False,
                    "generation": 1,
                }
            )

    def _route_signal(self, source: int, target: int, message: dict[str, Any]) -> None:
        operation = message.get("op")
        if operation == "local-sdp":
            if (
                self.coordinate_restart_answerers
                and message.get("sdp_type") == "offer"
            ):
                generation = self.generation[(target, source)]
                if generation != message.get("generation"):
                    self.errors.append((source, "asymmetric-restart-generation"))
                    return
                self.helpers[target].send(
                    {
                        "op": "restart-ice",
                        "peer_id": self.names[source],
                        "generation": generation,
                        "relay_only": False,
                        "create_offer": False,
                    }
                )
                self.coordinated_answerers.add((target, source, generation))
            self.helpers[target].send(
                {
                    "op": "set-remote-sdp",
                    "peer_id": self.names[source],
                    "generation": self.generation[(target, source)],
                    "sdp_type": message.get("sdp_type", ""),
                    "sdp": message.get("sdp", ""),
                }
            )
        elif operation == "local-candidate":
            # An empty candidate is the generation-scoped end-of-candidates marker. Preserve it
            # exactly; do not drop or transform it.
            self.helpers[target].send(
                {
                    "op": "add-ice-candidate",
                    "peer_id": self.names[source],
                    "generation": self.generation[(target, source)],
                    "candidate": message.get("candidate", ""),
                }
            )

    def _record_stats(self, source: int, message: dict[str, Any]) -> None:
        self.latest_stats[source] = message
        paths: dict[tuple[int, int], dict[str, Any]] = {}
        raw_paths = message.get("network_paths", [])
        if isinstance(raw_paths, list):
            for path in raw_paths:
                if not isinstance(path, dict):
                    continue
                target = self.name_to_index.get(str(path.get("peer_id", "")))
                generation = path.get("generation")
                if target is None or not isinstance(generation, int):
                    continue
                paths[(target, generation)] = path
        self.paths_by_source[source] = paths

    @staticmethod
    def _sdp_ufrag_hash(sdp: str) -> bytes | None:
        # Keep credentials private: retain only a digest of the normalized ICE ufrag set.
        ufrags = re.findall(r"(?m)^a=ice-ufrag:([^\r\n]+)\r?$", sdp)
        normalized = sorted({value.strip() for value in ufrags if value.strip()})
        if not normalized or any(len(value) > 256 for value in normalized):
            return None
        return hashlib.sha256("\n".join(normalized).encode("utf-8")).digest()

    def _record_message(self, source: int, message: dict[str, Any]) -> None:
        operation = str(message.get("op", ""))
        if operation in {"local-sdp", "local-candidate"}:
            target = self.name_to_index.get(str(message.get("peer_id", "")))
            generation = message.get("generation")
            if target is None or target == source or not isinstance(generation, int):
                self.errors.append((source, "invalid-signal-metadata"))
                return
            if generation != self.generation[(source, target)]:
                self.stale_signals += 1
                return
            pair = (min(source, target), max(source, target))
            if self.monitor_unaffected and pair in self.unaffected_pairs:
                self.unaffected_signal_events += 1
            key = (source, target, generation)
            if operation == "local-sdp":
                sdp_type = str(message.get("sdp_type", ""))
                sdp = message.get("sdp", "")
                if not isinstance(sdp, str) or sdp_type not in {"offer", "answer"}:
                    self.errors.append((source, "invalid-local-sdp"))
                    return
                ufrag_hash = self._sdp_ufrag_hash(sdp)
                if ufrag_hash is None:
                    self.errors.append((source, "local-sdp-missing-ice-ufrag"))
                    return
                sdp_key = (*key, sdp_type)
                # One local description opens one candidate-gathering negotiation. Reusing the
                # same peer generation for ICE restart must still open a distinct negotiation.
                local_sdp_count = self.sdp_counts[(*key, "offer")] + self.sdp_counts[
                    (*key, "answer")
                ]
                if local_sdp_count != self.eoc_epochs[key]:
                    self.errors.append((source, "local-sdp-before-prior-eoc"))
                    return
                self.sdp_counts[sdp_key] += 1
                self.sdp_hashes[sdp_key] = hashlib.sha256(sdp.encode("utf-8")).digest()
                self.sdp_ufrag_hashes[sdp_key] = ufrag_hash
            else:
                candidate = message.get("candidate", "")
                if not isinstance(candidate, str):
                    self.errors.append((source, "invalid-local-candidate"))
                    return
                local_sdp_count = self.sdp_counts[(*key, "offer")] + self.sdp_counts[
                    (*key, "answer")
                ]
                if local_sdp_count == 0:
                    self.errors.append((source, "candidate-before-local-sdp"))
                    return
                if candidate == "":
                    self.eoc_messages[key] += 1
                    if local_sdp_count == self.eoc_epochs[key] + 1:
                        self.eoc_epochs[key] += 1
                    elif local_sdp_count != self.eoc_epochs[key]:
                        self.errors.append((source, "eoc-before-local-sdp"))
                        return
                elif local_sdp_count == self.eoc_epochs[key]:
                    # Pion can discover an active ICE-TCP candidate after its first nil callback.
                    # Forward it and count the extension rather than discarding a usable path.
                    self.late_candidates_after_eoc += 1
                elif local_sdp_count != self.eoc_epochs[key] + 1:
                    self.errors.append((source, "candidate-outside-negotiation"))
                    return
            self._route_signal(source, target, message)
            return

        if operation == "peer-state":
            target = self.name_to_index.get(str(message.get("peer_id", "")))
            generation = message.get("generation")
            state = str(message.get("state", ""))
            if target is None or target == source or not isinstance(generation, int):
                self.errors.append((source, "invalid-peer-state"))
                return
            if generation != self.generation[(source, target)]:
                return
            key = (source, target, generation)
            pair = (min(source, target), max(source, target))
            if state not in {
                "new",
                "connecting",
                "connected",
                "disconnected",
                "failed",
                "closed",
            }:
                state = "other"
            history = self.state_histories.setdefault(key, [])
            if not history or history[-1] != state:
                history.append(state)
            if self.monitor_unaffected and pair in self.unaffected_pairs and state in {
                "new",
                "connecting",
                "disconnected",
                "failed",
                "closed",
            }:
                self.unaffected_bad_states += 1
            if state == "connected":
                self.connected.add(key)
            elif state in {"failed", "closed"}:
                self.errors.append((source, f"peer-{state}"))
            return

        if operation == "peer-levels":
            levels = message.get("levels", [])
            if not isinstance(levels, list):
                return
            for level in levels:
                if not isinstance(level, dict):
                    continue
                target = self.name_to_index.get(str(level.get("peer_id", "")))
                peak = level.get("peak")
                if (
                    target is not None
                    and target != source
                    and isinstance(peak, (int, float))
                ):
                    key = self.endpoint_key(source, target)
                    self.levels_seen.add(key)
                    if float(peak) > LEVEL_THRESHOLD:
                        self.heard.add(key)
            return

        if operation == "stats":
            self._record_stats(source, message)
            return

        if operation == "pong":
            self.pongs[source] += 1
            return

        if operation == "error":
            code = str(message.get("code", "helper-error"))
            if not re.fullmatch(r"[A-Za-z0-9_.:-]{1,80}", code):
                code = "helper-error"
            self.errors.append((source, code))

    def failure_progress(self) -> dict[str, object]:
        patterns = collections.Counter(
            "-".join(history) for history in self.state_histories.values() if history
        )
        architecture_pairs: collections.Counter[str] = collections.Counter()
        failed_endpoints: list[str] = []
        for (source, target, _generation), history in self.state_histories.items():
            if history and history[-1] in {"closed", "failed"}:
                pair = "-".join(
                    sorted((self.architectures[source], self.architectures[target]))
                )
                architecture_pairs[pair] += 1
                failed_endpoints.append(
                    f"c{source}-c{target}-g{_generation}:{'-'.join(history)}"
                )
        codes = collections.Counter(code for _source, code in self.errors)
        runtime = [helper.runtime_health() for helper in self.helpers]
        return {
            "apm_loaded": sum(health["apm_loaded"] for health in runtime),
            "arch_pairs": ",".join(
                f"{name}:{architecture_pairs[name]}" for name in sorted(architecture_pairs)
            )
            or "none",
            "codes": ",".join(f"{name}:{codes[name]}" for name in sorted(codes))
            or "none",
            "critical_logs": sum(health["critical"] for health in runtime),
            "failed_endpoints": ",".join(sorted(failed_endpoints)) or "none",
            "owner_exits": sum(health["owner_exit"] for health in runtime),
            "pion_loaded": sum(health["pion_loaded"] for health in runtime),
            "state_patterns": ",".join(
                f"{name}:{patterns[name]}" for name in sorted(patterns)
            )
            or "none",
        }

    def pump(self) -> None:
        now = time.monotonic()
        if now - self.last_ping >= 1.0:
            for helper in self.helpers:
                helper.send({"op": "ping"})
            self.last_ping = now
        for source, helper in enumerate(self.helpers):
            if helper._reader_error is not None:
                raise MeshFailure(
                    "helper-reader-stopped", client=source, error=helper._reader_error
                )
            if helper.process.poll() is not None:
                raise MeshFailure("helper-exited", client=source)
            for message in helper.drain():
                self._record_message(source, message)
            if helper._reader_error is not None:
                raise MeshFailure(
                    "helper-reader-stopped", client=source, error=helper._reader_error
                )
        if self.errors:
            source, code = self.errors[0]
            raise MeshFailure(
                "helper-event",
                client=source,
                code=code,
                errors=len(self.errors),
                **self.failure_progress(),
            )
    def wait(
        self,
        phase: str,
        condition: Callable[[], bool],
        progress: Callable[[], dict[str, object]],
        timeout: float | None = None,
    ) -> float:
        started = time.monotonic()
        deadline = started + (self.phase_timeout if timeout is None else timeout)
        while time.monotonic() < deadline:
            self.pump()
            if condition():
                return time.monotonic() - started
            time.sleep(0.005)
        self.pump()
        raise MeshFailure(f"{phase}-timeout", **progress())

    def settle(self, duration: float) -> None:
        deadline = time.monotonic() + duration
        while time.monotonic() < deadline:
            self.pump()
            time.sleep(0.005)

    def settle_paths(self, endpoints: Iterable[Endpoint]) -> None:
        endpoints = list(endpoints)
        deadline = time.monotonic() + min(self.phase_timeout, 15.0)
        fingerprints = {
            endpoint: self._path_fingerprint(*endpoint) for endpoint in endpoints
        }
        stable_since = time.monotonic()
        changes = 0
        while time.monotonic() < deadline:
            self.pump()
            current = {
                endpoint: self._path_fingerprint(*endpoint) for endpoint in endpoints
            }
            if current != fingerprints:
                changes += sum(
                    current[endpoint] != fingerprints[endpoint] for endpoint in endpoints
                )
                fingerprints = current
                stable_since = time.monotonic()
            elif (
                all(value is not None for value in current.values())
                and time.monotonic() - stable_since >= 2.0
            ):
                return
            time.sleep(0.01)
        raise MeshFailure("path-stability-timeout", changes=changes)


    def current_connected_count(self, endpoints: Iterable[Endpoint]) -> int:
        return sum(self.endpoint_key(*endpoint) in self.connected for endpoint in endpoints)

    def current_heard_count(self, endpoints: Iterable[Endpoint]) -> int:
        return sum(self.endpoint_key(*endpoint) in self.heard for endpoint in endpoints)

    def current_level_count(self, endpoints: Iterable[Endpoint]) -> int:
        return sum(self.endpoint_key(*endpoint) in self.levels_seen for endpoint in endpoints)

    def current_eoc_count(self, endpoints: Iterable[Endpoint]) -> int:
        return sum(self.eoc_epochs[self.endpoint_key(*endpoint)] > 0 for endpoint in endpoints)

    def path(self, source: int, target: int) -> dict[str, Any] | None:
        generation = self.generation[(source, target)]
        return self.paths_by_source.get(source, {}).get((target, generation))

    def path_healthy(self, source: int, target: int) -> bool:
        path = self.path(source, target)
        if path is None:
            return False
        ice_state = str(path.get("ice_connection_state", "")).lower()
        local_type = str(path.get("local_candidate_type", "")).lower()
        remote_type = str(path.get("remote_candidate_type", "")).lower()
        local_protocol = str(path.get("local_candidate_protocol", "")).lower()
        remote_protocol = str(path.get("remote_candidate_protocol", "")).lower()
        return (
            bool(path.get("candidate_pair_id"))
            and str(path.get("candidate_state", "")).lower() == "succeeded"
            and ice_state in {"connected", "completed"}
            and int(path.get("selected_pair_changes", 0)) > 0
            and not bool(path.get("relay"))
            and local_type not in {"", "relay"}
            and remote_type not in {"", "relay"}
            and local_protocol in {"udp", "tcp"}
            and remote_protocol in {"udp", "tcp"}
        )

    def healthy_path_count(self, endpoints: Iterable[Endpoint]) -> int:
        return sum(self.path_healthy(*endpoint) for endpoint in endpoints)

    def feedback_baseline(self, endpoints: Iterable[Endpoint]) -> dict[Endpoint, int]:
        result: dict[Endpoint, int] = {}
        for endpoint in endpoints:
            path = self.path(*endpoint)
            result[endpoint] = int(path.get("remote_packets_received", 0)) if path else 0
        return result

    def feedback_advanced_count(self, baseline: dict[Endpoint, int]) -> int:
        return sum(
            self.path(*endpoint) is not None
            and int(self.path(*endpoint).get("remote_packets_received", 0)) > previous
            for endpoint, previous in baseline.items()
        )

    def current_packet_flow_count(self, endpoints: Iterable[Endpoint]) -> int:
        return sum(
            self.path(*endpoint) is not None
            and int(self.path(*endpoint).get("remote_packets_received", 0)) > 0
            for endpoint in endpoints
        )

    def heartbeat_baseline(self) -> dict[int, int]:
        return {source: self.pongs[source] for source in range(CLIENT_COUNT)}

    def heartbeat_advanced_count(self, baseline: dict[int, int]) -> int:
        return sum(self.pongs[source] > previous for source, previous in baseline.items())

    def silent_routes_applied(self) -> bool:
        if len(self.latest_stats) != CLIENT_COUNT:
            return False
        return all(
            abs(float(stats.get("applied_master", -1.0))) <= 1e-9
            and int(stats.get("applied_peer_count", -1)) == CLIENT_COUNT - 1
            for stats in self.latest_stats.values()
        )

    def media_totals(self) -> dict[str, int]:
        fields = (
            "capture_frames",
            "opus_encoded",
            "rtp_tx_ok",
            "rtp_rx_packets",
            "decode_frames",
            "peer_level_batches",
        )
        return {
            field: sum(int(stats.get(field, 0)) for stats in self.latest_stats.values())
            for field in fields
        }

    def initial_ready(self) -> bool:
        endpoints = self.endpoints()
        return (
            self.current_connected_count(endpoints) == ENDPOINT_COUNT
            and self.current_level_count(endpoints) == ENDPOINT_COUNT
            and self.current_packet_flow_count(endpoints) == ENDPOINT_COUNT
            and self.current_heard_count(endpoints) > 0
            and self.current_eoc_count(endpoints) == ENDPOINT_COUNT
            and self.healthy_path_count(endpoints) == ENDPOINT_COUNT
            and self.silent_routes_applied()
            and len(self.pongs) == CLIENT_COUNT
        )

    def initial_progress(self) -> dict[str, object]:
        endpoints = self.endpoints()
        return {
            "connected": self.current_connected_count(endpoints),
            "eoc": self.current_eoc_count(endpoints),
            "heard": self.current_heard_count(endpoints),
            "levels": self.current_level_count(endpoints),
            "packet_flows": self.current_packet_flow_count(endpoints),
            "paths": self.healthy_path_count(endpoints),
            "pongs": len(self.pongs),
            "stats": len(self.latest_stats),
        }

    def restart_all_pairs(self) -> tuple[float, float]:
        endpoints = self.endpoints()
        baseline_eoc = {
            self.endpoint_key(source, target): self.eoc_epochs[
                self.endpoint_key(source, target)
            ]
            for source, target in endpoints
        }
        baseline_pair_changes = {
            endpoint: int(self.path(*endpoint).get("selected_pair_changes", 0))
            for endpoint in endpoints
            if self.path(*endpoint) is not None
        }
        negotiation_keys = [
            (*self.endpoint_key(source, target), sdp_type)
            for lower, higher in self.pairs()
            for source, target, sdp_type in (
                (lower, higher, "offer"),
                (higher, lower, "answer"),
            )
        ]
        baseline_sdp = {
            key: (
                self.sdp_counts[key],
                self.sdp_hashes.get(key, b""),
                self.sdp_ufrag_hashes.get(key, b""),
            )
            for key in negotiation_keys
        }
        started = time.monotonic()
        self.coordinated_answerers.clear()
        self.coordinate_restart_answerers = True

        def restart(pair: tuple[int, int]) -> None:
            lower, higher = pair
            self.helpers[lower].send(
                {
                    "op": "restart-ice",
                    "peer_id": self.names[higher],
                    "generation": self.generation[(lower, higher)],
                    "relay_only": False,
                    "create_offer": True,
                }
            )

        with concurrent.futures.ThreadPoolExecutor(max_workers=LINK_COUNT) as executor:
            futures = [executor.submit(restart, pair) for pair in self.pairs()]
            for future in futures:
                future.result()

        def signaling_ready() -> bool:
            if self.healthy_path_count(endpoints) != ENDPOINT_COUNT:
                return False
            for source, target in endpoints:
                key = self.endpoint_key(source, target)
                if self.eoc_epochs[key] <= baseline_eoc[key]:
                    return False
                if int(self.path(source, target).get("selected_pair_changes", 0)) <= (
                    baseline_pair_changes.get((source, target), 0)
                ):
                    return False
            for key, (baseline_count, baseline_hash, baseline_ufrag) in baseline_sdp.items():
                current = self.sdp_hashes.get(key, b"")
                current_ufrag = self.sdp_ufrag_hashes.get(key, b"")
                if (
                    self.sdp_counts[key] != baseline_count + 1
                    or not current
                    or current == baseline_hash
                    or not current_ufrag
                    or current_ufrag == baseline_ufrag
                ):
                    return False
            return True

        signaling_duration = self.wait(
            "restart-signaling",
            signaling_ready,
            lambda: {
                "eoc_advanced": sum(
                    self.eoc_epochs[key] > count for key, count in baseline_eoc.items()
                ),
                "sdp_changed": sum(
                    self.sdp_counts[key] == baseline_count + 1
                    and bool(self.sdp_hashes.get(key, b""))
                    and self.sdp_hashes.get(key, b"") != baseline_hash
                    for key, (baseline_count, baseline_hash, _baseline_ufrag) in baseline_sdp.items()
                ),
                "ufrag_changed": sum(
                    self.sdp_counts[key] == baseline_count + 1
                    and bool(self.sdp_ufrag_hashes.get(key, b""))
                    and self.sdp_ufrag_hashes.get(key, b"") != baseline_ufrag
                    for key, (baseline_count, _baseline_hash, baseline_ufrag) in baseline_sdp.items()
                ),
                "paths": self.healthy_path_count(endpoints),
                "paths_changed": sum(
                    self.path(*endpoint) is not None
                    and int(self.path(*endpoint).get("selected_pair_changes", 0))
                    > baseline_pair_changes.get(endpoint, 0)
                    for endpoint in endpoints
                ),
            },
        )
        self.coordinate_restart_answerers = False
        if len(self.coordinated_answerers) != LINK_COUNT:
            raise MeshFailure(
                "restart-answerer-coordination",
                actual=len(self.coordinated_answerers),
                expected=LINK_COUNT,
            )

        feedback_baseline = self.feedback_baseline(endpoints)
        heartbeat_baseline = self.heartbeat_baseline()
        media_baseline = self.media_totals()
        self.heard.clear()
        self.levels_seen.clear()
        media_duration = self.wait(
            "restart-media",
            lambda: sum(
                self.endpoint_key(*endpoint) in self.levels_seen for endpoint in endpoints
            )
            == ENDPOINT_COUNT
            and self.feedback_advanced_count(feedback_baseline) == ENDPOINT_COUNT
            and self.heartbeat_advanced_count(heartbeat_baseline) == CLIENT_COUNT
            and self.healthy_path_count(endpoints) == ENDPOINT_COUNT
            and self.media_totals()["rtp_tx_ok"] > media_baseline["rtp_tx_ok"]
            and self.media_totals()["rtp_rx_packets"]
            > media_baseline["rtp_rx_packets"]
            and self.media_totals()["decode_frames"] > media_baseline["decode_frames"],
            lambda: {
                "capture_delta": self.media_totals()["capture_frames"]
                - media_baseline["capture_frames"],
                "decode_delta": self.media_totals()["decode_frames"]
                - media_baseline["decode_frames"],
                "feedback": self.feedback_advanced_count(feedback_baseline),
                "heartbeats": self.heartbeat_advanced_count(heartbeat_baseline),
                "heard": self.current_heard_count(endpoints),
                "levels": sum(
                    self.endpoint_key(*endpoint) in self.levels_seen
                    for endpoint in endpoints
                ),
                "level_delta": self.media_totals()["peer_level_batches"]
                - media_baseline["peer_level_batches"],
                "paths": self.healthy_path_count(endpoints),
                "rx_delta": self.media_totals()["rtp_rx_packets"]
                - media_baseline["rtp_rx_packets"],
                "tx_delta": self.media_totals()["rtp_tx_ok"]
                - media_baseline["rtp_tx_ok"],
            },
        )
        self.restart_feedback_count = self.feedback_advanced_count(feedback_baseline)
        self.restart_level_count = sum(
            self.endpoint_key(*endpoint) in self.levels_seen for endpoint in endpoints
        )
        return time.monotonic() - started, media_duration

    def churn_client_nine(self) -> float:
        churned = CLIENT_COUNT - 1
        affected_endpoints = [
            endpoint for endpoint in self.endpoints() if churned in endpoint
        ]
        unaffected_endpoints = [
            endpoint for endpoint in self.endpoints() if churned not in endpoint
        ]
        self.unaffected_pairs = {
            (lower, higher)
            for lower, higher in self.pairs()
            if churned not in (lower, higher)
        }
        self.settle_paths(unaffected_endpoints)
        unaffected_paths = {
            endpoint: self._path_fingerprint(*endpoint) for endpoint in unaffected_endpoints
        }
        if any(value is None for value in unaffected_paths.values()):
            raise MeshFailure("churn-baseline-path-missing")
        churn_negotiations: list[
            tuple[tuple[int, int, int, str], tuple[int, int, int, str], bytes]
        ] = []
        for other in range(churned):
            for source, target, sdp_type in (
                (other, churned, "offer"),
                (churned, other, "answer"),
            ):
                old_key = (source, target, 1, sdp_type)
                expected_key = (source, target, 2, sdp_type)
                unexpected_type = "answer" if sdp_type == "offer" else "offer"
                unexpected_key = (source, target, 2, unexpected_type)
                old_ufrag = self.sdp_ufrag_hashes.get(old_key, b"")
                if not old_ufrag:
                    raise MeshFailure(
                        "churn-baseline-ufrag-missing", client=source, peer=target
                    )
                churn_negotiations.append((expected_key, unexpected_key, old_ufrag))
        self.unaffected_signal_events = 0
        self.unaffected_bad_states = 0
        self.monitor_unaffected = True
        started = time.monotonic()

        for other in range(churned):
            self.helpers[other].send(
                {"op": "peer-remove", "peer_id": self.names[churned], "generation": 1}
            )
            self.helpers[churned].send(
                {"op": "peer-remove", "peer_id": self.names[other], "generation": 1}
            )
            self.generation[(other, churned)] = 2
            self.generation[(churned, other)] = 2

        # Client 9 is the deterministic answerer for each lower-numbered client.
        for other in range(churned):
            self.helpers[churned].send(
                {
                    "op": "peer-add",
                    "peer_id": self.names[other],
                    "offerer": False,
                    "relay_only": False,
                    "generation": 2,
                }
            )
        for other in range(churned):
            self.helpers[other].send(
                {
                    "op": "peer-add",
                    "peer_id": self.names[churned],
                    "offerer": True,
                    "relay_only": False,
                    "generation": 2,
                }
            )

        def reconnect_ready() -> bool:
            return (
                self.current_connected_count(affected_endpoints) == len(affected_endpoints)
                and all(
                    self.eoc_epochs[self.endpoint_key(*endpoint)] == 1
                    and self.eoc_messages[self.endpoint_key(*endpoint)] == 1
                    for endpoint in affected_endpoints
                )
                and all(
                    self.sdp_counts[expected_key] == 1
                    and self.sdp_counts[unexpected_key] == 0
                    and bool(self.sdp_hashes.get(expected_key, b""))
                    and bool(self.sdp_ufrag_hashes.get(expected_key, b""))
                    and self.sdp_ufrag_hashes.get(expected_key, b"") != old_ufrag
                    for expected_key, unexpected_key, old_ufrag in churn_negotiations
                )
                and self.healthy_path_count(affected_endpoints) == len(affected_endpoints)
                and self.healthy_path_count(unaffected_endpoints) == len(unaffected_endpoints)
            )

        self.wait(
            "churn-reconnect",
            reconnect_ready,
            lambda: {
                "connected": self.current_connected_count(affected_endpoints),
                "eoc": self.current_eoc_count(affected_endpoints),
                "fresh_ufrags": sum(
                    self.sdp_counts[expected_key] == 1
                    and bool(self.sdp_ufrag_hashes.get(expected_key, b""))
                    and self.sdp_ufrag_hashes.get(expected_key, b"") != old_ufrag
                    for expected_key, _unexpected_key, old_ufrag in churn_negotiations
                ),
                "negotiations": sum(
                    self.sdp_counts[expected_key] == 1
                    and self.sdp_counts[unexpected_key] == 0
                    for expected_key, unexpected_key, _old_ufrag in churn_negotiations
                ),
                "paths": self.healthy_path_count(affected_endpoints),
                "stable_paths": self.healthy_path_count(unaffected_endpoints),
            },
        )
        all_endpoints = self.endpoints()
        feedback_baseline = self.feedback_baseline(all_endpoints)
        heartbeat_baseline = self.heartbeat_baseline()
        media_baseline = self.media_totals()
        self.heard.clear()
        self.levels_seen.clear()
        self.wait(
            "churn-media",
            lambda: sum(
                self.endpoint_key(*endpoint) in self.levels_seen
                for endpoint in all_endpoints
            )
            == ENDPOINT_COUNT
            and self.feedback_advanced_count(feedback_baseline) == ENDPOINT_COUNT
            and self.heartbeat_advanced_count(heartbeat_baseline) == CLIENT_COUNT
            and self.healthy_path_count(all_endpoints) == ENDPOINT_COUNT
            and self.media_totals()["rtp_tx_ok"] > media_baseline["rtp_tx_ok"]
            and self.media_totals()["rtp_rx_packets"]
            > media_baseline["rtp_rx_packets"]
            and self.media_totals()["decode_frames"] > media_baseline["decode_frames"],
            lambda: {
                "feedback": self.feedback_advanced_count(feedback_baseline),
                "heartbeats": self.heartbeat_advanced_count(heartbeat_baseline),
                "heard": self.current_heard_count(all_endpoints),
                "levels": sum(
                    self.endpoint_key(*endpoint) in self.levels_seen
                    for endpoint in all_endpoints
                ),
                "paths": self.healthy_path_count(all_endpoints),
                "rx_delta": self.media_totals()["rtp_rx_packets"]
                - media_baseline["rtp_rx_packets"],
                "tx_delta": self.media_totals()["rtp_tx_ok"]
                - media_baseline["rtp_tx_ok"],
            },
        )
        self.churn_feedback_count = self.feedback_advanced_count(feedback_baseline)
        self.churn_level_count = sum(
            self.endpoint_key(*endpoint) in self.levels_seen for endpoint in all_endpoints
        )
        self.monitor_unaffected = False

        changed_paths = sum(
            self._path_fingerprint(*endpoint) != fingerprint
            for endpoint, fingerprint in unaffected_paths.items()
        )
        if self.unaffected_signal_events or self.unaffected_bad_states or changed_paths:
            raise MeshFailure(
                "churn-disturbed-stable-links",
                bad_states=self.unaffected_bad_states,
                changed_paths=changed_paths,
                signal_events=self.unaffected_signal_events,
            )
        return time.monotonic() - started

    def _path_fingerprint(self, source: int, target: int) -> tuple[object, ...] | None:
        path = self.path(source, target)
        if path is None:
            return None
        return (
            path.get("generation"),
            path.get("candidate_pair_id"),
            path.get("candidate_state"),
            path.get("local_candidate_type"),
            path.get("remote_candidate_type"),
            bool(path.get("relay")),
            path.get("ice_connection_state"),
            path.get("local_candidate_protocol"),
            path.get("remote_candidate_protocol"),
        )

    def wait_for_feedback(self) -> float:
        endpoints = self.endpoints()
        return self.wait(
            "network-feedback",
            lambda: all(
                self.path(source, target) is not None
                and int(self.path(source, target).get("remote_packets_received", 0)) > 0
                for source, target in endpoints
            ),
            lambda: {
                "feedback": sum(
                    self.path(source, target) is not None
                    and int(self.path(source, target).get("remote_packets_received", 0)) > 0
                    for source, target in endpoints
                )
            },
            timeout=min(self.phase_timeout, 30.0),
        )

    def health_summary(self) -> dict[str, object]:
        endpoints = self.endpoints()
        paths = [self.path(*endpoint) for endpoint in endpoints]
        if any(path is None for path in paths):
            raise MeshFailure("final-path-missing")
        actual_paths = [path for path in paths if path is not None]
        relay_paths = sum(bool(path.get("relay")) for path in actual_paths)
        if relay_paths or self.healthy_path_count(endpoints) != ENDPOINT_COUNT:
            raise MeshFailure("final-path-invalid", relay=relay_paths)

        type_counts: collections.Counter[str] = collections.Counter()
        for path in actual_paths:
            local_type = str(path.get("local_candidate_type", "unknown")).lower()
            remote_type = str(path.get("remote_candidate_type", "unknown")).lower()
            local_protocol = str(path.get("local_candidate_protocol", "unknown")).lower()
            remote_protocol = str(path.get("remote_candidate_protocol", "unknown")).lower()
            type_counts[
                f"{local_type}-{remote_type}/{local_protocol}-{remote_protocol}"
            ] += 1

        critical_fields = (
            "opus_errors",
            "decode_errors",
            "playback_errors",
            "playback_callback_errors",
        )
        totals = {
            field: sum(int(stats.get(field, 0)) for stats in self.latest_stats.values())
            for field in critical_fields
        }
        ingress_overflow = sum(
            int(stats.get("media_receive", {}).get("ingress_queue_overflow", 0))
            for stats in self.latest_stats.values()
            if isinstance(stats.get("media_receive", {}), dict)
        )
        encoded_overflow = sum(
            int(stats.get("media_receive", {}).get("encoded_overflow_drops", 0))
            for stats in self.latest_stats.values()
            if isinstance(stats.get("media_receive", {}), dict)
        )
        nonzero_critical = sum(totals.values())
        if nonzero_critical:
            nonzero_counters = ",".join(
                f"{name}:{value}" for name, value in sorted(totals.items()) if value
            )
            raise MeshFailure(
                "network-health-counters",
                counters=nonzero_counters,
                failures=nonzero_critical,
            )
        received_packets = sum(
            int(stats.get("rtp_rx_packets", 0)) for stats in self.latest_stats.values()
        )
        overflow_drops = ingress_overflow + encoded_overflow
        overflow_budget = max(ENDPOINT_COUNT, received_packets // 1000)
        if overflow_drops > overflow_budget:
            raise MeshFailure(
                "network-overflow-rate",
                budget=overflow_budget,
                drops=overflow_drops,
                packets=received_packets,
            )
        rtp_tx_errors = sum(
            int(stats.get("rtp_tx_errors", 0)) for stats in self.latest_stats.values()
        )
        rtp_tx_queue_dropped = sum(
            int(stats.get("rtp_tx_queue_dropped", 0))
            for stats in self.latest_stats.values()
        )
        rtp_tx_write_timeouts = sum(
            int(stats.get("rtp_tx_write_timeouts", 0))
            for stats in self.latest_stats.values()
        )

        for index, stats in self.latest_stats.items():
            if (
                int(stats.get("rtp_tx_ok", 0)) <= 0
                or int(stats.get("rtp_rx_packets", 0)) <= 0
                or int(stats.get("decode_frames", 0)) <= 0
            ):
                raise MeshFailure("media-counter-missing", client=index)

        max_rtt = max(float(path.get("current_rtt_ms", 0.0)) for path in actual_paths)
        max_loss = max(float(path.get("remote_fraction_lost", 0.0)) for path in actual_paths)
        min_feedback = min(int(path.get("remote_packets_received", 0)) for path in actual_paths)
        stale_rx = sum(
            int(stats.get("stale_rtp_rx_dropped", 0)) for stats in self.latest_stats.values()
        )
        late_drops = sum(
            int(stats.get("media_receive", {}).get("late_drops", 0))
            for stats in self.latest_stats.values()
            if isinstance(stats.get("media_receive", {}), dict)
        )
        plc_frames = sum(
            int(stats.get("media_receive", {}).get("plc_frames", 0))
            for stats in self.latest_stats.values()
            if isinstance(stats.get("media_receive", {}), dict)
        )
        local_media_gap_frames = sum(
            int(stats.get("media_receive", {}).get("local_media_gap_frames", 0))
            for stats in self.latest_stats.values()
            if isinstance(stats.get("media_receive", {}), dict)
        )
        min_pair_changes = min(
            int(path.get("selected_pair_changes", 0)) for path in actual_paths
        )
        candidate_types = ",".join(
            f"{name}:{type_counts[name]}" for name in sorted(type_counts)
        )
        return {
            "rtp_tx_errors": rtp_tx_errors,
            "rtp_tx_queue_dropped": rtp_tx_queue_dropped,
            "rtp_tx_write_timeouts": rtp_tx_write_timeouts,
            "ingress_overflow": ingress_overflow,
            "encoded_overflow": encoded_overflow,
            "candidate_types": candidate_types,
            "late_drops": late_drops,
            "local_media_gap_frames": local_media_gap_frames,
            "max_loss_pct": max_loss * 100.0,
            "max_rtt_ms": max_rtt,
            "min_remote_packets": min_feedback,
            "min_pair_changes": min_pair_changes,
            "plc_frames": plc_frames,
            "relay_paths": relay_paths,
            "stale_rx": stale_rx,
        }


def existing_file(path: Path | None) -> Path | None:
    if path is None:
        return None
    resolved = path.resolve()
    return resolved if resolved.is_file() else None


def discover_bundles(args: argparse.Namespace) -> list[ArchitectureBundle]:
    raw = (
        (
            "x64",
            existing_file(args.helper_x64),
            existing_file(args.pion_x64),
            existing_file(args.apm_x64),
            "pc-pion.x64.dll",
            "webrtc-apm.x64.dll",
        ),
        (
            "x86",
            existing_file(args.helper_x86),
            existing_file(args.pion_x86),
            existing_file(args.apm_x86),
            "pc-pion.x86.dll",
            "webrtc-apm.x86.dll",
        ),
    )
    bundles = [
        ArchitectureBundle(name, helper, pion, apm, pion_name, apm_name)
        for name, helper, pion, apm, pion_name, apm_name in raw
        if helper is not None and pion is not None
    ]
    if not bundles:
        raise MeshFailure("no-complete-helper-bundle")
    return bundles


def stage_clients(work: Path, bundles: list[ArchitectureBundle]) -> list[StagedClient]:
    by_name = {bundle.name: bundle for bundle in bundles}
    if "x64" in by_name and "x86" in by_name:
        assignments = [by_name["x64" if index % 2 == 0 else "x86"] for index in range(CLIENT_COUNT)]
    else:
        assignments = [bundles[0]] * CLIENT_COUNT

    staged: list[StagedClient] = []
    for index, bundle in enumerate(assignments):
        directory = work / f"client-{index:02d}-{bundle.name}"
        directory.mkdir(parents=True, exist_ok=False)
        executable = directory / f"pc-capture-{bundle.name}.exe"
        shutil.copy2(bundle.helper, executable)
        shutil.copy2(bundle.pion, directory / bundle.pion_name)
        apm_expected = bundle.apm is not None
        if bundle.apm is not None:
            shutil.copy2(bundle.apm, directory / bundle.apm_name)
        staged.append(StagedClient(index, bundle.name, executable, apm_expected))
    return staged


def start_helpers(
    staged: list[StagedClient], work: Path, protocol: int, timeout: float
) -> list[Helper]:
    helpers: list[Helper | None] = [None] * CLIENT_COUNT
    with concurrent.futures.ThreadPoolExecutor(max_workers=CLIENT_COUNT) as executor:
        futures = {
            executor.submit(
                Helper,
                f"c{client.index}",
                client.executable,
                work,
                protocol,
                timeout,
            ): client.index
            for client in staged
        }
        failure: BaseException | None = None
        for future in concurrent.futures.as_completed(futures):
            index = futures[future]
            try:
                helpers[index] = future.result()
            except BaseException as exc:
                failure = exc
        if failure is not None:
            for helper in helpers:
                if helper is not None:
                    helper.close()
            raise failure
    return [helper for helper in helpers if helper is not None]


def udp_socket_counts(helpers: list[Helper]) -> tuple[int, ...]:
    pids = {helper.process.pid: index for index, helper in enumerate(helpers)}
    counts = [0] * len(helpers)
    try:
        completed = subprocess.run(
            ["netstat", "-ano", "-p", "udp"],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except Exception as exc:
        raise MeshFailure("netstat-execution", error=type(exc).__name__) from None
    if completed.returncode != 0:
        raise MeshFailure("netstat-exit", code=completed.returncode)
    # Parse only protocol and PID. Local/remote address columns are deliberately discarded.
    for line in completed.stdout.splitlines():
        fields = line.split()
        if len(fields) < 4 or fields[0].upper() != "UDP":
            continue
        try:
            pid = int(fields[-1])
        except ValueError:
            continue
        index = pids.get(pid)
        if index is not None:
            counts[index] += 1
    return tuple(counts)


def stable_udp_socket_counts(mesh: Mesh, timeout: float = 6.0) -> tuple[int, ...]:
    deadline = time.monotonic() + timeout
    previous: tuple[int, ...] | None = None
    consecutive = 0
    observations = 0
    while time.monotonic() < deadline:
        mesh.pump()
        current = udp_socket_counts(mesh.helpers)
        observations += 1
        if current == previous and all(count > 0 for count in current):
            consecutive += 1
        else:
            previous = current
            consecutive = 1 if all(count > 0 for count in current) else 0
        if consecutive >= 3:
            return current
        time.sleep(0.15)
    raise MeshFailure("udp-mux-sockets-unstable", observations=observations)


def format_ms(seconds: float) -> int:
    return int(round(seconds * 1000.0))


@contextlib.contextmanager
def staging_directory(parent: Path | None) -> Iterable[Path]:
    '''Create a normal Windows directory instead of tempfile's restrictive ACL directory.'''

    root = (parent or Path(tempfile.gettempdir())).resolve()
    candidate: Path | None = None
    for _ in range(20):
        proposed = root / f'perfect-comms-p2p-mesh-{secrets.token_hex(12)}'
        try:
            proposed.mkdir(parents=False, exist_ok=False)
            candidate = proposed.resolve()
            break
        except FileExistsError:
            continue
    if candidate is None:
        raise MeshFailure('staging-directory-collision')
    try:
        yield candidate
    finally:
        resolved_root = root.resolve()
        resolved_candidate = candidate.resolve()
        safe_to_remove = (
            resolved_candidate.parent == resolved_root
            and resolved_candidate.name.startswith('perfect-comms-p2p-mesh-')
        )
        if not safe_to_remove:
            raise MeshFailure('staging-cleanup-safety')
        cleanup_attempts = 30
        for attempt in range(cleanup_attempts):
            try:
                shutil.rmtree(resolved_candidate)
            except FileNotFoundError:
                break
            except OSError:
                if attempt + 1 < cleanup_attempts:
                    time.sleep(0.2)
            if not resolved_candidate.exists():
                break
        if resolved_candidate.exists():
            raise MeshFailure('staging-cleanup-failed', attempts=cleanup_attempts)


def build_parser(root: Path) -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Run a bounded 10-client full-mesh smoke against real Windows pc-capture/Pion "
            "processes. This is direct P2P only; it never fetches or prints TURN credentials."
        )
    )
    parser.add_argument(
        "--phase-timeout",
        type=float,
        default=60.0,
        help="seconds allowed for each connect/restart/churn phase (default: 60)",
    )
    parser.add_argument(
        "--startup-timeout",
        type=float,
        default=20.0,
        help="seconds allowed for each helper handshake (default: 20)",
    )
    parser.add_argument(
        "--temp-root",
        type=Path,
        help="optional parent for the automatically cleaned staging directory",
    )
    parser.add_argument(
        "--helper-x64",
        type=Path,
        default=root / "Libs" / "pc-capture" / "pc-capture-win-x64.exe",
        help="x64 pc-capture executable",
    )
    parser.add_argument(
        "--helper-x86",
        type=Path,
        default=root / "Libs" / "pc-capture" / "pc-capture-win-x86.exe",
        help="x86 pc-capture executable",
    )
    parser.add_argument(
        "--pion-x64",
        type=Path,
        default=root / "Libs" / "pion" / "pc-pion.x64.dll",
        help=f"x64 Pion transport DLL (must report {PION_VERSION})",
    )
    parser.add_argument(
        "--pion-x86",
        type=Path,
        default=root / "Libs" / "pion" / "pc-pion.x86.dll",
        help=f"x86 Pion transport DLL (must report {PION_VERSION})",
    )
    parser.add_argument(
        "--apm-x64",
        type=Path,
        default=root / "Libs" / "webrtc-apm.x64.dll",
        help="optional matching x64 WebRTC APM DLL staged for production fidelity",
    )
    parser.add_argument(
        "--apm-x86",
        type=Path,
        default=root / "Libs" / "webrtc-apm.x86.dll",
        help="optional matching x86 WebRTC APM DLL staged for production fidelity",
    )
    return parser


def run(args: argparse.Namespace, root: Path) -> dict[str, object]:
    if os.name != "nt":
        raise MeshFailure("windows-only")
    if args.phase_timeout <= 0 or args.startup_timeout <= 0:
        raise MeshFailure("invalid-timeout")
    temp_root = args.temp_root.resolve() if args.temp_root else None
    if temp_root is not None and not temp_root.is_dir():
        raise MeshFailure("temp-root-missing")

    protocol = source_protocol_version(root)
    bundles = discover_bundles(args)
    helpers: list[Helper] = []
    total_started = time.monotonic()
    with staging_directory(temp_root) as work:
        staged = stage_clients(work, bundles)
        startup_started = time.monotonic()
        try:
            helpers = start_helpers(staged, work, protocol, args.startup_timeout)
            startup_duration = time.monotonic() - startup_started
            if len(helpers) != CLIENT_COUNT:
                raise MeshFailure("helper-count", helpers=len(helpers))

            mesh = Mesh(
                helpers,
                args.phase_timeout,
                [client.architecture for client in staged],
            )
            pre_peer_udp_sockets = stable_udp_socket_counts(mesh)
            if not all(1 <= count <= 2 for count in pre_peer_udp_sockets):
                raise MeshFailure(
                    "udp-mux-baseline-unexpected",
                    actual=",".join(str(count) for count in pre_peer_udp_sockets),
                )
            mesh.configure()
            mesh.add_initial_peers()
            initial_duration = mesh.wait(
                "initial-mesh", mesh.initial_ready, mesh.initial_progress
            )
            initial_endpoints = mesh.endpoints()
            initial_nonzero_levels = mesh.current_heard_count(initial_endpoints)
            initial_level_entries = mesh.current_level_count(initial_endpoints)
            initial_packet_flows = mesh.current_packet_flow_count(initial_endpoints)
            initial_udp_sockets = stable_udp_socket_counts(mesh)
            deltas = tuple(
                after - before
                for before, after in zip(
                    pre_peer_udp_sockets, initial_udp_sockets, strict=True
                )
            )
            # STUN gathering opens short-lived per-server sockets outside the shared host UDP
            # mux. Unreachable servers may close them before observation, so enforce a strict
            # no-growth ceiling rather than brittle exact equality across clients and restarts.
            max_expected_per_peer = len(MANAGED_STUN_URLS) + 2
            max_added_sockets = max_expected_per_peer * (CLIENT_COUNT - 1)
            if any(delta < 0 or delta > max_added_sockets for delta in deltas):
                raise MeshFailure(
                    "udp-socket-budget-unexpected",
                    actual=",".join(str(count) for count in initial_udp_sockets),
                    baseline=",".join(str(count) for count in pre_peer_udp_sockets),
                    maximum=max_added_sockets,
                )
            per_peer_floor = min(deltas) // (CLIENT_COUNT - 1)
            per_peer_ceiling = (
                max(deltas) + CLIENT_COUNT - 2
            ) // (CLIENT_COUNT - 1)
            sockets_per_peer = f"{per_peer_floor}-{per_peer_ceiling}"
            restart_duration, restart_media_duration = mesh.restart_all_pairs()
            restart_udp_sockets = stable_udp_socket_counts(mesh)
            if any(
                after < baseline or after > baseline + max_added_sockets
                for baseline, after in zip(
                    pre_peer_udp_sockets, restart_udp_sockets, strict=True
                )
            ):
                raise MeshFailure(
                    "udp-mux-sockets-unbounded-after-restart",
                    actual=",".join(str(count) for count in restart_udp_sockets),
                    baseline=",".join(str(count) for count in pre_peer_udp_sockets),
                    maximum=max_added_sockets,
                )
            churn_duration = mesh.churn_client_nine()
            churn_udp_sockets = stable_udp_socket_counts(mesh)
            if any(
                after < baseline or after > baseline + max_added_sockets
                for baseline, after in zip(
                    pre_peer_udp_sockets, churn_udp_sockets, strict=True
                )
            ):
                raise MeshFailure(
                    "udp-mux-sockets-unbounded-after-churn",
                    actual=",".join(str(count) for count in churn_udp_sockets),
                    baseline=",".join(str(count) for count in pre_peer_udp_sockets),
                    maximum=max_added_sockets,
                )
            feedback_duration = mesh.wait_for_feedback()
            mesh.settle(0.25)

            runtime_health = [helper.runtime_health() for helper in helpers]
            pion_loaded = sum(health["pion_loaded"] for health in runtime_health)
            critical_logs = sum(health["critical"] for health in runtime_health)
            if pion_loaded != CLIENT_COUNT or critical_logs:
                raise MeshFailure(
                    "native-runtime-health",
                    critical=critical_logs,
                    pion_loaded=pion_loaded,
                )
            apm_expected = sum(client.apm_expected for client in staged)
            apm_loaded = sum(
                bool(mesh.latest_stats[index].get("dsp_apm_loaded", False))
                for index, client in enumerate(staged)
                if client.apm_expected
            )
            if apm_loaded != apm_expected:
                raise MeshFailure(
                    "apm-runtime-health", expected=apm_expected, loaded=apm_loaded
                )

            health = mesh.health_summary()
            architectures = collections.Counter(client.architecture for client in staged)
            return {
                "apm_loaded": apm_loaded,
                "architectures": ",".join(
                    f"{name}:{architectures[name]}" for name in sorted(architectures)
                ),
                "churn_ms": format_ms(churn_duration),
                "duplicate_eoc": sum(mesh.eoc_messages.values())
                - sum(mesh.eoc_epochs.values()),
                "late_candidates_after_eoc": mesh.late_candidates_after_eoc,
                "feedback_ms": format_ms(feedback_duration),
                "health": health,
                "heartbeat_min": min(mesh.pongs.values()),
                "initial_ms": format_ms(initial_duration),
                "initial_level_entries": initial_level_entries,
                "initial_nonzero_levels": initial_nonzero_levels,
                "initial_packet_flows": initial_packet_flows,
                "protocol": protocol,
                "restart_feedback": mesh.restart_feedback_count,
                "restart_levels": mesh.restart_level_count,
                "restart_media_ms": format_ms(restart_media_duration),
                "restart_ms": format_ms(restart_duration),
                "stale_signals": mesh.stale_signals,
                "churn_feedback": mesh.churn_feedback_count,
                "churn_levels": mesh.churn_level_count,
                "startup_ms": format_ms(startup_duration),
                "total_ms": format_ms(time.monotonic() - total_started),
                "udp_baseline": ",".join(
                    str(count) for count in pre_peer_udp_sockets
                ),
                "udp_per_peer": sockets_per_peer,
                "udp_sockets": ",".join(str(count) for count in initial_udp_sockets),
            }
        finally:
            for helper in reversed(helpers):
                helper.close()


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    parser = build_parser(root)
    args = parser.parse_args()
    try:
        result = run(args, root)
    except MeshFailure as failure:
        print(f"P2P_MESH_SMOKE_FAILED {failure.summary()}", file=sys.stderr)
        return 1
    except Exception as exc:
        # Never echo arbitrary exception text: OS/network errors can include addresses, while
        # signaling exceptions could include SDP or candidates.
        print(
            f"P2P_MESH_SMOKE_FAILED reason=internal-error error={type(exc).__name__}",
            file=sys.stderr,
        )
        return 1

    health = result["health"]
    assert isinstance(health, dict)
    print(
        "P2P_MESH_SMOKE_OK "
        f"clients={CLIENT_COUNT} links={LINK_COUNT} endpoints={ENDPOINT_COUNT} "
        f"architectures={result['architectures']} relay_only=false "
        f"stun_servers={len(MANAGED_STUN_URLS)} pion={PION_VERSION} "
        f"protocol={result['protocol']} apm_loaded={result['apm_loaded']}"
    )
    print(
        "P2P_MESH_TIMING "
        f"startup_ms={result['startup_ms']} initial_ms={result['initial_ms']} "
        f"restart_ms={result['restart_ms']} restart_media_ms={result['restart_media_ms']} "
        f"churn_ms={result['churn_ms']} feedback_ms={result['feedback_ms']} "
        f"total_ms={result['total_ms']}"
    )
    print(
        "P2P_MESH_MEDIA "
        f"initial_packet_flows={result['initial_packet_flows']} "
        f"initial_level_entries={result['initial_level_entries']} "
        f"initial_nonzero_levels={result['initial_nonzero_levels']} "
        f"restart_packet_flows={result['restart_feedback']} "
        f"restart_level_entries={result['restart_levels']} "
        f"churn_packet_flows={result['churn_feedback']} "
        f"churn_level_entries={result['churn_levels']} "
        f"final_paths={ENDPOINT_COUNT} "
        f"relay_paths={health['relay_paths']} min_remote_packets={health['min_remote_packets']}"
    )
    print(
        "P2P_MESH_HEALTH "
        "critical_errors=0 "
        f"rtp_tx_errors={health['rtp_tx_errors']} "
        f"rtp_tx_queue_dropped={health['rtp_tx_queue_dropped']} "
        f"rtp_tx_write_timeouts={health['rtp_tx_write_timeouts']} "
        f"ingress_overflow={health['ingress_overflow']} "
        f"encoded_overflow={health['encoded_overflow']} "
        f"max_rtt_ms={float(health['max_rtt_ms']):.3f} "
        f"max_loss_pct={float(health['max_loss_pct']):.3f} "
        f"late_drops={health['late_drops']} plc_frames={health['plc_frames']} "
        f"local_media_gap_frames={health['local_media_gap_frames']} "
        f"min_pair_changes={health['min_pair_changes']} "
        f"stale_rx={health['stale_rx']} stale_signals={result['stale_signals']} "
        f"duplicate_eoc={result['duplicate_eoc']} "
        f"late_candidates_after_eoc={result['late_candidates_after_eoc']} "
        f"udp_baseline={result['udp_baseline']} udp_per_peer={result['udp_per_peer']} "
        f"udp_sockets={result['udp_sockets']} "
        f"heartbeat_min={result['heartbeat_min']} "
        f"candidate_types={health['candidate_types']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
