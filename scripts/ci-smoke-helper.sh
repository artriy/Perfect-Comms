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

if [[ "$(uname -s)" == "Darwin" && "$helper" == *".app/Contents/MacOS/"* ]]; then
  "$python_cmd" - "$helper" <<'PY'
import json, os, secrets, socket, stat, struct, subprocess, sys, time

helper = os.path.abspath(sys.argv[1])
marker = ".app/Contents/MacOS/"
app = helper.split(marker, 1)[0] + ".app"
bundle_parent = os.path.dirname(app)
request_directory = os.path.join(bundle_parent, ".perfect-comms-broker-v1")
os.makedirs(request_directory, mode=0o755, exist_ok=True)
os.chmod(request_directory, 0o755)

request_nonce = secrets.token_hex(32)
private_directory = os.path.join("/tmp", "perfect-comms-" + secrets.token_hex(16))
pending_token = os.path.join(private_directory, ".token.pending")
receipt = os.path.join(private_directory, ".bootstrap-ready")
launch_owned = os.path.join(private_directory, ".launch-owned")
expected_receipt = "perfect-comms-host-action-v1:" + secrets.token_hex(32)
request_path = os.path.join(request_directory, request_nonce + ".json")
temporary_request = request_path + ".tmp"
request = {
    "schema": 1,
    "operation": "prepare-private-directory",
    "request_nonce": request_nonce,
    "private_directory": private_directory,
    "pending_token": pending_token,
    "receipt": receipt,
    "expected_receipt": expected_receipt,
    "launch_owned": launch_owned,
}
descriptor = os.open(temporary_request, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(descriptor, "w", encoding="utf-8") as output:
    json.dump(request, output, separators=(",", ":"))
os.replace(temporary_request, request_path)

# Reproduce Windows-target ZipFile extraction under CrossOver: archive modes are lost and a
# downloaded bundle may be quarantined. The runtime preflight must repair metadata without
# changing the signed bundle's contents before LaunchServices starts it.
os.chmod(helper, stat.S_IMODE(os.stat(helper).st_mode) & ~0o111)
assert stat.S_IMODE(os.stat(helper).st_mode) & 0o111 == 0
subprocess.run(
    ["/usr/bin/xattr", "-w", "com.apple.quarantine", "0081;00000000;PerfectCommsCI;", app],
    check=True,
)
subprocess.run(["/bin/chmod", "u+x", helper], check=True)
subprocess.run(
    ["/usr/bin/xattr", "-dr", "com.apple.quarantine", app],
    check=True,
)
assert stat.S_IMODE(os.stat(helper).st_mode) & stat.S_IXUSR
quarantine = subprocess.run(
    ["/usr/bin/xattr", "-p", "com.apple.quarantine", app],
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
)
assert quarantine.returncode != 0
subprocess.run(
    ["/usr/bin/codesign", "--verify", "--deep", "--strict", app],
    check=True,
)

subprocess.run(["/usr/bin/open", "-g", "-j", "-n", app], check=True)
deadline = time.time() + 15
while time.time() < deadline:
    try:
        with open(receipt, encoding="utf-8") as source:
            if source.read() == expected_receipt:
                break
    except OSError:
        pass
    time.sleep(0.05)
else:
    raise RuntimeError("argument-free macOS app broker did not publish its bootstrap receipt")

assert stat.S_IMODE(os.stat(private_directory).st_mode) == 0o700
assert stat.S_IMODE(os.stat(pending_token).st_mode) == 0o600

os.remove(pending_token)
token_file = os.path.join(private_directory, "token")
descriptor = os.open(token_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(descriptor, "w", encoding="utf-8") as output:
    output.write("ci-broker-token")
handshake_file = os.path.join(private_directory, "handshake.json")
launch_started = os.path.join(private_directory, ".launch-started")
launch_failed = os.path.join(private_directory, ".launch-failed")
helper_exited = os.path.join(private_directory, ".helper-exited")
launch_cancelled = os.path.join(private_directory, ".launch-cancelled")
launch_nonce = secrets.token_hex(32)
launch_request_nonce = secrets.token_hex(32)
expected_build_info = subprocess.check_output(
    [helper, "--build-info"], text=True, timeout=5
).strip()
arguments = [
    "--handshake", handshake_file,
    "--cancel-file", launch_cancelled,
    "--cancel-nonce", launch_nonce,
    "--token-file", token_file,
]
launch_request = {
    "schema": 1,
    "operation": "launch-helper",
    "request_nonce": launch_request_nonce,
    "private_directory": private_directory,
    "token_file": token_file,
    "handshake_file": handshake_file,
    "launch_owned": launch_owned,
    "launch_started": launch_started,
    "launch_failed": launch_failed,
    "helper_exited": helper_exited,
    "launch_cancelled": launch_cancelled,
    "launch_nonce": launch_nonce,
    "expected_build_info": expected_build_info,
    "arguments": arguments,
}
launch_request_path = os.path.join(request_directory, launch_request_nonce + ".json")
temporary_launch_request = launch_request_path + ".tmp"
descriptor = os.open(
    temporary_launch_request, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600
)
with os.fdopen(descriptor, "w", encoding="utf-8") as output:
    json.dump(launch_request, output, separators=(",", ":"))
os.replace(temporary_launch_request, launch_request_path)
subprocess.run(["open", "-g", app], check=True)

deadline = time.time() + 15
handshake = None
while time.time() < deadline:
    if os.path.exists(launch_failed):
        with open(launch_failed, encoding="utf-8") as source:
            raise RuntimeError("macOS broker launch failed: " + source.read())
    try:
        with open(handshake_file, encoding="utf-8") as source:
            handshake = json.load(source)
        with open(launch_started, encoding="utf-8") as source:
            started = source.read()
        if started == (
            "perfect-comms-launch-started-v1:"
            + launch_nonce
            + ":"
            + str(handshake["pid"])
        ):
            break
    except (OSError, KeyError, ValueError):
        handshake = None
    time.sleep(0.05)
else:
    raise RuntimeError("macOS app broker did not start a helper child with a handshake")

def recv_exact(connection, length):
    body = b""
    while len(body) < length:
        chunk = connection.recv(length - len(body))
        if not chunk:
            raise RuntimeError("macOS broker child closed during its ready frame")
        body += chunk
    return body

connection = socket.create_connection(("127.0.0.1", handshake["port"]), timeout=5)
hello = json.dumps(
    {
        "op": "hello",
        "proto": int(json.loads(expected_build_info)["protocol"]),
        "token": "ci-broker-token",
    }
).encode()
connection.sendall(bytes([0x01]) + struct.pack("<I", len(hello)) + hello)
header = recv_exact(connection, 5)
assert header[0] == 0x01
body_length = struct.unpack("<I", header[1:])[0]
body = recv_exact(connection, body_length)
assert json.loads(body)["op"] == "ready"
connection.shutdown(socket.SHUT_RDWR)
connection.close()

deadline = time.time() + 20
while time.time() < deadline and os.path.isdir(private_directory):
    time.sleep(0.05)
if os.path.isdir(private_directory):
    raise RuntimeError("macOS broker did not supervise child exit and clean its private directory")
os.rmdir(request_directory)
print(
    "MAC_BROKER_SMOKE_OK argument_free_app=true permission_repaired=true "
    "quarantine_removed=true private_mode=0700 token_mode=0600 child_handshake=true"
)
PY
fi

"$python_cmd" "$root/scripts/verify-release-assets.py" \
  --helper-build-info "$helper" \
  --expected-protocol "$managed_proto"

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
