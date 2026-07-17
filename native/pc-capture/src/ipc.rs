use crate::audio::{
    monotonic_ns, now_ns, peak, spawn_cpal_capture, spawn_cpal_playback, AecTiming,
    PlaybackProgress, ToneSource,
};
use crate::codec::{
    decode_with_concealment_report, EncodedPacketBuffer, EncodedRtpPacket, OpusCodec,
};
use crate::diagnostics::{
    media_state_json, send_media_state, CaptureDiagnostics, MediaDiagnostics, MediaStateEvent,
    StreamDescriptor,
};
use crate::engine::reset_decoded_peer_timeline;
use crate::gamestate::{GameState, LocalState, PeerState};
use crate::input::{
    InputConfig, LevelCadence, NoiseGate, PeerLevelCadence, TelemetryMailbox, TELEMETRY_INTERVAL,
};
use crate::mix::{Mixer, PeerJitter};
use crate::proto;
use crate::proto::{
    capture_frame_ring, encode_control, error_json, level_json, local_candidate_json,
    local_sdp_json, parse_inbound, peer_levels_json, peer_state_json, pong_json, ready_json,
    stats_json_with_diagnostics, AudioFrame, AudioOutFrame, CaptureFrameConsumer,
    CaptureFrameMetadata, CaptureFrameProducer, DeviceInfo, Frame, InboundOp, MediaReceiveStats,
    NetworkPathStats, PlaybackRing, PROTO_VERSION, RING_CAPACITY,
};
use crate::rtc::{LocalSignal, NativeCounters, RtcEngine};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::{Shutdown, TcpListener, TcpStream};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{Receiver, Sender, SyncSender};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

pub const MAX_FRAME_LEN: usize = 1 << 20;
const CONTROL_EOF_CLEANUP_DEADLINE: Duration = Duration::from_secs(3);
const PLAYBACK_SPAWN_THROTTLE: Duration = Duration::from_secs(1);
const PLAYBACK_START_TIMEOUT: Duration = Duration::from_secs(5);
const PLAYBACK_CALLBACK_STALL_TIMEOUT: Duration = Duration::from_secs(3);
const PLAYBACK_STOP_TIMEOUT: Duration = Duration::from_millis(750);
const CAPTURE_STOP_TIMEOUT: Duration = Duration::from_millis(750);
const ENCODER_PRIVACY_EPOCH_TIMEOUT: Duration = Duration::from_secs(2);
const PLAYBACK_WATCHDOG_INTERVAL: Duration = Duration::from_millis(100);
const CRITICAL_FAILURE_POLL_INTERVAL: Duration = Duration::from_millis(100);

fn duration_ns(duration: Duration) -> u64 {
    duration.as_nanos().min(u64::MAX as u128) as u64
}

#[derive(Clone)]
struct SessionSupervisor {
    stopping: Arc<AtomicBool>,
    failures: Sender<String>,
}

impl SessionSupervisor {
    fn report(&self, worker: &str, detail: &str) {
        if !self.stopping.load(Ordering::Acquire) {
            let _ = self.failures.send(format!("{worker}: {detail}"));
        }
    }
}

#[derive(Clone)]
struct PlaybackSupervision {
    progress: Arc<PlaybackProgress>,
    aec_timing: Arc<AecTiming>,
    media_diagnostics: Arc<MediaDiagnostics>,
    media_events: SyncSender<MediaStateEvent>,
    session: SessionSupervisor,
}

#[derive(Clone)]
struct TelemetrySources {
    counters: Arc<NativeCounters>,
    capture_ring: CaptureFrameConsumer,
    playback_ring: Arc<Mutex<PlaybackRing>>,
    dsp: Arc<Mutex<crate::dsp::Dsp>>,
    input: Arc<Mutex<InputConfig>>,
    aec_timing: Arc<AecTiming>,
    media_diagnostics: Arc<MediaDiagnostics>,
    rtc: Arc<RtcEngine>,
}

fn panic_detail(payload: &(dyn std::any::Any + Send)) -> &str {
    if let Some(message) = payload.downcast_ref::<&str>() {
        message
    } else if let Some(message) = payload.downcast_ref::<String>() {
        message.as_str()
    } else {
        "unknown panic payload"
    }
}

fn spawn_critical_worker<F>(
    worker: &'static str,
    supervisor: SessionSupervisor,
    run: F,
) -> std::thread::JoinHandle<()>
where
    F: FnOnce() + Send + 'static,
{
    std::thread::Builder::new()
        .name(format!("pc-capture-{worker}"))
        .spawn(move || {
            let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(run));
            if supervisor.stopping.load(Ordering::Acquire) {
                return;
            }
            let detail = match &outcome {
                Ok(()) => "exited unexpectedly".to_string(),
                Err(payload) => format!("panicked: {}", panic_detail(payload.as_ref())),
            };
            supervisor.report(worker, &detail);
        })
        .unwrap_or_else(|error| panic!("cannot spawn critical worker {worker}: {error}"))
}

fn spawn_critical_failure_monitor(
    shutdown_stream: TcpStream,
    session_stopping: Arc<AtomicBool>,
    failures: Receiver<String>,
) -> std::thread::JoinHandle<()> {
    std::thread::Builder::new()
        .name("pc-capture-critical-monitor".to_string())
        .spawn(move || loop {
            match failures.recv_timeout(CRITICAL_FAILURE_POLL_INTERVAL) {
                Ok(reason) => {
                    if session_stopping.swap(true, Ordering::AcqRel) {
                        break;
                    }
                    eprintln!("pc-capture: critical media failure: {reason}; closing session");
                    let _ = shutdown_stream.shutdown(Shutdown::Both);
                    break;
                }
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                    if session_stopping.load(Ordering::Acquire) {
                        break;
                    }
                }
                Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
            }
        })
        .unwrap_or_else(|error| panic!("cannot spawn critical failure monitor: {error}"))
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum PlaybackWatchdogFailure {
    StartupTimedOut,
    CallbackStalled,
}

impl PlaybackWatchdogFailure {
    fn detail(self) -> &'static str {
        match self {
            Self::StartupTimedOut => "output stream startup timed out",
            Self::CallbackStalled => "output callback stalled with queued audio",
        }
    }
}

#[derive(Default)]
struct PlaybackWatchdog {
    observed_callback_ns: u64,
    stalled_since_ns: Option<u64>,
}

impl PlaybackWatchdog {
    fn observe(
        &mut self,
        now_ns: u64,
        worker_running: bool,
        spawned_ns: u64,
        started_ns: u64,
        callback_ns: u64,
        queued_pairs: usize,
    ) -> Option<PlaybackWatchdogFailure> {
        if !worker_running {
            self.observed_callback_ns = 0;
            self.stalled_since_ns = None;
            return None;
        }

        if started_ns == 0 {
            self.observed_callback_ns = 0;
            self.stalled_since_ns = None;
            if spawned_ns != 0
                && now_ns.saturating_sub(spawned_ns) >= duration_ns(PLAYBACK_START_TIMEOUT)
            {
                return Some(PlaybackWatchdogFailure::StartupTimedOut);
            }
            return None;
        }

        if queued_pairs == 0 {
            self.observed_callback_ns = callback_ns;
            self.stalled_since_ns = None;
            return None;
        }

        if callback_ns != self.observed_callback_ns {
            self.observed_callback_ns = callback_ns;
            self.stalled_since_ns = None;
            return None;
        }

        match self.stalled_since_ns {
            None => self.stalled_since_ns = Some(now_ns),
            Some(stalled_since)
                if now_ns.saturating_sub(stalled_since)
                    >= duration_ns(PLAYBACK_CALLBACK_STALL_TIMEOUT) =>
            {
                return Some(PlaybackWatchdogFailure::CallbackStalled);
            }
            Some(_) => {}
        }
        None
    }
}

/// Arms a fail-safe for teardown work that can block inside platform audio/RTC code.
/// Completion wakes and joins the watchdog immediately; a healthy shutdown never waits
/// for the deadline to elapse.
struct CleanupDeadline {
    state: Arc<(Mutex<bool>, Condvar)>,
    handle: std::thread::JoinHandle<()>,
}

impl CleanupDeadline {
    fn arm<F>(timeout: Duration, on_timeout: F) -> std::io::Result<Self>
    where
        F: FnOnce() + Send + 'static,
    {
        let state = Arc::new((Mutex::new(false), Condvar::new()));
        let thread_state = state.clone();
        let handle = std::thread::Builder::new()
            .name("pc-capture-cleanup-deadline".to_string())
            .spawn(move || {
                let (complete, wake) = &*thread_state;
                let should_fire = match complete.lock() {
                    Ok(complete) => match wake.wait_timeout_while(complete, timeout, |done| !*done)
                    {
                        Ok((complete, wait)) => wait.timed_out() && !*complete,
                        Err(_) => true,
                    },
                    Err(_) => true,
                };
                if should_fire {
                    on_timeout();
                }
            })?;
        Ok(Self { state, handle })
    }

    fn complete(self) {
        let Self { state, handle } = self;
        let (complete, wake) = &*state;
        let mut complete = complete.lock().unwrap_or_else(|poison| poison.into_inner());
        *complete = true;
        wake.notify_all();
        drop(complete);
        let _ = handle.join();
    }
}

pub struct ServerConfig {
    pub handshake_path: PathBuf,
    pub token: String,
    pub synthetic: bool,
    pub owner_pid: Option<u32>,
    /// Production helpers use a hard deadline so device/RTC teardown cannot leave an orphan.
    /// Tests disable it to keep a failed cleanup assertion inside the test process.
    pub hard_exit_on_disconnect: bool,
}

pub fn bind_loopback() -> std::io::Result<TcpListener> {
    TcpListener::bind(("127.0.0.1", 0))
}

pub fn write_handshake_file(path: &Path, port: u16, pid: u32) -> std::io::Result<()> {
    let body = format!("{{\"port\":{port},\"pid\":{pid}}}");
    let temp = path.with_extension(format!("tmp.{pid}"));
    {
        let mut f = std::fs::File::create(&temp)?;
        f.write_all(body.as_bytes())?;
        f.flush()?;
    }
    std::fs::rename(&temp, path)?;
    Ok(())
}

pub fn write_devices_file(path: &Path, json: &str) -> std::io::Result<()> {
    let temp = path.with_extension(format!("tmp.{}", std::process::id()));
    {
        let mut f = std::fs::File::create(&temp)?;
        f.write_all(json.as_bytes())?;
        f.flush()?;
    }
    std::fs::rename(&temp, path)?;
    Ok(())
}

pub fn accept_single(listener: &TcpListener) -> std::io::Result<TcpStream> {
    let (stream, _addr) = listener.accept()?;
    stream.set_nodelay(true).ok();
    Ok(stream)
}

pub fn reject_extra_client(stream: &mut TcpStream) -> std::io::Result<()> {
    let body = proto::encode_control(&proto::error_json("busy", "single client only"));
    stream.write_all(&body).ok();
    stream.shutdown(std::net::Shutdown::Both)
}

pub fn read_token_line<R: BufRead>(r: &mut R) -> std::io::Result<String> {
    let mut line = String::new();
    r.read_line(&mut line)?;
    Ok(line.trim_end_matches(['\r', '\n']).to_string())
}

pub fn check_frame_len(len: usize) -> Result<(), proto::DecodeError> {
    if len > MAX_FRAME_LEN {
        return Err(proto::DecodeError::BadLen(len));
    }
    Ok(())
}

pub fn read_frame_checked<R: BufRead>(r: &mut R) -> Result<Frame, proto::DecodeError> {
    let mut header = [0u8; 5];
    r.read_exact(&mut header)?;
    let ftype = header[0];
    let len = u32::from_le_bytes([header[1], header[2], header[3], header[4]]) as usize;
    check_frame_len(len)?;
    match ftype {
        proto::TYPE_CONTROL => {
            let mut body = vec![0u8; len];
            r.read_exact(&mut body)?;
            let s = String::from_utf8(body).map_err(proto::DecodeError::Utf8)?;
            Ok(Frame::Control(s))
        }
        proto::TYPE_AUDIO => {
            if len != proto::FRAME_BYTES {
                return Err(proto::DecodeError::BadLen(len));
            }
            let mut body = vec![0u8; proto::FRAME_BYTES];
            r.read_exact(&mut body)?;
            let ts = u64::from_le_bytes(body[0..8].try_into().unwrap());
            let mut samples = Vec::with_capacity(proto::FRAME_SAMPLES);
            for chunk in body[8..].chunks_exact(4) {
                samples.push(f32::from_le_bytes(chunk.try_into().unwrap()));
            }
            Ok(Frame::Audio(AudioFrame {
                encoder_epoch: 0,
                capture_generation: 0,
                capture_open_attempt: 0,
                capture_ts_ns: ts,
                capture_callback_ts_ns: ts,
                capture_timestamp_valid: false,
                samples,
            }))
        }
        proto::TYPE_AUDIO_OUT => {
            if len != proto::AUDIO_OUT_BYTES {
                return Err(proto::DecodeError::BadLen(len));
            }
            let mut body = vec![0u8; proto::AUDIO_OUT_BYTES];
            r.read_exact(&mut body)?;
            let mut samples = Vec::with_capacity(proto::AUDIO_OUT_SAMPLES);
            for chunk in body.chunks_exact(4) {
                samples.push(f32::from_le_bytes(chunk.try_into().unwrap()));
            }
            Ok(Frame::AudioOut(AudioOutFrame { samples }))
        }
        other => Err(proto::DecodeError::BadType(other)),
    }
}

fn enqueue_audio_out(playback: &Arc<Mutex<PlaybackRing>>, samples: &[f32]) -> usize {
    let mut playback = playback.lock().unwrap();
    let queued_pairs_before_frame = playback.len();
    playback.push(samples);
    queued_pairs_before_frame
}

