use std::collections::HashMap;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::mpsc::{Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use bytes::Bytes;
use tokio::runtime::Runtime;

use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_OPUS};
use webrtc::api::{APIBuilder, API};
use webrtc::ice_transport::ice_candidate::RTCIceCandidateInit;
use webrtc::ice_transport::ice_credential_type::RTCIceCredentialType;
use webrtc::ice_transport::ice_server::RTCIceServer;
use webrtc::interceptor::registry::Registry;
use webrtc::media::Sample;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::policy::ice_transport_policy::RTCIceTransportPolicy;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtp_transceiver::rtp_codec::{
    RTCRtpCodecCapability, RTCRtpCodecParameters, RTPCodecType,
};
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

type ReceivedPacket = (String, u32, u16, Vec<u8>);

#[derive(Default)]
pub struct NativeCounters {
    pub capture_frames: AtomicU64,
    pub opus_encoded: AtomicU64,
    pub opus_empty: AtomicU64,
    pub opus_errors: AtomicU64,
    pub rtp_tx_attempts: AtomicU64,
    pub rtp_tx_ok: AtomicU64,
    pub rtp_tx_errors: AtomicU64,
    pub rtp_rx_packets: AtomicU64,
    pub rtp_rx_bytes: AtomicU64,
    pub stale_rtp_rx_dropped: AtomicU64,
    pub decode_packets: AtomicU64,
    pub decode_frames: AtomicU64,
    pub decode_empty: AtomicU64,
    pub decode_errors: AtomicU64,
    pub peer_level_batches: AtomicU64,
    pub mix_rounds: AtomicU64,
    pub mixed_peer_frames: AtomicU64,
    pub mix_nonzero_rounds: AtomicU64,
    pub mix_silent_rounds: AtomicU64,
    pub mix_samples: AtomicU64,
    pub mix_nonzero_samples: AtomicU64,
    mix_peak_bits: AtomicU32,
    mix_square_sum_bits: AtomicU64,
    pub jitter_idle_ticks: AtomicU64,
    pub game_state_updates: AtomicU64,
    pub applied_deaf: AtomicU64,
    applied_master_bits: AtomicU32,
    pub applied_peer_count: AtomicU64,
    pub applied_nonzero_gain_peers: AtomicU64,
    pub playback_queued_pairs: AtomicU64,
    pub playback_spawn_attempts: AtomicU64,
    pub playback_starts: AtomicU64,
    pub playback_stops: AtomicU64,
    pub playback_errors: AtomicU64,
    pub playback_callback_errors: AtomicU64,
    pub playback_callbacks: AtomicU64,
    pub playback_requested_pairs: AtomicU64,
    pub playback_consumed_pairs: AtomicU64,
    pub playback_underrun_pairs: AtomicU64,
    pub playback_lock_contention_callbacks: AtomicU64,
    pub playback_lock_contention_silence_pairs: AtomicU64,
    pub playback_output_nonzero_samples: AtomicU64,
    playback_output_peak_bits: AtomicU32,
}

impl NativeCounters {
    pub fn record_game_state(
        &self,
        deaf: bool,
        master: f32,
        peer_count: usize,
        nonzero_gain_peers: usize,
    ) {
        let effective_master = if master.is_finite() {
            master.clamp(0.0, 2.0)
        } else {
            0.0
        };
        self.applied_deaf.store(deaf as u64, Ordering::Relaxed);
        self.applied_master_bits
            .store(effective_master.to_bits(), Ordering::Relaxed);
        self.applied_peer_count
            .store(peer_count as u64, Ordering::Relaxed);
        self.applied_nonzero_gain_peers
            .store(nonzero_gain_peers as u64, Ordering::Relaxed);
        self.game_state_updates.fetch_add(1, Ordering::Relaxed);
    }

    pub fn record_mix(&self, samples: &[f32]) {
        let mut peak = 0.0f32;
        let mut square_sum = 0.0f64;
        let mut nonzero = 0u64;
        for &sample in samples {
            let sample = if sample.is_finite() { sample } else { 0.0 };
            let abs = sample.abs();
            peak = peak.max(abs);
            square_sum += (sample as f64) * (sample as f64);
            if abs > 0.000_001 {
                nonzero += 1;
            }
        }
        self.mix_samples
            .fetch_add(samples.len() as u64, Ordering::Relaxed);
        self.mix_nonzero_samples
            .fetch_add(nonzero, Ordering::Relaxed);
        if nonzero == 0 {
            self.mix_silent_rounds.fetch_add(1, Ordering::Relaxed);
        } else {
            self.mix_nonzero_rounds.fetch_add(1, Ordering::Relaxed);
        }
        Self::observe_peak(&self.mix_peak_bits, peak);
        Self::add_f64(&self.mix_square_sum_bits, square_sum);
    }

