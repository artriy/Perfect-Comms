use serde::Serialize;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU64, Ordering};
use std::sync::mpsc::SyncSender;
use std::sync::{Arc, Mutex};

pub const MEDIA_DIAGNOSTICS_SCHEMA: u32 = 1;
const UNKNOWN_MIN: u64 = u64::MAX;
const LATE_CALLBACK_GRACE_NS: u64 = 5_000_000;
const NEAR_CLIP_LEVEL: f32 = 0.98;
const SILENCE_LEVEL: f32 = 0.000_001;
static MEDIA_STATE_EVENTS_DROPPED: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
#[repr(u64)]
pub enum CaptureClockStatus {
    #[default]
    Unavailable = 0,
    Anchored = 1,
    Continuous = 2,
    Recovered = 3,
    DeltaMismatch = 4,
    BackendClockReversed = 5,
    InvalidLatency = 6,
    AnchorUnderflow = 7,
    MappingOverflow = 8,
}

impl CaptureClockStatus {
    fn from_code(code: u64) -> Self {
        match code {
            1 => Self::Anchored,
            2 => Self::Continuous,
            3 => Self::Recovered,
            4 => Self::DeltaMismatch,
            5 => Self::BackendClockReversed,
            6 => Self::InvalidLatency,
            7 => Self::AnchorUnderflow,
            8 => Self::MappingOverflow,
            _ => Self::Unavailable,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Unavailable => "unavailable",
            Self::Anchored => "anchor-established",
            Self::Continuous => "continuous",
            Self::Recovered => "bridge-recovered",
            Self::DeltaMismatch => "backend-capture-delta-mismatch",
            Self::BackendClockReversed => "backend-capture-clock-reversed",
            Self::InvalidLatency => "invalid-capture-callback-latency",
            Self::AnchorUnderflow => "clock-bridge-anchor-underflow",
            Self::MappingOverflow => "clock-bridge-mapping-overflow",
        }
    }

    fn is_discontinuity(self) -> bool {
        matches!(
            self,
            Self::DeltaMismatch | Self::BackendClockReversed | Self::MappingOverflow
        )
    }
}

pub fn send_media_state(sender: &SyncSender<MediaStateEvent>, event: MediaStateEvent) {
    if sender.try_send(event).is_err() {
        MEDIA_STATE_EVENTS_DROPPED.fetch_add(1, Ordering::Relaxed);
    }
}

fn duration_ms(now_ns: u64, then_ns: u64) -> u64 {
    if then_ns == 0 || now_ns < then_ns {
        0
    } else {
        now_ns.saturating_sub(then_ns) / 1_000_000
    }
}

fn observe_max(target: &AtomicU64, value: u64) {
    let mut current = target.load(Ordering::Relaxed);
    while value > current {
        match target.compare_exchange_weak(current, value, Ordering::Relaxed, Ordering::Relaxed) {
            Ok(_) => return,
            Err(actual) => current = actual,
        }
    }
}

