use crate::proto::{AudioFrame, PeerLevel, FRAME_SAMPLES, SAMPLE_RATE};
use std::collections::HashMap;
use std::sync::{Mutex, TryLockError};
use std::time::{Duration, Instant};

pub const DEFAULT_INPUT_GAIN: f32 = 1.0;
pub const MAX_INPUT_GAIN: f32 = 2.0;
const LIMITER_KNEE: f32 = 0.90;
const LIMITER_HEADROOM: f32 = 1.0 - LIMITER_KNEE;
pub const DEFAULT_VAD_THRESHOLD: f32 = 0.004;
pub const MIN_VAD_THRESHOLD: f32 = 0.0001;
pub const MAX_VAD_THRESHOLD: f32 = 1.0;
pub const DEFAULT_NOISE_GATE_THRESHOLD: f32 = 0.003;
pub const MIN_NOISE_GATE_THRESHOLD: f32 = 0.0;
pub const MAX_NOISE_GATE_THRESHOLD: f32 = 1.0;
const GATE_CLOSE_RATIO: f32 = 0.65;
const GATE_PEAK_OPEN_RATIO: f32 = 2.0;
const GATE_PEAK_HOLD_RATIO: f32 = 1.4;
const GATE_HANGOVER_FRAMES: usize = 10; // 200 ms at 20 ms/frame
pub const SYNTHETIC_TONE_HZ: f32 = 220.0;
pub const SYNTHETIC_TONE_AMPLITUDE: f32 = 0.012;
pub const TELEMETRY_INTERVAL: Duration = Duration::from_millis(100);
pub const MAX_PEER_LEVELS: usize = 32;
pub const MAX_PEER_ID_BYTES: usize = 32;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct InputConfig {
    pub gain: f32,
    pub vad_threshold: f32,
    pub noise_gate_threshold: f32,
}

impl Default for InputConfig {
    fn default() -> Self {
        Self {
            gain: DEFAULT_INPUT_GAIN,
            vad_threshold: DEFAULT_VAD_THRESHOLD,
            noise_gate_threshold: DEFAULT_NOISE_GATE_THRESHOLD,
        }
    }
}

impl InputConfig {
    pub fn sanitized(gain: f32, vad_threshold: f32) -> Self {
        Self::sanitized_with_gate(gain, vad_threshold, DEFAULT_NOISE_GATE_THRESHOLD)
    }

    pub fn sanitized_with_gate(gain: f32, vad_threshold: f32, noise_gate_threshold: f32) -> Self {
        let gain = finite_or(gain, DEFAULT_INPUT_GAIN).clamp(0.0, MAX_INPUT_GAIN);
        let vad_threshold = finite_or(vad_threshold, DEFAULT_VAD_THRESHOLD)
            .clamp(MIN_VAD_THRESHOLD, MAX_VAD_THRESHOLD);
        let noise_gate_threshold = finite_or(noise_gate_threshold, DEFAULT_NOISE_GATE_THRESHOLD)
            .clamp(MIN_NOISE_GATE_THRESHOLD, MAX_NOISE_GATE_THRESHOLD);
        Self {
            gain,
            vad_threshold,
            noise_gate_threshold,
        }
    }