#[derive(Debug, PartialEq)]
pub enum HelloResult {
    Accept,
    RejectToken,
    RejectProto,
}

pub fn validate_hello(op: &InboundOp, expected_token: &str) -> HelloResult {
    match op {
        InboundOp::Hello { proto, token } => {
            if *proto != PROTO_VERSION {
                HelloResult::RejectProto
            } else if !ct_eq(token.as_bytes(), expected_token.as_bytes()) {
                HelloResult::RejectToken
            } else {
                HelloResult::Accept
            }
        }
        _ => HelloResult::RejectToken,
    }
}

fn ct_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff = 0u8;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}

fn synthetic_devices() -> Vec<DeviceInfo> {
    vec![DeviceInfo {
        id: "synthetic-tone".to_string(),
        name: "Synthetic Tone (220 Hz)".to_string(),
        default: true,
    }]
}

// Session readiness and control must never depend on a host device API returning. Real device
// discovery is performed by the short-lived `--enumerate` helper, which has a process-level hard
// deadline. Keeping it out of the long-lived media session means a missing mic, denied permission,
// or a Wine/CoreAudio enumeration hang cannot block signaling or receive-only playback.
fn session_devices(synthetic: bool) -> Vec<DeviceInfo> {
    if synthetic {
        synthetic_devices()
    } else {
        Vec::new()
    }
}

fn join_thread_bounded(
    handle: std::thread::JoinHandle<()>,
    worker: &str,
    timeout: Duration,
) -> Result<(), String> {
    let deadline = Instant::now() + timeout;
    while !handle.is_finished() && Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(10));
    }
    if !handle.is_finished() {
        // Dropping a JoinHandle detaches the wedged worker. The caller must terminate the
        // isolated helper session; production run_session has a process-level cleanup deadline.
        return Err(format!(
            "{worker} did not stop within {}ms",
            timeout.as_millis()
        ));
    }
    handle
        .join()
        .map_err(|_| format!("{worker} panicked while stopping"))
}

fn spawn_synthetic_producer(
    ring: CaptureFrameProducer,
    encoder_epoch: Arc<AtomicU64>,
    stop: Arc<AtomicBool>,
    diagnostics: Arc<CaptureDiagnostics>,
    stream_generation: u64,
    media_events: SyncSender<MediaStateEvent>,
) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        let mut src = ToneSource::new();
        let open_attempt = diagnostics.begin_open_attempt();
        let descriptor = StreamDescriptor {
            requested_device: "synthetic-tone".to_string(),
            resolved_device: "synthetic-tone".to_string(),
            requested_default: true,
            requested_matched: true,
            fell_back_to_default: false,
            sample_rate: proto::SAMPLE_RATE,
            channels: proto::CHANNELS,
            sample_format: "F32".to_string(),
            buffer_mode: "synthetic-fixed".to_string(),
            buffer_min_frames: proto::FRAME_SAMPLES as u32,
            buffer_max_frames: proto::FRAME_SAMPLES as u32,
        };
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
                sample_rate: descriptor.sample_rate,
                channels: descriptor.channels,
                sample_format: descriptor.sample_format,
                buffer_mode: descriptor.buffer_mode,
                ..Default::default()
            },
        );
        while !stop.load(Ordering::Relaxed) {
            // Stamp the privacy epoch at capture-frame begin, before any PCM is generated.
            let frame_encoder_epoch = encoder_epoch.load(Ordering::Acquire);
            let callback_ns = monotonic_ns();
            let first =
                diagnostics.observe_callback(callback_ns, proto::FRAME_SAMPLES, proto::SAMPLE_RATE);
            let frame = src.fill_frame(callback_ns, callback_ns, true);
            if diagnostics.signal_windows_enabled() {
                diagnostics.raw_input.record(&frame.samples);
            }
            diagnostics.observe_resampled_samples(frame.samples.len());
            diagnostics.observe_frames_produced(1);
            let _ = ring.push(
                CaptureFrameMetadata {
                    encoder_epoch: frame_encoder_epoch,
                    capture_generation: stream_generation,
                    capture_open_attempt: open_attempt,
                    capture_ts_ns: frame.capture_ts_ns,
                    capture_callback_ts_ns: frame.capture_callback_ts_ns,
                    capture_timestamp_valid: frame.capture_timestamp_valid,
                },
                &frame.samples,
            );
            diagnostics.observe_ring_len(ring.len());
            if first {
                send_media_state(
                    &media_events,
                    MediaStateEvent {
                        direction: "capture".to_string(),
                        state: "first-callback".to_string(),
                        command_seq: diagnostics.current_command_seq(),
                        stream_generation,
                        open_attempt,
                        running: true,
                        callback_frames: proto::FRAME_SAMPLES as u64,
                        elapsed_ms: callback_ns.saturating_sub(started_ns) / 1_000_000,
                        ..Default::default()
                    },
                );
            }
            std::thread::sleep(Duration::from_millis(20));
        }
    })
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ProducerAction {
    None,
    Start,
    Stop,
    Restart,
}

#[derive(Debug)]
struct ProducerLifecycle {
    running: bool,
    synthetic: bool,
    selected: Option<String>,
}

impl ProducerLifecycle {
    fn new(synthetic: bool) -> Self {
        Self {
            running: false,
            synthetic,
            selected: None,
        }
    }

    fn start(&mut self) -> ProducerAction {
        if self.running {
            ProducerAction::None
        } else {
            self.running = true;
            ProducerAction::Start
        }
    }

    fn stop(&mut self) -> ProducerAction {
        if self.running {
            self.running = false;
            ProducerAction::Stop
        } else {
            ProducerAction::None
        }
    }

    fn select(&mut self, selected: Option<String>) -> ProducerAction {
        if self.selected == selected {
            return ProducerAction::None;
        }
        self.selected = selected;
        if self.running {
            ProducerAction::Restart
        } else {
            ProducerAction::None
        }
    }

    fn set_synthetic(&mut self, enabled: bool) -> ProducerAction {
        if self.synthetic == enabled {
            return ProducerAction::None;
        }
        self.synthetic = enabled;
        if self.running {
            ProducerAction::Restart
        } else {
            ProducerAction::None
        }
    }
}

struct CaptureProducer {
    lifecycle: ProducerLifecycle,
    ring_producer: CaptureFrameProducer,
    ring_consumer: CaptureFrameConsumer,
    encoder_epoch: Arc<AtomicU64>,
    conn: Arc<Mutex<TcpStream>>,
    aec_timing: Arc<AecTiming>,
    diagnostics: Arc<CaptureDiagnostics>,
    media_events: SyncSender<MediaStateEvent>,
    stream_generation: u64,
    stop: Option<Arc<AtomicBool>>,
    handle: Option<std::thread::JoinHandle<()>>,
}

impl CaptureProducer {
    // These are distinct preallocated callback resources and shared real-time state; grouping them
    // only to shorten this private constructor would obscure their ownership and lifecycle.
    #[allow(clippy::too_many_arguments)]
    fn new(
        synthetic: bool,
        ring_producer: CaptureFrameProducer,
        ring_consumer: CaptureFrameConsumer,
        encoder_epoch: Arc<AtomicU64>,
        conn: Arc<Mutex<TcpStream>>,
        aec_timing: Arc<AecTiming>,
        diagnostics: Arc<CaptureDiagnostics>,
        media_events: SyncSender<MediaStateEvent>,
    ) -> Self {
        Self {
            lifecycle: ProducerLifecycle::new(synthetic),
            ring_producer,
            ring_consumer,
            encoder_epoch,
            conn,
            aec_timing,
            diagnostics,
            media_events,
            stream_generation: 0,
            stop: None,
            handle: None,
        }
    }

    fn apply(&mut self, action: ProducerAction) -> Result<(), String> {
        match action {
            ProducerAction::None => self.reap_finished(),
            ProducerAction::Start => self.spawn(),
            ProducerAction::Stop => self.stop_join_clear(),
            ProducerAction::Restart => {
                self.stop_join_clear()?;
                self.spawn()
            }
        }
    }

    fn start(&mut self) -> Result<(), String> {
        let command_seq = self.diagnostics.next_command();
        let action = self.lifecycle.start();
        let acknowledged_generation = if action == ProducerAction::Start {
            self.stream_generation.saturating_add(1)
        } else {
            self.stream_generation
        };
        send_media_state(
            &self.media_events,
            MediaStateEvent {
                direction: "capture".to_string(),
                state: "command-accepted".to_string(),
                action: "start".to_string(),
                command_seq,
                stream_generation: acknowledged_generation,
                open_attempt: self.diagnostics.current_open_attempt(),
                changed: action != ProducerAction::None,
                running: self.lifecycle.running,
                ..Default::default()
            },
        );
        self.apply(action)?;
        if self.lifecycle.running && self.handle.is_none() {
            self.spawn()?;
        }
        Ok(())
    }

    fn stop(&mut self) -> Result<(), String> {
        let command_seq = self.diagnostics.next_command();
        let action = self.lifecycle.stop();
        send_media_state(
            &self.media_events,
            MediaStateEvent {
                direction: "capture".to_string(),
                state: "command-accepted".to_string(),
                action: "stop".to_string(),
                command_seq,
                stream_generation: self.stream_generation,
                open_attempt: self.diagnostics.current_open_attempt(),
                changed: action != ProducerAction::None,
                running: self.lifecycle.running,
                ..Default::default()
            },
        );
        self.apply(action)?;
        // A producer may have exited just before Stop; always clear any stale queued PCM.
        if !self.lifecycle.running {
            self.stop_join_clear()?;
        }
        Ok(())
    }

    fn select_device(&mut self, id: String) -> Result<(), String> {
        let selected = if id.is_empty() { None } else { Some(id) };
        let changed = self.lifecycle.selected != selected;
        let command_seq = self.diagnostics.next_command();
        let action = self.lifecycle.select(selected);
        send_media_state(
            &self.media_events,
            MediaStateEvent {
                direction: "capture".to_string(),
                state: "command-accepted".to_string(),
                action: "select-device".to_string(),
                command_seq,
                stream_generation: self.stream_generation,
                open_attempt: self.diagnostics.current_open_attempt(),
                changed,
                running: self.lifecycle.running,
                ..Default::default()
            },
        );
        self.apply(action)
    }

    fn set_synthetic(&mut self, enabled: bool) -> Result<(), String> {
        let changed = self.lifecycle.synthetic != enabled;
        let command_seq = self.diagnostics.next_command();
        let action = self.lifecycle.set_synthetic(enabled);
        send_media_state(
            &self.media_events,
            MediaStateEvent {
                direction: "capture".to_string(),
                state: "command-accepted".to_string(),
                action: "set-synthetic".to_string(),
                command_seq,
                stream_generation: self.stream_generation,
                open_attempt: self.diagnostics.current_open_attempt(),
                changed,
                running: self.lifecycle.running,
                ..Default::default()
            },
        );
        self.apply(action)
    }

    fn reap_finished(&mut self) -> Result<(), String> {
        if self
            .handle
            .as_ref()
            .is_some_and(|handle| handle.is_finished())
        {
            if let Some(handle) = self.handle.take() {
                let result = handle
                    .join()
                    .map_err(|_| "capture worker panicked unexpectedly".to_string());
                self.stop = None;
                return result;
            }
            self.stop = None;
        }
        Ok(())
    }

    fn spawn(&mut self) -> Result<(), String> {
        self.reap_finished()?;
        if self.handle.is_some() || !self.lifecycle.running {
            return Ok(());
        }
        self.ring_consumer.reset();
        self.stream_generation = self.diagnostics.begin_stream(
            monotonic_ns(),
            self.lifecycle.synthetic,
            self.lifecycle.selected.as_deref(),
        );
        self.aec_timing.reset_capture_path();
        send_media_state(
            &self.media_events,
            MediaStateEvent {
                direction: "capture".to_string(),
                state: "starting".to_string(),
                command_seq: self.diagnostics.current_command_seq(),
                stream_generation: self.stream_generation,
                open_attempt: self.diagnostics.current_open_attempt().saturating_add(1),
                running: true,
                requested_device: self.lifecycle.selected.clone().unwrap_or_default(),
                requested_default: self.lifecycle.selected.as_deref().is_none_or(str::is_empty),
                ..Default::default()
            },
        );
        let stop = Arc::new(AtomicBool::new(false));
        self.handle = Some(if self.lifecycle.synthetic {
            spawn_synthetic_producer(
                self.ring_producer.clone(),
                self.encoder_epoch.clone(),
                stop.clone(),
                self.diagnostics.clone(),
                self.stream_generation,
                self.media_events.clone(),
            )
        } else {
            spawn_real_producer(
                self.lifecycle.selected.clone(),
                self.ring_producer.clone(),
                self.ring_consumer.clone(),
                self.encoder_epoch.clone(),
                stop.clone(),
                self.conn.clone(),
                self.aec_timing.clone(),
                self.diagnostics.clone(),
                self.stream_generation,
                self.media_events.clone(),
            )
        });
        self.stop = Some(stop);
        Ok(())
    }

    fn stop_join_clear(&mut self) -> Result<(), String> {
        let had_worker = self.stop.is_some() || self.handle.is_some();
        let stop_started = Instant::now();
        if had_worker {
            self.diagnostics.mark_stopping();
        }
        if let Some(stop) = self.stop.take() {
            stop.store(true, Ordering::Release);
        }
        let join_result = self.handle.take().map_or(Ok(()), |handle| {
            join_thread_bounded(handle, "capture worker", CAPTURE_STOP_TIMEOUT)
        });
        self.ring_consumer.reset();
        let result = join_result;
        if had_worker {
            let stopped = result.is_ok();
            let final_window = if stopped {
                Some(self.diagnostics.mark_stopped())
            } else {
                self.diagnostics.mark_stop_failed();
                None
            };
            send_media_state(
                &self.media_events,
                MediaStateEvent {
                    direction: "capture".to_string(),
                    state: if stopped { "stopped" } else { "stop-failed" }.to_string(),
                    command_seq: self.diagnostics.current_command_seq(),
                    stream_generation: self.stream_generation,
                    open_attempt: self.diagnostics.current_open_attempt(),
                    running: false,
                    elapsed_ms: stop_started.elapsed().as_millis() as u64,
                    final_window,
                    ..Default::default()
                },
            );
        }
        result
    }
}