    pub fn record_playback_output(&self, samples: &[f32]) {
        let mut peak = 0.0f32;
        let mut nonzero = 0u64;
        for &sample in samples {
            let abs = if sample.is_finite() {
                sample.abs()
            } else {
                0.0
            };
            peak = peak.max(abs);
            if abs > 0.000_001 {
                nonzero += 1;
            }
        }
        self.playback_output_nonzero_samples
            .fetch_add(nonzero, Ordering::Relaxed);
        Self::observe_peak(&self.playback_output_peak_bits, peak);
    }

    fn observe_peak(target: &AtomicU32, peak: f32) {
        if !peak.is_finite() || peak <= 0.0 {
            return;
        }
        let mut current = target.load(Ordering::Relaxed);
        loop {
            if f32::from_bits(current) >= peak {
                return;
            }
            match target.compare_exchange_weak(
                current,
                peak.to_bits(),
                Ordering::Relaxed,
                Ordering::Relaxed,
            ) {
                Ok(_) => return,
                Err(actual) => current = actual,
            }
        }
    }

    fn add_f64(target: &AtomicU64, value: f64) {
        if !value.is_finite() || value <= 0.0 {
            return;
        }
        let mut current = target.load(Ordering::Relaxed);
        loop {
            let next = (f64::from_bits(current) + value).to_bits();
            match target.compare_exchange_weak(current, next, Ordering::Relaxed, Ordering::Relaxed)
            {
                Ok(_) => return,
                Err(actual) => current = actual,
            }
        }
    }

    pub fn snapshot(
        &self,
        capture_ring_dropped: u64,
        playback_ring_len: u64,
        playback_ring_dropped: u64,
    ) -> crate::proto::NativeStatsSnapshot {
        let mix_samples = self.mix_samples.load(Ordering::Relaxed);
        let mix_square_sum = f64::from_bits(self.mix_square_sum_bits.load(Ordering::Relaxed));
        let mix_rms = if mix_samples == 0 {
            0.0
        } else {
            (mix_square_sum / mix_samples as f64).sqrt()
        };
        crate::proto::NativeStatsSnapshot {
            capture_frames: self.capture_frames.load(Ordering::Relaxed),
            opus_encoded: self.opus_encoded.load(Ordering::Relaxed),
            opus_empty: self.opus_empty.load(Ordering::Relaxed),
            opus_errors: self.opus_errors.load(Ordering::Relaxed),
            rtp_tx_attempts: self.rtp_tx_attempts.load(Ordering::Relaxed),
            rtp_tx_ok: self.rtp_tx_ok.load(Ordering::Relaxed),
            rtp_tx_errors: self.rtp_tx_errors.load(Ordering::Relaxed),
            rtp_rx_packets: self.rtp_rx_packets.load(Ordering::Relaxed),
            rtp_rx_bytes: self.rtp_rx_bytes.load(Ordering::Relaxed),
            stale_rtp_rx_dropped: self.stale_rtp_rx_dropped.load(Ordering::Relaxed),
            decode_packets: self.decode_packets.load(Ordering::Relaxed),
            decode_frames: self.decode_frames.load(Ordering::Relaxed),
            decode_empty: self.decode_empty.load(Ordering::Relaxed),
            decode_errors: self.decode_errors.load(Ordering::Relaxed),
            peer_level_batches: self.peer_level_batches.load(Ordering::Relaxed),
            mix_rounds: self.mix_rounds.load(Ordering::Relaxed),
            mixed_peer_frames: self.mixed_peer_frames.load(Ordering::Relaxed),
            mix_nonzero_rounds: self.mix_nonzero_rounds.load(Ordering::Relaxed),
            mix_silent_rounds: self.mix_silent_rounds.load(Ordering::Relaxed),
            mix_samples,
            mix_nonzero_samples: self.mix_nonzero_samples.load(Ordering::Relaxed),
            mix_peak: f32::from_bits(self.mix_peak_bits.load(Ordering::Relaxed)),
            mix_rms,
            jitter_idle_ticks: self.jitter_idle_ticks.load(Ordering::Relaxed),
            game_state_updates: self.game_state_updates.load(Ordering::Relaxed),
            applied_deaf: self.applied_deaf.load(Ordering::Relaxed) != 0,
            applied_master: f32::from_bits(self.applied_master_bits.load(Ordering::Relaxed)),
            applied_peer_count: self.applied_peer_count.load(Ordering::Relaxed),
            applied_nonzero_gain_peers: self.applied_nonzero_gain_peers.load(Ordering::Relaxed),
            playback_queued_pairs: self.playback_queued_pairs.load(Ordering::Relaxed),
            playback_spawn_attempts: self.playback_spawn_attempts.load(Ordering::Relaxed),
            playback_starts: self.playback_starts.load(Ordering::Relaxed),
            playback_stops: self.playback_stops.load(Ordering::Relaxed),
            playback_errors: self.playback_errors.load(Ordering::Relaxed),
            playback_callback_errors: self.playback_callback_errors.load(Ordering::Relaxed),
            playback_callbacks: self.playback_callbacks.load(Ordering::Relaxed),
            playback_requested_pairs: self.playback_requested_pairs.load(Ordering::Relaxed),
            playback_consumed_pairs: self.playback_consumed_pairs.load(Ordering::Relaxed),
            playback_underrun_pairs: self.playback_underrun_pairs.load(Ordering::Relaxed),
            playback_lock_contention_callbacks: self
                .playback_lock_contention_callbacks
                .load(Ordering::Relaxed),
            playback_lock_contention_silence_pairs: self
                .playback_lock_contention_silence_pairs
                .load(Ordering::Relaxed),
            playback_output_nonzero_samples: self
                .playback_output_nonzero_samples
                .load(Ordering::Relaxed),
            playback_output_peak: f32::from_bits(
                self.playback_output_peak_bits.load(Ordering::Relaxed),
            ),
            capture_ring_dropped,
            playback_ring_len,
            playback_ring_dropped,
        }
    }
}

