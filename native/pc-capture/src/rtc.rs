use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::mpsc::Sender;
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use bytes::Bytes;
use tokio::net::UdpSocket;
use tokio::runtime::Runtime;

use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_OPUS};
use webrtc::api::setting_engine::SettingEngine;
use webrtc::api::{APIBuilder, API};
use webrtc::ice::network_type::NetworkType;
use webrtc::ice::udp_mux::{UDPMuxDefault, UDPMuxParams};
use webrtc::ice::udp_network::UDPNetwork;
use webrtc::ice_transport::ice_candidate::RTCIceCandidateInit;
use webrtc::ice_transport::ice_gathering_state::RTCIceGatheringState;
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
use webrtc::stats::{ICECandidatePairStats, StatsReportType};
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

use crate::codec::{
    EncoderFeedback, EncoderNetworkController, EncoderPolicySnapshot, MediaReceiveCounters,
    MediaReceiveSnapshot,
};

const RTP_INGRESS_QUEUE_CAPACITY: usize = 512;
const RTP_INGRESS_PER_PEER_CAPACITY: usize = 32;
const RTP_EGRESS_QUEUE_CAPACITY: usize = 4;
const RTP_EGRESS_WRITE_TIMEOUT: Duration = Duration::from_millis(100);
const RTP_EGRESS_PRIVACY_DRAIN_TIMEOUT: Duration = Duration::from_millis(500);
const ENCODER_STATS_INTERVAL: Duration = Duration::from_secs(2);
const ENCODER_FEEDBACK_FRESHNESS: Duration = Duration::from_secs(6);
// ICE checks normally nominate host immediately, srflx after 500 ms, and prflx after 1 s.
// Hold a successful relay pair for one additional second so direct paths discovered during
// ordinary Internet jitter win without adding more than two seconds to a genuinely relayed call.
const RELAY_ACCEPTANCE_MIN_WAIT: Duration = Duration::from_millis(2_000);
const ICE_GATHERING_IDLE_WAIT: Duration = Duration::from_secs(6);
const ICE_RESTART_OPERATION_TIMEOUT: Duration = Duration::from_secs(10);
const MAX_BUFFERED_LOCAL_ICE: usize = 64;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReceivedPacket {
    pub peer_id: String,
    pub generation: u32,
    pub sequence: u16,
    pub timestamp: u32,
    pub arrival: Instant,
    pub payload: Vec<u8>,
}

#[derive(Default)]
struct ReceiveQueueState {
    queues: HashMap<String, VecDeque<ReceivedPacket>>,
    ready: VecDeque<String>,
    queued: usize,
}

/// Bounded round-robin RTP ingress. A burst from one peer can consume at most 32 slots and its
/// oldest packet is discarded first, so it cannot starve every other talker in the shared decoder
/// path. The consumer receives at most one packet per peer before that peer is scheduled again.
struct SharedReceiveQueue {
    state: Mutex<ReceiveQueueState>,
    counters: Arc<MediaReceiveCounters>,
}

impl SharedReceiveQueue {
    fn new(counters: Arc<MediaReceiveCounters>) -> Self {
        Self {
            state: Mutex::new(ReceiveQueueState::default()),
            counters,
        }
    }

    fn push(&self, packet: ReceivedPacket) {
        let peer_id = packet.peer_id.clone();
        let mut state = self.state.lock().unwrap();
        let mut dropped = 0u64;

        if state.queued >= RTP_INGRESS_QUEUE_CAPACITY {
            // The game has fewer peers than the global/per-peer ratio in normal operation. Keep
            // the global bound defensive as well: evict from the largest queue, not whichever
            // peer happens to arrive next.
            if let Some(victim) = state
                .queues
                .iter()
                .max_by_key(|(_, queue)| queue.len())
                .map(|(peer, _)| peer.clone())
            {
                let became_empty = state.queues.get_mut(&victim).is_some_and(|queue| {
                    let removed = queue.pop_front().is_some();
                    removed && queue.is_empty()
                });
                state.queued = state.queued.saturating_sub(1);
                dropped += 1;
                if became_empty {
                    state.queues.remove(&victim);
                    state.ready.retain(|peer| peer != &victim);
                }
            }
        }

        let was_empty;
        let per_peer_depth;
        let mut grew = false;
        {
            let queue = state.queues.entry(peer_id.clone()).or_default();
            was_empty = queue.is_empty();
            if queue.len() >= RTP_INGRESS_PER_PEER_CAPACITY {
                queue.pop_front();
                dropped += 1;
            } else {
                grew = true;
            }
            queue.push_back(packet);
            per_peer_depth = queue.len();
        }
        if grew {
            state.queued += 1;
        }
        if was_empty {
            state.ready.push_back(peer_id);
        }
        let total_depth = state.queued;
        drop(state);

        for _ in 0..dropped {
            self.counters.record_ingress_queue_overflow();
        }
        self.counters
            .record_ingress_queue_depth(total_depth, per_peer_depth);
    }

    fn pop(&self) -> Option<ReceivedPacket> {
        let mut state = self.state.lock().unwrap();
        while let Some(peer_id) = state.ready.pop_front() {
            let (packet, has_more) = match state.queues.get_mut(&peer_id) {
                Some(queue) => (queue.pop_front(), !queue.is_empty()),
                None => continue,
            };
            let Some(packet) = packet else {
                state.queues.remove(&peer_id);
                continue;
            };
            state.queued = state.queued.saturating_sub(1);
            if has_more {
                state.ready.push_back(peer_id);
            } else {
                state.queues.remove(&peer_id);
            }
            let total_depth = state.queued;
            drop(state);
            self.counters.record_ingress_queue_depth(total_depth, 0);
            return Some(packet);
        }
        None
    }

    fn remove_peer(&self, peer_id: &str) {
        let mut state = self.state.lock().unwrap();
        if let Some(queue) = state.queues.remove(peer_id) {
            state.queued = state.queued.saturating_sub(queue.len());
        }
        state.ready.retain(|peer| peer != peer_id);
        let total_depth = state.queued;
        drop(state);
        self.counters.record_ingress_queue_depth(total_depth, 0);
    }
}

#[derive(Default)]
struct OutboundEpochState {
    floor: u64,
    in_flight: usize,
}

#[derive(Default)]
struct SharedOutboundEpoch {
    state: Mutex<OutboundEpochState>,
    idle: Condvar,
}

impl SharedOutboundEpoch {
    fn begin(self: &Arc<Self>, epoch: u64) -> Option<OutboundPermit> {
        let mut state = self.state.lock().unwrap();
        if epoch < state.floor {
            return None;
        }
        state.in_flight += 1;
        Some(OutboundPermit { gate: self.clone() })
    }

    fn advance_to(&self, epoch: u64, timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        let mut state = self.state.lock().unwrap();
        state.floor = state.floor.max(epoch);
        while state.in_flight != 0 {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return false;
            }
            let (next, wait) = self.idle.wait_timeout(state, remaining).unwrap();
            state = next;
            if wait.timed_out() && state.in_flight != 0 {
                return false;
            }
        }
        true
    }
}

struct OutboundPermit {
    gate: Arc<SharedOutboundEpoch>,
}

impl Drop for OutboundPermit {
    fn drop(&mut self) {
        let mut state = self.gate.state.lock().unwrap();
        state.in_flight = state.in_flight.saturating_sub(1);
        if state.in_flight == 0 {
            self.gate.idle.notify_all();
        }
    }
}

#[derive(Clone)]
struct OutboundPacket {
    payload: Bytes,
    encoder_epoch: u64,
    media_sequence: u64,
}

