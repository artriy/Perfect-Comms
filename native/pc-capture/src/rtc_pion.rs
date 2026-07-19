use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::Sender;
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use serde::Deserialize;

use super::{
    LocalSignal, NativeCounters, PeerNetworkPathSnapshot, ReceivedPacket, SharedEncoderPolicy,
    SharedNetworkPathCache, SharedReceiveQueue, ENCODER_STATS_INTERVAL,
    RTP_EGRESS_PRIVACY_DRAIN_TIMEOUT,
};
use crate::codec::{
    EncoderFeedback, EncoderPolicySnapshot, MediaReceiveCounters, MediaReceiveSnapshot,
};

const POLL_IDLE: Duration = Duration::from_millis(1);
const COUNTER_SYNC_INTERVAL: Duration = Duration::from_millis(100);
const MAX_CONTROL_EVENTS_PER_TICK: usize = 256;
const MAX_RTP_EVENTS_PER_TICK: usize = 512;
const MAX_CONSECUTIVE_POLL_ERRORS: u32 = 8;

static TRANSPORT_LIBRARY_PATH: OnceLock<Mutex<Option<PathBuf>>> = OnceLock::new();

/// Sets the exact Pion shared-library path used by subsequently created engines. Android calls
/// this before constructing the in-process voice engine; desktop normally loads the library
/// staged beside the helper executable.
pub fn set_transport_library_path(path: Option<&Path>) {
    let slot = TRANSPORT_LIBRARY_PATH.get_or_init(|| Mutex::new(None));
    *slot.lock().unwrap() = path.map(Path::to_path_buf);
}

fn transport_library_path() -> Option<PathBuf> {
    let configured = TRANSPORT_LIBRARY_PATH
        .get()
        .and_then(|slot| slot.lock().ok()?.clone());
    #[cfg(test)]
    if configured.is_none() && std::env::var_os("PC_REQUIRE_PION").is_some() {
        return std::env::var_os("PC_PION_LIB").map(PathBuf::from);
    }
    configured
}

struct PionBackend {
    api: Arc<pion_sys::Api>,
    handle: AtomicU64,
}

impl PionBackend {
    fn load() -> Result<(Arc<Self>, PathBuf), String> {
        let configured = transport_library_path();
        let (api, path) = pion_sys::Api::load_default(configured.as_deref())?;
        let handle = api
            .engine_new()
            .map_err(|status| format!("engine-create:{status}"))?;
        Ok((
            Arc::new(Self {
                api,
                handle: AtomicU64::new(handle),
            }),
            path,
        ))
    }

    fn handle(&self) -> Option<u64> {
        let handle = self.handle.load(Ordering::Acquire);
        (handle != 0).then_some(handle)
    }

    fn close(&self) {
        let handle = self.handle.swap(0, Ordering::AcqRel);
        if handle != 0 {
            let status = self.api.engine_close(handle);
            if status != pion_sys::STATUS_OK {
                eprintln!("pc-capture: Pion engine close failed status={status}");
            }
        }
    }
}

impl Drop for PionBackend {
    fn drop(&mut self) {
        self.close();
    }
}

#[derive(Clone, Copy)]
struct PeerConfig {
    relay_only: bool,
    generation: u32,
    min_encoder_epoch: u64,
}

#[derive(Debug, Default, Deserialize)]
#[serde(default)]
struct PionControlEvent {
    kind: String,
    peer_id: String,
    generation: u32,
    sdp_type: String,
    sdp: String,
    candidate: String,
    state: String,
    message: String,
    candidate_pair_id: String,
    candidate_state: String,
    local_candidate_type: String,
    remote_candidate_type: String,
    relay: bool,
    bandwidth_estimate_valid: bool,
    available_outgoing_bitrate: f64,
    available_incoming_bitrate: f64,
    current_rtt_ms: f64,
    remote_packets_received: u64,
    remote_packets_lost: i64,
    remote_fraction_lost: f64,
    remote_report_rtt_ms: f64,
    remote_rtt_measurements: u64,
}

