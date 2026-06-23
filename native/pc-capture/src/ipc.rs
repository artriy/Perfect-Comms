use crate::audio::{enumerate_devices, now_ns, peak, spawn_cpal_capture, ToneSource};
use crate::proto;
use crate::proto::{
    devices_json, encode_audio, encode_control, error_json, level_json, parse_inbound, pong_json,
    ready_json, AudioFrame, AudioRing, DeviceInfo, Frame, InboundOp, PROTO_VERSION, RING_CAPACITY,
};
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

pub const MAX_FRAME_LEN: usize = proto::FRAME_BYTES * 4;

pub struct ServerConfig {
    pub handshake_path: PathBuf,
    pub token: String,
    pub synthetic: bool,
}

pub fn bind_loopback() -> std::io::Result<TcpListener> {
    TcpListener::bind(("127.0.0.1", 0))
}

pub fn write_handshake_file(path: &Path, port: u16, pid: u32) -> std::io::Result<()> {
    let body = format!("{{\"port\":{port},\"pid\":{pid}}}");
    let temp = path.with_extension(format!("tmp.{pid}"));
    {
        let mut f = std::fs::File::create(&temp)?;
        f.write_all(body.as_bytes())?;
        f.flush()?;
    }
    std::fs::rename(&temp, path)?;
    Ok(())
}

pub fn accept_single(listener: &TcpListener) -> std::io::Result<TcpStream> {
    let (stream, _addr) = listener.accept()?;
    stream.set_nodelay(true).ok();
    Ok(stream)
}

pub fn reject_extra_client(stream: &mut TcpStream) -> std::io::Result<()> {
    let body = proto::encode_control(&proto::error_json("busy", "single client only"));
    stream.write_all(&body).ok();
    stream.shutdown(std::net::Shutdown::Both)
}

pub fn read_token_line<R: BufRead>(r: &mut R) -> std::io::Result<String> {
    let mut line = String::new();
    r.read_line(&mut line)?;
    Ok(line.trim_end_matches(['\r', '\n']).to_string())
}

pub fn check_frame_len(len: usize) -> Result<(), proto::DecodeError> {
    if len > MAX_FRAME_LEN {
        return Err(proto::DecodeError::BadLen(len));
    }
    Ok(())
}

// Reads one frame after enforcing check_frame_len on the declared body length,
// so an attacker-supplied len can never drive an unbounded allocation
// (proto::read_frame allocates vec![0u8; len] for CONTROL with no bound).
pub fn read_frame_checked<R: BufRead>(r: &mut R) -> Result<Frame, proto::DecodeError> {
    let mut header = [0u8; 5];
    r.read_exact(&mut header)?;
    let ftype = header[0];
    let len = u32::from_le_bytes([header[1], header[2], header[3], header[4]]) as usize;
    check_frame_len(len)?;
    match ftype {
        proto::TYPE_CONTROL => {
            let mut body = vec![0u8; len];
            r.read_exact(&mut body)?;
            let s = String::from_utf8(body).map_err(proto::DecodeError::Utf8)?;
            Ok(Frame::Control(s))
        }
        proto::TYPE_AUDIO => {
            if len != proto::FRAME_BYTES {
                return Err(proto::DecodeError::BadLen(len));
            }
            let mut body = vec![0u8; proto::FRAME_BYTES];
            r.read_exact(&mut body)?;
            let ts = u64::from_le_bytes(body[0..8].try_into().unwrap());
            let mut samples = Vec::with_capacity(proto::FRAME_SAMPLES);
            for chunk in body[8..].chunks_exact(4) {
                samples.push(f32::from_le_bytes(chunk.try_into().unwrap()));
            }
            Ok(Frame::Audio(AudioFrame { capture_ts_ns: ts, samples }))
        }
        other => Err(proto::DecodeError::BadType(other)),
    }
}

#[derive(Debug, PartialEq)]
pub enum HelloResult {
    Accept,
    RejectToken,
    RejectProto,
}

