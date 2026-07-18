#!/usr/bin/env python3
"""Two-helper, relay-only Perfect Comms smoke using ephemeral TURN credentials.

Credentials, SDP, and ICE candidates stay in memory and are never printed. This is a
manual release diagnostic, not a normal CI test, because it allocates real TURN relay
traffic and depends on the deployed credential service.
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import re
import secrets
import socket
import struct
import subprocess
import tempfile
import threading
import time
import urllib.request
from pathlib import Path
from typing import Any


TYPE_CONTROL = 0x01
MAX_FRAME_BYTES = 1024 * 1024
DEFAULT_ENDPOINT = "https://perfect-comms-lobbies.edgetel.workers.dev/turn-credentials"


def source_protocol_version() -> int:
    root = Path(__file__).resolve().parents[1]
    managed = (root / "Comms" / "SidecarVoiceClient.cs").read_text(encoding="utf-8")
    native = (root / "native" / "pc-capture" / "src" / "proto.rs").read_text(
        encoding="utf-8"
    )
    managed_match = re.search(r"public const int Proto\s*=\s*(\d+)", managed)
    native_match = re.search(r"PROTO_VERSION:\s*u32\s*=\s*(\d+)", native)
    if managed_match is None or native_match is None:
        raise RuntimeError("could not read the sidecar protocol contract from source")
    managed_version = int(managed_match.group(1))
    native_version = int(native_match.group(1))
    if managed_version != native_version:
        raise RuntimeError(
            "managed/native sidecar protocol mismatch: "
            f"managed={managed_version} native={native_version}"
        )
    return managed_version


SIDECAR_PROTOCOL = source_protocol_version()


def recv_exact(sock: socket.socket, length: int) -> bytes:
    chunks: list[bytes] = []
    remaining = length
    while remaining:
        chunk = sock.recv(remaining)
        if not chunk:
            raise ConnectionError("helper closed its control connection")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def recv_frame(sock: socket.socket) -> tuple[int, bytes]:
    header = recv_exact(sock, 5)
    frame_type = header[0]
    length = struct.unpack("<I", header[1:])[0]
    if length > MAX_FRAME_BYTES:
        raise ValueError(f"helper frame exceeded safety bound: {length}")
    return frame_type, recv_exact(sock, length)


def fetch_turn_servers(endpoint: str, timeout: float) -> list[dict[str, Any]]:
    request = urllib.request.Request(
        endpoint,
        data=b"",
        method="POST",
        headers={"User-Agent": "PerfectComms-live-turn-smoke/1"},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        body = response.read(256 * 1024 + 1)
    if len(body) > 256 * 1024:
        raise ValueError("TURN credential response exceeded safety bound")
    payload = json.loads(body)
    servers: list[dict[str, Any]] = []
    for raw in payload.get("iceServers", []):
        urls = raw.get("urls", [])
        if isinstance(urls, str):
            urls = [urls]
        turn_urls = [
            value
            for value in urls
            if isinstance(value, str) and value.lower().startswith(("turn:", "turns:"))
        ]
        username = raw.get("username")
        credential = raw.get("credential")
        if turn_urls and isinstance(username, str) and username and isinstance(credential, str) and credential:
            servers.append({"urls": turn_urls, "username": username, "credential": credential})
    if not servers:
        raise ValueError("credential service returned no authenticated TURN server")
    return servers


class Helper:
    def __init__(self, name: str, executable: Path, work: Path, timeout: float) -> None:
        self.name = name
        self._events: queue.Queue[dict[str, Any]] = queue.Queue()
        self._write_lock = threading.Lock()
        self._reader_error: str | None = None
        self._stderr = tempfile.TemporaryFile()
        handshake = work / f"{name}-handshake.json"
        token = secrets.token_urlsafe(24)
        self.process = subprocess.Popen(
            [str(executable), "--synthetic-tone", "--handshake", str(handshake)],
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=self._stderr,
        )
        assert self.process.stdin is not None
        self.process.stdin.write((token + "\n").encode("utf-8"))
        self.process.stdin.flush()
        self.process.stdin.close()

        deadline = time.monotonic() + timeout
        port = 0
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError(f"helper {name} exited before handshake")
            try:
                port = int(json.loads(handshake.read_text(encoding="utf-8"))["port"])
                break
            except (FileNotFoundError, KeyError, ValueError, json.JSONDecodeError):
                time.sleep(0.02)
        if not port:
            raise TimeoutError(f"helper {name} did not publish a handshake port")

        self.socket = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.socket.settimeout(timeout)
        self.send({"op": "hello", "proto": SIDECAR_PROTOCOL, "token": token})
        frame_type, body = recv_frame(self.socket)
        ready = json.loads(body)
        if (
            frame_type != TYPE_CONTROL
            or ready.get("op") != "ready"
            or ready.get("proto") != SIDECAR_PROTOCOL
        ):
            raise RuntimeError(f"helper {name} returned an incompatible ready frame")

        self.socket.settimeout(None)
        self._reader = threading.Thread(target=self._read_loop, name=f"turn-smoke-{name}", daemon=True)
        self._reader.start()

    def _read_loop(self) -> None:
        try:
            while True:
                frame_type, body = recv_frame(self.socket)
                if frame_type != TYPE_CONTROL:
                    continue
                message = json.loads(body)
                if isinstance(message, dict):
                    self._events.put(message)
        except Exception as exc:  # diagnostics are deliberately sanitized below
            self._reader_error = type(exc).__name__

    def send(self, message: dict[str, Any]) -> None:
        body = json.dumps(message, separators=(",", ":")).encode("utf-8")
        if len(body) > MAX_FRAME_BYTES:
            raise ValueError("outbound helper control frame exceeded safety bound")
        frame = bytes([TYPE_CONTROL]) + struct.pack("<I", len(body)) + body
        with self._write_lock:
            self.socket.sendall(frame)

    def drain(self) -> list[dict[str, Any]]:
        result: list[dict[str, Any]] = []
        while True:
            try:
                result.append(self._events.get_nowait())
            except queue.Empty:
                return result

    def diagnostics(self, redactions: list[str]) -> str:
        self._stderr.flush()
        self._stderr.seek(0)
        text = self._stderr.read().decode("utf-8", errors="replace")
        for secret in redactions:
            if secret:
                text = text.replace(secret, "<redacted>")
        useful = [
            line.strip()
            for line in text.splitlines()
            if "peer creation failed" in line or "remote SDP failed" in line
        ]
        return " | ".join(useful[-4:])

    def close(self) -> None:
        try:
            self.send({"op": "stop"})
        except Exception:
            pass
        try:
            self.socket.close()
        except Exception:
            pass
        if self.process.poll() is None:
            self.process.kill()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
        self._stderr.close()


def configure(helper: Helper, remote_id: str, servers: list[dict[str, Any]]) -> None:
    helper.send({"op": "set-ice-servers", "servers": servers})
    helper.send({"op": "set-dsp", "aec": True, "agc": True, "ns": True,
                 "ns_very_high": True, "hpf": True})
    helper.send({"op": "set-diagnostics", "enabled": True})
    helper.send({"op": "set-input", "gain": 1.0, "vad_threshold": 0.005,
                 "noise_gate_threshold": 0.003})
    helper.send({"op": "set-synthetic", "enabled": True})
    helper.send(
        {
            "op": "game-state",
            "deaf": False,
            "master": 1.0,
            "peers": [{"id": remote_id, "gain": 1.0, "pan": 0.0, "mode": 0}],
        }
    )
    helper.send({"op": "start"})


def route_signal(source: Helper, target: Helper, target_peer_id: str, message: dict[str, Any]) -> None:
    operation = message.get("op")
    if operation == "local-sdp":
        target.send(
            {
                "op": "set-remote-sdp",
                "peer_id": target_peer_id,
                "sdp_type": message.get("sdp_type", ""),
                "sdp": message.get("sdp", ""),
            }
        )
    elif operation == "local-candidate":
        target.send(
            {
                "op": "add-ice-candidate",
                "peer_id": target_peer_id,
                "candidate": message.get("candidate", ""),
            }
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("helper", type=Path)
    parser.add_argument("--endpoint", default=os.environ.get("PC_TURN_CREDENTIAL_URL", DEFAULT_ENDPOINT))
    parser.add_argument("--timeout", type=float, default=45.0)
    parser.add_argument("--direct", action="store_true", help="diagnostic control: allow direct ICE")
    parser.add_argument(
        "--first-turn-url",
        action="store_true",
        help="diagnostic control: use only the first authenticated TURN URL",
    )
    args = parser.parse_args()

    executable = args.helper.resolve()
    if not executable.is_file():
        parser.error(f"helper does not exist: {executable}")

    # A direct smoke must not depend on the credential service or accidentally succeed through a
    # relay. Relay mode fetches authenticated TURN and forces relay-only ICE below.
    servers = [] if args.direct else fetch_turn_servers(args.endpoint, min(args.timeout, 15.0))
    if args.first_turn_url and servers:
        servers = [{**server, "urls": server["urls"][:1]} for server in servers]
    helpers: list[Helper] = []
    connected: set[str] = set()
    heard: set[str] = set()
    errors: list[str] = []
    states: dict[str, list[str]] = {"a": [], "b": []}
    relay_candidates: dict[str, int] = {"a": 0, "b": 0}
    relay_only = not args.direct
    try:
        with tempfile.TemporaryDirectory(prefix="perfect-comms-turn-") as temp:
            work = Path(temp)
            a = Helper("a", executable, work, min(args.timeout, 15.0))
            helpers.append(a)
            b = Helper("b", executable, work, min(args.timeout, 15.0))
            helpers.append(b)

            configure(a, "b", servers)
            configure(b, "a", servers)
            b.send({"op": "peer-add", "peer_id": "a", "offerer": False, "relay_only": relay_only, "generation": 1})
            a.send({"op": "peer-add", "peer_id": "b", "offerer": True, "relay_only": relay_only, "generation": 1})

            deadline = time.monotonic() + args.timeout
            while time.monotonic() < deadline and (len(connected) < 2 or len(heard) < 2):
                for source, target, target_peer in ((a, b, "a"), (b, a, "b")):
                    for message in source.drain():
                        route_signal(source, target, target_peer, message)
                        operation = message.get("op")
                        if operation == "local-candidate":
                            candidate = str(message.get("candidate", ""))
                            if re.search(r"\btyp\s+relay\b", candidate, re.IGNORECASE):
                                relay_candidates[source.name] += 1
                        if operation == "peer-state" and message.get("generation") == 1:
                            state = str(message.get("state", ""))
                            if not states[source.name] or states[source.name][-1] != state:
                                states[source.name].append(state)
                            if state == "connected":
                                connected.add(source.name)
                            elif state in {"failed", "closed"}:
                                errors.append(f"{source.name}:{state}")
                        elif operation == "peer-levels":
                            levels = message.get("levels", [])
                            if any(
                                isinstance(level, dict)
                                and level.get("peer_id") == ("b" if source is a else "a")
                                and isinstance(level.get("peak"), (int, float))
                                and float(level["peak"]) > 0.001
                                for level in levels
                            ):
                                heard.add(source.name)
                        elif operation == "error":
                            errors.append(f"{source.name}:{message.get('code', 'error')}")
                time.sleep(0.01)

            if len(connected) != 2 or len(heard) != 2:
                reader_errors = [f"{helper.name}:{helper._reader_error}" for helper in helpers if helper._reader_error]
                detail = ",".join(errors + reader_errors) or "timeout"
                redactions = [
                    value
                    for server in servers
                    for value in (str(server.get("username", "")), str(server.get("credential", "")))
                    if value
                ]
                native = [f"{helper.name}:{helper.diagnostics(redactions)}" for helper in helpers]
                raise RuntimeError(
                    "relay smoke failed "
                    f"(connected={len(connected)}/2 heard={len(heard)}/2 detail={detail} "
                    f"states={states} relay_candidates={relay_candidates} native={native})"
                )
    finally:
        for helper in reversed(helpers):
            helper.close()

    mode = "TURN_RELAY" if relay_only else "DIRECT"
    print(f"{mode}_SMOKE_OK helpers=2 relay_only={str(relay_only).lower()} connected=2 remote_levels=2")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
