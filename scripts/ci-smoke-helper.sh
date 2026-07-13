#!/usr/bin/env bash
set -euo pipefail

helper="${1:?usage: ci-smoke-helper.sh <helper-binary> [--require-dsp]}"
require_dsp="${2:-}"
name="$(basename "$helper")"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
managed_proto="$(sed -nE 's/.*public const int Proto = ([0-9]+).*/\1/p' "$root/Comms/SidecarVoiceClient.cs" | head -n1)"
[[ -n "$managed_proto" ]] || { echo "Could not read managed sidecar protocol" >&2; exit 1; }
helper_proto="$("$helper" --protocol-version)"
[[ "$helper_proto" == "$managed_proto" ]] || {
  echo "Sidecar protocol mismatch: managed=$managed_proto helper=$helper_proto helper=$helper" >&2
  exit 1
}
hs="$(mktemp -u)-pc-smoke.json"
rm -f "$hs"

if python3 -c 'import sys' >/dev/null 2>&1; then
  python_cmd=python3
elif python -c 'import sys' >/dev/null 2>&1; then
  python_cmd=python
else
  echo "Python 3 is required for the helper smoke" >&2
  exit 1
fi

"$python_cmd" - "$helper" "$hs" "$name" "$require_dsp" "$managed_proto" <<'PY'
import json, socket, struct, subprocess, sys, time, os

helper, hs, name, require_dsp, managed_proto = sys.argv[1:6]
proc = subprocess.Popen([helper, "--synthetic-tone", "--handshake", hs],
                        stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                        text=False)
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

def recv_exact(n):
    buf = b""
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            raise RuntimeError("helper closed the connection before sending a full frame "
                               "(proto mismatch or early exit?)")
        buf += chunk
    return buf

def recv_frame():
    hdr = recv_exact(5)
    t, ln = hdr[0], struct.unpack("<I", hdr[1:5])[0]
    return t, recv_exact(ln)

failure = None
disconnected_elapsed = None
try:
    send_control({"op": "hello", "proto": int(managed_proto), "token": "ci-token"})
    t, body = recv_frame()
    assert t == 0x01, "first reply not CONTROL"
    ready = json.loads(body)
    assert ready["op"] == "ready", ready
    assert ready["format"] == {"rate": 48000, "channels": 1, "sample": "f32"}, ready

    send_control({"op": "set-dsp", "aec": True, "agc": False, "ns": True, "hpf": True})
    send_control({"op": "set-input", "gain": 1.0, "vad_threshold": 0.01})
    send_control({"op": "set-synthetic", "enabled": True})
    send_control({"op": "start"})
    levels = 0
    deadline2 = time.time() + 15
    while levels < 2:
        if time.time() > deadline2:
            raise RuntimeError("did not receive 2 level frames within 15s")
        t, body = recv_frame()
        if t != 0x01:
            continue
        msg = json.loads(body)
        if msg.get("op") != "level":
            continue
        assert "speaking" in msg, msg
        levels += 1

    # Runtime suppression changes must reconfigure the already-loaded WebRTC APM in place.
    send_control({"op": "set-dsp", "aec": True, "agc": False, "ns": False, "hpf": True})
    send_control({"op": "set-dsp", "aec": True, "agc": False, "ns": True, "hpf": True})

    # A lobby/session stop must leave the process reusable. Only control EOF (or owner exit)
    # owns process lifetime, including the Wine/CrossOver path where guest PIDs are unusable.
    send_control({"op": "stop"})
    time.sleep(0.2)
    assert proc.poll() is None, "helper exited on stop instead of remaining idle"

    disconnected_at = time.monotonic()
    s.shutdown(socket.SHUT_RDWR)
    s.close()
    s = None
    try:
        return_code = proc.wait(timeout=5)
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError("helper stayed alive more than 5s after control EOF") from exc
    disconnected_elapsed = time.monotonic() - disconnected_at
    assert return_code == 0, f"helper exited unsuccessfully after EOF: {return_code}"
except Exception as exc:
    failure = exc
finally:
    if s is not None:
        try:
            s.close()
        except OSError:
            pass
    if proc.poll() is None:
        proc.kill()
    _, stderr = proc.communicate(timeout=5)
    try:
        os.remove(hs)
    except OSError:
        pass
log = stderr.decode("utf-8", errors="replace")
if failure is not None:
    raise RuntimeError(f"{failure}\nhelper stderr:\n{log}") from failure
if require_dsp == "--require-dsp":
    assert "dsp set apm=true webrtc-ns=false automatic-gain=false" in log, \
        "suppression-off toggle did not reconfigure WebRTC APM:\n" + log
    assert "dsp set apm=true webrtc-ns=true automatic-gain=false" in log, \
        "final helper bundle could not load and reconfigure WebRTC APM:\n" + log
print(f"SMOKE_OK {name} stop_reusable=true eof_exit_seconds={disconnected_elapsed:.3f}")
PY
