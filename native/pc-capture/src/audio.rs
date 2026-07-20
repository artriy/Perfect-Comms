use crate::diagnostics::{
    send_media_state, CaptureClockStatus, CaptureDiagnostics, MediaStateEvent, PlaybackDiagnostics,
    StreamDescriptor,
};
use crate::proto::{
    AudioFrame, CaptureFrameMetadata, CaptureFrameProducer, DeviceInfo, PlaybackConsumer,
    PlaybackRing, FRAME_SAMPLES, SAMPLE_RATE,
};
use crate::rtc::NativeCounters;
use cubeb::{
    ChannelLayout, DeviceCollection, DeviceId, DeviceState, DeviceType, MonoFrame, SampleFormat,
    State, StereoFrame, StreamBuilder, StreamParamsBuilder, StreamPrefs,
};
use std::cell::UnsafeCell;
use std::collections::VecDeque;
#[cfg(target_os = "linux")]
use std::ffi::CStr;
use std::ffi::CString;
use std::mem::MaybeUninit;
use std::ptr;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::mpsc::SyncSender;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub use crate::input::SyntheticTone as ToneSource;
pub use crate::input::SYNTHETIC_TONE_HZ as TONE_HZ;

/// Single-owner state installed after Cubeb successfully creates a stream and consumed only by
/// that stream's serialized realtime callback. Keeping the heavy state outside StreamBuilder also
/// keeps its DSP and audio buffers out of Cubeb's callback allocation during stream initialization.
struct RealtimeCallbackState<T> {
    initialized: AtomicBool,
    value: UnsafeCell<MaybeUninit<T>>,
}

impl<T> RealtimeCallbackState<T> {
    fn new() -> Self {
        Self {
            initialized: AtomicBool::new(false),
            value: UnsafeCell::new(MaybeUninit::uninit()),
        }
    }

    fn initialize(&self, value: T) -> Result<(), T> {
        if self.initialized.load(Ordering::Acquire) {
            return Err(value);
        }
        // SAFETY: the stream owner installs the value exactly once, before the successful stream
        // is started and before the callback gate is published.
        unsafe { (*self.value.get()).write(value) };
        self.initialized.store(true, Ordering::Release);
        Ok(())
    }

    fn with_callback_mut<R>(&self, action: impl FnOnce(&mut T) -> R) -> Option<R> {
        if !self.initialized.load(Ordering::Acquire) {
            return None;
        }
        // SAFETY: Cubeb serializes data callbacks for one stream. The control thread never reads
        // or mutates this value after initialization and stops the stream before its final drop.
        let value = unsafe { &mut *(*self.value.get()).as_mut_ptr() };
        Some(action(value))
    }
}

// SAFETY: access is published with release/acquire ordering and mutable access is restricted to
// Cubeb's single serialized data callback as described above.
unsafe impl<T: Send> Sync for RealtimeCallbackState<T> {}

