use crate::diagnostics::{
    send_media_state, CaptureClockStatus, CaptureDiagnostics, MediaStateEvent, PlaybackDiagnostics,
    StreamDescriptor,
};
use crate::proto::{
    AudioFrame, CaptureFrameMetadata, CaptureFrameProducer, DeviceInfo, PlaybackRing,
    FRAME_SAMPLES, SAMPLE_RATE,
};
use crate::rtc::NativeCounters;
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::SyncSender;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub use crate::input::SyntheticTone as ToneSource;
pub use crate::input::SYNTHETIC_TONE_HZ as TONE_HZ;

const DOWNMIX_SWITCH_RATIO: f64 = 1.6;
const CORRELATED_STEREO_THRESHOLD: f64 = 0.55;

/// Stateful microphone downmixer. Many USB headsets expose a nominal stereo stream with speech
/// on only one side; averaging that stream loses 6 dB. Other devices expose inverted channels,
/// where averaging can cancel speech almost completely. Correlated, balanced stereo is still
/// averaged normally, while asymmetric or decorrelated input follows a dominant channel with
/// hysteresis so small block-to-block energy changes cannot make the source flutter left/right.
pub struct AdaptiveDownmixer {
    channels: usize,
    dominant_channel: usize,
    scratch: Vec<f32>,
    energy: Vec<f64>,
}

impl AdaptiveDownmixer {
    pub fn new(channels: usize) -> Self {
        Self::with_capacity(channels, 0)
    }

    fn with_capacity(channels: usize, frames: usize) -> Self {
        let channels = channels.max(1);
        Self {
            channels,
            dominant_channel: 0,
            scratch: Vec::with_capacity(frames),
            energy: vec![0.0; channels],
        }
    }

    pub fn process(&mut self, interleaved: &[f32]) -> &[f32] {
        let frames = interleaved.len() / self.channels;
        self.scratch.clear();
        self.scratch.reserve(frames);
        if self.channels == 1 {
            self.scratch
                .extend(interleaved.iter().take(frames).map(|sample| {
                    if sample.is_finite() {
                        *sample
                    } else {
                        0.0
                    }
                }));
            return &self.scratch;
        }

        self.energy.fill(0.0);
        let mut cross = 0.0f64;
        for frame in interleaved.chunks_exact(self.channels) {
            for (channel, sample) in frame.iter().enumerate() {
                let sample = if sample.is_finite() { *sample } else { 0.0 };
                self.energy[channel] += f64::from(sample) * f64::from(sample);
            }
            let left = if frame[0].is_finite() { frame[0] } else { 0.0 };
            let right = if frame[1].is_finite() { frame[1] } else { 0.0 };
            cross += f64::from(left) * f64::from(right);
        }

        let candidate = self
            .energy
            .iter()
            .enumerate()
            .max_by(|(_, left), (_, right)| left.total_cmp(right))
            .map_or(0, |(channel, _)| channel);
        let current_energy = self.energy[self.dominant_channel.min(self.channels - 1)];
        if candidate != self.dominant_channel
            && self.energy[candidate] > current_energy * DOWNMIX_SWITCH_RATIO
        {
            self.dominant_channel = candidate;
        }

        let left_energy = self.energy[0];
        let right_energy = self.energy[1];
        let correlation = if left_energy > f64::EPSILON && right_energy > f64::EPSILON {
            cross / (left_energy * right_energy).sqrt()
        } else {
            0.0
        };
        let stereo_balance = if left_energy.min(right_energy) > f64::EPSILON {
            left_energy.max(right_energy) / left_energy.min(right_energy)
        } else {
            f64::INFINITY
        };
        let average_correlated_stereo = self.channels == 2
            && correlation >= CORRELATED_STEREO_THRESHOLD
            && stereo_balance <= DOWNMIX_SWITCH_RATIO;

        for frame in interleaved.chunks_exact(self.channels) {
            let sample = if average_correlated_stereo {
                let left = if frame[0].is_finite() { frame[0] } else { 0.0 };
                let right = if frame[1].is_finite() { frame[1] } else { 0.0 };
                (left + right) * 0.5
            } else {
                let value = frame[self.dominant_channel.min(frame.len() - 1)];
                if value.is_finite() {
                    value
                } else {
                    0.0
                }
            };
            self.scratch.push(sample);
        }
        &self.scratch
    }
}

pub fn downmix_to_mono(interleaved: &[f32], channels: usize) -> Vec<f32> {
    AdaptiveDownmixer::new(channels)
        .process(interleaved)
        .to_vec()
}

const SINC_FILTER_TAPS: usize = 48;
const SINC_FILTER_HALF: usize = SINC_FILTER_TAPS / 2;
const SINC_FILTER_PHASES: usize = 1024;

pub struct Resampler {
    in_rate: u32,
    // Source position in units of 1 / SAMPLE_RATE input samples. Keeping the phase rational
    // avoids floating-point drift when callbacks use different chunk boundaries.
    pos_numerator: u64,
    source: Vec<f32>,
    kernel: Vec<[f32; SINC_FILTER_TAPS]>,
    source_base_ts_ns: Option<u64>,
    // A discontinuity can arrive in a callback too small to emit a resampled sample. Preserve
    // that fact until a non-empty output block carries the invalid boundary to the accumulator.
    timing_tainted: bool,
}

#[derive(Debug, Default)]
pub struct ResampledBlock {
    pub samples: Vec<f32>,
    pub first_capture_ts_ns: u64,
    pub capture_callback_ts_ns: u64,
    pub timing_valid: bool,
}

#[derive(Debug, Default, Clone, Copy)]
struct ResampledTiming {
    first_capture_ts_ns: u64,
    capture_callback_ts_ns: u64,
    timing_valid: bool,
}

impl Resampler {
    pub fn new(in_rate: u32) -> Resampler {
        let in_rate = in_rate.max(1);
        let kernel = if in_rate == SAMPLE_RATE {
            Vec::new()
        } else {
            build_sinc_kernel(in_rate)
        };
        Resampler {
            in_rate,
            pos_numerator: SINC_FILTER_HALF as u64 * u64::from(SAMPLE_RATE),
            source: if in_rate == SAMPLE_RATE {
                Vec::new()
            } else {
                vec![0.0; SINC_FILTER_HALF]
            },
            kernel,
            source_base_ts_ns: None,
            timing_tainted: false,
        }
    }

    pub fn process(&mut self, mono_in: &[f32]) -> Vec<f32> {
        self.process_timed(mono_in, 0, 0, false, false).samples
    }

    pub fn reset(&mut self) {
        self.pos_numerator = SINC_FILTER_HALF as u64 * u64::from(SAMPLE_RATE);
        self.source.clear();
        if self.in_rate != SAMPLE_RATE {
            self.source.resize(SINC_FILTER_HALF, 0.0);
        }
        self.source_base_ts_ns = None;
        self.timing_tainted = false;
    }

    fn reserve_realtime_input(&mut self, input_samples: usize) {
        self.source.reserve(
            input_samples
                .saturating_add(SINC_FILTER_TAPS)
                .saturating_sub(self.source.capacity()),
        );
    }

    pub fn process_timed(
        &mut self,
        mono_in: &[f32],
        first_capture_ts_ns: u64,
        callback_ts_ns: u64,
        capture_timestamp_valid: bool,
        frame_timing_valid: bool,
    ) -> ResampledBlock {
        let mut samples = Vec::new();
        let timing = self.process_timed_into(
            mono_in,
            first_capture_ts_ns,
            callback_ts_ns,
            capture_timestamp_valid,
            frame_timing_valid,
            &mut samples,
        );
        ResampledBlock {
            samples,
            first_capture_ts_ns: timing.first_capture_ts_ns,
            capture_callback_ts_ns: timing.capture_callback_ts_ns,
            timing_valid: timing.timing_valid,
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn process_timed_into(
        &mut self,
        mono_in: &[f32],
        first_capture_ts_ns: u64,
        callback_ts_ns: u64,
        capture_timestamp_valid: bool,
        frame_timing_valid: bool,
        output: &mut Vec<f32>,
    ) -> ResampledTiming {
        output.clear();
        self.timing_tainted |= !frame_timing_valid;
        if mono_in.is_empty() {
            return ResampledTiming::default();
        }
        if self.in_rate == SAMPLE_RATE {
            let timing_valid = !self.timing_tainted;
            self.timing_tainted = false;
            output.extend_from_slice(mono_in);
            return ResampledTiming {
                first_capture_ts_ns,
                capture_callback_ts_ns: callback_ts_ns,
                timing_valid,
            };
        }

        // Anchor the retained source sample(s) against every usable backend timestamp. This
        // follows hardware clock drift instead of extrapolating forever from the first callback.
        if capture_timestamp_valid {
            let retained_duration_ns =
                (self.source.len() as u64).saturating_mul(1_000_000_000) / u64::from(self.in_rate);
            self.source_base_ts_ns = Some(first_capture_ts_ns.saturating_sub(retained_duration_ns));
        }
        self.source.extend_from_slice(mono_in);
        let first_output_offset_ns = ((u128::from(self.pos_numerator) * 1_000_000_000)
            / (u128::from(SAMPLE_RATE) * u128::from(self.in_rate)))
            as u64;
        let output_ts_ns = self
            .source_base_ts_ns
            .map_or(0, |base| base.saturating_add(first_output_offset_ns));
        let expected =
            mono_in.len().saturating_mul(SAMPLE_RATE as usize) / self.in_rate as usize + 2;
        output.reserve(expected);
        // A precomputed 48-tap Blackman-windowed sinc rejects content above the destination
        // Nyquist frequency before downsampling. Retaining half a filter of history and future
        // input makes output independent of backend callback chunking.
        let phase_denominator = u64::from(SAMPLE_RATE);
        while self.pos_numerator + SINC_FILTER_HALF as u64 * phase_denominator
            < self.source.len() as u64 * phase_denominator
        {
            let center = (self.pos_numerator / phase_denominator) as usize;
            let phase = (((self.pos_numerator % phase_denominator) * SINC_FILTER_PHASES as u64)
                / phase_denominator) as usize;
            let start = center + 1 - SINC_FILTER_HALF;
            let coefficients = &self.kernel[phase];
            let sample = self.source[start..start + SINC_FILTER_TAPS]
                .iter()
                .zip(coefficients)
                .map(|(sample, coefficient)| sample * coefficient)
                .sum();
            output.push(sample);
            self.pos_numerator += u64::from(self.in_rate);
        }

        // Retain enough source history for the next convolution. At very high input rates the
        // position may overshoot this callback; positional debt is kept just as before.
        let consumed = (self.pos_numerator / phase_denominator)
            .saturating_sub(SINC_FILTER_HALF as u64) as usize;
        let consumed = consumed.min(self.source.len().saturating_sub(SINC_FILTER_HALF));
        if consumed > 0 {
            self.source.drain(0..consumed);
            self.pos_numerator -= consumed as u64 * phase_denominator;
            if let Some(base) = self.source_base_ts_ns.as_mut() {
                *base = base.saturating_add(
                    (consumed as u64).saturating_mul(1_000_000_000) / u64::from(self.in_rate),
                );
            }
        }
        let timing_valid = !self.timing_tainted && output_ts_ns != 0;
        if !output.is_empty() {
            self.timing_tainted = false;
        }
        ResampledTiming {
            first_capture_ts_ns: output_ts_ns,
            capture_callback_ts_ns: callback_ts_ns,
            timing_valid,
        }
    }
}

fn build_sinc_kernel(in_rate: u32) -> Vec<[f32; SINC_FILTER_TAPS]> {
    // Leave a small transition band so common 44.1/48/96/192 kHz device clocks receive useful
    // stop-band attenuation despite the deliberately compact real-time filter.
    let cutoff = (SAMPLE_RATE as f64 / f64::from(in_rate)).min(1.0) * 0.94;
    let mut phases = Vec::with_capacity(SINC_FILTER_PHASES);
    for phase in 0..SINC_FILTER_PHASES {
        let fraction = phase as f64 / SINC_FILTER_PHASES as f64;
        let mut coefficients = [0.0f32; SINC_FILTER_TAPS];
        let mut sum = 0.0f64;
        for (tap, coefficient) in coefficients.iter_mut().enumerate() {
            let offset = tap as f64 - (SINC_FILTER_HALF - 1) as f64 - fraction;
            let sinc_x = cutoff * offset;
            let sinc = if sinc_x.abs() < 1.0e-12 {
                cutoff
            } else {
                cutoff * (std::f64::consts::PI * sinc_x).sin() / (std::f64::consts::PI * sinc_x)
            };
            let normalized = (offset.abs() / SINC_FILTER_HALF as f64).min(1.0);
            let window = 0.42
                + 0.5 * (std::f64::consts::PI * normalized).cos()
                + 0.08 * (std::f64::consts::TAU * normalized).cos();
            let value = sinc * window;
            *coefficient = value as f32;
            sum += value;
        }
        if sum.abs() > f64::EPSILON {
            for coefficient in &mut coefficients {
                *coefficient = (*coefficient as f64 / sum) as f32;
            }
        }
        phases.push(coefficients);
    }
    phases
}

pub struct FrameAccumulator {
    buf: Vec<f32>,
    timeline: VecDeque<TimingSegment>,
}

#[derive(Debug, Clone, Copy)]
struct TimingSegment {
    samples: usize,
    capture_ts_ns: u64,
    callback_ts_ns: u64,
    valid: bool,
}

impl Default for FrameAccumulator {
    fn default() -> Self {
        Self::new()
    }
}

impl FrameAccumulator {
    pub fn new() -> FrameAccumulator {
        Self::with_capacity(FRAME_SAMPLES * 2, 8)
    }

    fn with_capacity(sample_capacity: usize, timeline_capacity: usize) -> FrameAccumulator {
        FrameAccumulator {
            buf: Vec::with_capacity(sample_capacity),
            timeline: VecDeque::with_capacity(timeline_capacity),
        }
    }

    pub fn reset(&mut self) {
        self.buf.clear();
        self.timeline.clear();
    }

    pub fn pending_samples(&self) -> usize {
        self.buf.len()
    }

    pub fn push_and_drain(&mut self, block: ResampledBlock) -> Vec<AudioFrame> {
        let mut frames = Vec::new();
        self.push_samples_and_drain_into(
            &block.samples,
            ResampledTiming {
                first_capture_ts_ns: block.first_capture_ts_ns,
                capture_callback_ts_ns: block.capture_callback_ts_ns,
                timing_valid: block.timing_valid,
            },
            &mut frames,
        );
        frames
    }

    fn push_samples_and_drain_into(
        &mut self,
        samples: &[f32],
        timing: ResampledTiming,
        frames: &mut Vec<AudioFrame>,
    ) {
        frames.clear();
        self.push_samples_and_drain_with(samples, timing, |timing, samples| {
            frames.push(AudioFrame {
                encoder_epoch: 0,
                capture_generation: 0,
                capture_open_attempt: 0,
                capture_ts_ns: timing.capture_ts_ns,
                capture_callback_ts_ns: timing.callback_ts_ns,
                capture_timestamp_valid: timing.valid,
                samples: samples.to_vec(),
            });
        });
    }

    fn push_samples_and_drain_with<F>(
        &mut self,
        samples: &[f32],
        timing: ResampledTiming,
        mut emit: F,
    ) -> usize
    where
        F: FnMut(TimingSegment, &[f32]),
    {
        if samples.is_empty() {
            return 0;
        }
        let block_len = samples.len();
        self.buf.extend_from_slice(samples);
        self.timeline.push_back(TimingSegment {
            samples: block_len,
            capture_ts_ns: timing.first_capture_ts_ns,
            callback_ts_ns: timing.capture_callback_ts_ns,
            valid: timing.timing_valid,
        });
        let mut emitted = 0;
        while self.buf.len() >= FRAME_SAMPLES {
            let timing = self.frame_timing(FRAME_SAMPLES);
            emit(timing, &self.buf[..FRAME_SAMPLES]);
            self.consume_timeline(FRAME_SAMPLES);
            let remaining = self.buf.len() - FRAME_SAMPLES;
            self.buf.copy_within(FRAME_SAMPLES.., 0);
            self.buf.truncate(remaining);
            emitted += 1;
        }
        emitted
    }

    fn frame_timing(&self, samples: usize) -> TimingSegment {
        let Some(first) = self.timeline.front().copied() else {
            return TimingSegment {
                samples,
                capture_ts_ns: 0,
                callback_ts_ns: 0,
                valid: false,
            };
        };
        let mut remaining = samples;
        let mut valid = first.valid;
        for segment in &self.timeline {
            if remaining == 0 {
                break;
            }
            valid &= segment.valid;
            remaining = remaining.saturating_sub(segment.samples);
        }
        TimingSegment { valid, ..first }
    }

    fn consume_timeline(&mut self, mut samples: usize) {
        while samples > 0 {
            let Some(front) = self.timeline.front_mut() else {
                break;
            };
            let consumed = samples.min(front.samples);
            if consumed == front.samples {
                samples -= consumed;
                self.timeline.pop_front();
            } else {
                front.samples -= consumed;
                front.capture_ts_ns = front.capture_ts_ns.saturating_add(
                    (consumed as u64).saturating_mul(1_000_000_000) / u64::from(SAMPLE_RATE),
                );
                samples = 0;
            }
        }
    }
}

pub fn now_ns() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0)
}