#[derive(Debug, Clone)]
pub enum LocalSignal {
    Sdp {
        peer_id: String,
        generation: u32,
        sdp_type: String,
        sdp: String,
    },
    Candidate {
        peer_id: String,
        generation: u32,
        candidate: String,
    },
    PeerState {
        peer_id: String,
        generation: u32,
        state: String,
    },
}

struct PeerHandle {
    pc: Arc<RTCPeerConnection>,
    track: Arc<TrackLocalStaticSample>,
    generation: u32,
}

#[derive(Clone, Copy)]
struct PeerConfig {
    relay_only: bool,
    generation: u32,
}

pub struct RtcEngine {
    rt: Runtime,
    peers: Mutex<HashMap<String, PeerHandle>>,
    peer_configs: Mutex<HashMap<String, PeerConfig>>,
    ice_servers: Mutex<Vec<RTCIceServer>>,
    out_local_signal: Sender<LocalSignal>,
    recv_tx: Sender<ReceivedPacket>,
    recv_rx: Mutex<Receiver<ReceivedPacket>>,
    counters: Arc<NativeCounters>,

    pending_ice: Mutex<HashMap<String, Vec<String>>>,
}

fn opus_capability() -> RTCRtpCodecCapability {
    RTCRtpCodecCapability {
        mime_type: MIME_TYPE_OPUS.to_owned(),
        clock_rate: 48000,
        channels: 2,
        sdp_fmtp_line: "minptime=10;useinbandfec=1".to_owned(),
        rtcp_feedback: vec![],
    }
}

fn build_api() -> Result<API, webrtc::Error> {
    let mut m = MediaEngine::default();
    m.register_codec(
        RTCRtpCodecParameters {
            capability: opus_capability(),
            payload_type: 111,
            ..Default::default()
        },
        RTPCodecType::Audio,
    )?;
    let mut registry = Registry::new();
    registry = register_default_interceptors(registry, &mut m)?;
    Ok(APIBuilder::new()
        .with_media_engine(m)
        .with_interceptor_registry(registry)
        .build())
}

struct CreatePeerArgs {
    peer_id: String,
    offerer: bool,
    ice_servers: Vec<RTCIceServer>,
    relay_only: bool,
    generation: u32,
    out_local_signal: Sender<LocalSignal>,
    recv_tx: Sender<ReceivedPacket>,
    counters: Arc<NativeCounters>,
}