impl Drop for CaptureProducer {
    fn drop(&mut self) {
        if let Err(error) = self.stop_join_clear() {
            eprintln!("pc-capture: critical media failure during capture teardown: {error}");
        }
    }
}

fn spawn_telemetry_writer(
    mailbox: Arc<TelemetryMailbox>,
    media_events: Receiver<MediaStateEvent>,
    stop: Arc<AtomicBool>,
    conn: Arc<Mutex<TcpStream>>,
    sources: TelemetrySources,
    supervisor: SessionSupervisor,
) -> std::thread::JoinHandle<()> {
    spawn_critical_worker("telemetry", supervisor, move || {
        const STATS_INTERVAL: Duration = Duration::from_secs(2);
        let mut last_stats = Instant::now() - STATS_INTERVAL;
        while !stop.load(Ordering::Acquire) {
            while let Ok(event) = media_events.try_recv() {
                let bytes = encode_control(&media_state_json(&event));
                if write_frame(&conn, &bytes).is_err() {
                    return;
                }
            }
            if let Some((peak, speaking)) = mailbox.take_local() {
                let bytes = encode_control(&level_json(peak, speaking));
                if write_frame(&conn, &bytes).is_err() {
                    break;
                }
            }
            if let Some(levels) = mailbox.take_peers() {
                sources
                    .counters
                    .peer_level_batches
                    .fetch_add(1, Ordering::Relaxed);
                let bytes = encode_control(&peer_levels_json(&levels));
                if write_frame(&conn, &bytes).is_err() {
                    break;
                }
            }
            if sources.media_diagnostics.is_enabled() && last_stats.elapsed() >= STATS_INTERVAL {
                let now_ns = monotonic_ns();
                let capture = sources.capture_ring.snapshot();
                let capture_len = capture.len as u64;
                let capture_capacity = capture.capacity as u64;
                let capture_dropped = capture.dropped;
                let capture_oldest_age_ms = capture
                    .oldest_capture_ts_ns
                    .map_or(0, |timestamp| now_ns.saturating_sub(timestamp) / 1_000_000);
                let (playback_len, playback_dropped) = {
                    let playback = sources.playback_ring.lock().unwrap();
                    (playback.len() as u64, playback.dropped())
                };
                let mut snapshot =
                    sources
                        .counters
                        .snapshot(capture_dropped, playback_len, playback_dropped);
                let dsp = sources.dsp.lock().unwrap().status();
                let input = *sources.input.lock().unwrap();
                snapshot.dsp_config_generation = dsp.config_generation;
                snapshot.dsp_requested_aec = dsp.requested.aec;
                snapshot.dsp_requested_agc = dsp.requested.agc;
                snapshot.dsp_requested_ns = dsp.requested.ns;
                snapshot.dsp_requested_hpf = dsp.requested.hpf;
                snapshot.dsp_apm_loaded = dsp.apm_loaded;
                snapshot.dsp_config_fully_applied = dsp.config_fully_applied;
                snapshot.dsp_applied_aec = dsp.applied_aec;
                snapshot.dsp_applied_agc = dsp.applied_agc;
                snapshot.dsp_applied_ns = dsp.applied_ns;
                snapshot.dsp_applied_hpf = dsp.applied_hpf;
                snapshot.input_gain = input.gain;
                snapshot.input_vad_threshold = input.vad_threshold;
                snapshot.input_noise_gate_threshold = input.noise_gate_threshold;
                let receive = sources.rtc.media_receive_snapshot();
                snapshot.media_receive = MediaReceiveStats {
                    active_peers: receive.active_peers,
                    ingress_queue_overflow: receive.ingress_queue_overflow,
                    ingress_queue_depth_current: receive.ingress_queue_depth_current,
                    ingress_queue_depth_max: receive.ingress_queue_depth_max,
                    ingress_peer_queue_depth_max: receive.ingress_peer_queue_depth_max,
                    sequence_gaps: receive.sequence_gaps,
                    reordered_recovered: receive.reordered_recovered,
                    late_drops: receive.late_drops,
                    duplicate_drops: receive.duplicate_drops,
                    encoded_overflow_drops: receive.encoded_overflow_drops,
                    deadline_losses: receive.deadline_losses,
                    dred_frames: receive.dred_frames,
                    fec_frames: receive.fec_frames,
                    plc_frames: receive.plc_frames,
                    decoder_resets: receive.decoder_resets,
                    talkspurt_resets: receive.talkspurt_resets,
                    underruns: receive.underruns,
                    rebuffers: receive.rebuffers,
                    target_frames_max: receive.target_frames_max,
                    target_frames_current_max: receive.target_frames_current_max,
                    depth_frames_max: receive.depth_frames_max,
                    depth_frames_current: receive.depth_frames_current,
                    rtp_jitter_ms_max: receive.rtp_jitter_ms_max,
                };
                snapshot.network_paths = sources
                    .rtc
                    .network_path_snapshots()
                    .into_iter()
                    .map(|path| NetworkPathStats {
                        peer_id: path.peer_id,
                        generation: path.generation,
                        candidate_pair_id: path.candidate_pair_id,
                        candidate_state: path.candidate_state,
                        local_candidate_type: path.local_candidate_type,
                        remote_candidate_type: path.remote_candidate_type,
                        relay: path.relay,
                        current_rtt_ms: path.current_rtt_ms,
                        bandwidth_estimate_valid: path.bandwidth_estimate_valid,
                        available_outgoing_bitrate: path.available_outgoing_bitrate,
                        available_incoming_bitrate: path.available_incoming_bitrate,
                        remote_packets_received: path.remote_packets_received,
                        remote_packets_lost: path.remote_packets_lost,
                        remote_fraction_lost: path.remote_fraction_lost,
                        remote_report_rtt_ms: path.remote_report_rtt_ms,
                        remote_rtt_measurements: path.remote_rtt_measurements,
                    })
                    .collect();
                let encoder_policy = sources.rtc.encoder_policy_snapshot();
                snapshot.encoder_packet_loss_percent =
                    u64::from(encoder_policy.packet_loss_percent);
                snapshot.encoder_bitrate = encoder_policy.bitrate.max(0) as u64;
                let aec = sources.aec_timing.snapshot(now_ns);
                snapshot.aec_delay_ms = sources.aec_timing.applied_delay_ms().unwrap_or(0);
                snapshot.aec_recommended_delay_ms = aec.recommended_delay_ms.max(0) as u64;
                snapshot.aec_measured_delay_ms = aec.measured_delay_ms;
                snapshot.aec_input_latency_ms = aec.input_latency_ms;
                snapshot.aec_output_latency_ms = aec.output_latency_ms;
                snapshot.aec_render_queue_ms = aec.render_queue_ms;
                snapshot.aec_capture_processing_ms = aec.capture_processing_ms;
                snapshot.aec_capture_path_ms = aec.capture_path_ms;
                snapshot.aec_timing_complete = aec.timing_complete;
                snapshot.aec_input_timing_present = aec.input_timing_present;
                snapshot.aec_output_timing_present = aec.output_timing_present;
                snapshot.aec_render_timing_present = aec.render_timing_present;
                snapshot.aec_capture_path_present = aec.capture_path_present;
                snapshot.aec_fallback_reason = aec.fallback_reason.to_string();
                snapshot.aec_frame_timestamp_valid = aec.frame_timestamp_valid;
                snapshot.aec_render_observations = aec.render_observations;
                snapshot.aec_invalid_timestamp_samples = aec.invalid_timestamp_samples;
                snapshot.aec_invalid_frame_timestamp_samples = aec.invalid_frame_timestamp_samples;
                snapshot.aec_last_frame_processed_present = aec.last_frame_processed_present;
                snapshot.aec_last_frame_processed_age_ms = aec.last_frame_processed_age_ms;
                snapshot.aec_delay_frames = sources.aec_timing.applied_delay_frames();
                let media = sources.media_diagnostics.snapshot(
                    now_ns,
                    capture_len,
                    capture_capacity,
                    capture_dropped,
                    capture_oldest_age_ms,
                    playback_len,
                    playback_dropped,
                );
                let bytes = encode_control(&stats_json_with_diagnostics(&snapshot, &media));
                if write_frame(&conn, &bytes).is_err() {
                    break;
                }
                last_stats = Instant::now();
            }
            std::thread::sleep(TELEMETRY_INTERVAL / 5);
        }
    })
}

#[allow(clippy::too_many_arguments)]
fn spawn_real_producer(
    device_id: Option<String>,
    ring_producer: CaptureFrameProducer,
    ring_consumer: CaptureFrameConsumer,
    encoder_epoch: Arc<AtomicU64>,
    stop: Arc<AtomicBool>,
    conn: Arc<Mutex<TcpStream>>,
    aec_timing: Arc<AecTiming>,
    diagnostics: Arc<CaptureDiagnostics>,
    stream_generation: u64,
    media_events: SyncSender<MediaStateEvent>,
) -> std::thread::JoinHandle<()> {
    std::thread::spawn(move || {
        const ESCALATE_AFTER: Duration = Duration::from_secs(10);
        let healthy = Arc::new(AtomicBool::new(false));
        let mut ever_healthy = false;
        let mut outage_start: Option<Instant> = None;
        let mut escalated = false;
        let mut retry_attempt = 0u32;
        while !stop.load(Ordering::Relaxed) {
            healthy.store(false, Ordering::Relaxed);
            match spawn_cpal_capture(
                device_id.clone(),
                ring_producer.clone(),
                encoder_epoch.clone(),
                stop.clone(),
                healthy.clone(),
                aec_timing.clone(),
                diagnostics.clone(),
                stream_generation,
                media_events.clone(),
            ) {
                Ok(()) => break,
                Err(e) => {
                    if healthy.load(Ordering::Relaxed) {
                        ever_healthy = true;
                        outage_start = None;
                        escalated = false;
                        retry_attempt = 0;
                    }
                    retry_attempt = retry_attempt.saturating_add(1);
                    let retry_delay = capture_retry_delay(retry_attempt);
                    diagnostics.mark_retrying(retry_attempt as u64);
                    ring_consumer.discard_all();
                    send_media_state(
                        &media_events,
                        MediaStateEvent {
                            direction: "capture".to_string(),
                            state: "retrying".to_string(),
                            command_seq: diagnostics.current_command_seq(),
                            stream_generation,
                            open_attempt: diagnostics.current_open_attempt(),
                            running: true,
                            retry_attempt: retry_attempt as u64,
                            retry_delay_ms: retry_delay.as_millis() as u64,
                            ..Default::default()
                        },
                    );
                    // A permanently absent/denied microphone is a supported listen-only state.
                    // Log the first failure and then only exponentially-sparse attempts so stderr
                    // diagnostics remain bounded while the speaker/signaling engine stays alive.
                    if retry_attempt == 1 || retry_attempt.is_power_of_two() {
                        eprintln!(
                            "capture unavailable: {e}; retry attempt={retry_attempt} delay_ms={}",
                            retry_delay.as_millis()
                        );
                    }
                    let grace = if ever_healthy {
                        ESCALATE_AFTER
                    } else {
                        Duration::ZERO
                    };
                    let started = *outage_start.get_or_insert_with(Instant::now);
                    if !escalated && started.elapsed() >= grace {
                        let _ = write_frame(&conn, &encode_control(&error_json("mic-error", &e)));
                        escalated = true;
                    }
                    if stop.load(Ordering::Relaxed) {
                        break;
                    }
                    sleep_until_stopped(&stop, retry_delay);
                }
            }
        }
    })
}

fn capture_retry_delay(attempt: u32) -> Duration {
    let shift = attempt.saturating_sub(1).min(6);
    Duration::from_millis((500u64 << shift).min(30_000))
}

fn sleep_until_stopped(stop: &AtomicBool, duration: Duration) {
    const POLL: Duration = Duration::from_millis(50);
    let deadline = Instant::now() + duration;
    while !stop.load(Ordering::Relaxed) {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            break;
        }
        std::thread::sleep(remaining.min(POLL));
    }
}

fn write_frame(conn: &Arc<Mutex<TcpStream>>, bytes: &[u8]) -> std::io::Result<()> {
    let mut s = conn.lock().unwrap();
    s.write_all(bytes)?;
    s.flush()
}

fn spawn_playback_watchdog(
    out_thread: Arc<Mutex<Option<std::thread::JoinHandle<()>>>>,
    playback: Arc<Mutex<PlaybackRing>>,
    spawned_ns: Arc<AtomicU64>,
    supervision: PlaybackSupervision,
) -> std::thread::JoinHandle<()> {
    std::thread::Builder::new()
        .name("pc-capture-playback-watchdog".to_string())
        .spawn(move || {
            let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                let mut watchdog = PlaybackWatchdog::default();
                while !supervision.session.stopping.load(Ordering::Acquire) {
                    let worker_running = out_thread
                        .lock()
                        .unwrap()
                        .as_ref()
                        .is_some_and(|handle| !handle.is_finished());
                    let queued_pairs = if worker_running {
                        playback.lock().unwrap().len()
                    } else {
                        0
                    };
                    let (started_ns, callback_ns) = supervision.progress.snapshot();
                    let failure = watchdog.observe(
                        monotonic_ns(),
                        worker_running,
                        spawned_ns.load(Ordering::Acquire),
                        started_ns,
                        callback_ns,
                        queued_pairs,
                    );
                    if let Some(failure) = failure {
                        supervision
                            .session
                            .report("playback watchdog", failure.detail());
                        return;
                    }
                    sleep_until_stopped(&supervision.session.stopping, PLAYBACK_WATCHDOG_INTERVAL);
                }
            }));
            if let Err(payload) = outcome {
                supervision.session.report(
                    "playback watchdog",
                    &format!("panicked: {}", panic_detail(payload.as_ref())),
                );
            }
        })
        .unwrap_or_else(|error| panic!("cannot spawn playback watchdog: {error}"))
}