/// A monotonic process-local clock for liveness decisions. Wall-clock adjustments must not
/// extend or prematurely fire the output watchdog.
pub fn monotonic_ns() -> u64 {
    static EPOCH: OnceLock<Instant> = OnceLock::new();
    // Leave one second of headroom so a first-sample timestamp can safely subtract the CPAL
    // hardware latency even when the first callback arrives immediately after process startup.
    (EPOCH
        .get_or_init(Instant::now)
        .elapsed()
        .as_nanos()
        .min((u64::MAX - 1_000_000_000) as u128) as u64)
        .saturating_add(1_000_000_000)
}

#[derive(Default)]
pub struct PlaybackProgress {
    started_ns: AtomicU64,
    callback_ns: AtomicU64,
}

impl PlaybackProgress {
    pub fn reset(&self) {
        self.started_ns.store(0, Ordering::Release);
        self.callback_ns.store(0, Ordering::Release);
    }

    pub fn mark_started(&self) {
        self.started_ns.store(monotonic_ns(), Ordering::Release);
    }

    pub fn mark_callback(&self) {
        self.callback_ns.store(monotonic_ns(), Ordering::Release);
    }

    pub fn snapshot(&self) -> (u64, u64) {
        (
            self.started_ns.load(Ordering::Acquire),
            self.callback_ns.load(Ordering::Acquire),
        )
    }
}

