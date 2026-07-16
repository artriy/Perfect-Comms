use std::collections::{BTreeMap, HashMap};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::opus_native::{
    DredDecodeOutcome, DredParseOutcome, OpusDecoder, OpusEncoder, OpusError,
};

pub use crate::opus_native::FRAME_SIZE;
pub const SAMPLE_RATE: i32 = crate::opus_native::SAMPLE_RATE;

/// RTP is clocked at the Opus sample rate. PerfectComms sends one 20 ms packet (960 ticks).
pub const RTP_CLOCK_RATE: u32 = 48_000;
pub const RTP_FRAME_TICKS: u32 = 960;

/// Encoded packet retention and playout latency are deliberately separate controls. The store is
/// large enough to absorb a severe burst/reorder event without allowing the normal latency target
/// to grow beyond 300 ms.
pub const ENCODED_PACKET_CAPACITY: usize = 32;
pub const MIN_PLAYOUT_FRAMES: usize = 2;
pub const MAX_PLAYOUT_FRAMES: usize = 15;

const TALKSPURT_RESET: Duration = Duration::from_millis(300);
const MIN_REORDER_DEADLINE: Duration = Duration::from_millis(20);
const MAX_REORDER_DEADLINE: Duration = Duration::from_millis(120);
const MAX_SEQUENCE_DISCONTINUITY: u64 = 128;
const MAX_TIMESTAMP_DISCONTINUITY: i64 = RTP_CLOCK_RATE as i64 * 2;
const TARGET_DECAY_STABLE_PACKETS: usize = 250;