async fn create_peer(args: CreatePeerArgs) -> Result<PeerHandle, webrtc::Error> {
    let CreatePeerArgs {
        peer_id,
        offerer,
        ice_servers,
        relay_only,
        generation,
        out_local_signal,
        recv_tx,
        counters,
    } = args;
    let api = build_api()?;
    let config = RTCConfiguration {
        ice_servers: servers_for_policy(&ice_servers, relay_only),
        ice_transport_policy: if relay_only {
            RTCIceTransportPolicy::Relay
        } else {
            RTCIceTransportPolicy::All
        },
        ..Default::default()
    };
    let pc = Arc::new(api.new_peer_connection(config).await?);

    let track = Arc::new(TrackLocalStaticSample::new(
        opus_capability(),
        "audio".to_owned(),
        "perfectcomms".to_owned(),
    ));
    let rtp_sender = pc
        .add_track(Arc::clone(&track) as Arc<dyn TrackLocal + Send + Sync>)
        .await?;
    tokio::spawn(async move {
        let mut buf = vec![0u8; 1500];
        while rtp_sender.read(&mut buf).await.is_ok() {}
    });

    let cand_signal = out_local_signal.clone();
    let cand_peer = peer_id.clone();
    pc.on_ice_candidate(Box::new(move |c| {
        let signal = cand_signal.clone();
        let pid = cand_peer.clone();
        Box::pin(async move {
            if let Some(c) = c {
                if let Ok(init) = c.to_json() {
                    if let Ok(s) = serde_json::to_string(&init) {
                        let _ = signal.send(LocalSignal::Candidate {
                            peer_id: pid,
                            generation,
                            candidate: s,
                        });
                    }
                }
            }
        })
    }));

    let state_signal = out_local_signal.clone();
    let state_peer = peer_id.clone();
    pc.on_peer_connection_state_change(Box::new(move |s: RTCPeerConnectionState| {
        let signal = state_signal.clone();
        let pid = state_peer.clone();
        Box::pin(async move {
            let _ = signal.send(LocalSignal::PeerState {
                peer_id: pid,
                generation,
                state: s.to_string(),
            });
        })
    }));

    let track_peer = peer_id.clone();
    let track_counters = counters.clone();
    pc.on_track(Box::new(move |track, _receiver, _transceiver| {
        let tx = recv_tx.clone();
        let pid = track_peer.clone();
        let counters = track_counters.clone();
        Box::pin(async move {
            tokio::spawn(async move {
                while let Ok((pkt, _attr)) = track.read_rtp().await {
                    if !pkt.payload.is_empty() {
                        counters.rtp_rx_packets.fetch_add(1, Ordering::Relaxed);
                        counters
                            .rtp_rx_bytes
                            .fetch_add(pkt.payload.len() as u64, Ordering::Relaxed);
                        let _ = tx.send((
                            pid.clone(),
                            generation,
                            pkt.header.sequence_number,
                            pkt.payload.to_vec(),
                        ));
                    }
                }
            });
        })
    }));

    if offerer {
        let offer = pc.create_offer(None).await?;
        pc.set_local_description(offer).await?;
        if let Some(local) = pc.local_description().await {
            let _ = out_local_signal.send(LocalSignal::Sdp {
                peer_id: peer_id.clone(),
                generation,
                sdp_type: local.sdp_type.to_string(),
                sdp: local.sdp,
            });
        }
    }

    Ok(PeerHandle {
        pc,
        track,
        generation,
    })
}

fn is_turn_url(url: &str) -> bool {
    let trimmed = url.trim();
    trimmed
        .get(..5)
        .is_some_and(|p| p.eq_ignore_ascii_case("turn:"))
        || trimmed
            .get(..6)
            .is_some_and(|p| p.eq_ignore_ascii_case("turns:"))
}

/// Direct attempts deliberately omit TURN URLs so healthy peers never allocate a relay.
/// Relay retries receive only TURN URLs and use WebRTC's relay-only gather policy. Splitting
/// mixed URL entries preserves the username/credential attached to the original ICE server.
fn servers_for_policy(servers: &[RTCIceServer], relay_only: bool) -> Vec<RTCIceServer> {
    servers
        .iter()
        .filter_map(|server| {
            let urls: Vec<String> = server
                .urls
                .iter()
                .filter(|url| is_turn_url(url) == relay_only)
                .cloned()
                .collect();
            if urls.is_empty() {
                None
            } else {
                let mut selected = server.clone();
                selected.urls = urls;
                Some(selected)
            }
        })
        .collect()
}

#[allow(dead_code)]
impl RtcEngine {
    pub fn new(out_local_signal: Sender<LocalSignal>) -> RtcEngine {
        Self::new_with_counters(out_local_signal, Arc::new(NativeCounters::default()))
    }

    pub fn new_with_counters(
        out_local_signal: Sender<LocalSignal>,
        counters: Arc<NativeCounters>,
    ) -> RtcEngine {
        let rt = tokio::runtime::Builder::new_multi_thread()
            .enable_all()
            .build()
            .expect("rtc tokio runtime");
        let (recv_tx, recv_rx) = std::sync::mpsc::channel();
        RtcEngine {
            rt,
            peers: Mutex::new(HashMap::new()),
            peer_configs: Mutex::new(HashMap::new()),
            ice_servers: Mutex::new(vec![RTCIceServer {
                urls: vec![
                    "stun:stun.l.google.com:19302".to_string(),
                    "stun:stun1.l.google.com:19302".to_string(),
                    "stun:stun2.l.google.com:19302".to_string(),
                    "stun:stun.cloudflare.com:3478".to_string(),
                    "stun:global.stun.twilio.com:3478".to_string(),
                ],
                ..Default::default()
            }]),
            out_local_signal,
            recv_tx,
            recv_rx: Mutex::new(recv_rx),
            counters,
            pending_ice: Mutex::new(HashMap::new()),
        }
    }

