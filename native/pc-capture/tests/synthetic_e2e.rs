use std::io::{BufReader, Read, Write};
use std::net::TcpStream;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::OnceLock;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const TYPE_CONTROL: u8 = 0x01;
const TYPE_AUDIO: u8 = 0x02;
const FRAME_BYTES: usize = 8 + 960 * 4;

fn encode_control(json: &str) -> Vec<u8> {
    let body = json.as_bytes();
    let mut out = Vec::with_capacity(5 + body.len());
    out.push(TYPE_CONTROL);
    out.extend_from_slice(&(body.len() as u32).to_le_bytes());
    out.extend_from_slice(body);
    out
}

enum Frame {
    Control(String),
    Audio,
}

fn read_frame<R: Read>(r: &mut R) -> std::io::Result<Frame> {
    let mut header = [0u8; 5];
    r.read_exact(&mut header)?;
    let ftype = header[0];
    let len = u32::from_le_bytes([header[1], header[2], header[3], header[4]]) as usize;
    let mut body = vec![0u8; len];
    r.read_exact(&mut body)?;
    match ftype {
        TYPE_CONTROL => Ok(Frame::Control(String::from_utf8_lossy(&body).into_owned())),
        TYPE_AUDIO => {
            assert_eq!(len, FRAME_BYTES, "audio frame wrong size");
            Ok(Frame::Audio)
        }
        other => panic!("unknown frame type {other}"),
    }
}

struct KillOnDrop(Child);

impl KillOnDrop {
    fn terminate(&mut self) {
        if self.0.try_wait().ok().flatten().is_none() {
            let _ = self.0.kill();
        }
        let _ = self.0.wait();
    }

    fn wait_for_exit(&mut self, timeout: Duration) -> std::io::Result<Option<ExitStatus>> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(status) = self.0.try_wait()? {
                return Ok(Some(status));
            }
            if Instant::now() >= deadline {
                return Ok(None);
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    }
}

impl Drop for KillOnDrop {
    fn drop(&mut self) {
        self.terminate();
    }
}

fn parse_port(body: &str) -> Option<u16> {
    let key = "\"port\":";
    let start = body.find(key)? + key.len();
    let rest = &body[start..];
    let end = rest.find(|c: char| !c.is_ascii_digit())?;
    rest[..end].parse().ok()
}

fn unique_handshake_path(label: &str) -> PathBuf {
    std::env::temp_dir().join(format!(
        "pc-e2e-{label}-{}-{}.json",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0)
    ))
}

fn spawn_helper(exe: &str, handshake: &Path, owner_pid: Option<u32>) -> KillOnDrop {
    static PION_STAGED: OnceLock<()> = OnceLock::new();
    PION_STAGED.get_or_init(|| {
        if std::env::var_os("PC_REQUIRE_PION").is_none() {
            return;
        }
        let source = PathBuf::from(
            std::env::var_os("PC_PION_LIB")
                .expect("PC_REQUIRE_PION requires PC_PION_LIB for integration tests"),
        );
        let destination = Path::new(exe)
            .parent()
            .expect("test helper has no parent directory")
            .join(pion_sys::platform_library_name());
        if source != destination {
            std::fs::copy(&source, &destination).unwrap_or_else(|error| {
                panic!(
                    "stage Pion test transport {} -> {}: {error}",
                    source.display(),
                    destination.display()
                )
            });
        }
    });
    let mut command = Command::new(exe);
    command
        .arg("--handshake")
        .arg(handshake)
        .arg("--synthetic-tone")
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    if let Some(pid) = owner_pid {
        command.arg("--owner-pid").arg(pid.to_string());
    }

    let mut child = command.spawn().expect("spawn pc-capture");
    child
        .stdin
        .take()
        .unwrap()
        .write_all(b"e2e-token\n")
        .expect("write token");
    KillOnDrop(child)
}