const UNKNOWN_AEC_TIMING_US: u64 = u64::MAX;
const DEFAULT_AEC_DELAY_MS: i32 = 50;
const MAX_AEC_DELAY_MS: u64 = 500;
const MAX_AEC_COMPONENT_US: u64 = MAX_AEC_DELAY_MS * 1_000;
const AEC_CAPTURE_CALLBACK_STALE_NS: u64 = 250_000_000;
const CAPTURE_CLOCK_DISCONTINUITY_TOLERANCE_NS: u64 = 5_000_000;
const AEC_REASON_COMPLETE: u64 = 0;
const AEC_REASON_CALLBACK_FALLBACK: u64 = 1;
const AEC_REASON_MISSING_CAPTURE: u64 = 2;
const AEC_REASON_MISSING_OUTPUT: u64 = 3;
const AEC_REASON_NO_RENDER: u64 = 4;
const AEC_REASON_STALE: u64 = 5;
// Hardware timestamps stop at the DAC/ADC boundaries. Include a small bounded allowance for the
// acoustic path between the speaker and microphone when a render stream is actually active.
const AEC_ACOUSTIC_PATH_US: u64 = 3_000;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct AecDelaySnapshot {
    pub recommended_delay_ms: i32,
    pub measured_delay_ms: u64,
    pub input_latency_ms: u64,
    pub output_latency_ms: u64,
    pub render_queue_ms: u64,
    pub capture_processing_ms: u64,
    pub capture_path_ms: u64,
    pub timing_complete: bool,
    pub input_timing_present: bool,
    pub output_timing_present: bool,
    pub render_timing_present: bool,
    pub capture_path_present: bool,
    pub fallback_reason: &'static str,
    pub frame_timestamp_valid: bool,
    pub last_frame_processed_present: bool,
    pub last_frame_processed_age_ms: u64,
    pub render_observations: u64,
    pub invalid_timestamp_samples: u64,
    pub invalid_frame_timestamp_samples: u64,
    /// Changes whenever the concrete output stream is reopened. This is process-local timing
    /// context for the DSP delay smoother and is intentionally not part of protocol v7.
    pub playback_timing_epoch: u64,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
struct InputTimingObservation {
    callback_mono_ns: u64,
    first_sample_mono_ns: u64,
    input_latency_us: u64,
    valid: bool,
    frame_timing_valid: bool,
    discontinuity: bool,
    capture_clock_delta_ns: Option<u64>,
    expected_capture_delta_ns: Option<u64>,
    capture_clock_delta_error_ns: Option<i64>,
    bridge_residual_ns: Option<i64>,
    clock_status: CaptureClockStatus,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum BackendCaptureStep {
    First,
    Forward(u64),
    Reversed,
}

#[derive(Default)]
struct CaptureClockBridge {
    last_mapped_capture_ns: Option<u64>,
    previous_frames: Option<u64>,
    previous_sample_rate: u32,
    recovery_pending: bool,
}

impl CaptureClockBridge {
    fn signed_difference(lhs: u64, rhs: u64) -> i64 {
        if lhs >= rhs {
            lhs.saturating_sub(rhs).min(i64::MAX as u64) as i64
        } else {
            -(rhs.saturating_sub(lhs).min(i64::MAX as u64) as i64)
        }
    }

    fn expected_delta_ns(&self) -> Option<u64> {
        self.previous_frames.map(|frames| {
            frames.saturating_mul(1_000_000_000) / u64::from(self.previous_sample_rate.max(1))
        })
    }

    fn invalidate(&mut self) {
        self.last_mapped_capture_ns = None;
        self.previous_frames = None;
        self.previous_sample_rate = 0;
        self.recovery_pending = true;
    }

    fn observe(
        &mut self,
        callback_mono_ns: u64,
        capture_to_callback_ns: Option<u64>,
        step: BackendCaptureStep,
        frames: usize,
        sample_rate: u32,
    ) -> InputTimingObservation {
        let capture_clock_delta_ns = match step {
            BackendCaptureStep::Forward(delta) => Some(delta),
            BackendCaptureStep::First | BackendCaptureStep::Reversed => None,
        };
        let expected_capture_delta_ns = self.expected_delta_ns();
        let capture_clock_delta_error_ns = capture_clock_delta_ns
            .zip(expected_capture_delta_ns)
            .map(|(actual, expected)| Self::signed_difference(actual, expected));
        let Some(latency_ns) = capture_to_callback_ns else {
            self.invalidate();
            return InputTimingObservation {
                callback_mono_ns,
                capture_clock_delta_ns,
                expected_capture_delta_ns,
                capture_clock_delta_error_ns,
                clock_status: CaptureClockStatus::InvalidLatency,
                ..Default::default()
            };
        };
        let Some(fresh_capture_mono_ns) = callback_mono_ns.checked_sub(latency_ns) else {
            self.invalidate();
            return InputTimingObservation {
                callback_mono_ns,
                input_latency_us: latency_ns / 1_000,
                capture_clock_delta_ns,
                expected_capture_delta_ns,
                capture_clock_delta_error_ns,
                clock_status: CaptureClockStatus::AnchorUnderflow,
                ..Default::default()
            };
        };
        if fresh_capture_mono_ns == 0 {
            self.invalidate();
            return InputTimingObservation {
                callback_mono_ns,
                input_latency_us: latency_ns / 1_000,
                capture_clock_delta_ns,
                expected_capture_delta_ns,
                capture_clock_delta_error_ns,
                clock_status: CaptureClockStatus::AnchorUnderflow,
                ..Default::default()
            };
        }

        let recovering = self.recovery_pending;
        let mut observation = InputTimingObservation {
            callback_mono_ns,
            first_sample_mono_ns: fresh_capture_mono_ns,
            input_latency_us: latency_ns / 1_000,
            valid: true,
            frame_timing_valid: !recovering,
            capture_clock_delta_ns,
            expected_capture_delta_ns,
            capture_clock_delta_error_ns,
            clock_status: if recovering {
                CaptureClockStatus::Recovered
            } else {
                CaptureClockStatus::Anchored
            },
            ..Default::default()
        };

        match step {
            BackendCaptureStep::First => {}
            BackendCaptureStep::Reversed => {
                observation.frame_timing_valid = false;
                observation.discontinuity = true;
                observation.clock_status = CaptureClockStatus::BackendClockReversed;
            }
            BackendCaptureStep::Forward(delta_ns) => {
                if let Some(previous_mapped_ns) = self.last_mapped_capture_ns {
                    let Some(mapped_capture_ns) = previous_mapped_ns.checked_add(delta_ns) else {
                        observation.frame_timing_valid = false;
                        observation.discontinuity = true;
                        observation.clock_status = CaptureClockStatus::MappingOverflow;
                        self.last_mapped_capture_ns = Some(fresh_capture_mono_ns);
                        self.previous_frames = Some(frames as u64);
                        self.previous_sample_rate = sample_rate;
                        self.recovery_pending = false;
                        return observation;
                    };
                    observation.first_sample_mono_ns = mapped_capture_ns;
                    observation.bridge_residual_ns = Some(Self::signed_difference(
                        fresh_capture_mono_ns,
                        mapped_capture_ns,
                    ));
                    let delta_mismatch = expected_capture_delta_ns.is_some_and(|expected| {
                        delta_ns.abs_diff(expected) > CAPTURE_CLOCK_DISCONTINUITY_TOLERANCE_NS
                    });
                    if delta_mismatch {
                        observation.frame_timing_valid = false;
                        observation.discontinuity = true;
                        observation.clock_status = CaptureClockStatus::DeltaMismatch;
                    } else if !recovering {
                        observation.clock_status = CaptureClockStatus::Continuous;
                    }
                }
            }
        }

        self.last_mapped_capture_ns = Some(observation.first_sample_mono_ns);
        self.previous_frames = Some(frames as u64);
        self.previous_sample_rate = sample_rate;
        self.recovery_pending = false;
        observation
    }
}

#[derive(Default)]
struct CaptureClockMapper {
    previous_capture: Option<cpal::StreamInstant>,
    bridge: CaptureClockBridge,
}

impl CaptureClockMapper {
    fn duration_ns(duration: Duration) -> u64 {
        duration.as_nanos().min(u64::MAX as u128) as u64
    }

    fn observe(
        &mut self,
        info: &cpal::InputCallbackInfo,
        callback_mono_ns: u64,
        frames: usize,
        sample_rate: u32,
    ) -> InputTimingObservation {
        let timestamp = info.timestamp();
        let step = match self.previous_capture {
            None => BackendCaptureStep::First,
            Some(previous) => timestamp
                .capture
                .checked_duration_since(previous)
                .map_or(BackendCaptureStep::Reversed, |duration| {
                    BackendCaptureStep::Forward(Self::duration_ns(duration))
                }),
        };
        self.previous_capture = Some(timestamp.capture);
        let capture_to_callback_ns = timestamp
            .callback
            .checked_duration_since(timestamp.capture)
            .map(Self::duration_ns)
            .filter(|latency_ns| *latency_ns <= MAX_AEC_COMPONENT_US.saturating_mul(1_000));
        self.bridge.observe(
            callback_mono_ns,
            capture_to_callback_ns,
            step,
            frames,
            sample_rate,
        )
    }
}

/// Lock-free timing shared by the capture, render, playback, encoder, and telemetry threads.
///
/// WebRTC defines its stream delay as:
///   (hardware render - reverse analysis) + (capture processing - hardware capture).
/// CPAL exposes the hardware-side input/output latency in callback timestamps. The remaining
/// render queue and capture scheduling components are measured inside the helper.
pub struct AecTiming {
    input_latency_us: AtomicU64,
    output_latency_us: AtomicU64,
    render_queue_pairs: AtomicU64,
    capture_callback_ns: AtomicU64,
    render_observations: AtomicU64,
    playback_timing_epoch: AtomicU64,
    invalid_timestamp_samples: AtomicU64,
    invalid_frame_timestamp_samples: AtomicU64,
    latest_recommended_delay_ms: AtomicU64,
    latest_measured_delay_ms: AtomicU64,
    latest_frame_input_latency_ms: AtomicU64,
    latest_capture_processing_ms: AtomicU64,
    latest_capture_path_ms: AtomicU64,
    latest_frame_timestamp_valid: AtomicBool,
    latest_timing_complete: AtomicBool,
    latest_input_timing_present: AtomicBool,
    latest_capture_path_present: AtomicBool,
    latest_fallback_reason: AtomicU64,
    latest_frame_processed_ns: AtomicU64,
    applied_delay_ms: AtomicU64,
    applied_delay_frames: AtomicU64,
}

impl Default for AecTiming {
    fn default() -> Self {
        Self {
            input_latency_us: AtomicU64::new(UNKNOWN_AEC_TIMING_US),
            output_latency_us: AtomicU64::new(UNKNOWN_AEC_TIMING_US),
            render_queue_pairs: AtomicU64::new(0),
            capture_callback_ns: AtomicU64::new(0),
            render_observations: AtomicU64::new(0),
            playback_timing_epoch: AtomicU64::new(0),
            invalid_timestamp_samples: AtomicU64::new(0),
            invalid_frame_timestamp_samples: AtomicU64::new(0),
            latest_recommended_delay_ms: AtomicU64::new(DEFAULT_AEC_DELAY_MS as u64),
            latest_measured_delay_ms: AtomicU64::new(0),
            latest_frame_input_latency_ms: AtomicU64::new(0),
            latest_capture_processing_ms: AtomicU64::new(0),
            latest_capture_path_ms: AtomicU64::new(0),
            latest_frame_timestamp_valid: AtomicBool::new(false),
            latest_timing_complete: AtomicBool::new(false),
            latest_input_timing_present: AtomicBool::new(false),
            latest_capture_path_present: AtomicBool::new(false),
            latest_fallback_reason: AtomicU64::new(AEC_REASON_STALE),
            latest_frame_processed_ns: AtomicU64::new(0),
            applied_delay_ms: AtomicU64::new(UNKNOWN_AEC_TIMING_US),
            applied_delay_frames: AtomicU64::new(0),
        }
    }
}

impl AecTiming {
    fn duration_us(duration: Duration) -> u64 {
        duration.as_micros().min(MAX_AEC_COMPONENT_US as u128) as u64
    }

    fn observe_latency(target: &AtomicU64, latency: Option<Duration>, invalid: &AtomicU64) {
        match latency {
            Some(duration) => target.store(Self::duration_us(duration), Ordering::Release),
            None => {
                invalid.fetch_add(1, Ordering::Relaxed);
            }
        }
    }

    fn observe_input_callback(
        &self,
        mapper: &mut CaptureClockMapper,
        info: &cpal::InputCallbackInfo,
        frames: usize,
        sample_rate: u32,
    ) -> InputTimingObservation {
        let callback_mono_ns = monotonic_ns();
        let observation = mapper.observe(info, callback_mono_ns, frames, sample_rate);
        if observation.valid {
            self.input_latency_us
                .store(observation.input_latency_us, Ordering::Release);
        } else {
            self.invalid_timestamp_samples
                .fetch_add(1, Ordering::Relaxed);
        }
        self.capture_callback_ns
            .store(callback_mono_ns, Ordering::Release);
        observation
    }

    pub fn observe_output_callback(&self, info: &cpal::OutputCallbackInfo) {
        let timestamp = info.timestamp();
        Self::observe_latency(
            &self.output_latency_us,
            timestamp
                .playback
                .checked_duration_since(timestamp.callback),
            &self.invalid_timestamp_samples,
        );
    }

    pub fn observe_render_queue_pairs(&self, queued_pairs_before_frame: usize) {
        self.render_queue_pairs.store(
            queued_pairs_before_frame.min(u64::MAX as usize) as u64,
            Ordering::Release,
        );
        self.render_observations.fetch_add(1, Ordering::Relaxed);
    }

    pub fn note_applied_delay(&self, delay_ms: i32) {
        self.applied_delay_ms.store(
            delay_ms.clamp(0, MAX_AEC_DELAY_MS as i32) as u64,
            Ordering::Release,
        );
        self.applied_delay_frames.fetch_add(1, Ordering::Relaxed);
    }

    pub fn applied_delay_ms(&self) -> Option<u64> {
        let value = self.applied_delay_ms.load(Ordering::Acquire);
        (value != UNKNOWN_AEC_TIMING_US).then_some(value)
    }

    pub fn applied_delay_frames(&self) -> u64 {
        self.applied_delay_frames.load(Ordering::Relaxed)
    }

    /// Clear capture-side observations when a capture stream is (re)opened. Render/output
    /// measurements belong to the continuously-running playback path and intentionally survive.
    pub fn reset_capture_path(&self) {
        self.input_latency_us
            .store(UNKNOWN_AEC_TIMING_US, Ordering::Release);
        self.capture_callback_ns.store(0, Ordering::Release);
        self.latest_recommended_delay_ms
            .store(DEFAULT_AEC_DELAY_MS as u64, Ordering::Release);
        self.latest_measured_delay_ms.store(0, Ordering::Release);
        self.latest_frame_input_latency_ms
            .store(0, Ordering::Release);
        self.latest_capture_processing_ms
            .store(0, Ordering::Release);
        self.latest_capture_path_ms.store(0, Ordering::Release);
        self.latest_frame_timestamp_valid
            .store(false, Ordering::Release);
        self.latest_timing_complete.store(false, Ordering::Release);
        self.latest_input_timing_present
            .store(false, Ordering::Release);
        self.latest_capture_path_present
            .store(false, Ordering::Release);
        self.latest_fallback_reason
            .store(AEC_REASON_STALE, Ordering::Release);
        self.latest_frame_processed_ns.store(0, Ordering::Release);
        self.applied_delay_ms
            .store(UNKNOWN_AEC_TIMING_US, Ordering::Release);
    }

    pub(crate) fn clear_playback_measurements(&self) {
        self.output_latency_us
            .store(UNKNOWN_AEC_TIMING_US, Ordering::Release);
        self.render_queue_pairs.store(0, Ordering::Release);
        self.render_observations.store(0, Ordering::Release);
        self.latest_timing_complete.store(false, Ordering::Release);
        self.latest_fallback_reason
            .store(AEC_REASON_MISSING_OUTPUT, Ordering::Release);
    }

    pub fn reset_playback_path(&self) {
        self.clear_playback_measurements();
        // Publish the new path only after its old measurements have been invalidated. An encoder
        // that observes this epoch is therefore guaranteed to treat any still-missing timing as
        // startup data for the new output stream.
        self.playback_timing_epoch.fetch_add(1, Ordering::AcqRel);
    }

    fn component_us(target: &AtomicU64) -> Option<u64> {
        let value = target.load(Ordering::Acquire);
        (value != UNKNOWN_AEC_TIMING_US).then_some(value)
    }

    fn render_queue_us(&self, render_observations: u64) -> u64 {
        if render_observations == 0 {
            0
        } else {
            (self
                .render_queue_pairs
                .load(Ordering::Acquire)
                .saturating_mul(1_000_000)
                / SAMPLE_RATE as u64)
                .min(MAX_AEC_COMPONENT_US)
        }
    }

    fn finish_snapshot(
        &self,
        input_us: Option<u64>,
        capture_processing_us: Option<u64>,
        capture_path_us: Option<u64>,
        frame_timestamp_valid: bool,
    ) -> AecDelaySnapshot {
        let playback_timing_epoch = self.playback_timing_epoch.load(Ordering::Acquire);
        let output_us = Self::component_us(&self.output_latency_us);
        let render_observations = self.render_observations.load(Ordering::Acquire);
        let render_queue_us = self.render_queue_us(render_observations);
        let timing_complete = capture_path_us.is_some()
            && output_us.is_some()
            && render_observations > 0
            && capture_processing_us.is_some();
        // The frame-specific capture path already includes ADC-to-callback latency. Adding
        // input_us again here would double count the hardware capture component.
        let measured_us = output_us
            .unwrap_or(0)
            .saturating_add(render_queue_us)
            .saturating_add(capture_path_us.unwrap_or(0))
            .saturating_add(if render_observations > 0 {
                AEC_ACOUSTIC_PATH_US
            } else {
                0
            });
        let measured_delay_ms = ((measured_us + 500) / 1_000).min(MAX_AEC_DELAY_MS);
        let recommended_delay_ms = if timing_complete {
            measured_delay_ms as i32
        } else {
            DEFAULT_AEC_DELAY_MS
        };
        let fallback_reason_code = if !frame_timestamp_valid {
            if capture_path_us.is_some() {
                AEC_REASON_CALLBACK_FALLBACK
            } else {
                AEC_REASON_MISSING_CAPTURE
            }
        } else if capture_path_us.is_none() || capture_processing_us.is_none() {
            AEC_REASON_MISSING_CAPTURE
        } else if output_us.is_none() {
            AEC_REASON_MISSING_OUTPUT
        } else if render_observations == 0 {
            AEC_REASON_NO_RENDER
        } else {
            AEC_REASON_COMPLETE
        };

        AecDelaySnapshot {
            recommended_delay_ms,
            measured_delay_ms,
            input_latency_ms: ((input_us.unwrap_or(0) + 500) / 1_000),
            output_latency_ms: ((output_us.unwrap_or(0) + 500) / 1_000),
            render_queue_ms: ((render_queue_us + 500) / 1_000),
            capture_processing_ms: ((capture_processing_us.unwrap_or(0) + 500) / 1_000),
            capture_path_ms: ((capture_path_us.unwrap_or(0) + 500) / 1_000),
            timing_complete,
            input_timing_present: input_us.is_some(),
            output_timing_present: output_us.is_some(),
            render_timing_present: render_observations > 0,
            capture_path_present: capture_path_us.is_some(),
            fallback_reason: Self::fallback_reason(fallback_reason_code),
            frame_timestamp_valid,
            last_frame_processed_present: false,
            last_frame_processed_age_ms: 0,
            render_observations,
            invalid_timestamp_samples: self.invalid_timestamp_samples.load(Ordering::Relaxed),
            invalid_frame_timestamp_samples: self
                .invalid_frame_timestamp_samples
                .load(Ordering::Relaxed),
            playback_timing_epoch,
        }
    }

    pub fn snapshot_for_capture(&self, now_ns: u64, frame: &AudioFrame) -> AecDelaySnapshot {
        let frame_valid = frame.capture_timestamp_valid
            && frame.capture_ts_ns != 0
            && frame.capture_callback_ts_ns >= frame.capture_ts_ns
            && now_ns >= frame.capture_callback_ts_ns
            && now_ns >= frame.capture_ts_ns
            && now_ns.saturating_sub(frame.capture_ts_ns)
                <= MAX_AEC_COMPONENT_US.saturating_mul(1_000);

        let (input_us, processing_us, path_us, used_frame_timestamp) = if frame_valid {
            (
                Some(
                    frame
                        .capture_callback_ts_ns
                        .saturating_sub(frame.capture_ts_ns)
                        / 1_000,
                ),
                Some(now_ns.saturating_sub(frame.capture_callback_ts_ns) / 1_000),
                Some(now_ns.saturating_sub(frame.capture_ts_ns) / 1_000),
                true,
            )
        } else {
            self.invalid_frame_timestamp_samples
                .fetch_add(1, Ordering::Relaxed);
            // Preserve a bounded startup fallback for a platform callback with an invalid hardware
            // timestamp. Valid frames immediately switch to the frame-specific path above.
            // Even when the backend cannot provide an ADC timestamp, each queued frame still
            // carries the callback time that produced its first samples. Prefer that frame-local
            // value so backlog cannot be made artificially fresh by a newer callback.
            let frame_callback_current = frame.capture_callback_ts_ns != 0
                && now_ns >= frame.capture_callback_ts_ns
                && now_ns.saturating_sub(frame.capture_callback_ts_ns)
                    <= MAX_AEC_COMPONENT_US.saturating_mul(1_000);
            let callback_ns = if frame_callback_current {
                frame.capture_callback_ts_ns
            } else {
                self.capture_callback_ns.load(Ordering::Acquire)
            };
            let callback_age_ns = now_ns.saturating_sub(callback_ns);
            let callback_current = callback_ns != 0
                && now_ns >= callback_ns
                && callback_age_ns <= AEC_CAPTURE_CALLBACK_STALE_NS;
            let input = Self::component_us(&self.input_latency_us);
            let processing = callback_current.then_some(callback_age_ns / 1_000);
            let path = input.and_then(|latency| {
                processing
                    .map(|processing| latency.saturating_add(processing).min(MAX_AEC_COMPONENT_US))
            });
            (input, processing, path, false)
        };

        let snapshot = self.finish_snapshot(input_us, processing_us, path_us, used_frame_timestamp);
        self.latest_recommended_delay_ms.store(
            snapshot.recommended_delay_ms.max(0) as u64,
            Ordering::Release,
        );
        self.latest_measured_delay_ms
            .store(snapshot.measured_delay_ms, Ordering::Release);
        self.latest_frame_input_latency_ms
            .store(snapshot.input_latency_ms, Ordering::Release);
        self.latest_capture_processing_ms
            .store(snapshot.capture_processing_ms, Ordering::Release);
        self.latest_capture_path_ms
            .store(snapshot.capture_path_ms, Ordering::Release);
        self.latest_frame_timestamp_valid
            .store(snapshot.frame_timestamp_valid, Ordering::Release);
        self.latest_timing_complete
            .store(snapshot.timing_complete, Ordering::Release);
        self.latest_input_timing_present
            .store(snapshot.input_timing_present, Ordering::Release);
        self.latest_capture_path_present
            .store(snapshot.capture_path_present, Ordering::Release);
        self.latest_fallback_reason.store(
            Self::fallback_reason_code(snapshot.fallback_reason),
            Ordering::Release,
        );
        self.latest_frame_processed_ns
            .store(now_ns, Ordering::Release);
        snapshot
    }

    pub fn snapshot(&self, now_ns: u64) -> AecDelaySnapshot {
        let playback_timing_epoch = self.playback_timing_epoch.load(Ordering::Acquire);
        let output_us = Self::component_us(&self.output_latency_us);
        let render_observations = self.render_observations.load(Ordering::Acquire);
        let render_queue_us = self.render_queue_us(render_observations);
        let processed_ns = self.latest_frame_processed_ns.load(Ordering::Acquire);
        let processed_age_ns = now_ns.saturating_sub(processed_ns);
        let fresh = processed_ns != 0
            && now_ns >= processed_ns
            && processed_age_ns <= AEC_CAPTURE_CALLBACK_STALE_NS;
        AecDelaySnapshot {
            recommended_delay_ms: self
                .latest_recommended_delay_ms
                .load(Ordering::Acquire)
                .min(MAX_AEC_DELAY_MS) as i32,
            measured_delay_ms: self.latest_measured_delay_ms.load(Ordering::Acquire),
            input_latency_ms: self.latest_frame_input_latency_ms.load(Ordering::Acquire),
            output_latency_ms: (output_us.unwrap_or(0) + 500) / 1_000,
            render_queue_ms: (render_queue_us + 500) / 1_000,
            capture_processing_ms: self.latest_capture_processing_ms.load(Ordering::Acquire),
            capture_path_ms: self.latest_capture_path_ms.load(Ordering::Acquire),
            timing_complete: fresh && self.latest_timing_complete.load(Ordering::Acquire),
            input_timing_present: self.latest_input_timing_present.load(Ordering::Acquire),
            output_timing_present: output_us.is_some(),
            render_timing_present: render_observations > 0,
            capture_path_present: self.latest_capture_path_present.load(Ordering::Acquire),
            fallback_reason: if fresh {
                Self::fallback_reason(self.latest_fallback_reason.load(Ordering::Acquire))
            } else {
                Self::fallback_reason(AEC_REASON_STALE)
            },
            frame_timestamp_valid: self.latest_frame_timestamp_valid.load(Ordering::Acquire),
            last_frame_processed_present: processed_ns != 0 && now_ns >= processed_ns,
            last_frame_processed_age_ms: if processed_ns == 0 || now_ns < processed_ns {
                0
            } else {
                processed_age_ns / 1_000_000
            },
            render_observations,
            invalid_timestamp_samples: self.invalid_timestamp_samples.load(Ordering::Relaxed),
            invalid_frame_timestamp_samples: self
                .invalid_frame_timestamp_samples
                .load(Ordering::Relaxed),
            playback_timing_epoch,
        }
    }

    fn fallback_reason(code: u64) -> &'static str {
        match code {
            AEC_REASON_COMPLETE => "complete",
            AEC_REASON_CALLBACK_FALLBACK => "capture-callback-fallback",
            AEC_REASON_MISSING_CAPTURE => "missing-capture-timing",
            AEC_REASON_MISSING_OUTPUT => "missing-output-timing",
            AEC_REASON_NO_RENDER => "no-render-observation",
            _ => "stale-or-no-capture-frame",
        }
    }

    fn fallback_reason_code(reason: &str) -> u64 {
        match reason {
            "complete" => AEC_REASON_COMPLETE,
            "capture-callback-fallback" => AEC_REASON_CALLBACK_FALLBACK,
            "missing-capture-timing" => AEC_REASON_MISSING_CAPTURE,
            "missing-output-timing" => AEC_REASON_MISSING_OUTPUT,
            "no-render-observation" => AEC_REASON_NO_RENDER,
            _ => AEC_REASON_STALE,
        }
    }

    #[cfg(test)]
    fn observe_input_latency_for_test(&self, latency: Duration, callback_ns: u64) {
        self.input_latency_us
            .store(Self::duration_us(latency), Ordering::Release);
        self.capture_callback_ns
            .store(callback_ns, Ordering::Release);
    }

    #[cfg(test)]
    fn observe_output_latency_for_test(&self, latency: Duration) {
        self.output_latency_us
            .store(Self::duration_us(latency), Ordering::Release);
    }
}

pub fn peak(samples: &[f32]) -> f32 {
    samples.iter().fold(0.0f32, |m, &s| m.max(s.abs()))
}

fn choose_device_display_name(
    primary_name: &str,
    extended: &[String],
    prefer_extended_friendly_name: bool,
) -> Option<String> {
    // CPAL's WASAPI backend exposes DEVPKEY_Device_DeviceDesc as `name()`
    // (usually just "Microphone", "Speakers", or "Headphones") and preserves the
    // Windows endpoint FriendlyName as the first extended line. Other backends use
    // `name()` as their actual user-facing label, so only Windows opts into extended.
    if prefer_extended_friendly_name {
        if let Some(friendly_name) = extended
            .iter()
            .map(|line| line.trim())
            .find(|line| !line.is_empty())
        {
            return Some(friendly_name.to_string());
        }
    }

    let primary_name = primary_name.trim();
    (!primary_name.is_empty()).then(|| primary_name.to_string())
}

fn device_display_name(description: &cpal::DeviceDescription) -> Option<String> {
    let extended = description
        .extended()
        .map(str::to_string)
        .collect::<Vec<_>>();
    choose_device_display_name(description.name(), &extended, cfg!(target_os = "windows"))
}

pub fn enumerate_devices() -> Vec<DeviceInfo> {
    let host = cpal::default_host();
    let default_id = host
        .default_input_device()
        .and_then(|device| device.id().ok())
        .map(|id| id.to_string());
    let mut out = Vec::new();
    if let Ok(devices) = host.input_devices() {
        for device in devices {
            // Only publish devices with a stable identifier and a stream format the helper can
            // actually process. A picker entry that can never be opened is worse than omitting it.
            let id = match device.id() {
                Ok(id) => id.to_string(),
                Err(_) => continue,
            };
            let name = match device
                .description()
                .ok()
                .and_then(|description| device_display_name(&description))
            {
                Some(name) => name,
                _ => continue,
            };
            if pick_input_config(&device).is_err() {
                continue;
            }
            let is_default = Some(&id) == default_id.as_ref();
            out.push(DeviceInfo {
                id,
                name,
                default: is_default,
            });
        }
    }
    out.sort_by_key(|d| std::cmp::Reverse(d.default));
    out
}

struct SelectedDevice {
    device: cpal::Device,
    requested_device: String,
    resolved_device: String,
    requested_default: bool,
    requested_matched: bool,
    fell_back_to_default: bool,
}

fn selected_or_default<T>(
    requested_id: Option<&str>,
    selected: Option<T>,
    default_device: impl FnOnce() -> Option<T>,
    direction: &str,
) -> Result<T, String> {
    if requested_id.is_some_and(|id| !id.is_empty()) {
        return selected.ok_or_else(|| format!("selected {direction} device is unavailable"));
    }
    default_device().ok_or_else(|| format!("no {direction} device"))
}

fn pick_device(device_id: &Option<String>) -> Result<SelectedDevice, String> {
    let host = cpal::default_host();
    let requested_id = device_id.as_deref().filter(|id| !id.is_empty());
    let selected = requested_id
        .and_then(|id| id.parse::<cpal::DeviceId>().ok())
        .and_then(|id| host.device_by_id(&id));
    let device = selected_or_default(
        device_id.as_deref(),
        selected,
        || host.default_input_device(),
        "input",
    )?;
    let resolved_device = device.id().map(|id| id.to_string()).unwrap_or_default();
    Ok(SelectedDevice {
        device,
        requested_device: device_id.clone().unwrap_or_default(),
        resolved_device,
        requested_default: requested_id.is_none(),
        requested_matched: true,
        fell_back_to_default: false,
    })
}

fn supported_sample_format(format: cpal::SampleFormat) -> bool {
    matches!(
        format,
        cpal::SampleFormat::F32 | cpal::SampleFormat::I16 | cpal::SampleFormat::U16
    )
}

fn input_config_score(
    channels: u16,
    format: cpal::SampleFormat,
    sample_rate: u32,
) -> (u8, u8, u32, u16) {
    let channel_rank = match channels {
        1 => 0,
        2 => 1,
        _ => 2,
    };
    let format_rank = match format {
        cpal::SampleFormat::F32 => 0,
        cpal::SampleFormat::I16 => 1,
        cpal::SampleFormat::U16 => 2,
        _ => u8::MAX,
    };
    (
        channel_rank,
        format_rank,
        sample_rate.abs_diff(SAMPLE_RATE),
        channels,
    )
}

fn pick_input_config(device: &cpal::Device) -> Result<cpal::SupportedStreamConfig, String> {
    if let Ok(default) = device.default_input_config() {
        let rate = default.sample_rate();
        if supported_sample_format(default.sample_format())
            && (MIN_REALTIME_CAPTURE_RATE..=MAX_REALTIME_CAPTURE_RATE).contains(&rate)
        {
            return Ok(default);
        }
    }

    let ranges = device
        .supported_input_configs()
        .map_err(|e| format!("supported input configs: {e}"))?;
    let mut best: Option<(cpal::SupportedStreamConfigRange, u32)> = None;
    for range in ranges {
        if !supported_sample_format(range.sample_format()) {
            continue;
        }
        let min_rate = range.min_sample_rate().max(MIN_REALTIME_CAPTURE_RATE);
        let max_rate = range.max_sample_rate().min(MAX_REALTIME_CAPTURE_RATE);
        if min_rate > max_rate {
            continue;
        }
        let rate = SAMPLE_RATE.clamp(min_rate, max_rate);
        let better = best.as_ref().is_none_or(|(current, current_rate)| {
            input_config_score(range.channels(), range.sample_format(), rate)
                < input_config_score(current.channels(), current.sample_format(), *current_rate)
        });
        if better {
            best = Some((range, rate));
        }
    }
    best.map(|(range, rate)| range.with_sample_rate(rate))
        .ok_or_else(|| "no supported input stream format".to_string())
}

fn supported_buffer_details(config: &cpal::SupportedStreamConfig) -> (String, u32, u32) {
    match config.buffer_size() {
        cpal::SupportedBufferSize::Range { min, max } => ("default-range".to_string(), *min, *max),
        cpal::SupportedBufferSize::Unknown => ("default-unknown".to_string(), 0, 0),
    }
}

const MIN_REALTIME_CAPTURE_RATE: u32 = 8_000;
const MAX_REALTIME_CAPTURE_RATE: u32 = 384_000;
const CAPTURE_PROCESS_CHUNK_FRAMES: usize = 256;
const UNKNOWN_CALLBACK_MAX_FRAMES: usize = 65_536;
const MAX_RESAMPLED_CHUNK_SAMPLES: usize =
    CAPTURE_PROCESS_CHUNK_FRAMES * SAMPLE_RATE as usize / MIN_REALTIME_CAPTURE_RATE as usize + 2;
const REALTIME_ACCUMULATOR_SAMPLES: usize = FRAME_SAMPLES + MAX_RESAMPLED_CHUNK_SAMPLES;
const REALTIME_TIMING_SEGMENTS: usize = 64;

fn callback_scratch_samples(buffer_max_frames: u32, channels: usize) -> Result<usize, String> {
    let frames = if buffer_max_frames == 0 {
        UNKNOWN_CALLBACK_MAX_FRAMES
    } else {
        (buffer_max_frames as usize).min(UNKNOWN_CALLBACK_MAX_FRAMES)
    };
    frames
        .checked_mul(channels.max(1))
        .ok_or_else(|| "audio callback scratch capacity overflow".to_string())
}

#[allow(clippy::too_many_arguments)]
pub fn spawn_cpal_capture(
    device_id: Option<String>,
    ring: CaptureFrameProducer,
    encoder_epoch: Arc<AtomicU64>,
    stop: Arc<AtomicBool>,
    healthy: Arc<AtomicBool>,
    aec_timing: Arc<AecTiming>,
    diagnostics: Arc<CaptureDiagnostics>,
    stream_generation: u64,
    media_events: SyncSender<MediaStateEvent>,
) -> Result<(), String> {
    aec_timing.reset_capture_path();
    let open_attempt = diagnostics.begin_open_attempt();
    let selected = pick_device(&device_id)?;
    let config = pick_input_config(&selected.device)?;
    let in_rate = config.sample_rate();
    let channels = config.channels() as usize;
    if !(MIN_REALTIME_CAPTURE_RATE..=MAX_REALTIME_CAPTURE_RATE).contains(&in_rate) {
        return Err(format!(
            "unsupported realtime input rate {in_rate}Hz (expected {MIN_REALTIME_CAPTURE_RATE}..={MAX_REALTIME_CAPTURE_RATE})"
        ));
    }
    let sample_format = config.sample_format();
    let (buffer_mode, buffer_min_frames, buffer_max_frames) = supported_buffer_details(&config);
    let descriptor = StreamDescriptor {
        requested_device: selected.requested_device.clone(),
        resolved_device: selected.resolved_device.clone(),
        requested_default: selected.requested_default,
        requested_matched: selected.requested_matched,
        fell_back_to_default: selected.fell_back_to_default,
        sample_rate: in_rate,
        channels: channels as u16,
        sample_format: format!("{sample_format:?}"),
        buffer_mode,
        buffer_min_frames,
        buffer_max_frames,
    };
    let errored = Arc::new(AtomicBool::new(false));
    let make_err = || {
        let ef = errored.clone();
        move |_e| {
            ef.store(true, Ordering::Relaxed);
        }
    };

    let cb_ring = ring;
    let cb_diagnostics = diagnostics.clone();
    let mut capture_clock = CaptureClockMapper::default();
    let mut downmixer = AdaptiveDownmixer::with_capacity(channels, CAPTURE_PROCESS_CHUNK_FRAMES);
    let mut resampler = Resampler::new(in_rate);
    resampler.reserve_realtime_input(CAPTURE_PROCESS_CHUNK_FRAMES);
    let mut accumulator =
        FrameAccumulator::with_capacity(REALTIME_ACCUMULATOR_SAMPLES, REALTIME_TIMING_SEGMENTS);
    let mut resampled_scratch = Vec::with_capacity(MAX_RESAMPLED_CHUNK_SAMPLES);
    let first_callback_ns = Arc::new(AtomicU64::new(0));
    let first_callback_frames = Arc::new(AtomicU64::new(0));
    let cb_first_callback_ns = first_callback_ns.clone();
    let cb_first_callback_frames = first_callback_frames.clone();
    let mut push = move |data: &[f32], info: &cpal::InputCallbackInfo| {
        // A callback is one capture transaction: every derived 20 ms frame retains the privacy
        // epoch that was current before this callback touched its PCM.
        let callback_encoder_epoch = encoder_epoch.load(Ordering::Acquire);
        let input_frames = data.len() / channels.max(1);
        let observation =
            aec_timing.observe_input_callback(&mut capture_clock, info, input_frames, in_rate);
        let first_callback =
            cb_diagnostics.observe_callback(observation.callback_mono_ns, input_frames, in_rate);
        if first_callback {
            cb_first_callback_frames.store(input_frames as u64, Ordering::Release);
            cb_first_callback_ns.store(observation.callback_mono_ns, Ordering::Release);
        }
        if cb_diagnostics.signal_windows_enabled() {
            cb_diagnostics.raw_input.record(data);
        }
        cb_diagnostics.observe_capture_clock(
            observation.clock_status,
            observation.capture_clock_delta_ns,
            observation.expected_capture_delta_ns,
            observation.capture_clock_delta_error_ns,
            observation.bridge_residual_ns,
        );
        if observation.discontinuity {
            cb_diagnostics.note_timestamp_discontinuity();
        }
        if !observation.valid {
            cb_diagnostics.note_invalid_timestamp();
        }
        let chunk_samples = CAPTURE_PROCESS_CHUNK_FRAMES.saturating_mul(channels.max(1));
        let mut produced_frames = 0;
        for (chunk_index, chunk) in data.chunks(chunk_samples).enumerate() {
            let complete_samples = chunk.len() - chunk.len() % channels.max(1);
            if complete_samples == 0 {
                continue;
            }
            let chunk = &chunk[..complete_samples];
            let frame_offset = chunk_index.saturating_mul(CAPTURE_PROCESS_CHUNK_FRAMES);
            let chunk_capture_ts_ns = observation.first_sample_mono_ns.saturating_add(
                (frame_offset as u64).saturating_mul(1_000_000_000) / u64::from(in_rate),
            );
            let mono = downmixer.process(chunk);
            let timing = resampler.process_timed_into(
                mono,
                chunk_capture_ts_ns,
                observation.callback_mono_ns,
                observation.valid,
                observation.frame_timing_valid,
                &mut resampled_scratch,
            );
            cb_diagnostics.observe_resampled_samples(resampled_scratch.len());
            produced_frames += accumulator.push_samples_and_drain_with(
                &resampled_scratch,
                timing,
                |timing, samples| {
                    let _ = cb_ring.push(
                        CaptureFrameMetadata {
                            encoder_epoch: callback_encoder_epoch,
                            capture_generation: stream_generation,
                            capture_open_attempt: open_attempt,
                            capture_ts_ns: timing.capture_ts_ns,
                            capture_callback_ts_ns: timing.callback_ts_ns,
                            capture_timestamp_valid: timing.valid,
                        },
                        samples,
                    );
                },
            );
        }
        let pending_samples = accumulator.pending_samples();
        cb_diagnostics.set_accumulator_pending(pending_samples);
        cb_diagnostics.observe_frames_produced(produced_frames);
        if produced_frames > 0 {
            cb_diagnostics.observe_ring_len(cb_ring.len());
        }
    };

    let stream_config: cpal::StreamConfig = config.into();
    let stream = match sample_format {
        cpal::SampleFormat::F32 => selected.device.build_input_stream(
            stream_config,
            move |data: &[f32], info| {
                push(data, info);
            },
            make_err(),
            None,
        ),
        cpal::SampleFormat::I16 => selected.device.build_input_stream(
            stream_config,
            {
                let max_samples = callback_scratch_samples(buffer_max_frames, channels)?;
                let mut scratch = Vec::with_capacity(max_samples);
                let oversize = errored.clone();
                move |data: &[i16], info| {
                    if data.len() > scratch.capacity() {
                        oversize.store(true, Ordering::Relaxed);
                        return;
                    }
                    scratch.clear();
                    scratch.extend(data.iter().map(|&sample| sample as f32 / 32768.0));
                    push(&scratch, info);
                }
            },
            make_err(),
            None,
        ),
        cpal::SampleFormat::U16 => selected.device.build_input_stream(
            stream_config,
            {
                let max_samples = callback_scratch_samples(buffer_max_frames, channels)?;
                let mut scratch = Vec::with_capacity(max_samples);
                let oversize = errored.clone();
                move |data: &[u16], info| {
                    if data.len() > scratch.capacity() {
                        oversize.store(true, Ordering::Relaxed);
                        return;
                    }
                    scratch.clear();
                    scratch.extend(
                        data.iter()
                            .map(|&sample| (sample as f32 - 32768.0) / 32768.0),
                    );
                    push(&scratch, info);
                }
            },
            make_err(),
            None,
        ),
        fmt => return Err(format!("unsupported sample format: {fmt:?}")),
    }
    .map_err(|e| format!("build input stream: {e}"))?;

    let started_ns = monotonic_ns();
    stream.play().map_err(|e| format!("stream play: {e}"))?;
    diagnostics.mark_stream_started(started_ns, descriptor.clone());
    send_media_state(
        &media_events,
        MediaStateEvent {
            direction: "capture".to_string(),
            state: "stream-started".to_string(),
            command_seq: diagnostics.current_command_seq(),
            stream_generation,
            open_attempt,
            running: true,
            requested_device: descriptor.requested_device,
            resolved_device: descriptor.resolved_device,
            requested_default: descriptor.requested_default,
            requested_matched: descriptor.requested_matched,
            fell_back_to_default: descriptor.fell_back_to_default,
            sample_rate: descriptor.sample_rate,
            channels: descriptor.channels,
            sample_format: descriptor.sample_format,
            buffer_mode: descriptor.buffer_mode,
            ..Default::default()
        },
    );
    healthy.store(true, Ordering::Relaxed);
    let mut first_callback_reported = false;
    while !stop.load(Ordering::Relaxed) {
        if !first_callback_reported {
            let callback_ns = first_callback_ns.load(Ordering::Acquire);
            if callback_ns != 0 {
                send_media_state(
                    &media_events,
                    MediaStateEvent {
                        direction: "capture".to_string(),
                        state: "first-callback".to_string(),
                        command_seq: diagnostics.current_command_seq(),
                        stream_generation,
                        open_attempt,
                        running: true,
                        callback_frames: first_callback_frames.load(Ordering::Acquire),
                        elapsed_ms: callback_ns.saturating_sub(started_ns) / 1_000_000,
                        ..Default::default()
                    },
                );
                first_callback_reported = true;
            }
        }
        if errored.load(Ordering::Relaxed) {
            drop(stream);
            return Err("input device error (disconnected?)".into());
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
    drop(stream);
    Ok(())
}

pub fn enumerate_output_devices() -> Vec<DeviceInfo> {
    let host = cpal::default_host();
    let default_id = host
        .default_output_device()
        .and_then(|device| device.id().ok())
        .map(|id| id.to_string());
    let mut out = Vec::new();
    if let Ok(devices) = host.output_devices() {
        for device in devices {
            let id = match device.id() {
                Ok(id) => id.to_string(),
                Err(_) => continue,
            };
            let name = match device
                .description()
                .ok()
                .and_then(|description| device_display_name(&description))
            {
                Some(name) => name,
                _ => continue,
            };
            if pick_output_config(&device).is_err() {
                continue;
            }
            let is_default = Some(&id) == default_id.as_ref();
            out.push(DeviceInfo {
                id,
                name,
                default: is_default,
            });
        }
    }
    out.sort_by_key(|d| std::cmp::Reverse(d.default));
    out
}

fn pick_output_device(
    host: &cpal::Host,
    device_id: &Option<String>,
) -> Result<SelectedDevice, String> {
    let requested_id = device_id.as_deref().filter(|id| !id.is_empty());
    let selected = requested_id
        .and_then(|id| id.parse::<cpal::DeviceId>().ok())
        .and_then(|id| host.device_by_id(&id));
    let device = selected_or_default(
        device_id.as_deref(),
        selected,
        || host.default_output_device(),
        "output",
    )?;
    let resolved_device = device.id().map(|id| id.to_string()).unwrap_or_default();
    Ok(SelectedDevice {
        device,
        requested_device: device_id.clone().unwrap_or_default(),
        resolved_device,
        requested_default: requested_id.is_none(),
        requested_matched: true,
        fell_back_to_default: false,
    })
}

fn pick_output_config(device: &cpal::Device) -> Result<cpal::SupportedStreamConfig, String> {
    if let Ok(ranges) = device.supported_output_configs() {
        let mut best: Option<cpal::SupportedStreamConfigRange> = None;
        for r in ranges {
            if !supported_sample_format(r.sample_format()) {
                continue;
            }
            if r.min_sample_rate() <= SAMPLE_RATE && r.max_sample_rate() >= SAMPLE_RATE {
                let better = match &best {
                    None => true,
                    Some(b) => {
                        output_config_score(r.channels(), r.sample_format())
                            < output_config_score(b.channels(), b.sample_format())
                    }
                };
                if better {
                    best = Some(r);
                }
            }
        }
        if let Some(r) = best {
            return Ok(r.with_sample_rate(SAMPLE_RATE));
        }
    }
    let default = device
        .default_output_config()
        .map_err(|e| format!("default output config: {e}"))?;
    if !supported_sample_format(default.sample_format()) {
        return Err(format!(
            "unsupported default output sample format: {:?}",
            default.sample_format()
        ));
    }
    Ok(default)
}

fn output_config_score(channels: u16, format: cpal::SampleFormat) -> (u8, u8, u16) {
    let channel_rank = match channels {
        2 => 0, // Preserve spatial pan without relying on an ambiguous multichannel layout.
        1 => 1,
        _ => 2,
    };
    let format_rank = match format {
        cpal::SampleFormat::F32 => 0,
        cpal::SampleFormat::I16 => 1,
        cpal::SampleFormat::U16 => 2,
        _ => 3,
    };
    (channel_rank, format_rank, channels)
}

fn write_out_frame(out: &mut [f32], frame: usize, channels: usize, l: f32, r: f32) {
    let base = frame * channels;
    if channels == 1 {
        out[base] = (l + r) * 0.5;
        return;
    }
    out[base..base + channels].fill(0.0);
    out[base] = l;
    out[base + 1] = r;
    match channels {
        // Common channel orders: 3.0 = FL/FR/FC, quad = FL/FR/RL/RR,
        // 5.0 = FL/FR/FC/RL/RR, 5.1+ = FL/FR/FC/LFE/SL/SR/...
        3 => out[base + 2] = (l + r) * 0.5,
        4 => {
            out[base + 2] = l * 0.5;
            out[base + 3] = r * 0.5;
        }
        5 => {
            out[base + 2] = (l + r) * 0.5;
            out[base + 3] = l * 0.5;
            out[base + 4] = r * 0.5;
        }
        6.. => {
            out[base + 2] = (l + r) * 0.5;
            // Keep LFE silent; voice-band content does not belong there.
            out[base + 4] = l * 0.5;
            out[base + 5] = r * 0.5;
        }
        _ => {}
    }
}

const UNDERRUN_FADE_SAMPLES: u32 = 240; // 5 ms at the internal 48 kHz clock.
const UNDERRUN_RESUME_SAMPLES: u32 = 96; // 2 ms crossfade when audio returns.

#[derive(Default)]
struct UnderrunFader {
    last_output: (f32, f32),
    fade_from: (f32, f32),
    fade_remaining: u32,
    resume_remaining: u32,
    resume_from: (f32, f32),
    starved: bool,
}

impl UnderrunFader {
    fn process(&mut self, pair: (f32, f32), available: bool) -> (f32, f32) {
        let output = if !available {
            if !self.starved {
                self.starved = true;
                self.fade_remaining = UNDERRUN_FADE_SAMPLES;
                self.fade_from = self.last_output;
                self.resume_remaining = 0;
            }
            if self.fade_remaining == 0 {
                (0.0, 0.0)
            } else {
                let factor = self.fade_remaining as f32 / UNDERRUN_FADE_SAMPLES as f32;
                self.fade_remaining -= 1;
                (self.fade_from.0 * factor, self.fade_from.1 * factor)
            }
        } else {
            if self.starved {
                self.starved = false;
                self.resume_remaining = UNDERRUN_RESUME_SAMPLES;
                self.resume_from = self.last_output;
            }
            if self.resume_remaining == 0 {
                pair
            } else {
                let progress = 1.0 - self.resume_remaining as f32 / UNDERRUN_RESUME_SAMPLES as f32;
                self.resume_remaining -= 1;
                (
                    self.resume_from.0 + (pair.0 - self.resume_from.0) * progress,
                    self.resume_from.1 + (pair.1 - self.resume_from.1) * progress,
                )
            }
        };
        self.last_output = output;
        output
    }
}

#[allow(clippy::too_many_arguments)]
pub fn spawn_cpal_playback(
    device_id: Option<String>,
    playback: Arc<Mutex<PlaybackRing>>,
    stop: Arc<AtomicBool>,
    counters: Arc<NativeCounters>,
    progress: Arc<PlaybackProgress>,
    aec_timing: Arc<AecTiming>,
    diagnostics: Arc<PlaybackDiagnostics>,
    stream_generation: u64,
    media_events: SyncSender<MediaStateEvent>,
) -> Result<(), String> {
    let host = cpal::default_host();
    let selected = pick_output_device(&host, &device_id)?;
    let config = pick_output_config(&selected.device)?;
    let out_channels = config.channels() as usize;
    let out_rate = config.sample_rate();
    let sample_format = config.sample_format();
    let (buffer_mode, buffer_min_frames, buffer_max_frames) = supported_buffer_details(&config);
    let descriptor = StreamDescriptor {
        requested_device: selected.requested_device.clone(),
        resolved_device: selected.resolved_device.clone(),
        requested_default: selected.requested_default,
        requested_matched: selected.requested_matched,
        fell_back_to_default: selected.fell_back_to_default,
        sample_rate: out_rate,
        channels: out_channels as u16,
        sample_format: format!("{sample_format:?}"),
        buffer_mode,
        buffer_min_frames,
        buffer_max_frames,
    };
    let stream_config: cpal::StreamConfig = config.into();
    let ratio = SAMPLE_RATE as f64 / out_rate.max(1) as f64;

    // Take a lock-free consumer handle once. The outer mutex remains for control-path ABI
    // compatibility, but is never touched by the hardware callback.
    let cb_ring = playback
        .lock()
        .map_err(|_| "playback ring lock poisoned".to_string())?
        .consumer();
    let target_pairs = (FRAME_SAMPLES * 4) as f64;
    let mut s0 = (0.0f32, 0.0f32);
    let mut s1 = (0.0f32, 0.0f32);
    let mut s0_available = false;
    let mut s1_available = false;
    let mut pos = 1.0f64;
    let mut underrun_fader = UnderrunFader::default();
    let fill_counters = counters.clone();
    let fill_progress = progress.clone();
    let fill_timing = aec_timing.clone();
    let fill_diagnostics = diagnostics.clone();
    let first_callback_ns = Arc::new(AtomicU64::new(0));
    let first_callback_frames = Arc::new(AtomicU64::new(0));
    let fill_first_callback_ns = first_callback_ns.clone();
    let fill_first_callback_frames = first_callback_frames.clone();
    let mut fill = move |out: &mut [f32], info: &cpal::OutputCallbackInfo| {
        let frames = out.len() / out_channels.max(1);
        let callback_ns = monotonic_ns();
        if fill_diagnostics.observe_callback(callback_ns, frames) {
            fill_first_callback_frames.store(frames as u64, Ordering::Release);
            fill_first_callback_ns.store(callback_ns, Ordering::Release);
        }
        fill_timing.observe_output_callback(info);
        fill_progress.mark_callback();
        fill_counters
            .playback_callbacks
            .fetch_add(1, Ordering::Relaxed);
        let err = (cb_ring.len() as f64 - target_pairs) / target_pairs;
        let eff_ratio = ratio * (1.0 + (err * 0.05).clamp(-0.004, 0.004));
        let mut requested_pairs = 0u64;
        let mut consumed_pairs = 0u64;
        let mut underrun_pairs = 0u64;
        for f in 0..frames {
            while pos >= 1.0 {
                s0 = s1;
                s0_available = s1_available;
                requested_pairs += 1;
                match cb_ring.pop_stereo() {
                    Some(pair) => {
                        s1 = pair;
                        s1_available = true;
                        consumed_pairs += 1;
                    }
                    None => {
                        s1 = (0.0, 0.0);
                        s1_available = false;
                        underrun_pairs += 1;
                    }
                }
                pos -= 1.0;
            }
            let t = pos as f32;
            let l = s0.0 + (s1.0 - s0.0) * t;
            let r = s0.1 + (s1.1 - s0.1) * t;
            let (l, r) = underrun_fader.process((l, r), s0_available || s1_available);
            write_out_frame(out, f, out_channels, l, r);
            pos += eff_ratio;
        }
        fill_counters
            .playback_requested_pairs
            .fetch_add(requested_pairs, Ordering::Relaxed);
        fill_counters
            .playback_consumed_pairs
            .fetch_add(consumed_pairs, Ordering::Relaxed);
        fill_counters
            .playback_underrun_pairs
            .fetch_add(underrun_pairs, Ordering::Relaxed);
        fill_counters.record_playback_output(out);
    };

    let errored = Arc::new(AtomicBool::new(false));
    let make_err = || {
        let es = stop.clone();
        let errored = errored.clone();
        move |_e| {
            errored.store(true, Ordering::Release);
            es.store(true, Ordering::Relaxed);
        }
    };

    let stream = match sample_format {
        cpal::SampleFormat::F32 => selected.device.build_output_stream(
            stream_config,
            move |data: &mut [f32], info| fill(data, info),
            make_err(),
            None,
        ),
        cpal::SampleFormat::I16 => {
            let mut scratch = vec![0.0; callback_scratch_samples(buffer_max_frames, out_channels)?];
            let oversize = errored.clone();
            let oversize_stop = stop.clone();
            selected.device.build_output_stream(
                stream_config,
                move |data: &mut [i16], info| {
                    let data_len = data.len();
                    if data_len > scratch.len() {
                        data.fill(0);
                        oversize.store(true, Ordering::Release);
                        oversize_stop.store(true, Ordering::Release);
                        return;
                    }
                    fill(&mut scratch[..data_len], info);
                    for (d, &s) in data.iter_mut().zip(&scratch[..data_len]) {
                        *d = (s.clamp(-1.0, 1.0) * 32767.0) as i16;
                    }
                },
                make_err(),
                None,
            )
        }
        cpal::SampleFormat::U16 => {
            let mut scratch = vec![0.0; callback_scratch_samples(buffer_max_frames, out_channels)?];
            let oversize = errored.clone();
            let oversize_stop = stop.clone();
            selected.device.build_output_stream(
                stream_config,
                move |data: &mut [u16], info| {
                    let data_len = data.len();
                    if data_len > scratch.len() {
                        data.fill(32768);
                        oversize.store(true, Ordering::Release);
                        oversize_stop.store(true, Ordering::Release);
                        return;
                    }
                    fill(&mut scratch[..data_len], info);
                    for (d, &s) in data.iter_mut().zip(&scratch[..data_len]) {
                        *d = ((s.clamp(-1.0, 1.0) * 32767.0) + 32768.0) as u16;
                    }
                },
                make_err(),
                None,
            )
        }
        fmt => return Err(format!("unsupported output sample format: {fmt:?}")),
    }
    .map_err(|e| format!("build output stream: {e}"))?;

    let started_ns = monotonic_ns();
    stream
        .play()
        .map_err(|e| format!("output stream play: {e}"))?;
    diagnostics.mark_stream_started(descriptor.clone());
    send_media_state(
        &media_events,
        MediaStateEvent {
            direction: "playback".to_string(),
            state: "stream-started".to_string(),
            stream_generation,
            running: true,
            requested_device: descriptor.requested_device,
            resolved_device: descriptor.resolved_device,
            requested_default: descriptor.requested_default,
            requested_matched: descriptor.requested_matched,
            fell_back_to_default: descriptor.fell_back_to_default,
            sample_rate: descriptor.sample_rate,
            channels: descriptor.channels,
            sample_format: descriptor.sample_format,
            buffer_mode: descriptor.buffer_mode,
            ..Default::default()
        },
    );
    progress.mark_started();
    counters.playback_starts.fetch_add(1, Ordering::Relaxed);
    let mut first_callback_reported = false;
    while !stop.load(Ordering::Relaxed) {
        if !first_callback_reported {
            let callback_ns = first_callback_ns.load(Ordering::Acquire);
            if callback_ns != 0 {
                send_media_state(
                    &media_events,
                    MediaStateEvent {
                        direction: "playback".to_string(),
                        state: "first-callback".to_string(),
                        stream_generation,
                        running: true,
                        callback_frames: first_callback_frames.load(Ordering::Acquire),
                        elapsed_ms: callback_ns.saturating_sub(started_ns) / 1_000_000,
                        ..Default::default()
                    },
                );
                first_callback_reported = true;
            }
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
    drop(stream);
    if errored.load(Ordering::Acquire) {
        counters
            .playback_callback_errors
            .fetch_add(1, Ordering::Relaxed);
        return Err("output device callback failed".to_string());
    }
    counters.playback_stops.fetch_add(1, Ordering::Relaxed);
    diagnostics.mark_stopped();
    send_media_state(
        &media_events,
        MediaStateEvent {
            direction: "playback".to_string(),
            state: "stopped".to_string(),
            stream_generation,
            running: false,
            ..Default::default()
        },
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn explicit_device_selection_never_falls_back_to_default() {
        let mut default_called = false;
        let result = selected_or_default(
            Some("missing-stable-id"),
            None::<u8>,
            || {
                default_called = true;
                Some(7)
            },
            "input",
        );

        assert_eq!(result.unwrap_err(), "selected input device is unavailable");
        assert!(!default_called);
    }

    #[test]
    fn empty_device_selection_explicitly_uses_default() {
        assert_eq!(
            selected_or_default(Some(""), None, || Some(7u8), "output").unwrap(),
            7
        );
        assert_eq!(
            selected_or_default(None, None, || Some(9u8), "input").unwrap(),
            9
        );
    }

    #[test]
    fn matching_explicit_device_does_not_probe_default() {
        let selected = selected_or_default(
            Some("stable-id"),
            Some(11u8),
            || panic!("default device must not be used for an explicit selection"),
            "output",
        )
        .unwrap();
        assert_eq!(selected, 11);
    }

    #[test]
    fn windows_device_label_prefers_endpoint_friendly_name() {
        let extended = vec!["Microphone (Razer Seiren Mini)".to_string()];
        assert_eq!(
            choose_device_display_name("Microphone", &extended, true).as_deref(),
            Some("Microphone (Razer Seiren Mini)")
        );
    }

    #[test]
    fn windows_device_label_ignores_blank_extended_lines_and_falls_back() {
        let extended = vec!["  ".to_string(), "".to_string()];
        assert_eq!(
            choose_device_display_name("  Headphones  ", &extended, true).as_deref(),
            Some("Headphones")
        );
    }

    #[test]
    fn non_windows_device_label_keeps_primary_name() {
        let extended = vec!["diagnostic metadata".to_string()];
        assert_eq!(
            choose_device_display_name("Built-in Audio Analog Stereo", &extended, false).as_deref(),
            Some("Built-in Audio Analog Stereo")
        );
    }

    #[test]
    fn device_label_rejects_empty_presentation_data() {
        assert_eq!(choose_device_display_name("  ", &[], true), None);
    }
    use crate::proto::{FRAME_SAMPLES, SAMPLE_RATE};

    fn timed_frame(capture_ns: u64, callback_ns: u64, valid: bool) -> AudioFrame {
        AudioFrame {
            encoder_epoch: 0,
            capture_generation: 0,
            capture_open_attempt: 0,
            capture_ts_ns: capture_ns,
            capture_callback_ts_ns: callback_ns,
            capture_timestamp_valid: valid,
            samples: vec![0.0; FRAME_SAMPLES],
        }
    }

    fn block(samples: usize, capture_ns: u64, callback_ns: u64) -> ResampledBlock {
        ResampledBlock {
            samples: vec![0.0; samples],
            first_capture_ts_ns: capture_ns,
            capture_callback_ts_ns: callback_ns,
            timing_valid: true,
        }
    }

    #[test]
    fn callback_scratch_is_bounded_and_channel_sized() {
        assert_eq!(callback_scratch_samples(480, 2).unwrap(), 960);
        assert_eq!(
            callback_scratch_samples(0, 2).unwrap(),
            UNKNOWN_CALLBACK_MAX_FRAMES * 2
        );
        assert_eq!(
            callback_scratch_samples(u32::MAX, 6).unwrap(),
            UNKNOWN_CALLBACK_MAX_FRAMES * 6
        );
        assert!(callback_scratch_samples(2, usize::MAX).is_err());
    }

    #[test]
    fn realtime_capture_pipeline_retains_preallocated_capacities() {
        for rate in [
            MIN_REALTIME_CAPTURE_RATE,
            SAMPLE_RATE,
            MAX_REALTIME_CAPTURE_RATE,
        ] {
            let mut downmixer = AdaptiveDownmixer::with_capacity(2, CAPTURE_PROCESS_CHUNK_FRAMES);
            let downmix_capacity = downmixer.scratch.capacity();
            let interleaved = vec![0.1f32; CAPTURE_PROCESS_CHUNK_FRAMES * 2];

            let mut resampler = Resampler::new(rate);
            resampler.reserve_realtime_input(CAPTURE_PROCESS_CHUNK_FRAMES);
            let source_capacity = resampler.source.capacity();
            let mut resampled = Vec::with_capacity(MAX_RESAMPLED_CHUNK_SAMPLES);
            let resampled_capacity = resampled.capacity();

            let mut accumulator = FrameAccumulator::with_capacity(
                REALTIME_ACCUMULATOR_SAMPLES,
                REALTIME_TIMING_SEGMENTS,
            );
            let accumulator_capacity = accumulator.buf.capacity();
            let timeline_capacity = accumulator.timeline.capacity();
            let mut emitted = 0usize;

            for callback in 0..2_000u64 {
                let mono = downmixer.process(&interleaved);
                let timing = resampler.process_timed_into(
                    mono,
                    callback * 1_000_000,
                    callback * 1_000_000 + 500_000,
                    true,
                    true,
                    &mut resampled,
                );
                emitted += accumulator.push_samples_and_drain_with(
                    &resampled,
                    timing,
                    |_timing, frame| assert_eq!(frame.len(), FRAME_SAMPLES),
                );
            }

            assert!(emitted > 0);
            assert_eq!(downmixer.scratch.capacity(), downmix_capacity);
            assert_eq!(resampler.source.capacity(), source_capacity);
            assert_eq!(resampled.capacity(), resampled_capacity);
            assert_eq!(accumulator.buf.capacity(), accumulator_capacity);
            assert_eq!(accumulator.timeline.capacity(), timeline_capacity);
        }
    }

    #[test]
    fn capture_clock_bridge_uses_backend_capture_deltas_not_callback_latency_jitter() {
        let mut bridge = CaptureClockBridge::default();
        let first = bridge.observe(
            2_008_000_000,
            Some(8_000_000),
            BackendCaptureStep::First,
            480,
            48_000,
        );
        assert_eq!(first.first_sample_mono_ns, 2_000_000_000);
        assert!(first.frame_timing_valid);
        assert_eq!(first.clock_status, CaptureClockStatus::Anchored);

        // The fresh callback-minus-latency estimate jumps backwards by 10ms, matching the
        // WASAPI behavior seen in the paired logs. The hardware capture clock itself advances by
        // exactly one buffer, so the stable bridge must preserve that 10ms sample timeline.
        let second = bridge.observe(
            2_018_000_000,
            Some(18_000_000),
            BackendCaptureStep::Forward(10_000_000),
            480,
            48_000,
        );
        assert_eq!(second.first_sample_mono_ns, 2_010_000_000);
        assert_eq!(second.bridge_residual_ns, Some(-10_000_000));
        assert_eq!(second.capture_clock_delta_ns, Some(10_000_000));
        assert_eq!(second.expected_capture_delta_ns, Some(10_000_000));
        assert_eq!(second.capture_clock_delta_error_ns, Some(0));
        assert!(second.frame_timing_valid);
        assert!(!second.discontinuity);
        assert_eq!(second.clock_status, CaptureClockStatus::Continuous);
    }

    #[test]
    fn capture_clock_bridge_flags_a_real_backend_gap_once_then_recovers() {
        let mut bridge = CaptureClockBridge::default();
        bridge.observe(
            3_008_000_000,
            Some(8_000_000),
            BackendCaptureStep::First,
            480,
            48_000,
        );
        let gap = bridge.observe(
            3_038_000_000,
            Some(8_000_000),
            BackendCaptureStep::Forward(30_000_000),
            480,
            48_000,
        );
        assert_eq!(gap.first_sample_mono_ns, 3_030_000_000);
        assert_eq!(gap.capture_clock_delta_error_ns, Some(20_000_000));
        assert!(gap.discontinuity);
        assert!(!gap.frame_timing_valid);
        assert_eq!(gap.clock_status, CaptureClockStatus::DeltaMismatch);

        let recovered = bridge.observe(
            3_048_000_000,
            Some(8_000_000),
            BackendCaptureStep::Forward(10_000_000),
            480,
            48_000,
        );
        assert_eq!(recovered.first_sample_mono_ns, 3_040_000_000);
        assert!(!recovered.discontinuity);
        assert!(recovered.frame_timing_valid);
        assert_eq!(recovered.clock_status, CaptureClockStatus::Continuous);
    }

    #[test]
    fn capture_clock_bridge_invalidates_one_boundary_when_timing_recovers() {
        let mut bridge = CaptureClockBridge::default();
        bridge.observe(
            4_008_000_000,
            Some(8_000_000),
            BackendCaptureStep::First,
            480,
            48_000,
        );
        let invalid = bridge.observe(
            4_018_000_000,
            None,
            BackendCaptureStep::Forward(10_000_000),
            480,
            48_000,
        );
        assert!(!invalid.valid);
        assert_eq!(invalid.clock_status, CaptureClockStatus::InvalidLatency);

        let boundary = bridge.observe(
            4_028_000_000,
            Some(8_000_000),
            BackendCaptureStep::Forward(10_000_000),
            480,
            48_000,
        );
        assert!(boundary.valid);
        assert!(!boundary.frame_timing_valid);
        assert!(!boundary.discontinuity);
        assert_eq!(boundary.clock_status, CaptureClockStatus::Recovered);

        let continuous = bridge.observe(
            4_038_000_000,
            Some(8_000_000),
            BackendCaptureStep::Forward(10_000_000),
            480,
            48_000,
        );
        assert!(continuous.frame_timing_valid);
        assert_eq!(continuous.clock_status, CaptureClockStatus::Continuous);
    }

    #[test]
    fn capture_clock_bridge_reanchors_a_reversed_backend_clock() {
        let mut bridge = CaptureClockBridge::default();
        bridge.observe(
            5_008_000_000,
            Some(8_000_000),
            BackendCaptureStep::First,
            480,
            48_000,
        );
        let reversed = bridge.observe(
            5_018_000_000,
            Some(8_000_000),
            BackendCaptureStep::Reversed,
            480,
            48_000,
        );
        assert!(reversed.valid);
        assert!(reversed.discontinuity);
        assert!(!reversed.frame_timing_valid);
        assert_eq!(reversed.first_sample_mono_ns, 5_010_000_000);
        assert_eq!(
            reversed.clock_status,
            CaptureClockStatus::BackendClockReversed
        );

        let next = bridge.observe(
            5_028_000_000,
            Some(8_000_000),
            BackendCaptureStep::Forward(10_000_000),
            480,
            48_000,
        );
        assert_eq!(next.first_sample_mono_ns, 5_020_000_000);
        assert!(next.frame_timing_valid);
        assert_eq!(next.clock_status, CaptureClockStatus::Continuous);
    }

    #[test]
    fn aec_timing_combines_hardware_queue_and_processing_delay() {
        let timing = AecTiming::default();
        timing.observe_input_latency_for_test(Duration::from_millis(12), 1_000_000_000);
        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);

        let frame = timed_frame(988_000_000, 1_000_000_000, true);
        let snapshot = timing.snapshot_for_capture(1_004_000_000, &frame);
        assert!(snapshot.timing_complete);
        assert!(snapshot.frame_timestamp_valid);
        assert_eq!(snapshot.input_latency_ms, 12);
        assert_eq!(snapshot.output_latency_ms, 18);
        assert_eq!(snapshot.render_queue_ms, 20);
        assert_eq!(snapshot.capture_processing_ms, 4);
        assert_eq!(snapshot.capture_path_ms, 16);
        assert_eq!(snapshot.measured_delay_ms, 57);
        assert_eq!(snapshot.recommended_delay_ms, 57);
    }

    #[test]
    fn aec_timing_uses_safe_startup_delay_until_measurements_are_current() {
        let timing = AecTiming::default();
        let startup = timing.snapshot(1_000_000_000);
        assert!(!startup.timing_complete);
        assert!(!startup.last_frame_processed_present);
        assert_eq!(startup.recommended_delay_ms, DEFAULT_AEC_DELAY_MS);

        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);
        let frame = timed_frame(988_000_000, 1_000_000_000, true);
        timing.snapshot_for_capture(1_004_000_000, &frame);
        let stale = timing.snapshot(1_004_000_000 + AEC_CAPTURE_CALLBACK_STALE_NS + 1);
        assert!(!stale.timing_complete);
        assert!(stale.last_frame_processed_present);
        assert_eq!(stale.recommended_delay_ms, 57);
    }

    #[test]
    fn aec_timing_clamps_extreme_measurements() {
        let timing = AecTiming::default();
        timing.observe_output_latency_for_test(Duration::from_millis(900));
        timing.observe_render_queue_pairs(usize::MAX);

        let frame = timed_frame(1_000_000_000, 1_100_000_000, true);
        let snapshot = timing.snapshot_for_capture(1_500_000_000, &frame);
        assert!(snapshot.timing_complete);
        assert_eq!(snapshot.measured_delay_ms, MAX_AEC_DELAY_MS);
        assert_eq!(snapshot.recommended_delay_ms, MAX_AEC_DELAY_MS as i32);
    }

    #[test]
    fn aec_timing_uses_the_processed_frame_not_the_latest_callback() {
        let timing = AecTiming::default();
        // Simulate a newer callback arriving while an older frame remains queued.
        timing.observe_input_latency_for_test(Duration::from_millis(12), 1_090_000_000);
        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);
        let queued = timed_frame(1_000_000_000, 1_012_000_000, true);

        let snapshot = timing.snapshot_for_capture(1_100_000_000, &queued);

        assert_eq!(snapshot.input_latency_ms, 12);
        assert_eq!(snapshot.capture_processing_ms, 88);
        assert_eq!(snapshot.capture_path_ms, 100);
        assert_eq!(snapshot.measured_delay_ms, 141);
        assert_eq!(snapshot.fallback_reason, "complete");
        assert!(snapshot.input_timing_present);
        assert!(snapshot.output_timing_present);
        assert!(snapshot.render_timing_present);
        assert!(snapshot.capture_path_present);
    }

    #[test]
    fn aec_invalid_adc_timestamp_still_uses_its_own_queued_callback_age() {
        let timing = AecTiming::default();
        timing.observe_input_latency_for_test(Duration::from_millis(12), 1_090_000_000);
        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);
        let queued = timed_frame(0, 1_012_000_000, false);

        let snapshot = timing.snapshot_for_capture(1_100_000_000, &queued);

        assert_eq!(snapshot.capture_processing_ms, 88);
        assert_eq!(snapshot.capture_path_ms, 100);
        assert_eq!(snapshot.fallback_reason, "capture-callback-fallback");
    }

    #[test]
    fn aec_timing_does_not_double_count_input_latency() {
        let timing = AecTiming::default();
        timing.observe_output_latency_for_test(Duration::from_millis(10));
        timing.observe_render_queue_pairs(0);
        let frame = timed_frame(1_000_000_000, 1_020_000_000, true);

        let snapshot = timing.snapshot_for_capture(1_025_000_000, &frame);

        assert_eq!(snapshot.input_latency_ms, 20);
        assert_eq!(snapshot.capture_processing_ms, 5);
        assert_eq!(snapshot.capture_path_ms, 25);
        assert_eq!(snapshot.measured_delay_ms, 38); // 10 output + 25 capture + 3 acoustic
    }

    #[test]
    fn aec_timing_rejects_future_frame_timestamp() {
        let timing = AecTiming::default();
        timing.observe_output_latency_for_test(Duration::from_millis(10));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);
        let future = timed_frame(2_000_000_000, 2_010_000_000, true);

        let snapshot = timing.snapshot_for_capture(1_000_000_000, &future);

        assert!(!snapshot.frame_timestamp_valid);
        assert!(!snapshot.timing_complete);
        assert_eq!(snapshot.recommended_delay_ms, DEFAULT_AEC_DELAY_MS);
        assert_eq!(snapshot.invalid_frame_timestamp_samples, 1);
        assert_eq!(snapshot.fallback_reason, "missing-capture-timing");
    }

    #[test]
    fn aec_timing_tracks_applied_delay_for_diagnostics() {
        let timing = AecTiming::default();
        assert_eq!(timing.applied_delay_ms(), None);
        timing.note_applied_delay(73);
        assert_eq!(timing.applied_delay_ms(), Some(73));
        assert_eq!(timing.applied_delay_frames(), 1);
    }

    #[test]
    fn aec_capture_reset_preserves_render_measurements_but_drops_stale_capture_state() {
        let timing = AecTiming::default();
        timing.observe_input_latency_for_test(Duration::from_millis(12), 1_010_000_000);
        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);
        timing.note_applied_delay(73);

        timing.reset_capture_path();
        let snapshot = timing.snapshot(1_020_000_000);

        assert_eq!(snapshot.output_latency_ms, 18);
        assert_eq!(snapshot.render_queue_ms, 20);
        assert_eq!(snapshot.input_latency_ms, 0);
        assert!(!snapshot.timing_complete);
        assert!(!snapshot.frame_timestamp_valid);
        assert_eq!(timing.applied_delay_ms(), None);
    }

    #[test]
    fn aec_playback_reset_invalidates_output_and_render_measurements() {
        let timing = AecTiming::default();
        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);
        let previous_epoch = timing.snapshot(1_010_000_000).playback_timing_epoch;

        timing.reset_playback_path();
        let snapshot = timing.snapshot(1_020_000_000);

        assert_eq!(snapshot.playback_timing_epoch, previous_epoch + 1);
        assert!(!snapshot.output_timing_present);
        assert!(!snapshot.render_timing_present);
        assert_eq!(snapshot.output_latency_ms, 0);
        assert_eq!(snapshot.render_queue_ms, 0);
        assert!(!snapshot.timing_complete);
    }

    #[test]
    fn clearing_playback_measurements_preserves_the_current_epoch() {
        let timing = AecTiming::default();
        timing.reset_playback_path();
        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);
        let previous_epoch = timing.snapshot(1_010_000_000).playback_timing_epoch;

        timing.clear_playback_measurements();
        let snapshot = timing.snapshot(1_020_000_000);

        assert_eq!(snapshot.playback_timing_epoch, previous_epoch);
        assert!(!snapshot.output_timing_present);
        assert!(!snapshot.render_timing_present);
        assert_eq!(snapshot.output_latency_ms, 0);
        assert_eq!(snapshot.render_queue_ms, 0);
        assert!(!snapshot.timing_complete);
    }

    #[test]
    fn downmix_stereo_averages_channels() {
        let interleaved = [1.0f32, 0.8, 0.0, 0.0, 0.5, 0.4];
        let mono = downmix_to_mono(&interleaved, 2);
        assert_eq!(mono, vec![0.9, 0.0, 0.45]);
    }

    #[test]
    fn downmix_mono_is_identity() {
        let mono = downmix_to_mono(&[0.1, 0.2, 0.3], 1);
        assert_eq!(mono, vec![0.1, 0.2, 0.3]);
    }

    #[test]
    fn downmix_preserves_one_sided_and_antiphase_microphones() {
        let one_sided = downmix_to_mono(&[0.0, 0.8, 0.0, -0.6, 0.0, 0.4], 2);
        assert_eq!(one_sided, vec![0.8, -0.6, 0.4]);

        let antiphase = downmix_to_mono(&[0.7, -0.7, -0.4, 0.4, 0.2, -0.2], 2);
        assert_eq!(antiphase, vec![0.7, -0.4, 0.2]);
    }

    #[test]
    fn downmix_dominant_channel_has_block_hysteresis() {
        let mut downmixer = AdaptiveDownmixer::new(2);
        assert_eq!(downmixer.process(&[0.0, 0.8, 0.0, -0.8]), &[0.8, -0.8]);
        // A small energy advantage on the other channel must not flip the selected microphone.
        assert_eq!(downmixer.process(&[0.81, 0.8, 0.81, -0.8]), &[0.8, -0.8]);
    }

    #[test]
    fn resampler_passthrough_at_48k() {
        let mut rs = Resampler::new(SAMPLE_RATE);
        let input: Vec<f32> = (0..480).map(|i| i as f32).collect();
        let out = rs.process(&input);
        assert_eq!(out.len(), 480);
        assert_eq!(out[0], 0.0);
        assert_eq!(out[479], 479.0);
    }

    #[test]
    fn resampler_upsamples_count_roughly_doubles_from_24k() {
        let mut rs = Resampler::new(24_000);
        let input = vec![0.0f32; 24_000];
        let out = rs.process(&input);
        assert!((47_900..=48_100).contains(&out.len()), "got {}", out.len());
    }

    #[test]
    fn resampler_downsamples_count_roughly_halves_from_96k() {
        let mut rs = Resampler::new(96_000);
        let input = vec![0.0f32; 96_000];
        let out = rs.process(&input);
        assert!((47_900..=48_100).contains(&out.len()), "got {}", out.len());
    }

    #[test]
    fn resampler_handles_odd_chunks_above_96k_without_overdraining() {
        let mut rs = Resampler::new(192_000);
        let mut produced = 0usize;
        for chunk in [10usize, 7, 13, 3, 19, 5].into_iter().cycle().take(2_000) {
            produced += rs.process(&vec![0.25; chunk]).len();
        }
        assert!(produced > 0);
    }

    #[test]
    fn resampler_carries_a_zero_output_discontinuity_into_the_next_frame() {
        let mut accumulator = FrameAccumulator::new();
        let base = 2_000_000_000u64;
        assert!(accumulator
            .push_and_drain(block(FRAME_SAMPLES - 1, base, base + 20_000_000))
            .is_empty());

        let mut resampler = Resampler::new(192_000);
        let discontinuous =
            resampler.process_timed(&[0.25], base + 100_000_000, base + 108_000_000, true, false);
        assert!(discontinuous.samples.is_empty());

        let continued = resampler.process_timed(
            &[0.25; SINC_FILTER_TAPS * 2],
            base + 100_005_208,
            base + 108_005_208,
            true,
            true,
        );
        assert!(!continued.samples.is_empty());
        assert!(!continued.timing_valid);

        let frames = accumulator.push_and_drain(continued);
        assert_eq!(frames.len(), 1);
        assert!(!frames[0].capture_timestamp_valid);
    }

    #[test]
    fn resampler_timestamps_follow_steady_hardware_clock_drift() {
        let mut rs = Resampler::new(44_100);
        let samples = vec![0.0f32; 441];
        let base = 2_000_000_000u64;
        let callback_period_ns = 10_002_000u64; // 200 ppm drift, deliberately exaggerated.
        let mut last_error = 0u64;
        for index in 0..3_000u64 {
            let capture_ns = base + index * callback_period_ns;
            let block = rs.process_timed(&samples, capture_ns, capture_ns + 8_000_000, true, true);
            if !block.samples.is_empty() {
                last_error = block.first_capture_ts_ns.abs_diff(capture_ns);
            }
        }
        assert!(
            last_error < 700_000,
            "capture timestamp drifted {last_error}ns from the current hardware callback"
        );
    }

    #[test]
    fn resampler_is_invariant_to_callback_chunking_at_44k1() {
        let input: Vec<f32> = (0..44_100)
            .map(|index| (std::f32::consts::TAU * 1_000.0 * index as f32 / 44_100.0).sin())
            .collect();
        let mut one_shot = Resampler::new(44_100);
        let expected = one_shot.process(&input);
        let mut streaming = Resampler::new(44_100);
        let mut actual = Vec::new();
        let mut offset = 0usize;
        let chunks = [137usize, 511, 89, 1024, 333, 480, 777];
        let mut chunk_index = 0usize;
        while offset < input.len() {
            let end = (offset + chunks[chunk_index % chunks.len()]).min(input.len());
            actual.extend(streaming.process(&input[offset..end]));
            offset = end;
            chunk_index += 1;
        }
        assert_eq!(actual.len(), expected.len());
        let max_error = actual
            .iter()
            .zip(expected.iter())
            .map(|(actual, expected)| (actual - expected).abs())
            .fold(0.0f32, f32::max);
        assert!(
            max_error < 0.000_001,
            "max callback-boundary error {max_error}"
        );
    }

    #[test]
    fn resampler_rejects_above_nyquist_energy_when_downsampling() {
        fn rms(samples: &[f32]) -> f32 {
            (samples.iter().map(|sample| sample * sample).sum::<f32>() / samples.len() as f32)
                .sqrt()
        }

        let input_rate = 96_000u32;
        let seconds = 1usize;
        let passband: Vec<f32> = (0..input_rate as usize * seconds)
            .map(|index| (std::f32::consts::TAU * 5_000.0 * index as f32 / input_rate as f32).sin())
            .collect();
        let stopband: Vec<f32> = (0..input_rate as usize * seconds)
            .map(|index| {
                (std::f32::consts::TAU * 30_000.0 * index as f32 / input_rate as f32).sin()
            })
            .collect();
        let mut passband_resampler = Resampler::new(input_rate);
        let passband_output = passband_resampler.process(&passband);
        let mut stopband_resampler = Resampler::new(input_rate);
        let stopband_output = stopband_resampler.process(&stopband);
        // Ignore the short startup transient created by the finite filter history.
        let passband_rms = rms(&passband_output[256..]);
        let stopband_rms = rms(&stopband_output[256..]);
        assert!(passband_rms > 0.65, "passband RMS was {passband_rms}");
        assert!(
            stopband_rms < 0.015,
            "30 kHz aliased into 48 kHz output at RMS {stopband_rms}"
        );
    }

    #[test]
    fn underrun_fader_reaches_silence_smoothly_and_crossfades_resume() {
        let mut fader = UnderrunFader::default();
        assert_eq!(fader.process((0.8, -0.4), true), (0.8, -0.4));
        let first_missing = fader.process((0.0, 0.0), false);
        assert_eq!(first_missing, (0.8, -0.4));
        let mut previous = first_missing.0;
        for _ in 1..UNDERRUN_FADE_SAMPLES {
            let current = fader.process((0.0, 0.0), false).0;
            assert!(current <= previous);
            previous = current;
        }
        assert_eq!(fader.process((0.0, 0.0), false), (0.0, 0.0));
        assert_eq!(fader.process((1.0, 1.0), true), (0.0, 0.0));
        let resumed = fader.process((1.0, 1.0), true).0;
        assert!(resumed > 0.0 && resumed < 1.0);
    }

    #[test]
    fn multichannel_output_mapping_uses_front_center_and_surround_without_lfe() {
        let mut output = [99.0; 6];
        write_out_frame(&mut output, 0, 6, 0.8, -0.4);
        assert_eq!(output, [0.8, -0.4, 0.2, 0.0, 0.4, -0.2]);
    }

    #[test]
    fn output_config_prefers_stereo_then_mono_before_multichannel() {
        assert!(
            output_config_score(2, cpal::SampleFormat::I16)
                < output_config_score(1, cpal::SampleFormat::F32)
        );
        assert!(
            output_config_score(1, cpal::SampleFormat::I16)
                < output_config_score(6, cpal::SampleFormat::F32)
        );
        assert!(
            output_config_score(2, cpal::SampleFormat::F32)
                < output_config_score(2, cpal::SampleFormat::I16)
        );
    }

    #[test]
    fn stream_format_filter_matches_the_formats_with_callback_implementations() {
        assert!(supported_sample_format(cpal::SampleFormat::F32));
        assert!(supported_sample_format(cpal::SampleFormat::I16));
        assert!(supported_sample_format(cpal::SampleFormat::U16));
        assert!(!supported_sample_format(cpal::SampleFormat::I32));
    }

    #[test]
    fn accumulator_emits_exact_960_sample_frames() {
        let mut acc = FrameAccumulator::new();
        let base = 1_000_000_000u64;
        let frames = acc.push_and_drain(block(100, base, base + 10_000_000));
        assert!(frames.is_empty());
        let second_capture = base + 100 * 1_000_000_000u64 / SAMPLE_RATE as u64;
        let frames = acc.push_and_drain(block(
            FRAME_SAMPLES * 2 + 10,
            second_capture,
            base + 20_000_000,
        ));
        assert_eq!(frames.len(), 2);
        for f in &frames {
            assert_eq!(f.samples.len(), FRAME_SAMPLES);
        }
        assert_eq!(frames[0].capture_ts_ns, base);
        assert_eq!(frames[0].capture_callback_ts_ns, base + 10_000_000);
        assert!(frames[1].capture_ts_ns.abs_diff(base + 20_000_000) <= 1);
        let frames = acc.push_and_drain(block(
            FRAME_SAMPLES - 110,
            base + 40_208_333,
            base + 50_000_000,
        ));
        assert_eq!(frames.len(), 1);
        assert_eq!(frames[0].samples.len(), FRAME_SAMPLES);
    }

    #[test]
    fn accumulator_two_ten_ms_callbacks_keep_first_sample_timestamp() {
        let mut acc = FrameAccumulator::new();
        let base = 2_000_000_000u64;
        assert!(acc
            .push_and_drain(block(480, base, base + 8_000_000))
            .is_empty());
        let frames = acc.push_and_drain(block(480, base + 10_000_000, base + 18_000_000));
        assert_eq!(frames.len(), 1);
        assert_eq!(frames[0].capture_ts_ns, base);
        assert_eq!(frames[0].capture_callback_ts_ns, base + 8_000_000);
    }

    #[test]
    fn accumulator_marks_a_frame_invalid_when_any_timing_segment_is_invalid() {
        let mut acc = FrameAccumulator::new();
        let base = 2_000_000_000u64;
        assert!(acc
            .push_and_drain(block(480, base, base + 8_000_000))
            .is_empty());
        let mut discontinuous = block(480, base + 30_000_000, base + 38_000_000);
        discontinuous.timing_valid = false;
        let frames = acc.push_and_drain(discontinuous);
        assert_eq!(frames.len(), 1);
        assert!(!frames[0].capture_timestamp_valid);
    }

    #[test]
    fn accumulator_large_callback_emits_twenty_ms_timeline() {
        let mut acc = FrameAccumulator::new();
        let base = 3_000_000_000u64;
        let frames = acc.push_and_drain(block(FRAME_SAMPLES * 2, base, base + 40_000_000));
        assert_eq!(frames.len(), 2);
        assert_eq!(frames[0].capture_ts_ns, base);
        assert_eq!(frames[1].capture_ts_ns, base + 20_000_000);
        assert_eq!(frames[1].capture_callback_ts_ns, base + 40_000_000);
    }

    #[test]
    fn tone_frame_is_full_and_audible() {
        let mut src = ToneSource::new();
        let timestamp = monotonic_ns();
        let f = src.fill_frame(timestamp, timestamp, true);
        assert_eq!(f.samples.len(), FRAME_SAMPLES);
        let p = peak(&f.samples);
        assert!(p > 0.011, "tone too quiet: {p}");
        assert!(p <= 0.012_01, "tone exceeds diagnostic amplitude: {p}");
    }

    #[test]
    fn tone_is_continuous_across_frames() {
        let mut src = ToneSource::new();
        let f1 = src.fill_frame(monotonic_ns(), monotonic_ns(), true);
        let f2 = src.fill_frame(monotonic_ns(), monotonic_ns(), true);
        assert_ne!(f1.samples, f2.samples);
        assert!(peak(&f2.samples) > 0.011);
    }

    #[test]
    fn peak_reports_max_abs() {
        assert_eq!(peak(&[0.0, -0.7, 0.3]), 0.7);
        assert_eq!(peak(&[]), 0.0);
    }

    #[test]
    fn now_ns_is_nonzero_and_nondecreasing() {
        let a = now_ns();
        let b = now_ns();
        assert!(a > 0);
        assert!(b >= a);
    }
}
