use crate::codec::SAMPLE_RATE;

const BLOCK_SAMPLES: usize = SAMPLE_RATE as usize / 10; // 100 ms.
const HISTORY_BLOCKS: usize = 50; // Five seconds at 10 Hz.
const MIN_ACTIVE_BLOCKS: usize = 4;
const STABLE_HISTORY_BLOCKS: usize = 30;
const TARGET_SPEECH_DB: f32 = -23.0;
const ABSOLUTE_SPEECH_GATE_DB: f32 = -55.0;
const RELATIVE_SPEECH_GATE_DB: f32 = 10.0;
const NORMALIZER_DEADBAND_DB: f32 = 2.0;
const MIN_NORMALIZER_GAIN_DB: f32 = -18.0;
const ORDINARY_MAKEUP_LIMIT_DB: f32 = 6.0;
const CONFIDENT_MAKEUP_LIMIT_DB: f32 = 12.0;
const MAKEUP_MIN_SNR_DB: f32 = 15.0;
const CONFIDENT_MAKEUP_MIN_SNR_DB: f32 = 20.0;
const MAX_OUTPUT_NOISE_DB: f32 = -45.0;
const LEVEL_ATTACK_SECONDS: f32 = 0.15;
const LEVEL_RELEASE_SECONDS: f32 = 4.0;
const OVERLOAD_THRESHOLD_DB: f32 = TARGET_SPEECH_DB + 10.0;
const OVERLOAD_ATTACK_SECONDS: f32 = 0.03;
const OVERLOAD_RELEASE_SECONDS: f32 = 1.5;
const PEER_PEAK_CEILING: f32 = 0.707_945_76; // -3 dBFS.
const PEER_PEAK_ATTACK_SECONDS: f32 = 0.005;
const PEER_PEAK_RELEASE_SECONDS: f32 = 1.0;
const DB_FLOOR: f32 = -120.0;

pub(crate) const LIMITER_LOOKAHEAD_FRAMES: usize = SAMPLE_RATE as usize / 200; // 5 ms.
pub(crate) const MIX_LIMITER_CEILING: f32 = 0.891_250_9; // -1 dBFS.
const MIX_LIMITER_ATTACK_SECONDS: f32 = 0.000_5;
const MIX_LIMITER_RELEASE_SECONDS: f32 = 0.1;

#[derive(Clone, Copy)]
struct FixedBiquad {
    b0: f32,
    b1: f32,
    b2: f32,
    a1: f32,
    a2: f32,
    z1: f32,
    z2: f32,
}

impl FixedBiquad {
    const fn new(b0: f32, b1: f32, b2: f32, a1: f32, a2: f32) -> Self {
        Self {
            b0,
            b1,
            b2,
            a1,
            a2,
            z1: 0.0,
            z2: 0.0,
        }
    }

    fn process(&mut self, input: f32) -> f32 {
        let output = self.b0 * input + self.z1;
        self.z1 = self.b1 * input - self.a1 * output + self.z2;
        self.z2 = self.b2 * input - self.a2 * output;
        output
    }
}

struct KWeighting {
    // ITU-R BS.1770 coefficients for 48 kHz.
    shelf: FixedBiquad,
    high_pass: FixedBiquad,
}

impl KWeighting {
    fn new() -> Self {
        Self {
            shelf: FixedBiquad::new(
                1.535_124_9,
                -2.691_696_2,
                1.198_392_9,
                -1.690_659_3,
                0.732_480_76,
            ),
            high_pass: FixedBiquad::new(1.0, -2.0, 1.0, -1.990_047_5, 0.990_072_25),
        }
    }

    fn process(&mut self, sample: f32) -> f32 {
        let shelf = self.shelf.process(sample);
        self.high_pass.process(shelf)
    }
}

struct LoudnessHistory {
    levels_db: [f32; HISTORY_BLOCKS],
    speech_like: [bool; HISTORY_BLOCKS],
    count: usize,
    next: usize,
}

