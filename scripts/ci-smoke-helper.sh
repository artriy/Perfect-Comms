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

    send_control({"op": "set-dsp", "aec": True, "agc": False, "ns": True,
                  "ns_very_high": True, "hpf": True})
    send_control({"op": "set-diagnostics", "enabled": True})
    send_control({"op": "set-input", "gain": 1.0, "vad_threshold": 0.01,
                  "noise_gate_threshold": 0.003})
    send_control({"op": "set-synthetic", "enabled": True})
    # Exercise the protocol-13 monitor command on every helper without requiring an audio output
    # device on headless CI runners. Enabled monitor mixing is covered by focused audio tests.
    send_control({"op": "set-monitor", "enabled": False, "delay_ms": 0, "gain": 1.0})
    send_control({"op": "start"})
    levels = 0
    stats_seen = False
    dsp_generation = None
    deadline2 = time.time() + 15
    while levels < 2 or not stats_seen:
        if time.time() > deadline2:
            raise RuntimeError("did not receive 2 level frames within 15s")
        t, body = recv_frame()
        if t != 0x01:
            continue
        msg = json.loads(body)
        if msg.get("op") == "level":
            assert "speaking" in msg, msg
            levels += 1
        elif msg.get("op") == "stats":
            assert msg.get("input_noise_gate_threshold") == 0.003, msg
            assert isinstance(msg.get("media_receive"), dict), msg
            assert isinstance(msg.get("network_paths"), list), msg
            assert isinstance(msg.get("encoder_packet_loss_percent"), int), msg
            assert isinstance(msg.get("encoder_bitrate"), int), msg
            assert msg.get("diagnostics", {}).get("schema") == 1, msg
            dsp_generation = msg.get("dsp_config_generation")
            assert isinstance(dsp_generation, int), msg
            assert msg.get("dsp_requested_ns") is True, msg
            assert msg.get("dsp_requested_ns_very_high") is True, msg
            if require_dsp == "--require-dsp":
                assert msg.get("dsp_applied_ns") is True, msg
                assert msg.get("dsp_applied_ns_very_high") is True, msg
                assert msg.get("dsp_config_fully_applied") is True, msg
            stats_seen = True

    def wait_for_dsp_state(after_generation, ns, ns_very_high):
        deadline = time.time() + 15
        while time.time() <= deadline:
            frame_type, frame_body = recv_frame()
            if frame_type != 0x01:
                continue
            message = json.loads(frame_body)
            if message.get("op") != "stats":
                continue
            generation = message.get("dsp_config_generation")
            if not isinstance(generation, int) or generation <= after_generation:
                continue
            expected = {
                "dsp_requested_aec": True,
                "dsp_requested_agc": False,
                "dsp_requested_ns": ns,
                "dsp_requested_ns_very_high": ns and ns_very_high,
                "dsp_requested_hpf": True,
                "dsp_apm_loaded": True,
                "dsp_config_fully_applied": True,
                "dsp_applied_aec": True,
                "dsp_applied_agc": False,
                "dsp_applied_ns": ns,
                "dsp_applied_ns_very_high": ns and ns_very_high,
                "dsp_applied_hpf": True,
            }
            for key, value in expected.items():
                assert message.get(key) is value, message
            return generation
        raise RuntimeError("DSP reconfiguration was not confirmed by diagnostics within 15s")

    # Runtime suppression changes must reconfigure the already-loaded WebRTC APM in place.
    send_control({"op": "set-dsp", "aec": True, "agc": False, "ns": False,
                  "ns_very_high": False, "hpf": True})
    if require_dsp == "--require-dsp":
        dsp_generation = wait_for_dsp_state(dsp_generation, False, False)
    send_control({"op": "set-dsp", "aec": True, "agc": False, "ns": True,
                  "ns_very_high": True, "hpf": True})
    if require_dsp == "--require-dsp":
        dsp_generation = wait_for_dsp_state(dsp_generation, True, True)

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
    suppression_off = "dsp set apm=true webrtc-ns=false webrtc-ns-level=high automatic-gain=false"
    suppression_on = "dsp set apm=true webrtc-ns=true webrtc-ns-level=very-high automatic-gain=false"
    suppression_off_position = log.find(suppression_off)
    assert suppression_off_position >= 0, \
        "suppression-off toggle did not reconfigure WebRTC APM:\n" + log
    assert log.find(suppression_on, suppression_off_position + len(suppression_off)) >= 0, \
        "final helper bundle could not load and reconfigure WebRTC APM:\n" + log
print(f"SMOKE_OK {name} stop_reusable=true eof_exit_seconds={disconnected_elapsed:.3f}")
PY