pub fn validate_hello(op: &InboundOp, expected_token: &str) -> HelloResult {
    match op {
        InboundOp::Hello { proto, token } => {
            if *proto != PROTO_VERSION {
                HelloResult::RejectProto
            } else if token != expected_token {
                HelloResult::RejectToken
            } else {
                HelloResult::Accept
            }
        }
        _ => HelloResult::RejectToken,
    }
}

fn synthetic_devices() -> Vec<DeviceInfo> {
    vec![DeviceInfo {
        id: "synthetic-tone".to_string(),
        name: "Synthetic Tone (440 Hz)".to_string(),
        default: true,
    }]
}

fn spawn_synthetic_producer(
    ring: Arc<Mutex<AudioRing>>,
    stop: Arc<AtomicBool>,
) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        let mut src = ToneSource::new();
        while !stop.load(Ordering::Relaxed) {
            let frame = src.fill_frame();
            ring.lock().unwrap().push(frame);
            std::thread::sleep(Duration::from_millis(20));
        }
    })
}

fn spawn_real_producer(
    device_id: Option<String>,
    ring: Arc<Mutex<AudioRing>>,
    stop: Arc<AtomicBool>,
    conn: Arc<Mutex<TcpStream>>,
) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        if let Err(e) = spawn_cpal_capture(device_id, ring, stop) {
            eprintln!("capture error: {e}");
            let _ = write_frame(&conn, &encode_control(&error_json("mic-denied", &e)));
        }
    })
}

fn write_frame(conn: &Arc<Mutex<TcpStream>>, bytes: &[u8]) -> std::io::Result<()> {
    let mut s = conn.lock().unwrap();
    s.write_all(bytes)?;
    s.flush()
}

pub fn run_session(stream: TcpStream, cfg: &ServerConfig) -> std::io::Result<()> {
    stream.set_nodelay(true).ok();
    let mut reader = BufReader::new(stream.try_clone()?);
    let conn = Arc::new(Mutex::new(stream.try_clone()?));

    let first = match read_frame_checked(&mut reader) {
        Ok(Frame::Control(s)) => s,
        _ => return Ok(()),
    };
    let op = match parse_inbound(&first) {
        Ok(op) => op,
        Err(_) => return Ok(()),
    };
    if validate_hello(&op, &cfg.token) != HelloResult::Accept {
        return Ok(());
    }

    let devices = if cfg.synthetic { synthetic_devices() } else { enumerate_devices() };
    write_frame(&conn, &encode_control(&ready_json(&devices)))?;

    let ring = Arc::new(Mutex::new(AudioRing::new(RING_CAPACITY)));
    let stop = Arc::new(AtomicBool::new(false));
    let selected: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
    let producer_stop = Arc::new(AtomicBool::new(false));
    let producer: Arc<Mutex<Option<std::thread::JoinHandle<()>>>> = Arc::new(Mutex::new(None));

    let writer_ring = ring.clone();
    let writer_stop = stop.clone();
    let writer_conn = conn.clone();
    let writer_handle = std::thread::spawn(move || {
        let mut since_level = 0u32;
        while !writer_stop.load(Ordering::Relaxed) {
            let frame: Option<AudioFrame> = writer_ring.lock().unwrap().pop();
            match frame {
                Some(f) => {
                    let pk = peak(&f.samples);
                    if write_frame(&writer_conn, &encode_audio(&f)).is_err() {
                        break;
                    }
                    since_level += 1;
                    if since_level >= 50 {
                        since_level = 0;
                        let _ = write_frame(&writer_conn, &encode_control(&level_json(pk)));
                    }
                }
                None => std::thread::sleep(Duration::from_millis(5)),
            }
        }
    });

    loop {
        let frame = match read_frame_checked(&mut reader) {
            Ok(Frame::Control(s)) => s,
            _ => break,
        };
        let op = match parse_inbound(&frame) {
            Ok(op) => op,
            Err(_) => continue,
        };
        match op {
            InboundOp::SelectDevice { id } => {
                *selected.lock().unwrap() = Some(id);
                let devs = if cfg.synthetic { synthetic_devices() } else { enumerate_devices() };
                let _ = write_frame(&conn, &encode_control(&devices_json(&devs)));
            }
            InboundOp::Start => {
                producer_stop.store(false, Ordering::Relaxed);
                let mut guard = producer.lock().unwrap();
                if guard.is_none() {
                    if cfg.synthetic {
                        *guard = Some(spawn_synthetic_producer(ring.clone(), producer_stop.clone()));
                    } else {
                        let dev = selected.lock().unwrap().clone();
                        *guard = Some(spawn_real_producer(
                            dev,
                            ring.clone(),
                            producer_stop.clone(),
                            conn.clone(),
                        ));
                    }
                }
            }
            InboundOp::Stop => {
                producer_stop.store(true, Ordering::Relaxed);
                if let Some(h) = producer.lock().unwrap().take() {
                    h.join().ok();
                }
            }
            InboundOp::Ping => {
                let _ = write_frame(&conn, &encode_control(&pong_json(now_ns())));
            }
            InboundOp::Hello { .. } => {}
        }
    }

    producer_stop.store(true, Ordering::Relaxed);
    stop.store(true, Ordering::Relaxed);
    if let Some(h) = producer.lock().unwrap().take() {
        h.join().ok();
    }
    writer_handle.join().ok();
    Ok(())
}