impl LoudnessHistory {
    fn new() -> Self {
        Self {
            levels_db: [DB_FLOOR; HISTORY_BLOCKS],
            speech_like: [false; HISTORY_BLOCKS],
            count: 0,
            next: 0,
        }
    }

    fn push(&mut self, level_db: f32, speech_like: bool) {
        self.levels_db[self.next] = level_db.clamp(DB_FLOOR, 6.0);
        self.speech_like[self.next] = speech_like;
        self.next = (self.next + 1) % HISTORY_BLOCKS;
        self.count = (self.count + 1).min(HISTORY_BLOCKS);
    }

    fn percentile_all(&self, percentile: f32) -> Option<f32> {
        let mut scratch = [0.0f32; HISTORY_BLOCKS];
        scratch[..self.count].copy_from_slice(&self.levels_db[..self.count]);
        percentile_of(&mut scratch, self.count, percentile)
    }

    fn percentile_speech_above(&self, threshold_db: f32, percentile: f32) -> (Option<f32>, usize) {
        let mut scratch = [0.0f32; HISTORY_BLOCKS];
        let mut count = 0;
        for index in 0..self.count {
            if self.speech_like[index] && self.levels_db[index] >= threshold_db {
                scratch[count] = self.levels_db[index];
                count += 1;
            }
        }
        (percentile_of(&mut scratch, count, percentile), count)
    }
}

fn percentile_of(values: &mut [f32; HISTORY_BLOCKS], count: usize, percentile: f32) -> Option<f32> {
    if count == 0 {
        return None;
    }
    values[..count].sort_unstable_by(f32::total_cmp);
    let position = ((count - 1) as f32 * percentile.clamp(0.0, 1.0)).round() as usize;
    Some(values[position.min(count - 1)])
}

#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct GainRamp {
    pub start: f32,
    pub end: f32,
}

impl GainRamp {
    pub fn at(self, sample: usize, samples: usize) -> f32 {
        if samples <= 1 {
            return self.end;
        }
        let progress = sample as f32 / (samples - 1) as f32;
        self.start + (self.end - self.start) * progress
    }
}

#[derive(Debug, Clone, Copy)]
pub(crate) struct PeerLoudnessSnapshot {
    pub gain_db: f32,
    pub active_level_db: f32,
    pub noise_level_db: f32,
    pub clipping_confidence: f32,
    pub overload_active: bool,
    pub peak_limiter_reduction_db: f32,
}

impl Default for PeerLoudnessSnapshot {
    fn default() -> Self {
        Self {
            gain_db: 0.0,
            active_level_db: DB_FLOOR,
            noise_level_db: DB_FLOOR,
            clipping_confidence: 0.0,
            overload_active: false,
            peak_limiter_reduction_db: 0.0,
        }
    }
}

pub(crate) struct PeerLoudnessNormalizer {
    k_weighting: KWeighting,
    history: LoudnessHistory,
    block_weighted_square_sum: f64,
    block_raw_square_sum: f64,
    block_correlation_sum: f64,
    block_peak: f32,
    block_near_rail: usize,
    block_flat_rail: usize,
    block_zero_crossings: usize,
    block_samples: usize,
    previous_raw: f32,
    normalizer_gain_db: f32,
    desired_normalizer_gain_db: f32,
    overload_gain_db: f32,
    desired_overload_gain_db: f32,
    peer_peak_gain: f32,
    active_level_db: f32,
    noise_level_db: f32,
    clipping_confidence: f32,
    overload_active: bool,
}

impl Default for PeerLoudnessNormalizer {
    fn default() -> Self {
        Self::new()
    }
}

impl PeerLoudnessNormalizer {
    pub fn new() -> Self {
        Self {
            k_weighting: KWeighting::new(),
            history: LoudnessHistory::new(),
            block_weighted_square_sum: 0.0,
            block_raw_square_sum: 0.0,
            block_correlation_sum: 0.0,
            block_peak: 0.0,
            block_near_rail: 0,
            block_flat_rail: 0,
            block_zero_crossings: 0,
            block_samples: 0,
            previous_raw: 0.0,
            normalizer_gain_db: 0.0,
            desired_normalizer_gain_db: 0.0,
            overload_gain_db: 0.0,
            desired_overload_gain_db: 0.0,
            peer_peak_gain: 1.0,
            active_level_db: DB_FLOOR,
            noise_level_db: DB_FLOOR,
            clipping_confidence: 0.0,
            overload_active: false,
        }
    }

