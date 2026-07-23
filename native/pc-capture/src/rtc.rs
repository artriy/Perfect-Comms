use parking_lot::Mutex;
use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use crate::codec::{
    EncoderFeedback, EncoderNetworkController, EncoderPolicySnapshot, MediaReceiveCounters,
};

const RTP_INGRESS_QUEUE_CAPACITY: usize = 2048;
const RTP_INGRESS_PER_PEER_CAPACITY: usize = 128;
const RTP_EGRESS_PRIVACY_DRAIN_TIMEOUT: Duration = Duration::from_millis(500);
const ENCODER_STATS_INTERVAL: Duration = Duration::from_secs(2);
const ENCODER_FEEDBACK_FRESHNESS: Duration = Duration::from_secs(6);
const ENCODER_OUTLIER_QUALIFYING_MEASUREMENTS: u8 = 3;
const ENCODER_OUTLIER_LOSS_THRESHOLD: f64 = 0.07;
const ENCODER_OUTLIER_BANDWIDTH_THRESHOLD: i32 = 64_000;

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

/// Bounded round-robin RTP ingress. A burst from one peer can consume at most 128 slots and its
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
        let mut state = self.state.lock();
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
        let mut state = self.state.lock();
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
        let mut state = self.state.lock();
        if let Some(queue) = state.queues.remove(peer_id) {
            state.queued = state.queued.saturating_sub(queue.len());
        }
        state.ready.retain(|peer| peer != peer_id);
        let total_depth = state.queued;
        drop(state);
        self.counters.record_ingress_queue_depth(total_depth, 0);
    }
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
    pub ice_connection_state: String,
    pub local_candidate_protocol: String,
    pub remote_candidate_protocol: String,
    pub selected_pair_changes: u64,
    pub current_rtt_ms: f64,
    /// True only when Pion's congestion-control estimator produced a real send estimate.
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
        let mut state = self.state.lock();
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
        let mut state = self.state.lock();
        if state
            .generations
            .get(&snapshot.peer_id)
            .is_some_and(|generation| *generation == snapshot.generation)
        {
            state.snapshots.insert(snapshot.peer_id.clone(), snapshot);
        }
    }

    fn update_selected_path(&self, path: PeerNetworkPathSnapshot) {
        let mut state = self.state.lock();
        if !state
            .generations
            .get(&path.peer_id)
            .is_some_and(|generation| *generation == path.generation)
        {
            return;
        }
        let snapshot = state
            .snapshots
            .entry(path.peer_id.clone())
            .or_insert_with(|| PeerNetworkPathSnapshot {
                peer_id: path.peer_id.clone(),
                generation: path.generation,
                ..PeerNetworkPathSnapshot::default()
            });
        let pair_changed = snapshot.candidate_pair_id != path.candidate_pair_id;
        snapshot.candidate_pair_id = path.candidate_pair_id;
        snapshot.candidate_state = path.candidate_state;
        snapshot.local_candidate_type = path.local_candidate_type;
        snapshot.remote_candidate_type = path.remote_candidate_type;
        snapshot.local_candidate_protocol = path.local_candidate_protocol;
        snapshot.remote_candidate_protocol = path.remote_candidate_protocol;
        snapshot.relay = path.relay;
        snapshot.ice_connection_state = path.ice_connection_state;
        snapshot.selected_pair_changes = path.selected_pair_changes;
        if pair_changed {
            snapshot.current_rtt_ms = 0.0;
            snapshot.bandwidth_estimate_valid = false;
            snapshot.available_outgoing_bitrate = 0.0;
            snapshot.available_incoming_bitrate = 0.0;
        }
    }

    fn update_ice_state(&self, peer_id: &str, generation: u32, ice_state: String) {
        let mut state = self.state.lock();
        if !state
            .generations
            .get(peer_id)
            .is_some_and(|current| *current == generation)
        {
            return;
        }
        let snapshot = state
            .snapshots
            .entry(peer_id.to_string())
            .or_insert_with(|| PeerNetworkPathSnapshot {
                peer_id: peer_id.to_string(),
                generation,
                ..PeerNetworkPathSnapshot::default()
            });
        snapshot.ice_connection_state = ice_state;
    }

    fn update_bandwidth(&self, peer_id: &str, generation: u32, estimate: f64, pair_changes: u64) {
        let mut state = self.state.lock();
        if !state
            .generations
            .get(peer_id)
            .is_some_and(|current| *current == generation)
        {
            return;
        }
        let snapshot = state
            .snapshots
            .entry(peer_id.to_string())
            .or_insert_with(|| PeerNetworkPathSnapshot {
                peer_id: peer_id.to_string(),
                generation,
                ..PeerNetworkPathSnapshot::default()
            });
        snapshot.bandwidth_estimate_valid = estimate.is_finite() && estimate > 0.0;
        snapshot.available_outgoing_bitrate = if snapshot.bandwidth_estimate_valid {
            estimate
        } else {
            0.0
        };
        snapshot.selected_pair_changes = pair_changes;
    }

    fn remove_peer(&self, peer_id: &str, generation: Option<u32>) {
        let mut state = self.state.lock();
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
        let state = self.state.lock();
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
struct EncoderOutlierLease {
    generation: u32,
    path_epoch: u64,
    expires_at: Instant,
}

#[derive(Debug, Clone, Copy)]
struct PeerEncoderFeedback {
    generation: u32,
    path_epoch: u64,
    sample: EncoderFeedback,
    packets_received: u64,
    rtt_measurements: u64,
    bandwidth_estimate: Option<i32>,
    last_loss_fresh: Option<Instant>,
    last_bandwidth_fresh: Option<Instant>,
    outlier_measurements: u8,
    last_outlier_evidence: Option<Instant>,
    outlier_lease: Option<EncoderOutlierLease>,
}

#[derive(Debug, Clone, Copy)]
struct PeerEncoderFeedbackUpdate {
    generation: u32,
    sample: EncoderFeedback,
    bandwidth_estimate: Option<i32>,
    packets_received: u64,
    rtt_measurements: u64,
    now: Instant,
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
        let mut state = self.state.lock();
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
                path_epoch: 0,
                sample: EncoderFeedback::default(),
                packets_received: 0,
                rtt_measurements: 0,
                bandwidth_estimate: None,
                last_loss_fresh: None,
                last_bandwidth_fresh: None,
                outlier_measurements: 0,
                last_outlier_evidence: None,
                outlier_lease: None,
            },
        );
    }

    fn remove_peer(&self, peer_id: &str, generation: Option<u32>) {
        let mut state = self.state.lock();
        if generation.is_none_or(|generation| {
            state
                .peers
                .get(peer_id)
                .is_some_and(|peer| peer.generation == generation)
        }) {
            state.peers.remove(peer_id);
        }
    }

    fn update_peer(&self, peer_id: &str, update: PeerEncoderFeedbackUpdate) {
        let mut state = self.state.lock();
        let Some(peer) = state.peers.get_mut(peer_id) else {
            return;
        };
        if peer.generation != update.generation {
            return;
        }
        let evidence_advanced = update.packets_received > peer.packets_received
            || update.rtt_measurements > peer.rtt_measurements;
        let loss_fresh = evidence_advanced || peer.last_loss_fresh.is_none();
        peer.packets_received = peer.packets_received.max(update.packets_received);
        peer.rtt_measurements = peer.rtt_measurements.max(update.rtt_measurements);
        if loss_fresh {
            peer.sample = update.sample;
            peer.last_loss_fresh = Some(update.now);
        }
        if let Some(estimate) = update.bandwidth_estimate.filter(|estimate| *estimate > 0) {
            peer.bandwidth_estimate = Some(estimate);
            peer.last_bandwidth_fresh = Some(update.now);
        }

        if evidence_advanced {
            let impaired_loss = update.sample.fraction_lost.is_finite()
                && update.sample.fraction_lost >= ENCODER_OUTLIER_LOSS_THRESHOLD;
            let impaired_bandwidth = update.bandwidth_estimate.is_some_and(|estimate| {
                estimate > 0 && estimate < ENCODER_OUTLIER_BANDWIDTH_THRESHOLD
            });
            if impaired_loss || impaired_bandwidth {
                if peer.last_outlier_evidence.is_none_or(|previous| {
                    update.now.saturating_duration_since(previous) > ENCODER_FEEDBACK_FRESHNESS
                }) {
                    peer.outlier_measurements = 0;
                }
                peer.outlier_measurements = peer.outlier_measurements.saturating_add(1);
                peer.last_outlier_evidence = Some(update.now);
                if peer.outlier_measurements >= ENCODER_OUTLIER_QUALIFYING_MEASUREMENTS {
                    peer.outlier_lease = Some(EncoderOutlierLease {
                        generation: peer.generation,
                        path_epoch: peer.path_epoch,
                        expires_at: update
                            .now
                            .checked_add(ENCODER_FEEDBACK_FRESHNESS)
                            .unwrap_or(update.now),
                    });
                }
            } else {
                peer.outlier_measurements = 0;
                peer.last_outlier_evidence = None;
                peer.outlier_lease = None;
            }
        }
    }

    fn update_peer_bandwidth(&self, peer_id: &str, generation: u32, estimate: i32, now: Instant) {
        let mut state = self.state.lock();
        if let Some(peer) = state
            .peers
            .get_mut(peer_id)
            .filter(|peer| peer.generation == generation && estimate > 0)
        {
            peer.bandwidth_estimate = Some(estimate);
            peer.last_bandwidth_fresh = Some(now);
        }
    }

    fn invalidate_peer_path(&self, peer_id: &str, generation: u32) {
        let mut state = self.state.lock();
        if let Some(peer) = state
            .peers
            .get_mut(peer_id)
            .filter(|peer| peer.generation == generation)
        {
            peer.path_epoch = peer.path_epoch.saturating_add(1);
            peer.packets_received = 0;
            peer.rtt_measurements = 0;
            peer.last_loss_fresh = None;
            peer.last_bandwidth_fresh = None;
            peer.bandwidth_estimate = None;
            peer.outlier_measurements = 0;
            peer.last_outlier_evidence = None;
            peer.outlier_lease = None;
        }
    }

    fn evaluate(&self, now: Instant) {
        let mut state = self.state.lock();
        let capacity = state.peers.len().saturating_mul(2);
        let mut feedback = Vec::with_capacity(capacity);
        let mut bandwidth_estimates = Vec::with_capacity(capacity);
        for peer in state.peers.values_mut() {
            let evidence_expired = peer.last_outlier_evidence.is_some_and(|fresh| {
                now.saturating_duration_since(fresh) > ENCODER_FEEDBACK_FRESHNESS
            });
            let qualified = peer.outlier_lease.is_some_and(|lease| {
                lease.generation == peer.generation
                    && lease.path_epoch == peer.path_epoch
                    && now <= lease.expires_at
            });
            if evidence_expired || (peer.outlier_lease.is_some() && !qualified) {
                peer.outlier_measurements = 0;
                peer.last_outlier_evidence = None;
                peer.outlier_lease = None;
            }

            if peer.last_loss_fresh.is_some_and(|fresh| {
                now.saturating_duration_since(fresh) <= ENCODER_FEEDBACK_FRESHNESS
            }) {
                feedback.push(peer.sample);
                if qualified {
                    // The controller deliberately discards one worst route. Repeating only the
                    // qualified identity makes its persistent sample the second-worst input while
                    // retaining the controller's existing degradation/recovery hysteresis.
                    feedback.push(peer.sample);
                }
            }
            if peer.last_bandwidth_fresh.is_some_and(|fresh| {
                now.saturating_duration_since(fresh) <= ENCODER_FEEDBACK_FRESHNESS
            }) {
                if let Some(estimate) = peer.bandwidth_estimate {
                    bandwidth_estimates.push(estimate);
                    if qualified {
                        bandwidth_estimates.push(estimate);
                    }
                }
            }
        }
        let snapshot = state
            .controller
            .observe_with_bandwidth(&feedback, &bandwidth_estimates);
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

#[derive(Default)]
pub struct NativeCounters {
    pub capture_frames: AtomicU64,
    pub opus_encoded: AtomicU64,
    pub opus_empty: AtomicU64,
    pub opus_errors: AtomicU64,
    pub capture_media_gap_frames: AtomicU64,
    pub opus_gap_placeholders: AtomicU64,
    pub opus_discontinuity_resets: AtomicU64,
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
            capture_media_gap_frames: self.capture_media_gap_frames.load(Ordering::Relaxed),
            opus_gap_placeholders: self.opus_gap_placeholders.load(Ordering::Relaxed),
            opus_discontinuity_resets: self.opus_discontinuity_resets.load(Ordering::Relaxed),
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

#[path = "rtc_pion.rs"]
mod pion_impl;
pub use pion_impl::{set_transport_library_path, RtcEngine};

#[cfg(test)]
mod tests {
    use super::*;

    fn register_room(policy: &SharedEncoderPolicy) {
        for peer in ["a", "b", "c"] {
            policy.register_peer(peer, 1);
        }
    }

    fn report(
        policy: &SharedEncoderPolicy,
        peer: &str,
        measurement: u64,
        loss: f64,
        bandwidth: i32,
        now: Instant,
    ) {
        policy.update_peer(
            peer,
            PeerEncoderFeedbackUpdate {
                generation: 1,
                sample: EncoderFeedback {
                    fraction_lost: loss,
                },
                bandwidth_estimate: Some(bandwidth),
                packets_received: measurement,
                rtt_measurements: measurement,
                now,
            },
        );
    }

    fn report_window(
        policy: &SharedEncoderPolicy,
        measurement: u64,
        losses: [f64; 3],
        bandwidths: [i32; 3],
        now: Instant,
    ) {
        for ((peer, loss), bandwidth) in ["a", "b", "c"].into_iter().zip(losses).zip(bandwidths) {
            report(policy, peer, measurement, loss, bandwidth, now);
        }
        policy.evaluate(now);
    }

    #[test]
    fn transient_and_alternating_room_outliers_do_not_degrade_encoder_policy() {
        let policy = SharedEncoderPolicy::default();
        register_room(&policy);
        let start = Instant::now();

        report_window(
            &policy,
            1,
            [0.30, 0.0, 0.0],
            [32_000, 96_000, 96_000],
            start,
        );
        assert_eq!(policy.snapshot(), EncoderPolicySnapshot::default());
        report(
            &policy,
            "a",
            1,
            0.30,
            32_000,
            start + Duration::from_millis(250),
        );
        policy.evaluate(start + Duration::from_millis(250));
        assert!(policy.state.lock().peers.get("a").is_some_and(|peer| {
            peer.outlier_measurements == 1 && peer.outlier_lease.is_none()
        }));

        report_window(
            &policy,
            2,
            [0.0, 0.30, 0.0],
            [96_000, 32_000, 96_000],
            start + Duration::from_secs(1),
        );
        report_window(
            &policy,
            3,
            [0.30, 0.0, 0.0],
            [32_000, 96_000, 96_000],
            start + Duration::from_secs(2),
        );
        assert_eq!(policy.snapshot(), EncoderPolicySnapshot::default());
        let state = policy.state.lock();
        assert!(state
            .peers
            .values()
            .all(|peer| peer.outlier_lease.is_none() && peer.outlier_measurements <= 1));
    }

    #[test]
    fn persistent_peer_outlier_uses_controller_degradation_and_recovery_hysteresis() {
        let policy = SharedEncoderPolicy::default();
        register_room(&policy);
        let start = Instant::now();

        for measurement in 1..=2 {
            report_window(
                &policy,
                measurement,
                [0.30, 0.0, 0.0],
                [32_000, 96_000, 96_000],
                start + Duration::from_secs(measurement - 1),
            );
            assert_eq!(policy.snapshot(), EncoderPolicySnapshot::default());
        }
        report_window(
            &policy,
            3,
            [0.30, 0.0, 0.0],
            [32_000, 96_000, 96_000],
            start + Duration::from_secs(2),
        );
        let degraded = policy.snapshot();
        assert_eq!(degraded.packet_loss_percent, 30);
        assert_eq!(degraded.bitrate, 24_000);
        assert!(policy
            .state
            .lock()
            .peers
            .get("a")
            .is_some_and(|peer| peer.outlier_lease.is_some()));

        for recovery_window in 0..4 {
            let measurement = 4 + recovery_window;
            report_window(
                &policy,
                measurement,
                [0.0, 0.0, 0.0],
                [96_000, 96_000, 96_000],
                start + Duration::from_secs(measurement),
            );
            assert_eq!(policy.snapshot(), degraded);
        }
        report_window(
            &policy,
            8,
            [0.0, 0.0, 0.0],
            [96_000, 96_000, 96_000],
            start + Duration::from_secs(8),
        );
        let recovered = policy.snapshot();
        assert_eq!(recovered.packet_loss_percent, 5);
        assert_eq!(recovered.bitrate, 48_000);
    }

    #[test]
    fn stale_and_path_changed_outlier_evidence_must_requalify() {
        let start = Instant::now();
        let stale = SharedEncoderPolicy::default();
        register_room(&stale);
        for measurement in 1..=2 {
            report_window(
                &stale,
                measurement,
                [0.30, 0.0, 0.0],
                [32_000, 96_000, 96_000],
                start + Duration::from_secs(measurement - 1),
            );
        }
        stale.evaluate(start + Duration::from_secs(8));
        assert!(stale.state.lock().peers.get("a").is_some_and(|peer| {
            peer.outlier_measurements == 0
                && peer.last_outlier_evidence.is_none()
                && peer.outlier_lease.is_none()
        }));
        report_window(
            &stale,
            3,
            [0.30, 0.0, 0.0],
            [32_000, 96_000, 96_000],
            start + Duration::from_secs(8),
        );
        assert_eq!(stale.snapshot(), EncoderPolicySnapshot::default());

        report_window(
            &stale,
            4,
            [0.30, 0.0, 0.0],
            [32_000, 96_000, 96_000],
            start + Duration::from_secs(9),
        );
        report_window(
            &stale,
            5,
            [0.30, 0.0, 0.0],
            [32_000, 96_000, 96_000],
            start + Duration::from_secs(10),
        );
        assert!(stale
            .state
            .lock()
            .peers
            .get("a")
            .is_some_and(|peer| peer.outlier_lease.is_some()));
        stale.evaluate(start + Duration::from_secs(17));
        assert!(stale
            .state
            .lock()
            .peers
            .get("a")
            .is_some_and(|peer| peer.outlier_lease.is_none()));

        let changed_path = SharedEncoderPolicy::default();
        register_room(&changed_path);
        for measurement in 1..=2 {
            report_window(
                &changed_path,
                measurement,
                [0.30, 0.0, 0.0],
                [32_000, 96_000, 96_000],
                start + Duration::from_secs(measurement - 1),
            );
        }
        changed_path.invalidate_peer_path("a", 1);
        report_window(
            &changed_path,
            3,
            [0.30, 0.0, 0.0],
            [32_000, 96_000, 96_000],
            start + Duration::from_secs(2),
        );
        assert_eq!(changed_path.snapshot(), EncoderPolicySnapshot::default());
        assert!(changed_path
            .state
            .lock()
            .peers
            .get("a")
            .is_some_and(|peer| {
                peer.path_epoch == 1
                    && peer.outlier_measurements == 1
                    && peer.outlier_lease.is_none()
            }));
    }

    #[test]
    fn generation_replacement_and_removal_discard_outlier_identity_state() {
        let policy = SharedEncoderPolicy::default();
        policy.register_peer("peer", 1);
        let start = Instant::now();
        for measurement in 1..=ENCODER_OUTLIER_QUALIFYING_MEASUREMENTS {
            report(
                &policy,
                "peer",
                u64::from(measurement),
                0.30,
                32_000,
                start + Duration::from_secs(u64::from(measurement)),
            );
        }
        assert!(policy
            .state
            .lock()
            .peers
            .get("peer")
            .is_some_and(|peer| peer.outlier_lease.is_some()));

        policy.register_peer("peer", 2);
        assert!(policy.state.lock().peers.get("peer").is_some_and(|peer| {
            peer.generation == 2 && peer.outlier_measurements == 0 && peer.outlier_lease.is_none()
        }));
        policy.remove_peer("peer", Some(1));
        assert!(policy.state.lock().peers.contains_key("peer"));
        policy.remove_peer("peer", Some(2));
        assert!(!policy.state.lock().peers.contains_key("peer"));
    }
}
