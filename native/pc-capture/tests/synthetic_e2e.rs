use std::io::{BufReader, Read, Write};
use std::net::TcpStream;
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

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
    Audio(usize),
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
            let samples = (len - 8) / 4;
            Ok(Frame::Audio(samples))
        }
        other => panic!("unknown frame type {other}"),
    }
}

struct KillOnDrop(Child);
impl Drop for KillOnDrop {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

fn parse_port(body: &str) -> Option<u16> {
    let key = "\"port\":";
    let start = body.find(key)? + key.len();
    let rest = &body[start..];
    let end = rest.find(|c: char| !c.is_ascii_digit())?;
    rest[..end].parse().ok()
}

#[test]
fn synthetic_helper_streams_audio_end_to_end() {
    let exe = env!("CARGO_BIN_EXE_pc-capture");
    let hs = std::env::temp_dir().join(format!(
        "pc-e2e-hs-{}-{}.json",
        std::process::id(),
        Instant::now().elapsed().as_nanos()
    ));
    let _ = std::fs::remove_file(&hs);

    let mut child = Command::new(exe)
        .arg("--handshake")
        .arg(&hs)
        .arg("--synthetic-tone")
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("spawn pc-capture");

    child
        .stdin
        .take()
        .unwrap()
        .write_all(b"e2e-token\n")
        .expect("write token");

    let guard = KillOnDrop(child);

    let mut port = 0u16;
    let deadline = Instant::now() + Duration::from_secs(10);
    while Instant::now() < deadline {
        if let Ok(body) = std::fs::read_to_string(&hs) {
            if let Some(p) = parse_port(&body) {
                port = p;
                break;
            }
        }
        std::thread::sleep(Duration::from_millis(20));
    }
    assert_ne!(port, 0, "helper never wrote handshake port");

    let mut client = TcpStream::connect(("127.0.0.1", port)).expect("connect helper");
    client.set_read_timeout(Some(Duration::from_secs(5))).ok();
    client.set_nodelay(true).ok();
    client
        .write_all(&encode_control(
            r#"{"op":"hello","proto":1,"token":"e2e-token"}"#,
        ))
        .unwrap();

    let mut reader = BufReader::new(client.try_clone().unwrap());
    match read_frame(&mut reader).expect("read ready") {
        Frame::Control(s) => {
            assert!(s.contains("\"ready\""), "expected ready, got {s}");
            assert!(s.contains("48000"), "expected 48000 rate, got {s}");
        }
        Frame::Audio(_) => panic!("expected ready before audio"),
    }

    client
        .write_all(&encode_control(r#"{"op":"ping"}"#))
        .unwrap();
    client
        .write_all(&encode_control(r#"{"op":"start"}"#))
        .unwrap();

    let mut got_pong = false;
    let mut audio_frames = 0;
    let deadline = Instant::now() + Duration::from_secs(8);
    while Instant::now() < deadline && (!got_pong || audio_frames < 3) {
        match read_frame(&mut reader) {
            Ok(Frame::Control(s)) => {
                if s.contains("\"pong\"") {
                    got_pong = true;
                }
            }
            Ok(Frame::Audio(samples)) => {
                assert_eq!(samples, 960);
                audio_frames += 1;
            }
            Err(_) => break,
        }
    }

    assert!(got_pong, "never received pong");
    assert!(
        audio_frames >= 3,
        "expected >=3 audio frames, got {audio_frames}"
    );

    client
        .write_all(&encode_control(r#"{"op":"stop"}"#))
        .unwrap();
    drop(reader);
    drop(client);
    drop(guard);
    let _ = std::fs::remove_file(&hs);
}