impl<T> Drop for RealtimeCallbackState<T> {
    fn drop(&mut self) {
        if self.initialized.load(Ordering::Acquire) {
            // SAFETY: an initialized value is written exactly once and has not been dropped yet.
            unsafe { self.value.get_mut().assume_init_drop() };
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MicrophoneMonitorConfigChange {
    Unchanged,
    GainOnly,
    Playout,
}

pub struct MicrophoneMonitorState {
    enabled: AtomicBool,
    delay_pairs: AtomicU32,
    gain_bits: AtomicU32,
    playout_generation: AtomicU64,
}

impl Default for MicrophoneMonitorState {
    fn default() -> Self {
        Self {
            enabled: AtomicBool::new(false),
            delay_pairs: AtomicU32::new(0),
            gain_bits: AtomicU32::new(1.0f32.to_bits()),
            playout_generation: AtomicU64::new(0),
        }
    }
}

impl MicrophoneMonitorState {
    pub fn configure<F>(
        &self,
        enabled: bool,
        delay_pairs: usize,
        gain: f32,
        reset_playout: F,
    ) -> MicrophoneMonitorConfigChange
    where
        F: FnOnce(),
    {
        let delay_pairs = if enabled {
            delay_pairs.min(u32::MAX as usize) as u32
        } else {
            0
        };
        let gain = if gain.is_finite() {
            gain.clamp(0.0, 2.0)
        } else {
            1.0
        };
        let gain_bits = gain.to_bits();
        let enabled_changed = self.enabled.load(Ordering::Acquire) != enabled;
        let delay_changed = self.delay_pairs.load(Ordering::Acquire) != delay_pairs;
        let gain_changed = self.gain_bits.load(Ordering::Acquire) != gain_bits;

        if !enabled_changed && !delay_changed {
            if !gain_changed {
                return MicrophoneMonitorConfigChange::Unchanged;
            }
            self.gain_bits.store(gain_bits, Ordering::Release);
            return MicrophoneMonitorConfigChange::GainOnly;
        }

        // Make the callback fail silent before the old delayed timeline is discarded. The
        // generation then forces its interpolation and priming state to reset before the new
        // configuration can become audible.
        self.enabled.store(false, Ordering::Release);
        reset_playout();
        self.delay_pairs.store(delay_pairs, Ordering::Release);
        self.gain_bits.store(gain_bits, Ordering::Release);
        self.playout_generation.fetch_add(1, Ordering::AcqRel);
        self.enabled.store(enabled, Ordering::Release);
        MicrophoneMonitorConfigChange::Playout
    }

    pub fn enabled(&self) -> bool {
        self.enabled.load(Ordering::Acquire)
    }

    fn delay_pairs(&self) -> usize {
        self.delay_pairs.load(Ordering::Acquire) as usize
    }

    fn gain(&self) -> f32 {
        f32::from_bits(self.gain_bits.load(Ordering::Acquire))
    }

    fn playout_generation(&self) -> u64 {
        self.playout_generation.load(Ordering::Acquire)
    }

    pub fn reset_playout<F>(&self, reset_playout: F)
    where
        F: FnOnce(),
    {
        reset_playout();
        self.playout_generation.fetch_add(1, Ordering::AcqRel);
    }
}

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

    fn reset(&mut self) {
        self.dominant_channel = 0;
        self.scratch.clear();
        self.energy.fill(0.0);
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
    // Leave one second of headroom so a first-sample timestamp can safely subtract the audio
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
    bridge: CaptureClockBridge,
}

impl CaptureClockMapper {
    fn observe(
        &mut self,
        callback_mono_ns: u64,
        input_latency_frames: Option<u32>,
        frames: usize,
        sample_rate: u32,
    ) -> InputTimingObservation {
        // Cubeb exposes stream latency in frames on the control thread, but intentionally does
        // not expose backend timestamps to its realtime data callback. Advance by the prior
        // callback's duration while the fresh callback-minus-latency anchor agrees. A scheduler
        // stall, dropped callback, or abrupt latency change must re-anchor instead of making the
        // capture clock look falsely continuous to the echo canceller.
        let capture_to_callback_ns = input_latency_frames
            .map(|latency| {
                u64::from(latency).saturating_mul(1_000_000_000) / u64::from(sample_rate.max(1))
            })
            .filter(|latency_ns| *latency_ns <= MAX_AEC_COMPONENT_US.saturating_mul(1_000));
        let expected_delta_ns = self.bridge.expected_delta_ns();
        let step = match (
            expected_delta_ns,
            self.bridge.last_mapped_capture_ns,
            capture_to_callback_ns.and_then(|latency| callback_mono_ns.checked_sub(latency)),
        ) {
            (Some(expected), Some(previous), Some(fresh)) => {
                let predicted = previous.saturating_add(expected);
                if fresh.abs_diff(predicted) <= CAPTURE_CLOCK_DISCONTINUITY_TOLERANCE_NS {
                    BackendCaptureStep::Forward(expected)
                } else if fresh >= previous {
                    BackendCaptureStep::Forward(fresh - previous)
                } else {
                    BackendCaptureStep::Reversed
                }
            }
            _ => BackendCaptureStep::First,
        };
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
/// Cubeb exposes hardware-side input/output latency in frames on the stream control thread. The
/// callbacks consume lock-free atomic snapshots of those values; the remaining render queue and
/// capture scheduling components are measured inside the helper.
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
        input_latency_frames: Option<u32>,
        frames: usize,
        sample_rate: u32,
    ) -> InputTimingObservation {
        let callback_mono_ns = monotonic_ns();
        let observation =
            mapper.observe(callback_mono_ns, input_latency_frames, frames, sample_rate);
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

    pub fn observe_output_latency_frames(&self, frames: Option<u32>, sample_rate: u32) {
        Self::observe_latency(
            &self.output_latency_us,
            frames.map(|latency| {
                Duration::from_nanos(
                    u64::from(latency).saturating_mul(1_000_000_000)
                        / u64::from(sample_rate.max(1)),
                )
            }),
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

const CUBEB_DEVICE_ID_V1_PREFIX: &str = "cubeb-v1:";
const CUBEB_DEVICE_ID_V2_PREFIX: &str = "cubeb-v2:";
const MAX_DEVICE_ID_BYTES: usize = 4_096;
const UNKNOWN_CUBEB_LATENCY_FRAMES: u32 = u32::MAX;
const CUBEB_MAX_LATENCY_FRAMES: u32 = 96_000;
const CUBEB_LATENCY_REFRESH: Duration = Duration::from_millis(250);
const CUBEB_FAST_LATENCY_REFRESH: Duration = Duration::from_millis(20);
const CUBEB_FAST_LATENCY_PROBE_WINDOW: Duration = Duration::from_millis(80);

#[cfg(windows)]
const COM_S_OK: i32 = 0;
#[cfg(windows)]
const COM_S_FALSE: i32 = 1;
#[cfg(windows)]
const COM_RPC_E_CHANGED_MODE: i32 = 0x8001_0106_u32 as i32;

#[cfg(windows)]
fn com_initialization_needs_uninitialize(result: i32) -> Result<bool, i32> {
    match result {
        COM_S_OK | COM_S_FALSE => Ok(true),
        // The thread already owns a different COM apartment. COM is still initialized and
        // endpoint APIs are usable, but this call did not add a reference to balance.
        COM_RPC_E_CHANGED_MODE => Ok(false),
        failure => Err(failure),
    }
}

#[cfg(windows)]
struct WindowsComApartment {
    uninitialize: bool,
}

#[cfg(windows)]
impl WindowsComApartment {
    fn initialize() -> Result<Self, String> {
        #[link(name = "ole32")]
        extern "system" {
            fn CoInitializeEx(reserved: *mut std::ffi::c_void, coinit: u32) -> i32;
        }

        // COINIT_MULTITHREADED is zero. Cubeb's WASAPI backend queries the default endpoint
        // during context initialization and requires this worker thread to own a COM apartment.
        let result = unsafe { CoInitializeEx(std::ptr::null_mut(), 0) };
        com_initialization_needs_uninitialize(result)
            .map(|uninitialize| Self { uninitialize })
            .map_err(|failure| format!("HRESULT 0x{:08x}", failure as u32))
    }
}

#[cfg(windows)]
impl Drop for WindowsComApartment {
    fn drop(&mut self) {
        #[link(name = "ole32")]
        extern "system" {
            fn CoUninitialize();
        }

        if self.uninitialize {
            unsafe { CoUninitialize() };
        }
    }
}

fn allowed_cubeb_backend(configured: Option<&str>, is_linux: bool) -> Option<&str> {
    if !is_linux {
        return None;
    }
    configured.filter(|backend| matches!(*backend, "pulse" | "alsa"))
}

fn init_cubeb_context(name: &str) -> cubeb::Result<cubeb::Context> {
    let name = CString::new(name)?;
    let configured = std::env::var("PC_CUBEB_BACKEND").ok();
    let backend = allowed_cubeb_backend(configured.as_deref(), cfg!(target_os = "linux"))
        .map(CString::new)
        .transpose()?;
    cubeb::Context::init(Some(name.as_c_str()), backend.as_deref())
}

fn cubeb_buffer_mode(context: &cubeb::Context) -> String {
    let backend = String::from_utf8_lossy(context.backend_id_bytes());
    let backend = backend.trim();
    let backend = if backend.is_empty() {
        "unknown"
    } else {
        backend
    };
    format!("cubeb-{backend}-min-latency")
}

fn append_hex(encoded: &mut String, bytes: &[u8]) {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    for &byte in bytes {
        encoded.push(HEX[(byte >> 4) as usize] as char);
        encoded.push(HEX[(byte & 0x0f) as usize] as char);
    }
}

fn decode_hex(value: &str) -> Option<Vec<u8>> {
    if value.is_empty() || value.len() > MAX_DEVICE_ID_BYTES * 2 || value.len() & 1 != 0 {
        return None;
    }
    fn nibble(value: u8) -> Option<u8> {
        match value {
            b'0'..=b'9' => Some(value - b'0'),
            b'a'..=b'f' => Some(value - b'a' + 10),
            b'A'..=b'F' => Some(value - b'A' + 10),
            _ => None,
        }
    }
    value
        .as_bytes()
        .chunks_exact(2)
        .map(|pair| Some((nibble(pair[0])? << 4) | nibble(pair[1])?))
        .collect()
}

fn normalized_cubeb_backend_family(backend_id: &[u8]) -> String {
    let backend = String::from_utf8_lossy(backend_id)
        .trim()
        .to_ascii_lowercase();
    match backend.as_str() {
        "audiounit" | "audiounit-rust" => "coreaudio".to_string(),
        "pulse" | "pulse-rust" => "pulse".to_string(),
        _ => backend,
    }
}

fn cubeb_backend_supports_device_change_callback(backend_id: &[u8]) -> bool {
    normalized_cubeb_backend_family(backend_id) == "coreaudio"
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CubebDeviceChangeAction {
    None,
    ResetDefaultStream,
    ReopenExplicitStream,
}

#[derive(Default)]
struct CubebDeviceChangeSignal {
    pending: AtomicBool,
    epoch: AtomicU64,
}

impl CubebDeviceChangeSignal {
    fn notify(&self) {
        // Publish the epoch first so both the control thread and the realtime capture callback
        // observe the new path after acquiring the pending notification.
        self.epoch.fetch_add(1, Ordering::AcqRel);
        self.pending.store(true, Ordering::Release);
    }

    fn clear_pending(&self) {
        self.pending.store(false, Ordering::Release);
    }

    fn epoch(&self) -> u64 {
        self.epoch.load(Ordering::Acquire)
    }

    fn update_observed_epoch(&self, observed: &mut u64) -> bool {
        let current = self.epoch();
        if current == *observed {
            return false;
        }
        *observed = current;
        true
    }

    fn take_action(&self, requested_default: bool) -> CubebDeviceChangeAction {
        if !self.pending.swap(false, Ordering::AcqRel) {
            return CubebDeviceChangeAction::None;
        }
        if requested_default {
            CubebDeviceChangeAction::ResetDefaultStream
        } else {
            CubebDeviceChangeAction::ReopenExplicitStream
        }
    }
}

fn encode_device_id(backend_id: &[u8], bytes: &[u8]) -> String {
    let family = normalized_cubeb_backend_family(backend_id);
    let mut encoded = String::with_capacity(
        CUBEB_DEVICE_ID_V2_PREFIX.len() + family.len() * 2 + 1 + bytes.len() * 2,
    );
    encoded.push_str(CUBEB_DEVICE_ID_V2_PREFIX);
    append_hex(&mut encoded, family.as_bytes());
    encoded.push(':');
    append_hex(&mut encoded, bytes);
    encoded
}

fn parse_cubeb_device_id(value: &str) -> Option<(Option<Vec<u8>>, Vec<u8>)> {
    if let Some(raw) = value.strip_prefix(CUBEB_DEVICE_ID_V1_PREFIX) {
        return decode_hex(raw).map(|bytes| (None, bytes));
    }
    let value = value.strip_prefix(CUBEB_DEVICE_ID_V2_PREFIX)?;
    let (family, raw) = value.split_once(':')?;
    Some((Some(decode_hex(family)?), decode_hex(raw)?))
}

fn legacy_backend_device_id<'a>(requested: &'a str, backend_id: &[u8]) -> Option<&'a [u8]> {
    let (legacy_host, raw) = requested.split_once(':')?;
    if raw.is_empty() || raw.len() > MAX_DEVICE_ID_BYTES {
        return None;
    }
    let family = normalized_cubeb_backend_family(backend_id);
    let compatible = matches!(
        (legacy_host, family.as_str()),
        ("wasapi", "wasapi")
            | ("coreaudio", "coreaudio")
            | ("pulseaudio", "pulse")
            | ("alsa", "alsa")
    );
    compatible.then_some(raw.as_bytes())
}

fn requested_device_matches(requested: &str, backend_id: &[u8], raw_id: &[u8]) -> bool {
    if let Some((requested_family, requested_raw)) = parse_cubeb_device_id(requested) {
        let family_matches = requested_family
            .is_none_or(|family| family == normalized_cubeb_backend_family(backend_id).as_bytes());
        return family_matches && requested_raw == raw_id;
    }
    legacy_backend_device_id(requested, backend_id) == Some(raw_id)
}

fn friendly_device_name(info: &cubeb::DeviceInfoRef, stable_id: &str) -> String {
    let friendly = info
        .friendly_name_bytes()
        .map(String::from_utf8_lossy)
        .map(|name| name.trim().to_string())
        .filter(|name| !name.is_empty());
    friendly.unwrap_or_else(|| stable_id.to_string())
}

#[cfg(target_os = "linux")]
#[derive(Debug, Clone, PartialEq, Eq)]
struct AlsaHintDevice {
    pcm_name: Vec<u8>,
    friendly_name: String,
    supports_input: bool,
    supports_output: bool,
}

#[cfg(target_os = "linux")]
fn alsa_hint_directions(ioid: Option<&[u8]>) -> (bool, bool) {
    match ioid {
        Some(value) if value.eq_ignore_ascii_case(b"input") => (true, false),
        Some(value) if value.eq_ignore_ascii_case(b"output") => (false, true),
        // ALSA documents a missing IOID as a duplex-capable hint. Unknown values are not safe to
        // advertise because selecting the wrong direction can block some hardware plugins.
        None => (true, true),
        Some(_) => (false, false),
    }
}

#[cfg(target_os = "linux")]
struct AlsaHintApi {
    handle: *mut libc::c_void,
    device_name_hint: unsafe extern "C" fn(
        libc::c_int,
        *const libc::c_char,
        *mut *mut *mut libc::c_void,
    ) -> libc::c_int,
    device_name_get_hint:
        unsafe extern "C" fn(*const libc::c_void, *const libc::c_char) -> *mut libc::c_char,
    device_name_free_hint: unsafe extern "C" fn(*mut *mut libc::c_void) -> libc::c_int,
}

#[cfg(target_os = "linux")]
impl AlsaHintApi {
    fn load() -> Result<Self, String> {
        unsafe fn symbol<T: Copy>(
            handle: *mut libc::c_void,
            name: &'static [u8],
        ) -> Result<T, String> {
            // SAFETY: `name` is NUL-terminated and the caller supplies the exact ALSA function
            // signature. Function pointers and dlsym pointers have the same representation on
            // supported Unix targets.
            let address = unsafe { libc::dlsym(handle, name.as_ptr().cast()) };
            if address.is_null() {
                return Err(format!(
                    "libasound is missing {}",
                    String::from_utf8_lossy(&name[..name.len().saturating_sub(1)])
                ));
            }
            // SAFETY: validated non-null symbol with the exact function type declared above.
            Ok(unsafe { std::mem::transmute_copy(&address) })
        }

        // SAFETY: constant NUL-terminated soname and standard dlopen flags.
        let handle = unsafe {
            libc::dlopen(
                c"libasound.so.2".as_ptr(),
                libc::RTLD_LAZY | libc::RTLD_LOCAL,
            )
        };
        if handle.is_null() {
            return Err("load libasound.so.2 for device hints".to_string());
        }
        // Close the handle if any required symbol is absent.
        let loaded = unsafe {
            Ok::<Self, String>(Self {
                handle,
                device_name_hint: symbol(handle, b"snd_device_name_hint\0")?,
                device_name_get_hint: symbol(handle, b"snd_device_name_get_hint\0")?,
                device_name_free_hint: symbol(handle, b"snd_device_name_free_hint\0")?,
            })
        };
        if loaded.is_err() {
            // SAFETY: `handle` was returned by dlopen and has not been closed.
            unsafe { libc::dlclose(handle) };
        }
        loaded
    }

    unsafe fn value(&self, hint: *const libc::c_void, key: &'static [u8]) -> Option<Vec<u8>> {
        // SAFETY: `hint` belongs to the live ALSA hint array and `key` is NUL-terminated.
        let value = unsafe { (self.device_name_get_hint)(hint, key.as_ptr().cast()) };
        if value.is_null() {
            return None;
        }
        // SAFETY: ALSA returns a NUL-terminated malloc allocation for this accessor.
        let bytes = unsafe { CStr::from_ptr(value) }.to_bytes().to_vec();
        // SAFETY: snd_device_name_get_hint documents that callers release the value with free().
        unsafe { libc::free(value.cast()) };
        Some(bytes)
    }
}

#[cfg(target_os = "linux")]
impl Drop for AlsaHintApi {
    fn drop(&mut self) {
        // SAFETY: the handle is owned by this value and all hint allocations are already freed.
        unsafe { libc::dlclose(self.handle) };
    }
}

#[cfg(target_os = "linux")]
fn enumerate_alsa_hint_devices() -> Result<Vec<AlsaHintDevice>, String> {
    const MAX_ALSA_HINTS: usize = 4_096;
    let api = AlsaHintApi::load()?;
    let mut hints: *mut *mut libc::c_void = ptr::null_mut();
    // SAFETY: output pointer is valid and ALSA owns the returned null-terminated hint array.
    let result = unsafe { (api.device_name_hint)(-1, c"pcm".as_ptr(), &mut hints as *mut _) };
    if result < 0 || hints.is_null() {
        return Err(format!("enumerate ALSA PCM hints: error {result}"));
    }

    let mut devices = Vec::new();
    for index in 0..MAX_ALSA_HINTS {
        // SAFETY: ALSA returns a null-terminated pointer array; the explicit cap also bounds a
        // malformed implementation before any dereference beyond a reasonable device count.
        let hint = unsafe { *hints.add(index) };
        if hint.is_null() {
            break;
        }
        // SAFETY: the hint remains live until snd_device_name_free_hint below.
        let Some(pcm_name) = (unsafe { api.value(hint, b"NAME\0") }) else {
            continue;
        };
        if pcm_name.is_empty() || pcm_name.len() > MAX_DEVICE_ID_BYTES || pcm_name.contains(&0) {
            continue;
        }
        // SAFETY: same live hint as above.
        let ioid = unsafe { api.value(hint, b"IOID\0") };
        let (supports_input, supports_output) = alsa_hint_directions(ioid.as_deref());
        if !supports_input && !supports_output {
            continue;
        }
        // SAFETY: same live hint as above.
        let description = unsafe { api.value(hint, b"DESC\0") };
        let friendly_name = description
            .as_deref()
            .map(String::from_utf8_lossy)
            .and_then(|description| {
                description
                    .lines()
                    .map(str::trim)
                    .find(|line| !line.is_empty())
                    .map(str::to_string)
            })
            .unwrap_or_else(|| String::from_utf8_lossy(&pcm_name).into_owned());
        devices.push(AlsaHintDevice {
            pcm_name,
            friendly_name,
            supports_input,
            supports_output,
        });
    }
    // SAFETY: releases the complete array returned by snd_device_name_hint.
    let free_result = unsafe { (api.device_name_free_hint)(hints) };
    if free_result < 0 {
        return Err(format!("free ALSA PCM hints: error {free_result}"));
    }
    devices.sort_by(|left, right| {
        left.pcm_name
            .cmp(&right.pcm_name)
            .then_with(|| left.friendly_name.cmp(&right.friendly_name))
    });
    devices.dedup_by(|left, right| left.pcm_name == right.pcm_name);
    Ok(devices)
}

fn cubeb_preference_is_default(preferred_bits: impl Into<i64>) -> bool {
    preferred_bits.into() & i64::from(cubeb::ffi::CUBEB_DEVICE_PREF_MULTIMEDIA) != 0
}

fn enumerate_cubeb_devices(direction: DeviceType) -> Result<Vec<DeviceInfo>, String> {
    #[cfg(windows)]
    let _com = WindowsComApartment::initialize()
        .map_err(|error| format!("initialize COM for Cubeb device enumeration: {error}"))?;
    let mut out = Vec::new();
    let context = init_cubeb_context("PerfectComms device enumeration")
        .map_err(|error| format!("initialize Cubeb device enumeration: {error}"))?;
    let backend_id = context.backend_id_bytes();
    #[cfg(target_os = "linux")]
    if normalized_cubeb_backend_family(backend_id) == "alsa" {
        match enumerate_alsa_hint_devices() {
            Ok(hints) => {
                for hint in hints {
                    let direction_supported = (direction.intersects(DeviceType::INPUT)
                        && hint.supports_input)
                        || (direction.intersects(DeviceType::OUTPUT) && hint.supports_output);
                    if !direction_supported {
                        continue;
                    }
                    out.push(DeviceInfo {
                        id: encode_device_id(backend_id, &hint.pcm_name),
                        name: hint.friendly_name,
                        default: hint.pcm_name == b"default",
                    });
                }
                if !out.is_empty() {
                    out.sort_by(|left, right| {
                        right
                            .default
                            .cmp(&left.default)
                            .then_with(|| left.name.cmp(&right.name))
                            .then_with(|| left.id.cmp(&right.id))
                    });
                    out.dedup_by(|left, right| left.id == right.id);
                    return Ok(out);
                }
            }
            Err(error) => {
                eprintln!("pc-capture: ALSA hint enumeration unavailable: {error}");
            }
        }
    }
    let devices = context
        .enumerate_devices(direction)
        .map_err(|error| format!("enumerate Cubeb devices: {error}"))?;
    for info in devices.iter() {
        if info.state() != DeviceState::Enabled || !info.device_type().intersects(direction) {
            continue;
        }
        let Some(id_bytes) = info.device_id_bytes().filter(|id| !id.is_empty()) else {
            continue;
        };
        let id = encode_device_id(backend_id, id_bytes);
        out.push(DeviceInfo {
            name: friendly_device_name(info, &id),
            id,
            // Cubeb does not expose a separate default-device query. Preferred endpoints are
            // its authoritative enumeration marker for the current platform defaults.
            // A default stream with StreamPrefs::NONE follows the multimedia/console route.
            // WASAPI can separately mark the communications endpoint as VOICE; that endpoint
            // must not replace the actual default in the managed device picker.
            default: cubeb_preference_is_default(info.preferred().bits()),
        });
    }
    out.sort_by(|left, right| {
        right
            .default
            .cmp(&left.default)
            .then_with(|| left.name.cmp(&right.name))
            .then_with(|| left.id.cmp(&right.id))
    });
    out.dedup_by(|left, right| left.id == right.id);
    Ok(out)
}

pub fn enumerate_devices() -> Result<Vec<DeviceInfo>, String> {
    enumerate_cubeb_devices(DeviceType::INPUT)
}

struct SelectedDevice {
    device: DeviceId,
    // Some backends (currently ALSA) can open raw device names that their Cubeb enumerator does
    // not publish. Keep that C string alive for as long as the stream may retain its pointer.
    _owned_device_id: Option<CString>,
    preferred_rate: u32,
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

fn find_cubeb_device(
    devices: &DeviceCollection<'_>,
    requested_id: &str,
    backend_id: &[u8],
) -> Option<(DeviceId, u32, String, Option<CString>)> {
    devices.iter().find_map(|info| {
        (info.state() == DeviceState::Enabled)
            .then(|| info.device_id_bytes())
            .flatten()
            .filter(|bytes| requested_device_matches(requested_id, backend_id, bytes))
            .map(|bytes| {
                (
                    info.devid(),
                    info.default_rate(),
                    encode_device_id(backend_id, bytes),
                    None,
                )
            })
    })
}

#[cfg(target_os = "linux")]
fn find_alsa_hint_device(
    requested_id: &str,
    backend_id: &[u8],
    default_rate: u32,
    direction: DeviceType,
) -> Option<(DeviceId, u32, String, Option<CString>)> {
    if normalized_cubeb_backend_family(backend_id) != "alsa" {
        return None;
    }
    enumerate_alsa_hint_devices()
        .ok()?
        .into_iter()
        .find_map(|hint| {
            let direction_supported = (direction.intersects(DeviceType::INPUT)
                && hint.supports_input)
                || (direction.intersects(DeviceType::OUTPUT) && hint.supports_output);
            if !direction_supported
                || !requested_device_matches(requested_id, backend_id, &hint.pcm_name)
            {
                return None;
            }
            let owned = CString::new(hint.pcm_name).ok()?;
            let device = owned.as_ptr().cast();
            Some((
                device,
                default_rate,
                encode_device_id(backend_id, owned.as_bytes()),
                Some(owned),
            ))
        })
}

fn pick_device(
    devices: Option<&DeviceCollection<'_>>,
    device_id: &Option<String>,
    default_rate: u32,
    direction: &str,
    direction_type: DeviceType,
    backend_id: &[u8],
) -> Result<SelectedDevice, String> {
    let requested_id = device_id.as_deref().filter(|id| !id.is_empty());
    let selected = requested_id
        .and_then(|id| devices.and_then(|items| find_cubeb_device(items, id, backend_id)));
    #[cfg(target_os = "linux")]
    let selected = selected.or_else(|| {
        requested_id
            .and_then(|id| find_alsa_hint_device(id, backend_id, default_rate, direction_type))
    });
    #[cfg(not(target_os = "linux"))]
    let _ = direction_type;
    let (device, preferred_rate, resolved_device, owned_device_id) = selected_or_default(
        device_id.as_deref(),
        selected,
        || Some((ptr::null(), default_rate, String::new(), None)),
        direction,
    )?;
    Ok(SelectedDevice {
        device,
        _owned_device_id: owned_device_id,
        preferred_rate: normalize_device_rate(preferred_rate),
        requested_device: device_id.clone().unwrap_or_default(),
        resolved_device,
        requested_default: requested_id.is_none(),
        requested_matched: true,
        fell_back_to_default: false,
    })
}

const MIN_REALTIME_CAPTURE_RATE: u32 = 8_000;
const MAX_REALTIME_DEVICE_RATE: u32 = 384_000;
const CAPTURE_PROCESS_CHUNK_FRAMES: usize = 256;
const MAX_RESAMPLED_CHUNK_SAMPLES: usize =
    CAPTURE_PROCESS_CHUNK_FRAMES * SAMPLE_RATE as usize / MIN_REALTIME_CAPTURE_RATE as usize + 2;
const REALTIME_ACCUMULATOR_SAMPLES: usize = FRAME_SAMPLES + MAX_RESAMPLED_CHUNK_SAMPLES;
const REALTIME_TIMING_SEGMENTS: usize = 64;

fn normalize_device_rate(rate: u32) -> u32 {
    if (MIN_REALTIME_CAPTURE_RATE..=MAX_REALTIME_DEVICE_RATE).contains(&rate) {
        rate
    } else {
        SAMPLE_RATE
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CubebSampleKind {
    Float32,
    Signed16,
}

impl CubebSampleKind {
    fn format(self) -> SampleFormat {
        match self {
            Self::Float32 => SampleFormat::Float32NE,
            Self::Signed16 => SampleFormat::S16NE,
        }
    }

    fn label(self) -> &'static str {
        match self {
            Self::Float32 => "Float32NE",
            Self::Signed16 => "S16NE",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct CubebStreamCandidate {
    sample: CubebSampleKind,
    rate: u32,
    channels: u16,
}

fn push_candidate_pair(
    candidates: &mut Vec<CubebStreamCandidate>,
    sample: CubebSampleKind,
    rate: u32,
) {
    for channels in [2, 1] {
        let candidate = CubebStreamCandidate {
            sample,
            rate,
            channels,
        };
        if !candidates.contains(&candidate) {
            candidates.push(candidate);
        }
    }
}

fn cubeb_stream_candidates(_backend_id: &[u8], preferred_rate: u32) -> Vec<CubebStreamCandidate> {
    let mut candidates = Vec::with_capacity(8);
    push_candidate_pair(&mut candidates, CubebSampleKind::Float32, SAMPLE_RATE);
    let preferred_rate = normalize_device_rate(preferred_rate);
    // ALSA and WinMM commonly advertise S16NE at the endpoint's preferred rate, but these
    // same-device fallbacks are harmless and useful for strict endpoints on every backend.
    push_candidate_pair(&mut candidates, CubebSampleKind::Signed16, preferred_rate);
    push_candidate_pair(&mut candidates, CubebSampleKind::Signed16, SAMPLE_RATE);
    push_candidate_pair(&mut candidates, CubebSampleKind::Float32, preferred_rate);
    candidates
}

fn cubeb_capture_stream_candidates(
    backend_id: &[u8],
    preferred_rate: u32,
) -> Vec<CubebStreamCandidate> {
    let mut candidates = cubeb_stream_candidates(backend_id, preferred_rate);
    let formats_and_rates: Vec<_> = candidates
        .iter()
        .filter(|candidate| candidate.channels == 2)
        .map(|candidate| (candidate.sample, candidate.rate))
        .collect();
    candidates.reserve(formats_and_rates.len() * 3);
    for (sample, rate) in formats_and_rates {
        for channels in [4, 6, 8] {
            candidates.push(CubebStreamCandidate {
                sample,
                rate,
                channels,
            });
        }
    }
    candidates
}

fn cubeb_playback_stream_candidates(
    backend_id: &[u8],
    preferred_rate: u32,
) -> Vec<CubebStreamCandidate> {
    let mut candidates = cubeb_stream_candidates(backend_id, preferred_rate);
    if normalized_cubeb_backend_family(backend_id) != "alsa" {
        return candidates;
    }

    // Preserve every stereo/mono attempt first. Some strict ALSA hardware PCMs expose only their
    // physical multichannel width, so append matching quad/5.1/7.1 attempts without broadening
    // capture or changing the preferred path for normal endpoints.
    let formats_and_rates: Vec<_> = candidates
        .iter()
        .filter(|candidate| candidate.channels == 2)
        .map(|candidate| (candidate.sample, candidate.rate))
        .collect();
    candidates.reserve(formats_and_rates.len() * 3);
    for (sample, rate) in formats_and_rates {
        for channels in [4, 6, 8] {
            candidates.push(CubebStreamCandidate {
                sample,
                rate,
                channels,
            });
        }
    }
    candidates
}

fn cubeb_output_channel_layout(channels: u16) -> Option<ChannelLayout> {
    match channels {
        1 => Some(ChannelLayout::MONO),
        2 => Some(ChannelLayout::STEREO),
        4 => Some(ChannelLayout::QUAD),
        6 => Some(ChannelLayout::_3F2_LFE),
        8 => Some(ChannelLayout::_3F4_LFE),
        _ => None,
    }
}

trait CubebCaptureSample: Copy + Send + Sync + 'static {
    fn to_float(self) -> f32;
}

impl CubebCaptureSample for f32 {
    fn to_float(self) -> f32 {
        if self.is_finite() {
            self
        } else {
            0.0
        }
    }
}

impl CubebCaptureSample for i16 {
    fn to_float(self) -> f32 {
        f32::from(self) / 32_768.0
    }
}

trait CubebPlaybackSample: Copy + Send + Sync + 'static {
    fn from_float(sample: f32) -> Self;
}

impl CubebPlaybackSample for f32 {
    fn from_float(sample: f32) -> Self {
        sample
    }
}

impl CubebPlaybackSample for i16 {
    fn from_float(sample: f32) -> Self {
        if !sample.is_finite() {
            0
        } else if sample < 0.0 {
            (sample.max(-1.0) * 32_768.0).round() as i16
        } else {
            (sample.min(1.0) * f32::from(i16::MAX)).round() as i16
        }
    }
}

fn cubeb_latency_frames(context: &cubeb::Context, params: &cubeb::StreamParamsRef) -> u32 {
    context
        .min_latency(params)
        .unwrap_or(FRAME_SAMPLES as u32)
        .clamp(1, CUBEB_MAX_LATENCY_FRAMES)
}

fn store_cubeb_latency(target: &AtomicU32, value: cubeb::Result<u32>) {
    if let Ok(frames) = value {
        if frames > 0 {
            target.store(frames.min(CUBEB_MAX_LATENCY_FRAMES), Ordering::Release);
        }
    }
}

fn load_cubeb_latency(source: &AtomicU32) -> Option<u32> {
    let value = source.load(Ordering::Acquire);
    (value != UNKNOWN_CUBEB_LATENCY_FRAMES).then_some(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct PrerollDecision {
    raw_frames: usize,
    skip_frames: usize,
    admitted_frames: usize,
    matched: bool,
}

impl PrerollDecision {
    fn unchanged(raw_frames: usize) -> Self {
        Self {
            raw_frames,
            skip_frames: 0,
            admitted_frames: raw_frames,
            matched: false,
        }
    }
}

/// libcubeb's WASAPI input implementation injects two backend buffers of bit-exact silence
/// whenever it sets up or transparently reconfigures a stream. Those buffers are an internal
/// resampler guard, not captured time, and must never become queued Opus/RTP frames.
struct WasapiPrerollFilter {
    enabled: bool,
    sample_rate: u32,
    nominal_frames: usize,
}

impl WasapiPrerollFilter {
    fn new(enabled: bool, sample_rate: u32, requested_latency_frames: u32) -> Self {
        let sample_rate = sample_rate.max(1);
        let min_frames = (sample_rate as usize / 1_000).max(1);
        let max_frames = (sample_rate as usize * 40 / 1_000).max(min_frames);
        Self {
            enabled,
            sample_rate,
            nominal_frames: (requested_latency_frames as usize).clamp(min_frames, max_frames),
        }
    }

    fn classify(
        &mut self,
        raw_frames: usize,
        mut is_zero_frame: impl FnMut(usize) -> bool,
    ) -> PrerollDecision {
        if !self.enabled || raw_frames == 0 {
            return PrerollDecision::unchanged(raw_frames);
        }

        let nominal = self.nominal_frames.max(1);
        // A normal Cubeb callback can legally contain more than one packet. Only an oversized
        // callback with a long exact-zero prefix and a small retained tail matches the WASAPI
        // implementation's injected-padding signature.
        if raw_frames < nominal.saturating_mul(3) {
            let min_frames = (self.sample_rate as usize / 1_000).max(1);
            if raw_frames >= min_frames {
                self.nominal_frames = self.nominal_frames.min(raw_frames);
            }
            return PrerollDecision::unchanged(raw_frames);
        }

        let leading_zero_frames = (0..raw_frames)
            .take_while(|index| is_zero_frame(*index))
            .count();
        for retained_quanta in 1..=4usize {
            let retained = nominal.saturating_mul(retained_quanta);
            if retained >= raw_frames {
                break;
            }
            let skipped = raw_frames - retained;
            if skipped >= nominal.saturating_mul(2) && skipped <= leading_zero_frames {
                return PrerollDecision {
                    raw_frames,
                    skip_frames: skipped,
                    admitted_frames: retained,
                    matched: true,
                };
            }
        }
        PrerollDecision::unchanged(raw_frames)
    }
}

#[derive(Clone)]
struct CaptureCallbackResources {
    ring: CaptureFrameProducer,
    encoder_epoch: Arc<AtomicU64>,
    aec_timing: Arc<AecTiming>,
    diagnostics: Arc<CaptureDiagnostics>,
    device_change: Arc<CubebDeviceChangeSignal>,
    backend_latency_frames: Arc<AtomicU32>,
    latency_probe_requested: Arc<AtomicBool>,
    first_callback_ns: Arc<AtomicU64>,
    first_callback_frames: Arc<AtomicU64>,
    wasapi_preroll_filter: bool,
    requested_latency_frames: u32,
    stream_generation: u64,
    open_attempt: u64,
}

struct CaptureCallbackProcessor {
    resources: CaptureCallbackResources,
    sample_rate: u32,
    device_change_epoch: u64,
    capture_clock: CaptureClockMapper,
    resampler: Resampler,
    accumulator: FrameAccumulator,
    resampled_scratch: Vec<f32>,
    preroll_filter: WasapiPrerollFilter,
}

impl CaptureCallbackProcessor {
    fn new(resources: CaptureCallbackResources, sample_rate: u32) -> Self {
        let mut resampler = Resampler::new(sample_rate);
        resampler.reserve_realtime_input(CAPTURE_PROCESS_CHUNK_FRAMES);
        let device_change_epoch = resources.device_change.epoch();
        let preroll_filter = WasapiPrerollFilter::new(
            resources.wasapi_preroll_filter,
            sample_rate,
            resources.requested_latency_frames,
        );
        Self {
            resources,
            sample_rate,
            device_change_epoch,
            capture_clock: CaptureClockMapper::default(),
            resampler,
            accumulator: FrameAccumulator::with_capacity(
                REALTIME_ACCUMULATOR_SAMPLES,
                REALTIME_TIMING_SEGMENTS,
            ),
            resampled_scratch: Vec::with_capacity(MAX_RESAMPLED_CHUNK_SAMPLES),
            preroll_filter,
        }
    }

    fn classify_preroll(
        &mut self,
        raw_frames: usize,
        is_zero_frame: impl FnMut(usize) -> bool,
    ) -> PrerollDecision {
        let decision = self.preroll_filter.classify(raw_frames, is_zero_frame);
        if decision.matched {
            self.resources
                .diagnostics
                .note_backend_preroll(decision.skip_frames);
            self.resources
                .backend_latency_frames
                .store(self.resources.requested_latency_frames, Ordering::Release);
            self.resources
                .latency_probe_requested
                .store(true, Ordering::Release);
            self.resources.aec_timing.reset_capture_path();
            self.capture_clock = CaptureClockMapper::default();
            self.resampler.reset();
            let pending = self.accumulator.pending_samples();
            self.accumulator.reset();
            self.resources.diagnostics.set_accumulator_pending(0);
            self.resources
                .diagnostics
                .note_preroll_pending_samples_discarded(pending);
        }
        decision
    }

    fn process(&mut self, mono: &[f32], raw: &[f32], input_frames: usize) {
        if self
            .resources
            .device_change
            .update_observed_epoch(&mut self.device_change_epoch)
        {
            // A default CoreAudio route switch keeps the Cubeb stream alive, but all timing and
            // partial resampling state belongs to the old hardware path. Explicit streams are
            // stopped by the control thread; resetting here also prevents a final mixed frame.
            self.resources.aec_timing.reset_capture_path();
            self.capture_clock = CaptureClockMapper::default();
            self.resampler.reset();
            self.accumulator.reset();
            self.resources.diagnostics.set_accumulator_pending(0);
            self.resources.diagnostics.note_timestamp_discontinuity();
        }
        let resources = &self.resources;
        let callback_encoder_epoch = resources.encoder_epoch.load(Ordering::Acquire);
        let observation = resources.aec_timing.observe_input_callback(
            &mut self.capture_clock,
            load_cubeb_latency(&resources.backend_latency_frames),
            input_frames,
            self.sample_rate,
        );
        if resources.diagnostics.observe_callback(
            observation.callback_mono_ns,
            input_frames,
            self.sample_rate,
        ) {
            resources
                .first_callback_frames
                .store(input_frames as u64, Ordering::Release);
            resources
                .first_callback_ns
                .store(observation.callback_mono_ns, Ordering::Release);
        }
        if resources.diagnostics.signal_windows_enabled() {
            resources.diagnostics.raw_input.record(raw);
        }
        resources.diagnostics.observe_capture_clock(
            observation.clock_status,
            observation.capture_clock_delta_ns,
            observation.expected_capture_delta_ns,
            observation.capture_clock_delta_error_ns,
            observation.bridge_residual_ns,
        );
        if observation.discontinuity {
            resources.diagnostics.note_timestamp_discontinuity();
        }
        if !observation.valid {
            resources.diagnostics.note_invalid_timestamp();
        }

        let mut produced_frames = 0;
        for (chunk_index, chunk) in mono.chunks(CAPTURE_PROCESS_CHUNK_FRAMES).enumerate() {
            let frame_offset = chunk_index.saturating_mul(CAPTURE_PROCESS_CHUNK_FRAMES);
            let chunk_capture_ts_ns = observation.first_sample_mono_ns.saturating_add(
                (frame_offset as u64).saturating_mul(1_000_000_000) / u64::from(self.sample_rate),
            );
            let timing = self.resampler.process_timed_into(
                chunk,
                chunk_capture_ts_ns,
                observation.callback_mono_ns,
                observation.valid,
                observation.frame_timing_valid,
                &mut self.resampled_scratch,
            );
            resources
                .diagnostics
                .observe_resampled_samples(self.resampled_scratch.len());
            produced_frames += self.accumulator.push_samples_and_drain_with(
                &self.resampled_scratch,
                timing,
                |timing, samples| {
                    let _ = resources.ring.push(
                        CaptureFrameMetadata {
                            encoder_epoch: callback_encoder_epoch,
                            capture_generation: resources.stream_generation,
                            capture_open_attempt: resources.open_attempt,
                            capture_ts_ns: timing.capture_ts_ns,
                            capture_callback_ts_ns: timing.callback_ts_ns,
                            capture_timestamp_valid: timing.valid,
                        },
                        samples,
                    );
                },
            );
        }
        resources
            .diagnostics
            .set_accumulator_pending(self.accumulator.pending_samples());
        resources
            .diagnostics
            .observe_frames_produced(produced_frames);
        if produced_frames > 0 {
            resources.diagnostics.observe_ring_len(resources.ring.len());
        }
    }
}

enum CubebCaptureStream {
    MonoF32(cubeb::Stream<MonoFrame<f32>>),
    StereoF32(cubeb::Stream<StereoFrame<f32>>),
    QuadF32(cubeb::Stream<MultichannelFrame<f32, 4>>),
    Surround51F32(cubeb::Stream<MultichannelFrame<f32, 6>>),
    Surround71F32(cubeb::Stream<MultichannelFrame<f32, 8>>),
    MonoI16(cubeb::Stream<MonoFrame<i16>>),
    StereoI16(cubeb::Stream<StereoFrame<i16>>),
    QuadI16(cubeb::Stream<MultichannelFrame<i16, 4>>),
    Surround51I16(cubeb::Stream<MultichannelFrame<i16, 6>>),
    Surround71I16(cubeb::Stream<MultichannelFrame<i16, 8>>),
}

impl CubebCaptureStream {
    fn stop(&self) -> cubeb::Result<()> {
        match self {
            Self::MonoF32(stream) => stream.stop(),
            Self::StereoF32(stream) => stream.stop(),
            Self::QuadF32(stream) => stream.stop(),
            Self::Surround51F32(stream) => stream.stop(),
            Self::Surround71F32(stream) => stream.stop(),
            Self::MonoI16(stream) => stream.stop(),
            Self::StereoI16(stream) => stream.stop(),
            Self::QuadI16(stream) => stream.stop(),
            Self::Surround51I16(stream) => stream.stop(),
            Self::Surround71I16(stream) => stream.stop(),
        }
    }

    fn input_latency(&self) -> cubeb::Result<u32> {
        match self {
            Self::MonoF32(stream) => stream.input_latency(),
            Self::StereoF32(stream) => stream.input_latency(),
            Self::QuadF32(stream) => stream.input_latency(),
            Self::Surround51F32(stream) => stream.input_latency(),
            Self::Surround71F32(stream) => stream.input_latency(),
            Self::MonoI16(stream) => stream.input_latency(),
            Self::StereoI16(stream) => stream.input_latency(),
            Self::QuadI16(stream) => stream.input_latency(),
            Self::Surround51I16(stream) => stream.input_latency(),
            Self::Surround71I16(stream) => stream.input_latency(),
        }
    }
}

fn cubeb_backend_injects_wasapi_preroll(backend_id: &[u8]) -> bool {
    backend_id.eq_ignore_ascii_case(b"wasapi")
}

struct StereoCaptureRealtime {
    processor: CaptureCallbackProcessor,
    raw_scratch: Vec<f32>,
    downmixer: AdaptiveDownmixer,
}

impl StereoCaptureRealtime {
    fn new(resources: CaptureCallbackResources, sample_rate: u32) -> Self {
        Self {
            processor: CaptureCallbackProcessor::new(resources, sample_rate),
            raw_scratch: vec![0.0; CUBEB_MAX_LATENCY_FRAMES as usize * 2],
            downmixer: AdaptiveDownmixer::with_capacity(2, CUBEB_MAX_LATENCY_FRAMES as usize),
        }
    }

    fn process<T: CubebCaptureSample>(&mut self, input: &[StereoFrame<T>]) -> bool {
        let decision = self.processor.classify_preroll(input.len(), |index| {
            input[index].l.to_float() == 0.0 && input[index].r.to_float() == 0.0
        });
        if decision.matched {
            self.downmixer.reset();
        }
        let input = &input[decision.skip_frames..];
        let Some(samples) = input.len().checked_mul(2) else {
            return false;
        };
        if samples > self.raw_scratch.len() {
            return false;
        }
        for (index, frame) in input.iter().enumerate() {
            self.raw_scratch[index * 2] = frame.l.to_float();
            self.raw_scratch[index * 2 + 1] = frame.r.to_float();
        }
        let raw = &self.raw_scratch[..samples];
        let mono = self.downmixer.process(raw);
        self.processor.process(mono, raw, input.len());
        true
    }
}

struct MonoCaptureRealtime {
    processor: CaptureCallbackProcessor,
    scratch: Vec<f32>,
}

struct MultichannelCaptureRealtime<const CHANNELS: usize> {
    processor: CaptureCallbackProcessor,
    raw_scratch: Vec<f32>,
    downmixer: AdaptiveDownmixer,
}

type StereoCaptureInit<T> = (
    cubeb::Stream<StereoFrame<T>>,
    Arc<RealtimeCallbackState<StereoCaptureRealtime>>,
);
type MonoCaptureInit<T> = (
    cubeb::Stream<MonoFrame<T>>,
    Arc<RealtimeCallbackState<MonoCaptureRealtime>>,
);
type MultichannelCaptureInit<T, const CHANNELS: usize> = (
    cubeb::Stream<MultichannelFrame<T, CHANNELS>>,
    Arc<RealtimeCallbackState<MultichannelCaptureRealtime<CHANNELS>>>,
);

impl MonoCaptureRealtime {
    fn new(resources: CaptureCallbackResources, sample_rate: u32) -> Self {
        Self {
            processor: CaptureCallbackProcessor::new(resources, sample_rate),
            scratch: vec![0.0; CUBEB_MAX_LATENCY_FRAMES as usize],
        }
    }

    fn process<T: CubebCaptureSample>(&mut self, input: &[MonoFrame<T>]) -> bool {
        let decision = self
            .processor
            .classify_preroll(input.len(), |index| input[index].m.to_float() == 0.0);
        let input = &input[decision.skip_frames..];
        if input.len() > self.scratch.len() {
            return false;
        }
        for (sample, frame) in self.scratch.iter_mut().zip(input) {
            *sample = frame.m.to_float();
        }
        let mono = &self.scratch[..input.len()];
        self.processor.process(mono, mono, input.len());
        true
    }
}

impl<const CHANNELS: usize> MultichannelCaptureRealtime<CHANNELS> {
    fn new(resources: CaptureCallbackResources, sample_rate: u32) -> Self {
        Self {
            processor: CaptureCallbackProcessor::new(resources, sample_rate),
            raw_scratch: vec![0.0; CUBEB_MAX_LATENCY_FRAMES as usize * CHANNELS],
            downmixer: AdaptiveDownmixer::with_capacity(
                CHANNELS,
                CUBEB_MAX_LATENCY_FRAMES as usize,
            ),
        }
    }

    fn process<T: CubebCaptureSample>(&mut self, input: &[MultichannelFrame<T, CHANNELS>]) -> bool {
        let decision = self.processor.classify_preroll(input.len(), |index| {
            input[index]
                .channels
                .iter()
                .all(|sample| sample.to_float() == 0.0)
        });
        if decision.matched {
            self.downmixer.reset();
        }
        let input = &input[decision.skip_frames..];
        let Some(samples) = input.len().checked_mul(CHANNELS) else {
            return false;
        };
        if CHANNELS == 0 || samples > self.raw_scratch.len() {
            return false;
        }
        for (target, sample) in self
            .raw_scratch
            .iter_mut()
            .zip(input.iter().flat_map(|frame| frame.channels.iter()))
        {
            *target = sample.to_float();
        }
        let raw = &self.raw_scratch[..samples];
        let mono = self.downmixer.process(raw);
        self.processor.process(mono, raw, input.len());
        true
    }
}

#[allow(clippy::too_many_arguments)]
fn init_stereo_capture_stream<T: CubebCaptureSample>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    device_change: Arc<CubebDeviceChangeSignal>,
    errored: Arc<AtomicBool>,
) -> cubeb::Result<StereoCaptureInit<T>> {
    let realtime: Arc<RealtimeCallbackState<StereoCaptureRealtime>> =
        Arc::new(RealtimeCallbackState::new());
    let callback_realtime = realtime.clone();
    let callback_errored = errored.clone();
    let state_errored = errored.clone();
    let mut builder = StreamBuilder::<StereoFrame<T>>::new();
    builder
        .name("PerfectComms microphone")
        .latency(latency_frames)
        .data_callback(move |input, _| {
            if callback_errored.load(Ordering::Acquire) {
                return input.len().min(isize::MAX as usize) as isize;
            }
            if callback_realtime.with_callback_mut(|state| state.process(input)) != Some(true) {
                callback_errored.store(true, Ordering::Release);
            }
            input.len().min(isize::MAX as usize) as isize
        })
        .state_callback(move |state| {
            if matches!(state, State::Error | State::Drained) {
                state_errored.store(true, Ordering::Release);
            }
        });
    if cubeb_backend_supports_device_change_callback(context.backend_id_bytes()) {
        let callback_device_change = device_change;
        let callback_errored = (!selected.requested_default).then(|| errored.clone());
        builder.device_changed_cb(move || {
            callback_device_change.notify();
            if let Some(errored) = callback_errored.as_ref() {
                // Fail closed until the explicit endpoint is reopened. The legacy AudioUnit
                // backend can otherwise retarget this stream to the system default.
                errored.store(true, Ordering::Release);
            }
        });
    }
    if selected.requested_default {
        builder.default_input(params);
    } else {
        builder.input(selected.device, params);
    }
    builder.init(context).map(|stream| (stream, realtime))
}

#[allow(clippy::too_many_arguments)]
fn init_mono_capture_stream<T: CubebCaptureSample>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    device_change: Arc<CubebDeviceChangeSignal>,
    errored: Arc<AtomicBool>,
) -> cubeb::Result<MonoCaptureInit<T>> {
    let realtime: Arc<RealtimeCallbackState<MonoCaptureRealtime>> =
        Arc::new(RealtimeCallbackState::new());
    let callback_realtime = realtime.clone();
    let callback_errored = errored.clone();
    let state_errored = errored.clone();
    let mut builder = StreamBuilder::<MonoFrame<T>>::new();
    builder
        .name("PerfectComms microphone")
        .latency(latency_frames)
        .data_callback(move |input, _| {
            if callback_errored.load(Ordering::Acquire) {
                return input.len().min(isize::MAX as usize) as isize;
            }
            if callback_realtime.with_callback_mut(|state| state.process(input)) != Some(true) {
                callback_errored.store(true, Ordering::Release);
            }
            input.len().min(isize::MAX as usize) as isize
        })
        .state_callback(move |state| {
            if matches!(state, State::Error | State::Drained) {
                state_errored.store(true, Ordering::Release);
            }
        });
    if cubeb_backend_supports_device_change_callback(context.backend_id_bytes()) {
        let callback_device_change = device_change;
        let callback_errored = (!selected.requested_default).then(|| errored.clone());
        builder.device_changed_cb(move || {
            callback_device_change.notify();
            if let Some(errored) = callback_errored.as_ref() {
                errored.store(true, Ordering::Release);
            }
        });
    }
    if selected.requested_default {
        builder.default_input(params);
    } else {
        builder.input(selected.device, params);
    }
    builder.init(context).map(|stream| (stream, realtime))
}

#[allow(clippy::too_many_arguments)]
fn init_multichannel_capture_stream<T: CubebCaptureSample, const CHANNELS: usize>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    device_change: Arc<CubebDeviceChangeSignal>,
    errored: Arc<AtomicBool>,
) -> cubeb::Result<MultichannelCaptureInit<T, CHANNELS>> {
    let realtime: Arc<RealtimeCallbackState<MultichannelCaptureRealtime<CHANNELS>>> =
        Arc::new(RealtimeCallbackState::new());
    let callback_realtime = realtime.clone();
    let callback_errored = errored.clone();
    let state_errored = errored.clone();
    let mut builder = StreamBuilder::<MultichannelFrame<T, CHANNELS>>::new();
    builder
        .name("PerfectComms microphone")
        .latency(latency_frames)
        .data_callback(move |input, _| {
            if callback_errored.load(Ordering::Acquire) {
                return input.len().min(isize::MAX as usize) as isize;
            }
            if callback_realtime.with_callback_mut(|state| state.process(input)) != Some(true) {
                callback_errored.store(true, Ordering::Release);
            }
            input.len().min(isize::MAX as usize) as isize
        })
        .state_callback(move |state| {
            if matches!(state, State::Error | State::Drained) {
                state_errored.store(true, Ordering::Release);
            }
        });
    if cubeb_backend_supports_device_change_callback(context.backend_id_bytes()) {
        let callback_device_change = device_change;
        let callback_errored = (!selected.requested_default).then(|| errored.clone());
        builder.device_changed_cb(move || {
            callback_device_change.notify();
            if let Some(errored) = callback_errored.as_ref() {
                errored.store(true, Ordering::Release);
            }
        });
    }
    if selected.requested_default {
        builder.default_input(params);
    } else {
        builder.input(selected.device, params);
    }
    builder.init(context).map(|stream| (stream, realtime))
}

fn open_stereo_capture_stream<T: CubebCaptureSample>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    sample_rate: u32,
    resources: CaptureCallbackResources,
    errored: Arc<AtomicBool>,
) -> Result<cubeb::Stream<StereoFrame<T>>, String> {
    let device_change = resources.device_change.clone();
    let (stream, realtime) = init_stereo_capture_stream(
        context,
        params,
        selected,
        latency_frames,
        device_change,
        errored,
    )
    .map_err(|error| format!("init failed: {error}"))?;
    realtime
        .initialize(StereoCaptureRealtime::new(resources, sample_rate))
        .map_err(|_| "internal stereo callback initialization was duplicated".to_string())?;
    stream
        .start()
        .map_err(|error| format!("start failed: {error}"))?;
    Ok(stream)
}

fn open_mono_capture_stream<T: CubebCaptureSample>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    sample_rate: u32,
    resources: CaptureCallbackResources,
    errored: Arc<AtomicBool>,
) -> Result<cubeb::Stream<MonoFrame<T>>, String> {
    let device_change = resources.device_change.clone();
    let (stream, realtime) = init_mono_capture_stream(
        context,
        params,
        selected,
        latency_frames,
        device_change,
        errored,
    )
    .map_err(|error| format!("init failed: {error}"))?;
    realtime
        .initialize(MonoCaptureRealtime::new(resources, sample_rate))
        .map_err(|_| "internal mono callback initialization was duplicated".to_string())?;
    stream
        .start()
        .map_err(|error| format!("start failed: {error}"))?;
    Ok(stream)
}

fn open_multichannel_capture_stream<T: CubebCaptureSample, const CHANNELS: usize>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    sample_rate: u32,
    resources: CaptureCallbackResources,
    errored: Arc<AtomicBool>,
) -> Result<cubeb::Stream<MultichannelFrame<T, CHANNELS>>, String> {
    let device_change = resources.device_change.clone();
    let (stream, realtime) = init_multichannel_capture_stream(
        context,
        params,
        selected,
        latency_frames,
        device_change,
        errored,
    )
    .map_err(|error| format!("init failed: {error}"))?;
    realtime
        .initialize(MultichannelCaptureRealtime::new(resources, sample_rate))
        .map_err(|_| {
            "internal multichannel input callback initialization was duplicated".to_string()
        })?;
    stream
        .start()
        .map_err(|error| format!("start failed: {error}"))?;
    Ok(stream)
}

fn open_capture_candidate(
    context: &cubeb::Context,
    selected: &SelectedDevice,
    prefs: StreamPrefs,
    candidate: CubebStreamCandidate,
    resources: CaptureCallbackResources,
    errored: Arc<AtomicBool>,
) -> Result<(CubebCaptureStream, u32), String> {
    let layout = cubeb_output_channel_layout(candidate.channels).ok_or_else(|| {
        format!(
            "unsupported Cubeb input channel count: {}",
            candidate.channels
        )
    })?;
    let params = StreamParamsBuilder::new()
        .format(candidate.sample.format())
        .rate(candidate.rate)
        .channels(u32::from(candidate.channels))
        .layout(layout)
        .prefs(prefs)
        .take();
    let latency_frames = cubeb_latency_frames(context, params.as_ref());
    let mut resources = resources;
    resources.requested_latency_frames = latency_frames;
    resources.wasapi_preroll_filter =
        cubeb_backend_injects_wasapi_preroll(context.backend_id_bytes());
    // Seed every backend with a bounded estimate before start. Current C PulseAudio and ALSA
    // backends do not implement stream_get_input_latency, and WASAPI's first callback can race
    // the first successful query. A later backend value atomically upgrades this estimate.
    resources
        .backend_latency_frames
        .store(latency_frames, Ordering::Release);
    resources
        .latency_probe_requested
        .store(true, Ordering::Release);
    let stream = match (candidate.sample, candidate.channels) {
        (CubebSampleKind::Float32, 2) => open_stereo_capture_stream::<f32>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::StereoF32),
        (CubebSampleKind::Float32, 1) => open_mono_capture_stream::<f32>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::MonoF32),
        (CubebSampleKind::Float32, 4) => open_multichannel_capture_stream::<f32, 4>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::QuadF32),
        (CubebSampleKind::Float32, 6) => open_multichannel_capture_stream::<f32, 6>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::Surround51F32),
        (CubebSampleKind::Float32, 8) => open_multichannel_capture_stream::<f32, 8>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::Surround71F32),
        (CubebSampleKind::Signed16, 2) => open_stereo_capture_stream::<i16>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::StereoI16),
        (CubebSampleKind::Signed16, 1) => open_mono_capture_stream::<i16>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::MonoI16),
        (CubebSampleKind::Signed16, 4) => open_multichannel_capture_stream::<i16, 4>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::QuadI16),
        (CubebSampleKind::Signed16, 6) => open_multichannel_capture_stream::<i16, 6>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::Surround51I16),
        (CubebSampleKind::Signed16, 8) => open_multichannel_capture_stream::<i16, 8>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebCaptureStream::Surround71I16),
        _ => Err("unsupported Cubeb capture candidate".to_string()),
    }?;
    Ok((stream, latency_frames))
}