    pub fn set_ice_servers(&self, servers: &[crate::proto::IceServer]) {
        let mapped = servers
            .iter()
            .map(|s| RTCIceServer {
                urls: s.urls.clone(),
                username: s.username.clone().unwrap_or_default(),
                credential: s.credential.clone().unwrap_or_default(),
                // TURN API/custom coturn credentials are long-term username/password values.
                // webrtc-rs rejects a populated credential whose type remains Unspecified before
                // ICE gathering, so relay fallback would otherwise fail without one candidate.
                credential_type: RTCIceCredentialType::Password,
            })
            .collect();
        *self.ice_servers.lock().unwrap() = mapped;
    }

    fn emit_failed(&self, peer_id: &str, generation: u32) {
        let _ = self.out_local_signal.send(LocalSignal::PeerState {
            peer_id: peer_id.to_string(),
            generation,
            state: "failed".to_string(),
        });
    }

    pub fn add_peer(&self, peer_id: String, offerer: bool, relay_only: bool, generation: u32) {
        eprintln!(
            "pc-capture: rtc peer-add begin peer={peer_id} generation={generation} offerer={offerer} relay_only={relay_only}"
        );
        let config = PeerConfig {
            relay_only,
            generation,
        };
        self.peer_configs
            .lock()
            .unwrap()
            .insert(peer_id.clone(), config);

        let existing = self.peers.lock().unwrap().remove(&peer_id);
        if let Some(handle) = existing {
            if handle.generation == generation {
                self.peers.lock().unwrap().insert(peer_id, handle);
                eprintln!("pc-capture: rtc peer-add reused generation={generation}");
                return;
            }
            let _ = self.rt.block_on(async move { handle.pc.close().await });
        }
        let signal = self.out_local_signal.clone();
        let recv_tx = self.recv_tx.clone();
        let servers = self.ice_servers.lock().unwrap().clone();
        let pid = peer_id.clone();
        match self.rt.block_on(create_peer(CreatePeerArgs {
            peer_id: pid,
            offerer,
            ice_servers: servers,
            relay_only,
            generation,
            out_local_signal: signal,
            recv_tx,
            counters: self.counters.clone(),
        })) {
            Ok(handle) => {
                self.peers.lock().unwrap().insert(peer_id, handle);
                eprintln!("pc-capture: rtc peer-add created generation={generation}");
            }
            Err(error) => {
                eprintln!(
                    "pc-capture: peer creation failed peer={peer_id} generation={generation} relay_only={relay_only}: {error}"
                );
                self.emit_failed(&peer_id, generation);
            }
        }
    }

    pub fn remove_peer(&self, peer_id: &str) {
        self.pending_ice.lock().unwrap().remove(peer_id);
        self.peer_configs.lock().unwrap().remove(peer_id);
        let handle = self.peers.lock().unwrap().remove(peer_id);
        if let Some(h) = handle {
            let _ = self.rt.block_on(async move { h.pc.close().await });
        }
        eprintln!("pc-capture: rtc peer-remove peer={peer_id}");
    }