    #[cfg(test)]
    pub fn process_frame(&mut self, samples: &[f32]) -> GainRamp {
        self.process_frame_with_measurement(samples, true)
    }

    pub fn process_frame_with_measurement(
        &mut self,
        samples: &[f32],
        measurement_eligible: bool,
    ) -> GainRamp {
        let start_gain = self.combined_gain();
        let mut frame_peak = 0.0f32;

        for &sample in samples {
            let sample = finite(sample);
            frame_peak = frame_peak.max(sample.abs());
            if measurement_eligible {
                self.observe_sample(sample);
            } else {
                // Preserve the weighting filter's timeline without allowing DRED/FEC/PLC or a
                // locally synthesized fade to train speech loudness, noise, or clipping evidence.
                let _ = self.k_weighting.process(sample);
                self.previous_raw = sample;
            }
        }

        let duration = samples.len() as f32 / SAMPLE_RATE as f32;
        if measurement_eligible {
            self.normalizer_gain_db = smooth(
                self.normalizer_gain_db,
                self.desired_normalizer_gain_db,
                if self.desired_normalizer_gain_db < self.normalizer_gain_db {
                    LEVEL_ATTACK_SECONDS
                } else {
                    LEVEL_RELEASE_SECONDS
                },
                duration,
            );
            self.overload_gain_db = smooth(
                self.overload_gain_db,
                self.desired_overload_gain_db,
                if self.desired_overload_gain_db < self.overload_gain_db {
                    OVERLOAD_ATTACK_SECONDS
                } else {
                    OVERLOAD_RELEASE_SECONDS
                },
                duration,
            );
        }

        // Peak safety still observes concealed playback: it may not train the learned level, but
        // it must not be allowed to overload the listener's final mix.
        let level_gain = db_to_gain(
            (self.normalizer_gain_db + self.overload_gain_db)
                .clamp(MIN_NORMALIZER_GAIN_DB, CONFIDENT_MAKEUP_LIMIT_DB),
        );
        let peak_after_leveling = frame_peak * level_gain;
        let desired_peak_gain = if peak_after_leveling > PEER_PEAK_CEILING {
            PEER_PEAK_CEILING / peak_after_leveling
        } else {
            1.0
        };
        self.peer_peak_gain = smooth(
            self.peer_peak_gain,
            desired_peak_gain,
            if desired_peak_gain < self.peer_peak_gain {
                PEER_PEAK_ATTACK_SECONDS
            } else {
                PEER_PEAK_RELEASE_SECONDS
            },
            duration,
        )
        .clamp(0.0, 1.0);

        GainRamp {
            start: start_gain,
            end: self.combined_gain(),
        }
    }

    pub fn snapshot(&self) -> PeerLoudnessSnapshot {
        let peak_limiter_reduction_db = gain_to_db(self.peer_peak_gain).min(0.0);
        PeerLoudnessSnapshot {
            gain_db: gain_to_db(self.combined_gain()),
            active_level_db: self.active_level_db,
            noise_level_db: self.noise_level_db,
            clipping_confidence: self.clipping_confidence,
            overload_active: self.overload_active,
            peak_limiter_reduction_db,
        }
    }

    fn combined_gain(&self) -> f32 {
        let level_db = (self.normalizer_gain_db + self.overload_gain_db)
            .clamp(MIN_NORMALIZER_GAIN_DB, CONFIDENT_MAKEUP_LIMIT_DB);
        (db_to_gain(level_db) * self.peer_peak_gain).clamp(0.0, 4.0)
    }