#[allow(clippy::too_many_arguments)]
pub fn spawn_cubeb_capture(
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
    #[cfg(windows)]
    let _com = WindowsComApartment::initialize()
        .map_err(|error| format!("initialize COM for Cubeb input: {error}"))?;
    aec_timing.reset_capture_path();
    let open_attempt = diagnostics.begin_open_attempt();
    let context = init_cubeb_context("PerfectComms microphone")
        .map_err(|error| format!("initialize Cubeb input context: {error}"))?;
    let explicit = device_id.as_deref().is_some_and(|id| !id.is_empty());
    let devices = if explicit {
        Some(
            context
                .enumerate_devices(DeviceType::INPUT)
                .map_err(|error| format!("enumerate input devices: {error}"))?,
        )
    } else {
        None
    };
    let preferred_rate = context.preferred_sample_rate().unwrap_or(SAMPLE_RATE);
    let selected = pick_device(
        devices.as_ref(),
        &device_id,
        preferred_rate,
        "input",
        DeviceType::INPUT,
        context.backend_id_bytes(),
    )?;
    let prefs = if explicit {
        StreamPrefs::DISABLE_DEVICE_SWITCHING
    } else {
        StreamPrefs::NONE
    };
    let errored = Arc::new(AtomicBool::new(false));
    let device_change = Arc::new(CubebDeviceChangeSignal::default());
    let backend_latency_frames = Arc::new(AtomicU32::new(UNKNOWN_CUBEB_LATENCY_FRAMES));
    let latency_probe_requested = Arc::new(AtomicBool::new(true));
    let first_callback_ns = Arc::new(AtomicU64::new(0));
    let first_callback_frames = Arc::new(AtomicU64::new(0));
    let resources = CaptureCallbackResources {
        ring,
        encoder_epoch,
        aec_timing: aec_timing.clone(),
        diagnostics: diagnostics.clone(),
        device_change: device_change.clone(),
        backend_latency_frames: backend_latency_frames.clone(),
        latency_probe_requested: latency_probe_requested.clone(),
        first_callback_ns: first_callback_ns.clone(),
        first_callback_frames: first_callback_frames.clone(),
        wasapi_preroll_filter: false,
        requested_latency_frames: FRAME_SAMPLES as u32,
        stream_generation,
        open_attempt,
    };

    // Preserve stereo and mono as the preferred paths, then try quad/5.1/7.1 capture for strict
    // USB interfaces and hardware endpoints. Every attempt retains the exact selected endpoint,
    // and multichannel streams are adaptively downmixed before entering the mono voice pipeline.
    let mut failures = Vec::new();
    let mut opened = None;
    for candidate in
        cubeb_capture_stream_candidates(context.backend_id_bytes(), selected.preferred_rate)
    {
        device_change.clear_pending();
        errored.store(false, Ordering::Release);
        match open_capture_candidate(
            &context,
            &selected,
            prefs,
            candidate,
            resources.clone(),
            errored.clone(),
        ) {
            Ok((stream, latency_frames)) => {
                opened = Some((stream, candidate, latency_frames));
                break;
            }
            Err(error) => failures.push(format!(
                "{}/{}/{}ch: {error}",
                candidate.sample.label(),
                candidate.rate,
                candidate.channels
            )),
        }
    }
    let (stream, candidate, latency_frames) =
        opened.ok_or_else(|| format!("build input stream: {}", failures.join("; ")))?;
    let descriptor = StreamDescriptor {
        requested_device: selected.requested_device.clone(),
        resolved_device: selected.resolved_device.clone(),
        requested_default: selected.requested_default,
        requested_matched: selected.requested_matched,
        fell_back_to_default: selected.fell_back_to_default,
        sample_rate: candidate.rate,
        channels: candidate.channels,
        sample_format: candidate.sample.label().to_string(),
        buffer_mode: cubeb_buffer_mode(&context),
        buffer_min_frames: latency_frames,
        buffer_max_frames: latency_frames,
    };
    // Keep `devices` alive through all init/startup work: backend endpoint handles can point into
    // the enumeration collection and are only safe to release after Cubeb resolves the stream.
    match device_change.take_action(selected.requested_default) {
        CubebDeviceChangeAction::ResetDefaultStream => {
            aec_timing.reset_capture_path();
            backend_latency_frames.store(latency_frames, Ordering::Release);
            store_cubeb_latency(&backend_latency_frames, stream.input_latency());
            latency_probe_requested.store(true, Ordering::Release);
        }
        CubebDeviceChangeAction::ReopenExplicitStream => {
            let _ = stream.stop();
            return Err(
                "selected input device changed; CoreAudio callback requires stream reopen"
                    .to_string(),
            );
        }
        CubebDeviceChangeAction::None => {}
    }
    if errored.load(Ordering::Acquire) {
        let _ = stream.stop();
        return Err("input device failed while starting".to_string());
    }
    let started_ns = monotonic_ns();
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
    let mut last_latency_refresh = Instant::now() - CUBEB_FAST_LATENCY_REFRESH;
    let mut fast_probe_active = true;
    let mut fast_probe_deadline = Instant::now() + CUBEB_FAST_LATENCY_PROBE_WINDOW;
    while !stop.load(Ordering::Relaxed) {
        match device_change.take_action(selected.requested_default) {
            CubebDeviceChangeAction::ResetDefaultStream => {
                aec_timing.reset_capture_path();
                backend_latency_frames.store(latency_frames, Ordering::Release);
                latency_probe_requested.store(true, Ordering::Release);
                last_latency_refresh = Instant::now() - CUBEB_FAST_LATENCY_REFRESH;
            }
            CubebDeviceChangeAction::ReopenExplicitStream => {
                let _ = stream.stop();
                return Err(
                    "selected input device changed; CoreAudio callback requires stream reopen"
                        .to_string(),
                );
            }
            CubebDeviceChangeAction::None => {}
        }
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
        if latency_probe_requested.swap(false, Ordering::AcqRel) {
            fast_probe_active = true;
            fast_probe_deadline = Instant::now() + CUBEB_FAST_LATENCY_PROBE_WINDOW;
        }
        let refresh_interval = if fast_probe_active {
            CUBEB_FAST_LATENCY_REFRESH
        } else {
            CUBEB_LATENCY_REFRESH
        };
        if last_latency_refresh.elapsed() >= refresh_interval {
            let latency = stream.input_latency();
            let succeeded = latency.as_ref().is_ok_and(|frames| *frames > 0);
            store_cubeb_latency(&backend_latency_frames, latency);
            if succeeded || Instant::now() >= fast_probe_deadline {
                fast_probe_active = false;
            }
            last_latency_refresh = Instant::now();
        }
        if errored.load(Ordering::Acquire) {
            let _ = stream.stop();
            return Err("input device stream failed".to_string());
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
    stream
        .stop()
        .map_err(|error| format!("input stream stop: {error}"))?;
    Ok(())
}

pub fn enumerate_output_devices() -> Result<Vec<DeviceInfo>, String> {
    enumerate_cubeb_devices(DeviceType::OUTPUT)
}

const UNDERRUN_FADE_SAMPLES: u32 = 240; // 5 ms at the internal 48 kHz clock.
const UNDERRUN_RESUME_SAMPLES: u32 = 96; // 2 ms crossfade when audio returns.

struct UnderrunFader {
    fade_samples: u32,
    resume_samples: u32,
    last_output: (f32, f32),
    fade_from: (f32, f32),
    fade_remaining: u32,
    resume_remaining: u32,
    resume_from: (f32, f32),
    starved: bool,
}

impl Default for UnderrunFader {
    fn default() -> Self {
        Self::for_sample_rate(SAMPLE_RATE)
    }
}

impl UnderrunFader {
    fn for_sample_rate(sample_rate: u32) -> Self {
        let scaled = |samples: u32| {
            u64::from(samples)
                .saturating_mul(u64::from(sample_rate.max(1)))
                .div_ceil(u64::from(SAMPLE_RATE))
                .clamp(1, u64::from(u32::MAX)) as u32
        };
        Self {
            fade_samples: scaled(UNDERRUN_FADE_SAMPLES),
            resume_samples: scaled(UNDERRUN_RESUME_SAMPLES),
            last_output: (0.0, 0.0),
            fade_from: (0.0, 0.0),
            fade_remaining: 0,
            resume_remaining: 0,
            resume_from: (0.0, 0.0),
            starved: false,
        }
    }

    fn reset(&mut self) {
        let fade_samples = self.fade_samples;
        let resume_samples = self.resume_samples;
        *self = Self {
            fade_samples,
            resume_samples,
            ..Self::for_sample_rate(SAMPLE_RATE)
        };
    }

    fn process(&mut self, pair: (f32, f32), available: bool) -> (f32, f32) {
        let output = if !available {
            if !self.starved {
                self.starved = true;
                self.fade_remaining = self.fade_samples;
                self.fade_from = self.last_output;
                self.resume_remaining = 0;
            }
            if self.fade_remaining == 0 {
                (0.0, 0.0)
            } else {
                let factor = self.fade_remaining as f32 / self.fade_samples as f32;
                self.fade_remaining -= 1;
                (self.fade_from.0 * factor, self.fade_from.1 * factor)
            }
        } else {
            if self.starved {
                self.starved = false;
                self.resume_remaining = self.resume_samples;
                self.resume_from = self.last_output;
            }
            if self.resume_remaining == 0 {
                pair
            } else {
                let progress = 1.0 - self.resume_remaining as f32 / self.resume_samples as f32;
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

const MONITOR_IMMEDIATE_TARGET_PAIRS: usize = FRAME_SAMPLES;
const MONITOR_DEPTH_RESPONSE: f64 = 0.05;
const MONITOR_MAX_RATE_ADJUSTMENT: f64 = 0.004;

fn monitor_target_pairs(delay_pairs: usize) -> usize {
    if delay_pairs == 0 {
        MONITOR_IMMEDIATE_TARGET_PAIRS
    } else {
        delay_pairs
    }
}

fn monitor_effective_ratio(nominal_ratio: f64, queued_pairs: usize, target_pairs: usize) -> f64 {
    if target_pairs == 0 {
        return nominal_ratio;
    }
    let error = (queued_pairs as f64 - target_pairs as f64) / target_pairs as f64;
    nominal_ratio
        * (1.0
            + (error * MONITOR_DEPTH_RESPONSE)
                .clamp(-MONITOR_MAX_RATE_ADJUSTMENT, MONITOR_MAX_RATE_ADJUSTMENT))
}

struct MonitorPlayout {
    observed_generation: u64,
    primed: bool,
    s0: (f32, f32),
    s1: (f32, f32),
    s0_available: bool,
    s1_available: bool,
    pos: f64,
    underrun_fader: UnderrunFader,
}

impl Default for MonitorPlayout {
    fn default() -> Self {
        Self {
            observed_generation: u64::MAX,
            primed: false,
            s0: (0.0, 0.0),
            s1: (0.0, 0.0),
            s0_available: false,
            s1_available: false,
            pos: 1.0,
            underrun_fader: UnderrunFader::default(),
        }
    }
}

impl MonitorPlayout {
    fn for_sample_rate(sample_rate: u32) -> Self {
        Self {
            underrun_fader: UnderrunFader::for_sample_rate(sample_rate),
            ..Self::default()
        }
    }

    fn reset_interpolation(&mut self) {
        self.primed = false;
        self.s0 = (0.0, 0.0);
        self.s1 = (0.0, 0.0);
        self.s0_available = false;
        self.s1_available = false;
        self.pos = 1.0;
    }

    fn reset_for_generation(&mut self, generation: u64) {
        self.observed_generation = generation;
        self.reset_interpolation();
        self.underrun_fader.reset();
    }

    fn render(
        &mut self,
        ring: &PlaybackConsumer,
        state: &MicrophoneMonitorState,
        nominal_ratio: f64,
    ) -> (f32, f32) {
        let generation = state.playout_generation();
        if generation != self.observed_generation {
            self.reset_for_generation(generation);
        }

        if !state.enabled() {
            self.reset_interpolation();
            return self.underrun_fader.process((0.0, 0.0), false);
        }

        let target_pairs = monitor_target_pairs(state.delay_pairs());
        let queued_pairs = ring.len();
        if !self.primed {
            if queued_pairs < target_pairs {
                return self.underrun_fader.process((0.0, 0.0), false);
            }
            // A prior underrun can leave interpolation history even though the ring itself is
            // empty. Start every newly primed timeline from silence so stale audio cannot bridge
            // a capture outage or generation reset.
            self.reset_interpolation();
            self.primed = true;
        }

        let effective_ratio = monitor_effective_ratio(nominal_ratio, queued_pairs, target_pairs);
        while self.pos >= 1.0 {
            self.s0 = self.s1;
            self.s0_available = self.s1_available;
            match ring.pop_stereo() {
                Some(pair) => {
                    self.s1 = pair;
                    self.s1_available = true;
                }
                None => {
                    self.s1 = (0.0, 0.0);
                    self.s1_available = false;
                    self.primed = false;
                }
            }
            self.pos -= 1.0;
        }

        let t = self.pos as f32;
        let pair = (
            self.s0.0 + (self.s1.0 - self.s0.0) * t,
            self.s0.1 + (self.s1.1 - self.s0.1) * t,
        );
        let available = self.s0_available || self.s1_available;
        self.pos += effective_ratio;
        self.underrun_fader.process(pair, available)
    }
}

#[derive(Clone)]
struct PlaybackCallbackResources {
    ring: PlaybackConsumer,
    monitor_ring: PlaybackConsumer,
    monitor_state: Arc<MicrophoneMonitorState>,
    counters: Arc<NativeCounters>,
    progress: Arc<PlaybackProgress>,
    aec_timing: Arc<AecTiming>,
    diagnostics: Arc<PlaybackDiagnostics>,
    device_change: Arc<CubebDeviceChangeSignal>,
    start_succeeded: Arc<AtomicBool>,
    backend_latency_frames: Arc<AtomicU32>,
    first_callback_ns: Arc<AtomicU64>,
    first_callback_frames: Arc<AtomicU64>,
}

struct PlaybackRealtime {
    resources: PlaybackCallbackResources,
    sample_rate: u32,
    remote_s0: (f32, f32),
    remote_s1: (f32, f32),
    remote_s0_available: bool,
    remote_s1_available: bool,
    remote_pos: f64,
    remote_underrun_fader: UnderrunFader,
    monitor_playout: MonitorPlayout,
    flat_output: Vec<f32>,
}

impl PlaybackRealtime {
    fn new(resources: PlaybackCallbackResources, sample_rate: u32) -> Self {
        Self {
            resources,
            sample_rate,
            remote_s0: (0.0, 0.0),
            remote_s1: (0.0, 0.0),
            remote_s0_available: false,
            remote_s1_available: false,
            remote_pos: 1.0,
            remote_underrun_fader: UnderrunFader::for_sample_rate(sample_rate),
            monitor_playout: MonitorPlayout::for_sample_rate(sample_rate),
            flat_output: vec![0.0; CUBEB_MAX_LATENCY_FRAMES as usize * 2],
        }
    }

    fn render(&mut self, frames: usize, mut write: impl FnMut(usize, f32, f32)) -> bool {
        let Some(flat_samples) = frames.checked_mul(2) else {
            return false;
        };
        if flat_samples > self.flat_output.len() {
            return false;
        }
        let resources = &self.resources;
        let callback_ns = monotonic_ns();
        if resources.diagnostics.observe_callback(callback_ns, frames) {
            resources
                .first_callback_frames
                .store(frames as u64, Ordering::Release);
            resources
                .first_callback_ns
                .store(callback_ns, Ordering::Release);
        }
        resources.aec_timing.observe_output_latency_frames(
            load_cubeb_latency(&resources.backend_latency_frames),
            self.sample_rate,
        );
        resources.progress.mark_callback();
        resources
            .counters
            .playback_callbacks
            .fetch_add(1, Ordering::Relaxed);

        let target_pairs = (FRAME_SAMPLES * 4) as f64;
        let queued_pairs = resources.ring.len();
        let ratio = SAMPLE_RATE as f64 / f64::from(self.sample_rate.max(1));
        let effective_ratio = if queued_pairs == 0 {
            ratio
        } else {
            let error = (queued_pairs as f64 - target_pairs) / target_pairs;
            ratio * (1.0 + (error * 0.05).clamp(-0.004, 0.004))
        };
        let mut requested_pairs = 0u64;
        let mut consumed_pairs = 0u64;
        let mut underrun_pairs = 0u64;
        for frame in 0..frames {
            while self.remote_pos >= 1.0 {
                self.remote_s0 = self.remote_s1;
                self.remote_s0_available = self.remote_s1_available;
                requested_pairs += 1;
                match resources.ring.pop_stereo() {
                    Some(pair) => {
                        self.remote_s1 = pair;
                        self.remote_s1_available = true;
                        consumed_pairs += 1;
                    }
                    None => {
                        self.remote_s1 = (0.0, 0.0);
                        self.remote_s1_available = false;
                        underrun_pairs += 1;
                    }
                }
                self.remote_pos -= 1.0;
            }
            let remote_t = self.remote_pos as f32;
            let remote_pair = (
                self.remote_s0.0 + (self.remote_s1.0 - self.remote_s0.0) * remote_t,
                self.remote_s0.1 + (self.remote_s1.1 - self.remote_s0.1) * remote_t,
            );
            let remote_pair = self.remote_underrun_fader.process(
                remote_pair,
                self.remote_s0_available || self.remote_s1_available,
            );
            self.remote_pos += effective_ratio;

            let monitor_pair = self.monitor_playout.render(
                &resources.monitor_ring,
                &resources.monitor_state,
                ratio,
            );
            let monitor_gain = resources.monitor_state.gain();
            let left = (remote_pair.0 + monitor_pair.0 * monitor_gain).clamp(-1.0, 1.0);
            let right = (remote_pair.1 + monitor_pair.1 * monitor_gain).clamp(-1.0, 1.0);
            write(frame, left, right);
            self.flat_output[frame * 2] = left;
            self.flat_output[frame * 2 + 1] = right;
        }
        resources
            .counters
            .playback_requested_pairs
            .fetch_add(requested_pairs, Ordering::Relaxed);
        resources
            .counters
            .playback_consumed_pairs
            .fetch_add(consumed_pairs, Ordering::Relaxed);
        resources
            .counters
            .playback_underrun_pairs
            .fetch_add(underrun_pairs, Ordering::Relaxed);
        resources
            .counters
            .record_playback_output(&self.flat_output[..flat_samples]);
        true
    }
}

#[repr(transparent)]
#[derive(Clone, Copy)]
struct MultichannelFrame<T, const CHANNELS: usize> {
    channels: [T; CHANNELS],
}

impl<T, const CHANNELS: usize> cubeb::Frame for MultichannelFrame<T, CHANNELS> {}

fn map_stereo_to_multichannel<const CHANNELS: usize>(left: f32, right: f32) -> [f32; CHANNELS] {
    let mut output = [0.0; CHANNELS];
    if CHANNELS < 2 {
        return output;
    }
    output[0] = left;
    output[1] = right;
    match CHANNELS {
        // Cubeb QUAD: FL, FR, BL, BR.
        4 => {
            output[2] = left * 0.5;
            output[3] = right * 0.5;
        }
        // Cubeb 3F2_LFE: FL, FR, FC, LFE, SL, SR.
        6 => {
            output[2] = (left + right) * 0.5;
            output[4] = left * 0.5;
            output[5] = right * 0.5;
        }
        // Cubeb 3F4_LFE: FL, FR, FC, LFE, BL, BR, SL, SR.
        8 => {
            output[2] = (left + right) * 0.5;
            output[4] = left * 0.5;
            output[5] = right * 0.5;
            output[6] = left * 0.5;
            output[7] = right * 0.5;
        }
        _ => {}
    }
    output
}

enum CubebPlaybackStream {
    MonoF32(cubeb::Stream<MonoFrame<f32>>),
    StereoF32(cubeb::Stream<StereoFrame<f32>>),
    QuadF32(cubeb::Stream<MultichannelFrame<f32, 4>>),
    Surround51F32(cubeb::Stream<MultichannelFrame<f32, 6>>),
    Surround71F32(cubeb::Stream<MultichannelFrame<f32, 8>>),
    MonoI16(cubeb::Stream<MonoFrame<i16>>),
    StereoI16(cubeb::Stream<StereoFrame<i16>>),
    QuadI16(cubeb::Stream<MultichannelFrame<i16, 4>>),
    Surround51I16(cubeb::Stream<MultichannelFrame<i16, 6>>),
    Surround71I16(cubeb::Stream<MultichannelFrame<i16, 8>>),
}

type StereoPlaybackInit<T> = (
    cubeb::Stream<StereoFrame<T>>,
    Arc<RealtimeCallbackState<PlaybackRealtime>>,
);
type MonoPlaybackInit<T> = (
    cubeb::Stream<MonoFrame<T>>,
    Arc<RealtimeCallbackState<PlaybackRealtime>>,
);
type MultichannelPlaybackInit<T, const CHANNELS: usize> = (
    cubeb::Stream<MultichannelFrame<T, CHANNELS>>,
    Arc<RealtimeCallbackState<PlaybackRealtime>>,
);

impl CubebPlaybackStream {
    fn stop(&self) -> cubeb::Result<()> {
        match self {
            Self::MonoF32(stream) => stream.stop(),
            Self::StereoF32(stream) => stream.stop(),
            Self::QuadF32(stream) => stream.stop(),
            Self::Surround51F32(stream) => stream.stop(),
            Self::Surround71F32(stream) => stream.stop(),
            Self::MonoI16(stream) => stream.stop(),
            Self::StereoI16(stream) => stream.stop(),
            Self::QuadI16(stream) => stream.stop(),
            Self::Surround51I16(stream) => stream.stop(),
            Self::Surround71I16(stream) => stream.stop(),
        }
    }

    fn latency(&self) -> cubeb::Result<u32> {
        match self {
            Self::MonoF32(stream) => stream.latency(),
            Self::StereoF32(stream) => stream.latency(),
            Self::QuadF32(stream) => stream.latency(),
            Self::Surround51F32(stream) => stream.latency(),
            Self::Surround71F32(stream) => stream.latency(),
            Self::MonoI16(stream) => stream.latency(),
            Self::StereoI16(stream) => stream.latency(),
            Self::QuadI16(stream) => stream.latency(),
            Self::Surround51I16(stream) => stream.latency(),
            Self::Surround71I16(stream) => stream.latency(),
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn init_stereo_playback_stream<T: CubebPlaybackSample>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    gate: Arc<AtomicBool>,
    device_change: Arc<CubebDeviceChangeSignal>,
    errored: Arc<AtomicBool>,
) -> cubeb::Result<StereoPlaybackInit<T>> {
    let realtime: Arc<RealtimeCallbackState<PlaybackRealtime>> =
        Arc::new(RealtimeCallbackState::new());
    let callback_realtime = realtime.clone();
    let callback_errored = errored.clone();
    let state_errored = errored.clone();
    let mut builder = StreamBuilder::<StereoFrame<T>>::new();
    builder
        .name("PerfectComms speaker")
        .latency(latency_frames)
        .data_callback(move |_, output| {
            if !gate.load(Ordering::Acquire) || callback_errored.load(Ordering::Acquire) {
                for frame in output.iter_mut() {
                    frame.l = T::from_float(0.0);
                    frame.r = T::from_float(0.0);
                }
                return output.len().min(isize::MAX as usize) as isize;
            }
            let rendered = callback_realtime.with_callback_mut(|state| {
                state.render(output.len(), |index, left, right| {
                    output[index].l = T::from_float(left);
                    output[index].r = T::from_float(right);
                })
            });
            if rendered != Some(true) {
                for frame in output.iter_mut() {
                    frame.l = T::from_float(0.0);
                    frame.r = T::from_float(0.0);
                }
                callback_errored.store(true, Ordering::Release);
            }
            output.len().min(isize::MAX as usize) as isize
        })
        .state_callback(move |state| {
            if matches!(state, State::Error | State::Drained) {
                state_errored.store(true, Ordering::Release);
            }
        });
    if cubeb_backend_supports_device_change_callback(context.backend_id_bytes()) {
        let callback_device_change = device_change;
        let callback_errored = (!selected.requested_default).then(|| errored.clone());
        builder.device_changed_cb(move || {
            callback_device_change.notify();
            if let Some(errored) = callback_errored.as_ref() {
                // Silence an explicit stream immediately; the control thread will report the
                // change and let the managed watchdog reopen the same selected endpoint.
                errored.store(true, Ordering::Release);
            }
        });
    }
    if selected.requested_default {
        builder.default_output(params);
    } else {
        builder.output(selected.device, params);
    }
    builder.init(context).map(|stream| (stream, realtime))
}

#[allow(clippy::too_many_arguments)]
fn init_mono_playback_stream<T: CubebPlaybackSample>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    gate: Arc<AtomicBool>,
    device_change: Arc<CubebDeviceChangeSignal>,
    errored: Arc<AtomicBool>,
) -> cubeb::Result<MonoPlaybackInit<T>> {
    let realtime: Arc<RealtimeCallbackState<PlaybackRealtime>> =
        Arc::new(RealtimeCallbackState::new());
    let callback_realtime = realtime.clone();
    let callback_errored = errored.clone();
    let state_errored = errored.clone();
    let mut builder = StreamBuilder::<MonoFrame<T>>::new();
    builder
        .name("PerfectComms speaker")
        .latency(latency_frames)
        .data_callback(move |_, output| {
            if !gate.load(Ordering::Acquire) || callback_errored.load(Ordering::Acquire) {
                for frame in output.iter_mut() {
                    frame.m = T::from_float(0.0);
                }
                return output.len().min(isize::MAX as usize) as isize;
            }
            let rendered = callback_realtime.with_callback_mut(|state| {
                state.render(output.len(), |index, left, right| {
                    output[index].m = T::from_float((left + right) * 0.5);
                })
            });
            if rendered != Some(true) {
                for frame in output.iter_mut() {
                    frame.m = T::from_float(0.0);
                }
                callback_errored.store(true, Ordering::Release);
            }
            output.len().min(isize::MAX as usize) as isize
        })
        .state_callback(move |state| {
            if matches!(state, State::Error | State::Drained) {
                state_errored.store(true, Ordering::Release);
            }
        });
    if cubeb_backend_supports_device_change_callback(context.backend_id_bytes()) {
        let callback_device_change = device_change;
        let callback_errored = (!selected.requested_default).then(|| errored.clone());
        builder.device_changed_cb(move || {
            callback_device_change.notify();
            if let Some(errored) = callback_errored.as_ref() {
                errored.store(true, Ordering::Release);
            }
        });
    }
    if selected.requested_default {
        builder.default_output(params);
    } else {
        builder.output(selected.device, params);
    }
    builder.init(context).map(|stream| (stream, realtime))
}

#[allow(clippy::too_many_arguments)]
fn init_multichannel_playback_stream<T: CubebPlaybackSample, const CHANNELS: usize>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    gate: Arc<AtomicBool>,
    device_change: Arc<CubebDeviceChangeSignal>,
    errored: Arc<AtomicBool>,
) -> cubeb::Result<MultichannelPlaybackInit<T, CHANNELS>> {
    let realtime: Arc<RealtimeCallbackState<PlaybackRealtime>> =
        Arc::new(RealtimeCallbackState::new());
    let callback_realtime = realtime.clone();
    let callback_errored = errored.clone();
    let state_errored = errored.clone();
    let mut builder = StreamBuilder::<MultichannelFrame<T, CHANNELS>>::new();
    builder
        .name("PerfectComms speaker")
        .latency(latency_frames)
        .data_callback(move |_, output| {
            if !gate.load(Ordering::Acquire) || callback_errored.load(Ordering::Acquire) {
                for frame in output.iter_mut() {
                    frame.channels.fill(T::from_float(0.0));
                }
                return output.len().min(isize::MAX as usize) as isize;
            }
            let rendered = callback_realtime.with_callback_mut(|state| {
                state.render(output.len(), |index, left, right| {
                    let mapped = map_stereo_to_multichannel::<CHANNELS>(left, right);
                    for (target, sample) in output[index].channels.iter_mut().zip(mapped) {
                        *target = T::from_float(sample);
                    }
                })
            });
            if rendered != Some(true) {
                for frame in output.iter_mut() {
                    frame.channels.fill(T::from_float(0.0));
                }
                callback_errored.store(true, Ordering::Release);
            }
            output.len().min(isize::MAX as usize) as isize
        })
        .state_callback(move |state| {
            if matches!(state, State::Error | State::Drained) {
                state_errored.store(true, Ordering::Release);
            }
        });
    if cubeb_backend_supports_device_change_callback(context.backend_id_bytes()) {
        let callback_device_change = device_change;
        let callback_errored = (!selected.requested_default).then(|| errored.clone());
        builder.device_changed_cb(move || {
            callback_device_change.notify();
            if let Some(errored) = callback_errored.as_ref() {
                errored.store(true, Ordering::Release);
            }
        });
    }
    if selected.requested_default {
        builder.default_output(params);
    } else {
        builder.output(selected.device, params);
    }
    builder.init(context).map(|stream| (stream, realtime))
}

fn open_stereo_playback_stream<T: CubebPlaybackSample>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    sample_rate: u32,
    resources: PlaybackCallbackResources,
    errored: Arc<AtomicBool>,
) -> Result<cubeb::Stream<StereoFrame<T>>, String> {
    let gate = resources.start_succeeded.clone();
    let device_change = resources.device_change.clone();
    let (stream, realtime) = init_stereo_playback_stream(
        context,
        params,
        selected,
        latency_frames,
        gate,
        device_change,
        errored,
    )
    .map_err(|error| format!("init failed: {error}"))?;
    realtime
        .initialize(PlaybackRealtime::new(resources, sample_rate))
        .map_err(|_| "internal stereo output callback initialization was duplicated".to_string())?;
    stream
        .start()
        .map_err(|error| format!("start failed: {error}"))?;
    Ok(stream)
}

fn open_mono_playback_stream<T: CubebPlaybackSample>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    sample_rate: u32,
    resources: PlaybackCallbackResources,
    errored: Arc<AtomicBool>,
) -> Result<cubeb::Stream<MonoFrame<T>>, String> {
    let gate = resources.start_succeeded.clone();
    let device_change = resources.device_change.clone();
    let (stream, realtime) = init_mono_playback_stream(
        context,
        params,
        selected,
        latency_frames,
        gate,
        device_change,
        errored,
    )
    .map_err(|error| format!("init failed: {error}"))?;
    realtime
        .initialize(PlaybackRealtime::new(resources, sample_rate))
        .map_err(|_| "internal mono output callback initialization was duplicated".to_string())?;
    stream
        .start()
        .map_err(|error| format!("start failed: {error}"))?;
    Ok(stream)
}

fn open_multichannel_playback_stream<T: CubebPlaybackSample, const CHANNELS: usize>(
    context: &cubeb::Context,
    params: &cubeb::StreamParamsRef,
    selected: &SelectedDevice,
    latency_frames: u32,
    sample_rate: u32,
    resources: PlaybackCallbackResources,
    errored: Arc<AtomicBool>,
) -> Result<cubeb::Stream<MultichannelFrame<T, CHANNELS>>, String> {
    let gate = resources.start_succeeded.clone();
    let device_change = resources.device_change.clone();
    let (stream, realtime) = init_multichannel_playback_stream(
        context,
        params,
        selected,
        latency_frames,
        gate,
        device_change,
        errored,
    )
    .map_err(|error| format!("init failed: {error}"))?;
    realtime
        .initialize(PlaybackRealtime::new(resources, sample_rate))
        .map_err(|_| {
            "internal multichannel output callback initialization was duplicated".to_string()
        })?;
    stream
        .start()
        .map_err(|error| format!("start failed: {error}"))?;
    Ok(stream)
}

fn open_playback_candidate(
    context: &cubeb::Context,
    selected: &SelectedDevice,
    prefs: StreamPrefs,
    candidate: CubebStreamCandidate,
    resources: PlaybackCallbackResources,
    errored: Arc<AtomicBool>,
) -> Result<(CubebPlaybackStream, u32), String> {
    let layout = cubeb_output_channel_layout(candidate.channels).ok_or_else(|| {
        format!(
            "unsupported Cubeb output channel count: {}",
            candidate.channels
        )
    })?;
    let params = StreamParamsBuilder::new()
        .format(candidate.sample.format())
        .rate(candidate.rate)
        .channels(u32::from(candidate.channels))
        .layout(layout)
        .prefs(prefs)
        .take();
    let latency_frames = cubeb_latency_frames(context, params.as_ref());
    let stream = match (candidate.sample, candidate.channels) {
        (CubebSampleKind::Float32, 2) => open_stereo_playback_stream::<f32>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::StereoF32),
        (CubebSampleKind::Float32, 1) => open_mono_playback_stream::<f32>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::MonoF32),
        (CubebSampleKind::Float32, 4) => open_multichannel_playback_stream::<f32, 4>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::QuadF32),
        (CubebSampleKind::Float32, 6) => open_multichannel_playback_stream::<f32, 6>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::Surround51F32),
        (CubebSampleKind::Float32, 8) => open_multichannel_playback_stream::<f32, 8>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::Surround71F32),
        (CubebSampleKind::Signed16, 2) => open_stereo_playback_stream::<i16>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::StereoI16),
        (CubebSampleKind::Signed16, 1) => open_mono_playback_stream::<i16>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::MonoI16),
        (CubebSampleKind::Signed16, 4) => open_multichannel_playback_stream::<i16, 4>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::QuadI16),
        (CubebSampleKind::Signed16, 6) => open_multichannel_playback_stream::<i16, 6>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::Surround51I16),
        (CubebSampleKind::Signed16, 8) => open_multichannel_playback_stream::<i16, 8>(
            context,
            params.as_ref(),
            selected,
            latency_frames,
            candidate.rate,
            resources,
            errored,
        )
        .map(CubebPlaybackStream::Surround71I16),
        _ => Err("unsupported Cubeb playback candidate".to_string()),
    }?;
    Ok((stream, latency_frames))
}

