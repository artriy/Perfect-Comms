#!/usr/bin/env bash
set -euo pipefail

helper="${1:?usage: ci-smoke-helper.sh <helper-binary>}"
name="$(basename "$helper")"
hs="$(mktemp -u)-pc-smoke.json"
rm -f "$hs"

python3 - "$helper" "$hs" "$name" <<'PY'
import json, socket, struct, subprocess, sys, time, os

helper, hs, name = sys.argv[1], sys.argv[2], sys.argv[3]
proc = subprocess.Popen([helper, "--synthetic-tone", "--handshake", hs],
                        stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
proc.stdin.write(b"ci-token\n"); proc.stdin.flush()

deadline = time.time() + 15
port = None
while time.time() < deadline:
    try:
        with open(hs) as f:
            port = json.load(f)["port"]; break
    except Exception:
        time.sleep(0.05)
assert port, "no handshake port"

s = socket.create_connection(("127.0.0.1", port), timeout=5)
s.settimeout(5)

def send_control(obj):
    b = json.dumps(obj).encode()
    s.sendall(bytes([0x01]) + struct.pack("<I", len(b)) + b)

def recv_frame():
    hdr = b""
    while len(hdr) < 5:
        hdr += s.recv(5 - len(hdr))
    t, ln = hdr[0], struct.unpack("<I", hdr[1:5])[0]
    body = b""
    while len(body) < ln:
        body += s.recv(ln - len(body))
    return t, body

send_control({"op": "hello", "proto": 4, "token": "ci-token"})
t, body = recv_frame()
assert t == 0x01, "first reply not CONTROL"
ready = json.loads(body)
assert ready["op"] == "ready", ready
assert ready["format"] == {"rate": 48000, "channels": 1, "sample": "f32"}, ready

send_control({"op": "start"})
levels = 0
while levels < 2:
    t, body = recv_frame()
    if t != 0x01:
        continue
    msg = json.loads(body)
    if msg.get("op") != "level":
        continue
    assert "speaking" in msg, msg
    levels += 1

proc.kill()
os.remove(hs)
print(f"SMOKE_OK {name}")
PY