    pub fn set_remote_sdp(&self, peer_id: &str, sdp_type: &str, sdp: &str) {
        eprintln!(
            "pc-capture: rtc remote-sdp begin peer={peer_id} type={sdp_type} sdp_bytes={} ",
            sdp.len()
        );
        let existing = self
            .peers
            .lock()
            .unwrap()
            .get(peer_id)
            .map(|h| Arc::clone(&h.pc));
        let pc = match existing {
            Some(p) => p,
            None => {
                let config = match self.peer_configs.lock().unwrap().get(peer_id).copied() {
                    Some(config) => config,
                    None => {
                        eprintln!(
                            "pc-capture: rtc remote-sdp rejected peer={peer_id} type={sdp_type} reason=missing-peer-config"
                        );
                        return;
                    }
                };
                let signal = self.out_local_signal.clone();
                let recv_tx = self.recv_tx.clone();
                let servers = self.ice_servers.lock().unwrap().clone();
                let pid = peer_id.to_string();
                match self.rt.block_on(create_peer(CreatePeerArgs {
                    peer_id: pid,
                    offerer: false,
                    ice_servers: servers,
                    relay_only: config.relay_only,
                    generation: config.generation,
                    out_local_signal: signal,
                    recv_tx,
                    counters: self.counters.clone(),
                })) {
                    Ok(h) => {
                        let pc = Arc::clone(&h.pc);
                        self.peers.lock().unwrap().insert(peer_id.to_string(), h);
                        pc
                    }
                    Err(error) => {
                        eprintln!(
                            "pc-capture: answer peer creation failed peer={peer_id} generation={} relay_only={}: {error}",
                            config.generation, config.relay_only
                        );
                        self.emit_failed(peer_id, config.generation);
                        return;
                    }
                }
            }
        };

        let generation = match self.peers.lock().unwrap().get(peer_id) {
            Some(handle) => handle.generation,
            None => return,
        };
        let desc = match sdp_type {
            "offer" => RTCSessionDescription::offer(sdp.to_string()),
            "answer" => RTCSessionDescription::answer(sdp.to_string()),
            "pranswer" => RTCSessionDescription::pranswer(sdp.to_string()),
            _ => {
                eprintln!(
                    "pc-capture: rtc remote-sdp rejected peer={peer_id} generation={generation} type={sdp_type} reason=unsupported-type"
                );
                self.emit_failed(peer_id, generation);
                return;
            }
        };
        let desc = match desc {
            Ok(d) => d,
            Err(error) => {
                eprintln!(
                    "pc-capture: rtc remote-sdp rejected peer={peer_id} generation={generation} type={sdp_type} reason=invalid-description error={error}"
                );
                self.emit_failed(peer_id, generation);
                return;
            }
        };
        let signal = self.out_local_signal.clone();
        let pid = peer_id.to_string();
        let failed_signal = self.out_local_signal.clone();
        let failed_peer = peer_id.to_string();
        let is_offer = sdp_type == "offer";

        let pending = self
            .pending_ice
            .lock()
            .unwrap()
            .remove(peer_id)
            .unwrap_or_default();
        let pending_count = pending.len();
        if let Err(error) = self.rt.block_on(async move {
            pc.set_remote_description(desc).await?;
            for cand in pending {
                let init = serde_json::from_str::<RTCIceCandidateInit>(&cand).unwrap_or(
                    RTCIceCandidateInit {
                        candidate: cand,
                        ..Default::default()
                    },
                );
                let _ = pc.add_ice_candidate(init).await;
            }
            if is_offer {
                let answer = pc.create_answer(None).await?;
                pc.set_local_description(answer).await?;
                if let Some(local) = pc.local_description().await {
                    let _ = signal.send(LocalSignal::Sdp {
                        peer_id: pid,
                        generation,
                        sdp_type: local.sdp_type.to_string(),
                        sdp: local.sdp,
                    });
                }
            }
            Ok::<(), webrtc::Error>(())
        }) {
            eprintln!(
                "pc-capture: remote SDP failed peer={peer_id} generation={generation} type={sdp_type}: {error}"
            );
            let _ = failed_signal.send(LocalSignal::PeerState {
                peer_id: failed_peer,
                generation,
                state: "failed".to_string(),
            });
        } else {
            eprintln!(
                "pc-capture: rtc remote-sdp applied peer={peer_id} generation={generation} type={sdp_type} pending_ice={pending_count}"
            );
        }
    }

    pub fn add_ice_candidate(&self, peer_id: &str, candidate: &str) {
        let pc = self
            .peers
            .lock()
            .unwrap()
            .get(peer_id)
            .map(|h| Arc::clone(&h.pc));
        let pc = match pc {
            Some(p) => p,
            None => {
                self.buffer_ice(peer_id, candidate);
                eprintln!(
                    "pc-capture: rtc candidate buffered peer={peer_id} candidate_bytes={} reason=missing-peer",
                    candidate.len()
                );
                return;
            }
        };

        let has_remote = self
            .rt
            .block_on(async { pc.remote_description().await.is_some() });
        if !has_remote {
            self.buffer_ice(peer_id, candidate);
            eprintln!(
                "pc-capture: rtc candidate buffered peer={peer_id} candidate_bytes={} reason=no-remote-description",
                candidate.len()
            );
            return;
        }
        let init =
            serde_json::from_str::<RTCIceCandidateInit>(candidate).unwrap_or(RTCIceCandidateInit {
                candidate: candidate.to_string(),
                ..Default::default()
            });
        match self
            .rt
            .block_on(async move { pc.add_ice_candidate(init).await })
        {
            Ok(()) => eprintln!(
                "pc-capture: rtc candidate applied peer={peer_id} candidate_bytes={}",
                candidate.len()
            ),
            Err(error) => eprintln!(
                "pc-capture: rtc candidate failed peer={peer_id} candidate_bytes={} error={error}",
                candidate.len()
            ),
        }
    }

    fn buffer_ice(&self, peer_id: &str, candidate: &str) {
        const MAX_PENDING_ICE: usize = 64;
        let mut pending = self.pending_ice.lock().unwrap();
        let q = pending.entry(peer_id.to_string()).or_default();
        if q.len() >= MAX_PENDING_ICE {
            q.remove(0);
        }
        q.push(candidate.to_string());
    }