fn stop_playback_bounded(
    out_thread: &Mutex<Option<std::thread::JoinHandle<()>>>,
    out_stop: &AtomicBool,
    progress: &PlaybackProgress,
    timeout: Duration,
) -> Result<(), String> {
    out_stop.store(true, Ordering::Release);
    let handle = out_thread.lock().unwrap().take();
    if let Some(handle) = handle {
        join_thread_bounded(handle, "output worker", timeout)?;
    }
    progress.reset();
    Ok(())
}

fn reap_finished_playback_worker(
    worker: &mut Option<std::thread::JoinHandle<()>>,
    progress: &PlaybackProgress,
    aec_timing: &AecTiming,
) -> bool {
    if !worker.as_ref().is_some_and(|handle| handle.is_finished()) {
        return false;
    }
    if let Some(handle) = worker.take() {
        handle.join().ok();
    }
    progress.reset();
    // Invalidate the dead output route immediately. This must happen even when the spawn throttle
    // delays replacement, otherwise reverse audio can look renderable against stale output timing.
    aec_timing.reset_playback_path();
    true
}

fn ensure_playback(
    out_thread: &Mutex<Option<std::thread::JoinHandle<()>>>,
    out_selected: &Mutex<Option<String>>,
    out_stop: &Arc<AtomicBool>,
    playback: &Arc<Mutex<PlaybackRing>>,
    last_spawn_ns: &AtomicU64,
    counters: &Arc<NativeCounters>,
    supervision: &PlaybackSupervision,
) {
    let mut guard = out_thread.lock().unwrap();
    let reaped_finished_worker =
        reap_finished_playback_worker(&mut guard, &supervision.progress, &supervision.aec_timing);
    if guard.is_none() {
        let now = monotonic_ns();
        let last = last_spawn_ns.load(Ordering::Relaxed);
        if last != 0 && now.saturating_sub(last) < duration_ns(PLAYBACK_SPAWN_THROTTLE) {
            return;
        }
        last_spawn_ns.store(now, Ordering::Release);
        let dev = out_selected.lock().unwrap().clone();
        let pb = playback.clone();
        let st = out_stop.clone();
        let stats = counters.clone();
        let playback_progress = supervision.progress.clone();
        let aec_timing = supervision.aec_timing.clone();
        // Initial starts and explicit device switches set the spawn marker to zero. A failed
        // worker was already invalidated when reaped, including when a prior call returned at the
        // throttle above, so do not advance the timing epoch again for that replacement.
        if !reaped_finished_worker && last == 0 {
            aec_timing.reset_playback_path();
        } else {
            // A failed worker advances the epoch as soon as it is reaped, even if the spawn
            // throttle delays its replacement. Drop render observations accumulated during that
            // outage before the replacement starts, without advancing the same epoch twice.
            aec_timing.clear_playback_measurements();
        }
        let media_diagnostics = supervision.media_diagnostics.clone();
        let media_events = supervision.media_events.clone();
        let stream_generation = media_diagnostics.playback.begin_stream(dev.as_deref());
        let playback_diagnostics = media_diagnostics.playback.clone();
        let worker_supervisor = supervision.session.clone();
        supervision.progress.reset();
        st.store(false, Ordering::Release);
        counters
            .playback_spawn_attempts
            .fetch_add(1, Ordering::Relaxed);
        *guard = Some(std::thread::spawn(move || {
            let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                spawn_cpal_playback(
                    dev,
                    pb,
                    st,
                    stats.clone(),
                    playback_progress,
                    aec_timing,
                    playback_diagnostics.clone(),
                    stream_generation,
                    media_events.clone(),
                )
            }));
            match outcome {
                Ok(Ok(())) => {}
                Ok(Err(error)) => {
                    stats.playback_errors.fetch_add(1, Ordering::Relaxed);
                    playback_diagnostics.mark_error();
                    send_media_state(
                        &media_events,
                        MediaStateEvent {
                            direction: "playback".to_string(),
                            state: "error".to_string(),
                            stream_generation,
                            running: false,
                            ..Default::default()
                        },
                    );
                    eprintln!("pc-capture: playback error: {error}");
                }
                Err(payload) => {
                    playback_diagnostics.mark_error();
                    send_media_state(
                        &media_events,
                        MediaStateEvent {
                            direction: "playback".to_string(),
                            state: "error".to_string(),
                            stream_generation,
                            running: false,
                            ..Default::default()
                        },
                    );
                    worker_supervisor.report(
                        "playback worker",
                        &format!("panicked: {}", panic_detail(payload.as_ref())),
                    );
                }
            }
        }));
    }
}

enum RtcOp {
    AddPeer {
        peer_id: String,
        offerer: bool,
        relay_only: bool,
        generation: u32,
        min_encoder_epoch: u64,
    },
    RemovePeer {
        peer_id: String,
    },
    SetRemoteSdp {
        peer_id: String,
        sdp_type: String,
        sdp: String,
    },
    AddIce {
        peer_id: String,
        candidate: String,
    },
    SetIceServers {
        servers: Vec<crate::proto::IceServer>,
    },
}

#[derive(Default)]
struct EncoderPrivacyEpochState {
    applied: u64,
    failed: bool,
}

/// Coordinates privacy boundaries between the control thread and the encoder writer. Requests
/// are monotonic and may be coalesced by the writer; waiters only require that their requested
/// epoch (or a newer one) has been fully applied.
#[derive(Default)]
struct EncoderPrivacyEpoch {
    requested: Arc<AtomicU64>,
    state: Mutex<EncoderPrivacyEpochState>,
    changed: Condvar,
}

impl EncoderPrivacyEpoch {
    fn request(&self) -> Result<u64, String> {
        let mut current = self.requested.load(Ordering::Acquire);
        loop {
            let next = current
                .checked_add(1)
                .ok_or_else(|| "encoder privacy epoch exhausted".to_string())?;
            match self.requested.compare_exchange_weak(
                current,
                next,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => return Ok(next),
                Err(observed) => current = observed,
            }
        }
    }

    fn requested(&self) -> u64 {
        self.requested.load(Ordering::Acquire)
    }

    fn requested_source(&self) -> Arc<AtomicU64> {
        self.requested.clone()
    }

    fn publish_applied(&self, epoch: u64) {
        let mut state = self.state.lock().unwrap();
        state.applied = state.applied.max(epoch);
        self.changed.notify_all();
    }

    fn fail(&self) {
        let mut state = self.state.lock().unwrap();
        state.failed = true;
        self.changed.notify_all();
    }

    fn wait_applied(&self, epoch: u64, timeout: Duration) -> Result<(), String> {
        let deadline = Instant::now() + timeout;
        let mut state = self.state.lock().unwrap();
        while state.applied < epoch && !state.failed {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Err(format!(
                    "encoder privacy epoch {epoch} was not applied within {}ms",
                    timeout.as_millis()
                ));
            }
            let (next, wait) = self.changed.wait_timeout(state, remaining).unwrap();
            state = next;
            if wait.timed_out() && state.applied < epoch {
                return Err(format!(
                    "encoder privacy epoch {epoch} was not applied within {}ms",
                    timeout.as_millis()
                ));
            }
        }
        if state.failed {
            Err("encoder privacy reset failed".to_string())
        } else {
            Ok(())
        }
    }
}

fn capture_frame_authorized(frame_epoch: u64, active_epoch: u64, requested_epoch: u64) -> bool {
    frame_epoch == active_epoch && requested_epoch == active_epoch
}

pub fn run_session(stream: TcpStream, cfg: &ServerConfig) -> std::io::Result<()> {
    let Some(reader) = authenticate_session(&stream, cfg)? else {
        return Ok(());
    };
    run_authenticated_session(stream, cfg, reader)
}

fn authenticate_session(
    stream: &TcpStream,
    cfg: &ServerConfig,
) -> std::io::Result<Option<BufReader<TcpStream>>> {
    stream.set_nodelay(true).ok();
    stream
        .set_write_timeout(Some(Duration::from_millis(250)))
        .ok();
    stream.set_read_timeout(Some(Duration::from_secs(10))).ok();
    let mut reader = BufReader::new(stream.try_clone()?);
    let first = match read_frame_checked(&mut reader) {
        Ok(Frame::Control(s)) => s,
        _ => return Ok(None),
    };
    let op = match parse_inbound(&first) {
        Ok(op) => op,
        Err(_) => return Ok(None),
    };
    if validate_hello(&op, &cfg.token) != HelloResult::Accept {
        return Ok(None);
    }
    stream.set_read_timeout(None).ok();
    Ok(Some(reader))
}