    fn observe_sample(&mut self, sample: f32) {
        let weighted = finite(self.k_weighting.process(sample));
        self.block_weighted_square_sum += f64::from(weighted) * f64::from(weighted);
        self.block_raw_square_sum += f64::from(sample) * f64::from(sample);
        self.block_correlation_sum += f64::from(self.previous_raw) * f64::from(sample);
        self.block_peak = self.block_peak.max(sample.abs());

        let near_rail = sample.abs() >= 0.98;
        if near_rail {
            self.block_near_rail += 1;
            if self.previous_raw.abs() >= 0.98
                && sample.signum() == self.previous_raw.signum()
                && (sample - self.previous_raw).abs() <= 0.000_1
            {
                self.block_flat_rail += 1;
            }
        }
        if sample != 0.0
            && self.previous_raw != 0.0
            && sample.is_sign_positive() != self.previous_raw.is_sign_positive()
        {
            self.block_zero_crossings += 1;
        }
        self.previous_raw = sample;
        self.block_samples += 1;

        if self.block_samples >= BLOCK_SAMPLES {
            self.finish_block();
        }
    }

    fn finish_block(&mut self) {
        let samples = self.block_samples.max(1) as f64;
        let weighted_mean_square = self.block_weighted_square_sum / samples;
        let raw_mean_square = self.block_raw_square_sum / samples;
        let level_db = mean_square_to_db(weighted_mean_square);
        let raw_rms = raw_mean_square.sqrt() as f32;
        let crest_db = if raw_rms > 0.0 {
            20.0 * (self.block_peak / raw_rms).max(1.0).log10()
        } else {
            0.0
        };
        let correlation = if self.block_raw_square_sum > 1.0e-12 {
            (self.block_correlation_sum / self.block_raw_square_sum) as f32
        } else {
            0.0
        };
        let zero_cross_rate = self.block_zero_crossings as f32 / self.block_samples.max(2) as f32;
        let speech_like = level_db >= ABSOLUTE_SPEECH_GATE_DB
            && correlation > 0.05
            && zero_cross_rate < 0.35
            && (2.0..=30.0).contains(&crest_db);

        let near_rail_ratio = self.block_near_rail as f32 / self.block_samples.max(1) as f32;
        let flat_rail_ratio = self.block_flat_rail as f32 / self.block_samples.max(1) as f32;
        let clipping_evidence = near_rail_ratio >= 0.01 && flat_rail_ratio >= 0.000_5;
        if clipping_evidence {
            self.clipping_confidence += (1.0 - self.clipping_confidence) * 0.25;
        } else {
            self.clipping_confidence *= 0.98;
        }

        self.history.push(level_db, speech_like);
        self.update_targets(level_db);

        self.block_weighted_square_sum = 0.0;
        self.block_raw_square_sum = 0.0;
        self.block_correlation_sum = 0.0;
        self.block_peak = 0.0;
        self.block_near_rail = 0;
        self.block_flat_rail = 0;
        self.block_zero_crossings = 0;
        self.block_samples = 0;
    }