#[allow(clippy::too_many_arguments)]
pub fn spawn_cubeb_playback(
    device_id: Option<String>,
    playback: Arc<Mutex<PlaybackRing>>,
    monitor_playback: Arc<Mutex<PlaybackRing>>,
    monitor_state: Arc<MicrophoneMonitorState>,
    stop: Arc<AtomicBool>,
    counters: Arc<NativeCounters>,
    progress: Arc<PlaybackProgress>,
    aec_timing: Arc<AecTiming>,
    diagnostics: Arc<PlaybackDiagnostics>,
    stream_generation: u64,
    media_events: SyncSender<MediaStateEvent>,
) -> Result<(), String> {
    #[cfg(windows)]
    let _com = WindowsComApartment::initialize()
        .map_err(|error| format!("initialize COM for Cubeb output: {error}"))?;
    let context = init_cubeb_context("PerfectComms speaker")
        .map_err(|error| format!("initialize Cubeb output context: {error}"))?;
    let explicit = device_id.as_deref().is_some_and(|id| !id.is_empty());
    let devices = if explicit {
        Some(
            context
                .enumerate_devices(DeviceType::OUTPUT)
                .map_err(|error| format!("enumerate output devices: {error}"))?,
        )
    } else {
        None
    };
    let preferred_rate = context.preferred_sample_rate().unwrap_or(SAMPLE_RATE);
    let selected = pick_device(
        devices.as_ref(),
        &device_id,
        preferred_rate,
        "output",
        DeviceType::OUTPUT,
        context.backend_id_bytes(),
    )?;
    let prefs = if explicit {
        StreamPrefs::DISABLE_DEVICE_SWITCHING
    } else {
        StreamPrefs::NONE
    };
    // Take lock-free consumer handles once. The outer mutexes remain for control-path ABI
    // compatibility, but are never touched by a hardware callback.
    let ring = playback
        .lock()
        .map_err(|_| "playback ring lock poisoned".to_string())?
        .consumer();
    let monitor_ring = monitor_playback
        .lock()
        .map_err(|_| "monitor playback ring lock poisoned".to_string())?
        .consumer();
    let errored = Arc::new(AtomicBool::new(false));
    let device_change = Arc::new(CubebDeviceChangeSignal::default());
    let start_succeeded = Arc::new(AtomicBool::new(false));
    let backend_latency_frames = Arc::new(AtomicU32::new(UNKNOWN_CUBEB_LATENCY_FRAMES));
    let first_callback_ns = Arc::new(AtomicU64::new(0));
    let first_callback_frames = Arc::new(AtomicU64::new(0));
    let resources = PlaybackCallbackResources {
        ring,
        monitor_ring,
        monitor_state: monitor_state.clone(),
        counters: counters.clone(),
        progress: progress.clone(),
        aec_timing: aec_timing.clone(),
        diagnostics: diagnostics.clone(),
        device_change: device_change.clone(),
        start_succeeded: start_succeeded.clone(),
        backend_latency_frames: backend_latency_frames.clone(),
        first_callback_ns: first_callback_ns.clone(),
        first_callback_frames: first_callback_frames.clone(),
    };

    // Keep the app contract at stereo float/48 kHz while adapting the hardware side. Strict ALSA
    // devices receive native S16NE/preferred-rate attempts and, only after every stereo/mono
    // attempt, output-only physical-width fallbacks on the same selected endpoint.
    let mut failures = Vec::new();
    let mut opened = None;
    for candidate in
        cubeb_playback_stream_candidates(context.backend_id_bytes(), selected.preferred_rate)
    {
        device_change.clear_pending();
        errored.store(false, Ordering::Release);
        match open_playback_candidate(
            &context,
            &selected,
            prefs,
            candidate,
            resources.clone(),
            errored.clone(),
        ) {
            Ok((stream, latency_frames)) => {
                opened = Some((stream, candidate, latency_frames));
                break;
            }
            Err(error) => failures.push(format!(
                "{}/{}/{}ch: {error}",
                candidate.sample.label(),
                candidate.rate,
                candidate.channels
            )),
        }
    }
    let (stream, candidate, latency_frames) =
        opened.ok_or_else(|| format!("build output stream: {}", failures.join("; ")))?;
    let descriptor = StreamDescriptor {
        requested_device: selected.requested_device.clone(),
        resolved_device: selected.resolved_device.clone(),
        requested_default: selected.requested_default,
        requested_matched: selected.requested_matched,
        fell_back_to_default: selected.fell_back_to_default,
        sample_rate: candidate.rate,
        channels: candidate.channels,
        sample_format: candidate.sample.label().to_string(),
        buffer_mode: cubeb_buffer_mode(&context),
        buffer_min_frames: latency_frames,
        buffer_max_frames: latency_frames,
    };
    // Keep `devices` alive until both possible init attempts have resolved the endpoint handle.
    store_cubeb_latency(&backend_latency_frames, stream.latency());
    match device_change.take_action(selected.requested_default) {
        CubebDeviceChangeAction::ResetDefaultStream => {
            aec_timing.reset_playback_path();
            backend_latency_frames.store(UNKNOWN_CUBEB_LATENCY_FRAMES, Ordering::Release);
            store_cubeb_latency(&backend_latency_frames, stream.latency());
        }
        CubebDeviceChangeAction::ReopenExplicitStream => {
            let _ = stream.stop();
            return Err(
                "selected output device changed; CoreAudio callback requires stream reopen"
                    .to_string(),
            );
        }
        CubebDeviceChangeAction::None => {}
    }
    if errored.load(Ordering::Acquire) {
        let _ = stream.stop();
        return Err("output device failed while starting".to_string());
    }
    let started_ns = monotonic_ns();
    diagnostics.mark_stream_started(descriptor.clone());
    start_succeeded.store(true, Ordering::Release);
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
    let mut last_latency_refresh = Instant::now();
    while !stop.load(Ordering::Relaxed) {
        match device_change.take_action(selected.requested_default) {
            CubebDeviceChangeAction::ResetDefaultStream => {
                aec_timing.reset_playback_path();
                backend_latency_frames.store(UNKNOWN_CUBEB_LATENCY_FRAMES, Ordering::Release);
                store_cubeb_latency(&backend_latency_frames, stream.latency());
                last_latency_refresh = Instant::now();
            }
            CubebDeviceChangeAction::ReopenExplicitStream => {
                start_succeeded.store(false, Ordering::Release);
                let _ = stream.stop();
                return Err(
                    "selected output device changed; CoreAudio callback requires stream reopen"
                        .to_string(),
                );
            }
            CubebDeviceChangeAction::None => {}
        }
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
        if last_latency_refresh.elapsed() >= CUBEB_LATENCY_REFRESH {
            store_cubeb_latency(&backend_latency_frames, stream.latency());
            last_latency_refresh = Instant::now();
        }
        if errored.load(Ordering::Acquire) {
            start_succeeded.store(false, Ordering::Release);
            let _ = stream.stop();
            counters
                .playback_callback_errors
                .fetch_add(1, Ordering::Relaxed);
            return Err("output device stream failed".to_string());
        }
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
    start_succeeded.store(false, Ordering::Release);
    let _ = stream.stop();
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
    use std::cell::Cell;
    use std::sync::atomic::AtomicUsize;

    fn stereo_test_pairs(count: usize) -> Vec<f32> {
        let mut samples = Vec::with_capacity(count * 2);
        for index in 0..count {
            let sample = (index + 1) as f32 / count.max(1) as f32;
            samples.push(sample);
            samples.push(sample);
        }
        samples
    }

    #[cfg(windows)]
    #[test]
    fn windows_com_initialization_balances_only_successful_references() {
        assert_eq!(com_initialization_needs_uninitialize(COM_S_OK), Ok(true));
        assert_eq!(com_initialization_needs_uninitialize(COM_S_FALSE), Ok(true));
        assert_eq!(
            com_initialization_needs_uninitialize(COM_RPC_E_CHANGED_MODE),
            Ok(false)
        );
        assert_eq!(
            com_initialization_needs_uninitialize(0x8000_4005_u32 as i32),
            Err(0x8000_4005_u32 as i32)
        );
    }

    #[test]
    fn monitor_gain_only_update_preserves_playout_generation() {
        let state = MicrophoneMonitorState::default();
        let resets = Cell::new(0);
        let reset = || resets.set(resets.get() + 1);

        assert_eq!(
            state.configure(true, 48_000, 1.0, reset),
            MicrophoneMonitorConfigChange::Playout
        );
        let generation = state.playout_generation();
        assert_eq!(resets.get(), 1);

        assert_eq!(
            state.configure(true, 48_000, 0.5, reset),
            MicrophoneMonitorConfigChange::GainOnly
        );
        assert_eq!(state.playout_generation(), generation);
        assert_eq!(state.gain(), 0.5);
        assert_eq!(resets.get(), 1);

        assert_eq!(
            state.configure(true, 48_000, 0.5, reset),
            MicrophoneMonitorConfigChange::Unchanged
        );
        assert_eq!(state.playout_generation(), generation);
        assert_eq!(resets.get(), 1);

        assert_eq!(
            state.configure(true, 24_000, 0.5, reset),
            MicrophoneMonitorConfigChange::Playout
        );
        assert!(state.playout_generation() > generation);
        assert_eq!(resets.get(), 2);
    }

    #[test]
    fn monitor_delayed_playout_primes_then_drains_without_retaining_a_tail() {
        let state = MicrophoneMonitorState::default();
        let mut ring = PlaybackRing::new(16);
        state.configure(true, 4, 1.0, || ring.discard_all());
        let consumer = ring.consumer();
        let mut playout = MonitorPlayout::default();

        ring.push(&stereo_test_pairs(3));
        let _ = playout.render(&consumer, &state, 1.0);
        assert!(!playout.primed);
        assert_eq!(consumer.len(), 3);

        ring.push(&stereo_test_pairs(1));
        let _ = playout.render(&consumer, &state, 1.0);
        assert!(playout.primed);

        for _ in 0..16 {
            let _ = playout.render(&consumer, &state, 1.0);
        }
        assert_eq!(consumer.len(), 0);
        assert!(!playout.primed);
    }

    #[test]
    fn monitor_generation_reset_discards_and_reprimes_the_timeline() {
        let state = MicrophoneMonitorState::default();
        let mut ring = PlaybackRing::new(16);
        state.configure(true, 4, 1.0, || ring.discard_all());
        let consumer = ring.consumer();
        let mut playout = MonitorPlayout::default();

        ring.push(&stereo_test_pairs(4));
        let _ = playout.render(&consumer, &state, 1.0);
        assert!(playout.primed);
        let generation = state.playout_generation();

        state.reset_playout(|| ring.discard_all());
        assert!(state.playout_generation() > generation);
        ring.push(&stereo_test_pairs(3));
        let _ = playout.render(&consumer, &state, 1.0);
        assert!(!playout.primed);
        assert_eq!(consumer.len(), 3);

        ring.push(&stereo_test_pairs(1));
        let _ = playout.render(&consumer, &state, 1.0);
        assert!(playout.primed);
    }

    #[test]
    fn monitor_clock_depth_correction_is_bounded_and_bidirectional() {
        let nominal = 1.0;
        let target = 48_000;
        assert_eq!(monitor_effective_ratio(nominal, target, target), nominal);

        let low = monitor_effective_ratio(nominal, 0, target);
        let high = monitor_effective_ratio(nominal, target * 2, target);
        assert!(low < nominal);
        assert!(high > nominal);
        assert!((low - (1.0 - MONITOR_MAX_RATE_ADJUSTMENT)).abs() < f64::EPSILON);
        assert!((high - (1.0 + MONITOR_MAX_RATE_ADJUSTMENT)).abs() < f64::EPSILON);
    }

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
    fn cubeb_device_ids_are_binary_safe_and_backend_scoped() {
        assert_eq!(
            encode_device_id(b"wasapi", b"headphones"),
            "cubeb-v2:776173617069:6865616470686f6e6573"
        );
        assert_eq!(
            encode_device_id(b"alsa", &[0, 0xff, b':']),
            "cubeb-v2:616c7361:00ff3a"
        );
        assert_ne!(
            encode_device_id(b"pulse", b"same"),
            encode_device_id(b"alsa", b"same")
        );
        assert_eq!(
            encode_device_id(b"audiounit", b"same"),
            encode_device_id(b"audiounit-rust", b"same")
        );
        assert_ne!(
            encode_device_id(b"wasapi", b"ab"),
            encode_device_id(b"wasapi", b"a:b")
        );
    }

    #[test]
    fn device_change_callbacks_are_only_registered_for_coreaudio_backends() {
        assert!(cubeb_backend_supports_device_change_callback(b"audiounit"));
        assert!(cubeb_backend_supports_device_change_callback(
            b"audiounit-rust"
        ));
        assert!(cubeb_backend_supports_device_change_callback(
            b"  AudioUnit-Rust  "
        ));
        assert!(!cubeb_backend_supports_device_change_callback(b"wasapi"));
        assert!(!cubeb_backend_supports_device_change_callback(b"pulse"));
        assert!(!cubeb_backend_supports_device_change_callback(b"alsa"));
    }

    #[test]
    fn device_change_signal_resets_defaults_and_reopens_explicit_streams() {
        let signal = CubebDeviceChangeSignal::default();
        let mut observed_epoch = signal.epoch();

        assert_eq!(signal.take_action(true), CubebDeviceChangeAction::None);
        assert!(!signal.update_observed_epoch(&mut observed_epoch));

        signal.notify();
        assert!(signal.update_observed_epoch(&mut observed_epoch));
        assert!(!signal.update_observed_epoch(&mut observed_epoch));
        assert_eq!(
            signal.take_action(true),
            CubebDeviceChangeAction::ResetDefaultStream
        );
        assert_eq!(signal.take_action(true), CubebDeviceChangeAction::None);

        signal.notify();
        signal.notify();
        assert!(signal.update_observed_epoch(&mut observed_epoch));
        assert_eq!(
            signal.take_action(false),
            CubebDeviceChangeAction::ReopenExplicitStream
        );
        assert_eq!(signal.take_action(false), CubebDeviceChangeAction::None);

        signal.notify();
        signal.clear_pending();
        assert!(signal.update_observed_epoch(&mut observed_epoch));
        assert_eq!(signal.take_action(true), CubebDeviceChangeAction::None);
    }

    #[test]
    fn cubeb_device_match_accepts_v1_and_exact_compatible_legacy_ids() {
        let raw = b"endpoint:with:colons";
        let v2 = encode_device_id(b"wasapi", raw);
        let mut v1 = CUBEB_DEVICE_ID_V1_PREFIX.to_string();
        append_hex(&mut v1, raw);
        assert!(requested_device_matches(&v2, b"wasapi", raw));
        assert!(requested_device_matches(&v1, b"wasapi", raw));
        assert!(requested_device_matches(
            "wasapi:endpoint:with:colons",
            b"wasapi",
            raw
        ));
        assert!(!requested_device_matches(&v2, b"winmm", raw));
        assert!(!requested_device_matches(
            "wasapi:endpoint:with:colons",
            b"winmm",
            raw
        ));
        assert!(!requested_device_matches(
            "pipewire:endpoint:with:colons",
            b"pulse",
            raw
        ));
        assert!(!requested_device_matches(
            "wasapi:endpoint:with:colons",
            b"wasapi",
            b"different"
        ));
        assert!(!requested_device_matches(
            "cubeb-v2:zz:00",
            b"wasapi",
            b"\0"
        ));
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn alsa_hint_ioid_direction_filter_is_fail_closed() {
        assert_eq!(alsa_hint_directions(None), (true, true));
        assert_eq!(alsa_hint_directions(Some(b"Input")), (true, false));
        assert_eq!(alsa_hint_directions(Some(b"OUTPUT")), (false, true));
        assert_eq!(alsa_hint_directions(Some(b"unknown")), (false, false));
    }

    #[test]
    fn cubeb_default_marker_tracks_multimedia_not_voice_preference() {
        assert!(!cubeb_preference_is_default(
            cubeb::ffi::CUBEB_DEVICE_PREF_NONE
        ));
        assert!(!cubeb_preference_is_default(
            cubeb::ffi::CUBEB_DEVICE_PREF_VOICE
        ));
        assert!(cubeb_preference_is_default(
            cubeb::ffi::CUBEB_DEVICE_PREF_MULTIMEDIA
        ));
        assert!(cubeb_preference_is_default(
            cubeb::ffi::CUBEB_DEVICE_PREF_ALL
        ));
    }

    #[test]
    fn cubeb_backend_override_is_linux_only_and_allowlisted() {
        assert_eq!(allowed_cubeb_backend(Some("pulse"), true), Some("pulse"));
        assert_eq!(allowed_cubeb_backend(Some("alsa"), true), Some("alsa"));
        assert_eq!(allowed_cubeb_backend(Some("jack"), true), None);
        assert_eq!(allowed_cubeb_backend(Some("pulse"), false), None);
    }

    #[test]
    fn only_wasapi_enables_capture_preroll_filter() {
        assert!(cubeb_backend_injects_wasapi_preroll(b"wasapi"));
        assert!(cubeb_backend_injects_wasapi_preroll(b"WASAPI"));
        assert!(!cubeb_backend_injects_wasapi_preroll(b"winmm"));
        assert!(!cubeb_backend_injects_wasapi_preroll(b"pulse"));
        assert!(!cubeb_backend_injects_wasapi_preroll(b"alsa"));
        assert!(!cubeb_backend_injects_wasapi_preroll(b"audiounit"));
    }

    #[test]
    fn wasapi_preroll_filter_preserves_real_tail_and_rearms_implicitly() {
        let mut filter = WasapiPrerollFilter::new(true, 48_000, 480);
        let mut first = vec![0.0f32; 8_160];
        first[7_680..].fill(0.25);
        let decision = filter.classify(first.len(), |index| first[index] == 0.0);
        assert_eq!(
            decision,
            PrerollDecision {
                raw_frames: 8_160,
                skip_frames: 7_680,
                admitted_frames: 480,
                matched: true,
            }
        );
        assert!(first[decision.skip_frames..]
            .iter()
            .all(|sample| *sample == 0.25));

        let normal = vec![0.5f32; 480];
        assert_eq!(
            filter.classify(normal.len(), |index| normal[index] == 0.0),
            PrerollDecision::unchanged(480)
        );

        let mut reconfigured = vec![0.0f32; 8_640];
        reconfigured[7_680..].fill(-0.5);
        let second = filter.classify(reconfigured.len(), |index| reconfigured[index] == 0.0);
        assert!(second.matched);
        assert_eq!(second.skip_frames, 7_680);
        assert_eq!(second.admitted_frames, 960);
    }

    #[test]
    fn wasapi_preroll_filter_keeps_one_quantum_of_all_zero_input() {
        let mut filter = WasapiPrerollFilter::new(true, 48_000, 480);
        let silence = vec![0.0f32; 8_160];
        let decision = filter.classify(silence.len(), |index| silence[index] == 0.0);
        assert!(decision.matched);
        assert_eq!(decision.skip_frames, 7_680);
        assert_eq!(decision.admitted_frames, 480);
    }

    #[test]
    fn preroll_filter_preserves_normal_silence_large_nonzero_and_other_backends() {
        let normal_silence = vec![0.0f32; 960];
        let mut wasapi = WasapiPrerollFilter::new(true, 48_000, 480);
        assert_eq!(
            wasapi.classify(normal_silence.len(), |index| normal_silence[index] == 0.0),
            PrerollDecision::unchanged(960)
        );

        let large_nonzero = vec![0.25f32; 8_160];
        assert_eq!(
            wasapi.classify(large_nonzero.len(), |index| large_nonzero[index] == 0.0),
            PrerollDecision::unchanged(8_160)
        );

        let large_silence = vec![0.0f32; 8_160];
        let mut pulse = WasapiPrerollFilter::new(false, 48_000, 480);
        assert_eq!(
            pulse.classify(large_silence.len(), |index| large_silence[index] == 0.0),
            PrerollDecision::unchanged(8_160)
        );
    }

    #[test]
    fn cubeb_candidates_keep_float_48k_first_then_try_native_s16() {
        let candidates = cubeb_stream_candidates(b"alsa", 44_100);
        assert_eq!(
            &candidates[..4],
            &[
                CubebStreamCandidate {
                    sample: CubebSampleKind::Float32,
                    rate: SAMPLE_RATE,
                    channels: 2,
                },
                CubebStreamCandidate {
                    sample: CubebSampleKind::Float32,
                    rate: SAMPLE_RATE,
                    channels: 1,
                },
                CubebStreamCandidate {
                    sample: CubebSampleKind::Signed16,
                    rate: 44_100,
                    channels: 2,
                },
                CubebStreamCandidate {
                    sample: CubebSampleKind::Signed16,
                    rate: 44_100,
                    channels: 1,
                },
            ]
        );
        assert!(candidates.contains(&CubebStreamCandidate {
            sample: CubebSampleKind::Signed16,
            rate: SAMPLE_RATE,
            channels: 2,
        }));
        assert!(candidates.contains(&CubebStreamCandidate {
            sample: CubebSampleKind::Float32,
            rate: 44_100,
            channels: 1,
        }));
        assert_eq!(cubeb_stream_candidates(b"winmm", 44_100), candidates);
        assert_eq!(cubeb_stream_candidates(b"wasapi", 44_100), candidates);
    }

    #[test]
    fn strict_alsa_playback_appends_multichannel_candidates_only_after_stereo_and_mono() {
        let base_candidates = cubeb_stream_candidates(b"alsa", 44_100);
        let playback_candidates = cubeb_playback_stream_candidates(b"alsa", 44_100);
        assert_eq!(
            &playback_candidates[..base_candidates.len()],
            base_candidates.as_slice()
        );
        assert!(base_candidates
            .iter()
            .all(|candidate| candidate.channels <= 2));
        assert!(playback_candidates[base_candidates.len()..]
            .iter()
            .all(|candidate| matches!(candidate.channels, 4 | 6 | 8)));
        for sample in [CubebSampleKind::Float32, CubebSampleKind::Signed16] {
            for channels in [4, 6, 8] {
                assert!(playback_candidates.contains(&CubebStreamCandidate {
                    sample,
                    rate: SAMPLE_RATE,
                    channels,
                }));
            }
        }
        assert_eq!(
            cubeb_playback_stream_candidates(b"wasapi", 44_100),
            cubeb_stream_candidates(b"wasapi", 44_100)
        );
        assert_eq!(
            cubeb_playback_stream_candidates(b"pulse", 44_100),
            cubeb_stream_candidates(b"pulse", 44_100)
        );
    }

    #[test]
    fn capture_appends_multichannel_candidates_only_after_stereo_and_mono() {
        for backend in [
            b"wasapi".as_slice(),
            b"winmm".as_slice(),
            b"pulse".as_slice(),
            b"alsa".as_slice(),
            b"audiounit".as_slice(),
        ] {
            let base_candidates = cubeb_stream_candidates(backend, 44_100);
            let capture_candidates = cubeb_capture_stream_candidates(backend, 44_100);
            assert_eq!(
                &capture_candidates[..base_candidates.len()],
                base_candidates.as_slice()
            );
            assert!(base_candidates
                .iter()
                .all(|candidate| candidate.channels <= 2));
            assert!(capture_candidates[base_candidates.len()..]
                .iter()
                .all(|candidate| matches!(candidate.channels, 4 | 6 | 8)));
            for sample in [CubebSampleKind::Float32, CubebSampleKind::Signed16] {
                for channels in [4, 6, 8] {
                    assert!(capture_candidates.contains(&CubebStreamCandidate {
                        sample,
                        rate: SAMPLE_RATE,
                        channels,
                    }));
                }
            }
        }
    }

    #[test]
    fn cubeb_multichannel_layouts_and_stereo_mapping_match_speaker_order() {
        assert_eq!(cubeb_output_channel_layout(1), Some(ChannelLayout::MONO));
        assert_eq!(cubeb_output_channel_layout(2), Some(ChannelLayout::STEREO));
        assert_eq!(cubeb_output_channel_layout(4), Some(ChannelLayout::QUAD));
        assert_eq!(
            cubeb_output_channel_layout(6),
            Some(ChannelLayout::_3F2_LFE)
        );
        assert_eq!(
            cubeb_output_channel_layout(8),
            Some(ChannelLayout::_3F4_LFE)
        );
        assert_eq!(cubeb_output_channel_layout(3), None);

        assert_eq!(
            map_stereo_to_multichannel::<4>(0.8, -0.4),
            [0.8, -0.4, 0.4, -0.2]
        );
        assert_eq!(
            map_stereo_to_multichannel::<6>(0.8, -0.4),
            [0.8, -0.4, 0.2, 0.0, 0.4, -0.2]
        );
        assert_eq!(
            map_stereo_to_multichannel::<8>(0.8, -0.4),
            [0.8, -0.4, 0.2, 0.0, 0.4, -0.2, 0.4, -0.2]
        );
        assert_eq!(
            std::mem::size_of::<MultichannelFrame<i16, 8>>(),
            std::mem::size_of::<i16>() * 8
        );
    }

    #[test]
    fn signed16_conversion_covers_full_scale_and_non_finite_output() {
        assert_eq!(<i16 as CubebCaptureSample>::to_float(i16::MIN), -1.0);
        assert!(
            (<i16 as CubebCaptureSample>::to_float(i16::MAX) - (32_767.0 / 32_768.0)).abs()
                < f32::EPSILON
        );
        assert_eq!(<i16 as CubebPlaybackSample>::from_float(-1.0), i16::MIN);
        assert_eq!(<i16 as CubebPlaybackSample>::from_float(1.0), i16::MAX);
        assert_eq!(<i16 as CubebPlaybackSample>::from_float(f32::NAN), 0);
    }

    #[test]
    fn realtime_callback_state_initializes_once_and_mutates_after_publish() {
        let state = RealtimeCallbackState::new();
        assert_eq!(state.with_callback_mut(|value: &mut u32| *value), None);
        assert_eq!(state.initialize(7), Ok(()));
        assert_eq!(state.with_callback_mut(|value| *value += 5), Some(()));
        assert_eq!(state.with_callback_mut(|value| *value), Some(12));
        assert_eq!(state.initialize(99), Err(99));
    }

    #[test]
    fn realtime_callback_state_drops_contained_value_exactly_once() {
        struct DropSignal(Arc<AtomicUsize>);
        impl Drop for DropSignal {
            fn drop(&mut self) {
                self.0.fetch_add(1, Ordering::Relaxed);
            }
        }

        let drops = Arc::new(AtomicUsize::new(0));
        {
            let state = RealtimeCallbackState::new();
            assert!(state.initialize(DropSignal(drops.clone())).is_ok());
        }
        assert_eq!(drops.load(Ordering::Relaxed), 1);
    }

    #[test]
    fn transient_cubeb_latency_error_preserves_last_good_value() {
        let latency = AtomicU32::new(240);
        store_cubeb_latency(&latency, Err(cubeb::Error::Error));
        assert_eq!(load_cubeb_latency(&latency), Some(240));
        store_cubeb_latency(&latency, Ok(0));
        assert_eq!(load_cubeb_latency(&latency), Some(240));
        store_cubeb_latency(&latency, Ok(480));
        assert_eq!(load_cubeb_latency(&latency), Some(480));
        store_cubeb_latency(&latency, Ok(CUBEB_MAX_LATENCY_FRAMES + 1));
        assert_eq!(load_cubeb_latency(&latency), Some(CUBEB_MAX_LATENCY_FRAMES));
    }

    #[test]
    fn realtime_capture_pipeline_retains_preallocated_capacities() {
        for rate in [MIN_REALTIME_CAPTURE_RATE, SAMPLE_RATE, 384_000] {
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
    fn cubeb_capture_clock_mapper_smooths_normal_callback_jitter() {
        let mut mapper = CaptureClockMapper::default();
        let first = mapper.observe(2_008_000_000, Some(384), 480, 48_000);
        assert_eq!(first.first_sample_mono_ns, 2_000_000_000);

        let jittered = mapper.observe(2_018_500_000, Some(384), 480, 48_000);
        assert_eq!(jittered.first_sample_mono_ns, 2_010_000_000);
        assert_eq!(jittered.bridge_residual_ns, Some(500_000));
        assert!(jittered.frame_timing_valid);
        assert!(!jittered.discontinuity);
        assert_eq!(jittered.clock_status, CaptureClockStatus::Continuous);
    }

    #[test]
    fn cubeb_capture_clock_mapper_reanchors_and_flags_a_large_gap() {
        let mut mapper = CaptureClockMapper::default();
        mapper.observe(3_008_000_000, Some(384), 480, 48_000);

        let gap = mapper.observe(3_038_000_000, Some(384), 480, 48_000);
        assert_eq!(gap.first_sample_mono_ns, 3_030_000_000);
        assert_eq!(gap.capture_clock_delta_ns, Some(30_000_000));
        assert_eq!(gap.capture_clock_delta_error_ns, Some(20_000_000));
        assert!(!gap.frame_timing_valid);
        assert!(gap.discontinuity);
        assert_eq!(gap.clock_status, CaptureClockStatus::DeltaMismatch);

        let recovered = mapper.observe(3_048_000_000, Some(384), 480, 48_000);
        assert_eq!(recovered.first_sample_mono_ns, 3_040_000_000);
        assert!(recovered.frame_timing_valid);
        assert!(!recovered.discontinuity);
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
    fn underrun_fader_durations_scale_with_hardware_rate() {
        let at_48k = UnderrunFader::for_sample_rate(48_000);
        let at_96k = UnderrunFader::for_sample_rate(96_000);
        assert_eq!(at_48k.fade_samples, UNDERRUN_FADE_SAMPLES);
        assert_eq!(at_48k.resume_samples, UNDERRUN_RESUME_SAMPLES);
        assert_eq!(at_96k.fade_samples, UNDERRUN_FADE_SAMPLES * 2);
        assert_eq!(at_96k.resume_samples, UNDERRUN_RESUME_SAMPLES * 2);
    }

    #[test]
    fn cubeb_primary_stream_contract_is_native_float_48k_stereo() {
        let params = StreamParamsBuilder::new()
            .format(SampleFormat::Float32NE)
            .rate(SAMPLE_RATE)
            .channels(2)
            .layout(ChannelLayout::STEREO)
            .prefs(StreamPrefs::NONE)
            .take();
        assert!(matches!(
            params.format(),
            SampleFormat::Float32LE | SampleFormat::Float32BE
        ));
        assert_eq!(params.rate(), SAMPLE_RATE);
        assert_eq!(params.channels(), 2);
        assert_eq!(params.layout(), ChannelLayout::STEREO);
        assert_eq!(params.prefs(), StreamPrefs::NONE);
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