fn dropped_packets_between(previous: Option<u64>, current: u64) -> u16 {
    let Some(previous) = previous else {
        return 0;
    };
    let distance = current.wrapping_sub(previous);
    if distance <= 1 {
        return 0;
    }
    u16::try_from(distance - 1).unwrap_or(u16::MAX)
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct PeerNetworkPathSnapshot {
    pub peer_id: String,
    pub generation: u32,
    pub candidate_pair_id: String,
    pub candidate_state: String,
    pub local_candidate_type: String,
    pub remote_candidate_type: String,
    pub relay: bool,
    pub current_rtt_ms: f64,
    /// False with the pinned webrtc-rs stats implementation, whose BWE fields are placeholders.
    pub bandwidth_estimate_valid: bool,
    pub available_outgoing_bitrate: f64,
    pub available_incoming_bitrate: f64,
    pub remote_packets_received: u64,
    pub remote_packets_lost: i64,
    pub remote_fraction_lost: f64,
    pub remote_report_rtt_ms: f64,
    pub remote_rtt_measurements: u64,
}

#[derive(Default)]
struct NetworkPathCacheState {
    generations: HashMap<String, u32>,
    snapshots: HashMap<String, PeerNetworkPathSnapshot>,
}

/// Generation-gated cache populated by the existing low-frequency RTCP/ICE stats sampler.
/// Keeping this separate from `RtcEngine::peers` prevents telemetry reads from blocking the
/// runtime once per peer, and ensures a sampler from a closed generation cannot overwrite the
/// replacement peer's path data.
#[derive(Default)]
struct SharedNetworkPathCache {
    state: Mutex<NetworkPathCacheState>,
}

impl SharedNetworkPathCache {
    fn register_peer(&self, peer_id: &str, generation: u32) {
        let mut state = self.state.lock().unwrap();
        let changed = state
            .generations
            .insert(peer_id.to_string(), generation)
            .is_some_and(|previous| previous != generation);
        if changed
            || state
                .snapshots
                .get(peer_id)
                .is_some_and(|snapshot| snapshot.generation != generation)
        {
            state.snapshots.remove(peer_id);
        }
    }

    fn update(&self, snapshot: PeerNetworkPathSnapshot) {
        let mut state = self.state.lock().unwrap();
        if state
            .generations
            .get(&snapshot.peer_id)
            .is_some_and(|generation| *generation == snapshot.generation)
        {
            state.snapshots.insert(snapshot.peer_id.clone(), snapshot);
        }
    }

    fn remove_peer(&self, peer_id: &str, generation: Option<u32>) {
        let mut state = self.state.lock().unwrap();
        if generation.is_none_or(|generation| {
            state
                .generations
                .get(peer_id)
                .is_some_and(|current| *current == generation)
        }) {
            state.generations.remove(peer_id);
            state.snapshots.remove(peer_id);
        }
    }

    fn snapshots(&self) -> Vec<PeerNetworkPathSnapshot> {
        let state = self.state.lock().unwrap();
        let mut snapshots: Vec<_> = state
            .snapshots
            .values()
            .filter(|snapshot| {
                state
                    .generations
                    .get(&snapshot.peer_id)
                    .is_some_and(|generation| *generation == snapshot.generation)
            })
            .cloned()
            .collect();
        snapshots.sort_unstable_by(|left, right| left.peer_id.cmp(&right.peer_id));
        snapshots
    }
}

#[derive(Debug, Clone, Copy)]
struct PeerEncoderFeedback {
    generation: u32,
    sample: EncoderFeedback,
    packets_received: u64,
    rtt_measurements: u64,
    last_fresh: Option<Instant>,
}

struct EncoderPolicyState {
    controller: EncoderNetworkController,
    peers: HashMap<String, PeerEncoderFeedback>,
}

struct SharedEncoderPolicy {
    state: Mutex<EncoderPolicyState>,
    packet_loss_percent: AtomicU32,
    bitrate: AtomicU32,
    generation: AtomicU64,
}

impl Default for SharedEncoderPolicy {
    fn default() -> Self {
        let controller = EncoderNetworkController::new();
        let snapshot = controller.snapshot();
        Self {
            state: Mutex::new(EncoderPolicyState {
                controller,
                peers: HashMap::new(),
            }),
            packet_loss_percent: AtomicU32::new(u32::from(snapshot.packet_loss_percent)),
            bitrate: AtomicU32::new(snapshot.bitrate as u32),
            generation: AtomicU64::new(snapshot.generation),
        }
    }
}

impl SharedEncoderPolicy {
    fn register_peer(&self, peer_id: &str, generation: u32) {
        let mut state = self.state.lock().unwrap();
        if state
            .peers
            .get(peer_id)
            .is_some_and(|peer| peer.generation == generation)
        {
            return;
        }
        state.peers.insert(
            peer_id.to_string(),
            PeerEncoderFeedback {
                generation,
                sample: EncoderFeedback::default(),
                packets_received: 0,
                rtt_measurements: 0,
                last_fresh: None,
            },
        );
    }

    fn remove_peer(&self, peer_id: &str, generation: Option<u32>) {
        let mut state = self.state.lock().unwrap();
        if generation.is_none_or(|generation| {
            state
                .peers
                .get(peer_id)
                .is_some_and(|peer| peer.generation == generation)
        }) {
            state.peers.remove(peer_id);
        }
    }

    fn update_peer(
        &self,
        peer_id: &str,
        generation: u32,
        sample: EncoderFeedback,
        packets_received: u64,
        rtt_measurements: u64,
        now: Instant,
    ) {
        let mut state = self.state.lock().unwrap();
        let Some(peer) = state.peers.get_mut(peer_id) else {
            return;
        };
        if peer.generation != generation {
            return;
        }
        let advanced = packets_received > peer.packets_received
            || rtt_measurements > peer.rtt_measurements
            || peer.last_fresh.is_none();
        peer.packets_received = peer.packets_received.max(packets_received);
        peer.rtt_measurements = peer.rtt_measurements.max(rtt_measurements);
        if advanced {
            peer.sample = sample;
            peer.last_fresh = Some(now);
        }
    }

    fn evaluate(&self, now: Instant) {
        let mut state = self.state.lock().unwrap();
        let feedback: Vec<EncoderFeedback> = state
            .peers
            .values()
            .filter(|peer| {
                peer.last_fresh.is_some_and(|fresh| {
                    now.saturating_duration_since(fresh) <= ENCODER_FEEDBACK_FRESHNESS
                })
            })
            .map(|peer| peer.sample)
            .collect();
        let snapshot = state.controller.observe(&feedback);
        self.packet_loss_percent
            .store(u32::from(snapshot.packet_loss_percent), Ordering::Release);
        self.bitrate
            .store(snapshot.bitrate as u32, Ordering::Release);
        self.generation
            .store(snapshot.generation, Ordering::Release);
    }

    fn snapshot(&self) -> EncoderPolicySnapshot {
        EncoderPolicySnapshot {
            packet_loss_percent: self.packet_loss_percent.load(Ordering::Acquire) as u8,
            bitrate: self.bitrate.load(Ordering::Acquire) as i32,
            generation: self.generation.load(Ordering::Acquire),
        }
    }
}

fn select_freshest_nominated_succeeded<A: Ord, T>(
    pairs: impl IntoIterator<Item = (bool, bool, A, u64, T)>,
) -> Option<T> {
    pairs
        .into_iter()
        .filter(|(nominated, succeeded, _, _, _)| *nominated && *succeeded)
        // Counters are lifetime totals and stay high after ICE renomination. Prefer the pair with
        // the freshest actual media activity, using total traffic only as a deterministic tie-break.
        .max_by(
            |(_, _, activity_a, traffic_a, _), (_, _, activity_b, traffic_b, _)| {
                activity_a
                    .cmp(activity_b)
                    .then_with(|| traffic_a.cmp(traffic_b))
            },
        )
        .map(|(_, _, _, _, value)| value)
}

fn active_nominated_candidate_pair(
    report: &webrtc::stats::StatsReport,
) -> Option<&ICECandidatePairStats> {
    select_freshest_nominated_succeeded(report.reports.values().filter_map(|value| {
        match value {
            StatsReportType::CandidatePair(pair) => Some((
                pair.nominated,
                pair.state.to_string() == "succeeded",
                pair.last_packet_sent_timestamp
                    .max(pair.last_packet_received_timestamp),
                u64::from(pair.packets_sent).saturating_add(u64::from(pair.packets_received)),
                pair,
            )),
            _ => None,
        }
    }))
}

fn encoder_feedback_from_stats(
    report: &webrtc::stats::StatsReport,
) -> Option<(EncoderFeedback, u64, u64)> {
    report
        .reports
        .values()
        .filter_map(|value| match value {
            StatsReportType::RemoteInboundRTP(remote)
                if remote.kind.eq_ignore_ascii_case("audio")
                    && (remote.packets_received > 0 || remote.round_trip_time_measurements > 0) =>
            {
                Some(remote)
            }
            _ => None,
        })
        .max_by_key(|remote| remote.packets_received)
        .map(|remote| {
            (
                EncoderFeedback {
                    fraction_lost: if remote.fraction_lost.is_finite() {
                        remote.fraction_lost.clamp(0.0, 1.0)
                    } else {
                        0.0
                    },
                },
                remote.packets_received,
                remote.round_trip_time_measurements,
            )
        })
}

fn network_path_from_stats(
    peer_id: String,
    generation: u32,
    report: &webrtc::stats::StatsReport,
) -> PeerNetworkPathSnapshot {
    let mut candidate_types: HashMap<String, String> = HashMap::new();
    for value in report.reports.values() {
        match value {
            StatsReportType::LocalCandidate(candidate)
            | StatsReportType::RemoteCandidate(candidate) => {
                candidate_types.insert(candidate.id.clone(), candidate.candidate_type.to_string());
            }
            _ => {}
        }
    }

    let mut snapshot = PeerNetworkPathSnapshot {
        peer_id,
        generation,
        ..Default::default()
    };
    if let Some(pair) = active_nominated_candidate_pair(report) {
        snapshot.candidate_pair_id = pair.id.clone();
        snapshot.candidate_state = pair.state.to_string();
        snapshot.local_candidate_type = candidate_types
            .get(&pair.local_candidate_id)
            .cloned()
            .unwrap_or_default();
        snapshot.remote_candidate_type = candidate_types
            .get(&pair.remote_candidate_id)
            .cloned()
            .unwrap_or_default();
        snapshot.relay = snapshot.local_candidate_type.eq_ignore_ascii_case("relay")
            || snapshot.remote_candidate_type.eq_ignore_ascii_case("relay");
        snapshot.current_rtt_ms = finite_nonnegative(pair.current_round_trip_time * 1000.0);
        // webrtc-rs 0.17.1 leaves both available-bitrate fields at their zero defaults. Preserve
        // the wire fields for backward-compatible diagnostics, but mark them unsupported and do
        // not feed them into encoder policy.
        snapshot.bandwidth_estimate_valid = false;
    }

    let mut remote_feedback_packets = 0u64;
    for value in report.reports.values() {
        match value {
            StatsReportType::RemoteInboundRTP(remote)
                if remote.kind.eq_ignore_ascii_case("audio")
                    && remote.packets_received >= remote_feedback_packets =>
            {
                remote_feedback_packets = remote.packets_received;
                snapshot.remote_packets_received = remote.packets_received;
                snapshot.remote_packets_lost = remote.packets_lost;
                snapshot.remote_fraction_lost = if remote.fraction_lost.is_finite() {
                    remote.fraction_lost.clamp(0.0, 1.0)
                } else {
                    0.0
                };
                snapshot.remote_report_rtt_ms =
                    finite_nonnegative(remote.round_trip_time.unwrap_or(0.0) * 1000.0);
                snapshot.remote_rtt_measurements = remote.round_trip_time_measurements;
            }
            _ => {}
        }
    }
    snapshot
}

fn finite_nonnegative(value: f64) -> f64 {
    if value.is_finite() && value >= 0.0 {
        value
    } else {
        0.0
    }
}

fn encoder_epoch_authorizes_peer(packet_epoch: u64, min_peer_epoch: u64) -> bool {
    packet_epoch >= min_peer_epoch
}

#[derive(Default)]
pub struct NativeCounters {
    pub capture_frames: AtomicU64,
    pub opus_encoded: AtomicU64,
    pub opus_empty: AtomicU64,
    pub opus_errors: AtomicU64,
    pub rtp_tx_attempts: AtomicU64,
    pub rtp_tx_ok: AtomicU64,
    pub rtp_tx_errors: AtomicU64,
    pub rtp_tx_queue_dropped: AtomicU64,
    pub rtp_tx_stale_epoch_dropped: AtomicU64,
    pub rtp_tx_write_timeouts: AtomicU64,
    pub rtp_tx_queue_depth_max: AtomicU64,
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
    fn record_rtp_tx_queue_depth(&self, depth: usize) {
        let mut current = self.rtp_tx_queue_depth_max.load(Ordering::Relaxed);
        while depth as u64 > current {
            match self.rtp_tx_queue_depth_max.compare_exchange_weak(
                current,
                depth as u64,
                Ordering::Relaxed,
                Ordering::Relaxed,
            ) {
                Ok(_) => break,
                Err(actual) => current = actual,
            }
        }
    }

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
            rtp_tx_queue_dropped: self.rtp_tx_queue_dropped.load(Ordering::Relaxed),
            rtp_tx_stale_epoch_dropped: self.rtp_tx_stale_epoch_dropped.load(Ordering::Relaxed),
            rtp_tx_write_timeouts: self.rtp_tx_write_timeouts.load(Ordering::Relaxed),
            rtp_tx_queue_depth_max: self.rtp_tx_queue_depth_max.load(Ordering::Relaxed),
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
            ..Default::default()
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
    outbound_tx: tokio::sync::mpsc::Sender<OutboundPacket>,
    outbound_writer: tokio::task::JoinHandle<()>,
    stats_worker: tokio::task::JoinHandle<()>,
    candidate_gate: Arc<LocalCandidateGate>,
    state_signal_gate: Arc<StateSignalGate>,
    generation: u32,
    min_encoder_epoch: u64,
}

/// ICE restart gathers candidates before the replacement SDP is emitted. Buffering those
/// candidates keeps the signaling order deterministic: the remote peer applies the new ICE
/// credentials first, then receives trickled candidates for that credential generation.
#[derive(Default)]
struct LocalCandidateGate {
    buffered: Mutex<Option<Vec<String>>>,
}

impl LocalCandidateGate {
    fn hold(&self) {
        *self.buffered.lock().unwrap() = Some(Vec::new());
    }

    #[cfg(test)]
    fn queue_or_pass(&self, candidate: String) -> Option<String> {
        let mut buffered = self.buffered.lock().unwrap();
        match buffered.as_mut() {
            Some(queue) => {
                if queue.len() >= MAX_BUFFERED_LOCAL_ICE {
                    queue.remove(0);
                }
                queue.push(candidate);
                None
            }
            None => Some(candidate),
        }
    }

    fn send_or_queue(
        &self,
        candidate: String,
        signal: &Sender<LocalSignal>,
        peer_id: &str,
        generation: u32,
    ) {
        let mut buffered = self.buffered.lock().unwrap();
        if let Some(queue) = buffered.as_mut() {
            if queue.len() >= MAX_BUFFERED_LOCAL_ICE {
                queue.remove(0);
            }
            queue.push(candidate);
        } else {
            // Keep the gate locked through enqueue. A transport replacement can then hold this
            // gate before its signaling barrier and prove that no old candidate follows it.
            let _ = signal.send(LocalSignal::Candidate {
                peer_id: peer_id.to_string(),
                generation,
                candidate,
            });
        }
    }

    fn release(&self) -> Vec<String> {
        self.buffered.lock().unwrap().take().unwrap_or_default()
    }

    fn cancel(&self) {
        *self.buffered.lock().unwrap() = None;
    }
}

struct StateSignalGate {
    enabled: Mutex<bool>,
}

impl StateSignalGate {
    fn new() -> Self {
        Self {
            enabled: Mutex::new(true),
        }
    }

    fn send_if_enabled(
        &self,
        signal: &Sender<LocalSignal>,
        peer_id: &str,
        generation: u32,
        state: String,
    ) {
        let enabled = self.enabled.lock().unwrap();
        if *enabled {
            let _ = signal.send(LocalSignal::PeerState {
                peer_id: peer_id.to_string(),
                generation,
                state,
            });
        }
    }

    fn disable_with_replacement_barrier(
        &self,
        signal: &Sender<LocalSignal>,
        peer_id: &str,
        generation: u32,
    ) {
        let mut enabled = self.enabled.lock().unwrap();
        *enabled = false;
        // This known state is also a FIFO barrier: all old state/candidate events precede it,
        // and events from the replacement transport follow it.
        let _ = signal.send(LocalSignal::PeerState {
            peer_id: peer_id.to_string(),
            generation,
            state: "connecting".to_string(),
        });
    }
}

async fn cancel_outbound_writer(
    outbound_tx: tokio::sync::mpsc::Sender<OutboundPacket>,
    outbound_writer: tokio::task::JoinHandle<()>,
) {
    // Closing an mpsc sender normally lets the receiver drain buffered packets. Removal is an
    // authorization boundary, so abort the task as well and await cancellation before closing the
    // peer connection. Dropping the task future also drops any active epoch permit immediately.
    drop(outbound_tx);
    outbound_writer.abort();
    let _ = outbound_writer.await;
}

async fn close_peer_handle(handle: PeerHandle) {
    let PeerHandle {
        pc,
        outbound_tx,
        outbound_writer,
        stats_worker,
        ..
    } = handle;
    cancel_outbound_writer(outbound_tx, outbound_writer).await;
    stats_worker.abort();
    let _ = stats_worker.await;
    let _ = pc.close().await;
}

#[derive(Clone, Copy)]
struct PeerConfig {
    relay_only: bool,
    generation: u32,
    min_encoder_epoch: u64,
}

pub struct RtcEngine {
    rt: Runtime,
    apis: RtcApis,
    peers: Mutex<HashMap<String, PeerHandle>>,
    peer_configs: Mutex<HashMap<String, PeerConfig>>,
    ice_servers: Mutex<Vec<RTCIceServer>>,
    out_local_signal: Sender<LocalSignal>,
    receive_queue: Arc<SharedReceiveQueue>,
    outbound_epoch: Arc<SharedOutboundEpoch>,
    outbound_media_sequence: Mutex<u64>,
    counters: Arc<NativeCounters>,
    media_receive: Arc<MediaReceiveCounters>,
    encoder_policy: Arc<SharedEncoderPolicy>,
    network_paths: Arc<SharedNetworkPathCache>,

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

struct RtcApis {
    ephemeral: Arc<API>,
    shared_udp: Option<Arc<API>>,
}

impl RtcApis {
    fn new(rt: &Runtime) -> Self {
        let ephemeral = Arc::new(build_api(None).expect("rtc WebRTC API"));
        let shared_udp = rt.block_on(async {
            match UdpSocket::bind("0.0.0.0:0").await {
                Ok(socket) => {
                    let local_addr = socket.local_addr().ok();
                    let mux = UDPMuxDefault::new(UDPMuxParams::new(socket));
                    match build_api(Some(UDPNetwork::Muxed(mux))) {
                        Ok(api) => {
                            if let Some(addr) = local_addr {
                                eprintln!(
                                    "pc-capture: rtc shared-udp-mux ready local={addr} family=ipv4"
                                );
                            }
                            Some(Arc::new(api))
                        }
                        Err(error) => {
                            eprintln!(
                                "pc-capture: rtc shared-udp-mux disabled reason=api-build error={error}"
                            );
                            None
                        }
                    }
                }
                Err(error) => {
                    eprintln!(
                        "pc-capture: rtc shared-udp-mux disabled reason=socket-bind error={error}"
                    );
                    None
                }
            }
        });
        Self {
            ephemeral,
            shared_udp,
        }
    }

    fn for_peer(&self, ice_servers: &[RTCIceServer], relay_only: bool) -> Arc<API> {
        if should_use_shared_udp_mux(ice_servers, relay_only) {
            if let Some(api) = &self.shared_udp {
                return api.clone();
            }
        }
        self.ephemeral.clone()
    }
}

fn build_api(udp_network: Option<UDPNetwork>) -> Result<API, webrtc::Error> {
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
    let mut setting_engine = SettingEngine::default();
    setting_engine.set_relay_acceptance_min_wait(Some(RELAY_ACCEPTANCE_MIN_WAIT));
    if let Some(udp_network) = udp_network {
        // The shared socket is IPv4 so the mux never advertises an IPv6 candidate it cannot send.
        setting_engine.set_network_types(vec![NetworkType::Udp4]);
        setting_engine.set_udp_network(udp_network);
    }
    Ok(APIBuilder::new()
        .with_media_engine(m)
        .with_interceptor_registry(registry)
        .with_setting_engine(setting_engine)
        .build())
}

struct CreatePeerArgs {
    api: Arc<API>,
    peer_id: String,
    offerer: bool,
    ice_servers: Vec<RTCIceServer>,
    relay_only: bool,
    generation: u32,
    out_local_signal: Sender<LocalSignal>,
    receive_queue: Arc<SharedReceiveQueue>,
    outbound_epoch: Arc<SharedOutboundEpoch>,
    counters: Arc<NativeCounters>,
    encoder_policy: Arc<SharedEncoderPolicy>,
    network_paths: Arc<SharedNetworkPathCache>,
    min_encoder_epoch: u64,
}

async fn create_peer(args: CreatePeerArgs) -> Result<PeerHandle, webrtc::Error> {
    let CreatePeerArgs {
        api,
        peer_id,
        offerer,
        ice_servers,
        relay_only,
        generation,
        out_local_signal,
        receive_queue,
        outbound_epoch,
        counters,
        encoder_policy,
        network_paths,
        min_encoder_epoch,
    } = args;
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

    // Isolate each peer's potentially blocking WebRTC write behind a small bounded queue. A
    // congested route can shed its own stale voice packets without delaying capture or every
    // healthy peer. Epoch permits retain the synchronous privacy guarantee despite async writes.
    let (outbound_tx, mut outbound_rx) =
        tokio::sync::mpsc::channel::<OutboundPacket>(RTP_EGRESS_QUEUE_CAPACITY);
    let writer_track = track.clone();
    let writer_counters = counters.clone();
    let writer_epoch = outbound_epoch.clone();
    let outbound_writer = tokio::spawn(async move {
        let mut last_packetized_media_sequence = None;
        while let Some(packet) = outbound_rx.recv().await {
            let Some(_permit) = writer_epoch.begin(packet.encoder_epoch) else {
                writer_counters
                    .rtp_tx_stale_epoch_dropped
                    .fetch_add(1, Ordering::Relaxed);
                continue;
            };
            // Track the shared capture timeline independently for every peer. Queue-full shedding
            // can remove different Opus frames on different routes; telling TrackLocalStaticSample
            // exactly how many frames were omitted advances both RTP sequence and timestamp so the
            // receiver observes a real gap and can use FEC/PLC/DRED. Epoch-rejected packets remain
            // uncommitted here, so the next authorized packet includes them in its gap.
            let prev_dropped_packets =
                dropped_packets_between(last_packetized_media_sequence, packet.media_sequence);
            last_packetized_media_sequence = Some(packet.media_sequence);
            let sample = Sample {
                data: packet.payload,
                duration: Duration::from_millis(20),
                prev_dropped_packets,
                ..Default::default()
            };
            match tokio::time::timeout(RTP_EGRESS_WRITE_TIMEOUT, writer_track.write_sample(&sample))
                .await
            {
                Ok(Ok(())) => {
                    writer_counters.rtp_tx_ok.fetch_add(1, Ordering::Relaxed);
                }
                Ok(Err(_)) => {
                    writer_counters
                        .rtp_tx_errors
                        .fetch_add(1, Ordering::Relaxed);
                }
                Err(_) => {
                    writer_counters
                        .rtp_tx_write_timeouts
                        .fetch_add(1, Ordering::Relaxed);
                    writer_counters
                        .rtp_tx_errors
                        .fetch_add(1, Ordering::Relaxed);
                }
            }
        }
    });

    let candidate_gate = Arc::new(LocalCandidateGate::default());
    let cand_gate = candidate_gate.clone();
    let cand_signal = out_local_signal.clone();
    let cand_peer = peer_id.clone();
    pc.on_ice_candidate(Box::new(move |c| {
        let gate = cand_gate.clone();
        let signal = cand_signal.clone();
        let pid = cand_peer.clone();
        Box::pin(async move {
            if let Some(c) = c {
                if let Ok(init) = c.to_json() {
                    if let Ok(s) = serde_json::to_string(&init) {
                        gate.send_or_queue(s, &signal, &pid, generation);
                    }
                }
            }
        })
    }));

    let state_signal_gate = Arc::new(StateSignalGate::new());
    let state_gate = state_signal_gate.clone();
    let state_signal = out_local_signal.clone();
    let state_peer = peer_id.clone();
    pc.on_peer_connection_state_change(Box::new(move |s: RTCPeerConnectionState| {
        let gate = state_gate.clone();
        let signal = state_signal.clone();
        let pid = state_peer.clone();
        Box::pin(async move {
            gate.send_if_enabled(&signal, &pid, generation, s.to_string());
        })
    }));

    let track_peer = peer_id.clone();
    let track_counters = counters.clone();
    let track_receive_queue = receive_queue.clone();
    pc.on_track(Box::new(move |track, _receiver, _transceiver| {
        let queue = track_receive_queue.clone();
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
                        let received = ReceivedPacket {
                            peer_id: pid.clone(),
                            generation,
                            sequence: pkt.header.sequence_number,
                            timestamp: pkt.header.timestamp,
                            arrival: Instant::now(),
                            payload: pkt.payload.to_vec(),
                        };
                        queue.push(received);
                    }
                }
            });
        })
    }));

    let stats_pc = Arc::clone(&pc);
    let stats_peer = peer_id.clone();
    let stats_policy = encoder_policy.clone();
    let stats_paths = network_paths.clone();
    let stats_worker = tokio::spawn(async move {
        let mut interval = tokio::time::interval(ENCODER_STATS_INTERVAL);
        loop {
            interval.tick().await;
            if stats_pc.connection_state() == RTCPeerConnectionState::Closed {
                stats_policy.remove_peer(&stats_peer, Some(generation));
                stats_paths.remove_peer(&stats_peer, Some(generation));
                break;
            }
            let report = stats_pc.get_stats().await;
            stats_paths.update(network_path_from_stats(
                stats_peer.clone(),
                generation,
                &report,
            ));
            if let Some((feedback, packets_received, rtt_measurements)) =
                encoder_feedback_from_stats(&report)
            {
                stats_policy.update_peer(
                    &stats_peer,
                    generation,
                    feedback,
                    packets_received,
                    rtt_measurements,
                    Instant::now(),
                );
            }
        }
    });

    if offerer {
        let offer = match pc.create_offer(None).await {
            Ok(offer) => offer,
            Err(error) => {
                stats_worker.abort();
                let _ = stats_worker.await;
                cancel_outbound_writer(outbound_tx, outbound_writer).await;
                let _ = pc.close().await;
                return Err(error);
            }
        };
        if let Err(error) = pc.set_local_description(offer).await {
            stats_worker.abort();
            let _ = stats_worker.await;
            cancel_outbound_writer(outbound_tx, outbound_writer).await;
            let _ = pc.close().await;
            return Err(error);
        }
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
        outbound_tx,
        outbound_writer,
        stats_worker,
        candidate_gate,
        state_signal_gate,
        generation,
        min_encoder_epoch,
    })
}