    pub fn send_opus(&self, pkt: &[u8]) {
        let tracks: Vec<Arc<TrackLocalStaticSample>> = self
            .peers
            .lock()
            .unwrap()
            .values()
            .map(|h| Arc::clone(&h.track))
            .collect();
        if tracks.is_empty() {
            return;
        }
        self.counters
            .rtp_tx_attempts
            .fetch_add(tracks.len() as u64, Ordering::Relaxed);
        let data = Bytes::copy_from_slice(pkt);
        let (ok, errors) = self.rt.block_on(async move {
            let mut ok = 0u64;
            let mut errors = 0u64;
            for t in tracks {
                let sample = Sample {
                    data: data.clone(),
                    duration: Duration::from_millis(20),
                    ..Default::default()
                };
                if t.write_sample(&sample).await.is_ok() {
                    ok += 1;
                } else {
                    errors += 1;
                }
            }
            (ok, errors)
        });
        self.counters.rtp_tx_ok.fetch_add(ok, Ordering::Relaxed);
        self.counters
            .rtp_tx_errors
            .fetch_add(errors, Ordering::Relaxed);
    }

    pub fn recv(&self) -> Option<(String, u32, u16, Vec<u8>)> {
        let receiver = self.recv_rx.lock().unwrap();
        while let Ok((peer_id, generation, sequence, payload)) = receiver.try_recv() {
            let is_current = self
                .peer_configs
                .lock()
                .unwrap()
                .get(&peer_id)
                .is_some_and(|config| config.generation == generation);
            if is_current {
                return Some((peer_id, generation, sequence, payload));
            }
            self.counters
                .stale_rtp_rx_dropped
                .fetch_add(1, Ordering::Relaxed);
        }
        None
    }