fn observe_min(target: &AtomicU64, value: u64) {
    let mut current = target.load(Ordering::Relaxed);
    while value < current {
        match target.compare_exchange_weak(current, value, Ordering::Relaxed, Ordering::Relaxed) {
            Ok(_) => return,
            Err(actual) => current = actual,
        }
    }
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct SignalWindowSnapshot {
    pub samples: u64,
    pub dropped_records: u64,
    pub nonfinite_samples: u64,
    pub near_clip_samples: u64,
    pub hard_clip_samples: u64,
    pub silent_frames: u64,
    pub peak: f32,
    pub rms: f64,
    pub dc: f64,
}

#[derive(Default)]
struct SignalWindowAccumulator {
    samples: u64,
    nonfinite_samples: u64,
    near_clip_samples: u64,
    hard_clip_samples: u64,
    silent_frames: u64,
    peak: f32,
    square_sum: f64,
    sum: f64,
}

#[derive(Default)]
pub struct SignalWindow {
    accumulator: Mutex<SignalWindowAccumulator>,
    dropped_records: AtomicU64,
}

impl SignalWindow {
    pub fn record(&self, samples: &[f32]) {
        let mut peak = 0.0f32;
        let mut square_sum = 0.0f64;
        let mut sum = 0.0f64;
        let mut nonfinite = 0u64;
        let mut near_clip = 0u64;
        let mut hard_clip = 0u64;
        for &sample in samples {
            if !sample.is_finite() {
                nonfinite += 1;
                continue;
            }
            let abs = sample.abs();
            peak = peak.max(abs);
            let sample64 = f64::from(sample);
            square_sum += sample64 * sample64;
            sum += sample64;
            if abs >= NEAR_CLIP_LEVEL {
                near_clip += 1;
            }
            if abs >= 1.0 {
                hard_clip += 1;
            }
        }
        // Never make a realtime media callback wait behind the telemetry thread. A skipped
        // diagnostics record is surfaced explicitly and is preferable to distorting audio.
        let Ok(mut accumulator) = self.accumulator.try_lock() else {
            self.dropped_records.fetch_add(1, Ordering::Relaxed);
            return;
        };
        accumulator.samples = accumulator.samples.saturating_add(samples.len() as u64);
        accumulator.nonfinite_samples = accumulator.nonfinite_samples.saturating_add(nonfinite);
        accumulator.near_clip_samples = accumulator.near_clip_samples.saturating_add(near_clip);
        accumulator.hard_clip_samples = accumulator.hard_clip_samples.saturating_add(hard_clip);
        if peak <= SILENCE_LEVEL {
            accumulator.silent_frames = accumulator.silent_frames.saturating_add(1);
        }
        accumulator.peak = accumulator.peak.max(peak);
        accumulator.square_sum += square_sum;
        accumulator.sum += sum;
    }

    pub fn reset(&self) {
        *self.accumulator.lock().unwrap() = SignalWindowAccumulator::default();
        self.dropped_records.store(0, Ordering::Relaxed);
    }

    pub fn take(&self) -> SignalWindowSnapshot {
        let accumulator = std::mem::take(&mut *self.accumulator.lock().unwrap());
        SignalWindowSnapshot {
            samples: accumulator.samples,
            dropped_records: self.dropped_records.swap(0, Ordering::Relaxed),
            nonfinite_samples: accumulator.nonfinite_samples,
            near_clip_samples: accumulator.near_clip_samples,
            hard_clip_samples: accumulator.hard_clip_samples,
            silent_frames: accumulator.silent_frames,
            peak: accumulator.peak,
            rms: if accumulator.samples == 0 {
                0.0
            } else {
                (accumulator.square_sum / accumulator.samples as f64).sqrt()
            },
            dc: if accumulator.samples == 0 {
                0.0
            } else {
                accumulator.sum / accumulator.samples as f64
            },
        }
    }
}

#[derive(Debug, Clone, Default)]
pub struct StreamDescriptor {
    pub requested_device: String,
    pub resolved_device: String,
    pub requested_default: bool,
    pub requested_matched: bool,
    pub fell_back_to_default: bool,
    pub sample_rate: u32,
    pub channels: u16,
    pub sample_format: String,
    pub buffer_mode: String,
    pub buffer_min_frames: u32,
    pub buffer_max_frames: u32,
}

#[derive(Debug, Clone)]
struct CaptureMetadata {
    stream_generation: u64,
    state: String,
    running: bool,
    healthy: bool,
    generation_started_ns: u64,
    stream_started_ns: u64,
    synthetic: bool,
    requested_device: String,
    resolved_device: String,
    requested_default: bool,
    requested_matched: bool,
    fell_back_to_default: bool,
    sample_rate: u32,
    channels: u16,
    sample_format: String,
    buffer_mode: String,
    buffer_min_frames: u32,
    buffer_max_frames: u32,
}

impl Default for CaptureMetadata {
    fn default() -> Self {
        Self {
            stream_generation: 0,
            state: "stopped".to_string(),
            running: false,
            healthy: false,
            generation_started_ns: 0,
            stream_started_ns: 0,
            synthetic: false,
            requested_device: String::new(),
            resolved_device: String::new(),
            requested_default: true,
            requested_matched: false,
            fell_back_to_default: false,
            sample_rate: 0,
            channels: 0,
            sample_format: String::new(),
            buffer_mode: String::new(),
            buffer_min_frames: 0,
            buffer_max_frames: 0,
        }
    }
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct CaptureDiagnosticsSnapshot {
    pub command_seq: u64,
    pub stream_generation: u64,
    pub state: String,
    pub running: bool,
    pub healthy: bool,
    pub synthetic: bool,
    pub requested_device: String,
    pub resolved_device: String,
    pub requested_default: bool,
    pub requested_matched: bool,
    pub fell_back_to_default: bool,
    pub sample_rate: u32,
    pub channels: u16,
    pub sample_format: String,
    pub buffer_mode: String,
    pub buffer_min_frames: u32,
    pub buffer_max_frames: u32,
    pub open_attempts: u64,
    pub stream_errors: u64,
    pub retry_attempt: u64,
    pub stream_started: bool,
    pub first_callback_seen: bool,
    pub callback_seen: bool,
    pub callback_window_seen: bool,
    pub callback_interval_seen: bool,
    pub callback_interval_window_seen: bool,
    pub start_to_open_ms: u64,
    pub open_to_first_callback_ms: u64,
    pub stream_age_ms: u64,
    pub callbacks_total: u64,
    pub callbacks_window: u64,
    pub callback_age_ms: u64,
    pub callback_frames_last: u64,
    pub callback_frames_min: u64,
    pub callback_frames_max: u64,
    pub callback_interval_last_us: u64,
    pub callback_interval_max_us: u64,
    pub late_callbacks: u64,
    pub input_samples_total: u64,
    pub resampled_samples_total: u64,
    pub frames_produced_total: u64,
    pub accumulator_pending_samples: u64,
    pub invalid_timestamps: u64,
    pub timestamp_discontinuities: u64,
    pub capture_clock_delta_seen: bool,
    pub capture_clock_delta_last_us: u64,
    pub capture_clock_expected_delta_us: u64,
    pub capture_clock_delta_error_us: i64,
    pub capture_clock_bridge_residual_seen: bool,
    pub capture_clock_bridge_residual_us: i64,
    pub capture_clock_status: String,
    pub last_timestamp_discontinuity_reason: String,
    pub ring_len: u64,
    pub ring_capacity: u64,
    pub ring_high_water: u64,
    pub ring_dropped: u64,
    pub ring_oldest_frame_age_ms: u64,
    pub ring_has_frames: bool,
    pub encoder_pop_age_last_ms: u64,
    pub encoder_pop_age_max_ms: u64,
    pub encoder_frame_seen: bool,
    pub encoder_window_seen: bool,
    pub stale_generation_frames: u64,
    pub raw_input: SignalWindowSnapshot,
    pub pre_dsp: SignalWindowSnapshot,
    pub post_dsp: SignalWindowSnapshot,
    pub post_gain: SignalWindowSnapshot,
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct CaptureWindowSummary {
    pub callbacks: u64,
    pub raw_input: SignalWindowSnapshot,
    pub pre_dsp: SignalWindowSnapshot,
    pub post_dsp: SignalWindowSnapshot,
    pub post_gain: SignalWindowSnapshot,
}

pub struct CaptureDiagnostics {
    metadata: Mutex<CaptureMetadata>,
    command_seq: AtomicU64,
    first_callback_ns: AtomicU64,
    last_callback_ns: AtomicU64,
    previous_callback_expected_ns: AtomicU64,
    open_attempts: AtomicU64,
    stream_errors: AtomicU64,
    retry_attempt: AtomicU64,
    callbacks_total: AtomicU64,
    callbacks_window: AtomicU64,
    callback_frames_last: AtomicU64,
    callback_frames_min: AtomicU64,
    callback_frames_max: AtomicU64,
    callback_interval_last_ns: AtomicU64,
    callback_interval_max_ns: AtomicU64,
    late_callbacks: AtomicU64,
    input_samples_total: AtomicU64,
    resampled_samples_total: AtomicU64,
    frames_produced_total: AtomicU64,
    accumulator_pending_samples: AtomicU64,
    invalid_timestamps: AtomicU64,
    timestamp_discontinuities: AtomicU64,
    capture_clock_delta_seen: AtomicBool,
    capture_clock_delta_last_ns: AtomicU64,
    capture_clock_expected_delta_ns: AtomicU64,
    capture_clock_delta_error_ns: AtomicI64,
    capture_clock_bridge_residual_seen: AtomicBool,
    capture_clock_bridge_residual_ns: AtomicI64,
    capture_clock_status: AtomicU64,
    last_timestamp_discontinuity_reason: AtomicU64,
    ring_high_water: AtomicU64,
    encoder_pop_age_last_ns: AtomicU64,
    encoder_pop_age_max_ns: AtomicU64,
    stale_generation_frames: AtomicU64,
    pub raw_input: SignalWindow,
    pub pre_dsp: SignalWindow,
    pub post_dsp: SignalWindow,
    pub post_gain: SignalWindow,
}

impl Default for CaptureDiagnostics {
    fn default() -> Self {
        Self {
            metadata: Mutex::new(CaptureMetadata::default()),
            command_seq: AtomicU64::new(0),
            first_callback_ns: AtomicU64::new(0),
            last_callback_ns: AtomicU64::new(0),
            previous_callback_expected_ns: AtomicU64::new(0),
            open_attempts: AtomicU64::new(0),
            stream_errors: AtomicU64::new(0),
            retry_attempt: AtomicU64::new(0),
            callbacks_total: AtomicU64::new(0),
            callbacks_window: AtomicU64::new(0),
            callback_frames_last: AtomicU64::new(0),
            callback_frames_min: AtomicU64::new(UNKNOWN_MIN),
            callback_frames_max: AtomicU64::new(0),
            callback_interval_last_ns: AtomicU64::new(0),
            callback_interval_max_ns: AtomicU64::new(0),
            late_callbacks: AtomicU64::new(0),
            input_samples_total: AtomicU64::new(0),
            resampled_samples_total: AtomicU64::new(0),
            frames_produced_total: AtomicU64::new(0),
            accumulator_pending_samples: AtomicU64::new(0),
            invalid_timestamps: AtomicU64::new(0),
            timestamp_discontinuities: AtomicU64::new(0),
            capture_clock_delta_seen: AtomicBool::new(false),
            capture_clock_delta_last_ns: AtomicU64::new(0),
            capture_clock_expected_delta_ns: AtomicU64::new(0),
            capture_clock_delta_error_ns: AtomicI64::new(0),
            capture_clock_bridge_residual_seen: AtomicBool::new(false),
            capture_clock_bridge_residual_ns: AtomicI64::new(0),
            capture_clock_status: AtomicU64::new(CaptureClockStatus::Unavailable as u64),
            last_timestamp_discontinuity_reason: AtomicU64::new(
                CaptureClockStatus::Unavailable as u64,
            ),
            ring_high_water: AtomicU64::new(0),
            encoder_pop_age_last_ns: AtomicU64::new(0),
            encoder_pop_age_max_ns: AtomicU64::new(0),
            stale_generation_frames: AtomicU64::new(0),
            raw_input: SignalWindow::default(),
            pre_dsp: SignalWindow::default(),
            post_dsp: SignalWindow::default(),
            post_gain: SignalWindow::default(),
        }
    }
}

impl CaptureDiagnostics {
    fn reset_capture_clock_observation(&self) {
        self.capture_clock_delta_seen
            .store(false, Ordering::Release);
        self.capture_clock_delta_last_ns.store(0, Ordering::Relaxed);
        self.capture_clock_expected_delta_ns
            .store(0, Ordering::Relaxed);
        self.capture_clock_delta_error_ns
            .store(0, Ordering::Relaxed);
        self.capture_clock_bridge_residual_seen
            .store(false, Ordering::Release);
        self.capture_clock_bridge_residual_ns
            .store(0, Ordering::Relaxed);
        self.capture_clock_status
            .store(CaptureClockStatus::Unavailable as u64, Ordering::Release);
        self.last_timestamp_discontinuity_reason
            .store(CaptureClockStatus::Unavailable as u64, Ordering::Release);
    }

    pub fn next_command(&self) -> u64 {
        self.command_seq.fetch_add(1, Ordering::Relaxed) + 1
    }

    pub fn begin_stream(&self, now_ns: u64, synthetic: bool, requested: Option<&str>) -> u64 {
        self.first_callback_ns.store(0, Ordering::Release);
        self.last_callback_ns.store(0, Ordering::Release);
        self.previous_callback_expected_ns
            .store(0, Ordering::Release);
        self.retry_attempt.store(0, Ordering::Relaxed);
        self.callbacks_window.store(0, Ordering::Relaxed);
        self.callback_frames_min
            .store(UNKNOWN_MIN, Ordering::Relaxed);
        self.callback_frames_max.store(0, Ordering::Relaxed);
        self.callback_interval_last_ns.store(0, Ordering::Relaxed);
        self.callback_interval_max_ns.store(0, Ordering::Relaxed);
        self.ring_high_water.store(0, Ordering::Relaxed);
        self.encoder_pop_age_last_ns.store(0, Ordering::Relaxed);
        self.encoder_pop_age_max_ns.store(0, Ordering::Relaxed);
        self.accumulator_pending_samples.store(0, Ordering::Relaxed);
        self.reset_capture_clock_observation();
        self.raw_input.reset();
        self.pre_dsp.reset();
        self.post_dsp.reset();
        self.post_gain.reset();
        let requested = requested.unwrap_or_default();
        let mut metadata = self.metadata.lock().unwrap();
        let generation = metadata.stream_generation.saturating_add(1);
        *metadata = CaptureMetadata {
            stream_generation: generation,
            state: "starting".to_string(),
            running: true,
            healthy: false,
            generation_started_ns: now_ns,
            synthetic,
            requested_device: requested.to_string(),
            requested_default: requested.is_empty(),
            ..CaptureMetadata::default()
        };
        generation
    }

    pub fn begin_open_attempt(&self) -> u64 {
        let attempt = self.open_attempts.fetch_add(1, Ordering::Relaxed) + 1;
        self.first_callback_ns.store(0, Ordering::Release);
        self.last_callback_ns.store(0, Ordering::Release);
        self.previous_callback_expected_ns
            .store(0, Ordering::Release);
        self.callback_frames_last.store(0, Ordering::Relaxed);
        self.callback_frames_min
            .store(UNKNOWN_MIN, Ordering::Relaxed);
        self.callback_frames_max.store(0, Ordering::Relaxed);
        self.callback_interval_last_ns.store(0, Ordering::Relaxed);
        self.callback_interval_max_ns.store(0, Ordering::Relaxed);
        self.reset_capture_clock_observation();
        let mut metadata = self.metadata.lock().unwrap();
        metadata.healthy = false;
        metadata.stream_started_ns = 0;
        metadata.state = "opening".to_string();
        attempt
    }

    pub fn mark_stream_started(&self, now_ns: u64, descriptor: StreamDescriptor) {
        self.retry_attempt.store(0, Ordering::Relaxed);
        let mut metadata = self.metadata.lock().unwrap();
        metadata.state = "running".to_string();
        metadata.healthy = true;
        metadata.stream_started_ns = now_ns;
        metadata.requested_device = descriptor.requested_device;
        metadata.resolved_device = descriptor.resolved_device;
        metadata.requested_default = descriptor.requested_default;
        metadata.requested_matched = descriptor.requested_matched;
        metadata.fell_back_to_default = descriptor.fell_back_to_default;
        metadata.sample_rate = descriptor.sample_rate;
        metadata.channels = descriptor.channels;
        metadata.sample_format = descriptor.sample_format;
        metadata.buffer_mode = descriptor.buffer_mode;
        metadata.buffer_min_frames = descriptor.buffer_min_frames;
        metadata.buffer_max_frames = descriptor.buffer_max_frames;
    }

    pub fn mark_retrying(&self, retry_attempt: u64) {
        self.retry_attempt.store(retry_attempt, Ordering::Relaxed);
        self.stream_errors.fetch_add(1, Ordering::Relaxed);
        let mut metadata = self.metadata.lock().unwrap();
        metadata.healthy = false;
        metadata.state = "retrying".to_string();
    }

    pub fn mark_stopped(&self) -> CaptureWindowSummary {
        self.accumulator_pending_samples.store(0, Ordering::Relaxed);
        let mut metadata = self.metadata.lock().unwrap();
        metadata.running = false;
        metadata.healthy = false;
        metadata.state = "stopped".to_string();
        drop(metadata);
        CaptureWindowSummary {
            callbacks: self.callbacks_window.swap(0, Ordering::Relaxed),
            raw_input: self.raw_input.take(),
            pre_dsp: self.pre_dsp.take(),
            post_dsp: self.post_dsp.take(),
            post_gain: self.post_gain.take(),
        }
    }

    pub fn mark_stopping(&self) {
        let mut metadata = self.metadata.lock().unwrap();
        metadata.running = false;
        metadata.healthy = false;
        metadata.state = "stopping".to_string();
    }

    pub fn mark_stop_failed(&self) {
        let mut metadata = self.metadata.lock().unwrap();
        metadata.running = false;
        metadata.healthy = false;
        metadata.state = "stop-failed".to_string();
    }

    pub fn current_generation(&self) -> u64 {
        self.metadata.lock().unwrap().stream_generation
    }

    pub fn current_command_seq(&self) -> u64 {
        self.command_seq.load(Ordering::Relaxed)
    }

    pub fn current_open_attempt(&self) -> u64 {
        self.open_attempts.load(Ordering::Relaxed)
    }

    pub fn is_active_stream(&self, generation: u64, open_attempt: u64) -> bool {
        let metadata = self.metadata.lock().unwrap();
        metadata.running
            && metadata.healthy
            && metadata.stream_generation == generation
            && self.open_attempts.load(Ordering::Relaxed) == open_attempt
    }

    pub fn observe_callback(&self, now_ns: u64, frames: usize, sample_rate: u32) -> bool {
        let frames = frames as u64;
        let previous = self.last_callback_ns.swap(now_ns, Ordering::AcqRel);
        let expected = self.previous_callback_expected_ns.swap(
            frames.saturating_mul(1_000_000_000) / u64::from(sample_rate.max(1)),
            Ordering::AcqRel,
        );
        if previous != 0 && now_ns >= previous {
            let interval = now_ns - previous;
            self.callback_interval_last_ns
                .store(interval, Ordering::Relaxed);
            observe_max(&self.callback_interval_max_ns, interval);
            if expected != 0 && interval > expected.saturating_add(LATE_CALLBACK_GRACE_NS) {
                self.late_callbacks.fetch_add(1, Ordering::Relaxed);
            }
        }
        self.callbacks_total.fetch_add(1, Ordering::Relaxed);
        self.callbacks_window.fetch_add(1, Ordering::Relaxed);
        self.callback_frames_last.store(frames, Ordering::Relaxed);
        observe_min(&self.callback_frames_min, frames);
        observe_max(&self.callback_frames_max, frames);
        self.input_samples_total
            .fetch_add(frames, Ordering::Relaxed);
        self.first_callback_ns
            .compare_exchange(0, now_ns, Ordering::AcqRel, Ordering::Acquire)
            .is_ok()
    }

    pub fn observe_resampled_samples(&self, samples: usize) {
        self.resampled_samples_total
            .fetch_add(samples as u64, Ordering::Relaxed);
    }

    pub fn observe_frames_produced(&self, frames: usize) {
        self.frames_produced_total
            .fetch_add(frames as u64, Ordering::Relaxed);
    }

    pub fn set_accumulator_pending(&self, samples: usize) {
        self.accumulator_pending_samples
            .store(samples as u64, Ordering::Relaxed);
    }

    pub fn note_invalid_timestamp(&self) {
        self.invalid_timestamps.fetch_add(1, Ordering::Relaxed);
    }

    pub fn observe_capture_clock(
        &self,
        status: CaptureClockStatus,
        delta_ns: Option<u64>,
        expected_delta_ns: Option<u64>,
        delta_error_ns: Option<i64>,
        bridge_residual_ns: Option<i64>,
    ) {
        if let (Some(delta), Some(expected), Some(error)) =
            (delta_ns, expected_delta_ns, delta_error_ns)
        {
            self.capture_clock_delta_last_ns
                .store(delta, Ordering::Relaxed);
            self.capture_clock_expected_delta_ns
                .store(expected, Ordering::Relaxed);
            self.capture_clock_delta_error_ns
                .store(error, Ordering::Relaxed);
            self.capture_clock_delta_seen.store(true, Ordering::Release);
        }
        if let Some(residual) = bridge_residual_ns {
            self.capture_clock_bridge_residual_ns
                .store(residual, Ordering::Relaxed);
            self.capture_clock_bridge_residual_seen
                .store(true, Ordering::Release);
        }
        self.capture_clock_status
            .store(status as u64, Ordering::Release);
        if status.is_discontinuity() {
            self.last_timestamp_discontinuity_reason
                .store(status as u64, Ordering::Release);
        }
    }

    pub fn note_timestamp_discontinuity(&self) {
        self.timestamp_discontinuities
            .fetch_add(1, Ordering::Relaxed);
    }

    pub fn observe_ring_len(&self, len: usize) {
        observe_max(&self.ring_high_water, len as u64);
    }

    pub fn observe_encoder_pop_age(&self, age_ns: u64) {
        self.encoder_pop_age_last_ns
            .store(age_ns, Ordering::Relaxed);
        observe_max(&self.encoder_pop_age_max_ns, age_ns);
    }

    pub fn note_stale_generation_frame(&self) {
        self.stale_generation_frames.fetch_add(1, Ordering::Relaxed);
    }

    pub fn snapshot(
        &self,
        now_ns: u64,
        ring_len: u64,
        ring_capacity: u64,
        ring_dropped: u64,
        ring_oldest_frame_age_ms: u64,
    ) -> CaptureDiagnosticsSnapshot {
        let metadata = self.metadata.lock().unwrap().clone();
        let generation_started = metadata.generation_started_ns;
        let stream_started = metadata.stream_started_ns;
        let first_callback = self.first_callback_ns.load(Ordering::Acquire);
        let last_callback = self.last_callback_ns.load(Ordering::Acquire);
        let callback_min = self
            .callback_frames_min
            .swap(UNKNOWN_MIN, Ordering::Relaxed);
        let callback_max = self.callback_frames_max.swap(0, Ordering::Relaxed);
        let callbacks_window = self.callbacks_window.swap(0, Ordering::Relaxed);
        let callback_interval_max = self.callback_interval_max_ns.swap(0, Ordering::Relaxed);
        let encoder_pop_max = self.encoder_pop_age_max_ns.swap(0, Ordering::Relaxed);
        CaptureDiagnosticsSnapshot {
            command_seq: self.command_seq.load(Ordering::Relaxed),
            stream_generation: metadata.stream_generation,
            state: metadata.state,
            running: metadata.running,
            healthy: metadata.healthy,
            synthetic: metadata.synthetic,
            requested_device: metadata.requested_device,
            resolved_device: metadata.resolved_device,
            requested_default: metadata.requested_default,
            requested_matched: metadata.requested_matched,
            fell_back_to_default: metadata.fell_back_to_default,
            sample_rate: metadata.sample_rate,
            channels: metadata.channels,
            sample_format: metadata.sample_format,
            buffer_mode: metadata.buffer_mode,
            buffer_min_frames: metadata.buffer_min_frames,
            buffer_max_frames: metadata.buffer_max_frames,
            open_attempts: self.open_attempts.load(Ordering::Relaxed),
            stream_errors: self.stream_errors.load(Ordering::Relaxed),
            retry_attempt: self.retry_attempt.load(Ordering::Relaxed),
            stream_started: stream_started != 0,
            first_callback_seen: first_callback != 0,
            callback_seen: last_callback != 0,
            callback_window_seen: callbacks_window != 0,
            callback_interval_seen: self.callback_interval_last_ns.load(Ordering::Relaxed) != 0,
            callback_interval_window_seen: callback_interval_max != 0,
            start_to_open_ms: if stream_started == 0 {
                0
            } else {
                duration_ms(stream_started, generation_started)
            },
            open_to_first_callback_ms: if first_callback == 0 || stream_started == 0 {
                0
            } else {
                duration_ms(first_callback, stream_started)
            },
            stream_age_ms: duration_ms(now_ns, stream_started),
            callbacks_total: self.callbacks_total.load(Ordering::Relaxed),
            callbacks_window,
            callback_age_ms: duration_ms(now_ns, last_callback),
            callback_frames_last: self.callback_frames_last.load(Ordering::Relaxed),
            callback_frames_min: if callback_min == UNKNOWN_MIN {
                0
            } else {
                callback_min
            },
            callback_frames_max: callback_max,
            callback_interval_last_us: self.callback_interval_last_ns.load(Ordering::Relaxed)
                / 1_000,
            callback_interval_max_us: callback_interval_max / 1_000,
            late_callbacks: self.late_callbacks.load(Ordering::Relaxed),
            input_samples_total: self.input_samples_total.load(Ordering::Relaxed),
            resampled_samples_total: self.resampled_samples_total.load(Ordering::Relaxed),
            frames_produced_total: self.frames_produced_total.load(Ordering::Relaxed),
            accumulator_pending_samples: self.accumulator_pending_samples.load(Ordering::Relaxed),
            invalid_timestamps: self.invalid_timestamps.load(Ordering::Relaxed),
            timestamp_discontinuities: self.timestamp_discontinuities.load(Ordering::Relaxed),
            capture_clock_delta_seen: self.capture_clock_delta_seen.load(Ordering::Acquire),
            capture_clock_delta_last_us: self.capture_clock_delta_last_ns.load(Ordering::Relaxed)
                / 1_000,
            capture_clock_expected_delta_us: self
                .capture_clock_expected_delta_ns
                .load(Ordering::Relaxed)
                / 1_000,
            capture_clock_delta_error_us: self.capture_clock_delta_error_ns.load(Ordering::Relaxed)
                / 1_000,
            capture_clock_bridge_residual_seen: self
                .capture_clock_bridge_residual_seen
                .load(Ordering::Acquire),
            capture_clock_bridge_residual_us: self
                .capture_clock_bridge_residual_ns
                .load(Ordering::Relaxed)
                / 1_000,
            capture_clock_status: CaptureClockStatus::from_code(
                self.capture_clock_status.load(Ordering::Acquire),
            )
            .as_str()
            .to_string(),
            last_timestamp_discontinuity_reason: {
                let reason = CaptureClockStatus::from_code(
                    self.last_timestamp_discontinuity_reason
                        .load(Ordering::Acquire),
                );
                if reason == CaptureClockStatus::Unavailable {
                    "none".to_string()
                } else {
                    reason.as_str().to_string()
                }
            },
            ring_len,
            ring_capacity,
            ring_high_water: self.ring_high_water.load(Ordering::Relaxed),
            ring_dropped,
            ring_oldest_frame_age_ms,
            ring_has_frames: ring_len > 0,
            encoder_pop_age_last_ms: self.encoder_pop_age_last_ns.load(Ordering::Relaxed)
                / 1_000_000,
            encoder_pop_age_max_ms: encoder_pop_max / 1_000_000,
            encoder_frame_seen: self.encoder_pop_age_last_ns.load(Ordering::Relaxed) != 0,
            encoder_window_seen: encoder_pop_max != 0,
            stale_generation_frames: self.stale_generation_frames.load(Ordering::Relaxed),
            raw_input: self.raw_input.take(),
            pre_dsp: self.pre_dsp.take(),
            post_dsp: self.post_dsp.take(),
            post_gain: self.post_gain.take(),
        }
    }
}

#[derive(Debug, Clone)]
struct PlaybackMetadata {
    stream_generation: u64,
    state: String,
    running: bool,
    requested_device: String,
    resolved_device: String,
    requested_default: bool,
    requested_matched: bool,
    fell_back_to_default: bool,
    sample_rate: u32,
    channels: u16,
    sample_format: String,
    buffer_mode: String,
    buffer_min_frames: u32,
    buffer_max_frames: u32,
}

impl Default for PlaybackMetadata {
    fn default() -> Self {
        Self {
            stream_generation: 0,
            state: "stopped".to_string(),
            running: false,
            requested_device: String::new(),
            resolved_device: String::new(),
            requested_default: true,
            requested_matched: false,
            fell_back_to_default: false,
            sample_rate: 0,
            channels: 0,
            sample_format: String::new(),
            buffer_mode: String::new(),
            buffer_min_frames: 0,
            buffer_max_frames: 0,
        }
    }
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct PlaybackDiagnosticsSnapshot {
    pub stream_generation: u64,
    pub state: String,
    pub running: bool,
    pub requested_device: String,
    pub resolved_device: String,
    pub requested_default: bool,
    pub requested_matched: bool,
    pub fell_back_to_default: bool,
    pub sample_rate: u32,
    pub channels: u16,
    pub sample_format: String,
    pub buffer_mode: String,
    pub buffer_min_frames: u32,
    pub buffer_max_frames: u32,
    pub callbacks_total: u64,
    pub callback_seen: bool,
    pub callback_interval_seen: bool,
    pub callback_interval_window_seen: bool,
    pub callback_age_ms: u64,
    pub callback_frames_last: u64,
    pub callback_interval_last_us: u64,
    pub callback_interval_max_us: u64,
    pub stream_errors: u64,
    pub ring_len: u64,
    pub ring_dropped: u64,
}

pub struct PlaybackDiagnostics {
    metadata: Mutex<PlaybackMetadata>,
    callbacks_total: AtomicU64,
    last_callback_ns: AtomicU64,
    callback_frames_last: AtomicU64,
    callback_interval_last_ns: AtomicU64,
    callback_interval_max_ns: AtomicU64,
    stream_errors: AtomicU64,
}

impl Default for PlaybackDiagnostics {
    fn default() -> Self {
        Self {
            metadata: Mutex::new(PlaybackMetadata::default()),
            callbacks_total: AtomicU64::new(0),
            last_callback_ns: AtomicU64::new(0),
            callback_frames_last: AtomicU64::new(0),
            callback_interval_last_ns: AtomicU64::new(0),
            callback_interval_max_ns: AtomicU64::new(0),
            stream_errors: AtomicU64::new(0),
        }
    }
}

impl PlaybackDiagnostics {
    pub fn begin_stream(&self, requested: Option<&str>) -> u64 {
        self.last_callback_ns.store(0, Ordering::Release);
        self.callback_interval_last_ns.store(0, Ordering::Relaxed);
        self.callback_interval_max_ns.store(0, Ordering::Relaxed);
        let requested = requested.unwrap_or_default();
        let mut metadata = self.metadata.lock().unwrap();
        let generation = metadata.stream_generation.saturating_add(1);
        *metadata = PlaybackMetadata {
            stream_generation: generation,
            state: "opening".to_string(),
            running: true,
            requested_device: requested.to_string(),
            requested_default: requested.is_empty(),
            ..PlaybackMetadata::default()
        };
        generation
    }

    pub fn mark_stream_started(&self, descriptor: StreamDescriptor) {
        let mut metadata = self.metadata.lock().unwrap();
        metadata.state = "running".to_string();
        metadata.requested_device = descriptor.requested_device;
        metadata.resolved_device = descriptor.resolved_device;
        metadata.requested_default = descriptor.requested_default;
        metadata.requested_matched = descriptor.requested_matched;
        metadata.fell_back_to_default = descriptor.fell_back_to_default;
        metadata.sample_rate = descriptor.sample_rate;
        metadata.channels = descriptor.channels;
        metadata.sample_format = descriptor.sample_format;
        metadata.buffer_mode = descriptor.buffer_mode;
        metadata.buffer_min_frames = descriptor.buffer_min_frames;
        metadata.buffer_max_frames = descriptor.buffer_max_frames;
    }

    pub fn observe_callback(&self, now_ns: u64, frames: usize) -> bool {
        let previous = self.last_callback_ns.swap(now_ns, Ordering::AcqRel);
        if previous != 0 && now_ns >= previous {
            let interval = now_ns - previous;
            self.callback_interval_last_ns
                .store(interval, Ordering::Relaxed);
            observe_max(&self.callback_interval_max_ns, interval);
        }
        self.callbacks_total.fetch_add(1, Ordering::Relaxed);
        self.callback_frames_last
            .store(frames as u64, Ordering::Relaxed);
        previous == 0
    }

    pub fn mark_error(&self) {
        self.stream_errors.fetch_add(1, Ordering::Relaxed);
        let mut metadata = self.metadata.lock().unwrap();
        metadata.running = false;
        metadata.state = "error".to_string();
    }

    pub fn mark_stopped(&self) {
        let mut metadata = self.metadata.lock().unwrap();
        metadata.running = false;
        metadata.state = "stopped".to_string();
    }

    pub fn snapshot(
        &self,
        now_ns: u64,
        ring_len: u64,
        ring_dropped: u64,
    ) -> PlaybackDiagnosticsSnapshot {
        let metadata = self.metadata.lock().unwrap().clone();
        let max_interval = self.callback_interval_max_ns.swap(0, Ordering::Relaxed);
        PlaybackDiagnosticsSnapshot {
            stream_generation: metadata.stream_generation,
            state: metadata.state,
            running: metadata.running,
            requested_device: metadata.requested_device,
            resolved_device: metadata.resolved_device,
            requested_default: metadata.requested_default,
            requested_matched: metadata.requested_matched,
            fell_back_to_default: metadata.fell_back_to_default,
            sample_rate: metadata.sample_rate,
            channels: metadata.channels,
            sample_format: metadata.sample_format,
            buffer_mode: metadata.buffer_mode,
            buffer_min_frames: metadata.buffer_min_frames,
            buffer_max_frames: metadata.buffer_max_frames,
            callbacks_total: self.callbacks_total.load(Ordering::Relaxed),
            callback_seen: self.last_callback_ns.load(Ordering::Acquire) != 0,
            callback_interval_seen: self.callback_interval_last_ns.load(Ordering::Relaxed) != 0,
            callback_interval_window_seen: max_interval != 0,
            callback_age_ms: duration_ms(now_ns, self.last_callback_ns.load(Ordering::Acquire)),
            callback_frames_last: self.callback_frames_last.load(Ordering::Relaxed),
            callback_interval_last_us: self.callback_interval_last_ns.load(Ordering::Relaxed)
                / 1_000,
            callback_interval_max_us: max_interval / 1_000,
            stream_errors: self.stream_errors.load(Ordering::Relaxed),
            ring_len,
            ring_dropped,
        }
    }
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct MediaDiagnosticsSnapshot {
    pub schema: u32,
    pub window_seq: u64,
    pub window_ms: u64,
    pub media_state_events_dropped: u64,
    pub capture: CaptureDiagnosticsSnapshot,
    pub playback: PlaybackDiagnosticsSnapshot,
}

#[derive(Default)]
pub struct MediaDiagnostics {
    pub capture: Arc<CaptureDiagnostics>,
    pub playback: Arc<PlaybackDiagnostics>,
    window_seq: AtomicU64,
    last_window_ns: AtomicU64,
}

impl MediaDiagnostics {
    #[allow(clippy::too_many_arguments)]
    pub fn snapshot(
        &self,
        now_ns: u64,
        capture_ring_len: u64,
        capture_ring_capacity: u64,
        capture_ring_dropped: u64,
        capture_ring_oldest_age_ms: u64,
        playback_ring_len: u64,
        playback_ring_dropped: u64,
    ) -> MediaDiagnosticsSnapshot {
        let previous = self.last_window_ns.swap(now_ns, Ordering::AcqRel);
        MediaDiagnosticsSnapshot {
            schema: MEDIA_DIAGNOSTICS_SCHEMA,
            window_seq: self.window_seq.fetch_add(1, Ordering::Relaxed) + 1,
            window_ms: if previous == 0 {
                0
            } else {
                duration_ms(now_ns, previous)
            },
            media_state_events_dropped: MEDIA_STATE_EVENTS_DROPPED.load(Ordering::Relaxed),
            capture: self.capture.snapshot(
                now_ns,
                capture_ring_len,
                capture_ring_capacity,
                capture_ring_dropped,
                capture_ring_oldest_age_ms,
            ),
            playback: self
                .playback
                .snapshot(now_ns, playback_ring_len, playback_ring_dropped),
        }
    }
}

#[derive(Debug, Clone, Default, Serialize)]
pub struct MediaStateEvent {
    pub direction: String,
    pub state: String,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub action: String,
    pub command_seq: u64,
    pub stream_generation: u64,
    pub open_attempt: u64,
    pub changed: bool,
    pub running: bool,
    pub retry_attempt: u64,
    pub retry_delay_ms: u64,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub requested_device: String,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub resolved_device: String,
    pub requested_default: bool,
    pub requested_matched: bool,
    pub fell_back_to_default: bool,
    pub sample_rate: u32,
    pub channels: u16,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub sample_format: String,
    #[serde(skip_serializing_if = "String::is_empty")]
    pub buffer_mode: String,
    pub callback_frames: u64,
    pub elapsed_ms: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub final_window: Option<CaptureWindowSummary>,
}

#[derive(Serialize)]
struct MediaStateMessage<'a> {
    op: &'static str,
    schema: u32,
    #[serde(flatten)]
    event: &'a MediaStateEvent,
}

pub fn media_state_json(event: &MediaStateEvent) -> String {
    serde_json::to_string(&MediaStateMessage {
        op: "media-state",
        schema: MEDIA_DIAGNOSTICS_SCHEMA,
        event,
    })
    .expect("media-state serialize")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn signal_window_reports_and_resets_exact_metrics() {
        let window = SignalWindow::default();
        window.record(&[0.0, 0.5, -1.0, f32::NAN, f32::INFINITY]);
        let snapshot = window.take();
        assert_eq!(snapshot.samples, 5);
        assert_eq!(snapshot.nonfinite_samples, 2);
        assert_eq!(snapshot.near_clip_samples, 1);
        assert_eq!(snapshot.hard_clip_samples, 1);
        assert_eq!(snapshot.peak, 1.0);
        assert!((snapshot.rms - (1.25f64 / 5.0).sqrt()).abs() < 1e-9);
        assert!((snapshot.dc - (-0.5f64 / 5.0)).abs() < 1e-9);
        assert_eq!(window.take().samples, 0);
    }

    #[test]
    fn signal_window_reports_realtime_records_skipped_during_snapshot_contention() {
        let window = SignalWindow::default();
        let guard = window.accumulator.lock().unwrap();
        window.record(&[0.5]);
        drop(guard);
        let snapshot = window.take();
        assert_eq!(snapshot.samples, 0);
        assert_eq!(snapshot.dropped_records, 1);
    }

    #[test]
    fn saturated_media_state_queue_increments_visible_drop_counter() {
        let before = MEDIA_STATE_EVENTS_DROPPED.load(Ordering::Relaxed);
        let (sender, _receiver) = std::sync::mpsc::sync_channel(0);
        send_media_state(&sender, MediaStateEvent::default());
        assert!(MEDIA_STATE_EVENTS_DROPPED.load(Ordering::Relaxed) > before);
    }

    #[test]
    fn capture_generation_and_window_counters_are_distinct() {
        let diagnostics = CaptureDiagnostics::default();
        assert_eq!(diagnostics.begin_stream(1_000_000, false, None), 1);
        assert!(diagnostics.observe_callback(2_000_000, 480, 48_000));
        assert!(!diagnostics.observe_callback(12_000_000, 480, 48_000));
        let first = diagnostics.snapshot(20_000_000, 1, 8, 0, 3);
        assert_eq!(first.stream_generation, 1);
        assert_eq!(first.callbacks_total, 2);
        assert_eq!(first.callbacks_window, 2);
        assert!(first.callback_window_seen);
        assert!(first.callback_interval_window_seen);
        assert!(!first.encoder_window_seen);
        assert_eq!(first.callback_frames_min, 480);
        assert_eq!(first.callback_frames_max, 480);
        let second = diagnostics.snapshot(22_000_000, 0, 8, 0, 0);
        assert_eq!(second.callbacks_total, 2);
        assert_eq!(second.callbacks_window, 0);
        assert!(!second.callback_window_seen);
        assert!(!second.callback_interval_window_seen);
        diagnostics.mark_stopped();
        assert_eq!(diagnostics.begin_stream(30_000_000, false, Some("mic")), 2);
    }

    #[test]
    fn pristine_media_snapshot_marks_unobserved_zero_values_as_unavailable() {
        let diagnostics = MediaDiagnostics::default();
        let snapshot = diagnostics.snapshot(1_000_000_000, 0, 8, 0, 0, 0, 0);

        assert!(!snapshot.capture.stream_started);
        assert!(!snapshot.capture.first_callback_seen);
        assert!(!snapshot.capture.callback_seen);
        assert!(!snapshot.capture.callback_window_seen);
        assert!(!snapshot.capture.callback_interval_seen);
        assert!(!snapshot.capture.callback_interval_window_seen);
        assert!(!snapshot.capture.ring_has_frames);
        assert!(!snapshot.capture.encoder_frame_seen);
        assert!(!snapshot.capture.encoder_window_seen);
        assert!(!snapshot.playback.callback_seen);
        assert!(!snapshot.playback.callback_interval_seen);
        assert!(!snapshot.playback.callback_interval_window_seen);
    }

    #[test]
    fn capture_clock_diagnostics_report_deltas_residuals_and_last_failure_reason() {
        let diagnostics = CaptureDiagnostics::default();
        diagnostics.begin_stream(1_000_000, false, None);
        diagnostics.begin_open_attempt();
        diagnostics.observe_capture_clock(
            CaptureClockStatus::Continuous,
            Some(10_001_000),
            Some(10_000_000),
            Some(1_000),
            Some(-2_500_000),
        );
        let healthy = diagnostics.snapshot(20_000_000, 0, 8, 0, 0);
        assert!(healthy.capture_clock_delta_seen);
        assert_eq!(healthy.capture_clock_delta_last_us, 10_001);
        assert_eq!(healthy.capture_clock_expected_delta_us, 10_000);
        assert_eq!(healthy.capture_clock_delta_error_us, 1);
        assert!(healthy.capture_clock_bridge_residual_seen);
        assert_eq!(healthy.capture_clock_bridge_residual_us, -2_500);
        assert_eq!(healthy.capture_clock_status, "continuous");
        assert_eq!(healthy.last_timestamp_discontinuity_reason, "none");

        diagnostics.observe_capture_clock(
            CaptureClockStatus::DeltaMismatch,
            Some(30_000_000),
            Some(10_000_000),
            Some(20_000_000),
            Some(0),
        );
        diagnostics.note_timestamp_discontinuity();
        let failed = diagnostics.snapshot(21_000_000, 0, 8, 0, 0);
        assert_eq!(
            failed.last_timestamp_discontinuity_reason,
            "backend-capture-delta-mismatch"
        );

        diagnostics.begin_open_attempt();
        let reopened = diagnostics.snapshot(22_000_000, 0, 8, 0, 0);
        assert!(!reopened.capture_clock_delta_seen);
        assert!(!reopened.capture_clock_bridge_residual_seen);
        assert_eq!(reopened.capture_clock_status, "unavailable");
        assert_eq!(reopened.last_timestamp_discontinuity_reason, "none");
    }

    #[test]
    fn active_capture_identity_includes_each_concrete_open_attempt() {
        let diagnostics = CaptureDiagnostics::default();
        let generation = diagnostics.begin_stream(1_000_000, false, None);
        let first_open = diagnostics.begin_open_attempt();
        diagnostics.mark_stream_started(2_000_000, StreamDescriptor::default());
        assert!(diagnostics.is_active_stream(generation, first_open));

        let second_open = diagnostics.begin_open_attempt();
        assert!(!diagnostics.is_active_stream(generation, first_open));
        diagnostics.mark_stream_started(3_000_000, StreamDescriptor::default());
        assert!(diagnostics.is_active_stream(generation, second_open));
    }

    #[test]
    fn stop_seals_the_generation_window_before_restart_resets_it() {
        let diagnostics = CaptureDiagnostics::default();
        diagnostics.begin_stream(1_000_000, false, None);
        diagnostics.begin_open_attempt();
        diagnostics.mark_stream_started(2_000_000, StreamDescriptor::default());
        diagnostics.observe_callback(3_000_000, 480, 48_000);
        diagnostics.raw_input.record(&[0.25, -0.25]);
        diagnostics.pre_dsp.record(&[0.20, -0.20]);

        let closed = diagnostics.mark_stopped();
        assert_eq!(closed.callbacks, 1);
        assert_eq!(closed.raw_input.samples, 2);
        assert_eq!(closed.pre_dsp.samples, 2);

        diagnostics.begin_stream(4_000_000, false, None);
        let next = diagnostics.snapshot(5_000_000, 0, 8, 0, 0);
        assert_eq!(next.callbacks_window, 0);
        assert_eq!(next.raw_input.samples, 0);
        assert_eq!(next.pre_dsp.samples, 0);
    }

    #[test]
    fn media_state_json_is_flat_and_identifies_schema_event() {
        let event = MediaStateEvent {
            direction: "capture".to_string(),
            state: "command-accepted".to_string(),
            action: "start".to_string(),
            command_seq: 4,
            stream_generation: 2,
            changed: true,
            running: true,
            ..Default::default()
        };
        let value: serde_json::Value = serde_json::from_str(&media_state_json(&event)).unwrap();
        assert_eq!(value["op"], "media-state");
        assert_eq!(value["schema"], MEDIA_DIAGNOSTICS_SCHEMA);
        assert_eq!(value["direction"], "capture");
        assert_eq!(value["command_seq"], 4);
    }
}