    /// Applies user input gain and a continuous soft-knee limiter immediately before Opus.
    /// Invalid input samples are made silent so NaN/Inf can never poison the encoder or level
    /// telemetry. Unlike a hard clamp, the limiter keeps distinct over-threshold sample values
    /// distinct, avoiding the flat-topped waveform produced by 200% microphone gain.
    pub fn apply_gain(&self, samples: &mut [f32]) {
        for sample in samples {
            let value = if sample.is_finite() { *sample } else { 0.0 };
            *sample = soft_limit(value * self.gain);
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GateDecision {
    Bypassed,
    Opened,
    Held,
    Closing,
    Closed,
}

/// Frame-aware speech gate. Detection examines the complete 20 ms frame before mutating it, so
/// the first consonant that opens the gate is transmitted in full. Hysteresis and a 200 ms tail
/// prevent chatter/word-end clipping; the final quiet frame is faded before subsequent frames are
/// made silent. RTP/Opus cadence remains continuous because callers still encode every frame.
#[derive(Debug, Default)]
pub struct NoiseGate {
    open: bool,
    hangover_frames: usize,
}

impl NoiseGate {
    pub fn process(&mut self, samples: &mut [f32], threshold: f32) -> GateDecision {
        let threshold = finite_or(threshold, DEFAULT_NOISE_GATE_THRESHOLD)
            .clamp(MIN_NOISE_GATE_THRESHOLD, MAX_NOISE_GATE_THRESHOLD);
        if threshold <= 0.0 {
            self.open = true;
            self.hangover_frames = GATE_HANGOVER_FRAMES;
            return GateDecision::Bypassed;
        }

        let mut peak = 0.0f32;
        let mut square_sum = 0.0f64;
        for &sample in samples.iter() {
            let finite = if sample.is_finite() { sample } else { 0.0 };
            peak = peak.max(finite.abs());
            square_sum += f64::from(finite) * f64::from(finite);
        }
        let rms = if samples.is_empty() {
            0.0
        } else {
            (square_sum / samples.len() as f64).sqrt() as f32
        };
        let active = if self.open {
            rms >= threshold * GATE_CLOSE_RATIO || peak >= threshold * GATE_PEAK_HOLD_RATIO
        } else {
            rms >= threshold || peak >= threshold * GATE_PEAK_OPEN_RATIO
        };

        if active {
            let decision = if self.open {
                GateDecision::Held
            } else {
                GateDecision::Opened
            };
            self.open = true;
            self.hangover_frames = GATE_HANGOVER_FRAMES;
            return decision;
        }
        if self.open && self.hangover_frames > 0 {
            self.hangover_frames -= 1;
            return GateDecision::Held;
        }
        if self.open {
            self.open = false;
            let sample_count = samples.len().max(1) as f32;
            for (index, sample) in samples.iter_mut().enumerate() {
                let scale = 1.0 - (index + 1) as f32 / sample_count;
                *sample *= scale;
            }
            return GateDecision::Closing;
        }

        samples.fill(0.0);
        GateDecision::Closed
    }

    pub fn reset(&mut self) {
        self.open = false;
        self.hangover_frames = 0;
    }
}

fn soft_limit(sample: f32) -> f32 {
    let magnitude = sample.abs();
    if magnitude <= LIMITER_KNEE {
        return sample;
    }
    // y = knee + x / (1 + x / headroom) joins the linear region with unit slope and
    // asymptotically approaches full scale. It is cheap enough for the encoder hot path and
    // introduces neither a discontinuity nor hard-clipped plateaus.
    let excess = magnitude - LIMITER_KNEE;
    let limited = LIMITER_KNEE + excess / (1.0 + excess / LIMITER_HEADROOM);
    sample.signum() * limited.min(1.0)
}

fn finite_or(value: f32, fallback: f32) -> f32 {
    if value.is_finite() {
        value
    } else {
        fallback
    }
}

pub struct SyntheticTone {
    phase: f32,
}

impl Default for SyntheticTone {
    fn default() -> Self {
        Self::new()
    }
}

impl SyntheticTone {
    pub fn new() -> Self {
        Self { phase: 0.0 }
    }

    pub fn fill_frame(
        &mut self,
        capture_ts_ns: u64,
        capture_callback_ts_ns: u64,
        capture_timestamp_valid: bool,
    ) -> AudioFrame {
        let step = std::f32::consts::TAU * SYNTHETIC_TONE_HZ / SAMPLE_RATE as f32;
        let mut samples = Vec::with_capacity(FRAME_SAMPLES);
        for _ in 0..FRAME_SAMPLES {
            samples.push(SYNTHETIC_TONE_AMPLITUDE * self.phase.sin());
            self.phase += step;
            if self.phase >= std::f32::consts::TAU {
                self.phase -= std::f32::consts::TAU;
            }
        }
        AudioFrame {
            encoder_epoch: 0,
            capture_generation: 0,
            capture_open_attempt: 0,
            capture_ts_ns,
            capture_callback_ts_ns,
            capture_timestamp_valid,
            samples,
        }
    }
}

pub struct LevelCadence {
    next_emit: Instant,
    peak: f32,
}

impl LevelCadence {
    pub fn new(now: Instant) -> Self {
        Self {
            next_emit: now + TELEMETRY_INTERVAL,
            peak: 0.0,
        }
    }

    pub fn observe(&mut self, now: Instant, peak: f32) -> Option<f32> {
        if peak.is_finite() {
            self.peak = self.peak.max(peak.clamp(0.0, 1.0));
        }
        if now < self.next_emit {
            return None;
        }
        while self.next_emit <= now {
            self.next_emit += TELEMETRY_INTERVAL;
        }
        let result = self.peak;
        self.peak = 0.0;
        Some(result)
    }
}

pub struct PeerLevelCadence {
    next_emit: Instant,
    peaks: HashMap<String, f32>,
}

impl PeerLevelCadence {
    pub fn new(now: Instant) -> Self {
        Self {
            next_emit: now + TELEMETRY_INTERVAL,
            peaks: HashMap::new(),
        }
    }

    pub fn observe(&mut self, peer_id: &str, peak: f32) {
        if !peak.is_finite() || peer_id.is_empty() || peer_id.len() > MAX_PEER_ID_BYTES {
            return;
        }
        if !self.peaks.contains_key(peer_id) && self.peaks.len() >= MAX_PEER_LEVELS {
            return;
        }
        self.peaks
            .entry(peer_id.to_string())
            .and_modify(|current| *current = current.max(peak.clamp(0.0, 1.0)))
            .or_insert_with(|| peak.clamp(0.0, 1.0));
    }

    pub fn remove(&mut self, peer_id: &str) {
        self.peaks.remove(peer_id);
    }

    pub fn take_due(&mut self, now: Instant) -> Option<Vec<PeerLevel>> {
        if now < self.next_emit {
            return None;
        }
        while self.next_emit <= now {
            self.next_emit += TELEMETRY_INTERVAL;
        }
        if self.peaks.is_empty() {
            return None;
        }
        let mut levels: Vec<PeerLevel> = self
            .peaks
            .drain()
            .map(|(peer_id, peak)| PeerLevel { peer_id, peak })
            .collect();
        levels.sort_unstable_by(|a, b| a.peer_id.cmp(&b.peer_id));
        Some(levels)
    }
}

#[derive(Default)]
struct LatestTelemetry {
    local_level: Option<(f32, bool)>,
    peer_levels: Option<Vec<PeerLevel>>,
}

/// Two-slot, latest-wins telemetry mailbox. Audio threads only use `try_lock`; a slow IPC or JNI
/// consumer therefore drops old meter updates instead of ever blocking capture or playout.
#[derive(Default)]
pub struct TelemetryMailbox {
    latest: Mutex<LatestTelemetry>,
}

impl TelemetryMailbox {
    pub fn publish_local(&self, peak: f32, speaking: bool) -> bool {
        match self.latest.try_lock() {
            Ok(mut latest) => {
                latest.local_level = Some((peak.clamp(0.0, 1.0), speaking));
                true
            }
            Err(TryLockError::WouldBlock) | Err(TryLockError::Poisoned(_)) => false,
        }
    }

    pub fn publish_peers(&self, mut levels: Vec<PeerLevel>) -> bool {
        levels.truncate(MAX_PEER_LEVELS);
        match self.latest.try_lock() {
            Ok(mut latest) => {
                latest.peer_levels = Some(levels);
                true
            }
            Err(TryLockError::WouldBlock) | Err(TryLockError::Poisoned(_)) => false,
        }
    }

    pub fn take_local(&self) -> Option<(f32, bool)> {
        self.latest.lock().ok()?.local_level.take()
    }

    pub fn take_peers(&self) -> Option<Vec<PeerLevel>> {
        self.latest.lock().ok()?.peer_levels.take()
    }

    pub fn clear(&self) {
        if let Ok(mut latest) = self.latest.lock() {
            *latest = LatestTelemetry::default();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn input_config_clamps_and_rejects_non_finite_values() {
        assert_eq!(
            InputConfig::sanitized(f32::NAN, f32::INFINITY),
            InputConfig::default()
        );
        assert_eq!(InputConfig::sanitized(-4.0, -1.0).gain, 0.0);
        assert_eq!(
            InputConfig::sanitized(99.0, 99.0),
            InputConfig {
                gain: MAX_INPUT_GAIN,
                vad_threshold: MAX_VAD_THRESHOLD,
                noise_gate_threshold: DEFAULT_NOISE_GATE_THRESHOLD,
            }
        );
    }

    #[test]
    fn gain_is_finite_soft_limited_and_does_not_gate_audio() {
        let cfg = InputConfig::sanitized(2.0, 0.1);
        let mut samples = [-0.75, 0.01, f32::NAN, f32::INFINITY];
        cfg.apply_gain(&mut samples);
        assert!((-0.99..-0.98).contains(&samples[0]), "{}", samples[0]);
        assert_eq!(samples[1..], [0.02, 0.0, 0.0]);
        assert_ne!(
            samples[1], 0.0,
            "VAD threshold must never gate the Opus input"
        );
    }

    #[test]
    fn gate_preserves_complete_onset_and_speech_tail_then_closes() {
        let mut gate = NoiseGate::default();
        let onset = vec![0.02f32; FRAME_SAMPLES];
        let mut first = onset.clone();
        assert_eq!(gate.process(&mut first, 0.01), GateDecision::Opened);
        assert_eq!(
            first, onset,
            "the frame that detects speech must remain intact"
        );

        for _ in 0..GATE_HANGOVER_FRAMES {
            let mut tail = vec![0.001f32; FRAME_SAMPLES];
            assert_eq!(gate.process(&mut tail, 0.01), GateDecision::Held);
            assert!(tail.iter().any(|sample| *sample != 0.0));
        }
        let mut closing = vec![0.001f32; FRAME_SAMPLES];
        assert_eq!(gate.process(&mut closing, 0.01), GateDecision::Closing);
        assert!(closing[0] > closing[FRAME_SAMPLES - 1]);
        let mut closed = vec![0.001f32; FRAME_SAMPLES];
        assert_eq!(gate.process(&mut closed, 0.01), GateDecision::Closed);
        assert!(closed.iter().all(|sample| *sample == 0.0));
    }

    #[test]
    fn zero_threshold_bypasses_gate() {
        let mut gate = NoiseGate::default();
        let mut samples = [0.000_01f32; 8];
        assert_eq!(gate.process(&mut samples, 0.0), GateDecision::Bypassed);
        assert!(samples.iter().all(|sample| *sample > 0.0));
    }

    #[test]
    fn limiter_is_continuous_monotonic_and_never_flat_tops() {
        let just_below = soft_limit(LIMITER_KNEE - 0.000_1);
        let at_knee = soft_limit(LIMITER_KNEE);
        let just_above = soft_limit(LIMITER_KNEE + 0.000_1);
        assert!(just_below < at_knee && at_knee < just_above);
        assert!((just_above - just_below) < 0.000_21);

        let over = [1.0, 1.2, 1.5, 2.0].map(soft_limit);
        assert!(over.windows(2).all(|pair| pair[0] < pair[1]));
        assert!(over.iter().all(|sample| *sample < 1.0));
    }

    #[test]
    fn synthetic_tone_matches_managed_frequency_and_level() {
        let mut tone = SyntheticTone::new();
        let frame = tone.fill_frame(7, 9, true);
        assert_eq!(frame.capture_ts_ns, 7);
        assert_eq!(frame.capture_callback_ts_ns, 9);
        assert!(frame.capture_timestamp_valid);
        assert_eq!(frame.samples.len(), FRAME_SAMPLES);
        let peak = frame.samples.iter().fold(0.0f32, |p, s| p.max(s.abs()));
        assert!((peak - SYNTHETIC_TONE_AMPLITUDE).abs() < 0.000_01);
        let positive_crossings = frame
            .samples
            .windows(2)
            .filter(|pair| pair[0] <= 0.0 && pair[1] > 0.0)
            .count();
        assert!((4..=5).contains(&positive_crossings));
    }

    #[test]
    fn local_level_cadence_is_one_hundred_ms_and_keeps_window_peak() {
        let start = Instant::now();
        let mut cadence = LevelCadence::new(start);
        for frame in 0..4 {
            assert_eq!(
                cadence.observe(
                    start + Duration::from_millis(frame * 20),
                    0.1 + frame as f32
                ),
                None
            );
        }
        assert_eq!(
            cadence.observe(start + Duration::from_millis(100), 0.5),
            Some(1.0)
        );
        assert_eq!(
            cadence.observe(start + Duration::from_millis(120), 0.2),
            None
        );
    }

    #[test]
    fn peer_levels_are_batched_capped_and_sorted() {
        let start = Instant::now();
        let mut cadence = PeerLevelCadence::new(start);
        cadence.observe("b", 0.2);
        cadence.observe("a", 0.1);
        cadence.observe("a", 0.7);
        cadence.observe(&"x".repeat(MAX_PEER_ID_BYTES + 1), 1.0);
        assert!(cadence
            .take_due(start + Duration::from_millis(99))
            .is_none());
        let batch = cadence
            .take_due(start + Duration::from_millis(100))
            .unwrap();
        assert_eq!(batch.len(), 2);
        assert_eq!(batch[0].peer_id, "a");
        assert_eq!(batch[0].peak, 0.7);
        assert_eq!(batch[1].peer_id, "b");
    }

    #[test]
    fn telemetry_mailbox_is_bounded_and_latest_wins() {
        let mailbox = TelemetryMailbox::default();
        assert!(mailbox.publish_local(0.1, false));
        assert!(mailbox.publish_local(0.8, true));
        assert_eq!(mailbox.take_local(), Some((0.8, true)));
        assert!(mailbox.take_local().is_none());

        let levels = (0..(MAX_PEER_LEVELS + 10))
            .map(|i| PeerLevel {
                peer_id: format!("p{i}"),
                peak: 0.5,
            })
            .collect();
        assert!(mailbox.publish_peers(levels));
        assert_eq!(mailbox.take_peers().unwrap().len(), MAX_PEER_LEVELS);
    }
}