pub struct RtcEngine {
    backend: Option<Arc<PionBackend>>,
    load_error: Option<String>,
    control_gate: Mutex<()>,
    poll_healthy: Arc<AtomicBool>,
    peer_configs: Arc<Mutex<HashMap<String, PeerConfig>>>,
    ice_servers: Mutex<Vec<crate::proto::IceServer>>,
    out_local_signal: Sender<LocalSignal>,
    receive_queue: Arc<SharedReceiveQueue>,
    outbound_media_sequence: Mutex<u64>,
    counters: Arc<NativeCounters>,
    media_receive: Arc<MediaReceiveCounters>,
    encoder_policy: Arc<SharedEncoderPolicy>,
    network_paths: Arc<SharedNetworkPathCache>,
    stop: Arc<AtomicBool>,
    poll_thread: Mutex<Option<JoinHandle<()>>>,
}

impl RtcEngine {
    pub fn new(out_local_signal: Sender<LocalSignal>) -> Self {
        Self::new_with_counters(out_local_signal, Arc::new(NativeCounters::default()))
    }

    pub fn new_with_counters(
        out_local_signal: Sender<LocalSignal>,
        counters: Arc<NativeCounters>,
    ) -> Self {
        let media_receive = Arc::new(MediaReceiveCounters::default());
        let receive_queue = Arc::new(SharedReceiveQueue::new(media_receive.clone()));
        let encoder_policy = Arc::new(SharedEncoderPolicy::default());
        let network_paths = Arc::new(SharedNetworkPathCache::default());
        let peer_configs = Arc::new(Mutex::new(HashMap::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let poll_healthy = Arc::new(AtomicBool::new(false));

        let (backend, load_error, poll_thread) = match PionBackend::load() {
            Ok((backend, path)) => {
                eprintln!(
                    "pc-capture: Pion WebRTC v{} transport loaded path={}",
                    pion_sys::PION_VERSION_TEXT,
                    path.display()
                );
                poll_healthy.store(true, Ordering::Release);
                let thread = spawn_poll_thread(PollThreadArgs {
                    backend: backend.clone(),
                    stop: stop.clone(),
                    peer_configs: peer_configs.clone(),
                    out_local_signal: out_local_signal.clone(),
                    receive_queue: receive_queue.clone(),
                    counters: counters.clone(),
                    media_receive: media_receive.clone(),
                    encoder_policy: encoder_policy.clone(),
                    network_paths: network_paths.clone(),
                    poll_healthy: poll_healthy.clone(),
                });
                (Some(backend), None, Some(thread))
            }
            Err(error) => {
                eprintln!("pc-capture: Pion WebRTC transport unavailable: {error}");
                (None, Some(error), None)
            }
        };

        let engine = Self {
            backend,
            load_error,
            control_gate: Mutex::new(()),
            poll_healthy,
            peer_configs,
            ice_servers: Mutex::new(Vec::new()),
            out_local_signal,
            receive_queue,
            outbound_media_sequence: Mutex::new(0),
            counters,
            media_receive,
            encoder_policy,
            network_paths,
            stop,
            poll_thread: Mutex::new(poll_thread),
        };
        engine.set_ice_servers(&default_ice_servers());
        engine
    }

    pub fn transport_ready(&self) -> bool {
        self.poll_healthy.load(Ordering::Acquire)
            && self
                .backend
                .as_ref()
                .and_then(|backend| backend.handle())
                .is_some()
    }

    pub fn transport_error(&self) -> Option<&str> {
        self.load_error.as_deref()
    }

    fn backend_handle(&self) -> Option<(&PionBackend, u64)> {
        if !self.poll_healthy.load(Ordering::Acquire) {
            return None;
        }
        let backend = self.backend.as_deref()?;
        Some((backend, backend.handle()?))
    }

    pub fn set_ice_servers(&self, servers: &[crate::proto::IceServer]) {
        let _control = self.control_gate.lock().unwrap();
        *self.ice_servers.lock().unwrap() = servers.to_vec();
        let Some((backend, handle)) = self.backend_handle() else {
            return;
        };
        match serde_json::to_vec(servers) {
            Ok(json) => {
                let status = backend.api.set_ice_servers(handle, &json);
                if status != pion_sys::STATUS_OK {
                    eprintln!("pc-capture: Pion set ICE servers failed status={status}");
                }
            }
            Err(error) => eprintln!("pc-capture: cannot serialize ICE servers: {error}"),
        }
    }

    fn emit_failed(&self, peer_id: &str, generation: u32) {
        let _ = self.out_local_signal.send(LocalSignal::PeerState {
            peer_id: peer_id.to_string(),
            generation,
            state: "failed".to_string(),
        });
    }

    pub fn add_peer(
        &self,
        peer_id: String,
        offerer: bool,
        relay_only: bool,
        generation: u32,
        min_encoder_epoch: u64,
    ) {
        let _control = self.control_gate.lock().unwrap();
        let config = {
            let mut configs = self.peer_configs.lock().unwrap();
            let min_encoder_epoch = configs.get(&peer_id).map_or(min_encoder_epoch, |existing| {
                existing.min_encoder_epoch.max(min_encoder_epoch)
            });
            let config = PeerConfig {
                relay_only,
                generation,
                min_encoder_epoch,
            };
            configs.insert(peer_id.clone(), config);
            config
        };
        self.encoder_policy.register_peer(&peer_id, generation);
        self.network_paths.register_peer(&peer_id, generation);
        self.receive_queue.remove_peer(&peer_id);

        let Some((backend, handle)) = self.backend_handle() else {
            eprintln!(
                "pc-capture: Pion peer add failed peer={peer_id} generation={generation} reason={}",
                self.load_error
                    .as_deref()
                    .unwrap_or("transport-unavailable")
            );
            self.emit_failed(&peer_id, generation);
            return;
        };
        let status = backend.api.add_peer(
            handle,
            &peer_id,
            offerer,
            config.relay_only,
            generation,
            config.min_encoder_epoch,
        );
        if status != pion_sys::STATUS_OK {
            eprintln!(
                "pc-capture: Pion peer add failed peer={peer_id} generation={generation} status={status}"
            );
            self.emit_failed(&peer_id, generation);
        }
    }

    pub fn remove_peer(&self, peer_id: &str) {
        let _control = self.control_gate.lock().unwrap();
        self.peer_configs.lock().unwrap().remove(peer_id);
        self.encoder_policy.remove_peer(peer_id, None);
        self.network_paths.remove_peer(peer_id, None);
        self.receive_queue.remove_peer(peer_id);
        if let Some((backend, handle)) = self.backend_handle() {
            let status = backend.api.remove_peer(handle, peer_id);
            if status != pion_sys::STATUS_OK {
                eprintln!("pc-capture: Pion peer remove failed peer={peer_id} status={status}");
            }
        }
    }

    pub fn set_remote_sdp(&self, peer_id: &str, sdp_type: &str, sdp: &str) {
        let _control = self.control_gate.lock().unwrap();
        let Some(config) = self.peer_configs.lock().unwrap().get(peer_id).copied() else {
            eprintln!("pc-capture: Pion remote SDP rejected peer={peer_id} reason=missing-peer");
            return;
        };
        let Some((backend, handle)) = self.backend_handle() else {
            self.emit_failed(peer_id, config.generation);
            return;
        };
        let status = backend
            .api
            .set_remote_sdp(handle, peer_id, config.generation, sdp_type, sdp);
        if status != pion_sys::STATUS_OK {
            eprintln!(
                "pc-capture: Pion remote SDP failed peer={peer_id} generation={} type={sdp_type} status={status}",
                config.generation
            );
            self.emit_failed(peer_id, config.generation);
        }
    }

    pub fn restart_ice(&self, peer_id: &str, relay_only: bool, create_offer: bool) -> bool {
        let _control = self.control_gate.lock().unwrap();
        let Some(config) = self.peer_configs.lock().unwrap().get(peer_id).copied() else {
            eprintln!("pc-capture: Pion ICE restart rejected peer={peer_id} reason=missing-peer");
            return false;
        };
        let Some((backend, handle)) = self.backend_handle() else {
            self.emit_failed(peer_id, config.generation);
            return false;
        };
        let status =
            backend
                .api
                .restart_ice(handle, peer_id, config.generation, relay_only, create_offer);
        if status != pion_sys::STATUS_OK {
            eprintln!(
                "pc-capture: Pion ICE restart failed peer={peer_id} generation={} relay_only={relay_only} create_offer={create_offer} status={status}",
                config.generation
            );
            self.emit_failed(peer_id, config.generation);
            return false;
        }
        if let Some(current) = self.peer_configs.lock().unwrap().get_mut(peer_id) {
            if current.generation == config.generation {
                current.relay_only = relay_only;
            }
        }
        true
    }

    pub fn add_ice_candidate(&self, peer_id: &str, candidate: &str) {
        let _control = self.control_gate.lock().unwrap();
        let Some(config) = self.peer_configs.lock().unwrap().get(peer_id).copied() else {
            eprintln!("pc-capture: Pion candidate rejected peer={peer_id} reason=missing-peer");
            return;
        };
        let Some((backend, handle)) = self.backend_handle() else {
            self.emit_failed(peer_id, config.generation);
            return;
        };
        let status = backend
            .api
            .add_ice_candidate(handle, peer_id, config.generation, candidate);
        if status != pion_sys::STATUS_OK {
            eprintln!(
                "pc-capture: Pion candidate failed peer={peer_id} generation={} candidate_bytes={} status={status}",
                config.generation,
                candidate.len()
            );
        }
    }

    pub fn send_opus(&self, packet: &[u8], packet_encoder_epoch: u64) {
        let Some((backend, handle)) = self.backend_handle() else {
            return;
        };
        let media_sequence = {
            let mut sequence = self.outbound_media_sequence.lock().unwrap();
            let value = *sequence;
            *sequence = sequence.wrapping_add(1);
            value
        };
        if let Err(status) =
            backend
                .api
                .send_opus(handle, packet, packet_encoder_epoch, media_sequence)
        {
            eprintln!("pc-capture: Pion RTP enqueue failed status={status}");
        }
    }

    pub fn advance_encoder_epoch(&self, epoch: u64) -> bool {
        let Some((backend, handle)) = self.backend_handle() else {
            return false;
        };
        backend.api.advance_epoch(
            handle,
            epoch,
            RTP_EGRESS_PRIVACY_DRAIN_TIMEOUT.as_millis() as u32,
        ) == pion_sys::STATUS_OK
    }

    pub fn recv(&self) -> Option<ReceivedPacket> {
        while let Some(packet) = self.receive_queue.pop() {
            let current = self
                .peer_configs
                .lock()
                .unwrap()
                .get(&packet.peer_id)
                .is_some_and(|config| config.generation == packet.generation);
            if current {
                return Some(packet);
            }
            self.counters
                .stale_rtp_rx_dropped
                .fetch_add(1, Ordering::Relaxed);
        }
        None
    }

    pub fn media_receive_counters(&self) -> Arc<MediaReceiveCounters> {
        self.media_receive.clone()
    }

    pub fn media_receive_snapshot(&self) -> MediaReceiveSnapshot {
        self.media_receive.snapshot()
    }

    pub fn native_transport_snapshot(&self) -> crate::proto::NativeStatsSnapshot {
        self.counters.snapshot(0, 0, 0)
    }

    pub fn encoder_policy_snapshot(&self) -> EncoderPolicySnapshot {
        self.encoder_policy.snapshot()
    }

    pub fn network_path_snapshots(&self) -> Vec<PeerNetworkPathSnapshot> {
        self.network_paths.snapshots()
    }

    pub fn ice_servers_snapshot(&self) -> Vec<crate::proto::IceServer> {
        self.ice_servers.lock().unwrap().clone()
    }

    #[cfg(test)]
    pub fn inject_received_for_test(
        &self,
        peer_id: &str,
        generation: u32,
        sequence: u16,
        timestamp: u32,
        arrival: Instant,
        payload: Vec<u8>,
    ) {
        self.peer_configs
            .lock()
            .unwrap()
            .entry(peer_id.to_string())
            .or_insert(PeerConfig {
                relay_only: false,
                generation,
                min_encoder_epoch: 0,
            });
        self.receive_queue.push(ReceivedPacket {
            peer_id: peer_id.to_string(),
            generation,
            sequence,
            timestamp,
            arrival,
            payload,
        });
    }
}

impl Drop for RtcEngine {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(thread) = self.poll_thread.lock().unwrap().take() {
            let _ = thread.join();
        }
        if let Some(backend) = &self.backend {
            backend.close();
        }
    }
}

fn default_ice_servers() -> Vec<crate::proto::IceServer> {
    vec![crate::proto::IceServer {
        urls: vec![
            "stun:stun.l.google.com:19302".to_string(),
            "stun:stun1.l.google.com:19302".to_string(),
            "stun:stun2.l.google.com:19302".to_string(),
            "stun:stun.cloudflare.com:3478".to_string(),
            "stun:global.stun.twilio.com:3478".to_string(),
        ],
        username: None,
        credential: None,
    }]
}

struct PollThreadArgs {
    backend: Arc<PionBackend>,
    stop: Arc<AtomicBool>,
    peer_configs: Arc<Mutex<HashMap<String, PeerConfig>>>,
    out_local_signal: Sender<LocalSignal>,
    receive_queue: Arc<SharedReceiveQueue>,
    counters: Arc<NativeCounters>,
    media_receive: Arc<MediaReceiveCounters>,
    encoder_policy: Arc<SharedEncoderPolicy>,
    network_paths: Arc<SharedNetworkPathCache>,
    poll_healthy: Arc<AtomicBool>,
}

struct PollHealthGuard(Arc<AtomicBool>);

impl Drop for PollHealthGuard {
    fn drop(&mut self) {
        self.0.store(false, Ordering::Release);
    }
}

fn spawn_poll_thread(args: PollThreadArgs) -> JoinHandle<()> {
    std::thread::Builder::new()
        .name("pion-rtc-poll".to_string())
        .spawn(move || poll_thread(args))
        .expect("spawn Pion RTC poll thread")
}

fn poll_thread(args: PollThreadArgs) {
    let _health_guard = PollHealthGuard(args.poll_healthy.clone());
    let mut control_buffer = Vec::with_capacity(4096);
    let mut peer_buffer = Vec::with_capacity(64);
    let mut payload_buffer = Vec::with_capacity(2048);
    let mut last_counters = pion_sys::TransportCounters::default();
    let mut last_counter_sync = Instant::now();
    let mut last_policy_evaluation = Instant::now();
    let mut last_go_ingress_overflow = 0u64;
    let mut consecutive_control_errors = 0u32;
    let mut consecutive_rtp_errors = 0u32;
    let mut consecutive_counter_errors = 0u32;

    while !args.stop.load(Ordering::Acquire) {
        let Some(handle) = args.backend.handle() else {
            break;
        };
        let mut did_work = false;
        for _ in 0..MAX_CONTROL_EVENTS_PER_TICK {
            match args.backend.api.poll_control(handle, &mut control_buffer) {
                Ok(true) => {
                    consecutive_control_errors = 0;
                    did_work = true;
                    match serde_json::from_slice::<PionControlEvent>(&control_buffer) {
                        Ok(event) => dispatch_control(&args, event),
                        Err(error) => eprintln!(
                            "pc-capture: invalid Pion control event bytes={} error={error}",
                            control_buffer.len()
                        ),
                    }
                }
                Ok(false) => {
                    consecutive_control_errors = 0;
                    break;
                }
                Err(status) => {
                    if poll_error_is_fatal("control", status, &mut consecutive_control_errors) {
                        return;
                    }
                    break;
                }
            }
        }

        for _ in 0..MAX_RTP_EVENTS_PER_TICK {
            match args
                .backend
                .api
                .poll_rtp(handle, &mut peer_buffer, &mut payload_buffer)
            {
                Ok(Some(event)) => {
                    consecutive_rtp_errors = 0;
                    did_work = true;
                    if event.ingress_overflow > last_go_ingress_overflow {
                        for _ in last_go_ingress_overflow..event.ingress_overflow {
                            args.media_receive.record_ingress_queue_overflow();
                        }
                        last_go_ingress_overflow = event.ingress_overflow;
                    }
                    let Ok(peer_id) = std::str::from_utf8(&peer_buffer) else {
                        eprintln!("pc-capture: Pion RTP event had invalid peer id UTF-8");
                        continue;
                    };
                    let current = args
                        .peer_configs
                        .lock()
                        .unwrap()
                        .get(peer_id)
                        .is_some_and(|config| config.generation == event.generation);
                    if !current {
                        args.counters
                            .stale_rtp_rx_dropped
                            .fetch_add(1, Ordering::Relaxed);
                        continue;
                    }
                    let age = Duration::from_nanos(event.arrival_age_ns);
                    let now = Instant::now();
                    args.receive_queue.push(ReceivedPacket {
                        peer_id: peer_id.to_string(),
                        generation: event.generation,
                        sequence: event.sequence,
                        timestamp: event.timestamp,
                        arrival: now.checked_sub(age).unwrap_or(now),
                        payload: payload_buffer.clone(),
                    });
                }
                Ok(None) => {
                    consecutive_rtp_errors = 0;
                    break;
                }
                Err(status) => {
                    if poll_error_is_fatal("rtp", status, &mut consecutive_rtp_errors) {
                        return;
                    }
                    break;
                }
            }
        }

        let now = Instant::now();
        if now.duration_since(last_counter_sync) >= COUNTER_SYNC_INTERVAL {
            match args.backend.api.counters(handle) {
                Ok(current) => {
                    consecutive_counter_errors = 0;
                    mirror_transport_counters(&args.counters, &last_counters, &current);
                    last_counters = current;
                }
                Err(status) => {
                    if poll_error_is_fatal("counters", status, &mut consecutive_counter_errors) {
                        return;
                    }
                }
            }
            last_counter_sync = now;
        }
        if now.duration_since(last_policy_evaluation) >= ENCODER_STATS_INTERVAL {
            args.encoder_policy.evaluate(now);
            last_policy_evaluation = now;
        }
        if !did_work {
            std::thread::sleep(POLL_IDLE);
        }
    }
}

fn poll_error_is_fatal(kind: &str, status: i32, consecutive: &mut u32) -> bool {
    *consecutive = consecutive.saturating_add(1);
    if *consecutive == 1 || *consecutive == MAX_CONSECUTIVE_POLL_ERRORS {
        eprintln!("pc-capture: Pion {kind} poll failed status={status} consecutive={consecutive}");
    }
    *consecutive >= MAX_CONSECUTIVE_POLL_ERRORS
}

fn dispatch_control(args: &PollThreadArgs, event: PionControlEvent) {
    let current = args
        .peer_configs
        .lock()
        .unwrap()
        .get(&event.peer_id)
        .is_some_and(|config| config.generation == event.generation);
    if event.kind == "error" {
        eprintln!(
            "pc-capture: Pion peer error peer={} generation={} error={}",
            event.peer_id, event.generation, event.message
        );
        if current {
            let _ = args.out_local_signal.send(LocalSignal::PeerState {
                peer_id: event.peer_id,
                generation: event.generation,
                state: "failed".to_string(),
            });
        }
        return;
    }
    if !current {
        return;
    }
    match event.kind.as_str() {
        "sdp" => {
            let _ = args.out_local_signal.send(LocalSignal::Sdp {
                peer_id: event.peer_id,
                generation: event.generation,
                sdp_type: event.sdp_type,
                sdp: event.sdp,
            });
        }
        "candidate" => {
            // Pion emits an empty candidate exactly once when gathering completes. The existing
            // signaling payload already supports an empty string, so EOC remains generation-scoped
            // without changing the player or helper protocol.
            let _ = args.out_local_signal.send(LocalSignal::Candidate {
                peer_id: event.peer_id,
                generation: event.generation,
                candidate: event.candidate,
            });
        }
        "state" => {
            let _ = args.out_local_signal.send(LocalSignal::PeerState {
                peer_id: event.peer_id,
                generation: event.generation,
                state: event.state,
            });
        }
        "stats" => {
            let snapshot = PeerNetworkPathSnapshot {
                peer_id: event.peer_id.clone(),
                generation: event.generation,
                candidate_pair_id: event.candidate_pair_id,
                candidate_state: event.candidate_state,
                local_candidate_type: event.local_candidate_type,
                remote_candidate_type: event.remote_candidate_type,
                relay: event.relay,
                current_rtt_ms: finite_nonnegative(event.current_rtt_ms),
                bandwidth_estimate_valid: event.bandwidth_estimate_valid,
                available_outgoing_bitrate: finite_nonnegative(event.available_outgoing_bitrate),
                available_incoming_bitrate: finite_nonnegative(event.available_incoming_bitrate),
                remote_packets_received: event.remote_packets_received,
                remote_packets_lost: event.remote_packets_lost,
                remote_fraction_lost: finite_nonnegative(event.remote_fraction_lost)
                    .clamp(0.0, 1.0),
                remote_report_rtt_ms: finite_nonnegative(event.remote_report_rtt_ms),
                remote_rtt_measurements: event.remote_rtt_measurements,
            };
            args.network_paths.update(snapshot);
            args.encoder_policy.update_peer(
                &event.peer_id,
                event.generation,
                EncoderFeedback {
                    fraction_lost: finite_nonnegative(event.remote_fraction_lost).clamp(0.0, 1.0),
                },
                event.remote_packets_received,
                event.remote_rtt_measurements,
                Instant::now(),
            );
        }
        other => eprintln!("pc-capture: unknown Pion control event kind={other}"),
    }
}

fn finite_nonnegative(value: f64) -> f64 {
    if value.is_finite() && value >= 0.0 {
        value
    } else {
        0.0
    }
}

fn counter_delta(previous: u64, current: u64) -> u64 {
    current.saturating_sub(previous)
}

fn mirror_transport_counters(
    counters: &NativeCounters,
    previous: &pion_sys::TransportCounters,
    current: &pion_sys::TransportCounters,
) {
    macro_rules! add_delta {
        ($target:ident, $field:ident) => {
            counters.$target.fetch_add(
                counter_delta(previous.$field, current.$field),
                Ordering::Relaxed,
            );
        };
    }
    add_delta!(rtp_tx_attempts, rtp_tx_attempts);
    add_delta!(rtp_tx_ok, rtp_tx_ok);
    add_delta!(rtp_tx_errors, rtp_tx_errors);
    add_delta!(rtp_tx_queue_dropped, rtp_tx_queue_dropped);
    add_delta!(rtp_tx_stale_epoch_dropped, rtp_tx_stale_epoch_dropped);
    add_delta!(rtp_tx_write_timeouts, rtp_tx_write_timeouts);
    add_delta!(rtp_rx_packets, rtp_rx_packets);
    add_delta!(rtp_rx_bytes, rtp_rx_bytes);
    add_delta!(stale_rtp_rx_dropped, stale_rtp_rx_dropped);
    counters.record_rtp_tx_queue_depth(current.rtp_tx_queue_depth_max as usize);
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc::{channel, Receiver, TryRecvError};

    #[test]
    fn poll_health_guard_marks_stopped_worker_unhealthy() {
        let healthy = Arc::new(AtomicBool::new(true));
        {
            let _guard = PollHealthGuard(healthy.clone());
            assert!(healthy.load(Ordering::Acquire));
        }
        assert!(!healthy.load(Ordering::Acquire));
    }

    #[test]
    fn repeated_poll_errors_become_fatal_without_log_flooding() {
        let mut consecutive = 0;
        for _ in 1..MAX_CONSECUTIVE_POLL_ERRORS {
            assert!(!poll_error_is_fatal("test", -7, &mut consecutive));
        }
        assert!(poll_error_is_fatal("test", -7, &mut consecutive));
    }

    fn relay_signals(
        source: &Receiver<LocalSignal>,
        destination: &RtcEngine,
        destination_peer_id: &str,
    ) -> bool {
        let mut connected = false;
        loop {
            match source.try_recv() {
                Ok(LocalSignal::Sdp { sdp_type, sdp, .. }) => {
                    destination.set_remote_sdp(destination_peer_id, &sdp_type, &sdp)
                }
                Ok(LocalSignal::Candidate { candidate, .. }) => {
                    destination.add_ice_candidate(destination_peer_id, &candidate)
                }
                Ok(LocalSignal::PeerState { state, .. }) => connected |= state == "connected",
                Err(TryRecvError::Empty | TryRecvError::Disconnected) => break,
            }
        }
        connected
    }

    #[test]
    fn stale_generation_packets_are_discarded() {
        let (tx, _rx) = channel();
        let engine = RtcEngine::new(tx);
        engine.peer_configs.lock().unwrap().insert(
            "peer".to_string(),
            PeerConfig {
                relay_only: false,
                generation: 8,
                min_encoder_epoch: 0,
            },
        );
        engine.inject_received_for_test("peer", 7, 1, 960, Instant::now(), vec![7]);
        engine.inject_received_for_test("peer", 8, 2, 1920, Instant::now(), vec![8]);
        assert_eq!(engine.recv().unwrap().payload, vec![8]);
    }

    #[test]
    fn loopback_two_engines_exchange_opus() {
        let (a_tx, a_rx) = channel();
        let (b_tx, b_rx) = channel();
        let a = RtcEngine::new(a_tx);
        let b = RtcEngine::new(b_tx);
        if !a.transport_ready() || !b.transport_ready() {
            if std::env::var_os("PC_REQUIRE_PION").is_some() {
                panic!(
                    "Pion transport required but unavailable: a={:?} b={:?}",
                    a.transport_error(),
                    b.transport_error()
                );
            }
            eprintln!("skipping Pion loopback because PC_PION_LIB is not staged");
            return;
        }
        a.set_ice_servers(&[]);
        b.set_ice_servers(&[]);
        a.add_peer("B".to_string(), true, false, 1, 0);
        b.add_peer("A".to_string(), false, false, 1, 0);

        let deadline = Instant::now() + Duration::from_secs(15);
        let mut connected_a = false;
        let mut connected_b = false;
        let mut sent = false;
        while Instant::now() < deadline {
            connected_b |= relay_signals(&a_rx, &b, "A");
            connected_a |= relay_signals(&b_rx, &a, "B");
            if connected_a && connected_b && !sent {
                a.send_opus(b"pion-opus-loopback", 0);
                sent = true;
            }
            if let Some(packet) = b.recv() {
                if packet.payload == b"pion-opus-loopback" {
                    return;
                }
            }
            std::thread::sleep(Duration::from_millis(5));
        }
        panic!(
            "Pion loopback timed out connected_a={connected_a} connected_b={connected_b} sent={sent}"
        );
    }
}
