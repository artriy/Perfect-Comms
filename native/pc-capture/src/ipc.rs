use crate::audio::{
    enumerate_devices, enumerate_output_devices, now_ns, peak, spawn_cpal_capture,
    spawn_cpal_playback, ToneSource,
};
use crate::codec::OpusCodec;
use crate::gamestate::{GameState, LocalState, PeerState};
use crate::mix::{Mixer, PeerJitter};
use crate::proto;
use crate::proto::{
    devices_json, encode_control, error_json, level_json, local_candidate_json, local_sdp_json,
    parse_inbound, peer_state_json, pong_json, ready_json, AudioFrame, AudioOutFrame, AudioRing,
    DeviceInfo, Frame, InboundOp, PlaybackRing, PROTO_VERSION, RING_CAPACITY,
};
use crate::rtc::{LocalSignal, RtcEngine};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub const MAX_FRAME_LEN: usize = 1 << 20;

const SPEAKING_PEAK: f32 = 0.02;

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

pub fn write_devices_file(path: &Path, json: &str) -> std::io::Result<()> {
    let temp = path.with_extension(format!("tmp.{}", std::process::id()));
    {
        let mut f = std::fs::File::create(&temp)?;
        f.write_all(json.as_bytes())?;
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
            Ok(Frame::Audio(AudioFrame {
                capture_ts_ns: ts,
                samples,
            }))
        }
        proto::TYPE_AUDIO_OUT => {
            if len != proto::AUDIO_OUT_BYTES {
                return Err(proto::DecodeError::BadLen(len));
            }
            let mut body = vec![0u8; proto::AUDIO_OUT_BYTES];
            r.read_exact(&mut body)?;
            let mut samples = Vec::with_capacity(proto::AUDIO_OUT_SAMPLES);
            for chunk in body.chunks_exact(4) {
                samples.push(f32::from_le_bytes(chunk.try_into().unwrap()));
            }
            Ok(Frame::AudioOut(AudioOutFrame { samples }))
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
            } else if !ct_eq(token.as_bytes(), expected_token.as_bytes()) {
                HelloResult::RejectToken
            } else {
                HelloResult::Accept
            }
        }
        _ => HelloResult::RejectToken,
    }
}

fn ct_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
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
        const ESCALATE_AFTER: Duration = Duration::from_secs(10);
        const RETRY_DELAY: Duration = Duration::from_millis(500);
        let healthy = Arc::new(AtomicBool::new(false));
        let mut ever_healthy = false;
        let mut outage_start: Option<Instant> = None;
        let mut escalated = false;
        while !stop.load(Ordering::Relaxed) {
            healthy.store(false, Ordering::Relaxed);
            match spawn_cpal_capture(
                device_id.clone(),
                ring.clone(),
                stop.clone(),
                healthy.clone(),
            ) {
                Ok(()) => break,
                Err(e) => {
                    eprintln!("capture error: {e}");
                    if healthy.load(Ordering::Relaxed) {
                        ever_healthy = true;
                        outage_start = None;
                        escalated = false;
                    }
                    let grace = if ever_healthy {
                        ESCALATE_AFTER
                    } else {
                        Duration::ZERO
                    };
                    let started = *outage_start.get_or_insert_with(Instant::now);
                    if !escalated && started.elapsed() >= grace {
                        let _ = write_frame(&conn, &encode_control(&error_json("mic-error", &e)));
                        escalated = true;
                    }
                    if stop.load(Ordering::Relaxed) {
                        break;
                    }
                    std::thread::sleep(RETRY_DELAY);
                }
            }
        }
    })
}

fn write_frame(conn: &Arc<Mutex<TcpStream>>, bytes: &[u8]) -> std::io::Result<()> {
    let mut s = conn.lock().unwrap();
    s.write_all(bytes)?;
    s.flush()
}

fn ensure_playback(
    out_thread: &Mutex<Option<std::thread::JoinHandle<()>>>,
    out_selected: &Mutex<Option<String>>,
    out_stop: &Arc<AtomicBool>,
    playback: &Arc<Mutex<PlaybackRing>>,
    last_spawn_ns: &AtomicU64,
) {
    let mut guard = out_thread.lock().unwrap();
    if guard.as_ref().is_some_and(|h| h.is_finished()) {
        if let Some(h) = guard.take() {
            h.join().ok();
        }
    }
    if guard.is_none() {
        let now = now_ns();
        let last = last_spawn_ns.load(Ordering::Relaxed);
        if last != 0 && now.saturating_sub(last) < 1_000_000_000 {
            return;
        }
        last_spawn_ns.store(now, Ordering::Relaxed);
        let dev = out_selected.lock().unwrap().clone();
        let pb = playback.clone();
        let st = out_stop.clone();
        st.store(false, Ordering::Relaxed);
        *guard = Some(std::thread::spawn(move || {
            if let Err(e) = spawn_cpal_playback(dev, pb, st) {
                eprintln!("pc-capture: playback error: {e}");
            }
        }));
    }
}