    fn update_targets(&mut self, latest_level_db: f32) {
        self.noise_level_db = self.history.percentile_all(0.20).unwrap_or(DB_FLOOR);
        let active_gate =
            ABSOLUTE_SPEECH_GATE_DB.max(self.noise_level_db + RELATIVE_SPEECH_GATE_DB);
        let (active_level, active_blocks) = self.history.percentile_speech_above(active_gate, 0.65);

        if let Some(active_level_db) = active_level.filter(|_| active_blocks >= MIN_ACTIVE_BLOCKS) {
            self.active_level_db = active_level_db;
            let mut desired = TARGET_SPEECH_DB - active_level_db;
            if desired.abs() <= NORMALIZER_DEADBAND_DB {
                desired = 0.0;
            }
            if desired > 0.0 {
                let snr_db = active_level_db - self.noise_level_db;
                let confident = self.history.count >= STABLE_HISTORY_BLOCKS
                    && active_blocks >= STABLE_HISTORY_BLOCKS / 2
                    && snr_db >= CONFIDENT_MAKEUP_MIN_SNR_DB;
                let makeup_limit = if confident {
                    CONFIDENT_MAKEUP_LIMIT_DB
                } else {
                    ORDINARY_MAKEUP_LIMIT_DB
                };
                let noise_limited_gain = (MAX_OUTPUT_NOISE_DB - self.noise_level_db).max(0.0);
                if snr_db < MAKEUP_MIN_SNR_DB || self.clipping_confidence >= 0.5 {
                    desired = 0.0;
                } else {
                    desired = desired.min(makeup_limit).min(noise_limited_gain);
                }
            }
            self.desired_normalizer_gain_db =
                desired.clamp(MIN_NORMALIZER_GAIN_DB, CONFIDENT_MAKEUP_LIMIT_DB);
        }

        self.overload_active = latest_level_db >= OVERLOAD_THRESHOLD_DB;
        self.desired_overload_gain_db = if self.overload_active {
            (TARGET_SPEECH_DB - latest_level_db).clamp(MIN_NORMALIZER_GAIN_DB, 0.0)
        } else {
            0.0
        };
    }
}

#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct MixLimiterSnapshot {
    pub gain_reduction_db: f32,
    pub detected_peak: f32,
    pub limited_samples: u64,
    pub reduction_events: u64,
}

pub(crate) struct LookaheadLimiter {
    delay: Vec<(f32, f32)>,
    max_peaks: Vec<f32>,
    max_indices: Vec<u64>,
    max_head: usize,
    max_len: usize,
    sample_index: u64,
    write: usize,
    gain: f32,
    previous_left: [f32; 3],
    previous_right: [f32; 3],
    nonzero_slots: usize,
    limited_samples: u64,
    reduction_events: u64,
    was_reducing: bool,
    detected_peak: f32,
}

impl Default for LookaheadLimiter {
    fn default() -> Self {
        Self::new()
    }
}

impl LookaheadLimiter {
    pub fn new() -> Self {
        Self {
            delay: vec![(0.0, 0.0); LIMITER_LOOKAHEAD_FRAMES],
            max_peaks: vec![0.0; LIMITER_LOOKAHEAD_FRAMES + 1],
            max_indices: vec![0; LIMITER_LOOKAHEAD_FRAMES + 1],
            max_head: 0,
            max_len: 0,
            sample_index: 0,
            write: 0,
            gain: 1.0,
            previous_left: [0.0; 3],
            previous_right: [0.0; 3],
            nonzero_slots: 0,
            limited_samples: 0,
            reduction_events: 0,
            was_reducing: false,
            detected_peak: 0.0,
        }
    }

    pub fn reset(&mut self) {
        self.delay.fill((0.0, 0.0));
        self.max_peaks.fill(0.0);
        self.max_indices.fill(0);
        self.max_head = 0;
        self.max_len = 0;
        self.sample_index = 0;
        self.write = 0;
        self.gain = 1.0;
        self.previous_left = [0.0; 3];
        self.previous_right = [0.0; 3];
        self.nonzero_slots = 0;
        self.was_reducing = false;
        self.detected_peak = 0.0;
    }

    pub fn has_pending_audio(&self) -> bool {
        self.nonzero_slots > 0
    }