fn wait_for_port(handshake: &Path, timeout: Duration) -> u16 {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        if let Ok(body) = std::fs::read_to_string(handshake) {
            if let Some(port) = parse_port(&body) {
                return port;
            }
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    panic!("helper never wrote handshake port: {}", handshake.display());
}

#[test]
fn synthetic_helper_survives_stop_and_exits_promptly_on_control_eof() {
    let exe = env!("CARGO_BIN_EXE_pc-capture");
    let hs = unique_handshake_path("control-eof");
    let _ = std::fs::remove_file(&hs);

    let mut guard = spawn_helper(exe, &hs, None);
    let port = wait_for_port(&hs, Duration::from_secs(10));

    let mut client = TcpStream::connect(("127.0.0.1", port)).expect("connect helper");
    client.set_read_timeout(Some(Duration::from_secs(5))).ok();
    client.set_nodelay(true).ok();
    client
        .write_all(&encode_control(
            r#"{"op":"hello","proto":15,"token":"e2e-token"}"#,
        ))
        .unwrap();

    let mut reader = BufReader::new(client.try_clone().unwrap());
    match read_frame(&mut reader).expect("read ready") {
        Frame::Control(s) => {
            assert!(s.contains("\"ready\""), "expected ready, got {s}");
            assert!(s.contains("48000"), "expected 48000 rate, got {s}");
        }
        Frame::Audio => panic!("expected ready before audio"),
    }

    client
        .write_all(&encode_control(r#"{"op":"ping"}"#))
        .unwrap();
    // Extended signal windows/stats are intentionally opt-in in production.
    client
        .write_all(&encode_control(
            r#"{"op":"set-diagnostics","enabled":true}"#,
        ))
        .unwrap();
    client
        .write_all(&encode_control(r#"{"op":"start"}"#))
        .unwrap();

    let mut got_pong = false;
    let mut level_frames = 0;
    let deadline = Instant::now() + Duration::from_secs(8);
    while Instant::now() < deadline && (!got_pong || level_frames < 2) {
        match read_frame(&mut reader) {
            Ok(Frame::Control(s)) => {
                if s.contains("\"pong\"") {
                    got_pong = true;
                }
                if s.contains("\"level\"") {
                    level_frames += 1;
                }
            }
            Ok(Frame::Audio) => {}
            Err(_) => break,
        }
    }

    assert!(got_pong, "never received pong");
    assert!(
        level_frames >= 2,
        "expected >=2 level frames, got {level_frames}"
    );

    client
        .write_all(&encode_control(r#"{"op":"stop"}"#))
        .unwrap();
    std::thread::sleep(Duration::from_millis(150));
    assert!(
        guard.0.try_wait().unwrap().is_none(),
        "capture stop/lobby transition must not terminate the helper"
    );

    client
        .write_all(&encode_control(r#"{"op":"start"}"#))
        .unwrap();
    let mut got_second_generation = false;
    let mut got_extended_stats = false;
    let mut got_level_after_restart = false;
    let restart_deadline = Instant::now() + Duration::from_secs(5);
    while Instant::now() < restart_deadline
        && (!got_second_generation || !got_extended_stats || !got_level_after_restart)
    {
        match read_frame(&mut reader) {
            Ok(Frame::Control(body)) => {
                let Ok(value) = serde_json::from_str::<serde_json::Value>(&body) else {
                    continue;
                };
                match value["op"].as_str() {
                    Some("media-state") => {
                        if value["direction"] == "capture"
                            && value["stream_generation"] == 2
                            && value["running"] == true
                        {
                            got_second_generation = true;
                        }
                    }
                    Some("stats") => {
                        if value["diagnostics"]["schema"] == 1
                            && value["diagnostics"]["capture"]["stream_generation"] == 2
                            && value["diagnostics"]["capture"]["running"] == true
                        {
                            got_extended_stats = true;
                        }
                    }
                    Some("level") => got_level_after_restart = true,
                    _ => {}
                }
            }
            Ok(Frame::Audio) => {}
            Err(_) => break,
        }
    }
    assert!(
        got_second_generation,
        "restart did not acknowledge generation 2"
    );
    assert!(
        got_extended_stats,
        "restart did not emit schema-1 capture stats"
    );
    assert!(
        got_level_after_restart,
        "restart did not resume capture levels"
    );

    let disconnected_at = Instant::now();
    drop(reader);
    drop(client);
    let status = guard
        .wait_for_exit(Duration::from_secs(5))
        .unwrap()
        .expect("helper stayed alive after control EOF");
    assert!(status.success(), "helper exited unsuccessfully: {status}");
    assert!(
        disconnected_at.elapsed() < Duration::from_secs(2),
        "healthy cleanup waited for the hard deadline"
    );
    let _ = std::fs::remove_file(&hs);
}

#[test]
fn helper_exits_when_explicit_owner_process_dies() {
    let exe = env!("CARGO_BIN_EXE_pc-capture");
    let owner_hs = unique_handshake_path("owner");
    let child_hs = unique_handshake_path("owned");
    let _ = std::fs::remove_file(&owner_hs);
    let _ = std::fs::remove_file(&child_hs);

    // A second helper is a portable, long-lived owner process for this test. It intentionally
    // has no control client and therefore remains in its startup accept window until killed.
    let mut owner = spawn_helper(exe, &owner_hs, None);
    let _ = wait_for_port(&owner_hs, Duration::from_secs(10));
    let owner_pid = owner.0.id();

    let mut owned = spawn_helper(exe, &child_hs, Some(owner_pid));
    let _ = wait_for_port(&child_hs, Duration::from_secs(10));
    assert!(owned.0.try_wait().unwrap().is_none());

    owner.terminate();
    let status = owned
        .wait_for_exit(Duration::from_secs(4))
        .unwrap()
        .expect("helper stayed alive after its explicit owner exited");
    assert!(
        status.success(),
        "owner guard exit was unsuccessful: {status}"
    );

    let _ = std::fs::remove_file(&owner_hs);
    let _ = std::fs::remove_file(&child_hs);
}