enum RtcOp {
    AddPeer {
        peer_id: String,
        offerer: bool,
    },
    RemovePeer {
        peer_id: String,
    },
    SetRemoteSdp {
        peer_id: String,
        sdp_type: String,
        sdp: String,
    },
    AddIce {
        peer_id: String,
        candidate: String,
    },
    SetIceServers {
        servers: Vec<crate::proto::IceServer>,
    },
}

#[allow(clippy::while_let_loop)]
pub fn run_session(stream: TcpStream, cfg: &ServerConfig) -> std::io::Result<()> {
    stream.set_nodelay(true).ok();

    stream.set_read_timeout(Some(Duration::from_secs(10))).ok();
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
    stream.set_read_timeout(None).ok();

    let devices = if cfg.synthetic {
        synthetic_devices()
    } else {
        enumerate_devices()
    };
    let output_devices = if cfg.synthetic {
        synthetic_devices()
    } else {
        enumerate_output_devices()
    };
    write_frame(
        &conn,
        &encode_control(&ready_json(&devices, &output_devices)),
    )?;

    let dsp = Arc::new(Mutex::new(crate::dsp::Dsp::new(
        crate::dsp::DspConfig::default(),
    )));

    let playback = Arc::new(Mutex::new(PlaybackRing::new(8 * proto::AUDIO_OUT_FRAMES)));
    let out_stop = Arc::new(AtomicBool::new(false));
    let out_selected: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
    let out_thread: Arc<Mutex<Option<std::thread::JoinHandle<()>>>> = Arc::new(Mutex::new(None));
    let out_spawn_ns = Arc::new(AtomicU64::new(0));

    let ring = Arc::new(Mutex::new(AudioRing::new(RING_CAPACITY)));
    let stop = Arc::new(AtomicBool::new(false));
    let selected: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
    let producer_stop = Arc::new(AtomicBool::new(false));
    let producer: Arc<Mutex<Option<std::thread::JoinHandle<()>>>> = Arc::new(Mutex::new(None));

    let (local_signal_tx, local_signal_rx) = std::sync::mpsc::channel::<LocalSignal>();

    let rtc = Arc::new(RtcEngine::new(local_signal_tx));
    let rtc_stop = Arc::new(AtomicBool::new(false));

    let (rtc_op_tx, rtc_op_rx) = std::sync::mpsc::channel::<RtcOp>();
    let ctrl_rtc = rtc.clone();
    let ctrl_handle = std::thread::spawn(move || {
        while let Ok(op) = rtc_op_rx.recv() {
            match op {
                RtcOp::AddPeer { peer_id, offerer } => ctrl_rtc.add_peer(peer_id, offerer),
                RtcOp::RemovePeer { peer_id } => ctrl_rtc.remove_peer(&peer_id),
                RtcOp::SetRemoteSdp {
                    peer_id,
                    sdp_type,
                    sdp,
                } => ctrl_rtc.set_remote_sdp(&peer_id, &sdp_type, &sdp),
                RtcOp::AddIce { peer_id, candidate } => {
                    ctrl_rtc.add_ice_candidate(&peer_id, &candidate)
                }
                RtcOp::SetIceServers { servers } => ctrl_rtc.set_ice_servers(&servers),
            }
        }
    });

    let writer_ring = ring.clone();
    let writer_stop = stop.clone();
    let writer_conn = conn.clone();
    let writer_dsp = dsp.clone();
    let writer_rtc = rtc.clone();
    let writer_handle = std::thread::spawn(move || {
        let mut encoder = OpusCodec::new().ok();
        let mut since_level = 0u32;
        let mut last_dropped = 0u64;
        while !writer_stop.load(Ordering::Relaxed) {
            let (frame, dropped) = {
                let mut ring = writer_ring.lock().unwrap();
                (ring.pop(), ring.dropped())
            };
            match frame {
                Some(mut f) => {
                    writer_dsp.lock().unwrap().capture(&mut f.samples);
                    let pk = peak(&f.samples);
                    if let Some(enc) = encoder.as_mut() {
                        let pkt = enc.encode(&f.samples);
                        if !pkt.is_empty() {
                            writer_rtc.send_opus(&pkt);
                        }
                    }
                    since_level += 1;
                    if since_level >= 50 {
                        since_level = 0;
                        let speaking = pk >= SPEAKING_PEAK;
                        if write_frame(&writer_conn, &encode_control(&level_json(pk, speaking)))
                            .is_err()
                        {
                            break;
                        }
                        if dropped != last_dropped {
                            eprintln!("pc-capture: dropped {dropped} audio frames (backpressure)");
                            last_dropped = dropped;
                        }
                    }
                }
                None => std::thread::sleep(Duration::from_millis(5)),
            }
        }
    });

    let game_state = Arc::new(GameState::new());
    let (dec_remove_tx, dec_remove_rx) = std::sync::mpsc::channel::<String>();

    let drain_rtc = rtc.clone();
    let drain_stop = rtc_stop.clone();
    let drain_playback = playback.clone();
    let drain_dsp = dsp.clone();
    let drain_gs = game_state.clone();
    let drain_out_thread = out_thread.clone();
    let drain_out_selected = out_selected.clone();
    let drain_out_stop = out_stop.clone();
    let drain_out_spawn = out_spawn_ns.clone();
    let drain_handle = std::thread::spawn(move || {
        let mut decoders: HashMap<String, OpusCodec> = HashMap::new();
        let mut last_seq: HashMap<String, u16> = HashMap::new();
        let mut mixer = Mixer::new();

        let mut jitter = PeerJitter::new();
        let mut stereo = [0f32; crate::codec::FRAME_SIZE * 2];

        let frame_dur = Duration::from_millis(20);
        let mut next_tick = Instant::now();
        while !drain_stop.load(Ordering::Relaxed) {
            while let Ok(id) = dec_remove_rx.try_recv() {
                decoders.remove(&id);
                jitter.remove(&id);
                last_seq.remove(&id);
            }

            let mut drained = 0;
            while let Some((peer, seq, data)) = drain_rtc.recv() {
                if !decoders.contains_key(&peer) {
                    match OpusCodec::new() {
                        Ok(c) => {
                            decoders.insert(peer.clone(), c);
                        }
                        Err(_) => continue,
                    }
                }
                let last = last_seq.get(&peer).copied();
                let (frames, advance) = {
                    let codec = decoders.get_mut(&peer).unwrap();
                    crate::codec::decode_with_concealment(codec, last, seq, &data)
                };
                for f in frames {
                    jitter.push(&peer, f);
                }
                if advance {
                    last_seq.insert(peer.clone(), seq);
                }
                drained += 1;
                if drained >= 256 {
                    break;
                }
            }
            if jitter.is_idle() {
                std::thread::sleep(Duration::from_millis(5));
                next_tick = Instant::now();
                continue;
            }

            let round = jitter.playout_round();
            if !round.is_empty() {
                let per_peer: Vec<(String, &[f32])> = round
                    .iter()
                    .map(|(k, v)| (k.clone(), v.as_slice()))
                    .collect();
                mixer.mix(&per_peer, &drain_gs, &mut stereo);

                ensure_playback(
                    &drain_out_thread,
                    &drain_out_selected,
                    &drain_out_stop,
                    &drain_playback,
                    &drain_out_spawn,
                );
                drain_playback.lock().unwrap().push(&stereo);
                drain_dsp.lock().unwrap().far_end(&stereo);
            }

            next_tick += frame_dur;
            let now = Instant::now();
            if next_tick > now {
                std::thread::sleep(next_tick - now);
            } else {
                next_tick = now;
            }
        }
    });

    let signal_conn = conn.clone();
    let signal_handle = std::thread::spawn(move || {
        while let Ok(sig) = local_signal_rx.recv() {
            let json = match sig {
                LocalSignal::Sdp {
                    peer_id,
                    sdp_type,
                    sdp,
                } => local_sdp_json(&peer_id, &sdp_type, &sdp),
                LocalSignal::Candidate { peer_id, candidate } => {
                    local_candidate_json(&peer_id, &candidate)
                }
                LocalSignal::PeerState { peer_id, state } => peer_state_json(&peer_id, &state),
            };
            if write_frame(&signal_conn, &encode_control(&json)).is_err() {
                break;
            }
        }
    });

    loop {
        let frame = match read_frame_checked(&mut reader) {
            Ok(f) => f,
            Err(_) => break,
        };
        match frame {
            Frame::AudioOut(_) => {
                ensure_playback(
                    &out_thread,
                    &out_selected,
                    &out_stop,
                    &playback,
                    &out_spawn_ns,
                );
            }
            Frame::Control(text) => {
                let op = match parse_inbound(&text) {
                    Ok(op) => op,
                    Err(_) => continue,
                };
                match op {
                    InboundOp::SelectDevice { id } => {
                        *selected.lock().unwrap() = Some(id);
                        let devs = if cfg.synthetic {
                            synthetic_devices()
                        } else {
                            enumerate_devices()
                        };
                        let outs = if cfg.synthetic {
                            synthetic_devices()
                        } else {
                            enumerate_output_devices()
                        };
                        let _ = write_frame(&conn, &encode_control(&devices_json(&devs, &outs)));
                    }
                    InboundOp::SelectOutputDevice { id } => {
                        *out_selected.lock().unwrap() = Some(id);

                        out_stop.store(true, Ordering::Relaxed);
                        if let Some(h) = out_thread.lock().unwrap().take() {
                            h.join().ok();
                        }
                        out_stop.store(false, Ordering::Relaxed);
                        out_spawn_ns.store(0, Ordering::Relaxed);
                        ensure_playback(
                            &out_thread,
                            &out_selected,
                            &out_stop,
                            &playback,
                            &out_spawn_ns,
                        );
                    }
                    InboundOp::Start => {
                        producer_stop.store(false, Ordering::Relaxed);
                        let mut guard = producer.lock().unwrap();
                        if guard.as_ref().is_some_and(|h| h.is_finished()) {
                            if let Some(h) = guard.take() {
                                h.join().ok();
                            }
                        }
                        if guard.is_none() {
                            if cfg.synthetic {
                                *guard = Some(spawn_synthetic_producer(
                                    ring.clone(),
                                    producer_stop.clone(),
                                ));
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
                    InboundOp::SetDsp { aec, agc, ns, hpf } => {
                        dsp.lock()
                            .unwrap()
                            .set(crate::dsp::DspConfig { aec, agc, ns, hpf });
                    }
                    InboundOp::Ping => {
                        let _ = write_frame(&conn, &encode_control(&pong_json(now_ns())));
                    }
                    InboundOp::PeerAdd { peer_id, offerer } => {
                        let _ = rtc_op_tx.send(RtcOp::AddPeer { peer_id, offerer });
                        ensure_playback(
                            &out_thread,
                            &out_selected,
                            &out_stop,
                            &playback,
                            &out_spawn_ns,
                        );
                    }
                    InboundOp::PeerRemove { peer_id } => {
                        let _ = rtc_op_tx.send(RtcOp::RemovePeer {
                            peer_id: peer_id.clone(),
                        });
                        game_state.remove_peer(&peer_id);
                        let _ = dec_remove_tx.send(peer_id);
                    }
                    InboundOp::SetRemoteSdp {
                        peer_id,
                        sdp_type,
                        sdp,
                    } => {
                        let _ = rtc_op_tx.send(RtcOp::SetRemoteSdp {
                            peer_id,
                            sdp_type,
                            sdp,
                        });
                    }
                    InboundOp::AddIceCandidate { peer_id, candidate } => {
                        let _ = rtc_op_tx.send(RtcOp::AddIce { peer_id, candidate });
                    }
                    InboundOp::SetIceServers { servers } => {
                        let _ = rtc_op_tx.send(RtcOp::SetIceServers { servers });
                    }
                    InboundOp::GameState {
                        deaf,
                        master,
                        peers,
                    } => {
                        let local = LocalState { deafened: deaf };
                        let peer_states: Vec<(String, PeerState)> = peers
                            .into_iter()
                            .map(|p| {
                                (
                                    p.id,
                                    PeerState {
                                        gain: p.gain,
                                        pan: p.pan,
                                        mode: p.mode,
                                    },
                                )
                            })
                            .collect();
                        game_state.apply(local, master, peer_states);
                    }
                    InboundOp::Hello { .. } => {}
                }
            }
            Frame::Audio(_) => {}
        }
    }

    producer_stop.store(true, Ordering::Relaxed);
    stop.store(true, Ordering::Relaxed);
    out_stop.store(true, Ordering::Relaxed);
    rtc_stop.store(true, Ordering::Relaxed);
    if let Some(h) = producer.lock().unwrap().take() {
        h.join().ok();
    }
    if let Some(h) = out_thread.lock().unwrap().take() {
        h.join().ok();
    }
    writer_handle.join().ok();
    drain_handle.join().ok();
    drop(rtc_op_tx);
    ctrl_handle.join().ok();
    drop(rtc);
    signal_handle.join().ok();
    Ok(())
}

pub fn serve(cfg: ServerConfig) -> std::io::Result<()> {
    let listener = bind_loopback()?;
    let port = listener.local_addr()?.port();
    write_handshake_file(&cfg.handshake_path, port, std::process::id())?;

    let connected = Arc::new(AtomicBool::new(false));
    let guard_connected = connected.clone();
    let guard_path = cfg.handshake_path.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_secs(15));
        if !guard_connected.load(Ordering::Relaxed) {
            let _ = std::fs::remove_file(&guard_path);
            std::process::exit(0);
        }
    });

    let first = accept_single(&listener)?;
    connected.store(true, Ordering::Relaxed);

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
        const { assert!(MAX_FRAME_LEN >= proto::FRAME_BYTES) };
    }

    #[test]
    fn validate_hello_accepts_matching_token_and_proto() {
        let op = parse_inbound(r#"{"op":"hello","proto":5,"token":"good"}"#).unwrap();
        assert!(matches!(validate_hello(&op, "good"), HelloResult::Accept));
    }

    #[test]
    fn validate_hello_rejects_bad_token() {
        let op = parse_inbound(r#"{"op":"hello","proto":5,"token":"bad"}"#).unwrap();
        assert!(matches!(
            validate_hello(&op, "good"),
            HelloResult::RejectToken
        ));
    }

    #[test]
    fn validate_hello_rejects_proto_mismatch() {
        let op = parse_inbound(r#"{"op":"hello","proto":99,"token":"good"}"#).unwrap();
        assert!(matches!(
            validate_hello(&op, "good"),
            HelloResult::RejectProto
        ));
    }

    #[test]
    fn validate_hello_rejects_non_hello() {
        let op = parse_inbound(r#"{"op":"start"}"#).unwrap();
        assert!(matches!(
            validate_hello(&op, "good"),
            HelloResult::RejectToken
        ));
    }

    #[test]
    fn synthetic_session_handshakes_pings_emits_level_and_replies_devices() {
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
            .write_all(&encode_control(
                r#"{"op":"hello","proto":5,"token":"tok123"}"#,
            ))
            .unwrap();

        let mut reader = std::io::BufReader::new(client.try_clone().unwrap());
        match read_frame(&mut reader).unwrap() {
            Frame::Control(s) => {
                let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                assert_eq!(v["op"], "ready");
                assert_eq!(v["proto"], 5);
                assert_eq!(v["format"]["rate"], 48_000);
            }
            other => panic!("expected ready, got {other:?}"),
        }

        client
            .write_all(&encode_control(
                r#"{"op":"select-device","id":"synthetic-tone"}"#,
            ))
            .unwrap();
        let mut got_devices = false;
        client
            .write_all(&encode_control(r#"{"op":"ping"}"#))
            .unwrap();
        client
            .write_all(&encode_control(r#"{"op":"start"}"#))
            .unwrap();

        let mut got_pong = false;
        let mut got_level = false;
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
                    if v["op"] == "level" {
                        got_level = true;
                        assert!(v["peak"].is_number());
                        assert!(v["speaking"].is_boolean());
                    }
                }
                Frame::Audio(_) => {}
                Frame::AudioOut(_) => {}
            }
            if got_pong && got_level && got_devices {
                break;
            }
        }
        assert!(got_devices, "never got devices reply");
        assert!(got_pong, "never got pong");
        assert!(got_level, "never got level");

        client
            .write_all(&encode_control(r#"{"op":"stop"}"#))
            .unwrap();
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
            .write_all(&encode_control(
                r#"{"op":"hello","proto":5,"token":"wrong"}"#,
            ))
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
        assert_eq!(
            n, 0,
            "server must close on oversized declared len, never send ready"
        );
        server.join().unwrap();
    }

    #[test]
    fn serve_writes_handshake_and_rejects_second_connection() {
        let hs = std::env::temp_dir().join(format!("pc-serve-hs-{}.json", std::process::id()));
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
            .write_all(&encode_control(
                r#"{"op":"hello","proto":5,"token":"servetok"}"#,
            ))
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
        assert_eq!(
            n, 0,
            "second connection should close after busy error (EOF)"
        );

        drop(r1);
        drop(first);
        server.join().unwrap();
        std::fs::remove_file(&hs).ok();
    }
}