#[allow(clippy::while_let_loop)]
fn run_authenticated_session(
    stream: TcpStream,
    cfg: &ServerConfig,
    mut reader: BufReader<TcpStream>,
) -> std::io::Result<()> {
    // Establish the process-local monotonic epoch before either audio stream can callback.
    let _ = monotonic_ns();
    let conn = Arc::new(Mutex::new(stream.try_clone()?));

    // Codec initialization is a session prerequisite even when microphone capture is absent:
    // receive-only users still need decoding, and a helper that can report levels but never
    // encode RTP must not advertise itself as ready.
    let encoder = OpusCodec::new().map_err(|error| {
        eprintln!("pc-capture: opus encoder init failed before ready: {error}");
        std::io::Error::other(format!("opus encoder init failed: {error}"))
    })?;

    let devices = session_devices(cfg.synthetic);
    let output_devices = session_devices(cfg.synthetic);
    write_frame(
        &conn,
        &encode_control(&ready_json(&devices, &output_devices)),
    )?;

    let dsp = Arc::new(Mutex::new(crate::dsp::Dsp::new(
        crate::dsp::DspConfig::default(),
    )));

    let playback = Arc::new(Mutex::new(PlaybackRing::new(8 * proto::AUDIO_OUT_FRAMES)));
    let out_stop = Arc::new(AtomicBool::new(false));
    let out_selected: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
    let out_thread: Arc<Mutex<Option<std::thread::JoinHandle<()>>>> = Arc::new(Mutex::new(None));
    let out_spawn_ns = Arc::new(AtomicU64::new(0));
    let out_progress = Arc::new(PlaybackProgress::default());
    let aec_timing = Arc::new(AecTiming::default());
    let media_diagnostics = Arc::new(MediaDiagnostics::default());
    let (media_event_tx, media_event_rx) = std::sync::mpsc::sync_channel(64);

    let session_stopping = Arc::new(AtomicBool::new(false));
    let (critical_tx, critical_rx) = std::sync::mpsc::channel::<String>();
    let session_supervisor = SessionSupervisor {
        stopping: session_stopping.clone(),
        failures: critical_tx.clone(),
    };
    let playback_supervision = PlaybackSupervision {
        progress: out_progress.clone(),
        aec_timing: aec_timing.clone(),
        media_diagnostics: media_diagnostics.clone(),
        media_events: media_event_tx.clone(),
        session: session_supervisor.clone(),
    };
    let critical_monitor_handle =
        spawn_critical_failure_monitor(stream.try_clone()?, session_stopping.clone(), critical_rx);
    let playback_watchdog_handle = spawn_playback_watchdog(
        out_thread.clone(),
        playback.clone(),
        out_spawn_ns.clone(),
        playback_supervision.clone(),
    );

    let (ring_producer, ring_consumer) = capture_frame_ring(RING_CAPACITY);
    let encoder_privacy = Arc::new(EncoderPrivacyEpoch::default());
    let capture_transmit_enabled = Arc::new(AtomicBool::new(false));
    let stop = Arc::new(AtomicBool::new(false));
    let mut producer = CaptureProducer::new(
        cfg.synthetic,
        ring_producer,
        ring_consumer.clone(),
        encoder_privacy.requested_source(),
        conn.clone(),
        aec_timing.clone(),
        media_diagnostics.capture.clone(),
        media_event_tx.clone(),
    );
    let input = Arc::new(Mutex::new(InputConfig::default()));
    let telemetry = Arc::new(TelemetryMailbox::default());
    let telemetry_stop = Arc::new(AtomicBool::new(false));
    let counters = Arc::new(NativeCounters::default());
    let (local_signal_tx, local_signal_rx) = std::sync::mpsc::channel::<LocalSignal>();

    let rtc = Arc::new(RtcEngine::new_with_counters(
        local_signal_tx,
        counters.clone(),
    ));
    let telemetry_handle = spawn_telemetry_writer(
        telemetry.clone(),
        media_event_rx,
        telemetry_stop.clone(),
        conn.clone(),
        TelemetrySources {
            counters: counters.clone(),
            capture_ring: ring_consumer.clone(),
            playback_ring: playback.clone(),
            dsp: dsp.clone(),
            input: input.clone(),
            aec_timing: aec_timing.clone(),
            media_diagnostics: media_diagnostics.clone(),
            rtc: rtc.clone(),
        },
        session_supervisor.clone(),
    );
    let rtc_stop = Arc::new(AtomicBool::new(false));
    let (rtc_op_tx, rtc_op_rx) = std::sync::mpsc::channel::<RtcOp>();
    let ctrl_rtc = rtc.clone();
    let ctrl_handle = spawn_critical_worker("rtc-control", session_supervisor.clone(), move || {
        while let Ok(op) = rtc_op_rx.recv() {
            match op {
                RtcOp::AddPeer {
                    peer_id,
                    offerer,
                    relay_only,
                    generation,
                    min_encoder_epoch,
                } => ctrl_rtc.add_peer(peer_id, offerer, relay_only, generation, min_encoder_epoch),
                RtcOp::RemovePeer { peer_id } => ctrl_rtc.remove_peer(&peer_id),
                RtcOp::SetRemoteSdp {
                    peer_id,
                    sdp_type,
                    sdp,
                } => ctrl_rtc.set_remote_sdp(&peer_id, &sdp_type, &sdp),
                RtcOp::AddIce { peer_id, candidate } => {
                    ctrl_rtc.add_ice_candidate(&peer_id, &candidate)
                }
                RtcOp::SetIceServers { servers } => ctrl_rtc.set_ice_servers(&servers),
            }
        }
    });

    let writer_ring = ring_consumer.clone();
    let writer_stop = stop.clone();
    let writer_dsp = dsp.clone();
    let writer_rtc = rtc.clone();
    let writer_input = input.clone();
    let writer_telemetry = telemetry.clone();
    let writer_counters = counters.clone();
    let writer_aec_timing = aec_timing.clone();
    let writer_capture_diagnostics = media_diagnostics.capture.clone();
    let writer_privacy = encoder_privacy.clone();
    let writer_transmit_enabled = capture_transmit_enabled.clone();
    let writer_handle = spawn_critical_worker("encoder", session_supervisor.clone(), move || {
        let mut encoder = encoder;
        let mut level_cadence = LevelCadence::new(Instant::now());
        let mut last_dropped = 0u64;
        let mut last_capture_stream = None;
        let mut noise_gate = NoiseGate::default();
        let mut applied_encoder_policy_generation = 0u64;
        let mut active_encoder_epoch = 0u64;
        let mut frame = AudioFrame {
            encoder_epoch: 0,
            capture_generation: 0,
            capture_open_attempt: 0,
            capture_ts_ns: 0,
            capture_callback_ts_ns: 0,
            capture_timestamp_valid: false,
            samples: vec![0.0; proto::FRAME_SAMPLES],
        };
        while !writer_stop.load(Ordering::Relaxed) {
            let requested_epoch = writer_privacy.requested();
            if requested_epoch > active_encoder_epoch {
                writer_ring.discard_all();
                if let Err(error) = encoder.reset_encoder() {
                    writer_counters.opus_errors.fetch_add(1, Ordering::Relaxed);
                    writer_privacy.fail();
                    eprintln!("pc-capture: Opus privacy reset failed: {error}");
                    return;
                }
                if !writer_rtc.advance_encoder_epoch(requested_epoch) {
                    writer_counters.opus_errors.fetch_add(1, Ordering::Relaxed);
                    writer_privacy.fail();
                    eprintln!("pc-capture: RTP privacy drain exceeded {}ms", 500);
                    return;
                }
                noise_gate.reset();
                last_capture_stream = None;
                applied_encoder_policy_generation = 0;
                active_encoder_epoch = requested_epoch;
                writer_privacy.publish_applied(active_encoder_epoch);
            }
            let dropped = writer_ring.dropped();
            if writer_ring.pop_into(&mut frame) {
                if !writer_transmit_enabled.load(Ordering::Acquire) {
                    continue;
                }
                let f = &mut frame;
                if !capture_frame_authorized(
                    f.encoder_epoch,
                    active_encoder_epoch,
                    writer_privacy.requested(),
                ) {
                    // This includes a callback that began before a privacy reset but enqueued
                    // afterward. Such PCM must never seed the reset encoder/DRED history.
                    continue;
                }
                if !writer_capture_diagnostics
                    .is_active_stream(f.capture_generation, f.capture_open_attempt)
                {
                    writer_capture_diagnostics.note_stale_generation_frame();
                    continue;
                }
                writer_counters
                    .capture_frames
                    .fetch_add(1, Ordering::Relaxed);
                if writer_capture_diagnostics.signal_windows_enabled() {
                    writer_capture_diagnostics.pre_dsp.record(&f.samples);
                }
                let mut dsp = writer_dsp.lock().unwrap();
                let capture_stream = (f.capture_generation, f.capture_open_attempt);
                if last_capture_stream != Some(capture_stream) {
                    dsp.begin_capture_generation();
                    noise_gate.reset();
                    last_capture_stream = Some(capture_stream);
                }
                // Measure at the real DSP boundary so time spent waiting behind reverse-stream
                // analysis and capture-ring backlog is included in the AEC delay.
                let process_ns = monotonic_ns();
                let aec = writer_aec_timing.snapshot_for_capture(process_ns, f);
                if f.capture_timestamp_valid && process_ns >= f.capture_ts_ns {
                    writer_capture_diagnostics
                        .observe_encoder_pop_age(process_ns - f.capture_ts_ns);
                }
                let applied_delay_ms = dsp.capture_with_stream_delay(
                    &mut f.samples,
                    aec.recommended_delay_ms,
                    aec.timing_complete,
                    aec.playback_timing_epoch,
                );
                drop(dsp);
                writer_aec_timing.note_applied_delay(applied_delay_ms);
                if writer_capture_diagnostics.signal_windows_enabled() {
                    writer_capture_diagnostics.post_dsp.record(&f.samples);
                }
                let input = *writer_input.lock().unwrap();
                input.apply_gain(&mut f.samples);
                if !writer_capture_diagnostics
                    .is_active_stream(f.capture_generation, f.capture_open_attempt)
                {
                    writer_capture_diagnostics.note_stale_generation_frame();
                    continue;
                }
                if writer_capture_diagnostics.signal_windows_enabled() {
                    writer_capture_diagnostics.post_gain.record(&f.samples);
                }
                let pk = peak(&f.samples);
                noise_gate.process(&mut f.samples, input.noise_gate_threshold);
                if !capture_frame_authorized(
                    f.encoder_epoch,
                    active_encoder_epoch,
                    writer_privacy.requested(),
                ) {
                    continue;
                }
                let policy = writer_rtc.encoder_policy_snapshot();
                if policy.generation != applied_encoder_policy_generation {
                    if let Err(error) =
                        encoder.set_network_conditions(policy.packet_loss_percent, policy.bitrate)
                    {
                        writer_counters.opus_errors.fetch_add(1, Ordering::Relaxed);
                        eprintln!(
                            "pc-capture: opus network policy failed loss={} bitrate={}: {error}",
                            policy.packet_loss_percent, policy.bitrate
                        );
                        return;
                    }
                    applied_encoder_policy_generation = policy.generation;
                }
                let pkt = encoder.encode(&f.samples);
                if !pkt.is_empty() {
                    if !writer_transmit_enabled.load(Ordering::Acquire)
                        || !capture_frame_authorized(
                            f.encoder_epoch,
                            active_encoder_epoch,
                            writer_privacy.requested(),
                        )
                    {
                        continue;
                    }
                    writer_counters.opus_encoded.fetch_add(1, Ordering::Relaxed);
                    writer_rtc.send_opus(&pkt, active_encoder_epoch);
                } else {
                    writer_counters.opus_empty.fetch_add(1, Ordering::Relaxed);
                    eprintln!("pc-capture: Opus encoder produced no packet for a valid frame");
                    return;
                }
                if let Some(window_peak) = level_cadence.observe(Instant::now(), pk) {
                    writer_telemetry.publish_local(window_peak, window_peak >= input.vad_threshold);
                    if dropped != last_dropped {
                        eprintln!("pc-capture: dropped {dropped} audio frames (backpressure)");
                        last_dropped = dropped;
                    }
                }
            } else {
                std::thread::sleep(Duration::from_millis(5));
            }
        }
    });

    let game_state = Arc::new(GameState::new());
    enum DecoderOp {
        Reset { peer_id: String, generation: u32 },
        Remove { peer_id: String },
    }
    let (dec_op_tx, dec_op_rx) = std::sync::mpsc::channel::<DecoderOp>();

    let drain_rtc = rtc.clone();
    let drain_stop = rtc_stop.clone();
    let drain_playback = playback.clone();
    let drain_dsp = dsp.clone();
    let drain_gs = game_state.clone();
    let drain_out_thread = out_thread.clone();
    let drain_out_selected = out_selected.clone();
    let drain_out_stop = out_stop.clone();
    let drain_out_spawn = out_spawn_ns.clone();
    let drain_playback_supervision = playback_supervision.clone();
    let drain_telemetry = telemetry.clone();
    let drain_counters = counters.clone();
    let drain_aec_timing = aec_timing.clone();
    let drain_handle =
        spawn_critical_worker("decoder-mixer", session_supervisor.clone(), move || {
            let mut decoders: HashMap<String, OpusCodec> = HashMap::new();
            let mut last_seq: HashMap<String, u16> = HashMap::new();
            let mut encoded: HashMap<String, EncodedPacketBuffer> = HashMap::new();
            let mut generations: HashMap<String, u32> = HashMap::new();
            let mut mixer = Mixer::new();
            let mut peer_levels = PeerLevelCadence::new(Instant::now());

            // The encoded buffer owns network jitter adaptation. Keep only a one-frame decoded
            // staging queue so the two layers do not double the configured playout delay.
            let mut jitter = PeerJitter::with_staging_limits(1, 8);
            let mut stereo = [0f32; crate::codec::FRAME_SIZE * 2];

            let frame_dur = Duration::from_millis(20);
            let mut next_tick = Instant::now();
            while !drain_stop.load(Ordering::Relaxed) {
                while let Ok(op) = dec_op_rx.try_recv() {
                    let (id, generation) = match op {
                        DecoderOp::Reset {
                            peer_id,
                            generation,
                        } => (peer_id, Some(generation)),
                        DecoderOp::Remove { peer_id } => (peer_id, None),
                    };
                    decoders.remove(&id);
                    encoded.remove(&id);
                    jitter.remove(&id);
                    last_seq.remove(&id);
                    peer_levels.remove(&id);
                    if let Some(generation) = generation {
                        generations.insert(id, generation);
                    } else {
                        generations.remove(&id);
                    }
                }

                let mut drained = 0;
                while let Some(packet) = drain_rtc.recv() {
                    let peer = packet.peer_id;
                    if generations.get(&peer).copied() != Some(packet.generation) {
                        decoders.remove(&peer);
                        encoded.remove(&peer);
                        jitter.remove(&peer);
                        last_seq.remove(&peer);
                        peer_levels.remove(&peer);
                        generations.insert(peer.clone(), packet.generation);
                    }
                    let media = drain_rtc.media_receive_counters();
                    encoded
                        .entry(peer.clone())
                        .or_insert_with(|| EncodedPacketBuffer::new(peer, media))
                        .insert(EncodedRtpPacket {
                            sequence: packet.sequence,
                            timestamp: packet.timestamp,
                            arrival: packet.arrival,
                            payload: packet.payload,
                        });
                    drained += 1;
                    if drained >= 256 {
                        break;
                    }
                }

                let now = Instant::now();
                let peers: Vec<String> = encoded.keys().cloned().collect();
                for peer in peers {
                    let Some(packet) = encoded
                        .get_mut(&peer)
                        .and_then(|buffer| buffer.pop_ready(now))
                    else {
                        continue;
                    };
                    drain_counters
                        .decode_packets
                        .fetch_add(1, Ordering::Relaxed);
                    if packet.reset_decoder {
                        if let Err(error) = reset_decoded_peer_timeline(
                            &mut decoders,
                            &mut last_seq,
                            &mut jitter,
                            &peer,
                        ) {
                            drain_counters.decode_errors.fetch_add(1, Ordering::Relaxed);
                            eprintln!("pc-capture: opus decoder reset failed peer={peer}: {error}");
                            return;
                        }
                    }
                    if !decoders.contains_key(&peer) {
                        match OpusCodec::new() {
                            Ok(codec) => {
                                decoders.insert(peer.clone(), codec);
                            }
                            Err(error) => {
                                drain_counters.decode_errors.fetch_add(1, Ordering::Relaxed);
                                eprintln!(
                                    "pc-capture: opus decoder init failed peer={peer}: {error}"
                                );
                                return;
                            }
                        }
                    }
                    let last = last_seq.get(&peer).copied();
                    let (frames, advance, report) = {
                        let codec = decoders.get_mut(&peer).unwrap();
                        decode_with_concealment_report(
                            codec,
                            last,
                            packet.sequence,
                            &packet.payload,
                        )
                    };
                    if let Some(buffer) = encoded.get(&peer) {
                        buffer.record_decode(report);
                    }
                    if frames.is_empty() {
                        drain_counters.decode_empty.fetch_add(1, Ordering::Relaxed);
                    } else {
                        drain_counters
                            .decode_frames
                            .fetch_add(frames.len() as u64, Ordering::Relaxed);
                    }
                    let recovered_frames =
                        report.dred_frames + report.fec_frames + report.plc_frames;
                    for f in &frames {
                        peer_levels.observe(&peer, peak(f));
                    }
                    jitter.push_batch(&peer, frames, recovered_frames);
                    if advance {
                        last_seq.insert(peer, packet.sequence);
                    }
                }
                if let Some(levels) = peer_levels.take_due(Instant::now()) {
                    drain_telemetry.publish_peers(levels);
                }
                if jitter.is_idle() {
                    drain_counters
                        .jitter_idle_ticks
                        .fetch_add(1, Ordering::Relaxed);
                    std::thread::sleep(Duration::from_millis(5));
                    next_tick = Instant::now();
                    continue;
                }

                let round = jitter.playout_round();
                if !round.is_empty() {
                    drain_counters.mix_rounds.fetch_add(1, Ordering::Relaxed);
                    drain_counters
                        .mixed_peer_frames
                        .fetch_add(round.len() as u64, Ordering::Relaxed);
                    let per_peer: Vec<(String, &[f32])> = round
                        .iter()
                        .map(|(k, v)| (k.clone(), v.as_slice()))
                        .collect();
                    mixer.mix(&per_peer, &drain_gs, &mut stereo);
                    drain_counters.record_mix(&stereo);

                    ensure_playback(
                        &drain_out_thread,
                        &drain_out_selected,
                        &drain_out_stop,
                        &drain_playback,
                        &drain_out_spawn,
                        &drain_counters,
                        &drain_playback_supervision,
                    );
                    // Feed the reverse stream before making these same samples available to the
                    // output callback. The queue depth at that point is the render-to-analysis
                    // component of WebRTC's required stream-delay value.
                    drain_dsp.lock().unwrap().far_end(&stereo);
                    let queued_pairs_before_frame = {
                        let mut playback = drain_playback.lock().unwrap();
                        let queued = playback.len();
                        playback.push(&stereo);
                        queued
                    };
                    drain_aec_timing.observe_render_queue_pairs(queued_pairs_before_frame);
                    drain_counters
                        .playback_queued_pairs
                        .fetch_add((stereo.len() / 2) as u64, Ordering::Relaxed);
                }

                next_tick += frame_dur;
                let now = Instant::now();
                if next_tick > now {
                    std::thread::sleep(next_tick - now);
                } else {
                    next_tick = now;
                }
            }
        });

    let signal_conn = conn.clone();
    let signal_handle =
        spawn_critical_worker("signaling-writer", session_supervisor.clone(), move || {
            while let Ok(sig) = local_signal_rx.recv() {
                let json = match sig {
                    LocalSignal::Sdp {
                        peer_id,
                        generation,
                        sdp_type,
                        sdp,
                    } => local_sdp_json(&peer_id, generation, &sdp_type, &sdp),
                    LocalSignal::Candidate {
                        peer_id,
                        generation,
                        candidate,
                    } => local_candidate_json(&peer_id, generation, &candidate),
                    LocalSignal::PeerState {
                        peer_id,
                        generation,
                        state,
                    } => peer_state_json(&peer_id, generation, &state),
                };
                if write_frame(&signal_conn, &encode_control(&json)).is_err() {
                    break;
                }
            }
        });

    'control: loop {
        let frame = match read_frame_checked(&mut reader) {
            Ok(f) => f,
            Err(error) => {
                eprintln!("pc-capture: control channel ended: {error}");
                break;
            }
        };
        match frame {
            Frame::AudioOut(frame) => {
                ensure_playback(
                    &out_thread,
                    &out_selected,
                    &out_stop,
                    &playback,
                    &out_spawn_ns,
                    &counters,
                    &playback_supervision,
                );
                // Managed setup playback uses the already-versioned AUDIO_OUT frame to test the
                // exact CPAL output selected in Perfect Comms. Pace is controlled by managed code;
                // this bounded ring still drops oldest samples if a broken caller floods it.
                dsp.lock().unwrap().far_end(&frame.samples);
                let queued_pairs_before_frame = enqueue_audio_out(&playback, &frame.samples);
                aec_timing.observe_render_queue_pairs(queued_pairs_before_frame);
                counters
                    .playback_queued_pairs
                    .fetch_add((frame.samples.len() / 2) as u64, Ordering::Relaxed);
            }
            Frame::Control(text) => {
                let op = match parse_inbound(&text) {
                    Ok(op) => op,
                    Err(_) => continue,
                };
                match op {
                    InboundOp::SelectDevice { id } => {
                        if let Err(error) = producer.select_device(id) {
                            eprintln!(
                                "pc-capture: critical media failure: capture device switch: {error}"
                            );
                            break 'control;
                        }
                    }
                    InboundOp::SelectOutputDevice { id } => {
                        let requested_output = id.clone();
                        *out_selected.lock().unwrap() = Some(id);

                        if let Err(error) = stop_playback_bounded(
                            &out_thread,
                            &out_stop,
                            &out_progress,
                            PLAYBACK_STOP_TIMEOUT,
                        ) {
                            eprintln!(
                                "pc-capture: critical media failure: playback device switch: {error}"
                            );
                            break 'control;
                        }
                        out_stop.store(false, Ordering::Release);
                        out_spawn_ns.store(0, Ordering::Release);
                        // This event is emitted only after the previous worker has fully stopped.
                        // Managed setup can therefore ignore stale playback events, wait for the
                        // following stream-started/first-callback pair, and avoid feeding its test
                        // chime into a ring whose output device is still opening.
                        send_media_state(
                            &playback_supervision.media_events,
                            MediaStateEvent {
                                direction: "playback".to_string(),
                                state: "command-accepted".to_string(),
                                action: "select-output-device".to_string(),
                                requested_default: requested_output.is_empty(),
                                requested_device: requested_output,
                                running: false,
                                ..Default::default()
                            },
                        );
                        ensure_playback(
                            &out_thread,
                            &out_selected,
                            &out_stop,
                            &playback,
                            &out_spawn_ns,
                            &counters,
                            &playback_supervision,
                        );
                    }
                    InboundOp::Start => {
                        capture_transmit_enabled.store(false, Ordering::Release);
                        let epoch = match encoder_privacy.request() {
                            Ok(epoch) => epoch,
                            Err(error) => {
                                eprintln!(
                                    "pc-capture: critical media failure: capture start privacy boundary: {error}"
                                );
                                break 'control;
                            }
                        };
                        ring_consumer.discard_all();
                        if let Err(error) =
                            encoder_privacy.wait_applied(epoch, ENCODER_PRIVACY_EPOCH_TIMEOUT)
                        {
                            eprintln!(
                                "pc-capture: critical media failure: capture start privacy boundary: {error}"
                            );
                            break 'control;
                        }
                        if let Err(error) = producer.start() {
                            eprintln!("pc-capture: critical media failure: capture start: {error}");
                            break 'control;
                        }
                        capture_transmit_enabled.store(true, Ordering::Release);
                    }
                    InboundOp::Stop => {
                        capture_transmit_enabled.store(false, Ordering::Release);
                        if let Err(error) = producer.stop() {
                            eprintln!("pc-capture: critical media failure: capture stop: {error}");
                            break 'control;
                        }
                        let epoch = match encoder_privacy.request() {
                            Ok(epoch) => epoch,
                            Err(error) => {
                                eprintln!(
                                    "pc-capture: critical media failure: capture stop privacy boundary: {error}"
                                );
                                break 'control;
                            }
                        };
                        ring_consumer.discard_all();
                        if let Err(error) =
                            encoder_privacy.wait_applied(epoch, ENCODER_PRIVACY_EPOCH_TIMEOUT)
                        {
                            eprintln!(
                                "pc-capture: critical media failure: capture stop privacy boundary: {error}"
                            );
                            break 'control;
                        }
                    }
                    InboundOp::SetDsp { aec, agc, ns, hpf } => {
                        dsp.lock()
                            .unwrap()
                            .set(crate::dsp::DspConfig { aec, agc, ns, hpf });
                    }
                    InboundOp::SetDiagnostics { enabled } => {
                        media_diagnostics.set_enabled(enabled);
                    }
                    InboundOp::SetInput {
                        gain,
                        vad_threshold,
                        noise_gate_threshold,
                    } => {
                        *input.lock().unwrap() = InputConfig::sanitized_with_gate(
                            gain,
                            vad_threshold,
                            noise_gate_threshold,
                        );
                    }
                    InboundOp::SetSynthetic { enabled } => {
                        if let Err(error) = producer.set_synthetic(enabled) {
                            eprintln!(
                                "pc-capture: critical media failure: capture mode switch: {error}"
                            );
                            break 'control;
                        }
                    }
                    InboundOp::Ping => {
                        if write_frame(&conn, &encode_control(&pong_json(now_ns()))).is_err() {
                            break 'control;
                        }
                    }
                    InboundOp::PeerAdd {
                        peer_id,
                        offerer,
                        relay_only,
                        generation,
                    } => {
                        let min_encoder_epoch = match encoder_privacy.request() {
                            Ok(epoch) => epoch,
                            Err(error) => {
                                eprintln!(
                                    "pc-capture: critical media failure: peer-add privacy boundary: {error}"
                                );
                                break 'control;
                            }
                        };
                        if dec_op_tx
                            .send(DecoderOp::Reset {
                                peer_id: peer_id.clone(),
                                generation,
                            })
                            .is_err()
                        {
                            eprintln!(
                                "pc-capture: critical media failure: decoder command channel closed"
                            );
                            break 'control;
                        }
                        if rtc_op_tx
                            .send(RtcOp::AddPeer {
                                peer_id,
                                offerer,
                                relay_only,
                                generation,
                                min_encoder_epoch,
                            })
                            .is_err()
                        {
                            eprintln!(
                                "pc-capture: critical media failure: RTC command channel closed"
                            );
                            break 'control;
                        }
                        ensure_playback(
                            &out_thread,
                            &out_selected,
                            &out_stop,
                            &playback,
                            &out_spawn_ns,
                            &counters,
                            &playback_supervision,
                        );
                    }
                    InboundOp::PeerRemove { peer_id } => {
                        if rtc_op_tx
                            .send(RtcOp::RemovePeer {
                                peer_id: peer_id.clone(),
                            })
                            .is_err()
                        {
                            eprintln!(
                                "pc-capture: critical media failure: RTC command channel closed"
                            );
                            break 'control;
                        }
                        game_state.remove_peer(&peer_id);
                        if dec_op_tx.send(DecoderOp::Remove { peer_id }).is_err() {
                            eprintln!(
                                "pc-capture: critical media failure: decoder command channel closed"
                            );
                            break 'control;
                        }
                    }
                    InboundOp::SetRemoteSdp {
                        peer_id,
                        sdp_type,
                        sdp,
                    } => {
                        if rtc_op_tx
                            .send(RtcOp::SetRemoteSdp {
                                peer_id,
                                sdp_type,
                                sdp,
                            })
                            .is_err()
                        {
                            eprintln!(
                                "pc-capture: critical media failure: RTC command channel closed"
                            );
                            break 'control;
                        }
                    }
                    InboundOp::AddIceCandidate { peer_id, candidate } => {
                        if rtc_op_tx
                            .send(RtcOp::AddIce { peer_id, candidate })
                            .is_err()
                        {
                            eprintln!(
                                "pc-capture: critical media failure: RTC command channel closed"
                            );
                            break 'control;
                        }
                    }
                    InboundOp::SetIceServers { servers } => {
                        if rtc_op_tx.send(RtcOp::SetIceServers { servers }).is_err() {
                            eprintln!(
                                "pc-capture: critical media failure: RTC command channel closed"
                            );
                            break 'control;
                        }
                    }
                    InboundOp::GameState {
                        deaf,
                        master,
                        peers,
                    } => {
                        let local = LocalState { deafened: deaf };
                        let peer_states: Vec<(String, PeerState)> = peers
                            .into_iter()
                            .map(|p| {
                                (
                                    p.id,
                                    PeerState {
                                        gain: p.gain,
                                        pan: p.pan,
                                        mode: p.mode,
                                    },
                                )
                            })
                            .collect();
                        let nonzero_gain_peers = peer_states
                            .iter()
                            .filter(|(_, peer)| {
                                peer.gain.is_finite() && peer.gain.abs() > 0.000_001
                            })
                            .count();
                        let peer_count = peer_states.len();
                        game_state.apply(local, master, peer_states);
                        counters.record_game_state(deaf, master, peer_count, nonzero_gain_peers);
                    }
                    InboundOp::Hello { .. } => {}
                }
            }
            Frame::Audio(_) => {}
        }
    }

    // The control connection is the Wine-safe lifetime signal. Arm this only after its
    // terminal read/EOF so lobby/game transitions on the still-open connection do not
    // restart the helper. If audio or RTC teardown wedges, force the process down before
    // its cloned listener can remain as an orphaned LISTEN socket.
    let cleanup_deadline = if cfg.hard_exit_on_disconnect {
        Some(
            CleanupDeadline::arm(CONTROL_EOF_CLEANUP_DEADLINE, || {
                eprintln!(
                    "pc-capture: cleanup exceeded {}ms after control EOF; terminating fail-safe",
                    CONTROL_EOF_CLEANUP_DEADLINE.as_millis()
                );
                std::process::exit(0);
            })
            .unwrap_or_else(|error| {
                eprintln!(
                    "pc-capture: cannot arm control EOF cleanup fail-safe: {error}; terminating"
                );
                std::process::exit(1);
            }),
        )
    } else {
        None
    };

    session_stopping.store(true, Ordering::Release);
    if let Err(error) = producer.stop() {
        eprintln!("pc-capture: capture teardown exceeded its bound: {error}");
    }
    stop.store(true, Ordering::Relaxed);
    out_stop.store(true, Ordering::Relaxed);
    rtc_stop.store(true, Ordering::Relaxed);
    telemetry_stop.store(true, Ordering::Release);
    playback_watchdog_handle.join().ok();
    if let Some(h) = out_thread.lock().unwrap().take() {
        h.join().ok();
    }
    writer_handle.join().ok();
    drain_handle.join().ok();
    telemetry_handle.join().ok();
    drop(rtc_op_tx);
    ctrl_handle.join().ok();
    drop(rtc);
    signal_handle.join().ok();
    drop(critical_tx);
    critical_monitor_handle.join().ok();
    if let Some(deadline) = cleanup_deadline {
        deadline.complete();
    }
    Ok(())
}