    pub fn process(&mut self, stereo: &mut [f32]) {
        let frames = stereo.len() / 2;
        for frame in 0..frames {
            let input = (finite(stereo[2 * frame]), finite(stereo[2 * frame + 1]));
            let delayed = self.delay[self.write];
            let old_nonzero = pair_nonzero(delayed);
            let new_nonzero = pair_nonzero(input);
            if old_nonzero && !new_nonzero {
                self.nonzero_slots = self.nonzero_slots.saturating_sub(1);
            } else if !old_nonzero && new_nonzero {
                self.nonzero_slots += 1;
            }

            self.delay[self.write] = input;
            let inserted_peak = self.estimate_inserted_peak(input.0, input.1);
            self.write = (self.write + 1) % self.delay.len();

            let detected_peak = self.observe_peak(inserted_peak);
            self.detected_peak = detected_peak;
            let desired_gain = if detected_peak > MIX_LIMITER_CEILING {
                MIX_LIMITER_CEILING / detected_peak
            } else {
                1.0
            };
            let time_constant = if desired_gain < self.gain {
                MIX_LIMITER_ATTACK_SECONDS
            } else {
                MIX_LIMITER_RELEASE_SECONDS
            };
            self.gain = smooth(
                self.gain,
                desired_gain,
                time_constant,
                1.0 / SAMPLE_RATE as f32,
            )
            .clamp(0.0, 1.0);

            let reducing = self.gain < 0.999_9;
            if reducing {
                self.limited_samples = self.limited_samples.saturating_add(1);
            }
            if reducing && !self.was_reducing {
                self.reduction_events = self.reduction_events.saturating_add(1);
            }
            self.was_reducing = reducing;

            stereo[2 * frame] = (delayed.0 * self.gain).clamp(-1.0, 1.0);
            stereo[2 * frame + 1] = (delayed.1 * self.gain).clamp(-1.0, 1.0);
        }
        if !stereo.len().is_multiple_of(2) {
            stereo[stereo.len() - 1] = 0.0;
        }
    }

    pub fn snapshot(&self) -> MixLimiterSnapshot {
        MixLimiterSnapshot {
            gain_reduction_db: gain_to_db(self.gain).min(0.0),
            detected_peak: self.detected_peak,
            limited_samples: self.limited_samples,
            reduction_events: self.reduction_events,
        }
    }

    fn observe_peak(&mut self, peak: f32) -> f32 {
        let capacity = self.max_peaks.len();
        while self.max_len > 0 {
            let back = (self.max_head + self.max_len - 1) % capacity;
            if self.max_peaks[back] > peak {
                break;
            }
            self.max_len -= 1;
        }

        let tail = (self.max_head + self.max_len) % capacity;
        self.max_peaks[tail] = peak;
        self.max_indices[tail] = self.sample_index;
        self.max_len += 1;

        let oldest = self
            .sample_index
            .saturating_sub(LIMITER_LOOKAHEAD_FRAMES as u64);
        while self.max_len > 0 && self.max_indices[self.max_head] < oldest {
            self.max_head = (self.max_head + 1) % capacity;
            self.max_len -= 1;
        }
        self.sample_index = self.sample_index.saturating_add(1);
        self.max_peaks[self.max_head]
    }

    fn estimate_inserted_peak(&mut self, left: f32, right: f32) -> f32 {
        let left_peak = cubic_segment_peak(
            self.previous_left[0],
            self.previous_left[1],
            self.previous_left[2],
            left,
        );
        let right_peak = cubic_segment_peak(
            self.previous_right[0],
            self.previous_right[1],
            self.previous_right[2],
            right,
        );
        self.previous_left = [self.previous_left[1], self.previous_left[2], left];
        self.previous_right = [self.previous_right[1], self.previous_right[2], right];
        left.abs().max(right.abs()).max(left_peak).max(right_peak)
    }
}

fn cubic_segment_peak(y0: f32, y1: f32, y2: f32, y3: f32) -> f32 {
    let mut peak = y1.abs().max(y2.abs());
    for t in [0.25f32, 0.5, 0.75] {
        let t2 = t * t;
        let t3 = t2 * t;
        let sample = 0.5
            * (2.0 * y1
                + (-y0 + y2) * t
                + (2.0 * y0 - 5.0 * y1 + 4.0 * y2 - y3) * t2
                + (-y0 + 3.0 * y1 - 3.0 * y2 + y3) * t3);
        peak = peak.max(sample.abs());
    }
    finite(peak)
}

fn smooth(current: f32, target: f32, seconds: f32, duration: f32) -> f32 {
    if !current.is_finite() || !target.is_finite() {
        return target;
    }
    if seconds <= 0.0 || duration <= 0.0 {
        return target;
    }
    let alpha = 1.0 - (-duration / seconds).exp();
    current + (target - current) * alpha.clamp(0.0, 1.0)
}

