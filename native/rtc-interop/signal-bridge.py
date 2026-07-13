import os
import subprocess
import sys
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
OFFER_EXE = os.path.join(HERE, "target", "debug", "rtc-interop.exe")
ANSWER_EXE = os.path.join(HERE, "sipsorcery-answer", "bin", "Release", "net10.0", "sipsorcery-answer.exe")


def pump_stderr(proc, tag):
    for line in proc.stderr:
        sys.stdout.write(f"{tag} {line.rstrip()}\n")
        sys.stdout.flush()


def main():
    for exe in (OFFER_EXE, ANSWER_EXE):
        if not os.path.exists(exe):
            print(f"BRIDGE: missing binary {exe}", flush=True)
            return 10

    offer = subprocess.Popen(
        [OFFER_EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, bufsize=1)
    answer = subprocess.Popen(
        [ANSWER_EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, bufsize=1)

    threading.Thread(target=pump_stderr, args=(offer, "[offer]"), daemon=True).start()
    threading.Thread(target=pump_stderr, args=(answer, "[answer]"), daemon=True).start()

    offer_sdp = offer.stdout.readline()
    if not offer_sdp.strip():
        print("BRIDGE: offerer produced no offer SDP", flush=True)
        offer.kill(); answer.kill()
        return 11
    print(f"BRIDGE: offer SDP ({len(offer_sdp)} bytes) -> answerer", flush=True)
    answer.stdin.write(offer_sdp)
    answer.stdin.flush()

    answer_sdp = answer.stdout.readline()
    if not answer_sdp.strip():
        print("BRIDGE: answerer produced no answer SDP", flush=True)
        offer.kill(); answer.kill()
        return 12
    print(f"BRIDGE: answer SDP ({len(answer_sdp)} bytes) -> offerer", flush=True)
    offer.stdin.write(answer_sdp)
    offer.stdin.flush()

    try:
        oc = offer.wait(timeout=45)
    except subprocess.TimeoutExpired:
        offer.kill(); oc = -1
    try:
        ac = answer.wait(timeout=45)
    except subprocess.TimeoutExpired:
        answer.kill(); ac = -1

    print(f"BRIDGE: offerer exit={oc} answerer exit={ac}", flush=True)
    return 0 if (oc == 0 and ac == 0) else 20


if __name__ == "__main__":
    sys.exit(main())