pub fn serve(cfg: ServerConfig) -> std::io::Result<()> {
    let listener = bind_loopback()?;
    let port = listener.local_addr()?.port();
    write_handshake_file(&cfg.handshake_path, port, std::process::id())?;

    let first = accept_single(&listener)?;

    let reject_listener = listener.try_clone()?;
    let _reject = std::thread::spawn(move || {
        while let Ok((mut extra, _)) = reject_listener.accept() {
            let _ = reject_extra_client(&mut extra);
        }
    });

    let result = run_session(first, &cfg);

    drop(listener);
    let _ = std::fs::remove_file(&cfg.handshake_path);
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::proto::{encode_control, parse_inbound, read_frame, Frame};
    use std::io::BufReader;

    #[test]
    fn bind_is_loopback_with_ephemeral_port() {
        let l = bind_loopback().unwrap();
        let addr = l.local_addr().unwrap();
        assert!(addr.ip().is_loopback());
        assert_ne!(addr.port(), 0);
    }

    #[test]
    fn handshake_file_is_valid_json_with_port_and_pid() {
        let dir = std::env::temp_dir();
        let path = dir.join(format!("pc-capture-hs-{}.json", std::process::id()));
        write_handshake_file(&path, 54321, 9999).unwrap();
        let body = std::fs::read_to_string(&path).unwrap();
        let v: serde_json::Value = serde_json::from_str(&body).unwrap();
        assert_eq!(v["port"], 54321);
        assert_eq!(v["pid"], 9999);
        std::fs::remove_file(&path).ok();
    }

    #[test]
    fn read_token_line_trims_newline() {
        let mut r = BufReader::new(&b"my-secret-token\r\n"[..]);
        let tok = read_token_line(&mut r).unwrap();
        assert_eq!(tok, "my-secret-token");
    }

    #[test]
    fn accept_single_returns_first_client() {
        let l = bind_loopback().unwrap();
        let port = l.local_addr().unwrap().port();
        let h = std::thread::spawn(move || {
            let _c = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
            std::thread::sleep(std::time::Duration::from_millis(50));
        });
        let stream = accept_single(&l).unwrap();
        assert!(stream.peer_addr().unwrap().ip().is_loopback());
        h.join().unwrap();
    }

    #[test]
    fn second_connection_is_rejected_after_first_accepted() {
        let l = bind_loopback().unwrap();
        let port = l.local_addr().unwrap().port();
        let _first = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let accepted = accept_single(&l).unwrap();
        assert!(accepted.peer_addr().unwrap().ip().is_loopback());

        let _second = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let mut rejected = accept_single(&l).unwrap();
        reject_extra_client(&mut rejected).unwrap();
    }

    #[test]
    fn max_frame_len_guard_rejects_oversized_len() {
        assert!(check_frame_len(MAX_FRAME_LEN).is_ok());
        assert!(check_frame_len(MAX_FRAME_LEN + 1).is_err());
        assert!(check_frame_len(0).is_ok());
    }

    #[test]
    fn max_frame_len_covers_largest_legit_frame() {
        assert!(MAX_FRAME_LEN >= proto::FRAME_BYTES);
    }

    #[test]
    fn validate_hello_accepts_matching_token_and_proto() {
        let op = parse_inbound(r#"{"op":"hello","proto":1,"token":"good"}"#).unwrap();
        assert!(matches!(validate_hello(&op, "good"), HelloResult::Accept));
    }

    #[test]
    fn validate_hello_rejects_bad_token() {
        let op = parse_inbound(r#"{"op":"hello","proto":1,"token":"bad"}"#).unwrap();
        assert!(matches!(validate_hello(&op, "good"), HelloResult::RejectToken));
    }

    #[test]
    fn validate_hello_rejects_proto_mismatch() {
        let op = parse_inbound(r#"{"op":"hello","proto":2,"token":"good"}"#).unwrap();
        assert!(matches!(validate_hello(&op, "good"), HelloResult::RejectProto));
    }

    #[test]
    fn validate_hello_rejects_non_hello() {
        let op = parse_inbound(r#"{"op":"start"}"#).unwrap();
        assert!(matches!(validate_hello(&op, "good"), HelloResult::RejectToken));
    }

    #[test]
    fn synthetic_session_handshakes_pings_streams_audio_and_replies_devices() {
        let listener = bind_loopback().unwrap();
        let port = listener.local_addr().unwrap().port();
        let cfg = ServerConfig {
            handshake_path: std::env::temp_dir().join("unused-hs.json"),
            token: "tok123".to_string(),
            synthetic: true,
        };
        let server = std::thread::spawn(move || {
            let stream = accept_single(&listener).unwrap();
            run_session(stream, &cfg).ok();
        });

        let mut client = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        client.set_nodelay(true).ok();

        client
            .write_all(&encode_control(r#"{"op":"hello","proto":1,"token":"tok123"}"#))
            .unwrap();

        let mut reader = std::io::BufReader::new(client.try_clone().unwrap());
        match read_frame(&mut reader).unwrap() {
            Frame::Control(s) => {
                let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                assert_eq!(v["op"], "ready");
                assert_eq!(v["proto"], 1);
                assert_eq!(v["format"]["rate"], 48_000);
            }
            other => panic!("expected ready, got {other:?}"),
        }

        client
            .write_all(&encode_control(r#"{"op":"select-device","id":"synthetic-tone"}"#))
            .unwrap();
        let mut got_devices = false;
        client.write_all(&encode_control(r#"{"op":"ping"}"#)).unwrap();
        client.write_all(&encode_control(r#"{"op":"start"}"#)).unwrap();

        let mut got_pong = false;
        let mut got_audio = false;
        for _ in 0..400 {
            match read_frame(&mut reader).unwrap() {
                Frame::Control(s) => {
                    let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                    if v["op"] == "devices" {
                        got_devices = true;
                        assert_eq!(v["devices"][0]["id"], "synthetic-tone");
                    }
                    if v["op"] == "pong" {
                        got_pong = true;
                        assert!(v["capTs"].as_u64().unwrap() > 0);
                    }
                }
                Frame::Audio(f) => {
                    assert_eq!(f.samples.len(), 960);
                    got_audio = true;
                }
            }
            if got_pong && got_audio && got_devices {
                break;
            }
        }
        assert!(got_devices, "never got devices reply");
        assert!(got_pong, "never got pong");
        assert!(got_audio, "never got audio");

        client.write_all(&encode_control(r#"{"op":"stop"}"#)).unwrap();
        drop(reader);
        drop(client);
        server.join().unwrap();
    }

    #[test]
    fn session_rejects_bad_token_then_closes() {
        let listener = bind_loopback().unwrap();
        let port = listener.local_addr().unwrap().port();
        let cfg = ServerConfig {
            handshake_path: std::env::temp_dir().join("unused-hs2.json"),
            token: "right".to_string(),
            synthetic: true,
        };
        let server = std::thread::spawn(move || {
            let stream = accept_single(&listener).unwrap();
            run_session(stream, &cfg).ok();
        });
        let mut client = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        client
            .write_all(&encode_control(r#"{"op":"hello","proto":1,"token":"wrong"}"#))
            .unwrap();
        let mut reader = std::io::BufReader::new(client.try_clone().unwrap());
        let mut buf = [0u8; 1];
        use std::io::Read;
        let n = reader.read(&mut buf).unwrap_or(0);
        assert_eq!(n, 0, "server should have closed without sending ready");
        server.join().unwrap();
    }

    #[test]
    fn session_rejects_oversized_control_frame_without_allocating() {
        let listener = bind_loopback().unwrap();
        let port = listener.local_addr().unwrap().port();
        let cfg = ServerConfig {
            handshake_path: std::env::temp_dir().join("unused-hs3.json"),
            token: "tok".to_string(),
            synthetic: true,
        };
        let server = std::thread::spawn(move || {
            let stream = accept_single(&listener).unwrap();
            run_session(stream, &cfg).ok();
        });
        let mut client = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let mut oversized = Vec::new();
        oversized.push(proto::TYPE_CONTROL);
        oversized.extend_from_slice(&((MAX_FRAME_LEN as u32) + 1).to_le_bytes());
        client.write_all(&oversized).unwrap();
        let mut reader = std::io::BufReader::new(client.try_clone().unwrap());
        let mut buf = [0u8; 1];
        use std::io::Read;
        let n = reader.read(&mut buf).unwrap_or(0);
        assert_eq!(n, 0, "server must close on oversized declared len, never send ready");
        server.join().unwrap();
    }

    #[test]
    fn serve_writes_handshake_and_rejects_second_connection() {
        let hs = std::env::temp_dir()
            .join(format!("pc-serve-hs-{}.json", std::process::id()));
        let hs_for_thread = hs.clone();
        let cfg = ServerConfig {
            handshake_path: hs.clone(),
            token: "servetok".to_string(),
            synthetic: true,
        };
        let server = std::thread::spawn(move || {
            serve(cfg).ok();
        });

        let mut port = 0u16;
        for _ in 0..200 {
            if let Ok(body) = std::fs::read_to_string(&hs_for_thread) {
                if let Ok(v) = serde_json::from_str::<serde_json::Value>(&body) {
                    port = v["port"].as_u64().unwrap_or(0) as u16;
                    if port != 0 {
                        break;
                    }
                }
            }
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        assert_ne!(port, 0, "handshake file never produced a port");

        let mut first = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        first
            .write_all(&encode_control(r#"{"op":"hello","proto":1,"token":"servetok"}"#))
            .unwrap();
        let mut r1 = BufReader::new(first.try_clone().unwrap());
        match read_frame(&mut r1).unwrap() {
            Frame::Control(s) => {
                let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                assert_eq!(v["op"], "ready");
            }
            other => panic!("expected ready, got {other:?}"),
        }

        let second = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let mut r2 = BufReader::new(second);
        match read_frame(&mut r2).unwrap() {
            Frame::Control(s) => {
                let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                assert_eq!(v["op"], "error");
                assert_eq!(v["code"], "busy");
            }
            other => panic!("expected busy error, got {other:?}"),
        }
        let mut probe = [0u8; 1];
        use std::io::Read;
        let n = r2.read(&mut probe).unwrap_or(0);
        assert_eq!(n, 0, "second connection should close after busy error (EOF)");

        drop(r1);
        drop(first);
        server.join().unwrap();
        std::fs::remove_file(&hs).ok();
    }
}