fn is_stun_url(url: &str) -> bool {
    url.trim()
        .get(..5)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case("stun:"))
}

fn is_supported_udp_turn_url(url: &str) -> bool {
    let trimmed = url.trim();
    if !trimmed
        .get(..5)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case("turn:"))
    {
        return false;
    }
    let Some((_, query)) = trimmed.split_once('?') else {
        return !trimmed.contains('#');
    };
    if query.is_empty() || query.contains(['&', '%', '#']) {
        return false;
    }
    let Some((key, value)) = query.split_once('=') else {
        return false;
    };
    !value.contains('=')
        && key.eq_ignore_ascii_case("transport")
        && value.eq_ignore_ascii_case("udp")
}

/// Normal peers gather STUN and supported TURN/UDP candidates together. ICE can therefore keep a
/// direct route when one appears during the relay acceptance window instead of requiring a peer
/// rebuild. Explicit relay-only peers still receive only TURN/UDP URLs. Unsupported TCP/TLS URLs
/// stay out of the configuration because webrtc-rs 0.17.1 cannot gather them.
fn servers_for_policy(servers: &[RTCIceServer], relay_only: bool) -> Vec<RTCIceServer> {
    servers
        .iter()
        .filter_map(|server| {
            let urls: Vec<String> = server
                .urls
                .iter()
                .filter(|url| {
                    if relay_only {
                        is_supported_udp_turn_url(url)
                    } else {
                        is_stun_url(url) || is_supported_udp_turn_url(url)
                    }
                })
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

/// webrtc-ice 0.17.1 deliberately skips server-reflexive gathering when `UDPNetwork::Muxed` is
/// active. Use the single mesh socket only when no STUN URL would be sacrificed; Internet peers
/// with STUN stay on reusable ephemeral-socket APIs so the optimization cannot increase relaying.
fn should_use_shared_udp_mux(servers: &[RTCIceServer], relay_only: bool) -> bool {
    !servers.is_empty()
        && !relay_only
        && !servers
            .iter()
            .flat_map(|server| server.urls.iter())
            .any(|url| is_stun_url(url))
}

fn emit_buffered_local_candidates(
    gate: &LocalCandidateGate,
    signal: &Sender<LocalSignal>,
    peer_id: &str,
    generation: u32,
) {
    for candidate in gate.release() {
        let _ = signal.send(LocalSignal::Candidate {
            peer_id: peer_id.to_string(),
            generation,
            candidate,
        });
    }
}

async fn wait_for_ice_gathering_idle(pc: &RTCPeerConnection) -> bool {
    tokio::time::timeout(ICE_GATHERING_IDLE_WAIT, async {
        while pc.ice_gathering_state() == RTCIceGatheringState::Gathering {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .is_ok()
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
        let media_receive = Arc::new(MediaReceiveCounters::default());
        let receive_queue = Arc::new(SharedReceiveQueue::new(media_receive.clone()));
        let outbound_epoch = Arc::new(SharedOutboundEpoch::default());
        let encoder_policy = Arc::new(SharedEncoderPolicy::default());
        let network_paths = Arc::new(SharedNetworkPathCache::default());
        let policy_worker = encoder_policy.clone();
        rt.spawn(async move {
            let mut interval = tokio::time::interval(ENCODER_STATS_INTERVAL);
            loop {
                interval.tick().await;
                policy_worker.evaluate(Instant::now());
            }
        });
        let apis = RtcApis::new(&rt);
        RtcEngine {
            rt,
            apis,
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
            receive_queue,
            outbound_epoch,
            outbound_media_sequence: Mutex::new(0),
            counters,
            media_receive,
            encoder_policy,
            network_paths,
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

    pub fn add_peer(
        &self,
        peer_id: String,
        offerer: bool,
        relay_only: bool,
        generation: u32,
        min_encoder_epoch: u64,
    ) {
        eprintln!(
            "pc-capture: rtc peer-add begin peer={peer_id} generation={generation} offerer={offerer} relay_only={relay_only} min_encoder_epoch={min_encoder_epoch}"
        );
        // An epoch boundary is a privacy floor: never lower it for a reused peer identity, even
        // if duplicate or replacement signaling arrives with stale control metadata.
        let config = {
            let mut configs = self.peer_configs.lock().unwrap();
            let effective_min_epoch = configs.get(&peer_id).map_or(min_encoder_epoch, |existing| {
                existing.min_encoder_epoch.max(min_encoder_epoch)
            });
            let config = PeerConfig {
                relay_only,
                generation,
                min_encoder_epoch: effective_min_epoch,
            };
            configs.insert(peer_id.clone(), config);
            config
        };
        self.encoder_policy.register_peer(&peer_id, generation);
        self.network_paths.register_peer(&peer_id, generation);
        self.receive_queue.remove_peer(&peer_id);

        let existing = self.peers.lock().unwrap().remove(&peer_id);
        if let Some(mut handle) = existing {
            if handle.generation == generation {
                handle.min_encoder_epoch = handle.min_encoder_epoch.max(config.min_encoder_epoch);
                self.peers.lock().unwrap().insert(peer_id, handle);
                eprintln!(
                    "pc-capture: rtc peer-add reused generation={generation} min_encoder_epoch={}",
                    config.min_encoder_epoch
                );
                return;
            }
            self.rt.block_on(close_peer_handle(handle));
        }
        let signal = self.out_local_signal.clone();
        let servers = self.ice_servers.lock().unwrap().clone();
        let api = self.apis.for_peer(&servers, relay_only);
        let pid = peer_id.clone();
        match self.rt.block_on(create_peer(CreatePeerArgs {
            api,
            peer_id: pid,
            offerer,
            ice_servers: servers,
            relay_only,
            generation,
            out_local_signal: signal,
            receive_queue: self.receive_queue.clone(),
            outbound_epoch: self.outbound_epoch.clone(),
            counters: self.counters.clone(),
            encoder_policy: self.encoder_policy.clone(),
            network_paths: self.network_paths.clone(),
            min_encoder_epoch: config.min_encoder_epoch,
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
        self.encoder_policy.remove_peer(peer_id, None);
        self.network_paths.remove_peer(peer_id, None);
        self.receive_queue.remove_peer(peer_id);
        let handle = self.peers.lock().unwrap().remove(peer_id);
        if let Some(h) = handle {
            self.rt.block_on(close_peer_handle(h));
        }
        eprintln!("pc-capture: rtc peer-remove peer={peer_id}");
    }

    fn rebuild_peer_for_ice(
        &self,
        peer_id: &str,
        relay_only: bool,
        create_offer: bool,
        generation: u32,
    ) -> bool {
        let Some(handle) = self.peers.lock().unwrap().remove(peer_id) else {
            eprintln!(
                "pc-capture: rtc ice-restart rebuild rejected peer={peer_id} generation={generation} reason=missing-peer"
            );
            self.emit_failed(peer_id, generation);
            return false;
        };
        if handle.generation != generation {
            let current_generation = handle.generation;
            self.peers
                .lock()
                .unwrap()
                .insert(peer_id.to_string(), handle);
            eprintln!(
                "pc-capture: rtc ice-restart rebuild rejected peer={peer_id} generation={generation} current_generation={current_generation} reason=generation-mismatch"
            );
            self.emit_failed(peer_id, generation);
            return false;
        }
        // Hold the old candidate path first, then disable old state callbacks and enqueue a known
        // `connecting` state. Since both callbacks keep their gates locked through channel enqueue,
        // this state is a FIFO replacement barrier: no signal from the old transport can follow it.
        handle.candidate_gate.hold();
        handle.state_signal_gate.disable_with_replacement_barrier(
            &self.out_local_signal,
            peer_id,
            generation,
        );
        let min_encoder_epoch = handle.min_encoder_epoch;
        self.rt.block_on(close_peer_handle(handle));
        self.pending_ice.lock().unwrap().remove(peer_id);
        self.receive_queue.remove_peer(peer_id);
        self.encoder_policy.register_peer(peer_id, generation);
        self.network_paths.register_peer(peer_id, generation);

        if let Some(config) = self.peer_configs.lock().unwrap().get_mut(peer_id) {
            if config.generation == generation {
                config.relay_only = relay_only;
            }
        }
        let servers = self.ice_servers.lock().unwrap().clone();
        let api = self.apis.for_peer(&servers, relay_only);
        let result = self.rt.block_on(create_peer(CreatePeerArgs {
            api,
            peer_id: peer_id.to_string(),
            offerer: create_offer,
            ice_servers: servers,
            relay_only,
            generation,
            out_local_signal: self.out_local_signal.clone(),
            receive_queue: self.receive_queue.clone(),
            outbound_epoch: self.outbound_epoch.clone(),
            counters: self.counters.clone(),
            encoder_policy: self.encoder_policy.clone(),
            network_paths: self.network_paths.clone(),
            min_encoder_epoch,
        }));
        match result {
            Ok(handle) => {
                self.peers
                    .lock()
                    .unwrap()
                    .insert(peer_id.to_string(), handle);
                eprintln!(
                    "pc-capture: rtc ice-restart rebuilt peer={peer_id} generation={generation} relay_only={relay_only} create_offer={create_offer}"
                );
                true
            }
            Err(error) => {
                eprintln!(
                    "pc-capture: rtc ice-restart rebuild failed peer={peer_id} generation={generation} relay_only={relay_only} create_offer={create_offer} error={error}"
                );
                self.emit_failed(peer_id, generation);
                false
            }
        }
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
            .map(|h| (Arc::clone(&h.pc), h.candidate_gate.clone()));
        let (pc, candidate_gate) = match existing {
            Some(existing) => existing,
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
                let servers = self.ice_servers.lock().unwrap().clone();
                let api = self.apis.for_peer(&servers, config.relay_only);
                let pid = peer_id.to_string();
                match self.rt.block_on(create_peer(CreatePeerArgs {
                    api,
                    peer_id: pid,
                    offerer: false,
                    ice_servers: servers,
                    relay_only: config.relay_only,
                    generation: config.generation,
                    out_local_signal: signal,
                    receive_queue: self.receive_queue.clone(),
                    outbound_epoch: self.outbound_epoch.clone(),
                    counters: self.counters.clone(),
                    encoder_policy: self.encoder_policy.clone(),
                    network_paths: self.network_paths.clone(),
                    min_encoder_epoch: config.min_encoder_epoch,
                })) {
                    Ok(h) => {
                        let pc = Arc::clone(&h.pc);
                        let candidate_gate = h.candidate_gate.clone();
                        self.peers.lock().unwrap().insert(peer_id.to_string(), h);
                        (pc, candidate_gate)
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
        let result = self.rt.block_on(async move {
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
        });
        match result {
            Err(error) => {
                if is_offer {
                    candidate_gate.cancel();
                }
                eprintln!(
                    "pc-capture: remote SDP failed peer={peer_id} generation={generation} type={sdp_type}: {error}"
                );
                let _ = failed_signal.send(LocalSignal::PeerState {
                    peer_id: failed_peer,
                    generation,
                    state: "failed".to_string(),
                });
            }
            Ok(()) => {
                if is_offer {
                    emit_buffered_local_candidates(
                        &candidate_gate,
                        &self.out_local_signal,
                        peer_id,
                        generation,
                    );
                }
                eprintln!(
                    "pc-capture: rtc remote-sdp applied peer={peer_id} generation={generation} type={sdp_type} pending_ice={pending_count}"
                );
            }
        }
    }

    /// Re-gathers ICE against the latest server snapshot and optionally emits a replacement
    /// offer. webrtc-rs 0.17.1 can wedge while applying a restart offer to an established
    /// connection, so established peers are rebuilt in place under the same identity/generation.
    /// A still-new peer can use the library restart primitive directly.
    pub fn restart_ice(&self, peer_id: &str, relay_only: bool, create_offer: bool) -> bool {
        let peer = self.peers.lock().unwrap().get(peer_id).map(|handle| {
            (
                Arc::clone(&handle.pc),
                handle.candidate_gate.clone(),
                handle.generation,
            )
        });
        let Some((pc, candidate_gate, generation)) = peer else {
            eprintln!("pc-capture: rtc ice-restart rejected peer={peer_id} reason=missing-peer");
            if let Some(config) = self.peer_configs.lock().unwrap().get(peer_id).copied() {
                self.emit_failed(peer_id, config.generation);
            }
            return false;
        };
        if pc.connection_state() != RTCPeerConnectionState::New {
            return self.rebuild_peer_for_ice(peer_id, relay_only, create_offer, generation);
        }

        let servers = servers_for_policy(&self.ice_servers.lock().unwrap(), relay_only);
        let policy = if relay_only {
            RTCIceTransportPolicy::Relay
        } else {
            RTCIceTransportPolicy::All
        };
        let signal = self.out_local_signal.clone();
        let pid = peer_id.to_string();
        if create_offer && !self.rt.block_on(wait_for_ice_gathering_idle(&pc)) {
            eprintln!(
                "pc-capture: rtc ice-restart rejected peer={peer_id} generation={generation} reason=gathering-timeout"
            );
            self.emit_failed(peer_id, generation);
            return false;
        }
        candidate_gate.hold();

        let result = self.rt.block_on(async move {
            let operation = async {
                // Preserve immutable certificates/bundle settings while refreshing the fields
                // that are allowed to change after negotiation has started.
                let mut configuration = pc.get_configuration().await;
                configuration.ice_servers = servers;
                configuration.ice_transport_policy = policy;
                pc.set_configuration(configuration).await?;

                if create_offer {
                    pc.restart_ice().await?;
                    let offer = pc.create_offer(None).await?;
                    pc.set_local_description(offer).await?;
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
            };
            match tokio::time::timeout(ICE_RESTART_OPERATION_TIMEOUT, operation).await {
                Ok(Ok(())) => Ok(()),
                Ok(Err(error)) => Err(error.to_string()),
                Err(_) => Err(format!(
                    "operation timed out after {}ms",
                    ICE_RESTART_OPERATION_TIMEOUT.as_millis()
                )),
            }
        });

        match result {
            Ok(()) => {
                if let Some(config) = self.peer_configs.lock().unwrap().get_mut(peer_id) {
                    if config.generation == generation {
                        config.relay_only = relay_only;
                    }
                }
                if create_offer {
                    emit_buffered_local_candidates(
                        &candidate_gate,
                        &self.out_local_signal,
                        peer_id,
                        generation,
                    );
                }
                eprintln!(
                    "pc-capture: rtc ice-restart applied peer={peer_id} generation={generation} relay_only={relay_only} create_offer={create_offer}"
                );
                true
            }
            Err(error) => {
                candidate_gate.cancel();
                eprintln!(
                    "pc-capture: rtc ice-restart failed peer={peer_id} generation={generation} relay_only={relay_only} create_offer={create_offer} error={error}"
                );
                self.emit_failed(peer_id, generation);
                false
            }
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

    /// Sends one packet only to peers authorized for the encoder history represented by
    /// `packet_encoder_epoch`. A newly added track is invisible to any packet encoded before its
    /// reset boundary, closing the encode-before-peer-add race for DRED history.
    pub fn send_opus(&self, pkt: &[u8], packet_encoder_epoch: u64) {
        // One sequence belongs to the shared encoded frame, not to an individual peer. Per-peer
        // writers compare this value with their last packetized frame to make route-local queue
        // shedding visible as an RTP gap. Keep assignment and fan-out in one tiny critical section:
        // current callers are single-writer, but this also prevents future concurrent callers from
        // assigning N/N+1 and enqueueing them in reverse order. Wrapping is intentional.
        let mut next_media_sequence = self.outbound_media_sequence.lock().unwrap();
        let media_sequence = *next_media_sequence;
        *next_media_sequence = next_media_sequence.wrapping_add(1);
        let senders: Vec<tokio::sync::mpsc::Sender<OutboundPacket>> = self
            .peers
            .lock()
            .unwrap()
            .values()
            .filter(|handle| {
                encoder_epoch_authorizes_peer(packet_encoder_epoch, handle.min_encoder_epoch)
            })
            .map(|handle| handle.outbound_tx.clone())
            .collect();
        if senders.is_empty() {
            return;
        }
        self.counters
            .rtp_tx_attempts
            .fetch_add(senders.len() as u64, Ordering::Relaxed);
        let payload = Bytes::copy_from_slice(pkt);
        for sender in senders {
            let packet = OutboundPacket {
                payload: payload.clone(),
                encoder_epoch: packet_encoder_epoch,
                media_sequence,
            };
            match sender.try_send(packet) {
                Ok(()) => {
                    let depth = sender.max_capacity().saturating_sub(sender.capacity());
                    self.counters.record_rtp_tx_queue_depth(depth);
                }
                Err(tokio::sync::mpsc::error::TrySendError::Full(_)) => {
                    self.counters
                        .rtp_tx_queue_dropped
                        .fetch_add(1, Ordering::Relaxed);
                }
                Err(tokio::sync::mpsc::error::TrySendError::Closed(_)) => {
                    self.counters.rtp_tx_errors.fetch_add(1, Ordering::Relaxed);
                }
            }
        }
        drop(next_media_sequence);
    }

    /// Raises the global privacy floor and waits for every already-started per-peer write to
    /// finish. Queued packets from an older epoch are rejected by the writer before WebRTC sees
    /// them. Returning false is fail-closed: callers must stop the media session.
    pub fn advance_encoder_epoch(&self, epoch: u64) -> bool {
        self.outbound_epoch
            .advance_to(epoch, RTP_EGRESS_PRIVACY_DRAIN_TIMEOUT)
    }

    pub fn recv(&self) -> Option<ReceivedPacket> {
        while let Some(packet) = self.receive_queue.pop() {
            let is_current = self
                .peer_configs
                .lock()
                .unwrap()
                .get(&packet.peer_id)
                .is_some_and(|config| config.generation == packet.generation);
            if is_current {
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

    /// Returns the latest sanitized WebRTC/ICE rows from the existing two-second sampler.
    /// This is a cache read and performs no runtime or per-peer `get_stats` work.
    pub fn network_path_snapshots(&self) -> Vec<PeerNetworkPathSnapshot> {
        self.network_paths.snapshots()
    }

    #[cfg(test)]
    pub fn ice_servers_snapshot(&self) -> Vec<RTCIceServer> {
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
        self.peer_configs.lock().unwrap().insert(
            peer_id.to_string(),
            PeerConfig {
                relay_only: false,
                generation,
                min_encoder_epoch: 0,
            },
        );
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

#[cfg(test)]
mod tests {
    use super::*;
    use serde::Deserialize;
    use std::sync::mpsc::channel;
    use std::time::Instant;

    #[derive(Deserialize)]
    struct LiveTurnResponse {
        #[serde(rename = "iceServers")]
        ice_servers: Vec<crate::proto::IceServer>,
    }

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
    }

    #[test]
    fn normal_and_relay_policies_split_mixed_ice_servers() {
        let mixed = vec![
            RTCIceServer {
                urls: vec![
                    "stun:stun.example.com:3478".to_string(),
                    "turn:turn.example.com:3478?transport=udp".to_string(),
                    "turn:turn.example.com:3478?foo=bar".to_string(),
                    "turn:turn.example.com:3478?transport=udp&transport=udp".to_string(),
                    "turn:turn.example.com:3478?transport=udp&transport=tcp".to_string(),
                    "turn:turn.example.com:3478?transport".to_string(),
                    "turn:turn.example.com:80?trans%70ort=tcp".to_string(),
                    "turn:turn.example.com:3478#fragment".to_string(),
                    "turn:turn.example.com:80?transport=tcp".to_string(),
                    "turns:turn.example.com:5349?transport=tcp".to_string(),
                ],
                username: "user".to_string(),
                credential: "secret".to_string(),
            },
            RTCIceServer {
                urls: vec!["STUN:backup.example.com:3478".to_string()],
                ..Default::default()
            },
        ];

        let normal = servers_for_policy(&mixed, false);
        assert_eq!(normal.len(), 2);
        assert_eq!(
            normal[0].urls,
            vec![
                "stun:stun.example.com:3478",
                "turn:turn.example.com:3478?transport=udp"
            ]
        );
        assert_eq!(normal[1].urls, vec!["STUN:backup.example.com:3478"]);
        assert_eq!(normal[0].username, "user");
        assert_eq!(normal[0].credential, "secret");

        let udp = servers_for_policy(&mixed, true);
        assert_eq!(udp.len(), 1);
        assert_eq!(
            udp[0].urls,
            vec!["turn:turn.example.com:3478?transport=udp"]
        );

        assert_eq!(udp[0].username, "user");
        assert_eq!(udp[0].credential, "secret");
    }

    #[test]
    fn shared_udp_mux_never_replaces_stun_gathering() {
        let stun = RTCIceServer {
            urls: vec!["stun:stun.example.com:3478".to_string()],
            ..Default::default()
        };
        let turn = RTCIceServer {
            urls: vec!["turn:turn.example.com:3478?transport=udp".to_string()],
            ..Default::default()
        };

        assert!(!should_use_shared_udp_mux(
            &[stun.clone(), turn.clone()],
            false
        ));
        assert!(should_use_shared_udp_mux(
            std::slice::from_ref(&turn),
            false
        ));
        assert!(!should_use_shared_udp_mux(&[], false));
        assert!(!should_use_shared_udp_mux(&[turn], true));
    }

    #[test]
    fn local_candidate_gate_preserves_restart_signal_order() {
        let gate = LocalCandidateGate::default();
        assert_eq!(
            gate.queue_or_pass("initial".to_string()).as_deref(),
            Some("initial")
        );

        gate.hold();
        assert!(gate.queue_or_pass("restart-a".to_string()).is_none());
        assert!(gate.queue_or_pass("restart-b".to_string()).is_none());
        assert_eq!(gate.release(), vec!["restart-a", "restart-b"]);
        assert_eq!(
            gate.queue_or_pass("after-sdp".to_string()).as_deref(),
            Some("after-sdp")
        );
    }

    #[test]
    fn ice_restart_offer_uses_new_credentials_and_sdp_precedes_candidates() {
        let (tx, rx) = channel::<LocalSignal>();
        let engine = RtcEngine::new(tx);
        engine.set_ice_servers(&[]);
        engine.add_peer("restart-offerer".to_string(), true, false, 51, 0);

        let initial_offer = rx
            .try_iter()
            .find_map(|signal| match signal {
                LocalSignal::Sdp { sdp_type, sdp, .. } if sdp_type == "offer" => Some(sdp),
                _ => None,
            })
            .expect("initial offer");
        let pc = Arc::clone(&engine.peers.lock().unwrap()["restart-offerer"].pc);
        assert!(engine.rt.block_on(wait_for_ice_gathering_idle(&pc)));
        // Discard any final candidates from the initial generation before checking restart order.
        let _ = rx.try_iter().count();

        assert!(engine.restart_ice("restart-offerer", false, true));
        let restart_signals: Vec<_> = rx
            .try_iter()
            .filter(|signal| {
                matches!(
                    signal,
                    LocalSignal::Sdp { .. } | LocalSignal::Candidate { .. }
                )
            })
            .collect();
        let restart_offer = restart_signals
            .iter()
            .find_map(|signal| match signal {
                LocalSignal::Sdp { sdp_type, sdp, .. } if sdp_type == "offer" => Some(sdp),
                _ => None,
            })
            .expect("restart offer");

        assert_ne!(initial_offer, *restart_offer);
        assert!(matches!(
            restart_signals.first(),
            Some(LocalSignal::Sdp { .. })
        ));
        assert!(!engine.peer_configs.lock().unwrap()["restart-offerer"].relay_only);
    }

    #[test]
    fn answerer_prepare_holds_candidates_until_remote_offer() {
        let (offer_tx, offer_rx) = channel::<LocalSignal>();
        let (answer_tx, answer_rx) = channel::<LocalSignal>();
        let offerer = RtcEngine::new(offer_tx);
        let answerer = RtcEngine::new(answer_tx);
        offerer.set_ice_servers(&[]);
        answerer.set_ice_servers(&[]);
        offerer.add_peer("answerer".to_string(), true, false, 51, 0);
        answerer.add_peer("offerer".to_string(), false, false, 52, 0);

        let deadline = Instant::now() + Duration::from_secs(5);
        let mut connected = false;
        while Instant::now() < deadline && !connected {
            while let Ok(signal) = offer_rx.try_recv() {
                match signal {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => {
                        answerer.set_remote_sdp("offerer", &sdp_type, &sdp)
                    }
                    LocalSignal::Candidate { candidate, .. } => {
                        answerer.add_ice_candidate("offerer", &candidate)
                    }
                    LocalSignal::PeerState { state, .. } => connected |= state == "connected",
                }
            }
            while let Ok(signal) = answer_rx.try_recv() {
                match signal {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => {
                        offerer.set_remote_sdp("answerer", &sdp_type, &sdp)
                    }
                    LocalSignal::Candidate { candidate, .. } => {
                        offerer.add_ice_candidate("answerer", &candidate)
                    }
                    LocalSignal::PeerState { state, .. } => connected |= state == "connected",
                }
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        assert!(connected, "initial peer connection did not complete");
        let _ = offer_rx.try_iter().count();
        let _ = answer_rx.try_iter().count();

        assert!(answerer.restart_ice("offerer", false, false));
        assert!(answer_rx.try_iter().all(|signal| !matches!(
            signal,
            LocalSignal::Sdp { .. } | LocalSignal::Candidate { .. }
        )));

        assert!(offerer.restart_ice("answerer", false, true));
        let payload = vec![0x5a; 80];
        let deadline = Instant::now() + Duration::from_secs(5);
        let mut saw_offer = false;
        let mut saw_answer = false;
        let mut saw_closed = false;
        let mut received = false;
        while Instant::now() < deadline && !received {
            while let Ok(signal) = offer_rx.try_recv() {
                match signal {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => {
                        saw_offer |= sdp_type == "offer";
                        answerer.set_remote_sdp("offerer", &sdp_type, &sdp);
                    }
                    LocalSignal::Candidate { candidate, .. } => {
                        answerer.add_ice_candidate("offerer", &candidate)
                    }
                    LocalSignal::PeerState { state, .. } => saw_closed |= state == "closed",
                }
            }
            while let Ok(signal) = answer_rx.try_recv() {
                match signal {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => {
                        saw_answer |= sdp_type == "answer";
                        offerer.set_remote_sdp("answerer", &sdp_type, &sdp);
                    }
                    LocalSignal::Candidate { candidate, .. } => {
                        offerer.add_ice_candidate("answerer", &candidate)
                    }
                    LocalSignal::PeerState { state, .. } => saw_closed |= state == "closed",
                }
            }
            offerer.send_opus(&payload, 0);
            while let Some(packet) = answerer.recv() {
                received |= packet.payload == payload;
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        assert!(saw_offer, "restart offer was not emitted");
        assert!(saw_answer, "restart answer was not emitted");
        assert!(
            !saw_closed,
            "transport replacement leaked a closed peer state"
        );
        assert!(
            received,
            "media did not recover after the coordinated ICE restart"
        );
    }

    #[test]
    fn malformed_remote_sdp_emits_failed_for_current_generation() {
        let (tx, rx) = channel::<LocalSignal>();
        let engine = RtcEngine::new(tx);
        engine.add_peer("bad-sdp".to_string(), false, false, 41, 0);

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
                min_encoder_epoch: 0,
            },
        );
        engine.receive_queue.push(ReceivedPacket {
            peer_id: "peer".to_string(),
            generation: 41,
            sequence: 1,
            timestamp: 960,
            arrival: Instant::now(),
            payload: vec![1],
        });
        engine.receive_queue.push(ReceivedPacket {
            peer_id: "peer".to_string(),
            generation: 42,
            sequence: 2,
            timestamp: 1_920,
            arrival: Instant::now(),
            payload: vec![2],
        });

        let packet = engine.recv().expect("current generation packet");
        assert_eq!(packet.peer_id, "peer");
        assert_eq!(packet.generation, 42);
        assert_eq!(packet.sequence, 2);
        assert_eq!(packet.timestamp, 1_920);
        assert_eq!(packet.payload, vec![2]);
        assert_eq!(engine.recv(), None);
    }

    #[test]
    fn ingress_queue_is_bounded_per_peer_and_keeps_the_fresh_tail() {
        let counters = Arc::new(MediaReceiveCounters::default());
        let queue = SharedReceiveQueue::new(counters.clone());
        for sequence in 0..RTP_INGRESS_PER_PEER_CAPACITY + 7 {
            queue.push(ReceivedPacket {
                peer_id: "peer".to_string(),
                generation: 1,
                sequence: sequence as u16,
                timestamp: sequence as u32 * crate::codec::RTP_FRAME_TICKS,
                arrival: Instant::now(),
                payload: vec![1],
            });
        }
        let snapshot = counters.snapshot();
        assert_eq!(snapshot.ingress_queue_overflow, 7);
        assert_eq!(snapshot.ingress_queue_depth_current, 32);
        assert_eq!(snapshot.ingress_peer_queue_depth_max, 32);
        assert_eq!(queue.pop().unwrap().sequence, 7);
    }

    #[test]
    fn ingress_queue_round_robins_talkers_instead_of_draining_one_burst_first() {
        let counters = Arc::new(MediaReceiveCounters::default());
        let queue = SharedReceiveQueue::new(counters);
        let packet = |peer: &str, sequence: u16| ReceivedPacket {
            peer_id: peer.to_string(),
            generation: 1,
            sequence,
            timestamp: u32::from(sequence) * crate::codec::RTP_FRAME_TICKS,
            arrival: Instant::now(),
            payload: vec![1],
        };
        queue.push(packet("noisy", 1));
        queue.push(packet("noisy", 2));
        queue.push(packet("noisy", 3));
        queue.push(packet("quiet", 10));

        assert_eq!(queue.pop().unwrap().peer_id, "noisy");
        assert_eq!(queue.pop().unwrap().peer_id, "quiet");
        assert_eq!(queue.pop().unwrap().sequence, 2);
    }

    #[test]
    fn outbound_epoch_gate_waits_for_in_flight_writes_and_rejects_stale_packets() {
        let gate = Arc::new(SharedOutboundEpoch::default());
        let permit = gate.begin(4).expect("initial epoch accepted");
        let advancing = gate.clone();
        let (done_tx, done_rx) = std::sync::mpsc::channel();
        let worker = std::thread::spawn(move || {
            done_tx
                .send(advancing.advance_to(5, Duration::from_secs(1)))
                .unwrap();
        });
        assert!(done_rx.recv_timeout(Duration::from_millis(20)).is_err());
        drop(permit);
        assert!(done_rx.recv_timeout(Duration::from_secs(1)).unwrap());
        worker.join().unwrap();
        assert!(gate.begin(4).is_none());
        assert!(gate.begin(5).is_some());
    }

    #[test]
    fn outbound_media_sequence_reports_healthy_one_and_multiple_frame_gaps() {
        assert_eq!(dropped_packets_between(None, 41), 0);
        assert_eq!(dropped_packets_between(Some(41), 42), 0);
        assert_eq!(dropped_packets_between(Some(41), 43), 1);
        assert_eq!(dropped_packets_between(Some(41), 46), 4);
    }

    #[test]
    fn outbound_media_sequence_wraps_saturates_and_leaves_epoch_rejections_uncommitted() {
        // From MAX-1 to 1, MAX and 0 were omitted across the u64 wrap.
        assert_eq!(dropped_packets_between(Some(u64::MAX - 1), 1), 2);
        assert_eq!(
            dropped_packets_between(Some(0), u64::from(u16::MAX) + 2),
            u16::MAX,
            "Sample can represent at most u16::MAX preceding packet drops"
        );

        // Sequence 8 was rejected by the epoch gate, so the writer retains 7 as its last
        // packetized value and reports that omitted frame when sequence 9 is authorized.
        assert_eq!(dropped_packets_between(Some(7), 9), 1);
    }

    #[test]
    fn cancelling_outbound_writer_discards_buffered_frames_before_returning() {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        let processed = Arc::new(AtomicU64::new(0));
        let observed = processed.clone();

        runtime.block_on(async move {
            let (tx, mut rx) =
                tokio::sync::mpsc::channel::<OutboundPacket>(RTP_EGRESS_QUEUE_CAPACITY);
            let (started_tx, started_rx) = tokio::sync::oneshot::channel();
            let writer = tokio::spawn(async move {
                if rx.recv().await.is_some() {
                    observed.fetch_add(1, Ordering::Relaxed);
                    let _ = started_tx.send(());
                    std::future::pending::<()>().await;
                }
            });
            let packet = |media_sequence| OutboundPacket {
                payload: Bytes::from_static(b"opus"),
                encoder_epoch: 1,
                media_sequence,
            };
            tx.try_send(packet(1)).unwrap();
            tx.try_send(packet(2)).unwrap();
            started_rx.await.unwrap();

            cancel_outbound_writer(tx, writer).await;
        });

        assert_eq!(
            processed.load(Ordering::Relaxed),
            1,
            "the buffered second frame must not drain after removal"
        );
    }

    #[test]
    fn encoder_epoch_filter_blocks_packets_encoded_before_peer_authorization() {
        assert!(!encoder_epoch_authorizes_peer(6, 7));
        assert!(encoder_epoch_authorizes_peer(7, 7));
        assert!(encoder_epoch_authorizes_peer(8, 7));
    }

    #[test]
    fn bwe_selects_fresh_succeeded_nominated_pair_after_ice_switch() {
        let selected = select_freshest_nominated_succeeded([
            // A formerly active pair retains cumulative traffic after it fails. It must never
            // outrank the lower-traffic path that ICE successfully switched to.
            (true, false, 100, 5_000, 1_000_000.0),
            // Renomination can also leave an old pair succeeded+nominated. Its large lifetime
            // count must not beat the new pair's more recent packet activity.
            (true, true, 90, 50_000, 900_000.0),
            (true, true, 110, 250, 80_000.0),
            (false, true, 120, 20_000, 2_000_000.0),
        ]);
        assert_eq!(selected, Some(80_000.0));

        assert_eq!(
            select_freshest_nominated_succeeded([
                (true, false, 100, 5_000, 1_000_000.0),
                (false, true, 120, 20_000, 2_000_000.0),
            ]),
            None,
            "failed and non-nominated pairs cannot supply optimistic BWE"
        );

        assert_eq!(
            select_freshest_nominated_succeeded([
                (true, true, 120, 10, 70_000.0),
                (true, true, 120, 20, 75_000.0),
            ]),
            Some(75_000.0),
            "lifetime traffic is only a tie-break for equally fresh paths"
        );
    }

    #[test]
    fn network_path_cache_rejects_stale_generations_and_clears_on_remove() {
        let cache = SharedNetworkPathCache::default();
        cache.register_peer("peer", 7);
        cache.update(PeerNetworkPathSnapshot {
            peer_id: "peer".to_string(),
            generation: 6,
            candidate_pair_id: "stale".to_string(),
            ..Default::default()
        });
        assert!(cache.snapshots().is_empty());

        cache.update(PeerNetworkPathSnapshot {
            peer_id: "peer".to_string(),
            generation: 7,
            candidate_pair_id: "generation-7".to_string(),
            ..Default::default()
        });
        assert_eq!(cache.snapshots()[0].candidate_pair_id, "generation-7");

        cache.register_peer("peer", 8);
        assert!(
            cache.snapshots().is_empty(),
            "a replacement peer must not expose the old candidate path"
        );
        cache.update(PeerNetworkPathSnapshot {
            peer_id: "peer".to_string(),
            generation: 7,
            candidate_pair_id: "late-generation-7".to_string(),
            ..Default::default()
        });
        assert!(cache.snapshots().is_empty());

        cache.update(PeerNetworkPathSnapshot {
            peer_id: "peer".to_string(),
            generation: 8,
            candidate_pair_id: "generation-8".to_string(),
            ..Default::default()
        });
        cache.remove_peer("peer", Some(7));
        assert_eq!(cache.snapshots()[0].candidate_pair_id, "generation-8");
        cache.remove_peer("peer", Some(8));
        assert!(cache.snapshots().is_empty());
    }

    #[test]
    fn shared_encoder_policy_uses_only_fresh_current_generation_feedback() {
        let policy = SharedEncoderPolicy::default();
        let start = Instant::now();
        policy.register_peer("peer", 7);
        policy.update_peer(
            "peer",
            6,
            EncoderFeedback {
                fraction_lost: 0.30,
            },
            100,
            1,
            start,
        );
        policy.evaluate(start);
        assert_eq!(
            policy.snapshot(),
            EncoderPolicySnapshot::default(),
            "stale generation feedback must be ignored"
        );

        policy.update_peer(
            "peer",
            7,
            EncoderFeedback {
                fraction_lost: 0.30,
            },
            100,
            1,
            start,
        );
        policy.evaluate(start);
        let degraded = policy.snapshot();
        assert_eq!(degraded.packet_loss_percent, 30);
        assert_eq!(degraded.bitrate, 36_000);

        // Once RTCP stops advancing, five stale windows recover to the safe baseline.
        for window in 0..5 {
            policy.evaluate(
                start + ENCODER_FEEDBACK_FRESHNESS + Duration::from_secs(1 + window as u64 * 2),
            );
        }
        let recovered = policy.snapshot();
        assert_eq!(
            recovered.packet_loss_percent,
            crate::codec::DEFAULT_ENCODER_PACKET_LOSS_PERCENT
        );
        assert_eq!(recovered.bitrate, crate::codec::DEFAULT_ENCODER_BITRATE);
    }

    #[test]
    fn loopback_two_engines_exchange_opus() {
        let (a_tx, a_rx) = channel::<LocalSignal>();
        let (b_tx, b_rx) = channel::<LocalSignal>();
        let a = RtcEngine::new(a_tx);
        let b = RtcEngine::new(b_tx);

        a.add_peer("B".to_string(), true, false, 41, 0);

        b.add_peer("A".to_string(), false, false, 42, 0);

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
            a.send_opus(&payload, 0);
            while let Some(packet) = b.recv() {
                count += 1;
                if received.is_none() {
                    received = Some(packet.payload);
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
        let stats_deadline = Instant::now() + ENCODER_STATS_INTERVAL * 2;
        let path = loop {
            if let Some(path) = a
                .network_path_snapshots()
                .into_iter()
                .find(|path| path.peer_id == "B" && !path.candidate_pair_id.is_empty())
            {
                break path;
            }
            assert!(
                Instant::now() < stats_deadline,
                "connected peer did not publish a cached network stats row"
            );
            std::thread::sleep(Duration::from_millis(25));
        };
        assert!(
            !path.candidate_pair_id.is_empty(),
            "selected ICE candidate pair was not exposed"
        );
        assert!(!path.local_candidate_type.is_empty());
        assert!(!path.remote_candidate_type.is_empty());
        assert!(path.current_rtt_ms >= 0.0);
    }

    /// Opt-in release lab: obtains short-lived credentials outside the test process, then proves
    /// that two relay-only engines exchange media over the exact TURN-over-UDP path used in game.
    /// Keeping the credential JSON in an environment variable avoids committing or printing it.
    #[test]
    #[ignore = "requires PERFECTCOMMS_LIVE_TURN_JSON and internet access"]
    fn live_turn_udp_two_engines_exchange_opus_bidirectionally() {
        let json = std::env::var("PERFECTCOMMS_LIVE_TURN_JSON")
            .expect("set PERFECTCOMMS_LIVE_TURN_JSON to the credential response");
        let response: LiveTurnResponse =
            serde_json::from_str(&json).expect("credential response must contain iceServers");
        assert!(
            response
                .ice_servers
                .iter()
                .flat_map(|server| server.urls.iter())
                .any(|url| is_supported_udp_turn_url(url)),
            "credential response contained no supported TURN-over-UDP URL"
        );

        let (a_tx, a_rx) = channel::<LocalSignal>();
        let (b_tx, b_rx) = channel::<LocalSignal>();
        let a = RtcEngine::new(a_tx);
        let b = RtcEngine::new(b_tx);
        a.set_ice_servers(&response.ice_servers);
        b.set_ice_servers(&response.ice_servers);
        a.add_peer("B".to_string(), true, true, 71, 0);
        b.add_peer("A".to_string(), false, true, 72, 0);

        let payload_a: Vec<u8> = (0..96u8).map(|value| value ^ 0xa5).collect();
        let payload_b: Vec<u8> = (0..96u8).map(|value| value ^ 0x5a).collect();
        let deadline = Instant::now() + Duration::from_secs(45);
        let mut received_at_a = false;
        let mut received_at_b = false;
        let mut connected_a = false;
        let mut connected_b = false;

        while Instant::now() < deadline {
            while let Ok(signal) = a_rx.try_recv() {
                match signal {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => {
                        b.set_remote_sdp("A", &sdp_type, &sdp)
                    }
                    LocalSignal::Candidate { candidate, .. } => {
                        b.add_ice_candidate("A", &candidate)
                    }
                    LocalSignal::PeerState { state, .. } => connected_a |= state == "connected",
                }
            }
            while let Ok(signal) = b_rx.try_recv() {
                match signal {
                    LocalSignal::Sdp { sdp_type, sdp, .. } => {
                        a.set_remote_sdp("B", &sdp_type, &sdp)
                    }
                    LocalSignal::Candidate { candidate, .. } => {
                        a.add_ice_candidate("B", &candidate)
                    }
                    LocalSignal::PeerState { state, .. } => connected_b |= state == "connected",
                }
            }

            a.send_opus(&payload_a, 0);
            b.send_opus(&payload_b, 0);
            while let Some(packet) = a.recv() {
                received_at_a |= packet.payload == payload_b;
            }
            while let Some(packet) = b.recv() {
                received_at_b |= packet.payload == payload_a;
            }

            let relay_a = a
                .network_path_snapshots()
                .iter()
                .any(|path| path.peer_id == "B" && path.relay);
            let relay_b = b
                .network_path_snapshots()
                .iter()
                .any(|path| path.peer_id == "A" && path.relay);
            if connected_a && connected_b && received_at_a && received_at_b && relay_a && relay_b {
                a.remove_peer("B");
                b.remove_peer("A");
                return;
            }
            std::thread::sleep(Duration::from_millis(20));
        }

        panic!(
            "TURN/UDP relay did not complete bidirectional media: connected_a={connected_a} connected_b={connected_b} received_at_a={received_at_a} received_at_b={received_at_b}"
        );
    }
}