pub const DEFAULT_ENCODER_PACKET_LOSS_PERCENT: u8 = 15;
pub const DEFAULT_ENCODER_BITRATE: i32 = 48_000;
const ENCODER_POLICY_RECOVERY_WINDOWS: usize = 5;

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct EncoderFeedback {
    pub fraction_lost: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EncoderPolicySnapshot {
    pub packet_loss_percent: u8,
    pub bitrate: i32,
    pub generation: u64,
}

impl Default for EncoderPolicySnapshot {
    fn default() -> Self {
        Self {
            packet_loss_percent: DEFAULT_ENCODER_PACKET_LOSS_PERCENT,
            bitrate: DEFAULT_ENCODER_BITRATE,
            generation: 0,
        }
    }
}

/// Hysteretic policy for PerfectComms' single fan-out encoder. Degradation is immediate; quality
/// recovery requires five consecutive two-second windows so one optimistic RTCP report cannot
/// oscillate bitrate/FEC configuration. RTT and candidate-pair bandwidth are deliberately absent
/// from this API: webrtc-rs 0.11 exposes the latter as an unpopulated zero-valued stats field.
pub struct EncoderNetworkController {
    current: EncoderPolicySnapshot,
    recovery_candidate: Option<(u8, i32)>,
    recovery_windows: usize,
}

impl Default for EncoderNetworkController {
    fn default() -> Self {
        Self::new()
    }
}

impl EncoderNetworkController {
    pub fn new() -> Self {
        Self {
            current: EncoderPolicySnapshot::default(),
            recovery_candidate: None,
            recovery_windows: 0,
        }
    }

    fn desired(feedback: &[EncoderFeedback]) -> (u8, i32) {
        let mut losses: Vec<f64> = feedback
            .iter()
            .map(|sample| sample.fraction_lost)
            .filter(|value| value.is_finite())
            .map(|value| value.clamp(0.0, 1.0))
            .collect();
        losses.sort_unstable_by(f64::total_cmp);
        // One shared Opus frame must serve every recipient. The upper quartile protects the
        // typical lossy route without allowing one isolated, severely broken peer to force lower
        // fidelity on an otherwise healthy lobby. For one to three recipients nearest-rank P75
        // is still the worst sample, which is the conservative behavior wanted for small calls.
        let representative_loss = losses
            .get((losses.len().saturating_mul(3).saturating_sub(1)) / 4)
            .copied()
            .unwrap_or(0.0);
        let packet_loss_percent = if representative_loss < 0.01 {
            5
        } else if representative_loss < 0.03 {
            10
        } else if representative_loss < 0.07 {
            15
        } else if representative_loss < 0.12 {
            20
        } else if representative_loss < 0.20 {
            25
        } else {
            30
        };

        let loss_limited = if representative_loss >= 0.20 {
            36_000
        } else if representative_loss >= 0.12 {
            40_000
        } else if representative_loss >= 0.07 {
            44_000
        } else {
            DEFAULT_ENCODER_BITRATE
        };
        (packet_loss_percent, loss_limited)
    }

    pub fn observe(&mut self, fresh_feedback: &[EncoderFeedback]) -> EncoderPolicySnapshot {
        // With no fresh RTCP, recover slowly to the safe shipped baseline rather than retaining a
        // transient bad-route clamp forever or optimistically dropping FEC to 5%.
        let desired = if fresh_feedback.is_empty() {
            (DEFAULT_ENCODER_PACKET_LOSS_PERCENT, DEFAULT_ENCODER_BITRATE)
        } else {
            Self::desired(fresh_feedback)
        };
        let worsened =
            desired.0 > self.current.packet_loss_percent || desired.1 < self.current.bitrate;
        if worsened {
            let next_loss = self.current.packet_loss_percent.max(desired.0);
            let next_bitrate = self.current.bitrate.min(desired.1);
            if next_loss != self.current.packet_loss_percent || next_bitrate != self.current.bitrate
            {
                self.current.packet_loss_percent = next_loss;
                self.current.bitrate = next_bitrate;
                self.current.generation = self.current.generation.saturating_add(1);
            }
            self.recovery_candidate = None;
            self.recovery_windows = 0;
            return self.current;
        }

        if desired.0 == self.current.packet_loss_percent && desired.1 == self.current.bitrate {
            self.recovery_candidate = None;
            self.recovery_windows = 0;
            return self.current;
        }

        if self.recovery_candidate == Some(desired) {
            self.recovery_windows += 1;
        } else {
            self.recovery_candidate = Some(desired);
            self.recovery_windows = 1;
        }
        if self.recovery_windows >= ENCODER_POLICY_RECOVERY_WINDOWS {
            self.current.packet_loss_percent = desired.0;
            self.current.bitrate = desired.1;
            self.current.generation = self.current.generation.saturating_add(1);
            self.recovery_candidate = None;
            self.recovery_windows = 0;
        }
        self.current
    }

    pub fn snapshot(&self) -> EncoderPolicySnapshot {
        self.current
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct MediaReceiveSnapshot {
    pub active_peers: u64,
    pub ingress_queue_overflow: u64,
    pub ingress_queue_depth_current: u64,
    pub ingress_queue_depth_max: u64,
    pub ingress_peer_queue_depth_max: u64,
    pub sequence_gaps: u64,
    pub reordered_recovered: u64,
    pub late_drops: u64,
    pub duplicate_drops: u64,
    pub encoded_overflow_drops: u64,
    pub deadline_losses: u64,
    pub dred_frames: u64,
    pub fec_frames: u64,
    pub plc_frames: u64,
    pub decoder_resets: u64,
    pub talkspurt_resets: u64,
    pub underruns: u64,
    pub rebuffers: u64,
    pub target_frames_max: u64,
    pub target_frames_current_max: u64,
    pub depth_frames_max: u64,
    pub depth_frames_current: u64,
    pub rtp_jitter_ms_max: f64,
}

#[derive(Debug, Clone, Copy, Default)]
struct PeerMediaGauge {
    target_frames: usize,
    depth_frames: usize,
    jitter_ms: f64,
}

/// Process-wide receive counters. Event counters are atomics; only low-frequency per-peer gauges
/// take a lock. The audio callback never touches this object.
#[derive(Default)]
pub struct MediaReceiveCounters {
    ingress_queue_overflow: AtomicU64,
    ingress_queue_depth_current: AtomicU64,
    ingress_queue_depth_max: AtomicU64,
    ingress_peer_queue_depth_max: AtomicU64,
    sequence_gaps: AtomicU64,
    reordered_recovered: AtomicU64,
    late_drops: AtomicU64,
    duplicate_drops: AtomicU64,
    encoded_overflow_drops: AtomicU64,
    deadline_losses: AtomicU64,
    dred_frames: AtomicU64,
    fec_frames: AtomicU64,
    plc_frames: AtomicU64,
    decoder_resets: AtomicU64,
    talkspurt_resets: AtomicU64,
    underruns: AtomicU64,
    rebuffers: AtomicU64,
    target_frames_max: AtomicU64,
    depth_frames_max: AtomicU64,
    peers: Mutex<HashMap<String, PeerMediaGauge>>,
}

impl MediaReceiveCounters {
    fn observe_max(target: &AtomicU64, value: u64) {
        let mut current = target.load(Ordering::Relaxed);
        while value > current {
            match target.compare_exchange_weak(current, value, Ordering::Relaxed, Ordering::Relaxed)
            {
                Ok(_) => break,
                Err(actual) => current = actual,
            }
        }
    }

    pub fn record_ingress_queue_overflow(&self) {
        self.ingress_queue_overflow.fetch_add(1, Ordering::Relaxed);
    }

    pub fn record_ingress_queue_depth(&self, total: usize, per_peer: usize) {
        self.ingress_queue_depth_current
            .store(total as u64, Ordering::Relaxed);
        Self::observe_max(&self.ingress_queue_depth_max, total as u64);
        Self::observe_max(&self.ingress_peer_queue_depth_max, per_peer as u64);
    }

    fn update_peer(&self, peer: &str, target: usize, depth: usize, jitter_ms: f64) {
        Self::observe_max(&self.target_frames_max, target as u64);
        Self::observe_max(&self.depth_frames_max, depth as u64);
        let gauge = PeerMediaGauge {
            target_frames: target,
            depth_frames: depth,
            jitter_ms,
        };
        let mut peers = self.peers.lock().unwrap();
        if let Some(existing) = peers.get_mut(peer) {
            *existing = gauge;
        } else {
            peers.insert(peer.to_string(), gauge);
        }
    }

    pub fn remove_peer(&self, peer: &str) {
        self.peers.lock().unwrap().remove(peer);
    }

    pub fn snapshot(&self) -> MediaReceiveSnapshot {
        let peers = self.peers.lock().unwrap();
        MediaReceiveSnapshot {
            active_peers: peers.len() as u64,
            ingress_queue_overflow: self.ingress_queue_overflow.load(Ordering::Relaxed),
            ingress_queue_depth_current: self.ingress_queue_depth_current.load(Ordering::Relaxed),
            ingress_queue_depth_max: self.ingress_queue_depth_max.load(Ordering::Relaxed),
            ingress_peer_queue_depth_max: self.ingress_peer_queue_depth_max.load(Ordering::Relaxed),
            sequence_gaps: self.sequence_gaps.load(Ordering::Relaxed),
            reordered_recovered: self.reordered_recovered.load(Ordering::Relaxed),
            late_drops: self.late_drops.load(Ordering::Relaxed),
            duplicate_drops: self.duplicate_drops.load(Ordering::Relaxed),
            encoded_overflow_drops: self.encoded_overflow_drops.load(Ordering::Relaxed),
            deadline_losses: self.deadline_losses.load(Ordering::Relaxed),
            dred_frames: self.dred_frames.load(Ordering::Relaxed),
            fec_frames: self.fec_frames.load(Ordering::Relaxed),
            plc_frames: self.plc_frames.load(Ordering::Relaxed),
            decoder_resets: self.decoder_resets.load(Ordering::Relaxed),
            talkspurt_resets: self.talkspurt_resets.load(Ordering::Relaxed),
            underruns: self.underruns.load(Ordering::Relaxed),
            rebuffers: self.rebuffers.load(Ordering::Relaxed),
            target_frames_max: self.target_frames_max.load(Ordering::Relaxed),
            target_frames_current_max: peers
                .values()
                .map(|peer| peer.target_frames as u64)
                .max()
                .unwrap_or(0),
            depth_frames_max: self.depth_frames_max.load(Ordering::Relaxed),
            depth_frames_current: peers.values().map(|peer| peer.depth_frames as u64).sum(),
            rtp_jitter_ms_max: peers
                .values()
                .map(|peer| peer.jitter_ms)
                .fold(0.0, f64::max),
        }
    }
}

#[derive(Debug, Clone)]
pub struct EncodedRtpPacket {
    pub sequence: u16,
    pub timestamp: u32,
    pub arrival: Instant,
    pub payload: Vec<u8>,
}

#[derive(Debug, Clone)]
pub struct ReadyEncodedPacket {
    pub sequence: u16,
    pub extended_sequence: u64,
    pub timestamp: u32,
    pub arrival: Instant,
    pub payload: Vec<u8>,
    pub reset_decoder: bool,
    pub gap_before: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PacketInsertOutcome {
    Accepted,
    Reordered,
    Late,
    Duplicate,
    Overflow,
}

#[derive(Debug)]
struct StoredPacket {
    sequence: u16,
    timestamp: u32,
    arrival: Instant,
    payload: Vec<u8>,
}

/// Per-peer encoded RTP reorder/deadline buffer. All sequence decisions happen here, before the
/// stateful Opus decoder is touched.
pub struct EncodedPacketBuffer {
    peer: String,
    packets: BTreeMap<u64, StoredPacket>,
    highest_extended: Option<u64>,
    expected_extended: Option<u64>,
    highest_timestamp: Option<u32>,
    last_arrival: Option<Instant>,
    jitter_ms: f64,
    /// Desired path cushion derived from variance/lateness.
    target_frames: usize,
    /// Cushion already acquired without interrupting an active talkspurt.
    playout_target_frames: usize,
    stable_packets: usize,
    primed: bool,
    /// Once a packet has been emitted, `expected_extended` is the authoritative media timeline
    /// even while an underrun is re-priming. Initial priming alone may still move it backward to
    /// absorb packets that arrived out of order before playout began.
    timeline_started: bool,
    missing_since: Option<Instant>,
    reset_on_next_packet: bool,
    metrics: Arc<MediaReceiveCounters>,
}

impl EncodedPacketBuffer {
    pub fn new(peer: impl Into<String>, metrics: Arc<MediaReceiveCounters>) -> Self {
        let peer = peer.into();
        let result = Self {
            peer,
            packets: BTreeMap::new(),
            highest_extended: None,
            expected_extended: None,
            highest_timestamp: None,
            last_arrival: None,
            jitter_ms: 0.0,
            target_frames: MIN_PLAYOUT_FRAMES,
            playout_target_frames: MIN_PLAYOUT_FRAMES,
            stable_packets: 0,
            primed: false,
            timeline_started: false,
            missing_since: None,
            reset_on_next_packet: false,
            metrics,
        };
        result.publish_gauge();
        result
    }

    fn extend_sequence(highest: Option<u64>, sequence: u16) -> u64 {
        let Some(highest) = highest else {
            // Start one cycle above zero so a packet from immediately before the first observed
            // wrap can still be represented and reordered during priming without u64 underflow.
            return 0x1_0000 + u64::from(sequence);
        };
        let cycle = highest & !0xffff;
        let mut candidate = cycle | u64::from(sequence);
        if candidate.saturating_add(0x8000) < highest {
            candidate = candidate.saturating_add(0x1_0000);
        } else if candidate > highest.saturating_add(0x8000) {
            candidate = candidate.saturating_sub(0x1_0000);
        }
        candidate
    }

    fn publish_gauge(&self) {
        self.metrics.update_peer(
            &self.peer,
            self.target_frames,
            self.packets.len(),
            self.jitter_ms,
        );
    }

    fn reset_stream(&mut self, talkspurt: bool) {
        self.packets.clear();
        self.highest_extended = None;
        self.expected_extended = None;
        self.highest_timestamp = None;
        self.last_arrival = None;
        // Sequence/decoder state belongs to a media generation; the learned path variance does
        // not. Keep the adaptive target across normal talkspurts so every new sentence does not
        // rediscover the same high-jitter Wi-Fi route from the unsafe minimum.
        self.stable_packets = 0;
        self.primed = false;
        self.timeline_started = false;
        self.playout_target_frames = self.target_frames;
        self.missing_since = None;
        self.reset_on_next_packet = true;
        self.metrics.decoder_resets.fetch_add(1, Ordering::Relaxed);
        if talkspurt {
            self.metrics
                .talkspurt_resets
                .fetch_add(1, Ordering::Relaxed);
        }
    }

    /// A decoder stall must not turn the retention cap into permanent latency. Once a newer
    /// forward packet arrives to a full store, jump to the newest target-sized tail and restart
    /// decoder chronology there. The retained tail is at most 300 ms and its first sequence is an
    /// authoritative floor, so a severely late packet cannot rewind the catch-up while re-priming.
    fn fast_forward_overflow(&mut self, extended: u64, packet: StoredPacket) {
        self.packets.insert(extended, packet);
        let keep = self
            .target_frames
            .clamp(MIN_PLAYOUT_FRAMES, MAX_PLAYOUT_FRAMES);
        let drop_count = self.packets.len().saturating_sub(keep);
        for _ in 0..drop_count {
            self.packets.pop_first();
        }

        self.expected_extended = self
            .packets
            .first_key_value()
            .map(|(sequence, _)| *sequence);
        self.primed = false;
        self.timeline_started = true;
        self.playout_target_frames = keep;
        self.missing_since = None;
        self.reset_on_next_packet = true;
        self.metrics
            .encoded_overflow_drops
            .fetch_add(drop_count as u64, Ordering::Relaxed);
        self.metrics.decoder_resets.fetch_add(1, Ordering::Relaxed);
        self.publish_gauge();
    }

    fn observe_network(&mut self, timestamp: u32, arrival: Instant, forward: bool) {
        if !forward {
            return;
        }
        if let (Some(previous_arrival), Some(previous_timestamp)) =
            (self.last_arrival, self.highest_timestamp)
        {
            let arrival_ms = arrival
                .saturating_duration_since(previous_arrival)
                .as_secs_f64()
                * 1000.0;
            let rtp_ticks = timestamp.wrapping_sub(previous_timestamp) as i32 as f64;
            if rtp_ticks > 0.0 {
                let transit_delta_ms =
                    (arrival_ms - rtp_ticks * 1000.0 / RTP_CLOCK_RATE as f64).abs();
                self.jitter_ms += (transit_delta_ms - self.jitter_ms) / 16.0;
            }
        }
        self.last_arrival = Some(arrival);
        self.highest_timestamp = Some(timestamp);

        // Four jitter deviations is conservative enough for bursty Wi-Fi. It is based on packet
        // arrival variance, never ICE RTT, so a stable 300 ms path remains at the 40 ms minimum.
        let jitter_frames = ((self.jitter_ms * 4.0) / 20.0).ceil() as usize;
        let desired = MIN_PLAYOUT_FRAMES
            .saturating_add(jitter_frames)
            .clamp(MIN_PLAYOUT_FRAMES, MAX_PLAYOUT_FRAMES);
        if desired > self.target_frames {
            self.target_frames = desired;
            self.stable_packets = 0;
        } else if desired < self.target_frames {
            self.stable_packets += 1;
            if self.stable_packets >= TARGET_DECAY_STABLE_PACKETS {
                self.target_frames -= 1;
                self.stable_packets = 0;
            }
        } else {
            self.stable_packets = 0;
        }
    }

    pub fn insert(&mut self, packet: EncodedRtpPacket) -> PacketInsertOutcome {
        let mut previous_highest = self.highest_extended;
        let mut extended = Self::extend_sequence(previous_highest, packet.sequence);
        if previous_highest.is_none_or(|highest| extended > highest)
            && self.last_arrival.is_some_and(|last| {
                packet.arrival.saturating_duration_since(last) >= TALKSPURT_RESET
            })
        {
            self.reset_stream(true);
            previous_highest = None;
            extended = 0x1_0000 + u64::from(packet.sequence);
        }
        let forward = previous_highest.is_none_or(|highest| extended > highest);

        if self.timeline_started
            && self
                .expected_extended
                .is_some_and(|expected| extended < expected)
        {
            self.metrics.late_drops.fetch_add(1, Ordering::Relaxed);
            return PacketInsertOutcome::Late;
        }
        if self.packets.contains_key(&extended) {
            self.metrics.duplicate_drops.fetch_add(1, Ordering::Relaxed);
            return PacketInsertOutcome::Duplicate;
        }

        if let Some(highest) = previous_highest {
            if extended > highest.saturating_add(MAX_SEQUENCE_DISCONTINUITY) {
                self.reset_stream(false);
                return self.insert(packet);
            }
            if extended > highest + 1 {
                self.metrics
                    .sequence_gaps
                    .fetch_add(extended - highest - 1, Ordering::Relaxed);
            }
            if forward {
                if let Some(previous_timestamp) = self.highest_timestamp {
                    let timestamp_delta = packet.timestamp.wrapping_sub(previous_timestamp) as i32;
                    if i64::from(timestamp_delta).abs() > MAX_TIMESTAMP_DISCONTINUITY {
                        self.reset_stream(false);
                        return self.insert(packet);
                    }
                }
            } else {
                self.metrics
                    .reordered_recovered
                    .fetch_add(1, Ordering::Relaxed);
            }
        }

        self.observe_network(packet.timestamp, packet.arrival, forward);
        if forward {
            self.highest_extended = Some(extended);
        }
        if !self.timeline_started {
            self.expected_extended = Some(
                self.expected_extended
                    .map_or(extended, |expected| expected.min(extended)),
            );
        }

        if self.packets.len() >= ENCODED_PACKET_CAPACITY {
            let latest = self.packets.last_key_value().map(|(key, _)| *key);
            if latest.is_some_and(|latest| extended > latest) {
                self.fast_forward_overflow(
                    extended,
                    StoredPacket {
                        sequence: packet.sequence,
                        timestamp: packet.timestamp,
                        arrival: packet.arrival,
                        payload: packet.payload,
                    },
                );
                return PacketInsertOutcome::Overflow;
            }
            if latest.is_none_or(|latest| extended >= latest) {
                self.metrics
                    .encoded_overflow_drops
                    .fetch_add(1, Ordering::Relaxed);
                self.publish_gauge();
                return PacketInsertOutcome::Overflow;
            }
            if let Some(latest) = latest {
                self.packets.remove(&latest);
            }
            self.metrics
                .encoded_overflow_drops
                .fetch_add(1, Ordering::Relaxed);
        }

        self.packets.insert(
            extended,
            StoredPacket {
                sequence: packet.sequence,
                timestamp: packet.timestamp,
                arrival: packet.arrival,
                payload: packet.payload,
            },
        );
        self.publish_gauge();
        if forward {
            PacketInsertOutcome::Accepted
        } else {
            PacketInsertOutcome::Reordered
        }
    }

    fn reorder_deadline(&self) -> Duration {
        Duration::from_secs_f64(
            ((20.0 + self.jitter_ms * 2.0).clamp(
                MIN_REORDER_DEADLINE.as_secs_f64() * 1000.0,
                MAX_REORDER_DEADLINE.as_secs_f64() * 1000.0,
            )) / 1000.0,
        )
    }

    pub fn pop_ready(&mut self, now: Instant) -> Option<ReadyEncodedPacket> {
        if self.packets.is_empty() {
            if self.primed {
                self.primed = false;
                self.metrics.underruns.fetch_add(1, Ordering::Relaxed);
                self.metrics.rebuffers.fetch_add(1, Ordering::Relaxed);
            }
            self.publish_gauge();
            return None;
        }

        if !self.primed {
            if self.packets.len() < self.target_frames {
                self.publish_gauge();
                return None;
            }
            self.primed = true;
            self.playout_target_frames = self.target_frames;
            // Keep the pre-underrun decoder timeline. Re-priming is a latency operation, not a
            // stream reset: silently aligning expected to the first retained packet would hide a
            // long loss burst and feed discontinuous Opus data into stale decoder state.
            if self.expected_extended.is_none() {
                self.expected_extended = self.packets.first_key_value().map(|(key, _)| *key);
            }
        }

        // Never manufacture a voiced 20 ms hole merely because the estimator requested more
        // latency. During an active talkspurt, acquire at most one extra frame only after a burst
        // has already delivered that cushion. New talkspurts still prime to the full desired
        // target above, so learned high-jitter routes remain protected from their first syllable.
        if self.playout_target_frames < self.target_frames
            && self.packets.len() > self.playout_target_frames
        {
            self.playout_target_frames += 1;
        } else if self.playout_target_frames > self.target_frames {
            self.playout_target_frames = self.target_frames;
        }

        let expected = self.expected_extended?;
        let first = self.packets.first_key_value().map(|(key, _)| *key)?;
        let (selected, gap_before) = if self.packets.contains_key(&expected) {
            self.missing_since = None;
            (expected, 0usize)
        } else if first > expected {
            let first_arrival = self
                .packets
                .first_key_value()
                .map(|(_, packet)| packet.arrival)
                .unwrap_or(now);
            let missing_since = *self.missing_since.get_or_insert(first_arrival);
            if now.saturating_duration_since(missing_since) < self.reorder_deadline() {
                self.publish_gauge();
                return None;
            }
            self.missing_since = None;
            let gap = (first - expected) as usize;
            self.metrics
                .deadline_losses
                .fetch_add(gap as u64, Ordering::Relaxed);
            // A late deadline is an immediate signal to add one frame of safety. Stable arrivals
            // must then persist for five seconds before this latency is removed.
            self.target_frames = (self.target_frames + 1).min(MAX_PLAYOUT_FRAMES);
            self.stable_packets = 0;
            (first, gap)
        } else {
            // This can only occur after an explicit stream reset; align to the oldest retained
            // packet rather than allowing stale expected state to deadlock the decoder.
            self.missing_since = None;
            (first, 0usize)
        };

        let stored = self.packets.remove(&selected)?;
        let next_expected = selected.saturating_add(1);
        self.expected_extended = Some(next_expected);
        self.timeline_started = true;
        // Age a missing packet from the arrival of the first packet that proved the gap, not from
        // the later playout poll. The already-buffered cushion therefore doubles as reorder wait
        // instead of adding another silent 20 ms slot at the decoder boundary.
        self.missing_since = self
            .packets
            .first_key_value()
            .and_then(|(sequence, packet)| (*sequence > next_expected).then_some(packet.arrival));
        let gap_requires_reset = gap_before > MAX_CONCEAL_FRAMES;
        let reset_decoder = std::mem::take(&mut self.reset_on_next_packet) || gap_requires_reset;
        if gap_requires_reset {
            self.metrics.decoder_resets.fetch_add(1, Ordering::Relaxed);
        }
        self.publish_gauge();
        Some(ReadyEncodedPacket {
            sequence: stored.sequence,
            extended_sequence: selected,
            timestamp: stored.timestamp,
            arrival: stored.arrival,
            payload: stored.payload,
            reset_decoder,
            gap_before,
        })
    }

    pub fn record_decode(&self, report: ConcealmentReport) {
        self.metrics
            .dred_frames
            .fetch_add(report.dred_frames as u64, Ordering::Relaxed);
        self.metrics
            .fec_frames
            .fetch_add(report.fec_frames as u64, Ordering::Relaxed);
        self.metrics
            .plc_frames
            .fetch_add(report.plc_frames as u64, Ordering::Relaxed);
    }

    pub fn is_idle(&self) -> bool {
        self.packets.is_empty()
    }

    pub fn target_frames(&self) -> usize {
        self.target_frames
    }

    pub fn depth_frames(&self) -> usize {
        self.packets.len()
    }

    pub fn playout_target_frames(&self) -> usize {
        self.playout_target_frames
    }

    pub fn jitter_ms(&self) -> f64 {
        self.jitter_ms
    }
}

impl Drop for EncodedPacketBuffer {
    fn drop(&mut self) {
        self.metrics.remove_peer(&self.peer);
    }
}

// Cap on how many frames we conceal across a single RTP sequence gap. Beyond this a gap is treated
// as a stream restart (long silence / reconnect), not packet loss, so we don't flood the jitter
// buffer with concealment after a stall.
pub const MAX_CONCEAL_FRAMES: usize = 5;

pub struct OpusCodec {
    encoder: OpusEncoder,
    decoder: OpusDecoder,
}

impl OpusCodec {
    pub fn new() -> Result<Self, OpusError> {
        let encoder = OpusEncoder::new()?;
        let decoder = OpusDecoder::new()?;
        Ok(Self { encoder, decoder })
    }

    pub fn encode(&mut self, pcm: &[f32]) -> Vec<u8> {
        self.encoder
            .encode(pcm)
            .map_or_else(|_| Vec::new(), <[u8]>::to_vec)
    }

    pub fn decode(&mut self, pkt: &[u8], out: &mut [f32]) -> usize {
        self.decoder.decode(pkt, out).unwrap_or(0)
    }

    // Packet-loss concealment: synthesize a frame for a lost packet with no data available.
    pub fn decode_plc(&mut self, out: &mut [f32]) -> usize {
        self.decoder.decode_plc(out).unwrap_or(0)
    }

    // Recover the frame *before* `next_pkt` from that packet's in-band FEC (the encoder sets
    // inband_fec, so a packet carries redundant coding for the previous frame).
    pub fn decode_fec(&mut self, next_pkt: &[u8], out: &mut [f32]) -> usize {
        self.decoder.decode_fec(next_pkt, out).unwrap_or(0)
    }

    pub fn reset_decoder(&mut self) -> Result<(), OpusError> {
        self.decoder.reset()
    }

    /// Clears stateful encoder history before the authorized receiver set expands. In particular,
    /// this prevents a newly added peer from decoding pre-authorization speech through DRED.
    pub fn reset_encoder(&mut self) -> Result<(), OpusError> {
        self.encoder.reset()
    }

    /// Applies a route-wide encoder policy. PerfectComms uses one encoded packet for every peer,
    /// so callers should pass a conservative aggregate (for example the worst fresh receiver
    /// window), not pretend this can be configured independently per destination.
    pub fn set_network_conditions(
        &mut self,
        packet_loss_percent: u8,
        bitrate: i32,
    ) -> Result<(), OpusError> {
        self.encoder
            .set_network_conditions(packet_loss_percent, bitrate)
    }

    pub fn dred_duration_10ms(&self) -> u8 {
        self.encoder.dred_duration_10ms()
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct ConcealmentReport {
    pub decoded_frames: usize,
    pub dred_frames: usize,
    pub fec_frames: usize,
    pub plc_frames: usize,
}

// Decode `data` (RTP payload with sequence number `seq`) against the peer's last in-order seq,
// emitting concealment frames for any gap. Recovery walks oldest-to-newest: every missing frame
// first tries the current packet's deep redundancy, older uncovered frames fall back to PLC, and
// the newest uncovered frame falls back to classic in-band FEC. The current packet is decoded
// last. Late and duplicate RTP is dropped because rewinding would corrupt the stateful decoder.
pub fn decode_with_concealment(
    codec: &mut OpusCodec,
    last: Option<u16>,
    seq: u16,
    data: &[u8],
) -> (Vec<Vec<f32>>, bool) {
    let (frames, advance, _) = decode_with_concealment_report(codec, last, seq, data);
    (frames, advance)
}

pub fn decode_with_concealment_report(
    codec: &mut OpusCodec,
    last: Option<u16>,
    seq: u16,
    data: &[u8],
) -> (Vec<Vec<f32>>, bool, ConcealmentReport) {
    let mut frames: Vec<Vec<f32>> = Vec::new();
    let mut report = ConcealmentReport::default();
    let mut pcm = [0f32; FRAME_SIZE];
    let last = match last {
        None => {
            let n = codec.decode(data, &mut pcm);
            if n > 0 {
                frames.push(pcm[..n].to_vec());
                report.decoded_frames += 1;
            }
            return (frames, true, report);
        }
        Some(l) => l,
    };
    let delta = seq.wrapping_sub(last) as i16;
    if delta <= 0 {
        return (frames, false, report);
    }
    let lost = (delta - 1) as usize;
    if lost > 0 && lost <= MAX_CONCEAL_FRAMES {
        let dred_available = matches!(
            codec.decoder.parse_dred(data, lost * FRAME_SIZE),
            Ok(DredParseOutcome::Available(_))
        );
        for offset_frames in (1..=lost).rev() {
            let mut recovery = [0f32; FRAME_SIZE];
            // Opus dedicates classic LBRR/FEC to the immediately preceding frame. DRED is deep
            // history for older offsets, matching the upstream decoder chronology.
            let dred_samples = if dred_available && offset_frames > 1 {
                match codec
                    .decoder
                    .decode_parsed_dred(offset_frames * FRAME_SIZE, &mut recovery)
                {
                    Ok(DredDecodeOutcome::Decoded { samples, .. }) => samples,
                    Ok(
                        DredDecodeOutcome::NotPresent | DredDecodeOutcome::OffsetNotCovered { .. },
                    )
                    | Err(_) => 0,
                }
            } else {
                0
            };
            if dred_samples > 0 {
                frames.push(recovery[..dred_samples].to_vec());
                report.dred_frames += 1;
                continue;
            }

            let fallback_samples = if offset_frames == 1 {
                codec.decode_fec(data, &mut recovery)
            } else {
                codec.decode_plc(&mut recovery)
            };
            if fallback_samples > 0 {
                frames.push(recovery[..fallback_samples].to_vec());
                if offset_frames == 1 {
                    report.fec_frames += 1;
                } else {
                    report.plc_frames += 1;
                }
            }
        }
    }
    let n = codec.decode(data, &mut pcm);
    if n > 0 {
        frames.push(pcm[..n].to_vec());
        report.decoded_frames += 1;
    }
    (frames, true, report)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rtp(sequence: u16, arrival: Instant) -> EncodedRtpPacket {
        EncodedRtpPacket {
            sequence,
            timestamp: u32::from(sequence).wrapping_mul(RTP_FRAME_TICKS),
            arrival,
            payload: vec![(sequence & 0xff) as u8],
        }
    }

    fn modulated_tone_frame(frame: usize) -> [f32; FRAME_SIZE] {
        let mut pcm = [0.0f32; FRAME_SIZE];
        for (sample, value) in pcm.iter_mut().enumerate() {
            let time = (frame * FRAME_SIZE + sample) as f32 / SAMPLE_RATE as f32;
            let envelope = 0.65 + 0.35 * (std::f32::consts::TAU * 3.7 * time).sin().abs();
            *value = envelope
                * (0.22 * (std::f32::consts::TAU * 173.0 * time).sin()
                    + 0.10 * (std::f32::consts::TAU * 347.0 * time).sin()
                    + 0.05 * (std::f32::consts::TAU * 521.0 * time).sin());
        }
        pcm
    }

    fn encoded_tone_packets(count: usize) -> Vec<Vec<u8>> {
        let mut codec = OpusCodec::new().expect("opus codec init");
        assert!(codec.dred_duration_10ms() > 0, "DRED must be enabled");
        (0..count)
            .map(|frame| {
                let packet = codec.encode(&modulated_tone_frame(frame));
                assert!(!packet.is_empty(), "tone packet {frame} was empty");
                assert!(
                    packet.len() <= 1_200,
                    "tone packet {frame} exceeded a conservative RTP MTU payload"
                );
                packet
            })
            .collect()
    }

    #[test]
    fn round_trips_a_tone_frame() {
        let mut codec = OpusCodec::new().expect("opus codec init");
        let mut pcm = [0f32; FRAME_SIZE];
        for (i, s) in pcm.iter_mut().enumerate() {
            *s = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / SAMPLE_RATE as f32).sin() * 0.5;
        }
        let packet = codec.encode(&pcm);
        assert!(!packet.is_empty(), "encoder produced an empty packet");

        let mut decoded = [0f32; FRAME_SIZE];
        let samples = codec.decode(&packet, &mut decoded);
        assert_eq!(samples, FRAME_SIZE, "decoded sample count != frame size");
    }

    #[test]
    fn worst_dynamic_encoder_policy_stays_mtu_safe_and_decodes_full_frames() {
        let mut encoder = OpusCodec::new().expect("opus codec init");
        encoder
            .set_network_conditions(30, 24_000)
            .expect("apply worst route policy");
        let packets: Vec<Vec<u8>> = (0..80)
            .map(|frame| {
                let packet = encoder.encode(&modulated_tone_frame(frame));
                assert!(!packet.is_empty());
                assert!(
                    packet.len() <= 1_200,
                    "packet exceeded conservative RTP MTU"
                );
                packet
            })
            .collect();

        let mut decoder = OpusCodec::new().expect("opus decoder init");
        let mut output = [0.0f32; FRAME_SIZE];
        let mut energy = 0.0f32;
        for packet in packets {
            assert_eq!(decoder.decode(&packet, &mut output), FRAME_SIZE);
            assert!(output.iter().all(|sample| sample.is_finite()));
            energy += output.iter().map(|sample| sample * sample).sum::<f32>();
        }
        assert!(energy > 1e-3, "worst-policy primary audio was silent");
    }

    #[test]
    fn plc_and_fec_yield_full_frames() {
        let mut codec = OpusCodec::new().expect("opus codec init");
        let mut pcm = [0f32; FRAME_SIZE];
        for (i, s) in pcm.iter_mut().enumerate() {
            *s = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / SAMPLE_RATE as f32).sin() * 0.5;
        }
        let next = codec.encode(&pcm);
        assert!(!next.is_empty());

        let mut plc = [0f32; FRAME_SIZE];
        assert_eq!(
            codec.decode_plc(&mut plc),
            FRAME_SIZE,
            "PLC must produce a full concealment frame"
        );

        let mut fec = [0f32; FRAME_SIZE];
        assert_eq!(
            codec.decode_fec(&next, &mut fec),
            FRAME_SIZE,
            "FEC recovery must produce a full frame"
        );
    }

    #[test]
    fn concealment_emits_extra_frames_only_for_real_gaps() {
        let mut codec = OpusCodec::new().expect("opus codec init");
        let mut pcm = [0f32; FRAME_SIZE];
        for (i, s) in pcm.iter_mut().enumerate() {
            *s = (2.0 * std::f32::consts::PI * 440.0 * i as f32 / SAMPLE_RATE as f32).sin() * 0.5;
        }
        let pkt = codec.encode(&pcm);
        assert!(!pkt.is_empty());

        // First packet for a peer: one frame, advance.
        let (f, adv) = decode_with_concealment(&mut codec, None, 100, &pkt);
        assert_eq!(f.len(), 1);
        assert!(adv);

        // In-order next packet (seq 101): one frame, advance.
        let (f, adv) = decode_with_concealment(&mut codec, Some(100), 101, &pkt);
        assert_eq!(f.len(), 1);
        assert!(adv);

        // Gap of 2 lost packets (last=101, seq=104): 1 PLC + 1 FEC + the live frame = 3.
        let (f, adv) = decode_with_concealment(&mut codec, Some(101), 104, &pkt);
        assert_eq!(f.len(), 3, "two-packet gap must conceal then play");
        assert!(adv);

        // Out-of-order / duplicate (seq <= last): its slot was already concealed, so drop it.
        let (f, adv) = decode_with_concealment(&mut codec, Some(104), 102, &pkt);
        assert!(f.is_empty());
        assert!(!adv, "late packet must not rewind gap tracking");

        // Huge gap beyond the cap: treat as restart, no concealment flood (just the live frame).
        let (f, adv) = decode_with_concealment(&mut codec, Some(104), 1000, &pkt);
        assert_eq!(
            f.len(),
            1,
            "oversized gap is a restart, not a conceal flood"
        );
        assert!(adv);
    }

    #[test]
    fn concealment_uses_deep_redundancy_before_plc_for_burst_loss() {
        const PACKETS: usize = 100;
        const LOST: usize = 4;
        let packets = encoded_tone_packets(PACKETS);
        let current = PACKETS - 1;
        let last = current - LOST - 1;
        let mut codec = OpusCodec::new().expect("opus codec init");
        let mut scratch = [0.0f32; FRAME_SIZE];
        for packet in &packets[..=last] {
            assert_eq!(codec.decode(packet, &mut scratch), FRAME_SIZE);
        }

        let (frames, advance, report) = decode_with_concealment_report(
            &mut codec,
            Some(last as u16),
            current as u16,
            &packets[current],
        );
        assert!(advance);
        assert_eq!(frames.len(), LOST + 1);
        assert_eq!(report.decoded_frames, 1);
        assert_eq!(report.fec_frames, 1, "newest loss should use LBRR/FEC");
        assert!(
            report.dred_frames >= 2,
            "configured DRED did not recover deep burst history: {report:?}"
        );
        assert_eq!(
            report.dred_frames + report.fec_frames + report.plc_frames,
            LOST
        );
        assert!(frames.iter().all(
            |frame| frame.len() == FRAME_SIZE && frame.iter().all(|sample| sample.is_finite())
        ));
        let energy: f32 = frames.iter().flatten().map(|sample| sample * sample).sum();
        assert!(energy > 1e-3, "recovered burst was unexpectedly silent");
    }

    #[test]
    fn encoded_buffer_recovers_reorder_before_decode() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("peer", metrics.clone());
        let start = Instant::now();
        assert_eq!(
            buffer.insert(rtp(102, start)),
            PacketInsertOutcome::Accepted
        );
        assert_eq!(
            buffer.insert(rtp(100, start + Duration::from_millis(5))),
            PacketInsertOutcome::Reordered
        );
        assert_eq!(
            buffer.insert(rtp(101, start + Duration::from_millis(10))),
            PacketInsertOutcome::Reordered
        );

        let first = buffer.pop_ready(start + Duration::from_millis(10)).unwrap();
        let second = buffer.pop_ready(start + Duration::from_millis(30)).unwrap();
        let third = buffer.pop_ready(start + Duration::from_millis(50)).unwrap();
        assert_eq!(
            [
                first.extended_sequence,
                second.extended_sequence,
                third.extended_sequence
            ],
            [65_636, 65_637, 65_638]
        );
        assert_eq!(metrics.snapshot().reordered_recovered, 2);
        assert_eq!(metrics.snapshot().deadline_losses, 0);
    }

    #[test]
    fn encoded_buffer_extends_sequence_across_wrap() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("wrap", metrics);
        let start = Instant::now();
        for (index, sequence) in [65_534, 65_535, 0, 1].into_iter().enumerate() {
            let mut packet = rtp(sequence, start + Duration::from_millis(index as u64 * 20));
            packet.timestamp = (index as u32).wrapping_mul(RTP_FRAME_TICKS);
            assert!(matches!(
                buffer.insert(packet),
                PacketInsertOutcome::Accepted
            ));
        }
        let first = buffer.pop_ready(start + Duration::from_millis(80)).unwrap();
        let second = buffer
            .pop_ready(start + Duration::from_millis(100))
            .unwrap();
        let third = buffer
            .pop_ready(start + Duration::from_millis(120))
            .unwrap();
        assert_eq!(first.extended_sequence, 131_070);
        assert_eq!(second.extended_sequence, 131_071);
        assert_eq!(third.extended_sequence, 131_072);
    }

    #[test]
    fn encoded_buffer_reorders_packet_from_before_initial_wrap() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("initial-wrap", metrics);
        let start = Instant::now();
        let mut after_wrap = rtp(0, start);
        after_wrap.timestamp = RTP_FRAME_TICKS;
        let mut before_wrap = rtp(u16::MAX, start + Duration::from_millis(5));
        before_wrap.timestamp = 0;
        assert_eq!(buffer.insert(after_wrap), PacketInsertOutcome::Accepted);
        assert_eq!(buffer.insert(before_wrap), PacketInsertOutcome::Reordered);
        let first = buffer.pop_ready(start + Duration::from_millis(5)).unwrap();
        let second = buffer.pop_ready(start + Duration::from_millis(25)).unwrap();
        assert_eq!(first.sequence, u16::MAX);
        assert_eq!(first.extended_sequence, 65_535);
        assert_eq!(second.sequence, 0);
        assert_eq!(second.extended_sequence, 65_536);
    }

    #[test]
    fn stable_latency_does_not_inflate_target_from_rtt() {
        for fixed_path_ms in [1u64, 50, 150, 300] {
            let metrics = Arc::new(MediaReceiveCounters::default());
            let mut buffer = EncodedPacketBuffer::new(format!("path-{fixed_path_ms}"), metrics);
            let start = Instant::now() + Duration::from_millis(fixed_path_ms);
            for index in 0..100u16 {
                let mut packet = rtp(index, start + Duration::from_millis(u64::from(index) * 20));
                packet.timestamp = u32::from(index) * RTP_FRAME_TICKS;
                buffer.insert(packet);
            }
            assert_eq!(buffer.target_frames(), MIN_PLAYOUT_FRAMES);
            assert!(buffer.jitter_ms() < 0.001);
        }
    }

    #[test]
    fn jitter_grows_fast_and_decays_slowly() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("wifi", metrics);
        let start = Instant::now();
        let mut arrival = start;
        for index in 0..40u16 {
            arrival += if index % 2 == 0 {
                Duration::from_millis(5)
            } else {
                Duration::from_millis(55)
            };
            let mut packet = rtp(index, arrival);
            packet.timestamp = u32::from(index) * RTP_FRAME_TICKS;
            buffer.insert(packet);
        }
        let raised = buffer.target_frames();
        assert!(raised >= 5, "jitter target did not grow enough: {raised}");

        for index in 40..(40 + TARGET_DECAY_STABLE_PACKETS as u16 - 1) {
            arrival += Duration::from_millis(20);
            let mut packet = rtp(index, arrival);
            packet.timestamp = u32::from(index) * RTP_FRAME_TICKS;
            buffer.insert(packet);
        }
        assert_eq!(buffer.target_frames(), raised, "target decayed too quickly");
    }

    #[test]
    fn active_talkspurt_acquires_larger_target_without_intentional_gap() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("continuous", metrics.clone());
        let start = Instant::now();
        buffer.insert(rtp(0, start));
        buffer.insert(rtp(1, start + Duration::from_millis(20)));
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(20))
                .unwrap()
                .sequence,
            0
        );
        assert_eq!(buffer.playout_target_frames(), 2);

        // Simulate an immediate estimator jump while voiced packets continue at exactly 20 ms.
        // The old implementation returned None here until depth reached five, creating a cut.
        buffer.target_frames = 5;
        for sequence in 2..8u16 {
            let at = start + Duration::from_millis(u64::from(sequence) * 20);
            buffer.insert(rtp(sequence, at));
            let ready = buffer
                .pop_ready(at)
                .expect("active target growth must not withhold voiced audio");
            assert_eq!(ready.sequence, sequence - 1);
        }
        assert_eq!(buffer.playout_target_frames(), 2);
        assert_eq!(metrics.snapshot().rebuffers, 0);

        // A real arrival burst supplies extra cushion, so the acquired target can advance without
        // stealing a playout slot. It grows at most one frame per round.
        for sequence in 8..11u16 {
            buffer.insert(rtp(
                sequence,
                start + Duration::from_millis(160 + u64::from(sequence - 8)),
            ));
        }
        let ready = buffer
            .pop_ready(start + Duration::from_millis(180))
            .unwrap();
        assert_eq!(ready.sequence, 7);
        assert_eq!(buffer.playout_target_frames(), 3);
        assert_eq!(metrics.snapshot().rebuffers, 0);
    }

    #[test]
    fn primed_stream_drains_available_tail_without_a_minimum_depth_pause() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("tail", metrics.clone());
        let start = Instant::now();
        buffer.insert(rtp(0, start));
        buffer.insert(rtp(1, start + Duration::from_millis(20)));

        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(20))
                .unwrap()
                .sequence,
            0
        );
        assert_eq!(buffer.depth_frames(), 1);
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(40))
                .expect("a present tail frame must not be held for a timeout")
                .sequence,
            1
        );
        assert_eq!(metrics.snapshot().rebuffers, 0);
    }

    #[test]
    fn late_packet_after_full_underrun_cannot_rewind_repriming_timeline() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("underrun-late", metrics.clone());
        let start = Instant::now();
        buffer.insert(rtp(10, start));
        buffer.insert(rtp(11, start + Duration::from_millis(20)));

        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(20))
                .unwrap()
                .sequence,
            10
        );
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(40))
                .unwrap()
                .sequence,
            11
        );
        assert!(buffer
            .pop_ready(start + Duration::from_millis(60))
            .is_none());

        assert_eq!(
            buffer.insert(rtp(10, start + Duration::from_millis(30))),
            PacketInsertOutcome::Late,
            "a packet from the drained timeline must stay late while re-priming"
        );
        assert_eq!(
            buffer.insert(rtp(12, start + Duration::from_millis(40))),
            PacketInsertOutcome::Accepted
        );
        assert_eq!(
            buffer.insert(rtp(13, start + Duration::from_millis(60))),
            PacketInsertOutcome::Accepted
        );

        let resumed = buffer
            .pop_ready(start + Duration::from_millis(60))
            .expect("fresh consecutive packets must re-prime the retained timeline");
        assert_eq!(resumed.sequence, 12);
        assert_eq!(resumed.gap_before, 0);
        assert_eq!(metrics.snapshot().late_drops, 1);
    }

    #[test]
    fn talkspurt_resets_sequence_state_but_keeps_learned_route_target() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("talkspurt", metrics.clone());
        let start = Instant::now();
        let mut arrival = start;
        for sequence in 0..30u16 {
            arrival += if sequence % 2 == 0 {
                Duration::from_millis(5)
            } else {
                Duration::from_millis(55)
            };
            let mut packet = rtp(sequence, arrival);
            packet.timestamp = u32::from(sequence) * RTP_FRAME_TICKS;
            buffer.insert(packet);
        }
        let learned = buffer.target_frames();
        assert!(learned > MIN_PLAYOUT_FRAMES);

        let mut next = rtp(2_000, arrival + Duration::from_secs(1));
        next.timestamp = 123_456;
        assert_eq!(buffer.insert(next), PacketInsertOutcome::Accepted);
        assert_eq!(buffer.target_frames(), learned);
        assert_eq!(buffer.depth_frames(), 1);
        assert_eq!(metrics.snapshot().talkspurt_resets, 1);
    }

    #[test]
    fn severely_late_old_packet_cannot_fake_a_new_talkspurt() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("late", metrics.clone());
        let start = Instant::now();
        for sequence in 10..13u16 {
            let mut packet = rtp(
                sequence,
                start + Duration::from_millis(u64::from(sequence - 10) * 20),
            );
            packet.timestamp = u32::from(sequence - 10) * RTP_FRAME_TICKS;
            buffer.insert(packet);
        }
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(40))
                .unwrap()
                .sequence,
            10
        );
        let mut stale = rtp(9, start + Duration::from_secs(1));
        stale.timestamp = 0;
        assert_eq!(buffer.insert(stale), PacketInsertOutcome::Late);
        assert_eq!(metrics.snapshot().talkspurt_resets, 0);
    }

    #[test]
    fn loss_waits_for_deadline_then_reports_gap_for_fec_plc() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("loss", metrics.clone());
        let start = Instant::now();
        for sequence in [10u16, 11, 13, 14] {
            let mut packet = rtp(
                sequence,
                start + Duration::from_millis(u64::from(sequence - 10) * 20),
            );
            packet.timestamp = u32::from(sequence - 10) * RTP_FRAME_TICKS;
            buffer.insert(packet);
        }
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(80))
                .unwrap()
                .sequence,
            10
        );
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(100))
                .unwrap()
                .sequence,
            11
        );
        let recovered = buffer
            .pop_ready(start + Duration::from_millis(110))
            .expect("buffered cushion should satisfy the reorder deadline");
        assert_eq!(recovered.sequence, 13);
        assert_eq!(recovered.gap_before, 1);
        buffer.record_decode(ConcealmentReport {
            decoded_frames: 1,
            dred_frames: 0,
            fec_frames: 1,
            plc_frames: 0,
        });
        let snapshot = metrics.snapshot();
        assert_eq!(snapshot.deadline_losses, 1);
        assert_eq!(snapshot.fec_frames, 1);
    }

    #[test]
    fn freshly_observed_gap_gets_a_bounded_reorder_window() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("fresh-gap", metrics);
        let start = Instant::now();
        buffer.insert(rtp(10, start));
        buffer.insert(rtp(11, start + Duration::from_millis(20)));
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(20))
                .unwrap()
                .sequence,
            10
        );

        let mut future = rtp(13, start + Duration::from_millis(100));
        future.timestamp = 3 * RTP_FRAME_TICKS;
        buffer.insert(future);
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(100))
                .unwrap()
                .sequence,
            11
        );
        assert!(
            buffer
                .pop_ready(start + Duration::from_millis(105))
                .is_none(),
            "a just-arrived future packet must allow the missing packet to reorder"
        );
        let released = buffer
            .pop_ready(start + Duration::from_millis(160))
            .expect("bounded reorder deadline must eventually release future audio");
        assert_eq!(released.sequence, 13);
        assert_eq!(released.gap_before, 1);
    }

    #[test]
    fn gap_beyond_concealment_cap_forces_decoder_timeline_reset() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("restart-gap", metrics.clone());
        let start = Instant::now();
        for sequence in [10u16, 11, 20, 21] {
            buffer.insert(rtp(
                sequence,
                start + Duration::from_millis(u64::from(sequence - 10) * 20),
            ));
        }
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(200))
                .unwrap()
                .sequence,
            10
        );
        assert_eq!(
            buffer
                .pop_ready(start + Duration::from_millis(220))
                .unwrap()
                .sequence,
            11
        );
        let restart = buffer
            .pop_ready(start + Duration::from_millis(240))
            .expect("oversized gap must release the next stream without concealment flood");
        assert_eq!(restart.sequence, 20);
        assert_eq!(restart.gap_before, 8);
        assert!(restart.reset_decoder);
        assert_eq!(metrics.snapshot().decoder_resets, 1);
    }

    #[test]
    fn full_underrun_reprime_preserves_gap_and_resets_stale_decoder() {
        let packets = encoded_tone_packets(101);
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("full-underrun", metrics.clone());
        let start = Instant::now();
        let mut decoder = OpusCodec::new().expect("decoder init");
        let mut scratch = [0.0f32; FRAME_SIZE];
        for packet in &packets[..90] {
            assert_eq!(decoder.decode(packet, &mut scratch), FRAME_SIZE);
        }

        for sequence in [90u16, 91] {
            buffer.insert(EncodedRtpPacket {
                sequence,
                timestamp: u32::from(sequence) * RTP_FRAME_TICKS,
                arrival: start + Duration::from_millis(u64::from(sequence) * 20),
                payload: packets[sequence as usize].clone(),
            });
        }
        let first = buffer
            .pop_ready(start + Duration::from_millis(1_820))
            .unwrap();
        let (frames, advance, _) =
            decode_with_concealment_report(&mut decoder, Some(89), first.sequence, &first.payload);
        assert!(advance);
        assert_eq!(frames.len(), 1);
        let second = buffer
            .pop_ready(start + Duration::from_millis(1_840))
            .unwrap();
        let (frames, advance, _) = decode_with_concealment_report(
            &mut decoder,
            Some(first.sequence),
            second.sequence,
            &second.payload,
        );
        assert!(advance);
        assert_eq!(frames.len(), 1);

        // A playout tick with no packet is a real complete underrun and forces re-priming.
        assert!(buffer
            .pop_ready(start + Duration::from_millis(1_860))
            .is_none());
        for sequence in [99u16, 100] {
            buffer.insert(EncodedRtpPacket {
                sequence,
                timestamp: u32::from(sequence) * RTP_FRAME_TICKS,
                arrival: start + Duration::from_millis(u64::from(sequence) * 20),
                payload: packets[sequence as usize].clone(),
            });
        }
        let restarted = buffer
            .pop_ready(start + Duration::from_millis(2_000))
            .expect("re-primed stream must release after the buffered reorder deadline");
        assert_eq!(restarted.sequence, 99);
        assert_eq!(restarted.gap_before, 7);
        assert!(
            restarted.reset_decoder,
            "a gap beyond concealment capacity must not decode against stale state"
        );

        decoder.reset_decoder().expect("reset stale decoder");
        let (frames, advance, report) = decode_with_concealment_report(
            &mut decoder,
            None,
            restarted.sequence,
            &restarted.payload,
        );
        assert!(advance);
        assert_eq!(report.decoded_frames, 1);
        assert_eq!(
            report.dred_frames + report.fec_frames + report.plc_frames,
            0
        );
        assert_eq!(frames.len(), 1);
        assert_eq!(frames[0].len(), FRAME_SIZE);
        assert!(frames[0].iter().all(|sample| sample.is_finite()));
        let snapshot = metrics.snapshot();
        assert_eq!(snapshot.deadline_losses, 7);
        assert_eq!(snapshot.decoder_resets, 1);
        assert_eq!(snapshot.underruns, 1);
        assert_eq!(snapshot.rebuffers, 1);
    }

    #[test]
    fn encoded_overflow_fast_forwards_to_a_bounded_fresh_tail() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let mut buffer = EncodedPacketBuffer::new("overflow", metrics.clone());
        buffer.target_frames = MAX_PLAYOUT_FRAMES;
        let start = Instant::now();
        for sequence in 0..ENCODED_PACKET_CAPACITY as u16 {
            buffer.insert(rtp(
                sequence,
                start + Duration::from_millis(u64::from(sequence) * 20),
            ));
        }
        assert_eq!(buffer.depth_frames(), ENCODED_PACKET_CAPACITY);
        assert_eq!(
            buffer.insert(rtp(
                ENCODED_PACKET_CAPACITY as u16,
                start + Duration::from_millis(ENCODED_PACKET_CAPACITY as u64 * 20),
            )),
            PacketInsertOutcome::Overflow
        );
        assert_eq!(buffer.depth_frames(), MAX_PLAYOUT_FRAMES);

        // The catch-up floor is authoritative even before the tail re-primes.
        assert_eq!(
            buffer.insert(rtp(0, start + Duration::from_millis(650))),
            PacketInsertOutcome::Late
        );

        let newest = ENCODED_PACKET_CAPACITY as u16;
        let first = buffer
            .pop_ready(start + Duration::from_millis(650))
            .expect("target-sized newest tail must immediately re-prime");
        assert!(first.reset_decoder);
        assert_eq!(first.gap_before, 0);
        assert!(
            newest.wrapping_sub(first.sequence) as usize * 20 <= MAX_PLAYOUT_FRAMES * 20,
            "overflow recovery exceeded the 300 ms playout ceiling"
        );

        // At one arrival and one playout per tick, the receiver remains within the bounded tail
        // instead of staying a full 32-frame retention window behind forever.
        for sequence in newest + 1..newest + 20 {
            let at = start + Duration::from_millis(u64::from(sequence) * 20);
            assert_eq!(
                buffer.insert(rtp(sequence, at)),
                PacketInsertOutcome::Accepted
            );
            let ready = buffer
                .pop_ready(at)
                .expect("caught-up stream must keep draining");
            assert!(sequence.wrapping_sub(ready.sequence) as usize * 20 <= MAX_PLAYOUT_FRAMES * 20);
        }

        let snapshot = metrics.snapshot();
        assert_eq!(snapshot.encoded_overflow_drops, 18);
        assert_eq!(snapshot.decoder_resets, 1);
    }

    #[test]
    fn fifteen_peer_impairment_matrix_stays_bounded_and_ordered() {
        let metrics = Arc::new(MediaReceiveCounters::default());
        let start = Instant::now();
        let mut peers: Vec<EncodedPacketBuffer> = (0..15)
            .map(|index| EncodedPacketBuffer::new(format!("peer-{index}"), metrics.clone()))
            .collect();

        // Direct/relay is intentionally represented as fixed path delay: target selection must be
        // invariant to 1-300 ms latency. Per-peer deterministic impairment adds jitter, reordering,
        // independent loss, burst loss, duplicates, and one severely late packet.
        for (peer_index, buffer) in peers.iter_mut().enumerate() {
            let path_ms = 1 + (peer_index as u64 * 299 / 14);
            let mut events: Vec<(u64, EncodedRtpPacket)> = Vec::new();
            for sequence in 0..80u16 {
                if sequence % 19 == 7 || matches!(sequence, 40 | 41) {
                    continue;
                }
                let jitter = ((sequence as i64 * 13 + peer_index as i64 * 7) % 31) - 15;
                let reorder_delay = if sequence % 11 == 3 { 55 } else { 0 };
                let severe_late = if sequence == 25 { 180 } else { 0 };
                let arrival_ms = (path_ms as i64
                    + i64::from(sequence) * 20
                    + jitter
                    + reorder_delay
                    + severe_late)
                    .max(0) as u64;
                let mut packet = rtp(sequence, start + Duration::from_millis(arrival_ms));
                packet.timestamp = u32::from(sequence) * RTP_FRAME_TICKS;
                events.push((arrival_ms, packet.clone()));
                if sequence % 23 == 5 {
                    let mut duplicate = packet;
                    duplicate.arrival += Duration::from_millis(4);
                    events.push((arrival_ms + 4, duplicate));
                }
            }
            events.sort_by_key(|(arrival_ms, _)| *arrival_ms);

            let mut event_index = 0usize;
            let mut last_extended = None;
            let mut released = 0usize;
            for tick_ms in (0..=2_600u64).step_by(20) {
                while event_index < events.len() && events[event_index].0 <= tick_ms {
                    buffer.insert(events[event_index].1.clone());
                    event_index += 1;
                }
                if let Some(packet) = buffer.pop_ready(start + Duration::from_millis(tick_ms)) {
                    if let Some(last) = last_extended {
                        assert!(
                            packet.extended_sequence > last,
                            "peer {peer_index} decoder order regressed"
                        );
                    }
                    last_extended = Some(packet.extended_sequence);
                    released += 1;
                }
                assert!(buffer.depth_frames() <= ENCODED_PACKET_CAPACITY);
            }
            assert!(released > 50, "peer {peer_index} stalled after impairment");
            assert!((MIN_PLAYOUT_FRAMES..=MAX_PLAYOUT_FRAMES).contains(&buffer.target_frames()));
        }
        assert_eq!(metrics.snapshot().active_peers, 15);
        assert!(metrics.snapshot().depth_frames_current <= (15 * ENCODED_PACKET_CAPACITY) as u64);
        assert!(metrics.snapshot().reordered_recovered > 0);
        assert!(metrics.snapshot().deadline_losses > 0);
        assert!(metrics.snapshot().duplicate_drops > 0);
        assert!(metrics.snapshot().late_drops > 0);
    }

    #[test]
    fn decoded_tone_impairment_matrix_stays_finite_ordered_and_bounded() {
        const PACKETS: usize = 120;
        let encoded = encoded_tone_packets(PACKETS);
        let mut final_targets = Vec::new();
        let mut recovered_with_dred = 0u64;

        for fixed_path_ms in [1u64, 100, 200, 300] {
            let start = Instant::now();
            let metrics = Arc::new(MediaReceiveCounters::default());
            let mut buffer =
                EncodedPacketBuffer::new(format!("decoded-path-{fixed_path_ms}"), metrics.clone());
            let mut events: Vec<(u64, EncodedRtpPacket)> = Vec::new();
            for (sequence, payload) in encoded.iter().enumerate() {
                let sequence = sequence as u16;
                // Independent loss plus a three-packet burst, after enough history exists for
                // real DRED data. Every path receives the exact same impairment timeline.
                if sequence % 31 == 17 || matches!(sequence, 70..=72) {
                    continue;
                }
                let jitter_ms = (u64::from(sequence) * 17 + 5) % 31;
                let reorder_ms = if sequence % 13 == 4 { 55 } else { 0 };
                let severely_late_ms = if sequence == 52 { 500 } else { 0 };
                let arrival_ms = fixed_path_ms
                    + u64::from(sequence) * 20
                    + jitter_ms
                    + reorder_ms
                    + severely_late_ms;
                let packet = EncodedRtpPacket {
                    sequence,
                    timestamp: u32::from(sequence) * RTP_FRAME_TICKS,
                    arrival: start + Duration::from_millis(arrival_ms),
                    payload: payload.clone(),
                };
                events.push((arrival_ms, packet.clone()));
                if sequence % 23 == 5 {
                    let mut duplicate = packet;
                    duplicate.arrival += Duration::from_millis(4);
                    events.push((arrival_ms + 4, duplicate));
                }
            }
            events.sort_by_key(|(arrival_ms, packet)| (*arrival_ms, packet.sequence));

            let mut decoder = OpusCodec::new().expect("decoder init");
            let mut event_index = 0usize;
            let mut last_extended = None;
            let mut last_sequence = None;
            let mut released = 0usize;
            let mut decoded_energy = 0.0f64;
            let end_ms = fixed_path_ms + PACKETS as u64 * 20 + 1_000;
            for tick_ms in (fixed_path_ms..=end_ms).step_by(20) {
                while event_index < events.len() && events[event_index].0 <= tick_ms {
                    buffer.insert(events[event_index].1.clone());
                    event_index += 1;
                }

                let present_in_order = buffer.primed
                    && buffer
                        .expected_extended
                        .is_some_and(|expected| buffer.packets.contains_key(&expected));
                let ready = buffer.pop_ready(start + Duration::from_millis(tick_ms));
                if present_in_order {
                    assert!(
                        ready.is_some(),
                        "path {fixed_path_ms} inserted a target/tail hold in active audio"
                    );
                }
                let Some(packet) = ready else {
                    continue;
                };
                if let Some(previous) = last_extended {
                    assert!(
                        packet.extended_sequence > previous,
                        "path {fixed_path_ms} decoder timeline regressed"
                    );
                }
                last_extended = Some(packet.extended_sequence);
                if packet.reset_decoder {
                    decoder.reset_decoder().expect("decoder timeline reset");
                    last_sequence = None;
                }
                let (frames, advance, report) = decode_with_concealment_report(
                    &mut decoder,
                    last_sequence,
                    packet.sequence,
                    &packet.payload,
                );
                assert!(advance, "ordered packet did not advance decoder state");
                assert_eq!(report.decoded_frames, 1);
                let concealed = report.dred_frames + report.fec_frames + report.plc_frames;
                assert!(concealed <= MAX_CONCEAL_FRAMES);
                assert_eq!(frames.len(), report.decoded_frames + concealed);
                assert!(
                    frames.iter().all(|frame| {
                        frame.len() == FRAME_SIZE && frame.iter().all(|sample| sample.is_finite())
                    }),
                    "path {fixed_path_ms} emitted a partial or non-finite frame"
                );
                decoded_energy += frames
                    .iter()
                    .flatten()
                    .map(|sample| f64::from(*sample) * f64::from(*sample))
                    .sum::<f64>();
                buffer.record_decode(report);
                last_sequence = Some(packet.sequence);
                released += 1;
            }

            assert_eq!(event_index, events.len());
            assert!(buffer.is_idle(), "path {fixed_path_ms} did not drain");
            assert!(
                released > 90,
                "path {fixed_path_ms} stalled under impairment"
            );
            assert!(
                decoded_energy > 1.0,
                "path {fixed_path_ms} decoded tone was unexpectedly silent"
            );
            let snapshot = metrics.snapshot();
            let concealed = snapshot.dred_frames + snapshot.fec_frames + snapshot.plc_frames;
            assert!(snapshot.deadline_losses > 0);
            assert!(concealed <= snapshot.deadline_losses);
            assert!(snapshot.duplicate_drops > 0);
            assert!(snapshot.late_drops > 0);
            assert_eq!(snapshot.encoded_overflow_drops, 0);
            assert!((MIN_PLAYOUT_FRAMES..=MAX_PLAYOUT_FRAMES).contains(&buffer.target_frames()));
            recovered_with_dred += snapshot.dred_frames;
            final_targets.push(buffer.target_frames());
        }

        assert!(
            final_targets.windows(2).all(|pair| pair[0] == pair[1]),
            "fixed 1-300 ms path delay changed the jitter target: {final_targets:?}"
        );
        assert!(
            recovered_with_dred > 0,
            "impairment matrix never exercised DRED"
        );
    }

    #[test]
    fn encoder_policy_degrades_immediately_and_recovers_with_hysteresis() {
        let mut controller = EncoderNetworkController::new();
        let bad = [EncoderFeedback {
            fraction_lost: 0.16,
        }];
        let degraded = controller.observe(&bad);
        assert_eq!(degraded.packet_loss_percent, 25);
        assert_eq!(degraded.bitrate, 40_000);
        assert_eq!(degraded.generation, 1);

        let healthy = [EncoderFeedback { fraction_lost: 0.0 }];
        for _ in 0..ENCODER_POLICY_RECOVERY_WINDOWS - 1 {
            assert_eq!(controller.observe(&healthy), degraded);
        }
        let recovered = controller.observe(&healthy);
        assert_eq!(recovered.packet_loss_percent, 5);
        assert_eq!(recovered.bitrate, DEFAULT_ENCODER_BITRATE);
        assert_eq!(recovered.generation, 2);
    }

    #[test]
    fn encoder_policy_uses_upper_quartile_without_sacrificing_a_lobby_to_one_outlier() {
        let mut controller = EncoderNetworkController::new();
        let peers = [
            EncoderFeedback { fraction_lost: 0.0 },
            EncoderFeedback {
                fraction_lost: 0.01,
            },
            EncoderFeedback {
                fraction_lost: 0.02,
            },
            EncoderFeedback {
                fraction_lost: 0.04,
            },
            EncoderFeedback {
                fraction_lost: 0.60,
            },
        ];
        let policy = controller.observe(&peers);
        assert_eq!(policy.packet_loss_percent, 15);
        assert_eq!(policy.bitrate, DEFAULT_ENCODER_BITRATE);

        let mut small_call = EncoderNetworkController::new();
        let small_policy = small_call.observe(&[
            EncoderFeedback { fraction_lost: 0.0 },
            EncoderFeedback {
                fraction_lost: 0.09,
            },
        ]);
        assert_eq!(small_policy.packet_loss_percent, 20);
        assert_eq!(small_policy.bitrate, 44_000);
    }

    #[test]
    fn encoder_policy_stale_feedback_returns_to_safe_baseline_slowly() {
        let mut controller = EncoderNetworkController::new();
        let degraded = controller.observe(&[EncoderFeedback {
            fraction_lost: 0.30,
        }]);
        assert_eq!(degraded.packet_loss_percent, 30);
        assert_eq!(degraded.bitrate, 36_000);
        for _ in 0..ENCODER_POLICY_RECOVERY_WINDOWS - 1 {
            assert_eq!(controller.observe(&[]), degraded);
        }
        let baseline = controller.observe(&[]);
        assert_eq!(
            baseline.packet_loss_percent,
            DEFAULT_ENCODER_PACKET_LOSS_PERCENT
        );
        assert_eq!(baseline.bitrate, DEFAULT_ENCODER_BITRATE);
    }
}
