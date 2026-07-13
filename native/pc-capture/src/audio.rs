use crate::proto::{AudioFrame, AudioRing, DeviceInfo, PlaybackRing, FRAME_SAMPLES, SAMPLE_RATE};
use crate::rtc::NativeCounters;
use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

pub use crate::input::SyntheticTone as ToneSource;
pub use crate::input::SYNTHETIC_TONE_HZ as TONE_HZ;

pub fn downmix_to_mono(interleaved: &[f32], channels: usize) -> Vec<f32> {
    if channels <= 1 {
        return interleaved.to_vec();
    }
    let frames = interleaved.len() / channels;
    let mut out = Vec::with_capacity(frames);
    for f in 0..frames {
        let base = f * channels;
        let mut sum = 0.0f32;
        for c in 0..channels {
            sum += interleaved[base + c];
        }
        out.push(sum / channels as f32);
    }
    out
}

pub struct Resampler {
    ratio: f64,
    pos: f64,
    last: f32,
    primed: bool,
}

impl Resampler {
    pub fn new(in_rate: u32) -> Resampler {
        let in_rate = in_rate.max(1);
        Resampler {
            ratio: in_rate as f64 / SAMPLE_RATE as f64,
            pos: 0.0,
            last: 0.0,
            primed: false,
        }
    }

    pub fn process(&mut self, mono_in: &[f32]) -> Vec<f32> {
        if mono_in.is_empty() {
            return Vec::new();
        }
        if (self.ratio - 1.0).abs() < f64::EPSILON {
            return mono_in.to_vec();
        }
        let mut out = Vec::with_capacity((mono_in.len() as f64 / self.ratio) as usize + 2);
        let n = mono_in.len();
        loop {
            let idx_f = self.pos;
            let i0 = idx_f.floor() as isize;
            if i0 >= n as isize {
                break;
            }
            let frac = (idx_f - i0 as f64) as f32;
            let s0 = if i0 < 0 {
                self.last
            } else {
                mono_in[i0 as usize]
            };
            let i1 = i0 + 1;
            let s1 = if i1 < n as isize {
                mono_in[i1 as usize]
            } else {
                mono_in[n - 1]
            };
            out.push(s0 + (s1 - s0) * frac);
            self.pos += self.ratio;
        }
        self.pos -= n as f64;
        self.last = mono_in[n - 1];
        self.primed = true;
        out
    }
}

pub struct FrameAccumulator {
    buf: Vec<f32>,
}

impl Default for FrameAccumulator {
    fn default() -> Self {
        Self::new()
    }
}

impl FrameAccumulator {
    pub fn new() -> FrameAccumulator {
        FrameAccumulator {
            buf: Vec::with_capacity(FRAME_SAMPLES * 2),
        }
    }

