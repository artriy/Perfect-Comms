use crate::proto::{AudioFrame, PeerLevel, FRAME_SAMPLES, SAMPLE_RATE};
use std::collections::HashMap;
use std::sync::{Mutex, TryLockError};
use std::time::{Duration, Instant};

pub const DEFAULT_INPUT_GAIN: f32 = 1.0;
pub const MAX_INPUT_GAIN: f32 = 2.0;
pub const DEFAULT_VAD_THRESHOLD: f32 = 0.004;
pub const MIN_VAD_THRESHOLD: f32 = 0.0001;
pub const MAX_VAD_THRESHOLD: f32 = 1.0;
pub const SYNTHETIC_TONE_HZ: f32 = 220.0;
pub const SYNTHETIC_TONE_AMPLITUDE: f32 = 0.012;
pub const TELEMETRY_INTERVAL: Duration = Duration::from_millis(100);
pub const MAX_PEER_LEVELS: usize = 32;
pub const MAX_PEER_ID_BYTES: usize = 32;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct InputConfig {
    pub gain: f32,
    pub vad_threshold: f32,
}

impl Default for InputConfig {
    fn default() -> Self {
        Self {
            gain: DEFAULT_INPUT_GAIN,
            vad_threshold: DEFAULT_VAD_THRESHOLD,
        }
    }
}

impl InputConfig {
    pub fn sanitized(gain: f32, vad_threshold: f32) -> Self {
        let gain = finite_or(gain, DEFAULT_INPUT_GAIN).clamp(0.0, MAX_INPUT_GAIN);
        let vad_threshold = finite_or(vad_threshold, DEFAULT_VAD_THRESHOLD)
            .clamp(MIN_VAD_THRESHOLD, MAX_VAD_THRESHOLD);
        Self {
            gain,
            vad_threshold,
        }
    }

    /// Applies user input gain immediately before Opus. Invalid input samples are made silent so
    /// NaN/Inf can never poison the encoder or level telemetry.
    pub fn apply_gain(&self, samples: &mut [f32]) {
        for sample in samples {
            let value = if sample.is_finite() { *sample } else { 0.0 };
            *sample = (value * self.gain).clamp(-1.0, 1.0);
        }
    }
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

    pub fn fill_frame(&mut self, capture_ts_ns: u64) -> AudioFrame {
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
            capture_ts_ns,
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
            }
        );
    }

    #[test]
    fn gain_is_finite_clamped_and_does_not_gate_audio() {
        let cfg = InputConfig::sanitized(2.0, 0.1);
        let mut samples = [-0.75, 0.01, f32::NAN, f32::INFINITY];
        cfg.apply_gain(&mut samples);
        assert_eq!(samples, [-1.0, 0.02, 0.0, 0.0]);
        assert_ne!(
            samples[1], 0.0,
            "VAD threshold must never gate the Opus input"
        );
    }

    #[test]
    fn synthetic_tone_matches_managed_frequency_and_level() {
        let mut tone = SyntheticTone::new();
        let frame = tone.fill_frame(7);
        assert_eq!(frame.capture_ts_ns, 7);
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