fn mean_square_to_db(mean_square: f64) -> f32 {
    if !mean_square.is_finite() || mean_square <= 1.0e-12 {
        DB_FLOOR
    } else {
        (10.0 * mean_square.log10()) as f32
    }
}

fn db_to_gain(db: f32) -> f32 {
    10.0f32.powf(db / 20.0)
}

fn gain_to_db(gain: f32) -> f32 {
    if gain <= 1.0e-6 || !gain.is_finite() {
        DB_FLOOR
    } else {
        20.0 * gain.log10()
    }
}

fn finite(sample: f32) -> f32 {
    if sample.is_finite() {
        sample
    } else {
        0.0
    }
}

fn pair_nonzero(pair: (f32, f32)) -> bool {
    pair.0.abs() > 1.0e-9 || pair.1.abs() > 1.0e-9
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::codec::FRAME_SIZE;

    fn feed_tone(
        normalizer: &mut PeerLoudnessNormalizer,
        amplitude: f32,
        frames: usize,
        start_sample: &mut usize,
    ) {
        let mut frame = [0.0f32; FRAME_SIZE];
        for _ in 0..frames {
            for (offset, sample) in frame.iter_mut().enumerate() {
                let phase = std::f32::consts::TAU * 1_000.0 * (*start_sample + offset) as f32
                    / SAMPLE_RATE as f32;
                *sample = amplitude * phase.sin();
            }
            normalizer.process_frame(&frame);
            *start_sample += FRAME_SIZE;
        }
    }

    #[test]
    fn loud_peer_is_attenuated_without_changing_normal_peer_gain() {
        let mut loud = PeerLoudnessNormalizer::new();
        let mut normal = PeerLoudnessNormalizer::new();
        let mut loud_sample = 0;
        let mut normal_sample = 0;

        feed_tone(&mut loud, 0.8, 100, &mut loud_sample);
        feed_tone(&mut normal, 0.1, 100, &mut normal_sample);

        assert!(
            loud.snapshot().gain_db < -10.0,
            "sustained loud speech must receive substantial per-peer attenuation"
        );
        assert!(
            normal.snapshot().gain_db.abs() < 1.0,
            "one loud peer must not change another peer's normal gain"
        );
    }

    #[test]
    fn quiet_clean_speech_receives_slow_bounded_makeup() {
        let mut normalizer = PeerLoudnessNormalizer::new();
        let silence = [0.0f32; FRAME_SIZE];
        let mut sample_index = 0;

        // Alternate exact 100 ms speech and silence blocks so the independent noise estimate has
        // a clean floor while the speech history accumulates enough evidence for bounded makeup.
        for block in 0..60 {
            if block % 2 == 0 {
                feed_tone(&mut normalizer, 0.01, 5, &mut sample_index);
            } else {
                for _ in 0..5 {
                    normalizer.process_frame(&silence);
                    sample_index += FRAME_SIZE;
                }
            }
        }

        let snapshot = normalizer.snapshot();
        assert!(
            snapshot.gain_db > 3.0,
            "quiet high-SNR speech should be raised gradually: {snapshot:?}"
        );
        assert!(snapshot.gain_db <= CONFIDENT_MAKEUP_LIMIT_DB + 0.01);
    }

    #[test]
    fn noise_like_input_is_not_blindly_boosted() {
        let mut normalizer = PeerLoudnessNormalizer::new();
        let mut frame = [0.0f32; FRAME_SIZE];
        let mut random = 0x1234_5678u32;
        for _ in 0..300 {
            for sample in &mut frame {
                random = random.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
                *sample = ((random >> 8) as f32 / 8_388_607.5 - 1.0) * 0.01;
            }
            normalizer.process_frame(&frame);
        }

        assert!(
            normalizer.snapshot().gain_db <= 0.1,
            "unqualified noise must not receive automatic makeup"
        );
    }

    #[test]
    fn sustained_overload_reduces_gain_quickly_but_never_mutes() {
        let mut normalizer = PeerLoudnessNormalizer::new();
        let mut sample_index = 0;
        feed_tone(&mut normalizer, 1.0, 10, &mut sample_index);

        let snapshot = normalizer.snapshot();
        assert!(snapshot.overload_active);
        assert!(
            snapshot.gain_db < -6.0,
            "100-200 ms overload protection must react before the slow leveler: {snapshot:?}"
        );
        assert!(
            snapshot.gain_db > -30.0,
            "protection may attenuate a hot participant but must never mute them"
        );
    }

    #[test]
    fn repeated_flat_rails_raise_diagnostic_clipping_confidence_only() {
        let mut normalizer = PeerLoudnessNormalizer::new();
        let clipped = [1.0f32; FRAME_SIZE];
        for _ in 0..20 {
            normalizer.process_frame(&clipped);
        }

        let snapshot = normalizer.snapshot();
        assert!(snapshot.clipping_confidence >= 0.5);
        assert!(snapshot.gain_db.is_finite());
        assert!(snapshot.gain_db > -30.0);
    }

    #[test]
    fn concealed_audio_does_not_train_loudness_or_clipping_state() {
        let mut normalizer = PeerLoudnessNormalizer::new();
        let mut sample_index = 0;
        feed_tone(&mut normalizer, 0.1, 50, &mut sample_index);
        let before = normalizer.snapshot();
        let before_normalizer_gain = normalizer.normalizer_gain_db;
        let before_overload_gain = normalizer.overload_gain_db;
        let concealed = [1.0f32; FRAME_SIZE];

        for _ in 0..50 {
            normalizer.process_frame_with_measurement(&concealed, false);
        }

        let after = normalizer.snapshot();
        assert_eq!(normalizer.normalizer_gain_db, before_normalizer_gain);
        assert_eq!(normalizer.overload_gain_db, before_overload_gain);
        assert_eq!(after.active_level_db, before.active_level_db);
        assert_eq!(after.noise_level_db, before.noise_level_db);
        assert_eq!(after.clipping_confidence, before.clipping_confidence);
        assert!(!after.overload_active);
    }

    #[test]
    fn linked_limiter_delays_bounds_and_preserves_stereo_ratio() {
        let mut limiter = LookaheadLimiter::new();
        let mut stereo = vec![0.0f32; FRAME_SIZE * 2];
        let impulse = FRAME_SIZE - 60;
        stereo[2 * impulse] = 1.5;
        stereo[2 * impulse + 1] = 0.75;

        limiter.process(&mut stereo);
        assert!(stereo.iter().all(|sample| sample.is_finite()));
        assert!(
            stereo.iter().all(|sample| sample.abs() < 1.0e-8),
            "an impulse inside the lookahead tail must not play early"
        );
        assert!(limiter.has_pending_audio());

        let mut flush = vec![0.0f32; FRAME_SIZE * 2];
        limiter.process(&mut flush);
        let delayed = impulse + LIMITER_LOOKAHEAD_FRAMES - FRAME_SIZE;
        let left = flush[2 * delayed];
        let right = flush[2 * delayed + 1];
        assert!(left.abs() <= MIX_LIMITER_CEILING + 0.001);
        assert!(right.abs() <= MIX_LIMITER_CEILING + 0.001);
        assert!((left / right - 2.0).abs() < 1.0e-4);
        assert!(!limiter.has_pending_audio());

        let snapshot = limiter.snapshot();
        assert!(snapshot.limited_samples > 0);
        assert_eq!(snapshot.reduction_events, 1);
    }

    #[test]
    fn limiter_sanitizes_nonfinite_input() {
        let mut limiter = LookaheadLimiter::new();
        let mut stereo = vec![0.0f32; FRAME_SIZE * 2];
        stereo[0] = f32::NAN;
        stereo[1] = f32::INFINITY;
        stereo[2] = f32::NEG_INFINITY;
        limiter.process(&mut stereo);
        limiter.process(&mut stereo);
        assert!(stereo.iter().all(|sample| sample.is_finite()));
    }
}