    pub fn push_and_drain(
        &mut self,
        mono48k: &[f32],
        ts_provider: &mut dyn FnMut() -> u64,
    ) -> Vec<AudioFrame> {
        self.buf.extend_from_slice(mono48k);
        let mut frames = Vec::new();
        while self.buf.len() >= FRAME_SAMPLES {
            let samples: Vec<f32> = self.buf.drain(0..FRAME_SAMPLES).collect();
            frames.push(AudioFrame {
                capture_ts_ns: ts_provider(),
                samples,
            });
        }
        frames
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
    (EPOCH
        .get_or_init(Instant::now)
        .elapsed()
        .as_nanos()
        .min(u64::MAX as u128) as u64)
        // Zero is reserved as the "not observed" sentinel in PlaybackProgress.
        .max(1)
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
    pub timing_complete: bool,
    pub render_observations: u64,
    pub invalid_timestamp_samples: u64,
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
    invalid_timestamp_samples: AtomicU64,
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
            invalid_timestamp_samples: AtomicU64::new(0),
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

    pub fn observe_input_callback(&self, info: &cpal::InputCallbackInfo) {
        let timestamp = info.timestamp();
        Self::observe_latency(
            &self.input_latency_us,
            timestamp.callback.duration_since(&timestamp.capture),
            &self.invalid_timestamp_samples,
        );
        self.capture_callback_ns
            .store(monotonic_ns(), Ordering::Release);
    }

    pub fn observe_output_callback(&self, info: &cpal::OutputCallbackInfo) {
        let timestamp = info.timestamp();
        Self::observe_latency(
            &self.output_latency_us,
            timestamp.playback.duration_since(&timestamp.callback),
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

    fn component_us(target: &AtomicU64) -> Option<u64> {
        let value = target.load(Ordering::Acquire);
        (value != UNKNOWN_AEC_TIMING_US).then_some(value)
    }

    pub fn snapshot(&self, now_ns: u64) -> AecDelaySnapshot {
        let input_us = Self::component_us(&self.input_latency_us);
        let output_us = Self::component_us(&self.output_latency_us);
        let render_observations = self.render_observations.load(Ordering::Acquire);
        let render_queue_us = if render_observations == 0 {
            0
        } else {
            self.render_queue_pairs
                .load(Ordering::Acquire)
                .saturating_mul(1_000_000)
                / SAMPLE_RATE as u64
        };

        let callback_ns = self.capture_callback_ns.load(Ordering::Acquire);
        let callback_age_ns = now_ns.saturating_sub(callback_ns);
        let capture_processing_us = if callback_ns != 0
            && now_ns >= callback_ns
            && callback_age_ns <= AEC_CAPTURE_CALLBACK_STALE_NS
        {
            callback_age_ns / 1_000
        } else {
            0
        };

        let timing_complete = input_us.is_some()
            && output_us.is_some()
            && render_observations > 0
            && callback_ns != 0
            && callback_age_ns <= AEC_CAPTURE_CALLBACK_STALE_NS;
        let measured_us = input_us
            .unwrap_or(0)
            .saturating_add(output_us.unwrap_or(0))
            .saturating_add(render_queue_us)
            .saturating_add(capture_processing_us)
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

        AecDelaySnapshot {
            recommended_delay_ms,
            measured_delay_ms,
            input_latency_ms: ((input_us.unwrap_or(0) + 500) / 1_000),
            output_latency_ms: ((output_us.unwrap_or(0) + 500) / 1_000),
            render_queue_ms: ((render_queue_us + 500) / 1_000),
            capture_processing_ms: ((capture_processing_us + 500) / 1_000),
            timing_complete,
            render_observations,
            invalid_timestamp_samples: self.invalid_timestamp_samples.load(Ordering::Relaxed),
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

pub fn enumerate_devices() -> Vec<DeviceInfo> {
    let host = cpal::default_host();
    let default_name = host.default_input_device().and_then(|d| d.name().ok());
    let mut out = Vec::new();
    if let Ok(devices) = host.input_devices() {
        for d in devices {
            let name = match d.name() {
                Ok(n) => n,
                Err(_) => continue,
            };
            let is_default = Some(&name) == default_name.as_ref();
            out.push(DeviceInfo {
                id: name.clone(),
                name,
                default: is_default,
            });
        }
    }
    out.sort_by_key(|d| std::cmp::Reverse(d.default));
    out
}

fn pick_device(device_id: &Option<String>) -> Result<cpal::Device, String> {
    let host = cpal::default_host();
    if let Some(id) = device_id {
        if let Ok(devices) = host.input_devices() {
            for d in devices {
                if d.name().ok().as_deref() == Some(id.as_str()) {
                    return Ok(d);
                }
            }
        }
    }
    host.default_input_device()
        .ok_or_else(|| "no input device".to_string())
}

pub fn spawn_cpal_capture(
    device_id: Option<String>,
    ring: Arc<Mutex<AudioRing>>,
    stop: Arc<AtomicBool>,
    healthy: Arc<AtomicBool>,
    aec_timing: Arc<AecTiming>,
) -> Result<(), String> {
    let device = pick_device(&device_id)?;
    let config = device
        .default_input_config()
        .map_err(|e| format!("default input config: {e}"))?;
    let in_rate = config.sample_rate().0;
    let channels = config.channels() as usize;
    let resampler = Arc::new(Mutex::new(Resampler::new(in_rate)));
    let accumulator = Arc::new(Mutex::new(FrameAccumulator::new()));
    let errored = Arc::new(AtomicBool::new(false));
    let make_err = || {
        let ef = errored.clone();
        move |e| {
            eprintln!("cpal stream error: {e}");
            ef.store(true, Ordering::Relaxed);
        }
    };

    let cb_ring = ring.clone();
    let cb_rs = resampler.clone();
    let cb_acc = accumulator.clone();
    let cb_timing = aec_timing.clone();
    let push = move |data: &[f32], info: &cpal::InputCallbackInfo| {
        cb_timing.observe_input_callback(info);
        let mono = downmix_to_mono(data, channels);
        let resampled = cb_rs.lock().unwrap().process(&mono);
        let mut clock = now_ns;
        let frames = cb_acc
            .lock()
            .unwrap()
            .push_and_drain(&resampled, &mut clock);
        if frames.is_empty() {
            return;
        }
        let mut ring = cb_ring.lock().unwrap();
        for f in frames {
            ring.push(f);
        }
    };

    let sample_format = config.sample_format();
    let stream_config: cpal::StreamConfig = config.into();
    let stream = match sample_format {
        cpal::SampleFormat::F32 => device.build_input_stream(
            &stream_config,
            move |data: &[f32], info| push(data, info),
            make_err(),
            None,
        ),
        cpal::SampleFormat::I16 => device.build_input_stream(
            &stream_config,
            move |data: &[i16], info| {
                let f: Vec<f32> = data.iter().map(|&s| s as f32 / 32768.0).collect();
                push(&f, info);
            },
            make_err(),
            None,
        ),
        cpal::SampleFormat::U16 => device.build_input_stream(
            &stream_config,
            move |data: &[u16], info| {
                let f: Vec<f32> = data
                    .iter()
                    .map(|&s| (s as f32 - 32768.0) / 32768.0)
                    .collect();
                push(&f, info);
            },
            make_err(),
            None,
        ),
        fmt => return Err(format!("unsupported sample format: {fmt:?}")),
    }
    .map_err(|e| format!("build input stream: {e}"))?;

    stream.play().map_err(|e| format!("stream play: {e}"))?;
    healthy.store(true, Ordering::Relaxed);
    while !stop.load(Ordering::Relaxed) {
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
    let default_name = host.default_output_device().and_then(|d| d.name().ok());
    let mut out = Vec::new();
    if let Ok(devices) = host.output_devices() {
        for d in devices {
            let name = match d.name() {
                Ok(n) => n,
                Err(_) => continue,
            };
            let is_default = Some(&name) == default_name.as_ref();
            out.push(DeviceInfo {
                id: name.clone(),
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
) -> Result<cpal::Device, String> {
    if let Some(id) = device_id {
        if let Ok(devices) = host.output_devices() {
            for d in devices {
                if d.name().ok().as_deref() == Some(id.as_str()) {
                    return Ok(d);
                }
            }
        }
    }
    host.default_output_device()
        .ok_or_else(|| "no output device".to_string())
}

fn pick_output_config(device: &cpal::Device) -> Result<cpal::SupportedStreamConfig, String> {
    if let Ok(ranges) = device.supported_output_configs() {
        let mut best: Option<cpal::SupportedStreamConfigRange> = None;
        for r in ranges {
            if r.min_sample_rate().0 <= SAMPLE_RATE && r.max_sample_rate().0 >= SAMPLE_RATE {
                let better = match &best {
                    None => true,
                    Some(b) => {
                        let bf = b.sample_format() == cpal::SampleFormat::F32;
                        let rf = r.sample_format() == cpal::SampleFormat::F32;
                        (rf && !bf) || (rf == bf && r.channels() > b.channels())
                    }
                };
                if better {
                    best = Some(r);
                }
            }
        }
        if let Some(r) = best {
            return Ok(r.with_sample_rate(cpal::SampleRate(SAMPLE_RATE)));
        }
    }
    device
        .default_output_config()
        .map_err(|e| format!("default output config: {e}"))
}

fn write_out_frame(out: &mut [f32], frame: usize, channels: usize, l: f32, r: f32) {
    let base = frame * channels;
    if channels == 1 {
        out[base] = (l + r) * 0.5;
        return;
    }
    for c in 0..channels {
        out[base + c] = match c {
            0 => l,
            1 => r,
            _ => 0.0,
        };
    }
}

pub fn spawn_cpal_playback(
    device_id: Option<String>,
    playback: Arc<Mutex<PlaybackRing>>,
    stop: Arc<AtomicBool>,
    counters: Arc<NativeCounters>,
    progress: Arc<PlaybackProgress>,
    aec_timing: Arc<AecTiming>,
) -> Result<(), String> {
    let host = cpal::default_host();
    let device = pick_output_device(&host, &device_id)?;
    let config = pick_output_config(&device)?;
    let out_channels = config.channels() as usize;
    let out_rate = config.sample_rate().0;
    let sample_format = config.sample_format();
    let stream_config: cpal::StreamConfig = config.into();
    let ratio = SAMPLE_RATE as f64 / out_rate.max(1) as f64;

    let cb_ring = playback.clone();
    let target_pairs = (FRAME_SAMPLES * 4) as f64;
    let mut s0 = (0.0f32, 0.0f32);
    let mut s1 = (0.0f32, 0.0f32);
    let mut pos = 1.0f64;
    let fill_counters = counters.clone();
    let fill_progress = progress.clone();
    let fill_timing = aec_timing.clone();
    let mut fill = move |out: &mut [f32], info: &cpal::OutputCallbackInfo| {
        let frames = out.len() / out_channels.max(1);
        fill_timing.observe_output_callback(info);
        fill_progress.mark_callback();
        fill_counters
            .playback_callbacks
            .fetch_add(1, Ordering::Relaxed);
        let mut ring = match cb_ring.try_lock() {
            Ok(g) => g,
            Err(_) => {
                for s in out.iter_mut() {
                    *s = 0.0;
                }
                fill_counters
                    .playback_lock_contention_callbacks
                    .fetch_add(1, Ordering::Relaxed);
                fill_counters
                    .playback_lock_contention_silence_pairs
                    .fetch_add(frames as u64, Ordering::Relaxed);
                return;
            }
        };
        let err = (ring.len() as f64 - target_pairs) / target_pairs;
        let eff_ratio = ratio * (1.0 + (err * 0.05).clamp(-0.004, 0.004));
        let mut requested_pairs = 0u64;
        let mut consumed_pairs = 0u64;
        let mut underrun_pairs = 0u64;
        for f in 0..frames {
            while pos >= 1.0 {
                s0 = s1;
                requested_pairs += 1;
                match ring.pop_stereo() {
                    Some(pair) => {
                        s1 = pair;
                        consumed_pairs += 1;
                    }
                    None => {
                        s1 = (0.0, 0.0);
                        underrun_pairs += 1;
                    }
                }
                pos -= 1.0;
            }
            let t = pos as f32;
            let l = s0.0 + (s1.0 - s0.0) * t;
            let r = s0.1 + (s1.1 - s0.1) * t;
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

    let make_err = || {
        let es = stop.clone();
        let error_counters = counters.clone();
        move |e| {
            error_counters
                .playback_errors
                .fetch_add(1, Ordering::Relaxed);
            error_counters
                .playback_callback_errors
                .fetch_add(1, Ordering::Relaxed);
            eprintln!("cpal output stream error: {e}");
            es.store(true, Ordering::Relaxed);
        }
    };

    let stream = match sample_format {
        cpal::SampleFormat::F32 => device.build_output_stream(
            &stream_config,
            move |data: &mut [f32], info| fill(data, info),
            make_err(),
            None,
        ),
        cpal::SampleFormat::I16 => {
            let mut scratch: Vec<f32> = Vec::new();
            device.build_output_stream(
                &stream_config,
                move |data: &mut [i16], info| {
                    scratch.resize(data.len(), 0.0);
                    fill(&mut scratch, info);
                    for (d, &s) in data.iter_mut().zip(scratch.iter()) {
                        *d = (s.clamp(-1.0, 1.0) * 32767.0) as i16;
                    }
                },
                make_err(),
                None,
            )
        }
        cpal::SampleFormat::U16 => {
            let mut scratch: Vec<f32> = Vec::new();
            device.build_output_stream(
                &stream_config,
                move |data: &mut [u16], info| {
                    scratch.resize(data.len(), 0.0);
                    fill(&mut scratch, info);
                    for (d, &s) in data.iter_mut().zip(scratch.iter()) {
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

    stream
        .play()
        .map_err(|e| format!("output stream play: {e}"))?;
    progress.mark_started();
    counters.playback_starts.fetch_add(1, Ordering::Relaxed);
    while !stop.load(Ordering::Relaxed) {
        std::thread::sleep(std::time::Duration::from_millis(20));
    }
    drop(stream);
    counters.playback_stops.fetch_add(1, Ordering::Relaxed);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::proto::{FRAME_SAMPLES, SAMPLE_RATE};

    #[test]
    fn aec_timing_combines_hardware_queue_and_processing_delay() {
        let timing = AecTiming::default();
        timing.observe_input_latency_for_test(Duration::from_millis(12), 1_000_000_000);
        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);

        let snapshot = timing.snapshot(1_004_000_000);
        assert!(snapshot.timing_complete);
        assert_eq!(snapshot.input_latency_ms, 12);
        assert_eq!(snapshot.output_latency_ms, 18);
        assert_eq!(snapshot.render_queue_ms, 20);
        assert_eq!(snapshot.capture_processing_ms, 4);
        assert_eq!(snapshot.measured_delay_ms, 57);
        assert_eq!(snapshot.recommended_delay_ms, 57);
    }

    #[test]
    fn aec_timing_uses_safe_startup_delay_until_measurements_are_current() {
        let timing = AecTiming::default();
        let startup = timing.snapshot(1_000_000_000);
        assert!(!startup.timing_complete);
        assert_eq!(startup.recommended_delay_ms, DEFAULT_AEC_DELAY_MS);

        timing.observe_input_latency_for_test(Duration::from_millis(12), 1_000_000_000);
        timing.observe_output_latency_for_test(Duration::from_millis(18));
        timing.observe_render_queue_pairs(FRAME_SAMPLES);
        let stale = timing.snapshot(1_000_000_000 + AEC_CAPTURE_CALLBACK_STALE_NS + 1);
        assert!(!stale.timing_complete);
        assert_eq!(stale.recommended_delay_ms, DEFAULT_AEC_DELAY_MS);
    }

    #[test]
    fn aec_timing_clamps_extreme_measurements() {
        let timing = AecTiming::default();
        timing.observe_input_latency_for_test(Duration::from_millis(900), 1_000_000_000);
        timing.observe_output_latency_for_test(Duration::from_millis(900));
        timing.observe_render_queue_pairs(usize::MAX);

        let snapshot = timing.snapshot(1_001_000_000);
        assert!(snapshot.timing_complete);
        assert_eq!(snapshot.measured_delay_ms, MAX_AEC_DELAY_MS);
        assert_eq!(snapshot.recommended_delay_ms, MAX_AEC_DELAY_MS as i32);
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
    fn downmix_stereo_averages_channels() {
        let interleaved = [1.0f32, 0.0, 0.0, 1.0, 0.5, 0.5];
        let mono = downmix_to_mono(&interleaved, 2);
        assert_eq!(mono, vec![0.5, 0.5, 0.5]);
    }

    #[test]
    fn downmix_mono_is_identity() {
        let mono = downmix_to_mono(&[0.1, 0.2, 0.3], 1);
        assert_eq!(mono, vec![0.1, 0.2, 0.3]);
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
    fn accumulator_emits_exact_960_sample_frames() {
        let mut acc = FrameAccumulator::new();
        let mut ts = 0u64;
        let mut clock = move || {
            ts += 1;
            ts
        };
        let frames = acc.push_and_drain(&vec![0.0f32; 100], &mut clock);
        assert!(frames.is_empty());
        let frames = acc.push_and_drain(&vec![0.0f32; FRAME_SAMPLES * 2 + 10], &mut clock);
        assert_eq!(frames.len(), 2);
        for f in &frames {
            assert_eq!(f.samples.len(), FRAME_SAMPLES);
        }
        assert_eq!(frames[0].capture_ts_ns, 1);
        assert_eq!(frames[1].capture_ts_ns, 2);
        let frames = acc.push_and_drain(&vec![0.0f32; FRAME_SAMPLES - 110], &mut clock);
        assert_eq!(frames.len(), 1);
        assert_eq!(frames[0].samples.len(), FRAME_SAMPLES);
    }

    #[test]
    fn tone_frame_is_full_and_audible() {
        let mut src = ToneSource::new();
        let f = src.fill_frame(now_ns());
        assert_eq!(f.samples.len(), FRAME_SAMPLES);
        let p = peak(&f.samples);
        assert!(p > 0.011, "tone too quiet: {p}");
        assert!(p <= 0.012_01, "tone exceeds diagnostic amplitude: {p}");
    }

    #[test]
    fn tone_is_continuous_across_frames() {
        let mut src = ToneSource::new();
        let f1 = src.fill_frame(now_ns());
        let f2 = src.fill_frame(now_ns());
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