pub fn serve(cfg: ServerConfig) -> std::io::Result<()> {
    if let Some(owner_pid) = cfg.owner_pid {
        crate::owner::spawn_owner_guard(owner_pid)?;
        eprintln!("pc-capture: owner guard active pid={owner_pid}");
    } else {
        eprintln!("pc-capture: owner guard omitted; control EOF fail-safe active");
    }
    let listener = bind_loopback()?;
    let port = listener.local_addr()?.port();
    write_handshake_file(&cfg.handshake_path, port, std::process::id())?;

    let connected = Arc::new(AtomicBool::new(false));
    let guard_connected = connected.clone();
    let guard_path = cfg.handshake_path.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_secs(15));
        if !guard_connected.load(Ordering::Relaxed) {
            let _ = std::fs::remove_file(&guard_path);
            std::process::exit(0);
        }
    });

    let (first, reader) = loop {
        let candidate = accept_single(&listener)?;
        if let Some(reader) = authenticate_session(&candidate, &cfg)? {
            break (candidate, reader);
        }
    };
    connected.store(true, Ordering::Relaxed);

    let reject_listener = listener.try_clone()?;
    let _reject = std::thread::spawn(move || {
        while let Ok((mut extra, _)) = reject_listener.accept() {
            let _ = reject_extra_client(&mut extra);
        }
    });

    let result = run_authenticated_session(first, &cfg, reader);

    drop(listener);
    let _ = std::fs::remove_file(&cfg.handshake_path);
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::proto::{encode_control, parse_inbound, read_frame, Frame};
    use std::io::BufReader;

    #[test]
    fn bind_is_loopback_with_ephemeral_port() {
        let l = bind_loopback().unwrap();
        let addr = l.local_addr().unwrap();
        assert!(addr.ip().is_loopback());
        assert_ne!(addr.port(), 0);
    }

    #[test]
    fn handshake_file_is_valid_json_with_port_and_pid() {
        let dir = std::env::temp_dir();
        let path = dir.join(format!("pc-capture-hs-{}.json", std::process::id()));
        write_handshake_file(&path, 54321, 9999).unwrap();
        let body = std::fs::read_to_string(&path).unwrap();
        let v: serde_json::Value = serde_json::from_str(&body).unwrap();
        assert_eq!(v["port"], 54321);
        assert_eq!(v["pid"], 9999);
        std::fs::remove_file(&path).ok();
    }

    #[test]
    fn real_session_readiness_never_enumerates_host_devices() {
        assert!(session_devices(false).is_empty());
        assert_eq!(session_devices(true)[0].id, "synthetic-tone");
    }

    #[test]
    fn absent_microphone_retry_is_capped() {
        assert_eq!(capture_retry_delay(1), Duration::from_millis(500));
        assert_eq!(capture_retry_delay(2), Duration::from_millis(1_000));
        assert_eq!(capture_retry_delay(7), Duration::from_millis(30_000));
        assert_eq!(capture_retry_delay(100), Duration::from_millis(30_000));
    }

    #[test]
    fn encoder_privacy_epochs_are_monotonic_and_coalescible() {
        let privacy = EncoderPrivacyEpoch::default();
        let first = privacy.request().unwrap();
        let second = privacy.request().unwrap();
        assert_eq!((first, second), (1, 2));

        // The writer may observe only the newest request. Publishing it satisfies both waiters.
        privacy.publish_applied(second);
        privacy
            .wait_applied(first, Duration::from_millis(1))
            .unwrap();
        privacy
            .wait_applied(second, Duration::from_millis(1))
            .unwrap();
    }

    #[test]
    fn encoder_privacy_wait_observes_async_apply_and_times_out_closed() {
        let privacy = Arc::new(EncoderPrivacyEpoch::default());
        let epoch = privacy.request().unwrap();
        let writer_privacy = privacy.clone();
        let writer = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(5));
            writer_privacy.publish_applied(epoch);
        });
        privacy.wait_applied(epoch, Duration::from_secs(1)).unwrap();
        writer.join().unwrap();

        let unapplied = privacy.request().unwrap();
        assert!(privacy
            .wait_applied(unapplied, Duration::from_millis(1))
            .unwrap_err()
            .contains("not applied"));
    }

    #[test]
    fn privacy_boundary_discards_pre_boundary_capture_frames() {
        let (producer, consumer) = capture_frame_ring(2);
        let privacy = EncoderPrivacyEpoch::default();
        // Model a hardware callback that snapshots the epoch before PeerAdd, then stalls.
        let in_flight_callback_epoch = privacy.requested();
        assert!(producer.push(
            CaptureFrameMetadata {
                encoder_epoch: in_flight_callback_epoch,
                capture_timestamp_valid: true,
                capture_ts_ns: 123,
                ..Default::default()
            },
            &[0.25; proto::FRAME_SAMPLES],
        ));

        let epoch = privacy.request().unwrap();
        assert_eq!(consumer.discard_all(), 1);
        privacy.publish_applied(epoch);
        privacy
            .wait_applied(epoch, Duration::from_millis(1))
            .unwrap();

        // The old callback completes after discard/reset. Epoch metadata, rather than queue
        // timing, lets the writer reject it before DSP/encode.
        assert!(producer.push(
            CaptureFrameMetadata {
                encoder_epoch: in_flight_callback_epoch,
                capture_timestamp_valid: true,
                capture_ts_ns: 456,
                ..Default::default()
            },
            &[0.5; proto::FRAME_SAMPLES],
        ));

        let mut frame = AudioFrame {
            encoder_epoch: 0,
            capture_generation: 0,
            capture_open_attempt: 0,
            capture_ts_ns: 0,
            capture_callback_ts_ns: 0,
            capture_timestamp_valid: false,
            samples: vec![0.0; proto::FRAME_SAMPLES],
        };
        assert!(consumer.pop_into(&mut frame));
        assert!(frame.encoder_epoch < epoch);
        assert!(!capture_frame_authorized(
            frame.encoder_epoch,
            epoch,
            privacy.requested()
        ));
    }

    #[test]
    fn bounded_join_detaches_a_stalled_platform_worker() {
        let finished = Arc::new(AtomicBool::new(false));
        let worker_finished = finished.clone();
        let handle = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(100));
            worker_finished.store(true, Ordering::Release);
        });

        let error = join_thread_bounded(handle, "test worker", Duration::from_millis(5))
            .expect_err("stalled worker unexpectedly joined inside the bound");
        assert!(error.contains("did not stop within 5ms"));

        let wait_until = Instant::now() + Duration::from_secs(1);
        while !finished.load(Ordering::Acquire) && Instant::now() < wait_until {
            std::thread::sleep(Duration::from_millis(5));
        }
        assert!(finished.load(Ordering::Acquire));
    }

    #[test]
    fn bounded_join_accepts_a_finished_worker() {
        let handle = std::thread::spawn(|| {});
        join_thread_bounded(handle, "test worker", Duration::from_secs(1)).unwrap();
    }

    #[test]
    fn reaping_finished_playback_worker_invalidates_timing_before_respawn() {
        let progress = PlaybackProgress::default();
        progress.mark_started();
        progress.mark_callback();
        let timing = AecTiming::default();
        let previous_epoch = timing.snapshot(monotonic_ns()).playback_timing_epoch;
        let handle = std::thread::spawn(|| {});
        let deadline = Instant::now() + Duration::from_secs(1);
        while !handle.is_finished() && Instant::now() < deadline {
            std::thread::yield_now();
        }
        assert!(handle.is_finished());
        let mut worker = Some(handle);

        assert!(reap_finished_playback_worker(
            &mut worker,
            &progress,
            &timing,
        ));

        assert!(worker.is_none());
        assert_eq!(progress.snapshot(), (0, 0));
        assert_eq!(
            timing.snapshot(monotonic_ns()).playback_timing_epoch,
            previous_epoch + 1
        );
    }

    #[test]
    fn playback_watchdog_bounds_stream_startup() {
        let mut watchdog = PlaybackWatchdog::default();
        let spawned_ns = 100;
        let timeout_ns = duration_ns(PLAYBACK_START_TIMEOUT);

        assert_eq!(
            watchdog.observe(spawned_ns + timeout_ns - 1, true, spawned_ns, 0, 0, 0,),
            None
        );
        assert_eq!(
            watchdog.observe(spawned_ns + timeout_ns, true, spawned_ns, 0, 0, 0,),
            Some(PlaybackWatchdogFailure::StartupTimedOut)
        );
    }

    #[test]
    fn playback_watchdog_requires_queued_audio_before_policing_callbacks() {
        let mut watchdog = PlaybackWatchdog::default();
        let long_after_start = duration_ns(PLAYBACK_CALLBACK_STALL_TIMEOUT) * 10;

        assert_eq!(watchdog.observe(long_after_start, true, 1, 2, 3, 0), None);
        assert_eq!(
            watchdog.observe(long_after_start + 1, true, 1, 2, 3, 128),
            None,
            "the first queued observation starts a fresh callback grace period"
        );
    }

    #[test]
    fn playback_watchdog_detects_callback_stall_and_accepts_recovery() {
        let mut watchdog = PlaybackWatchdog::default();
        let first_observation = 1_000;
        let stall_timeout = duration_ns(PLAYBACK_CALLBACK_STALL_TIMEOUT);

        // First observe the callback advancing, then begin a stall window when it stops.
        assert_eq!(
            watchdog.observe(first_observation, true, 1, 2, 10, 128),
            None
        );
        let stalled_since = first_observation + 1;
        assert_eq!(watchdog.observe(stalled_since, true, 1, 2, 10, 128), None);
        assert_eq!(
            watchdog.observe(stalled_since + stall_timeout - 1, true, 1, 2, 10, 128,),
            None
        );
        assert_eq!(
            watchdog.observe(stalled_since + stall_timeout, true, 1, 2, 10, 128,),
            Some(PlaybackWatchdogFailure::CallbackStalled)
        );

        assert_eq!(
            watchdog.observe(stalled_since + stall_timeout + 1, true, 1, 2, 11, 128,),
            None,
            "a newly observed callback clears the stall"
        );
    }

    #[test]
    fn playback_watchdog_detects_stream_that_never_callbacks() {
        let mut watchdog = PlaybackWatchdog::default();
        let first_queued_observation = 500;
        let stall_timeout = duration_ns(PLAYBACK_CALLBACK_STALL_TIMEOUT);

        assert_eq!(
            watchdog.observe(first_queued_observation, true, 1, 2, 0, 64),
            None
        );
        assert_eq!(
            watchdog.observe(first_queued_observation + stall_timeout, true, 1, 2, 0, 64,),
            Some(PlaybackWatchdogFailure::CallbackStalled)
        );
    }

    #[test]
    fn unexpected_critical_worker_exit_is_reported() {
        let stopping = Arc::new(AtomicBool::new(false));
        let (tx, rx) = std::sync::mpsc::channel();
        let supervisor = SessionSupervisor {
            stopping,
            failures: tx,
        };
        let handle = spawn_critical_worker("test-worker", supervisor, || {});
        handle.join().unwrap();

        let failure = rx.recv_timeout(Duration::from_secs(1)).unwrap();
        assert!(failure.contains("test-worker: exited unexpectedly"));
    }

    #[test]
    fn critical_worker_exit_during_normal_teardown_is_suppressed() {
        let stopping = Arc::new(AtomicBool::new(true));
        let (tx, rx) = std::sync::mpsc::channel();
        let supervisor = SessionSupervisor {
            stopping,
            failures: tx,
        };
        let handle = spawn_critical_worker("test-worker", supervisor, || {});
        handle.join().unwrap();

        assert!(rx.try_recv().is_err());
    }

    #[test]
    fn read_token_line_trims_newline() {
        let mut r = BufReader::new(&b"my-secret-token\r\n"[..]);
        let tok = read_token_line(&mut r).unwrap();
        assert_eq!(tok, "my-secret-token");
    }

    #[test]
    fn cleanup_deadline_fires_when_teardown_stalls() {
        let fired = Arc::new(AtomicBool::new(false));
        let fired_on_timeout = fired.clone();
        let deadline = CleanupDeadline::arm(Duration::from_millis(30), move || {
            fired_on_timeout.store(true, Ordering::Release);
        })
        .unwrap();

        let wait_until = Instant::now() + Duration::from_secs(1);
        while !fired.load(Ordering::Acquire) && Instant::now() < wait_until {
            std::thread::sleep(Duration::from_millis(5));
        }
        assert!(fired.load(Ordering::Acquire));
        deadline.complete();
    }

    #[test]
    fn completed_cleanup_cancels_deadline_without_waiting_for_it() {
        let fired = Arc::new(AtomicBool::new(false));
        let fired_on_timeout = fired.clone();
        let deadline = CleanupDeadline::arm(Duration::from_secs(2), move || {
            fired_on_timeout.store(true, Ordering::Release);
        })
        .unwrap();

        let started = Instant::now();
        deadline.complete();
        assert!(
            started.elapsed() < Duration::from_secs(1),
            "clean completion waited for the hard deadline"
        );
        assert!(!fired.load(Ordering::Acquire));
    }

    #[test]
    fn accept_single_returns_first_client() {
        let l = bind_loopback().unwrap();
        let port = l.local_addr().unwrap().port();
        let h = std::thread::spawn(move || {
            let _c = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
            std::thread::sleep(std::time::Duration::from_millis(50));
        });
        let stream = accept_single(&l).unwrap();
        assert!(stream.peer_addr().unwrap().ip().is_loopback());
        h.join().unwrap();
    }

    #[test]
    fn second_connection_is_rejected_after_first_accepted() {
        let l = bind_loopback().unwrap();
        let port = l.local_addr().unwrap().port();
        let _first = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let accepted = accept_single(&l).unwrap();
        assert!(accepted.peer_addr().unwrap().ip().is_loopback());

        let _second = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let mut rejected = accept_single(&l).unwrap();
        reject_extra_client(&mut rejected).unwrap();
    }

    #[test]
    fn max_frame_len_guard_rejects_oversized_len() {
        assert!(check_frame_len(MAX_FRAME_LEN).is_ok());
        assert!(check_frame_len(MAX_FRAME_LEN + 1).is_err());
        assert!(check_frame_len(0).is_ok());
    }

    #[test]
    fn max_frame_len_covers_largest_legit_frame() {
        const { assert!(MAX_FRAME_LEN >= proto::FRAME_BYTES) };
    }

    #[test]
    fn validate_hello_accepts_matching_token_and_proto() {
        let op = parse_inbound(r#"{"op":"hello","proto":10,"token":"good"}"#).unwrap();
        assert!(matches!(validate_hello(&op, "good"), HelloResult::Accept));
    }

    #[test]
    fn validate_hello_rejects_bad_token() {
        let op = parse_inbound(r#"{"op":"hello","proto":10,"token":"bad"}"#).unwrap();
        assert!(matches!(
            validate_hello(&op, "good"),
            HelloResult::RejectToken
        ));
    }

    #[test]
    fn validate_hello_rejects_proto_mismatch() {
        let op = parse_inbound(r#"{"op":"hello","proto":99,"token":"good"}"#).unwrap();
        assert!(matches!(
            validate_hello(&op, "good"),
            HelloResult::RejectProto
        ));
    }

    #[test]
    fn audio_out_enqueue_preserves_samples_for_selected_output_playback() {
        let playback = Arc::new(Mutex::new(PlaybackRing::new(8)));
        let first = [0.25, -0.25, 0.5, -0.5];
        assert_eq!(enqueue_audio_out(&playback, &first), 0);

        let second = [0.75, -0.75];
        assert_eq!(enqueue_audio_out(&playback, &second), 2);

        let mut ring = playback.lock().unwrap();
        assert_eq!(ring.pop_stereo(), Some((0.25, -0.25)));
        assert_eq!(ring.pop_stereo(), Some((0.5, -0.5)));
        assert_eq!(ring.pop_stereo(), Some((0.75, -0.75)));
        assert_eq!(ring.pop_stereo(), None);
    }

    #[test]
    fn validate_hello_rejects_non_hello() {
        let op = parse_inbound(r#"{"op":"start"}"#).unwrap();
        assert!(matches!(
            validate_hello(&op, "good"),
            HelloResult::RejectToken
        ));
    }

    #[test]
    fn producer_lifecycle_restarts_only_for_live_changes() {
        let mut lifecycle = ProducerLifecycle::new(false);
        assert_eq!(lifecycle.select(Some("mic-a".into())), ProducerAction::None);
        assert_eq!(lifecycle.start(), ProducerAction::Start);
        assert_eq!(lifecycle.start(), ProducerAction::None);
        assert_eq!(
            lifecycle.select(Some("mic-b".into())),
            ProducerAction::Restart
        );
        assert_eq!(lifecycle.select(Some("mic-b".into())), ProducerAction::None);
        assert_eq!(lifecycle.set_synthetic(true), ProducerAction::Restart);
        assert_eq!(lifecycle.set_synthetic(true), ProducerAction::None);
        assert_eq!(lifecycle.stop(), ProducerAction::Stop);
        assert_eq!(lifecycle.stop(), ProducerAction::None);
        assert_eq!(lifecycle.set_synthetic(false), ProducerAction::None);
        assert_eq!(lifecycle.start(), ProducerAction::Start);
    }

    #[test]
    fn capture_producer_stop_clears_stale_frames_and_can_restart() {
        let listener = bind_loopback().unwrap();
        let port = listener.local_addr().unwrap().port();
        let client = TcpStream::connect(("127.0.0.1", port)).unwrap();
        let (server, _) = listener.accept().unwrap();
        let (ring_producer, ring_consumer) = capture_frame_ring(RING_CAPACITY);
        let diagnostics = Arc::new(CaptureDiagnostics::default());
        let (media_tx, _media_rx) = std::sync::mpsc::sync_channel(64);
        let mut producer = CaptureProducer::new(
            true,
            ring_producer,
            ring_consumer.clone(),
            Arc::new(AtomicU64::new(0)),
            Arc::new(Mutex::new(server)),
            Arc::new(AecTiming::default()),
            diagnostics.clone(),
            media_tx,
        );

        producer.start().unwrap();
        for _ in 0..100 {
            if ring_consumer.len() > 0 {
                break;
            }
            std::thread::sleep(Duration::from_millis(5));
        }
        assert!(ring_consumer.len() > 0);
        assert_eq!(
            diagnostics
                .snapshot(monotonic_ns(), 1, RING_CAPACITY as u64, 0, 0)
                .stream_generation,
            1
        );
        producer.stop().unwrap();
        assert_eq!(ring_consumer.len(), 0);

        producer.start().unwrap();
        for _ in 0..100 {
            if ring_consumer.len() > 0 {
                break;
            }
            std::thread::sleep(Duration::from_millis(5));
        }
        assert!(ring_consumer.len() > 0);
        assert_eq!(
            diagnostics
                .snapshot(monotonic_ns(), 1, RING_CAPACITY as u64, 0, 0)
                .stream_generation,
            2
        );
        producer.stop().unwrap();
        drop(client);
    }

    #[test]
    fn receive_only_session_is_ready_without_starting_or_enumerating_capture() {
        let listener = bind_loopback().unwrap();
        let port = listener.local_addr().unwrap().port();
        let cfg = ServerConfig {
            handshake_path: std::env::temp_dir().join("unused-receive-only-hs.json"),
            token: "listen-only".to_string(),
            synthetic: false,
            owner_pid: None,
            hard_exit_on_disconnect: false,
        };
        let server = std::thread::spawn(move || {
            let stream = accept_single(&listener).unwrap();
            run_session(stream, &cfg).unwrap();
        });

        let mut client = TcpStream::connect(("127.0.0.1", port)).unwrap();
        client
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        client
            .write_all(&encode_control(
                r#"{"op":"hello","proto":10,"token":"listen-only"}"#,
            ))
            .unwrap();
        let mut reader = BufReader::new(client.try_clone().unwrap());
        match read_frame(&mut reader).unwrap() {
            Frame::Control(json) => {
                let ready: serde_json::Value = serde_json::from_str(&json).unwrap();
                assert_eq!(ready["op"], "ready");
                assert_eq!(ready["devices"].as_array().unwrap().len(), 0);
            }
            other => panic!("expected ready, got {other:?}"),
        }

        // Never send select-device or start. Control/RTC readiness and heartbeat must remain
        // independent of microphone presence, permission, and platform capture APIs.
        client
            .write_all(&encode_control(r#"{"op":"ping"}"#))
            .unwrap();
        let mut got_pong = false;
        for _ in 0..10 {
            if let Frame::Control(json) = read_frame(&mut reader).unwrap() {
                let message: serde_json::Value = serde_json::from_str(&json).unwrap();
                if message["op"] == "pong" {
                    got_pong = true;
                    break;
                }
            }
        }
        assert!(got_pong, "receive-only session never answered ping");

        drop(reader);
        drop(client);
        server.join().unwrap();
    }

    #[test]
    fn synthetic_session_handshakes_pings_and_emits_level() {
        let listener = bind_loopback().unwrap();
        let port = listener.local_addr().unwrap().port();
        let cfg = ServerConfig {
            handshake_path: std::env::temp_dir().join("unused-hs.json"),
            token: "tok123".to_string(),
            synthetic: true,
            owner_pid: None,
            hard_exit_on_disconnect: false,
        };
        let server = std::thread::spawn(move || {
            let stream = accept_single(&listener).unwrap();
            run_session(stream, &cfg).ok();
        });

        let mut client = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        client.set_nodelay(true).ok();

        client
            .write_all(&encode_control(
                r#"{"op":"hello","proto":10,"token":"tok123"}"#,
            ))
            .unwrap();

        let mut reader = std::io::BufReader::new(client.try_clone().unwrap());
        match read_frame(&mut reader).unwrap() {
            Frame::Control(s) => {
                let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                assert_eq!(v["op"], "ready");
                assert_eq!(v["proto"], 10);
                assert_eq!(v["format"]["rate"], 48_000);
                assert_eq!(v["devices"][0]["id"], "synthetic-tone");
            }
            other => panic!("expected ready, got {other:?}"),
        }

        client
            .write_all(&encode_control(
                r#"{"op":"select-device","id":"synthetic-tone"}"#,
            ))
            .unwrap();
        client
            .write_all(&encode_control(r#"{"op":"ping"}"#))
            .unwrap();
        client
            .write_all(&encode_control(r#"{"op":"start"}"#))
            .unwrap();

        let mut got_pong = false;
        let mut got_level = false;
        for _ in 0..400 {
            match read_frame(&mut reader).unwrap() {
                Frame::Control(s) => {
                    let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                    if v["op"] == "pong" {
                        got_pong = true;
                        assert!(v["capTs"].as_u64().unwrap() > 0);
                    }
                    if v["op"] == "level" {
                        got_level = true;
                        assert!(v["peak"].is_number());
                        assert!(v["speaking"].is_boolean());
                    }
                }
                Frame::Audio(_) => {}
                Frame::AudioOut(_) => {}
            }
            if got_pong && got_level {
                break;
            }
        }
        assert!(got_pong, "never got pong");
        assert!(got_level, "never got level");

        client
            .write_all(&encode_control(r#"{"op":"stop"}"#))
            .unwrap();
        drop(reader);
        drop(client);
        server.join().unwrap();
    }

    #[test]
    fn session_rejects_bad_token_then_closes() {
        let listener = bind_loopback().unwrap();
        let port = listener.local_addr().unwrap().port();
        let cfg = ServerConfig {
            handshake_path: std::env::temp_dir().join("unused-hs2.json"),
            token: "right".to_string(),
            synthetic: true,
            owner_pid: None,
            hard_exit_on_disconnect: false,
        };
        let server = std::thread::spawn(move || {
            let stream = accept_single(&listener).unwrap();
            run_session(stream, &cfg).ok();
        });
        let mut client = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        client
            .write_all(&encode_control(
                r#"{"op":"hello","proto":10,"token":"wrong"}"#,
            ))
            .unwrap();
        let mut reader = std::io::BufReader::new(client.try_clone().unwrap());
        let mut buf = [0u8; 1];
        use std::io::Read;
        let n = reader.read(&mut buf).unwrap_or(0);
        assert_eq!(n, 0, "server should have closed without sending ready");
        server.join().unwrap();
    }

    #[test]
    fn session_rejects_oversized_control_frame_without_allocating() {
        let listener = bind_loopback().unwrap();
        let port = listener.local_addr().unwrap().port();
        let cfg = ServerConfig {
            handshake_path: std::env::temp_dir().join("unused-hs3.json"),
            token: "tok".to_string(),
            synthetic: true,
            owner_pid: None,
            hard_exit_on_disconnect: false,
        };
        let server = std::thread::spawn(move || {
            let stream = accept_single(&listener).unwrap();
            run_session(stream, &cfg).ok();
        });
        let mut client = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let mut oversized = Vec::new();
        oversized.push(proto::TYPE_CONTROL);
        oversized.extend_from_slice(&((MAX_FRAME_LEN as u32) + 1).to_le_bytes());
        client.write_all(&oversized).unwrap();
        let mut reader = std::io::BufReader::new(client.try_clone().unwrap());
        let mut buf = [0u8; 1];
        use std::io::Read;
        let n = reader.read(&mut buf).unwrap_or(0);
        assert_eq!(
            n, 0,
            "server must close on oversized declared len, never send ready"
        );
        server.join().unwrap();
    }

    #[test]
    fn serve_ignores_unauthorized_first_connector_and_rejects_extra_session() {
        let hs = std::env::temp_dir().join(format!("pc-serve-hs-{}.json", std::process::id()));
        let hs_for_thread = hs.clone();
        let cfg = ServerConfig {
            handshake_path: hs.clone(),
            token: "servetok".to_string(),
            synthetic: true,
            owner_pid: None,
            hard_exit_on_disconnect: false,
        };
        let server = std::thread::spawn(move || {
            serve(cfg).ok();
        });

        let mut port = 0u16;
        for _ in 0..200 {
            if let Ok(body) = std::fs::read_to_string(&hs_for_thread) {
                if let Ok(v) = serde_json::from_str::<serde_json::Value>(&body) {
                    port = v["port"].as_u64().unwrap_or(0) as u16;
                    if port != 0 {
                        break;
                    }
                }
            }
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
        assert_ne!(port, 0, "handshake file never produced a port");

        let mut unauthorized = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        unauthorized
            .write_all(&encode_control(
                r#"{"op":"hello","proto":10,"token":"wrong"}"#,
            ))
            .unwrap();
        let mut unauthorized_reader = BufReader::new(unauthorized.try_clone().unwrap());
        let mut unauthorized_probe = [0u8; 1];
        use std::io::Read;
        assert_eq!(
            unauthorized_reader
                .read(&mut unauthorized_probe)
                .unwrap_or(0),
            0,
            "unauthorized connector should close without claiming the helper"
        );
        drop(unauthorized_reader);
        drop(unauthorized);

        let mut first = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        first
            .write_all(&encode_control(
                r#"{"op":"hello","proto":10,"token":"servetok"}"#,
            ))
            .unwrap();
        let mut r1 = BufReader::new(first.try_clone().unwrap());
        match read_frame(&mut r1).unwrap() {
            Frame::Control(s) => {
                let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                assert_eq!(v["op"], "ready");
            }
            other => panic!("expected ready, got {other:?}"),
        }

        let second = std::net::TcpStream::connect(("127.0.0.1", port)).unwrap();
        let mut r2 = BufReader::new(second);
        match read_frame(&mut r2).unwrap() {
            Frame::Control(s) => {
                let v: serde_json::Value = serde_json::from_str(&s).unwrap();
                assert_eq!(v["op"], "error");
                assert_eq!(v["code"], "busy");
            }
            other => panic!("expected busy error, got {other:?}"),
        }
        let mut probe = [0u8; 1];
        let n = r2.read(&mut probe).unwrap_or(0);
        assert_eq!(
            n, 0,
            "second connection should close after busy error (EOF)"
        );

        drop(r1);
        drop(first);
        server.join().unwrap();
        std::fs::remove_file(&hs).ok();
    }
}