    #[cfg(test)]
    pub fn ice_servers_snapshot(&self) -> Vec<RTCIceServer> {
        self.ice_servers.lock().unwrap().clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc::channel;
    use std::time::Instant;

    #[test]
    fn native_counter_snapshot_distinguishes_routed_audio_from_silence() {
        let counters = NativeCounters::default();
        counters.record_game_state(false, 0.75, 2, 1);
        counters.record_mix(&[0.0, 0.5, -0.5, 0.0]);
        counters.record_mix(&[0.0; 4]);
        counters.record_playback_output(&[0.0, 0.25, -0.25, 0.0]);
        counters.playback_callbacks.fetch_add(1, Ordering::Relaxed);
        counters
            .playback_consumed_pairs
            .fetch_add(2, Ordering::Relaxed);

        let snapshot = counters.snapshot(3, 4, 5);
        assert_eq!(snapshot.game_state_updates, 1);
        assert!(!snapshot.applied_deaf);
        assert_eq!(snapshot.applied_master, 0.75);
        assert_eq!(snapshot.applied_peer_count, 2);
        assert_eq!(snapshot.applied_nonzero_gain_peers, 1);
        assert_eq!(snapshot.mix_nonzero_rounds, 1);
        assert_eq!(snapshot.mix_silent_rounds, 1);
        assert_eq!(snapshot.mix_samples, 8);
        assert_eq!(snapshot.mix_nonzero_samples, 2);
        assert_eq!(snapshot.mix_peak, 0.5);
        assert!((snapshot.mix_rms - 0.25).abs() < 0.000_001);
        assert_eq!(snapshot.playback_callbacks, 1);
        assert_eq!(snapshot.playback_consumed_pairs, 2);
        assert_eq!(snapshot.playback_output_nonzero_samples, 2);
        assert_eq!(snapshot.playback_output_peak, 0.25);
        assert_eq!(snapshot.capture_ring_dropped, 3);
        assert_eq!(snapshot.playback_ring_len, 4);
        assert_eq!(snapshot.playback_ring_dropped, 5);
    }

    #[test]
    fn set_ice_servers_stores_mapped_servers() {
        let (tx, _rx) = channel::<LocalSignal>();
        let engine = RtcEngine::new(tx);
        let default_servers = engine.ice_servers_snapshot();
        assert_eq!(default_servers.len(), 1);
        assert_eq!(default_servers[0].urls[0], "stun:stun.l.google.com:19302");
        engine.set_ice_servers(&[
            crate::proto::IceServer {
                urls: vec!["stun:stun.l.google.com:19302".to_string()],
                username: None,
                credential: None,
            },
            crate::proto::IceServer {
                urls: vec!["turn:turn.example.com:3478".to_string()],
                username: Some("u".to_string()),
                credential: Some("c".to_string()),
            },
        ]);
        let stored = engine.ice_servers_snapshot();
        assert_eq!(stored.len(), 2);
        assert_eq!(stored[0].urls, vec!["stun:stun.l.google.com:19302"]);
        assert_eq!(stored[0].username, "");
        assert_eq!(stored[0].credential, "");
        assert_eq!(stored[1].urls, vec!["turn:turn.example.com:3478"]);
        assert_eq!(stored[1].username, "u");
        assert_eq!(stored[1].credential, "c");
        assert_eq!(stored[1].credential_type, RTCIceCredentialType::Password);
    }

    #[test]
    fn direct_and_relay_policies_split_mixed_ice_servers() {
        let mixed = vec![
            RTCIceServer {
                urls: vec![
                    "stun:stun.example.com:3478".to_string(),
                    "turn:turn.example.com:3478?transport=udp".to_string(),
                    "turns:turn.example.com:5349?transport=tcp".to_string(),
                ],
                username: "user".to_string(),
                credential: "secret".to_string(),
                ..Default::default()
            },
            RTCIceServer {
                urls: vec!["STUN:backup.example.com:3478".to_string()],
                ..Default::default()
            },
        ];

        let direct = servers_for_policy(&mixed, false);
        assert_eq!(direct.len(), 2);
        assert_eq!(direct[0].urls, vec!["stun:stun.example.com:3478"]);
        assert_eq!(direct[1].urls, vec!["STUN:backup.example.com:3478"]);

        let relay = servers_for_policy(&mixed, true);
        assert_eq!(relay.len(), 1);
        assert_eq!(relay[0].urls.len(), 2);
        assert_eq!(relay[0].username, "user");
        assert_eq!(relay[0].credential, "secret");
    }

    #[test]
    fn malformed_remote_sdp_emits_failed_for_current_generation() {
        let (tx, rx) = channel::<LocalSignal>();
        let engine = RtcEngine::new(tx);
        engine.add_peer("bad-sdp".to_string(), false, false, 41);

        engine.set_remote_sdp("bad-sdp", "offer", "this is not valid SDP");

        let saw_failure = std::iter::from_fn(|| rx.try_recv().ok()).any(|signal| {
            matches!(
                signal,
                LocalSignal::PeerState {
                    peer_id,
                    generation: 41,
                    state,
                } if peer_id == "bad-sdp" && state == "failed"
            )
        });
        assert!(saw_failure, "malformed SDP failure was not surfaced");
    }

    #[test]
    fn recv_drops_media_from_replaced_generation() {
        let (tx, _rx) = channel::<LocalSignal>();
        let engine = RtcEngine::new(tx);
        engine.peer_configs.lock().unwrap().insert(
            "peer".to_string(),
            PeerConfig {
                relay_only: true,
                generation: 42,
            },
        );
        engine
            .recv_tx
            .send(("peer".to_string(), 41, 1, vec![1]))
            .unwrap();
        engine
            .recv_tx
            .send(("peer".to_string(), 42, 2, vec![2]))
            .unwrap();

        assert_eq!(engine.recv(), Some(("peer".to_string(), 42, 2, vec![2])));
        assert_eq!(engine.recv(), None);
    }

    #[test]
    fn loopback_two_engines_exchange_opus() {
        let (a_tx, a_rx) = channel::<LocalSignal>();
        let (b_tx, b_rx) = channel::<LocalSignal>();
        let a = RtcEngine::new(a_tx);
        let b = RtcEngine::new(b_tx);

        a.add_peer("B".to_string(), true, false, 41);

        b.add_peer("A".to_string(), false, false, 42);

        let payload: Vec<u8> = (0..80u8).map(|i| i ^ 0x5a).collect();
        let deadline = Instant::now() + Duration::from_secs(30);
        let mut received: Option<Vec<u8>> = None;
        let mut count = 0u32;
        let mut saw_connected = false;

        while Instant::now() < deadline {
            while let Ok(sig) = a_rx.try_recv() {
                match sig {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => {
                        b.set_remote_sdp("A", &sdp_type, &sdp)
                    }
                    LocalSignal::Candidate { candidate, .. } => {
                        b.add_ice_candidate("A", &candidate)
                    }
                    LocalSignal::PeerState { state, .. } => {
                        if state == "connected" {
                            saw_connected = true;
                        }
                    }
                }
            }
            while let Ok(sig) = b_rx.try_recv() {
                match sig {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => {
                        a.set_remote_sdp("B", &sdp_type, &sdp)
                    }
                    LocalSignal::Candidate { candidate, .. } => {
                        a.add_ice_candidate("B", &candidate)
                    }
                    LocalSignal::PeerState { .. } => {}
                }
            }
            a.send_opus(&payload);
            while let Some((_pid, _generation, _seq, pkt)) = b.recv() {
                count += 1;
                if received.is_none() {
                    received = Some(pkt);
                }
            }
            if count >= 10 {
                break;
            }
            std::thread::sleep(Duration::from_millis(20));
        }

        assert!(
            count >= 1,
            "peer B never received any opus packets over webrtc"
        );
        assert_eq!(
            received.as_deref(),
            Some(payload.as_slice()),
            "received opus payload mismatch"
        );
        assert!(
            saw_connected,
            "engine A never emitted a connected peer-state"
        );
    }
}
